using LibreMetaverse;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private readonly ConcurrentDictionary<UUID, DateTimeOffset> _primPropertiesRefreshedAtByObjectId = new();
    
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

            var info = BuildPrimInfo(
                prim,
                includeFaceTextures,
                refreshRequested: false,
                refreshReceived: false,
                refreshDetail: "Using simulator cache only (no explicit property refresh requested).",
                refreshedAtUtc: null);

            return Task.FromResult(PrimInspectResult.OkResult(info));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrimInspectResult> FetchPrimPropertiesAsync(
        uint localId,
        bool includeFaceTextures,
        float waitTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (waitTimeoutSeconds <= 0f || waitTimeoutSeconds > 30f)
        {
            return PrimInspectResult.FailResult("waitTimeoutSeconds must be > 0 and <= 30.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return PrimInspectResult.FailResult("No current simulator available.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return PrimInspectResult.FailResult($"Prim {localId} not found in current simulator cache.");
            }

            var refresh = await RefreshPrimPropertiesAsync(
                client,
                sim,
                prim,
                TimeSpan.FromSeconds(waitTimeoutSeconds),
                token).ConfigureAwait(false);

            var info = BuildPrimInfo(
                prim,
                includeFaceTextures,
                refreshRequested: true,
                refreshReceived: refresh.Received,
                refreshDetail: refresh.Detail,
                refreshedAtUtc: refresh.RefreshedAtUtc);

            var message = refresh.Received
                ? $"Fetched refreshed prim properties for localId={localId}."
                : $"Property refresh timed out for localId={localId}; returned best-effort cached data.";

            return PrimInspectResult.OkResult(info, message);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Received, string Detail, DateTimeOffset? RefreshedAtUtc)> RefreshPrimPropertiesAsync(
        GridClient client,
        Simulator simulator,
        Primitive prim,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var familyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset? refreshedAtUtc = null;

        void MarkRefreshed()
        {
            refreshedAtUtc = DateTimeOffset.UtcNow;
            _primPropertiesRefreshedAtByObjectId[prim.ID] = refreshedAtUtc.Value;
        }

        void OnObjectPropertiesFamily(object? sender, ObjectPropertiesFamilyEventArgs e)
        {
            if (!ReferenceEquals(e.Simulator, simulator) || e.Properties.ObjectID != prim.ID)
            {
                return;
            }

            prim.Properties ??= new Primitive.ObjectProperties();
            prim.Properties.SetFamilyProperties(e.Properties);
            MarkRefreshed();
            familyTcs.TrySetResult(true);
        }

        void OnObjectPropertiesUpdated(object? sender, ObjectPropertiesUpdatedEventArgs e)
        {
            if (!ReferenceEquals(e.Simulator, simulator) || e.Prim.LocalID != prim.LocalID)
            {
                return;
            }

            prim.Properties = e.Properties;
            MarkRefreshed();
            fullTcs.TrySetResult(true);
        }

        client.Objects.ObjectPropertiesFamily += OnObjectPropertiesFamily;
        client.Objects.ObjectPropertiesUpdated += OnObjectPropertiesUpdated;

        try
        {
            client.Objects.RequestObjectPropertiesFamily(simulator, prim.ID);
            client.Objects.SelectObject(simulator, prim.LocalID, automaticDeselect: true);

            var waitTask = Task.Delay(timeout, cancellationToken);
            var bothTask = Task.WhenAll(familyTcs.Task, fullTcs.Task);
            var completed = await Task.WhenAny(bothTask, waitTask).ConfigureAwait(false);

            if (completed == bothTask)
            {
                return (true, "Received both family and full object property updates.", refreshedAtUtc);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var gotFamily = familyTcs.Task.IsCompletedSuccessfully;
            var gotFull = fullTcs.Task.IsCompletedSuccessfully;
            var gotAny = gotFamily || gotFull;
            var detail = gotAny
                ? $"Timed out waiting for full property refresh ({(gotFamily ? "family " : string.Empty)}{(gotFull ? "full" : string.Empty)} update received)."
                : "Timed out waiting for object property refresh updates.";
            return (gotAny, detail.Trim(), refreshedAtUtc);
        }
        finally
        {
            client.Objects.ObjectPropertiesFamily -= OnObjectPropertiesFamily;
            client.Objects.ObjectPropertiesUpdated -= OnObjectPropertiesUpdated;
        }
    }

    private PrimInfo BuildPrimInfo(
        Primitive prim,
        bool includeFaceTextures,
        bool refreshRequested,
        bool refreshReceived,
        string refreshDetail,
        DateTimeOffset? refreshedAtUtc)
    {
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

        var properties = prim.Properties;
        var permissions = properties == null
            ? null
            : new PrimPermissionsInfo(
                (uint)properties.Permissions.BaseMask,
                (uint)properties.Permissions.OwnerMask,
                (uint)properties.Permissions.GroupMask,
                (uint)properties.Permissions.EveryoneMask,
                (uint)properties.Permissions.NextOwnerMask);

        var sale = properties == null
            ? null
            : new PrimSaleInfo(properties.SaleType.ToString(), properties.SalePrice);

        var sitNamePresent = !string.IsNullOrWhiteSpace(properties?.SitName);
        var clickActionSit = prim.ClickAction == ClickAction.Sit;
        var likelySittablePrim = !prim.IsAttachment;
        var isSittable = sitNamePresent || clickActionSit || likelySittablePrim;
        var sitDetection = sitNamePresent
            ? "SitName is populated on object properties."
            : clickActionSit
                ? "ClickAction is Sit."
                : likelySittablePrim
                    ? "Prim is non-attachment; most in-world prims can be sat even when SitName is empty."
                    : "No sit indicators found from cached properties/click action.";

        var sit = new PrimSitInfo(
            properties?.SitName,
            properties?.TouchName,
            isSittable,
            prim.ClickAction.ToString(),
            sitDetection);

        var flexible = prim.Flexible == null
            ? null
            : new PrimFlexibleInfo(
                prim.Flexible.Softness,
                prim.Flexible.Tension,
                prim.Flexible.Drag,
                prim.Flexible.Gravity,
                prim.Flexible.Wind,
                prim.Flexible.Force.X,
                prim.Flexible.Force.Y,
                prim.Flexible.Force.Z);

        var light = prim.Light == null
            ? null
            : new PrimLightInfo(
                prim.Light.Color.R,
                prim.Light.Color.G,
                prim.Light.Color.B,
                prim.Light.Intensity,
                prim.Light.Radius,
                prim.Light.Cutoff,
                prim.Light.Falloff);

        var sculpt = prim.Sculpt == null
            ? null
            : new PrimSculptInfo(
                prim.Sculpt.SculptTexture.ToString(),
                prim.Sculpt.Type.ToString(),
                prim.Sculpt.Type == SculptType.Mesh,
                prim.Sculpt.Invert,
                prim.Sculpt.Mirror,
                prim.ExtendedMeshFlags);

        var shape = new PrimShapeDetail(
            prim.PrimData.PathCurve.ToString(),
            prim.PrimData.ProfileCurve.ToString(),
            prim.PrimData.ProfileHole.ToString(),
            prim.PrimData.Material.ToString(),
            prim.PrimData.PathBegin,
            prim.PrimData.PathEnd,
            prim.PrimData.PathScaleX,
            prim.PrimData.PathScaleY,
            prim.PrimData.PathShearX,
            prim.PrimData.PathShearY,
            prim.PrimData.PathTwist,
            prim.PrimData.PathTwistBegin,
            prim.PrimData.PathTaperX,
            prim.PrimData.PathTaperY,
            prim.PrimData.PathRadiusOffset,
            prim.PrimData.PathSkew,
            prim.PrimData.PathRevolutions,
            prim.PrimData.ProfileBegin,
            prim.PrimData.ProfileEnd,
            prim.PrimData.ProfileHollow);

        var freshestAt = refreshedAtUtc;
        if (!freshestAt.HasValue && _primPropertiesRefreshedAtByObjectId.TryGetValue(prim.ID, out var cachedRefresh))
        {
            freshestAt = cachedRefresh;
        }

        var freshness = new PrimPropertyFreshnessInfo(
            refreshRequested,
            refreshReceived,
            freshestAt?.ToString("O"),
            refreshDetail);

        return new PrimInfo(
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
            properties?.Name,
            properties?.Description,
            properties?.OwnerID.ToString(),
            properties?.CreatorID.ToString(),
            defaultTextureId,
            faceTextures,
            shape,
            permissions,
            sale,
            sit,
            flexible,
            light,
            sculpt,
            freshness);
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

    private async Task<Primitive?> WaitForCreatedPrimAsync(
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

    private async Task<LinksetInspectResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<LinksetInspectResult>> action,
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
            return LinksetInspectResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }
}


internal sealed record PrimCreateResult(bool Ok, string Message, uint LocalId)
{
    public static PrimCreateResult OkResult(uint localId, string message) => new(true, message, localId);
    public static PrimCreateResult FailResult(string message) => new(false, message, 0);
}

internal sealed record PrimFaceTextureInfo(int FaceIndex, string TextureId);

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
    IReadOnlyList<PrimFaceTextureInfo> FaceTextureOverrides,
    PrimShapeDetail Shape,
    PrimPermissionsInfo? Permissions,
    PrimSaleInfo? Sale,
    PrimSitInfo? Sit,
    PrimFlexibleInfo? Flexible,
    PrimLightInfo? Light,
    PrimSculptInfo? Sculpt,
    PrimPropertyFreshnessInfo Freshness);

internal sealed record PrimShapeDetail(
    string PathCurve,
    string ProfileCurve,
    string ProfileHole,
    string Material,
    float PathBegin,
    float PathEnd,
    float PathScaleX,
    float PathScaleY,
    float PathShearX,
    float PathShearY,
    float PathTwist,
    float PathTwistBegin,
    float PathTaperX,
    float PathTaperY,
    float PathRadiusOffset,
    float PathSkew,
    float PathRevolutions,
    float ProfileBegin,
    float ProfileEnd,
    float ProfileHollow);

internal sealed record PrimPermissionsInfo(
    uint BaseMask,
    uint OwnerMask,
    uint GroupMask,
    uint EveryoneMask,
    uint NextOwnerMask);

internal sealed record PrimSaleInfo(string SaleType, int SalePrice);

internal sealed record PrimSitInfo(
    string? SitName,
    string? TouchName,
    bool IsSittable,
    string ClickAction,
    string Detection);

internal sealed record PrimFlexibleInfo(
    int Softness,
    float Tension,
    float Drag,
    float Gravity,
    float Wind,
    float ForceX,
    float ForceY,
    float ForceZ);

internal sealed record PrimLightInfo(
    float Red,
    float Green,
    float Blue,
    float Intensity,
    float Radius,
    float Cutoff,
    float Falloff);

internal sealed record PrimSculptInfo(
    string SculptTextureId,
    string SculptType,
    bool IsMesh,
    bool Invert,
    bool Mirror,
    uint ExtendedMeshFlags);

internal sealed record PrimPropertyFreshnessInfo(
    bool RefreshRequested,
    bool RefreshReceived,
    string? RefreshedAtUtc,
    string Detail);

internal sealed record PrimInspectResult(bool Ok, string Message, PrimInfo? Prim)
{
    public static PrimInspectResult OkResult(PrimInfo prim, string message = "OK") => new(true, message, prim);
    public static PrimInspectResult FailResult(string message) => new(false, message, null);
}

internal sealed record PrimQueryResult(bool Ok, string Message, IReadOnlyList<PrimSummary> Prims)
{
    public static PrimQueryResult OkResult(IReadOnlyList<PrimSummary> prims, string message) => new(true, message, prims);
    public static PrimQueryResult FailResult(string message) => new(false, message, Array.Empty<PrimSummary>());
}

internal sealed record LinksetNodeInfo(
    uint LocalId,
    string Uuid,
    uint ParentId,
    bool IsRoot,
    int Order,
    string? Name,
    string PrimType,
    float PositionX,
    float PositionY,
    float PositionZ,
    float ScaleX,
    float ScaleY,
    float ScaleZ);

internal sealed record LinksetInspectResult(bool Ok, string Message, uint RootLocalId, IReadOnlyList<LinksetNodeInfo> Nodes)
{
    public static LinksetInspectResult OkResult(uint rootLocalId, IReadOnlyList<LinksetNodeInfo> nodes, string message)
        => new(true, message, rootLocalId, nodes);

    public static LinksetInspectResult FailResult(string message)
        => new(false, message, 0, Array.Empty<LinksetNodeInfo>());
}