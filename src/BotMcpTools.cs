using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Opensim.Metaverse2Mcp;

[McpServerToolType]
internal sealed class BotMcpTools
{
    private readonly BotSession _bot;
    private readonly SpawnerClient _spawnerClient;
    private readonly AppOptions _options;

    public BotMcpTools(BotSession bot, SpawnerClient spawnerClient, AppOptions options)
    {
        _bot = bot;
        _spawnerClient = spawnerClient;
        _options = options;
    }

    [McpServerTool, Description("Get bot connection and location status.")]
    public BotStatus GetStatus()
    {
        return _bot.GetStatus();
    }

    [McpServerTool, Description("List bot instances from the opensim-spawner API.")]
    public Task<DataToolResult> BotList(CancellationToken cancellationToken)
    {
        return _spawnerClient.ListBotsAsync(cancellationToken);
    }

    [McpServerTool, Description("Get one bot instance and container status from the opensim-spawner API.")]
    public Task<DataToolResult> BotGet(
        [Description("Bot first name.")] string first,
        [Description("Bot last name.")] string last,
        CancellationToken cancellationToken)
    {
        return _spawnerClient.GetBotAsync(first, last, cancellationToken);
    }

    [McpServerTool, Description("Create a new bot through opensim-spawner.")]
    public Task<DataToolResult> BotCreate(
        [Description("New bot first name.")] string first,
        [Description("New bot last name.")] string last,
        [Description("Bot level (for example: GOVERNOR, BUILDER, ACTOR).")]
        string level,
        [Description("Optional email override. If omitted, spawner defaults to <first>.<last>@localhost.")]
        string? email,
        [Description("Optional 3D avatar model override. If omitted, spawner defaults to Ruth.")]
        string? model,
        [Description("Optional appearance name (for example: Cube Bot, Actor, Construction).")]
        string? appearance,
        [Description("Optional gender (male, female, neutral).")]
        string? gender,
        CancellationToken cancellationToken)
    {
        return _spawnerClient.CreateBotAsync(
            first,
            last,
            level,
            $"{_options.BotFirstName} {_options.BotLastName}".Trim(),
            email,
            model,
            appearance,
            gender,
            _options.OpencodeInitialProvider,
            _options.OpencodeInitialModel,
            cancellationToken);
    }

    [McpServerTool, Description("Delete a bot through opensim-spawner (stops/removes its containers).")]
    public Task<DataToolResult> BotDelete(
        [Description("Bot first name.")] string first,
        [Description("Bot last name.")] string last,
        CancellationToken cancellationToken)
    {
        return _spawnerClient.DeleteBotAsync(first, last, cancellationToken);
    }

    [McpServerTool, Description("Start a bot through opensim-spawner (self or descendant bots only).")]
    public Task<DataToolResult> BotStart(
        [Description("Bot first name.")] string first,
        [Description("Bot last name.")] string last,
        CancellationToken cancellationToken)
    {
        return ChangeBotRunningStateAsync(first, last, "start", cancellationToken);
    }

    [McpServerTool, Description("Stop a bot through opensim-spawner (self or descendant bots only).")]
    public Task<DataToolResult> BotStop(
        [Description("Bot first name.")] string first,
        [Description("Bot last name.")] string last,
        CancellationToken cancellationToken)
    {
        return ChangeBotRunningStateAsync(first, last, "stop", cancellationToken);
    }

    [McpServerTool, Description("Restart a bot through opensim-spawner (self or descendant bots only).")]
    public Task<DataToolResult> BotRestart(
        [Description("Bot first name.")] string first,
        [Description("Bot last name.")] string last,
        CancellationToken cancellationToken)
    {
        return ChangeBotRunningStateAsync(first, last, "restart", cancellationToken);
    }

    private async Task<DataToolResult> ChangeBotRunningStateAsync(
        string first,
        string last,
        string action,
        CancellationToken cancellationToken)
    {
        var ownershipError = await ValidateOwnershipForStateChangeAsync(first, last, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ownershipError))
        {
            return DataToolResult.FailResult(ownershipError);
        }

        return await _spawnerClient.PatchBotAsync(first, last, action, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ValidateOwnershipForStateChangeAsync(
        string targetFirst,
        string targetLast,
        CancellationToken cancellationToken)
    {
        var safeTargetFirst = targetFirst?.Trim() ?? string.Empty;
        var safeTargetLast = targetLast?.Trim() ?? string.Empty;
        if (safeTargetFirst.Length == 0 || safeTargetLast.Length == 0)
        {
            return "first and last are required.";
        }

        var selfFirst = _options.BotFirstName?.Trim() ?? string.Empty;
        var selfLast = _options.BotLastName?.Trim() ?? string.Empty;
        if (selfFirst.Length == 0 || selfLast.Length == 0)
        {
            return "Cannot evaluate ownership: current bot identity is not configured.";
        }

        var selfFullName = $"{selfFirst} {selfLast}";
        if (string.Equals(safeTargetFirst, selfFirst, StringComparison.OrdinalIgnoreCase)
            && string.Equals(safeTargetLast, selfLast, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var currentFirst = safeTargetFirst;
        var currentLast = safeTargetLast;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var currentFullName = $"{currentFirst} {currentLast}";
            if (!visited.Add(currentFullName))
            {
                return $"Ownership check failed for {safeTargetFirst} {safeTargetLast}: parent cycle detected at {currentFullName}.";
            }

            var targetResult = await _spawnerClient.GetBotAsync(currentFirst, currentLast, cancellationToken).ConfigureAwait(false);
            if (!targetResult.Ok)
            {
                return $"Ownership check failed while reading {currentFullName}: {targetResult.Message}";
            }

            var parent = TryReadParentName(targetResult.PayloadJson);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return $"Not allowed: {selfFullName} can only change state of itself or descendants.";
            }

            if (string.Equals(parent.Trim(), selfFullName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!TrySplitAvatarName(parent, out currentFirst, out currentLast))
            {
                return $"Ownership check failed: parent name '{parent}' for {currentFullName} is not in '<first> <last>' form.";
            }
        }
    }

    private static string? TryReadParentName(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("parent", out var parentElement)
                || parentElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return parentElement.ValueKind == JsonValueKind.String ? parentElement.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TrySplitAvatarName(string fullName, out string first, out string last)
    {
        first = string.Empty;
        last = string.Empty;

        var parts = fullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        first = parts[0];
        last = parts[1];
        return true;
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

    [McpServerTool, Description("Enable or disable voice routing for synthesized speech output.")]
    public Task<BotToolResult> Voice(
        [Description("True to enable Piper -> voice backend routing; false to disable.")] bool enabled,
        CancellationToken cancellationToken)
    {
        return _bot.SetVoiceRoutingAsync(enabled, cancellationToken);
    }

    [McpServerTool, Description("List available Piper voices from the configured /voices endpoint.")]
    public Task<DataToolResult> Voices(CancellationToken cancellationToken)
    {
        return _bot.ListVoicesAsync(cancellationToken);
    }

    [McpServerTool, Description("Query current voice routing, backend, and Piper endpoint state.")]
    public Task<DataToolResult> QueryVoice(CancellationToken cancellationToken)
    {
        return _bot.QueryVoiceAsync(cancellationToken);
    }

    [McpServerTool, Description("Synthesize text with Piper and play it through the configured voice backend.")]
    public Task<BotToolResult> Say(
        [Description("Text to synthesize and speak.")] string text,
        [Description("Optional Piper voice name; defaults to configured PIPER_DEFAULT_VOICE.")] string? voice,
        [Description("Optional Piper speaker id.")] int? speaker,
        [Description("Optional Piper speaker_id alias (used when speaker is omitted).") ] int? speakerId,
        [Description("Optional Piper length_scale.")] float? lengthScale,
        [Description("Optional Piper noise_scale.")] float? noiseScale,
        [Description("Optional Piper noise_w.")] float? noiseW,
        [Description("Optional Piper sentence_silence.")] float? sentenceSilence,
        CancellationToken cancellationToken)
    {
        return _bot.SayAsync(text, voice, speaker, speakerId, lengthScale, noiseScale, noiseW, sentenceSilence, cancellationToken);
    }

    [McpServerTool, Description("Send an instant message to an avatar UUID.")]
    public Task<BotToolResult> SendInstantMessage(
        [Description("Recipient agent UUID.")] string agentId,
        [Description("Message to send.")] string message,
        CancellationToken cancellationToken)
    {
        return _bot.SendImAsync(agentId, message, cancellationToken);
    }

    [McpServerTool, Description("Set the cube bot mood/emotion on the bridge-controlled emoter attachment.")]
    public Task<BotToolResult> SetBotMood(
        [Description("Emotion name (for example: happy, sad, angry, surprised, neutral).")]
        string emotion,
        CancellationToken cancellationToken)
    {
        return _bot.SetBotMoodAsync(emotion, cancellationToken);
    }

    [McpServerTool, Description("List available cube-bot mood names from textures inside the trusted dialog bridge object.")]
    public Task<DataToolResult> MoodList(
        [Description("When true, include utility textures such as 'base' and 'cross' in moodNames.")]
        bool includeUtilityTextures,
        CancellationToken cancellationToken)
    {
        return _bot.BotMoodListAsync(includeUtilityTextures, cancellationToken);
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

    [McpServerTool, Description("List current friends and online state.")]
    public Task<DataToolResult> FriendList(
        [Description("Include rights detail fields in each friend row.")] bool includeDetails,
        CancellationToken cancellationToken)
    {
        return _bot.FriendListAsync(includeDetails, cancellationToken);
    }

    [McpServerTool, Description("List pending incoming friendship offers.")]
    public Task<DataToolResult> FriendOffersList(CancellationToken cancellationToken)
    {
        return _bot.FriendOffersListAsync(cancellationToken);
    }

    [McpServerTool, Description("Offer friendship to an avatar UUID.")]
    public Task<BotToolResult> FriendOfferSend(
        [Description("Target avatar UUID.")] string targetAgentId,
        [Description("Optional message to include with the offer.")] string? message,
        [Description("Seconds to wait for accept/decline response (0 = do not wait, max 60).") ] int waitForResponseSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.FriendOfferSendAsync(targetAgentId, message, waitForResponseSeconds, cancellationToken);
    }

    [McpServerTool, Description("Accept or decline a pending incoming friendship offer.")]
    public Task<BotToolResult> FriendOfferRespond(
        [Description("Agent UUID that sent the friendship offer.")] string fromAgentId,
        [Description("Action: accept or decline.")] string action,
        [Description("Use capability endpoints when available (recommended for offline-cap offers).") ] bool useCapabilities,
        CancellationToken cancellationToken)
    {
        return _bot.FriendOfferRespondAsync(fromAgentId, action, useCapabilities, cancellationToken);
    }

    [McpServerTool, Description("Terminate friendship with a friend UUID.")]
    public Task<BotToolResult> FriendRemove(
        [Description("Friend avatar UUID.")] string friendAgentId,
        CancellationToken cancellationToken)
    {
        return _bot.FriendRemoveAsync(friendAgentId, cancellationToken);
    }

    [McpServerTool, Description("Set rights granted to a friend (see online/map status and modify objects).")]
    public Task<BotToolResult> FriendSetRights(
        [Description("Friend avatar UUID.")] string friendAgentId,
        [Description("Grant right to see your online status.")] bool canSeeOnline,
        [Description("Grant right to see your map location (requires canSeeOnline=true).") ] bool canSeeOnMap,
        [Description("Grant right to modify your objects.")] bool canModifyObjects,
        CancellationToken cancellationToken)
    {
        return _bot.FriendSetRightsAsync(friendAgentId, canSeeOnline, canSeeOnMap, canModifyObjects, cancellationToken);
    }

    [McpServerTool, Description("Request map location for a friend UUID and optionally wait for reply.")]
    public Task<DataToolResult> FriendMapLocate(
        [Description("Friend avatar UUID.")] string friendAgentId,
        [Description("Seconds to wait for location reply (0 = do not wait, max 60).") ] int waitForReplySeconds,
        CancellationToken cancellationToken)
    {
        return _bot.FriendMapLocateAsync(friendAgentId, waitForReplySeconds, cancellationToken);
    }

    [McpServerTool, Description("Send a teleport offer (lure) to an avatar UUID.")]
    public Task<BotToolResult> TeleportOfferSend(
        [Description("Target avatar UUID.")] string targetAgentId,
        [Description("Optional lure message.")] string? message,
        CancellationToken cancellationToken)
    {
        return _bot.TeleportOfferSendAsync(targetAgentId, message, cancellationToken);
    }

    [McpServerTool, Description("Request a teleport invite from another avatar UUID.")]
    public Task<BotToolResult> TeleportRequestSend(
        [Description("Target avatar UUID.")] string targetAgentId,
        [Description("Optional request message.")] string? message,
        CancellationToken cancellationToken)
    {
        return _bot.TeleportRequestSendAsync(targetAgentId, message, cancellationToken);
    }

    [McpServerTool, Description("List pending incoming teleport offers (lures) captured during this session.")]
    public Task<DataToolResult> TeleportOffersList(CancellationToken cancellationToken)
    {
        return _bot.TeleportOffersListAsync(cancellationToken);
    }

    [McpServerTool, Description("List pending incoming teleport requests captured during this session.")]
    public Task<DataToolResult> TeleportRequestsList(CancellationToken cancellationToken)
    {
        return _bot.TeleportRequestsListAsync(cancellationToken);
    }

    [McpServerTool, Description("Accept or decline a pending teleport offer using requester and IM session IDs.")]
    public Task<BotToolResult> TeleportOfferRespond(
        [Description("Requester avatar UUID (sender of the offer)." )] string requesterAgentId,
        [Description("IM session UUID from the teleport offer message.")] string sessionId,
        [Description("True to accept and teleport, false to decline.")] bool accept,
        CancellationToken cancellationToken)
    {
        return _bot.TeleportOfferRespondAsync(requesterAgentId, sessionId, accept, cancellationToken);
    }

    [McpServerTool, Description("Search the avatar directory for people by name text.")]
    public Task<DataToolResult> DirectorySearchPeople(
        [Description("Name text to search for.")] string query,
        [Description("Directory query start/page offset (typically 0,1,2...).")] int queryStart,
        CancellationToken cancellationToken)
    {
        return _bot.DirectorySearchPeopleAsync(query, queryStart, cancellationToken);
    }

    [McpServerTool, Description("Search the directory for groups by name text.")]
    public Task<DataToolResult> DirectorySearchGroups(
        [Description("Group name text to search for.")] string query,
        [Description("Directory query start/page offset (typically 0,1,2...).")] int queryStart,
        CancellationToken cancellationToken)
    {
        return _bot.DirectorySearchGroupsAsync(query, queryStart, cancellationToken);
    }

    [McpServerTool, Description("Search the places directory for parcels listed in search.")]
    public Task<DataToolResult> DirectorySearchPlaces(
        [Description("Place search text (keywords).") ] string query,
        [Description("Directory query start/page offset (typically 0,1,2...).")] int queryStart,
        CancellationToken cancellationToken)
    {
        return _bot.DirectorySearchPlacesAsync(query, queryStart, cancellationToken);
    }

    [McpServerTool, Description("Search land-for-sale listings in the directory.")]
    public Task<DataToolResult> DirectorySearchLand(
        [Description("Land scope: any, mainland, estate, auction.")] string landType,
        [Description("Directory query start offset for land search (commonly 0,100,200...).")] int queryStart,
        [Description("Optional maximum sale price filter (0 disables).") ] int maxPrice,
        [Description("Optional minimum parcel area filter (0 disables).") ] int minArea,
        CancellationToken cancellationToken)
    {
        return _bot.DirectorySearchLandAsync(landType, queryStart, maxPrice, minArea, cancellationToken);
    }

    [McpServerTool, Description("Fetch avatar profile and interests for an avatar UUID, with optional AgentProfile capability details.")]
    public Task<DataToolResult> AvatarProfileGet(
        [Description("Target avatar UUID.")] string avatarId,
        [Description("When true, also query AgentProfile capability data if available.")] bool includeAgentProfileCapability,
        [Description("Seconds to wait for UDP profile/interests replies (1..30).") ] int waitForReplySeconds,
        CancellationToken cancellationToken)
    {
        return _bot.AvatarProfileGetAsync(avatarId, includeAgentProfileCapability, waitForReplySeconds, cancellationToken);
    }

    [McpServerTool, Description("List profile picks for an avatar UUID, with optional per-pick detail reads.")]
    public Task<DataToolResult> AvatarPicksList(
        [Description("Target avatar UUID.")] string avatarId,
        [Description("When true, request full details for each returned pick.")] bool includeDetails,
        [Description("Seconds to wait for each pick detail reply when includeDetails=true (1..30).") ] int detailWaitSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.AvatarPicksListAsync(avatarId, includeDetails, detailWaitSeconds, cancellationToken);
    }

    [McpServerTool, Description("List avatar classifieds for an avatar UUID, with optional per-classified detail reads.")]
    public Task<DataToolResult> AvatarClassifiedsList(
        [Description("Target avatar UUID.")] string avatarId,
        [Description("When true, request full details for each classified entry.")] bool includeDetails,
        [Description("Seconds to wait for each classified detail reply when includeDetails=true (1..30).") ] int detailWaitSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.AvatarClassifiedsListAsync(avatarId, includeDetails, detailWaitSeconds, cancellationToken);
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
        [Description("Comma-separated local IDs; last ID becomes root.")] string localIdsCsv,
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

    [McpServerTool, Description("Inspect a full linkset tree from any member prim local ID.")]
    public Task<LinksetInspectResult> PrimInspectLinkset(
        [Description("Any local ID in the target linkset (root or child).") ] uint localId,
        CancellationToken cancellationToken)
    {
        return _bot.InspectLinksetAsync(localId, cancellationToken);
    }

    [McpServerTool, Description("Set a new root prim for a linkset by relinking members with the requested root last.")]
    public Task<BotToolResult> PrimSetLinksetRoot(
        [Description("Any local ID in the target linkset (root or child).") ] uint localId,
        [Description("Local ID of the prim that should become root.")] uint newRootLocalId,
        CancellationToken cancellationToken)
    {
        return _bot.SetLinksetRootAsync(localId, newRootLocalId, cancellationToken);
    }

    [McpServerTool, Description("Reorder links in a linkset (including child order) by explicit local ID sequence, with chosen root.")]
    public Task<BotToolResult> PrimReorderLinkset(
        [Description("Any local ID in the target linkset (root or child).") ] uint localId,
        [Description("Comma-separated local IDs containing every prim in the linkset exactly once.")] string orderedLocalIdsCsv,
        [Description("Local ID of the desired root prim. Root is linked last by protocol behavior.")] uint rootLocalId,
        CancellationToken cancellationToken)
    {
        return _bot.ReorderLinksetAsync(localId, orderedLocalIdsCsv, rootLocalId, cancellationToken);
    }

    [McpServerTool, Description("Bulk-adjust selected links by optional position delta, rotation delta, and scale multiplier.")]
    public Task<BotToolResult> PrimBulkAdjustLinks(
        [Description("Comma-separated local IDs to adjust.")] string localIdsCsv,
        [Description("Optional position delta X.") ] float? deltaX,
        [Description("Optional position delta Y.") ] float? deltaY,
        [Description("Optional position delta Z.") ] float? deltaZ,
        [Description("Optional rotation delta roll in degrees.") ] float? deltaRollDegrees,
        [Description("Optional rotation delta pitch in degrees.") ] float? deltaPitchDegrees,
        [Description("Optional rotation delta yaw in degrees.") ] float? deltaYawDegrees,
        [Description("Optional scale multiplier (>0).") ] float? scaleMultiplier,
        [Description("True to apply as child-only edits (recommended for linked children).") ] bool childOnly,
        CancellationToken cancellationToken)
    {
        return _bot.BulkAdjustLinksAsync(
            localIdsCsv,
            deltaX,
            deltaY,
            deltaZ,
            deltaRollDegrees,
            deltaPitchDegrees,
            deltaYawDegrees,
            scaleMultiplier,
            childOnly,
            cancellationToken);
    }

    [McpServerTool, Description("Set next-owner copy/modify/transfer permissions on one or more prims.")]
    public Task<BotToolResult> PrimSetNextOwnerPermissions(
        [Description("Comma-separated local IDs.")] string localIdsCsv,
        [Description("Optional next-owner copy permission. Omit to leave unchanged.")] bool? allowCopy,
        [Description("Optional next-owner modify permission. Omit to leave unchanged.")] bool? allowModify,
        [Description("Optional next-owner transfer permission. Omit to leave unchanged.")] bool? allowTransfer,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimNextOwnerPermissionsAsync(localIdsCsv, allowCopy, allowModify, allowTransfer, cancellationToken);
    }

    [McpServerTool, Description("Set sale info or clear for-sale state for one or more prims.")]
    public Task<BotToolResult> PrimSetSaleInfo(
        [Description("Comma-separated local IDs.")] string localIdsCsv,
        [Description("True to set for-sale details, false to clear for-sale state.")] bool forSale,
        [Description("Sale type when forSale=true: Original, Copy, or Contents.")] string saleType,
        [Description("Sale price in L$ when forSale=true. Must be >= 0.")] int price,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimSaleInfoAsync(localIdsCsv, forSale, saleType, price, cancellationToken);
    }

    [McpServerTool, Description("Assign objects to a group and optionally share permissions and/or deed to that group.")]
    public Task<BotToolResult> PrimSetGroupOwnership(
        [Description("Comma-separated local IDs.")] string localIdsCsv,
        [Description("Target group UUID.")] string groupId,
        [Description("True to share with group permissions (group mask all); false to clear group share unless deeding.")] bool shareWithGroup,
        [Description("True to request deed to group (requires group-share permissions and sufficient rights).") ] bool deedToGroup,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimGroupOwnershipAsync(localIdsCsv, groupId, shareWithGroup, deedToGroup, cancellationToken);
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

    [McpServerTool, Description("Request fresh object properties for a prim and wait briefly for simulator updates before returning rich inspection details.")]
    public Task<PrimInspectResult> PrimFetchProperties(
        [Description("Prim local ID.")] uint localId,
        [Description("Include explicit per-face texture overrides in the response.")] bool includeFaceTextures,
        [Description("How long to wait for refreshed object properties (seconds, >0 and <=30).") ] float waitTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        return _bot.FetchPrimPropertiesAsync(localId, includeFaceTextures, waitTimeoutSeconds, cancellationToken);
    }

    [McpServerTool, Description("Edit core prim build shape parameters (cut, hollow, taper, twist, shear, skew, revolutions, and profile hole).")]
    public Task<BotToolResult> PrimSetBuildParams(
        [Description("Prim local ID.")] uint localId,
        [Description("Optional path begin cut (0..1).") ] float? pathBegin,
        [Description("Optional path end cut (0..1).") ] float? pathEnd,
        [Description("Optional profile begin cut (0..1).") ] float? profileBegin,
        [Description("Optional profile end cut (0..1).") ] float? profileEnd,
        [Description("Optional profile hollow amount (0..0.95).") ] float? hollow,
        [Description("Optional taper X (-1..1).") ] float? taperX,
        [Description("Optional taper Y (-1..1).") ] float? taperY,
        [Description("Optional twist (-1..1).") ] float? twist,
        [Description("Optional twist begin (-1..1).") ] float? twistBegin,
        [Description("Optional shear X (-2..2).") ] float? shearX,
        [Description("Optional shear Y (-2..2).") ] float? shearY,
        [Description("Optional skew (-1..1).") ] float? skew,
        [Description("Optional radius offset (-1..1).") ] float? radiusOffset,
        [Description("Optional revolutions (1..4).") ] float? revolutions,
        [Description("Optional profile hole type from HoleType enum (e.g. Same, Circle, Square, Triangle).") ] string? profileHole,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimBuildParamsAsync(
            localId,
            pathBegin,
            pathEnd,
            profileBegin,
            profileEnd,
            hollow,
            taperX,
            taperY,
            twist,
            twistBegin,
            shearX,
            shearY,
            skew,
            radiusOffset,
            revolutions,
            profileHole,
            cancellationToken);
    }

    [McpServerTool, Description("Enable/disable and edit prim flexible parameters.")]
    public Task<BotToolResult> PrimSetFlexible(
        [Description("Prim local ID.")] uint localId,
        [Description("True to enable/update flexible settings; false disables flexible data.")] bool enabled,
        [Description("Optional softness (0..3).") ] int? softness,
        [Description("Optional tension (0..10).") ] float? tension,
        [Description("Optional drag (0..10).") ] float? drag,
        [Description("Optional gravity (-10..10).") ] float? gravity,
        [Description("Optional wind sensitivity (0..10).") ] float? wind,
        [Description("Optional force X.") ] float? forceX,
        [Description("Optional force Y.") ] float? forceY,
        [Description("Optional force Z.") ] float? forceZ,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimFlexibleParamsAsync(localId, enabled, softness, tension, drag, gravity, wind, forceX, forceY, forceZ, cancellationToken);
    }

    [McpServerTool, Description("Enable/disable and edit prim light parameters.")]
    public Task<BotToolResult> PrimSetLight(
        [Description("Prim local ID.")] uint localId,
        [Description("True to enable/update light settings; false disables light data.")] bool enabled,
        [Description("Optional light color red (0..1).") ] float? red,
        [Description("Optional light color green (0..1).") ] float? green,
        [Description("Optional light color blue (0..1).") ] float? blue,
        [Description("Optional light intensity (0..1).") ] float? intensity,
        [Description("Optional light radius (0..20).") ] float? radius,
        [Description("Optional light cutoff angle (0..180).") ] float? cutoff,
        [Description("Optional light falloff (0..2).") ] float? falloff,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimLightParamsAsync(localId, enabled, red, green, blue, intensity, radius, cutoff, falloff, cancellationToken);
    }

    [McpServerTool, Description("Enable/disable and edit prim sculpt/mesh parameters.")]
    public Task<BotToolResult> PrimSetSculpt(
        [Description("Prim local ID.")] uint localId,
        [Description("True to enable/update sculpt settings; false disables sculpt data.")] bool enabled,
        [Description("Optional sculpt or mesh texture UUID.")] string? textureId,
        [Description("Optional base sculpt type: None, Sphere, Torus, Plane, Cylinder, Mesh.") ] string? sculptType,
        [Description("Optional invert sculpt normals flag.") ] bool? invert,
        [Description("Optional mirror sculpt flag.") ] bool? mirror,
        CancellationToken cancellationToken)
    {
        return _bot.SetPrimSculptParamsAsync(localId, enabled, textureId, sculptType, invert, mirror, cancellationToken);
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

    [McpServerTool, Description("Discover objects by parcel and ownership/status filters (owner, scripted, physical).")]
    public Task<DataToolResult> PrimQueryObjects(
        [Description("Optional parcel local ID filter.")] int? parcelLocalId,
        [Description("Optional owner avatar UUID filter.")] string? ownerId,
        [Description("Optional scripted filter: true=only scripted, false=only non-scripted, null=either.")] bool? scriptedOnly,
        [Description("Optional physics filter: true=only physical, false=only non-physical, null=either.")] bool? physicalOnly,
        [Description("Maximum results to return (1..2000).") ] int maxResults,
        [Description("When parcelLocalId is set, refresh parcel map first.")] bool forceRefreshParcelMap,
        CancellationToken cancellationToken)
    {
        return _bot.PrimQueryObjectsAsync(
            parcelLocalId,
            ownerId,
            scriptedOnly,
            physicalOnly,
            maxResults,
            forceRefreshParcelMap,
            cancellationToken);
    }

    [McpServerTool, Description("Request pay price information for an in-world object so payment options can be validated before paying/buying.")]
    public Task<DataToolResult> PrimRequestPayPrice(
        [Description("Prim local ID in current simulator cache.")] uint localId,
        [Description("Optional object UUID override. If omitted, resolves from localId.")] string? objectId,
        [Description("Wait timeout for pay price reply in milliseconds (250..15000).") ] int waitTimeoutMs,
        CancellationToken cancellationToken)
    {
        return _bot.PrimRequestPayPriceAsync(localId, objectId, waitTimeoutMs, cancellationToken);
    }

    [McpServerTool, Description("Submit a buy request for an object configured for sale (supports 0-price purchases).")]
    public Task<BotToolResult> PrimBuy(
        [Description("Prim local ID in current simulator cache.")] uint localId,
        [Description("Sale type expected on the object: Original, Copy, or Contents.")] string saleType,
        [Description("Expected sale price in L$ (0 allowed).") ] int price,
        [Description("Optional destination inventory folder UUID.")] string? categoryFolderId,
        [Description("Optional group UUID to associate with the purchase; defaults to active group.")] string? activeGroupId,
        CancellationToken cancellationToken)
    {
        return _bot.PrimBuyAsync(localId, saleType, price, categoryFolderId, activeGroupId, cancellationToken);
    }

    [McpServerTool, Description("Get wallet balance, optionally forcing a fresh balance request first.")]
    public Task<DataToolResult> WalletGetBalance(
        [Description("True to request an updated balance from simulator; false to return cached value.")] bool refresh,
        [Description("Wait timeout for refreshed balance reply in milliseconds (250..15000).") ] int waitTimeoutMs,
        CancellationToken cancellationToken)
    {
        return _bot.WalletGetBalanceAsync(refresh, waitTimeoutMs, cancellationToken);
    }

    [McpServerTool, Description("Send money to an avatar, object, or group by UUID.")]
    public Task<BotToolResult> Pay(
        [Description("Target type: avatar, object, or group.")] string targetType,
        [Description("Target UUID.")] string targetId,
        [Description("Amount in L$ (must be > 0).") ] int amount,
        [Description("Optional transaction description/memo.")] string? description,
        CancellationToken cancellationToken)
    {
        return _bot.PayAsync(targetType, targetId, amount, description, cancellationToken);
    }

    [McpServerTool, Description("List inventory entries under a folder UUID (or root if omitted), with optional filtering and cursor pagination.")]
    public Task<InventoryQueryResult> InventoryList(
        [Description("Optional folder UUID. Leave empty for inventory root.")] string? folderId,
        [Description("True to recurse into subfolders.")] bool recursive,
        [Description("Maximum matched results considered before pagination (1..10000).") ] int maxResults,
        [Description("Optional case-insensitive substring filter applied to entry names.")] string? nameContains,
        [Description("Optional type filter (matches kind/assetType/inventoryType, case-insensitive).") ] string? type,
        [Description("Optional lower-bound creation timestamp (ISO-8601 UTC).")] string? createdAfterUtc,
        [Description("Optional upper-bound creation timestamp (ISO-8601 UTC).")] string? createdBeforeUtc,
        [Description("Optional creator avatar UUID filter (items only).") ] string? creatorId,
        [Description("Optional cursor from a prior InventoryList response.")] string? cursor,
        [Description("Page size for this response (1..500).") ] int pageSize,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryListAsync(
            folderId,
            recursive,
            maxResults,
            nameContains,
            type,
            createdAfterUtc,
            createdBeforeUtc,
            creatorId,
            cursor,
            pageSize,
            cancellationToken);
    }

    [McpServerTool, Description("Create a new inventory folder under a parent folder (or root if omitted).")]
    public Task<BotToolResult> InventoryCreateFolder(
        [Description("Optional parent folder UUID. Leave empty for inventory root.")] string? parentFolderId,
        [Description("Folder name.")] string name,
        [Description("Preferred folder type (optional, defaults to None).") ] string? preferredType,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryCreateFolderAsync(parentFolderId, name, preferredType, cancellationToken);
    }

    [McpServerTool, Description("Rename an inventory folder by UUID.")]
    public Task<BotToolResult> InventoryRenameFolder(
        [Description("Inventory folder UUID to rename.")] string folderId,
        [Description("New folder name.")] string newName,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryRenameFolderAsync(folderId, newName, cancellationToken);
    }

    [McpServerTool, Description("Rename an inventory item by UUID.")]
    public Task<BotToolResult> InventoryRenameItem(
        [Description("Inventory item UUID to rename.")] string itemId,
        [Description("New item name.")] string newName,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryRenameItemAsync(itemId, newName, cancellationToken);
    }

    [McpServerTool, Description("Move an inventory folder to a new parent folder.")]
    public Task<BotToolResult> InventoryMoveFolder(
        [Description("Inventory folder UUID to move.")] string folderId,
        [Description("Destination parent folder UUID.")] string destinationParentFolderId,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryMoveFolderAsync(folderId, destinationParentFolderId, cancellationToken);
    }

    [McpServerTool, Description("Move an inventory item to a destination folder.")]
    public Task<BotToolResult> InventoryMoveItem(
        [Description("Inventory item UUID to move.")] string itemId,
        [Description("Destination folder UUID.")] string destinationFolderId,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryMoveItemAsync(itemId, destinationFolderId, cancellationToken);
    }

    [McpServerTool, Description("Move multiple inventory items to a destination folder.")]
    public Task<BotToolResult> InventoryMoveMany(
        [Description("Comma-separated inventory item UUIDs.")] string itemIdsCsv,
        [Description("Destination folder UUID.")] string destinationFolderId,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryMoveManyAsync(itemIdsCsv, destinationFolderId, cancellationToken);
    }

    [McpServerTool, Description("Copy an inventory item to another folder, optionally with a new name.")]
    public Task<BotToolResult> InventoryCopyItem(
        [Description("Source inventory item UUID.")] string itemId,
        [Description("Destination folder UUID.")] string destinationFolderId,
        [Description("Optional new name for the copied item.")] string? newName,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryCopyItemAsync(itemId, destinationFolderId, newName, cancellationToken);
    }

    [McpServerTool, Description("Create an inventory link item pointing to another inventory item.")]
    public Task<BotToolResult> InventoryLinkItem(
        [Description("Source inventory item UUID to link to.")] string itemId,
        [Description("Destination folder UUID for the link item.")] string destinationFolderId,
        [Description("Optional link item name. Defaults to source item name.")] string? linkName,
        [Description("Optional link item description. Defaults to source item description.")] string? linkDescription,
        CancellationToken cancellationToken)
    {
        return _bot.InventoryLinkItemAsync(itemId, destinationFolderId, linkName, linkDescription, cancellationToken);
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

    [McpServerTool, Description("List task inventory (contents) for an in-world object. Provide objectLocalId, objectId, or both.")]
    public Task<InventoryQueryResult> TaskInventoryList(
        [Description("Object local ID in current simulator (0 allowed when objectId is provided).") ] uint objectLocalId,
        [Description("Optional object UUID; when local ID is omitted or stale, this is used to resolve the current local ID from simulator cache.")] string? objectId,
        [Description("Maximum number of results (1..2000).") ] int maxResults,
        CancellationToken cancellationToken)
    {
        return _bot.TaskInventoryListAsync(objectLocalId, objectId, maxResults, cancellationToken);
    }

    [McpServerTool, Description("Request moving/copying a task-inventory item from an object into agent inventory. Provide objectLocalId, objectId, or both.")]
    public Task<BotToolResult> TaskInventoryTake(
        [Description("Object local ID in current simulator (0 allowed when objectId is provided).") ] uint objectLocalId,
        [Description("Task-inventory item UUID on the object.")] string taskItemId,
        [Description("Optional destination folder UUID. If omitted, uses default folder for item asset type.")] string? destinationFolderId,
        [Description("Optional object UUID; when local ID is omitted or stale, this is used to resolve the current local ID from simulator cache.")] string? objectId,
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

    [McpServerTool, Description("Wear outfit items from an inventory folder UUID, including replace/add category conflict feedback.")]
    public Task<AppearanceWearFolderResult> AppearanceWearFolder(
        [Description("Folder UUID containing outfit items/links.")] string folderId,
        [Description("True to replace current outfit, false to add.")] bool replaceItems,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceWearFolderAsync(folderId, replaceItems, cancellationToken);
    }

    [McpServerTool, Description("Save the current outfit links into a new inventory folder snapshot.")]
    public Task<OutfitSaveResult> AppearanceSaveCurrentOutfit(
        [Description("Name for the new snapshot folder.")] string folderName,
        [Description("Optional parent folder UUID. Empty uses Clothing folder when available.")] string? parentFolderId,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceSaveCurrentOutfitAsync(folderName, parentFolderId, cancellationToken);
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

    [McpServerTool, Description("Wear a wearable item directly via COF, with optional replacement of existing slot/type.")]
    public Task<BotToolResult> AppearanceWearWearableItem(
        [Description("Wearable inventory item UUID.")] string itemId,
        [Description("True to replace already-worn wearables in the same slot/type.")] bool replaceExistingSlot,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceWearWearableItemAsync(itemId, replaceExistingSlot, cancellationToken);
    }

    [McpServerTool, Description("Remove a currently worn wearable item directly via COF.")]
    public Task<WearableDirectControlResult> AppearanceRemoveWearableItem(
        [Description("Wearable inventory item UUID.")] string itemId,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceRemoveWearableItemAsync(itemId, cancellationToken);
    }

    [McpServerTool, Description("Remove currently worn wearables by wearable type.")]
    public Task<WearableDirectControlResult> AppearanceRemoveWearablesByType(
        [Description("WearableType enum name (e.g. Shirt, Pants, Alpha).") ] string wearableType,
        [Description("True to remove all layers of the type, false for one layer.")] bool removeAllLayers,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceRemoveWearablesByTypeAsync(wearableType, removeAllLayers, cancellationToken);
    }

    [McpServerTool, Description("List current attachment item-to-point mappings from worn attachments.")]
    public Task<AttachmentPointMappingResult> AppearanceListAttachmentPointMappings(CancellationToken cancellationToken)
    {
        return _bot.AppearanceListAttachmentPointMappingsAsync(cancellationToken);
    }

    [McpServerTool, Description("Resolve a worn attachment inventory item UUID to its current in-world object UUID/local ID.")]
    public Task<AttachmentObjectResolutionResult> AttachmentResolveObject(
        [Description("Worn attachment inventory item UUID.")] string itemId,
        CancellationToken cancellationToken)
    {
        return _bot.AttachmentResolveObjectAsync(itemId, cancellationToken);
    }

    [McpServerTool, Description("Change an attachment item's mapped/worn attachment point by reattaching it to a new point.")]
    public Task<BotToolResult> AppearanceSetAttachmentPointMapping(
        [Description("Attachment inventory item UUID.")] string itemId,
        [Description("AttachmentPoint enum name (e.g. Spine, Chest, RightHand).") ] string attachmentPoint,
        [Description("True to replace any existing attachment at the target point.")] bool replace,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceSetAttachmentPointMappingAsync(itemId, attachmentPoint, replace, cancellationToken);
    }

    [McpServerTool, Description("Detach a currently worn attachment item by inventory item UUID.")]
    public Task<BotToolResult> AppearanceDetachItem(
        [Description("Inventory item UUID.")] string itemId,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceDetachItemAsync(itemId, cancellationToken);
    }

    [McpServerTool, Description("Get cached transform snapshot for a worn attachment item.")]
    public Task<AttachmentTransformResult> AppearanceGetAttachedItemTransform(
        [Description("Worn attachment inventory item UUID.")] string itemId,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceGetAttachedItemTransformAsync(itemId, cancellationToken);
    }

    [McpServerTool, Description("Request transform updates for a worn attachment item (position/scale/rotation in euler degrees).")]
    public Task<AttachmentTransformResult> AppearanceSetAttachedItemTransform(
        [Description("Worn attachment inventory item UUID.")] string itemId,
        [Description("Optional target position X.")] float? positionX,
        [Description("Optional target position Y.")] float? positionY,
        [Description("Optional target position Z.")] float? positionZ,
        [Description("Optional target scale X.")] float? scaleX,
        [Description("Optional target scale Y.")] float? scaleY,
        [Description("Optional target scale Z.")] float? scaleZ,
        [Description("Optional target roll in degrees.")] float? rollDegrees,
        [Description("Optional target pitch in degrees.")] float? pitchDegrees,
        [Description("Optional target yaw in degrees.")] float? yawDegrees,
        [Description("True to edit only this child prim; false for whole linked object.")] bool childOnly,
        [Description("True to request uniform scale when scale values are set.")] bool uniformScale,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceSetAttachedItemTransformAsync(
            itemId,
            positionX,
            positionY,
            positionZ,
            scaleX,
            scaleY,
            scaleZ,
            rollDegrees,
            pitchDegrees,
            yawDegrees,
            childOnly,
            uniformScale,
            cancellationToken);
    }

    [McpServerTool, Description("Request appearance rebake/update.")]
    public Task<BotToolResult> AppearanceRebake(
        [Description("True to force a rebake, false for normal update.")] bool forceRebake,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceRebakeAsync(forceRebake, cancellationToken);
    }

    [McpServerTool, Description("List avatar visual parameters (shape sliders and related values), including ranges and current values.")]
    public Task<AppearanceVisualParamsResult> AppearanceVisualParamsList(
        [Description("Optional wearable category filter (e.g. shape, eyes, hair).") ] string? wearable,
        [Description("Optional case-insensitive name filter (substring).") ] string? nameContains,
        [Description("True to return only directly editable group-0 parameters.") ] bool editableOnly,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceVisualParamsListAsync(wearable, nameContains, editableOnly, cancellationToken);
    }

    [McpServerTool, Description("Set one avatar visual parameter by id or exact name and request a rebake.")]
    public Task<AppearanceVisualParamSetResult> AppearanceVisualParamSet(
        [Description("Optional visual param ID. If not provided, paramName is required.") ] int? paramId,
        [Description("Optional exact visual param name. Case-insensitive.") ] string? paramName,
        [Description("Optional wearable filter when resolving paramName (e.g. shape).") ] string? wearable,
        [Description("Target value to apply.") ] float value,
        [Description("True to clamp out-of-range values to min/max; false to return validation error.") ] bool clampToRange,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceVisualParamSetAsync(paramId, paramName, wearable, value, clampToRange, cancellationToken);
    }

    [McpServerTool, Description("Return appearance bake diagnostics, including baked-texture slots and optional cache-probe latency.")]
    public Task<AppearanceBakeDiagnosticsResult> AppearanceBakeDiagnostics(
        [Description("If true, send RequestCachedBakes and wait for reply/timeout.") ] bool requestCacheProbe,
        [Description("Cache probe timeout in milliseconds (100..15000).") ] int cacheProbeTimeoutMs,
        CancellationToken cancellationToken)
    {
        return _bot.AppearanceBakeDiagnosticsAsync(requestCacheProbe, cacheProbeTimeoutMs, cancellationToken);
    }

    [McpServerTool, Description("Bootstrap-install the dialog bridge by uploading and attaching prim containing scripts.")]
    public Task<DialogBridgeInstallResult> DialogBridgeInstall(CancellationToken cancellationToken)
    {
        return _bot.DialogBridgeInstallAsync(cancellationToken);
    }

    [McpServerTool, Description("Uninstall the dialog bridge: delete pinned bridge prim in-world and clear trust pins.")]
    public Task<BotToolResult> DialogBridgeUninstall(
        CancellationToken cancellationToken)
    {
        return _bot.DialogBridgeUninstallAsync(true, cancellationToken);
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
        [Description("True to remove same-name script entries from task inventory before copy.")] bool forceOverwrite,
        CancellationToken cancellationToken)
    {
        return _bot.ScriptCopyInventoryToTaskAsync(objectLocalId, inventoryScriptItemId, enableScript, forceOverwrite, cancellationToken);
    }

    [McpServerTool, Description("Copy a notecard from agent inventory into an object's task inventory.")]
    public Task<BotToolResult> NotecardCopyInventoryToTask(
        [Description("Target object local ID.")] uint objectLocalId,
        [Description("Agent inventory notecard item UUID.")] string inventoryNotecardItemId,
        [Description("True to remove same-name notecard entries from task inventory before copy.")] bool forceOverwrite,
        CancellationToken cancellationToken)
    {
        return _bot.NotecardCopyInventoryToTaskAsync(objectLocalId, inventoryNotecardItemId, forceOverwrite, cancellationToken);
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