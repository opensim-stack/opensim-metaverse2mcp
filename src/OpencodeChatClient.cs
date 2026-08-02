using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NOpenCode;

namespace Opensim.Metaverse2Mcp;

internal interface IOpencodeChatClient
{
    Task<OpencodeChatReply> SendMessageAsync(string conversationKey, string title, string message, CancellationToken cancellationToken);
}

internal sealed record OpencodeChatReply(string Text, bool IsConfirmationPrompt);

internal sealed class OpencodeChatClient : IOpencodeChatClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
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
    }

    public async Task<OpencodeChatReply> SendMessageAsync(string conversationKey, string title, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            throw new ArgumentException("conversationKey is required.", nameof(conversationKey));
        }

        var sessionId = await EnsureSessionAsync(conversationKey, title, cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendToSessionAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (OpencodeHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Session may have expired on server restart; create a new one and retry once.
            _sessionIds.TryRemove(conversationKey, out _);
            sessionId = await EnsureSessionAsync(conversationKey, title, cancellationToken).ConfigureAwait(false);
            return await SendToSessionAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
        }
        catch (OpencodeEmbeddedErrorException ex) when (ex.ShouldResetSession)
        {
            // Some providers return API errors inside a 200 response; rebuild the session and retry once.
            _sessionIds.TryRemove(conversationKey, out _);
            sessionId = await EnsureSessionAsync(conversationKey, title, cancellationToken).ConfigureAwait(false);
            return await SendToSessionAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> EnsureSessionAsync(string conversationKey, string title, CancellationToken cancellationToken)
    {
        if (_sessionIds.TryGetValue(conversationKey, out var existing))
        {
            return existing;
        }

        var body = new { title = string.IsNullOrWhiteSpace(title) ? "OpenSim Conversation" : title };
        var created = await PostJsonAsync<OpencodeSessionInfo>("/session", body, cancellationToken).ConfigureAwait(false);
        if (created == null || string.IsNullOrWhiteSpace(created.Id))
        {
            throw new InvalidOperationException("Opencode server created a session without an ID.");
        }

        return _sessionIds.GetOrAdd(conversationKey, created.Id);
    }

    private async Task<OpencodeChatReply> SendToSessionAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        var body = new
        {
            parts = new[]
            {
                new { type = "text", text = message }
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

    private static string BuildBaseUrl(string scheme, string host, int port)
    {
        var normalizedScheme = string.IsNullOrWhiteSpace(scheme) ? "http" : scheme.Trim().ToLowerInvariant();
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        return $"{normalizedScheme}://{normalizedHost}:{port}";
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

    public void Dispose()
    {
        _http.Dispose();
    }
}

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