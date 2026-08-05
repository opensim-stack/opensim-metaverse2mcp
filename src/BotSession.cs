using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession : IDisposable
{
    private readonly AppOptions _options;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly IOpencodeChatClient? _opencodeChat;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentImEvents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _imConversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImConversationConfig> _imConversationConfigs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _latestPendingPermissionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _latestPendingQuestionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _announcedPendingPermissionByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _announcedPendingQuestionByConversation = new(StringComparer.Ordinal);
    private readonly string? _handlerFullName;
    private readonly object _promptStateLock = new();

    private string? _projectAgentsPromptCache;
    private DateTime _projectAgentsPromptCacheLastWriteUtc;
    private string? _activeAgentsNotecardPrompt;
    private string? _activeAgentsNotecardSourceName;
    private string? _activeAgentsNotecardItemId;
    private DateTimeOffset? _activeAgentsNotecardInstalledAt;

    private const string BuiltInBridgePrompt =
        "You are an in-world assistant running through opensim-metaverse2mcp for OpenSimulator/Second Life style worlds.\n" +
        "Environment basics:\n" +
        "- Avatars, regions, parcels, prim objects, inventory, scripts, and environment settings are stateful and shared.\n" +
        "- Simulator/cache state may be stale; verify current state before mutating it.\n" +
        "Tooling basics:\n" +
        "- Use metaverse MCP tools for avatar/world operations (movement, prims, inventory, scripts, environment).\n" +
        "- Use console2mcp tools for simulator administration tasks when needed.\n" +
        "Operating rules:\n" +
        "- Prefer safe and reversible actions.\n" +
        "- Confirm destructive or high-impact actions first (delete, bulk changes, ownership/permission changes, restarts).\n" +
        "- Ask concise clarifying questions when instructions are ambiguous or missing required identifiers.\n" +
        "- For multi-step tasks, inspect -> plan -> execute -> verify and report results clearly.\n" +
        "- Respect handler and policy restrictions configured by the bridge.";

    private GridClient? _client;
    private bool _connected;
    private string _lastLoginMessage = string.Empty;

    public BotSession(AppOptions options)
    {
        _options = options;
        _handlerFullName = BuildHandlerFullName(_options.OpencodeHandlerFirstName, _options.OpencodeHandlerLastName);
        if (_options.OpencodeChatEnabled)
        {
            _opencodeChat = new OpencodeChatClient(_options);
            var startupModel = GetStartupDefaultModelId();
            if (!string.IsNullOrWhiteSpace(startupModel))
            {
                Console.WriteLine($"[opencode] startup default model configured (runtime-overridable): {startupModel}");
            }
        }

        if (!string.IsNullOrWhiteSpace(_handlerFullName))
        {
            Console.WriteLine($"[bot] handler restriction enabled: {_handlerFullName}");
        }
    }

    public string LastLoginMessage => _lastLoginMessage;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            return true;
        }

        var client = new GridClient();
        client.Network.LoginProgress += OnLoginProgress;
        client.Network.Disconnected += OnDisconnected;
        client.Self.IM += OnInstantMessage;
        client.Self.ChatFromSimulator += OnChatFromSimulator;
        client.Inventory.InventoryObjectOffered += OnInventoryObjectOffered;

        var login = client.Network.DefaultLoginParams(
            _options.BotFirstName!,
            _options.BotLastName!,
            _options.BotPassword!,
            "opensim-metaverse2mcp",
            "0.1.0");

        login.URI = _options.BotLoginUri;
        login.Start = _options.BotStartLocation;

        Console.WriteLine($"[bot] logging in as {_options.BotFirstName} {_options.BotLastName} ...");

        var success = await client.Network.LoginAsync(login, cancellationToken).ConfigureAwait(false);
        _lastLoginMessage = client.Network.LoginMessage ?? string.Empty;

        if (!success)
        {
            client.Network.Logout();
            client.Self.IM -= OnInstantMessage;
            client.Self.ChatFromSimulator -= OnChatFromSimulator;
            client.Inventory.InventoryObjectOffered -= OnInventoryObjectOffered;
            client.Network.Disconnected -= OnDisconnected;
            client.Network.LoginProgress -= OnLoginProgress;
            client.Dispose();
            return false;
        }

        _client = client;
        _connected = true;

        await TryLoadInventoryOfferPoliciesFromConfiguredFileAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public BotStatus GetStatus()
    {
        var client = EnsureClient();
        var sim = client.Network.CurrentSim;
        var pos = client.Self.SimPosition;

        return new BotStatus(
            _connected,
            sim?.Name ?? "unknown",
            pos.X,
            pos.Y,
            pos.Z,
            client.Self.AgentID.ToString(),
            _lastLoginMessage);
    }

    public async Task<BotToolResult> SitAsync(CancellationToken cancellationToken)
    {
        return await RunActionAsync("Sitting down...", c => c.Self.SitOnGround(), cancellationToken);
    }

    public async Task<BotToolResult> StandAsync(CancellationToken cancellationToken)
    {
        return await RunActionAsync("Standing up.", c => c.Self.Stand(), cancellationToken);
    }

    public async Task<BotToolResult> FlyAsync(bool enabled, CancellationToken cancellationToken)
    {
        return await RunActionAsync(enabled ? "Taking off." : "Walking now.", c => c.Self.Fly(enabled), cancellationToken);
    }

    public async Task<BotToolResult> JumpAsync(CancellationToken cancellationToken)
    {
        var result = await RunActionAsync("Jumping.", c => c.Self.Jump(true), cancellationToken);
        if (!result.Ok)
        {
            return result;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                var cl = _client;
                if (cl != null)
                {
                    cl.Self.Jump(false);
                }
            }
            catch
            {
                // Ignored: this is a best-effort reset.
            }
        });

        return result;
    }

    public async Task<BotToolResult> DanceAsync(bool enabled, CancellationToken cancellationToken)
    {
        var message = enabled ? "Started dancing." : "Stopped dancing.";
        return await RunActionAsync(
            message,
            c =>
            {
                if (enabled)
                {
                    c.Self.AnimationStart(Animations.DANCE1, true);
                }
                else
                {
                    c.Self.AnimationStop(Animations.DANCE1, true);
                }
            },
            cancellationToken);
    }

    public async Task<BotToolResult> SayChatAsync(string message, int channel, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        return await RunActionAsync(
            $"Sent chat message on channel {channel}.",
            c => c.Self.Chat(message, channel, ChatType.Normal),
            cancellationToken);
    }

    public async Task<BotToolResult> SendImAsync(string agentId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return BotToolResult.Fail("agentId is required.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        if (!UUID.TryParse(agentId, out var recipient))
        {
            return BotToolResult.Fail("agentId is not a valid UUID.");
        }

        return await RunActionAsync(
            $"Sent IM to {agentId}.",
            c => c.Self.InstantMessage(recipient, message),
            cancellationToken);
    }

    public async Task<EnvironmentToolResult> GetRegionEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetRegionEnvironmentAsync(token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult("Unable to fetch region environment (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult("Fetched region environment.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentToolResult> GetParcelEnvironmentAsync(int parcelId, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return EnvironmentToolResult.FailResult("parcelId must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetParcelEnvironmentAsync(parcelId, token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult($"Unable to fetch parcel environment for parcelId={parcelId} (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult($"Fetched parcel environment for parcelId={parcelId}.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetRegionEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.ResetRegionEnvironmentAsync(token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Region environment reset failed or was rejected.");
            }

            return BotToolResult.OkResult("Region environment reset requested successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetParcelEnvironmentAsync(int parcelId, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return BotToolResult.Fail("parcelId must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.ResetParcelEnvironmentAsync(parcelId, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail($"Parcel environment reset failed or was rejected for parcelId={parcelId}.");
            }

            return BotToolResult.OkResult($"Parcel environment reset requested for parcelId={parcelId}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentToolResult> GetLegacyEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetLegacyEnvironmentAsync(token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult("Unable to fetch legacy environment (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult("Fetched legacy environment.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetLegacyEnvironmentRawAsync(string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.SetLegacyEnvironmentAsync(map, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Legacy environment set failed or was rejected.");
            }

            return BotToolResult.OkResult("Legacy environment update posted successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetRegionEnvironmentRawAsync(string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        if (!TryBuildEnvironmentDataFromPayloadMap(map, out var environment, out var environmentError))
        {
            return BotToolResult.Fail(environmentError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var response = await client.Environment.SetRegionEnvironmentAsync(environment, token).ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail("Region environment update failed (capability unavailable or request failed).");
            }

            if (!response.Success)
            {
                var detail = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : $" Detail: {response.Message}";
                return BotToolResult.Fail($"Region environment update was rejected.{detail}");
            }

            return BotToolResult.OkResult($"Region environment updated successfully (version={response.Version}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetParcelEnvironmentRawAsync(int parcelId, string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return BotToolResult.Fail("parcelId must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        if (!TryBuildEnvironmentDataFromPayloadMap(map, out var environment, out var environmentError))
        {
            return BotToolResult.Fail(environmentError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var response = await client.Environment.SetParcelEnvironmentAsync(parcelId, environment, token).ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail($"Parcel environment update failed for parcelId={parcelId} (capability unavailable or request failed).");
            }

            if (!response.Success)
            {
                var detail = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : $" Detail: {response.Message}";
                return BotToolResult.Fail($"Parcel environment update was rejected for parcelId={parcelId}.{detail}");
            }

            return BotToolResult.OkResult($"Parcel environment updated for parcelId={parcelId} (version={response.Version}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetLegacyEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.SetLegacyEnvironmentAsync(new OSDMap(), token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Legacy environment reset failed or was rejected.");
            }

            return BotToolResult.OkResult("Legacy environment reset posted using an empty LLSD map.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimCreateResult> CreatePrimAsync(
        string shape,
        float x,
        float y,
        float z,
        float scaleX,
        float scaleY,
        float scaleZ,
        float rollDegrees,
        float pitchDegrees,
        float yawDegrees,
        string material,
        string? name,
        string? description,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return PrimCreateResult.FailResult("No current simulator available.");
            }

            if (!TryBuildConstructionData(shape, material, out var primData, out var shapeError))
            {
                return PrimCreateResult.FailResult(shapeError);
            }

            var position = ClampLocalPosition(new Vector3(x, y, z));
            var scale = ClampScale(new Vector3(scaleX, scaleY, scaleZ));
            var rotation = Quaternion.CreateFromEulers(
                rollDegrees * Utils.DEG_TO_RAD,
                pitchDegrees * Utils.DEG_TO_RAD,
                yawDegrees * Utils.DEG_TO_RAD);

            var createdPrimTask = WaitForCreatedPrimAsync(client, sim, position, token);
            client.Objects.AddPrim(sim, primData, client.Self.ActiveGroup, position, scale, rotation);

            var created = await createdPrimTask.ConfigureAwait(false);
            if (created == null)
            {
                return PrimCreateResult.FailResult("Timed out waiting for created prim confirmation.");
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                client.Objects.SetName(sim, created.LocalID, name);
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                client.Objects.SetDescription(sim, created.LocalID, description);
            }

            return PrimCreateResult.OkResult(
                created.LocalID,
                $"Created {shape} prim localId={created.LocalID} at {FormatVector(created.Position)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimPositionAsync(uint localId, float x, float y, float z, bool childOnly, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var position = ClampLocalPosition(new Vector3(x, y, z));
            client.Objects.SetPosition(sim, localId, position, childOnly);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} position to {FormatVector(position)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimScaleAsync(uint localId, float x, float y, float z, bool childOnly, bool uniform, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var scale = ClampScale(new Vector3(x, y, z));
            client.Objects.SetScale(sim, localId, scale, childOnly, uniform);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} scale to {FormatVector(scale)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimRotationEulerAsync(uint localId, float rollDegrees, float pitchDegrees, float yawDegrees, bool childOnly, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var quat = Quaternion.CreateFromEulers(
                rollDegrees * Utils.DEG_TO_RAD,
                pitchDegrees * Utils.DEG_TO_RAD,
                yawDegrees * Utils.DEG_TO_RAD);
            client.Objects.SetRotation(sim, localId, quat, childOnly);
            return Task.FromResult(BotToolResult.OkResult(
                $"Set prim {localId} rotation to roll={rollDegrees:F2}, pitch={pitchDegrees:F2}, yaw={yawDegrees:F2} degrees."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimTextureAsync(uint localId, string textureId, int faceIndex, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(textureId, out var textureUuid))
        {
            return BotToolResult.Fail("textureId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            if (faceIndex < 0)
            {
                te.DefaultTexture ??= new Primitive.TextureEntryFace(null);
                te.DefaultTexture.TextureID = textureUuid;
                client.Objects.SetTextures(sim, localId, te);
                return Task.FromResult(BotToolResult.OkResult($"Set default texture on prim {localId} to {textureUuid}."));
            }

            if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
            {
                return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
            }

            var face = te.CreateFace((uint)faceIndex);
            face.TextureID = textureUuid;
            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Set texture on prim {localId} face {faceIndex} to {textureUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimFaceParamsAsync(
        uint localId,
        int faceIndex,
        float? red,
        float? green,
        float? blue,
        float? alpha,
        float? repeatU,
        float? repeatV,
        float? offsetU,
        float? offsetV,
        float? rotationRadians,
        float? glow,
        bool? fullbright,
        string? shiny,
        string? bump,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            if (red.HasValue || green.HasValue || blue.HasValue || alpha.HasValue)
            {
                var rgba = face.RGBA;
                var r = Math.Clamp(red ?? rgba.R, 0f, 1f);
                var g = Math.Clamp(green ?? rgba.G, 0f, 1f);
                var b = Math.Clamp(blue ?? rgba.B, 0f, 1f);
                var a = Math.Clamp(alpha ?? rgba.A, 0f, 1f);
                face.RGBA = new Color4(r, g, b, a);
            }

            if (repeatU.HasValue)
            {
                face.RepeatU = repeatU.Value;
            }

            if (repeatV.HasValue)
            {
                face.RepeatV = repeatV.Value;
            }

            if (offsetU.HasValue)
            {
                face.OffsetU = Math.Clamp(offsetU.Value, -1f, 1f);
            }

            if (offsetV.HasValue)
            {
                face.OffsetV = Math.Clamp(offsetV.Value, -1f, 1f);
            }

            if (rotationRadians.HasValue)
            {
                face.Rotation = rotationRadians.Value;
            }

            if (glow.HasValue)
            {
                face.Glow = Math.Clamp(glow.Value, 0f, 1f);
            }

            if (fullbright.HasValue)
            {
                face.Fullbright = fullbright.Value;
            }

            if (!string.IsNullOrWhiteSpace(shiny))
            {
                if (!Enum.TryParse<Shininess>(shiny, true, out var shinyValue))
                {
                    return Task.FromResult(BotToolResult.Fail("Invalid shiny value. Use: None, Low, Medium, High."));
                }

                face.Shiny = shinyValue;
            }

            if (!string.IsNullOrWhiteSpace(bump))
            {
                if (!Enum.TryParse<Bumpiness>(bump, true, out var bumpValue))
                {
                    return Task.FromResult(BotToolResult.Fail("Invalid bump value. Use values from Bumpiness enum (e.g. None, Brightness, Darkness, Woodgrain)."));
                }

                face.Bump = bumpValue;
            }

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Updated {faceLabel} parameters on prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> NudgePrimFaceUvAsync(
        uint localId,
        int faceIndex,
        float? deltaRepeatU,
        float? deltaRepeatV,
        float? deltaOffsetU,
        float? deltaOffsetV,
        float? deltaRotationRadians,
        CancellationToken cancellationToken)
    {
        if (!deltaRepeatU.HasValue
            && !deltaRepeatV.HasValue
            && !deltaOffsetU.HasValue
            && !deltaOffsetV.HasValue
            && !deltaRotationRadians.HasValue)
        {
            return BotToolResult.Fail("At least one delta value is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            if (deltaRepeatU.HasValue)
            {
                face.RepeatU += deltaRepeatU.Value;
            }

            if (deltaRepeatV.HasValue)
            {
                face.RepeatV += deltaRepeatV.Value;
            }

            if (deltaOffsetU.HasValue)
            {
                face.OffsetU = Math.Clamp(face.OffsetU + deltaOffsetU.Value, -1f, 1f);
            }

            if (deltaOffsetV.HasValue)
            {
                face.OffsetV = Math.Clamp(face.OffsetV + deltaOffsetV.Value, -1f, 1f);
            }

            if (deltaRotationRadians.HasValue)
            {
                face.Rotation += deltaRotationRadians.Value;
            }

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Nudged UV parameters on {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ApplyPrimFaceUvPresetAsync(
        uint localId,
        int faceIndex,
        string preset,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return BotToolResult.Fail("preset is required. Use: fit, reset, tile2x2, tile4x4, flipU, flipV, rotate90, rotate180, rotate270, center.");
        }

        var normalized = preset.Trim().ToLowerInvariant();

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            switch (normalized)
            {
                case "fit":
                case "reset":
                    face.RepeatU = 1f;
                    face.RepeatV = 1f;
                    face.OffsetU = 0f;
                    face.OffsetV = 0f;
                    face.Rotation = 0f;
                    break;
                case "tile2x2":
                    face.RepeatU = 2f;
                    face.RepeatV = 2f;
                    break;
                case "tile4x4":
                    face.RepeatU = 4f;
                    face.RepeatV = 4f;
                    break;
                case "flipu":
                    face.RepeatU = -face.RepeatU;
                    break;
                case "flipv":
                    face.RepeatV = -face.RepeatV;
                    break;
                case "rotate90":
                    face.Rotation += MathF.PI / 2f;
                    break;
                case "rotate180":
                    face.Rotation += MathF.PI;
                    break;
                case "rotate270":
                    face.Rotation += (MathF.PI * 3f) / 2f;
                    break;
                case "center":
                    face.OffsetU = 0f;
                    face.OffsetV = 0f;
                    break;
                default:
                    return Task.FromResult(BotToolResult.Fail("Unknown preset. Use: fit, reset, tile2x2, tile4x4, flipU, flipV, rotate90, rotate180, rotate270, center."));
            }

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Applied UV preset '{preset}' to {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TilePrimFaceUvAsync(
        uint localId,
        int faceIndex,
        float repeat,
        CancellationToken cancellationToken)
    {
        if (repeat <= 0f)
        {
            return BotToolResult.Fail("repeat must be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            face.RepeatU = repeat;
            face.RepeatV = repeat;

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Set tiling to {repeat:F2}x{repeat:F2} on {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TilePrimFaceUvNonUniformAsync(
        uint localId,
        int faceIndex,
        float repeatU,
        float repeatV,
        CancellationToken cancellationToken)
    {
        if (repeatU <= 0f || repeatV <= 0f)
        {
            return BotToolResult.Fail("repeatU and repeatV must both be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            Primitive.TextureEntry te;
            if (sim.ObjectsPrimitives.TryGetValue(localId, out var prim) && prim.Textures != null)
            {
                te = new Primitive.TextureEntry(prim.Textures);
            }
            else
            {
                te = new Primitive.TextureEntry(Primitive.TextureEntry.WHITE_TEXTURE);
            }

            Primitive.TextureEntryFace face;
            var faceLabel = faceIndex < 0 ? "default" : $"face {faceIndex}";
            if (faceIndex < 0)
            {
                face = te.DefaultTexture ?? new Primitive.TextureEntryFace(null);
                te.DefaultTexture = face;
            }
            else
            {
                if (faceIndex >= Primitive.TextureEntry.MAX_FACES)
                {
                    return Task.FromResult(BotToolResult.Fail($"faceIndex must be between 0 and {Primitive.TextureEntry.MAX_FACES - 1}, or -1 for default."));
                }

                face = te.CreateFace((uint)faceIndex);
            }

            face.RepeatU = repeatU;
            face.RepeatV = repeatV;

            client.Objects.SetTextures(sim, localId, te);
            return Task.FromResult(BotToolResult.OkResult($"Set tiling to U={repeatU:F2}, V={repeatV:F2} on {faceLabel} of prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimInspectResult> InspectPrimAsync(uint localId, bool includeFaceTextures, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimInspectResult.FailResult("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return Task.FromResult(PrimInspectResult.FailResult($"Prim {localId} not found in current simulator cache."));
            }

            var faceTextures = new List<PrimFaceTextureInfo>();
            string? defaultTextureId = null;
            if (prim.Textures?.DefaultTexture != null)
            {
                defaultTextureId = prim.Textures.DefaultTexture.TextureID.ToString();
            }

            if (includeFaceTextures && prim.Textures != null)
            {
                for (var i = 0; i < Primitive.TextureEntry.MAX_FACES; i++)
                {
                    var face = prim.Textures.FaceTextures[i];
                    if (face == null)
                    {
                        continue;
                    }

                    faceTextures.Add(new PrimFaceTextureInfo(i, face.TextureID.ToString()));
                }
            }

            var info = new PrimInfo(
                prim.LocalID,
                prim.ID.ToString(),
                prim.ParentID,
                prim.Type.ToString(),
                prim.PrimData.PathCurve.ToString(),
                prim.PrimData.ProfileCurve.ToString(),
                prim.PrimData.Material.ToString(),
                prim.Position.X,
                prim.Position.Y,
                prim.Position.Z,
                prim.Scale.X,
                prim.Scale.Y,
                prim.Scale.Z,
                prim.Rotation.X,
                prim.Rotation.Y,
                prim.Rotation.Z,
                prim.Rotation.W,
                prim.Properties?.Name,
                prim.Properties?.Description,
                prim.Properties?.OwnerID.ToString(),
                prim.Properties?.CreatorID.ToString(),
                defaultTextureId,
                faceTextures);

            return Task.FromResult(PrimInspectResult.OkResult(info));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SelectPrimAsync(uint localId, bool automaticDeselect, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SelectObject(sim, localId, automaticDeselect);
            return Task.FromResult(BotToolResult.OkResult(
                automaticDeselect
                    ? $"Selected prim {localId} (auto-deselect enabled)."
                    : $"Selected prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DeselectPrimAsync(uint localId, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.DeselectObject(sim, localId);
            return Task.FromResult(BotToolResult.OkResult($"Deselected prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DeletePrimAsync(uint localId, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            // DeRez to inventory; if localId is a child prim, simulator deletes the whole linkset.
            client.Inventory.RequestDeRezToInventory(localId);
            return Task.FromResult(BotToolResult.OkResult($"Delete request sent for prim {localId} (de-rez to inventory)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DeleteManyPrimsAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count == 0)
        {
            return BotToolResult.Fail("At least one local ID is required to delete prims.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            foreach (var localId in localIds)
            {
                // DeRez to inventory; if localId is a child prim, simulator deletes the whole linkset.
                client.Inventory.RequestDeRezToInventory(localId);
            }

            return Task.FromResult(BotToolResult.OkResult($"Delete request sent for {localIds.Count} prim(s): {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimQueryResult> FindPrimsByNameAsync(string name, int maxResults, bool caseSensitive, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return PrimQueryResult.FailResult("name is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimQueryResult.FailResult("No current simulator available."));
            }

            var limit = Math.Clamp(maxResults, 1, 500);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var at = client.Self.SimPosition;

            var prims = sim.ObjectsPrimitives.Values
                .Where(p => !string.IsNullOrWhiteSpace(p.Properties?.Name)
                    && p.Properties!.Name.Contains(name, comparison))
                .Select(p => ToPrimSummary(p, at))
                .OrderBy(p => p.DistanceMeters)
                .ThenBy(p => p.LocalId)
                .Take(limit)
                .ToList();

            return Task.FromResult(PrimQueryResult.OkResult(prims, $"Matched {prims.Count} prim(s)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimQueryResult> ListNearbyPrimsAsync(float radiusMeters, int maxResults, CancellationToken cancellationToken)
    {
        if (radiusMeters <= 0f)
        {
            return PrimQueryResult.FailResult("radiusMeters must be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimQueryResult.FailResult("No current simulator available."));
            }

            var limit = Math.Clamp(maxResults, 1, 500);
            var radius = Math.Clamp(radiusMeters, 0.1f, 4096f);
            var at = client.Self.SimPosition;

            var prims = sim.ObjectsPrimitives.Values
                .Select(p => ToPrimSummary(p, at))
                .Where(p => p.DistanceMeters <= radius)
                .OrderBy(p => p.DistanceMeters)
                .ThenBy(p => p.LocalId)
                .Take(limit)
                .ToList();

            return Task.FromResult(PrimQueryResult.OkResult(prims, $"Found {prims.Count} nearby prim(s) within {radius:F2}m."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimNameAsync(uint localId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BotToolResult.Fail("name is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SetName(sim, localId, name);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} name to '{name}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimDescriptionAsync(uint localId, string description, CancellationToken cancellationToken)
    {
        if (description == null)
        {
            return BotToolResult.Fail("description is required (empty string is allowed).");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SetDescription(sim, localId, description);
            return Task.FromResult(BotToolResult.OkResult($"Set prim {localId} description."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> LinkPrimsAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count < 2)
        {
            return BotToolResult.Fail("At least two local IDs are required to link prims.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.LinkPrims(sim, localIds);
            return Task.FromResult(BotToolResult.OkResult($"Link request sent for prims: {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> UnlinkPrimsAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count == 0)
        {
            return BotToolResult.Fail("At least one local ID is required to unlink prims.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.DelinkPrims(sim, localIds);
            return Task.FromResult(BotToolResult.OkResult($"Unlink request sent for prims: {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimCreateResult> ClonePrimAsync(
        uint sourceLocalId,
        float offsetX,
        float offsetY,
        float offsetZ,
        bool copyTextures,
        bool copyName,
        bool copyDescription,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return PrimCreateResult.FailResult("No current simulator available.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(sourceLocalId, out var sourcePrim))
            {
                return PrimCreateResult.FailResult($"Source prim {sourceLocalId} not found in current simulator cache.");
            }

            var newPosition = ClampLocalPosition(new Vector3(
                sourcePrim.Position.X + offsetX,
                sourcePrim.Position.Y + offsetY,
                sourcePrim.Position.Z + offsetZ));
            var newScale = ClampScale(sourcePrim.Scale);
            var newRotation = sourcePrim.Rotation;
            var primData = new Primitive.ConstructionData(sourcePrim.PrimData);

            var createdPrimTask = WaitForCreatedPrimAsync(client, sim, newPosition, token);
            client.Objects.AddPrim(sim, primData, client.Self.ActiveGroup, newPosition, newScale, newRotation);

            var created = await createdPrimTask.ConfigureAwait(false);
            if (created == null)
            {
                return PrimCreateResult.FailResult("Timed out waiting for cloned prim confirmation.");
            }

            if (copyTextures && sourcePrim.Textures != null)
            {
                client.Objects.SetTextures(sim, created.LocalID, new Primitive.TextureEntry(sourcePrim.Textures));
            }

            if (copyName && sourcePrim.Properties != null && !string.IsNullOrWhiteSpace(sourcePrim.Properties.Name))
            {
                client.Objects.SetName(sim, created.LocalID, sourcePrim.Properties.Name);
            }

            if (copyDescription && sourcePrim.Properties != null && sourcePrim.Properties.Description != null)
            {
                client.Objects.SetDescription(sim, created.LocalID, sourcePrim.Properties.Description);
            }

            return PrimCreateResult.OkResult(
                created.LocalID,
                $"Cloned prim {sourceLocalId} -> {created.LocalID} at {FormatVector(created.Position)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> MoveByAsync(string direction, float meters, bool fly, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return BotToolResult.Fail("direction is required.");
        }

        if (meters <= 0f)
        {
            return BotToolResult.Fail("meters must be greater than 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var delta = ResolveDelta(direction, meters, client);
            var from = client.Self.SimPosition;
            var target = ClampLocalPosition(new Vector3(from.X + delta.X, from.Y + delta.Y, from.Z + delta.Z));
            return await MoveToLocalPositionCoreAsync(client, target, fly, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> MoveToAsync(float x, float y, float z, bool fly, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync(
            (client, token) => MoveToLocalPositionCoreAsync(client, target, fly, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportToAsync(float x, float y, float z, string? regionName, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var currentSim = client.Network.CurrentSim;
            if (currentSim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            bool ok;
            string destinationLabel;
            if (string.IsNullOrWhiteSpace(regionName) || string.Equals(regionName, currentSim.Name, StringComparison.OrdinalIgnoreCase))
            {
                destinationLabel = currentSim.Name;
                ok = await client.Self.TeleportAsync(currentSim.Name, target, token).ConfigureAwait(false);
            }
            else
            {
                var region = await client.Grid.GetGridRegionAsync(regionName, GridLayerType.Objects, token).ConfigureAwait(false);
                if (!region.HasValue)
                {
                    return BotToolResult.Fail($"Unable to resolve region '{regionName}' to a region handle.");
                }

                destinationLabel = $"{region.Value.Name} ({region.Value.RegionHandle})";
                ok = await client.Self.TeleportAsync(region.Value.RegionHandle, target, token).ConfigureAwait(false);
            }

            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(client.Self.TeleportMessage)
                    ? "Teleport failed."
                    : client.Self.TeleportMessage;
                return BotToolResult.Fail(message);
            }

            var at = client.Self.SimPosition;
            return BotToolResult.OkResult($"Teleported to {destinationLabel} at {FormatVector(at)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportToRegionHandleAsync(string regionHandle, float x, float y, float z, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(regionHandle))
        {
            return BotToolResult.Fail("regionHandle is required.");
        }

        if (!ulong.TryParse(regionHandle, out var handle))
        {
            return BotToolResult.Fail("regionHandle must be an unsigned 64-bit integer.");
        }

        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Self.TeleportAsync(handle, target, token).ConfigureAwait(false);
            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(client.Self.TeleportMessage)
                    ? "Teleport failed."
                    : client.Self.TeleportMessage;
                return BotToolResult.Fail(message);
            }

            return BotToolResult.OkResult($"Teleported to region handle {handle} at {FormatVector(client.Self.SimPosition)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> StopMovementAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            client.Self.AutoPilotCancel();
            client.Self.Movement.ResetControlFlags();
            client.Self.Movement.SendUpdate(true);
            return Task.FromResult(BotToolResult.OkResult("Movement stopped (autopilot canceled, control flags reset)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        var client = _client;
        _client = null;
        _connected = false;

        if (client == null)
        {
            return;
        }

        client.Self.IM -= OnInstantMessage;
        client.Self.ChatFromSimulator -= OnChatFromSimulator;
        client.Inventory.InventoryObjectOffered -= OnInventoryObjectOffered;
        client.Network.Disconnected -= OnDisconnected;
        client.Network.LoginProgress -= OnLoginProgress;

        try
        {
            client.Network.Logout();
        }
        catch
        {
            // No-op during shutdown.
        }

        client.Dispose();
        if (_opencodeChat is IDisposable disposableOpencodeChat)
        {
            disposableOpencodeChat.Dispose();
        }

        foreach (var gate in _imConversationLocks.Values)
        {
            gate.Dispose();
        }

        _actionGate.Dispose();
    }

    private async Task<BotToolResult> RunActionAsync(string successMessage, Action<GridClient> action, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            action(client);
            return Task.FromResult(BotToolResult.OkResult(successMessage));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PrimCreateResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<PrimCreateResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PrimCreateResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<PrimInspectResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<PrimInspectResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PrimInspectResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<PrimQueryResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<PrimQueryResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return PrimQueryResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<BotToolResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<BotToolResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BotToolResult.Fail(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<EnvironmentToolResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<EnvironmentToolResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return EnvironmentToolResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private GridClient EnsureClient()
    {
        if (!_connected || _client == null)
        {
            throw new InvalidOperationException("Bot is not connected.");
        }

        return _client;
    }

    private static string FormatWhereText(GridClient client)
    {
        var sim = client.Network.CurrentSim?.Name ?? "unknown";
        var pos = client.Self.SimPosition;
        return $"I'm in {sim} at <{pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}>";
    }

    private static bool TryBuildConstructionData(string shape, string material, out Primitive.ConstructionData primData, out string error)
    {
        primData = BuildDefaultConstructionData();
        error = string.Empty;

        var normalizedShape = (shape ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalizedShape)
        {
            case "box":
            case "cube":
                primData.PathCurve = PathCurve.Line;
                primData.ProfileCurve = ProfileCurve.Square;
                break;
            case "cylinder":
                primData.PathCurve = PathCurve.Line;
                primData.ProfileCurve = ProfileCurve.Circle;
                break;
            case "prism":
                primData.PathCurve = PathCurve.Line;
                primData.ProfileCurve = ProfileCurve.EqualTriangle;
                break;
            case "sphere":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.HalfCircle;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 1f;
                break;
            case "torus":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.Circle;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 0.25f;
                break;
            case "tube":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.Square;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 0.25f;
                break;
            case "ring":
                primData.PathCurve = PathCurve.Circle;
                primData.ProfileCurve = ProfileCurve.EqualTriangle;
                primData.PathScaleX = 1f;
                primData.PathScaleY = 0.25f;
                break;
            default:
                error = "Unsupported shape. Use: box, cylinder, prism, sphere, torus, tube, ring.";
                return false;
        }

        if (!Enum.TryParse<Material>((material ?? string.Empty).Trim(), true, out var parsedMaterial))
        {
            error = "Unsupported material. Use: Stone, Metal, Glass, Wood, Flesh, Plastic, Rubber, Light.";
            return false;
        }

        primData.Material = parsedMaterial;
        return true;
    }

    private static Primitive.ConstructionData BuildDefaultConstructionData()
    {
        return new Primitive.ConstructionData
        {
            PCode = PCode.Prim,
            Material = Material.Wood,
            PathCurve = PathCurve.Line,
            PathBegin = 0f,
            PathEnd = 1f,
            PathRadiusOffset = 0f,
            PathSkew = 0f,
            PathScaleX = 1f,
            PathScaleY = 1f,
            PathShearX = 0f,
            PathShearY = 0f,
            PathTaperX = 0f,
            PathTaperY = 0f,
            PathTwist = 0f,
            PathTwistBegin = 0f,
            PathRevolutions = 1f,
            ProfileBegin = 0f,
            ProfileEnd = 1f,
            ProfileHollow = 0f,
            ProfileCurve = ProfileCurve.Square,
            ProfileHole = HoleType.Same
        };
    }

    private static Vector3 ResolveDelta(string direction, float meters, GridClient client)
    {
        var normalized = direction.Trim().ToLowerInvariant();
        return normalized switch
        {
            "north" => new Vector3(0f, meters, 0f),
            "south" => new Vector3(0f, -meters, 0f),
            "east" => new Vector3(meters, 0f, 0f),
            "west" => new Vector3(-meters, 0f, 0f),
            "up" => new Vector3(0f, 0f, meters),
            "down" => new Vector3(0f, 0f, -meters),
            "forward" => ScaleToLength(Flatten(client.Self.Movement.Camera.AtAxis), meters),
            "back" or "backward" => ScaleToLength(Flatten(Negate(client.Self.Movement.Camera.AtAxis)), meters),
            "left" => ScaleToLength(Flatten(client.Self.Movement.Camera.LeftAxis), meters),
            "right" => ScaleToLength(Flatten(Negate(client.Self.Movement.Camera.LeftAxis)), meters),
            _ => throw new ArgumentException("Unsupported direction. Use: north, south, east, west, up, down, forward, back, left, right")
        };
    }

    private async Task<BotToolResult> MoveToLocalPositionCoreAsync(
        GridClient client,
        Vector3 target,
        bool fly,
        CancellationToken cancellationToken)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return BotToolResult.Fail("No current simulator available.");
        }

        var from = client.Self.SimPosition;

        var distance = Vector3.Distance(from, target);
        if (distance <= 1.0f)
        {
            return BotToolResult.OkResult($"Already at {FormatVector(from)}.");
        }

        var maxStepMeters = 48f;
        var steps = Math.Max(1, (int)MathF.Ceiling(distance / maxStepMeters));

        try
        {
            client.Self.Fly(fly);

            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ratio = step / (float)steps;
                var waypoint = ClampLocalPosition(Interpolate(from, target, ratio));
                var current = client.Self.SimPosition;
                var legDistance = MathF.Max(1f, Vector3.Distance(current, waypoint));
                var timeoutSeconds = Math.Clamp((int)MathF.Ceiling(legDistance * 0.9f), 10, 40);

                client.Self.AutoPilotLocal(
                    (int)MathF.Round(waypoint.X),
                    (int)MathF.Round(waypoint.Y),
                    waypoint.Z);

                var reached = await WaitForArrivalAsync(
                        client,
                        waypoint,
                        step == steps ? 1.5f : 2.5f,
                        TimeSpan.FromSeconds(timeoutSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!reached)
                {
                    var atTimeout = client.Self.SimPosition;
                    return BotToolResult.Fail(
                        $"Movement timed out on step {step}/{steps}. Current {FormatVector(atTimeout)}, waypoint {FormatVector(waypoint)}, final target {FormatVector(target)}.");
                }
            }
        }
        finally
        {
            client.Self.AutoPilotCancel();
        }

        var mode = fly ? "flying" : "walking";
        return BotToolResult.OkResult($"Moved by {mode} from {FormatVector(from)} to {FormatVector(client.Self.SimPosition)}.");
    }

    private static async Task<bool> WaitForArrivalAsync(
        GridClient client,
        Vector3 target,
        float tolerance,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var at = client.Self.SimPosition;
            if (Vector3.Distance(at, target) <= tolerance)
            {
                return true;
            }

            if ((DateTime.UtcNow - startedAt) >= timeout)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static Vector3 ClampLocalPosition(Vector3 pos)
    {
        return new Vector3(
            Math.Clamp(pos.X, 1f, 255f),
            Math.Clamp(pos.Y, 1f, 255f),
            Math.Clamp(pos.Z, 0f, 4096f));
    }

    private static Vector3 ClampScale(Vector3 scale)
    {
        return new Vector3(
            Math.Clamp(scale.X, 0.01f, 64f),
            Math.Clamp(scale.Y, 0.01f, 64f),
            Math.Clamp(scale.Z, 0.01f, 64f));
    }

    private static Vector3 Flatten(Vector3 source)
    {
        return new Vector3(source.X, source.Y, 0f);
    }

    private static Vector3 Negate(Vector3 source)
    {
        return new Vector3(-source.X, -source.Y, -source.Z);
    }

    private static Vector3 ScaleToLength(Vector3 source, float length)
    {
        var norm = source.Length();
        if (norm <= 0.0001f)
        {
            return new Vector3(0f, length, 0f);
        }

        var scale = length / norm;
        return new Vector3(source.X * scale, source.Y * scale, source.Z * scale);
    }

    private static Vector3 Interpolate(Vector3 from, Vector3 to, float ratio)
    {
        return new Vector3(
            from.X + ((to.X - from.X) * ratio),
            from.Y + ((to.Y - from.Y) * ratio),
            from.Z + ((to.Z - from.Z) * ratio));
    }

    private static string FormatVector(Vector3 pos)
    {
        return $"<{pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}>";
    }

    private static PrimSummary ToPrimSummary(Primitive prim, Vector3 at)
    {
        return new PrimSummary(
            prim.LocalID,
            prim.ID.ToString(),
            prim.ParentID,
            prim.Properties?.Name,
            prim.Type.ToString(),
            prim.Position.X,
            prim.Position.Y,
            prim.Position.Z,
            Vector3.Distance(at, prim.Position));
    }

    private static bool TryParseLocalIdsCsv(string localIdsCsv, out List<uint> localIds, out string error)
    {
        localIds = new List<uint>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(localIdsCsv))
        {
            error = "localIdsCsv is required (comma-separated local IDs).";
            return false;
        }

        var parts = localIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "No valid local IDs were provided.";
            return false;
        }

        foreach (var part in parts)
        {
            if (!uint.TryParse(part, out var id))
            {
                error = $"Invalid local ID '{part}'. All IDs must be unsigned integers.";
                return false;
            }

            if (!localIds.Contains(id))
            {
                localIds.Add(id);
            }
        }

        return true;
    }

    private static bool TryParseLlsdPayload(string payload, string payloadFormat, out OSD osd, out string error)
    {
        osd = new OSD();
        error = string.Empty;

        var format = (payloadFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(format))
        {
            format = "auto";
        }

        try
        {
            osd = format switch
            {
                "auto" => OSDParser.Deserialize(payload),
                "json" => OSDParser.DeserializeJson(payload),
                "xml" or "llsdxml" or "llsd-xml" => OSDParser.DeserializeLLSDXml(payload),
                _ => throw new ArgumentException("payloadFormat must be one of: auto, json, xml.")
            };

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse LLSD payload ({format}): {ex.Message}";
            return false;
        }
    }

    private static bool TryBuildEnvironmentDataFromPayloadMap(OSDMap payloadMap, out EnvironmentData environment, out string error)
    {
        environment = new EnvironmentData();
        error = string.Empty;

        // Accept either a direct EnvironmentData map or an ExtEnvironment-style wrapper map
        // containing an "environment" map.
        OSDMap? environmentMap = null;
        if (payloadMap.TryGetValue("environment", out var wrappedEnvironment))
        {
            environmentMap = wrappedEnvironment as OSDMap;
            if (environmentMap == null)
            {
                error = "payload contains an 'environment' key, but its value is not an LLSD map/object.";
                return false;
            }
        }
        else
        {
            environmentMap = payloadMap;
        }

        try
        {
            environment.Deserialize(environmentMap);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to deserialize EnvironmentData payload: {ex.Message}";
            return false;
        }
    }

    private static async Task<Primitive?> WaitForCreatedPrimAsync(
        GridClient client,
        Simulator simulator,
        Vector3 expectedPosition,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Primitive>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnObjectUpdate(object? sender, PrimEventArgs e)
        {
            if (!ReferenceEquals(e.Simulator, simulator))
            {
                return;
            }

            if ((e.Prim.Flags & PrimFlags.CreateSelected) == 0)
            {
                return;
            }

            if (Vector3.Distance(e.Prim.Position, expectedPosition) > 24f)
            {
                return;
            }

            tcs.TrySetResult(e.Prim);
        }

        client.Objects.ObjectUpdate += OnObjectUpdate;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Objects.ObjectUpdate -= OnObjectUpdate;
        }
    }

    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        var client = _client;
        if (client == null || e.IM.FromAgentID == client.Self.AgentID)
        {
            return;
        }

        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent
            && e.IM.Dialog != InstantMessageDialog.SessionSend)
        {
            return;
        }

        var from = e.IM.FromAgentName;
        var text = e.IM.Message?.Trim() ?? string.Empty;
        Console.WriteLine($"[im] ({e.IM.Dialog}) {from}: {SanitizeImLogText(text)}");

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (IsLikelyTypingIndicator(e.IM, text))
        {
            Console.WriteLine($"[im] typing indicator ignored for {from} ({e.IM.Dialog}).");
            return;
        }

        if (IsDuplicateImEvent(e.IM.FromAgentID, text, e.IM.Timestamp))
        {
            Console.WriteLine($"[im] duplicate suppressed for {from} ({e.IM.Dialog}).");
            return;
        }

        if (IsHandlerRestricted() && !IsHandlerAvatar(from))
        {
            Console.WriteLine($"[im] denied non-handler IM from {from}. Handler is '{_handlerFullName}'.");
            try
            {
                client.Self.InstantMessage(e.IM.FromAgentID, $"Hi! I can currently only accept instructions from my handler ({_handlerFullName}).");
            }
            catch
            {
                // Ignore failures while trying to send access-denied feedback.
            }

            return;
        }

        var conversationKey = $"im:{e.IM.FromAgentID}";

        _ = Task.Run(async () =>
        {
            var gate = _imConversationLocks.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0).ConfigureAwait(false))
            {
                if (text.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*permission", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("*question", StringComparison.OrdinalIgnoreCase))
                {
                    var handledBusyCommand = await TryHandleStarCommandAsync(client, e.IM.FromAgentID, from, conversationKey, text).ConfigureAwait(false);
                    if (handledBusyCommand)
                    {
                        return;
                    }
                }

                var handledBusyQuestion = await TryHandlePendingQuestionBeforeRoutingAsync(
                    client,
                    e.IM.FromAgentID,
                    from,
                    conversationKey,
                    text).ConfigureAwait(false);
                if (handledBusyQuestion)
                {
                    return;
                }

                if (TryParseSimplePermissionResponse(text, out var busyResponse, out var busyRemember))
                {
                    var handledBusyResponse = await TryHandleImplicitPermissionResponseAsync(
                        client,
                        e.IM.FromAgentID,
                        from,
                        conversationKey,
                        busyResponse,
                        busyRemember).ConfigureAwait(false);
                    if (handledBusyResponse)
                    {
                        return;
                    }
                }

                if (TryParseSimpleQuestionResponse(text, out var busyQuestionResponse))
                {
                    var handledQuestion = await TryHandleImplicitQuestionResponseAsync(
                        client,
                        e.IM.FromAgentID,
                        from,
                        conversationKey,
                        busyQuestionResponse).ConfigureAwait(false);
                    if (handledQuestion)
                    {
                        return;
                    }
                }

                if (text.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCancelCommandAsync(client, e.IM.FromAgentID, from, conversationKey).ConfigureAwait(false);
                    return;
                }

                try
                {
                    Console.WriteLine($"[im] overlapping message while previous request is still in flight for {from} ({conversationKey}).");
                    client.Self.InstantMessage(e.IM.FromAgentID, "I am still working on your previous request. You can send *cancel to abort while waiting.");
                }
                catch
                {
                    // Ignore failures while trying to report overlap state.
                }

                return;
            }

            var startedAt = Stopwatch.StartNew();
            try
            {
                if (_opencodeChat == null)
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, "AI chat is currently disabled by configuration.");
                    return;
                }

                if (text.StartsWith('*'))
                {
                    var handled = await TryHandleStarCommandAsync(client, e.IM.FromAgentID, from, conversationKey, text).ConfigureAwait(false);
                    if (handled)
                    {
                        return;
                    }
                }

                var handledQuestion = await TryHandlePendingQuestionBeforeRoutingAsync(
                    client,
                    e.IM.FromAgentID,
                    from,
                    conversationKey,
                    text).ConfigureAwait(false);
                if (handledQuestion)
                {
                    return;
                }

                if (TryParseSimplePermissionResponse(text, out var permissionResponse, out var rememberPermission))
                {
                    var handledPermission = await TryHandleImplicitPermissionResponseAsync(
                        client,
                        e.IM.FromAgentID,
                        from,
                        conversationKey,
                        permissionResponse,
                        rememberPermission).ConfigureAwait(false);
                    if (handledPermission)
                    {
                        return;
                    }
                }

                if (TryParseSimpleQuestionResponse(text, out var questionResponse))
                {
                    var handledQuestionReply = await TryHandleImplicitQuestionResponseAsync(
                        client,
                        e.IM.FromAgentID,
                        from,
                        conversationKey,
                        questionResponse).ConfigureAwait(false);
                    if (handledQuestionReply)
                    {
                        return;
                    }
                }

                var sendOptions = BuildSendOptions(conversationKey);
                // TEMP(event-first migration): remove this watcher once event-driven permission/question
                // routing is proven stable under reconnect/load; keep only bounded fallback polling.
                using var inFlightQuestionWatchCts = new CancellationTokenSource();
                var inFlightQuestionWatchTask = Task.Run(() =>
                    NotifyPendingQuestionDuringInFlightRequestAsync(
                        client,
                        e.IM.FromAgentID,
                        from,
                        conversationKey,
                        inFlightQuestionWatchCts.Token));

                // TODO(security): enforce who the AI is allowed to talk to (allowlist, roles, or parcel/group checks).
                Console.WriteLine($"[im] routing to opencode: from={from} conversation={conversationKey} textLength={text.Length} model={(sendOptions?.ModelId ?? "(default)")}");
                var reply = await _opencodeChat.SendMessageAsync(
                    conversationKey: conversationKey,
                    title: $"OpenSim IM with {from}",
                    message: text,
                    options: sendOptions,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                startedAt.Stop();
                Console.WriteLine($"[im] opencode reply received in {startedAt.ElapsedMilliseconds}ms: from={from} conversation={conversationKey} replyLength={reply.Text.Length}");

                var responseText = reply.IsConfirmationPrompt
                    ? reply.Text + "\n\nReply with yes or no to continue."
                    : reply.Text;

                if (reply.PendingPermissions != null && reply.PendingPermissions.Count > 0)
                {
                    var latestPermission = reply.PendingPermissions
                        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Id));
                    if (latestPermission != null)
                    {
                        _latestPendingPermissionByConversation[conversationKey] = latestPermission.Id;
                        _announcedPendingPermissionByConversation[conversationKey] = latestPermission.Id;
                        responseText += "\n\n" + BuildFriendlyPermissionPrompt(latestPermission);
                    }

                    if (reply.PendingPermissions.Count > 1)
                    {
                        responseText += $"\n(There are {reply.PendingPermissions.Count - 1} more pending approvals.)";
                    }
                }
                else
                {
                    var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                    if (!string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        var eventFirstPermissions = await GetPendingPermissionsEventFirstAsync(currentSessionId, CancellationToken.None).ConfigureAwait(false);
                        if (eventFirstPermissions.Count > 0)
                        {
                            var latestPermission = eventFirstPermissions[0];
                            _latestPendingPermissionByConversation[conversationKey] = latestPermission.Id;
                            if (!_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                                || !announcedPermissionId.Equals(latestPermission.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                _announcedPendingPermissionByConversation[conversationKey] = latestPermission.Id;
                                responseText += "\n\n" + BuildFriendlyPermissionPrompt(latestPermission);
                            }
                        }
                    }
                }

                if (reply.PendingQuestions != null && reply.PendingQuestions.Count > 0)
                {
                    var latestQuestion = reply.PendingQuestions
                        .FirstOrDefault(q => !string.IsNullOrWhiteSpace(q.Id));
                    if (latestQuestion != null)
                    {
                        _latestPendingQuestionByConversation[conversationKey] = latestQuestion.Id;
                        _announcedPendingQuestionByConversation[conversationKey] = latestQuestion.Id;
                    }

                    var questionLines = reply.PendingQuestions
                        .Take(3)
                        .Select(q =>
                        {
                            var header = string.IsNullOrWhiteSpace(q.Header) ? "Question" : q.Header.Trim();
                            var optionText = q.Options.Count == 0 ? string.Empty : $" options: {string.Join(", ", q.Options)}";
                            return $"- {header}: {q.Question}{optionText}";
                        })
                        .ToList();
                    questionLines.Insert(0, "Pending question request(s):");
                    questionLines.Add("Reply in chat with your answer (or option number) to continue.");
                    responseText += "\n\n" + string.Join("\n", questionLines);
                }
                else
                {
                    // TEMP(event-first migration): this post-reply poll is a safety net for delayed emits.
                    // Delete after event stream handlers populate pending question state reliably.
                    // Some prompts are emitted asynchronously after the initial message response.
                    var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                    if (!string.IsNullOrWhiteSpace(currentSessionId))
                    {
                        var polledQuestions = await GetPendingQuestionsEventFirstAsync(currentSessionId, CancellationToken.None).ConfigureAwait(false);
                        if (polledQuestions.Count > 0)
                        {
                            _latestPendingQuestionByConversation[conversationKey] = polledQuestions[0].Id;
                            if (!_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedQuestionId)
                                || !announcedQuestionId.Equals(polledQuestions[0].Id, StringComparison.OrdinalIgnoreCase))
                            {
                                _announcedPendingQuestionByConversation[conversationKey] = polledQuestions[0].Id;
                                responseText += "\n\n" + BuildFriendlyQuestionPrompt(polledQuestions[0]);
                            }
                        }
                    }
                }

                foreach (var chunk in SplitForInstantMessage(responseText, 900))
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, chunk);
                    Console.WriteLine($"[im] -> {from}: {chunk}");
                }

                inFlightQuestionWatchCts.Cancel();
                try
                {
                    await inFlightQuestionWatchTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore watcher cancellation or transient polling errors.
                }

                // TEMP(event-first migration): remove this delayed poll task once event-driven prompt
                // delivery is reliable across reconnects and all tested providers.
                // Some question prompts can arrive slightly after the first reply payload.
                _ = Task.Run(() => NotifyPendingQuestionIfAppearsAsync(client, e.IM.FromAgentID, from, conversationKey));
            }
            catch (OperationCanceledException ex) when (IsLikelyBackendTimeout(ex))
            {
                startedAt.Stop();
                Console.WriteLine($"[im] opencode timeout after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                _opencodeChat?.ResetConversation(conversationKey);
                try
                {
                    client.Self.InstantMessage(
                        e.IM.FromAgentID,
                        "The AI is taking longer than expected and timed out. Please try again in a moment.");
                }
                catch
                {
                    // Ignore failures while trying to report backend timeout errors.
                }
            }
            catch (Exception ex)
            {
                startedAt.Stop();
                if (IsLikelyBackendTimeout(ex))
                {
                    Console.WriteLine($"[im] opencode timeout after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                    _opencodeChat?.ResetConversation(conversationKey);
                    try
                    {
                        client.Self.InstantMessage(
                            e.IM.FromAgentID,
                            "The AI is taking longer than expected and timed out. Please try again in a moment.");
                    }
                    catch
                    {
                        // Ignore failures while trying to report backend timeout errors.
                    }

                    return;
                }

                Console.WriteLine($"[im] failed to route to opencode after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                _opencodeChat?.ResetConversation(conversationKey);
                // Preserve per-IM overrides (provider/model/thinking) across transient backend failures.
                try
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, "Sorry, I could not reach the AI service right now.");
                }
                catch
                {
                    // Ignore failures while trying to report backend errors.
                }
            }
            finally
            {
                gate.Release();
            }
        });
    }

    private OpencodeSendOptions? BuildSendOptions(string conversationKey)
    {
        _imConversationConfigs.TryGetValue(conversationKey, out var cfg);

        var systemPrompt = BuildLayeredPromptText();
        var modelId = cfg?.ModelId ?? GetStartupDefaultModelId();
        var thinkingLevel = cfg?.ThinkingLevel;

        if (cfg == null && string.IsNullOrWhiteSpace(systemPrompt) && string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return new OpencodeSendOptions(modelId, thinkingLevel, systemPrompt);
    }

    private string BuildPromptStatusText()
    {
        if (!_options.PromptHandlingEnabled)
        {
            return "prompt: disabled";
        }

        var sources = new List<string>();
        if (_options.PromptBuiltInEnabled)
        {
            sources.Add("builtin");
        }

        if (_options.PromptProjectAgentsEnabled)
        {
            var projectPath = ResolveProjectAgentsPromptPath();
            sources.Add(projectPath == null ? "project(AGENTS.md:missing)" : $"project({projectPath})");
        }

        lock (_promptStateLock)
        {
            if (_options.PromptNotecardEnabled && !string.IsNullOrWhiteSpace(_activeAgentsNotecardPrompt))
            {
                sources.Add($"notecard({_activeAgentsNotecardSourceName ?? "unknown"}, {_activeAgentsNotecardItemId ?? "n/a"})");
            }
        }

        return sources.Count == 0 ? "prompt: no active sources" : "prompt sources: " + string.Join(", ", sources);
    }

    private string? BuildLayeredPromptText()
    {
        if (!_options.PromptHandlingEnabled)
        {
            return null;
        }

        var layers = new List<string>();

        if (_options.PromptBuiltInEnabled)
        {
            layers.Add("[bridge]\n" + ClampPromptLength(BuiltInBridgePrompt));
        }

        if (_options.PromptProjectAgentsEnabled)
        {
            var projectAgents = TryLoadProjectAgentsPromptText();
            if (!string.IsNullOrWhiteSpace(projectAgents))
            {
                layers.Add("[project AGENTS.md]\n" + projectAgents);
            }
        }

        if (_options.PromptNotecardEnabled)
        {
            string? notecardPrompt;
            lock (_promptStateLock)
            {
                notecardPrompt = _activeAgentsNotecardPrompt;
            }

            if (!string.IsNullOrWhiteSpace(notecardPrompt))
            {
                layers.Add("[in-world AGENTS.md notecard]\n" + notecardPrompt);
            }
        }

        return layers.Count == 0 ? null : string.Join("\n\n", layers);
    }

    private string? TryLoadProjectAgentsPromptText()
    {
        var fullPath = ResolveProjectAgentsPromptPath();
        if (fullPath == null)
        {
            return null;
        }

        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);
            lock (_promptStateLock)
            {
                if (_projectAgentsPromptCache != null && lastWriteUtc == _projectAgentsPromptCacheLastWriteUtc)
                {
                    return _projectAgentsPromptCache;
                }
            }

            var raw = File.ReadAllText(fullPath);
            var normalized = NormalizePromptText(raw);
            lock (_promptStateLock)
            {
                _projectAgentsPromptCache = normalized;
                _projectAgentsPromptCacheLastWriteUtc = lastWriteUtc;
            }

            return normalized;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[prompt] failed to read project AGENTS prompt file: {ex.Message}");
            return null;
        }
    }

    private string? ResolveProjectAgentsPromptPath()
    {
        var configured = (_options.PromptProjectAgentsFile ?? "AGENTS.md").Trim();
        if (configured.Length == 0)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(configured);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        // Support running from ./src while keeping strict AGENTS.md semantics at project root.
        if (string.Equals(configured, "AGENTS.md", StringComparison.OrdinalIgnoreCase))
        {
            var cwd = Directory.GetCurrentDirectory();
            var parent = Directory.GetParent(cwd)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var parentPath = Path.Combine(parent, "AGENTS.md");
                if (File.Exists(parentPath))
                {
                    return parentPath;
                }
            }
        }

        return null;
    }

    private string NormalizePromptText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return ClampPromptLength(normalized);
    }

    private string ClampPromptLength(string value)
    {
        var maxChars = _options.PromptMaxChars < 512 ? 512 : _options.PromptMaxChars;
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "\n\n[prompt truncated]";
    }

    private void SetActiveAgentsNotecardPrompt(string promptText, string sourceName, string itemId)
    {
        var normalized = NormalizePromptText(promptText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_promptStateLock)
        {
            _activeAgentsNotecardPrompt = normalized;
            _activeAgentsNotecardSourceName = sourceName;
            _activeAgentsNotecardItemId = itemId;
            _activeAgentsNotecardInstalledAt = DateTimeOffset.UtcNow;
        }
    }

    private void ClearActiveAgentsNotecardPrompt()
    {
        lock (_promptStateLock)
        {
            _activeAgentsNotecardPrompt = null;
            _activeAgentsNotecardSourceName = null;
            _activeAgentsNotecardItemId = null;
            _activeAgentsNotecardInstalledAt = null;
        }
    }

    private void InvalidateProjectAgentsPromptCache()
    {
        lock (_promptStateLock)
        {
            _projectAgentsPromptCache = null;
            _projectAgentsPromptCacheLastWriteUtc = default;
        }
    }

    private static string BuildPromptPreviewText(string sourceName, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"Prompt source '{sourceName}' is empty or unavailable.";
        }

        const int maxPreviewChars = 2400;
        var preview = text.Length <= maxPreviewChars ? text : text[..maxPreviewChars] + "\n\n[prompt preview truncated]";
        return string.Join("\n", $"Prompt source: {sourceName}", preview);
    }

    private static bool IsLikelyBackendTimeout(Exception ex)
    {
        if (ex is TimeoutException)
        {
            return true;
        }

        // HttpClient timeouts often arrive as TaskCanceledException/OperationCanceledException.
        if (ex is TaskCanceledException)
        {
            return true;
        }

        var message = ex.Message ?? string.Empty;
        return message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryHandleStarCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0 || raw[0] != '*')
        {
            return false;
        }

        var commandLine = raw.Length == 1 ? string.Empty : raw[1..].Trim();
        var split = commandLine.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var command = split.Length > 0 ? split[0].ToLowerInvariant() : "help";
        var arg = split.Length > 1 ? split[1] : string.Empty;

        try
        {
            switch (command)
            {
                case "help":
                    SendImText(client, agentId, from, BuildStarHelpText(arg));
                    return true;
                case "status":
                    SendImText(client, agentId, from, BuildConversationStatusText(conversationKey));
                    return true;
                case "reset":
                    _imConversationConfigs.TryRemove(conversationKey, out _);
                    _opencodeChat?.ResetConversation(conversationKey);
                    SendImText(client, agentId, from, "Conversation AI settings reset for this IM. Using server defaults.");
                    return true;
                case "cancel":
                    await HandleCancelCommandAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
                    return true;
                case "providers":
                    await HandleProvidersCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "permission":
                case "permissions":
                    await HandlePermissionCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "question":
                case "questions":
                    await HandleQuestionCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "models":
                    await HandleModelsCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "configure":
                    await HandleConfigureCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "prompt":
                case "prompts":
                    await HandlePromptCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "auth":
                    await HandleAuthCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "session":
                case "sessions":
                    await HandleSessionCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "project":
                case "projects":
                    await HandleProjectCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                default:
                    SendImText(client, agentId, from, $"Unknown command '*{command}'. Try *help.");
                    return true;
            }
        }
        catch (Exception ex)
        {
            SendImText(client, agentId, from, $"Command failed: {ex.Message}");
            return true;
        }
    }

    private static string BuildStarHelpText(string topicArg)
    {
        if (string.IsNullOrWhiteSpace(topicArg))
        {
            return string.Join(
                "\n",
                "Star commands:",
                "*help - Show command summary",
                "*help <command> - Show detailed help for one command",
                "*help all - Show detailed help for all commands",
                "*status - Show active AI and prompt settings for this IM",
                "*cancel - Abort current in-flight AI request for this IM",
                "*prompt - Manage prompt layers (status/show/clear/reload)",
                "*permission - Manage pending permission requests",
                "*question - Manage pending question requests",
                "*providers - List providers",
                "*models - List models",
                "*auth - Provider API key/OAuth flows",
                "*session - Manage Opencode sessions",
                "*project - Inspect Opencode project context",
                "*configure - Configure provider/model/thinking for this IM",
                "*reset - Alias for '*configure reset'");
        }

        var topic = topicArg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.ToLowerInvariant() ?? "help";
        topic = topic switch
        {
            "permissions" => "permission",
            "questions" => "question",
            "projects" => "project",
            "sessions" => "session",
            "prompts" => "prompt",
            _ => topic
        };

        return topic switch
        {
            "help" => string.Join(
                "\n",
                "*help usage:",
                "*help - show command summary",
                "*help <command> - show detailed variants",
                "*help all - show detailed variants for all commands",
                "Examples: *help session, *help configure, *help prompt"),
            "all" => string.Join(
                "\n\n",
                BuildStarHelpText("status"),
                BuildStarHelpText("cancel"),
                BuildStarHelpText("prompt"),
                BuildStarHelpText("permission"),
                BuildStarHelpText("question"),
                BuildStarHelpText("providers"),
                BuildStarHelpText("models"),
                BuildStarHelpText("auth"),
                BuildStarHelpText("session"),
                BuildStarHelpText("project"),
                BuildStarHelpText("configure"),
                BuildStarHelpText("reset")),
            "status" => "*status - Show current provider/model/thinking/session and prompt source state for this IM.",
            "cancel" => "*cancel - Abort the current in-flight AI request for this IM conversation.",
            "prompt" => string.Join(
                "\n",
                "*prompt variants:",
                "*prompt status - Show prompt layer status",
                "*prompt show [effective|builtin|project|notecard] - Preview prompt text",
                "*prompt clear-notecard - Remove active in-world AGENTS.md prompt layer",
                "*prompt reload-project - Re-read project AGENTS.md from disk"),
            "permission" => string.Join(
                "\n",
                "*permission variants:",
                "*permission list - List pending permission requests",
                "*permission allow <permission-id> [remember] - Approve",
                "*permission deny <permission-id> [remember] - Reject"),
            "question" => string.Join(
                "\n",
                "*question variants:",
                "*question list - List pending question requests",
                "*question answer <question-id> <text> - Answer a question",
                "*question reject <question-id> - Reject a question"),
            "providers" => string.Join(
                "\n",
                "*providers variants:",
                "*providers - List all providers from Opencode",
                "*providers configured - List only configured providers"),
            "models" => "*models [provider] - List models, optionally filtered by provider id/name.",
            "auth" => string.Join(
                "\n",
                "*auth variants:",
                "*auth methods [provider] - List provider auth methods",
                "*auth <provider-id> api <api-key> - Save API key",
                "*auth <provider-id> oauth [method-index] - Start OAuth/device flow",
                "*auth <provider-id> oauth-complete [method-index] [code] - Complete OAuth flow"),
            "session" => string.Join(
                "\n",
                "*session variants:",
                "*session list",
                "*session create [title] [--no-select]",
                "*session use|select <session-id>",
                "*session current",
                "*session status",
                "*session details <session-id|current>",
                "*session children <session-id|current>",
                "*session patch-title <session-id|current> <new-title>",
                "*session summarize <session-id|current> [provider/model]",
                "*session abort <session-id|current>",
                "*session delete <session-id|current> [--force]",
                "*session delete --all [--force]"),
            "project" => string.Join(
                "\n",
                "*project variants:",
                "*projects - List all Opencode projects",
                "*project current - Show current Opencode project"),
            "configure" => string.Join(
                "\n",
                "*configure variants:",
                "*configure <provider-name-or-id>",
                "*configure provider <provider-name-or-id>",
                "*configure model <provider/model-id>",
                "*configure thinking <low|medium|high|off>",
                "*configure reset"),
            "reset" => "*reset - Alias for '*configure reset'.",
            _ => $"Unknown help topic '{topic}'. Try *help."
        };

    }

    private string BuildConversationStatusText(string conversationKey)
    {
        var currentSessionId = _opencodeChat?.GetConversationSessionId(conversationKey) ?? "(none)";
        var promptState = BuildPromptStatusText();

        if (!_imConversationConfigs.TryGetValue(conversationKey, out var cfg))
        {
            var startupModel = GetStartupDefaultModelId();
            var startupProvider = GetStartupDefaultProviderId(startupModel);
            return string.Join(
                "\n",
                "This IM conversation is using startup defaults (runtime-overridable).",
                $"provider: {startupProvider ?? "(server default)"}",
                $"model: {startupModel ?? "(server default)"}",
                "thinking: (default)",
                $"sessionId: {currentSessionId}",
                promptState);
        }

        return string.Join(
            "\n",
            "Current IM AI settings:",
            $"provider: {cfg.ProviderId ?? "(default)"}",
            $"model: {cfg.ModelId ?? "(default)"}",
            $"thinking: {cfg.ThinkingLevel ?? "(default)"}",
            $"sessionId: {currentSessionId}",
            promptState);
    }

    private Task HandlePromptCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();

        if (sub is "help" or "-h" or "--help")
        {
            SendImText(client, agentId, from, BuildStarHelpText("prompt"));
            return Task.CompletedTask;
        }

        if (sub == "status")
        {
            var sessionId = _opencodeChat?.GetConversationSessionId(conversationKey) ?? "(none)";
            var lines = new List<string>
            {
                "Prompt status:",
                $"conversation: {conversationKey}",
                $"sessionId: {sessionId}",
                $"handling: {_options.PromptHandlingEnabled}",
                $"builtin source: {_options.PromptBuiltInEnabled}",
                $"project source: {_options.PromptProjectAgentsEnabled}",
                $"project file: {_options.PromptProjectAgentsFile}",
                $"notecard source: {_options.PromptNotecardEnabled}",
                $"notecard handler-only install: {_options.PromptNotecardRequireHandler}",
                $"max chars per source: {_options.PromptMaxChars}",
                BuildPromptStatusText()
            };

            lock (_promptStateLock)
            {
                if (_activeAgentsNotecardInstalledAt.HasValue)
                {
                    lines.Add($"notecard installedAtUtc: {_activeAgentsNotecardInstalledAt.Value:O}");
                }
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return Task.CompletedTask;
        }

        if (sub == "show" || sub == "show-source")
        {
            var target = parts.Length > 1 ? parts[1].ToLowerInvariant() : "effective";
            target = target switch
            {
                "all" => "effective",
                _ => target
            };

            string? promptText = null;
            string promptName;
            switch (target)
            {
                case "effective":
                    promptName = "effective";
                    promptText = BuildLayeredPromptText();
                    break;
                case "builtin":
                    promptName = "builtin";
                    promptText = _options.PromptBuiltInEnabled ? ClampPromptLength(BuiltInBridgePrompt) : null;
                    break;
                case "project":
                    promptName = "project AGENTS.md";
                    promptText = _options.PromptProjectAgentsEnabled ? TryLoadProjectAgentsPromptText() : null;
                    break;
                case "notecard":
                    promptName = "in-world AGENTS.md notecard";
                    lock (_promptStateLock)
                    {
                        promptText = _activeAgentsNotecardPrompt;
                    }

                    break;
                default:
                    SendImText(client, agentId, from, "Usage: *prompt show [effective|builtin|project|notecard]");
                    return Task.CompletedTask;
            }

            SendImText(client, agentId, from, BuildPromptPreviewText(promptName, promptText));
            return Task.CompletedTask;
        }

        if (sub == "clear-notecard")
        {
            if (_options.PromptNotecardRequireHandler && !IsHandlerAvatar(from))
            {
                SendImText(client, agentId, from, "Only the configured handler may clear the AGENTS.md notecard prompt layer.");
                return Task.CompletedTask;
            }

            ClearActiveAgentsNotecardPrompt();
            SendImText(client, agentId, from, "Cleared active in-world AGENTS.md notecard prompt layer.");
            return Task.CompletedTask;
        }

        if (sub == "reload-project")
        {
            InvalidateProjectAgentsPromptCache();
            var path = ResolveProjectAgentsPromptPath();
            var loaded = TryLoadProjectAgentsPromptText();
            if (string.IsNullOrWhiteSpace(path))
            {
                SendImText(client, agentId, from, "Project AGENTS.md file is not found. Check PROMPT_PROJECT_AGENTS_FILE.");
                return Task.CompletedTask;
            }

            SendImText(client, agentId, from, string.IsNullOrWhiteSpace(loaded)
                ? $"Project AGENTS.md exists but no prompt text was loaded from: {path}"
                : $"Reloaded project AGENTS.md from {path} ({loaded.Length} chars)." );
            return Task.CompletedTask;
        }

        SendImText(client, agentId, from, "Usage: *prompt status | *prompt show [effective|builtin|project|notecard] | *prompt clear-notecard | *prompt reload-project");
        return Task.CompletedTask;
    }

    private async Task HandleProvidersCommandAsync(GridClient client, UUID agentId, string from, string arg = "")
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var configuredOnly = arg.Trim().Equals("configured", StringComparison.OrdinalIgnoreCase);
        var configured = await _opencodeChat.ListProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        if (configuredOnly)
        {
            if (configured.Count == 0)
            {
                SendImText(client, agentId, from, "No configured providers were reported by Opencode.");
                return;
            }

            var configuredLines = new List<string> { $"Configured providers ({configured.Count}):" };
            foreach (var provider in configured.Take(30))
            {
                configuredLines.Add($"- {provider.Name} ({provider.Id}) [configured]");
            }

            if (configured.Count > 30)
            {
                configuredLines.Add($"... and {configured.Count - 30} more");
            }

            SendImText(client, agentId, from, string.Join("\n", configuredLines));
            return;
        }

        var available = await _opencodeChat.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        if (available.Count == 0)
        {
            SendImText(client, agentId, from, "No providers reported by Opencode.");
            return;
        }

        var configuredIds = configured
            .Select(p => p.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lines = new List<string> { $"Providers ({available.Count}) [*providers configured for active only]:" };
        foreach (var provider in available.Take(30))
        {
            var status = provider.Connected == true || configuredIds.Contains(provider.Id)
                ? "configured"
                : "not configured";
            lines.Add($"- {provider.Name} ({provider.Id}) [{status}]");
        }

        if (available.Count > 30)
        {
            lines.Add($"... and {available.Count - 30} more");
        }

        SendImText(client, agentId, from, string.Join("\n", lines));
    }

    private async Task HandleCancelCommandAsync(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            SendImText(client, agentId, from, "There is no active Opencode session for this IM yet, so there is nothing to cancel.");
            return;
        }

        var ok = await _opencodeChat.AbortSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        SendImText(client, agentId, from, ok
            ? $"Abort requested for the in-flight session: {sessionId}"
            : $"Abort request sent for session {sessionId}, but Opencode did not return an explicit success flag.");
    }

    private async Task HandlePermissionCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            SendImText(client, agentId, from, "There is no active Opencode session for this IM yet.");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var pending = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                SendImText(client, agentId, from, "No pending permission requests were reported for this session.");
                return;
            }

            var lines = new List<string> { $"Pending permission requests ({pending.Count}):" };
            foreach (var permission in pending.Take(12))
            {
                lines.Add("- " + BuildFriendlyPermissionListLine(permission));
            }

            if (pending.Count > 12)
            {
                lines.Add($"... and {pending.Count - 12} more");
            }

            lines.Add("Use *permission allow <permission-id> [remember] or *permission deny <permission-id> [remember].");
            _latestPendingPermissionByConversation[conversationKey] = pending[0].Id;
            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        var action = parts[0].ToLowerInvariant();
        if (action is not ("allow" or "deny" or "reject"))
        {
            SendImText(client, agentId, from, "Usage: *permission list | *permission allow <permission-id> [remember] | *permission deny <permission-id> [remember]");
            return;
        }

        if (parts.Length < 2)
        {
            SendImText(client, agentId, from, $"Usage: *permission {action} <permission-id> [remember]");
            return;
        }

        var permissionId = NormalizeLooseQuery(parts[1]);
        var remember = parts.Skip(2).Any(p => p.Equals("remember", StringComparison.OrdinalIgnoreCase)
            || p.Equals("always", StringComparison.OrdinalIgnoreCase)
            || p.Equals("--remember", StringComparison.OrdinalIgnoreCase));

        if (!IsCanonicalPermissionRequestId(permissionId))
        {
            SendImText(client, agentId, from,
                $"'{permissionId}' is not a canonical permission request id (expected per...). Run *permission list and use the per... id.");
            return;
        }

        var response = action == "allow" ? "allow" : "reject";
        var ok = await _opencodeChat.RespondToPermissionAsync(sessionId, permissionId, response, remember, CancellationToken.None).ConfigureAwait(false);
        _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, ok
            ? $"Permission response sent: {response} ({permissionId}){(remember ? " [remembered]" : string.Empty)}"
            : $"Permission response request was sent for {permissionId}, but Opencode did not return an explicit success flag.");
    }

    private async Task<bool> TryHandleImplicitPermissionResponseAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string response,
        bool remember)
    {
        if (_opencodeChat == null)
        {
            return false;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!_latestPendingPermissionByConversation.TryGetValue(conversationKey, out var permissionId)
            || string.IsNullOrWhiteSpace(permissionId))
        {
            var pending = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var latest = pending.FirstOrDefault();
            if (latest == null)
            {
                return false;
            }

            permissionId = latest.Id;
            _latestPendingPermissionByConversation[conversationKey] = permissionId;
        }

        if (!IsCanonicalPermissionRequestId(permissionId))
        {
            var pending = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var canonical = pending.FirstOrDefault(p => IsCanonicalPermissionRequestId(p.Id));
            if (canonical != null)
            {
                permissionId = canonical.Id;
                _latestPendingPermissionByConversation[conversationKey] = permissionId;
            }
            else
            {
                SendImText(client, agentId, from,
                    "I can see a permission request, but its canonical id (per...) is not available yet. Try *permission list again in a moment.");
                return true;
            }
        }

        var ok = await _opencodeChat.RespondToPermissionAsync(sessionId, permissionId, response, remember, CancellationToken.None).ConfigureAwait(false);
        if (!ok)
        {
            _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
            SendImText(client, agentId, from, "I sent your approval response, but Opencode did not return an explicit success flag.");
            return true;
        }

        // Confirm the specific permission is no longer pending before claiming progress.
        await Task.Delay(250).ConfigureAwait(false);
        var remaining = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var stillPending = remaining.Any(p => p.Id.Equals(permissionId, StringComparison.OrdinalIgnoreCase));

        if (stillPending)
        {
            _latestPendingPermissionByConversation[conversationKey] = permissionId;
            _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);
            SendImText(client, agentId, from,
                    "I sent your approval, but it still appears pending. I will keep waiting for the current task.");
            return true;
        }

        _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, remember
            ? "Got it - approval sent and remembered."
            : "Got it - approval sent.");
        return true;
    }

    private async Task HandleQuestionCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            SendImText(client, agentId, from, "There is no active Opencode session for this IM yet.");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                SendImText(client, agentId, from, "No pending question requests were reported for this session.");
                return;
            }

            var lines = new List<string> { $"Pending question requests ({pending.Count}):" };
            foreach (var question in pending.Take(8))
            {
                var options = question.Options.Count == 0 ? string.Empty : $" options: {string.Join(", ", question.Options)}";
                lines.Add($"- {question.Header} ({question.Id}): {question.Question}{options}");
            }

            if (pending.Count > 8)
            {
                lines.Add($"... and {pending.Count - 8} more");
            }

            lines.Add("Use *question answer <question-id> <text> or *question reject <question-id>.");
            _latestPendingQuestionByConversation[conversationKey] = pending[0].Id;
            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        var action = parts[0].ToLowerInvariant();
        if (action == "reject" || action == "deny")
        {
            if (parts.Length < 2)
            {
                SendImText(client, agentId, from, "Usage: *question reject <question-id>");
                return;
            }

            var questionId = NormalizeLooseQuery(parts[1]);
            var ok = await _opencodeChat.RejectQuestionAsync(sessionId, questionId, CancellationToken.None).ConfigureAwait(false);
            _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
            SendImText(client, agentId, from, ok
                ? $"Question rejected: {questionId}"
                : $"Question reject request was sent for {questionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        if (action != "answer" && action != "reply")
        {
            SendImText(client, agentId, from, "Usage: *question list | *question answer <question-id> <text> | *question reject <question-id>");
            return;
        }

        if (parts.Length < 3)
        {
            SendImText(client, agentId, from, "Usage: *question answer <question-id> <text>");
            return;
        }

        var selectedQuestionId = NormalizeLooseQuery(parts[1]);
        var answerText = arg[(arg.IndexOf(parts[1], StringComparison.Ordinal) + parts[1].Length)..].Trim();
        if (string.IsNullOrWhiteSpace(answerText))
        {
            SendImText(client, agentId, from, "Usage: *question answer <question-id> <text>");
            return;
        }

        var answered = await _opencodeChat.ReplyToQuestionAsync(
            sessionId,
            selectedQuestionId,
            new[] { answerText },
            CancellationToken.None).ConfigureAwait(false);
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, answered
            ? $"Question answered: {selectedQuestionId}"
            : $"Question answer request was sent for {selectedQuestionId}, but Opencode did not return an explicit success flag.");
    }

    private async Task<bool> TryHandleImplicitQuestionResponseAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string answer)
    {
        if (_opencodeChat == null)
        {
            return false;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!_latestPendingQuestionByConversation.TryGetValue(conversationKey, out var questionId)
            || string.IsNullOrWhiteSpace(questionId))
        {
            var pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var latest = pending.FirstOrDefault();
            if (latest == null)
            {
                return false;
            }

            questionId = latest.Id;
            _latestPendingQuestionByConversation[conversationKey] = questionId;
        }

        var ok = await _opencodeChat.ReplyToQuestionAsync(sessionId, questionId, new[] { answer }, CancellationToken.None).ConfigureAwait(false);
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        SendImText(client, agentId, from, ok
            ? "Got it - answered your pending question."
            : "I sent your answer, but Opencode did not return an explicit success flag.");
        return true;
    }

    private async Task<bool> TryHandlePendingQuestionBeforeRoutingAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string text)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(text) || text.StartsWith('*'))
        {
            return false;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        // TEMP(event-first migration): this pre-routing poll can be deleted after session-correlated
        // question events are consumed directly from /event and mapped to conversationKey.
        var pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return false;
        }

        var question = pending[0];
        _latestPendingQuestionByConversation[conversationKey] = question.Id;

        if (TryResolveQuestionAnswer(question, text, out var resolvedAnswer))
        {
            var ok = await _opencodeChat.ReplyToQuestionAsync(sessionId, question.Id, new[] { resolvedAnswer }, CancellationToken.None).ConfigureAwait(false);
            _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
            SendImText(client, agentId, from, ok
                ? $"Got it - answered your pending question with: {resolvedAnswer}"
                : "I tried to answer your pending question, but Opencode did not return an explicit success flag.");
            return true;
        }

        SendImText(client, agentId, from, BuildFriendlyQuestionPrompt(question));
        return true;
    }

    private static bool TryResolveQuestionAnswer(OpencodePendingQuestion question, string text, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim();
        var normalized = raw.ToLowerInvariant();
        var options = question.Options ?? Array.Empty<string>();

        if (options.Count > 0)
        {
            if (int.TryParse(normalized, out var optionIndex)
                && optionIndex >= 1
                && optionIndex <= options.Count)
            {
                answer = options[optionIndex - 1];
                return true;
            }

            var exact = options.FirstOrDefault(o => o.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                answer = exact;
                return true;
            }

            if (normalized is "yes" or "y")
            {
                var yesOption = options.FirstOrDefault(o => o.Contains("yes", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(yesOption))
                {
                    answer = yesOption;
                    return true;
                }
            }

            if (normalized is "no" or "n")
            {
                var noOption = options.FirstOrDefault(o => o.Contains("no", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(noOption))
                {
                    answer = noOption;
                    return true;
                }
            }
        }

        if (question.AllowsCustom != false)
        {
            answer = raw;
            return true;
        }

        return false;
    }

    private static string BuildFriendlyQuestionPrompt(OpencodePendingQuestion question)
    {
        var header = string.IsNullOrWhiteSpace(question.Header) ? "Question" : question.Header.Trim();
        var lines = new List<string>
        {
            "I need your input before I can continue:",
            header,
            question.Question
        };

        if (question.Options.Count > 0)
        {
            for (var i = 0; i < question.Options.Count; i++)
            {
                lines.Add($"{i + 1}) {question.Options[i]}");
            }
        }

        lines.Add(question.AllowsCustom == false
            ? "Reply with one of the options above."
            : "Reply in plain text (or option number).");
        return string.Join("\n", lines);
    }

    private static string BuildFriendlyPermissionPrompt(OpencodePendingPermission permission)
    {
        var title = permission.Title?.Trim() ?? string.Empty;
        var primaryText = GetPermissionPrimaryText(permission, out var titleLooksLikeId);

        var lines = new List<string>
        {
            "I need your approval before I can continue:",
            primaryText,
            "Reply with yes/no to continue."
        };

        if (!string.IsNullOrWhiteSpace(title)
            && !titleLooksLikeId
            && !title.Equals(primaryText, StringComparison.OrdinalIgnoreCase))
        {
            lines.Insert(2, $"Request: {title}");
        }

        return string.Join("\n", lines);
    }

    private static string BuildFriendlyPermissionListLine(OpencodePendingPermission permission)
    {
        var requestId = permission.Id?.Trim() ?? string.Empty;
        var title = permission.Title?.Trim() ?? string.Empty;
        var primaryText = GetPermissionPrimaryText(permission, out var titleLooksLikeId);
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(title)
            && !titleLooksLikeId
            && !title.Equals(primaryText, StringComparison.OrdinalIgnoreCase))
        {
            details.Add($"request: {title}");
        }

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            details.Add(IsCanonicalPermissionRequestId(requestId)
                ? $"id: {requestId}"
                : $"request: {requestId}");
        }

        return details.Count == 0
            ? primaryText
            : primaryText + " (" + string.Join(", ", details) + ")";
    }

    private static string GetPermissionPrimaryText(OpencodePendingPermission permission, out bool titleLooksLikeId)
    {
        var requestId = permission.Id?.Trim() ?? string.Empty;
        var title = permission.Title?.Trim() ?? string.Empty;
        var description = permission.Description?.Trim() ?? string.Empty;
        titleLooksLikeId = !string.IsNullOrWhiteSpace(title)
            && (title.Equals(requestId, StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("per", StringComparison.OrdinalIgnoreCase)
                || title.StartsWith("que", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        if (!string.IsNullOrWhiteSpace(title) && !titleLooksLikeId)
        {
            return title;
        }

        return "This action requires your approval.";
    }

    private static bool IsCanonicalPermissionRequestId(string? permissionId)
        => !string.IsNullOrWhiteSpace(permissionId)
            && permissionId.Trim().StartsWith("per", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<OpencodePendingPermission>> GetPendingPermissionsEventFirstAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<OpencodePendingPermission>();
        }

        if (_opencodeChat.TryGetPendingPermissionsFromEvents(sessionId, out var fromEvents)
            && fromEvents.Count > 0)
        {
            return fromEvents;
        }

        return await _opencodeChat.ListPendingPermissionsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<OpencodePendingQuestion>> GetPendingQuestionsEventFirstAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<OpencodePendingQuestion>();
        }

        if (_opencodeChat.TryGetPendingQuestionsFromEvents(sessionId, out var fromEvents)
            && fromEvents.Count > 0)
        {
            return fromEvents;
        }

        return await _opencodeChat.ListPendingQuestionsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyPendingQuestionIfAppearsAsync(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (_opencodeChat == null)
        {
            return;
        }

        // TEMP(event-first migration): delete this method once event stream routing replaces delayed
        // polling of /question. This exists only as a migration fallback.
        // Keep this short to avoid stale prompts, but long enough for async question.asked emission.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(500).ConfigureAwait(false);

            var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            IReadOnlyList<OpencodePendingPermission> pendingPermissions;
            try
            {
                pendingPermissions = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var permission = pendingPermissions.FirstOrDefault();
            if (permission != null && !string.IsNullOrWhiteSpace(permission.Id))
            {
                _latestPendingPermissionByConversation[conversationKey] = permission.Id;
                if (_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                    && announcedPermissionId.Equals(permission.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _announcedPendingPermissionByConversation[conversationKey] = permission.Id;
                SendImText(client, agentId, from, BuildFriendlyPermissionPrompt(permission));
                return;
            }

            IReadOnlyList<OpencodePendingQuestion> pending;
            try
            {
                pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var question = pending.FirstOrDefault();
            if (question == null || string.IsNullOrWhiteSpace(question.Id))
            {
                continue;
            }

            _latestPendingQuestionByConversation[conversationKey] = question.Id;
            if (_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedId)
                && announcedId.Equals(question.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _announcedPendingQuestionByConversation[conversationKey] = question.Id;
            var prompt = BuildFriendlyQuestionPrompt(question);
            SendImText(client, agentId, from, prompt);
            return;
        }
    }

    private async Task NotifyPendingQuestionDuringInFlightRequestAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        CancellationToken cancellationToken)
    {
        if (_opencodeChat == null)
        {
            return;
        }

        // TEMP(event-first migration): delete this method once in-flight question/permission events
        // are forwarded directly to IM from the stream observer.
        // Keep watching until the in-flight request ends (token is canceled by caller).
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            IReadOnlyList<OpencodePendingPermission> pendingPermissions;
            try
            {
                pendingPermissions = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var permission = pendingPermissions.FirstOrDefault();
            if (permission != null && !string.IsNullOrWhiteSpace(permission.Id))
            {
                _latestPendingPermissionByConversation[conversationKey] = permission.Id;
                if (_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                    && announcedPermissionId.Equals(permission.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _announcedPendingPermissionByConversation[conversationKey] = permission.Id;
                SendImText(client, agentId, from, BuildFriendlyPermissionPrompt(permission));
                continue;
            }

            IReadOnlyList<OpencodePendingQuestion> pending;
            try
            {
                pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var question = pending.FirstOrDefault();
            if (question == null || string.IsNullOrWhiteSpace(question.Id))
            {
                continue;
            }

            _latestPendingQuestionByConversation[conversationKey] = question.Id;
            if (_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedId)
                && announcedId.Equals(question.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _announcedPendingQuestionByConversation[conversationKey] = question.Id;
            SendImText(client, agentId, from, BuildFriendlyQuestionPrompt(question));
            continue;
        }
    }

    private async Task HandleModelsCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        string? providerFilter = null;
        if (!string.IsNullOrWhiteSpace(arg))
        {
            providerFilter = NormalizeLooseQuery(arg);
        }
        else if (_imConversationConfigs.TryGetValue(conversationKey, out var cfg) && !string.IsNullOrWhiteSpace(cfg.ProviderId))
        {
            providerFilter = cfg.ProviderId;
        }

        var models = await _opencodeChat.ListModelsAsync(providerFilter, CancellationToken.None).ConfigureAwait(false);
        if (models.Count == 0)
        {
            SendImText(client, agentId, from, providerFilter == null
                ? "No models reported by Opencode."
                : $"No models found for provider '{providerFilter}'.");
            return;
        }

        var lines = new List<string>
        {
            providerFilter == null ? $"Models ({models.Count}):" : $"Models for '{providerFilter}' ({models.Count}):"
        };

        foreach (var model in models.Take(40))
        {
            var provider = string.IsNullOrWhiteSpace(model.Provider) ? "n/a" : model.Provider;
            lines.Add($"- {model.Name} ({model.Id}) [provider: {provider}]");
        }

        if (models.Count > 40)
        {
            lines.Add($"... and {models.Count - 40} more");
        }

        SendImText(client, agentId, from, string.Join("\n", lines));
    }

    private async Task HandleConfigureCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *configure <provider|model|thinking|reset> ... (try *help)");
            return;
        }

        var config = _imConversationConfigs.GetOrAdd(conversationKey, _ => new ImConversationConfig());
        var normalizedArg = arg.Trim();

        if (normalizedArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            _imConversationConfigs.TryRemove(conversationKey, out _);
            _opencodeChat.ResetConversation(conversationKey);
            SendImText(client, agentId, from, "Conversation AI settings reset for this IM.");
            return;
        }

        if (normalizedArg.StartsWith("thinking ", StringComparison.OrdinalIgnoreCase))
        {
            var requested = normalizedArg[9..].Trim().ToLowerInvariant();
            config.ThinkingLevel = requested switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "off" or "default" => null,
                _ => throw new InvalidOperationException("thinking must be one of: low, medium, high, off")
            };

            SendImText(client, agentId, from, $"Thinking level set to: {config.ThinkingLevel ?? "(default)"}");
            return;
        }

        if (normalizedArg.StartsWith("model ", StringComparison.OrdinalIgnoreCase))
        {
            var requestedModel = normalizedArg[6..].Trim();
            if (string.IsNullOrWhiteSpace(requestedModel))
            {
                throw new InvalidOperationException("model id is required, e.g. *configure model github-copilot/gpt-4.1");
            }

            var resolvedModelId = await ResolvePinnedModelIdAsync(requestedModel, CancellationToken.None).ConfigureAwait(false);
            config.ModelId = resolvedModelId;
            var slash = resolvedModelId.IndexOf('/');
            if (slash > 0)
            {
                config.ProviderId = resolvedModelId[..slash];
            }

            _opencodeChat.ResetConversation(conversationKey);
            SendImText(client, agentId, from, $"Model pinned for this IM: {config.ModelId}");
            return;
        }

        var providerLookup = normalizedArg;
        if (providerLookup.StartsWith("provider ", StringComparison.OrdinalIgnoreCase))
        {
            providerLookup = providerLookup[9..].Trim();
        }

        providerLookup = NormalizeLooseQuery(providerLookup);

        if (providerLookup.Contains('/'))
        {
            var resolvedModelId = await ResolvePinnedModelIdAsync(providerLookup, CancellationToken.None).ConfigureAwait(false);
            config.ModelId = resolvedModelId;
            var slash = resolvedModelId.IndexOf('/');
            if (slash > 0)
            {
                config.ProviderId = resolvedModelId[..slash];
            }

            _opencodeChat.ResetConversation(conversationKey);
            SendImText(client, agentId, from, $"Model pinned for this IM: {config.ModelId}");
            return;
        }

        var providers = await _opencodeChat.ListProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        var matchedProvider = FindProviderByNameOrId(providers, providerLookup);
        if (matchedProvider == null)
        {
            var available = await _opencodeChat.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
            var availableMatch = FindProviderByNameOrId(available, providerLookup);
            if (availableMatch != null)
            {
                SendImText(client, agentId, from, $"Provider '{availableMatch.Name}' exists but is not configured. Authorize it first with *auth (try *auth methods {availableMatch.Id}).");
                return;
            }

            SendImText(client, agentId, from, $"Provider '{providerLookup}' not found. Try *providers.");
            return;
        }

        config.ProviderId = matchedProvider.Id;
        config.ProviderName = matchedProvider.Name;

        var providerModels = await _opencodeChat.ListModelsAsync(matchedProvider.Id, CancellationToken.None).ConfigureAwait(false);
        var selectedModel = providerModels
            .FirstOrDefault(m => m.Id.EndsWith("-free", StringComparison.OrdinalIgnoreCase))
            ?? providerModels.FirstOrDefault();

        config.ModelId = selectedModel == null
            ? null
            : BuildCanonicalModelId(selectedModel.Id, selectedModel.Provider, matchedProvider.Id);
        _opencodeChat.ResetConversation(conversationKey);

        if (selectedModel == null)
        {
            SendImText(client, agentId, from, $"Provider set to {matchedProvider.Name} ({matchedProvider.Id}), but no models were returned.");
            return;
        }

        SendImText(client, agentId, from, $"Configured provider {matchedProvider.Name} ({matchedProvider.Id}) with model {selectedModel.Id} for this IM.");
    }

    private async Task HandleAuthCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *auth methods [provider] | *auth <provider-id> api <api-key> | *auth <provider-id> oauth [method-index] | *auth <provider-id> oauth-complete [method-index] [code]");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            SendImText(client, agentId, from, "Usage: *auth methods [provider] | *auth <provider-id> api <api-key> | *auth <provider-id> oauth [method-index] | *auth <provider-id> oauth-complete [method-index] [code]");
            return;
        }

        if (parts[0].Equals("methods", StringComparison.OrdinalIgnoreCase))
        {
            var filter = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : null;
            await HandleAuthMethodsCommandAsync(client, agentId, from, filter).ConfigureAwait(false);
            return;
        }

        if (parts.Length < 2)
        {
            SendImText(client, agentId, from, "Usage: *auth <provider-id> api <api-key> | *auth <provider-id> oauth [method-index] | *auth <provider-id> oauth-complete [method-index] [code]");
            return;
        }

        var providerQuery = NormalizeLooseQuery(parts[0]);
        var verb = parts[1].ToLowerInvariant();
        var provider = await ResolveProviderForAuthAsync(providerQuery).ConfigureAwait(false);
        if (provider == null)
        {
            SendImText(client, agentId, from, $"Provider '{providerQuery}' was not found. Try *providers.");
            return;
        }

        if (verb == "api")
        {
            if (parts.Length < 3)
            {
                SendImText(client, agentId, from, "Usage: *auth <provider-id> api <api-key>");
                return;
            }

            var apiKey = arg[(arg.IndexOf(" api ", StringComparison.OrdinalIgnoreCase) + 5)..].Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SendImText(client, agentId, from, "API key is required.");
                return;
            }

            await _opencodeChat.SetProviderApiKeyAsync(provider.Id, apiKey, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, $"Stored API key for provider {provider.Name} ({provider.Id}). Run *providers configured then *models {provider.Id}.");
            return;
        }

        if (verb == "oauth")
        {
            var methodIndex = ParseOptionalMethodIndex(parts, 2);
            var started = await _opencodeChat.StartProviderOAuthAsync(provider.Id, methodIndex, null, CancellationToken.None).ConfigureAwait(false);
            var instructions = string.IsNullOrWhiteSpace(started.Instructions)
                ? "Open the URL and complete login."
                : started.Instructions;
            var mode = string.IsNullOrWhiteSpace(started.Method) ? "unknown" : started.Method;
            SendImText(client, agentId, from, $"OAuth started for {provider.Name} ({provider.Id}) [method {methodIndex}, mode {mode}].\nURL: {started.Url}\n{instructions}\nThen run: *auth {provider.Id} oauth-complete {methodIndex}");
            return;
        }

        if (verb == "oauth-complete")
        {
            var methodIndex = ParseOptionalMethodIndex(parts, 2);
            string? code = null;
            if (parts.Length > 3)
            {
                code = string.Join(' ', parts.Skip(3));
            }

            var completed = await _opencodeChat.CompleteProviderOAuthAsync(provider.Id, methodIndex, code, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, completed.ProviderConfigured
                ? $"OAuth completed for {provider.Name} ({provider.Id}). Run *providers configured and *models {provider.Id}."
                : completed.Message);
            return;
        }

        SendImText(client, agentId, from, $"Unknown auth mode '{verb}'. Use api, oauth, or oauth-complete.");
    }

    private async Task HandleAuthMethodsCommandAsync(GridClient client, UUID agentId, string from, string? providerFilter)
    {
        var methodsByProvider = await _opencodeChat!.ListProviderAuthMethodsAsync(CancellationToken.None).ConfigureAwait(false);
        if (methodsByProvider.Count == 0)
        {
            SendImText(client, agentId, from, "No provider auth methods were reported by Opencode.");
            return;
        }

        var providers = await _opencodeChat.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        var providerNameById = providers.ToDictionary(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase);

        IEnumerable<KeyValuePair<string, IReadOnlyList<OpencodeProviderAuthMethod>>> selected = methodsByProvider;
        if (!string.IsNullOrWhiteSpace(providerFilter))
        {
            var resolved = await ResolveProviderForAuthAsync(providerFilter).ConfigureAwait(false);
            if (resolved == null)
            {
                SendImText(client, agentId, from, $"Provider '{providerFilter}' was not found. Try *providers.");
                return;
            }

            if (!methodsByProvider.TryGetValue(resolved.Id, out var resolvedMethods))
            {
                SendImText(client, agentId, from, $"No auth methods were reported for provider {resolved.Name} ({resolved.Id}).");
                return;
            }

            selected = new[] { new KeyValuePair<string, IReadOnlyList<OpencodeProviderAuthMethod>>(resolved.Id, resolvedMethods) };
        }

        var lines = new List<string> { "Provider auth methods:" };
        foreach (var entry in selected.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Take(20))
        {
            var providerName = providerNameById.TryGetValue(entry.Key, out var name) ? name : entry.Key;
            lines.Add($"- {providerName} ({entry.Key})");
            foreach (var method in entry.Value.Take(8))
            {
                lines.Add($"  [{method.MethodIndex}] {method.Type}: {method.Label}");
            }
        }

        SendImText(client, agentId, from, string.Join("\n", lines));
    }

    private async Task HandleSessionCommandAsync(GridClient client, UUID agentId, string from, string conversationKey, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        if (string.IsNullOrWhiteSpace(arg))
        {
            SendImText(client, agentId, from, "Usage: *session list | *session create [title] [--no-select] | *session use <session-id> | *session status | *session current | *session details <session-id|current> | *session children <session-id|current> | *session patch-title <session-id|current> <new-title> | *session delete <session-id|current> [--force] | *session delete --all [--force] | *session summarize <session-id|current> [provider/model] | *session abort <session-id|current>");
            return;
        }

        var parts = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var tail = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        if (verb is "list" or "ls")
        {
            var sessions = await _opencodeChat.ListSessionsAsync(CancellationToken.None).ConfigureAwait(false);
            if (sessions.Count == 0)
            {
                SendImText(client, agentId, from, "No sessions were reported by Opencode.");
                return;
            }

            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            var lines = new List<string> { $"Sessions ({sessions.Count}):" };
            foreach (var session in sessions.Take(40))
            {
                var status = string.IsNullOrWhiteSpace(session.Status) ? "n/a" : session.Status;
                var project = string.IsNullOrWhiteSpace(session.ProjectId) ? "n/a" : session.ProjectId;
                var marker = !string.IsNullOrWhiteSpace(currentSessionId) && session.Id.Equals(currentSessionId, StringComparison.OrdinalIgnoreCase)
                    ? " [current IM session]"
                    : string.Empty;
                lines.Add($"- {session.Title} ({session.Id}) [status: {status}, project: {project}]{marker}");
            }

            if (sessions.Count > 40)
            {
                lines.Add($"... and {sessions.Count - 40} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (verb == "create")
        {
            var createParts = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var titleParts = new List<string>();
            var selectCreated = true;
            foreach (var part in createParts)
            {
                if (part.Equals("--no-select", StringComparison.OrdinalIgnoreCase))
                {
                    selectCreated = false;
                    continue;
                }

                titleParts.Add(part);
            }

            var requestedTitle = titleParts.Count == 0 ? null : string.Join(' ', titleParts);
            var created = await _opencodeChat.CreateSessionAsync(requestedTitle, null, CancellationToken.None).ConfigureAwait(false);
            if (selectCreated)
            {
                _opencodeChat.SetConversationSessionId(conversationKey, created.Id);
            }

            var status = string.IsNullOrWhiteSpace(created.Status) ? "n/a" : created.Status;
            var selectedSuffix = selectCreated ? " [selected for this IM]" : string.Empty;
            SendImText(client, agentId, from, $"Created session: {created.Title} ({created.Id}) [status: {status}]{selectedSuffix}");
            return;
        }

        if (verb is "use" or "select")
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                SendImText(client, agentId, from, "Usage: *session use <session-id>");
                return;
            }

            var requested = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
            var sessionId = NormalizeLooseQuery(requested);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                SendImText(client, agentId, from, "Usage: *session use <session-id>");
                return;
            }

            _ = await _opencodeChat.GetSessionDetailsJsonAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            _opencodeChat.SetConversationSessionId(conversationKey, sessionId);
            SendImText(client, agentId, from, $"Current IM Opencode session set to: {sessionId}");
            return;
        }

        if (verb == "status")
        {
            var statuses = await _opencodeChat.GetSessionStatusAsync(CancellationToken.None).ConfigureAwait(false);
            if (statuses.Count == 0)
            {
                SendImText(client, agentId, from, "No session status data was reported by Opencode.");
                return;
            }

            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            var lines = new List<string> { $"Session status ({statuses.Count}):" };
            foreach (var entry in statuses.Take(60))
            {
                var marker = !string.IsNullOrWhiteSpace(currentSessionId) && entry.Key.Equals(currentSessionId, StringComparison.OrdinalIgnoreCase)
                    ? " [current IM session]"
                    : string.Empty;
                lines.Add($"- {entry.Key}: {entry.Value}{marker}");
            }

            if (statuses.Count > 60)
            {
                lines.Add($"... and {statuses.Count - 60} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (verb == "current")
        {
            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            SendImText(client, agentId, from, string.IsNullOrWhiteSpace(currentSessionId)
                ? "This IM conversation does not have an active Opencode session yet. Send a normal message first."
                : $"Current IM Opencode session: {currentSessionId}");
            return;
        }

        if (verb == "details")
        {
            var sessionId = ResolveSessionSelector(conversationKey, tail, requireExplicit: true);
            var details = await _opencodeChat.GetSessionDetailsJsonAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, $"Session details for {sessionId}:\n{details}");
            return;
        }

        if (verb == "children")
        {
            var sessionId = ResolveSessionSelector(conversationKey, tail, requireExplicit: false);
            var children = await _opencodeChat.GetSessionChildrenAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            if (children.Count == 0)
            {
                SendImText(client, agentId, from, $"Session {sessionId} has no child sessions.");
                return;
            }

            var lines = new List<string> { $"Child sessions for {sessionId} ({children.Count}):" };
            foreach (var child in children.Take(40))
            {
                var status = string.IsNullOrWhiteSpace(child.Status) ? "n/a" : child.Status;
                var project = string.IsNullOrWhiteSpace(child.ProjectId) ? "n/a" : child.ProjectId;
                lines.Add($"- {child.Title} ({child.Id}) [status: {status}, project: {project}]");
            }

            if (children.Count > 40)
            {
                lines.Add($"... and {children.Count - 40} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (verb == "patch-title")
        {
            var titleParts = tail.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (titleParts.Length < 2)
            {
                SendImText(client, agentId, from, "Usage: *session patch-title <session-id|current> <new-title>");
                return;
            }

            var sessionId = ResolveSessionSelector(conversationKey, titleParts[0], requireExplicit: true);
            var newTitle = titleParts[1].Trim();
            if (string.IsNullOrWhiteSpace(newTitle))
            {
                SendImText(client, agentId, from, "Usage: *session patch-title <session-id|current> <new-title>");
                return;
            }

            var updated = await _opencodeChat.UpdateSessionTitleAsync(sessionId, newTitle, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, $"Session renamed: {updated.Title} ({updated.Id})");
            return;
        }

        if (verb is "delete" or "remove")
        {
            var deleteParts = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (deleteParts.Length == 0)
            {
                SendImText(client, agentId, from, "Usage: *session delete <session-id|current> [--force] | *session delete --all [--force]");
                return;
            }

            var normalizedDeleteParts = deleteParts
                .Select(NormalizeLooseQuery)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            var deleteAllRequested = normalizedDeleteParts.Any(p => p.Equals("--all", StringComparison.OrdinalIgnoreCase)
                || p.Equals("all", StringComparison.OrdinalIgnoreCase));
            if (deleteAllRequested)
            {
                var deleteAllConfirmed = normalizedDeleteParts.Any(p => p.Equals("--force", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("confirm", StringComparison.OrdinalIgnoreCase));
                if (!deleteAllConfirmed)
                {
                    SendImText(client, agentId, from, "Deletion is destructive. To confirm deleting all sessions, run: *session delete --all --force");
                    return;
                }

                var sessions = await _opencodeChat.ListSessionsAsync(CancellationToken.None).ConfigureAwait(false);
                if (sessions.Count == 0)
                {
                    SendImText(client, agentId, from, "No sessions were reported by Opencode.");
                    return;
                }

                var mappedCurrentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                var deletedCount = 0;
                var failedCount = 0;
                foreach (var session in sessions)
                {
                    try
                    {
                        _ = await _opencodeChat.DeleteSessionAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
                        deletedCount++;
                    }
                    catch
                    {
                        failedCount++;
                    }
                }

                if (!string.IsNullOrWhiteSpace(mappedCurrentSessionId)
                    && sessions.Any(s => s.Id.Equals(mappedCurrentSessionId, StringComparison.OrdinalIgnoreCase)))
                {
                    _opencodeChat.ResetConversation(conversationKey);
                }

                SendImText(client, agentId, from, failedCount == 0
                    ? $"Deleted {deletedCount} session(s)."
                    : $"Deleted {deletedCount} session(s); {failedCount} failed.");
                return;
            }

            var sessionSelector = deleteParts[0];
            var deleteConfirmed = normalizedDeleteParts.Skip(1).Any(p => p.Equals("--force", StringComparison.OrdinalIgnoreCase)
                || p.Equals("confirm", StringComparison.OrdinalIgnoreCase));
            var sessionId = ResolveSessionSelector(conversationKey, sessionSelector, requireExplicit: false);
            if (!deleteConfirmed)
            {
                SendImText(client, agentId, from, $"Deletion is destructive. To confirm, run: *session delete {sessionSelector} --force");
                return;
            }

            var deleted = await _opencodeChat.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);

            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(currentSessionId)
                && currentSessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            {
                _opencodeChat.ResetConversation(conversationKey);
            }

            SendImText(client, agentId, from, deleted
                ? $"Deleted session {sessionId}."
                : $"Delete request completed for session {sessionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        if (verb is "summarize" or "summarise")
        {
            var partsForSummarize = tail.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var selector = partsForSummarize.Length > 0 ? partsForSummarize[0] : "current";
            var sessionId = ResolveSessionSelector(conversationKey, selector, requireExplicit: false);

            string? providerId = null;
            string? modelId = null;
            if (partsForSummarize.Length > 1)
            {
                var requestedModel = NormalizeLooseQuery(partsForSummarize[1]);
                if (requestedModel.Contains('/'))
                {
                    var slash = requestedModel.IndexOf('/');
                    providerId = requestedModel[..slash];
                    modelId = requestedModel;
                }
            }

            var ok = await _opencodeChat.SummarizeSessionAsync(sessionId, providerId, modelId, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, ok
                ? $"Requested summary for session {sessionId}."
                : $"Summary request completed for session {sessionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        if (verb == "abort")
        {
            var sessionId = ResolveSessionSelector(conversationKey, tail, requireExplicit: false);
            var ok = await _opencodeChat.AbortSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, ok
                ? $"Abort requested for session {sessionId}."
                : $"Abort request completed for session {sessionId}, but Opencode did not return an explicit success flag.");
            return;
        }

        SendImText(client, agentId, from, "Unknown session command. Usage: *session list | *session create [title] [--no-select] | *session use <session-id> | *session status | *session current | *session details <session-id|current> | *session children <session-id|current> | *session patch-title <session-id|current> <new-title> | *session delete <session-id|current> [--force] | *session delete --all [--force] | *session summarize <session-id|current> [provider/model] | *session abort <session-id|current>");
    }

    private async Task HandleProjectCommandAsync(GridClient client, UUID agentId, string from, string arg)
    {
        if (_opencodeChat == null)
        {
            SendImText(client, agentId, from, "AI chat is currently disabled by configuration.");
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(arg) ? "list" : arg.Trim().ToLowerInvariant();
        if (normalized is "list" or "all")
        {
            var projects = await _opencodeChat.ListProjectsAsync(CancellationToken.None).ConfigureAwait(false);
            if (projects.Count == 0)
            {
                SendImText(client, agentId, from, "No projects were reported by Opencode.");
                return;
            }

            var lines = new List<string> { $"Projects ({projects.Count}):" };
            foreach (var project in projects.Take(40))
            {
                var path = string.IsNullOrWhiteSpace(project.Path) ? "n/a" : project.Path;
                var marker = project.Current == true ? " [current]" : string.Empty;
                lines.Add($"- {project.Name} ({project.Id}) [path: {path}]{marker}");
            }

            if (projects.Count > 40)
            {
                lines.Add($"... and {projects.Count - 40} more");
            }

            SendImText(client, agentId, from, string.Join("\n", lines));
            return;
        }

        if (normalized == "current")
        {
            var current = await _opencodeChat.GetCurrentProjectAsync(CancellationToken.None).ConfigureAwait(false);
            if (current == null)
            {
                SendImText(client, agentId, from, "Opencode did not report a current project.");
                return;
            }

            var path = string.IsNullOrWhiteSpace(current.Path) ? "n/a" : current.Path;
            SendImText(client, agentId, from, $"Current project: {current.Name} ({current.Id}) [path: {path}]");
            return;
        }

        SendImText(client, agentId, from, "Usage: *projects | *project current");
    }

    private string ResolveSessionSelector(string conversationKey, string selector, bool requireExplicit)
    {
        var normalized = string.IsNullOrWhiteSpace(selector) ? "current" : NormalizeLooseQuery(selector);
        if (normalized.Equals("current", StringComparison.OrdinalIgnoreCase))
        {
            var current = _opencodeChat?.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(current))
            {
                return current;
            }

            throw new InvalidOperationException("This IM conversation does not have an active Opencode session yet. Send a normal message first, or pass an explicit session id.");
        }

        if (requireExplicit && string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("session id is required (or use 'current').");
        }

        return normalized;
    }

    private async Task<OpencodeProviderSummary?> ResolveProviderForAuthAsync(string query)
    {
        var available = await _opencodeChat!.ListAvailableProvidersAsync(CancellationToken.None).ConfigureAwait(false);
        return FindProviderByNameOrId(available, query);
    }

    private static int ParseOptionalMethodIndex(string[] parts, int index)
    {
        if (parts.Length <= index)
        {
            return 0;
        }

        return int.TryParse(parts[index], out var parsed) && parsed >= 0 ? parsed : 0;
    }

    private static string NormalizeLooseQuery(string value)
    {
        return value.Trim().TrimEnd('.', ',', ';', ':');
    }

    private static bool TryParseSimplePermissionResponse(string text, out string response, out bool remember)
    {
        response = string.Empty;
        remember = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized is "yes" or "y")
        {
            response = "allow";
            return true;
        }

        if (normalized is "yes always" or "always yes" or "yes remember" or "y always")
        {
            response = "allow";
            remember = true;
            return true;
        }

        if (normalized is "no" or "n")
        {
            response = "reject";
            return true;
        }

        if (normalized is "no always" or "always no" or "no remember" or "n always")
        {
            response = "reject";
            remember = true;
            return true;
        }

        return false;
    }

    private static bool TryParseSimpleQuestionResponse(string text, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('*'))
        {
            return false;
        }

        var normalized = trimmed.ToLowerInvariant();
        if (normalized is "yes" or "y" or "no" or "n")
        {
            answer = trimmed;
            return true;
        }

        return false;
    }

    private bool IsHandlerRestricted()
    {
        return !string.IsNullOrWhiteSpace(_handlerFullName);
    }

    private bool IsHandlerAvatar(string? avatarName)
    {
        if (string.IsNullOrWhiteSpace(_handlerFullName))
        {
            return false;
        }

        var normalized = NormalizeAvatarName(avatarName);
        return normalized.Equals(_handlerFullName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHandlerFullName(string? firstName, string? lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            return string.Empty;
        }

        return NormalizeAvatarName($"{firstName} {lastName}");
    }

    private static string NormalizeAvatarName(string? avatarName)
    {
        if (string.IsNullOrWhiteSpace(avatarName))
        {
            return string.Empty;
        }

        return string.Join(' ', avatarName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<string> ResolvePinnedModelIdAsync(string requestedModel, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLooseQuery(requestedModel);
        var slash = normalized.IndexOf('/');
        var providerHint = slash > 0 ? normalized[..slash] : null;

        var models = await _opencodeChat!.ListModelsAsync(providerHint, cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                providerHint == null
                    ? "No models are currently reported by Opencode. Try *models."
                    : $"Provider '{providerHint}' returned no models. Try *providers configured and *models {providerHint}.");
        }

        var matched = models.FirstOrDefault(m => ModelIdMatchesRequested(m, normalized, providerHint));
        if (matched == null)
        {
            var suggested = string.Join(", ",
                models.Take(5).Select(m => BuildCanonicalModelId(m.Id, m.Provider, providerHint)));
            var scopeHint = providerHint == null ? string.Empty : $" for provider '{providerHint}'";
            var modelsHint = providerHint == null ? "*models" : $"*models {providerHint}";
            var suggestionHint = string.IsNullOrWhiteSpace(suggested) ? string.Empty : $" Example IDs: {suggested}";
            throw new InvalidOperationException($"Model '{normalized}' is not available{scopeHint}. Try {modelsHint}.{suggestionHint}");
        }

        return BuildCanonicalModelId(matched.Id, matched.Provider, providerHint);
    }

    private static bool ModelIdMatchesRequested(OpencodeModelSummary model, string requestedModel, string? providerHint)
    {
        var canonical = BuildCanonicalModelId(model.Id, model.Provider, providerHint);
        if (canonical.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)
            || model.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var slash = canonical.IndexOf('/');
        if (slash > 0 && slash < canonical.Length - 1)
        {
            var leaf = canonical[(slash + 1)..];
            return leaf.Equals(requestedModel, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string BuildCanonicalModelId(string modelId, string? providerId, string? providerHint)
    {
        var trimmedModel = modelId.Trim();
        if (trimmedModel.Contains('/'))
        {
            return trimmedModel;
        }

        var provider = !string.IsNullOrWhiteSpace(providerId)
            ? providerId.Trim()
            : providerHint;
        return string.IsNullOrWhiteSpace(provider) ? trimmedModel : $"{provider}/{trimmedModel}";
    }

    private string? GetStartupDefaultModelId()
    {
        var configuredModel = _options.OpencodeInitialModel?.Trim();
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            return null;
        }

        if (configuredModel.Contains('/'))
        {
            return configuredModel;
        }

        var configuredProvider = _options.OpencodeInitialProvider?.Trim();
        return string.IsNullOrWhiteSpace(configuredProvider)
            ? configuredModel
            : $"{configuredProvider}/{configuredModel}";
    }

    private static string? GetStartupDefaultProviderId(string? startupModelId)
    {
        if (string.IsNullOrWhiteSpace(startupModelId))
        {
            return null;
        }

        var slash = startupModelId.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        return startupModelId[..slash];
    }

    private static string SanitizeImLogText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("*auth ", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (trimmed.IndexOf(" api ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "*auth <redacted> api <redacted>";
        }

        if (trimmed.IndexOf(" oauth-complete ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "*auth <redacted> oauth-complete <redacted>";
        }

        return trimmed;
    }

    private static OpencodeProviderSummary? FindProviderByNameOrId(IReadOnlyList<OpencodeProviderSummary> providers, string query)
    {
        var q = query.Trim();
        var exact = providers.FirstOrDefault(p =>
            p.Id.Equals(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        return providers.FirstOrDefault(p =>
            p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private static void SendImText(GridClient client, UUID agentId, string from, string responseText)
    {
        foreach (var chunk in SplitForInstantMessage(responseText, 900))
        {
            client.Self.InstantMessage(agentId, chunk);
            Console.WriteLine($"[im] -> {from}: {chunk}");
        }
    }

    private static bool IsLikelyTypingIndicator(InstantMessage message, string text)
    {
        if (!text.Equals("typing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Some viewers emit IM typing state as a pseudo-message with payload metadata.
        return message.BinaryBucket != null && message.BinaryBucket.Length > 0;
    }

    private bool IsDuplicateImEvent(UUID fromAgentId, string text, DateTime timestamp)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedText = text.Trim();
        var timestampKey = timestamp.Ticks > 0 ? timestamp.Ticks.ToString() : "no-ts";
        var key = $"{fromAgentId}:{timestampKey}:{normalizedText}";
        var duplicateWindow = TimeSpan.FromSeconds(8);

        if (_recentImEvents.TryGetValue(key, out var seenAt) && now - seenAt <= duplicateWindow)
        {
            return true;
        }

        _recentImEvents[key] = now;

        // Opportunistic cleanup to avoid unbounded growth for long-running sessions.
        foreach (var entry in _recentImEvents)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(5))
            {
                _recentImEvents.TryRemove(entry.Key, out _);
            }
        }

        return false;
    }

    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        var client = _client;
        if (client == null || e.SourceID == client.Self.AgentID)
        {
            return;
        }

        // TODO(ai-chat): route local chat to Opencode after conversation UX and anti-spam policies are finalized.
        // TODO(ai-chat): add group chat routing once we define session mapping semantics for groups.
    }

    private static IReadOnlyList<string> SplitForInstantMessage(string message, int maxChunkLength)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new[] { "(No reply text.)" };
        }

        if (message.Length <= maxChunkLength)
        {
            return new[] { message };
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < message.Length)
        {
            var remaining = message.Length - start;
            if (remaining <= maxChunkLength)
            {
                chunks.Add(message[start..]);
                break;
            }

            var span = message.AsSpan(start, maxChunkLength);
            var cut = span.LastIndexOf('\n');
            if (cut <= 0)
            {
                cut = span.LastIndexOf(' ');
            }

            if (cut <= 0)
            {
                cut = maxChunkLength;
            }

            var end = start + cut;
            chunks.Add(message[start..end].Trim());
            start = end;

            while (start < message.Length && char.IsWhiteSpace(message[start]))
            {
                start++;
            }
        }

        return chunks;
    }

    private void OnLoginProgress(object? sender, LoginProgressEventArgs e)
    {
        if (e.Status == LoginStatus.Success)
        {
            Console.WriteLine("[bot] login successful");
        }
        else if (e.Status == LoginStatus.Failed)
        {
            Console.WriteLine($"[bot] login failed: {e.Message}");
        }
    }

    private void OnDisconnected(object? sender, DisconnectedEventArgs e)
    {
        _connected = false;
        Console.WriteLine($"[bot] disconnected: {e.Reason} - {e.Message}");
    }
}

internal sealed record BotStatus(
    bool Connected,
    string Simulator,
    float X,
    float Y,
    float Z,
    string AgentId,
    string LastLoginMessage);

internal sealed record BotToolResult(bool Ok, string Message)
{
    public static BotToolResult OkResult(string message) => new(true, message);
    public static BotToolResult Fail(string message) => new(false, message);
}

internal sealed record EnvironmentToolResult(bool Ok, string Message, string? PayloadJson)
{
    public static EnvironmentToolResult OkResult(string message, string payloadJson) => new(true, message, payloadJson);
    public static EnvironmentToolResult FailResult(string message) => new(false, message, null);
}

internal sealed record PrimCreateResult(bool Ok, string Message, uint LocalId)
{
    public static PrimCreateResult OkResult(uint localId, string message) => new(true, message, localId);
    public static PrimCreateResult FailResult(string message) => new(false, message, 0);
}

internal sealed record PrimFaceTextureInfo(int FaceIndex, string TextureId);

internal sealed record PrimInfo(
    uint LocalId,
    string Uuid,
    uint ParentId,
    string PrimType,
    string PathCurve,
    string ProfileCurve,
    string Material,
    float PositionX,
    float PositionY,
    float PositionZ,
    float ScaleX,
    float ScaleY,
    float ScaleZ,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    string? Name,
    string? Description,
    string? OwnerId,
    string? CreatorId,
    string? DefaultTextureId,
    IReadOnlyList<PrimFaceTextureInfo> FaceTextureOverrides);

internal sealed record PrimInspectResult(bool Ok, string Message, PrimInfo? Prim)
{
    public static PrimInspectResult OkResult(PrimInfo prim) => new(true, "OK", prim);
    public static PrimInspectResult FailResult(string message) => new(false, message, null);
}

internal sealed record PrimSummary(
    uint LocalId,
    string Uuid,
    uint ParentId,
    string? Name,
    string PrimType,
    float PositionX,
    float PositionY,
    float PositionZ,
    float DistanceMeters);

internal sealed record PrimQueryResult(bool Ok, string Message, IReadOnlyList<PrimSummary> Prims)
{
    public static PrimQueryResult OkResult(IReadOnlyList<PrimSummary> prims, string message) => new(true, message, prims);
    public static PrimQueryResult FailResult(string message) => new(false, message, Array.Empty<PrimSummary>());
}

internal sealed class ImConversationConfig
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? ModelId { get; set; }
    public string? ThinkingLevel { get; set; }
}
