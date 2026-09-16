using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Assets;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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

    private async Task NotifyUserOfRetryLimitAsync(HarnessSessionStatusEvent statusEvent)
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
            Console.WriteLine($"[harness] failed to notify user of retry delay: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private string? FindConversationKeyForSessionId(string sessionId)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        foreach (var pair in _conversationAgentByKey)
        {
            var mappedSessionId = _harnessClient.GetConversationSessionId(pair.Key);
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
            ? $"[harness] session {sessionId} is retrying"
            : $"[harness] session {sessionId} is retrying: {statusMessage}";
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
        HarnessPendingPermission? Permission,
        HarnessPendingQuestion? Question,
        CancellationTokenSource TimeoutCts);

    private sealed record PendingTextPromptReply(
        PendingPromptKind Kind,
        string SessionId,
        string RequestId,
        UUID AgentId,
        string From,
        HarnessPendingPermission? Permission,
        HarnessPendingQuestion? Question,
        DateTimeOffset ActivatedAt);

    private sealed record PendingPromptQueueEntry(
        PendingPromptKind Kind,
        string SessionId,
        string RequestId,
        HarnessPendingPermission? Permission,
        HarnessPendingQuestion? Question);

    private sealed class PendingPromptQueueState
    {
        public readonly object SyncRoot = new();
        public readonly Queue<PendingPromptQueueEntry> Queue = new();
        public readonly HashSet<string> EnqueuedRequestIds = new(StringComparer.OrdinalIgnoreCase);
        public string? ActiveRequestId;
    }

    private readonly record struct RequesterImLocationHint(
        UUID RequesterAgentId,
        UUID RegionId,
        Vector3 Position,
        DateTimeOffset ObservedAt);

    private readonly AppOptions _options;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly SemaphoreSlim _globalConversationGate = new(1, 1);
    private readonly IHarnessClient? _harnessClient;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentImEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConversationConfig> _conversationConfigs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConversationRoute> _conversationRouteByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<UUID, string> _conversationKeyBySpeakerAgent = new();
    private readonly ConcurrentDictionary<string, HarnessUsageSummary> _latestUsageByConversation = new(StringComparer.Ordinal);
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingPromptLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPromptQueueState> _pendingPromptQueuesByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RequesterImLocationHint> _requesterImLocationHintByConversation = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _ambientConversationKey = new();
    private readonly string _handlerConfigPath;
    private readonly string? _parentFullName;
    private readonly object _promptStateLock = new();
    private readonly object _recentImSpeakerLock = new();
    private readonly object _dialogBridgeTrustLock = new();
    private readonly object _handlerConfigLock = new();
    private readonly object _typingStateLock = new();
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
    private readonly ConcurrentDictionary<string, byte> __busyHarnessSessions = new(StringComparer.OrdinalIgnoreCase);
    private string? _restoredHarnessSessionId;
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
    private const string LocalChatConversationKey = "local-chat";
    private const int TypingPulseMinimumIntervalMs = 2000;
    private const int TypingStopDelayMs = 2500;
    private static readonly IReadOnlyList<string> LslPermissionDialogOptions = new[] { "yes", "no", "yes always", "no always" };

    private GridClient? _client;
    private bool _connected;
    private string _lastLoginMessage = string.Empty;
    private int _reconnectLoopActive;

    public BotSession(AppOptions options)
    {
        _options = options;
        _followSpawnerClient = new SpawnerClient(options);
        _receiveChatAllowedTypes = ParseLocalChatAllowedTypes(_options.ReceiveChatAllowedTypes, out var invalidLocalChatTypeNames);
        _controlGroupName = BuildControlGroupName();
        InitializeVoiceSupport();
        _handlerConfigPath = string.IsNullOrWhiteSpace(_options.HandlerConfig)
            ? "/config/handlers.json"
            : _options.HandlerConfig.Trim();
        _parentFullName = NormalizeAvatarName(_options.BotSpawnerParent);
        _harnessClient = new OpencodeChatClient(_options);
        _harnessClient.SessionStatusChanged += OnHarnessSessionStatusChanged;
        _harnessClient.MessagePartUpdated += OnHarnessMessagePartUpdated;
        _harnessClient.PendingPromptStateChanged += OnHarnessPendingPromptStateChanged;
        var startupModel = GetStartupDefaultModelId();
        if (!string.IsNullOrWhiteSpace(startupModel))
        {
            Console.WriteLine($"[harness] startup default model configured (runtime-overridable): {startupModel}");
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


    private static string DescribeSimulator(Simulator? sim)
        => sim == null ? "(null)" : $"{sim.Name} ({sim.Handle})";
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
        if (_harnessClient != null)
        {
            _harnessClient.SessionStatusChanged -= OnHarnessSessionStatusChanged;
            _harnessClient.MessagePartUpdated -= OnHarnessMessagePartUpdated;
            _harnessClient.PendingPromptStateChanged -= OnHarnessPendingPromptStateChanged;
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
        __busyHarnessSessions.Clear();
        ClearBusyHoverText();
        DisposeVoiceSupport();
        _client = null;
        _connected = false;
        StopFollowInternal();
        CancelMovementAutoStop();
        _followSpawnerClient.Dispose();

        if (client == null)
        {
            _connectGate.Dispose();
            _lifecycleCts.Dispose();
            if (_harnessClient is IDisposable disposableWhenNoClient)
            {
                disposableWhenNoClient.Dispose();
            }
            return;
        }

        CleanupClient(client, logout: true);
        _connectGate.Dispose();
        _lifecycleCts.Dispose();
        if (_harnessClient is IDisposable dsp)
        {
            dsp.Dispose();
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

    private static Vector3 ClampScale(Vector3 scale)
    {
        return new Vector3(
            Math.Clamp(scale.X, 0.01f, 64f),
            Math.Clamp(scale.Y, 0.01f, 64f),
            Math.Clamp(scale.Z, 0.01f, 64f));
    }

    private static string FormatVector(Vector3 pos)
    {
        return $"<{pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}>";
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

    private void OnHarnessSessionStatusChanged(HarnessSessionStatusEvent statusEvent)
    {
        if (statusEvent == null || string.IsNullOrWhiteSpace(statusEvent.SessionId))
        {
            return;
        }

        var normalizedStatus = statusEvent.StatusType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedStatus == "busy")
        {
            __busyHarnessSessions[statusEvent.SessionId] = 1;
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
            MarkHarnessSessionIdle(statusEvent.SessionId);
        }
    }

    private void OnHarnessMessagePartUpdated(HarnessMessagePartUpdatedEvent partEvent)
    {
        if (partEvent == null || string.IsNullOrWhiteSpace(partEvent.SessionId))
        {
            return;
        }

        PulseTypingIndicator(partEvent.SessionId);
    }

    private void OnHarnessPendingPromptStateChanged(HarnessPendingPromptStateEvent promptStateEvent)
    {
        if (promptStateEvent == null || string.IsNullOrWhiteSpace(promptStateEvent.SessionId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await HandlePendingPromptStateChangedAsync(promptStateEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[opencode:event] pending prompt callback error: {ex.Message}");
            }
        });
    }

    private bool HasActivePromptForConversation(string conversationKey)
        => _pendingDialogPromptWaitByConversation.ContainsKey(conversationKey)
            || _pendingTextPromptReplyByConversation.ContainsKey(conversationKey);

    private PendingPromptQueueState GetPendingPromptQueueState(string conversationKey)
        => _pendingPromptQueuesByConversation.GetOrAdd(conversationKey, _ => new PendingPromptQueueState());

    private void EnqueuePendingPromptEntries(string conversationKey, IEnumerable<PendingPromptQueueEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.RequestId)
                    || (!string.IsNullOrWhiteSpace(state.ActiveRequestId)
                        && entry.RequestId.Equals(state.ActiveRequestId, StringComparison.OrdinalIgnoreCase))
                    || state.EnqueuedRequestIds.Contains(entry.RequestId))
                {
                    continue;
                }

                state.Queue.Enqueue(entry);
                state.EnqueuedRequestIds.Add(entry.RequestId);
            }
        }
    }

    private bool TryDequeueNextPendingPromptEntry(string conversationKey, out PendingPromptQueueEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return false;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            while (state.Queue.Count > 0)
            {
                var candidate = state.Queue.Dequeue();
                state.EnqueuedRequestIds.Remove(candidate.RequestId);
                if (!string.IsNullOrWhiteSpace(state.ActiveRequestId)
                    && candidate.RequestId.Equals(state.ActiveRequestId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                state.ActiveRequestId = candidate.RequestId;
                entry = candidate;
                return true;
            }
        }

        return false;
    }

    private void MarkPendingPromptActive(string conversationKey, string requestId)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            state.ActiveRequestId = requestId.Trim();
            state.EnqueuedRequestIds.Remove(state.ActiveRequestId);
        }
    }

    private void ClearPendingPromptActive(string conversationKey, string requestId)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(state.ActiveRequestId)
                && state.ActiveRequestId.Equals(requestId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                state.ActiveRequestId = null;
            }
        }
    }

    private async Task SeedPendingPromptQueueFromSnapshotAsync(string conversationKey, string sessionId)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            if (state.Queue.Count > 0 || !string.IsNullOrWhiteSpace(state.ActiveRequestId))
            {
                return;
            }
        }

        var permissions = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var questions = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (permissions.Count == 0 && questions.Count == 0)
        {
            return;
        }

        var entries = new List<PendingPromptQueueEntry>(permissions.Count + questions.Count);
        foreach (var permission in permissions)
        {
            if (!string.IsNullOrWhiteSpace(permission.Id))
            {
                entries.Add(new PendingPromptQueueEntry(PendingPromptKind.Permission, string.IsNullOrWhiteSpace(permission.SessionId) ? sessionId : permission.SessionId, permission.Id, permission, null));
            }
        }

        foreach (var question in questions)
        {
            if (!string.IsNullOrWhiteSpace(question.Id))
            {
                entries.Add(new PendingPromptQueueEntry(PendingPromptKind.Question, string.IsNullOrWhiteSpace(question.SessionId) ? sessionId : question.SessionId, question.Id, null, question));
            }
        }

        if (entries.Count > 0)
        {
            EnqueuePendingPromptEntries(conversationKey, entries);
        }
    }

    private void ScheduleDrainPendingPrompts(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DrainPendingPromptsAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[opencode:event] pending prompt drain error: {ex.Message}");
            }
        });
    }

    private async Task DrainPendingPromptsAsync(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(conversationKey) || HasActivePromptForConversation(conversationKey))
        {
            return;
        }

        var promptGate = _pendingPromptLocks.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
        await promptGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_harnessClient == null || string.IsNullOrWhiteSpace(conversationKey) || HasActivePromptForConversation(conversationKey))
            {
                return;
            }

            var sessionId = _harnessClient.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                await SeedPendingPromptQueueFromSnapshotAsync(conversationKey, sessionId).ConfigureAwait(false);
            }

            if (TryDequeueNextPendingPromptEntry(conversationKey, out var nextEntry)
                && nextEntry != null)
            {
                sessionId ??= nextEntry.SessionId;
                if (nextEntry.Kind == PendingPromptKind.Permission)
                {
                    if (nextEntry.Permission != null)
                    {
                        await OfferPermissionPromptWithFallbackAsync(client, agentId, from, conversationKey, string.IsNullOrWhiteSpace(nextEntry.Permission.SessionId) ? sessionId : nextEntry.Permission.SessionId, nextEntry.Permission).ConfigureAwait(false);
                    }
                }
                else if (nextEntry.Question != null)
                {
                    await OfferQuestionPromptWithFallbackAsync(client, agentId, from, conversationKey, string.IsNullOrWhiteSpace(nextEntry.Question.SessionId) ? sessionId : nextEntry.Question.SessionId, nextEntry.Question).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            promptGate.Release();
        }
    }

    private async Task HandlePendingPromptStateChangedAsync(HarnessPendingPromptStateEvent promptStateEvent)
    {
        var client = _client;
        if (_harnessClient == null || client == null || !_connected || string.IsNullOrWhiteSpace(promptStateEvent.SessionId))
        {
            return;
        }

        var conversationKey = FindConversationKeyForSessionId(promptStateEvent.SessionId);
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            var sessionFamily = await GetSessionFamilyIdsAsync(promptStateEvent.SessionId, CancellationToken.None).ConfigureAwait(false);
            foreach (var pair in _conversationAgentByKey)
            {
                var mappedSessionId = _harnessClient.GetConversationSessionId(pair.Key);
                if (!string.IsNullOrWhiteSpace(mappedSessionId)
                    && sessionFamily.Contains(mappedSessionId, StringComparer.OrdinalIgnoreCase))
                {
                    conversationKey = pair.Key;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(conversationKey)
            || !_conversationAgentByKey.TryGetValue(conversationKey, out var agentId)
            || agentId == UUID.Zero)
        {
            return;
        }

        var queueEntries = new List<PendingPromptQueueEntry>();
        foreach (var permission in promptStateEvent.PendingPermissions)
        {
            if (!string.IsNullOrWhiteSpace(permission?.Id))
            {
                queueEntries.Add(new PendingPromptQueueEntry(PendingPromptKind.Permission, string.IsNullOrWhiteSpace(permission.SessionId) ? promptStateEvent.SessionId : permission.SessionId, permission.Id, permission, null));
            }
        }

        foreach (var question in promptStateEvent.PendingQuestions)
        {
            if (!string.IsNullOrWhiteSpace(question?.Id))
            {
                queueEntries.Add(new PendingPromptQueueEntry(PendingPromptKind.Question, string.IsNullOrWhiteSpace(question.SessionId) ? promptStateEvent.SessionId : question.SessionId, question.Id, null, question));
            }
        }

        if (queueEntries.Count > 0)
        {
            EnqueuePendingPromptEntries(conversationKey, queueEntries);
        }

        if (!_conversationNameByKey.TryGetValue(conversationKey, out var from) || string.IsNullOrWhiteSpace(from))
        {
            from = "handler";
        }

        await DrainPendingPromptsAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
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

        if (_harnessClient != null && !string.IsNullOrWhiteSpace(sessionIdHint))
        {
            foreach (var pair in _conversationAgentByKey)
            {
                if (pair.Value == UUID.Zero)
                {
                    continue;
                }

                var mappedSessionId = _harnessClient.GetConversationSessionId(pair.Key);
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

    private void MarkHarnessSessionIdle(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        __busyHarnessSessions.TryRemove(sessionId, out _);
        if (__busyHarnessSessions.IsEmpty)
        {
            ClearBusyHoverText();
        }
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

    private static string BuildFriendlyPermissionListLine(HarnessPendingPermission permission)
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

    private static string GetPermissionPrimaryText(HarnessPendingPermission permission, out bool titleLooksLikeId)
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

    private async Task<IReadOnlyList<HarnessPendingPermission>> GetPendingPermissionsEventFirstAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<HarnessPendingPermission>();
        }

        var sessionFamily = await GetSessionFamilyIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFamily.Count == 0)
        {
            return Array.Empty<HarnessPendingPermission>();
        }

        var fromApiFamily = new List<HarnessPendingPermission>();
        foreach (var familySessionId in sessionFamily)
        {
            var fromApi = await _harnessClient.ListPendingPermissionsAsync(familySessionId, cancellationToken).ConfigureAwait(false);
            if (fromApi.Count > 0)
            {
                fromApiFamily.AddRange(fromApi);
            }
        }

        if (fromApiFamily.Count > 0)
        {
            return fromApiFamily
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fromEventFamily = new List<HarnessPendingPermission>();
        foreach (var familySessionId in sessionFamily)
        {
            if (_harnessClient.TryGetPendingPermissionsFromEvents(familySessionId, out var fromEvents)
                && fromEvents.Count > 0)
            {
                fromEventFamily.AddRange(fromEvents);
            }
        }

        if (fromEventFamily.Count == 0)
        {
            return Array.Empty<HarnessPendingPermission>();
        }

        return fromEventFamily
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<HarnessPendingQuestion>> GetPendingQuestionsEventFirstAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<HarnessPendingQuestion>();
        }

        var sessionFamily = await GetSessionFamilyIdsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (sessionFamily.Count == 0)
        {
            return Array.Empty<HarnessPendingQuestion>();
        }

        var fromApiFamily = new List<HarnessPendingQuestion>();
        foreach (var familySessionId in sessionFamily)
        {
            var fromApi = await _harnessClient.ListPendingQuestionsAsync(familySessionId, cancellationToken).ConfigureAwait(false);
            if (fromApi.Count > 0)
            {
                fromApiFamily.AddRange(fromApi);
            }
        }

        if (fromApiFamily.Count > 0)
        {
            return fromApiFamily
                .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(q => q.Header, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var fromEventFamily = new List<HarnessPendingQuestion>();
        foreach (var familySessionId in sessionFamily)
        {
            if (_harnessClient.TryGetPendingQuestionsFromEvents(familySessionId, out var fromEvents)
                && fromEvents.Count > 0)
            {
                fromEventFamily.AddRange(fromEvents);
            }
        }

        if (fromEventFamily.Count == 0)
        {
            return Array.Empty<HarnessPendingQuestion>();
        }

        return fromEventFamily
            .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(q => q.Header, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetSessionFamilyIdsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(sessionId))
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
            IReadOnlyList<HarnessSessionSummary> children;
            try
            {
                children = await _harnessClient.GetSessionChildrenAsync(current, cancellationToken).ConfigureAwait(false);
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
        HarnessPendingPermission permission)
    {
        if (string.IsNullOrWhiteSpace(permission.Id))
        {
            return Task.CompletedTask;
        }

        if (HasActivePromptForConversation(conversationKey))
        {
            return Task.CompletedTask;
        }

        MarkPendingPromptActive(conversationKey, permission.Id);
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
        HarnessPendingQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.Id))
        {
            return Task.CompletedTask;
        }

        if (HasActivePromptForConversation(conversationKey))
        {
            return Task.CompletedTask;
        }

        MarkPendingPromptActive(conversationKey, question.Id);
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
        HarnessPendingPermission? permission = null,
        HarnessPendingQuestion? question = null)
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
        HarnessPendingPermission? permission = null,
        HarnessPendingQuestion? question = null)
    {
        ClearPendingPromptWait(conversationKey);
        MarkPendingPromptActive(conversationKey, requestId);
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
            ? BuildTextFallbackPermissionPrompt(permission ?? new HarnessPendingPermission(requestId, sessionId, string.Empty, null))
            : BuildTextFallbackQuestionPrompt(question ?? new HarnessPendingQuestion(requestId, sessionId, "Question", "Please answer.", Array.Empty<string>(), null, true));
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

        if (_harnessClient == null
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

            _ = await _harnessClient.RespondToPermissionAsync(state.SessionId, state.RequestId, response, remember, CancellationToken.None).ConfigureAwait(false);
            _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
            _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
            _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);
            ClearPendingPromptActive(conversationKey, state.RequestId);
            ScheduleDrainPendingPrompts(client, agentId, from, conversationKey);
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

        _ = await _harnessClient.ReplyToQuestionAsync(state.SessionId, state.RequestId, new[] { resolved }, CancellationToken.None).ConfigureAwait(false);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptActive(conversationKey, state.RequestId);
        ScheduleDrainPendingPrompts(client, agentId, from, conversationKey);
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
        HarnessPendingPermission? permission,
        HarnessPendingQuestion? question,
        string conversationKey)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(requestId))
        {
            return false;
        }

        var effectiveSessionId = sessionId;
        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = _harnessClient.GetConversationSessionId(conversationKey) ?? string.Empty;
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

    private static string BuildTextFallbackPermissionPrompt(HarnessPendingPermission permission)
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

    private static string BuildTextFallbackQuestionPrompt(HarnessPendingQuestion question)
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

                if (IsFollowDiagnosticsEnabled())
                {
                    UUID trackedAvatarId;
                    uint trackedLocalId;
                    ulong anchorHandle;
                    string targetDescription;
                    lock (_movementLock)
                    {
                        trackedAvatarId = _followTrackedAvatarId;
                        trackedLocalId = _followTrackedLocalId;
                        anchorHandle = _followAnchorSimHandle;
                        targetDescription = _followTargetDescription ?? "(none)";
                    }

                    if (trackedAvatarId != UUID.Zero)
                    {
                        if (TryFindAvatarByIdAcrossSims(client, trackedAvatarId, out var seenSim, out var seenAvatar))
                        {
                            Console.WriteLine(
                                $"[follow][diag] sim_changed activeFollow={targetDescription} anchorHandle={anchorHandle} trackedUuid={trackedAvatarId} trackedLocalId={trackedLocalId} botSim={DescribeSimulator(sim)} seenSim={DescribeSimulator(seenSim)} seenLocalId={seenAvatar!.LocalID} seenPos={FormatPosition(seenAvatar.Position)} botPos={FormatPosition(client.Self.SimPosition)}");
                        }
                        else
                        {
                            Console.WriteLine(
                                $"[follow][diag] sim_changed activeFollow={targetDescription} anchorHandle={anchorHandle} trackedUuid={trackedAvatarId} trackedLocalId={trackedLocalId} botSim={DescribeSimulator(sim)} seenSim=(not visible) knownSims={client.Network.Simulators.Count}");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(targetDescription))
                    {
                        Console.WriteLine(
                            $"[follow][diag] sim_changed activeFollow={targetDescription} anchorHandle={anchorHandle} botSim={DescribeSimulator(sim)} (object/non-uuid target)");
                    }
                }

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

internal sealed class ConversationConfig
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ModelId { get; set; }
    public string? ThinkingLevel { get; set; }
}
