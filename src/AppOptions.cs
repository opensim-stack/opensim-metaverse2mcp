using System.Globalization;

namespace Opensim.Metaverse2Mcp;

internal sealed class AppOptions
{
    public string McpTransport { get; set; } = "http";
    public string McpHost { get; set; } = "0.0.0.0";
    public int McpPort { get; set; } = 8999;
    public string McpHttpEndpoint { get; set; } = "/mcp";
    public bool McpHttpDisallowDelete { get; set; }
    public string? McpHttpBearerToken { get; set; }
    public bool McpDiagnostics { get; set; }

    public string? InventoryOfferPolicyFile { get; set; }
    public bool InventoryOfferPolicyAutoSave { get; set; } = true;

    public string? BotFirstName { get; set; }
    public string? BotLastName { get; set; }
    public string? BotPassword { get; set; }
    public string? BotSpawnerParent { get; set; }
    public string? BotSpawnerLevel { get; set; }
    public string SpawnerHost { get; set; } = "opensim-spawner";
    public int SpawnerPort { get; set; } = 8993;
    public string? SpawnerToken { get; set; }
    public string BotLoginUri { get; set; } = "http://opensim:9000";
    public string BotStartLocation { get; set; } = "last";
    public string WearFolderName { get; set; } = "";
    public int BotLoginTimeoutSeconds { get; set; } = 30;

    public string OpencodeScheme { get; set; } = "http";
    public string OpencodeHost { get; set; } = "opensim-opencode";
    public int OpencodePort { get; set; } = 8998;
    public string? OpencodeUsername { get; set; }
    public string? OpencodePassword { get; set; }
    public string? OpencodeInitialProvider { get; set; }
    public string? OpencodeInitialModel { get; set; }
    public bool OpencodeEventDebug { get; set; }
    public int OpencodeRequestTimeoutSeconds { get; set; } = 60;
    public string HandlerConfig { get; set; } = "/config/handlers.json";
    public bool VoiceRoutingEnabled { get; set; }
    public string VoiceBackend { get; set; } = "webrtc";
    public string PiperScheme { get; set; } = "http";
    public string PiperHost { get; set; } = "opensim-piper";
    public int PiperPort { get; set; } = 8995;
    public string PiperTtsPath { get; set; } = "/tts";
    public string PiperVoicesPath { get; set; } = "/voices";
    public int PiperRequestTimeoutSeconds { get; set; } = 60;
    public string PiperDefaultVoice { get; set; } = "en_US-lessac-medium";
    public string? BridgeTrustStateFile { get; set; } = "/workspace/state/dialog-bridge-trust.json";

    // When true, the bot will check for a present dialog bridge when it first
    // enters a new region and attempt to auto-install the bridge if missing.
    public bool DialogBridgeAutoProvisionOnRegionEnter { get; set; } = true;
    public int DialogBridgePromptResponseTimeoutSeconds { get; set; } = 120;

    public bool PromptHandlingEnabled { get; set; } = true;
    public bool PromptBuiltInEnabled { get; set; } = true;
    public string? OpencodeDefaultPromptPath { get; set; }
    public bool PromptProjectAgentsEnabled { get; set; } = true;
    public string PromptProjectAgentsFile { get; set; } = "AGENTS.md";
    public bool PromptNotecardEnabled { get; set; } = true;
    public bool PromptNotecardRequireHandler { get; set; } = true;
    public int PromptMaxChars { get; set; } = 16000;
    public bool RequesterContextDebugLogging { get; set; }
    public string ReceiveChatAllowedTypes { get; set; } = "Normal";

    public bool ShowHelp { get; set; }

    public bool UseLegacySseCompatibility => string.Equals(McpTransport, "sse", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var transport = (McpTransport ?? string.Empty).Trim().ToLowerInvariant();
        if (transport != "http" && transport != "sse")
        {
            errors.Add("MCP transport must be 'http' or 'sse'.");
        }

        if (string.IsNullOrWhiteSpace(McpHost))
        {
            errors.Add("MCP host is required.");
        }

        if (McpPort < 1 || McpPort > 65535)
        {
            errors.Add("MCP port must be in range 1..65535.");
        }

        if (string.IsNullOrWhiteSpace(SpawnerHost))
        {
            errors.Add("Spawner host is required.");
        }

        if (SpawnerPort < 1 || SpawnerPort > 65535)
        {
            errors.Add("Spawner port must be in range 1..65535.");
        }

        if (string.IsNullOrWhiteSpace(McpHttpEndpoint))
        {
            errors.Add("MCP HTTP endpoint is required.");
        }

        if (!Uri.TryCreate(BotLoginUri, UriKind.Absolute, out _))
        {
            errors.Add("Bot login URI must be an absolute URI.");
        }

        if (BotLoginTimeoutSeconds < 1)
        {
            errors.Add("Bot login timeout must be at least 1 second.");
        }

        var scheme = (OpencodeScheme ?? string.Empty).Trim().ToLowerInvariant();
        if (scheme != "http" && scheme != "https")
        {
            errors.Add("Opencode scheme must be 'http' or 'https'.");
        }

        if (string.IsNullOrWhiteSpace(OpencodeHost))
        {
            errors.Add("Opencode host is required when chat bridge is enabled.");
        }

        if (OpencodePort < 1 || OpencodePort > 65535)
        {
            errors.Add("Opencode port must be in range 1..65535.");
        }

        if (OpencodeRequestTimeoutSeconds < 1)
        {
            errors.Add("Opencode timeout must be at least 1 second.");
        }

        if (PromptHandlingEnabled) 
        {
            if (!string.IsNullOrWhiteSpace(OpencodeDefaultPromptPath))
            {
                try
                {
                    _ = Path.GetFullPath(OpencodeDefaultPromptPath);
                }
                catch
                {
                    errors.Add("OPENCODE_DEFAULT_PROMPT_PATH is invalid.");
                }
            }

            if (PromptMaxChars < 512)
            {
                errors.Add("Prompt max chars must be at least 512 when prompt handling is enabled.");
            }
    
            if (string.IsNullOrWhiteSpace(PromptProjectAgentsFile))
            {
                errors.Add("Prompt project AGENTS file path must not be empty when prompt handling is enabled.");
            }
            else
            {
                try
                {
                    _ = Path.GetFullPath(PromptProjectAgentsFile);
                }
                catch
                {
                    errors.Add("Prompt project AGENTS file path is invalid.");
                }
            }
        }

        if (DialogBridgePromptResponseTimeoutSeconds < 5)
        {
            errors.Add("Dialog bridge prompt response timeout must be at least 5 seconds.");
        }

        if (string.IsNullOrWhiteSpace(HandlerConfig))
        {
            errors.Add("Handler config file path is required (--handler-config or OPENSIM_HANDLER_CONFIG).");
        }
        else
        {
            try
            {
                _ = Path.GetFullPath(HandlerConfig);
            }
            catch
            {
                errors.Add("Handler config file path is invalid.");
            }
        }

        if (!string.IsNullOrWhiteSpace(BridgeTrustStateFile))
        {
            try
            {
                _ = Path.GetFullPath(BridgeTrustStateFile);
            }
            catch
            {
                errors.Add("LSL dialog bridge trust state file path is invalid.");
            }
        }

        if (VoiceRoutingEnabled)
        {
            var backend = (VoiceBackend ?? string.Empty).Trim().ToLowerInvariant();
            if (backend != "webrtc")
            {
                errors.Add("Voice backend must be 'webrtc'.");
            }

            var piperScheme = (PiperScheme ?? string.Empty).Trim().ToLowerInvariant();
            if (piperScheme != "http" && piperScheme != "https")
            {
                errors.Add("Piper scheme must be 'http' or 'https'.");
            }

            if (string.IsNullOrWhiteSpace(PiperHost))
            {
                errors.Add("Piper host is required when voice routing is enabled.");
            }

            if (PiperPort < 1 || PiperPort > 65535)
            {
                errors.Add("Piper port must be in range 1..65535.");
            }

            if (PiperRequestTimeoutSeconds < 1)
            {
                errors.Add("Piper timeout must be at least 1 second.");
            }

            if (string.IsNullOrWhiteSpace(PiperTtsPath) || !PiperTtsPath.TrimStart().StartsWith('/'))
            {
                errors.Add("Piper TTS path must start with '/'.");
            }

            if (string.IsNullOrWhiteSpace(PiperVoicesPath) || !PiperVoicesPath.TrimStart().StartsWith('/'))
            {
                errors.Add("Piper voices path must start with '/'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(InventoryOfferPolicyFile))
        {
            try
            {
                _ = Path.GetFullPath(InventoryOfferPolicyFile);
            }
            catch
            {
                errors.Add("Inventory offer policy file path is invalid.");
            }
        }

        if (string.IsNullOrWhiteSpace(BotFirstName))
        {
            errors.Add("Bot first name is required (--first-name or OPENSIM_BOT_FIRST).");
        }

        if (string.IsNullOrWhiteSpace(BotLastName))
        {
            errors.Add("Bot last name is required (--last-name or OPENSIM_BOT_LAST).");
        }

        if (string.IsNullOrWhiteSpace(BotPassword))
        {
            errors.Add("Bot password is required (--password or OPENSIM_BOT_PASSWORD).");
        }

        return errors;
    }

    public static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        if (raw == null)
        {
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                value = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                value = false;
                return true;
            default:
                return bool.TryParse(raw, out value);
        }
    }

    public static bool TryParseInt(string? raw, out int value)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
