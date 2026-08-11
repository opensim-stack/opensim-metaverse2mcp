using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<BotToolResult> PrimReturnToOwnerAsync(string localIdsCsv, CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count == 0)
        {
            return BotToolResult.Fail("At least one local ID is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            foreach (var localId in localIds)
            {
                client.Inventory.RequestDeRezToInventory(
                    localId,
                    DeRezDestination.ReturnToOwner,
                    UUID.Zero,
                    UUID.Random());
            }

            return Task.FromResult(BotToolResult.OkResult(
                $"Return-to-owner request sent for {localIds.Count} object(s): {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> PrimTakeAsync(
        string localIdsCsv,
        bool takeCopy,
        string? destinationFolderId,
        CancellationToken cancellationToken)
    {
        if (!TryParseLocalIdsCsv(localIdsCsv, out var localIds, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (localIds.Count == 0)
        {
            return BotToolResult.Fail("At least one local ID is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var destination = client.Inventory.FindFolderForType(AssetType.Object);
            if (!string.IsNullOrWhiteSpace(destinationFolderId))
            {
                if (!UUID.TryParse(destinationFolderId, out destination))
                {
                    return Task.FromResult(BotToolResult.Fail("destinationFolderId is not a valid UUID."));
                }
            }

            var derezDestination = takeCopy
                ? DeRezDestination.AgentInventoryCopy
                : DeRezDestination.AgentInventoryTake;

            foreach (var localId in localIds)
            {
                client.Inventory.RequestDeRezToInventory(localId, derezDestination, destination, UUID.Random());
            }

            var verb = takeCopy ? "Take Copy" : "Take";
            return Task.FromResult(BotToolResult.OkResult(
                $"{verb} request sent for {localIds.Count} object(s) to folder {destination}: {string.Join(",", localIds)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> PrimRezFromInventoryAsync(
        string itemId,
        float x,
        float y,
        float z,
        float rollDegrees,
        float pitchDegrees,
        float yawDegrees,
        bool selectAfterRez,
        bool waitForObject,
        float? scaleX,
        float? scaleY,
        float? scaleZ,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        var scaleRequested = scaleX.HasValue || scaleY.HasValue || scaleZ.HasValue;
        if (scaleRequested && (!scaleX.HasValue || !scaleY.HasValue || !scaleZ.HasValue))
        {
            return BotToolResult.Fail("When specifying scale overrides, set all of scaleX, scaleY, and scaleZ.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            var position = ClampLocalPosition(new Vector3(x, y, z));
            var rotation = Quaternion.CreateFromEulers(
                rollDegrees * Utils.DEG_TO_RAD,
                pitchDegrees * Utils.DEG_TO_RAD,
                yawDegrees * Utils.DEG_TO_RAD);

            Task<Primitive?>? createdTask = null;
            if (waitForObject || scaleRequested)
            {
                createdTask = WaitForCreatedPrimAsync(client, sim, position, token);
            }

            var queryId = client.Inventory.RequestRezFromInventory(
                sim,
                rotation,
                position,
                item,
                client.Self.ActiveGroup);

            Primitive? created = null;
            if (createdTask != null)
            {
                created = await createdTask.ConfigureAwait(false);
                if (created == null)
                {
                    return BotToolResult.Fail(
                        $"Rez request sent (queryId={queryId}), but object confirmation timed out near {FormatVector(position)}.");
                }
            }

            if (created != null && selectAfterRez)
            {
                client.Objects.SelectObject(sim, created.LocalID, automaticDeselect: false);
            }

            if (created != null && scaleRequested)
            {
                var scale = ClampScale(new Vector3(scaleX!.Value, scaleY!.Value, scaleZ!.Value));
                client.Objects.SetScale(sim, created.LocalID, scale, childOnly: false, uniform: false);
            }

            if (created != null)
            {
                return BotToolResult.OkResult(
                    $"Rezzed inventory item {item.UUID} as localId={created.LocalID} at {FormatVector(position)} (queryId={queryId}).");
            }

            return BotToolResult.OkResult(
                $"Rez request sent for inventory item {item.UUID} at {FormatVector(position)} (queryId={queryId}).");
        }, cancellationToken).ConfigureAwait(false);
    }
}
