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

        var reply = await PostJsonAsync<OpenCodeReply>($"/session/{sessionId}/message", body, cancellationToken).ConfigureAwait(false);
        var text = ExtractReplyText(reply);
        var isConfirmationPrompt = IsLikelyConfirmationPrompt(text);

        return new OpencodeChatReply(text, isConfirmationPrompt);
    }

    private static string BuildBaseUrl(string scheme, string host, int port)
    {
        var normalizedScheme = string.IsNullOrWhiteSpace(scheme) ? "http" : scheme.Trim().ToLowerInvariant();
        var normalizedHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        return $"{normalizedScheme}://{normalizedHost}:{port}";
    }

    private string ExtractReplyText(OpenCodeReply? reply)
    {
        if (reply?.Parts == null || reply.Parts.Count == 0)
        {
            return "(No reply text was returned by Opencode.)";
        }

        var textParts = reply.Parts
            .Where(p => string.Equals(p.Type, "text", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text!.Trim())
            .ToArray();

        if (textParts.Length > 0)
        {
            return string.Join(Environment.NewLine, textParts);
        }

        var toolParts = reply.Parts
            .Where(p => string.Equals(p.Type, "tool", StringComparison.OrdinalIgnoreCase))
            .Select(p => string.IsNullOrWhiteSpace(p.ToolName) ? "tool" : p.ToolName)
            .ToArray();

        return toolParts.Length > 0
            ? $"(Opencode ran tool actions: {string.Join(", ", toolParts)})"
            : "(Opencode returned a non-text reply.)";
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

    private async Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(body, _jsonOptions);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);

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
