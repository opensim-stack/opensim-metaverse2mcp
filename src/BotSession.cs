using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Assets;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession : IDisposable
{
    private enum PendingPromptKind
    {
        Permission,
        Question
    }

    private enum ConversationChannel
    {
        Im,
        Group,
        Local
    }

    private sealed record ConversationRoute(
        ConversationChannel Channel,
        UUID ReplyTargetId,
        UUID SpeakerAgentId,
        string SpeakerName);

    private async Task NotifyUserOfRetryLimitAsync(OpencodeSessionStatusEvent statusEvent)
    {
        if (!statusEvent.NextRetryAt.HasValue)
        {
            return;
        }

        var delay = statusEvent.NextRetryAt.Value - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.FromMinutes(2))
        {
            return;
        }

        var conversationKey = FindConversationKeyForSessionId(statusEvent.SessionId);
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        if (!_conversationAgentByKey.TryGetValue(conversationKey, out var agentId) || agentId == UUID.Zero)
        {
            return;
        }

        var client = _client;
        if (!_connected || client == null)
        {
            return;
        }

        var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
            ? displayName
            : "handler";

        var attemptText = statusEvent.Attempt.HasValue ? $" (attempt {statusEvent.Attempt.Value})" : string.Empty;
        var message = $"The AI service is rate-limiting this request and will retry around {statusEvent.NextRetryAt.Value:HH:mm:ss UTC}{attemptText}. Send *cancel if you don't want to wait.";

        try
        {
            SendImText(client, agentId, from, message, conversationKey);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[opencode] failed to notify user of retry delay: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private string? FindConversationKeyForSessionId(string sessionId)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        foreach (var pair in _conversationAgentByKey)
        {
            var mappedSessionId = _opencodeChat.GetConversationSessionId(pair.Key);
            if (!string.IsNullOrWhiteSpace(mappedSessionId)
                && mappedSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    private void LogRetryStatusEvent(string sessionId, string? statusMessage)
    {
        var message = string.IsNullOrWhiteSpace(statusMessage)
            ? $"[opencode] session {sessionId} is retrying"
            : $"[opencode] session {sessionId} is retrying: {statusMessage}";
        Console.WriteLine(message);
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
    private readonly SemaphoreSlim _globalConversationGate = new(1, 1);
    private readonly IOpencodeChatClient? _opencodeChat;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentImEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<UUID, DateTimeOffset> _primPropertiesRefreshedAtByObjectId = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConversationConfig> _conversationConfigs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConversationRoute> _conversationRouteByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<UUID, string> _conversationKeyBySpeakerAgent = new();
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
    private readonly AsyncLocal<string?> _ambientConversationKey = new();
    private readonly string _handlerConfigPath;
    private readonly string? _parentFullName;
    private readonly object _promptStateLock = new();
    private readonly object _recentImSpeakerLock = new();
    private readonly object _dialogBridgeTrustLock = new();
    private readonly object _handlerConfigLock = new();
    private readonly object _opencodeSessionStateLock = new();
    private readonly object _typingStateLock = new();
    private readonly object _hoverStateLock = new();
    private readonly object _dialogBridgeAutoProvisionLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly HashSet<ChatType> _receiveChatAllowedTypes;

    private string? _projectAgentsPromptCache;
    private DateTime _projectAgentsPromptCacheLastWriteUtc;
    private string? _builtInPromptOverrideCache;
    private DateTime _builtInPromptOverrideCacheLastWriteUtc;
    private string? _builtInPromptOverrideCachePath;
    private string? _activeAgentsNotecardPrompt;
    private string? _activeAgentsNotecardSourceName;
    private string? _activeAgentsNotecardItemId;
    private DateTimeOffset? _activeAgentsNotecardInstalledAt;
    private string? _bridgeAgentsPrompt;
    private string? _bridgeAgentsPromptSourceName;
    private string? _bridgeAgentsPromptItemId;
    private UUID _bridgeAgentsPromptObjectId = UUID.Zero;
    private DateTimeOffset? _bridgeAgentsPromptInstalledAt;
    private UUID _bridgeAgentsProbeObjectId = UUID.Zero;
    private bool _bridgeAgentsProbeInFlight;
    private UUID _lastImSpeakerAgentId = UUID.Zero;
    private string? _lastImSpeakerName;
    private string? _lastImConversationKey;
    private long _scriptDialogSequence;
    private UUID _trustedDialogBridgeObjectId = UUID.Zero;
    private UUID _trustedDialogBridgeOwnerId = UUID.Zero;
    private bool _lslDialogBridgeRequireTrustedSender = true;
    private readonly ConcurrentDictionary<string, byte> _busyOpencodeSessions = new(StringComparer.OrdinalIgnoreCase);
    private string? _restoredOpencodeSessionId;
    private HashSet<string> _handlerNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _handlerConfigCacheInitialized;
    private DateTime _handlerConfigLastWriteUtc = DateTime.MinValue;
    private string? _handlerConfigLastError;
    private ConversationConfig? _persistedOpencodeDefaultConfig;
    private DateTimeOffset _lastTypingPulseAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _typingStopCts;
    private bool _typingIndicatorActive;
    private DateTimeOffset _lastHoverBusyUpdateAt = DateTimeOffset.MinValue;
    private int _busyHoverDots;
    private int _dialogBridgeAutoProvisionInFlight;
    private DateTimeOffset _lastDialogBridgeAutoProvisionAttemptAt = DateTimeOffset.MinValue;
    private const int LslDialogBridgeRequestChannel = -919191;
    private const string LocalChatConversationKey = "local-chat";
    private const string LslDialogBridgeRequestPrefix = "dlgreq";
    private const string LslDialogBridgeTextRequestPrefix = "txtreq";
    private const string LslDialogBridgeAckPrefix = "dlgack";
    private const string LslDialogBridgePingPrefix = "brping";
    private const string LslDialogBridgePongPrefix = "brpong";
    private const string LslDialogBridgeReplyPrefix = "dlgrep";
    private const string LslDialogBridgePermissionRequestPrefix = "perm:";
    private const string LslDialogBridgeMoodRequestPrefix = "moodreq";
    // OpenSimulator tolerates larger chat payloads than strict SL-era assumptions.
    // Keep this conservative enough to avoid most truncation while preserving prompt fidelity.
    private const int LslDialogBridgeMaxPayloadLength = 900;
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
    private const int LslDialogBridgeEmoterChannel = -919192;

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
        "- When requester identity metadata is provided for IM, resolve pronouns like 'me', 'my', and 'here' to that requester unless they explicitly override it.\n" +
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
        _receiveChatAllowedTypes = ParseLocalChatAllowedTypes(_options.ReceiveChatAllowedTypes, out var invalidLocalChatTypeNames);
        _controlGroupName = BuildControlGroupName();
        InitializeVoiceSupport();
        _handlerConfigPath = string.IsNullOrWhiteSpace(_options.HandlerConfig)
            ? "/config/handlers.json"
            : _options.HandlerConfig.Trim();
        _parentFullName = NormalizeAvatarName(_options.BotSpawnerParent);
        _opencodeChat = new OpencodeChatClient(_options);
        _opencodeChat.SessionStatusChanged += OnOpencodeSessionStatusChanged;
        _opencodeChat.MessagePartUpdated += OnOpencodeMessagePartUpdated;
        var startupModel = GetStartupDefaultModelId();
        if (!string.IsNullOrWhiteSpace(startupModel))
        {
            Console.WriteLine($"[opencode] startup default model configured (runtime-overridable): {startupModel}");
        }

        var configuredHandlers = GetConfiguredHandlerNamesOnStartup();
        if (configuredHandlers.Count > 0)
        {
            Console.WriteLine($"[bot] handler restriction enabled from '{_handlerConfigPath}': {string.Join(", ", configuredHandlers.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}");
        }
        else
        {
            var exists = File.Exists(_handlerConfigPath);
            Console.WriteLine($"[handler-config] no handlers loaded from '{_handlerConfigPath}' (exists={exists}). Strict schema expects an array of objects with handlerFirst and handlerLast.");
        }

        if (!string.IsNullOrWhiteSpace(_parentFullName))
        {
            Console.WriteLine($"[bot] parent controller enabled: {_parentFullName}");
        }

        if (invalidLocalChatTypeNames.Count > 0)
        {
            Console.WriteLine($"[chat] ignoring invalid LOCAL_CHAT_ALLOWED_TYPES entries: {string.Join(", ", invalidLocalChatTypeNames)}");
        }

        Console.WriteLine($"[chat] receive chat-type filter (local/group): {string.Join(", ", _receiveChatAllowedTypes.OrderBy(value => value.ToString(), StringComparer.OrdinalIgnoreCase))}");
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
            client.Objects.ObjectUpdate += OnWorldObjectUpdateForEventStream;

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
                EmitRuntimeEvent(
                    "general",
                    "login.failed",
                    "opensim",
                    string.IsNullOrWhiteSpace(_lastLoginMessage) ? "Login failed." : _lastLoginMessage,
                    new Dictionary<string, string?>
                    {
                        ["firstName"] = _options.BotFirstName,
                        ["lastName"] = _options.BotLastName
                    });
                CleanupClient(client, logout: true);
                // Clear the shared client field since login failed.
                _client = null;
                return false;
            }

            // client already assigned to _client above; mark connected.
            _connected = true;
            EmitRuntimeEvent(
                "general",
                "login.connected",
                "opensim",
                "Bot login successful.",
                new Dictionary<string, string?>
                {
                    ["agentId"] = client.Self.AgentID.ToString(),
                    ["simulator"] = client.Network.CurrentSim?.Name,
                    ["firstName"] = _options.BotFirstName,
                    ["lastName"] = _options.BotLastName
                });
            await EnsureVoiceBackendOnLoginAsync(client, cancellationToken).ConfigureAwait(false);

            // Load persisted trust pins after login so {bot_uuid} path templates resolve per avatar.
            TryLoadDialogBridgeTrustStateFromFile();
            TryLoadOpencodeSessionStateFromFile();

            await TryLoadInventoryOfferPoliciesFromConfiguredFileAsync(cancellationToken).ConfigureAwait(false);
            StartControlGroupBootstrap(client);
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

    private static bool TryResolveChatType(string? input, out ChatType chatType, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            chatType = ChatType.Normal;
            return true;
        }

        var trimmed = input.Trim();
        if (!Enum.TryParse(trimmed, ignoreCase: true, out chatType) || !Enum.IsDefined(chatType))
        {
            error = $"chatType '{trimmed}' is invalid. Allowed values: {string.Join(", ", Enum.GetNames<ChatType>())}.";
            chatType = ChatType.Normal;
            return false;
        }

        return true;
    }

    private static HashSet<ChatType> ParseLocalChatAllowedTypes(string? raw, out List<string> invalidNames)
    {
        invalidNames = new List<string>();
        var allowed = new HashSet<ChatType>();

        var input = string.IsNullOrWhiteSpace(raw) ? "Normal" : raw;
        var tokens = input
            .Split(new[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (Enum.TryParse(token, ignoreCase: true, out ChatType chatType) && Enum.IsDefined(chatType))
            {
                allowed.Add(chatType);
            }
            else
            {
                invalidNames.Add(token);
            }
        }

        if (allowed.Count == 0)
        {
            allowed.Add(ChatType.Normal);
        }

        return allowed;
    }

    public async Task<BotToolResult> SayChatAsync(string message, int channel, string? chatType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        if (!TryResolveChatType(chatType, out var resolvedChatType, out var chatTypeError))
        {
            return BotToolResult.Fail(chatTypeError);
        }

        return await RunActionAsync(
            $"Sent {resolvedChatType} chat message on channel {channel}.",
            c => c.Self.Chat(message, channel, resolvedChatType),
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

    public async Task<BotToolResult> SetBotMoodAsync(string emotion, CancellationToken cancellationToken)
    {
        var normalizedEmotion = NormalizeMoodName(emotion);
        if (string.IsNullOrWhiteSpace(normalizedEmotion))
        {
            return BotToolResult.Fail("emotion is required and must contain letters, numbers, '-' or '_'.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            UUID targetBridgeObjectId;
            lock (_dialogBridgeTrustLock)
            {
                targetBridgeObjectId = _trustedDialogBridgeObjectId;
            }

            if (targetBridgeObjectId == UUID.Zero)
            {
                return Task.FromResult(BotToolResult.Fail("No trusted dialog bridge object is pinned yet. Establish bridge communication first (for example via a dialog reply)."));
            }

            // Leave target object token empty so the currently running bridge script
            // in the attachment processes the mood request even if persisted UUID pins are stale.
            var payload = string.Join("|", new[]
            {
                LslDialogBridgeMoodRequestPrefix,
                EncodeDialogToken(string.Empty),
                EncodeDialogToken(normalizedEmotion)
            });

            client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
            Console.WriteLine($"[dialog-bridge] sent mood request: object={targetBridgeObjectId} emotion={normalizedEmotion}");
            return Task.FromResult(BotToolResult.OkResult($"Requested bot mood '{normalizedEmotion}' via dialog bridge request channel {LslDialogBridgeRequestChannel}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> BotMoodListAsync(bool includeUtilityTextures, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            UUID targetBridgeObjectId;
            lock (_dialogBridgeTrustLock)
            {
                targetBridgeObjectId = _trustedDialogBridgeObjectId;
            }

            if (targetBridgeObjectId == UUID.Zero)
            {
                return DataToolResult.FailResult("No trusted dialog bridge object is pinned yet. Establish bridge communication first (for example via a dialog reply).");
            }

            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            Primitive? bridgePrim = null;
            foreach (var prim in sim.ObjectsPrimitives.Values)
            {
                if (prim.ID == targetBridgeObjectId)
                {
                    bridgePrim = prim;
                    break;
                }
            }

            if (bridgePrim == null)
            {
                return DataToolResult.FailResult($"Pinned dialog bridge object {targetBridgeObjectId} is not present in current simulator cache.");
            }

            var entries = await client.Inventory
                .GetTaskInventoryAsync(targetBridgeObjectId, bridgePrim.LocalID, sim, token)
                .ConfigureAwait(false);

            var textureNames = entries
                .OfType<InventoryItem>()
                .Where(item => item.AssetType == AssetType.Texture)
                .Select(item => item.Name?.Trim() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (textureNames.Count == 0)
            {
                return DataToolResult.FailResult($"No texture assets were found in bridge object {targetBridgeObjectId} task inventory.");
            }

            var utilityNames = new[] { "base", "cross" };
            var utilitySet = new HashSet<string>(utilityNames, StringComparer.OrdinalIgnoreCase);
            var moodNames = textureNames
                .Where(name => includeUtilityTextures || !utilitySet.Contains(name))
                .ToList();

            var payload = JsonSerializer.Serialize(new
            {
                bridgeObjectId = targetBridgeObjectId.ToString(),
                includeUtilityTextures,
                textureCount = textureNames.Count,
                moodCount = moodNames.Count,
                utilityTextures = utilityNames,
                moodNames,
                allTextureNames = textureNames
            });

            return DataToolResult.OkResult($"Found {moodNames.Count} mood texture name(s) on bridge object {targetBridgeObjectId}.", payload);
        }, cancellationToken).ConfigureAwait(false);
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
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    message,
                    new Dictionary<string, string?>
                    {
                        ["targetRegion"] = destinationLabel,
                        ["targetPosition"] = FormatVector(target)
                    });
                return BotToolResult.Fail(message);
            }

            var at = client.Self.SimPosition;
            EmitRuntimeEvent(
                "teleport",
                "teleport.succeeded",
                "opensim",
                $"Teleported to {destinationLabel} at {FormatVector(at)}.",
                new Dictionary<string, string?>
                {
                    ["targetRegion"] = destinationLabel,
                    ["position"] = FormatVector(at)
                });
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
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    message,
                    new Dictionary<string, string?>
                    {
                        ["targetRegionHandle"] = handle.ToString(),
                        ["targetPosition"] = FormatVector(target)
                    });
                return BotToolResult.Fail(message);
            }

            EmitRuntimeEvent(
                "teleport",
                "teleport.succeeded",
                "opensim",
                $"Teleported to region handle {handle} at {FormatVector(client.Self.SimPosition)}.",
                new Dictionary<string, string?>
                {
                    ["targetRegionHandle"] = handle.ToString(),
                    ["position"] = FormatVector(client.Self.SimPosition)
                });
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
        DisposeVoiceSupport();
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

        foreach (var gate in _conversationLocks.Values)
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

    private async Task<AttachmentObjectResolutionResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AttachmentObjectResolutionResult>> action,
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
            return AttachmentObjectResolutionResult.FailResult(ex.Message);
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

    private bool TryConsumeWakeWordPrefix(string text, bool allowShortBotWakeWord, out string remainder)
    {
        remainder = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var botFirst = (_options.BotFirstName ?? string.Empty).Trim();
        var botLast = (_options.BotLastName ?? string.Empty).Trim();
        if (botFirst.Length > 0 && botLast.Length > 0)
        {
            var fullWakeWord = $"@{botFirst} {botLast}";
            if (TryConsumeWakeWordVariant(text, fullWakeWord, out remainder))
            {
                return true;
            }

            // Some viewers autocomplete mentions as a bare name without '@'.
            var bareFullWakeWord = $"{botFirst} {botLast}";
            if (TryConsumeWakeWordVariant(text, bareFullWakeWord, out remainder))
            {
                return true;
            }
        }

        if (allowShortBotWakeWord && TryConsumeWakeWordVariant(text, "@Bot", out remainder))
        {
            return true;
        }

        return false;
    }

    private static bool TryConsumeWakeWordVariant(string text, string wakeWord, out string remainder)
    {
        remainder = string.Empty;
        if (!TryConsumePrefixWithFlexibleWhitespace(text, wakeWord, out var consumedLength))
        {
            return false;
        }

        var rest = text[consumedLength..];
        if (rest.Length > 0)
        {
            var next = rest[0];
            if (!char.IsWhiteSpace(next) && next != ':' && next != ',' && next != '-' && next != '.' && next != '!')
            {
                return false;
            }
        }

        remainder = rest.TrimStart(' ', '\t', '\r', '\n', '\u00A0', ':', ',', '-', '.', '!').Trim();
        return true;
    }

    private static bool TryConsumePrefixWithFlexibleWhitespace(string text, string prefix, out int consumedLength)
    {
        consumedLength = 0;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        var textIndex = 0;
        var prefixIndex = 0;

        while (prefixIndex < prefix.Length)
        {
            if (textIndex >= text.Length)
            {
                return false;
            }

            if (char.IsWhiteSpace(prefix[prefixIndex]))
            {
                if (!char.IsWhiteSpace(text[textIndex]))
                {
                    return false;
                }

                while (prefixIndex < prefix.Length && char.IsWhiteSpace(prefix[prefixIndex]))
                {
                    prefixIndex++;
                }

                while (textIndex < text.Length && char.IsWhiteSpace(text[textIndex]))
                {
                    textIndex++;
                }

                continue;
            }

            if (char.ToUpperInvariant(text[textIndex]) != char.ToUpperInvariant(prefix[prefixIndex]))
            {
                return false;
            }

            textIndex++;
            prefixIndex++;
        }

        consumedLength = textIndex;
        return true;
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
            _ = Task.Run(() => NotifyUserOfRetryLimitAsync(statusEvent));
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

    private static string NormalizeMoodName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        if (chars.Length == 0)
        {
            return string.Empty;
        }

        var normalized = new string(chars).ToLowerInvariant();
        return normalized.Length <= 48 ? normalized : normalized[..48];
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

    private void SendImText(GridClient client, UUID agentId, string from, string responseText, string? conversationKey = null)
    {
        conversationKey ??= _ambientConversationKey.Value;
        conversationKey ??= ResolveConversationKeyForSpeaker(agentId);
        _conversationRouteByKey.TryGetValue(conversationKey ?? string.Empty, out var route);
        foreach (var chunk in SplitForInstantMessage(responseText, 900))
        {
            try
            {
                if (route != null && route.Channel == ConversationChannel.Group && route.ReplyTargetId != UUID.Zero)
                {
                    client.Self.InstantMessageGroup(route.ReplyTargetId, chunk);
                    Console.WriteLine($"[group] -> {from}: {chunk}");
                    continue;
                }

                if (route != null && route.Channel == ConversationChannel.Local)
                {
                    client.Self.Chat(chunk, 0, ChatType.Normal);
                    Console.WriteLine($"[local] -> {from}: {chunk}");
                    continue;
                }

                var targetId = route?.ReplyTargetId ?? agentId;
                if (targetId == UUID.Zero)
                {
                    targetId = agentId;
                }

                if (targetId != UUID.Zero)
                {
                    client.Self.InstantMessage(targetId, chunk);
                    Console.WriteLine($"[im] -> {from}: {chunk}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[chat] failed to send reply for conversation '{conversationKey ?? "(none)"}': {ex.Message}");
            }
        }
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

        SendImText(client, targetAgentId, from, BuildFriendlyScriptDialogPrompt(pending), conversationKey);
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
            EmitRuntimeEvent(
                "general",
                "login.success",
                "opensim",
                "Login progress reported success.",
                new Dictionary<string, string?>
                {
                    ["status"] = e.Status.ToString(),
                    ["message"] = e.Message
                });
        }
        else if (e.Status == LoginStatus.Failed)
        {
            Console.WriteLine($"[bot] login failed: {e.Message}");
            EmitRuntimeEvent(
                "general",
                "login.failed",
                "opensim",
                string.IsNullOrWhiteSpace(e.Message) ? "Login progress reported failure." : e.Message,
                new Dictionary<string, string?>
                {
                    ["status"] = e.Status.ToString(),
                    ["message"] = e.Message
                });
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

                // If we already have a pinned bridge object in this sim, probe its AGENTS.md now
                // so prompt status reflects bridge-source state even before any dialog reply arrives.
                if (TryGetPinnedBridgeObjectInCurrentSim(out var pinnedBridgeObjectId, out _))
                {
                    QueueBridgeAgentsPromptProbe(pinnedBridgeObjectId, "trusted bridge object");
                    Console.WriteLine("[dialog-bridge] pinned bridge object present in new region; no auto-provision needed.");
                    return;
                }

                var botItems = await ResolveSetupProvisioningItemsAsync(client, _options.WearFolderName, CancellationToken.None).ConfigureAwait(false);
                if (botItems.Ok)
                {
                    var appearance = await AppearanceListWornAsync(CancellationToken.None).ConfigureAwait(false);
                    var allAttachmentsWorn = false;
                    var allWearablesWorn = false;
                    if (appearance.Ok)
                    {
                        var wornAttachmentIds = appearance.Attachments
                            .Select(a => a.ItemId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            
                            /*
                        var wornWearableIds = appearance.Wearables
                            .Select(w => w.ItemId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                             */

                        allAttachmentsWorn = botItems.AttachmentItems.All(item => wornAttachmentIds.Contains(item.UUID.ToString()));
                        //allWearablesWorn = botItems.WearableItems.All(item => IsWearableItemPresent(item, wornWearableIds));
                        //var provisionedStateSatisfied = allAttachmentsWorn && allWearablesWorn;
                        var provisionedStateSatisfied = allAttachmentsWorn;
                        //if (!allWearablesWorn)
                        //{
                          //  LogWearableProvisioningMatches("sim-change initial verification", botItems.WearableItems, wornWearableIds);
                        //}

                        // Appearance snapshots can briefly lag right after login/sim change.
                        // Re-check a few times before deciding setup items are missing.
                        
                        /*
                        for (var verifyAttempt = 1; verifyAttempt <= 3 && !provisionedStateSatisfied; verifyAttempt++)
                        {
                            await Task.Delay(5000).ConfigureAwait(false);
                            appearance = await AppearanceListWornAsync(CancellationToken.None).ConfigureAwait(false);
                            if (!appearance.Ok)
                            {
                                break;
                            }

                            wornAttachmentIds = appearance.Attachments
                                .Select(a => a.ItemId)
                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            wornWearableIds = appearance.Wearables
                                .Select(w => w.ItemId)
                                .Where(id => !string.IsNullOrWhiteSpace(id))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

                            allAttachmentsWorn = botItems.AttachmentItems.All(item => wornAttachmentIds.Contains(item.UUID.ToString()));
                            allWearablesWorn = botItems.WearableItems.All(item => IsWearableItemPresent(item, wornWearableIds));
                            provisionedStateSatisfied = allAttachmentsWorn && allWearablesWorn;
                            if (!allWearablesWorn)
                            {
                                LogWearableProvisioningMatches($"sim-change verification attempt {verifyAttempt}/3", botItems.WearableItems, wornWearableIds);
                            }
                        }
                         */

                        if (provisionedStateSatisfied)
                        {
                            InventoryItem? anyPinnedAttachment = null;
                            UUID attachedObjectId = UUID.Zero;
                            uint attachedLocalId = 0;
                            foreach (var attachmentItem in botItems.AttachmentItems)
                            {
                                if (TryFindAttachedObjectForInventoryItem(client, attachmentItem.UUID, out attachedObjectId, out attachedLocalId))
                                {
                                    anyPinnedAttachment = attachmentItem;
                                    break;
                                }
                            }

                            if (attachedObjectId != UUID.Zero)
                            {
                                lock (_dialogBridgeTrustLock)
                                {
                                    _trustedDialogBridgeObjectId = attachedObjectId;
                                    _trustedDialogBridgeOwnerId = client.Self.AgentID;
                                }
                                TrySaveDialogBridgeTrustStateToFile();
                                QueueBridgeAgentsPromptProbe(attachedObjectId, "worn setup attachment");
                                Console.WriteLine($"[dialog-bridge] setup attachment already worn; refreshed trusted pin from attachment '{anyPinnedAttachment?.Name}' object={attachedObjectId} localId={attachedLocalId}.");
                            }
                            else
                            {
                                Console.WriteLine("[dialog-bridge] setup attachment already worn; trusted pin refresh is waiting for simulator cache visibility.");
                            }

                            if (!allWearablesWorn)
                            {
                                Console.WriteLine("[dialog-bridge] provisioning attachment is already worn; wearable verification still reports pending items.");
                            }

                            return;
                        }

                        Console.WriteLine("[dialog-bridge] setup inventory was found but not all setup wearables/attachments are currently worn.");
                    }
                    else
                    {
                        Console.WriteLine($"[dialog-bridge] could not verify current setup worn state: {appearance.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] setup inventory lookup failed: {botItems.Error}");
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
                    var install = await DialogBridgeInstallAsync(CancellationToken.None).ConfigureAwait(false);
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
        HandleVoiceDisconnected();
        StopFollowInternal();
        CancelMovementAutoStop();
        EmitRuntimeEvent(
            "general",
            "network.disconnected",
            "opensim",
            $"Disconnected: {e.Reason} - {e.Message}",
            new Dictionary<string, string?>
            {
                ["reason"] = e.Reason.ToString(),
                ["message"] = e.Message
            });
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
        try { client.Objects.ObjectUpdate -= OnWorldObjectUpdateForEventStream; } catch { }
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

internal sealed class ConversationConfig
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ModelId { get; set; }
    public string? ThinkingLevel { get; set; }
}
