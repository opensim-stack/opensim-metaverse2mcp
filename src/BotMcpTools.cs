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

    [McpServerTool, Description("Start playing an animation by built-in name (e.g. DANCE1, WAVE, SIT) or UUID.")]
    public Task<BotToolResult> AnimationStart(
        [Description("Animation name from the Animations class (e.g. DANCE1, CLAP, SIT) or a UUID string.")] string animation,
        CancellationToken cancellationToken)
    {
        return _bot.AnimationStartAsync(animation, cancellationToken);
    }

    [McpServerTool, Description("Stop playing an animation by built-in name or UUID.")]
    public Task<BotToolResult> AnimationStop(
        [Description("Animation name from the Animations class (e.g. DANCE1, CLAP, SIT) or a UUID string.")] string animation,
        CancellationToken cancellationToken)
    {
        return _bot.AnimationStopAsync(animation, cancellationToken);
    }

    [McpServerTool, Description("List all built-in animation names and UUIDs from the Animations class.")]
    public Task<AnimationListResult> AnimationsList(CancellationToken cancellationToken)
    {
        return _bot.AnimationsListAsync(cancellationToken);
    }

    [McpServerTool, Description("List currently active/signaled animations for the bot.")]
    public Task<AnimationListResult> ActiveAnimations(CancellationToken cancellationToken)
    {
        return _bot.ActiveAnimationsAsync(cancellationToken);
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

    [McpServerTool, Description("Get parcel details for the parcel under the bot's current position.")]
    public Task<DataToolResult> ParcelGetCurrent(
        [Description("Include allow/ban list entries when true.")] bool includeAccessLists,
        [Description("Force a fresh simulator parcel-map refresh before resolving current parcel.")] bool forceRefresh,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelGetCurrentAsync(includeAccessLists, forceRefresh, cancellationToken);
    }

    [McpServerTool, Description("Get parcel details by parcel local ID in the current simulator.")]
    public Task<DataToolResult> ParcelGetByLocalId(
        [Description("Parcel local ID.")] int localId,
        [Description("Include allow/ban list entries when true.")] bool includeAccessLists,
        [Description("Force a fresh simulator parcel-map refresh before reading parcel data.")] bool forceRefresh,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelGetByLocalIdAsync(localId, includeAccessLists, forceRefresh, cancellationToken);
    }

    [McpServerTool, Description("Edit parcel text/media fields (name, description, music URL, media URL).")]
    public Task<BotToolResult> ParcelSetInfo(
        [Description("Parcel local ID.")] int localId,
        [Description("Optional new parcel name (null leaves unchanged).") ] string? name,
        [Description("Optional new parcel description (null leaves unchanged).") ] string? description,
        [Description("Optional new parcel music stream URL (null leaves unchanged).") ] string? musicUrl,
        [Description("Optional new parcel media URL (null leaves unchanged).") ] string? mediaUrl,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelSetInfoAsync(localId, name, description, musicUrl, mediaUrl, cancellationToken);
    }

    [McpServerTool, Description("Set parcel landing behavior and optional landing/look-at vectors.")]
    public Task<BotToolResult> ParcelSetLanding(
        [Description("Parcel local ID.")] int localId,
        [Description("Landing type: none, landingpoint, direct.")] string landingType,
        [Description("Optional landing X (required for landingpoint).") ] float? x,
        [Description("Optional landing Y (required for landingpoint).") ] float? y,
        [Description("Optional landing Z (required for landingpoint).") ] float? z,
        [Description("Optional look-at X.") ] float? lookAtX,
        [Description("Optional look-at Y.") ] float? lookAtY,
        [Description("Optional look-at Z.") ] float? lookAtZ,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelSetLandingAsync(localId, landingType, x, y, z, lookAtX, lookAtY, lookAtZ, cancellationToken);
    }

    [McpServerTool, Description("Fetch parcel allowlist/banlist entries by parcel local ID.")]
    public Task<DataToolResult> ParcelAccessListGet(
        [Description("Parcel local ID.")] int localId,
        [Description("List type: both, access, ban.")] string listType,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelAccessListGetAsync(localId, listType, cancellationToken);
    }

    [McpServerTool, Description("Eject an avatar from the current parcel, with optional ban.")]
    public Task<BotToolResult> ParcelEjectUser(
        [Description("Target avatar UUID.")] string targetAgentId,
        [Description("True to ban while ejecting.")] bool ban,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelEjectUserAsync(targetAgentId, ban, cancellationToken);
    }

    [McpServerTool, Description("Join adjacent parcels in the current simulator using a bounding rectangle.")]
    public Task<BotToolResult> ParcelJoin(
        [Description("West bound (0..256).") ] float west,
        [Description("South bound (0..256).") ] float south,
        [Description("East bound (0..256).") ] float east,
        [Description("North bound (0..256).") ] float north,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelJoinAsync(west, south, east, north, cancellationToken);
    }

    [McpServerTool, Description("Subdivide parcels in the current simulator using a bounding rectangle.")]
    public Task<BotToolResult> ParcelSubdivide(
        [Description("West bound (0..256).") ] float west,
        [Description("South bound (0..256).") ] float south,
        [Description("East bound (0..256).") ] float east,
        [Description("North bound (0..256).") ] float north,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelSubdivideAsync(west, south, east, north, cancellationToken);
    }

    [McpServerTool, Description("Inspect parcel permission signals and likely authorization blockers for parcel operations.")]
    public Task<DataToolResult> ParcelPermissionDiagnostics(
        [Description("Optional parcel local ID. If omitted, uses current parcel under the bot.")] int? localId,
        [Description("If true, refresh parcel map cache before diagnostics.")] bool forceRefresh,
        CancellationToken cancellationToken)
    {
        return _bot.ParcelPermissionDiagnosticsAsync(localId, forceRefresh, cancellationToken);
    }

    [McpServerTool, Description("Sample terrain heights from cached land patches on a regular grid.")]
    public Task<DataToolResult> TerrainHeightmapSample(
        [Description("Grid sampling step in meters (1..64).") ] int stepMeters,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainHeightmapSampleAsync(stepMeters, cancellationToken);
    }

    [McpServerTool, Description("Export current terrain heightmap as 256x256 float32 RAW (.r32) file.")]
    public Task<DataToolResult> TerrainHeightmapExportRaw(
        [Description("Optional output path. If omitted, a temp file path is generated.")] string? outputPath,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainHeightmapExportRawAsync(outputPath, cancellationToken);
    }

    [McpServerTool, Description("Import and upload a 256x256 float32 RAW terrain heightmap (.r32) to the current region.")]
    public Task<BotToolResult> TerrainHeightmapImportRaw(
        [Description("Source file path or HTTP/HTTPS URL for .r32 data.")] string source,
        [Description("Optional uploaded file name hint; '.r32' is appended if missing.")] string? fileNameHint,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainHeightmapImportRawAsync(source, fileNameHint, cancellationToken);
    }

    [McpServerTool, Description("Verify terrain patch-cache coverage by sampling until a target coverage ratio or timeout is reached.")]
    public Task<DataToolResult> TerrainPatchCacheVerify(
        [Description("Grid sampling step in meters (1..64).") ] int stepMeters,
        [Description("Required successful sample ratio (0..1].") ] float minimumCoverageRatio,
        [Description("Maximum verification time in seconds (1..120).") ] int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainPatchCacheVerifyAsync(stepMeters, minimumCoverageRatio, timeoutSeconds, cancellationToken);
    }

    [McpServerTool, Description("Diff two terrain RAW sources (or 'current') and return changed-point summary plus sample deltas.")]
    public Task<DataToolResult> TerrainPatchDiffRaw(
        [Description("First source: file path, HTTP/HTTPS URL, or 'current'.")] string sourceA,
        [Description("Second source: file path, HTTP/HTTPS URL, or 'current'.")] string sourceB,
        [Description("Minimum absolute delta in meters to count as changed.")] float minDeltaMeters,
        [Description("Maximum sample deltas to include in response (0..1000).") ] int maxSamples,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainPatchDiffRawAsync(sourceA, sourceB, minDeltaMeters, maxSamples, cancellationToken);
    }

    [McpServerTool, Description("Apply a constant terrain height offset in a bounding rectangle, then upload as RAW terrain patch.")]
    public Task<BotToolResult> TerrainPatchApplyOffset(
        [Description("West bound (0..256).") ] float west,
        [Description("South bound (0..256).") ] float south,
        [Description("East bound (0..256).") ] float east,
        [Description("North bound (0..256).") ] float north,
        [Description("Height delta in meters (positive raises, negative lowers).") ] float deltaMeters,
        [Description("Optional minimum clamp height after offset.")] float? minHeight,
        [Description("Optional maximum clamp height after offset.")] float? maxHeight,
        [Description("Optional upload filename hint; '.r32' is appended if missing.")] string? fileNameHint,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainPatchApplyOffsetAsync(west, south, east, north, deltaMeters, minHeight, maxHeight, fileNameHint, cancellationToken);
    }

    [McpServerTool, Description("Apply a constant terrain height offset in a bounding rectangle using a provided RAW base source, then upload as terrain RAW.")]
    public Task<BotToolResult> TerrainPatchApplyOffsetRaw(
        [Description("Base source: file path, HTTP/HTTPS URL, or 'current'.")] string source,
        [Description("West bound (0..256).") ] float west,
        [Description("South bound (0..256).") ] float south,
        [Description("East bound (0..256).") ] float east,
        [Description("North bound (0..256).") ] float north,
        [Description("Height delta in meters (positive raises, negative lowers).") ] float deltaMeters,
        [Description("Optional minimum clamp height after offset.")] float? minHeight,
        [Description("Optional maximum clamp height after offset.")] float? maxHeight,
        [Description("Optional upload filename hint; '.r32' is appended if missing.")] string? fileNameHint,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainPatchApplyOffsetRawAsync(source, west, south, east, north, deltaMeters, minHeight, maxHeight, fileNameHint, cancellationToken);
    }

    [McpServerTool, Description("Run a terrain terraform operation on a parcel local ID or explicit area bounds.")]
    public Task<BotToolResult> TerrainTerraform(
        [Description("Optional parcel local ID. If omitted, west/south/east/north are required.")] int? localId,
        [Description("Optional west bound when localId is omitted.")] float? west,
        [Description("Optional south bound when localId is omitted.")] float? south,
        [Description("Optional east bound when localId is omitted.")] float? east,
        [Description("Optional north bound when localId is omitted.")] float? north,
        [Description("Action: level, raise, lower, smooth, noise, revert.")] string action,
        [Description("Brush size: small, medium, large.")] string brushSize,
        [Description("Terraform duration/intensity seconds (1..120).") ] int seconds,
        CancellationToken cancellationToken)
    {
        return _bot.TerrainTerraformAsync(localId, west, south, east, north, action, brushSize, seconds, cancellationToken);
    }

    [McpServerTool, Description("Fetch estate metadata (name, owner, flags, sun hour) from current region.")]
    public Task<DataToolResult> EstateGetInfo(CancellationToken cancellationToken)
    {
        return _bot.EstateGetInfoAsync(cancellationToken);
    }

    [McpServerTool, Description("Fetch estate covenant metadata (covenant asset ID, owner, timestamp).")]
    public Task<DataToolResult> EstateGetCovenant(CancellationToken cancellationToken)
    {
        return _bot.EstateGetCovenantAsync(cancellationToken);
    }

    [McpServerTool, Description("Request a region restart countdown.")]
    public Task<BotToolResult> EstateRestartRegion(
        [Description("Delay in seconds; simulator clamps to 30..240.")] int delaySeconds,
        CancellationToken cancellationToken)
    {
        return _bot.EstateRestartRegionAsync(delaySeconds, cancellationToken);
    }

    [McpServerTool, Description("Cancel a pending region restart countdown.")]
    public Task<BotToolResult> EstateCancelRestart(CancellationToken cancellationToken)
    {
        return _bot.EstateCancelRestartAsync(cancellationToken);
    }

    [McpServerTool, Description("Broadcast an administrative notice message to the current region or full estate.")]
    public Task<BotToolResult> EstateBroadcastMessage(
        [Description("Message body.")] string message,
        [Description("True for estate-wide notice, false for current region only.")] bool estateWide,
        CancellationToken cancellationToken)
    {
        return _bot.EstateBroadcastMessageAsync(message, estateWide, cancellationToken);
    }

    [McpServerTool, Description("Get the region automatic restart schedule (RegionSchedule capability).")]
    public Task<DataToolResult> EstateRestartScheduleGet(CancellationToken cancellationToken)
    {
        return _bot.EstateRestartScheduleGetAsync(cancellationToken);
    }

    [McpServerTool, Description("Set or clear the region automatic restart schedule.")]
    public Task<BotToolResult> EstateRestartScheduleSet(
        [Description("Mode: daily, weekly, off.")] string mode,
        [Description("CSV day list for weekly mode (sun,mon,tue,wed,thu,fri,sat).") ] string? daysCsv,
        [Description("UTC time as HH:mm or HH:mm:ss.")] string timeUtc,
        CancellationToken cancellationToken)
    {
        return _bot.EstateRestartScheduleSetAsync(mode, daysCsv, timeUtc, cancellationToken);
    }

    [McpServerTool, Description("List current groups the bot is a member of.")]
    public Task<DataToolResult> GroupListCurrent(
        [Description("Include detailed group profile fields in the result payload.")] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupListCurrentAsync(includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Get full profile data for a group UUID.")]
    public Task<DataToolResult> GroupGetProfile(
        [Description("Group UUID.")] string groupId,
        CancellationToken cancellationToken)
    {
        return _bot.GroupGetProfileAsync(groupId, cancellationToken);
    }

    [McpServerTool, Description("Get members for a group UUID.")]
    public Task<DataToolResult> GroupGetMembers(
        [Description("Group UUID.")] string groupId,
        [Description("Include detailed member fields (powers, contribution, online status).") ] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupGetMembersAsync(groupId, includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Get roles for a group UUID.")]
    public Task<DataToolResult> GroupGetRoles(
        [Description("Group UUID.")] string groupId,
        [Description("Include detailed role fields (description, powers).") ] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupGetRolesAsync(groupId, includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Get role-to-member mappings for a group UUID.")]
    public Task<DataToolResult> GroupGetRoleMembers(
        [Description("Group UUID.")] string groupId,
        [Description("Include full per-role member UUID lists.")] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupGetRoleMembersAsync(groupId, includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Get title definitions for a group UUID.")]
    public Task<DataToolResult> GroupGetTitles(
        [Description("Group UUID.")] string groupId,
        [Description("Include detailed title fields.")] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupGetTitlesAsync(groupId, includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Set the active group for the bot avatar.")]
    public Task<BotToolResult> GroupSetActive(
        [Description("Group UUID.")] string groupId,
        CancellationToken cancellationToken)
    {
        return _bot.GroupSetActiveAsync(groupId, cancellationToken);
    }

    [McpServerTool, Description("Set the active title role for the bot in a group.")]
    public Task<BotToolResult> GroupSetActiveTitle(
        [Description("Group UUID.")] string groupId,
        [Description("Role UUID used as active title.")] string roleId,
        CancellationToken cancellationToken)
    {
        return _bot.GroupSetActiveTitleAsync(groupId, roleId, cancellationToken);
    }

    [McpServerTool, Description("Create a group role from a structured role payload.")]
    public Task<BotToolResult> GroupRoleCreate(
        [Description("Group UUID.")] string groupId,
        [Description("Structured role payload: name, optional title/description, and optional powers (CSV enum names or numeric bitmask).") ] GroupRoleUpdateInput role,
        CancellationToken cancellationToken)
    {
        return _bot.GroupRoleCreateAsync(groupId, role, cancellationToken);
    }

    [McpServerTool, Description("Update an existing group role from a structured role payload.")]
    public Task<BotToolResult> GroupRoleUpdate(
        [Description("Group UUID.")] string groupId,
        [Description("Role UUID.")] string roleId,
        [Description("Structured role payload: name, optional title/description, and optional powers (CSV enum names or numeric bitmask).") ] GroupRoleUpdateInput role,
        CancellationToken cancellationToken)
    {
        return _bot.GroupRoleUpdateAsync(groupId, roleId, role, cancellationToken);
    }

    [McpServerTool, Description("Delete a role from a group.")]
    public Task<BotToolResult> GroupRoleDelete(
        [Description("Group UUID.")] string groupId,
        [Description("Role UUID.")] string roleId,
        CancellationToken cancellationToken)
    {
        return _bot.GroupRoleDeleteAsync(groupId, roleId, cancellationToken);
    }

    [McpServerTool, Description("Assign a group member avatar UUID to a role.")]
    public Task<BotToolResult> GroupRoleAddMember(
        [Description("Group UUID.")] string groupId,
        [Description("Role UUID.")] string roleId,
        [Description("Member avatar UUID.")] string memberAgentId,
        [Description("When true, perform read-back verification after submit.")] bool verifyAfterSubmit,
        [Description("Seconds to wait for verification when verifyAfterSubmit is true (1..60).") ] int verifyWaitSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.GroupRoleAddMemberAsync(groupId, roleId, memberAgentId, verifyAfterSubmit, verifyWaitSeconds, cancellationToken);
    }

    [McpServerTool, Description("Remove a group member avatar UUID from a role.")]
    public Task<BotToolResult> GroupRoleRemoveMember(
        [Description("Group UUID.")] string groupId,
        [Description("Role UUID.")] string roleId,
        [Description("Member avatar UUID.")] string memberAgentId,
        [Description("When true, perform read-back verification after submit.")] bool verifyAfterSubmit,
        [Description("Seconds to wait for verification when verifyAfterSubmit is true (1..60).") ] int verifyWaitSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.GroupRoleRemoveMemberAsync(groupId, roleId, memberAgentId, verifyAfterSubmit, verifyWaitSeconds, cancellationToken);
    }

    [McpServerTool, Description("Invite an avatar to a group with one or more role UUIDs.")]
    public Task<DataToolResult> GroupInviteUser(
        [Description("Group UUID.")] string groupId,
        [Description("Structured invite payload: targetAgentId, optional roleIdsCsv, and fallback useEveryoneRoleIfEmpty.") ] GroupInviteInput invite,
        CancellationToken cancellationToken)
    {
        return _bot.GroupInviteUserAsync(groupId, invite, cancellationToken);
    }

    [McpServerTool, Description("List currently banned agents for a group UUID.")]
    public Task<DataToolResult> GroupBanListGet(
        [Description("Group UUID.")] string groupId,
        [Description("Include detailed ban timestamps.")] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupBanListGetAsync(groupId, includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Ban or unban one or more agents from a group, with optional read-back verification.")]
    public Task<DataToolResult> GroupBanSet(
        [Description("Group UUID.")] string groupId,
        [Description("Structured payload: action (ban|unban) and agentIdsCsv.") ] GroupBanActionInput request,
        [Description("When true, verify post-submit state by re-reading group ban list.")] bool verifyAfterSubmit,
        [Description("Seconds to wait for verification when verifyAfterSubmit is true (1..60).") ] int verifyWaitSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.GroupBanSetAsync(groupId, request, verifyAfterSubmit, verifyWaitSeconds, cancellationToken);
    }

    [McpServerTool, Description("List notices for a group.")]
    public Task<DataToolResult> GroupNoticesList(
        [Description("Group UUID.")] string groupId,
        [Description("Include detailed notice fields (sender, timestamps, attachment type).") ] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupNoticesListAsync(groupId, includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Send a group notice from a structured notice payload.")]
    public Task<BotToolResult> GroupNoticeSend(
        [Description("Group UUID.")] string groupId,
        [Description("Structured notice payload: subject, message, optional attachment item/owner IDs.") ] GroupNoticeInput notice,
        CancellationToken cancellationToken)
    {
        return _bot.GroupNoticeSendAsync(groupId, notice, cancellationToken);
    }

    [McpServerTool, Description("Join a group chat session and optionally wait for join confirmation.")]
    public Task<BotToolResult> GroupChatJoin(
        [Description("Group/session UUID.")] string groupId,
        [Description("Seconds to wait for GroupChatJoined confirmation (0 = no wait).") ] int waitForJoinSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.GroupChatJoinAsync(groupId, waitForJoinSeconds, cancellationToken);
    }

    [McpServerTool, Description("Leave a group chat session.")]
    public Task<BotToolResult> GroupChatLeave(
        [Description("Group/session UUID.")] string groupId,
        CancellationToken cancellationToken)
    {
        return _bot.GroupChatLeaveAsync(groupId, cancellationToken);
    }

    [McpServerTool, Description("Send a message to an active group chat session.")]
    public Task<BotToolResult> GroupChatSend(
        [Description("Group/session UUID.")] string groupId,
        [Description("Message text.")] string message,
        CancellationToken cancellationToken)
    {
        return _bot.GroupChatSendAsync(groupId, message, cancellationToken);
    }

    [McpServerTool, Description("List active group chat sessions tracked by the client.")]
    public Task<DataToolResult> GroupChatSessionsList(
        [Description("Include detailed member state per session.")] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.GroupChatSessionsListAsync(includeDetails, cancellationToken);
    }

    [McpServerTool, Description("Accept a pending chat-session invitation by session UUID.")]
    public Task<BotToolResult> GroupChatAcceptInvite(
        [Description("Chat session UUID.")] string sessionId,
        CancellationToken cancellationToken)
    {
        return _bot.GroupChatAcceptInviteAsync(sessionId, cancellationToken);
    }

    [McpServerTool, Description("Create a group using a structured group payload.")]
    public Task<BotToolResult> GroupCreate(
        [Description("Structured group payload (name, charter, insigniaId, membership flags, and fee).") ] GroupCreateInput group,
        [Description("Seconds to wait for GroupCreatedReply (0 = no wait).") ] int waitForCreateSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.GroupCreateAsync(group, waitForCreateSeconds, cancellationToken);
    }

    [McpServerTool, Description("Update group profile/settings from a structured group payload.")]
    public Task<BotToolResult> GroupUpdate(
        [Description("Group UUID to update.")] string groupId,
        [Description("Structured group payload used to update group settings.") ] GroupCreateInput group,
        CancellationToken cancellationToken)
    {
        return _bot.GroupUpdateAsync(groupId, group, cancellationToken);
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

    [McpServerTool, Description("Start continuous movement on an axis until StopMovement or an optional auto-stop duration elapses.")]
    public Task<BotToolResult> StartMovement(
        [Description("Axis: forward, back, left, right, up, down.")] string axis,
        [Description("True for fast/run speed, false for normal walk speed.")] bool fast,
        [Description("Optional auto-stop duration in seconds (0.25-300). Omit or 0 to run until StopMovement.")] float? durationSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.StartMovementAsync(axis, fast, durationSeconds, cancellationToken);
    }

    [McpServerTool, Description("Turn the bot body and camera toward a local position.")]
    public Task<BotToolResult> LookAt(
        [Description("Local X coordinate (0..256).")] float x,
        [Description("Local Y coordinate (0..256).")] float y,
        [Description("Local Z coordinate.")] float z,
        CancellationToken cancellationToken)
    {
        return _bot.LookAtAsync(x, y, z, cancellationToken);
    }

    [McpServerTool, Description("Set camera heading in degrees (0 = east, 90 = north, 180 = west, 270 = south).")]
    public Task<BotToolResult> SetCameraHeading(
        [Description("Heading in degrees.")] float headingDegrees,
        CancellationToken cancellationToken)
    {
        return _bot.SetCameraHeadingAsync(headingDegrees, cancellationToken);
    }

    [McpServerTool, Description("Get current camera position, orientation axes, and agent position.")]
    public Task<CameraStateResult> GetCameraState(CancellationToken cancellationToken)
    {
        return _bot.GetCameraStateAsync(cancellationToken);
    }

    [McpServerTool, Description("Follow a target avatar or object in the current region using autopilot.")]
    public Task<BotToolResult> Follow(
        [Description("Target type: avatar or object.")] string targetType,
        [Description("Avatar full name or UUID, or object name, local ID, or UUID.")] string target,
        [Description("Distance buffer in meters; follow pauses inside this range (default 3).")] float distanceBuffer,
        CancellationToken cancellationToken)
    {
        return _bot.FollowAsync(targetType, target, distanceBuffer, cancellationToken);
    }

    [McpServerTool, Description("Stop an active follow started by Follow.")]
    public Task<BotToolResult> StopFollow(CancellationToken cancellationToken)
    {
        return _bot.StopFollowAsync(cancellationToken);
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

    [McpServerTool, Description("Return object(s) to their owner inventory by comma-separated local IDs.")]
    public Task<BotToolResult> PrimReturnToOwner(
        [Description("Comma-separated local IDs to return.")] string localIdsCsv,
        CancellationToken cancellationToken)
    {
        return _bot.PrimReturnToOwnerAsync(localIdsCsv, cancellationToken);
    }

    [McpServerTool, Description("Take object(s) into inventory (or take copy) by comma-separated local IDs.")]
    public Task<BotToolResult> PrimTake(
        [Description("Comma-separated local IDs to take.")] string localIdsCsv,
        [Description("True = take copy, false = take (move).") ] bool takeCopy,
        [Description("Optional destination inventory folder UUID. If omitted, default Objects folder is used.")] string? destinationFolderId,
        CancellationToken cancellationToken)
    {
        return _bot.PrimTakeAsync(localIdsCsv, takeCopy, destinationFolderId, cancellationToken);
    }

    [McpServerTool, Description("Rez an inventory object item at a target transform, with optional post-rez scaling.")]
    public Task<BotToolResult> PrimRezFromInventory(
        [Description("Inventory object item UUID.")] string itemId,
        [Description("Local X coordinate (0..256).") ] float x,
        [Description("Local Y coordinate (0..256).") ] float y,
        [Description("Local Z coordinate.")] float z,
        [Description("Roll in degrees.")] float rollDegrees,
        [Description("Pitch in degrees.")] float pitchDegrees,
        [Description("Yaw in degrees.")] float yawDegrees,
        [Description("Select the object after rez when true (requires waitForObject).") ] bool selectAfterRez,
        [Description("Wait for simulator object confirmation before returning.")] bool waitForObject,
        [Description("Optional post-rez scale X. Set all scale fields together or leave all null.")] float? scaleX,
        [Description("Optional post-rez scale Y. Set all scale fields together or leave all null.")] float? scaleY,
        [Description("Optional post-rez scale Z. Set all scale fields together or leave all null.")] float? scaleZ,
        CancellationToken cancellationToken)
    {
        return _bot.PrimRezFromInventoryAsync(
            itemId,
            x,
            y,
            z,
            rollDegrees,
            pitchDegrees,
            yawDegrees,
            selectAfterRez,
            waitForObject,
            scaleX,
            scaleY,
            scaleZ,
            cancellationToken);
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

    [McpServerTool, Description("Delete an inventory item by UUID.")]
    public Task<BotToolResult> InventoryDeleteItem(
        [Description("Inventory item UUID to delete.")] string itemId,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryDeleteItemAsync(itemId, cancellationToken);
    }

    [McpServerTool, Description("Delete an inventory folder by UUID.")]
    public Task<BotToolResult> InventoryDeleteFolder(
        [Description("Inventory folder UUID to delete.")] string folderId,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryDeleteFolderAsync(folderId, cancellationToken);
    }

    [McpServerTool, Description("Delete multiple inventory items by comma-separated UUID list.")]
    public Task<BotToolResult> InventoryDeleteMany(
        [Description("Comma-separated inventory item UUIDs to delete.")] string itemIdsCsv,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryDeleteManyAsync(itemIdsCsv, cancellationToken);
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

    [McpServerTool, Description("Upload a glTF/glb model as a mesh object using a Collada-free pipeline.")]
    public Task<AssetTransferResult> MeshUploadGltf(
        [Description("Source path or HTTP/HTTPS URL (.glb or .gltf).") ] string source,
        [Description("New inventory object name.") ] string name,
        [Description("New inventory object description.") ] string description,
        CancellationToken cancellationToken)
    {
        return _bot.MeshUploadGltfAsync(source, name, description, cancellationToken);
    }

    [McpServerTool, Description("Inspect a glTF/glb model for mesh upload readiness and texture ingest details without uploading.")]
    public Task<MeshInspectResult> MeshInspectGltf(
        [Description("Source path or HTTP/HTTPS URL (.glb or .gltf).") ] string source,
        [Description("Maximum warnings to include in output (1..200).") ] int maxWarnings,
        [Description("Strict mode: fail inspection when any primitive would be skipped or any texture ingest/transcode fails.") ] bool strict,
        CancellationToken cancellationToken)
    {
        return _bot.MeshInspectGltfAsync(source, maxWarnings, strict, cancellationToken);
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

    [McpServerTool, Description("Bootstrap-install the dialog bridge by uploading script inventory, creating a prim, and copying script into task inventory.")]
    public Task<DialogBridgeInstallResult> DialogBridgeInstall(
        [Description("Optional local path or HTTP/HTTPS URL to dialog-bridge.lsl. Empty = auto-discover lsl/dialog-bridge.lsl.")] string? scriptSource,
        [Description("Optional bridge prim name. Empty uses default.")] string? objectName,
        [Description("Optional bridge prim description. Empty uses default.")] string? objectDescription,
        [Description("Optional destination inventory folder UUID for uploaded script item.")] string? folderId,
        [Description("Create offset on X axis from bot position.")] float offsetX,
        [Description("Create offset on Y axis from bot position.")] float offsetY,
        [Description("Create offset on Z axis from bot position.")] float offsetZ,
        [Description("If true, pin the installed object as trusted bridge sender at runtime.")] bool pinAsTrustedSender,
        CancellationToken cancellationToken)
    {
        return _bot.DialogBridgeInstallAsync(
            scriptSource,
            objectName,
            objectDescription,
            folderId,
            offsetX,
            offsetY,
            offsetZ,
            pinAsTrustedSender,
            cancellationToken);
    }

    [McpServerTool, Description("Uninstall the dialog bridge: delete pinned bridge prim in-world, optionally delete inventory script copies, and clear trust pins.")]
    public Task<BotToolResult> DialogBridgeUninstall(
        [Description("If true, also delete dialog-bridge.lsl copies from inventory Scripts folder.")] bool deleteInventoryScripts,
        [Description("If true, clear trusted bridge object/owner pins and persist updated trust state.")] bool clearTrustPins,
        CancellationToken cancellationToken)
    {
        return _bot.DialogBridgeUninstallAsync(deleteInventoryScripts, clearTrustPins, cancellationToken);
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