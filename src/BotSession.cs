using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;
using LibreMetaverse.Assets;
using LibreMetaverse.Packets;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession : IDisposable
{
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

    private async Task<(string ConversationKey, UUID AgentId, string From)?> ResolveUserResponseConversationForSessionAsync(string sessionId)
    {
        if (_harnessClient == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var conversationKey = FindConversationKeyForSessionId(sessionId);
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            var sessionFamily = await GetSessionFamilyIdsAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
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
            return null;
        }

        if (!_conversationNameByKey.TryGetValue(conversationKey, out var from) || string.IsNullOrWhiteSpace(from))
        {
            from = "handler";
        }

        return (conversationKey, agentId, from);
    }

    private void LogRetryStatusEvent(string sessionId, string? statusMessage)
    {
        var message = string.IsNullOrWhiteSpace(statusMessage)
            ? $"[harness] session {sessionId} is retrying"
            : $"[harness] session {sessionId} is retrying: {statusMessage}";
        Console.WriteLine(message);
    }

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
    private readonly ConcurrentDictionary<string, UUID> _conversationAgentByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _conversationNameByKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlightRequestCtsByConversation = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _ambientConversationKey = new();
    private readonly string _handlerConfigPath;
    private readonly string? _parentFullName;
    private readonly object _promptStateLock = new();
    private readonly object _recentImSpeakerLock = new();
    private readonly object _handlerConfigLock = new();
    private readonly object _typingStateLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifecycleCts = new();
    private readonly BotTaskManager _botTaskManager;
    private readonly object _inventoryListResultLock = new();
    private readonly Dictionary<string, InventoryQueryResult> _inventoryListResultsByHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _inventoryListResultHandleByTaskHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _inventoryListResultOrder = new();
    private readonly HashSet<string> _inventoryListDiscardedHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _inventoryListResultCacheLimit;
    private readonly AgentLocator _agentLocator;
    private readonly HashSet<ChatType> _receiveChatAllowedTypes;
    private readonly IUserResponseHandler _userResponseHandler;

    private string? _projectAgentsPromptCache;
    private DateTime _projectAgentsPromptCacheLastWriteUtc;
    private string? _builtInPromptOverrideCache;
    private DateTime _builtInPromptOverrideCacheLastWriteUtc;
    private string? _builtInPromptOverrideCachePath;
    private string? _activeAgentsNotecardPrompt;
    private string? _activeAgentsNotecardSourceName;
    private string? _activeAgentsNotecardItemId;
    private DateTimeOffset? _activeAgentsNotecardInstalledAt;
    private UUID _lastImSpeakerAgentId = UUID.Zero;
    private string? _lastImSpeakerName;
    private string? _lastImConversationKey;
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
    private const string LocalChatConversationKey = "local-chat";
    private const int TypingPulseMinimumIntervalMs = 2000;
    private const int TypingStopDelayMs = 2500;
    private static readonly IReadOnlyList<string> LslPermissionDialogOptions = new[] { "yes", "no", "yes always", "no always" };

    private GridClient? _client;
    private readonly object _cofLock = new();
    private LibreMetaverse.Appearance.CurrentOutfitFolder? _sharedCurrentOutfitFolder;
    private GridClient? _sharedCurrentOutfitFolderClient;
    private bool _connected;
    private string _lastLoginMessage = string.Empty;
    private int _reconnectLoopActive;
    private int _startupSetupProvisionState;
    private readonly SpawnerClient _followSpawnerClient;

    public BotSession(AppOptions options)
    {
        _options = options;
        _botTaskManager = new BotTaskManager(_lifecycleCts.Token);
        _inventoryListResultCacheLimit = Math.Max(1, options.InventoryListResultCacheLimit);
        _followSpawnerClient = new SpawnerClient(options);
        _agentLocator = new AgentLocator(this, _followSpawnerClient);
        _receiveChatAllowedTypes = ParseLocalChatAllowedTypes(_options.ReceiveChatAllowedTypes, out var invalidLocalChatTypeNames);
        _controlGroupName = options.BotGroup.Trim();
        InitializeVoiceSupport();
        _handlerConfigPath = string.IsNullOrWhiteSpace(_options.HandlerConfig)
            ? "/config/handlers.json"
            : _options.HandlerConfig.Trim();
        _parentFullName = NormalizeAvatarName(_options.BotSpawnerParent);
        _harnessClient = new OpencodeChatClient(_options);
        _harnessClient.SessionStatusChanged += OnHarnessSessionStatusChanged;
        _harnessClient.MessagePartUpdated += OnHarnessMessagePartUpdated;
        _userResponseHandler = new TextChatUserResponse(
            harnessClient: _harnessClient,
            tryHandleStarCommandAsync: TryHandleStarCommandAsync,
            sendImText: SendImText,
            getActiveClient: () => _connected ? _client : null,
            resolveConversationForSessionAsync: ResolveUserResponseConversationForSessionAsync,
            getPendingPermissionsAsync: GetPendingPermissionsEventFirstAsync,
            getPendingQuestionsAsync: GetPendingQuestionsEventFirstAsync);
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

    internal AgentLocator AgentLocator => _agentLocator;

    public BotTaskHandle StartBotTask(string description, Func<BotTaskHandle, CancellationToken, Task> work)
    {
        return _botTaskManager.Start(description, work);
    }

    public IReadOnlyList<BotTaskHandle> ListActiveBotTasks()
    {
        return _botTaskManager.ListActive();
    }

    public BotTaskQueryResult GetBotTask(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return BotTaskQueryResult.FailResult("handle is required.");
        }

        return _botTaskManager.TryGet(handle, out var taskInfo)
            ? BotTaskQueryResult.OkResult(taskInfo!, "Task status returned.")
            : BotTaskQueryResult.FailResult("Task handle was not found.");
    }

    public BotToolResult CancelBotTask(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return BotToolResult.Fail("handle is required.");
        }

        return _botTaskManager.TryCancel(handle, out var taskHandle)
            ? BotToolResult.OkResult($"Requested cancellation for task {taskHandle!.Handle}.")
            : BotToolResult.Fail("Task handle was not found among active tasks.");
    }

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
            LibreMetaverse.Settings.LogLevel = Microsoft.Extensions.Logging.LogLevel.Debug;
            client.Settings.Logging.LogResends = false;
            client.Settings.World.StoreLandPatches = true;
            client.Settings.World.AlwaysDecodeObjects = true;
            client.Settings.World.AlwaysRequestObjects = true;
            client.Settings.Agent.SendUpdates = true;
            client.Settings.Agent.MultipleSims = true;
            
            //  Cache setup            
            client.Settings.AssetCache.Enabled = _options.CacheEnabled;
            client.Settings.AssetCache.MaxSize = _options.CacheMaxSize;
            if(!string.IsNullOrEmpty(_options.CacheDir)) {
                client.Settings.AssetCache.Dir = _options.CacheDir;
            }
            
            client.Network.LoginProgress += OnLoginProgress;
            client.Network.Disconnected += OnDisconnected;
            client.Network.SimChanged += OnNetworkSimChanged;
            client.Self.IM += OnInstantMessage;
            client.Self.ChatFromSimulator += OnChatFromSimulator;
            EnsureSocialImHookRegistered(client);
            EnsureScriptDialogHookRegistered(client);
            client.Friends.FriendshipOffered += OnFriendshipOffered;
            client.Inventory.InventoryObjectOffered += OnInventoryObjectOffered;
            client.Objects.ObjectUpdate += OnWorldObjectUpdateForEventStream;
            client.Objects.TerseObjectUpdate += OnTerseWorldObjectUpdateForEventStream;
            client.Objects.AvatarUpdate += AvatarUpdateHandler;     

            client.Network.RegisterCallback(PacketType.AlertMessage, AlertMessageHandler);
            

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
            Interlocked.Exchange(ref _startupSetupProvisionState, 0);
            QueueStartupSetupProvisioning("login-connected");
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

    public string GetCurrentSimName()
    {
        var client = _client;
        if (!_connected || client == null)
        {
            EnsureReconnectLoop("get-current-sim");
            return "(disconnected)";
        }
        else {
            var sim = client.Network.CurrentSim;
            return sim?.Name ?? "(unknown)";   
        }
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
        }
        _userResponseHandler.Dispose();
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
        __busyHarnessSessions.Clear();
        ClearBusyHoverText();
        DisposeVoiceSupport();
        ResetSharedCurrentOutfitFolder();
        _client = null;
        _connected = false;
        StopFollowInternal();
        CancelMovementAutoStop();
        _followSpawnerClient.Dispose();
        _botTaskManager.Dispose();

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

    internal bool TryGetConnectedClientSnapshot(out GridClient? client)
    {
        client = _connected ? _client : null;
        return client != null;
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
                client.Self.Chat("[typing] start", 3645376, ChatType.Normal);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[typing] failed to broadcast start marker: {ex.Message}");
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
            client.Self.Chat("[typing] stop", 3645376, ChatType.Normal);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[typing] failed to broadcast stop marker: {ex.Message}");
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

    private void AlertMessageHandler(object? sender, PacketReceivedEventArgs e)
    {
        Packet packet = e.Packet;

        AlertMessagePacket alert = (AlertMessagePacket)packet;
        if (alert.AlertInfo.Length > 0)
        {
            EmitRuntimeEvent(
                "general",
                "alert.message",
                "opensim",
                Utils.BytesToString(alert.AlertInfo[0].Message),
                new Dictionary<string, string?>());
        }
    }
    
    private void AvatarUpdateHandler(object? sender, AvatarUpdateEventArgs e)
    {
        if ( _client != null && e.Avatar.LocalID == _client.Self.LocalID)
        {
            SetDefaultCamera();
        }
    }
    
    private void SetDefaultCamera()
    {
        // SetCamera 5m behind the avatar
        if( _client != null) {
            _client.Self.Movement.Camera.LookAt(
                _client.Self.SimPosition + new Vector3(-5, 0, 0) * _client.Self.Movement.BodyRotation,
                _client.Self.SimPosition
            );
        }
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
        Console.WriteLine($"[sim-change] sim changed");
        // Fire-and-forget: run the health-check on a background task so we don't block
        // the network event loop.
        _ = Task.Run(async () =>
        {
            try
            {
                var client = _client;
                if (client == null) {
                Console.WriteLine("[sim-change] OnNetworkSimChanged: no client.");
                    return;
                }

                if (!_connected)
                {
                    Console.WriteLine("[sim-change] ignoring pre-login sim-change event.");
                    return;
                }

                QueueStartupSetupProvisioning("sim-change");

                Console.WriteLine("[sim-change] OnNetworkSimChanged: processing region transition.");

                // opensim-ai-docker#7: region transitions are the known trigger for
                // server-side visual-param pollution; schedule the (guarded,
                // self-delaying) appearance health check without blocking the
                // network event loop.
                RunAppearancePostSimChangeCheck();

                // Wait for core client subsystems to settle before running post-transition actions.
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
                    Console.WriteLine("[sim-change] client not fully initialized yet; skipping sim-change follow-up actions.");
                    return;
                }
                
                client.Self.Movement.SetFOVVerticalAngle(Utils.TWO_PI - 0.05f);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sim-change] follow-up processing error: {ex.Message}");
            }
        });
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e)
    {
        _connected = false;
        Interlocked.Exchange(ref _startupSetupProvisionState, 0);
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
        ResetSharedCurrentOutfitFolder(client);

        try { client.Self.IM -= OnInstantMessage; } catch { }
        try { client.Self.IM -= OnSocialInstantMessage; } catch { }
        try { client.Self.ScriptDialog -= OnScriptDialogReceived; } catch { }
        try { client.Self.ChatFromSimulator -= OnChatFromSimulator; } catch { }
        try { client.Friends.FriendshipOffered -= OnFriendshipOffered; } catch { }
        try { client.Inventory.InventoryObjectOffered -= OnInventoryObjectOffered; } catch { }
        try { client.Objects.ObjectUpdate -= OnWorldObjectUpdateForEventStream; } catch { }
        try { client.Network.Disconnected -= OnDisconnected; } catch { }
        try { client.Network.SimChanged -= OnNetworkSimChanged; } catch { }
        try { client.Network.LoginProgress -= OnLoginProgress; } catch { }
        try { client.Objects.TerseObjectUpdate -= OnTerseWorldObjectUpdateForEventStream; } catch { }
        try { client.Objects.AvatarUpdate -= AvatarUpdateHandler; } catch { }
        
        try { client.Network.UnregisterCallback(PacketType.AlertMessage, AlertMessageHandler); } catch { }

        lock (_socialImHookLock)
        {
            if (ReferenceEquals(_socialImHookClient, client))
            {
                _socialImHookClient = null;
            }
        }

        lock (_scriptDialogHookLock)
        {
            if (ReferenceEquals(_scriptDialogHookClient, client))
            {
                _scriptDialogHookClient = null;
            }
        }

        if (logout)
        {
            try { client.Network.Logout(); } catch { }
        }

        try { client.Dispose(); } catch { }
    }

    private LibreMetaverse.Appearance.CurrentOutfitFolder GetSharedCurrentOutfitFolder(GridClient client)
    {
        lock (_cofLock)
        {
            if (_sharedCurrentOutfitFolder != null && !ReferenceEquals(_sharedCurrentOutfitFolderClient, client))
            {
                try
                {
                    _sharedCurrentOutfitFolder.Dispose();
                }
                catch
                {
                    // Best-effort cleanup while switching clients.
                }

                _sharedCurrentOutfitFolder = null;
                _sharedCurrentOutfitFolderClient = null;
            }

            if (_sharedCurrentOutfitFolder == null)
            {
                _sharedCurrentOutfitFolder = new LibreMetaverse.Appearance.CurrentOutfitFolder(client);
                _sharedCurrentOutfitFolderClient = client;
            }

            return _sharedCurrentOutfitFolder;
        }
    }

    private void ResetSharedCurrentOutfitFolder(GridClient? onlyForClient = null)
    {
        lock (_cofLock)
        {
            if (_sharedCurrentOutfitFolder == null)
            {
                return;
            }

            if (onlyForClient != null && !ReferenceEquals(_sharedCurrentOutfitFolderClient, onlyForClient))
            {
                return;
            }

            try
            {
                _sharedCurrentOutfitFolder.Dispose();
            }
            catch
            {
                // Best-effort cleanup.
            }

            _sharedCurrentOutfitFolder = null;
            _sharedCurrentOutfitFolderClient = null;
        }
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

internal sealed record BotTaskQueryResult(bool Ok, string Message, BotTaskInfo? Task)
{
    public static BotTaskQueryResult OkResult(BotTaskInfo task, string message)
        => new(true, message, task);

    public static BotTaskQueryResult FailResult(string message)
        => new(false, message, null);
}

internal sealed class ConversationConfig
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ModelId { get; set; }
    public string? ThinkingLevel { get; set; }
}
