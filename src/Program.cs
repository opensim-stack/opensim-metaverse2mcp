using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using System.Collections;
using System.Text.Json;
using Opensim.Metaverse2Mcp;

var collectorExitCode = await ThreadDumpCollector.TryRunAsync(args).ConfigureAwait(false);
if (collectorExitCode.HasValue)
{
    return collectorExitCode.Value;
}

var (options, startupExitCode) = LoadOptions(args);
if (options == null)
{
    return startupExitCode;
}

using var botSession = new BotSession(options);
using var loginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.BotLoginTimeoutSeconds));
var toolAccessPolicy = McpToolAccessPolicy.FromEnvironment();

try
{
    var connected = await botSession.ConnectAsync(loginTimeout.Token).ConfigureAwait(false);
    if (!connected)
    {
        Console.Error.WriteLine("Bot login failed: " + botSession.LastLoginMessage);
        return 1;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine($"Bot login timed out after {options.BotLoginTimeoutSeconds} seconds.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Bot startup failed: " + ex.Message);
    return 1;
}

var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.UseUrls($"http://{options.McpHost}:{options.McpPort}");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(botSession);
builder.Services.AddSingleton<SpawnerClient>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(_ => { })
    .WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(next => async ValueTask<ListToolsResult> (request, cancellationToken) =>
        {
            if (request == null)
            {
                throw new McpException("Invalid tools/list request.");
            }

            var result = await next(request, cancellationToken).ConfigureAwait(false);
            if (result == null)
            {
                throw new McpException("Tools/list request returned no result.");
            }

            if (toolAccessPolicy.IsAllowAll)
            {
                return result;
            }

            FilterListToolsResultInPlace(result, toolAccessPolicy);
            return result;
        });

        filters.AddCallToolFilter(next => async ValueTask<CallToolResult> (request, cancellationToken) =>
        {
            try
            {
                if (request == null)
                {
                    throw new McpException("Invalid tools/call request.");
                }

                var toolName = request.Params?.Name;
                if (!toolAccessPolicy.IsAllowed(toolName))
                {
                    throw new McpException($"Tool '{toolName ?? "<unknown>"}' is disabled by MCP tool policy.");
                }

                return await next(request!, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                throw new McpException(BuildCallToolArgumentErrorMessage(request?.Params?.Name, request?.Params?.Arguments, ex.Message), ex);
            }
            catch (Exception ex)
            {
                throw new McpException($"Tool '{request?.Params?.Name ?? "<unknown>"}' failed: {ex.Message}", ex);
            }
        });
    })
    .WithTools<BotMcpTools>()
    .WithTools<RLVMcpTools>()
    .WithPrompts<BotMcpPrompts>();

if (!toolAccessPolicy.IsAllowAll)
{
    Console.WriteLine($"[mcp] tool policy active: allow={toolAccessPolicy.AllowLabel} deny={toolAccessPolicy.DenyLabel}");
}

var app = builder.Build();

var endpointPrefix = options.McpHttpEndpoint;
if (options.McpHttpDisallowDelete || !string.IsNullOrWhiteSpace(options.McpHttpBearerToken))
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments(endpointPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (options.McpHttpDisallowDelete && HttpMethods.IsDelete(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            await context.Response.WriteAsJsonAsync(new { error = "DELETE not allowed" }).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.McpHttpBearerToken))
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (!IsAuthorizedBearer(auth, options.McpHttpBearerToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" }).ConfigureAwait(false);
                return;
            }
        }

        await next().ConfigureAwait(false);
    });
}

app.MapGet("/healthz", () => Results.Ok(new
{
    ok = true,
    transport = options.UseLegacySseCompatibility ? "sse-compat" : "http",
    endpoint = options.McpHttpEndpoint,
    bot = botSession.GetStatus()
}));

app.MapMcp(options.McpHttpEndpoint);

Console.WriteLine($"MCP streamable endpoint ready at http://{options.McpHost}:{options.McpPort}{options.McpHttpEndpoint}");
if (options.UseLegacySseCompatibility)
{
    Console.WriteLine("MCP_TRANSPORT=sse was provided; using streamable HTTP transport.");
}
if (options.McpDiagnostics)
{
    Console.WriteLine($"[diag] loginUri={options.BotLoginUri} start={options.BotStartLocation} endpoint={options.McpHttpEndpoint}");
}

await app.RunAsync().ConfigureAwait(false);
return 0;

static (AppOptions? Options, int ExitCode) LoadOptions(string[] args)
{
    try
    {
        var options = ConfigLoader.Load(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(ConfigLoader.BuildUsage());
            return (null, 0);
        }

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine("Config error: " + error);
            }

            Console.Error.WriteLine();
            Console.Error.WriteLine(ConfigLoader.BuildUsage());
            return (null, 1);
        }

        return (options, 0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Argument/config error: " + ex.Message);
        Console.Error.WriteLine();
        Console.Error.WriteLine(ConfigLoader.BuildUsage());
        return (null, 1);
    }
}

static bool IsAuthorizedBearer(string? authorizationHeader, string expectedToken)
{
    if (string.IsNullOrWhiteSpace(expectedToken))
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(authorizationHeader))
    {
        return false;
    }

    const string prefix = "Bearer ";
    if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var token = authorizationHeader[prefix.Length..].Trim();
    return string.Equals(token, expectedToken, StringComparison.Ordinal);
}

static string BuildCallToolArgumentErrorMessage(
    string? toolName,
    IDictionary<string, JsonElement>? arguments,
    string? originalMessage)
{
    var normalizedToolName = string.IsNullOrWhiteSpace(toolName) ? "(unknown)" : toolName.Trim();
    var normalizedCause = string.IsNullOrWhiteSpace(originalMessage) ? "Invalid tool arguments." : originalMessage.Trim();
    var providedKeys = arguments == null || arguments.Count == 0
        ? "(none)"
        : string.Join(", ", arguments.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase));

    return $"Tool '{normalizedToolName}' argument error: {normalizedCause} Provided arguments: {providedKeys}. Check tools/list for required and optional parameter names.";
}

static void FilterListToolsResultInPlace(ListToolsResult result, McpToolAccessPolicy policy)
{
    var toolsProperty = result.GetType().GetProperty("Tools");
    var toolsValue = toolsProperty?.GetValue(result);
    if (toolsValue is not IEnumerable toolsEnumerable)
    {
        return;
    }

    var filtered = new List<object?>();
    foreach (var tool in toolsEnumerable)
    {
        if (tool == null)
        {
            continue;
        }

        var nameProperty = tool.GetType().GetProperty("Name");
        var nameValue = nameProperty?.GetValue(tool) as string;
        if (policy.IsAllowed(nameValue))
        {
            filtered.Add(tool);
        }
    }

    if (toolsProperty?.CanWrite == true)
    {
        toolsProperty.SetValue(result, filtered);
        return;
    }

    if (toolsValue is IList toolsList)
    {
        toolsList.Clear();
        foreach (var tool in filtered)
        {
            toolsList.Add(tool);
        }
    }
}

internal sealed class McpToolAccessPolicy
{
    private readonly HashSet<string>? _allow;
    private readonly HashSet<string>? _deny;

    private McpToolAccessPolicy(HashSet<string>? allow, HashSet<string>? deny)
    {
        _allow = allow;
        _deny = deny;
    }

    public bool IsAllowAll => _allow == null && _deny == null;

    public string AllowLabel => _allow == null ? "*" : string.Join(',', _allow.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

    public string DenyLabel => _deny == null ? "(none)" : string.Join(',', _deny.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

    public static McpToolAccessPolicy FromEnvironment()
    {
        var allow = ParseToolList(Environment.GetEnvironmentVariable("MCP_ALLOW_TOOL"));
        var deny = ParseToolList(Environment.GetEnvironmentVariable("MCP_DENY_TOOL"));
        return new McpToolAccessPolicy(allow, deny);
    }

    public bool IsAllowed(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var normalized = toolName.Trim();
        var allowMatch = _allow == null || _allow.Contains(normalized);
        if (!allowMatch)
        {
            return false;
        }

        return _deny == null || !_deny.Contains(normalized);
    }

    private static HashSet<string>? ParseToolList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var values = raw
            .Split([',', ';', '|', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return values.Count == 0 ? null : values;
    }
}
