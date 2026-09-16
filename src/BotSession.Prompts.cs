using LibreMetaverse;
using LibreMetaverse.Assets;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const string BuiltInBridgePrompt =
        "You are an in-world assistant running through opensim-metaverse2mcp for OpenSimulator/Second Life style worlds.\n" +
        "Environment basics:\n" +
        "- Make sure you say 'I did ...' instead of 'You did ..' when you as the bot are affected by the action.\n" +
        "- Avatars, regions, parcels, prim objects, inventory, scripts, and environment settings are stateful and shared.\n" +
        "- Simulator/cache state may be stale; verify current state before mutating it.\n" +
        "Tooling basics:\n" +
        "- Use metaverse MCP tools for avatar/world operations (movement, prims, inventory, scripts, environment).\n" +
        "Operating rules:\n" +
        "- Prefer safe and reversible actions.\n" +
        "- Confirm destructive or high-impact actions first (delete, bulk changes, ownership/permission changes, restarts).\n" +
        "- Attachment and wearable controls are different: use attachment tools for attachments/objects and wearable tools for clothing/body layers.\n" +
        "- If asked to 'detach/remove attachments', use appearance_detach_all_attachments_except (empty keep filters unless exclusions are requested), then re-check with appearance_list_attachment_point_mappings. Avoid item-by-item detach loops unless explicitly requested.\n" +
        "- If asked to remove everything worn, use appearance_detach_and_remove_all_worn_deterministic, then re-check and report both attachment and wearable sections separately.\n" +
        "- When requester identity metadata is provided for IM, resolve pronouns like 'me', 'my', and 'here' to that requester unless they explicitly override it.\n" +
        "- Ask concise clarifying questions when instructions are ambiguous or missing required identifiers.\n" +
        "- For multi-step tasks, inspect -> plan -> execute -> verify and report results clearly.\n" +
        "- Respect handler and policy restrictions configured by the bridge.";
        
    private HarnessSendOptions? BuildSendOptions(string conversationKey, UUID requesterAgentId = default, string? requesterName = null)
    {
        _conversationConfigs.TryGetValue(conversationKey, out var cfg);
        cfg ??= GetPersistedDefaultConversationConfigSnapshot();

        var requesterContextLayer = BuildRequesterContextPrompt(requesterAgentId, requesterName, conversationKey);
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
            var projectPath = ResolveProjectAgentsPromptPath();
            sources.Add(projectPath == null ? "project(AGENTS.md:missing)" : $"project({projectPath})");
        }

        lock (_promptStateLock)
        {
            if (_options.PromptNotecardEnabled && !string.IsNullOrWhiteSpace(_activeAgentsNotecardPrompt))
            {
                sources.Add($"notecard({_activeAgentsNotecardSourceName ?? "unknown"}, {_activeAgentsNotecardItemId ?? "n/a"})");
            }

            if (_options.PromptNotecardEnabled && !string.IsNullOrWhiteSpace(_bridgeAgentsPrompt))
            {
                var bridgeObject = _bridgeAgentsPromptObjectId == UUID.Zero ? "(unknown)" : _bridgeAgentsPromptObjectId.ToString();
                sources.Add($"bridge-object(AGENTS.md, object={bridgeObject}, {_bridgeAgentsPromptItemId ?? "n/a"})");
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
            var projectAgents = TryLoadProjectAgentsPromptText();
            if (!string.IsNullOrWhiteSpace(projectAgents))
            {
                layers.Add("[project AGENTS.md]\n" + projectAgents);
            }
        }

        if (_options.PromptNotecardEnabled)
        {
            string? notecardPrompt;
            string? bridgePrompt;
            lock (_promptStateLock)
            {
                notecardPrompt = _activeAgentsNotecardPrompt;
                bridgePrompt = _bridgeAgentsPrompt;
            }

            if (!string.IsNullOrWhiteSpace(notecardPrompt))
            {
                layers.Add("[in-world AGENTS.md notecard]\n" + notecardPrompt);
            }

            if (!string.IsNullOrWhiteSpace(bridgePrompt))
            {
                layers.Add("[dialog bridge object AGENTS.md]\n" + bridgePrompt);
            }
        }

        if (!string.IsNullOrWhiteSpace(requesterContextLayer))
        {
            layers.Add(requesterContextLayer);
        }

        return layers.Count == 0 ? null : string.Join("\n\n", layers);
    }

    private string? BuildRequesterContextPrompt(UUID requesterAgentId, string? requesterName, string conversationKey)
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
        var hint = TryGetRequesterImLocationHint(conversationKey, requesterAgentId);
        if (hint.HasValue)
        {
            var hintValue = hint.Value;
            var hintPosition = FormatPosition(hintValue.Position);
            if (hintValue.Position != Vector3.Zero)
            {
                lines.Add($"requester_position_local: {hintPosition}");
            }
            if (hintValue.RegionId != UUID.Zero)
            {
                lines.Add($"requester_region_uuid: {hintValue.RegionId}");
            }

            if (client != null)
            {
                var hintSim = TryFindSimulatorByRegionId(client, hintValue.RegionId);
                if (hintSim != null)
                {
                    lines.Add($"requester_sim_name: {hintSim.Name}");
                }

                if (sim != null && hintValue.RegionId != UUID.Zero && hintValue.RegionId == sim.ID)
                {
                    var distance = Vector3.Distance(client.Self.SimPosition, hintValue.Position);
                    lines.Add($"requester_distance_to_bot_m: {distance:F1}");
                    Console.WriteLine($"[prompt:location] conversation={conversationKey} requester={trimmedName} uuid={requesterAgentId} source=im sim={sim.Name} position={hintPosition} distance_m={distance:F1}");
                }
                else
                {
                    Console.WriteLine($"[prompt:location] conversation={conversationKey} requester={trimmedName} uuid={requesterAgentId} source=im sim={(sim?.Name ?? "(unknown)")} requester_region_uuid={(hintValue.RegionId == UUID.Zero ? "(unknown)" : hintValue.RegionId.ToString())} position={hintPosition} distance_m=n/a");
                }
            }
        }

        if (sim != null)
        {
            if (requesterAgentId != UUID.Zero && ( !hint.HasValue || hint.Value.RegionId == UUID.Zero || hint.Value.Position == Vector3.Zero))
            {
                var requesterAvatar = sim.ObjectsAvatars.Values
                    .FirstOrDefault(avatar => avatar != null && avatar.ID == requesterAgentId);
                if (requesterAvatar != null)
                {
                    var position = FormatPosition(requesterAvatar.Position);
                    lines.Add($"requester_position_local: {position}");
                    if (client != null)
                    {
                        var distance = Vector3.Distance(client.Self.SimPosition, requesterAvatar.Position);
                        lines.Add($"requester_distance_to_bot_m: {distance:F1}");
                        Console.WriteLine($"[prompt:location] conversation={conversationKey} requester={trimmedName} uuid={requesterAgentId} sim={sim.Name} position={position} distance_m={distance:F1}");
                    }

                    if (diagnosticsEnabled)
                    {
                        Console.WriteLine(
                            $"[requester][diag] resolved_in_current_sim conversation={conversationKey} requesterUuid={requesterAgentId} sim={DescribeSimulator(sim)} localId={requesterAvatar.LocalID} pos={FormatPosition(requesterAvatar.Position)}");
                    }
                }
                else if (diagnosticsEnabled && client != null)
                {
                    if (TryFindAvatarByIdAcrossSims(client, requesterAgentId, out var seenSim, out var seenAvatar))
                    {
                        Console.WriteLine(
                            $"[requester][diag] missing_from_current_sim conversation={conversationKey} requesterUuid={requesterAgentId} currentSim={DescribeSimulator(sim)} seenSim={DescribeSimulator(seenSim)} seenLocalId={seenAvatar!.LocalID} seenPos={FormatPosition(seenAvatar.Position)} botPos={FormatPosition(client.Self.SimPosition)}");
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[requester][diag] not_visible_any_sim conversation={conversationKey} requesterUuid={requesterAgentId} currentSim={DescribeSimulator(sim)} knownSims={client.Network.Simulators.Count}");
                    }
                }
            }

            var nearby = sim.ObjectsAvatars.Values
                .Where(avatar => avatar != null && avatar.ID != UUID.Zero && avatar.ID != client?.Self.AgentID)
                .Select(avatar =>
                {
                    var name = string.IsNullOrWhiteSpace(avatar!.Name) ? "(unknown)" : avatar.Name.Trim();
                    var distance = client == null ? float.NaN : Vector3.Distance(client.Self.SimPosition, avatar.Position);
                    return $"- {name} ({avatar.ID}) distance_m={(float.IsNaN(distance) ? "n/a" : distance.ToString("F1"))}";
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();

            if (nearby.Count > 0)
            {
                lines.Add("nearby_avatars:");
                lines.AddRange(nearby);
            }
        }

        return string.Join("\n", lines);
    }

    private RequesterImLocationHint? TryGetRequesterImLocationHint(string conversationKey, UUID requesterAgentId)
    {
        if (requesterAgentId == UUID.Zero)
        {
            return null;
        }

        if (!_requesterImLocationHintByConversation.TryGetValue(conversationKey, out var hint))
        {
            return null;
        }

        if (hint.RequesterAgentId != requesterAgentId)
        {
            return null;
        }

        // Ignore very old hints so stale IM metadata does not outlive long-running sessions.
        if (DateTimeOffset.UtcNow - hint.ObservedAt > TimeSpan.FromMinutes(10))
        {
            return null;
        }

        return hint;
    }

    private static Simulator? TryFindSimulatorByRegionId(GridClient client, UUID regionId)
    {
        if (regionId == UUID.Zero)
        {
            return null;
        }

        foreach (var candidate in client.Network.Simulators)
        {
            if (candidate.ID == regionId)
            {
                return candidate;
            }
        }

        return null;
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

    private void SetBridgeAgentsPrompt(string promptText, string sourceName, UUID objectId, string itemId)
    {
        var normalized = NormalizePromptText(promptText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_promptStateLock)
        {
            _bridgeAgentsPrompt = normalized;
            _bridgeAgentsPromptSourceName = sourceName;
            _bridgeAgentsPromptItemId = itemId;
            _bridgeAgentsPromptObjectId = objectId;
            _bridgeAgentsPromptInstalledAt = DateTimeOffset.UtcNow;
        }
    }

    private void QueueBridgeAgentsPromptProbe(UUID bridgeObjectId, string senderName)
    {
        if (!_options.PromptHandlingEnabled || !_options.PromptNotecardEnabled || bridgeObjectId == UUID.Zero)
        {
            return;
        }

        lock (_promptStateLock)
        {
            if (_bridgeAgentsProbeInFlight && _bridgeAgentsProbeObjectId == bridgeObjectId)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_bridgeAgentsPrompt) && _bridgeAgentsPromptObjectId == bridgeObjectId)
            {
                return;
            }

            _bridgeAgentsProbeInFlight = true;
            _bridgeAgentsProbeObjectId = bridgeObjectId;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await TryInstallAgentsPromptFromBridgeObjectAsync(bridgeObjectId, senderName).ConfigureAwait(false);
            }
            finally
            {
                lock (_promptStateLock)
                {
                    _bridgeAgentsProbeInFlight = false;
                }
            }
        });
    }

    private async Task TryInstallAgentsPromptFromBridgeObjectAsync(UUID bridgeObjectId, string senderName)
    {
        var attempts = 0;
        while (attempts < 5)
        {
            attempts++;
            await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);

            await _actionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var client = _client;
                var sim = client?.Network.CurrentSim;
                if (client == null || sim == null)
                {
                    return;
                }

                Primitive? bridgePrim = null;
                foreach (var prim in sim.ObjectsPrimitives.Values)
                {
                    if (prim.ID == bridgeObjectId)
                    {
                        bridgePrim = prim;
                        break;
                    }
                }

                if (bridgePrim == null)
                {
                    continue;
                }

                var entries = await client.Inventory
                    .GetTaskInventoryAsync(bridgeObjectId, bridgePrim.LocalID, sim, CancellationToken.None)
                    .ConfigureAwait(false);

                var agentsItem = entries
                    .OfType<InventoryItem>()
                    .FirstOrDefault(item => string.Equals(item.Name?.Trim(), "AGENTS.md", StringComparison.OrdinalIgnoreCase)
                        && item.AssetType == AssetType.Notecard);
                if (agentsItem == null)
                {
                    lock (_promptStateLock)
                    {
                        if (_bridgeAgentsPromptObjectId == bridgeObjectId)
                        {
                            _bridgeAgentsPrompt = null;
                            _bridgeAgentsPromptSourceName = null;
                            _bridgeAgentsPromptItemId = null;
                            _bridgeAgentsPromptInstalledAt = null;
                        }
                    }

                    Console.WriteLine($"[prompt] bridge object {bridgeObjectId} has no AGENTS.md task notecard.");
                    return;
                }

                var ownerId = bridgePrim.Properties?.OwnerID ?? client.Self.AgentID;
                var notecardAsset = await client.Assets.RequestInventoryAssetAsync(
                    agentsItem.AssetUUID,
                    agentsItem.UUID,
                    bridgeObjectId,
                    ownerId,
                    AssetType.Notecard,
                    true,
                    UUID.Random(),
                    CancellationToken.None).ConfigureAwait(false);

                if (notecardAsset?.AssetData == null || notecardAsset.AssetData.Length == 0)
                {
                    Console.WriteLine($"[prompt] failed to download bridge AGENTS.md from object {bridgeObjectId}, item={agentsItem.UUID}.");
                    return;
                }

                var notecard = new AssetNotecard(agentsItem.AssetUUID, notecardAsset.AssetData);
                if (!notecard.Decode() || string.IsNullOrWhiteSpace(notecard.BodyText))
                {
                    Console.WriteLine($"[prompt] failed to decode bridge AGENTS.md from object {bridgeObjectId}, item={agentsItem.UUID}.");
                    return;
                }

                SetBridgeAgentsPrompt(notecard.BodyText, senderName, bridgeObjectId, agentsItem.UUID.ToString());
                Console.WriteLine($"[prompt] installed bridge-object AGENTS.md prompt from '{senderName}', object={bridgeObjectId}, item={agentsItem.UUID}.");
                return;
            }
            catch (Exception ex)
            {
                if (attempts >= 5)
                {
                    Console.WriteLine($"[prompt] failed to probe bridge object AGENTS.md: {ex.Message}");
                }
            }
            finally
            {
                _actionGate.Release();
            }
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
}
