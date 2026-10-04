using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public Task<DataToolResult> RegionUuidFromGlobalAsync(uint globalX, uint globalY, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var regionHandle = PositionHelper.RegionHandleFromGlobal(globalX, globalY);
        Utils.LongToUInts(regionHandle, out var regionX, out var regionY);
        var payload = new
        {
            globalX,
            globalY,
            regionHandle = regionHandle.ToString(),
            regionX,
            regionY,
            localX = globalX - regionX,
            localY = globalY - regionY
        };

        return Task.FromResult(
            DataToolResult.OkResult(
                $"Resolved global position ({globalX}, {globalY}) to region handle {regionHandle}.",
                JsonSerializer.Serialize(payload, JsonOptions)));
    }

    public async Task<DataToolResult> MapItemsAsync(
        string regionHandle,
        string? itemType,
        string? layerType,
        CancellationToken cancellationToken)
    {
        if (!ulong.TryParse((regionHandle ?? string.Empty).Trim(), out var requestedRegionHandle))
        {
            return DataToolResult.FailResult("regionHandle must be an unsigned 64-bit integer (use 0 for current region).");
        }

        if (!TryParseGridItemType(itemType, out var parsedItemType))
        {
            return DataToolResult.FailResult($"itemType is invalid. Allowed values: {string.Join(", ", Enum.GetNames<GridItemType>())}.");
        }

        if (!TryParseGridLayerType(layerType, out var parsedLayerType))
        {
            return DataToolResult.FailResult($"layerType is invalid. Allowed values: {string.Join(", ", Enum.GetNames<GridLayerType>())}.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var resolvedRegionHandle = requestedRegionHandle;
            if (resolvedRegionHandle == 0)
            {
                var currentSim = client.Network.CurrentSim;
                if (currentSim == null)
                {
                    return DataToolResult.FailResult("No current simulator is available to resolve regionHandle=0.");
                }

                resolvedRegionHandle = currentSim.Handle;
            }

            var mapItems = await client.Grid
                .MapItemsAsync(resolvedRegionHandle, parsedItemType, parsedLayerType, token)
                .ConfigureAwait(false);

            var payloadRows = mapItems
                .Select(MapItemToPayload)
                .ToList();

            var payload = new
            {
                summary = new
                {
                    requestedRegionHandle = requestedRegionHandle.ToString(),
                    resolvedRegionHandle = resolvedRegionHandle.ToString(),
                    itemType = parsedItemType.ToString(),
                    layerType = parsedLayerType.ToString(),
                    count = payloadRows.Count
                },
                items = payloadRows
            };

            return DataToolResult.OkResult(
                $"Map query returned {payloadRows.Count} item(s).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseGridItemType(string? input, out GridItemType itemType)
    {
        var normalized = string.IsNullOrWhiteSpace(input) ? "AgentLocations" : input.Trim();
        return Enum.TryParse(normalized, ignoreCase: true, out itemType)
            && Enum.IsDefined(itemType);
    }

    private static bool TryParseGridLayerType(string? input, out GridLayerType layerType)
    {
        var normalized = string.IsNullOrWhiteSpace(input) ? "Objects" : input.Trim();
        return Enum.TryParse(normalized, ignoreCase: true, out layerType)
            && Enum.IsDefined(layerType);
    }

    private static object MapItemToPayload(MapItem item)
    {
        var basePayload = new
        {
            runtimeType = item.GetType().Name,
            globalX = item.GlobalX,
            globalY = item.GlobalY,
            localX = item.LocalX,
            localY = item.LocalY,
            regionHandle = item.RegionHandle.ToString()
        };

        return item switch
        {
            MapAgentLocation agent => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.AgentLocations.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle,
                avatarCount = agent.AvatarCount,
                identifier = agent.Identifier
            },
            MapTelehub => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.Telehub.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle
            },
            MapLandForSale land => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.LandForSale.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle,
                id = land.ID.ToString(),
                name = land.Name,
                size = land.Size,
                price = land.Price
            },
            MapAdultLandForSale land => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.AdultLandForSale.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle,
                id = land.ID.ToString(),
                name = land.Name,
                size = land.Size,
                price = land.Price
            },
            MapPGEvent pg => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.PgEvent.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle,
                description = pg.Description,
                flags = pg.Flags.ToString(),
                category = pg.Category.ToString()
            },
            MapMatureEvent mature => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.MatureEvent.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle,
                description = mature.Description,
                flags = mature.Flags.ToString(),
                category = mature.Category.ToString()
            },
            MapAdultEvent adult => new
            {
                basePayload.runtimeType,
                itemType = GridItemType.AdultEvent.ToString(),
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle,
                description = adult.Description,
                flags = adult.Flags.ToString(),
                category = adult.Category.ToString()
            },
            _ => new
            {
                basePayload.runtimeType,
                itemType = "Unknown",
                basePayload.globalX,
                basePayload.globalY,
                basePayload.localX,
                basePayload.localY,
                basePayload.regionHandle
            }
        };
    }
}
