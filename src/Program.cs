using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Opensim.Metaverse2Mcp;

var (options, startupExitCode) = LoadOptions(args);
if (options == null)
{
    return startupExitCode;
}

using var botSession = new BotSession(options);
using var loginTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.BotLoginTimeoutSeconds));

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

builder.Services
    .AddMcpServer()
    .WithHttpTransport(_ => { })
    .WithTools<BotMcpTools>();

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
