using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const string BuiltInBridgePrompt =
        "You are an in-world assistant running through opensim-metaverse2mcp for OpenSimulator/Second Life style worlds.\n";
    private bool _duplicatePromptSourceWarningLogged;
        
    private async Task<HarnessSendOptions?> BuildSendOptions(string conversationKey, UUID requesterAgentId = default, string? requesterName = null)
    {
        _conversationConfigs.TryGetValue(conversationKey, out var cfg);
        cfg ??= GetPersistedDefaultConversationConfigSnapshot();

        var requesterContextLayer = await BuildRequesterContextPrompt(requesterAgentId, requesterName, conversationKey).ConfigureAwait(false);
        var systemPrompt = BuildLayeredPromptText(requesterContextLayer);
        var modelId = cfg?.ModelId ?? GetStartupDefaultModelId();
        var thinkingLevel = cfg?.ThinkingLevel;

        if (cfg == null && string.IsNullOrWhiteSpace(systemPrompt) && string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return new HarnessSendOptions(modelId, thinkingLevel, systemPrompt);
    }

    private string BuildPromptStatusText()
    {
        if (!_options.PromptHandlingEnabled)
        {
            return "prompt: disabled";
        }

        var sources = new List<string>();
        if (_options.PromptBuiltInEnabled)
        {
            sources.Add("builtin");
        }

        if (_options.PromptProjectAgentsEnabled)
        {
            if (ShouldSuppressProjectAgentsPromptSource(out _, out var projectPath))
            {
                sources.Add($"project({projectPath}:disabled-duplicate)");
            }
            else
            {
                sources.Add(projectPath == null ? "project(AGENTS.md:missing)" : $"project({projectPath})");
            }
        }

        lock (_promptStateLock)
        {
            if (_options.PromptNotecardEnabled && !string.IsNullOrWhiteSpace(_activeAgentsNotecardPrompt))
            {
                sources.Add($"notecard({_activeAgentsNotecardSourceName ?? "unknown"}, {_activeAgentsNotecardItemId ?? "n/a"})");
            }

        }

        return sources.Count == 0 ? "prompt: no active sources" : "prompt sources: " + string.Join(", ", sources);
    }

    private string? BuildLayeredPromptText(string? requesterContextLayer = null)
    {
        if (!_options.PromptHandlingEnabled)
        {
            return null;
        }

        var layers = new List<string>();

        if (_options.PromptBuiltInEnabled)
        {
            layers.Add("[bridge]\n" + ResolveBuiltInBridgePromptText());
        }

        if (_options.PromptProjectAgentsEnabled)
        {
            if (!ShouldSuppressProjectAgentsPromptSource(out _, out _))
            {
                var projectAgents = TryLoadProjectAgentsPromptText();
                if (!string.IsNullOrWhiteSpace(projectAgents))
                {
                    layers.Add("[project AGENTS.md]\n" + projectAgents);
                }
            }
        }

        if (_options.PromptNotecardEnabled)
        {
            string? notecardPrompt;
            lock (_promptStateLock)
            {
                notecardPrompt = _activeAgentsNotecardPrompt;
            }

            if (!string.IsNullOrWhiteSpace(notecardPrompt))
            {
                layers.Add("[in-world AGENTS.md notecard]\n" + notecardPrompt);
            }

        }

        if (!string.IsNullOrWhiteSpace(requesterContextLayer))
        {
            layers.Add(requesterContextLayer);
        }

        return layers.Count == 0 ? null : string.Join("\n\n", layers);
    }

    private async Task<string?> BuildRequesterContextPrompt(UUID requesterAgentId, string? requesterName, string conversationKey)
    {
        var diagnosticsEnabled = IsFollowDiagnosticsEnabled();
        var trimmedName = (requesterName ?? string.Empty).Trim();
        if (requesterAgentId == UUID.Zero && trimmedName.Length == 0)
        {
            return null;
        }

        var lines = new List<string>
        {
            "[requester context]",
            "Treat first-person references ('me', 'my', 'mine', 'here') as the requester below unless explicitly overridden.",
            $"channel: {GetConversationChannelLabel(conversationKey)}",
            $"conversation_key: {conversationKey}",
            $"requester_name: {(trimmedName.Length == 0 ? "(unknown)" : trimmedName)}",
            $"requester_uuid: {(requesterAgentId == UUID.Zero ? "(unknown)" : requesterAgentId.ToString())}"
        };

        var client = _client;
        var sim = client?.Network.CurrentSim;
        var snapshot = await TryGetRequesterLocationSnapshotAsync(requesterAgentId).ConfigureAwait(false);
        if (snapshot != null)
        {
            if (snapshot.Position.HasValue)
            {
                lines.Add($"requester_position_local: {FormatPosition(snapshot.Position.Value)}");
            }

            if (snapshot.RegionHandle.HasValue)
            {
                lines.Add($"requester_region_handle: {snapshot.RegionHandle.Value}");
            }

            if (!string.IsNullOrWhiteSpace(snapshot.RegionName))
            {
                lines.Add($"requester_sim_name: {snapshot.RegionName}");
            }
        }
        else if (requesterAgentId != UUID.Zero && diagnosticsEnabled)
        {
            Console.WriteLine(
                $"[requester][diag] locator_unavailable conversation={conversationKey} requesterUuid={requesterAgentId} reason=location_unresolved");
        }

        return string.Join("\n", lines);
    }

    private async Task<AgentMonitorSnapshot?> TryGetRequesterLocationSnapshotAsync(UUID requesterAgentId)
    {
        if (requesterAgentId == UUID.Zero)
        {
            return null;
        }

        try
        {
            var monitorRead = await _agentLocator
                .ReadSingleMonitorSnapshotAsync(requesterAgentId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!monitorRead.Ok || monitorRead.Snapshot == null || monitorRead.Snapshot.Online != true)
            {
                return null;
            }

            return monitorRead.Snapshot;
        }
        catch
        {
            // Location enrichment is optional and must not fail prompt construction.
            return null;
        }
    }

    private static string FormatPosition(Vector3 position)
        => $"{position.X:F1},{position.Y:F1},{position.Z:F1}";

    private string? TryLoadProjectAgentsPromptText()
    {
        var fullPath = ResolveProjectAgentsPromptPath();
        if (fullPath == null)
        {
            return null;
        }

        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            lock (_promptStateLock)
            {
                if (_projectAgentsPromptCache != null && lastWriteUtc == _projectAgentsPromptCacheLastWriteUtc)
                {
                    return _projectAgentsPromptCache;
                }
            }

            var raw = File.ReadAllText(fullPath);
            var normalized = NormalizePromptText(raw);
            lock (_promptStateLock)
            {
                _projectAgentsPromptCache = normalized;
                _projectAgentsPromptCacheLastWriteUtc = lastWriteUtc;
            }

            return normalized;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[prompt] failed to read project AGENTS prompt file: {ex.Message}");
            return null;
        }
    }

    private string? ResolveProjectAgentsPromptPath()
    {
        var configured = (_options.PromptProjectAgentsFile ?? "AGENTS.md").Trim();
        if (configured.Length == 0)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(configured);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        // Support running from ./src while keeping strict AGENTS.md semantics at project root.
        if (string.Equals(configured, "AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            var cwd = Directory.GetCurrentDirectory();
            var parent = Directory.GetParent(cwd)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var parentPath = Path.Combine(parent, "AGENTS.md");
                if (File.Exists(parentPath))
                {
                    return parentPath;
                }
            }
        }

        return null;
    }

    private string ResolveBuiltInBridgePromptText()
    {
        var overridePath = ResolveBuiltInBridgePromptOverridePath();
        if (overridePath == null)
        {
            return ClampPromptLength(BuiltInBridgePrompt);
        }

        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(overridePath);
            lock (_promptStateLock)
            {
                if (_builtInPromptOverrideCache != null
                    && string.Equals(_builtInPromptOverrideCachePath, overridePath, StringComparison.Ordinal)
                    && lastWriteUtc == _builtInPromptOverrideCacheLastWriteUtc)
                {
                    return _builtInPromptOverrideCache;
                }
            }

            var raw = File.ReadAllText(overridePath);
            var normalized = NormalizePromptText(raw);
            lock (_promptStateLock)
            {
                _builtInPromptOverrideCache = normalized;
                _builtInPromptOverrideCacheLastWriteUtc = lastWriteUtc;
                _builtInPromptOverrideCachePath = overridePath;
            }

            return normalized;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[prompt] failed to read built-in prompt override file '{overridePath}': {ex.Message}");
            return ClampPromptLength(BuiltInBridgePrompt);
        }
    }

    private string? ResolveBuiltInBridgePromptOverridePath()
    {
        var configured = _options.OpencodeDefaultPromptPath?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(configured);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldSuppressProjectAgentsPromptSource(out string? builtInPath, out string? projectPath)
    {
        builtInPath = _options.PromptBuiltInEnabled ? ResolveBuiltInBridgePromptOverridePath() : null;
        projectPath = _options.PromptProjectAgentsEnabled ? ResolveProjectAgentsPromptPath() : null;

        if (string.IsNullOrWhiteSpace(builtInPath) || string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(builtInPath, projectPath, comparer))
        {
            return false;
        }

        lock (_promptStateLock)
        {
            if (!_duplicatePromptSourceWarningLogged)
            {
                _duplicatePromptSourceWarningLogged = true;
                Console.WriteLine(
                    $"[prompt] built-in prompt override and project AGENTS prompt resolve to the same file '{projectPath}'. Project AGENTS prompt source is being disabled to avoid duplicate merge.");
            }
        }

        return true;
    }

    private string NormalizePromptText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return ClampPromptLength(normalized);
    }

    private string ClampPromptLength(string value)
    {
        var maxChars = _options.PromptMaxChars < 512 ? 512 : _options.PromptMaxChars;
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "\n\n[prompt truncated]";
    }

    private void SetActiveAgentsNotecardPrompt(string promptText, string sourceName, string itemId)
    {
        var normalized = NormalizePromptText(promptText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_promptStateLock)
        {
            _activeAgentsNotecardPrompt = normalized;
            _activeAgentsNotecardSourceName = sourceName;
            _activeAgentsNotecardItemId = itemId;
            _activeAgentsNotecardInstalledAt = DateTimeOffset.UtcNow;
        }
    }

    private void ClearActiveAgentsNotecardPrompt()
    {
        lock (_promptStateLock)
        {
            _activeAgentsNotecardPrompt = null;
            _activeAgentsNotecardSourceName = null;
            _activeAgentsNotecardItemId = null;
            _activeAgentsNotecardInstalledAt = null;
        }
    }

    private void InvalidateProjectAgentsPromptCache()
    {
        lock (_promptStateLock)
        {
            _projectAgentsPromptCache = null;
            _projectAgentsPromptCacheLastWriteUtc = default;
        }
    }

    private static string BuildPromptPreviewText(string sourceName, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"Prompt source '{sourceName}' is empty or unavailable.";
        }

        const int maxPreviewChars = 2400;
        var preview = text.Length <= maxPreviewChars ? text : text[..maxPreviewChars] + "\n\n[prompt preview truncated]";
        return string.Join("\n", $"Prompt source: {sourceName}", preview);
    }

    internal string? GetLayeredPromptForMcp()
    {
        return BuildLayeredPromptText();
    }

    internal string GetBuiltInBridgePromptForMcp()
    {
        if (!_options.PromptHandlingEnabled || !_options.PromptBuiltInEnabled)
        {
            return "Built-in bridge prompt source is disabled.";
        }

        return ResolveBuiltInBridgePromptText();
    }

    internal string GetProjectAgentsPromptForMcp()
    {
        if (!_options.PromptHandlingEnabled || !_options.PromptProjectAgentsEnabled)
        {
            return "Project AGENTS.md prompt source is disabled.";
        }

        if (ShouldSuppressProjectAgentsPromptSource(out _, out var projectPath))
        {
            return $"Project AGENTS.md prompt source is disabled because it resolves to the same file as the built-in prompt override ({projectPath}).";
        }

        var projectPrompt = TryLoadProjectAgentsPromptText();
        return string.IsNullOrWhiteSpace(projectPrompt)
            ? "Project AGENTS.md prompt source is enabled, but no prompt text is currently available."
            : projectPrompt;
    }

    internal string GetNotecardAgentsPromptForMcp()
    {
        if (!_options.PromptHandlingEnabled || !_options.PromptNotecardEnabled)
        {
            return "In-world AGENTS.md notecard prompt source is disabled.";
        }

        string? notecardPrompt;
        string? notecardName;
        string? notecardId;
        lock (_promptStateLock)
        {
            notecardPrompt = _activeAgentsNotecardPrompt;
            notecardName = _activeAgentsNotecardSourceName;
            notecardId = _activeAgentsNotecardItemId;
        }

        if (string.IsNullOrWhiteSpace(notecardPrompt))
        {
            return "In-world AGENTS.md notecard prompt source is enabled, but no active notecard prompt is loaded.";
        }

        var source = string.IsNullOrWhiteSpace(notecardName) ? "unknown" : notecardName.Trim();
        var itemId = string.IsNullOrWhiteSpace(notecardId) ? "n/a" : notecardId.Trim();
        return $"[source: {source} ({itemId})]\n\n{notecardPrompt}";
    }
}
