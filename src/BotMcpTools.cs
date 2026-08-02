using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Opensim.Metaverse2Mcp;

[McpServerToolType]
internal sealed class BotMcpTools
{
    private readonly BotSession _bot;

    public BotMcpTools(BotSession bot)
    {
        _bot = bot;
    }

    [McpServerTool, Description("Get bot connection and location status.")]
    public BotStatus GetStatus()
    {
        return _bot.GetStatus();
    }

    [McpServerTool, Description("Sit on the ground.")]
    public Task<BotToolResult> Sit(CancellationToken cancellationToken)
    {
        return _bot.SitAsync(cancellationToken);
    }

    [McpServerTool, Description("Stand up.")]
    public Task<BotToolResult> Stand(CancellationToken cancellationToken)
    {
        return _bot.StandAsync(cancellationToken);
    }

    [McpServerTool, Description("Start or stop flying.")]
    public Task<BotToolResult> Fly(
        [Description("True to fly, false to walk.")] bool enabled,
        CancellationToken cancellationToken)
    {
        return _bot.FlyAsync(enabled, cancellationToken);
    }

    [McpServerTool, Description("Jump once.")]
    public Task<BotToolResult> Jump(CancellationToken cancellationToken)
    {
        return _bot.JumpAsync(cancellationToken);
    }

    [McpServerTool, Description("Start or stop dance animation DANCE1.")]
    public Task<BotToolResult> Dance(
        [Description("True to start dancing, false to stop.")] bool enabled,
        CancellationToken cancellationToken)
    {
        return _bot.DanceAsync(enabled, cancellationToken);
    }

    [McpServerTool, Description("Send a local chat message.")]
    public Task<BotToolResult> Chat(
        [Description("Message to send.")] string message,
        [Description("Chat channel number.")] int channel,
        CancellationToken cancellationToken)
    {
        return _bot.SayChatAsync(message, channel, cancellationToken);
    }

    [McpServerTool, Description("Send an instant message to an avatar UUID.")]
    public Task<BotToolResult> SendInstantMessage(
        [Description("Recipient agent UUID.")] string agentId,
        [Description("Message to send.")] string message,
        CancellationToken cancellationToken)
    {
        return _bot.SendImAsync(agentId, message, cancellationToken);
    }

    [McpServerTool, Description("Get current region EEP environment (ExtEnvironment capability).")]
    public Task<EnvironmentToolResult> EnvGetRegion(CancellationToken cancellationToken)
    {
        return _bot.GetRegionEnvironmentAsync(cancellationToken);
    }

    [McpServerTool, Description("Get parcel EEP environment by local parcel ID (ExtEnvironment capability).")]
    public Task<EnvironmentToolResult> EnvGetParcel(
        [Description("Local parcel ID in current region.")] int parcelId,
        CancellationToken cancellationToken)
    {
        return _bot.GetParcelEnvironmentAsync(parcelId, cancellationToken);
    }

    [McpServerTool, Description("Reset region EEP environment to inherited default (ExtEnvironment DELETE).")]
    public Task<BotToolResult> EnvResetRegion(CancellationToken cancellationToken)
    {
        return _bot.ResetRegionEnvironmentAsync(cancellationToken);
    }

    [McpServerTool, Description("Set region EEP environment from raw LLSD payload text (ExtEnvironment POST).")]
    public Task<BotToolResult> EnvSetRegionRaw(
        [Description("Raw LLSD payload text (JSON or XML). Can be either an EnvironmentData map or a wrapper map with an 'environment' object.") ] string payload,
        [Description("Payload format: auto, json, xml.") ] string payloadFormat,
        CancellationToken cancellationToken)
    {
        return _bot.SetRegionEnvironmentRawAsync(payload, payloadFormat, cancellationToken);
    }

    [McpServerTool, Description("Reset parcel EEP environment to region default (ExtEnvironment DELETE).")]
    public Task<BotToolResult> EnvResetParcel(
        [Description("Local parcel ID in current region.")] int parcelId,
        CancellationToken cancellationToken)
    {
        return _bot.ResetParcelEnvironmentAsync(parcelId, cancellationToken);
    }

    [McpServerTool, Description("Set parcel EEP environment from raw LLSD payload text (ExtEnvironment POST).")]
    public Task<BotToolResult> EnvSetParcelRaw(
        [Description("Local parcel ID in current region.")] int parcelId,
        [Description("Raw LLSD payload text (JSON or XML). Can be either an EnvironmentData map or a wrapper map with an 'environment' object.") ] string payload,
        [Description("Payload format: auto, json, xml.") ] string payloadFormat,
        CancellationToken cancellationToken)
    {
        return _bot.SetParcelEnvironmentRawAsync(parcelId, payload, payloadFormat, cancellationToken);
    }

    [McpServerTool, Description("Get legacy WindLight environment (EnvironmentSettings capability).")]
    public Task<EnvironmentToolResult> EnvGetLegacy(CancellationToken cancellationToken)
    {
        return _bot.GetLegacyEnvironmentAsync(cancellationToken);
    }

    [McpServerTool, Description("Set legacy WindLight environment from raw LLSD payload text.")]
    public Task<BotToolResult> EnvSetLegacyRaw(
        [Description("Raw LLSD payload text (JSON or XML).") ] string payload,
        [Description("Payload format: auto, json, xml.") ] string payloadFormat,
        CancellationToken cancellationToken)
    {
        return _bot.SetLegacyEnvironmentRawAsync(payload, payloadFormat, cancellationToken);
    }

    [McpServerTool, Description("Reset legacy WindLight environment by posting an empty LLSD map.")]
    public Task<BotToolResult> EnvResetLegacy(CancellationToken cancellationToken)
    {
        return _bot.ResetLegacyEnvironmentAsync(cancellationToken);
    }

    [McpServerTool, Description("Move by a relative amount in a direction using walk or fly mode.")]
    public Task<BotToolResult> MoveBy(
        [Description("Direction: north, south, east, west, up, down, forward, back, left, right.")] string direction,
        [Description("Distance in meters.")] float meters,
        [Description("True to fly, false to walk.")] bool fly,
        CancellationToken cancellationToken)
    {
        return _bot.MoveByAsync(direction, meters, fly, cancellationToken);
    }

    [McpServerTool, Description("Walk to an absolute local position in the current region.")]
    public Task<BotToolResult> WalkTo(
        [Description("Local X coordinate (0..256).") ] float x,
        [Description("Local Y coordinate (0..256).") ] float y,
        [Description("Local Z coordinate.")] float z,
        CancellationToken cancellationToken)
    {
        return _bot.MoveToAsync(x, y, z, false, cancellationToken);
    }

    [McpServerTool, Description("Fly to an absolute local position in the current region.")]
    public Task<BotToolResult> FlyTo(
        [Description("Local X coordinate (0..256).") ] float x,
        [Description("Local Y coordinate (0..256).") ] float y,
        [Description("Local Z coordinate.")] float z,
        CancellationToken cancellationToken)
    {
        return _bot.MoveToAsync(x, y, z, true, cancellationToken);
    }

    [McpServerTool, Description("Teleport to an absolute local position, optionally in a named region.")]
    public Task<BotToolResult> TeleportTo(
        [Description("Local X coordinate (0..256).") ] float x,
        [Description("Local Y coordinate (0..256).") ] float y,
        [Description("Local Z coordinate.")] float z,
        [Description("Optional destination region name. If omitted, current region is used.")] string? regionName,
        CancellationToken cancellationToken)
    {
        return _bot.TeleportToAsync(x, y, z, regionName, cancellationToken);
    }

    [McpServerTool, Description("Teleport to a region handle and local position.")]
    public Task<BotToolResult> TeleportToRegionHandle(
        [Description("Destination region handle as an unsigned 64-bit integer string.")] string regionHandle,
        [Description("Local X coordinate (0..256).") ] float x,
        [Description("Local Y coordinate (0..256).") ] float y,
        [Description("Local Z coordinate.")] float z,
        CancellationToken cancellationToken)
    {
        return _bot.TeleportToRegionHandleAsync(regionHandle, x, y, z, cancellationToken);
    }

    [McpServerTool, Description("Stop current movement by canceling autopilot and resetting movement control flags.")]
    public Task<BotToolResult> StopMovement(CancellationToken cancellationToken)
    {
        return _bot.StopMovementAsync(cancellationToken);
    }

    [McpServerTool, Description("Create a new prim shape at a position with scale and rotation.")]
    public Task<PrimCreateResult> PrimCreate(
        [Description("Shape: box, cylinder, prism, sphere, torus, tube, ring.")] string shape,
        [Description("Position X in local region coordinates.")] float x,
        [Description("Position Y in local region coordinates.")] float y,
        [Description("Position Z in local region coordinates.")] float z,
        [Description("Scale X.")] float scaleX,
        [Description("Scale Y.")] float scaleY,
        [Description("Scale Z.")] float scaleZ,
        [Description("Roll in degrees.")] float rollDegrees,
        [Description("Pitch in degrees.")] float pitchDegrees,
        [Description("Yaw in degrees.")] float yawDegrees,
        [Description("Material: Stone, Metal, Glass, Wood, Flesh, Plastic, Rubber, Light.")] string material,
        [Description("Optional object name.")] string? name,
        [Description("Optional object description.")] string? description,
        CancellationToken cancellationToken)
    {
        return _bot.CreatePrimAsync(
            shape,
            x,
            y,
            z,
            scaleX,
            scaleY,
            scaleZ,
            rollDegrees,
            pitchDegrees,
            yawDegrees,
            material,
            name,
            description,
            cancellationToken);
    }

    [McpServerTool, Description("Set prim position by local ID.")]
    public Task<BotToolResult> PrimSetPosition(
        [Description("Prim local ID.")] uint localId,
        [Description("Position X.")] float x,
        [Description("Position Y.")] float y,
        [Description("Position Z.")] float z,
        [Description("True to move only this child prim; false to move linked set.")] bool childOnly,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimPositionAsync(localId, x, y, z, childOnly, cancellationToken);
    }

    [McpServerTool, Description("Set prim scale by local ID.")]
    public Task<BotToolResult> PrimSetScale(
        [Description("Prim local ID.")] uint localId,
        [Description("Scale X.")] float x,
        [Description("Scale Y.")] float y,
        [Description("Scale Z.")] float z,
        [Description("True to scale only this child prim; false for linked set.")] bool childOnly,
        [Description("True to request uniform resize.")] bool uniform,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimScaleAsync(localId, x, y, z, childOnly, uniform, cancellationToken);
    }

    [McpServerTool, Description("Set prim rotation in euler degrees by local ID.")]
    public Task<BotToolResult> PrimSetRotation(
        [Description("Prim local ID.")] uint localId,
        [Description("Roll in degrees.")] float rollDegrees,
        [Description("Pitch in degrees.")] float pitchDegrees,
        [Description("Yaw in degrees.")] float yawDegrees,
        [Description("True to rotate only this child prim; false for linked set.")] bool childOnly,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimRotationEulerAsync(localId, rollDegrees, pitchDegrees, yawDegrees, childOnly, cancellationToken);
    }

    [McpServerTool, Description("Set prim texture by local ID, optionally for an individual face.")]
    public Task<BotToolResult> PrimSetTexture(
        [Description("Prim local ID.")] uint localId,
        [Description("Texture UUID.")] string textureId,
        [Description("Face index (0..44), or -1 for default texture.")] int faceIndex,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimTextureAsync(localId, textureId, faceIndex, cancellationToken);
    }

    [McpServerTool, Description("Set detailed texture/material parameters on a prim face or default face.")]
    public Task<BotToolResult> PrimSetFaceParams(
        [Description("Prim local ID.")] uint localId,
        [Description("Face index (0..44), or -1 for default face.")] int faceIndex,
        [Description("Optional red tint (0..1).") ] float? red,
        [Description("Optional green tint (0..1).") ] float? green,
        [Description("Optional blue tint (0..1).") ] float? blue,
        [Description("Optional alpha (0..1).") ] float? alpha,
        [Description("Optional texture repeat U (scale).") ] float? repeatU,
        [Description("Optional texture repeat V (scale).") ] float? repeatV,
        [Description("Optional texture offset U (-1..1).") ] float? offsetU,
        [Description("Optional texture offset V (-1..1).") ] float? offsetV,
        [Description("Optional texture rotation in radians.") ] float? rotationRadians,
        [Description("Optional glow amount (0..1).") ] float? glow,
        [Description("Optional fullbright toggle.") ] bool? fullbright,
        [Description("Optional shiny value: None, Low, Medium, High.") ] string? shiny,
        [Description("Optional bump value from Bumpiness enum (e.g. None, Brightness, Darkness).") ] string? bump,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimFaceParamsAsync(
            localId,
            faceIndex,
            red,
            green,
            blue,
            alpha,
            repeatU,
            repeatV,
            offsetU,
            offsetV,
            rotationRadians,
            glow,
            fullbright,
            shiny,
            bump,
            cancellationToken);
    }

    [McpServerTool, Description("Nudge UV parameters on a prim face by delta values for iterative alignment.")]
    public Task<BotToolResult> PrimNudgeFaceUv(
        [Description("Prim local ID.")] uint localId,
        [Description("Face index (0..44), or -1 for default face.")] int faceIndex,
        [Description("Optional delta for repeatU (scale U).") ] float? deltaRepeatU,
        [Description("Optional delta for repeatV (scale V).") ] float? deltaRepeatV,
        [Description("Optional delta for offsetU.") ] float? deltaOffsetU,
        [Description("Optional delta for offsetV.") ] float? deltaOffsetV,
        [Description("Optional delta for rotation in radians.") ] float? deltaRotationRadians,
        CancellationToken cancellationToken)
    {
        return _bot.NudgePrimFaceUvAsync(
            localId,
            faceIndex,
            deltaRepeatU,
            deltaRepeatV,
            deltaOffsetU,
            deltaOffsetV,
            deltaRotationRadians,
            cancellationToken);
    }

    [McpServerTool, Description("Apply a UV preset to a prim face: fit/reset, tile2x2, tile4x4, flipU, flipV, rotate90, rotate180, rotate270, center.")]
    public Task<BotToolResult> PrimApplyUvPreset(
        [Description("Prim local ID.")] uint localId,
        [Description("Face index (0..44), or -1 for default face.")] int faceIndex,
        [Description("Preset name: fit, reset, tile2x2, tile4x4, flipU, flipV, rotate90, rotate180, rotate270, center.")] string preset,
        CancellationToken cancellationToken)
    {
        return _bot.ApplyPrimFaceUvPresetAsync(localId, faceIndex, preset, cancellationToken);
    }

    [McpServerTool, Description("Set UV tiling to NxN (same repeat value for U and V) on a prim face.")]
    public Task<BotToolResult> PrimTileUv(
        [Description("Prim local ID.")] uint localId,
        [Description("Face index (0..44), or -1 for default face.")] int faceIndex,
        [Description("Tiling repeat value N (must be > 0).") ] float repeat,
        CancellationToken cancellationToken)
    {
        return _bot.TilePrimFaceUvAsync(localId, faceIndex, repeat, cancellationToken);
    }

    [McpServerTool, Description("Set UV tiling with separate U and V repeat values on a prim face.")]
    public Task<BotToolResult> PrimTileUvNonUniform(
        [Description("Prim local ID.")] uint localId,
        [Description("Face index (0..44), or -1 for default face.")] int faceIndex,
        [Description("Repeat value for U axis (must be > 0).") ] float repeatU,
        [Description("Repeat value for V axis (must be > 0).") ] float repeatV,
        CancellationToken cancellationToken)
    {
        return _bot.TilePrimFaceUvNonUniformAsync(localId, faceIndex, repeatU, repeatV, cancellationToken);
    }

    [McpServerTool, Description("Set prim name by local ID.")]
    public Task<BotToolResult> PrimSetName(
        [Description("Prim local ID.")] uint localId,
        [Description("New prim name.")] string name,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimNameAsync(localId, name, cancellationToken);
    }

    [McpServerTool, Description("Set prim description by local ID.")]
    public Task<BotToolResult> PrimSetDescription(
        [Description("Prim local ID.")] uint localId,
        [Description("New prim description (use empty string to clear).") ] string description,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimDescriptionAsync(localId, description, cancellationToken);
    }

    [McpServerTool, Description("Link multiple prims into a linkset using comma-separated local IDs.")]
    public Task<BotToolResult> PrimLink(
        [Description("Comma-separated local IDs; first ID becomes root.")] string localIdsCsv,
        CancellationToken cancellationToken)
    {
        return _bot.LinkPrimsAsync(localIdsCsv, cancellationToken);
    }

    [McpServerTool, Description("Unlink one or more prims using comma-separated local IDs.")]
    public Task<BotToolResult> PrimUnlink(
        [Description("Comma-separated local IDs to unlink.")] string localIdsCsv,
        CancellationToken cancellationToken)
    {
        return _bot.UnlinkPrimsAsync(localIdsCsv, cancellationToken);
    }

    [McpServerTool, Description("Clone an existing prim by local ID with a position offset.")]
    public Task<PrimCreateResult> PrimClone(
        [Description("Source prim local ID to clone.")] uint sourceLocalId,
        [Description("Offset X from source position.")] float offsetX,
        [Description("Offset Y from source position.")] float offsetY,
        [Description("Offset Z from source position.")] float offsetZ,
        [Description("Copy source textures onto clone.")] bool copyTextures,
        [Description("Copy source object name onto clone.")] bool copyName,
        [Description("Copy source object description onto clone.")] bool copyDescription,
        CancellationToken cancellationToken)
    {
        return _bot.ClonePrimAsync(sourceLocalId, offsetX, offsetY, offsetZ, copyTextures, copyName, copyDescription, cancellationToken);
    }

    [McpServerTool, Description("Inspect a prim from local simulator cache by local ID.")]
    public Task<PrimInspectResult> PrimInspect(
        [Description("Prim local ID.")] uint localId,
        [Description("Include explicit per-face texture overrides in the response.")] bool includeFaceTextures,
        CancellationToken cancellationToken)
    {
        return _bot.InspectPrimAsync(localId, includeFaceTextures, cancellationToken);
    }

    [McpServerTool, Description("Select a prim by local ID. Can optionally auto-deselect immediately.")]
    public Task<BotToolResult> PrimSelect(
        [Description("Prim local ID.")] uint localId,
        [Description("If true, deselect immediately after selection request.")] bool automaticDeselect,
        CancellationToken cancellationToken)
    {
        return _bot.SelectPrimAsync(localId, automaticDeselect, cancellationToken);
    }

    [McpServerTool, Description("Deselect a prim by local ID.")]
    public Task<BotToolResult> PrimDeselect(
        [Description("Prim local ID.")] uint localId,
        CancellationToken cancellationToken)
    {
        return _bot.DeselectPrimAsync(localId, cancellationToken);
    }

    [McpServerTool, Description("Delete (de-rez) a prim by local ID back to inventory.")]
    public Task<BotToolResult> PrimDelete(
        [Description("Prim local ID.")] uint localId,
        CancellationToken cancellationToken)
    {
        return _bot.DeletePrimAsync(localId, cancellationToken);
    }

    [McpServerTool, Description("Delete (de-rez) multiple prims by comma-separated local IDs.")]
    public Task<BotToolResult> PrimDeleteMany(
        [Description("Comma-separated local IDs to delete.")] string localIdsCsv,
        CancellationToken cancellationToken)
    {
        return _bot.DeleteManyPrimsAsync(localIdsCsv, cancellationToken);
    }

    [McpServerTool, Description("Find prims by object name (cache search in current simulator).")]
    public Task<PrimQueryResult> PrimFindByName(
        [Description("Name fragment to search for.")] string name,
        [Description("Maximum number of results (1..500).") ] int maxResults,
        [Description("If true, use case-sensitive matching.")] bool caseSensitive,
        CancellationToken cancellationToken)
    {
        return _bot.FindPrimsByNameAsync(name, maxResults, caseSensitive, cancellationToken);
    }

    [McpServerTool, Description("List nearby prims around the bot in the current simulator cache.")]
    public Task<PrimQueryResult> PrimListNearby(
        [Description("Radius in meters.")] float radiusMeters,
        [Description("Maximum number of results (1..500).") ] int maxResults,
        CancellationToken cancellationToken)
    {
        return _bot.ListNearbyPrimsAsync(radiusMeters, maxResults, cancellationToken);
    }

    [McpServerTool, Description("List inventory entries under a folder UUID (or root if omitted).")]
    public Task<InventoryQueryResult> InventoryList(
        [Description("Optional folder UUID. Leave empty for inventory root.")] string? folderId,
        [Description("True to recurse into subfolders.")] bool recursive,
        [Description("Maximum number of results (1..2000).") ] int maxResults,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryListAsync(folderId, recursive, maxResults, cancellationToken);
    }

    [McpServerTool, Description("Give one inventory item to another avatar UUID.")]
    public Task<BotToolResult> InventoryGiveItem(
        [Description("Inventory item UUID to send.")] string itemId,
        [Description("Recipient avatar UUID.")] string recipientAgentId,
        [Description("True to show transfer beam effect.")] bool withBeamEffect,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryGiveItemAsync(itemId, recipientAgentId, withBeamEffect, cancellationToken);
    }

    [McpServerTool, Description("Give an inventory folder to another avatar UUID.")]
    public Task<BotToolResult> InventoryGiveFolder(
        [Description("Inventory folder UUID to send.")] string folderId,
        [Description("Recipient avatar UUID.")] string recipientAgentId,
        [Description("True to show transfer beam effect.")] bool withBeamEffect,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryGiveFolderAsync(folderId, recipientAgentId, withBeamEffect, cancellationToken);
    }

    [McpServerTool, Description("List task inventory (contents) for an in-world object local ID.")]
    public Task<InventoryQueryResult> TaskInventoryList(
        [Description("Object local ID in current simulator.")] uint objectLocalId,
        [Description("Optional object UUID; if omitted, resolves from simulator cache by local ID.")] string? objectId,
        [Description("Maximum number of results (1..2000).") ] int maxResults,
        CancellationToken cancellationToken)
    {
        return _bot.TaskInventoryListAsync(objectLocalId, objectId, maxResults, cancellationToken);
    }

    [McpServerTool, Description("Request moving/copying a task-inventory item from an object into agent inventory.")]
    public Task<BotToolResult> TaskInventoryTake(
        [Description("Object local ID in current simulator.")] uint objectLocalId,
        [Description("Task-inventory item UUID on the object.")] string taskItemId,
        [Description("Optional destination folder UUID. If omitted, uses default folder for item asset type.")] string? destinationFolderId,
        [Description("Optional object UUID; if omitted, resolves from simulator cache by local ID.")] string? objectId,
        CancellationToken cancellationToken)
    {
        return _bot.TaskInventoryTakeAsync(objectLocalId, taskItemId, destinationFolderId, objectId, cancellationToken);
    }

    [McpServerTool, Description("Upload a local file path or HTTP/HTTPS URL as a new inventory item asset.")]
    public Task<AssetTransferResult> AssetUploadInventory(
        [Description("Source path or URL.")] string source,
        [Description("Asset type (e.g. texture, notecard, lsltext, animation, sound) or 'auto' to infer from extension.") ] string assetType,
        [Description("Inventory type (e.g. texture, notecard, lsl, animation, sound) or 'auto' to infer from extension.") ] string inventoryType,
        [Description("New inventory item name.")] string name,
        [Description("New inventory item description (empty allowed).") ] string description,
        [Description("Optional destination folder UUID.")] string? folderId,
        CancellationToken cancellationToken)
    {
        return _bot.AssetUploadInventoryAsync(source, assetType, inventoryType, name, description, folderId, cancellationToken);
    }

    [McpServerTool, Description("Download an asset by UUID and type. outputMode supports: both, base64, tempfile.")]
    public Task<AssetDownloadResult> AssetDownload(
        [Description("Asset UUID.")] string assetId,
        [Description("Asset type name.")] string assetType,
        [Description("Output mode: both, base64, tempfile.")] string outputMode,
        [Description("Optional filename hint when output includes tempfile.")] string? fileNameHint,
        CancellationToken cancellationToken)
    {
        return _bot.AssetDownloadAsync(assetId, assetType, outputMode, fileNameHint, cancellationToken);
    }

    [McpServerTool, Description("Download a texture by UUID. outputMode supports: both, base64, tempfile.")]
    public Task<AssetDownloadResult> TextureDownload(
        [Description("Texture UUID.")] string textureId,
        [Description("Output mode: both, base64, tempfile.")] string outputMode,
        [Description("Optional filename hint when output includes tempfile.")] string? fileNameHint,
        CancellationToken cancellationToken)
    {
        return _bot.TextureDownloadAsync(textureId, outputMode, fileNameHint, cancellationToken);
    }

    [McpServerTool, Description("Add a policy rule for incoming inventory offers. First matching rule decides accept/decline.")]
    public BotToolResult InventoryOfferPolicyRuleAdd(
        [Description("Rule name.")] string name,
        [Description("Action: accept or decline.")] string action,
        [Description("Optional exact sender avatar UUID match.")] string? senderAgentId,
        [Description("Optional sender name substring match (case-insensitive).") ] string? senderNameContains,
        [Description("Optional exact asset type match.")] string? assetType,
        [Description("Optional match on task-origin offers (true/false).") ] bool? fromTask,
        [Description("Optional destination folder UUID override for accepted offers.")] string? destinationFolderId)
    {
        return _bot.InventoryOfferPolicyRuleAdd(name, action, senderAgentId, senderNameContains, assetType, fromTask, destinationFolderId);
    }

    [McpServerTool, Description("List all inventory-offer policy rules.")]
    public InventoryOfferPolicyResult InventoryOfferPolicyRulesList()
    {
        return _bot.InventoryOfferPolicyRulesList();
    }

    [McpServerTool, Description("Clear all inventory-offer policy rules.")]
    public BotToolResult InventoryOfferPolicyRulesClear()
    {
        return _bot.InventoryOfferPolicyRulesClear();
    }

    [McpServerTool, Description("List recent incoming inventory-offer events and decisions.")]
    public InventoryOfferHistoryResult InventoryOfferHistoryList(
        [Description("Maximum entries to return (1..200).") ] int maxResults)
    {
        return _bot.InventoryOfferHistoryList(maxResults);
    }

    [McpServerTool, Description("Persist inventory-offer policy rules to JSON file.")]
    public Task<InventoryOfferPolicyResult> InventoryOfferPolicyRulesSave(
        [Description("Optional target JSON file path; defaults to configured policy file.")] string? filePath,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryOfferPolicyRulesSaveAsync(filePath, cancellationToken);
    }

    [McpServerTool, Description("Load inventory-offer policy rules from JSON file.")]
    public Task<InventoryOfferPolicyResult> InventoryOfferPolicyRulesLoad(
        [Description("Optional source JSON file path; defaults to configured policy file.")] string? filePath,
        [Description("If true, replaces existing in-memory rules before loading.")] bool replaceExisting,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryOfferPolicyRulesLoadAsync(filePath, replaceExisting, cancellationToken);
    }

    [McpServerTool, Description("List currently worn wearables and attachments.")]
    public Task<AppearanceStateResult> AppearanceListWorn(CancellationToken cancellationToken)
    {
        return _bot.AppearanceListWornAsync(cancellationToken);
    }

    [McpServerTool, Description("Wear outfit items from an inventory folder UUID.")]
    public Task<BotToolResult> AppearanceWearFolder(
        [Description("Folder UUID containing outfit items/links.")] string folderId,
        [Description("True to replace current outfit, false to add.")] bool replaceItems,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceWearFolderAsync(folderId, replaceItems, cancellationToken);
    }

    [McpServerTool, Description("Attach an inventory attachment/object item.")]
    public Task<BotToolResult> AppearanceAttachItem(
        [Description("Inventory item UUID.")] string itemId,
        [Description("Optional attachment point enum name (e.g. Chest, RightHand).") ] string? attachmentPoint,
        [Description("True to replace existing item on the point.")] bool replace,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceAttachItemAsync(itemId, attachmentPoint, replace, cancellationToken);
    }

    [McpServerTool, Description("Detach a currently worn attachment item by inventory item UUID.")]
    public Task<BotToolResult> AppearanceDetachItem(
        [Description("Inventory item UUID.")] string itemId,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceDetachItemAsync(itemId, cancellationToken);
    }

    [McpServerTool, Description("Request appearance rebake/update.")]
    public Task<BotToolResult> AppearanceRebake(
        [Description("True to force a rebake, false for normal update.")] bool forceRebake,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceRebakeAsync(forceRebake, cancellationToken);
    }

    [McpServerTool, Description("Upload script source (path or URL) to an existing agent inventory script item.")]
    public Task<ScriptUpdateResult> ScriptUploadAgent(
        [Description("Source path or URL containing script text.")] string source,
        [Description("Script inventory item UUID.")] string itemId,
        [Description("True for mono target, false for lsl2 target.")] bool mono,
        CancellationToken cancellationToken)
    {
        return _bot.ScriptUploadAgentAsync(source, itemId, mono, cancellationToken);
    }

    [McpServerTool, Description("Upload script source (path or URL) to an existing task/object inventory script item.")]
    public Task<ScriptUpdateResult> ScriptUploadTask(
        [Description("Source path or URL containing script text.")] string source,
        [Description("Script task-inventory item UUID.")] string itemId,
        [Description("Object UUID that contains the task script item.")] string objectId,
        [Description("True for mono target, false for lsl2 target.")] bool mono,
        [Description("Desired running state after upload.")] bool running,
        CancellationToken cancellationToken)
    {
        return _bot.ScriptUploadTaskAsync(source, itemId, objectId, mono, running, cancellationToken);
    }

    [McpServerTool, Description("Copy a script from agent inventory into an object's task inventory.")]
    public Task<BotToolResult> ScriptCopyInventoryToTask(
        [Description("Target object local ID.")] uint objectLocalId,
        [Description("Agent inventory script item UUID.")] string inventoryScriptItemId,
        [Description("True to run script after copy.")] bool enableScript,
        CancellationToken cancellationToken)
    {
        return _bot.ScriptCopyInventoryToTaskAsync(objectLocalId, inventoryScriptItemId, enableScript, cancellationToken);
    }

    [McpServerTool, Description("Get running state for a script item in task inventory.")]
    public Task<ScriptRunningResult> ScriptGetTaskRunning(
        [Description("Object UUID containing the script.")] string objectId,
        [Description("Script item UUID.")] string scriptItemId,
        CancellationToken cancellationToken)
    {
        return _bot.ScriptGetTaskRunningAsync(objectId, scriptItemId, cancellationToken);
    }

    [McpServerTool, Description("Set running state for a script item in task inventory and optionally verify.")]
    public Task<ScriptRunningResult> ScriptSetTaskRunning(
        [Description("Object UUID containing the script.")] string objectId,
        [Description("Script item UUID.")] string scriptItemId,
        [Description("Desired running state.")] bool running,
        [Description("If true, requests and waits for status verification reply.")] bool verifyAfterSet,
        CancellationToken cancellationToken)
    {
        return _bot.ScriptSetTaskRunningAsync(objectId, scriptItemId, running, verifyAfterSet, cancellationToken);
    }
}