using LibreMetaverse;
using LibreMetaverse.RLV;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private static readonly UUID RlvDefaultCommandSenderId = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));
    private readonly object _rlvSync = new();
    private RlvService? _rlvService;

    public RlvStatusSnapshot GetRlvStatus(int maxRestrictions = 20)
    {
        if (!_options.AllowRlv)
        {
            return new RlvStatusSnapshot(
                AllowRlv: false,
                RuntimeEnabled: false,
                EnableInstantMessageProcessing: false,
                Version: RlvService.RLVVersion,
                VersionNumber: RlvService.RLVVersionNum,
                ActiveRestrictionCount: 0,
                ReturnedRestrictionCount: 0,
                Restrictions: Array.Empty<RlvRestrictionSnapshot>());
        }

        var service = TryGetOrCreateRlvService();
        if (service == null)
        {
            return new RlvStatusSnapshot(
                AllowRlv: true,
                RuntimeEnabled: false,
                EnableInstantMessageProcessing: false,
                Version: RlvService.RLVVersion,
                VersionNumber: RlvService.RLVVersionNum,
                ActiveRestrictionCount: 0,
                ReturnedRestrictionCount: 0,
                Restrictions: Array.Empty<RlvRestrictionSnapshot>());
        }

        var restrictions = service.Restrictions.FindRestrictions();
        var clampedMax = Math.Clamp(maxRestrictions, 1, 100);
        var snapshots = restrictions
            .Take(clampedMax)
            .Select(static restriction => new RlvRestrictionSnapshot(
                Behavior: restriction.OriginalBehavior.ToString(),
                SenderId: restriction.Sender.ToString(),
                SenderName: restriction.SenderName,
                Args: restriction.Args.Select(static arg => arg?.ToString() ?? string.Empty).ToArray()))
            .ToArray();

        return new RlvStatusSnapshot(
            AllowRlv: true,
            RuntimeEnabled: service.Enabled,
            EnableInstantMessageProcessing: service.EnableInstantMessageProcessing,
            Version: RlvService.RLVVersion,
            VersionNumber: RlvService.RLVVersionNum,
            ActiveRestrictionCount: restrictions.Count,
            ReturnedRestrictionCount: snapshots.Length,
            Restrictions: snapshots);
    }

    public BotToolResult SetRlvRuntimeEnabled(bool enabled)
    {
        if (!_options.AllowRlv)
        {
            return BotToolResult.Fail("RLV is disabled by policy (ALLOW_RLV=false).");
        }

        var service = TryGetOrCreateRlvService();
        if (service == null)
        {
            return BotToolResult.Fail("RLV service is unavailable.");
        }

        service.Enabled = enabled;

        return BotToolResult.OkResult(enabled
            ? "RLV runtime is now enabled for this bot session."
            : "RLV runtime is now disabled for this bot session.");
    }

    public BotToolResult SetRlvInstantMessageIntakeEnabled(bool enabled)
    {
        if (!_options.AllowRlv)
        {
            return BotToolResult.Fail("RLV is disabled by policy (ALLOW_RLV=false).");
        }

        var service = TryGetOrCreateRlvService();
        if (service == null)
        {
            return BotToolResult.Fail("RLV service is unavailable.");
        }

        service.EnableInstantMessageProcessing = enabled;
        return BotToolResult.OkResult(enabled
            ? "RLV IM-driven intake is now enabled for this bot session."
            : "RLV IM-driven intake is now disabled for this bot session.");
    }

    public bool TryHandleRlvInstantMessage(UUID senderObjectId, string? senderObjectName, string? text)
    {
        if (!_options.AllowRlv)
        {
            return false;
        }

        var service = TryGetOrCreateRlvService();
        if (service == null || !service.Enabled || !service.EnableInstantMessageProcessing)
        {
            return false;
        }

        if (senderObjectId == UUID.Zero)
        {
            return false;
        }

        var normalizedCommand = (text ?? string.Empty).Trim();
        if (!normalizedCommand.StartsWith('@'))
        {
            return false;
        }

        var effectiveSenderName = string.IsNullOrWhiteSpace(senderObjectName)
            ? "im-object"
            : senderObjectName.Trim();

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await ProcessRlvCommandAsync(
                    normalizedCommand,
                    senderObjectId.ToString(),
                    effectiveSenderName,
                    CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"[rlv][im] {(result.Ok ? "processed" : "rejected")} {result.NormalizedCommand} from {result.SenderName} ({result.SenderId}): {result.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[rlv][im] failed to process incoming command from {effectiveSenderName} ({senderObjectId}): {ex.Message}");
            }
        });

        return true;
    }

    public async Task<RlvCommandProcessResult> ProcessRlvCommandAsync(
        string command,
        string? senderObjectId,
        string? senderObjectName,
        CancellationToken cancellationToken = default)
    {
        if (!_options.AllowRlv)
        {
            return RlvCommandProcessResult.Fail(
                "RLV is disabled by policy (ALLOW_RLV=false).",
                allowRlv: false,
                runtimeEnabled: false,
                submittedCommand: command,
                normalizedCommand: string.Empty,
                senderId: UUID.Zero.ToString(),
                senderName: string.Empty,
                processed: false,
                activeRestrictionCountBefore: 0,
                activeRestrictionCountAfter: 0);
        }

        var service = TryGetOrCreateRlvService();
        if (service == null)
        {
            return RlvCommandProcessResult.Fail(
                "RLV service is unavailable.",
                allowRlv: true,
                runtimeEnabled: false,
                submittedCommand: command,
                normalizedCommand: string.Empty,
                senderId: UUID.Zero.ToString(),
                senderName: string.Empty,
                processed: false,
                activeRestrictionCountBefore: 0,
                activeRestrictionCountAfter: 0);
        }

        if (!service.Enabled)
        {
            return RlvCommandProcessResult.Fail(
                "RLV runtime is disabled. Enable it first with RlvSetRuntimeEnabled(true).",
                allowRlv: true,
                runtimeEnabled: false,
                submittedCommand: command,
                normalizedCommand: string.Empty,
                senderId: UUID.Zero.ToString(),
                senderName: string.Empty,
                processed: false,
                activeRestrictionCountBefore: service.Restrictions.FindRestrictions().Count,
                activeRestrictionCountAfter: service.Restrictions.FindRestrictions().Count);
        }

        var submittedCommand = command ?? string.Empty;
        var normalizedCommand = submittedCommand.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCommand))
        {
            var existingCount = service.Restrictions.FindRestrictions().Count;
            return RlvCommandProcessResult.Fail(
                "RLV command cannot be empty.",
                allowRlv: true,
                runtimeEnabled: service.Enabled,
                submittedCommand: submittedCommand,
                normalizedCommand: string.Empty,
                senderId: UUID.Zero.ToString(),
                senderName: string.Empty,
                processed: false,
                activeRestrictionCountBefore: existingCount,
                activeRestrictionCountAfter: existingCount);
        }

        if (!normalizedCommand.StartsWith('@'))
        {
            normalizedCommand = "@" + normalizedCommand;
        }

        var effectiveSenderId = ResolveRlvCommandSenderId(senderObjectId);
        if (effectiveSenderId == UUID.Zero)
        {
            var existingCount = service.Restrictions.FindRestrictions().Count;
            return RlvCommandProcessResult.Fail(
                "senderObjectId must be a non-zero UUID when provided.",
                allowRlv: true,
                runtimeEnabled: service.Enabled,
                submittedCommand: submittedCommand,
                normalizedCommand: normalizedCommand,
                senderId: UUID.Zero.ToString(),
                senderName: string.Empty,
                processed: false,
                activeRestrictionCountBefore: existingCount,
                activeRestrictionCountAfter: existingCount);
        }

        var effectiveSenderName = string.IsNullOrWhiteSpace(senderObjectName)
            ? "mcp-rlv"
            : senderObjectName.Trim();

        var beforeRestrictions = service.Restrictions.FindRestrictions();
        var beforeCount = beforeRestrictions.Count;

        bool processed;
        try
        {
            processed = await service.ProcessMessageAsync(
                normalizedCommand,
                effectiveSenderId.Guid,
                effectiveSenderName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failedCount = service.Restrictions.FindRestrictions().Count;
            return RlvCommandProcessResult.Fail(
                $"RLV command execution failed: {ex.Message}",
                allowRlv: true,
                runtimeEnabled: service.Enabled,
                submittedCommand: submittedCommand,
                normalizedCommand: normalizedCommand,
                senderId: effectiveSenderId.ToString(),
                senderName: effectiveSenderName,
                processed: false,
                activeRestrictionCountBefore: beforeCount,
                activeRestrictionCountAfter: failedCount);
        }

        var afterCount = service.Restrictions.FindRestrictions().Count;
        if (!processed)
        {
            return RlvCommandProcessResult.Fail(
                "RLV command was not accepted. Check command syntax and runtime state.",
                allowRlv: true,
                runtimeEnabled: service.Enabled,
                submittedCommand: submittedCommand,
                normalizedCommand: normalizedCommand,
                senderId: effectiveSenderId.ToString(),
                senderName: effectiveSenderName,
                processed: false,
                activeRestrictionCountBefore: beforeCount,
                activeRestrictionCountAfter: afterCount);
        }

        return RlvCommandProcessResult.Success(
            "RLV command processed.",
            allowRlv: true,
            runtimeEnabled: service.Enabled,
            submittedCommand: submittedCommand,
            normalizedCommand: normalizedCommand,
            senderId: effectiveSenderId.ToString(),
            senderName: effectiveSenderName,
            processed: true,
            activeRestrictionCountBefore: beforeCount,
            activeRestrictionCountAfter: afterCount);
    }

    public RlvRestrictionListResult ListRlvRestrictions(
        string? behavior,
        string? senderObjectId,
        string? senderObjectNameContains,
        int maxRestrictions = 100)
    {
        if (!_options.AllowRlv)
        {
            return RlvRestrictionListResult.Fail(
                "RLV is disabled by policy (ALLOW_RLV=false).",
                allowRlv: false,
                runtimeEnabled: false,
                activeRestrictionCount: 0,
                matchedRestrictionCount: 0,
                returnedRestrictionCount: 0,
                behaviorFilter: NormalizeRlvFilter(behavior),
                senderIdFilter: NormalizeRlvFilter(senderObjectId),
                senderNameContainsFilter: NormalizeRlvFilter(senderObjectNameContains),
                restrictions: Array.Empty<RlvRestrictionSnapshot>());
        }

        var service = TryGetOrCreateRlvService();
        if (service == null)
        {
            return RlvRestrictionListResult.Fail(
                "RLV service is unavailable.",
                allowRlv: true,
                runtimeEnabled: false,
                activeRestrictionCount: 0,
                matchedRestrictionCount: 0,
                returnedRestrictionCount: 0,
                behaviorFilter: NormalizeRlvFilter(behavior),
                senderIdFilter: NormalizeRlvFilter(senderObjectId),
                senderNameContainsFilter: NormalizeRlvFilter(senderObjectNameContains),
                restrictions: Array.Empty<RlvRestrictionSnapshot>());
        }

        var behaviorFilter = NormalizeRlvFilter(behavior);
        var senderNameFilter = NormalizeRlvFilter(senderObjectNameContains);
        var senderIdFilter = NormalizeRlvFilter(senderObjectId);

        Guid? parsedSenderIdFilter = null;
        if (!string.IsNullOrWhiteSpace(senderIdFilter))
        {
            if (!Guid.TryParse(senderIdFilter, out var parsedSender) || parsedSender == Guid.Empty)
            {
                var activeCountOnInvalid = service.Restrictions.FindRestrictions().Count;
                return RlvRestrictionListResult.Fail(
                    "senderObjectId must be a non-zero UUID when provided.",
                    allowRlv: true,
                    runtimeEnabled: service.Enabled,
                    activeRestrictionCount: activeCountOnInvalid,
                    matchedRestrictionCount: 0,
                    returnedRestrictionCount: 0,
                    behaviorFilter: behaviorFilter,
                    senderIdFilter: senderIdFilter,
                    senderNameContainsFilter: senderNameFilter,
                    restrictions: Array.Empty<RlvRestrictionSnapshot>());
            }

            parsedSenderIdFilter = parsedSender;
        }

        var allRestrictions = service.Restrictions.FindRestrictions();
        IEnumerable<RlvRestriction> filtered = allRestrictions;

        if (!string.IsNullOrWhiteSpace(behaviorFilter))
        {
            filtered = filtered.Where(r =>
                string.Equals(
                    r.OriginalBehavior.ToString(),
                    behaviorFilter,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (parsedSenderIdFilter.HasValue)
        {
            var senderId = parsedSenderIdFilter.Value;
            filtered = filtered.Where(r => r.Sender == senderId);
        }

        if (!string.IsNullOrWhiteSpace(senderNameFilter))
        {
            filtered = filtered.Where(r =>
                (r.SenderName ?? string.Empty).Contains(senderNameFilter, StringComparison.OrdinalIgnoreCase));
        }

        var matchedRestrictions = filtered.ToList();
        var clampedMax = Math.Clamp(maxRestrictions, 1, 200);
        var snapshots = matchedRestrictions
            .Take(clampedMax)
            .Select(static restriction => new RlvRestrictionSnapshot(
                Behavior: restriction.OriginalBehavior.ToString(),
                SenderId: restriction.Sender.ToString(),
                SenderName: restriction.SenderName,
                Args: restriction.Args.Select(static arg => arg?.ToString() ?? string.Empty).ToArray()))
            .ToArray();

        return RlvRestrictionListResult.Success(
            "RLV restrictions listed.",
            allowRlv: true,
            runtimeEnabled: service.Enabled,
            activeRestrictionCount: allRestrictions.Count,
            matchedRestrictionCount: matchedRestrictions.Count,
            returnedRestrictionCount: snapshots.Length,
            behaviorFilter: behaviorFilter,
            senderIdFilter: senderIdFilter,
            senderNameContainsFilter: senderNameFilter,
            restrictions: snapshots);
    }

    private UUID ResolveRlvCommandSenderId(string? senderObjectId)
    {
        if (!string.IsNullOrWhiteSpace(senderObjectId))
        {
            return UUID.TryParse(senderObjectId.Trim(), out var parsed) && parsed != UUID.Zero
                ? parsed
                : UUID.Zero;
        }

        var selfAgentId = _client?.Self?.AgentID ?? UUID.Zero;
        if (selfAgentId != UUID.Zero)
        {
            return selfAgentId;
        }

        return RlvDefaultCommandSenderId;
    }

    private RlvService? TryGetOrCreateRlvService()
    {
        if (!_options.AllowRlv)
        {
            return null;
        }

        lock (_rlvSync)
        {
            if (_rlvService != null)
            {
                return _rlvService;
            }

            var queryCallbacks = new NoopRlvQueryCallbacks();
            var actionCallbacks = new NoopRlvActionCallbacks();
            _rlvService = new RlvService(queryCallbacks, actionCallbacks, enabled: false)
            {
                EnableInstantMessageProcessing = false
            };

            return _rlvService;
        }
    }

    private static string? NormalizeRlvFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private sealed class NoopRlvQueryCallbacks : IRlvQueryCallbacks
    {
        public Task<bool> ObjectExistsAsync(Guid objectId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> IsSittingAsync(CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<(bool Success, string EnvironmentSettingValue)> TryGetEnvironmentSettingValueAsync(string settingName, CancellationToken cancellationToken)
            => Task.FromResult((false, string.Empty));

        public Task<(bool Success, string DebugSettingValue)> TryGetDebugSettingValueAsync(string settingName, CancellationToken cancellationToken)
            => Task.FromResult((false, string.Empty));

        public Task<(bool Success, Guid SitId)> TryGetSitIdAsync(CancellationToken cancellationToken)
            => Task.FromResult((false, Guid.Empty));

        public Task<(bool Success, CameraSettings? CameraSettings)> TryGetCameraSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult<(bool Success, CameraSettings? CameraSettings)>((false, null));

        public Task<(bool Success, string ActiveGroupName)> TryGetActiveGroupNameAsync(CancellationToken cancellationToken)
            => Task.FromResult((false, string.Empty));

        public Task<(bool Success, InventoryMap? InventoryMap)> TryGetInventoryMapAsync(CancellationToken cancellationToken)
            => Task.FromResult<(bool Success, InventoryMap? InventoryMap)>((false, null));
    }

    private sealed class NoopRlvActionCallbacks : IRlvActionCallbacks
    {
        public Task SendReplyAsync(int channel, string message, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SendInstantMessageAsync(Guid targetUser, string message, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetRotAsync(float angleInRadians, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AdjustHeightAsync(float distance, float factor, float delta, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetCamFOVAsync(float fovInRadians, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task TpToAsync(float x, float y, float z, string? regionName, float? lookat, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SitAsync(Guid target, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UnsitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SitGroundAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task RemOutfitAsync(IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AttachAsync(IReadOnlyList<AttachmentRequest> itemsToAttach, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task DetachAsync(IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetGroupAsync(Guid groupId, string? roleName, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetGroupAsync(string groupName, string? roleName, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetEnvAsync(string settingName, string settingValue, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task SetDebugAsync(string settingName, string settingValue, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

internal sealed record RlvStatusSnapshot(
    bool AllowRlv,
    bool RuntimeEnabled,
    bool EnableInstantMessageProcessing,
    string Version,
    string VersionNumber,
    int ActiveRestrictionCount,
    int ReturnedRestrictionCount,
    IReadOnlyList<RlvRestrictionSnapshot> Restrictions);

internal sealed record RlvRestrictionSnapshot(
    string Behavior,
    string SenderId,
    string SenderName,
    IReadOnlyList<string> Args);

internal sealed record RlvCommandProcessResult(
    bool Ok,
    string Message,
    bool AllowRlv,
    bool RuntimeEnabled,
    string SubmittedCommand,
    string NormalizedCommand,
    string SenderId,
    string SenderName,
    bool Processed,
    int ActiveRestrictionCountBefore,
    int ActiveRestrictionCountAfter)
{
    public static RlvCommandProcessResult Success(
        string message,
        bool allowRlv,
        bool runtimeEnabled,
        string submittedCommand,
        string normalizedCommand,
        string senderId,
        string senderName,
        bool processed,
        int activeRestrictionCountBefore,
        int activeRestrictionCountAfter)
        => new(
            true,
            message,
            allowRlv,
            runtimeEnabled,
            submittedCommand,
            normalizedCommand,
            senderId,
            senderName,
            processed,
            activeRestrictionCountBefore,
            activeRestrictionCountAfter);

    public static RlvCommandProcessResult Fail(
        string message,
        bool allowRlv,
        bool runtimeEnabled,
        string submittedCommand,
        string normalizedCommand,
        string senderId,
        string senderName,
        bool processed,
        int activeRestrictionCountBefore,
        int activeRestrictionCountAfter)
        => new(
            false,
            message,
            allowRlv,
            runtimeEnabled,
            submittedCommand,
            normalizedCommand,
            senderId,
            senderName,
            processed,
            activeRestrictionCountBefore,
            activeRestrictionCountAfter);
}

internal sealed record RlvRestrictionListResult(
    bool Ok,
    string Message,
    bool AllowRlv,
    bool RuntimeEnabled,
    int ActiveRestrictionCount,
    int MatchedRestrictionCount,
    int ReturnedRestrictionCount,
    string? BehaviorFilter,
    string? SenderIdFilter,
    string? SenderNameContainsFilter,
    IReadOnlyList<RlvRestrictionSnapshot> Restrictions)
{
    public static RlvRestrictionListResult Success(
        string message,
        bool allowRlv,
        bool runtimeEnabled,
        int activeRestrictionCount,
        int matchedRestrictionCount,
        int returnedRestrictionCount,
        string? behaviorFilter,
        string? senderIdFilter,
        string? senderNameContainsFilter,
        IReadOnlyList<RlvRestrictionSnapshot> restrictions)
        => new(
            true,
            message,
            allowRlv,
            runtimeEnabled,
            activeRestrictionCount,
            matchedRestrictionCount,
            returnedRestrictionCount,
            behaviorFilter,
            senderIdFilter,
            senderNameContainsFilter,
            restrictions);

    public static RlvRestrictionListResult Fail(
        string message,
        bool allowRlv,
        bool runtimeEnabled,
        int activeRestrictionCount,
        int matchedRestrictionCount,
        int returnedRestrictionCount,
        string? behaviorFilter,
        string? senderIdFilter,
        string? senderNameContainsFilter,
        IReadOnlyList<RlvRestrictionSnapshot> restrictions)
        => new(
            false,
            message,
            allowRlv,
            runtimeEnabled,
            activeRestrictionCount,
            matchedRestrictionCount,
            returnedRestrictionCount,
            behaviorFilter,
            senderIdFilter,
            senderNameContainsFilter,
            restrictions);
}