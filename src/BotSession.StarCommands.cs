using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private async Task<bool> TryHandleStarCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0 || raw[0] != '*')
        {
            return false;
        }

        if (!IsHandlerRestricted())
        {
            SendImText(client, agentId, from, "Star commands are disabled until handler identity is configured.", conversationKey);
            return true;
        }

        if (!IsHandlerAgent(agentId, from))
        {
            SendImText(client, agentId, from, "Sorry, only my configured handler or parent controller can run star commands.", conversationKey);
            return true;
        }

        var commandLine = raw.Length == 1 ? string.Empty : raw[1..].Trim();
        var split = commandLine.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var command = split.Length > 0 ? split[0].ToLowerInvariant() : "help";
        var arg = split.Length > 1 ? split[1] : string.Empty;

        try
        {
            switch (command)
            {
                case "help":
                    SendImText(client, agentId, from, BuildStarHelpText(arg));
                    return true;
                case "status":
                    SendImText(client, agentId, from, BuildConversationStatusText(conversationKey));
                    return true;
                case "usage":
                    SendImText(client, agentId, from, BuildUsageText(conversationKey));
                    return true;
                case "reset":
                    _conversationConfigs.TryRemove(conversationKey, out _);
                    _opencodeChat?.ResetConversation(conversationKey);
                    SetPersistedDefaultConversationConfig(null);
                    TrySaveOpencodeSessionStateForConversation(conversationKey, null);
                    _latestUsageByConversation.TryRemove(conversationKey, out _);
                    SendImText(client, agentId, from, "Conversation AI settings reset for this IM. Using server defaults.");
                    return true;
                case "cancel":
                    await HandleCancelCommandAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
                    return true;
                case "restart":
                    await HandleRestartCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "providers":
                    await HandleProvidersCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "permission":
                case "permissions":
                    await HandlePermissionCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "dialog":
                case "dialogs":
                    HandleDialogCommand(client, agentId, from, conversationKey, arg);
                    return true;
                case "question":
                case "questions":
                    await HandleQuestionCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "models":
                    await HandleModelsCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "configure":
                    await HandleConfigureCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "prompt":
                case "prompts":
                    await HandlePromptCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "bridge":
                case "bridges":
                    await HandleBridgeCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "auth":
                    await HandleAuthCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "session":
                case "sessions":
                    await HandleSessionCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "project":
                case "projects":
                    await HandleProjectCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "voice":
                    await HandleVoiceCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "voices":
                    await HandleVoicesCommandAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
                    return true;
                case "say":
                    await HandleSayCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                default:
                    SendImText(client, agentId, from, $"Unknown command '*{command}'. Try *help.");
                    return true;
            }
        }
        catch (Exception ex)
        {
            SendImText(client, agentId, from, $"Command failed: {ex.Message}");
            return true;
        }
    }

    private static string BuildStarHelpText(string topicArg)
    {
        if (string.IsNullOrWhiteSpace(topicArg))
        {
            return string.Join(
                "\n",
                "Star commands:",
                "*help - Show command summary",
                "*help <command> - Show detailed help for one command",
                "*help all - Show detailed help for all commands",
                "*status - Show active AI and prompt settings for this IM",
                "*usage - Show latest Opencode usage (cost/tokens) for this IM",
                "*cancel - Abort current in-flight AI request for this IM",
                "*restart - Restart this bot via opensim-spawner",
                "*prompt - Manage prompt layers (status/show/clear/reload)",
                "*bridge - Manage dialog-bridge install/trust status",
                "*dialog - Manage pending script dialogs",
                "*permission - Manage pending permission requests",
                "*question - Manage pending question requests",
                "*providers - List providers",
                "*models - List models",
                "*auth - Provider API key/OAuth flows",
                "*session - Manage Opencode sessions",
                "*project - Inspect Opencode project context",
                "*voice - Manage voice routing (on/off/status)",
                "*voices - List available Piper voices",
                "*say - Speak text via Piper + configured voice backend",
                "*configure - Configure provider/model/thinking for this IM",
                "*reset - Alias for '*configure reset'");
        }

        var topic = topicArg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant() ?? "help";
        topic = topic switch
        {
            "permissions" => "permission",
            "questions" => "question",
            "projects" => "project",
            "sessions" => "session",
            "prompts" => "prompt",
            "bridges" => "bridge",
            _ => topic
        };

        return topic switch
        {
            "help" => string.Join(
                "\n",
                "*help usage:",
                "*help - show command summary",
                "*help <command> - show detailed variants",
                "*help all - show detailed variants for all commands",
                "Examples: *help session, *help configure, *help prompt"),
            "all" => string.Join(
                "\n\n",
                BuildStarHelpText("status"),
                BuildStarHelpText("usage"),
                BuildStarHelpText("cancel"),
                BuildStarHelpText("restart"),
                BuildStarHelpText("prompt"),
                BuildStarHelpText("bridge"),
                BuildStarHelpText("dialog"),
                BuildStarHelpText("permission"),
                BuildStarHelpText("question"),
                BuildStarHelpText("providers"),
                BuildStarHelpText("models"),
                BuildStarHelpText("auth"),
                BuildStarHelpText("session"),
                BuildStarHelpText("project"),
                BuildStarHelpText("voice"),
                BuildStarHelpText("voices"),
                BuildStarHelpText("say"),
                BuildStarHelpText("configure"),
                BuildStarHelpText("reset")),
            "status" => "*status - Show current provider/model/thinking/session and prompt source state for this IM.",
            "usage" => "*usage - Show the latest Opencode response usage for this IM conversation (cost/input/output/reasoning/cache).",
            "cancel" => "*cancel - Abort the current in-flight AI request for this IM conversation.",
            "restart" => "*restart - Restart this bot through opensim-spawner.",
            "prompt" => string.Join(
                "\n",
                "*prompt variants:",
                "*prompt status - Show prompt layer status",
                "*prompt show [effective|builtin|project|notecard] - Preview prompt text",
                "*prompt clear-notecard - Remove active in-world AGENTS.md prompt layer",
                "*prompt reload-project - Re-read project AGENTS.md from disk"),
            "bridge" => string.Join(
                "\n",
                "*bridge variants:",
                "*bridge status - Show dialog-bridge trust/install status",
                "*bridge install - Wear/attach dialog bridge from 'Cube Bot IAR' inventory folder",
                "*bridge uninstall [keep-scripts] - Delete pinned bridge prim and clear trust pins (default also removes script copies)"),
            "dialog" => string.Join(
                "\n",
                "*dialog variants:",
                "*dialog list - Show the latest pending in-world script dialog",
                "*dialog reply <option-number|button-label> - Reply to the latest script dialog"),
            "permission" => string.Join(
                "\n",
                "*permission variants:",
                "*permission list - List pending permission requests",
                "*permission allow <permission-id> [remember] - Approve",
                "*permission deny <permission-id> [remember] - Reject",
                "Quick reply equivalents: 1=yes, 2=no, 3=yes always, 4=no always"),
            "question" => string.Join(
                "\n",
                "*question variants:",
                "*question list - List pending question requests",
                "*question answer <question-id> <text> - Answer a question",
                "*question reject <question-id> - Reject a question"),
            "providers" => string.Join(
                "\n",
                "*providers variants:",
                "*providers - List all providers from Opencode",
                "*providers configured - List only configured providers"),
            "models" => "*models [provider] - List models, optionally filtered by provider id/name.",
            "auth" => string.Join(
                "\n",
                "*auth variants:",
                "*auth methods [provider] - List provider auth methods",
                "*auth <provider-id> api <api-key> - Save API key",
                "*auth <provider-id> oauth [method-index] - Start OAuth/device flow",
                "*auth <provider-id> oauth-complete [method-index] [code] - Complete OAuth flow"),
            "session" => string.Join(
                "\n",
                "*session variants:",
                "*session list",
                "*session create [title] [--no-select]",
                "*session use|select <session-id>",
                "*session current",
                "*session status",
                "*session details <session-id|current>",
                "*session children <session-id|current>",
                "*session patch-title <session-id|current> <new-title>",
                "*session summarize <session-id|current> [provider/model]",
                "*session abort <session-id|current>",
                "*session delete <session-id|current> [--force]",
                "*session delete --all [--force]"),
            "project" => string.Join(
                "\n",
                "*project variants:",
                "*projects - List all Opencode projects",
                "*project current - Show current Opencode project"),
            "voice" => string.Join(
                "\n",
                "*voice variants:",
                "*voice status - Show voice routing/backend/Piper endpoint status",
                "*voice on - Enable routing and connect the configured backend",
                "*voice off - Disable routing"),
            "voices" => "*voices - List available Piper voices and default voice.",
            "say" => string.Join(
                "\n",
                "*say usage:",
                "*say <text>",
                "*say voice=<voice-name> <text>",
                "Example: *say voice=en_US-lessac-medium Hello from OpenSim."),
            "configure" => string.Join(
                "\n",
                "*configure variants:",
                "*configure <provider|model|thinking|reset> ... (try *help)"),
            "reset" => "*reset - Alias for '*configure reset'.",
            _ => $"Unknown help topic '{topic}'. Try *help."
        };

    }

    private string BuildUsageText(string conversationKey)
    {
        var sessionId = _opencodeChat?.GetConversationSessionId(conversationKey) ?? "(none)";
        if (!_latestUsageByConversation.TryGetValue(conversationKey, out var usage))
        {
            return string.Join(
                "\n",
                "No usage data has been captured for this IM conversation yet.",
                $"sessionId: {sessionId}",
                "Send a normal chat message first, then run *usage.");
        }

        return string.Join(
            "\n",
            "Latest Opencode usage:",
            $"sessionId: {sessionId}",
            $"cost: {FormatUsageDouble(usage.Cost)}",
            $"input tokens: {FormatUsageInt(usage.InputTokens)}",
            $"output tokens: {FormatUsageInt(usage.OutputTokens)}",
            $"reasoning tokens: {FormatUsageInt(usage.ReasoningTokens)}",
            $"cache read tokens: {FormatUsageInt(usage.CacheReadTokens)}",
            $"cache write tokens: {FormatUsageInt(usage.CacheWriteTokens)}");
    }

    private static string FormatUsageInt(int? value)
        => value.HasValue ? value.Value.ToString() : "n/a";

    private static string FormatUsageDouble(double? value)
        => value.HasValue ? value.Value.ToString("0.########") : "n/a";

    private string BuildConversationStatusText(string conversationKey)
    {
        var currentSessionId = _opencodeChat?.GetConversationSessionId(conversationKey) ?? "(none)";
        var promptState = BuildPromptStatusText();

        if (!_conversationConfigs.TryGetValue(conversationKey, out var cfg))
        {
            var startupModel = GetStartupDefaultModelId();
            var startupProvider = GetStartupDefaultProviderId(startupModel);
            var persisted = GetPersistedDefaultConversationConfigSnapshot();
            if (!string.IsNullOrWhiteSpace(persisted?.ModelId))
            {
                startupModel = persisted!.ModelId;
                startupProvider = string.IsNullOrWhiteSpace(persisted.ProviderId)
                    ? GetStartupDefaultProviderId(startupModel)
                    : persisted.ProviderId;
            }

            return string.Join(
                "\n",
                persisted == null
                    ? "This IM conversation is using startup defaults (runtime-overridable)."
                    : "This IM conversation is using persisted bot defaults (runtime-overridable).",
                $"provider: {startupProvider ?? "(server default)"}",
                $"model: {startupModel ?? "(server default)"}",
                $"thinking: {persisted?.ThinkingLevel ?? "(default)"}",
                $"sessionId: {currentSessionId}",
                promptState);
        }

        return string.Join(
            "\n",
            "Current IM AI settings:",
            $"provider: {cfg.ProviderId ?? "(default)"}",
            $"model: {cfg.ModelId ?? "(default)"}",
            $"thinking: {cfg.ThinkingLevel ?? "(default)"}",
            $"sessionId: {currentSessionId}",
            promptState);
    }

    private Task HandlePromptCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();

        if (sub is "help" or "-h" or "--help")
        {
            SendImText(client, agentId, from, BuildStarHelpText("prompt"));
            return Task.CompletedTask;
        }

        if (sub == "status")
        {
            var sessionId = _opencodeChat?.GetConversationSessionId(conversationKey) ?? "(none)";
            var lines = new List<string>
            {
                "Prompt status:",
                $"conversation: {conversationKey}",
                $"sessionId: {sessionId}",
                $"handling: {_options.PromptHandlingEnabled}",
                $"builtin source: {_options.PromptBuiltInEnabled}",
                $"project source: {_options.PromptProjectAgentsEnabled}",
                $"project file: {_options.PromptProjectAgentsFile}",
                $"notecard source: {_options.PromptNotecardEnabled}",
                $"notecard handler-only install: {_options.PromptNotecardRequireHandler}",
                $"max chars per source: {_options.PromptMaxChars}",
                BuildPromptStatusText()
            };

            lock (_promptStateLock)
            {
                if (_activeAgentsNotecardInstalledAt.HasValue)
                {
                    lines.Add($"notecard installedAtUtc: {_activeAgentsNotecardInstalledAt.Value:O}");
                }

                if (_bridgeAgentsPromptInstalledAt.HasValue)
                {
                    lines.Add($"bridge AGENTS.md object: {_bridgeAgentsPromptObjectId}");
                    lines.Add($"bridge AGENTS.md itemId: {_bridgeAgentsPromptItemId ?? "(unknown)"}");
                    lines.Add($"bridge AGENTS.md installedAtUtc: {_bridgeAgentsPromptInstalledAt.Value:O}");
                }
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return Task.CompletedTask;
        }

        if (sub == "show" || sub == "show-source")
        {
            var target = parts.Length > 1 ? parts[1].ToLowerInvariant() : "effective";
            target = target switch
            {
                "all" => "effective",
                _ => target
            };

            string? promptText = null;
            string promptName;
            switch (target)
            {
                case "effective":
                    promptName = "effective";
                    promptText = BuildLayeredPromptText();
                    break;
                case "builtin":
                    promptName = "builtin";
                    promptText = _options.PromptBuiltInEnabled ? ClampPromptLength(BuiltInBridgePrompt) : null;
                    break;
                case "project":
                    promptName = "project AGENTS.md";
                    promptText = _options.PromptProjectAgentsEnabled ? TryLoadProjectAgentsPromptText() : null;
                    break;
                case "notecard":
                    promptName = "in-world AGENTS.md notecard";
                    lock (_promptStateLock)
                    {
                        promptText = _activeAgentsNotecardPrompt;
                    }

                    break;
                case "bridge":
                    promptName = "dialog bridge object AGENTS.md";
                    lock (_promptStateLock)
                    {
                        promptText = _bridgeAgentsPrompt;
                    }

                    break;
                default:
                    SendImText(client, agentId, from, "Usage: *prompt show [effective|builtin|project|notecard|bridge]");
                    return Task.CompletedTask;
            }

            SendImText(client, agentId, from, BuildPromptPreviewText(promptName, promptText));
            return Task.CompletedTask;
        }

        if (sub == "clear-notecard")
        {
            if (_options.PromptNotecardRequireHandler && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler or parent controller may clear the AGENTS.md notecard prompt layer.");
                return Task.CompletedTask;
            }

            ClearActiveAgentsNotecardPrompt();
            SendImText(client, agentId, from, "Cleared active in-world AGENTS.md notecard prompt layer.");
            return Task.CompletedTask;
        }

        if (sub == "reload-project")
        {
            InvalidateProjectAgentsPromptCache();
            var path = ResolveProjectAgentsPromptPath();
            var loaded = TryLoadProjectAgentsPromptText();
            if (string.IsNullOrWhiteSpace(path))
            {
                SendImText(client, agentId, from, "Project AGENTS.md file is not found. Check PROMPT_PROJECT_AGENTS_FILE.");
                return Task.CompletedTask;
            }

            SendImText(client, agentId, from, string.IsNullOrWhiteSpace(loaded)
                ? $"Project AGENTS.md exists but no prompt text was loaded from: {path}"
                : $"Reloaded project AGENTS.md from {path} ({loaded.Length} chars)." );
            return Task.CompletedTask;
        }

        SendImText(client, agentId, from, "Usage: *prompt status | *prompt show [effective|builtin|project|notecard|bridge] | *prompt clear-notecard | *prompt reload-project");
        return Task.CompletedTask;
    }

    private async Task HandleBridgeCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();

        if (sub is "help" or "-h" or "--help")
        {
            SendImText(client, agentId, from, BuildStarHelpText("bridge"));
            return;
        }

        if (sub == "status")
        {
            UUID pinnedObjectId;
            UUID pinnedOwnerId;
            bool requireTrusted;
            lock (_dialogBridgeTrustLock)
            {
                pinnedObjectId = _trustedDialogBridgeObjectId;
                pinnedOwnerId = _trustedDialogBridgeOwnerId;
                requireTrusted = _lslDialogBridgeRequireTrustedSender;
            }

            var lines = new List<string>
            {
                "Dialog bridge status:",
                $"request channel: {LslDialogBridgeRequestChannel}",
                $"require trusted sender: {requireTrusted}",
                $"trusted object pin: {(pinnedObjectId == UUID.Zero ? "(none)" : pinnedObjectId.ToString())}",
                $"trusted owner pin: {(pinnedOwnerId == UUID.Zero ? "(none)" : pinnedOwnerId.ToString())}"
            };
            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (sub == "install")
        {
            if (IsHandlerRestricted() && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler or parent controller may run *bridge install.");
                return;
            }

            var install = await DialogBridgeInstallAsync(CancellationToken.None).ConfigureAwait(false);

            SendImText(client, agentId, from, install.Message);
            return;
        }

        if (sub == "uninstall")
        {
            if (IsHandlerRestricted() && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler or parent controller may run *bridge uninstall.");
                return;
            }

            var uninstall = await DialogBridgeUninstallAsync(clearTrustPins: true, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, uninstall.Message);
            return;
        }

        SendImText(client, agentId, from, "Usage: *bridge status | *bridge install | *bridge uninstall [keep-scripts]");
    }

    private async Task HandleProvidersCommandAsync(GridClient client, UUID agentId, string from, string arg = "")
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var configuredOnly = arg.Trim().Equals("configured", StringComparison.OrdinalIgnoreCase);
        var configured = await _opencodeChat.ListProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        if (configuredOnly)
        {
            if (configured.Count == 0)
            {
                SendImText(client, agentId, from, "No configured providers were reported by Opencode.");
                return;
            }

            var configuredLines = new List<string> { $"Configured providers ({configured.Count}):" };
            foreach (var provider in configured.Take(30))
            {
                configuredLines.Add($"- {provider.Name} ({provider.Id}) [configured]");
            }

            if (configured.Count > 30)
            {
                configuredLines.Add($"... and {configured.Count - 30} more");
            }

            SendImText(client, agentId, from, string.Join("\n", configuredLines));
            return;
        }

        var available = await _opencodeChat.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        if (available.Count == 0)
        {
            SendImText(client, agentId, from, "No providers reported by Opencode.");
            return;
        }

        var configuredIds = configured
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = new List<string> { $"Providers ({available.Count}) [*providers configured for active only]:" };
        foreach (var provider in available.Take(30))
        {
            var status = provider.Connected == true || configuredIds.Contains(provider.Id)
                ? "configured"
                : "not configured";
            lines.Add($"- {provider.Name} ({provider.Id}) [{status}]");
        }

        if (available.Count > 30)
        {
            lines.Add($"... and {available.Count - 30} more");
        }

        SendImText(client, agentId, from, string.Join("\n", lines));
    }

    private async Task HandleCancelCommandAsync(GridClient client, UUID agentId, string from, string conversationKey)
    {
        var locallyCanceled = TryCancelLocalInFlightRequest(conversationKey);

        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, locallyCanceled
                ? "Canceled the current local request. AI chat is disabled by configuration, so no backend abort was sent."
                : "AI chat is currently disabled by configuration.");
            return;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            SendImText(client, agentId, from, locallyCanceled
                ? "Canceled the current local request. No active Opencode session id is known yet for backend abort."
                : "There is no active Opencode session for this IM yet, so there is nothing to cancel.");
            return;
        }

        var ok = await TryAbortSessionAsync(sessionId).ConfigureAwait(false);
        if (ok == true)
        {
            SendImText(client, agentId, from, locallyCanceled
                ? $"Canceled locally and requested backend abort for session {sessionId}."
                : $"Abort requested for the in-flight session: {sessionId}");
            return;
        }

        SendImText(client, agentId, from, locallyCanceled
            ? $"Canceled locally. Backend abort for session {sessionId} did not return an explicit success flag."
            : $"Abort request sent for session {sessionId}, but Opencode did not return an explicit success flag.");
    }

    private bool TryCancelLocalInFlightRequest(string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return false;
        }

        if (!_inFlightRequestCtsByConversation.TryRemove(conversationKey, out var localCts))
        {
            return false;
        }

        try
        {
            localCts.Cancel();
        }
        catch
        {
            // Already canceled/disposed; ignore.
        }
        finally
        {
            localCts.Dispose();
        }

        return true;
    }

    private async Task<bool?> TryAbortSessionAsync(string? sessionId)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return await _opencodeChat.AbortSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandlePermissionCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            SendImText(client, agentId, from, "There is no active Opencode session for this IM yet.");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var pending = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                SendImText(client, agentId, from, "No pending permission requests were reported for this session.");
                return;
            }

            var lines = new List<string> { $"Pending permission requests ({pending.Count}):" };
            foreach (var permission in pending.Take(12))
            {
                lines.Add("- " + BuildFriendlyPermissionListLine(permission));
            }

            if (pending.Count > 12)
            {
                lines.Add($"... and {pending.Count - 12} more");
            }

            lines.Add("Use *permission allow <permission-id> [remember] or *permission deny <permission-id> [remember].");
            _latestPendingPermissionByConversation[conversationKey] = pending[0].Id;
            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        var action = parts[0].ToLowerInvariant();
        if (action is not ("allow" or "deny" or "reject"))
        {
            SendImText(client, agentId, from, "Usage: *permission list | *permission allow <permission-id> [remember] | *permission deny <permission-id> [remember]");
            return;
        }

        if (parts.Length < 2)
        {
            SendImText(client, agentId, from, $"Usage: *permission {action} <permission-id> [remember]");
            return;
        }

        var permissionId = NormalizeLooseQuery(parts[1]);
        var remember = parts.Skip(2).Any(p => p.Equals("remember", StringComparison.OrdinalIgnoreCase)
            || p.Equals("always", StringComparison.OrdinalIgnoreCase)
            || p.Equals("--remember", StringComparison.OrdinalIgnoreCase));

        if (!IsCanonicalPermissionRequestId(permissionId))
        {
            SendImText(client, agentId, from,
                $"'{permissionId}' is not a canonical permission request id (expected per...). Run *permission list and use the per... id.");
            return;
        }

        var response = action == "allow" ? "allow" : "reject";
        var ok = await _opencodeChat.RespondToPermissionAsync(sessionId, permissionId, response, remember, CancellationToken.None).ConfigureAwait(false);
        _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptWait(conversationKey);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, ok
            ? $"Permission response sent: {response} ({permissionId}){(remember ? " [remembered]" : string.Empty)}"
            : $"Permission response request was sent for {permissionId}, but Opencode did not return an explicit success flag.");
    }

    private void HandleDialogCommand(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (!_latestScriptDialogByConversation.TryGetValue(conversationKey, out var dialog))
        {
            SendImText(client, agentId, from, "No pending script dialog for this conversation.");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            SendImText(client, agentId, from, BuildFriendlyScriptDialogPrompt(dialog));
            return;
        }

        var selectionText = parts[0].Equals("reply", StringComparison.OrdinalIgnoreCase)
            ? arg[parts[0].Length..].Trim()
            : arg.Trim();
        if (!TryResolveScriptDialogChoice(dialog, selectionText, out var selectedIndex, out var selectedLabel))
        {
            SendImText(client, agentId, from, "Could not match that dialog option. Reply with option number or exact button label.");
            return;
        }

        client.Self.ReplyToScriptDialog(dialog.Channel, selectedIndex, selectedLabel, dialog.ObjectId);
        _latestScriptDialogByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, $"Dialog response sent: {selectedLabel}");
    }

    private bool TryHandlePendingScriptDialogBeforeRouting(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('*'))
        {
            return false;
        }

        if (!_latestScriptDialogByConversation.TryGetValue(conversationKey, out var dialog))
        {
            return false;
        }

        if (!TryResolveScriptDialogChoice(dialog, text, out var selectedIndex, out var selectedLabel))
        {
            return false;
        }

        client.Self.ReplyToScriptDialog(dialog.Channel, selectedIndex, selectedLabel, dialog.ObjectId);
        _latestScriptDialogByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, $"Dialog response sent: {selectedLabel}");
        return true;
    }

    private static bool TryResolveScriptDialogChoice(PendingScriptDialog dialog, string input, out int selectedIndex, out string selectedLabel)
    {
        selectedIndex = -1;
        selectedLabel = string.Empty;
        if (dialog.Buttons.Count == 0 || string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Trim();
        if (int.TryParse(normalized, out var optionNumber)
            && optionNumber >= 1
            && optionNumber <= dialog.Buttons.Count)
        {
            selectedIndex = optionNumber - 1;
            selectedLabel = dialog.Buttons[selectedIndex];
            return true;
        }

        for (var i = 0; i < dialog.Buttons.Count; i++)
        {
            if (dialog.Buttons[i].Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
                selectedLabel = dialog.Buttons[i];
                return true;
            }
        }

        var answer = normalized.ToLowerInvariant();
        if (answer is "yes" or "y")
        {
            for (var i = 0; i < dialog.Buttons.Count; i++)
            {
                if (dialog.Buttons[i].Contains("yes", StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    selectedLabel = dialog.Buttons[i];
                    return true;
                }
            }
        }

        if (answer is "no" or "n")
        {
            for (var i = 0; i < dialog.Buttons.Count; i++)
            {
                if (dialog.Buttons[i].Contains("no", StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                    selectedLabel = dialog.Buttons[i];
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildFriendlyScriptDialogPrompt(PendingScriptDialog dialog)
    {
        var title = string.IsNullOrWhiteSpace(dialog.ObjectName) ? "Script dialog" : dialog.ObjectName;
        var lines = new List<string>
        {
            "I received an in-world script dialog:",
            title,
            dialog.Message
        };

        for (var i = 0; i < dialog.Buttons.Count; i++)
        {
            lines.Add($"{i + 1}) {dialog.Buttons[i]}");
        }

        lines.Add("Reply with option number or exact button text.");
        return string.Join("\n", lines);
    }

    private async Task HandleQuestionCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            SendImText(client, agentId, from, "There is no active Opencode session for this IM yet.");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                SendImText(client, agentId, from, "No pending question requests were reported for this session.");
                return;
            }

            var lines = new List<string> { $"Pending question requests ({pending.Count}):" };
            foreach (var question in pending.Take(8))
            {
                var options = question.Options.Count == 0 ? string.Empty : $" options: {string.Join(", ", question.Options)}";
                lines.Add($"- {question.Header} ({question.Id}): {question.Question}{options}");
            }

            if (pending.Count > 8)
            {
                lines.Add($"... and {pending.Count - 8} more");
            }

            lines.Add("Use *question answer <question-id> <text> or *question reject <question-id>.");
            _latestPendingQuestionByConversation[conversationKey] = pending[0].Id;
            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        var action = parts[0].ToLowerInvariant();
        if (action == "reject" || action == "deny")
        {
            if (parts.Length < 2)
            {
                SendImText(client, agentId, from, "Usage: *question reject <question-id>");
                return;
            }

            var questionId = NormalizeLooseQuery(parts[1]);
            var ok = await _opencodeChat.RejectQuestionAsync(sessionId, questionId, CancellationToken.None).ConfigureAwait(false);
            _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
            ClearPendingPromptWait(conversationKey);
            _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
            _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
            SendImText(client, agentId, from, ok
                ? $"Question rejected: {questionId}"
                : $"Question reject request was sent for {questionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        if (action != "answer" && action != "reply")
        {
            SendImText(client, agentId, from, "Usage: *question list | *question answer <question-id> <text> | *question reject <question-id>");
            return;
        }

        if (parts.Length < 3)
        {
            SendImText(client, agentId, from, "Usage: *question answer <question-id> <text>");
            return;
        }

        var selectedQuestionId = NormalizeLooseQuery(parts[1]);
        var answerText = arg[(arg.IndexOf(parts[1], StringComparison.Ordinal) + parts[1].Length)..].Trim();
        if (string.IsNullOrWhiteSpace(answerText))
        {
            SendImText(client, agentId, from, "Usage: *question answer <question-id> <text>");
            return;
        }

        var answered = await _opencodeChat.ReplyToQuestionAsync(
            sessionId,
            selectedQuestionId,
            new[] { answerText },
            CancellationToken.None).ConfigureAwait(false);
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptWait(conversationKey);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, answered
            ? $"Question answered: {selectedQuestionId}"
            : $"Question answer request was sent for {selectedQuestionId}, but Opencode did not return an explicit success flag.");
    }

    private async Task HandleModelsCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        string? providerFilter = null;
        if (!string.IsNullOrWhiteSpace(arg))
        {
            providerFilter = NormalizeLooseQuery(arg);
        }
        else if (_conversationConfigs.TryGetValue(conversationKey, out var cfg) && !string.IsNullOrWhiteSpace(cfg.ProviderId))
        {
            providerFilter = cfg.ProviderId;
        }

        var models = await _opencodeChat.ListModelsAsync(providerFilter, CancellationToken.None).ConfigureAwait(false);
        if (models.Count == 0)
        {
            SendImText(client, agentId, from, providerFilter == null
                ? "No models reported by Opencode."
                : $"No models found for provider '{providerFilter}'.");
            return;
        }

        var lines = new List<string>
        {
            providerFilter == null ? $"Models ({models.Count}):" : $"Models for '{providerFilter}' ({models.Count}):"
        };

        foreach (var model in models.Take(40))
        {
            var provider = string.IsNullOrWhiteSpace(model.Provider) ? "n/a" : model.Provider;
            lines.Add($"- {model.Name} ({model.Id}) [provider: {provider}]");
        }

        if (models.Count > 40)
        {
            lines.Add($"... and {models.Count - 40} more");
        }

        SendImText(client, agentId, from, string.Join("\n", lines));
    }

    private async Task HandleConfigureCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *configure <provider|model|thinking|reset> ... (try *help)");
            return;
        }

        var config = _conversationConfigs.GetOrAdd(conversationKey, _ => new ConversationConfig());
        var normalizedArg = arg.Trim();

        if (normalizedArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _conversationConfigs.TryRemove(conversationKey, out _);
            _opencodeChat.ResetConversation(conversationKey);
            SetPersistedDefaultConversationConfig(null);
            TrySaveOpencodeSessionStateForConversation(conversationKey, null);
            SendImText(client, agentId, from, "Conversation AI settings reset for this IM.");
            return;
        }

        if (normalizedArg.StartsWith("thinking ", StringComparison.OrdinalIgnoreCase))
        {
            var requested = normalizedArg[9..].Trim().ToLowerInvariant();
            config.ThinkingLevel = requested switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "off" or "default" => null,
                _ => throw new InvalidOperationException("thinking must be one of: low, medium, high, off")
            };

            SetPersistedDefaultConversationConfig(config);
            TrySaveOpencodeSessionStateForConversation(conversationKey, config);
            SendImText(client, agentId, from, $"Thinking level set to: {config.ThinkingLevel ?? "(default)"}");
            return;
        }

        if (normalizedArg.StartsWith("model ", StringComparison.OrdinalIgnoreCase))
        {
            var requestedModel = normalizedArg[6..].Trim();
            if (string.IsNullOrWhiteSpace(requestedModel))
            {
                throw new InvalidOperationException("model id is required, e.g. *configure model github-copilot/gpt-4.1");
            }

            var preferredProviderId = requestedModel.Contains('/')
                ? null
                : (config.ProviderId ?? GetPersistedDefaultConversationConfigSnapshot()?.ProviderId);
            var resolvedModelId = await ResolvePinnedModelIdAsync(requestedModel, preferredProviderId, CancellationToken.None).ConfigureAwait(false);
            config.ModelId = resolvedModelId;
            var slash = resolvedModelId.IndexOf('/');
            if (slash > 0)
            {
                config.ProviderId = resolvedModelId[..slash];
            }

            _opencodeChat.ResetConversation(conversationKey);
            SetPersistedDefaultConversationConfig(config);
            TrySaveOpencodeSessionStateForConversation(conversationKey, config);
            SendImText(client, agentId, from, $"Model pinned for this IM: {config.ModelId}");
            return;
        }

        var providerLookup = normalizedArg;
        if (providerLookup.StartsWith("provider ", StringComparison.OrdinalIgnoreCase))
        {
            providerLookup = providerLookup[9..].Trim();
        }

        providerLookup = NormalizeLooseQuery(providerLookup);

        if (providerLookup.Contains('/'))
        {
            var resolvedModelId = await ResolvePinnedModelIdAsync(providerLookup, null, CancellationToken.None).ConfigureAwait(false);
            config.ModelId = resolvedModelId;
            var slash = resolvedModelId.IndexOf('/');
            if (slash > 0)
            {
                config.ProviderId = resolvedModelId[..slash];
            }

            _opencodeChat.ResetConversation(conversationKey);
            SetPersistedDefaultConversationConfig(config);
            TrySaveOpencodeSessionStateForConversation(conversationKey, config);
            SendImText(client, agentId, from, $"Model pinned for this IM: {config.ModelId}");
            return;
        }

        var providers = await _opencodeChat.ListProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        var matchedProvider = FindProviderByNameOrId(providers, providerLookup);
        if (matchedProvider == null)
        {
            var available = await _opencodeChat.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
            var availableMatch = FindProviderByNameOrId(available, providerLookup);
            if (availableMatch != null)
            {
                SendImText(client, agentId, from, $"Provider '{availableMatch.Name}' exists but is not configured. Authorize it first with *auth (try *auth methods {availableMatch.Id}).");
                return;
            }

            SendImText(client, agentId, from, $"Provider '{providerLookup}' not found. Try *providers.");
            return;
        }

        config.ProviderId = matchedProvider.Id;
        config.ProviderName = matchedProvider.Name;

        var providerModels = await _opencodeChat.ListModelsAsync(matchedProvider.Id, CancellationToken.None).ConfigureAwait(false);
        var selectedModel = providerModels
            .FirstOrDefault(m => m.Id.EndsWith("-free", StringComparison.OrdinalIgnoreCase))
            ?? providerModels.FirstOrDefault();

        config.ModelId = selectedModel == null
            ? null
            : BuildCanonicalModelId(selectedModel.Id, selectedModel.Provider, matchedProvider.Id);
        _opencodeChat.ResetConversation(conversationKey);
        SetPersistedDefaultConversationConfig(config);
        TrySaveOpencodeSessionStateForConversation(conversationKey, config);

        if (selectedModel == null)
        {
            SendImText(client, agentId, from, $"Provider set to {matchedProvider.Name} ({matchedProvider.Id}), but no models were returned.");
            return;
        }

        SendImText(client, agentId, from, $"Configured provider {matchedProvider.Name} ({matchedProvider.Id}) with model {selectedModel.Id} for this IM.");
    }

    private async Task HandleAuthCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *auth methods [provider] | *auth <provider-id> api <api-key> | *auth <provider-id> oauth [method-index] | *auth <provider-id> oauth-complete [method-index] [code]");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            SendImText(client, agentId, from, "Usage: *auth methods [provider] | *auth <provider-id> api <api-key> | *auth <provider-id> oauth [method-index] | *auth <provider-id> oauth-complete [method-index] [code]");
            return;
        }

        if (parts[0].Equals("methods", StringComparison.OrdinalIgnoreCase))
        {
            var filter = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null;
            await HandleAuthMethodsCommandAsync(client, agentId, from, filter).ConfigureAwait(false);
            return;
        }

        if (parts.Length < 2)
        {
            SendImText(client, agentId, from, "Usage: *auth <provider-id> api <api-key> | *auth <provider-id> oauth [method-index] | *auth <provider-id> oauth-complete [method-index] [code]");
            return;
        }

        var providerQuery = NormalizeLooseQuery(parts[0]);
        var verb = parts[1].ToLowerInvariant();
        var provider = await ResolveProviderForAuthAsync(providerQuery).ConfigureAwait(false);
        if (provider == null)
        {
            SendImText(client, agentId, from, $"Provider '{providerQuery}' was not found. Try *providers.");
            return;
        }

        if (verb == "api")
        {
            if (parts.Length < 3)
            {
                SendImText(client, agentId, from, "Usage: *auth <provider-id> api <api-key>");
                return;
            }

            var apiKey = arg[(arg.IndexOf(" api ", StringComparison.OrdinalIgnoreCase) + 5)..].Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SendImText(client, agentId, from, "API key is required.");
                return;
            }

            await _opencodeChat.SetProviderApiKeyAsync(provider.Id, apiKey, CancellationToken.None).ConfigureAwait(false);
            ApplyAuthenticatedProviderAsConversationDefault(conversationKey, provider);
            SendImText(client, agentId, from, $"Stored API key for provider {provider.Name} ({provider.Id}). I will now restart myself. When I'm back, run *configure provider {provider.Id}, then optionally *models to list available models. ");
            await RestartSelfViaSpawnerAndReportAsync(client, agentId, from).ConfigureAwait(false);
            return;
        }

        if (verb == "oauth")
        {
            var methodIndex = ParseOptionalMethodIndex(parts, 2);
            var started = await _opencodeChat.StartProviderOAuthAsync(provider.Id, methodIndex, null, CancellationToken.None).ConfigureAwait(false);
            var instructions = string.IsNullOrWhiteSpace(started.Instructions)
                ? "Open the URL and complete login."
                : started.Instructions;
            var mode = string.IsNullOrWhiteSpace(started.Method) ? "unknown" : started.Method;
            SendImText(client, agentId, from, $"OAuth started for {provider.Name} ({provider.Id}) [method {methodIndex}, mode {mode}].\nURL: {started.Url}\n{instructions}\nThen run: *auth {provider.Id} oauth-complete {methodIndex}");
            return;
        }

        if (verb == "oauth-complete")
        {
            var methodIndex = ParseOptionalMethodIndex(parts, 2);
            string? code = null;
            if (parts.Length > 3)
            {
                code = string.Join(' ', parts.Skip(3));
            }

            var completed = await _opencodeChat.CompleteProviderOAuthAsync(provider.Id, methodIndex, code, CancellationToken.None).ConfigureAwait(false);
            if (completed.ProviderConfigured)
            {
                ApplyAuthenticatedProviderAsConversationDefault(conversationKey, provider);
                SendImText(client, agentId, from, $"OAuth completed for {provider.Name} ({provider.Id}). I will now restart myself. When I'm back, run *configure provider {provider.Id}, then optionally *models to list available models.");
                await RestartSelfViaSpawnerAndReportAsync(client, agentId, from).ConfigureAwait(false);
                return;
            }

            SendImText(client, agentId, from, completed.Message);
            return;
        }

        SendImText(client, agentId, from, $"Unknown auth mode '{verb}'. Use api, oauth, or oauth-complete.");
    }

    private async Task HandleRestartCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        if (!string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *restart");
            return;
        }

        SendImText(client, agentId, from, "Restart requested. I will now restart myself.");
        await RestartSelfViaSpawnerAndReportAsync(client, agentId, from).ConfigureAwait(false);
    }

    private async Task RestartSelfViaSpawnerAndReportAsync(GridClient client, UUID agentId, string from)
    {
        var restart = await RestartSelfViaSpawnerAsync(CancellationToken.None).ConfigureAwait(false);
        if (!restart.Ok)
        {
            SendImText(client, agentId, from, $"Failed to request restart: {restart.Message}");
            return;
        }

        SendImText(client, agentId, from, "Restart request accepted by opensim-spawner.");
    }

    private async Task<DataToolResult> RestartSelfViaSpawnerAsync(CancellationToken cancellationToken)
    {
        var first = _options.BotFirstName?.Trim();
        var last = _options.BotLastName?.Trim();
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return DataToolResult.FailResult("Current bot identity is not configured (first/last name).");
        }

        using var spawnerClient = new SpawnerClient(_options);
        return await spawnerClient.PatchBotAsync(first, last, "restart", cancellationToken).ConfigureAwait(false);
    }

    private void ApplyAuthenticatedProviderAsConversationDefault(string conversationKey, OpencodeProviderSummary provider)
    {
        if (string.IsNullOrWhiteSpace(conversationKey)
            || string.IsNullOrWhiteSpace(provider.Id))
        {
            return;
        }

        var config = _conversationConfigs.GetOrAdd(conversationKey, _ => new ConversationConfig());
        if (!string.IsNullOrWhiteSpace(config.ProviderId) || !string.IsNullOrWhiteSpace(config.ModelId))
        {
            return;
        }

        config.ProviderId = provider.Id.Trim();
        config.ProviderName = provider.Name;
        SetPersistedDefaultConversationConfig(config);
        TrySaveOpencodeSessionStateForConversation(conversationKey, config);
    }

    private async Task HandleAuthMethodsCommandAsync(GridClient client, UUID agentId, string from, string? providerFilter)
    {
        var methodsByProvider = await _opencodeChat!.ListProviderAuthMethodsAsync(CancellationToken.None).ConfigureAwait(false);
        if (methodsByProvider.Count == 0)
        {
            SendImText(client, agentId, from, "No provider auth methods were reported by Opencode.");
            return;
        }

        var providers = await _opencodeChat.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        var providerNameById = providers.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        IEnumerable<KeyValuePair<string, IReadOnlyList<OpencodeProviderAuthMethod>>> selected = methodsByProvider;
        if (!string.IsNullOrWhiteSpace(providerFilter))
        {
            var resolved = await ResolveProviderForAuthAsync(providerFilter).ConfigureAwait(false);
            if (resolved == null)
            {
                SendImText(client, agentId, from, $"Provider '{providerFilter}' was not found. Try *providers.");
                return;
            }

            if (!methodsByProvider.TryGetValue(resolved.Id, out var resolvedMethods))
            {
                SendImText(client, agentId, from, $"No auth methods were reported for provider {resolved.Name} ({resolved.Id}).");
                return;
            }

            selected = new[] { new KeyValuePair<string, IReadOnlyList<OpencodeProviderAuthMethod>>(resolved.Id, resolvedMethods) };
        }

        var lines = new List<string> { "Provider auth methods:" };
        foreach (var entry in selected.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Take(20))
        {
            var providerName = providerNameById.TryGetValue(entry.Key, out var name) ? name : entry.Key;
            lines.Add($"- {providerName} ({entry.Key})");
            foreach (var method in entry.Value.Take(8))
            {
                lines.Add($"  [{method.MethodIndex}] {method.Type}: {method.Label}");
            }
        }

        SendImText(client, agentId, from, string.Join("\n", lines));
    }

    private async Task HandleSessionCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *session list | *session create [title] [--no-select] | *session use <session-id> | *session status | *session current | *session details <session-id|current> | *session children <session-id|current> | *session patch-title <session-id|current> <new-title> | *session delete <session-id|current> [--force] | *session delete --all [--force] | *session summarize <session-id|current> [provider/model] | *session abort <session-id|current>");
            return;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var tail = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        if (verb is "list" or "ls")
        {
            var sessions = await _opencodeChat.ListSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            if (sessions.Count == 0)
            {
                SendImText(client, agentId, from, "No sessions were reported by Opencode.");
                return;
            }

            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            var lines = new List<string> { $"Sessions ({sessions.Count}):" };
            foreach (var session in sessions.Take(40))
            {
                var status = string.IsNullOrWhiteSpace(session.Status) ? "n/a" : session.Status;
                var project = string.IsNullOrWhiteSpace(session.ProjectId) ? "n/a" : session.ProjectId;
                var marker = !string.IsNullOrWhiteSpace(currentSessionId) && session.Id.Equals(currentSessionId, StringComparison.OrdinalIgnoreCase)
                    ? " [current IM session]"
                    : string.Empty;
                lines.Add($"- {session.Title} ({session.Id}) [status: {status}, project: {project}]{marker}");
            }

            if (sessions.Count > 40)
            {
                lines.Add($"... and {sessions.Count - 40} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (verb == "create")
        {
            var createParts = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var titleParts = new List<string>();
            var selectCreated = true;
            foreach (var part in createParts)
            {
                if (part.Equals("--no-select", StringComparison.OrdinalIgnoreCase))
                {
                    selectCreated = false;
                    continue;
                }

                titleParts.Add(part);
            }

            var requestedTitle = titleParts.Count == 0 ? null : string.Join(' ', titleParts);
            var createOptions = BuildSendOptions(conversationKey);
            var created = await _opencodeChat
                .CreateSessionAsync(requestedTitle, null, createOptions?.ModelId, CancellationToken.None)
                .ConfigureAwait(false);
            if (selectCreated)
            {
                _opencodeChat.SetConversationSessionId(conversationKey, created.Id);
                TrySaveOpencodeSessionStateForConversation(conversationKey);
            }

            var status = string.IsNullOrWhiteSpace(created.Status) ? "n/a" : created.Status;
            var selectedSuffix = selectCreated ? " [selected for this IM]" : string.Empty;
            SendImText(client, agentId, from, $"Created session: {created.Title} ({created.Id}) [status: {status}]{selectedSuffix}");
            return;
        }

        if (verb is "use" or "select")
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                SendImText(client, agentId, from, "Usage: *session use <session-id>");
                return;
            }

            var requested = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
            var sessionId = NormalizeLooseQuery(requested);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                SendImText(client, agentId, from, "Usage: *session use <session-id>");
                return;
            }

            _ = await _opencodeChat.GetSessionDetailsJsonAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            _opencodeChat.SetConversationSessionId(conversationKey, sessionId);
            TrySaveOpencodeSessionStateForConversation(conversationKey);
            SendImText(client, agentId, from, $"Current IM Opencode session set to: {sessionId}");
            return;
        }

        if (verb == "status")
        {
            var statuses = await _opencodeChat.GetSessionStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (statuses.Count == 0)
            {
                SendImText(client, agentId, from, "No session status data was reported by Opencode.");
                return;
            }

            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            var lines = new List<string> { $"Session status ({statuses.Count}):" };
            foreach (var entry in statuses.Take(60))
            {
                var marker = !string.IsNullOrWhiteSpace(currentSessionId) && entry.Key.Equals(currentSessionId, StringComparison.OrdinalIgnoreCase)
                    ? " [current IM session]"
                    : string.Empty;
                lines.Add($"- {entry.Key}: {entry.Value}{marker}");
            }

            if (statuses.Count > 60)
            {
                lines.Add($"... and {statuses.Count - 60} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (verb == "current")
        {
            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            SendImText(client, agentId, from, string.IsNullOrWhiteSpace(currentSessionId)
                ? "This IM conversation does not have an active Opencode session yet. Send a normal message first."
                : $"Current IM Opencode session: {currentSessionId}");
            return;
        }

        if (verb == "details")
        {
            var sessionId = ResolveSessionSelector(conversationKey, tail, requireExplicit: true);
            var details = await _opencodeChat.GetSessionDetailsJsonAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, $"Session details for {sessionId}:\n{details}");
            return;
        }

        if (verb == "children")
        {
            var sessionId = ResolveSessionSelector(conversationKey, tail, requireExplicit: false);
            var children = await _opencodeChat.GetSessionChildrenAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (children.Count == 0)
            {
                SendImText(client, agentId, from, $"Session {sessionId} has no child sessions.");
                return;
            }

            var lines = new List<string> { $"Child sessions for {sessionId} ({children.Count}):" };
            foreach (var child in children.Take(40))
            {
                var status = string.IsNullOrWhiteSpace(child.Status) ? "n/a" : child.Status;
                var project = string.IsNullOrWhiteSpace(child.ProjectId) ? "n/a" : child.ProjectId;
                lines.Add($"- {child.Title} ({child.Id}) [status: {status}, project: {project}]");
            }

            if (children.Count > 40)
            {
                lines.Add($"... and {children.Count - 40} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (verb == "patch-title")
        {
            var titleParts = tail.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (titleParts.Length < 2)
            {
                SendImText(client, agentId, from, "Usage: *session patch-title <session-id|current> <new-title>");
                return;
            }

            var sessionId = ResolveSessionSelector(conversationKey, titleParts[0], requireExplicit: true);
            var newTitle = titleParts[1].Trim();
            if (string.IsNullOrWhiteSpace(newTitle))
            {
                SendImText(client, agentId, from, "Usage: *session patch-title <session-id|current> <new-title>");
                return;
            }

            var updated = await _opencodeChat.UpdateSessionTitleAsync(sessionId, newTitle, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, $"Session renamed: {updated.Title} ({updated.Id})");
            return;
        }

        if (verb is "delete" or "remove")
        {
            var deleteParts = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (deleteParts.Length == 0)
            {
                SendImText(client, agentId, from, "Usage: *session delete <session-id|current> [--force] | *session delete --all [--force]");
                return;
            }

            var normalizedDeleteParts = deleteParts
                .Select(NormalizeLooseQuery)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            var deleteAllRequested = normalizedDeleteParts.Any(p => p.Equals("--all", StringComparison.OrdinalIgnoreCase)
                || p.Equals("all", StringComparison.OrdinalIgnoreCase));
            if (deleteAllRequested)
            {
                var deleteAllConfirmed = normalizedDeleteParts.Any(p => p.Equals("--force", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("confirm", StringComparison.OrdinalIgnoreCase));
                if (!deleteAllConfirmed)
                {
                    SendImText(client, agentId, from, "Deletion is destructive. To confirm deleting all sessions, run: *session delete --all --force");
                    return;
                }

                var sessions = await _opencodeChat.ListSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                if (sessions.Count == 0)
                {
                    SendImText(client, agentId, from, "No sessions were reported by Opencode.");
                    return;
                }

                var mappedCurrentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                var deletedCount = 0;
                var failedCount = 0;
                foreach (var session in sessions)
                {
                    try
                    {
                        _ = await _opencodeChat.DeleteSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
                        deletedCount++;
                    }
                    catch
                    {
                        failedCount++;
                    }
                }

                if (!string.IsNullOrWhiteSpace(mappedCurrentSessionId)
                    && sessions.Any(s => s.Id.Equals(mappedCurrentSessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    _opencodeChat.ResetConversation(conversationKey);
                }

                SendImText(client, agentId, from, failedCount == 0
                    ? $"Deleted {deletedCount} session(s)."
                    : $"Deleted {deletedCount} session(s); {failedCount} failed.");
                return;
            }

            var sessionSelector = deleteParts[0];
            var deleteConfirmed = normalizedDeleteParts.Skip(1).Any(p => p.Equals("--force", StringComparison.OrdinalIgnoreCase)
                || p.Equals("confirm", StringComparison.OrdinalIgnoreCase));
            var sessionId = ResolveSessionSelector(conversationKey, sessionSelector, requireExplicit: false);
            if (!deleteConfirmed)
            {
                SendImText(client, agentId, from, $"Deletion is destructive. To confirm, run: *session delete {sessionSelector} --force");
                return;
            }

            var deleted = await _opencodeChat.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);

            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(currentSessionId)
                && currentSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                _opencodeChat.ResetConversation(conversationKey);
            }

            SendImText(client, agentId, from, deleted
                ? $"Deleted session {sessionId}."
                : $"Delete request completed for session {sessionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        if (verb is "summarize" or "summarise")
        {
            var partsForSummarize = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var selector = partsForSummarize.Length > 0 ? partsForSummarize[0] : "current";
            var sessionId = ResolveSessionSelector(conversationKey, selector, requireExplicit: false);

            string? providerId = null;
            string? modelId = null;
            if (partsForSummarize.Length > 1)
            {
                var requestedModel = NormalizeLooseQuery(partsForSummarize[1]);
                if (requestedModel.Contains('/'))
                {
                    var slash = requestedModel.IndexOf('/');
                    providerId = requestedModel[..slash];
                    modelId = requestedModel;
                }
            }

            var ok = await _opencodeChat.SummarizeSessionAsync(sessionId, providerId, modelId, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, ok
                ? $"Requested summary for session {sessionId}."
                : $"Summary request completed for session {sessionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        if (verb == "abort")
        {
            var sessionId = ResolveSessionSelector(conversationKey, tail, requireExplicit: false);
            var ok = await _opencodeChat.AbortSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, ok
                ? $"Abort requested for session {sessionId}."
                : $"Abort request completed for session {sessionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        SendImText(client, agentId, from, "Unknown session command. Usage: *session list | *session create [title] [--no-select] | *session use <session-id> | *session status | *session current | *session details <session-id|current> | *session children <session-id|current> | *session patch-title <session-id|current> <new-title> | *session delete <session-id|current> [--force] | *session delete --all [--force] | *session summarize <session-id|current> [provider/model] | *session abort <session-id|current>");
    }

    private async Task HandleProjectCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(arg) ? "list" : arg.Trim().ToLowerInvariant();
        if (normalized is "list" or "all")
        {
            var projects = await _opencodeChat.ListProjectsAsync(CancellationToken.None).ConfigureAwait(false);
            if (projects.Count == 0)
            {
                SendImText(client, agentId, from, "No projects were reported by Opencode.");
                return;
            }

            var lines = new List<string> { $"Projects ({projects.Count}):" };
            foreach (var project in projects.Take(40))
            {
                var path = string.IsNullOrWhiteSpace(project.Path) ? "n/a" : project.Path;
                var marker = project.Current == true ? " [current]" : string.Empty;
                lines.Add($"- {project.Name} ({project.Id}) [path: {path}]{marker}");
            }

            if (projects.Count > 40)
            {
                lines.Add($"... and {projects.Count - 40} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (normalized == "current")
        {
            var current = await _opencodeChat.GetCurrentProjectAsync(CancellationToken.None).ConfigureAwait(false);
            if (current == null)
            {
                SendImText(client, agentId, from, "Opencode did not report a current project.");
                return;
            }

            var path = string.IsNullOrWhiteSpace(current.Path) ? "n/a" : current.Path;
            SendImText(client, agentId, from, $"Current project: {current.Name} ({current.Id}) [path: {path}]");
            return;
        }

        SendImText(client, agentId, from, "Usage: *projects | *project current");
    }

    private string ResolveSessionSelector(string conversationKey, string selector, bool requireExplicit)
    {
        var normalized = string.IsNullOrWhiteSpace(selector) ? "current" : NormalizeLooseQuery(selector);
        if (normalized.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            var current = _opencodeChat?.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(current))
            {
                return current;
            }

            throw new InvalidOperationException("This IM conversation does not have an active Opencode session yet. Send a normal message first, or pass an explicit session id.");
        }

        if (requireExplicit && string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("session id is required (or use 'current').");
        }

        return normalized;
    }

    private async Task<OpencodeProviderSummary?> ResolveProviderForAuthAsync(string query)
    {
        var available = await _opencodeChat!.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        return FindProviderByNameOrId(available, query);
    }

    private static int ParseOptionalMethodIndex(string[] parts, int index)
    {
        if (parts.Length <= index)
        {
            return 0;
        }

        return int.TryParse(parts[index], out var parsed) && parsed >= 0 ? parsed : 0;
    }

    private static string NormalizeLooseQuery(string value)
    {
        return value.Trim().TrimEnd('.', ',', ';', ':');
    }
}
