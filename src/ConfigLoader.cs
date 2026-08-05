namespace Opensim.Metaverse2Mcp;

internal static class ConfigLoader
{
    public static AppOptions Load(string[] args)
    {
        var options = new AppOptions
        {
            McpTransport = Env("MCP_TRANSPORT") ?? "http",
            McpHost = Env("MCP_HOST") ?? "0.0.0.0",
            McpHttpEndpoint = Env("MCP_HTTP_ENDPOINT") ?? "/mcp",
            McpHttpBearerToken = Env("MCP_HTTP_BEARER_TOKEN"),
            McpDiagnostics = ParseBool(Env("MCP_DIAGNOSTICS"), false),
            McpHttpDisallowDelete = ParseBool(Env("MCP_HTTP_DISALLOW_DELETE"), false),
            InventoryOfferPolicyFile = FirstDefined("INVENTORY_OFFER_POLICY_FILE", "OPENSIM_INVENTORY_OFFER_POLICY_FILE"),
            InventoryOfferPolicyAutoSave = ParseBool(FirstDefined("INVENTORY_OFFER_POLICY_AUTOSAVE", "OPENSIM_INVENTORY_OFFER_POLICY_AUTOSAVE"), true),
            BotFirstName = FirstDefined("OPENSIM_LOGIN_FIRSTNAME", "BOT_FIRSTNAME"),
            BotLastName = FirstDefined("OPENSIM_LOGIN_LASTNAME", "BOT_LASTNAME"),
            BotPassword = FirstDefined("OPENSIM_LOGIN_PASSWORD", "BOT_PASSWORD"),
            BotLoginUri = FirstDefined("OPENSIM_LOGIN_URI", "BOT_LOGIN_URI") ?? "http://opensim:9000",
            BotStartLocation = FirstDefined("OPENSIM_LOGIN_START", "BOT_LOGIN_START") ?? "last",
            BotLoginTimeoutSeconds = ParseInt(FirstDefined("BOT_LOGIN_TIMEOUT_SECONDS", "OPENSIM_LOGIN_TIMEOUT_SECONDS"), 30),
            OpencodeChatEnabled = ParseBool(Env("OPENCODE_CHAT_ENABLED"), true),
            OpencodeScheme = Env("OPENCODE_SCHEME") ?? "http",
            OpencodeHost = Env("OPENCODE_HOST") ?? "opensim-opencode",
            OpencodePort = ParseInt(Env("OPENCODE_PORT"), 8998),
            OpencodeUsername = Env("OPENCODE_USERNAME"),
            OpencodePassword = FirstDefined("OPENCODE_PASSWORD", "OPENCODE_SERVER_PASSWORD"),
            OpencodeInitialProvider = Env("OPENCODE_INITIAL_PROVIDER"),
            OpencodeInitialModel = Env("OPENCODE_INITIAL_MODEL"),
            OpencodeRequestTimeoutSeconds = ParseInt(Env("OPENCODE_REQUEST_TIMEOUT_SECONDS"), 60),
            OpencodeHandlerFirstName = Env("OPENCODE_HANDLER_FIRSTNAME"),
            OpencodeHandlerLastName = Env("OPENCODE_HANDLER_LASTNAME"),
            PromptHandlingEnabled = ParseBool(Env("PROMPT_HANDLING_ENABLED"), true),
            PromptBuiltInEnabled = ParseBool(Env("PROMPT_BUILTIN_ENABLED"), true),
            PromptProjectAgentsEnabled = ParseBool(Env("PROMPT_PROJECT_AGENTS_ENABLED"), true),
            PromptProjectAgentsFile = Env("PROMPT_PROJECT_AGENTS_FILE") ?? "AGENTS.md",
            PromptNotecardEnabled = ParseBool(Env("PROMPT_NOTECARD_ENABLED"), true),
            PromptNotecardRequireHandler = ParseBool(Env("PROMPT_NOTECARD_REQUIRE_HANDLER"), true),
            PromptMaxChars = ParseInt(Env("PROMPT_MAX_CHARS"), 16000)
        };

        options.McpPort = ParseInt(Env("MCP_PORT"), 8999);
        ApplyCliOverrides(options, args);
        options.McpHttpEndpoint = NormalizeEndpoint(options.McpHttpEndpoint);
        options.McpTransport = (options.McpTransport ?? "http").Trim().ToLowerInvariant();

        return options;
    }

    public static string BuildUsage()
    {
        return string.Join(
            Environment.NewLine,
            "opensim-metaverse2mcp",
            string.Empty,
            "A LibreMetaverse-powered OpenSim bot exposed via MCP Streamable HTTP.",
            string.Empty,
            "Usage:",
            "  opensim-metaverse2mcp [options]",
            string.Empty,
            "Bot login options (required):",
            "  --first-name <value>           Bot first name (env: OPENSIM_LOGIN_FIRSTNAME)",
            "  --last-name <value>            Bot last name  (env: OPENSIM_LOGIN_LASTNAME)",
            "  --password <value>             Bot password   (env: OPENSIM_LOGIN_PASSWORD)",
            string.Empty,
            "Bot login options (optional):",
            "  --login-uri <url>              Login URI (env: OPENSIM_LOGIN_URI, default: http://opensim:9000)",
            "  --start-location <value>       Start location (env: OPENSIM_LOGIN_START, default: last)",
            "  --login-timeout-seconds <int>  Login timeout (env: BOT_LOGIN_TIMEOUT_SECONDS, default: 30)",
            string.Empty,
            "Opencode chat bridge:",
            "  --opencode-chat-enabled <bool> Enable IM -> Opencode chat bridge (env: OPENCODE_CHAT_ENABLED, default: true)",
            "  --opencode-scheme <http|https> Opencode URL scheme (env: OPENCODE_SCHEME, default: http)",
            "  --opencode-host <host>         Opencode server host (env: OPENCODE_HOST, default: opensim-opencode)",
            "  --opencode-port <port>         Opencode server port (env: OPENCODE_PORT, default: 8998)",
            "  --opencode-username <value>    Optional Basic auth username (env: OPENCODE_USERNAME, default: opencode when password set)",
            "  --opencode-password <value>    Optional Basic auth password (env: OPENCODE_PASSWORD/OPENCODE_SERVER_PASSWORD)",
            "  --opencode-initial-provider <id>",
            "                                Optional startup provider for IM conversations without runtime overrides",
            "                                (env: OPENCODE_INITIAL_PROVIDER)",
            "  --opencode-initial-model <provider/model|model>",
            "                                Optional startup model for IM conversations without runtime overrides",
            "                                (env: OPENCODE_INITIAL_MODEL)",
            "  --opencode-timeout-seconds <int>",
            "                                Opencode request timeout in seconds (env: OPENCODE_REQUEST_TIMEOUT_SECONDS, default: 60)",
            "  --handler-first-name <value>   Optional handler first name (env: OPENCODE_HANDLER_FIRSTNAME)",
            "  --handler-last-name <value>    Optional handler last name (env: OPENCODE_HANDLER_LASTNAME)",
            string.Empty,
            "Prompt handling:",
            "  --prompt-handling-enabled <bool>",
            "                                Enable layered prompt handling (env: PROMPT_HANDLING_ENABLED, default: true)",
            "  --prompt-builtin-enabled <bool>",
            "                                Include built-in bridge prompt (env: PROMPT_BUILTIN_ENABLED, default: true)",
            "  --prompt-project-agents-enabled <bool>",
            "                                Include local AGENTS.md prompt file (env: PROMPT_PROJECT_AGENTS_ENABLED, default: true)",
            "  --prompt-project-agents-file <path>",
            "                                AGENTS.md path (env: PROMPT_PROJECT_AGENTS_FILE, default: AGENTS.md)",
            "  --prompt-notecard-enabled <bool>",
            "                                Allow in-world AGENTS.md notecard prompt source (env: PROMPT_NOTECARD_ENABLED, default: true)",
            "  --prompt-notecard-require-handler <bool>",
            "                                Only allow handler avatar to install/replace AGENTS.md notecard prompts",
            "                                (env: PROMPT_NOTECARD_REQUIRE_HANDLER, default: true)",
            "  --prompt-max-chars <int>      Per-source maximum characters after normalization",
            "                                (env: PROMPT_MAX_CHARS, default: 16000)",
            string.Empty,
            "MCP HTTP options:",
            "  --mcp-transport <http|sse>     Transport (env: MCP_TRANSPORT, default: http)",
            "  --mcp-host <host>              Bind host (env: MCP_HOST, default: 0.0.0.0)",
            "  --mcp-port <port>              Bind port (env: MCP_PORT, default: 8999)",
            "  --mcp-http-endpoint <path>     Endpoint path (env: MCP_HTTP_ENDPOINT, default: /mcp)",
            "  --mcp-http-bearer-token <tok>  Bearer auth token (env: MCP_HTTP_BEARER_TOKEN)",
            "  --mcp-http-disallow-delete     Reject DELETE on MCP endpoint (env: MCP_HTTP_DISALLOW_DELETE)",
            "  --mcp-diagnostics              Extra bot/MCP diagnostics (env: MCP_DIAGNOSTICS)",
            "  --inventory-offer-policy-file <path>",
            "                                JSON file for inventory-offer policy rules",
            "                                (env: INVENTORY_OFFER_POLICY_FILE)",
            "  --inventory-offer-policy-autosave <bool>",
            "                                Auto-save policy changes (env: INVENTORY_OFFER_POLICY_AUTOSAVE, default: true)",
            string.Empty,
            "General:",
            "  -h, --help                     Show help"
        );
    }

    private static void ApplyCliOverrides(AppOptions options, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "--first-name":
                    options.BotFirstName = RequireValue(args, ref i, arg);
                    break;
                case "--last-name":
                    options.BotLastName = RequireValue(args, ref i, arg);
                    break;
                case "--password":
                    options.BotPassword = RequireValue(args, ref i, arg);
                    break;
                case "--login-uri":
                    options.BotLoginUri = RequireValue(args, ref i, arg);
                    break;
                case "--start-location":
                    options.BotStartLocation = RequireValue(args, ref i, arg);
                    break;
                case "--login-timeout-seconds":
                    options.BotLoginTimeoutSeconds = ParseInt(RequireValue(args, ref i, arg), options.BotLoginTimeoutSeconds);
                    break;
                case "--opencode-chat-enabled":
                    options.OpencodeChatEnabled = ParseBool(RequireValue(args, ref i, arg), options.OpencodeChatEnabled);
                    break;
                case "--opencode-scheme":
                    options.OpencodeScheme = RequireValue(args, ref i, arg);
                    break;
                case "--opencode-host":
                    options.OpencodeHost = RequireValue(args, ref i, arg);
                    break;
                case "--opencode-port":
                    options.OpencodePort = ParseInt(RequireValue(args, ref i, arg), options.OpencodePort);
                    break;
                case "--opencode-username":
                    options.OpencodeUsername = RequireValue(args, ref i, arg);
                    break;
                case "--opencode-password":
                    options.OpencodePassword = RequireValue(args, ref i, arg);
                    break;
                case "--opencode-initial-provider":
                    options.OpencodeInitialProvider = RequireValue(args, ref i, arg);
                    break;
                case "--opencode-initial-model":
                    options.OpencodeInitialModel = RequireValue(args, ref i, arg);
                    break;
                case "--opencode-timeout-seconds":
                    options.OpencodeRequestTimeoutSeconds = ParseInt(RequireValue(args, ref i, arg), options.OpencodeRequestTimeoutSeconds);
                    break;
                case "--handler-first-name":
                    options.OpencodeHandlerFirstName = RequireValue(args, ref i, arg);
                    break;
                case "--handler-last-name":
                    options.OpencodeHandlerLastName = RequireValue(args, ref i, arg);
                    break;
                case "--prompt-handling-enabled":
                    options.PromptHandlingEnabled = ParseBool(RequireValue(args, ref i, arg), options.PromptHandlingEnabled);
                    break;
                case "--prompt-builtin-enabled":
                    options.PromptBuiltInEnabled = ParseBool(RequireValue(args, ref i, arg), options.PromptBuiltInEnabled);
                    break;
                case "--prompt-project-agents-enabled":
                    options.PromptProjectAgentsEnabled = ParseBool(RequireValue(args, ref i, arg), options.PromptProjectAgentsEnabled);
                    break;
                case "--prompt-project-agents-file":
                    options.PromptProjectAgentsFile = RequireValue(args, ref i, arg);
                    break;
                case "--prompt-notecard-enabled":
                    options.PromptNotecardEnabled = ParseBool(RequireValue(args, ref i, arg), options.PromptNotecardEnabled);
                    break;
                case "--prompt-notecard-require-handler":
                    options.PromptNotecardRequireHandler = ParseBool(RequireValue(args, ref i, arg), options.PromptNotecardRequireHandler);
                    break;
                case "--prompt-max-chars":
                    options.PromptMaxChars = ParseInt(RequireValue(args, ref i, arg), options.PromptMaxChars);
                    break;
                case "--mcp-transport":
                    options.McpTransport = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-host":
                    options.McpHost = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-port":
                    options.McpPort = ParseInt(RequireValue(args, ref i, arg), options.McpPort);
                    break;
                case "--mcp-http-endpoint":
                    options.McpHttpEndpoint = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-http-bearer-token":
                    options.McpHttpBearerToken = RequireValue(args, ref i, arg);
                    break;
                case "--mcp-http-disallow-delete":
                    options.McpHttpDisallowDelete = true;
                    break;
                case "--mcp-diagnostics":
                    options.McpDiagnostics = true;
                    break;
                case "--inventory-offer-policy-file":
                    options.InventoryOfferPolicyFile = RequireValue(args, ref i, arg);
                    break;
                case "--inventory-offer-policy-autosave":
                    options.InventoryOfferPolicyAutoSave = ParseBool(RequireValue(args, ref i, arg), options.InventoryOfferPolicyAutoSave);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}");
        }

        index++;
        return args[index];
    }

    private static string? Env(string key)
    {
        return Environment.GetEnvironmentVariable(key);
    }

    private static string? FirstDefined(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Env(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool ParseBool(string? raw, bool fallback)
    {
        if (!AppOptions.TryParseBool(raw, out var value))
        {
            return fallback;
        }

        return value;
    }

    private static int ParseInt(string? raw, int fallback)
    {
        if (!AppOptions.TryParseInt(raw, out var value))
        {
            return fallback;
        }

        return value;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var value = (endpoint ?? "/mcp").Trim();
        if (value.Length == 0)
        {
            return "/mcp";
        }

        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        while (value.Length > 1 && value.EndsWith('/'))
        {
            value = value[..^1];
        }

        return value;
    }
}
