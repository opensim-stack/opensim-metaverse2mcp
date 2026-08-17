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
            OpencodeEventDebug = ParseBool(Env("OPENCODE_EVENT_DEBUG"), false),
            OpencodeRequestTimeoutSeconds = ParseInt(Env("OPENCODE_REQUEST_TIMEOUT_SECONDS"), 1800),
            OpencodeHandlerFirstName = Env("OPENCODE_HANDLER_FIRSTNAME"),
            OpencodeHandlerLastName = Env("OPENCODE_HANDLER_LASTNAME"),
            VoiceRoutingEnabled = ParseBool(Env("VOICE_ROUTING_ENABLED"), false),
            VoiceBackend = Env("VOICE_BACKEND") ?? "webrtc",
            PiperScheme = Env("PIPER_SCHEME") ?? "http",
            PiperHost = Env("PIPER_HOST") ?? "opensim-piper",
            PiperPort = ParseInt(Env("PIPER_PORT"), 8995),
            PiperTtsPath = Env("PIPER_TTS_PATH") ?? "/tts",
            PiperVoicesPath = Env("PIPER_VOICES_PATH") ?? "/voices",
            PiperRequestTimeoutSeconds = ParseInt(Env("PIPER_TIMEOUT_SECONDS"), 60),
            PiperDefaultVoice = Env("PIPER_DEFAULT_VOICE") ?? "en_US-lessac-medium",
            LslDialogBridgeTrustedObjectId = FirstDefined("LSL_DIALOG_BRIDGE_TRUSTED_OBJECT_ID", "OPENCODE_LSL_DIALOG_BRIDGE_TRUSTED_OBJECT_ID"),
            LslDialogBridgeTrustedOwnerId = FirstDefined("LSL_DIALOG_BRIDGE_TRUSTED_OWNER_ID", "OPENCODE_LSL_DIALOG_BRIDGE_TRUSTED_OWNER_ID"),
            LslDialogBridgeRequireTrustedSender = ParseBool(FirstDefined("LSL_DIALOG_BRIDGE_REQUIRE_TRUSTED_SENDER", "OPENCODE_LSL_DIALOG_BRIDGE_REQUIRE_TRUSTED_SENDER"), true),
            LslDialogBridgeTrustStateFile = FirstDefined("LSL_DIALOG_BRIDGE_TRUST_STATE_FILE", "OPENCODE_LSL_DIALOG_BRIDGE_TRUST_STATE_FILE") ?? "/workspace/state/dialog-bridge-trust.json",
            DialogBridgeAutoProvisionOnRegionEnter = ParseBool(Env("DIALOG_BRIDGE_AUTO_PROVISION_ON_REGION_ENTER"), true),
            DialogBridgePromptResponseTimeoutSeconds = ParseInt(Env("DIALOG_BRIDGE_PROMPT_RESPONSE_TIMEOUT_SECONDS"), 120),
            PromptHandlingEnabled = ParseBool(Env("PROMPT_HANDLING_ENABLED"), true),
            PromptBuiltInEnabled = ParseBool(Env("PROMPT_BUILTIN_ENABLED"), true),
            PromptProjectAgentsEnabled = ParseBool(Env("PROMPT_PROJECT_AGENTS_ENABLED"), true),
            PromptProjectAgentsFile = Env("PROMPT_PROJECT_AGENTS_FILE") ?? "AGENTS.md",
            PromptNotecardEnabled = ParseBool(Env("PROMPT_NOTECARD_ENABLED"), true),
            PromptNotecardRequireHandler = ParseBool(Env("PROMPT_NOTECARD_REQUIRE_HANDLER"), true),
            PromptMaxChars = ParseInt(Env("PROMPT_MAX_CHARS"), 16000),
            RequesterContextDebugLogging = ParseBool(Env("REQUESTER_CONTEXT_DEBUG_LOGGING"), false)
        };

        options.McpPort = ParseInt(Env("MCP_PORT"), 8999);
        ApplyCliOverrides(options, args);
        options.McpHttpEndpoint = NormalizeEndpoint(options.McpHttpEndpoint);
        options.PiperTtsPath = NormalizeEndpoint(options.PiperTtsPath);
        options.PiperVoicesPath = NormalizeEndpoint(options.PiperVoicesPath);
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
            "  --opencode-event-debug <bool>",
            "                                Enable verbose Opencode event JSON logging (including message.part.updated reasoning/text)",
            "                                (env: OPENCODE_EVENT_DEBUG, default: false)",
            "  --opencode-timeout-seconds <int>",
            "                                Opencode request timeout in seconds (env: OPENCODE_REQUEST_TIMEOUT_SECONDS, default: 60)",
            "  --handler-first-name <value>   Optional handler first name (env: OPENCODE_HANDLER_FIRSTNAME)",
            "  --handler-last-name <value>    Optional handler last name (env: OPENCODE_HANDLER_LASTNAME)",
            string.Empty,
            "Voice routing (Piper + grid voice backend):",
            "  --voice-enabled <bool>         Enable voice routing for Say tool (env: VOICE_ROUTING_ENABLED, default: false)",
            "  --voice-backend <webrtc>       Voice backend for WAV playback to nearby avatars (env: VOICE_BACKEND, default: webrtc)",
            "  --piper-scheme <http|https>    Piper URL scheme (env: PIPER_SCHEME, default: http)",
            "  --piper-host <host>            Piper host (env: PIPER_HOST, default: opensim-piper)",
            "  --piper-port <port>            Piper port (env: PIPER_PORT, default: 8995)",
            "  --piper-tts-path <path>        Piper synthesis path (env: PIPER_TTS_PATH, default: /tts)",
            "  --piper-voices-path <path>     Piper voices list path (env: PIPER_VOICES_PATH, default: /voices)",
            "  --piper-timeout-seconds <int>  Piper request timeout in seconds (env: PIPER_TIMEOUT_SECONDS, default: 60)",
            "  --piper-default-voice <name>   Default voice name used by Say when omitted (env: PIPER_DEFAULT_VOICE, default: en_US-lessac-medium)",
            "  --lsl-dialog-bridge-trusted-object-id <uuid>",
            "                                Optional trusted bridge object UUID for dialog replies",
            "                                (env: LSL_DIALOG_BRIDGE_TRUSTED_OBJECT_ID)",
            "  --lsl-dialog-bridge-trusted-owner-id <uuid>",
            "                                Optional trusted owner UUID for bridge object replies",
            "                                (env: LSL_DIALOG_BRIDGE_TRUSTED_OWNER_ID)",
            "  --lsl-dialog-bridge-require-trusted-sender <bool>",
            "                                Require trusted object/owner checks for bridge replies",
            "                                (env: LSL_DIALOG_BRIDGE_REQUIRE_TRUSTED_SENDER, default: true)",
            "  --lsl-dialog-bridge-trust-state-file <path>",
            "                                Optional JSON file used to persist runtime bridge trust pins",
            "                                Supports {bot_uuid} in path templates for multi-bot deployments",
            "                                (env: LSL_DIALOG_BRIDGE_TRUST_STATE_FILE, default: /workspace/state/dialog-bridge-trust.json)",
            "  --dialog-bridge-auto-provision-on-region-enter <bool>",
            "                                When true, automatically install a dialog bridge when the bot first enters a new region",
            "                                (env: DIALOG_BRIDGE_AUTO_PROVISION_ON_REGION_ENTER, default: true)",
            "  --dialog-bridge-prompt-response-timeout-seconds <int>",
            "                                Wait time before a dialog prompt falls back to text reply mode",
            "                                (env: DIALOG_BRIDGE_PROMPT_RESPONSE_TIMEOUT_SECONDS, default: 120)",
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
            "  --requester-context-debug-logging <bool>",
            "                                Emit debug logs when requester-context prompt layers are attached",
            "                                (env: REQUESTER_CONTEXT_DEBUG_LOGGING, default: false)",
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
                case "--opencode-event-debug":
                    options.OpencodeEventDebug = ParseBool(RequireValue(args, ref i, arg), options.OpencodeEventDebug);
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
                case "--voice-enabled":
                    options.VoiceRoutingEnabled = ParseBool(RequireValue(args, ref i, arg), options.VoiceRoutingEnabled);
                    break;
                case "--voice-backend":
                    options.VoiceBackend = RequireValue(args, ref i, arg);
                    break;
                case "--piper-scheme":
                    options.PiperScheme = RequireValue(args, ref i, arg);
                    break;
                case "--piper-host":
                    options.PiperHost = RequireValue(args, ref i, arg);
                    break;
                case "--piper-port":
                    options.PiperPort = ParseInt(RequireValue(args, ref i, arg), options.PiperPort);
                    break;
                case "--piper-tts-path":
                    options.PiperTtsPath = RequireValue(args, ref i, arg);
                    break;
                case "--piper-voices-path":
                    options.PiperVoicesPath = RequireValue(args, ref i, arg);
                    break;
                case "--piper-timeout-seconds":
                    options.PiperRequestTimeoutSeconds = ParseInt(RequireValue(args, ref i, arg), options.PiperRequestTimeoutSeconds);
                    break;
                case "--piper-default-voice":
                    options.PiperDefaultVoice = RequireValue(args, ref i, arg);
                    break;
                case "--lsl-dialog-bridge-trusted-object-id":
                    options.LslDialogBridgeTrustedObjectId = RequireValue(args, ref i, arg);
                    break;
                case "--lsl-dialog-bridge-trusted-owner-id":
                    options.LslDialogBridgeTrustedOwnerId = RequireValue(args, ref i, arg);
                    break;
                case "--lsl-dialog-bridge-require-trusted-sender":
                    options.LslDialogBridgeRequireTrustedSender = ParseBool(RequireValue(args, ref i, arg), options.LslDialogBridgeRequireTrustedSender);
                    break;
                case "--lsl-dialog-bridge-trust-state-file":
                    options.LslDialogBridgeTrustStateFile = RequireValue(args, ref i, arg);
                    break;
                case "--dialog-bridge-auto-provision-on-region-enter":
                    options.DialogBridgeAutoProvisionOnRegionEnter = ParseBool(RequireValue(args, ref i, arg), options.DialogBridgeAutoProvisionOnRegionEnter);
                    break;
                case "--dialog-bridge-prompt-response-timeout-seconds":
                    options.DialogBridgePromptResponseTimeoutSeconds = ParseInt(RequireValue(args, ref i, arg), options.DialogBridgePromptResponseTimeoutSeconds);
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
                case "--requester-context-debug-logging":
                    options.RequesterContextDebugLogging = ParseBool(RequireValue(args, ref i, arg), options.RequesterContextDebugLogging);
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
