using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private bool IsHandlerRestricted()
    {
        return GetConfiguredHandlerNames().Count > 0
            || !string.IsNullOrWhiteSpace(_parentFullName);
    }

    private bool IsHandlerAgent(UUID agentId, string? avatarName)
    {
        if (!IsHandlerRestricted())
        {
            return false;
        }

        UUID handlerAgentId;
        lock (_controlGroupStateLock)
        {
            handlerAgentId = _handlerAgentId;
        }

        if (handlerAgentId != UUID.Zero && agentId != UUID.Zero && handlerAgentId == agentId)
        {
            return true;
        }

        if (!IsHandlerAvatar(avatarName))
        {
            return false;
        }

        if (agentId != UUID.Zero)
        {
            lock (_controlGroupStateLock)
            {
                _handlerAgentId = agentId;
            }
        }

        return true;
    }

    private bool IsControlGroupId(UUID groupId)
    {
        if (groupId == UUID.Zero)
        {
            return false;
        }

        UUID controlGroupId;
        lock (_controlGroupStateLock)
        {
            controlGroupId = _controlGroupId;
        }

        return controlGroupId != UUID.Zero && controlGroupId == groupId;
    }

    private bool TryGetActiveConversationRequester(out UUID requesterAgentId, out string requesterName)
    {
        requesterAgentId = UUID.Zero;
        requesterName = string.Empty;

        var conversationKey = _ambientConversationKey.Value;
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return false;
        }

        if (!_conversationRouteByKey.TryGetValue(conversationKey, out var route))
        {
            return false;
        }

        requesterAgentId = route.SpeakerAgentId;
        requesterName = route.SpeakerName;
        return requesterAgentId != UUID.Zero || !string.IsNullOrWhiteSpace(requesterName);
    }

    private async Task<bool> IsAgentInControlGroupAsync(GridClient client, UUID agentId, CancellationToken cancellationToken)
    {
        if (agentId == UUID.Zero)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_parentFullName))
        {
            // Parent-controlled bots skip C&C bootstrap and group authorization.
            return false;
        }

        var controlGroupId = await EnsureControlGroupExistsAsync(client, cancellationToken).ConfigureAwait(false);
        if (controlGroupId == UUID.Zero)
        {
            return false;
        }

        var requestId = client.Groups.RequestGroupMembers(controlGroupId);
        var membersReply = await WaitForGroupMembersReplyAsync(client, requestId, controlGroupId, cancellationToken).ConfigureAwait(false);
        return membersReply != null && membersReply.Members.ContainsKey(agentId);
    }

    private async Task<(bool Allowed, string Error)> ValidateFriendshipTargetPolicyAsync(GridClient client, UUID targetAgentId, string? targetName, CancellationToken cancellationToken)
    {
        if (!IsHandlerRestricted())
        {
            return (false, "Handler/parent identity is not configured; friendship policy enforcement requires OPENSIM_HANDLER_CONFIG or OPENSIM_SPAWNER_PARENT.");
        }

        if (IsHandlerAgent(targetAgentId, targetName))
        {
            return (true, string.Empty);
        }

        var inControlGroup = await IsAgentInControlGroupAsync(client, targetAgentId, cancellationToken).ConfigureAwait(false);
        if (inControlGroup)
        {
            return (true, string.Empty);
        }

        return (false, "Friendship is restricted: only the configured handler/parent controller or members of this bot's C&C group are allowed.");
    }

    private (bool Allowed, string Error) ValidateControlGroupAdditionPolicy(UUID targetAgentId, string? targetName)
    {
        if (!IsHandlerRestricted())
        {
            return (false, "Handler/parent identity is not configured; refusing C&C group membership changes.");
        }

        if (IsHandlerAgent(targetAgentId, targetName))
        {
            return (true, string.Empty);
        }

        if (!TryGetActiveConversationRequester(out var requesterAgentId, out var requesterName))
        {
            return (false, "Cannot verify requester identity for C&C group membership change.");
        }

        if (!IsHandlerAgent(requesterAgentId, requesterName))
        {
            return (false, "Only the configured handler or parent controller may add other avatars to this bot's C&C group.");
        }

        return (true, string.Empty);
    }

    private bool IsHandlerAvatar(string? avatarName)
    {
        var handlers = GetConfiguredHandlerNames();
        if (handlers.Count == 0 && string.IsNullOrWhiteSpace(_parentFullName))
        {
            return false;
        }

        var normalized = NormalizeAvatarName(avatarName);
        if (handlers.Contains(normalized))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(_parentFullName)
            && normalized.Equals(_parentFullName, StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<string> GetConfiguredHandlerNames()
    {
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.Exists(_handlerConfigPath)
                ? File.GetLastWriteTimeUtc(_handlerConfigPath)
                : DateTime.MinValue;
        }
        catch (Exception ex)
        {
            lastWriteUtc = DateTime.MinValue;
            MaybeLogHandlerConfigError($"Unable to stat handler config '{_handlerConfigPath}': {ex.Message}");
        }

        lock (_handlerConfigLock)
        {
            // Do not reuse a cached parse/read failure forever when timestamp is unchanged.
            if (_handlerConfigCacheInitialized
                && lastWriteUtc == _handlerConfigLastWriteUtc
                && _handlerConfigLastError == null)
            {
                return _handlerNames;
            }
        }

        HashSet<string> parsed = new(StringComparer.OrdinalIgnoreCase);
        string? parseError = null;

        if (lastWriteUtc != DateTime.MinValue)
        {
            try
            {
                var json = File.ReadAllText(_handlerConfigPath);
                using var document = JsonDocument.Parse(json);
                parsed = ParseConfiguredHandlers(document.RootElement);
            }
            catch (Exception ex)
            {
                parseError = $"Unable to parse handler config '{_handlerConfigPath}': {ex.Message}";
            }
        }
        else
        {
            parseError = $"Handler config file not found at '{_handlerConfigPath}'.";
        }

        lock (_handlerConfigLock)
        {
            _handlerConfigCacheInitialized = true;
            _handlerConfigLastWriteUtc = lastWriteUtc;
            _handlerNames = parsed;
            if (parseError == null)
            {
                _handlerConfigLastError = null;
            }
        }

        if (parseError != null)
        {
            MaybeLogHandlerConfigError(parseError);
        }

        return parsed;
    }

    private HashSet<string> GetConfiguredHandlerNamesOnStartup()
    {
        var handlers = GetConfiguredHandlerNames();
        if (handlers.Count > 0)
        {
            return handlers;
        }

        // Best-effort warmup for containerized shared volumes where the file can appear
        // or become readable just after process start.
        const int attempts = 8;
        const int delayMs = 250;
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            Thread.Sleep(delayMs);
            handlers = GetConfiguredHandlerNames();
            if (handlers.Count > 0)
            {
                return handlers;
            }
        }

        return handlers;
    }

    private void MaybeLogHandlerConfigError(string message)
    {
        lock (_handlerConfigLock)
        {
            if (string.Equals(_handlerConfigLastError, message, StringComparison.Ordinal))
            {
                return;
            }

            _handlerConfigLastError = message;
        }

        Console.WriteLine($"[handler-config] {message}");
    }

    private static HashSet<string> ParseConfiguredHandlers(JsonElement root)
    {
        var handlers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (root.ValueKind != JsonValueKind.Array)
        {
            return handlers;
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var first = ReadJsonString(entry, "handlerFirst");
            var last = ReadJsonString(entry, "handlerLast");
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
            {
                continue;
            }

            handlers.Add(NormalizeAvatarName($"{first} {last}"));
        }

        return handlers;
    }

    private static string? ReadJsonString(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string NormalizeAvatarName(string? avatarName)
    {
        if (string.IsNullOrWhiteSpace(avatarName))
        {
            return string.Empty;
        }

        return string.Join(' ', avatarName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
