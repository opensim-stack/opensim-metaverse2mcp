using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<DataToolResult> PrimQueryObjectsAsync(
        int? parcelLocalId,
        string? ownerId,
        bool? scriptedOnly,
        bool? physicalOnly,
        int maxResults,
        bool forceRefreshParcelMap,
        CancellationToken cancellationToken)
    {
        if (parcelLocalId.HasValue && parcelLocalId.Value <= 0)
        {
            return DataToolResult.FailResult("parcelLocalId must be greater than 0 when provided.");
        }

        UUID? ownerFilter = null;
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            if (!UUID.TryParse(ownerId.Trim(), out var ownerUuid) || ownerUuid == UUID.Zero)
            {
                return DataToolResult.FailResult("ownerId must be a valid non-zero UUID when provided.");
            }

            ownerFilter = ownerUuid;
        }

        var limit = Math.Clamp(maxResults, 1, 2000);

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            var needParcelResolution = parcelLocalId.HasValue;
            if (needParcelResolution)
            {
                await EnsureParcelMapAsync(client, sim, forceRefreshParcelMap, token).ConfigureAwait(false);
            }

            var candidates = new List<ParcelObjectDiscoveryInfo>();
            foreach (var prim in sim.ObjectsPrimitives.Values)
            {
                if (prim.IsAttachment)
                {
                    continue;
                }

                var isScripted = (prim.Flags & PrimFlags.Scripted) != 0;
                if (scriptedOnly.HasValue && scriptedOnly.Value != isScripted)
                {
                    continue;
                }

                var isPhysical = (prim.Flags & PrimFlags.Physics) != 0;
                if (physicalOnly.HasValue && physicalOnly.Value != isPhysical)
                {
                    continue;
                }

                var resolvedOwner = prim.Properties?.OwnerID ?? prim.OwnerID;
                if (ownerFilter.HasValue && resolvedOwner != ownerFilter.Value)
                {
                    continue;
                }

                int? resolvedParcelLocalId = null;
                if (needParcelResolution)
                {
                    var byPosition = client.Parcels.GetParcelLocalID(sim, prim.Position);
                    if (byPosition > 0)
                    {
                        resolvedParcelLocalId = byPosition;
                    }

                    if (!resolvedParcelLocalId.HasValue || resolvedParcelLocalId.Value != parcelLocalId!.Value)
                    {
                        continue;
                    }
                }

                candidates.Add(new ParcelObjectDiscoveryInfo(
                    prim.LocalID,
                    prim.ID.ToString(),
                    prim.ParentID,
                    prim.Properties?.Name,
                    resolvedOwner.ToString(),
                    resolvedParcelLocalId,
                    isScripted,
                    isPhysical,
                    prim.Position.X,
                    prim.Position.Y,
                    prim.Position.Z,
                    prim.Type.ToString()));
            }

            var ordered = candidates
                .OrderBy(p => p.ParcelLocalId ?? int.MaxValue)
                .ThenBy(p => p.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.LocalId)
                .Take(limit)
                .ToArray();

            var payload = new
            {
                simulator = sim.Name,
                filters = new
                {
                    parcelLocalId,
                    ownerId = ownerFilter?.ToString(),
                    scriptedOnly,
                    physicalOnly,
                    maxResults = limit,
                    forceRefreshParcelMap
                },
                totalMatchedBeforeLimit = candidates.Count,
                returned = ordered.Length,
                objects = ordered
            };

            var message = ordered.Length == 0
                ? "No objects matched the requested parcel/object filters."
                : $"Found {ordered.Length} matching object(s).";

            return DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record ParcelObjectDiscoveryInfo(
    uint LocalId,
    string Uuid,
    uint ParentId,
    string? Name,
    string OwnerId,
    int? ParcelLocalId,
    bool IsScripted,
    bool IsPhysical,
    float PositionX,
    float PositionY,
    float PositionZ,
    string PrimType);
