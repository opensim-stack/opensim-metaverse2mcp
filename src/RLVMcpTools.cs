using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Opensim.Metaverse2Mcp;

[McpServerToolType]
internal sealed class RLVMcpTools
{
    private readonly BotSession _bot;

    public RLVMcpTools(BotSession bot)
    {
        _bot = bot;
    }

    [McpServerTool, Description("Get current RLV policy/runtime status and active restrictions affecting this bot.")]
    public RlvStatusSnapshot RlvGetStatus(
        [Description("Maximum number of active restrictions to include in the response (1-100, default: 20).")]
        int maxRestrictions = 20)
    {
        return _bot.GetRlvStatus(maxRestrictions);
    }

    [McpServerTool, Description("Enable or disable RLV runtime processing for this bot session. Requires ALLOW_RLV=true at startup.")]
    public BotToolResult RlvSetRuntimeEnabled(
        [Description("True to enable runtime RLV processing, false to disable it.")] bool enabled)
    {
        return _bot.SetRlvRuntimeEnabled(enabled);
    }

    [McpServerTool, Description("Enable or disable IM-driven RLV command intake from object IM messages. Requires ALLOW_RLV=true.")]
    public BotToolResult RlvSetImIntakeEnabled(
        [Description("True to accept RLV commands from object IM messages, false to ignore them.")] bool enabled)
    {
        return _bot.SetRlvInstantMessageIntakeEnabled(enabled);
    }

    [McpServerTool, Description("Submit one RLV command string for processing through MCP. Requires ALLOW_RLV=true and runtime enabled.")]
    public Task<RlvCommandProcessResult> RlvProcessCommand(
        [Description("RLV command text. '@' prefix is optional (for example: @detach=n or detach=n).")]
        string command,
        [Description("Optional UUID of the effective sender object. Defaults to bot AgentID when connected.")]
        string? senderObjectId = null,
        [Description("Optional sender object name used for restriction attribution.")]
        string? senderObjectName = null,
        CancellationToken cancellationToken = default)
    {
        return _bot.ProcessRlvCommandAsync(command, senderObjectId, senderObjectName, cancellationToken);
    }

    [McpServerTool, Description("List active RLV restrictions with optional behavior/sender filters.")]
    public RlvRestrictionListResult RlvListRestrictions(
        [Description("Optional behavior filter (for example: detach, sendim, tplure). Exact match, case-insensitive.")]
        string? behavior = null,
        [Description("Optional sender object UUID filter.")]
        string? senderObjectId = null,
        [Description("Optional sender object name contains filter (case-insensitive).")]
        string? senderObjectNameContains = null,
        [Description("Maximum number of matched restrictions to return (1-200, default: 100).")]
        int maxRestrictions = 100)
    {
        return _bot.ListRlvRestrictions(behavior, senderObjectId, senderObjectNameContains, maxRestrictions);
    }
}