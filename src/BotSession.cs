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

    private GridClient? _client;
    private bool _connected;
    private string _lastLoginMessage = string.Empty;

    public BotSession(AppOptions options)
    {
        _options = options;
        if (_options.OpencodeChatEnabled)
        {
            _opencodeChat = new OpencodeChatClient(_options);
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

        var conversationKey = $"im:{e.IM.FromAgentID}";

        _ = Task.Run(async () =>
        {
            var gate = _imConversationLocks.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0).ConfigureAwait(false))
            {
                Console.WriteLine($"[im] skipping while previous request is still in flight for {from} ({conversationKey}).");
                try
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, "I am still working on your previous request. Please wait a moment and try again.");
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

                var sendOptions = BuildSendOptions(conversationKey);

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

                foreach (var chunk in SplitForInstantMessage(responseText, 900))
                {
                    client.Self.InstantMessage(e.IM.FromAgentID, chunk);
                    Console.WriteLine($"[im] -> {from}: {chunk}");
                }
            }
            catch (OperationCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
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
                Console.WriteLine($"[im] failed to route to opencode after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                _opencodeChat?.ResetConversation(conversationKey);
                _imConversationConfigs.TryRemove(conversationKey, out _);
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
        if (!_imConversationConfigs.TryGetValue(conversationKey, out var cfg))
        {
            return null;
        }

        return new OpencodeSendOptions(cfg.ModelId, cfg.ThinkingLevel);
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
                    SendImText(client, agentId, from, BuildStarHelpText());
                    return true;
                case "status":
                    SendImText(client, agentId, from, BuildConversationStatusText(conversationKey));
                    return true;
                case "reset":
                    _imConversationConfigs.TryRemove(conversationKey, out _);
                    _opencodeChat?.ResetConversation(conversationKey);
                    SendImText(client, agentId, from, "Conversation AI settings reset for this IM. Using server defaults.");
                    return true;
                case "providers":
                    await HandleProvidersCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
                    return true;
                case "models":
                    await HandleModelsCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "configure":
                    await HandleConfigureCommandAsync(client, agentId, from, conversationKey, arg).ConfigureAwait(false);
                    return true;
                case "auth":
                    await HandleAuthCommandAsync(client, agentId, from, arg).ConfigureAwait(false);
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

    private static string BuildStarHelpText()
    {
        return string.Join(
            "\n",
            "Star commands:",
            "*help - Show this help",
            "*status - Show active AI settings for this IM",
            "*providers [configured] - List providers from the live Opencode server",
            "*models [provider] - List models (optionally filtered by provider)",
            "*auth methods [provider] - List provider auth methods",
            "*auth <provider-id> api <api-key> - Save API key for a provider",
            "*auth <provider-id> oauth [method-index] - Start OAuth/device flow",
            "*auth <provider-id> oauth-complete [method-index] [code] - Complete OAuth flow",
            "*configure <provider-name-or-id> - Set provider and auto-pick a model",
            "*configure provider <provider-name-or-id> - Same as above",
            "*configure model <provider/model-id> - Pin an exact model for this IM",
            "*configure thinking <low|medium|high|off> - Set reasoning effort hint",
            "*configure reset - Clear settings for this IM",
            "*reset - Alias for '*configure reset'");
    }

    private string BuildConversationStatusText(string conversationKey)
    {
        if (!_imConversationConfigs.TryGetValue(conversationKey, out var cfg))
        {
            return "This IM conversation is using Opencode server defaults (no overrides).";
        }

        return string.Join(
            "\n",
            "Current IM AI settings:",
            $"provider: {cfg.ProviderId ?? "(default)"}",
            $"model: {cfg.ModelId ?? "(default)"}",
            $"thinking: {cfg.ThinkingLevel ?? "(default)"}");
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

            config.ModelId = requestedModel;
            var slash = requestedModel.IndexOf('/');
            if (slash > 0)
            {
                config.ProviderId = requestedModel[..slash];
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
            config.ModelId = providerLookup;
            var slash = providerLookup.IndexOf('/');
            if (slash > 0)
            {
                config.ProviderId = providerLookup[..slash];
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

        config.ModelId = selectedModel?.Id;
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

            await _opencodeChat.CompleteProviderOAuthAsync(provider.Id, methodIndex, code, CancellationToken.None).ConfigureAwait(false);
            SendImText(client, agentId, from, $"OAuth completed for {provider.Name} ({provider.Id}). Run *providers configured and *models {provider.Id}.");
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
