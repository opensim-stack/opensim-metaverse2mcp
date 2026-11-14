using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Opensim.Metaverse2Mcp;

[McpServerPromptType]
internal sealed class BotMcpPrompts
{
    private readonly BotSession _bot;

    public BotMcpPrompts(BotSession bot)
    {
        _bot = bot;
    }

    [McpServerPrompt, Description("Combined system prompt currently used by the bridge (built-in + AGENTS.md + in-world notecard when enabled).")]
    public string RuntimePromptLayered()
    {
        return _bot.GetLayeredPromptForMcp() ?? "Prompt handling is disabled or no prompt sources are active.";
    }

    [McpServerPrompt, Description("Built-in bridge system prompt text used as the base layer when enabled.")]
    public string RuntimePromptBridge()
    {
        return _bot.GetBuiltInBridgePromptForMcp();
    }

    [McpServerPrompt, Description("Project AGENTS.md prompt layer when enabled and available.")]
    public string RuntimePromptProjectAgents()
    {
        return _bot.GetProjectAgentsPromptForMcp();
    }

    [McpServerPrompt, Description("In-world AGENTS.md notecard prompt layer when enabled and currently loaded.")]
    public string RuntimePromptNotecardAgents()
    {
        return _bot.GetNotecardAgentsPromptForMcp();
    }
}
