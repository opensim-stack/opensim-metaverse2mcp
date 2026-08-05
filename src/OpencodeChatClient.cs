using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NOpenCode;

namespace Opensim.Metaverse2Mcp;

internal interface IOpencodeChatClient
{
    Task<OpencodeChatReply> SendMessageAsync(string conversationKey, string title, string message, OpencodeSendOptions? options, CancellationToken cancellationToken);
    void ResetConversation(string conversationKey);
    void SetConversationSessionId(string conversationKey, string? sessionId);
    string? GetConversationSessionId(string conversationKey);
    Task<IReadOnlyList<OpencodeProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeProviderSummary>> ListAvailableProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, IReadOnlyList<OpencodeProviderAuthMethod>>> ListProviderAuthMethodsAsync(CancellationToken cancellationToken);
    Task SetProviderApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken);
    Task<OpencodeOAuthStartResult> StartProviderOAuthAsync(string providerId, int methodIndex, IReadOnlyDictionary<string, string>? inputs, CancellationToken cancellationToken);
    Task<OpencodeOAuthCompleteResult> CompleteProviderOAuthAsync(string providerId, int methodIndex, string? code, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeModelSummary>> ListModelsAsync(string? providerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);
    Task<OpencodeSessionSummary> CreateSessionAsync(string? title, string? parentSessionId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string>> GetSessionStatusAsync(CancellationToken cancellationToken);
    Task<string> GetSessionDetailsJsonAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeSessionSummary>> GetSessionChildrenAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodePendingPermission>> ListPendingPermissionsAsync(string sessionId, CancellationToken cancellationToken);
    bool TryGetPendingPermissionsFromEvents(string sessionId, out IReadOnlyList<OpencodePendingPermission> pendingPermissions);
    Task<bool> RespondToPermissionAsync(string sessionId, string permissionId, string response, bool remember, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodePendingQuestion>> ListPendingQuestionsAsync(string sessionId, CancellationToken cancellationToken);
    bool TryGetPendingQuestionsFromEvents(string sessionId, out IReadOnlyList<OpencodePendingQuestion> pendingQuestions);
    Task<bool> ReplyToQuestionAsync(string sessionId, string questionId, IReadOnlyList<string> answers, CancellationToken cancellationToken);
    Task<bool> RejectQuestionAsync(string sessionId, string questionId, CancellationToken cancellationToken);
    Task<OpencodeSessionSummary> UpdateSessionTitleAsync(string sessionId, string title, CancellationToken cancellationToken);
    Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<bool> SummarizeSessionAsync(string sessionId, string? providerId, string? modelId, CancellationToken cancellationToken);
    Task<bool> AbortSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken);
    Task<OpencodeProjectSummary?> GetCurrentProjectAsync(CancellationToken cancellationToken);
}

internal sealed record OpencodeChatReply(
    string Text,
    bool IsConfirmationPrompt,
    IReadOnlyList<OpencodePendingPermission>? PendingPermissions = null,
    IReadOnlyList<OpencodePendingQuestion>? PendingQuestions = null);
internal sealed record OpencodeSendOptions(string? ModelId, string? ThinkingLevel, string? SystemPrompt);
internal sealed record OpencodeProviderSummary(string Id, string Name, bool? Connected);
internal sealed record OpencodeModelSummary(string Id, string Name, string? Provider);
internal sealed record OpencodeSessionSummary(string Id, string Title, string? Status, string? ProjectId);
internal sealed record OpencodeProjectSummary(string Id, string Name, string? Path, bool? Current);
internal sealed record OpencodePendingPermission(string Id, string SessionId, string Title, string? Description);
internal sealed record OpencodePendingQuestion(string Id, string SessionId, string Header, string Question, IReadOnlyList<string> Options, bool? AllowsMultiple, bool? AllowsCustom);
internal sealed record OpencodeProviderAuthMethod(int MethodIndex, string Type, string Label);
internal sealed record OpencodeOAuthStartResult(string Url, string? Method, string? Instructions);
internal sealed record OpencodeOAuthCompleteResult(bool CallbackAccepted, bool ProviderConfigured, string Message);

internal sealed class OpencodeChatClient : IOpencodeChatClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClient? _eventHttp;
    private readonly string _opencodeEventMode;
    private readonly CancellationTokenSource? _eventLoopCts;
    private readonly Task? _eventLoopTask;
    // TEMP(event-first migration): this is only for discovery-rate limiting while validating
    // endpoint behavior. Remove when event schema/flow is stable and structured metrics replace it.
    private readonly ConcurrentDictionary<string, int> _eventLogCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpencodeOAuthPendingState> _oauthPendingStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpencodePendingPermission>> _pendingPermissionsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpencodePendingQuestion>> _pendingQuestionsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpencodePendingPermission>> _eventPendingPermissionsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<OpencodePendingQuestion>> _eventPendingQuestionsBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _modelOverrideGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OpencodeChatClient(AppOptions options)
    {
        var baseUrl = BuildBaseUrl(options.OpencodeScheme, options.OpencodeHost, options.OpencodePort);
        _opencodeEventMode = NormalizeEventMode(options.OpencodeEventMode);

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(options.OpencodeRequestTimeoutSeconds)
        };

        var username = string.IsNullOrWhiteSpace(options.OpencodeUsername) ? "opencode" : options.OpencodeUsername.Trim();
        if (!string.IsNullOrWhiteSpace(options.OpencodePassword))
        {
            var bytes = Encoding.ASCII.GetBytes($"{username}:{options.OpencodePassword}");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }

        if (_opencodeEventMode == "off")
        {
            _eventHttp = null;
            _eventLoopCts = null;
            _eventLoopTask = null;
        }
        else
        {
            _eventHttp = new HttpClient
            {
                BaseAddress = new Uri(baseUrl, UriKind.Absolute),
                Timeout = Timeout.InfiniteTimeSpan
            };
            _eventHttp.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;
            _eventLoopCts = new CancellationTokenSource();
            _eventLoopTask = Task.Run(() => ObserveEventStreamsLoopAsync(_eventLoopCts.Token));
            Console.WriteLine($"[opencode:event] mode={_opencodeEventMode}; probing /event and /global/event for runtime behavior.");
            if (_opencodeEventMode == "active")
            {
                Console.WriteLine("[opencode:event] active mode currently runs discovery/observation only; command flow remains polling-backed for now.");
            }
        }

        Console.WriteLine("[opencode] model payload strategy: session-only (no per-message model override)");
    }

    // TEMP(event-first migration): keep this observer until BotSession consumes session-correlated
    // events directly for permission/question handling. After cutover, retain one production listener
    // path and remove duplicate probe behavior.
    private async Task ObserveEventStreamsLoopAsync(CancellationToken cancellationToken)
    {
        if (_eventHttp == null)
        {
            return;
        }

        var workers = new[]
        {
            ObserveEventStreamLoopAsync("/event", cancellationToken),
            ObserveEventStreamLoopAsync("/global/event", cancellationToken)
        };

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task ObserveEventStreamLoopAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (_eventHttp == null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                using var response = await _eventHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"[opencode:event] connect failed endpoint={endpoint} status={(int)response.StatusCode} {response.StatusCode} body={TruncateForLog(body, 240)}");
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                Console.WriteLine($"[opencode:event] connected endpoint={endpoint}");
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);

                var currentEventName = "message";
                var dataBuilder = new StringBuilder();

                while (!cancellationToken.IsCancellationRequested && !reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        if (dataBuilder.Length > 0)
                        {
                            LogObservedEvent(endpoint, currentEventName, dataBuilder.ToString());
                        }

                        currentEventName = "message";
                        dataBuilder.Clear();
                        continue;
                    }

                    if (line[0] == ':')
                    {
                        continue;
                    }

                    if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEventName = line[6..].Trim();
                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var chunk = line[5..].TrimStart();
                        if (dataBuilder.Length > 0)
                        {
                            dataBuilder.Append('\n');
                        }

                        dataBuilder.Append(chunk);
                    }
                }

                Console.WriteLine($"[opencode:event] stream ended endpoint={endpoint}; reconnecting...");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[opencode:event] stream error endpoint={endpoint}: {ex.Message}");
            }

            try
            {
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // TEMP(event-first migration): once event-first flow is proven, replace this with compact
    // structured metrics/correlation logging and remove discovery-oriented payload diagnostics.
    private void LogObservedEvent(string endpoint, string eventName, string rawData)
    {
        if (string.IsNullOrWhiteSpace(rawData))
        {
            return;
        }

        var normalizedEvent = string.IsNullOrWhiteSpace(eventName) ? "message" : eventName.Trim();
        var eventType = normalizedEvent;
        var sessionId = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(rawData);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetStringPropertyAny(root, out var parsedType, "type", "event", "name")
                    && !string.IsNullOrWhiteSpace(parsedType))
                {
                    eventType = parsedType!.Trim();
                }

                if (TryExtractSessionId(root, out var parsedSessionId)
                    && !string.IsNullOrWhiteSpace(parsedSessionId))
                {
                    sessionId = parsedSessionId!.Trim();
                }
            }

            IngestEventDerivedPendingState(eventType, sessionId, root);
        }
        catch (JsonException)
        {
            // Keep logging, but mark payload as non-JSON for discovery diagnostics.
            eventType = normalizedEvent + "/non-json";
        }

        var key = endpoint + "|" + eventType;
        var seenCount = _eventLogCounts.AddOrUpdate(key, 1, (_, count) => count + 1);
        var important = eventType.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("question", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("session", StringComparison.OrdinalIgnoreCase);

        if (!important && seenCount > 3)
        {
            return;
        }

        var sessionLabel = string.IsNullOrWhiteSpace(sessionId) ? "n/a" : sessionId;
        Console.WriteLine($"[opencode:event] endpoint={endpoint} event={normalizedEvent} type={eventType} session={sessionLabel} seen={seenCount} dataLength={rawData.Length}");
    }

    private void IngestEventDerivedPendingState(string eventType, string? hintedSessionId, JsonElement root)
    {
        var normalizedType = (eventType ?? string.Empty).Trim().ToLowerInvariant();
        var derivedPermissions = ParsePendingPermissions(root)
            .Select(p => string.IsNullOrWhiteSpace(p.SessionId) && !string.IsNullOrWhiteSpace(hintedSessionId)
                ? new OpencodePendingPermission(p.Id, hintedSessionId!, p.Title, p.Description)
                : p)
            .Where(p => !string.IsNullOrWhiteSpace(p.SessionId))
            .ToList();

        if (derivedPermissions.Count > 0)
        {
            foreach (var group in derivedPermissions.GroupBy(p => p.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                _eventPendingPermissionsBySession[group.Key] = group
                    .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                _pendingPermissionsBySession[group.Key] = _eventPendingPermissionsBySession[group.Key];
            }
        }
        else if (normalizedType.Contains("permission", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(hintedSessionId))
        {
            _eventPendingPermissionsBySession.TryRemove(hintedSessionId, out _);
        }

        var derivedQuestions = ParsePendingQuestions(root)
            .Select(q => string.IsNullOrWhiteSpace(q.SessionId) && !string.IsNullOrWhiteSpace(hintedSessionId)
                ? new OpencodePendingQuestion(q.Id, hintedSessionId!, q.Header, q.Question, q.Options, q.AllowsMultiple, q.AllowsCustom)
                : q)
            .Where(q => !string.IsNullOrWhiteSpace(q.SessionId))
            .ToList();

        if (derivedQuestions.Count > 0)
        {
            foreach (var group in derivedQuestions.GroupBy(q => q.SessionId, StringComparer.OrdinalIgnoreCase))
            {
                _eventPendingQuestionsBySession[group.Key] = group
                    .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
                _pendingQuestionsBySession[group.Key] = _eventPendingQuestionsBySession[group.Key];
            }
        }
        else if (normalizedType.Contains("question", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(hintedSessionId))
        {
            _eventPendingQuestionsBySession.TryRemove(hintedSessionId, out _);
        }
    }

    private static bool TryExtractSessionId(JsonElement element, out string? sessionId)
    {
        sessionId = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetStringPropertyAny(element, out var direct, "sessionID", "sessionId")
                    && !string.IsNullOrWhiteSpace(direct))
                {
                    sessionId = direct!.Trim();
                    return true;
                }

                if (element.TryGetProperty("session", out var sessionObject)
                    && sessionObject.ValueKind == JsonValueKind.Object
                    && TryGetStringPropertyAny(sessionObject, out var nested, "id", "sessionID", "sessionId")
                    && !string.IsNullOrWhiteSpace(nested))
                {
                    sessionId = nested!.Trim();
                    return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryExtractSessionId(property.Value, out sessionId))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    if (TryExtractSessionId(child, out sessionId))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static string NormalizeEventMode(string? raw)
    {
        var normalized = (raw ?? "off").Trim().ToLowerInvariant();
        return normalized is "observe" or "active"
            ? normalized
            : "off";
    }

    public async Task<OpencodeChatReply> SendMessageAsync(string conversationKey, string title, string message, OpencodeSendOptions? options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            throw new ArgumentException("conversationKey is required.", nameof(conversationKey));
        }

        if (string.IsNullOrWhiteSpace(options?.ModelId))
        {
            return await SendMessageCoreAsync(conversationKey, title, message, options, cancellationToken).ConfigureAwait(false);
        }

        var requestedModel = options!.ModelId!.Trim();
        await _modelOverrideGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? previousModel = null;
        var overrideApplied = false;
        try
        {
            var currentConfig = await GetJsonAsync<OpencodeRuntimeConfig>("/config", cancellationToken).ConfigureAwait(false);
            previousModel = string.IsNullOrWhiteSpace(currentConfig?.Model) ? null : currentConfig!.Model!.Trim();
            if (!string.Equals(previousModel, requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                await PatchConfigModelAsync(requestedModel, cancellationToken).ConfigureAwait(false);
                overrideApplied = true;
            }

            return await SendMessageCoreAsync(conversationKey, title, message, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (overrideApplied && !string.IsNullOrWhiteSpace(previousModel))
            {
                try
                {
                    await PatchConfigModelAsync(previousModel, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[opencode] warning: failed to restore /config model to '{previousModel}': {ex.Message}");
                }
            }

            _modelOverrideGate.Release();
        }
    }

    private async Task<OpencodeChatReply> SendMessageCoreAsync(string conversationKey, string title, string message, OpencodeSendOptions? options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            throw new ArgumentException("conversationKey is required.", nameof(conversationKey));
        }

        var sessionId = await EnsureSessionAsync(conversationKey, title, options, cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendToSessionAsync(sessionId, message, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OpencodeHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Session may have expired on server restart; create a new one and retry once.
            _sessionIds.TryRemove(conversationKey, out _);
            sessionId = await EnsureSessionAsync(conversationKey, title, options, cancellationToken).ConfigureAwait(false);
            return await SendToSessionAsync(sessionId, message, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OpencodeEmbeddedErrorException ex) when (ex.ShouldResetSession)
        {
            // Some providers return API errors inside a 200 response; rebuild the session and retry once.
            _sessionIds.TryRemove(conversationKey, out _);
            sessionId = await EnsureSessionAsync(conversationKey, title, options, cancellationToken).ConfigureAwait(false);
            return await SendToSessionAsync(sessionId, message, options, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PatchConfigModelAsync(string modelId, CancellationToken cancellationToken)
    {
        var patch = new
        {
            model = modelId
        };

        Console.WriteLine($"[opencode] PATCH /config model={modelId}");
        _ = await PatchJsonRawAsync("/config", patch, cancellationToken).ConfigureAwait(false);
    }

    public void ResetConversation(string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        _sessionIds.TryRemove(conversationKey, out _);
    }

    public string? GetConversationSessionId(string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return null;
        }

        return _sessionIds.TryGetValue(conversationKey, out var sessionId) ? sessionId : null;
    }

    public void SetConversationSessionId(string conversationKey, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _sessionIds.TryRemove(conversationKey, out _);
            return;
        }

        _sessionIds[conversationKey] = sessionId.Trim();
    }

    public async Task<IReadOnlyList<OpencodeProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken)
    {
        var available = await ListAvailableProvidersAsync(cancellationToken).ConfigureAwait(false);
        var connected = available
            .Where(p => p.Connected == true)
            .ToList();

        if (connected.Count > 0)
        {
            return connected;
        }

        // Fallback for older server builds where /provider does not expose connected status.
        var response = await GetJsonAsync<OpencodeProvidersResponse>("/config/providers", cancellationToken).ConfigureAwait(false);
        var all = response?.Providers ?? new List<OpencodeProviderEntry>();
        return all
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new OpencodeProviderSummary(p.Id!, string.IsNullOrWhiteSpace(p.Name) ? p.Id! : p.Name!, true))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<OpencodeProviderSummary>> ListAvailableProvidersAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<OpencodeAllProvidersResponse>("/provider", cancellationToken).ConfigureAwait(false);
        var all = response?.All ?? new List<OpencodeProviderEntry>();
        return all
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => new OpencodeProviderSummary(p.Id!, string.IsNullOrWhiteSpace(p.Name) ? p.Id! : p.Name!, p.Connected))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<OpencodeProviderAuthMethod>>> ListProviderAuthMethodsAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/provider/auth", cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpencodeHttpException(response.StatusCode, "/provider/auth", raw);
        }

        var result = new Dictionary<string, IReadOnlyList<OpencodeProviderAuthMethod>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var providerProperty in doc.RootElement.EnumerateObject())
        {
            if (providerProperty.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var methods = new List<OpencodeProviderAuthMethod>();
            var index = 0;
            foreach (var entry in providerProperty.Value.EnumerateArray())
            {
                var type = TryGetStringProperty(entry, "type", out var parsedType) && !string.IsNullOrWhiteSpace(parsedType)
                    ? parsedType!
                    : "unknown";
                var label = TryGetStringProperty(entry, "label", out var parsedLabel) && !string.IsNullOrWhiteSpace(parsedLabel)
                    ? parsedLabel!
                    : type;
                methods.Add(new OpencodeProviderAuthMethod(index, type, label));
                index++;
            }

            result[providerProperty.Name] = methods;
        }

        return result;
    }

    public async Task SetProviderApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("providerId is required.", nameof(providerId));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("apiKey is required.", nameof(apiKey));
        }

        var body = new
        {
            type = "api",
            key = apiKey
        };

        _ = await PutJsonRawAsync($"/auth/{Uri.EscapeDataString(providerId)}", body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpencodeOAuthStartResult> StartProviderOAuthAsync(string providerId, int methodIndex, IReadOnlyDictionary<string, string>? inputs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("providerId is required.", nameof(providerId));
        }

        if (methodIndex < 0)
        {
            throw new ArgumentException("methodIndex must be >= 0.", nameof(methodIndex));
        }

        var body = new
        {
            method = methodIndex,
            inputs = inputs ?? new Dictionary<string, string>()
        };

        var raw = await PostJsonRawAsync($"/provider/{Uri.EscapeDataString(providerId)}/oauth/authorize", body, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Opencode did not return OAuth authorization details.");
        }

        var parsed = JsonSerializer.Deserialize<OpencodeOAuthAuthorizeResponse>(raw, _jsonOptions);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Url))
        {
            throw new InvalidOperationException("Opencode OAuth authorize response did not include a URL.");
        }

        _oauthPendingStates[BuildOAuthStateKey(providerId, methodIndex)] = ParseOAuthPendingState(providerId, methodIndex, raw, parsed);

        return new OpencodeOAuthStartResult(parsed.Url!, parsed.Method, parsed.Instructions);
    }

    public async Task<OpencodeOAuthCompleteResult> CompleteProviderOAuthAsync(string providerId, int methodIndex, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("providerId is required.", nameof(providerId));
        }

        if (methodIndex < 0)
        {
            throw new ArgumentException("methodIndex must be >= 0.", nameof(methodIndex));
        }

        var stateKey = BuildOAuthStateKey(providerId, methodIndex);
        _oauthPendingStates.TryGetValue(stateKey, out var pending);
        var callbackPayloads = BuildOAuthCallbackPayloads(methodIndex, code, pending);

        Exception? lastCallbackError = null;
        var callbackSucceeded = false;
        foreach (var payload in callbackPayloads)
        {
            try
            {
                _ = await PostJsonRawAsync($"/provider/{Uri.EscapeDataString(providerId)}/oauth/callback", payload, cancellationToken).ConfigureAwait(false);
                callbackSucceeded = true;
                break;
            }
            catch (OpencodeHttpException ex) when ((int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500)
            {
                // Try alternate callback payload shapes before failing.
                lastCallbackError = ex;
            }
        }

        if (!callbackSucceeded)
        {
            throw lastCallbackError ?? new InvalidOperationException("OAuth callback request failed.");
        }

        // Some providers finalize asynchronously after callback; wait briefly so UX reflects real state.
        var configured = await WaitForProviderConfiguredAsync(providerId, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        if (!configured)
        {
            var pendingMessage =
                $"OAuth callback was accepted, but provider '{providerId}' is still not configured. " +
                "If this is a device flow, finish/confirm approval in your browser and retry *auth ... oauth-complete.";
            Console.WriteLine($"[opencode] oauth pending for provider {providerId}: callback accepted but provider not configured yet.");
            return new OpencodeOAuthCompleteResult(true, false, pendingMessage);
        }

        _oauthPendingStates.TryRemove(stateKey, out _);
        return new OpencodeOAuthCompleteResult(true, true, $"OAuth completed for provider '{providerId}'.");
    }

    public async Task<IReadOnlyList<OpencodeModelSummary>> ListModelsAsync(string? providerId, CancellationToken cancellationToken)
    {
        var normalizedProviderId = NormalizeProviderQuery(providerId);

        var response = await GetJsonAsync<OpencodeProvidersResponse>("/config/providers", cancellationToken).ConfigureAwait(false);
        var providers = response?.Providers ?? new List<OpencodeProviderEntry>();

        if (!string.IsNullOrWhiteSpace(normalizedProviderId))
        {
            providers = providers
                .Where(p => string.Equals(p.Id, normalizedProviderId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Name, normalizedProviderId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new List<OpencodeModelSummary>();
        foreach (var provider in providers)
        {
            var providerKey = provider.Id?.Trim();
            foreach (var modelEntry in provider.Models ?? new Dictionary<string, OpencodeModelEntry>())
            {
                var model = modelEntry.Value;
                var modelId = !string.IsNullOrWhiteSpace(model.Id)
                    ? model.Id!.Trim()
                    : modelEntry.Key.Trim();
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    continue;
                }

                var canonicalId = !string.IsNullOrWhiteSpace(providerKey) && !modelId.Contains('/')
                    ? $"{providerKey}/{modelId}"
                    : modelId;

                if (!seenIds.Add(canonicalId))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(model.Name) ? canonicalId : model.Name.Trim();
                models.Add(new OpencodeModelSummary(canonicalId, name, providerKey));
            }

            foreach (var defaultModel in EnumerateProviderDefaultModelIds(provider))
            {
                var modelId = defaultModel.Trim();
                var canonicalId = !string.IsNullOrWhiteSpace(providerKey) && !modelId.Contains('/')
                    ? $"{providerKey}/{modelId}"
                    : modelId;
                if (!seenIds.Add(canonicalId))
                {
                    continue;
                }

                models.Add(new OpencodeModelSummary(canonicalId, canonicalId, providerKey));
            }
        }

        return models
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<OpencodeSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync<JsonElement>("/session", cancellationToken).ConfigureAwait(false);
        return ParseSessionList(root);
    }

    public async Task<OpencodeSessionSummary> CreateSessionAsync(string? title, string? parentSessionId, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(title))
        {
            body["title"] = title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parentSessionId))
        {
            var parentId = parentSessionId.Trim();
            body["parentID"] = parentId;
            body["parentId"] = parentId;
        }

        var created = await PostJsonAsync<JsonElement>("/session", body, cancellationToken).ConfigureAwait(false);
        if (!TryBuildSessionSummary(created, null, out var summary))
        {
            throw new InvalidOperationException("Opencode returned an unexpected payload while creating a session.");
        }

        return summary!;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSessionStatusAsync(CancellationToken cancellationToken)
    {
        var response = await GetJsonAsync<Dictionary<string, JsonElement>>("/session/status", cancellationToken).ConfigureAwait(false)
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        return response
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                pair => pair.Key,
                pair => JsonElementToSingleLine(pair.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string> GetSessionDetailsJsonAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var path = $"/session/{Uri.EscapeDataString(sessionId.Trim())}";
        var details = await GetJsonAsync<JsonElement>(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<IReadOnlyList<OpencodePendingPermission>> ListPendingPermissionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var normalizedSessionId = sessionId.Trim();
        var allPending = await GetJsonAsync<JsonElement>("/permission", cancellationToken).ConfigureAwait(false);
        var filtered = ParsePendingPermissions(allPending)
            .Where(p => p.SessionId.Equals(normalizedSessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count > 0)
        {
            _pendingPermissionsBySession[normalizedSessionId] = filtered;
            return filtered;
        }

        if (_pendingPermissionsBySession.TryGetValue(normalizedSessionId, out var cached))
        {
            return cached;
        }

        return Array.Empty<OpencodePendingPermission>();
    }

    public bool TryGetPendingPermissionsFromEvents(string sessionId, out IReadOnlyList<OpencodePendingPermission> pendingPermissions)
    {
        pendingPermissions = Array.Empty<OpencodePendingPermission>();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return _eventPendingPermissionsBySession.TryGetValue(sessionId.Trim(), out pendingPermissions!);
    }

    public async Task<bool> RespondToPermissionAsync(string sessionId, string permissionId, string response, bool remember, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(permissionId))
        {
            throw new ArgumentException("permissionId is required.", nameof(permissionId));
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ArgumentException("response is required.", nameof(response));
        }

        var sessionKey = sessionId.Trim();
        var permissionKey = permissionId.Trim();
        var normalizedResponse = response.Trim().ToLowerInvariant();
        var payloads = BuildPermissionResponsePayloads(normalizedResponse, remember);
        Exception? lastClientError = null;
        var path = $"/permission/{Uri.EscapeDataString(permissionKey)}/reply";
        foreach (var payload in payloads)
        {
            try
            {
                var raw = await PostJsonRawAsync(path, payload, cancellationToken).ConfigureAwait(false);
                var accepted = TryInterpretBooleanResponse(raw, true);
                if (accepted && _pendingPermissionsBySession.TryGetValue(sessionKey, out var existing))
                {
                    var remaining = existing
                        .Where(p => !p.Id.Equals(permissionKey, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (remaining.Count == 0)
                    {
                        _pendingPermissionsBySession.TryRemove(sessionKey, out _);
                    }
                    else
                    {
                        _pendingPermissionsBySession[sessionKey] = remaining;
                    }
                }

                if (accepted && _eventPendingPermissionsBySession.TryGetValue(sessionKey, out var eventExisting))
                {
                    var eventRemaining = eventExisting
                        .Where(p => !p.Id.Equals(permissionKey, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (eventRemaining.Count == 0)
                    {
                        _eventPendingPermissionsBySession.TryRemove(sessionKey, out _);
                    }
                    else
                    {
                        _eventPendingPermissionsBySession[sessionKey] = eventRemaining;
                    }
                }

                return accepted;
            }
            catch (OpencodeHttpException ex) when ((int)ex.StatusCode >= 400 && (int)ex.StatusCode < 500)
            {
                lastClientError = ex;
            }
        }

        throw lastClientError ?? new InvalidOperationException("Permission response request failed.");
    }

    public async Task<IReadOnlyList<OpencodePendingQuestion>> ListPendingQuestionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var normalizedSessionId = sessionId.Trim();
        var allPending = await GetJsonAsync<JsonElement>("/question", cancellationToken).ConfigureAwait(false);
        var filtered = ParsePendingQuestions(allPending)
            .Where(q => q.SessionId.Equals(normalizedSessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filtered.Count > 0)
        {
            _pendingQuestionsBySession[normalizedSessionId] = filtered;
            return filtered;
        }

        if (_pendingQuestionsBySession.TryGetValue(normalizedSessionId, out var cached))
        {
            return cached;
        }

        return Array.Empty<OpencodePendingQuestion>();
    }

    public bool TryGetPendingQuestionsFromEvents(string sessionId, out IReadOnlyList<OpencodePendingQuestion> pendingQuestions)
    {
        pendingQuestions = Array.Empty<OpencodePendingQuestion>();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return _eventPendingQuestionsBySession.TryGetValue(sessionId.Trim(), out pendingQuestions!);
    }

    public async Task<bool> ReplyToQuestionAsync(string sessionId, string questionId, IReadOnlyList<string> answers, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("questionId is required.", nameof(questionId));
        }

        var questionKey = questionId.Trim();
        var path = $"/question/{Uri.EscapeDataString(questionKey)}/reply";
        var payload = new
        {
            answers = new[]
            {
                (answers ?? Array.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray()
            }
        };

        var raw = await PostJsonRawAsync(path, payload, cancellationToken).ConfigureAwait(false);
        var ok = TryInterpretBooleanResponse(raw, true);
        if (ok)
        {
            var sessionKey = sessionId.Trim();
            if (_pendingQuestionsBySession.TryGetValue(sessionKey, out var existing))
            {
                var remaining = existing
                    .Where(q => !q.Id.Equals(questionKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (remaining.Count == 0)
                {
                    _pendingQuestionsBySession.TryRemove(sessionKey, out _);
                }
                else
                {
                    _pendingQuestionsBySession[sessionKey] = remaining;
                }
            }

            if (_eventPendingQuestionsBySession.TryGetValue(sessionKey, out var eventExisting))
            {
                var eventRemaining = eventExisting
                    .Where(q => !q.Id.Equals(questionKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (eventRemaining.Count == 0)
                {
                    _eventPendingQuestionsBySession.TryRemove(sessionKey, out _);
                }
                else
                {
                    _eventPendingQuestionsBySession[sessionKey] = eventRemaining;
                }
            }
        }

        return ok;
    }

    public async Task<bool> RejectQuestionAsync(string sessionId, string questionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(questionId))
        {
            throw new ArgumentException("questionId is required.", nameof(questionId));
        }

        var questionKey = questionId.Trim();
        var path = $"/question/{Uri.EscapeDataString(questionKey)}/reject";
        var raw = await PostJsonRawAsync(path, new { }, cancellationToken).ConfigureAwait(false);
        var ok = TryInterpretBooleanResponse(raw, true);
        if (ok)
        {
            var sessionKey = sessionId.Trim();
            if (_pendingQuestionsBySession.TryGetValue(sessionKey, out var existing))
            {
                var remaining = existing
                    .Where(q => !q.Id.Equals(questionKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (remaining.Count == 0)
                {
                    _pendingQuestionsBySession.TryRemove(sessionKey, out _);
                }
                else
                {
                    _pendingQuestionsBySession[sessionKey] = remaining;
                }
            }

            if (_eventPendingQuestionsBySession.TryGetValue(sessionKey, out var eventExisting))
            {
                var eventRemaining = eventExisting
                    .Where(q => !q.Id.Equals(questionKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (eventRemaining.Count == 0)
                {
                    _eventPendingQuestionsBySession.TryRemove(sessionKey, out _);
                }
                else
                {
                    _eventPendingQuestionsBySession[sessionKey] = eventRemaining;
                }
            }
        }

        return ok;
    }

    public async Task<IReadOnlyList<OpencodeSessionSummary>> GetSessionChildrenAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var path = $"/session/{Uri.EscapeDataString(sessionId.Trim())}/children";
        var root = await GetJsonAsync<JsonElement>(path, cancellationToken).ConfigureAwait(false);
        return ParseSessionList(root);
    }

    public async Task<OpencodeSessionSummary> UpdateSessionTitleAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("title is required.", nameof(title));
        }

        var path = $"/session/{Uri.EscapeDataString(sessionId.Trim())}";
        var raw = await PatchJsonRawAsync(path, new { title = title.Trim() }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("Opencode returned an empty response while updating the session title.");
        }

        var updated = JsonSerializer.Deserialize<JsonElement>(raw, _jsonOptions);
        if (!TryBuildSessionSummary(updated, sessionId.Trim(), out var summary))
        {
            throw new InvalidOperationException("Opencode returned an unexpected payload while updating the session title.");
        }

        return summary!;
    }

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var path = $"/session/{Uri.EscapeDataString(sessionId.Trim())}";
        var raw = await DeleteRawAsync(path, cancellationToken).ConfigureAwait(false);
        return TryInterpretBooleanResponse(raw, true);
    }

    public async Task<bool> SummarizeSessionAsync(string sessionId, string? providerId, string? modelId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            var provider = providerId.Trim();
            body["providerID"] = provider;
            body["providerId"] = provider;
        }

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var model = modelId.Trim();
            body["modelID"] = model;
            body["modelId"] = model;
        }

        var path = $"/session/{Uri.EscapeDataString(sessionId.Trim())}/summarize";
        var raw = await PostJsonRawAsync(path, body, cancellationToken).ConfigureAwait(false);
        return TryInterpretBooleanResponse(raw, true);
    }

    public async Task<bool> AbortSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required.", nameof(sessionId));
        }

        var path = $"/session/{Uri.EscapeDataString(sessionId.Trim())}/abort";
        var raw = await PostJsonRawAsync(path, new { }, cancellationToken).ConfigureAwait(false);
        return TryInterpretBooleanResponse(raw, true);
    }

    public async Task<IReadOnlyList<OpencodeProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync<JsonElement>("/project", cancellationToken).ConfigureAwait(false);
        return ParseProjectList(root);
    }

    public async Task<OpencodeProjectSummary?> GetCurrentProjectAsync(CancellationToken cancellationToken)
    {
        var root = await GetJsonAsync<JsonElement>("/project/current", cancellationToken).ConfigureAwait(false);
        return TryBuildProjectSummary(root, null, out var project) ? project : null;
    }

    private static IEnumerable<string> EnumerateProviderDefaultModelIds(OpencodeProviderEntry provider)
    {
        var candidates = new[]
        {
            provider.Model,
            provider.ModelId,
            provider.DefaultModel,
            provider.DefaultModelId
        };

        foreach (var value in candidates)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static IReadOnlyList<OpencodeSessionSummary> ParseSessionList(JsonElement root)
    {
        var list = new List<OpencodeSessionSummary>();
        if (root.ValueKind == JsonValueKind.Undefined || root.ValueKind == JsonValueKind.Null)
        {
            return list;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (TryBuildSessionSummary(entry, null, out var session))
                {
                    list.Add(session!);
                }
            }

            return list
                .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        if (TryGetArrayPropertyAny(root, out var wrappedArray, "all", "sessions", "data", "items"))
        {
            return ParseSessionList(wrappedArray);
        }

        foreach (var property in root.EnumerateObject())
        {
            if (TryBuildSessionSummary(property.Value, property.Name, out var session))
            {
                list.Add(session!);
            }
        }

        return list
            .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryBuildSessionSummary(JsonElement element, string? fallbackId, out OpencodeSessionSummary? summary)
    {
        summary = null;
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var rawId = element.GetString();
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return false;
            }

            summary = new OpencodeSessionSummary(rawId.Trim(), rawId.Trim(), null, null);
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = TryGetStringPropertyAny(element, out var parsedId, "id", "sessionID", "sessionId") && !string.IsNullOrWhiteSpace(parsedId)
            ? parsedId!.Trim()
            : fallbackId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var title = TryGetStringPropertyAny(element, out var parsedTitle, "title", "name") && !string.IsNullOrWhiteSpace(parsedTitle)
            ? parsedTitle!.Trim()
            : id;
        var status = TryGetStringPropertyAny(element, out var parsedStatus, "status", "state") && !string.IsNullOrWhiteSpace(parsedStatus)
            ? parsedStatus!.Trim()
            : null;
        var projectId = TryGetStringPropertyAny(element, out var parsedProjectId, "projectID", "projectId", "project") && !string.IsNullOrWhiteSpace(parsedProjectId)
            ? parsedProjectId!.Trim()
            : null;

        summary = new OpencodeSessionSummary(id, title, status, projectId);
        return true;
    }

    private static IReadOnlyList<OpencodeProjectSummary> ParseProjectList(JsonElement root)
    {
        var list = new List<OpencodeProjectSummary>();
        if (root.ValueKind == JsonValueKind.Undefined || root.ValueKind == JsonValueKind.Null)
        {
            return list;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in root.EnumerateArray())
            {
                if (TryBuildProjectSummary(entry, null, out var project))
                {
                    list.Add(project!);
                }
            }

            return list
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        if (TryGetArrayPropertyAny(root, out var wrappedArray, "all", "projects", "data", "items"))
        {
            return ParseProjectList(wrappedArray);
        }

        foreach (var property in root.EnumerateObject())
        {
            if (TryBuildProjectSummary(property.Value, property.Name, out var project))
            {
                list.Add(project!);
            }
        }

        return list
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryBuildProjectSummary(JsonElement element, string? fallbackId, out OpencodeProjectSummary? summary)
    {
        summary = null;
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var rawId = element.GetString();
            if (string.IsNullOrWhiteSpace(rawId))
            {
                return false;
            }

            summary = new OpencodeProjectSummary(rawId.Trim(), rawId.Trim(), null, null);
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = TryGetStringPropertyAny(element, out var parsedId, "id", "projectID", "projectId") && !string.IsNullOrWhiteSpace(parsedId)
            ? parsedId!.Trim()
            : fallbackId?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var name = TryGetStringPropertyAny(element, out var parsedName, "name", "title") && !string.IsNullOrWhiteSpace(parsedName)
            ? parsedName!.Trim()
            : id;
        var path = TryGetStringPropertyAny(element, out var parsedPath, "path", "root", "rootPath") && !string.IsNullOrWhiteSpace(parsedPath)
            ? parsedPath!.Trim()
            : null;
        bool? current = null;
        if (TryGetBooleanPropertyAny(element, out var parsedCurrent, "current", "isCurrent"))
        {
            current = parsedCurrent;
        }

        summary = new OpencodeProjectSummary(id, name, path, current);
        return true;
    }

    private async Task<IReadOnlyList<OpencodeModelSummary>> TryListModelsFromEndpointAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OpencodeHttpException(response.StatusCode, path, raw);
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException($"Opencode returned an empty response for {path}.");
            }

            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '<')
            {
                throw new InvalidOperationException($"Opencode returned non-JSON content for {path}.");
            }

            using var doc = JsonDocument.Parse(raw);
            var found = new List<OpencodeModelSummary>();
            CollectModelsFromElement(doc.RootElement, null, found);
            return found;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse Opencode model payload from {path}: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            var baseAddress = _http.BaseAddress?.ToString() ?? "(unknown)";
            throw new InvalidOperationException($"Cannot reach Opencode at {baseAddress} while requesting {path}: {ex.Message}", ex);
        }
    }

    private static void CollectModelsFromElement(JsonElement element, string? inheritedProvider, List<OpencodeModelSummary> models)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectModelsFromElement(child, inheritedProvider, models);
                }

                return;

            case JsonValueKind.Object:
                if (TryBuildModelSummary(element, inheritedProvider, out var model))
                {
                    AddModelSummary(models, model!);
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    string? nextProvider = inheritedProvider;
                    if ((property.NameEquals("provider") || property.NameEquals("providerId") || property.NameEquals("provider_id"))
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        nextProvider = property.Value.GetString();
                    }

                    // Common /model shape: top-level provider key -> models payload.
                    if (string.IsNullOrWhiteSpace(nextProvider) && IsLikelyProviderPropertyName(property.Name))
                    {
                        nextProvider = property.Name;
                    }

                    if (!string.IsNullOrWhiteSpace(nextProvider)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var leafModelId = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(leafModelId))
                        {
                            AddModelSummary(models, new OpencodeModelSummary(leafModelId.Trim(), leafModelId.Trim(), nextProvider));
                        }

                        continue;
                    }

                    CollectModelsFromElement(property.Value, nextProvider, models);
                }

                return;

            case JsonValueKind.String:
                if (!string.IsNullOrWhiteSpace(inheritedProvider))
                {
                    var leafModelId = element.GetString();
                    if (!string.IsNullOrWhiteSpace(leafModelId))
                    {
                        AddModelSummary(models, new OpencodeModelSummary(leafModelId.Trim(), leafModelId.Trim(), inheritedProvider));
                    }
                }

                return;

            default:
                return;
        }
    }

    private static bool TryBuildModelSummary(JsonElement element, string? inheritedProvider, out OpencodeModelSummary? summary)
    {
        summary = null;
        if (!TryGetStringProperty(element, "id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var idValue = id.Trim();

        var name = TryGetStringProperty(element, "name", out var parsedName) && !string.IsNullOrWhiteSpace(parsedName)
            ? parsedName.Trim()
            : idValue;

        string? provider = null;
        if (TryGetStringProperty(element, "provider", out var parsedProvider) && !string.IsNullOrWhiteSpace(parsedProvider))
        {
            provider = parsedProvider.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(inheritedProvider))
        {
            provider = inheritedProvider;
        }
        else
        {
            var slash = idValue.IndexOf('/');
            if (slash > 0)
            {
                provider = idValue[..slash];
            }
        }

        summary = new OpencodeModelSummary(idValue, name, provider);
        return true;
    }

    private static void AddModelSummary(List<OpencodeModelSummary> models, OpencodeModelSummary candidate)
    {
        var id = candidate.Id.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var provider = string.IsNullOrWhiteSpace(candidate.Provider) ? null : candidate.Provider!.Trim();
        var canonicalId = id.Contains('/') || string.IsNullOrWhiteSpace(provider)
            ? id
            : $"{provider}/{id}";
        var name = string.IsNullOrWhiteSpace(candidate.Name) ? canonicalId : candidate.Name.Trim();

        if (models.Any(m => m.Id.Equals(canonicalId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        models.Add(new OpencodeModelSummary(canonicalId, name, provider));
    }

    private static bool IsLikelyProviderPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var key = propertyName.Trim();
        return !key.Equals("all", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("data", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("items", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("models", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("provider", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("providerId", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("provider_id", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("id", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("name", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> EnsureSessionAsync(string conversationKey, string title, OpencodeSendOptions? options, CancellationToken cancellationToken)
    {
        if (_sessionIds.TryGetValue(conversationKey, out var existing))
        {
            return existing;
        }

        var body = BuildSessionCreateBody(title, options?.ModelId);
        Console.WriteLine($"[opencode] POST /session payload: {JsonSerializer.Serialize(body, _jsonOptions)}");
        var created = await PostJsonAsync<OpencodeSessionInfo>("/session", body, cancellationToken).ConfigureAwait(false);
        if (created == null || string.IsNullOrWhiteSpace(created.Id))
        {
            throw new InvalidOperationException("Opencode server created a session without an ID.");
        }

        return _sessionIds.GetOrAdd(conversationKey, created.Id);
    }

    private async Task<OpencodeChatReply> SendToSessionAsync(string sessionId, string message, OpencodeSendOptions? options, CancellationToken cancellationToken)
    {
        var outboundMessage = BuildOutboundMessage(message, options?.ThinkingLevel, options?.SystemPrompt);
        var body = new Dictionary<string, object?>
        {
            ["parts"] = new[]
            {
                new { type = "text", text = outboundMessage }
            }
        };

        var rawReply = await PostJsonRawAsync($"/session/{sessionId}/message", body, cancellationToken).ConfigureAwait(false);
        var reply = string.IsNullOrWhiteSpace(rawReply)
            ? null
            : JsonSerializer.Deserialize<OpenCodeReply>(rawReply, _jsonOptions);

        var text = ExtractReplyText(reply, rawReply, out var hasUsableText);
        if (TryGetEmbeddedError(rawReply, out var embeddedError))
        {
            if (hasUsableText)
            {
                // Some Opencode backends include non-fatal tool errors alongside final assistant text.
                Console.WriteLine($"[opencode] embedded API warning ({embeddedError.StatusCode?.ToString() ?? "n/a"}): {embeddedError.Message}");
            }
            else
            {
                throw new OpencodeEmbeddedErrorException(embeddedError.Message, embeddedError.StatusCode);
            }
        }

        var isConfirmationPrompt = IsLikelyConfirmationPrompt(text);
        var pendingPermissions = ParsePendingPermissions(rawReply)
            .Where(p => p.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pendingPermissions.Count > 0)
        {
            _pendingPermissionsBySession[sessionId] = pendingPermissions;
        }
        else
        {
            _pendingPermissionsBySession.TryRemove(sessionId, out _);
        }

        var pendingQuestions = ParsePendingQuestions(rawReply)
            .Where(q => q.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pendingQuestions.Count > 0)
        {
            _pendingQuestionsBySession[sessionId] = pendingQuestions;
        }
        else
        {
            _pendingQuestionsBySession.TryRemove(sessionId, out _);
        }

        return new OpencodeChatReply(text, isConfirmationPrompt, pendingPermissions, pendingQuestions);
    }

    private static Dictionary<string, object?> BuildSessionCreateBody(string title, string? configuredModelId)
    {
        var body = new Dictionary<string, object?>
        {
            ["title"] = string.IsNullOrWhiteSpace(title) ? "OpenSim Conversation" : title
        };

        var sessionBody = new Dictionary<string, object?>();
        if (TryParseProviderAndModel(configuredModelId, out var providerId, out var modelLeaf))
        {
            var canonicalModelId = configuredModelId!.Trim();
            sessionBody["providerID"] = providerId;
            sessionBody["providerId"] = providerId;
            sessionBody["modelID"] = canonicalModelId;
            sessionBody["modelId"] = canonicalModelId;
            Console.WriteLine($"[opencode] creating session with body.providerID/body.providerId={providerId} body.modelID/body.modelId={canonicalModelId}");
        }
        else if (!string.IsNullOrWhiteSpace(configuredModelId))
        {
            var normalized = configuredModelId.Trim();
            sessionBody["modelID"] = normalized;
            sessionBody["modelId"] = normalized;
            Console.WriteLine($"[opencode] creating session with body.modelID/body.modelId={normalized}");
        }

        if (sessionBody.Count > 0)
        {
            body["body"] = sessionBody;
        }

        return body;
    }

    private static bool TryParseProviderAndModel(string? configuredModelId, out string providerId, out string modelId)
    {
        providerId = string.Empty;
        modelId = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredModelId))
        {
            return false;
        }

        var normalized = configuredModelId.Trim();
        var slash = normalized.IndexOf('/');
        if (slash <= 0 || slash >= normalized.Length - 1)
        {
            return false;
        }

        providerId = normalized[..slash].Trim();
        modelId = normalized[(slash + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId);
    }

    private static string BuildBaseUrl(string scheme, string host, int port)
    {
        var normalizedScheme = string.IsNullOrWhiteSpace(scheme) ? "http" : scheme.Trim().ToLowerInvariant();
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        return $"{normalizedScheme}://{normalizedHost}:{port}";
    }

    private static string BuildOutboundMessage(string message, string? thinkingLevel, string? systemPrompt)
    {
        var pieces = new List<string>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            pieces.Add("[system instructions]\n" + systemPrompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(thinkingLevel))
        {
            // Not all providers expose a first-class "reasoning effort" API; use a compact instruction prefix.
            pieces.Add($"[reasoning effort: {thinkingLevel.Trim()}]");
        }

        if (pieces.Count == 0)
        {
            return message;
        }

        pieces.Add("[user message]\n" + message);
        return string.Join("\n\n", pieces);
    }

    private string ExtractReplyText(OpenCodeReply? reply, string? rawReply, out bool hasUsableText)
    {
        hasUsableText = false;
        if (reply?.Parts != null && reply.Parts.Count > 0)
        {
            var textParts = reply.Parts
                .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Text))
                .Select(p => p.Text!.Trim())
                .ToArray();

            if (textParts.Length > 0)
            {
                hasUsableText = true;
                return string.Join(Environment.NewLine, textParts);
            }

            var toolParts = reply.Parts
                .Where(p => string.Equals(p.Type, "tool", StringComparison.OrdinalIgnoreCase))
                .Select(p => string.IsNullOrWhiteSpace(p.ToolName) ? "tool" : p.ToolName)
                .ToArray();

            if (toolParts.Length > 0)
            {
                hasUsableText = true;
                return $"(Opencode ran tool actions: {string.Join(", ", toolParts)})";
            }
        }

        var fallbackText = ExtractReplyTextFromRawJson(rawReply);
        if (!string.IsNullOrWhiteSpace(fallbackText))
        {
            hasUsableText = true;
            return fallbackText;
        }

        if (!string.IsNullOrWhiteSpace(rawReply))
        {
            Console.WriteLine($"[opencode] non-text reply payload: {TruncateForLog(rawReply, 500)}");
            return "(Opencode returned a non-text reply payload.)";
        }

        return "(No reply text was returned by Opencode.)";
    }

    private static string? ExtractReplyTextFromRawJson(string? rawReply)
    {
        if (string.IsNullOrWhiteSpace(rawReply))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawReply);
            return ExtractReplyTextFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<OpencodePendingPermission> ParsePendingPermissions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<OpencodePendingPermission>();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ParsePendingPermissions(doc.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<OpencodePendingPermission>();
        }
    }

    private static IReadOnlyList<OpencodePendingPermission> ParsePendingPermissions(JsonElement root)
    {
        var found = new List<OpencodePendingPermission>();
        CollectPendingPermissions(root, BuildInitialPermissionParseContext(root), found);
        return found
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BuildInitialPermissionParseContext(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetStringPropertyAny(root, out var eventType, "type", "event", "name")
            && !string.IsNullOrWhiteSpace(eventType)
            && eventType.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            return eventType.Trim();
        }

        return null;
    }

    private static void CollectPendingPermissions(JsonElement element, string? context, List<OpencodePendingPermission> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectPendingPermissions(child, context, output);
                }

                return;

            case JsonValueKind.Object:
                if (TryBuildPendingPermission(element, context, out var permission))
                {
                    output.Add(permission!);
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (!ShouldTraversePermissionProperty(context, property.Name))
                    {
                        continue;
                    }

                    var nextContext = string.IsNullOrWhiteSpace(context)
                        ? property.Name
                        : context + "." + property.Name;
                    CollectPendingPermissions(property.Value, nextContext, output);
                }

                return;

            default:
                return;
        }
    }

    private static bool ShouldTraversePermissionProperty(string? parentContext, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var key = propertyName.Trim();
        if (key.Contains("permission", StringComparison.OrdinalIgnoreCase)
            || key.Contains("request", StringComparison.OrdinalIgnoreCase)
            || key.Contains("pending", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Common envelope/container keys used by list/event payloads.
        if (key.Equals("data", StringComparison.OrdinalIgnoreCase)
            || key.Equals("item", StringComparison.OrdinalIgnoreCase)
            || key.Equals("items", StringComparison.OrdinalIgnoreCase)
            || key.Equals("payload", StringComparison.OrdinalIgnoreCase)
            || key.Equals("body", StringComparison.OrdinalIgnoreCase)
            || key.Equals("event", StringComparison.OrdinalIgnoreCase)
            || key.Equals("properties", StringComparison.OrdinalIgnoreCase)
            || key.Equals("details", StringComparison.OrdinalIgnoreCase)
            || key.Equals("metadata", StringComparison.OrdinalIgnoreCase)
            || key.Equals("context", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Once already inside a permission context, allow traversal to reach nested session/meta fields.
        return !string.IsNullOrWhiteSpace(parentContext)
            && parentContext.Contains("permission", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBuildPendingPermission(JsonElement element, string? context, out OpencodePendingPermission? permission)
    {
        permission = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var isPermissionContext = !string.IsNullOrWhiteSpace(context)
            && context.Contains("permission", StringComparison.OrdinalIgnoreCase);
        var hasPermissionIdField = TryGetStringPropertyAny(element, out var parsedId, "permissionID", "permissionId") && !string.IsNullOrWhiteSpace(parsedId);
        var hasRequestIdField = TryGetStringPropertyAny(element, out var parsedRequestId, "requestID", "requestId") && !string.IsNullOrWhiteSpace(parsedRequestId);
        var hasCanonicalIdField = hasPermissionIdField || hasRequestIdField;
        var hasGenericId = TryGetStringProperty(element, "id", out var genericId) && !string.IsNullOrWhiteSpace(genericId);
        var hasSessionField = TryGetStringPropertyAny(element, out var parsedSessionField, "sessionID", "sessionId")
            && !string.IsNullOrWhiteSpace(parsedSessionField);
        var hasNestedSessionField = element.TryGetProperty("session", out var nestedSession)
            && nestedSession.ValueKind == JsonValueKind.Object
            && TryGetStringPropertyAny(nestedSession, out _, "id", "sessionID", "sessionId");
        var hasPermissionSignals = element.TryGetProperty("pattern", out _)
            || element.TryGetProperty("permission", out _)
            || element.TryGetProperty("tool", out _)
            || element.TryGetProperty("path", out _)
            || element.TryGetProperty("command", out _)
            || element.TryGetProperty("rule", out _)
            || element.TryGetProperty("action", out _)
            || element.TryGetProperty("remember", out _);
        if (!isPermissionContext && !hasCanonicalIdField)
        {
            // Guard against unrelated objects that happen to have an id field.
            var typeLooksPermission = TryGetStringProperty(element, "type", out var typeValue)
                && !string.IsNullOrWhiteSpace(typeValue)
                && typeValue.Contains("permission", StringComparison.OrdinalIgnoreCase);
            var looksLikePermissionRecord = hasGenericId
                && (hasSessionField || hasNestedSessionField)
                && hasPermissionSignals;
            if (!typeLooksPermission && !looksLikePermissionRecord)
            {
                return false;
            }
        }

        // Prefer explicit request IDs, but fall back to generic id for newer/variant payload shapes.
        // Some event envelopes only expose a generic id in nested `properties` records.
        var id = hasRequestIdField
            ? parsedRequestId!.Trim()
            : hasPermissionIdField
                ? parsedId!.Trim()
                : hasGenericId
                    ? genericId!.Trim()
                    : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var response = TryGetStringProperty(element, "response", out var parsedResponse) && !string.IsNullOrWhiteSpace(parsedResponse)
            ? parsedResponse!.Trim().ToLowerInvariant()
            : null;
        if (response is "once" or "always" or "reject" or "allow" or "deny")
        {
            return false;
        }

        var title = TryGetStringPropertyAny(element, out var parsedTitle, "title", "name", "type") && !string.IsNullOrWhiteSpace(parsedTitle)
            ? parsedTitle!.Trim()
            : id;
        var sessionId = TryGetStringPropertyAny(element, out var parsedSessionId, "sessionID", "sessionId") && !string.IsNullOrWhiteSpace(parsedSessionId)
            ? parsedSessionId!.Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId)
            && element.TryGetProperty("session", out var sessionObject)
            && sessionObject.ValueKind == JsonValueKind.Object
            && TryGetStringPropertyAny(sessionObject, out var nestedParsedSessionId, "id", "sessionID", "sessionId")
            && !string.IsNullOrWhiteSpace(nestedParsedSessionId))
        {
            sessionId = nestedParsedSessionId!.Trim();
        }

        string? description = null;
        if (TryGetStringProperty(element, "pattern", out var parsedPattern) && !string.IsNullOrWhiteSpace(parsedPattern))
        {
            description = parsedPattern.Trim();
        }
        else if (element.TryGetProperty("pattern", out var patternArray) && patternArray.ValueKind == JsonValueKind.Array)
        {
            var parts = patternArray.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!.Trim())
                .ToArray();
            if (parts.Length > 0)
            {
                description = string.Join(", ", parts);
            }
        }
        else if (TryGetStringPropertyAny(element, out var parsedScope, "path", "command", "permission", "tool", "rule")
            && !string.IsNullOrWhiteSpace(parsedScope))
        {
            description = parsedScope!.Trim();
        }

        permission = new OpencodePendingPermission(id, sessionId, title, description);
        return true;
    }

    private static IReadOnlyList<OpencodePendingQuestion> ParsePendingQuestions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<OpencodePendingQuestion>();
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return ParsePendingQuestions(doc.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<OpencodePendingQuestion>();
        }
    }

    private static IReadOnlyList<OpencodePendingQuestion> ParsePendingQuestions(JsonElement root)
    {
        var found = new List<OpencodePendingQuestion>();
        CollectPendingQuestions(root, found);
        return found
            .GroupBy(q => q.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(q => q.Header, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectPendingQuestions(JsonElement element, List<OpencodePendingQuestion> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectPendingQuestions(child, output);
                }

                return;

            case JsonValueKind.Object:
                if (TryBuildPendingQuestion(element, out var question))
                {
                    output.Add(question!);
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectPendingQuestions(property.Value, output);
                }

                return;

            default:
                return;
        }
    }

    private static bool TryBuildPendingQuestion(JsonElement element, out OpencodePendingQuestion? question)
    {
        question = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasRequestShape = TryGetStringProperty(element, "id", out var parsedId)
            && !string.IsNullOrWhiteSpace(parsedId)
            && (TryGetArrayPropertyAny(element, out _, "questions")
                || TryGetStringPropertyAny(element, out _, "question", "header"));
        if (!hasRequestShape)
        {
            return false;
        }

        var id = parsedId!.Trim();
        if (!id.StartsWith("que", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sessionId = TryGetStringPropertyAny(element, out var parsedSessionId, "sessionID", "sessionId") && !string.IsNullOrWhiteSpace(parsedSessionId)
            ? parsedSessionId!.Trim()
            : string.Empty;

        var header = string.Empty;
        var prompt = string.Empty;
        bool? multiple = null;
        bool? custom = null;
        var options = new List<string>();

        if (element.TryGetProperty("questions", out var questions) && questions.ValueKind == JsonValueKind.Array)
        {
            var first = questions.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                header = TryGetStringProperty(first, "header", out var parsedHeader) && !string.IsNullOrWhiteSpace(parsedHeader)
                    ? parsedHeader!.Trim()
                    : id;
                prompt = TryGetStringProperty(first, "question", out var parsedQuestion) && !string.IsNullOrWhiteSpace(parsedQuestion)
                    ? parsedQuestion!.Trim()
                    : header;
                if (first.TryGetProperty("multiple", out var multipleElement)
                    && multipleElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    multiple = multipleElement.GetBoolean();
                }

                if (first.TryGetProperty("custom", out var customElement)
                    && customElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    custom = customElement.GetBoolean();
                }

                if (first.TryGetProperty("options", out var optionElements) && optionElements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionElements.EnumerateArray())
                    {
                        if (TryGetStringProperty(option, "label", out var label) && !string.IsNullOrWhiteSpace(label))
                        {
                            options.Add(label.Trim());
                        }
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(header))
        {
            header = id;
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            prompt = header;
        }

        question = new OpencodePendingQuestion(id, sessionId, header, prompt, options, multiple, custom);
        return true;
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildPermissionResponsePayloads(string normalizedResponse, bool remember)
    {
        var payloads = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string reply)
        {
            var payload = new Dictionary<string, object?>
            {
                ["reply"] = reply
            };
            if (remember)
            {
                payload["message"] = "remember";
            }

            var serialized = JsonSerializer.Serialize(payload);
            if (seen.Add(serialized))
            {
                payloads.Add(payload);
            }
        }

        if (normalizedResponse is "allow" or "approve" or "accept" or "yes" or "y")
        {
            Add("once");
            Add("always");
            return payloads;
        }

        if (normalizedResponse is "reject" or "deny" or "no" or "n")
        {
            Add("reject");
            return payloads;
        }

        Add(normalizedResponse);
        return payloads;
    }

    private static bool TryGetEmbeddedError(string? rawReply, out EmbeddedOpencodeError error)
    {
        error = default;
        if (string.IsNullOrWhiteSpace(rawReply))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawReply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object
                || !info.TryGetProperty("error", out var errorElement)
                || errorElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? message = null;
            int? statusCode = null;

            if (TryGetStringProperty(errorElement, "message", out var directMessage)
                && !string.IsNullOrWhiteSpace(directMessage))
            {
                message = directMessage.Trim();
            }

            if (errorElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
            {
                if (TryGetStringProperty(dataElement, "message", out var dataMessage)
                    && !string.IsNullOrWhiteSpace(dataMessage))
                {
                    message = dataMessage.Trim();
                }

                if (dataElement.TryGetProperty("statusCode", out var statusElement)
                    && statusElement.ValueKind == JsonValueKind.Number
                    && statusElement.TryGetInt32(out var parsedStatus))
                {
                    statusCode = parsedStatus;
                }
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                message = "Opencode returned an embedded API error.";
            }

            error = new EmbeddedOpencodeError(message, statusCode);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractReplyTextFromElement(JsonElement element)
    {
        // Direct content string shape: { "content": "..." }
        if (TryGetStringProperty(element, "content", out var directContent) && !string.IsNullOrWhiteSpace(directContent))
        {
            return directContent.Trim();
        }

        // Common message-wrapper shape: { "message": { ... } }
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.Object)
        {
            var nestedMessage = ExtractReplyTextFromElement(messageElement);
            if (!string.IsNullOrWhiteSpace(nestedMessage))
            {
                return nestedMessage;
            }
        }

        // SDK shape: { "parts": [{ "type":"text", "text":"..." }] }
        if (TryExtractTextFromParts(element, out var partsText))
        {
            return partsText;
        }

        // History shape: { "messages": [{ "role":"assistant", "content":"..." }] }
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            for (var i = messages.GetArrayLength() - 1; i >= 0; i--)
            {
                var message = messages[i];
                if (TryGetStringProperty(message, "role", out var role)
                    && !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nested = ExtractReplyTextFromElement(message);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool TryExtractTextFromParts(JsonElement element, out string? text)
    {
        text = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var textParts = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            if (TryGetStringProperty(part, "type", out var type)
                && string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                && TryGetStringProperty(part, "text", out var partText)
                && !string.IsNullOrWhiteSpace(partText))
            {
                textParts.Add(partText.Trim());
            }
        }

        if (textParts.Count == 0)
        {
            return false;
        }

        text = string.Join(Environment.NewLine, textParts);
        return true;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryGetStringPropertyAny(JsonElement element, out string? value, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (TryGetStringProperty(element, name, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetArrayPropertyAny(JsonElement element, out JsonElement value, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Array)
            {
                value = property;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetBooleanPropertyAny(JsonElement element, out bool value, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var property)
                && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.GetBoolean();
                return true;
            }
        }

        value = false;
        return false;
    }

    private static bool TryInterpretBooleanResponse(string? raw, bool defaultWhenEmpty)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultWhenEmpty;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return root.GetBoolean();
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("ok", out var okElement)
                    && okElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return okElement.GetBoolean();
                }

                if (root.TryGetProperty("success", out var successElement)
                    && successElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return successElement.GetBoolean();
                }
            }

            return defaultWhenEmpty;
        }
        catch (JsonException)
        {
            return defaultWhenEmpty;
        }
    }

    private static string JsonElementToSingleLine(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        return element.GetRawText();
    }

    private static bool IsLikelyConfirmationPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("yes/no", StringComparison.Ordinal)
            || normalized.Contains("y/n", StringComparison.Ordinal)
            || (normalized.Contains("confirm", StringComparison.Ordinal) && normalized.Contains('?'));
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private async Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        var raw = await PostJsonRawAsync(path, body, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(raw, _jsonOptions);
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpencodeHttpException(response.StatusCode, path, raw);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(raw, _jsonOptions);
    }

    private async Task<string> PostJsonRawAsync(string path, object body, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpencodeHttpException(response.StatusCode, path, raw);
        }

        return raw;
    }

    private async Task<string> PutJsonRawAsync(string path, object body, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync(path, content, cancellationToken).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpencodeHttpException(response.StatusCode, path, raw);
        }

        return raw;
    }

    private async Task<string> PatchJsonRawAsync(string path, object body, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = content
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpencodeHttpException(response.StatusCode, path, raw);
        }

        return raw;
    }

    private async Task<string> DeleteRawAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new OpencodeHttpException(response.StatusCode, path, raw);
        }

        return raw;
    }

    public void Dispose()
    {
        if (_eventLoopCts != null)
        {
            try
            {
                _eventLoopCts.Cancel();
            }
            catch
            {
                // Ignore shutdown cancellation races.
            }
        }

        if (_eventLoopTask != null)
        {
            try
            {
                _eventLoopTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Normal on shutdown.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[opencode:event] shutdown warning: {ex.Message}");
            }
        }

        _eventLoopCts?.Dispose();
        _eventHttp?.Dispose();
        _http.Dispose();
    }

    private async Task<bool> WaitForProviderConfiguredAsync(string providerId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsProviderConfiguredAsync(providerId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> IsProviderConfiguredAsync(string providerId, CancellationToken cancellationToken)
    {
        var available = await ListAvailableProvidersAsync(cancellationToken).ConfigureAwait(false);
        var availableMatch = available.FirstOrDefault(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (availableMatch != null && availableMatch.Connected == true)
        {
            return true;
        }

        var configured = await ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (configured.Any(p => p.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var models = await ListModelsAsync(providerId, cancellationToken).ConfigureAwait(false);
        return models.Count > 0;
    }

    private static string BuildOAuthStateKey(string providerId, int methodIndex)
    {
        return $"{providerId.Trim()}::{methodIndex}";
    }

    private static OpencodeOAuthPendingState ParseOAuthPendingState(
        string providerId,
        int methodIndex,
        string raw,
        OpencodeOAuthAuthorizeResponse parsed)
    {
        string? code = null;
        string? userCode = null;
        string? deviceCode = null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (TryGetStringProperty(root, "code", out var c) && !string.IsNullOrWhiteSpace(c))
            {
                code = c;
            }

            if (TryGetStringProperty(root, "userCode", out var uc) && !string.IsNullOrWhiteSpace(uc))
            {
                userCode = uc;
            }
            else if (TryGetStringProperty(root, "user_code", out var uc2) && !string.IsNullOrWhiteSpace(uc2))
            {
                userCode = uc2;
            }

            if (TryGetStringProperty(root, "deviceCode", out var dc) && !string.IsNullOrWhiteSpace(dc))
            {
                deviceCode = dc;
            }
            else if (TryGetStringProperty(root, "device_code", out var dc2) && !string.IsNullOrWhiteSpace(dc2))
            {
                deviceCode = dc2;
            }
        }
        catch (JsonException)
        {
            // Best effort only; OAuth can still continue without extracted fields.
        }

        return new OpencodeOAuthPendingState(
            providerId,
            methodIndex,
            parsed.Url,
            parsed.Method,
            parsed.Instructions,
            code,
            userCode,
            deviceCode);
    }

    private static IReadOnlyList<Dictionary<string, object?>> BuildOAuthCallbackPayloads(
        int methodIndex,
        string? explicitCode,
        OpencodeOAuthPendingState? pending)
    {
        var payloads = new List<Dictionary<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddPayload(Dictionary<string, object?> payload)
        {
            var serialized = JsonSerializer.Serialize(payload);
            if (seen.Add(serialized))
            {
                payloads.Add(payload);
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitCode))
        {
            AddPayload(new Dictionary<string, object?>
            {
                ["method"] = methodIndex,
                ["code"] = explicitCode.Trim()
            });
        }

        var candidateCodes = new List<string>();
        if (!string.IsNullOrWhiteSpace(pending?.Code))
        {
            candidateCodes.Add(pending.Code!);
        }

        if (!string.IsNullOrWhiteSpace(pending?.DeviceCode))
        {
            candidateCodes.Add(pending.DeviceCode!);
        }

        if (!string.IsNullOrWhiteSpace(pending?.UserCode))
        {
            candidateCodes.Add(pending.UserCode!);
        }

        foreach (var candidate in candidateCodes.Distinct(StringComparer.Ordinal))
        {
            AddPayload(new Dictionary<string, object?>
            {
                ["method"] = methodIndex,
                ["code"] = candidate
            });

            AddPayload(new Dictionary<string, object?>
            {
                ["method"] = methodIndex,
                ["deviceCode"] = candidate
            });

            AddPayload(new Dictionary<string, object?>
            {
                ["method"] = methodIndex,
                ["device_code"] = candidate
            });
        }

        // Final fallback for callback endpoints that track state server-side.
        AddPayload(new Dictionary<string, object?>
        {
            ["method"] = methodIndex
        });

        return payloads;
    }

    private static string? NormalizeProviderQuery(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return providerId.Trim().TrimEnd('.', ',', ';', ':');
    }
}

internal sealed class OpencodeProvidersResponse
{
    public List<OpencodeProviderEntry>? Providers { get; set; }
}

internal sealed class OpencodeAllProvidersResponse
{
    public List<OpencodeProviderEntry>? All { get; set; }
}

internal sealed class OpencodeProviderEntry
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool? Connected { get; set; }
    public string? Model { get; set; }
    public string? ModelId { get; set; }
    public string? DefaultModel { get; set; }
    public string? DefaultModelId { get; set; }
    public Dictionary<string, OpencodeModelEntry>? Models { get; set; }
}

internal sealed class OpencodeModelEntry
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

internal sealed class OpencodeAllModelsResponse
{
    public List<OpencodeModelCatalogEntry>? All { get; set; }
}

internal sealed class OpencodeModelCatalogEntry
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Provider { get; set; }
}

internal sealed class OpencodeOAuthAuthorizeResponse
{
    public string? Url { get; set; }
    public string? Method { get; set; }
    public string? Instructions { get; set; }
}

internal sealed class OpencodeRuntimeConfig
{
    public string? Model { get; set; }
}

internal sealed record OpencodeOAuthPendingState(
    string ProviderId,
    int MethodIndex,
    string? Url,
    string? Method,
    string? Instructions,
    string? Code,
    string? UserCode,
    string? DeviceCode);

internal sealed record OpencodeSessionInfo(string Id);

internal sealed class OpencodeHttpException : Exception
{
    public OpencodeHttpException(HttpStatusCode statusCode, string path, string body)
        : base($"Opencode request failed ({(int)statusCode} {statusCode}) on {path}: {Truncate(body, 300)}")
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}

internal sealed class OpencodeEmbeddedErrorException : Exception
{
    public OpencodeEmbeddedErrorException(string message, int? statusCode)
        : base($"Opencode embedded API error{(statusCode.HasValue ? $" ({statusCode.Value})" : string.Empty)}: {message}")
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }

    // A 400-level embedded error often means corrupted per-session provider state; retry once on a fresh session.
    public bool ShouldResetSession => StatusCode is >= 400 and < 500;
}

internal readonly record struct EmbeddedOpencodeError(string Message, int? StatusCode);