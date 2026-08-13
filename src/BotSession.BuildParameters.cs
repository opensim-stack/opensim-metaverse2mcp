using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<BotToolResult> SetPrimBuildParamsAsync(
        uint localId,
        float? pathBegin,
        float? pathEnd,
        float? profileBegin,
        float? profileEnd,
        float? hollow,
        float? taperX,
        float? taperY,
        float? twist,
        float? twistBegin,
        float? shearX,
        float? shearY,
        float? skew,
        float? radiusOffset,
        float? revolutions,
        string? profileHole,
        CancellationToken cancellationToken)
    {
        var hasAnyChange = pathBegin.HasValue
            || pathEnd.HasValue
            || profileBegin.HasValue
            || profileEnd.HasValue
            || hollow.HasValue
            || taperX.HasValue
            || taperY.HasValue
            || twist.HasValue
            || twistBegin.HasValue
            || shearX.HasValue
            || shearY.HasValue
            || skew.HasValue
            || radiusOffset.HasValue
            || revolutions.HasValue
            || !string.IsNullOrWhiteSpace(profileHole);

        if (!hasAnyChange)
        {
            return BotToolResult.Fail("At least one build parameter change is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return Task.FromResult(BotToolResult.Fail($"Prim {localId} not found in current simulator cache."));
            }

            var shape = new Primitive.ConstructionData(prim.PrimData);

            if (pathBegin.HasValue)
            {
                shape.PathBegin = Math.Clamp(pathBegin.Value, 0f, 1f);
            }

            if (pathEnd.HasValue)
            {
                shape.PathEnd = Math.Clamp(pathEnd.Value, 0f, 1f);
            }

            if (profileBegin.HasValue)
            {
                shape.ProfileBegin = Math.Clamp(profileBegin.Value, 0f, 1f);
            }

            if (profileEnd.HasValue)
            {
                shape.ProfileEnd = Math.Clamp(profileEnd.Value, 0f, 1f);
            }

            if (shape.PathBegin > shape.PathEnd)
            {
                return Task.FromResult(BotToolResult.Fail("pathBegin must be <= pathEnd after clamping to [0..1]."));
            }

            if (shape.ProfileBegin > shape.ProfileEnd)
            {
                return Task.FromResult(BotToolResult.Fail("profileBegin must be <= profileEnd after clamping to [0..1]."));
            }

            if (hollow.HasValue)
            {
                shape.ProfileHollow = Math.Clamp(hollow.Value, 0f, 0.95f);
            }

            if (taperX.HasValue)
            {
                shape.PathTaperX = Math.Clamp(taperX.Value, -1f, 1f);
            }

            if (taperY.HasValue)
            {
                shape.PathTaperY = Math.Clamp(taperY.Value, -1f, 1f);
            }

            if (twist.HasValue)
            {
                shape.PathTwist = Math.Clamp(twist.Value, -1f, 1f);
            }

            if (twistBegin.HasValue)
            {
                shape.PathTwistBegin = Math.Clamp(twistBegin.Value, -1f, 1f);
            }

            if (shearX.HasValue)
            {
                shape.PathShearX = Math.Clamp(shearX.Value, -2f, 2f);
            }

            if (shearY.HasValue)
            {
                shape.PathShearY = Math.Clamp(shearY.Value, -2f, 2f);
            }

            if (skew.HasValue)
            {
                shape.PathSkew = Math.Clamp(skew.Value, -1f, 1f);
            }

            if (radiusOffset.HasValue)
            {
                shape.PathRadiusOffset = Math.Clamp(radiusOffset.Value, -1f, 1f);
            }

            if (revolutions.HasValue)
            {
                shape.PathRevolutions = Math.Clamp(revolutions.Value, 1f, 4f);
            }

            if (!string.IsNullOrWhiteSpace(profileHole))
            {
                if (!Enum.TryParse<HoleType>(profileHole.Trim(), true, out var holeType))
                {
                    return Task.FromResult(BotToolResult.Fail("Invalid profileHole value. Use values from HoleType enum (e.g. Same, Circle, Square, Triangle)."));
                }

                shape.ProfileHole = holeType;
            }

            client.Objects.SetShape(sim, localId, shape);
            return Task.FromResult(BotToolResult.OkResult($"Updated build parameters on prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimFlexibleParamsAsync(
        uint localId,
        bool enabled,
        int? softness,
        float? tension,
        float? drag,
        float? gravity,
        float? wind,
        float? forceX,
        float? forceY,
        float? forceZ,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return Task.FromResult(BotToolResult.Fail($"Prim {localId} not found in current simulator cache."));
            }

            if (!enabled)
            {
                client.Objects.SetExtraParamOff(sim, localId, ExtraParamType.Flexible);
                return Task.FromResult(BotToolResult.OkResult($"Disabled flexible parameters on prim {localId}."));
            }

            var flexible = prim.Flexible ?? new Primitive.FlexibleData();

            if (softness.HasValue)
            {
                flexible.Softness = Math.Clamp(softness.Value, 0, 3);
            }

            if (tension.HasValue)
            {
                flexible.Tension = Math.Clamp(tension.Value, 0f, 10f);
            }

            if (drag.HasValue)
            {
                flexible.Drag = Math.Clamp(drag.Value, 0f, 10f);
            }

            if (gravity.HasValue)
            {
                flexible.Gravity = Math.Clamp(gravity.Value, -10f, 10f);
            }

            if (wind.HasValue)
            {
                flexible.Wind = Math.Clamp(wind.Value, 0f, 10f);
            }

            if (forceX.HasValue || forceY.HasValue || forceZ.HasValue)
            {
                var force = flexible.Force;
                flexible.Force = new Vector3(
                    forceX ?? force.X,
                    forceY ?? force.Y,
                    forceZ ?? force.Z);
            }

            client.Objects.SetFlexible(sim, localId, flexible);
            return Task.FromResult(BotToolResult.OkResult($"Updated flexible parameters on prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimLightParamsAsync(
        uint localId,
        bool enabled,
        float? red,
        float? green,
        float? blue,
        float? intensity,
        float? radius,
        float? cutoff,
        float? falloff,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return Task.FromResult(BotToolResult.Fail($"Prim {localId} not found in current simulator cache."));
            }

            if (!enabled)
            {
                client.Objects.SetExtraParamOff(sim, localId, ExtraParamType.Light);
                return Task.FromResult(BotToolResult.OkResult($"Disabled light parameters on prim {localId}."));
            }

            var light = prim.Light ?? new Primitive.LightData
            {
                Color = new Color4(1f, 1f, 1f, 1f),
                Intensity = 1f,
                Radius = 10f,
                Cutoff = 0f,
                Falloff = 0.75f
            };

            if (red.HasValue || green.HasValue || blue.HasValue)
            {
                var r = Math.Clamp(red ?? light.Color.R, 0f, 1f);
                var g = Math.Clamp(green ?? light.Color.G, 0f, 1f);
                var b = Math.Clamp(blue ?? light.Color.B, 0f, 1f);
                light.Color = new Color4(r, g, b, 1f);
            }

            if (intensity.HasValue)
            {
                light.Intensity = Math.Clamp(intensity.Value, 0f, 1f);
            }

            if (radius.HasValue)
            {
                light.Radius = Math.Clamp(radius.Value, 0f, 20f);
            }

            if (cutoff.HasValue)
            {
                light.Cutoff = Math.Clamp(cutoff.Value, 0f, 180f);
            }

            if (falloff.HasValue)
            {
                light.Falloff = Math.Clamp(falloff.Value, 0f, 2f);
            }

            client.Objects.SetLight(sim, localId, light);
            return Task.FromResult(BotToolResult.OkResult($"Updated light parameters on prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimSculptParamsAsync(
        uint localId,
        bool enabled,
        string? textureId,
        string? sculptType,
        bool? invert,
        bool? mirror,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
            {
                return Task.FromResult(BotToolResult.Fail($"Prim {localId} not found in current simulator cache."));
            }

            if (!enabled)
            {
                client.Objects.SetExtraParamOff(sim, localId, ExtraParamType.Sculpt);
                return Task.FromResult(BotToolResult.OkResult($"Disabled sculpt parameters on prim {localId}."));
            }

            var sculpt = prim.Sculpt ?? new Primitive.SculptData();

            if (!string.IsNullOrWhiteSpace(textureId))
            {
                if (!UUID.TryParse(textureId, out var textureUuid))
                {
                    return Task.FromResult(BotToolResult.Fail("textureId must be a valid UUID when provided."));
                }

                sculpt.SculptTexture = textureUuid;
            }
            else if (sculpt.SculptTexture == UUID.Zero)
            {
                return Task.FromResult(BotToolResult.Fail("textureId is required when enabling sculpt for a prim without existing sculpt data."));
            }

            var resolvedType = sculpt.Type;
            if (!string.IsNullOrWhiteSpace(sculptType))
            {
                if (!Enum.TryParse<SculptType>(sculptType.Trim(), true, out var parsedType))
                {
                    return Task.FromResult(BotToolResult.Fail("Invalid sculptType. Use: None, Sphere, Torus, Plane, Cylinder, Mesh."));
                }

                if (parsedType == SculptType.Invert || parsedType == SculptType.Mirror)
                {
                    return Task.FromResult(BotToolResult.Fail("sculptType must be a base sculpt type (None, Sphere, Torus, Plane, Cylinder, Mesh). Set invert/mirror with dedicated flags."));
                }

                resolvedType = parsedType;
            }

            var resolvedInvert = invert ?? sculpt.Invert;
            var resolvedMirror = mirror ?? sculpt.Mirror;

            var sculptTypeByte = (byte)resolvedType;
            if (resolvedInvert)
            {
                sculptTypeByte |= (byte)SculptType.Invert;
            }

            if (resolvedMirror)
            {
                sculptTypeByte |= (byte)SculptType.Mirror;
            }

            var sculptMap = new OSDMap
            {
                ["texture"] = OSD.FromUUID(sculpt.SculptTexture),
                ["type"] = OSD.FromInteger(sculptTypeByte)
            };
            sculpt = Primitive.SculptData.FromOSD(sculptMap);

            client.Objects.SetSculpt(sim, localId, sculpt);
            return Task.FromResult(BotToolResult.OkResult($"Updated sculpt parameters on prim {localId}."));
        }, cancellationToken).ConfigureAwait(false);
    }
}
