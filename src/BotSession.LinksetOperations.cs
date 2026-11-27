using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<LinksetInspectResult> InspectLinksetAsync(uint localId, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(LinksetInspectResult.FailResult("No current simulator available."));
            }

            if (!TryGetLinksetMembers(sim, localId, out var rootLocalId, out var members, out var error))
            {
                return Task.FromResult(LinksetInspectResult.FailResult(error));
            }

            var nodes = members
                .OrderBy(p => p.LocalID == rootLocalId ? 0 : 1)
                .ThenBy(p => p.LocalID)
                .Select((prim, index) => new LinksetNodeInfo(
                    prim.LocalID,
                    prim.ID.ToString(),
                    prim.ParentID,
                    prim.LocalID == rootLocalId,
                    index,
                    prim.Properties?.Name,
                    prim.Type.ToString(),
                    prim.Position.X,
                    prim.Position.Y,
                    prim.Position.Z,
                    prim.Scale.X,
                    prim.Scale.Y,
                    prim.Scale.Z))
                .ToList();

            var message = nodes.Count <= 1
                ? $"Prim {localId} is not currently linked; returning single-node linkset view."
                : $"Found linkset rooted at {rootLocalId} with {nodes.Count} prim(s).";

            return Task.FromResult(LinksetInspectResult.OkResult(rootLocalId, nodes, message));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetLinksetRootAsync(uint localId, uint newRootLocalId, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            if (!TryGetLinksetMembers(sim, localId, out var currentRootLocalId, out var members, out var error))
            {
                return BotToolResult.Fail(error);
            }

            if (members.Count <= 1)
            {
                return BotToolResult.Fail("Cannot set root on an unlinked/single prim object.");
            }

            if (!members.Any(p => p.LocalID == newRootLocalId))
            {
                return BotToolResult.Fail($"newRootLocalId {newRootLocalId} is not a member of this linkset.");
            }

            if (newRootLocalId == currentRootLocalId)
            {
                return BotToolResult.OkResult($"Prim {newRootLocalId} is already the root prim.");
            }

            // The simulator makes the first prim of a link request the root.
            var ordered = members
                .Select(p => p.LocalID)
                .Where(id => id != newRootLocalId)
                .ToList();
            ordered.Insert(0, newRootLocalId);

            var relinkError = await RelinkInOrderAsync(client, sim, members, ordered, token).ConfigureAwait(false);
            if (relinkError != null)
            {
                return BotToolResult.Fail(relinkError);
            }

            return BotToolResult.OkResult(
                $"Re-linked to set root {newRootLocalId} (previous root {currentRootLocalId}). Link order={string.Join(",", ordered)} (root is first).");
        }, cancellationToken).ConfigureAwait(false);
    }

    // Unlinks a linkset and links it again in the given order (root first), waiting in
    // between until every prim has become an object of its own. Sent back to back, the
    // link request can reach the simulator before the unlink is through; a prim caught
    // like that came out of the relink with its region position as its offset from the
    // root, about 1500 m away from it. Returns null on success, or what went wrong.
    private static async Task<string?> RelinkInOrderAsync(
        GridClient client,
        Simulator sim,
        IReadOnlyList<Primitive> members,
        List<uint> ordered,
        CancellationToken cancellationToken)
    {
        var allIds = members.Select(p => p.LocalID).ToList();
        client.Objects.DelinkPrims(sim, allIds);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var stillLinked = allIds.Count(id => sim.ObjectsPrimitives.TryGetValue(id, out var prim) && prim != null && prim.ParentID != 0);
            if (stillLinked == 0)
            {
                break;
            }

            if (DateTime.UtcNow > deadline)
            {
                return $"The linkset was unlinked, but {stillLinked} prim(s) did not report as unlinked within 8 s, so it was NOT linked again. "
                    + $"Check the object, then link it with prim_link \"{string.Join(",", ordered)}\".";
            }
        }

        // One more beat: the cache hears about the unlink a little before the simulator is done with it.
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        client.Objects.LinkPrims(sim, ordered);
        return null;
    }

    public async Task<BotToolResult> ReorderLinksetAsync(
        uint localId,
        string orderedLocalIdsCsv,
        uint rootLocalId,
        CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(orderedLocalIdsCsv, out var orderedIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            if (!TryGetLinksetMembers(sim, localId, out var currentRootLocalId, out var members, out var error))
            {
                return BotToolResult.Fail(error);
            }

            if (members.Count <= 1)
            {
                return BotToolResult.Fail("Cannot reorder an unlinked/single prim object.");
            }

            var linksetIds = members.Select(p => p.LocalID).OrderBy(id => id).ToList();
            var requestedIds = orderedIds.OrderBy(id => id).ToList();
            if (!linksetIds.SequenceEqual(requestedIds))
            {
                return BotToolResult.Fail(
                    "orderedLocalIdsCsv must contain exactly all prim local IDs in this linkset (no extras or missing IDs).");
            }

            if (!orderedIds.Contains(rootLocalId))
            {
                return BotToolResult.Fail($"rootLocalId {rootLocalId} must be included in orderedLocalIdsCsv.");
            }

            var ordered = orderedIds.Where(id => id != rootLocalId).ToList();
            ordered.Insert(0, rootLocalId);

            var relinkError = await RelinkInOrderAsync(client, sim, members, ordered, token).ConfigureAwait(false);
            if (relinkError != null)
            {
                return BotToolResult.Fail(relinkError);
            }

            return BotToolResult.OkResult(
                $"Re-linked. Root {rootLocalId} (was {currentRootLocalId}), ordered links={string.Join(",", ordered)} (root is first).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> BulkAdjustLinksAsync(
        string localIdsCsv,
        float? deltaX,
        float? deltaY,
        float? deltaZ,
        float? deltaRollDegrees,
        float? deltaPitchDegrees,
        float? deltaYawDegrees,
        float? scaleMultiplier,
        bool childOnly,
        CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (!deltaX.HasValue
            && !deltaY.HasValue
            && !deltaZ.HasValue
            && !deltaRollDegrees.HasValue
            && !deltaPitchDegrees.HasValue
            && !deltaYawDegrees.HasValue
            && !scaleMultiplier.HasValue)
        {
            return BotToolResult.Fail("At least one adjustment is required (position delta, rotation delta, or scaleMultiplier).");
        }

        if (scaleMultiplier.HasValue && scaleMultiplier.Value <= 0f)
        {
            return BotToolResult.Fail("scaleMultiplier must be greater than 0 when provided.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            var dx = deltaX ?? 0f;
            var dy = deltaY ?? 0f;
            var dz = deltaZ ?? 0f;
            var hasPositionDelta = dx != 0f || dy != 0f || dz != 0f;

            var dr = (deltaRollDegrees ?? 0f) * Utils.DEG_TO_RAD;
            var dp = (deltaPitchDegrees ?? 0f) * Utils.DEG_TO_RAD;
            var dyaw = (deltaYawDegrees ?? 0f) * Utils.DEG_TO_RAD;
            var hasRotationDelta = dr != 0f || dp != 0f || dyaw != 0f;
            var rotationDelta = Quaternion.CreateFromEulers(dr, dp, dyaw);

            var updated = 0;
            var missing = new List<uint>();

            foreach (var localId in localIds)
            {
                if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
                {
                    missing.Add(localId);
                    continue;
                }

                if (hasPositionDelta)
                {
                    var pos = ClampLocalPosition(new Vector3(prim.Position.X + dx, prim.Position.Y + dy, prim.Position.Z + dz));
                    client.Objects.SetPosition(sim, localId, pos, childOnly);
                }

                if (hasRotationDelta)
                {
                    var rotation = prim.Rotation * rotationDelta;
                    client.Objects.SetRotation(sim, localId, rotation, childOnly);
                }

                if (scaleMultiplier.HasValue)
                {
                    var m = scaleMultiplier.Value;
                    var scaled = ClampScale(new Vector3(prim.Scale.X * m, prim.Scale.Y * m, prim.Scale.Z * m));
                    client.Objects.SetScale(sim, localId, scaled, childOnly, uniform: true);
                }

                updated++;
            }

            if (updated == 0)
            {
                return Task.FromResult(BotToolResult.Fail($"None of the requested prims were found in cache: {string.Join(",", missing)}."));
            }

            var missingSuffix = missing.Count == 0 ? string.Empty : $" Missing from cache: {string.Join(",", missing)}.";
            return Task.FromResult(BotToolResult.OkResult($"Applied bulk link adjustments to {updated} prim(s).{missingSuffix}"));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetLinksetMembers(
        Simulator sim,
        uint localId,
        out uint rootLocalId,
        out List<Primitive> members,
        out string error)
    {
        rootLocalId = 0;
        members = new List<Primitive>();
        error = string.Empty;

        if (!sim.ObjectsPrimitives.TryGetValue(localId, out var anchor))
        {
            error = $"Prim {localId} not found in current simulator cache.";
            return false;
        }

        rootLocalId = anchor.ParentID == 0 ? anchor.LocalID : anchor.ParentID;
        var resolvedRootLocalId = rootLocalId;
        members = sim.ObjectsPrimitives.Values
            .Where(p => p.LocalID == resolvedRootLocalId || p.ParentID == resolvedRootLocalId)
            .ToList();

        if (members.Count == 0)
        {
            members.Add(anchor);
            rootLocalId = anchor.LocalID;
        }

        return true;
    }
}
