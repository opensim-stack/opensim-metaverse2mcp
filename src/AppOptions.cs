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

    public string? BotFirstName { get; set; }
    public string? BotLastName { get; set; }
    public string? BotPassword { get; set; }
    public string BotLoginUri { get; set; } = "http://opensim:9000";
    public string BotStartLocation { get; set; } = "last";
    public int BotLoginTimeoutSeconds { get; set; } = 30;

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

        if (string.IsNullOrWhiteSpace(BotFirstName))
        {
            errors.Add("Bot first name is required (--first-name or OPENSIM_LOGIN_FIRSTNAME).");
        }

        if (string.IsNullOrWhiteSpace(BotLastName))
        {
            errors.Add("Bot last name is required (--last-name or OPENSIM_LOGIN_LASTNAME).");
        }

        if (string.IsNullOrWhiteSpace(BotPassword))
        {
            errors.Add("Bot password is required (--password or OPENSIM_LOGIN_PASSWORD).");
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
