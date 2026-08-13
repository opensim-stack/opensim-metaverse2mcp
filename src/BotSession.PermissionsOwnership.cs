using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<BotToolResult> SetPrimNextOwnerPermissionsAsync(
        string localIdsCsv,
        bool? allowCopy,
        bool? allowModify,
        bool? allowTransfer,
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

        if (!allowCopy.HasValue && !allowModify.HasValue && !allowTransfer.HasValue)
        {
            return BotToolResult.Fail("At least one next-owner permission value must be provided (allowCopy, allowModify, allowTransfer).");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            if (allowCopy.HasValue)
            {
                client.Objects.SetPermissions(sim, localIds, PermissionWho.NextOwner, PermissionMask.Copy, allowCopy.Value);
            }

            if (allowModify.HasValue)
            {
                client.Objects.SetPermissions(sim, localIds, PermissionWho.NextOwner, PermissionMask.Modify, allowModify.Value);
            }

            if (allowTransfer.HasValue)
            {
                client.Objects.SetPermissions(sim, localIds, PermissionWho.NextOwner, PermissionMask.Transfer, allowTransfer.Value);
            }

            return Task.FromResult(BotToolResult.OkResult(
                $"Updated next-owner permissions for {localIds.Count} prim(s): copy={BoolLabel(allowCopy)}, modify={BoolLabel(allowModify)}, transfer={BoolLabel(allowTransfer)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimSaleInfoAsync(
        string localIdsCsv,
        bool forSale,
        string saleType,
        int price,
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

        if (price < 0)
        {
            return BotToolResult.Fail("price must be >= 0.");
        }

        var resolvedSaleType = SaleType.Not;
        var resolvedPrice = 0;

        if (forSale)
        {
            if (!Enum.TryParse<SaleType>(saleType?.Trim() ?? string.Empty, true, out resolvedSaleType))
            {
                return BotToolResult.Fail("Invalid saleType. Use: Original, Copy, or Contents.");
            }

            if (resolvedSaleType == SaleType.Not)
            {
                return BotToolResult.Fail("saleType cannot be Not when forSale is true.");
            }

            resolvedPrice = price;
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SetSaleInfo(sim, localIds, resolvedSaleType, resolvedPrice);

            var message = forSale
                ? $"Set for-sale info for {localIds.Count} prim(s): type={resolvedSaleType}, price={resolvedPrice}."
                : $"Cleared for-sale status for {localIds.Count} prim(s).";
            return Task.FromResult(BotToolResult.OkResult(message));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetPrimGroupOwnershipAsync(
        string localIdsCsv,
        string groupId,
        bool shareWithGroup,
        bool deedToGroup,
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

        if (!UUID.TryParse(groupId, out var groupUuid) || groupUuid == UUID.Zero)
        {
            return BotToolResult.Fail("groupId must be a valid non-zero UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Objects.SetObjectsGroup(sim, localIds, groupUuid);

            var shared = shareWithGroup || deedToGroup;
            client.Objects.SetPermissions(sim, localIds, PermissionWho.Group, PermissionMask.All, shared);

            if (deedToGroup)
            {
                // Deed requires group-share permissions; enforce that path above.
                client.Objects.DeedObjects(sim, localIds, groupUuid);
            }

            return Task.FromResult(BotToolResult.OkResult(
                $"Updated group assignment for {localIds.Count} prim(s): group={groupUuid}, sharedWithGroup={shared}, deedRequested={deedToGroup}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string BoolLabel(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "unchanged";
    }
}
