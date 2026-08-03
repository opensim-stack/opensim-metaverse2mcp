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
    string? GetConversationSessionId(string conversationKey);
    Task<IReadOnlyList<OpencodeProviderSummary>> ListProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeProviderSummary>> ListAvailableProvidersAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, IReadOnlyList<OpencodeProviderAuthMethod>>> ListProviderAuthMethodsAsync(CancellationToken cancellationToken);
    Task SetProviderApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken);
    Task<OpencodeOAuthStartResult> StartProviderOAuthAsync(string providerId, int methodIndex, IReadOnlyDictionary<string, string>? inputs, CancellationToken cancellationToken);
    Task CompleteProviderOAuthAsync(string providerId, int methodIndex, string? code, CancellationToken cancellationToken);
    Task<IReadOnlyList<OpencodeModelSummary>> ListModelsAsync(string? providerId, CancellationToken cancellationToken);
}

internal sealed record OpencodeChatReply(string Text, bool IsConfirmationPrompt);
internal sealed record OpencodeSendOptions(string? ModelId, string? ThinkingLevel);
internal sealed record OpencodeProviderSummary(string Id, string Name, bool? Connected);
internal sealed record OpencodeModelSummary(string Id, string Name, string? Provider);
internal sealed record OpencodeProviderAuthMethod(int MethodIndex, string Type, string Label);
internal sealed record OpencodeOAuthStartResult(string Url, string? Method, string? Instructions);

internal sealed class OpencodeChatClient : IOpencodeChatClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpencodeOAuthPendingState> _oauthPendingStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _modelOverrideGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public OpencodeChatClient(AppOptions options)
    {
        var baseUrl = BuildBaseUrl(options.OpencodeScheme, options.OpencodeHost, options.OpencodePort);
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

        Console.WriteLine("[opencode] model payload strategy: session-only (no per-message model override)");
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

    public async Task CompleteProviderOAuthAsync(string providerId, int methodIndex, string? code, CancellationToken cancellationToken)
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
        var configured = await WaitForProviderConfiguredAsync(providerId, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        if (!configured)
        {
            throw new InvalidOperationException(
                $"OAuth callback was accepted, but provider '{providerId}' is still not configured. " +
                "If this is a device flow, ensure approval completed in your browser and retry *auth ... oauth-complete.");
        }

        _oauthPendingStates.TryRemove(stateKey, out _);
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
        var outboundMessage = BuildOutboundMessage(message, options?.ThinkingLevel);
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
        return new OpencodeChatReply(text, isConfirmationPrompt);
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

    private static string BuildOutboundMessage(string message, string? thinkingLevel)
    {
        if (string.IsNullOrWhiteSpace(thinkingLevel))
        {
            return message;
        }

        // Not all providers expose a first-class "reasoning effort" API; use a compact instruction prefix.
        return $"[reasoning effort: {thinkingLevel.Trim()}]\n{message}";
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

    public void Dispose()
    {
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