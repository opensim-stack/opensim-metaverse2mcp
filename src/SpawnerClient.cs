using System.Net.Http.Headers;
using System.Text;

namespace Opensim.Metaverse2Mcp;

internal sealed class SpawnerClient : IDisposable
{
    private const string BotApiBasePath = "/api/bot";

    private readonly HttpClient _http;
    private readonly string? _token;

    public SpawnerClient(AppOptions options)
    {
        var baseUrl = BuildBaseUrl(options.SpawnerHost, options.SpawnerPort);
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _token = string.IsNullOrWhiteSpace(options.SpawnerToken) ? null : options.SpawnerToken.Trim();
    }

    public Task<DataToolResult> ListBotsAsync(CancellationToken cancellationToken)
    {
        var request = CreateRequest(HttpMethod.Get, BotApiBasePath);
        return SendAsync(request, "Listed bots from spawner.", cancellationToken);
    }

    public Task<DataToolResult> GetBotAsync(string first, string last, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return Task.FromResult(DataToolResult.FailResult("first and last are required."));
        }

        var path = $"{BotApiBasePath}/{Uri.EscapeDataString(first.Trim())}/{Uri.EscapeDataString(last.Trim())}";
        var request = CreateRequest(HttpMethod.Get, path);
        return SendAsync(request, $"Fetched bot status for {first.Trim()} {last.Trim()}.", cancellationToken);
    }

    public Task<DataToolResult> CreateBotAsync(
        string first,
        string last,
        string level,
        string? parent,
        string? email,
        string? model,
        string? appearance,
        string? gender,
        string? opencodeInitialProvider,
        string? opencodeInitialModel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return Task.FromResult(DataToolResult.FailResult("first and last are required."));
        }

        if (string.IsNullOrWhiteSpace(level))
        {
            return Task.FromResult(DataToolResult.FailResult("level is required."));
        }

        var path = $"{BotApiBasePath}/{Uri.EscapeDataString(first.Trim())}/{Uri.EscapeDataString(last.Trim())}";
        var request = CreateRequest(HttpMethod.Post, path);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["level"] = level.Trim().ToUpperInvariant()
        };

        if (parent != null)
        {
            form["parent"] = parent;
        }

        if (email != null)
        {
            form["email"] = email;
        }

        if (model != null)
        {
            form["model"] = model;
        }

        if (!string.IsNullOrWhiteSpace(appearance))
        {
            form["appearance"] = appearance.Trim();
        }

        if (!string.IsNullOrWhiteSpace(gender))
        {
            form["gender"] = gender.Trim();
        }

        if (!string.IsNullOrWhiteSpace(opencodeInitialProvider))
        {
            form["OPENCODE_INITIAL_PROVIDER"] = opencodeInitialProvider.Trim();
        }

        if (!string.IsNullOrWhiteSpace(opencodeInitialModel))
        {
            form["OPENCODE_INITIAL_MODEL"] = opencodeInitialModel.Trim();
        }

        request.Content = new FormUrlEncodedContent(form);
        return SendAsync(request, $"Created bot {first.Trim()} {last.Trim()} via spawner.", cancellationToken);
    }

    public Task<DataToolResult> DeleteBotAsync(string first, string last, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return Task.FromResult(DataToolResult.FailResult("first and last are required."));
        }

        var path = $"{BotApiBasePath}/{Uri.EscapeDataString(first.Trim())}/{Uri.EscapeDataString(last.Trim())}";
        var request = CreateRequest(HttpMethod.Delete, path);
        return SendAsync(request, $"Deleted bot {first.Trim()} {last.Trim()} via spawner.", cancellationToken);
    }

    public Task<DataToolResult> PatchBotAsync(string first, string last, string action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return Task.FromResult(DataToolResult.FailResult("first and last are required."));
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            return Task.FromResult(DataToolResult.FailResult("action is required."));
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        if (normalizedAction is not ("start" or "stop" or "restart"))
        {
            return Task.FromResult(DataToolResult.FailResult("action must be one of: start, stop, restart."));
        }

        var path = $"{BotApiBasePath}/{Uri.EscapeDataString(first.Trim())}/{Uri.EscapeDataString(last.Trim())}";
        var request = CreateRequest(HttpMethod.Patch, path);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action"] = normalizedAction
        });
        return SendAsync(request, $"Sent '{normalizedAction}' action for bot {first.Trim()} {last.Trim()} via spawner.", cancellationToken);
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/json");
        return request;
    }

    private async Task<DataToolResult> SendAsync(HttpRequestMessage request, string successMessage, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = string.IsNullOrWhiteSpace(body) ? "{}" : body;

            if (response.IsSuccessStatusCode)
            {
                return DataToolResult.OkResult(successMessage, payload);
            }

            var detail = string.IsNullOrWhiteSpace(body) ? "<empty>" : TrimForMessage(body, 240);
            return DataToolResult.FailResult($"Spawner API {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }
        catch (Exception ex)
        {
            return DataToolResult.FailResult($"Spawner API request failed: {ex.Message}");
        }
    }

    private static string BuildBaseUrl(string? host, int port)
    {
        var safeHost = string.IsNullOrWhiteSpace(host) ? "opensim-spawner" : host.Trim();
        return $"http://{safeHost}:{port}";
    }

    private static string TrimForMessage(string value, int maxChars)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxChars), "...");
    }
}
