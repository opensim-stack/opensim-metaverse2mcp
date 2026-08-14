using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession : IDisposable
{
    private enum PendingPromptKind
    {
        Permission,
        Question
    }

    private sealed record PendingScriptDialog(
        string Id,
        string Message,
        string ObjectName,
        UUID ObjectId,
        int Channel,
        IReadOnlyList<string> Buttons,
        DateTimeOffset ReceivedAt);

    private sealed record PendingDialogPromptWait(
        PendingPromptKind Kind,
        string SessionId,
        string RequestId,
        UUID AgentId,
        string From,
        OpencodePendingPermission? Permission,
        OpencodePendingQuestion? Question,
        CancellationTokenSource TimeoutCts);

    private sealed record PendingTextPromptReply(
        PendingPromptKind Kind,
        string SessionId,
        string RequestId,
        UUID AgentId,
        string From,
        OpencodePendingPermission? Permission,
        OpencodePendingQuestion? Question,
        DateTimeOffset ActivatedAt);

    private readonly AppOptions _options;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly IOpencodeChatClient? _opencodeChat;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentImEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<UUID, DateTimeOffset> _primPropertiesRefreshedAtByObjectId = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _imConversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImConversationConfig> _imConversationConfigs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpencodeUsageSummary> _latestUsageByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _latestPendingPermissionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _latestPendingQuestionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _announcedPendingPermissionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _announcedPendingQuestionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingScriptDialog> _latestScriptDialogByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingDialogPromptWait> _pendingDialogPromptWaitByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingTextPromptReply> _pendingTextPromptReplyByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentDialogBridgeReplies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UUID> _conversationAgentByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _conversationNameByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightRequestCtsByConversation = new(StringComparer.Ordinal);
    private readonly string? _handlerFullName;
    private readonly object _promptStateLock = new();
    private readonly object _recentImSpeakerLock = new();
    private readonly object _dialogBridgeTrustLock = new();
    private readonly object _opencodeSessionStateLock = new();
    private readonly object _typingStateLock = new();
    private readonly object _hoverStateLock = new();
    private readonly object _dialogBridgeAutoProvisionLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycleCts = new();

    private string? _projectAgentsPromptCache;
    private DateTime _projectAgentsPromptCacheLastWriteUtc;
    private string? _activeAgentsNotecardPrompt;
    private string? _activeAgentsNotecardSourceName;
    private string? _activeAgentsNotecardItemId;
    private DateTimeOffset? _activeAgentsNotecardInstalledAt;
    private UUID _lastImSpeakerAgentId = UUID.Zero;
    private string? _lastImSpeakerName;
    private string? _lastImConversationKey;
    private long _scriptDialogSequence;
    private UUID _trustedDialogBridgeObjectId = UUID.Zero;
    private UUID _trustedDialogBridgeOwnerId = UUID.Zero;
    private bool _lslDialogBridgeRequireTrustedSender = true;
    private readonly ConcurrentDictionary<string, byte> _busyOpencodeSessions = new(StringComparer.OrdinalIgnoreCase);
    private string? _restoredOpencodeSessionId;
    private ImConversationConfig? _persistedOpencodeDefaultConfig;
    private DateTimeOffset _lastTypingPulseAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _typingStopCts;
    private bool _typingIndicatorActive;
    private DateTimeOffset _lastHoverBusyUpdateAt = DateTimeOffset.MinValue;
    private int _busyHoverDots;
    private int _dialogBridgeAutoProvisionInFlight;
    private DateTimeOffset _lastDialogBridgeAutoProvisionAttemptAt = DateTimeOffset.MinValue;
    private const int LslDialogBridgeRequestChannel = -919191;
    private const string LslDialogBridgeRequestPrefix = "dlgreq";
    private const string LslDialogBridgeReplyPrefix = "dlgrep";
    private const string LslDialogBridgePermissionRequestPrefix = "perm:";
    private const int LslDialogBridgeMaxPayloadLength = 220;
    private const string LslDialogBridgeHoverRequestPrefix = "hovreq";
    private const int TypingPulseMinimumIntervalMs = 2000;
    private const int TypingStopDelayMs = 2500;
    private const int HoverBusyUpdateMinimumIntervalMs = 600;
    private const float WalkProgressThresholdMeters = 1.5f;
    private const float WalkStuckWindowSeconds = 6f;
    private const int WalkRecoveryMaxAttempts = 5;
    private const bool EnableWalkTeleportFallback = true;
    private static readonly string[] DoorHintKeywords = new[] { "door", "gate", "entry", "entrance", "open", "lobby" };
    private static readonly IReadOnlyList<string> LslPermissionDialogOptions = new[] { "yes", "no", "yes always", "no always" };

    private const string BuiltInBridgePrompt =
        "You are an in-world assistant running through opensim-metaverse2mcp for OpenSimulator/Second Life style worlds.\n" +
        "Environment basics:\n" +
        "- Make sure you say 'I did ...' instead of 'You did ..' when you as the bot are affected by the action.\n" +
        "- Avatars, regions, parcels, prim objects, inventory, scripts, and environment settings are stateful and shared.\n" +
        "- Simulator/cache state may be stale; verify current state before mutating it.\n" +
        "Tooling basics:\n" +
        "- Use metaverse MCP tools for avatar/world operations (movement, prims, inventory, scripts, environment).\n" +
        "- Use console2mcp tools for simulator administration tasks when needed.\n" +
        "Operating rules:\n" +
        "- Prefer safe and reversible actions.\n" +
        "- Confirm destructive or high-impact actions first (delete, bulk changes, ownership/permission changes, restarts).\n" +
        "- Attachment and wearable controls are different: use attachment tools for attachments/objects and wearable tools for clothing/body layers.\n" +
        "- If asked to 'detach/remove attachments', use appearance_detach_all_attachments_except (empty keep filters unless exclusions are requested), then re-check with appearance_list_attachment_point_mappings. Avoid item-by-item detach loops unless explicitly requested.\n" +
        "- If asked to remove everything worn, use appearance_detach_and_remove_all_worn_deterministic, then re-check and report both attachment and wearable sections separately.\n" +
        "- Ask concise clarifying questions when instructions are ambiguous or missing required identifiers.\n" +
        "- For multi-step tasks, inspect -> plan -> execute -> verify and report results clearly.\n" +
        "- Respect handler and policy restrictions configured by the bridge.";

    private GridClient? _client;
    private bool _connected;
    private string _lastLoginMessage = string.Empty;
    private int _reconnectLoopActive;

    private readonly object _movementLock = new();
    private CancellationTokenSource? _movementAutoStopCts;
    private CancellationTokenSource? _followCts;
    private Task? _followTask;
    private string? _followTargetDescription;

    public BotSession(AppOptions options)
    {
        _options = options;
        InitializeDialogBridgeTrustFromOptions();
        _handlerFullName = BuildHandlerFullName(_options.OpencodeHandlerFirstName, _options.OpencodeHandlerLastName);
        if (_options.OpencodeChatEnabled)
        {
            _opencodeChat = new OpencodeChatClient(_options);
            _opencodeChat.SessionStatusChanged += OnOpencodeSessionStatusChanged;
            _opencodeChat.MessagePartUpdated += OnOpencodeMessagePartUpdated;
            var startupModel = GetStartupDefaultModelId();
            if (!string.IsNullOrWhiteSpace(startupModel))
            {
                Console.WriteLine($"[opencode] startup default model configured (runtime-overridable): {startupModel}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_handlerFullName))
        {
            Console.WriteLine($"[bot] handler restriction enabled: {_handlerFullName}");
        }
    }

    private void InitializeDialogBridgeTrustFromOptions()
    {
        _lslDialogBridgeRequireTrustedSender = _options.LslDialogBridgeRequireTrustedSender;

        if (!string.IsNullOrWhiteSpace(_options.LslDialogBridgeTrustedObjectId)
            && UUID.TryParse(_options.LslDialogBridgeTrustedObjectId.Trim(), out var objectId)
            && objectId != UUID.Zero)
        {
            _trustedDialogBridgeObjectId = objectId;
        }

        if (!string.IsNullOrWhiteSpace(_options.LslDialogBridgeTrustedOwnerId)
            && UUID.TryParse(_options.LslDialogBridgeTrustedOwnerId.Trim(), out var ownerId)
            && ownerId != UUID.Zero)
        {
            _trustedDialogBridgeOwnerId = ownerId;
        }

        if (_trustedDialogBridgeObjectId != UUID.Zero || _trustedDialogBridgeOwnerId != UUID.Zero)
        {
            Console.WriteLine($"[dialog-bridge] trust config loaded: object={_trustedDialogBridgeObjectId} owner={_trustedDialogBridgeOwnerId} requireTrustedSender={_lslDialogBridgeRequireTrustedSender}");
        }
        else
        {
            Console.WriteLine($"[dialog-bridge] trust config loaded: no pinned bridge object/owner; requireTrustedSender={_lslDialogBridgeRequireTrustedSender} (first valid bridge sender will be pinned at runtime).");
        }
    }

    public string LastLoginMessage => _lastLoginMessage;

        public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected && _client != null)
            {
                return true;
            }

            // If a stale client exists (e.g. after simulator restart), fully recycle it before reconnect.
            var staleClient = _client;
            if (staleClient != null)
            {
                CleanupClient(staleClient, logout: true);
                _client = null;
                _connected = false;
            }

            var client = new GridClient();
            // Must be set before login/simulator creation; enabling later does not backfill Terrain arrays.
            client.Settings.World.StoreLandPatches = true;
            client.Network.LoginProgress += OnLoginProgress;
            client.Network.Disconnected += OnDisconnected;
            client.Network.SimChanged += OnNetworkSimChanged;
            client.Self.IM += OnInstantMessage;
            client.Self.ChatFromSimulator += OnChatFromSimulator;
            client.Self.ScriptDialog += OnScriptDialog;
            client.Inventory.InventoryObjectOffered += OnInventoryObjectOffered;

            // Assign the field-backed client early so event handlers that run during
            // the login process (for example SimChanged) can reference a non-null
            // _client. If login ultimately fails we'll clear this field during
            // cleanup below.
            _client = client;

            var login = client.Network.DefaultLoginParams(
                _options.BotFirstName!,
                _options.BotLastName!,
                _options.BotPassword!,
                "opensim-metaverse2mcp",
                "0.1.0");

            login.URI = _options.BotLoginUri;
            login.Start = _options.BotStartLocation;

            Console.WriteLine($"[bot] logging in as {_options.BotFirstName} {_options.BotLastName} ...");

            var success = await client.Network.LoginAsync(login, cancellationToken).ConfigureAwait(false);
            _lastLoginMessage = client.Network.LoginMessage ?? string.Empty;

            if (!success)
            {
                CleanupClient(client, logout: true);
                // Clear the shared client field since login failed.
                _client = null;
                return false;
            }

            // client already assigned to _client above; mark connected.
            _connected = true;

            // Load persisted trust pins after login so {bot_uuid} path templates resolve per avatar.
            TryLoadDialogBridgeTrustStateFromFile();
            TryLoadOpencodeSessionStateFromFile();

            await TryLoadInventoryOfferPoliciesFromConfiguredFileAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public BotStatus GetStatus()
    {
        var client = _client;
        if (!_connected || client == null)
        {
            EnsureReconnectLoop("status-check");
            return new BotStatus(
                false,
                "disconnected",
                0f,
                0f,
                0f,
                client?.Self.AgentID.ToString() ?? string.Empty,
                _lastLoginMessage);
        }

        var sim = client.Network.CurrentSim;
        var pos = client.Self.SimPosition;

        return new BotStatus(
            _connected,
            sim?.Name ?? "unknown",
            pos.X,
            pos.Y,
            pos.Z,
            client.Self.AgentID.ToString(),
            _lastLoginMessage);
    }

    public async Task<BotToolResult> SitAsync(CancellationToken cancellationToken)
    {
        return await RunActionAsync("Sitting down...", c => c.Self.SitOnGround(), cancellationToken);
    }

    public async Task<BotToolResult> StandAsync(CancellationToken cancellationToken)
    {
        return await RunActionAsync("Standing up.", c => c.Self.Stand(), cancellationToken);
    }

    public async Task<BotToolResult> FlyAsync(bool enabled, CancellationToken cancellationToken)
    {
        return await RunActionAsync(enabled ? "Taking off." : "Walking now.", c => c.Self.Fly(enabled), cancellationToken);
    }

    public async Task<BotToolResult> JumpAsync(CancellationToken cancellationToken)
    {
        var result = await RunActionAsync("Jumping.", c => c.Self.Jump(true), cancellationToken);
        if (!result.Ok)
        {
            return result;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                var cl = _client;
                if (cl != null)
                {
                    cl.Self.Jump(false);
                }
            }
            catch
            {
                // Ignored: this is a best-effort reset.
            }
        });

        return result;
    }

    public async Task<BotToolResult> AnimationStartAsync(string animation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return BotToolResult.Fail("animation is required.");
        }

        if (!TryResolveAnimation(animation, out var animationId, out var resolvedName, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await RunActionAsync(
            $"Started animation {resolvedName}.",
            c => c.Self.AnimationStart(animationId, true),
            cancellationToken);
    }

    public async Task<BotToolResult> AnimationStopAsync(string animation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return BotToolResult.Fail("animation is required.");
        }

        if (!TryResolveAnimation(animation, out var animationId, out var resolvedName, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await RunActionAsync(
            $"Stopped animation {resolvedName}.",
            c => c.Self.AnimationStop(animationId, true),
            cancellationToken);
    }

    public Task<AnimationListResult> AnimationsListAsync(CancellationToken cancellationToken)
    {
        return ExecuteLockedAsync((client, _) =>
        {
            var entries = Animations.ToDictionary()
                .Select(kvp => new AnimationInfo(kvp.Value, kvp.Key.ToString()))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(AnimationListResult.OkResult(entries, $"Listed {entries.Count} built-in animations."));
        }, cancellationToken);
    }

    public Task<AnimationListResult> ActiveAnimationsAsync(CancellationToken cancellationToken)
    {
        return ExecuteLockedAsync((client, _) =>
        {
            var dict = Animations.ToDictionary();
            var entries = client.Self.SignaledAnimations
                .Select(kvp =>
                {
                    var name = dict.TryGetValue(kvp.Key, out var n) ? n : null;
                    return new AnimationInfo(name ?? kvp.Key.ToString(), kvp.Key.ToString(), kvp.Value);
                })
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(AnimationListResult.OkResult(entries, $"Found {entries.Count} active animations."));
        }, cancellationToken);
    }

    private static bool TryResolveAnimation(string input, out UUID animationId, out string resolvedName, out string error)
    {
        animationId = UUID.Zero;
        resolvedName = input;
        error = string.Empty;

        var trimmed = input.Trim();

        if (UUID.TryParse(trimmed, out animationId))
        {
            resolvedName = trimmed;
            return true;
        }

        var dict = Animations.ToDictionary();
        var match = dict.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, trimmed, StringComparison.OrdinalIgnoreCase));

        if (match.Key != UUID.Zero)
        {
            animationId = match.Key;
            resolvedName = match.Value;
            return true;
        }

        error = $"Animation '{trimmed}' is not a valid UUID or built-in animation name.";
        return false;
    }

    public async Task<BotToolResult> SayChatAsync(string message, int channel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        return await RunActionAsync(
            $"Sent chat message on channel {channel}.",
            c => c.Self.Chat(message, channel, ChatType.Normal),
            cancellationToken);
    }

    public async Task<BotToolResult> SendImAsync(string agentId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return BotToolResult.Fail("agentId is required.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        if (!UUID.TryParse(agentId, out var recipient))
        {
            return BotToolResult.Fail("agentId is not a valid UUID.");
        }

        return await RunActionAsync(
            $"Sent IM to {agentId}.",
            c => c.Self.InstantMessage(recipient, message),
            cancellationToken);
    }

    public async Task<EnvironmentToolResult> GetRegionEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetRegionEnvironmentAsync(token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult("Unable to fetch region environment (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult("Fetched region environment.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentToolResult> GetParcelEnvironmentAsync(int parcelId, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return EnvironmentToolResult.FailResult("parcelId must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetParcelEnvironmentAsync(parcelId, token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult($"Unable to fetch parcel environment for parcelId={parcelId} (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult($"Fetched parcel environment for parcelId={parcelId}.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetRegionEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.ResetRegionEnvironmentAsync(token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Region environment reset failed or was rejected.");
            }

            return BotToolResult.OkResult("Region environment reset requested successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetParcelEnvironmentAsync(int parcelId, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return BotToolResult.Fail("parcelId must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.ResetParcelEnvironmentAsync(parcelId, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail($"Parcel environment reset failed or was rejected for parcelId={parcelId}.");
            }

            return BotToolResult.OkResult($"Parcel environment reset requested for parcelId={parcelId}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentToolResult> GetLegacyEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetLegacyEnvironmentAsync(token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult("Unable to fetch legacy environment (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult("Fetched legacy environment.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetLegacyEnvironmentRawAsync(string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.SetLegacyEnvironmentAsync(map, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Legacy environment set failed or was rejected.");
            }

            return BotToolResult.OkResult("Legacy environment update posted successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetRegionEnvironmentRawAsync(string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        if (!TryBuildEnvironmentDataFromPayloadMap(map, out var environment, out var environmentError))
        {
            return BotToolResult.Fail(environmentError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var response = await client.Environment.SetRegionEnvironmentAsync(environment, token).ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail("Region environment update failed (capability unavailable or request failed).");
            }

            if (!response.Success)
            {
                var detail = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : $" Detail: {response.Message}";
                return BotToolResult.Fail($"Region environment update was rejected.{detail}");
            }

            return BotToolResult.OkResult($"Region environment updated successfully (version={response.Version}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetParcelEnvironmentRawAsync(int parcelId, string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return BotToolResult.Fail("parcelId must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        if (!TryBuildEnvironmentDataFromPayloadMap(map, out var environment, out var environmentError))
        {
            return BotToolResult.Fail(environmentError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var response = await client.Environment.SetParcelEnvironmentAsync(parcelId, environment, token).ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail($"Parcel environment update failed for parcelId={parcelId} (capability unavailable or request failed).");
            }

            if (!response.Success)
            {
                var detail = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : $" Detail: {response.Message}";
                return BotToolResult.Fail($"Parcel environment update was rejected for parcelId={parcelId}.{detail}");
            }

            return BotToolResult.OkResult($"Parcel environment updated for parcelId={parcelId} (version={response.Version}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetLegacyEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.SetLegacyEnvironmentAsync(new OSDMap(), token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Legacy environment reset failed or was rejected.");
            }

            return BotToolResult.OkResult("Legacy environment reset posted using an empty LLSD map.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimCreateResult> CreatePrimAsync(
        string shape,
        float x,
        float y,
        float z,
        float scaleX,
        float scaleY,
        float scaleZ,
        float rollDegrees,
        float pitchDegrees,
        float yawDegrees,
        string material,
        string? name,
        string? description,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return PrimCreateResult.FailResult("No current simulator available.");
            }

            if (!TryBuildConstructionData(shape, material, out var primData, out var shapeError))
            {
                return PrimCreateResult.FailResult(shapeError);
            }

            var position = ClampLocalPosition(new Vector3(x, y, z));
            var scale = ClampScale(new Vector3(scaleX, scaleY, scaleZ));
            var rotation = Quaternion.CreateFromEulers(
                rollDegrees * Utils.DEG_TO_RAD,
                pitchDegrees * Utils.DEG_TO_RAD,
                yawDegrees * Utils.DEG_TO_RAD);

            var createdPrimTask = WaitForCreatedPrimAsync(client, sim, position, token);
            client.Objects.AddPrim(sim, primData, client.Self.ActiveGroup, position, scale, rotation);

            var created = await createdPrimTask.ConfigureAwait(false);
            if (created == null)
            {
                return PrimCreateResult.FailResult("Timed out waiting for created prim confirmation.");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                client.Objects.SetName(sim, created.LocalID, name);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                client.Objects.SetDescription(sim, created.LocalID, description);
            }

            return PrimCreateResult.OkResult(
                created.LocalID,
                $"Created {shape} prim localId={created.LocalID} at {FormatVector(created.Position)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimPositionAsync(uint localId, float x, float y, float z, bool childOnly, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var position = ClampLocalPosition(new Vector3(x, y, z));
            client.Objects.SetPosition(sim, localId, position, childOnly);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} position to {FormatVector(position)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimScaleAsync(uint localId, float x, float y, float z, bool childOnly, bool uniform, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var scale = ClampScale(new Vector3(x, y, z));
            client.Objects.SetScale(sim, localId, scale, childOnly, uniform);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} scale to {FormatVector(scale)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimRotationEulerAsync(uint localId, float rollDegrees, float pitchDegrees, float yawDegrees, bool childOnly, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var quat = Quaternion.CreateFromEulers(
                rollDegrees * Utils.DEG_TO_RAD,
                pitchDegrees * Utils.DEG_TO_RAD,
                yawDegrees * Utils.DEG_TO_RAD);
            client.Objects.SetRotation(sim, localId, quat, childOnly);
            return Task.FromResult(BotToolResult.OkResult(
                $"Set prim {localId} rotation to roll={rollDegrees:F2}, pitch={pitchDegrees:F2}, yaw={yawDegrees:F2} degrees."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimTextureAsync(uint localId, string textureId, int faceIndex, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(textureId, out var textureUuid))
        {
            return BotToolResult.Fail("textureId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            if (faceIndex < 0)
            {
                te.DefaultTexture ??= new Primitive.TextureEntryFace(null);
                te.DefaultTexture.TextureID = textureUuid;
                client.Objects.SetTextures(sim, localId, te);
                return Task.FromResult(BotToolResult.OkResult($"Set default texture on prim {localId} to {textureUuid}."));
            }

            if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
            {
                return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
            }

            var face = te.CreateFace((uint)faceIndex);
            face.TextureID = textureUuid;
            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Set texture on prim {localId} face {faceIndex} to {textureUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimFaceParamsAsync(
        uint localId,
        int faceIndex,
        float? red,
        float? green,
        float? blue,
        float? alpha,
        float? repeatU,
        float? repeatV,
        float? offsetU,
        float? offsetV,
        float? rotationRadians,
        float? glow,
        bool? fullbright,
        string? shiny,
        string? bump,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            if (red.HasValue || green.HasValue || blue.HasValue || alpha.HasValue)
            {
                var rgba = face.RGBA;
                var r = Math.Clamp(red ?? rgba.R, 0f, 1f);
                var g = Math.Clamp(green ?? rgba.G, 0f, 1f);
                var b = Math.Clamp(blue ?? rgba.B, 0f, 1f);
                var a = Math.Clamp(alpha ?? rgba.A, 0f, 1f);
                face.RGBA = new Color4(r, g, b, a);
            }

            if (repeatU.HasValue)
            {
                face.RepeatU = repeatU.Value;
            }

            if (repeatV.HasValue)
            {
                face.RepeatV = repeatV.Value;
            }

            if (offsetU.HasValue)
            {
                face.OffsetU = Math.Clamp(offsetU.Value, -1f, 1f);
            }

            if (offsetV.HasValue)
            {
                face.OffsetV = Math.Clamp(offsetV.Value, -1f, 1f);
            }

            if (rotationRadians.HasValue)
            {
                face.Rotation = rotationRadians.Value;
            }

            if (glow.HasValue)
            {
                face.Glow = Math.Clamp(glow.Value, 0f, 1f);
            }

            if (fullbright.HasValue)
            {
                face.Fullbright = fullbright.Value;
            }

            if (!string.IsNullOrWhiteSpace(shiny))
            {
                if (!Enum.TryParse<Shininess>(shiny, true, out var shinyValue))
                {
                    return Task.FromResult(BotToolResult.Fail("Invalid shiny value. Use: None, Low, Medium, High."));
                }

                face.Shiny = shinyValue;
            }

            if (!string.IsNullOrWhiteSpace(bump))
            {
                if (!Enum.TryParse<Bumpiness>(bump, true, out var bumpValue))
                {
                    return Task.FromResult(BotToolResult.Fail("Invalid bump value. Use values from Bumpiness enum (e.g. None, Brightness, Darkness, Woodgrain)."));
                }

                face.Bump = bumpValue;
            }

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Updated {faceLabel} parameters on prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> NudgePrimFaceUvAsync(
        uint localId,
        int faceIndex,
        float? deltaRepeatU,
        float? deltaRepeatV,
        float? deltaOffsetU,
        float? deltaOffsetV,
        float? deltaRotationRadians,
        CancellationToken cancellationToken)
    {
        if (!deltaRepeatU.HasValue
            && !deltaRepeatV.HasValue
            && !deltaOffsetU.HasValue
            && !deltaOffsetV.HasValue
            && !deltaRotationRadians.HasValue)
        {
            return BotToolResult.Fail("At least one delta value is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            if (deltaRepeatU.HasValue)
            {
                face.RepeatU += deltaRepeatU.Value;
            }

            if (deltaRepeatV.HasValue)
            {
                face.RepeatV += deltaRepeatV.Value;
            }

            if (deltaOffsetU.HasValue)
            {
                face.OffsetU = Math.Clamp(face.OffsetU + deltaOffsetU.Value, -1f, 1f);
            }

            if (deltaOffsetV.HasValue)
            {
                face.OffsetV = Math.Clamp(face.OffsetV + deltaOffsetV.Value, -1f, 1f);
            }

            if (deltaRotationRadians.HasValue)
            {
                face.Rotation += deltaRotationRadians.Value;
            }

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Nudged UV parameters on {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ApplyPrimFaceUvPresetAsync(
        uint localId,
        int faceIndex,
        string preset,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return BotToolResult.Fail("preset is required. Use: fit, reset, tile2x2, tile4x4, flipU, flipV, rotate90, rotate180, rotate270, center.");
        }

        var normalized = preset.Trim().ToLowerInvariant();

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            switch (normalized)
            {
                case "fit":
                case "reset":
                    face.RepeatU = 1f;
                    face.RepeatV = 1f;
                    face.OffsetU = 0f;
                    face.OffsetV = 0f;
                    face.Rotation = 0f;
                    break;
                case "tile2x2":
                    face.RepeatU = 2f;
                    face.RepeatV = 2f;
                    break;
                case "tile4x4":
                    face.RepeatU = 4f;
                    face.RepeatV = 4f;
                    break;
                case "flipu":
                    face.RepeatU = -face.RepeatU;
                    break;
                case "flipv":
                    face.RepeatV = -face.RepeatV;
                    break;
                case "rotate90":
                    face.Rotation += MathF.PI / 2f;
                    break;
                case "rotate180":
                    face.Rotation += MathF.PI;
                    break;
                case "rotate270":
                    face.Rotation += (MathF.PI * 3f) / 2f;
                    break;
                case "center":
                    face.OffsetU = 0f;
                    face.OffsetV = 0f;
                    break;
                default:
                    return Task.FromResult(BotToolResult.Fail("Unknown preset. Use: fit, reset, tile2x2, tile4x4, flipU, flipV, rotate90, rotate180, rotate270, center."));
            }

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Applied UV preset '{preset}' to {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TilePrimFaceUvAsync(
        uint localId,
        int faceIndex,
        float repeat,
        CancellationToken cancellationToken)
    {
        if (repeat <= 0f)
        {
            return BotToolResult.Fail("repeat must be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            face.RepeatU = repeat;
            face.RepeatV = repeat;

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Set tiling to {repeat:F2}x{repeat:F2} on {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TilePrimFaceUvNonUniformAsync(
        uint localId,
        int faceIndex,
        float repeatU,
        float repeatV,
        CancellationToken cancellationToken)
    {
        if (repeatU <= 0f || repeatV <= 0f)
        {
            return BotToolResult.Fail("repeatU and repeatV must both be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            face.RepeatU = repeatU;
            face.RepeatV = repeatV;

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Set tiling to U={repeatU:F2}, V={repeatV:F2} on {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimInspectResult> InspectPrimAsync(uint localId, bool includeFaceTextures, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimInspectResult.FailResult("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return Task.FromResult(PrimInspectResult.FailResult($"Prim {localId} not found in current simulator cache."));
            }

            var info = BuildPrimInfo(
                prim,
                includeFaceTextures,
                refreshRequested: false,
                refreshReceived: false,
                refreshDetail: "Using simulator cache only (no explicit property refresh requested).",
                refreshedAtUtc: null);

            return Task.FromResult(PrimInspectResult.OkResult(info));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimInspectResult> FetchPrimPropertiesAsync(
        uint localId,
        bool includeFaceTextures,
        float waitTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (waitTimeoutSeconds <= 0f || waitTimeoutSeconds > 30f)
        {
            return PrimInspectResult.FailResult("waitTimeoutSeconds must be > 0 and <= 30.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return PrimInspectResult.FailResult("No current simulator available.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return PrimInspectResult.FailResult($"Prim {localId} not found in current simulator cache.");
            }

            var refresh = await RefreshPrimPropertiesAsync(
                client,
                sim,
                prim,
                TimeSpan.FromSeconds(waitTimeoutSeconds),
                token).ConfigureAwait(false);

            var info = BuildPrimInfo(
                prim,
                includeFaceTextures,
                refreshRequested: true,
                refreshReceived: refresh.Received,
                refreshDetail: refresh.Detail,
                refreshedAtUtc: refresh.RefreshedAtUtc);

            var message = refresh.Received
                ? $"Fetched refreshed prim properties for localId={localId}."
                : $"Property refresh timed out for localId={localId}; returned best-effort cached data.";

            return PrimInspectResult.OkResult(info, message);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Received, string Detail, DateTimeOffset? RefreshedAtUtc)> RefreshPrimPropertiesAsync(
        GridClient client,
        Simulator simulator,
        Primitive prim,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var familyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset? refreshedAtUtc = null;

        void MarkRefreshed()
        {
            refreshedAtUtc = DateTimeOffset.UtcNow;
            _primPropertiesRefreshedAtByObjectId[prim.ID] = refreshedAtUtc.Value;
        }

        void OnObjectPropertiesFamily(object? sender, ObjectPropertiesFamilyEventArgs e)
        {
            if (!ReferenceEquals(e.Simulator, simulator) || e.Properties.ObjectID != prim.ID)
            {
                return;
            }

            prim.Properties ??= new Primitive.ObjectProperties();
            prim.Properties.SetFamilyProperties(e.Properties);
            MarkRefreshed();
            familyTcs.TrySetResult(true);
        }

        void OnObjectPropertiesUpdated(object? sender, ObjectPropertiesUpdatedEventArgs e)
        {
            if (!ReferenceEquals(e.Simulator, simulator) || e.Prim.LocalID != prim.LocalID)
            {
                return;
            }

            prim.Properties = e.Properties;
            MarkRefreshed();
            fullTcs.TrySetResult(true);
        }

        client.Objects.ObjectPropertiesFamily += OnObjectPropertiesFamily;
        client.Objects.ObjectPropertiesUpdated += OnObjectPropertiesUpdated;

        try
        {
            client.Objects.RequestObjectPropertiesFamily(simulator, prim.ID);
            client.Objects.SelectObject(simulator, prim.LocalID, automaticDeselect: true);

            var waitTask = Task.Delay(timeout, cancellationToken);
            var bothTask = Task.WhenAll(familyTcs.Task, fullTcs.Task);
            var completed = await Task.WhenAny(bothTask, waitTask).ConfigureAwait(false);

            if (completed == bothTask)
            {
                return (true, "Received both family and full object property updates.", refreshedAtUtc);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var gotFamily = familyTcs.Task.IsCompletedSuccessfully;
            var gotFull = fullTcs.Task.IsCompletedSuccessfully;
            var gotAny = gotFamily || gotFull;
            var detail = gotAny
                ? $"Timed out waiting for full property refresh ({(gotFamily ? "family " : string.Empty)}{(gotFull ? "full" : string.Empty)} update received)."
                : "Timed out waiting for object property refresh updates.";
            return (gotAny, detail.Trim(), refreshedAtUtc);
        }
        finally
        {
            client.Objects.ObjectPropertiesFamily -= OnObjectPropertiesFamily;
            client.Objects.ObjectPropertiesUpdated -= OnObjectPropertiesUpdated;
        }
    }

    private PrimInfo BuildPrimInfo(
        Primitive prim,
        bool includeFaceTextures,
        bool refreshRequested,
        bool refreshReceived,
        string refreshDetail,
        DateTimeOffset? refreshedAtUtc)
    {
        var faceTextures = new List<PrimFaceTextureInfo>();
        string? defaultTextureId = null;
        if (prim.Textures?.DefaultTexture != null)
        {
            defaultTextureId = prim.Textures.DefaultTexture.TextureID.ToString();
        }

        if (includeFaceTextures && prim.Textures != null)
        {
            for (var i = 0; i < Primitive.TextureEntry.MAX_FACES; i++)
            {
                var face = prim.Textures.FaceTextures[i];
                if (face == null)
                {
                    continue;
                }

                faceTextures.Add(new PrimFaceTextureInfo(i, face.TextureID.ToString()));
            }
        }

        var properties = prim.Properties;
        var permissions = properties == null
            ? null
            : new PrimPermissionsInfo(
                (uint)properties.Permissions.BaseMask,
                (uint)properties.Permissions.OwnerMask,
                (uint)properties.Permissions.GroupMask,
                (uint)properties.Permissions.EveryoneMask,
                (uint)properties.Permissions.NextOwnerMask);

        var sale = properties == null
            ? null
            : new PrimSaleInfo(properties.SaleType.ToString(), properties.SalePrice);

        var sitNamePresent = !string.IsNullOrWhiteSpace(properties?.SitName);
        var clickActionSit = prim.ClickAction == ClickAction.Sit;
        var likelySittablePrim = !prim.IsAttachment;
        var isSittable = sitNamePresent || clickActionSit || likelySittablePrim;
        var sitDetection = sitNamePresent
            ? "SitName is populated on object properties."
            : clickActionSit
                ? "ClickAction is Sit."
                : likelySittablePrim
                    ? "Prim is non-attachment; most in-world prims can be sat even when SitName is empty."
                    : "No sit indicators found from cached properties/click action.";

        var sit = new PrimSitInfo(
            properties?.SitName,
            properties?.TouchName,
            isSittable,
            prim.ClickAction.ToString(),
            sitDetection);

        var flexible = prim.Flexible == null
            ? null
            : new PrimFlexibleInfo(
                prim.Flexible.Softness,
                prim.Flexible.Tension,
                prim.Flexible.Drag,
                prim.Flexible.Gravity,
                prim.Flexible.Wind,
                prim.Flexible.Force.X,
                prim.Flexible.Force.Y,
                prim.Flexible.Force.Z);

        var light = prim.Light == null
            ? null
            : new PrimLightInfo(
                prim.Light.Color.R,
                prim.Light.Color.G,
                prim.Light.Color.B,
                prim.Light.Intensity,
                prim.Light.Radius,
                prim.Light.Cutoff,
                prim.Light.Falloff);

        var sculpt = prim.Sculpt == null
            ? null
            : new PrimSculptInfo(
                prim.Sculpt.SculptTexture.ToString(),
                prim.Sculpt.Type.ToString(),
                prim.Sculpt.Type == SculptType.Mesh,
                prim.Sculpt.Invert,
                prim.Sculpt.Mirror,
                prim.ExtendedMeshFlags);

        var shape = new PrimShapeDetail(
            prim.PrimData.PathCurve.ToString(),
            prim.PrimData.ProfileCurve.ToString(),
            prim.PrimData.ProfileHole.ToString(),
            prim.PrimData.Material.ToString(),
            prim.PrimData.PathBegin,
            prim.PrimData.PathEnd,
            prim.PrimData.PathScaleX,
            prim.PrimData.PathScaleY,
            prim.PrimData.PathShearX,
            prim.PrimData.PathShearY,
            prim.PrimData.PathTwist,
            prim.PrimData.PathTwistBegin,
            prim.PrimData.PathTaperX,
            prim.PrimData.PathTaperY,
            prim.PrimData.PathRadiusOffset,
            prim.PrimData.PathSkew,
            prim.PrimData.PathRevolutions,
            prim.PrimData.ProfileBegin,
            prim.PrimData.ProfileEnd,
            prim.PrimData.ProfileHollow);

        var freshestAt = refreshedAtUtc;
        if (!freshestAt.HasValue && _primPropertiesRefreshedAtByObjectId.TryGetValue(prim.ID, out var cachedRefresh))
        {
            freshestAt = cachedRefresh;
        }

        var freshness = new PrimPropertyFreshnessInfo(
            refreshRequested,
            refreshReceived,
            freshestAt?.ToString("O"),
            refreshDetail);

        return new PrimInfo(
            prim.LocalID,
            prim.ID.ToString(),
            prim.ParentID,
            prim.Type.ToString(),
            prim.PrimData.PathCurve.ToString(),
            prim.PrimData.ProfileCurve.ToString(),
            prim.PrimData.Material.ToString(),
            prim.Position.X,
            prim.Position.Y,
            prim.Position.Z,
            prim.Scale.X,
            prim.Scale.Y,
            prim.Scale.Z,
            prim.Rotation.X,
            prim.Rotation.Y,
            prim.Rotation.Z,
            prim.Rotation.W,
            properties?.Name,
            properties?.Description,
            properties?.OwnerID.ToString(),
            properties?.CreatorID.ToString(),
            defaultTextureId,
            faceTextures,
            shape,
            permissions,
            sale,
            sit,
            flexible,
            light,
            sculpt,
            freshness);
    }

    public async Task<BotToolResult> SelectPrimAsync(uint localId, bool automaticDeselect, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SelectObject(sim, localId, automaticDeselect);
            return Task.FromResult(BotToolResult.OkResult(
                automaticDeselect
                    ? $"Selected prim {localId} (auto-deselect enabled)."
                    : $"Selected prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DeselectPrimAsync(uint localId, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.DeselectObject(sim, localId);
            return Task.FromResult(BotToolResult.OkResult($"Deselected prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DeletePrimAsync(uint localId, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            // DeRez to inventory; if localId is a child prim, simulator deletes the whole linkset.
            client.Inventory.RequestDeRezToInventory(localId);
            return Task.FromResult(BotToolResult.OkResult($"Delete request sent for prim {localId} (de-rez to inventory)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DeleteManyPrimsAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count == 0)
        {
            return BotToolResult.Fail("At least one local ID is required to delete prims.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            foreach (var localId in localIds)
            {
                // DeRez to inventory; if localId is a child prim, simulator deletes the whole linkset.
                client.Inventory.RequestDeRezToInventory(localId);
            }

            return Task.FromResult(BotToolResult.OkResult($"Delete request sent for {localIds.Count} prim(s): {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimQueryResult> FindPrimsByNameAsync(string name, int maxResults, bool caseSensitive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return PrimQueryResult.FailResult("name is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimQueryResult.FailResult("No current simulator available."));
            }

            var limit = Math.Clamp(maxResults, 1, 500);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var at = client.Self.SimPosition;

            var prims = sim.ObjectsPrimitives.Values
                .Where(p => !string.IsNullOrWhiteSpace(p.Properties?.Name)
                    && p.Properties!.Name.Contains(name, comparison))
                .Select(p => ToPrimSummary(p, at))
                .OrderBy(p => p.DistanceMeters)
                .ThenBy(p => p.LocalId)
                .Take(limit)
                .ToList();

            return Task.FromResult(PrimQueryResult.OkResult(prims, $"Matched {prims.Count} prim(s)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimQueryResult> ListNearbyPrimsAsync(float radiusMeters, int maxResults, CancellationToken cancellationToken)
    {
        if (radiusMeters <= 0f)
        {
            return PrimQueryResult.FailResult("radiusMeters must be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimQueryResult.FailResult("No current simulator available."));
            }

            var limit = Math.Clamp(maxResults, 1, 500);
            var radius = Math.Clamp(radiusMeters, 0.1f, 4096f);
            var at = client.Self.SimPosition;

            var prims = sim.ObjectsPrimitives.Values
                .Select(p => ToPrimSummary(p, at))
                .Where(p => p.DistanceMeters <= radius)
                .OrderBy(p => p.DistanceMeters)
                .ThenBy(p => p.LocalId)
                .Take(limit)
                .ToList();

            return Task.FromResult(PrimQueryResult.OkResult(prims, $"Found {prims.Count} nearby prim(s) within {radius:F2}m."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimNameAsync(uint localId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BotToolResult.Fail("name is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SetName(sim, localId, name);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} name to '{name}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimDescriptionAsync(uint localId, string description, CancellationToken cancellationToken)
    {
        if (description == null)
        {
            return BotToolResult.Fail("description is required (empty string is allowed).");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SetDescription(sim, localId, description);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} description."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> LinkPrimsAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count < 2)
        {
            return BotToolResult.Fail("At least two local IDs are required to link prims.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.LinkPrims(sim, localIds);
            return Task.FromResult(BotToolResult.OkResult($"Link request sent for prims: {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> UnlinkPrimsAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count == 0)
        {
            return BotToolResult.Fail("At least one local ID is required to unlink prims.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.DelinkPrims(sim, localIds);
            return Task.FromResult(BotToolResult.OkResult($"Unlink request sent for prims: {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimCreateResult> ClonePrimAsync(
        uint sourceLocalId,
        float offsetX,
        float offsetY,
        float offsetZ,
        bool copyTextures,
        bool copyName,
        bool copyDescription,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return PrimCreateResult.FailResult("No current simulator available.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(sourceLocalId, out var sourcePrim))
            {
                return PrimCreateResult.FailResult($"Source prim {sourceLocalId} not found in current simulator cache.");
            }

            var newPosition = ClampLocalPosition(new Vector3(
                sourcePrim.Position.X + offsetX,
                sourcePrim.Position.Y + offsetY,
                sourcePrim.Position.Z + offsetZ));
            var newScale = ClampScale(sourcePrim.Scale);
            var newRotation = sourcePrim.Rotation;
            var primData = new Primitive.ConstructionData(sourcePrim.PrimData);

            var createdPrimTask = WaitForCreatedPrimAsync(client, sim, newPosition, token);
            client.Objects.AddPrim(sim, primData, client.Self.ActiveGroup, newPosition, newScale, newRotation);

            var created = await createdPrimTask.ConfigureAwait(false);
            if (created == null)
            {
                return PrimCreateResult.FailResult("Timed out waiting for cloned prim confirmation.");
            }

            if (copyTextures && sourcePrim.Textures != null)
            {
                client.Objects.SetTextures(sim, created.LocalID, new Primitive.TextureEntry(sourcePrim.Textures));
            }

            if (copyName && sourcePrim.Properties != null && !string.IsNullOrWhiteSpace(sourcePrim.Properties.Name))
            {
                client.Objects.SetName(sim, created.LocalID, sourcePrim.Properties.Name);
            }

            if (copyDescription && sourcePrim.Properties != null && sourcePrim.Properties.Description != null)
            {
                client.Objects.SetDescription(sim, created.LocalID, sourcePrim.Properties.Description);
            }

            return PrimCreateResult.OkResult(
                created.LocalID,
                $"Cloned prim {sourceLocalId} -> {created.LocalID} at {FormatVector(created.Position)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> MoveByAsync(string direction, float meters, bool fly, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return BotToolResult.Fail("direction is required.");
        }

        if (meters <= 0f)
        {
            return BotToolResult.Fail("meters must be greater than 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var delta = ResolveDelta(direction, meters, client);
            var from = client.Self.SimPosition;
            var target = ClampLocalPosition(new Vector3(from.X + delta.X, from.Y + delta.Y, from.Z + delta.Z));
            return await MoveToLocalPositionCoreAsync(client, target, fly, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> MoveToAsync(float x, float y, float z, bool fly, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync(
            (client, token) => MoveToLocalPositionCoreAsync(client, target, fly, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportToAsync(float x, float y, float z, string? regionName, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var currentSim = client.Network.CurrentSim;
            if (currentSim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            bool ok;
            string destinationLabel;
            if (string.IsNullOrWhiteSpace(regionName) || string.Equals(regionName, currentSim.Name, StringComparison.OrdinalIgnoreCase))
            {
                destinationLabel = currentSim.Name;
                ok = await client.Self.TeleportAsync(currentSim.Name, target, token).ConfigureAwait(false);
            }
            else
            {
                var region = await client.Grid.GetGridRegionAsync(regionName, GridLayerType.Objects, token).ConfigureAwait(false);
                if (!region.HasValue)
                {
                    return BotToolResult.Fail($"Unable to resolve region '{regionName}' to a region handle.");
                }

                destinationLabel = $"{region.Value.Name} ({region.Value.RegionHandle})";
                ok = await client.Self.TeleportAsync(region.Value.RegionHandle, target, token).ConfigureAwait(false);
            }

            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(client.Self.TeleportMessage)
                    ? "Teleport failed."
                    : client.Self.TeleportMessage;
                return BotToolResult.Fail(message);
            }

            var at = client.Self.SimPosition;
            return BotToolResult.OkResult($"Teleported to {destinationLabel} at {FormatVector(at)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportToRegionHandleAsync(string regionHandle, float x, float y, float z, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(regionHandle))
        {
            return BotToolResult.Fail("regionHandle is required.");
        }

        if (!ulong.TryParse(regionHandle, out var handle))
        {
            return BotToolResult.Fail("regionHandle must be an unsigned 64-bit integer.");
        }

        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Self.TeleportAsync(handle, target, token).ConfigureAwait(false);
            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(client.Self.TeleportMessage)
                    ? "Teleport failed."
                    : client.Self.TeleportMessage;
                return BotToolResult.Fail(message);
            }

            return BotToolResult.OkResult($"Teleported to region handle {handle} at {FormatVector(client.Self.SimPosition)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> StopMovementAsync(CancellationToken cancellationToken)
    {
        StopFollowInternal();
        CancelMovementAutoStop();
        return await ExecuteLockedAsync((client, _) =>
        {
            client.Self.AutoPilotCancel();
            client.Self.Movement.ResetControlFlags();
            client.Self.Movement.SendUpdate(true);
            return Task.FromResult(BotToolResult.OkResult("Movement stopped (autopilot canceled, control flags reset, follow stopped)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> StartMovementAsync(string axis, bool fast, float? durationSeconds, CancellationToken cancellationToken)
    {
        if (!TryResolveMovementAxis(axis, fast, out var flags, out var axisError))
        {
            return BotToolResult.Fail(axisError);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var movement = client.Self.Movement;
            movement.AtPos = (flags & AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0;
            movement.AtNeg = (flags & AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0;
            movement.LeftPos = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) != 0;
            movement.LeftNeg = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) != 0;
            movement.UpPos = (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0;
            movement.UpNeg = (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0;
            movement.FastAt = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT) != 0;
            movement.FastLeft = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FAST_LEFT) != 0;
            movement.FastUp = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FAST_UP) != 0;
            movement.SendUpdate(true);

            var durationNote = "until StopMovement";
            if (durationSeconds.HasValue && durationSeconds.Value > 0f)
            {
                var clamped = Math.Clamp(durationSeconds.Value, 0.25f, 300f);
                ScheduleMovementAutoStop(TimeSpan.FromSeconds(clamped));
                durationNote = $"for up to {clamped:F1}s (auto-stop)";
            }

            return Task.FromResult(BotToolResult.OkResult(
                $"Continuous movement started on axis '{axis}'{(fast ? " (fast)" : string.Empty)} {durationNote}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> LookAtAsync(float x, float y, float z, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync((client, _) =>
        {
            var ok = client.Self.Movement.TurnToward(target, true);
            return Task.FromResult(ok
                ? BotToolResult.OkResult($"Turned body and camera toward {FormatVector(target)}.")
                : BotToolResult.Fail("TurnToward failed (agent updates disabled or parent prim missing)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetCameraHeadingAsync(float headingDegrees, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var headingRadians = headingDegrees * Utils.DEG_TO_RAD;
            client.Self.Movement.UpdateFromHeading(headingRadians, true);
            return Task.FromResult(BotToolResult.OkResult($"Camera heading set to {headingDegrees:F1} degrees."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<CameraStateResult> GetCameraStateAsync(CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var cam = client.Self.Movement.Camera;
        var pos = client.Self.SimPosition;
        var state = new CameraState(
            cam.Position.X, cam.Position.Y, cam.Position.Z,
            cam.AtAxis.X, cam.AtAxis.Y, cam.AtAxis.Z,
            cam.LeftAxis.X, cam.LeftAxis.Y, cam.LeftAxis.Z,
            cam.UpAxis.X, cam.UpAxis.Y, cam.UpAxis.Z,
            cam.Far,
            pos.X, pos.Y, pos.Z);
        return Task.FromResult(new CameraStateResult(true, "OK", state));
    }

    public async Task<BotToolResult> FollowAsync(string targetType, string target, float distanceBuffer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return BotToolResult.Fail("target is required.");
        }

        var buffer = distanceBuffer <= 0f ? 3.0f : Math.Clamp(distanceBuffer, 0.5f, 50f);
        var isObject = string.Equals(targetType, "object", StringComparison.OrdinalIgnoreCase);
        var isAvatar = string.Equals(targetType, "avatar", StringComparison.OrdinalIgnoreCase);
        if (!isObject && !isAvatar)
        {
            return BotToolResult.Fail("targetType must be 'avatar' or 'object'.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            uint localId;
            string label;
            if (isAvatar)
            {
                if (!TryResolveAvatar(sim, target, out localId, out label))
                {
                    return Task.FromResult(BotToolResult.Fail(
                        $"Avatar '{target}' not found in current simulator. Use full name or UUID."));
                }
            }
            else
            {
                if (!TryResolveObject(sim, target, out localId, out label))
                {
                    return Task.FromResult(BotToolResult.Fail(
                        $"Object '{target}' not found in current simulator. Use name, local ID, or UUID."));
                }
            }

            StartFollowLoop(client, sim, isObject, localId, label, buffer);
            return Task.FromResult(BotToolResult.OkResult(
                $"Following {targetType} {label} (buffer {buffer:F1}m, same region only). Use StopFollow or StopMovement to end."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<BotToolResult> StopFollowAsync(CancellationToken cancellationToken)
    {
        var hadFollow = StopFollowInternal();
        return Task.FromResult(hadFollow
            ? BotToolResult.OkResult("Follow stopped.")
            : BotToolResult.OkResult("No active follow to stop."));
    }

    private void StartFollowLoop(GridClient client, Simulator sim, bool isObject, uint localId, string label, float buffer)
    {
        StopFollowInternal();

        var cts = new CancellationTokenSource();
        lock (_movementLock)
        {
            _followCts = cts;
            _followTargetDescription = $"{(isObject ? "object" : "avatar")} {label}";
            _followTask = Task.Run(() => FollowLoopAsync(client, sim, isObject, localId, label, buffer, cts.Token));
        }
    }

    private async Task FollowLoopAsync(
        GridClient client,
        Simulator sim,
        bool isObject,
        uint localId,
        string label,
        float buffer,
        CancellationToken cancellationToken)
    {
        var lastPilotAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                if (!ReferenceEquals(client.Network.CurrentSim, sim))
                {
                    Console.WriteLine($"[follow] target region changed; stopping follow of {label}.");
                    break;
                }

                Vector3 targetPos;
                if (isObject)
                {
                    if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
                    {
                        Console.WriteLine($"[follow] object {label} no longer in cache; stopping.");
                        break;
                    }

                    targetPos = prim.Position;
                }
                else
                {
                    if (!sim.ObjectsAvatars.TryGetValue(localId, out var avatar))
                    {
                        Console.WriteLine($"[follow] avatar {label} no longer in cache; stopping.");
                        break;
                    }

                    targetPos = avatar.Position;
                }

                var distance = Vector3.Distance(targetPos, client.Self.SimPosition);
                if (distance > buffer)
                {
                    // Re-issue autopilot at most once per second to avoid packet spam.
                    if ((DateTime.UtcNow - lastPilotAt) >= TimeSpan.FromSeconds(1))
                    {
                        client.Self.AutoPilotLocal(
                            (int)MathF.Round(targetPos.X),
                            (int)MathF.Round(targetPos.Y),
                            targetPos.Z);
                        lastPilotAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    client.Self.AutoPilotCancel();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[follow] error while following {label}: {ex.Message}");
                break;
            }
        }

        try
        {
            client.Self.AutoPilotCancel();
        }
        catch
        {
            // Best-effort cleanup.
        }

        lock (_movementLock)
        {
            _followTargetDescription = null;
        }
    }

    private bool StopFollowInternal()
    {
        CancellationTokenSource? cts;
        lock (_movementLock)
        {
            cts = _followCts;
            _followCts = null;
            _followTask = null;
            _followTargetDescription = null;
        }

        if (cts == null)
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // Ignore cancellation races.
        }

        cts.Dispose();
        return true;
    }

    private void ScheduleMovementAutoStop(TimeSpan delay)
    {
        CancelMovementAutoStop();
        var cts = new CancellationTokenSource();
        lock (_movementLock)
        {
            _movementAutoStopCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                var client = _client;
                if (client != null && _connected)
                {
                    client.Self.Movement.ResetControlFlags();
                    client.Self.Movement.SendUpdate(true);
                    Console.WriteLine("[movement] auto-stop fired after configured duration.");
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when movement is stopped manually.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[movement] auto-stop error: {ex.Message}");
            }
        });
    }

    private void CancelMovementAutoStop()
    {
        CancellationTokenSource? cts;
        lock (_movementLock)
        {
            cts = _movementAutoStopCts;
            _movementAutoStopCts = null;
        }

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancellation races.
            }

            cts.Dispose();
        }
    }

    private static bool TryResolveMovementAxis(string axis, bool fast, out AgentManager.ControlFlags flags, out string error)
    {
        flags = AgentManager.ControlFlags.NONE;
        error = string.Empty;
        var normalized = (axis ?? string.Empty).Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "forward":
            case "forwards":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT;
                return true;
            case "back":
            case "backward":
            case "backwards":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT;
                return true;
            case "left":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_LEFT;
                return true;
            case "right":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_LEFT;
                return true;
            case "up":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_UP;
                return true;
            case "down":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_UP;
                return true;
            default:
                error = "Unsupported axis. Use: forward, back, left, right, up, down.";
                return false;
        }
    }

    private static bool TryResolveAvatar(Simulator sim, string target, out uint localId, out string label)
    {
        localId = 0;
        label = string.Empty;

        if (UUID.TryParse(target, out var uuid))
        {
            var match = sim.ObjectsAvatars.FirstOrDefault(kvp => kvp.Value.ID == uuid);
            if (match.Value != null)
            {
                localId = match.Value.LocalID;
                label = $"{match.Value.Name} ({match.Value.ID})";
                return true;
            }

            return false;
        }

        var byName = sim.ObjectsAvatars.FirstOrDefault(kvp =>
            kvp.Value != null
            && !string.IsNullOrWhiteSpace(kvp.Value.Name)
            && kvp.Value.Name.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName.Value != null)
        {
            localId = byName.Value.LocalID;
            label = $"{byName.Value.Name} ({byName.Value.ID})";
            return true;
        }

        return false;
    }

    private static bool TryResolveObject(Simulator sim, string target, out uint localId, out string label)
    {
        localId = 0;
        label = string.Empty;
        var trimmed = target.Trim();

        if (uint.TryParse(trimmed, out var parsedLocalId)
            && sim.ObjectsPrimitives.TryGetValue(parsedLocalId, out var byLocalId))
        {
            localId = byLocalId.LocalID;
            label = $"{byLocalId.Properties?.Name ?? "(unnamed)"} (localId {byLocalId.LocalID})";
            return true;
        }

        if (UUID.TryParse(trimmed, out var uuid))
        {
            var match = sim.ObjectsPrimitives.FirstOrDefault(kvp => kvp.Value.ID == uuid);
            if (match.Value != null)
            {
                localId = match.Value.LocalID;
                label = $"{match.Value.Properties?.Name ?? "(unnamed)"} ({match.Value.ID})";
                return true;
            }

            return false;
        }

        var byName = sim.ObjectsPrimitives.FirstOrDefault(kvp =>
            kvp.Value?.Properties?.Name != null
            && kvp.Value.Properties.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (byName.Value != null)
        {
            localId = byName.Value.LocalID;
            label = $"{byName.Value.Properties!.Name} (localId {byName.Value.LocalID})";
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        try
        {
            _lifecycleCts.Cancel();
        }
        catch
        {
            // No-op during shutdown.
        }

        var client = _client;
        if (_opencodeChat != null)
        {
            _opencodeChat.SessionStatusChanged -= OnOpencodeSessionStatusChanged;
            _opencodeChat.MessagePartUpdated -= OnOpencodeMessagePartUpdated;
        }
        StopTypingIndicatorIfActive();
        foreach (var cts in _inFlightRequestCtsByConversation.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // No-op during shutdown.
            }
            finally
            {
                cts.Dispose();
            }
        }

        _inFlightRequestCtsByConversation.Clear();
        foreach (var wait in _pendingDialogPromptWaitByConversation.Values)
        {
            try
            {
                wait.TimeoutCts.Cancel();
            }
            catch
            {
                // No-op during shutdown.
            }
            finally
            {
                wait.TimeoutCts.Dispose();
            }
        }

        _pendingDialogPromptWaitByConversation.Clear();
        _pendingTextPromptReplyByConversation.Clear();
        _busyOpencodeSessions.Clear();
        ClearBusyHoverText();
        _client = null;
        _connected = false;
        StopFollowInternal();
        CancelMovementAutoStop();

        if (client == null)
        {
            _connectGate.Dispose();
            _lifecycleCts.Dispose();
            if (_opencodeChat is IDisposable disposableWhenNoClient)
            {
                disposableWhenNoClient.Dispose();
            }
            return;
        }

        CleanupClient(client, logout: true);
        _connectGate.Dispose();
        _lifecycleCts.Dispose();
        if (_opencodeChat is IDisposable disposableOpencodeChat)
        {
            disposableOpencodeChat.Dispose();
        }

        foreach (var gate in _imConversationLocks.Values)
        {
            gate.Dispose();
        }

        _actionGate.Dispose();
    }

    private async Task<BotToolResult> RunActionAsync(string successMessage, Action<GridClient> action, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            action(client);
            return Task.FromResult(BotToolResult.OkResult(successMessage));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PrimCreateResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<PrimCreateResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PrimCreateResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<PrimInspectResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<PrimInspectResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PrimInspectResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<PrimQueryResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<PrimQueryResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PrimQueryResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<LinksetInspectResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<LinksetInspectResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return LinksetInspectResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<BotToolResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<BotToolResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BotToolResult.Fail(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AnimationListResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AnimationListResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AnimationListResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<EnvironmentToolResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<EnvironmentToolResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EnvironmentToolResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<DataToolResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<DataToolResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return DataToolResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<WearableDirectControlResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<WearableDirectControlResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return WearableDirectControlResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AttachmentPointMappingResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AttachmentPointMappingResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AttachmentPointMappingResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceVisualParamsResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceVisualParamsResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AppearanceVisualParamsResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceVisualParamSetResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceVisualParamSetResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AppearanceVisualParamSetResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceBakeDiagnosticsResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceBakeDiagnosticsResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AppearanceBakeDiagnosticsResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private GridClient EnsureClient()
    {
        if (!_connected || _client == null)
        {
            EnsureReconnectLoop("ensure-client");
            throw new InvalidOperationException("Bot is not connected.");
        }

        return _client;
    }

    private static string FormatWhereText(GridClient client)
    {
        var sim = client.Network.CurrentSim?.Name ?? "unknown";
        var pos = client.Self.SimPosition;
        return $"I'm in {sim} at <{pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}>";
    }

    private static bool TryBuildConstructionData(string shape, string material, out Primitive.ConstructionData primData, out string error)
    {
        primData = BuildDefaultConstructionData();
        error = string.Empty;

        var normalizedShape = (shape ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalizedShape)
        {
            case "box":
            case "cube":
                primData.PathCurve = PathCurve.Line;
                primData.ProfileCurve = ProfileCurve.Square;
                break;
            case "cylinder":
                primData.PathCurve = PathCurve.Line;
                primData.ProfileCurve = ProfileCurve.Circle;
                break;
            case "prism":
                primData.PathCurve = PathCurve.Line;
                primData.ProfileCurve = ProfileCurve.EqualTriangle;
                break;
            case "sphere":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.HalfCircle;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 1f;
                break;
            case "torus":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.Circle;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 0.25f;
                break;
            case "tube":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.Square;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 0.25f;
                break;
            case "ring":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.EqualTriangle;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 0.25f;
                break;
            default:
                error = "Unsupported shape. Use: box, cylinder, prism, sphere, torus, tube, ring.";
                return false;
        }

        if (!Enum.TryParse<Material>((material ?? string.Empty).Trim(), true, out var parsedMaterial))
        {
            error = "Unsupported material. Use: Stone, Metal, Glass, Wood, Flesh, Plastic, Rubber, Light.";
            return false;
        }

        primData.Material = parsedMaterial;
        return true;
    }

    private static Primitive.ConstructionData BuildDefaultConstructionData()
    {
        return new Primitive.ConstructionData
        {
            PCode = PCode.Prim,
            Material = Material.Wood,
            PathCurve = PathCurve.Line,
            PathBegin = 0f,
            PathEnd = 1f,
            PathRadiusOffset = 0f,
            PathSkew = 0f,
            PathScaleX = 1f,
            PathScaleY = 1f,
            PathShearX = 0f,
            PathShearY = 0f,
            PathTaperX = 0f,
            PathTaperY = 0f,
            PathTwist = 0f,
            PathTwistBegin = 0f,
            PathRevolutions = 1f,
            ProfileBegin = 0f,
            ProfileEnd = 1f,
            ProfileHollow = 0f,
            ProfileCurve = ProfileCurve.Square,
            ProfileHole = HoleType.Same
        };
    }

    private static Vector3 ResolveDelta(string direction, float meters, GridClient client)
    {
        var normalized = direction.Trim().ToLowerInvariant();
        return normalized switch
        {
            "north" => new Vector3(0f, meters, 0f),
            "south" => new Vector3(0f, -meters, 0f),
            "east" => new Vector3(meters, 0f, 0f),
            "west" => new Vector3(-meters, 0f, 0f),
            "up" => new Vector3(0f, 0f, meters),
            "down" => new Vector3(0f, 0f, -meters),
            "forward" => ScaleToLength(Flatten(client.Self.Movement.Camera.AtAxis), meters),
            "back" or "backward" => ScaleToLength(Flatten(Negate(client.Self.Movement.Camera.AtAxis)), meters),
            "left" => ScaleToLength(Flatten(client.Self.Movement.Camera.LeftAxis), meters),
            "right" => ScaleToLength(Flatten(Negate(client.Self.Movement.Camera.LeftAxis)), meters),
            _ => throw new ArgumentException("Unsupported direction. Use: north, south, east, west, up, down, forward, back, left, right")
        };
    }

    private async Task<BotToolResult> MoveToLocalPositionCoreAsync(
        GridClient client,
        Vector3 target,
        bool fly,
        CancellationToken cancellationToken)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return BotToolResult.Fail("No current simulator available.");
        }

        var from = client.Self.SimPosition;

        var distance = Vector3.Distance(from, target);
        if (distance <= 1.0f)
        {
            return BotToolResult.OkResult($"Already at {FormatVector(from)}.");
        }

        var maxStepMeters = 48f;
        var steps = Math.Max(1, (int)MathF.Ceiling(distance / maxStepMeters));

        try
        {
            client.Self.Fly(fly);

            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ratio = step / (float)steps;
                var waypoint = ClampLocalPosition(Interpolate(from, target, ratio));
                var current = client.Self.SimPosition;
                var legDistance = MathF.Max(1f, Vector3.Distance(current, waypoint));
                var timeoutSeconds = Math.Clamp((int)MathF.Ceiling(legDistance * 0.9f), 10, 40);

                client.Self.AutoPilotLocal(
                    (int)MathF.Round(waypoint.X),
                    (int)MathF.Round(waypoint.Y),
                    waypoint.Z);

                var reached = await WaitForArrivalWithRecoveryAsync(
                        client,
                        sim,
                        waypoint,
                        step == steps ? 1.5f : 2.5f,
                        TimeSpan.FromSeconds(timeoutSeconds),
                        fly,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!reached)
                {
                    if (!fly && EnableWalkTeleportFallback)
                    {
                        var recoveredByTeleport = await TryWalkTeleportFallbackAsync(client, sim, waypoint, cancellationToken).ConfigureAwait(false);
                        if (recoveredByTeleport)
                        {
                            continue;
                        }
                    }

                    var atTimeout = client.Self.SimPosition;
                    return BotToolResult.Fail(
                        $"Movement timed out on step {step}/{steps}. Current {FormatVector(atTimeout)}, waypoint {FormatVector(waypoint)}, final target {FormatVector(target)}.");
                }
            }
        }
        finally
        {
            client.Self.AutoPilotCancel();
        }

        var mode = fly ? "flying" : "walking";
        return BotToolResult.OkResult($"Moved by {mode} from {FormatVector(from)} to {FormatVector(client.Self.SimPosition)}.");
    }

    private async Task<bool> WaitForArrivalWithRecoveryAsync(
        GridClient client,
        Simulator sim,
        Vector3 target,
        float tolerance,
        TimeSpan timeout,
        bool fly,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var bestDistance = Vector3.Distance(client.Self.SimPosition, target);
        var lastProgressAt = startedAt;
        var recoveryAttempts = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var at = client.Self.SimPosition;
            var distance = Vector3.Distance(at, target);
            if (distance <= tolerance)
            {
                return true;
            }

            if ((bestDistance - distance) >= WalkProgressThresholdMeters)
            {
                bestDistance = distance;
                lastProgressAt = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - lastProgressAt) >= TimeSpan.FromSeconds(WalkStuckWindowSeconds))
            {
                recoveryAttempts++;
                if (recoveryAttempts > WalkRecoveryMaxAttempts)
                {
                    return false;
                }

                var recovered = false;
                if (!fly)
                {
                    recovered = await TryDoorInteractionRecoveryAsync(client, sim, at, target, cancellationToken).ConfigureAwait(false);
                }

                if (!recovered)
                {
                    recovered = await TryDetourRecoveryAsync(client, at, target, recoveryAttempts, cancellationToken).ConfigureAwait(false);
                }

                if (!recovered)
                {
                    return false;
                }

                client.Self.AutoPilotLocal(
                    (int)MathF.Round(target.X),
                    (int)MathF.Round(target.Y),
                    target.Z);

                bestDistance = Vector3.Distance(client.Self.SimPosition, target);
                lastProgressAt = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - startedAt) >= timeout)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TryDoorInteractionRecoveryAsync(
        GridClient client,
        Simulator sim,
        Vector3 from,
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var candidates = sim.ObjectsPrimitives.Values
            .Where(p => p != null && !p.IsAttachment)
            .Where(p => Vector3.Distance(from, p.Position) <= 7.5f)
            .Where(p => DistancePointToSegment2D(p.Position, from, target) <= 2.75f)
            .Where(IsDoorLikePrim)
            .OrderBy(p => DistancePointToSegment2D(p.Position, from, target))
            .Take(3)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (var prim in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Self.Touch(prim.LocalID);
            await Task.Delay(900, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> TryDetourRecoveryAsync(
        GridClient client,
        Vector3 from,
        Vector3 target,
        int recoveryAttempt,
        CancellationToken cancellationToken)
    {
        var toTarget = Flatten(new Vector3(target.X - from.X, target.Y - from.Y, 0f));
        var norm = toTarget.Length();
        if (norm <= 0.0001f)
        {
            return false;
        }

        toTarget /= norm;
        var left = new Vector3(-toTarget.Y, toTarget.X, 0f);
        var offset = Math.Clamp(1.5f * recoveryAttempt, 1.5f, 8f);
        var forwardBias = Math.Clamp(1.2f + (0.4f * recoveryAttempt), 1.2f, 3.5f);

        foreach (var side in new[] { 1f, -1f })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ClampLocalPosition(new Vector3(
                from.X + (left.X * offset * side) + (toTarget.X * forwardBias),
                from.Y + (left.Y * offset * side) + (toTarget.Y * forwardBias),
                MathF.Max(from.Z, target.Z - 1f)));

            client.Self.AutoPilotLocal(
                (int)MathF.Round(candidate.X),
                (int)MathF.Round(candidate.Y),
                candidate.Z);

            var reached = await WaitForArrivalAsync(
                    client,
                    candidate,
                    tolerance: 2.5f,
                    timeout: TimeSpan.FromSeconds(Math.Clamp(6 + recoveryAttempt, 6, 12)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (reached)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryWalkTeleportFallbackAsync(
        GridClient client,
        Simulator sim,
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            target,
            ClampLocalPosition(new Vector3(target.X + 4f, target.Y, target.Z)),
            ClampLocalPosition(new Vector3(target.X - 4f, target.Y, target.Z)),
            ClampLocalPosition(new Vector3(target.X, target.Y + 4f, target.Z)),
            ClampLocalPosition(new Vector3(target.X, target.Y - 4f, target.Z))
        };

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var teleported = await client.Self.TeleportAsync(sim.Name, candidate, cancellationToken).ConfigureAwait(false);
            if (!teleported)
            {
                continue;
            }

            client.Self.AutoPilotLocal(
                (int)MathF.Round(target.X),
                (int)MathF.Round(target.Y),
                target.Z);

            var reached = await WaitForArrivalAsync(
                    client,
                    target,
                    tolerance: 2.5f,
                    timeout: TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);

            client.Self.AutoPilotCancel();
            if (reached)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDoorLikePrim(Primitive prim)
    {
        var name = prim.Properties?.Name ?? string.Empty;
        var description = prim.Properties?.Description ?? string.Empty;
        var touchName = prim.Properties?.TouchName ?? string.Empty;
        var searchable = $"{name} {description} {touchName}";

        var hasDoorHint = DoorHintKeywords.Any(keyword => searchable.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var scripted = (prim.Flags & PrimFlags.Scripted) != 0;

        return hasDoorHint || scripted;
    }

    private static float DistancePointToSegment2D(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
    {
        var ax = segmentStart.X;
        var ay = segmentStart.Y;
        var bx = segmentEnd.X;
        var by = segmentEnd.Y;
        var px = point.X;
        var py = point.Y;

        var abx = bx - ax;
        var aby = by - ay;
        var abLenSq = (abx * abx) + (aby * aby);
        if (abLenSq <= 0.0001f)
        {
            return MathF.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));
        }

        var apx = px - ax;
        var apy = py - ay;
        var t = Math.Clamp(((apx * abx) + (apy * aby)) / abLenSq, 0f, 1f);
        var nearestX = ax + (abx * t);
        var nearestY = ay + (aby * t);
        var dx = px - nearestX;
        var dy = py - nearestY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static async Task<bool> WaitForArrivalAsync(
        GridClient client,
        Vector3 target,
        float tolerance,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var at = client.Self.SimPosition;
            if (Vector3.Distance(at, target) <= tolerance)
            {
                return true;
            }

            if ((DateTime.UtcNow - startedAt) >= timeout)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static Vector3 ClampLocalPosition(Vector3 pos)
    {
        return new Vector3(
            Math.Clamp(pos.X, 1f, 255f),
            Math.Clamp(pos.Y, 1f, 255f),
            Math.Clamp(pos.Z, 0f, 4096f));
    }

    private static Vector3 ClampScale(Vector3 scale)
    {
        return new Vector3(
            Math.Clamp(scale.X, 0.01f, 64f),
            Math.Clamp(scale.Y, 0.01f, 64f),
            Math.Clamp(scale.Z, 0.01f, 64f));
    }

    private static Vector3 Flatten(Vector3 source)
    {
        return new Vector3(source.X, source.Y, 0f);
    }

    private static Vector3 Negate(Vector3 source)
    {
        return new Vector3(-source.X, -source.Y, -source.Z);
    }

    private static Vector3 ScaleToLength(Vector3 source, float length)
    {
        var norm = source.Length();
        if (norm <= 0.0001f)
        {
            return new Vector3(0f, length, 0f);
        }

        var scale = length / norm;
        return new Vector3(source.X * scale, source.Y * scale, source.Z * scale);
    }

    private static Vector3 Interpolate(Vector3 from, Vector3 to, float ratio)
    {
        return new Vector3(
            from.X + ((to.X - from.X) * ratio),
            from.Y + ((to.Y - from.Y) * ratio),
            from.Z + ((to.Z - from.Z) * ratio));
    }

    private static string FormatVector(Vector3 pos)
    {
        return $"<{pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}>";
    }

    private static PrimSummary ToPrimSummary(Primitive prim, Vector3 at)
    {
        return new PrimSummary(
            prim.LocalID,
            prim.ID.ToString(),
            prim.ParentID,
            prim.Properties?.Name,
            prim.Type.ToString(),
            prim.Position.X,
            prim.Position.Y,
            prim.Position.Z,
            Vector3.Distance(at, prim.Position));
    }

    private static bool TryParseLocalIdsCsv(string localIdsCsv, out List<uint> localIds, out string error)
    {
        localIds = new List<uint>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(localIdsCsv))
        {
            error = "localIdsCsv is required (comma-separated local IDs).";
            return false;
        }

        var parts = localIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "No valid local IDs were provided.";
            return false;
        }

        foreach (var part in parts)
        {
            if (!uint.TryParse(part, out var id))
            {
                error = $"Invalid local ID '{part}'. All IDs must be unsigned integers.";
                return false;
            }

            if (!localIds.Contains(id))
            {
                localIds.Add(id);
            }
        }

        return true;
    }

    private static bool TryParseLlsdPayload(string payload, string payloadFormat, out OSD osd, out string error)
    {
        osd = new OSD();
        error = string.Empty;

        var format = (payloadFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(format))
        {
            format = "auto";
        }

        try
        {
            osd = format switch
            {
                "auto" => OSDParser.Deserialize(payload),
                "json" => OSDParser.DeserializeJson(payload),
                "xml" or "llsdxml" or "llsd-xml" => OSDParser.DeserializeLLSDXml(payload),
                _ => throw new ArgumentException("payloadFormat must be one of: auto, json, xml.")
            };

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse LLSD payload ({format}): {ex.Message}";
            return false;
        }
    }

    private static bool TryBuildEnvironmentDataFromPayloadMap(OSDMap payloadMap, out EnvironmentData environment, out string error)
    {
        environment = new EnvironmentData();
        error = string.Empty;

        // Accept either a direct EnvironmentData map or an ExtEnvironment-style wrapper map
        // containing an "environment" map.
        OSDMap? environmentMap = null;
        if (payloadMap.TryGetValue("environment", out var wrappedEnvironment))
        {
            environmentMap = wrappedEnvironment as OSDMap;
            if (environmentMap == null)
            {
                error = "payload contains an 'environment' key, but its value is not an LLSD map/object.";
                return false;
            }
        }
        else
        {
            environmentMap = payloadMap;
        }

        try
        {
            environment.Deserialize(environmentMap);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to deserialize EnvironmentData payload: {ex.Message}";
            return false;
        }
    }

    private async Task<Primitive?> WaitForCreatedPrimAsync(
        GridClient client,
        Simulator simulator,
        Vector3 expectedPosition,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Primitive>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnObjectUpdate(object? sender, PrimEventArgs e)
        {
            if (!ReferenceEquals(e.Simulator, simulator))
            {
                return;
            }

            if ((e.Prim.Flags & PrimFlags.CreateSelected) == 0)
            {
                return;
            }

            if (Vector3.Distance(e.Prim.Position, expectedPosition) > 24f)
            {
                return;
            }

            tcs.TrySetResult(e.Prim);
        }

        client.Objects.ObjectUpdate += OnObjectUpdate;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Objects.ObjectUpdate -= OnObjectUpdate;
        }
    }

    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        var client = _client;
        if (client == null || e.IM.FromAgentID == client.Self.AgentID)
        {
            return;
        }

        var from = e.IM.FromAgentName;
        var text = e.IM.Message?.Trim() ?? string.Empty;
        var isDialogBridgePayload = text.StartsWith(LslDialogBridgeReplyPrefix + "|", StringComparison.OrdinalIgnoreCase);
        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent
            && e.IM.Dialog != InstantMessageDialog.SessionSend
            && e.IM.Dialog != InstantMessageDialog.MessageFromObject
            && !isDialogBridgePayload)
        {
            return;
        }

        Console.WriteLine($"[im] ({e.IM.Dialog}) {from}: {SanitizeImLogText(text)}");

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (e.IM.Dialog == InstantMessageDialog.MessageFromObject || isDialogBridgePayload)
        {
            var bridgeSenderObjectId = e.IM.FromAgentID;
            if (isDialogBridgePayload && e.IM.IMSessionID != UUID.Zero)
            {
                // For object-origin payloads, IMSessionID carries the object UUID in OpenSim.
                bridgeSenderObjectId = e.IM.IMSessionID;
            }

            _ = Task.Run(async () =>
            {
                await TryHandleLslDialogBridgeReplyAsync(client, bridgeSenderObjectId, e.IM.FromAgentName, text).ConfigureAwait(false);
            });
            return;
        }

        if (IsLikelyTypingIndicator(e.IM, text))
        {
            Console.WriteLine($"[im] typing indicator ignored for {from} ({e.IM.Dialog}).");
            return;
        }

        if (IsDuplicateImEvent(e.IM.FromAgentID, text, e.IM.Timestamp))
        {
            Console.WriteLine($"[im] duplicate suppressed for {from} ({e.IM.Dialog}).");
            return;
        }

        if (IsHandlerRestricted() && !IsHandlerAvatar(from))
        {
            Console.WriteLine($"[im] denied non-handler IM from {from}. Handler is '{_handlerFullName}'.");
            try
            {
                client.Self.InstantMessage(e.IM.FromAgentID, $"Hi! I can currently only accept instructions from my handler ({_handlerFullName}).");
            }
            catch
            {
                // Ignore failures while trying to send access-denied feedback.
            }

            return;
        }

        var conversationKey = $"im:{e.IM.FromAgentID}";
        _conversationAgentByKey[conversationKey] = e.IM.FromAgentID;
        _conversationNameByKey[conversationKey] = from;
        lock (_recentImSpeakerLock)
        {
            _lastImSpeakerAgentId = e.IM.FromAgentID;
            _lastImSpeakerName = from;
            _lastImConversationKey = conversationKey;
        }

        _ = Task.Run(async () =>
        {
            var gate = _imConversationLocks.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
            CancellationTokenSource? inFlightRequestCts = null;
            if (!await gate.WaitAsync(0).ConfigureAwait(false))
            {
                if (text.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*usage", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*help", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*dialog", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*dialogs", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*permission", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*question", StringComparison.OrdinalIgnoreCase))
                {
                    var handledBusyCommand = await TryHandleStarCommandAsync(client, e.IM.FromAgentID, from, conversationKey, text).ConfigureAwait(false);
                    if (handledBusyCommand)
                    {
                        return;
                    }
                }

                var handledBusyDialog = TryHandlePendingScriptDialogBeforeRouting(client, e.IM.FromAgentID, from, conversationKey, text);
                if (handledBusyDialog)
                {
                    return;
                }

                var handledBusyPromptReply = await TryHandlePendingTextPromptReplyBeforeRoutingAsync(
                    client,
                    e.IM.FromAgentID,
                    from,
                    conversationKey,
                    text).ConfigureAwait(false);
                if (handledBusyPromptReply)
                {
                    return;
                }

                if (text.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCancelCommandAsync(client, e.IM.FromAgentID, from, conversationKey).ConfigureAwait(false);
                    return;
                }

                try
                {
                    Console.WriteLine($"[im] overlapping message while previous request is still in flight for {from} ({conversationKey}).");
                    client.Self.InstantMessage(e.IM.FromAgentID, "I am still working on your previous request. You can send *cancel to abort while waiting.");
                }
                catch
                {
                    // Ignore failures while trying to report overlap state.
                }

                return;
            }

            var startedAt = Stopwatch.StartNew();
            try
            {
                if (_opencodeChat == null)
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, "AI chat is currently disabled by configuration.");
                    return;
                }

                if (text.StartsWith('*'))
                {
                    var handled = await TryHandleStarCommandAsync(client, e.IM.FromAgentID, from, conversationKey, text).ConfigureAwait(false);
                    if (handled)
                    {
                        return;
                    }
                }

                var handledDialog = TryHandlePendingScriptDialogBeforeRouting(client, e.IM.FromAgentID, from, conversationKey, text);
                if (handledDialog)
                {
                    return;
                }

                var handledPromptReply = await TryHandlePendingTextPromptReplyBeforeRoutingAsync(
                    client,
                    e.IM.FromAgentID,
                    from,
                    conversationKey,
                    text).ConfigureAwait(false);
                if (handledPromptReply)
                {
                    return;
                }

                TryBindRestoredOpencodeSessionToConversation(conversationKey);
                var sendOptions = BuildSendOptions(conversationKey);
                // TEMP(event-first migration): remove this watcher once event-driven permission/question
                // routing is proven stable under reconnect/load; keep only bounded fallback polling.
                using var requestCts = new CancellationTokenSource();
                inFlightRequestCts = requestCts;
                _inFlightRequestCtsByConversation.AddOrUpdate(
                    conversationKey,
                    requestCts,
                    (_, previous) =>
                    {
                        try
                        {
                            previous.Cancel();
                        }
                        catch
                        {
                            // Best effort: old inflight token may already be disposed.
                        }

                        previous.Dispose();
                        return requestCts;
                    });

                using var inFlightQuestionWatchCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token);
                var inFlightQuestionWatchTask = Task.Run(() =>
                    NotifyPendingQuestionDuringInFlightRequestAsync(
                        client,
                        e.IM.FromAgentID,
                        from,
                        conversationKey,
                        inFlightQuestionWatchCts.Token));

                // TODO(security): enforce who the AI is allowed to talk to (allowlist, roles, or parcel/group checks).
                Console.WriteLine($"[im] routing to opencode: from={from} conversation={conversationKey} textLength={text.Length} model={(sendOptions?.ModelId ?? "(default)")}");
                var reply = await _opencodeChat.SendMessageAsync(
                    conversationKey: conversationKey,
                    title: $"OpenSim IM with {from}",
                    message: text,
                    options: sendOptions,
                    cancellationToken: requestCts.Token).ConfigureAwait(false);
                if (reply.Usage != null)
                {
                    _latestUsageByConversation[conversationKey] = reply.Usage;
                }

                TrySaveOpencodeSessionStateForConversation(conversationKey);
                startedAt.Stop();
                Console.WriteLine($"[im] opencode reply received in {startedAt.ElapsedMilliseconds}ms: from={from} conversation={conversationKey} replyLength={reply.Text.Length}");

                var responseText = reply.IsConfirmationPrompt
                    ? reply.Text + "\n\nReply with yes or no to continue."
                    : reply.Text;

                if (reply.PendingPermissions != null && reply.PendingPermissions.Count > 0)
                {
                    var latestPermission = reply.PendingPermissions
                        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Id));
                    if (latestPermission != null)
                    {
                        await OfferPermissionPromptWithFallbackAsync(client, e.IM.FromAgentID, from, conversationKey, latestPermission.SessionId, latestPermission).ConfigureAwait(false);
                    }
                }
                else
                {
                    var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                    if (!string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        var eventFirstPermissions = await GetPendingPermissionsEventFirstAsync(currentSessionId, CancellationToken.None).ConfigureAwait(false);
                        if (eventFirstPermissions.Count > 0)
                        {
                            var latestPermission = eventFirstPermissions[0];
                            if (!_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                                || !announcedPermissionId.Equals(latestPermission.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                await OfferPermissionPromptWithFallbackAsync(client, e.IM.FromAgentID, from, conversationKey, currentSessionId, latestPermission).ConfigureAwait(false);
                            }
                        }
                    }
                }

                if (reply.PendingQuestions != null && reply.PendingQuestions.Count > 0)
                {
                    var latestQuestion = reply.PendingQuestions
                        .FirstOrDefault(q => !string.IsNullOrWhiteSpace(q.Id));
                    if (latestQuestion != null)
                    {
                        await OfferQuestionPromptWithFallbackAsync(client, e.IM.FromAgentID, from, conversationKey, latestQuestion.SessionId, latestQuestion).ConfigureAwait(false);
                    }
                }
                else
                {
                    // TEMP(event-first migration): this post-reply poll is a safety net for delayed emits.
                    // Delete after event stream handlers populate pending question state reliably.
                    // Some prompts are emitted asynchronously after the initial message response.
                    var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                    if (!string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        var polledQuestions = await GetPendingQuestionsEventFirstAsync(currentSessionId, CancellationToken.None).ConfigureAwait(false);
                        if (polledQuestions.Count > 0)
                        {
                            if (!_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedQuestionId)
                                || !announcedQuestionId.Equals(polledQuestions[0].Id, StringComparison.OrdinalIgnoreCase))
                            {
                                await OfferQuestionPromptWithFallbackAsync(client, e.IM.FromAgentID, from, conversationKey, currentSessionId, polledQuestions[0]).ConfigureAwait(false);
                            }
                        }
                    }
                }

                foreach (var chunk in SplitForInstantMessage(responseText, 900))
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, chunk);
                    Console.WriteLine($"[im] -> {from}: {chunk}");
                }

                StopTypingIndicatorIfActive();

                inFlightQuestionWatchCts.Cancel();
                try
                {
                    await inFlightQuestionWatchTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore watcher cancellation or transient polling errors.
                }

                // TEMP(event-first migration): remove this delayed poll task once event-driven prompt
                // delivery is reliable across reconnects and all tested providers.
                // Some question prompts can arrive slightly after the first reply payload.
                _ = Task.Run(() => NotifyPendingQuestionIfAppearsAsync(client, e.IM.FromAgentID, from, conversationKey));
            }
            catch (OperationCanceledException) when (inFlightRequestCts?.IsCancellationRequested == true)
            {
                startedAt.Stop();
                Console.WriteLine($"[im] opencode request canceled by handler after {startedAt.ElapsedMilliseconds}ms: from={from} conversation={conversationKey}");
                try
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, "Canceled the current request.");
                }
                catch
                {
                    // Ignore failures while trying to report cancellation.
                }
            }
            catch (OperationCanceledException ex) when (IsLikelyBackendTimeout(ex))
            {
                startedAt.Stop();
                Console.WriteLine($"[im] opencode timeout after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                _opencodeChat?.ResetConversation(conversationKey);
                try
                {
                    client.Self.InstantMessage(
                        e.IM.FromAgentID,
                        "The AI is taking longer than expected and timed out. Please try again in a moment.");
                }
                catch
                {
                    // Ignore failures while trying to report backend timeout errors.
                }
            }
            catch (Exception ex)
            {
                startedAt.Stop();
                if (IsLikelyBackendTimeout(ex))
                {
                    Console.WriteLine($"[im] opencode timeout after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                    _opencodeChat?.ResetConversation(conversationKey);
                    try
                    {
                        client.Self.InstantMessage(
                            e.IM.FromAgentID,
                            "The AI is taking longer than expected and timed out. Please try again in a moment.");
                    }
                    catch
                    {
                        // Ignore failures while trying to report backend timeout errors.
                    }

                    return;
                }

                Console.WriteLine($"[im] failed to route to opencode after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                _opencodeChat?.ResetConversation(conversationKey);
                // Preserve per-IM overrides (provider/model/thinking) across transient backend failures.
                try
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, "Sorry, I could not reach the AI service right now.");
                }
                catch
                {
                    // Ignore failures while trying to report backend errors.
                }
            }
            finally
            {
                if (inFlightRequestCts != null
                    && _inFlightRequestCtsByConversation.TryGetValue(conversationKey, out var currentInFlightCts)
                    && ReferenceEquals(currentInFlightCts, inFlightRequestCts))
                {
                    _inFlightRequestCtsByConversation.TryRemove(conversationKey, out _);
                }

                var activeSessionId = _opencodeChat?.GetConversationSessionId(conversationKey);
                if (!string.IsNullOrWhiteSpace(activeSessionId))
                {
                    MarkOpencodeSessionIdle(activeSessionId);
                }

                StopTypingIndicatorIfActive();
                gate.Release();
            }
        });
    }

    private void OnOpencodeSessionStatusChanged(OpencodeSessionStatusEvent statusEvent)
    {
        if (statusEvent == null || string.IsNullOrWhiteSpace(statusEvent.SessionId))
        {
            return;
        }

        var normalizedStatus = statusEvent.StatusType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedStatus == "busy")
        {
            _busyOpencodeSessions[statusEvent.SessionId] = 1;
            UpdateBusyHoverText(incrementDots: true);
            PulseTypingIndicator(statusEvent.SessionId);
            return;
        }

        if (normalizedStatus == "retry")
        {
            LogRetryStatusEvent(statusEvent.SessionId, statusEvent.StatusMessage);
            return;
        }

        if (normalizedStatus == "idle")
        {
            MarkOpencodeSessionIdle(statusEvent.SessionId);
        }
    }

    private void OnOpencodeMessagePartUpdated(OpencodeMessagePartUpdatedEvent partEvent)
    {
        if (partEvent == null || string.IsNullOrWhiteSpace(partEvent.SessionId))
        {
            return;
        }

        PulseTypingIndicator(partEvent.SessionId);
    }

    private void PulseTypingIndicator(string? sessionIdHint = null)
    {
        var client = _client;
        if (!_connected || client == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var shouldEmitStart = false;
        CancellationTokenSource? stopCts;
        lock (_typingStateLock)
        {
            if (!_typingIndicatorActive || (now - _lastTypingPulseAt).TotalMilliseconds >= TypingPulseMinimumIntervalMs)
            {
                shouldEmitStart = true;
                _lastTypingPulseAt = now;
            }

            _typingIndicatorActive = true;
            _typingStopCts?.Cancel();
            _typingStopCts?.Dispose();
            _typingStopCts = new CancellationTokenSource();
            stopCts = _typingStopCts;
        }

        if (shouldEmitStart)
        {
            try
            {
                client.Self.Chat(string.Empty, 0, ChatType.StartTyping);
                client.Self.AnimationStart(Animations.TYPE, false);
                SendImTypingState(client, isTyping: true, sessionIdHint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[typing] failed to emit StartTyping: {ex.Message}");
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TypingStopDelayMs, stopCts!.Token).ConfigureAwait(false);
                StopTypingIndicatorIfActive();
            }
            catch (OperationCanceledException)
            {
                // New typing pulse arrived; this stop timer is stale.
            }
        });
    }

    private void StopTypingIndicatorIfActive()
    {
        var client = _client;
        if (!_connected || client == null)
        {
            return;
        }

        var shouldStop = false;
        lock (_typingStateLock)
        {
            if (_typingIndicatorActive)
            {
                shouldStop = true;
                _typingIndicatorActive = false;
            }

            _typingStopCts?.Cancel();
            _typingStopCts?.Dispose();
            _typingStopCts = null;
        }

        if (!shouldStop)
        {
            return;
        }

        try
        {
            client.Self.Chat(string.Empty, 0, ChatType.StopTyping);
            client.Self.AnimationStop(Animations.TYPE, false);
            SendImTypingState(client, isTyping: false, sessionIdHint: null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[typing] failed to emit StopTyping: {ex.Message}");
        }
    }

    private void SendImTypingState(GridClient client, bool isTyping, string? sessionIdHint)
    {
        var dialog = isTyping ? InstantMessageDialog.StartTyping : InstantMessageDialog.StopTyping;
        var targets = new HashSet<UUID>();

        if (_opencodeChat != null && !string.IsNullOrWhiteSpace(sessionIdHint))
        {
            foreach (var pair in _conversationAgentByKey)
            {
                if (pair.Value == UUID.Zero)
                {
                    continue;
                }

                var mappedSessionId = _opencodeChat.GetConversationSessionId(pair.Key);
                if (!string.IsNullOrWhiteSpace(mappedSessionId)
                    && mappedSessionId.Equals(sessionIdHint, StringComparison.OrdinalIgnoreCase))
                {
                    targets.Add(pair.Value);
                }
            }
        }

        if (targets.Count == 0)
        {
            lock (_recentImSpeakerLock)
            {
                if (_lastImSpeakerAgentId != UUID.Zero)
                {
                    targets.Add(_lastImSpeakerAgentId);
                }
            }
        }

        foreach (var target in targets)
        {
            try
            {
                client.Self.InstantMessage(
                    client.Self.Name,
                    target,
                    string.Empty,
                    UUID.Zero,
                    dialog,
                    InstantMessageOnline.Online,
                    Vector3.Zero,
                    UUID.Zero,
                    Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[typing] failed to emit {(isTyping ? "StartTyping" : "StopTyping")} IM state: {ex.Message}");
            }
        }
    }

    private void MarkOpencodeSessionIdle(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        _busyOpencodeSessions.TryRemove(sessionId, out _);
        if (_busyOpencodeSessions.IsEmpty)
        {
            ClearBusyHoverText();
        }
    }

    private void UpdateBusyHoverText(bool incrementDots)
    {
        var now = DateTimeOffset.UtcNow;
        string hoverText;
        lock (_hoverStateLock)
        {
            if (incrementDots && (now - _lastHoverBusyUpdateAt).TotalMilliseconds < HoverBusyUpdateMinimumIntervalMs)
            {
                return;
            }

            if (incrementDots)
            {
                _busyHoverDots++;
                if (_busyHoverDots > 4)
                {
                    _busyHoverDots = 1;
                }
            }
            else if (_busyHoverDots <= 0)
            {
                _busyHoverDots = 1;
            }

            _lastHoverBusyUpdateAt = now;
            hoverText = "Thinking " + new string('.', _busyHoverDots);
        }

        SendHoverBridgeCommand("set", hoverText);
    }

    private void ClearBusyHoverText()
    {
        lock (_hoverStateLock)
        {
            _busyHoverDots = 0;
            _lastHoverBusyUpdateAt = DateTimeOffset.MinValue;
        }

        SendHoverBridgeCommand("clear", string.Empty);
    }

    private void SendHoverBridgeCommand(string mode, string text)
    {
        var client = _client;
        if (!_connected || client == null)
        {
            return;
        }

        UUID pinnedObjectId;
        lock (_dialogBridgeTrustLock)
        {
            pinnedObjectId = _trustedDialogBridgeObjectId;
        }

        var payload = string.Join("|", new[]
        {
            LslDialogBridgeHoverRequestPrefix,
            EncodeDialogToken(pinnedObjectId == UUID.Zero ? string.Empty : pinnedObjectId.ToString()),
            EncodeDialogToken(mode ?? string.Empty),
            EncodeDialogToken(text ?? string.Empty)
        });

        try
        {
            client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dialog-bridge] hover command failed: {ex.Message}");
        }
    }

    private OpencodeSendOptions? BuildSendOptions(string conversationKey)
    {
        _imConversationConfigs.TryGetValue(conversationKey, out var cfg);
        cfg ??= GetPersistedDefaultConversationConfigSnapshot();

        var systemPrompt = BuildLayeredPromptText();
        var modelId = cfg?.ModelId ?? GetStartupDefaultModelId();
        var thinkingLevel = cfg?.ThinkingLevel;

        if (cfg == null && string.IsNullOrWhiteSpace(systemPrompt) && string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return new OpencodeSendOptions(modelId, thinkingLevel, systemPrompt);
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
        }

        return sources.Count == 0 ? "prompt: no active sources" : "prompt sources: " + string.Join(", ", sources);
    }

    private string? BuildLayeredPromptText()
    {
        if (!_options.PromptHandlingEnabled)
        {
            return null;
        }

        var layers = new List<string>();

        if (_options.PromptBuiltInEnabled)
        {
            layers.Add("[bridge]\n" + ClampPromptLength(BuiltInBridgePrompt));
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
            lock (_promptStateLock)
            {
                notecardPrompt = _activeAgentsNotecardPrompt;
            }

            if (!string.IsNullOrWhiteSpace(notecardPrompt))
            {
                layers.Add("[in-world AGENTS.md notecard]\n" + notecardPrompt);
            }
        }

        return layers.Count == 0 ? null : string.Join("\n\n", layers);
    }

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

    private static bool IsLikelyBackendTimeout(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return true;
        }

        // HttpClient timeouts often arrive as a TaskCanceledException/OperationCanceledException.
        if (ex is TaskCanceledException)
        {
            return true;
        }

        var message = ex.Message ?? string.Empty;
        return message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryHandleStarCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0 || raw[0] != '*')
        {
            return false;
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
                    _imConversationConfigs.TryRemove(conversationKey, out _);
                    _opencodeChat?.ResetConversation(conversationKey);
                    SetPersistedDefaultConversationConfig(null);
                    TrySaveOpencodeSessionStateForConversation(conversationKey, null);
                    _latestUsageByConversation.TryRemove(conversationKey, out _);
                    SendImText(client, agentId, from, "Conversation AI settings reset for this IM. Using server defaults.");
                    return true;
                case "cancel":
                    await HandleCancelCommandAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
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
                BuildStarHelpText("configure"),
                BuildStarHelpText("reset")),
            "status" => "*status - Show current provider/model/thinking/session and prompt source state for this IM.",
            "usage" => "*usage - Show the latest Opencode response usage for this IM conversation (cost/input/output/reasoning/cache).",
            "cancel" => "*cancel - Abort the current in-flight AI request for this IM conversation.",
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

        if (!_imConversationConfigs.TryGetValue(conversationKey, out var cfg))
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
                default:
                    SendImText(client, agentId, from, "Usage: *prompt show [effective|builtin|project|notecard]");
                    return Task.CompletedTask;
            }

            SendImText(client, agentId, from, BuildPromptPreviewText(promptName, promptText));
            return Task.CompletedTask;
        }

        if (sub == "clear-notecard")
        {
            if (_options.PromptNotecardRequireHandler && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler may clear the AGENTS.md notecard prompt layer.");
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

        SendImText(client, agentId, from, "Usage: *prompt status | *prompt show [effective|builtin|project|notecard] | *prompt clear-notecard | *prompt reload-project");
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
                $"trusted owner pin: {(pinnedOwnerId == UUID.Zero ? "(none)" : pinnedOwnerId.ToString())}",
                "Install command: *bridge install",
                "Uninstall command: *bridge uninstall [keep-scripts]"
            };
            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (sub == "install")
        {
            if (IsHandlerRestricted() && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler may run *bridge install.");
                return;
            }

            var install = await DialogBridgeInstallAsync(
                null,
                objectName: "Opencode Dialog Bridge",
                objectDescription: "Auto-installed dialog bridge prim",
                folderId: null,
                offsetX: 1.5f,
                offsetY: 0f,
                offsetZ: 0.5f,
                pinAsTrustedSender: true,
                CancellationToken.None).ConfigureAwait(false);

            SendImText(client, agentId, from, install.Message);
            return;
        }

        if (sub == "uninstall")
        {
            if (IsHandlerRestricted() && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler may run *bridge uninstall.");
                return;
            }

            var keepScripts = parts.Skip(1).Any(p =>
                p.Equals("keep-scripts", StringComparison.OrdinalIgnoreCase)
                || p.Equals("--keep-scripts", StringComparison.OrdinalIgnoreCase)
                || p.Equals("no-delete-scripts", StringComparison.OrdinalIgnoreCase));
            var deleteScripts = !keepScripts;

            var uninstall = await DialogBridgeUninstallAsync(deleteScripts, clearTrustPins: true, CancellationToken.None).ConfigureAwait(false);
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

    private bool TryOfferQuestionViaLslDialogBridge(GridClient client, string conversationKey, OpencodePendingQuestion question)
    {
        if (question.Options.Count == 0)
        {
            Console.WriteLine($"[dialog-bridge] skip offer: no options for question {question.Id}.");
            return false;
        }

        if (!_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            || targetAgentId == UUID.Zero)
        {
            Console.WriteLine($"[dialog-bridge] skip offer: no target agent mapped for conversation {conversationKey}.");
            return false;
        }

        // Strict alpha payload format:
        // dlgreq|conversation|questionId|target|header|prompt|optionCount|opt1|opt2|...
        var header = question.Header?.Trim() ?? string.Empty;
        var prompt = BuildCompactQuestionDialogPrompt(question);
        var payload = BuildLslDialogBridgeRequestPayloadWithinLimit(
            conversationKey,
            question.Id,
            targetAgentId,
            header,
            prompt,
            question.Options,
            out var wasCompacted);
        if (wasCompacted)
        {
            Console.WriteLine($"[dialog-bridge] compacted question payload for {question.Id}: {payload.Length} chars.");
        }

        client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        if (payload.Length > LslDialogBridgeMaxPayloadLength)
        {
            Console.WriteLine($"[dialog-bridge] warning: payload length {payload.Length} may be truncated by simulator chat limits.");
        }
        Console.WriteLine(
            $"[dialog-bridge] offered question via channel {LslDialogBridgeRequestChannel}: conversation={conversationKey} question={question.Id} options={question.Options.Count} target={targetAgentId} payloadLength={payload.Length}");
        return true;
    }

    private bool TryOfferPermissionViaLslDialogBridge(GridClient client, string conversationKey, OpencodePendingPermission permission)
    {
        var permissionId = permission.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(permissionId))
        {
            Console.WriteLine("[dialog-bridge] skip offer: permission id is missing.");
            return false;
        }

        if (!_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            || targetAgentId == UUID.Zero)
        {
            Console.WriteLine($"[dialog-bridge] skip offer: no target agent mapped for conversation {conversationKey}.");
            return false;
        }

        var header = BuildPermissionDialogHeader(permission);
        var prompt = BuildCompactPermissionDialogPrompt(permission);
        // Tag permission request IDs so dialog replies can be routed deterministically.
        var bridgeRequestId = LslDialogBridgePermissionRequestPrefix + permissionId;
        var payload = BuildLslDialogBridgeRequestPayloadWithinLimit(
            conversationKey,
            bridgeRequestId,
            targetAgentId,
            header,
            prompt,
            LslPermissionDialogOptions,
            out var wasCompacted);
        if (wasCompacted)
        {
            Console.WriteLine($"[dialog-bridge] compacted permission payload for {permissionId}: {payload.Length} chars.");
        }

        client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        if (payload.Length > LslDialogBridgeMaxPayloadLength)
        {
            Console.WriteLine($"[dialog-bridge] warning: payload length {payload.Length} may be truncated by simulator chat limits.");
        }

        Console.WriteLine(
            $"[dialog-bridge] offered permission via channel {LslDialogBridgeRequestChannel}: conversation={conversationKey} permission={permissionId} target={targetAgentId} payloadLength={payload.Length}");
        return true;
    }

    private async Task<bool> TryHandleLslDialogBridgeReplyAsync(GridClient client, UUID senderObjectId, string senderName, string text)
    {
        if (!TryParseLslDialogBridgeReply(text, out var conversationKey, out var requestId, out var answer))
        {
            Console.WriteLine("[dialog-bridge] ignored object IM: not a dialog-bridge reply payload.");
            return false;
        }

        if (!IsTrustedDialogBridgeSender(client, senderObjectId, senderName, conversationKey))
        {
            return false;
        }

        if (IsDuplicateDialogBridgeReply(conversationKey, requestId, answer))
        {
            Console.WriteLine($"[dialog-bridge] duplicate reply suppressed: conversation={conversationKey} request={requestId} answer={answer}");
            return true;
        }

        Console.WriteLine($"[dialog-bridge] received reply payload: conversation={conversationKey} request={requestId} answer={answer}");

        if (_opencodeChat == null || string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(requestId))
        {
            Console.WriteLine("[dialog-bridge] dropped reply: opencode chat unavailable or payload missing conversation/request id.");
            return false;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Console.WriteLine($"[dialog-bridge] dropped reply: no active opencode session for conversation {conversationKey}.");
            return false;
        }

        if (await TryHandleLslDialogBridgePermissionReplyAsync(client, conversationKey, sessionId, requestId, answer).ConfigureAwait(false))
        {
            ClearPendingPromptWait(conversationKey);
            _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
            return true;
        }

        var resolvedAnswer = await ResolveLslDialogBridgeAnswerAsync(sessionId, requestId, answer).ConfigureAwait(false);
        var ok = await _opencodeChat.ReplyToQuestionAsync(sessionId, requestId, new[] { resolvedAnswer }, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[dialog-bridge] forwarded reply to opencode: session={sessionId} question={requestId} success={ok} answer={resolvedAnswer}");
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptWait(conversationKey);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);

        if (_conversationAgentByKey.TryGetValue(conversationKey, out var agentId)
            && agentId != UUID.Zero)
        {
            var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
                ? displayName
                : "handler";
                
            if(!ok) {
                SendImText(client, agentId, from,
                    "I sent your dialog choice, but Opencode did not return an explicit success flag.");
            }
                
        }

        return true;
    }

    private bool IsTrustedDialogBridgeSender(GridClient client, UUID senderObjectId, string senderName, string conversationKey)
    {
        if (senderObjectId == UUID.Zero)
        {
            Console.WriteLine("[dialog-bridge] dropped reply: sender object UUID missing.");
            return false;
        }

        UUID pinnedObjectId;
        UUID pinnedOwnerId;
        bool requireTrustedSender;
        lock (_dialogBridgeTrustLock)
        {
            pinnedObjectId = _trustedDialogBridgeObjectId;
            pinnedOwnerId = _trustedDialogBridgeOwnerId;
            requireTrustedSender = _lslDialogBridgeRequireTrustedSender;
        }

        var ownerResolved = TryGetObjectOwnerIdFromCache(client, senderObjectId, out var senderOwnerId);
        var objectMatchesPin = pinnedObjectId != UUID.Zero && senderObjectId == pinnedObjectId;
        if (pinnedObjectId != UUID.Zero && senderObjectId != pinnedObjectId)
        {
            Console.WriteLine($"[dialog-bridge] dropped reply: untrusted object {senderObjectId} (expected {pinnedObjectId}) sender='{senderName}'.");
            return false;
        }

        if (pinnedOwnerId != UUID.Zero)
        {
            if (!ownerResolved)
            {
                if (objectMatchesPin)
                {
                    Console.WriteLine($"[dialog-bridge] warning: owner not resolved for pinned object {senderObjectId}; accepting due to object pin match.");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] dropped reply: owner not resolved for object {senderObjectId} while trusted owner pin is enabled ({pinnedOwnerId}).");
                    return false;
                }
            }
            else if (senderOwnerId != pinnedOwnerId)
            {
                if (objectMatchesPin)
                {
                    Console.WriteLine($"[dialog-bridge] warning: owner mismatch for pinned object {senderObjectId}. got={senderOwnerId} expected={pinnedOwnerId}; accepting due to object pin match.");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] dropped reply: owner mismatch for object {senderObjectId}. got={senderOwnerId} expected={pinnedOwnerId}");
                    return false;
                }
            }
        }

        if (!requireTrustedSender)
        {
            return true;
        }

        if (pinnedObjectId == UUID.Zero)
        {
            var shouldPersistTrustState = false;
            lock (_dialogBridgeTrustLock)
            {
                if (_trustedDialogBridgeObjectId == UUID.Zero)
                {
                    _trustedDialogBridgeObjectId = senderObjectId;
                    if (_trustedDialogBridgeOwnerId == UUID.Zero && ownerResolved)
                    {
                        _trustedDialogBridgeOwnerId = senderOwnerId;
                    }

                    Console.WriteLine($"[dialog-bridge] pinned trusted bridge sender from first valid reply: object={_trustedDialogBridgeObjectId} owner={_trustedDialogBridgeOwnerId} conversation={conversationKey}");
                    shouldPersistTrustState = true;
                }
                else if (_trustedDialogBridgeObjectId != senderObjectId)
                {
                    Console.WriteLine($"[dialog-bridge] dropped reply: sender object changed during pinning race. got={senderObjectId} pinned={_trustedDialogBridgeObjectId}");
                    return false;
                }
            }

            if (shouldPersistTrustState)
            {
                TrySaveDialogBridgeTrustStateToFile();
            }
        }

        return true;
    }

    private static bool TryGetObjectOwnerIdFromCache(GridClient client, UUID objectId, out UUID ownerId)
    {
        ownerId = UUID.Zero;
        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return false;
        }

        foreach (var prim in sim.ObjectsPrimitives.Values)
        {
            if (prim.ID != objectId)
            {
                continue;
            }

            if (prim.Properties?.OwnerID is UUID resolvedOwner && resolvedOwner != UUID.Zero)
            {
                ownerId = resolvedOwner;
                return true;
            }

            return false;
        }

        return false;
    }

    private async Task<bool> TryHandleLslDialogBridgePermissionReplyAsync(
        GridClient client,
        string conversationKey,
        string sessionId,
        string requestId,
        string answer)
    {
        var permissionId = requestId.Trim();
        var taggedPermissionRequest = false;
        if (permissionId.StartsWith(LslDialogBridgePermissionRequestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            taggedPermissionRequest = true;
            permissionId = permissionId[LslDialogBridgePermissionRequestPrefix.Length..].Trim();
        }

        var isPermissionId = IsCanonicalPermissionRequestId(permissionId);
        if (!isPermissionId)
        {
            var pending = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var match = pending.FirstOrDefault(p => p.Id.Equals(permissionId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                if (!taggedPermissionRequest)
                {
                    return false;
                }

                match = pending.FirstOrDefault();
                if (match == null)
                {
                    Console.WriteLine($"[dialog-bridge] tagged permission reply could not resolve pending permission for session={sessionId} request={requestId}");
                    return false;
                }
            }

            permissionId = match.Id;
        }

        if (!TryParseSimplePermissionResponse(answer, out var response, out var remember))
        {
            Console.WriteLine($"[dialog-bridge] permission reply not understood for {permissionId}: '{answer}'");
            if (_conversationAgentByKey.TryGetValue(conversationKey, out var agentId) && agentId != UUID.Zero)
            {
                var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
                    ? displayName
                    : "handler";
                SendImText(client, agentId, from,
                    "I could not understand that approval choice. Reply with: 1) yes, 2) no, 3) yes always, 4) no always.");
            }

            return true;
        }

        var ok = await _opencodeChat!.RespondToPermissionAsync(sessionId, permissionId, response, remember, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[dialog-bridge] forwarded permission reply to opencode: session={sessionId} permission={permissionId} success={ok} response={response} remember={remember}");
        _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);

        if (_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            && targetAgentId != UUID.Zero)
        {
            var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
                ? displayName
                : "handler";

            if (!ok)
            {
                SendImText(client, targetAgentId, from,
                    "I could not confirm that approval was accepted. If needed, try again.");
            }
        }

        return true;
    }

    private async Task<string> ResolveLslDialogBridgeAnswerAsync(string sessionId, string questionId, string answer)
    {
        var trimmedAnswer = answer?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedAnswer))
        {
            return string.Empty;
        }

        var pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var question = pending.FirstOrDefault(q => q.Id.Equals(questionId, StringComparison.OrdinalIgnoreCase));
        if (question == null || question.Options.Count == 0)
        {
            return trimmedAnswer;
        }

        if (TryResolveQuestionAnswer(question, trimmedAnswer, out var resolvedAnswer))
        {
            return resolvedAnswer;
        }

        var onceDecoded = DecodeDialogToken(trimmedAnswer);
        if (!onceDecoded.Equals(trimmedAnswer, StringComparison.Ordinal)
            && TryResolveQuestionAnswer(question, onceDecoded, out resolvedAnswer))
        {
            return resolvedAnswer;
        }

        foreach (var option in question.Options)
        {
            if (option.StartsWith(trimmedAnswer, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }

            var encodedOption = EncodeDialogToken(option);
            if (encodedOption.StartsWith(trimmedAnswer, StringComparison.OrdinalIgnoreCase)
                || trimmedAnswer.StartsWith(encodedOption, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return onceDecoded;
    }

    private static bool TryParseLslDialogBridgeReply(string text, out string conversationKey, out string requestId, out string answer)
    {
        conversationKey = string.Empty;
        requestId = string.Empty;
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length < 4 || !parts[0].Equals(LslDialogBridgeReplyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        conversationKey = DecodeDialogToken(parts[1]);
        requestId = DecodeDialogToken(parts[2]);
        answer = DecodeDialogToken(parts[3]);
        return !string.IsNullOrWhiteSpace(conversationKey)
            && !string.IsNullOrWhiteSpace(requestId)
            && !string.IsNullOrWhiteSpace(answer);
    }

    private static string BuildPermissionDialogHeader(OpencodePendingPermission permission)
    {
        var title = permission.Title?.Trim() ?? string.Empty;
        var hasHumanTitle = !string.IsNullOrWhiteSpace(title)
            && !title.StartsWith("per", StringComparison.OrdinalIgnoreCase)
            && !title.StartsWith("que", StringComparison.OrdinalIgnoreCase);
        return hasHumanTitle ? title : "Approval required";
    }

    private static string BuildPermissionDialogPrompt(OpencodePendingPermission permission)
    {
        if (!string.IsNullOrWhiteSpace(permission.Description))
        {
            return permission.Description.Trim();
        }

        if (!string.IsNullOrWhiteSpace(permission.Title))
        {
            return permission.Title.Trim();
        }

        return "Choose whether to allow this action.";
    }

    private static string BuildCompactQuestionDialogPrompt(OpencodePendingQuestion question)
    {
        var prompt = question.Question?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "Choose an option:";
        }

        var firstLine = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            firstLine = prompt;
        }

        const int maxLength = 120;
        return firstLine!.Length <= maxLength
            ? firstLine
            : firstLine[..(maxLength - 3)] + "...";
    }

    private static string BuildLslDialogBridgeRequestPayloadWithinLimit(
        string conversationKey,
        string requestId,
        UUID targetAgentId,
        string header,
        string prompt,
        IReadOnlyList<string> options,
        out bool wasCompacted)
    {
        wasCompacted = false;
        var normalizedHeader = header?.Trim() ?? string.Empty;
        var normalizedPrompt = prompt?.Trim() ?? string.Empty;

        var payload = BuildLslDialogBridgeRequestPayload(
            conversationKey,
            requestId,
            targetAgentId,
            normalizedHeader,
            normalizedPrompt,
            options);
        if (payload.Length <= LslDialogBridgeMaxPayloadLength)
        {
            return payload;
        }

        wasCompacted = true;

        // First shed prompt verbosity while keeping header context.
        normalizedPrompt = CompactForBridge(normalizedPrompt, 80);
        payload = BuildLslDialogBridgeRequestPayload(conversationKey, requestId, targetAgentId, normalizedHeader, normalizedPrompt, options);
        if (payload.Length <= LslDialogBridgeMaxPayloadLength)
        {
            return payload;
        }

        // If still too large, reduce both header and prompt until payload fits.
        normalizedHeader = CompactForBridge(normalizedHeader, 36);
        normalizedPrompt = CompactForBridge(normalizedPrompt, 36);
        payload = BuildLslDialogBridgeRequestPayload(conversationKey, requestId, targetAgentId, normalizedHeader, normalizedPrompt, options);
        if (payload.Length <= LslDialogBridgeMaxPayloadLength)
        {
            return payload;
        }

        // Last-resort minimal body to preserve operability over strict prompt fidelity.
        return BuildLslDialogBridgeRequestPayload(conversationKey, requestId, targetAgentId, "Approval required", "Choose an option.", options);
    }

    private static string CompactForBridge(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var candidate = string.IsNullOrWhiteSpace(firstLine) ? text.Trim() : firstLine;
        if (candidate.Length <= maxLength)
        {
            return candidate;
        }

        return candidate[..Math.Max(1, maxLength - 3)] + "...";
    }

    private static string BuildCompactPermissionDialogPrompt(OpencodePendingPermission permission)
    {
        var description = permission.Description?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            return BuildPermissionDialogPrompt(permission);
        }

        var lines = description
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var firstPattern = lines
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l[2..].Trim() : l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)
                && !l.EndsWith(":", StringComparison.Ordinal)
                && !l.StartsWith("remembered", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(firstPattern))
        {
            return firstPattern;
        }

        return lines[0];
    }

    private static string BuildLslDialogBridgeRequestPayload(
        string conversationKey,
        string requestId,
        UUID targetAgentId,
        string header,
        string prompt,
        IReadOnlyList<string> options)
    {
        var payloadParts = new List<string>
        {
            LslDialogBridgeRequestPrefix,
            EncodeDialogToken(conversationKey),
            EncodeDialogToken(requestId),
            EncodeDialogToken(targetAgentId.ToString()),
            EncodeDialogToken(header),
            EncodeDialogToken(prompt),
            options.Count.ToString()
        };
        payloadParts.AddRange(options.Select(EncodeDialogToken));
        return string.Join("|", payloadParts);
    }

    private static string EncodeDialogToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Keep payload small for simulator chat transport: escape only delimiter-critical chars.
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);
    }

    private static string DecodeDialogToken(string value)
        => Uri.UnescapeDataString(value ?? string.Empty);

    private static bool TryResolveQuestionAnswer(OpencodePendingQuestion question, string text, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim();
        var normalized = raw.ToLowerInvariant();
        var options = question.Options ?? Array.Empty<string>();

        if (options.Count > 0)
        {
            if (int.TryParse(normalized, out var optionIndex)
                && optionIndex >= 1
                && optionIndex <= options.Count)
            {
                answer = options[optionIndex - 1];
                return true;
            }

            var exact = options.FirstOrDefault(o => o.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                answer = exact;
                return true;
            }

            if (normalized is "yes" or "y")
            {
                var yesOption = options.FirstOrDefault(o => o.Contains("yes", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(yesOption))
                {
                    answer = yesOption;
                    return true;
                }
            }

            if (normalized is "no" or "n")
            {
                var noOption = options.FirstOrDefault(o => o.Contains("no", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(noOption))
                {
                    answer = noOption;
                    return true;
                }
            }
        }

        if (question.AllowsCustom != false)
        {
            answer = raw;
            return true;
        }

        return false;
    }

    private static string BuildFriendlyPermissionListLine(OpencodePendingPermission permission)
    {
        var requestId = permission.Id?.Trim() ?? string.Empty;
        var summary = BuildCompactPermissionDialogPrompt(permission);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = GetPermissionPrimaryText(permission, out _);
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            return summary;
        }

        return $"[{requestId}] {summary}";
    }

    private static string GetPermissionPrimaryText(OpencodePendingPermission permission, out bool titleLooksLikeId)
    {
        var requestId = permission.Id?.Trim() ?? string.Empty;
        var title = permission.Title?.Trim() ?? string.Empty;
        var description = permission.Description?.Trim() ?? string.Empty;
        titleLooksLikeId = !string.IsNullOrWhiteSpace(title)
            && (title.Equals(requestId, StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("per", StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("que", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        if (!string.IsNullOrWhiteSpace(title) && !titleLooksLikeId)
        {
            return title;
        }

        return "This action requires your approval.";
    }

    private static bool IsCanonicalPermissionRequestId(string? permissionId)
        => !string.IsNullOrWhiteSpace(permissionId)
            && permissionId.Trim().StartsWith("per", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<OpencodePendingPermission>> GetPendingPermissionsEventFirstAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<OpencodePendingPermission>();
        }

        var sessionFamily = await GetSessionFamilyIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFamily.Count == 0)
        {
            return Array.Empty<OpencodePendingPermission>();
        }

        var fromEventFamily = new List<OpencodePendingPermission>();
        foreach (var familySessionId in sessionFamily)
        {
            if (_opencodeChat.TryGetPendingPermissionsFromEvents(familySessionId, out var fromEvents)
                && fromEvents.Count > 0)
            {
                fromEventFamily.AddRange(fromEvents);
            }
        }

        if (fromEventFamily.Count > 0)
        {
            return fromEventFamily
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fromApiFamily = new List<OpencodePendingPermission>();
        foreach (var familySessionId in sessionFamily)
        {
            var fromApi = await _opencodeChat.ListPendingPermissionsAsync(familySessionId, cancellationToken).ConfigureAwait(false);
            if (fromApi.Count > 0)
            {
                fromApiFamily.AddRange(fromApi);
            }
        }

        if (fromApiFamily.Count == 0)
        {
            return Array.Empty<OpencodePendingPermission>();
        }

        return fromApiFamily
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<OpencodePendingQuestion>> GetPendingQuestionsEventFirstAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<OpencodePendingQuestion>();
        }

        var sessionFamily = await GetSessionFamilyIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFamily.Count == 0)
        {
            return Array.Empty<OpencodePendingQuestion>();
        }

        var fromEventFamily = new List<OpencodePendingQuestion>();
        foreach (var familySessionId in sessionFamily)
        {
            if (_opencodeChat.TryGetPendingQuestionsFromEvents(familySessionId, out var fromEvents)
                && fromEvents.Count > 0)
            {
                fromEventFamily.AddRange(fromEvents);
            }
        }

        if (fromEventFamily.Count > 0)
        {
            return fromEventFamily
                .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(q => q.Header, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fromApiFamily = new List<OpencodePendingQuestion>();
        foreach (var familySessionId in sessionFamily)
        {
            var fromApi = await _opencodeChat.ListPendingQuestionsAsync(familySessionId, cancellationToken).ConfigureAwait(false);
            if (fromApi.Count > 0)
            {
                fromApiFamily.AddRange(fromApi);
            }
        }

        if (fromApiFamily.Count == 0)
        {
            return Array.Empty<OpencodePendingQuestion>();
        }

        return fromApiFamily
            .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(q => q.Header, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetSessionFamilyIdsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<string>();
        }

        var rootSessionId = sessionId.Trim();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            rootSessionId
        };
        var queue = new Queue<string>();
        queue.Enqueue(rootSessionId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            IReadOnlyList<OpencodeSessionSummary> children;
            try
            {
                children = await _opencodeChat.GetSessionChildrenAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Keep discovered IDs even if one branch fails to enumerate.
                continue;
            }

            foreach (var child in children)
            {
                if (string.IsNullOrWhiteSpace(child.Id))
                {
                    continue;
                }

                var childSessionId = child.Id.Trim();
                if (visited.Add(childSessionId))
                {
                    queue.Enqueue(childSessionId);
                }
            }
        }

        return visited.ToList();
    }

    private async Task NotifyPendingQuestionIfAppearsAsync(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (_opencodeChat == null)
        {
            return;
        }

        // TEMP(event-first migration): delete this method once event stream routing replaces delayed
        // polling of /question. This exists only as a migration fallback.
        // Keep this short to avoid stale prompts, but long enough for async question.asked emission.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(false);

            var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            IReadOnlyList<OpencodePendingPermission> pendingPermissions;
            try
            {
                pendingPermissions = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var permission = pendingPermissions.FirstOrDefault();
            if (permission != null && !string.IsNullOrWhiteSpace(permission.Id))
            {
                if (_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                    && announcedPermissionId.Equals(permission.Id, StringComparison.OrdinalIgnoreCase))
                {
                    // Already announced; still check for pending questions in this same poll cycle.
                }
                else
                {
                    await OfferPermissionPromptWithFallbackAsync(client, agentId, from, conversationKey, sessionId, permission).ConfigureAwait(false);
                }
            }

            IReadOnlyList<OpencodePendingQuestion> pending;
            try
            {
                pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var question = pending.FirstOrDefault();
            if (question == null || string.IsNullOrWhiteSpace(question.Id))
            {
                continue;
            }

            _latestPendingQuestionByConversation[conversationKey] = question.Id;
            if (!_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedQuestionId)
                || !announcedQuestionId.Equals(question.Id, StringComparison.OrdinalIgnoreCase))
            {
                await OfferQuestionPromptWithFallbackAsync(client, agentId, from, conversationKey, sessionId, question).ConfigureAwait(false);
                continue;
            }
        }
    }

    private async Task NotifyPendingQuestionDuringInFlightRequestAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        CancellationToken cancellationToken)
    {
        if (_opencodeChat == null)
        {
            return;
        }

        // TEMP(event-first migration): delete this method once in-flight question/permission events
        // are forwarded directly to IM from the stream observer.
        // Keep watching until the in-flight request ends (token is canceled by caller).
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            IReadOnlyList<OpencodePendingPermission> pendingPermissions;
            try
            {
                pendingPermissions = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var permission = pendingPermissions.FirstOrDefault();
            if (permission != null && !string.IsNullOrWhiteSpace(permission.Id))
            {
                if (_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                    && announcedPermissionId.Equals(permission.Id, StringComparison.OrdinalIgnoreCase))
                {
                    // Already announced; still check for pending questions in this same poll cycle.
                }
                else
                {
                    await OfferPermissionPromptWithFallbackAsync(client, agentId, from, conversationKey, sessionId, permission).ConfigureAwait(false);
                }
            }

            IReadOnlyList<OpencodePendingQuestion> pendingQuestions;
            try
            {
                pendingQuestions = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var question = pendingQuestions.FirstOrDefault();
            if (question == null || string.IsNullOrWhiteSpace(question.Id))
            {
                continue;
            }

            _latestPendingQuestionByConversation[conversationKey] = question.Id;
            if (_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedId)
                && announcedId.Equals(question.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await OfferQuestionPromptWithFallbackAsync(client, agentId, from, conversationKey, sessionId, question).ConfigureAwait(false);
            continue;
        }
    }

    private bool IsDialogBridgePinned()
    {
        lock (_dialogBridgeTrustLock)
        {
            return _trustedDialogBridgeObjectId != UUID.Zero;
        }
    }

    private Task OfferPermissionPromptWithFallbackAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string sessionId,
        OpencodePendingPermission permission)
    {
        if (string.IsNullOrWhiteSpace(permission.Id))
        {
            return Task.CompletedTask;
        }

        _latestPendingPermissionByConversation[conversationKey] = permission.Id;
        if (!IsDialogBridgePinned())
        {
            Console.WriteLine($"[dialog-bridge] permission fallback to text: bridge not pinned. conversation={conversationKey} permission={permission.Id}");
            ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Permission, sessionId, permission.Id, permission: permission);
            return Task.CompletedTask;
        }

        if (TryOfferPermissionViaLslDialogBridge(client, conversationKey, permission))
        {
            _announcedPendingPermissionByConversation[conversationKey] = permission.Id;
            ArmDialogPromptTimeout(client, conversationKey, agentId, from, PendingPromptKind.Permission, sessionId, permission.Id, permission: permission);
            return Task.CompletedTask;
        }

        Console.WriteLine($"[dialog-bridge] permission fallback to text: offer failed. conversation={conversationKey} permission={permission.Id}");
        ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Permission, sessionId, permission.Id, permission: permission);
        return Task.CompletedTask;
    }

    private Task OfferQuestionPromptWithFallbackAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string sessionId,
        OpencodePendingQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.Id))
        {
            return Task.CompletedTask;
        }

        _latestPendingQuestionByConversation[conversationKey] = question.Id;
        if (!IsDialogBridgePinned())
        {
            Console.WriteLine($"[dialog-bridge] question fallback to text: bridge not pinned. conversation={conversationKey} question={question.Id}");
            ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Question, sessionId, question.Id, question: question);
            return Task.CompletedTask;
        }

        if (TryOfferQuestionViaLslDialogBridge(client, conversationKey, question))
        {
            _announcedPendingQuestionByConversation[conversationKey] = question.Id;
            ArmDialogPromptTimeout(client, conversationKey, agentId, from, PendingPromptKind.Question, sessionId, question.Id, question: question);
            return Task.CompletedTask;
        }

        Console.WriteLine($"[dialog-bridge] question fallback to text: offer failed. conversation={conversationKey} question={question.Id}");
        ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Question, sessionId, question.Id, question: question);
        return Task.CompletedTask;
    }

    private void ArmDialogPromptTimeout(
        GridClient client,
        string conversationKey,
        UUID agentId,
        string from,
        PendingPromptKind kind,
        string sessionId,
        string requestId,
        OpencodePendingPermission? permission = null,
        OpencodePendingQuestion? question = null)
    {
        ClearPendingPromptWait(conversationKey);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);

        var timeoutCts = new CancellationTokenSource();
        var wait = new PendingDialogPromptWait(kind, sessionId, requestId, agentId, from, permission, question, timeoutCts);
        _pendingDialogPromptWaitByConversation[conversationKey] = wait;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.DialogBridgePromptResponseTimeoutSeconds), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_pendingDialogPromptWaitByConversation.TryGetValue(conversationKey, out var currentWait)
                || !ReferenceEquals(currentWait, wait))
            {
                return;
            }

            if (!await IsPromptStillPendingAsync(wait, conversationKey).ConfigureAwait(false))
            {
                ClearPendingPromptWait(conversationKey);
                return;
            }

            Console.WriteLine($"[dialog-bridge] prompt fallback to text: timeout after {_options.DialogBridgePromptResponseTimeoutSeconds}s conversation={conversationKey} request={requestId}");
            ActivateTextPromptFallback(client, conversationKey, wait.AgentId, wait.From, wait.Kind, wait.SessionId, wait.RequestId, wait.Permission, wait.Question);
        });
    }

    private void ClearPendingPromptWait(string conversationKey)
    {
        if (!_pendingDialogPromptWaitByConversation.TryRemove(conversationKey, out var wait))
        {
            return;
        }

        try
        {
            wait.TimeoutCts.Cancel();
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            wait.TimeoutCts.Dispose();
        }
    }

    private void ActivateTextPromptFallback(
        GridClient client,
        string conversationKey,
        UUID agentId,
        string from,
        PendingPromptKind kind,
        string sessionId,
        string requestId,
        OpencodePendingPermission? permission = null,
        OpencodePendingQuestion? question = null)
    {
        ClearPendingPromptWait(conversationKey);
        _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
        if (kind == PendingPromptKind.Permission)
        {
            _announcedPendingPermissionByConversation[conversationKey] = requestId;
        }
        else
        {
            _announcedPendingQuestionByConversation[conversationKey] = requestId;
        }

        var state = new PendingTextPromptReply(
            kind,
            sessionId,
            requestId,
            agentId,
            from,
            permission,
            question,
            DateTimeOffset.UtcNow);

        _pendingTextPromptReplyByConversation[conversationKey] = state;

        var promptText = kind == PendingPromptKind.Permission
            ? BuildTextFallbackPermissionPrompt(permission ?? new OpencodePendingPermission(requestId, sessionId, string.Empty, null))
            : BuildTextFallbackQuestionPrompt(question ?? new OpencodePendingQuestion(requestId, sessionId, "Question", "Please answer.", Array.Empty<string>(), null, true));
        SendImText(client, agentId, from, promptText);
    }

    private async Task<bool> TryHandlePendingTextPromptReplyBeforeRoutingAsync(
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

        if (_opencodeChat == null
            || !_pendingTextPromptReplyByConversation.TryGetValue(conversationKey, out var state))
        {
            return false;
        }

        if (!await IsPromptStillPendingAsync(state, conversationKey).ConfigureAwait(false))
        {
            _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
            return false;
        }

        if (state.Kind == PendingPromptKind.Permission)
        {
            if (!TryParseSimplePermissionResponse(text, out var response, out var remember))
            {
                SendImText(client, agentId, from,
                    "I could not understand that approval choice. Reply with: yes, no, yes always, or no always.");
                return true;
            }

            _ = await _opencodeChat.RespondToPermissionAsync(state.SessionId, state.RequestId, response, remember, CancellationToken.None).ConfigureAwait(false);
            _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
            _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
            _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);
            return true;
        }

        var resolved = text.Trim();
        if (state.Question != null)
        {
            if (!TryResolveQuestionAnswer(state.Question, text, out resolved))
            {
                SendImText(client, agentId, from,
                    "I could not map that answer to the question options. Reply with option number or exact option text.");
                return true;
            }
        }

        _ = await _opencodeChat.ReplyToQuestionAsync(state.SessionId, state.RequestId, new[] { resolved }, CancellationToken.None).ConfigureAwait(false);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
        return true;
    }

    private async Task<bool> IsPromptStillPendingAsync(PendingTextPromptReply state, string conversationKey)
    {
        return await IsPromptStillPendingAsync(
            state.Kind,
            state.SessionId,
            state.RequestId,
            state.Permission,
            state.Question,
            conversationKey).ConfigureAwait(false);
    }

    private async Task<bool> IsPromptStillPendingAsync(PendingDialogPromptWait state, string conversationKey)
    {
        return await IsPromptStillPendingAsync(
            state.Kind,
            state.SessionId,
            state.RequestId,
            state.Permission,
            state.Question,
            conversationKey).ConfigureAwait(false);
    }

    private async Task<bool> IsPromptStillPendingAsync(
        PendingPromptKind kind,
        string sessionId,
        string requestId,
        OpencodePendingPermission? permission,
        OpencodePendingQuestion? question,
        string conversationKey)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        var effectiveSessionId = sessionId;
        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = _opencodeChat.GetConversationSessionId(conversationKey) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            return false;
        }

        if (kind == PendingPromptKind.Permission)
        {
            var pendingPermissions = await GetPendingPermissionsEventFirstAsync(effectiveSessionId, CancellationToken.None).ConfigureAwait(false);
            var match = pendingPermissions.Any(p => p.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));
            if (!match)
            {
                return false;
            }

            if (permission != null)
            {
                _latestPendingPermissionByConversation[conversationKey] = permission.Id;
            }

            return true;
        }

        var pendingQuestions = await GetPendingQuestionsEventFirstAsync(effectiveSessionId, CancellationToken.None).ConfigureAwait(false);
        var questionMatch = pendingQuestions.FirstOrDefault(q => q.Id.Equals(requestId, StringComparison.OrdinalIgnoreCase));
        if (questionMatch == null)
        {
            return false;
        }

        _latestPendingQuestionByConversation[conversationKey] = questionMatch.Id;
        return true;
    }

    private static string BuildTextFallbackPermissionPrompt(OpencodePendingPermission permission)
    {
        var summary = BuildCompactPermissionDialogPrompt(permission);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = GetPermissionPrimaryText(permission, out _);
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            lines.Add(summary);
        }

        lines.Add("Reply now with: yes, no, yes always, or no always.");
        return string.Join("\n", lines);
    }

    private static string BuildTextFallbackQuestionPrompt(OpencodePendingQuestion question)
    {
        var lines = new List<string>
        {
            $"{question.Header}: {question.Question}"
        };

        if (question.Options.Count > 0)
        {
            for (var i = 0; i < question.Options.Count; i++)
            {
                lines.Add($"{i + 1}) {question.Options[i]}");
            }
        }

        lines.Add("Your next message will be used as the answer.");
        return string.Join("\n", lines);
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
        else if (_imConversationConfigs.TryGetValue(conversationKey, out var cfg) && !string.IsNullOrWhiteSpace(cfg.ProviderId))
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

        var config = _imConversationConfigs.GetOrAdd(conversationKey, _ => new ImConversationConfig());
        var normalizedArg = arg.Trim();

        if (normalizedArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _imConversationConfigs.TryRemove(conversationKey, out _);
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
            SendImText(client, agentId, from, $"Stored API key for provider {provider.Name} ({provider.Id}). Run *providers configured then *models {provider.Id}. OpenCode may need to be restarted for the new API key to take effect.");
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
            }

            SendImText(client, agentId, from, completed.ProviderConfigured
                ? $"OAuth completed for {provider.Name} ({provider.Id}). Run *providers configured and *models {provider.Id}. OpenCode may need to be restarted for the new API key to take effect."
                : completed.Message);
            return;
        }

        SendImText(client, agentId, from, $"Unknown auth mode '{verb}'. Use api, oauth, or oauth-complete.");
    }

    private void ApplyAuthenticatedProviderAsConversationDefault(string conversationKey, OpencodeProviderSummary provider)
    {
        if (string.IsNullOrWhiteSpace(conversationKey)
            || string.IsNullOrWhiteSpace(provider.Id))
        {
            return;
        }

        var config = _imConversationConfigs.GetOrAdd(conversationKey, _ => new ImConversationConfig());
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

    private static bool TryParseSimplePermissionResponse(string text, out string response, out bool remember)
    {
        response = string.Empty;
        remember = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        var compact = normalized
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal);
        compact = string.Join(" ", compact.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (compact is "1" or "yes" or "y" or "allow")
        {
            response = "allow";
            return true;
        }

        if (compact is "3" or "yes always" or "always yes" or "yes remember" or "y always" or "allow always")
        {
            response = "allow";
            remember = true;
            return true;
        }

        if (compact is "2" or "no" or "n" or "reject" or "deny")
        {
            response = "reject";
            return true;
        }

        if (compact is "4" or "no always" or "always no" or "no remember" or "n always" or "reject always" or "deny always")
        {
            response = "reject";
            remember = true;
            return true;
        }

        return false;
    }

    private bool IsHandlerRestricted()
    {
        return !string.IsNullOrWhiteSpace(_handlerFullName);
    }

    private bool IsHandlerAvatar(string? avatarName)
    {
        if (string.IsNullOrWhiteSpace(_handlerFullName))
        {
            return false;
        }

        var normalized = NormalizeAvatarName(avatarName);
        return normalized.Equals(_handlerFullName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHandlerFullName(string? firstName, string? lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            return string.Empty;
        }

        return NormalizeAvatarName($"{firstName} {lastName}");
    }

    private static string NormalizeAvatarName(string? avatarName)
    {
        if (string.IsNullOrWhiteSpace(avatarName))
        {
            return string.Empty;
        }

        return string.Join(' ', avatarName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<string> ResolvePinnedModelIdAsync(string requestedModel, string? preferredProviderId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLooseQuery(requestedModel);
        var slash = normalized.IndexOf('/');
        var providerHint = slash > 0 ? normalized[..slash] : null;
        var effectiveProviderFilter = !string.IsNullOrWhiteSpace(providerHint)
            ? providerHint
            : (string.IsNullOrWhiteSpace(preferredProviderId) ? null : NormalizeLooseQuery(preferredProviderId));

        var models = await _opencodeChat!.ListModelsAsync(effectiveProviderFilter, cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                effectiveProviderFilter == null
                    ? "No models are currently reported by Opencode. Try *models."
                    : $"Provider '{effectiveProviderFilter}' returned no models. Try *providers configured and *models {effectiveProviderFilter}.");
        }

        var candidates = models
            .Where(m => ModelIdMatchesRequested(m, normalized, effectiveProviderFilter))
            .ToList();

        if (candidates.Count == 0)
        {
            var suggested = string.Join(", ",
                models.Take(5).Select(m => BuildCanonicalModelId(m.Id, m.Provider, effectiveProviderFilter)));
            var scopeHint = effectiveProviderFilter == null ? string.Empty : $" for provider '{effectiveProviderFilter}'";
            var modelsHint = effectiveProviderFilter == null ? "*models" : $"*models {effectiveProviderFilter}";
            var suggestionHint = string.IsNullOrWhiteSpace(suggested) ? string.Empty : $" Example IDs: {suggested}";
            throw new InvalidOperationException($"Model '{normalized}' is not available{scopeHint}. Try {modelsHint}.{suggestionHint}");
        }

        if (candidates.Count > 1 && effectiveProviderFilter == null && slash < 0)
        {
            var distinctCanonical = candidates
                .Select(m => BuildCanonicalModelId(m.Id, m.Provider, null))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var sample = string.Join(", ", distinctCanonical);
            throw new InvalidOperationException(
                $"Model '{normalized}' is available from multiple providers. Use a fully qualified ID (provider/model), e.g. {sample}");
        }

        var matched = candidates[0];
        return BuildCanonicalModelId(matched.Id, matched.Provider, effectiveProviderFilter);
    }

    private static bool ModelIdMatchesRequested(OpencodeModelSummary model, string requestedModel, string? providerHint)
    {
        var canonical = BuildCanonicalModelId(model.Id, model.Provider, providerHint);
        if (canonical.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)
            || model.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var slash = canonical.IndexOf('/');
        if (slash > 0 && slash < canonical.Length - 1)
        {
            var leaf = canonical[(slash + 1)..];
            return leaf.Equals(requestedModel, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string BuildCanonicalModelId(string modelId, string? providerId, string? providerHint)
    {
        var trimmedModel = modelId.Trim();
        if (trimmedModel.Contains('/'))
        {
            return trimmedModel;
        }

        var provider = !string.IsNullOrWhiteSpace(providerId)
            ? providerId.Trim()
            : providerHint;
        return string.IsNullOrWhiteSpace(provider) ? trimmedModel : $"{provider}/{trimmedModel}";
    }

    private string? GetStartupDefaultModelId()
    {
        var configuredModel = _options.OpencodeInitialModel?.Trim();
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            return null;
        }

        if (configuredModel.Contains('/'))
        {
            return configuredModel;
        }

        var configuredProvider = _options.OpencodeInitialProvider?.Trim();
        return string.IsNullOrWhiteSpace(configuredProvider)
            ? configuredModel
            : $"{configuredProvider}/{configuredModel}";
    }

    private static string? GetStartupDefaultProviderId(string? startupModelId)
    {
        if (string.IsNullOrWhiteSpace(startupModelId))
        {
            return null;
        }

        var slash = startupModelId.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        return startupModelId[..slash];
    }

    private static string SanitizeImLogText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("*auth ", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (trimmed.IndexOf(" api ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "*auth <redacted> api <redacted>";
        }

        if (trimmed.IndexOf(" oauth-complete ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "*auth <redacted> oauth-complete <redacted>";
        }

        return trimmed;
    }

    private static OpencodeProviderSummary? FindProviderByNameOrId(IReadOnlyList<OpencodeProviderSummary> providers, string query)
    {
        var q = query.Trim();
        var exact = providers.FirstOrDefault(p =>
            p.Id.Equals(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        return providers.FirstOrDefault(p =>
            p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static void SendImText(GridClient client, UUID agentId, string from, string responseText)
    {
        foreach (var chunk in SplitForInstantMessage(responseText, 900))
        {
            client.Self.InstantMessage(agentId, chunk);
            Console.WriteLine($"[im] -> {from}: {chunk}");
        }
    }

    private static bool IsLikelyTypingIndicator(InstantMessage message, string text)
    {
        if (!text.Equals("typing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Some viewers emit IM typing state as a pseudo-message with payload metadata.
        return message.BinaryBucket != null && message.BinaryBucket.Length > 0;
    }

    private bool IsDuplicateImEvent(UUID fromAgentId, string text, DateTime timestamp)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedText = text.Trim();
        var timestampKey = timestamp.Ticks > 0 ? timestamp.Ticks.ToString() : "no-ts";
        var key = $"{fromAgentId}:{timestampKey}:{normalizedText}";
        var duplicateWindow = TimeSpan.FromSeconds(8);

        if (_recentImEvents.TryGetValue(key, out var seenAt) && now - seenAt <= duplicateWindow)
        {
            return true;
        }

        _recentImEvents[key] = now;

        // Opportunistic cleanup to avoid unbounded growth for long-running sessions.
        foreach (var entry in _recentImEvents)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(5))
            {
                _recentImEvents.TryRemove(entry.Key, out _);
            }
        }

        return false;
    }

    private bool IsDuplicateDialogBridgeReply(string conversationKey, string requestId, string answer)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{conversationKey}:{requestId}:{answer}";
        var duplicateWindow = TimeSpan.FromSeconds(10);

        if (_recentDialogBridgeReplies.TryGetValue(key, out var seenAt) && now - seenAt <= duplicateWindow)
        {
            return true;
        }

        _recentDialogBridgeReplies[key] = now;

        foreach (var entry in _recentDialogBridgeReplies)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(5))
            {
                _recentDialogBridgeReplies.TryRemove(entry.Key, out _);
            }
        }

        return false;
    }

    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        var client = _client;
        if (client == null || e.SourceID == client.Self.AgentID)
        {
            return;
        }

        var text = e.Message?.Trim() ?? string.Empty;
        if (!text.StartsWith(LslDialogBridgeReplyPrefix + "|", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Console.WriteLine($"[chat] ({e.SourceType}/{e.Type}) {e.FromName}: {SanitizeImLogText(text)}");
        _ = Task.Run(async () =>
        {
            await TryHandleLslDialogBridgeReplyAsync(client, e.SourceID, e.FromName, text).ConfigureAwait(false);
        });

        // TODO(ai-chat): route local chat to Opencode after conversation UX and anti-spam policies are finalized.
        // TODO(ai-chat): add group chat routing once we define session mapping semantics for groups.
    }

    private void OnScriptDialog(object? sender, ScriptDialogEventArgs e)
    {
        var client = _client;
        if (client == null)
        {
            return;
        }

        string? conversationKey;
        UUID targetAgentId;
        string from;
        lock (_recentImSpeakerLock)
        {
            conversationKey = _lastImConversationKey;
            targetAgentId = _lastImSpeakerAgentId;
            from = _lastImSpeakerName ?? "handler";
        }

        if (string.IsNullOrWhiteSpace(conversationKey) || targetAgentId == UUID.Zero)
        {
            Console.WriteLine($"[dialog] received script dialog from '{e.ObjectName}' but no active IM target is known.");
            return;
        }

        var labels = e.ButtonLabels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var dialogId = $"dlg_{Interlocked.Increment(ref _scriptDialogSequence)}";
        var pending = new PendingScriptDialog(
            dialogId,
            e.Message?.Trim() ?? string.Empty,
            e.ObjectName?.Trim() ?? string.Empty,
            e.ObjectID,
            e.Channel,
            labels,
            DateTimeOffset.UtcNow);
        _latestScriptDialogByConversation[conversationKey] = pending;

        SendImText(client, targetAgentId, from, BuildFriendlyScriptDialogPrompt(pending));
        Console.WriteLine($"[dialog] forwarded script dialog from '{pending.ObjectName}' to {from} ({conversationKey}).");
    }

    private static IReadOnlyList<string> SplitForInstantMessage(string message, int maxChunkLength)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new[] { "(No reply text.)" };
        }

        if (message.Length <= maxChunkLength)
        {
            return new[] { message };
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < message.Length)
        {
            var remaining = message.Length - start;
            if (remaining <= maxChunkLength)
            {
                chunks.Add(message[start..]);
                break;
            }

            var span = message.AsSpan(start, maxChunkLength);
            var cut = span.LastIndexOf('\n');
            if (cut <= 0)
            {
                cut = span.LastIndexOf(' ');
            }

            if (cut <= 0)
            {
                cut = maxChunkLength;
            }

            var end = start + cut;
            chunks.Add(message[start..end].Trim());
            start = end;

            while (start < message.Length && char.IsWhiteSpace(message[start]))
            {
                start++;
            }
        }

        return chunks;
    }

    private void OnLoginProgress(object? sender, LoginProgressEventArgs e)
    {
        if (e.Status == LoginStatus.Success)
        {
            Console.WriteLine("[bot] login successful");
        }
        else if (e.Status == LoginStatus.Failed)
        {
            Console.WriteLine($"[bot] login failed: {e.Message}");
        }
    }

    private void OnNetworkSimChanged(object? sender, LibreMetaverse.SimChangedEventArgs e)
    {
        // Fire-and-forget: run the health-check on a background task so we don't block
        // the network event loop.
        _ = Task.Run(async () =>
        {
            try
            {
                var client = _client;
                if (client == null) {
                Console.WriteLine($"[dialog-bridge] OnNetworkSimChanged: no client! autoProvisionEnabled={_options.DialogBridgeAutoProvisionOnRegionEnter}");
                    return;
                }

                // Diagnostic: report auto-provision option and current trusted pin state so we can
                // understand why automatic install may be skipped.
                Console.WriteLine($"[dialog-bridge] OnNetworkSimChanged: autoProvisionEnabled={_options.DialogBridgeAutoProvisionOnRegionEnter}");
                lock (_dialogBridgeTrustLock)
                {
                    Console.WriteLine($"[dialog-bridge] current trusted bridge pin: object={_trustedDialogBridgeObjectId} owner={_trustedDialogBridgeOwnerId}");
                }

                // Wait until the client appears fully initialized before attempting any automatic
                // provisioning. In containerized/docker startup scenarios the GridClient may have
                // connected at the UDP level but higher-level subsystems (inventory store, agent
                // identity, appearance) may still be initializing. Attempt a brief readiness wait
                // (total ~12s) and then allow a short extra delay for simulator object updates to
                // arrive in the local cache.
                var ready = false;
                var readinessDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
                while (DateTime.UtcNow < readinessDeadline)
                {
                    // If the client field was replaced concurrently, prefer the current field.
                    var checkClient = _client ?? client;
                    if (checkClient != null
                        && checkClient.Network.CurrentSim != null
                        && checkClient.Self.AgentID != UUID.Zero
                        && checkClient.Inventory?.Store != null)
                    {
                        ready = true;
                        // ensure the variable used below references the field-backed client
                        client = checkClient;
                        break;
                    }

                    await Task.Delay(500).ConfigureAwait(false);
                }

                if (!ready)
                {
                    Console.WriteLine("[dialog-bridge] OnNetworkSimChanged: client not fully initialized yet; postponing auto-provision until next sim change.");
                    return;
                }

                // Allow some time for the simulator to populate object updates in the client's
                // local cache after we've become ready.
                await Task.Delay(1500).ConfigureAwait(false);

                var sim = client.Network.CurrentSim;
                if (sim == null) return;

                Console.WriteLine($"[dialog-bridge] current sim: name={sim.Name} handle={sim.Handle} primitives={sim.ObjectsPrimitives?.Count ?? 0}");

                // If we already have a pinned bridge object in this sim, nothing to do.
                if (TryGetPinnedBridgeObjectInCurrentSim(out _, out _))
                {
                    Console.WriteLine("[dialog-bridge] pinned bridge object present in new region; no auto-provision needed.");
                    return;
                }

                var cubeItems = await ResolveCubeBotIarItemsAsync(client, CancellationToken.None).ConfigureAwait(false);
                if (cubeItems.Ok && cubeItems.AttachmentItem != null)
                {
                    var appearance = await AppearanceListWornAsync(CancellationToken.None).ConfigureAwait(false);
                    var attached = false;
                    var alphaWorn = false;
                    if (appearance.Ok)
                    {
                        attached = appearance.Attachments.Any(a => string.Equals(a.ItemId, cubeItems.AttachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
                        alphaWorn = cubeItems.AlphaItem != null
                            && appearance.Wearables.Any(w => string.Equals(w.ItemId, cubeItems.AlphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));

                        // Appearance snapshots can briefly lag right after login/sim change.
                        // Re-check a few times before deciding bridge items are missing.
                        for (var verifyAttempt = 1; verifyAttempt <= 3 && (!attached || !alphaWorn); verifyAttempt++)
                        {
                            await Task.Delay(900).ConfigureAwait(false);
                            appearance = await AppearanceListWornAsync(CancellationToken.None).ConfigureAwait(false);
                            if (!appearance.Ok)
                            {
                                break;
                            }

                            attached = appearance.Attachments.Any(a => string.Equals(a.ItemId, cubeItems.AttachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
                            alphaWorn = cubeItems.AlphaItem != null
                                && appearance.Wearables.Any(w => string.Equals(w.ItemId, cubeItems.AlphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
                        }

                        if (attached)
                        {
                            if (TryFindAttachedObjectForInventoryItem(client, cubeItems.AttachmentItem.UUID, out var attachedObjectId, out var attachedLocalId))
                            {
                                lock (_dialogBridgeTrustLock)
                                {
                                    _trustedDialogBridgeObjectId = attachedObjectId;
                                    _trustedDialogBridgeOwnerId = client.Self.AgentID;
                                }
                                TrySaveDialogBridgeTrustStateToFile();
                                Console.WriteLine($"[dialog-bridge] bridge attachment already worn; refreshed trusted pin object={attachedObjectId} localId={attachedLocalId}.");
                            }
                            else
                            {
                                Console.WriteLine("[dialog-bridge] bridge attachment already worn; trusted pin refresh is waiting for simulator cache visibility.");
                            }

                            if (alphaWorn)
                            {
                                return;
                            }

                            Console.WriteLine("[dialog-bridge] bridge attachment is worn, but alpha is missing; continuing to auto-provision for alpha self-heal.");
                        }
                        else
                        {
                            Console.WriteLine("[dialog-bridge] bridge attachment item found but not currently worn.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[dialog-bridge] could not verify worn bridge attachment state: {appearance.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] Cube Bot IAR inventory lookup failed: {cubeItems.Error}");
                }

                if (!_options.DialogBridgeAutoProvisionOnRegionEnter)
                {
                    Console.WriteLine("[dialog-bridge] bridge missing in new region but auto-provision is disabled.");
                    return;
                }

                if (Interlocked.CompareExchange(ref _dialogBridgeAutoProvisionInFlight, 1, 0) != 0)
                {
                    Console.WriteLine("[dialog-bridge] auto-provision already in progress; skipping duplicate trigger.");
                    return;
                }

                try
                {
                    var now = DateTimeOffset.UtcNow;
                    lock (_dialogBridgeAutoProvisionLock)
                    {
                        if ((now - _lastDialogBridgeAutoProvisionAttemptAt) < TimeSpan.FromSeconds(45))
                        {
                            Console.WriteLine("[dialog-bridge] auto-provision suppressed by cooldown.");
                            return;
                        }

                        _lastDialogBridgeAutoProvisionAttemptAt = now;
                    }

                    Console.WriteLine("[dialog-bridge] bridge missing in new region; attempting automatic install...");
                    var install = await DialogBridgeInstallAsync(null, null, null, null, 1f, 0f, 0f, pinAsTrustedSender: true, CancellationToken.None).ConfigureAwait(false);
                    if (install.Ok)
                    {
                        Console.WriteLine($"[dialog-bridge] auto-installed bridge: {install.Message}");
                    }
                    else
                    {
                        Console.WriteLine($"[dialog-bridge] auto-install failed: {install.Message}");
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _dialogBridgeAutoProvisionInFlight, 0);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[dialog-bridge] auto-provision error: {ex.Message}");
            }
        });
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e)
    {
        _connected = false;
        StopFollowInternal();
        CancelMovementAutoStop();
        Console.WriteLine($"[bot] disconnected: {e.Reason} - {e.Message}");
        EnsureReconnectLoop("network-disconnect");
    }

    private void EnsureReconnectLoop(string reason)
    {
        if (_lifecycleCts.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconnectLoopAsync(reason, _lifecycleCts.Token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectLoopActive, 0);
            }
        });
    }

    private async Task ReconnectLoopAsync(string reason, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[bot] reconnect loop started (reason={reason}).");
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_connected && _client != null)
            {
                Console.WriteLine("[bot] reconnect loop exiting: client is connected.");
                return;
            }

            attempt++;
            var backoffSeconds = Math.Min(30, Math.Max(2, attempt * 2));
            Console.WriteLine($"[bot] reconnect attempt {attempt} starting...");

            try
            {
                using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, _options.BotLoginTimeoutSeconds)));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
                var connected = await ConnectAsync(linked.Token).ConfigureAwait(false);
                if (connected)
                {
                    Console.WriteLine("[bot] reconnect successful.");
                    return;
                }

                Console.WriteLine("[bot] reconnect attempt failed (login returned false).");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[bot] reconnect attempt timed out.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bot] reconnect attempt error: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void CleanupClient(GridClient client, bool logout)
    {
        try { client.Self.IM -= OnInstantMessage; } catch { }
        try { client.Self.ChatFromSimulator -= OnChatFromSimulator; } catch { }
        try { client.Self.ScriptDialog -= OnScriptDialog; } catch { }
        try { client.Inventory.InventoryObjectOffered -= OnInventoryObjectOffered; } catch { }
        try { client.Network.Disconnected -= OnDisconnected; } catch { }
        try { client.Network.SimChanged -= OnNetworkSimChanged; } catch { }
        try { client.Network.LoginProgress -= OnLoginProgress; } catch { }

        if (logout)
        {
            try { client.Network.Logout(); } catch { }
        }

        try { client.Dispose(); } catch { }
    }
}

internal sealed record BotStatus(
    bool Connected,
    string Simulator,
    float X,
    float Y,
    float Z,
    string AgentId,
    string LastLoginMessage);

internal sealed record BotToolResult(bool Ok, string Message)
{
    public static BotToolResult OkResult(string message) => new(true, message);
    public static BotToolResult Fail(string message) => new(false, message);
}

internal sealed record EnvironmentToolResult(bool Ok, string Message, string? PayloadJson)
{
    public static EnvironmentToolResult OkResult(string message, string payloadJson) => new(true, message, payloadJson);
    public static EnvironmentToolResult FailResult(string message) => new(false, message, null);
}

internal sealed record PrimCreateResult(bool Ok, string Message, uint LocalId)
{
    public static PrimCreateResult OkResult(uint localId, string message) => new(true, message, localId);
    public static PrimCreateResult FailResult(string message) => new(false, message, 0);
}

internal sealed record PrimFaceTextureInfo(int FaceIndex, string TextureId);

internal sealed record PrimSummary(
    uint LocalId,
    string Uuid,
    uint ParentId,
    string? Name,
    string PrimType,
    float PositionX,
    float PositionY,
    float PositionZ,
    float DistanceMeters);

internal sealed record PrimInfo(
    uint LocalId,
    string Uuid,
    uint ParentId,
    string PrimType,
    string PathCurve,
    string ProfileCurve,
    string Material,
    float PositionX,
    float PositionY,
    float PositionZ,
    float ScaleX,
    float ScaleY,
    float ScaleZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    string? Name,
    string? Description,
    string? OwnerId,
    string? CreatorId,
    string? DefaultTextureId,
    IReadOnlyList<PrimFaceTextureInfo> FaceTextureOverrides,
    PrimShapeDetail Shape,
    PrimPermissionsInfo? Permissions,
    PrimSaleInfo? Sale,
    PrimSitInfo? Sit,
    PrimFlexibleInfo? Flexible,
    PrimLightInfo? Light,
    PrimSculptInfo? Sculpt,
    PrimPropertyFreshnessInfo Freshness);

internal sealed record PrimShapeDetail(
    string PathCurve,
    string ProfileCurve,
    string ProfileHole,
    string Material,
    float PathBegin,
    float PathEnd,
    float PathScaleX,
    float PathScaleY,
    float PathShearX,
    float PathShearY,
    float PathTwist,
    float PathTwistBegin,
    float PathTaperX,
    float PathTaperY,
    float PathRadiusOffset,
    float PathSkew,
    float PathRevolutions,
    float ProfileBegin,
    float ProfileEnd,
    float ProfileHollow);

internal sealed record PrimPermissionsInfo(
    uint BaseMask,
    uint OwnerMask,
    uint GroupMask,
    uint EveryoneMask,
    uint NextOwnerMask);

internal sealed record PrimSaleInfo(string SaleType, int SalePrice);

internal sealed record PrimSitInfo(
    string? SitName,
    string? TouchName,
    bool IsSittable,
    string ClickAction,
    string Detection);

internal sealed record PrimFlexibleInfo(
    int Softness,
    float Tension,
    float Drag,
    float Gravity,
    float Wind,
    float ForceX,
    float ForceY,
    float ForceZ);

internal sealed record PrimLightInfo(
    float Red,
    float Green,
    float Blue,
    float Intensity,
    float Radius,
    float Cutoff,
    float Falloff);

internal sealed record PrimSculptInfo(
    string SculptTextureId,
    string SculptType,
    bool IsMesh,
    bool Invert,
    bool Mirror,
    uint ExtendedMeshFlags);

internal sealed record PrimPropertyFreshnessInfo(
    bool RefreshRequested,
    bool RefreshReceived,
    string? RefreshedAtUtc,
    string Detail);

internal sealed record PrimInspectResult(bool Ok, string Message, PrimInfo? Prim)
{
    public static PrimInspectResult OkResult(PrimInfo prim, string message = "OK") => new(true, message, prim);
    public static PrimInspectResult FailResult(string message) => new(false, message, null);
}

internal sealed record PrimQueryResult(bool Ok, string Message, IReadOnlyList<PrimSummary> Prims)
{
    public static PrimQueryResult OkResult(IReadOnlyList<PrimSummary> prims, string message) => new(true, message, prims);
    public static PrimQueryResult FailResult(string message) => new(false, message, Array.Empty<PrimSummary>());
}

internal sealed record LinksetNodeInfo(
    uint LocalId,
    string Uuid,
    uint ParentId,
    bool IsRoot,
    int Order,
    string? Name,
    string PrimType,
    float PositionX,
    float PositionY,
    float PositionZ,
    float ScaleX,
    float ScaleY,
    float ScaleZ);

internal sealed record LinksetInspectResult(bool Ok, string Message, uint RootLocalId, IReadOnlyList<LinksetNodeInfo> Nodes)
{
    public static LinksetInspectResult OkResult(uint rootLocalId, IReadOnlyList<LinksetNodeInfo> nodes, string message)
        => new(true, message, rootLocalId, nodes);

    public static LinksetInspectResult FailResult(string message)
        => new(false, message, 0, Array.Empty<LinksetNodeInfo>());
}

internal sealed record CameraState(
    float CameraX,
    float CameraY,
    float CameraZ,
    float AtAxisX,
    float AtAxisY,
    float AtAxisZ,
    float LeftAxisX,
    float LeftAxisY,
    float LeftAxisZ,
    float UpAxisX,
    float UpAxisY,
    float UpAxisZ,
    float Far,
    float AgentX,
    float AgentY,
    float AgentZ);

internal sealed record AnimationInfo(string Name, string AnimationId, int? SequenceId = null);

internal sealed record AnimationListResult(
    bool Ok,
    string Message,
    IReadOnlyList<AnimationInfo> Animations)
{
    public static AnimationListResult OkResult(IReadOnlyList<AnimationInfo> animations, string message)
        => new(true, message, animations);

    public static AnimationListResult FailResult(string message)
        => new(false, message, Array.Empty<AnimationInfo>());
}

internal sealed record CameraStateResult(bool Ok, string Message, CameraState? State)
{
    public static CameraStateResult FailResult(string message) => new(false, message, null);
}

internal sealed class ImConversationConfig
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ModelId { get; set; }
    public string? ThinkingLevel { get; set; }
}
