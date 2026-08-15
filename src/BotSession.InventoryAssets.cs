using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private readonly object _inventoryOfferLock = new();
    private readonly Queue<InventoryOfferEventInfo> _inventoryOfferHistory = new();
    private readonly List<InventoryOfferPolicyRule> _inventoryOfferPolicyRules = new();
    private int _nextInventoryOfferRuleId;
    private int _nextInventoryOfferEventId;

    private const int MaxInventoryOfferHistory = 200;
    private static readonly HttpClient SharedHttpClient = new();

    public async Task<InventoryOfferPolicyResult> InventoryOfferPolicyRulesSaveAsync(string? filePath, CancellationToken cancellationToken)
    {
        var path = ResolvePolicyFilePath(filePath);
        if (path == null)
        {
            return InventoryOfferPolicyResult.FailResult("No policy file configured. Set --inventory-offer-policy-file or pass filePath.");
        }

        var save = await SaveInventoryOfferPoliciesToFileAsync(path, cancellationToken).ConfigureAwait(false);
        return save.Ok
            ? InventoryOfferPolicyResult.OkResult(InventoryOfferPolicyRulesList().Rules, save.Message)
            : InventoryOfferPolicyResult.FailResult(save.Message);
    }

    public async Task<InventoryOfferPolicyResult> InventoryOfferPolicyRulesLoadAsync(string? filePath, bool replaceExisting, CancellationToken cancellationToken)
    {
        var path = ResolvePolicyFilePath(filePath);
        if (path == null)
        {
            return InventoryOfferPolicyResult.FailResult("No policy file configured. Set --inventory-offer-policy-file or pass filePath.");
        }

        var load = await LoadInventoryOfferPoliciesFromFileAsync(path, replaceExisting, cancellationToken).ConfigureAwait(false);
        return load.Ok
            ? InventoryOfferPolicyResult.OkResult(InventoryOfferPolicyRulesList().Rules, load.Message)
            : InventoryOfferPolicyResult.FailResult(load.Message);
    }

    public async Task<AppearanceStateResult> AppearanceListWornAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var wearables = client.Appearance.GetWearables()
                .Select(w => new WearableInfo(
                    w.ItemID.ToString(),
                    w.AssetID.ToString(),
                    w.WearableType.ToString(),
                    w.AssetType.ToString()))
                .OrderBy(w => w.WearableType, StringComparer.Ordinal)
                .ThenBy(w => w.ItemId, StringComparer.Ordinal)
                .ToList();

            var attachments = (await CollectAttachmentPointMappingsAsync(client, token).ConfigureAwait(false))
                .Select(a =>
                {
                    string? attachedObjectId = null;
                    uint? attachedObjectLocalId = null;
                    if (TryFindAttachedObjectForInventoryItem(client, a.Key, out var objectId, out var localId))
                    {
                        attachedObjectId = objectId.ToString();
                        attachedObjectLocalId = localId;
                    }

                    return new AttachmentInfo(
                        a.Key.ToString(),
                        a.Value.ToString(),
                        attachedObjectId,
                        attachedObjectLocalId);
                })
                .OrderBy(a => a.AttachmentPoint, StringComparer.Ordinal)
                .ThenBy(a => a.ItemId, StringComparer.Ordinal)
                .ToList();

            return AppearanceStateResult.OkResult(wearables, attachments, "Collected currently worn wearables and attachments.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppearanceWearFolderResult> AppearanceWearFolderAsync(string folderId, bool replaceItems, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(folderId, out var folderUuid))
        {
            return AppearanceWearFolderResult.FailResult(replaceItems, "folderId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var entries = await client.Inventory
                .FolderContentsAsync(folderUuid, client.Self.AgentID, true, true, InventorySortOrder.ByName, token)
                .ConfigureAwait(false);

            var store = client.Inventory.Store;
            var resolved = new List<InventoryBase>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is InventoryItem item && item.IsLink() && store != null && store.TryGetValue(item.ResolvedItemID, out var linked))
                {
                    resolved.Add(linked);
                }
                else
                {
                    resolved.Add(entry);
                }
            }

            var resolvedItems = resolved.OfType<InventoryItem>().ToList();
            var categoryResolutions = await BuildOutfitCategoryResolutionsAsync(client, resolvedItems, replaceItems, token).ConfigureAwait(false);
            await client.Appearance.WearOutfitAsync(resolved, replaceItems).ConfigureAwait(false);

            var overlapCount = categoryResolutions.Count(r => r.CurrentlyWornCount > 0);
            var mode = replaceItems ? "replace" : "add";
            return AppearanceWearFolderResult.OkResult(
                replaceItems,
                entries.Count,
                resolvedItems.Count,
                categoryResolutions,
                $"Requested {mode} outfit from folder {folderUuid}: sourceEntries={entries.Count}, wearableCandidates={resolvedItems.Count}, overlappingCategories={overlapCount}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutfitSaveResult> AppearanceSaveCurrentOutfitAsync(
        string folderName,
        string? parentFolderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return OutfitSaveResult.FailResult("folderName is required.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            var root = store?.RootFolder;
            if (store == null || root == null)
            {
                return OutfitSaveResult.FailResult("Inventory store is not initialized.");
            }

            var parentId = root.UUID;
            if (!string.IsNullOrWhiteSpace(parentFolderId))
            {
                if (!UUID.TryParse(parentFolderId, out var parsedParentId))
                {
                    return OutfitSaveResult.FailResult("parentFolderId is not a valid UUID.");
                }

                if (!store.TryGetValue(parsedParentId, out var parentNode) || parentNode is not InventoryFolder)
                {
                    return OutfitSaveResult.FailResult($"Parent folder {parsedParentId} was not found in local inventory store.");
                }

                parentId = parsedParentId;
            }
            else
            {
                var clothingFolder = client.Inventory.FindFolderForType(FolderType.Clothing);
                if (clothingFolder != UUID.Zero)
                {
                    parentId = clothingFolder;
                }
            }

            var destinationFolderId = client.Inventory.CreateFolder(parentId, folderName.Trim(), FolderType.None);
            using var cof = new LibreMetaverse.Appearance.CurrentOutfitFolder(client);
            var currentLinks = await cof.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);

            var linkTargets = new List<InventoryItem>();
            var seen = new HashSet<UUID>();
            foreach (var link in currentLinks)
            {
                var resolved = ResolveLinkedInventoryItem(store, link);
                if (seen.Add(resolved.UUID))
                {
                    linkTargets.Add(resolved);
                }
            }

            var linkedCount = 0;
            var failedCount = 0;
            foreach (var target in linkTargets)
            {
                token.ThrowIfCancellationRequested();

                var createdLink = await client.Inventory.CreateLinkAsync(
                    destinationFolderId,
                    target.UUID,
                    target.Name,
                    target.Description,
                    target.InventoryType,
                    UUID.Random(),
                    token).ConfigureAwait(false);

                if (createdLink == null)
                {
                    failedCount++;
                }
                else
                {
                    linkedCount++;
                }
            }

            return OutfitSaveResult.OkResult(
                destinationFolderId.ToString(),
                linkedCount,
                failedCount,
                $"Saved current outfit links to folder '{folderName.Trim()}' ({destinationFolderId}). Linked={linkedCount}, failed={failedCount}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> AppearanceWearWearableItemAsync(string itemId, bool replaceExistingSlot, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        return await AppearanceWearWearableItemAsync(itemUuid, replaceExistingSlot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WearableDirectControlResult> AppearanceRemoveWearableItemAsync(string itemId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return WearableDirectControlResult.FailResult("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return WearableDirectControlResult.FailResult($"Inventory item {itemUuid} was not found.");
            }

            var resolved = ResolveLinkedInventoryItem(client.Inventory.Store, item);
            if (resolved is not InventoryWearable wearable)
            {
                return WearableDirectControlResult.FailResult(
                    $"Inventory item {resolved.UUID} ('{resolved.Name}') is not a wearable (assetType={resolved.AssetType}, inventoryType={resolved.InventoryType}).");
            }

            using var cof = new LibreMetaverse.Appearance.CurrentOutfitFolder(client);
            await cof.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
            await cof.RemoveFromOutfitAsync(wearable, token).ConfigureAwait(false);

            return WearableDirectControlResult.OkResult(
                wearable.WearableType.ToString(),
                1,
                1,
                new[] { wearable.UUID.ToString() },
                $"Requested remove wearable '{wearable.Name}' ({wearable.UUID}), type={wearable.WearableType} via COF.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WearableDirectControlResult> AppearanceRemoveWearablesByTypeAsync(string wearableType, bool removeAllLayers, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(wearableType) || !Enum.TryParse<WearableType>(wearableType.Trim(), true, out var parsedType))
        {
            return WearableDirectControlResult.FailResult($"wearableType '{wearableType}' is not valid.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            using var cof = new LibreMetaverse.Appearance.CurrentOutfitFolder(client);
            var wornOfType = await cof.GetWornAtAsync(parsedType, token).ConfigureAwait(false);
            if (wornOfType.Count == 0)
            {
                return WearableDirectControlResult.OkResult(parsedType.ToString(), 0, 0, Array.Empty<string>(), $"No currently worn wearables found for type {parsedType}.");
            }

            var removeList = removeAllLayers
                ? wornOfType
                : new List<InventoryItem> { wornOfType[0] };

            await cof.RemoveFromOutfitAsync(removeList, token).ConfigureAwait(false);

            var removedIds = removeList.Select(i => i.UUID.ToString()).ToList();
            var mode = removeAllLayers ? "all" : "single";
            return WearableDirectControlResult.OkResult(
                parsedType.ToString(),
                wornOfType.Count,
                removeList.Count,
                removedIds,
                $"Requested remove {mode} wearable layer(s) for type {parsedType}. wornOfType={wornOfType.Count}, removeRequested={removeList.Count}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentPointMappingResult> AppearanceListAttachmentPointMappingsAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);
            var attachmentsByItemId = await CollectAttachmentPointMappingsAsync(client, token).ConfigureAwait(false);

            var mappings = new List<AttachmentPointMappingInfo>(attachmentsByItemId.Count);
            foreach (var entry in attachmentsByItemId.OrderBy(kv => kv.Value.ToString(), StringComparer.Ordinal).ThenBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
            {
                var item = await ResolveInventoryItemAsync(client, entry.Key, token).ConfigureAwait(false);
                var name = item?.Name ?? string.Empty;
                mappings.Add(new AttachmentPointMappingInfo(entry.Key.ToString(), name, entry.Value.ToString()));
            }

            return AttachmentPointMappingResult.OkResult(mappings, $"Collected {mappings.Count} attachment point mapping(s) from currently worn attachments.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentObjectResolutionResult> AttachmentResolveObjectAsync(string itemId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return AttachmentObjectResolutionResult.FailResult("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return AttachmentObjectResolutionResult.FailResult("No current simulator available.");
            }

            if (!TryFindAttachedObjectForInventoryItem(client, itemUuid, out var attachedObjectId, out var attachedLocalId))
            {
                return AttachmentObjectResolutionResult.FailResult(
                    $"Unable to resolve an attached object for inventory item {itemUuid}. The item may not be worn yet or object updates are still pending.");
            }

            string? attachmentPoint = null;
            if (sim.ObjectsPrimitives.TryGetValue(attachedLocalId, out var prim))
            {
                attachmentPoint = prim.PrimData.AttachmentPoint.ToString();
            }
            else
            {
                var mappings = await CollectAttachmentPointMappingsAsync(client, token).ConfigureAwait(false);
                if (mappings.TryGetValue(itemUuid, out var mappedPoint))
                {
                    attachmentPoint = mappedPoint.ToString();
                }
            }

            return AttachmentObjectResolutionResult.OkResult(
                itemUuid.ToString(),
                attachedObjectId.ToString(),
                attachedLocalId,
                attachmentPoint,
                $"Resolved attachment item {itemUuid} to objectId={attachedObjectId}, objectLocalId={attachedLocalId}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> AppearanceSetAttachmentPointMappingAsync(string itemId, string attachmentPoint, bool replace, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        if (!Enum.TryParse<AttachmentPoint>(attachmentPoint.Trim(), true, out var parsedPoint))
        {
            return BotToolResult.Fail($"attachmentPoint '{attachmentPoint}' is not valid.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            var resolved = ResolveLinkedInventoryItem(client.Inventory.Store, item);
            if (resolved is not InventoryObject attachment)
            {
                return BotToolResult.Fail(
                    $"Inventory item {resolved.UUID} ('{resolved.Name}') is not an attachment/object (assetType={resolved.AssetType}, inventoryType={resolved.InventoryType}).");
            }

            attachment.AttachPoint = parsedPoint;
            client.Appearance.Attach(attachment, parsedPoint, replace);

            token.ThrowIfCancellationRequested();
            return BotToolResult.OkResult(
                $"Attach-point remap requested for '{attachment.Name}' ({attachment.UUID}) to {parsedPoint} (replace={replace}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentTransformResult> AppearanceGetAttachedItemTransformAsync(string itemId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return AttachmentTransformResult.FailResult("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return AttachmentTransformResult.FailResult("No current simulator available.");
            }

            if (!TryFindAttachedObjectForInventoryItem(client, itemUuid, out var attachedObjectId, out var attachedLocalId))
            {
                return AttachmentTransformResult.FailResult($"Unable to find a currently worn attachment object for inventory item {itemUuid}. The attachment may not be worn yet or object updates are still pending.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(attachedLocalId, out var prim))
            {
                return AttachmentTransformResult.FailResult($"Attachment object localId={attachedLocalId} was not found in simulator cache.");
            }

            return BuildAttachmentTransformResult(
                itemUuid,
                attachedObjectId,
                attachedLocalId,
                prim,
                requestedUpdate: false,
                $"Read transform snapshot for attached item {itemUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AttachmentTransformResult> AppearanceSetAttachedItemTransformAsync(
        string itemId,
        float? positionX,
        float? positionY,
        float? positionZ,
        float? scaleX,
        float? scaleY,
        float? scaleZ,
        float? rollDegrees,
        float? pitchDegrees,
        float? yawDegrees,
        bool childOnly,
        bool uniformScale,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return AttachmentTransformResult.FailResult("itemId is not a valid UUID.");
        }

        var hasPosition = positionX.HasValue || positionY.HasValue || positionZ.HasValue;
        var hasScale = scaleX.HasValue || scaleY.HasValue || scaleZ.HasValue;
        var hasRotation = rollDegrees.HasValue || pitchDegrees.HasValue || yawDegrees.HasValue;
        if (!hasPosition && !hasScale && !hasRotation)
        {
            return AttachmentTransformResult.FailResult("At least one transform field is required (position, scale, or rotation).");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return AttachmentTransformResult.FailResult("No current simulator available.");
            }

            if (!TryFindAttachedObjectForInventoryItem(client, itemUuid, out var attachedObjectId, out var attachedLocalId))
            {
                return AttachmentTransformResult.FailResult($"Unable to find a currently worn attachment object for inventory item {itemUuid}. The attachment may not be worn yet or object updates are still pending.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(attachedLocalId, out var prim))
            {
                return AttachmentTransformResult.FailResult($"Attachment object localId={attachedLocalId} was not found in simulator cache.");
            }

            if (hasPosition)
            {
                var targetPosition = new Vector3(
                    positionX ?? prim.Position.X,
                    positionY ?? prim.Position.Y,
                    positionZ ?? prim.Position.Z);
                targetPosition = ClampLocalPosition(targetPosition);
                client.Objects.SetPosition(sim, attachedLocalId, targetPosition, childOnly);
            }

            if (hasScale)
            {
                var targetScale = new Vector3(
                    scaleX ?? prim.Scale.X,
                    scaleY ?? prim.Scale.Y,
                    scaleZ ?? prim.Scale.Z);
                targetScale = ClampScale(targetScale);
                client.Objects.SetScale(sim, attachedLocalId, targetScale, childOnly, uniformScale);
            }

            if (hasRotation)
            {
                prim.Rotation.GetEulerAngles(out var currentRoll, out var currentPitch, out var currentYaw);
                var targetRoll = (rollDegrees ?? (currentRoll * Utils.RAD_TO_DEG)) * Utils.DEG_TO_RAD;
                var targetPitch = (pitchDegrees ?? (currentPitch * Utils.RAD_TO_DEG)) * Utils.DEG_TO_RAD;
                var targetYaw = (yawDegrees ?? (currentYaw * Utils.RAD_TO_DEG)) * Utils.DEG_TO_RAD;
                var targetRotation = Quaternion.CreateFromEulers(targetRoll, targetPitch, targetYaw);
                client.Objects.SetRotation(sim, attachedLocalId, targetRotation, childOnly);
            }

            token.ThrowIfCancellationRequested();
            return BuildAttachmentTransformResult(
                itemUuid,
                attachedObjectId,
                attachedLocalId,
                prim,
                requestedUpdate: true,
                $"Transform update requested for attached item {itemUuid}. Note: simulator applies attachment transform updates asynchronously and may constrain the final result.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<OutfitCategoryResolutionInfo>> BuildOutfitCategoryResolutionsAsync(
        GridClient client,
        IReadOnlyList<InventoryItem> incomingItems,
        bool replaceItems,
        CancellationToken cancellationToken)
    {
        await client.Appearance.RequestAgentWornAsync(cancellationToken).ConfigureAwait(false);

        var incomingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in incomingItems)
        {
            if (item is InventoryWearable wearable)
            {
                IncrementCount(incomingCounts, $"wearable:{wearable.WearableType}");
            }
            else if (item is InventoryObject attachment)
            {
                IncrementCount(incomingCounts, $"attachment:{attachment.AttachPoint}");
            }
        }

        var wornCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var wearable in client.Appearance.GetWearables())
        {
            IncrementCount(wornCounts, $"wearable:{wearable.WearableType}");
        }

        foreach (var attachment in await CollectAttachmentPointMappingsAsync(client, cancellationToken).ConfigureAwait(false))
        {
            IncrementCount(wornCounts, $"attachment:{attachment.Value}");
        }

        var action = replaceItems ? "replace" : "add";
        return incomingCounts
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new OutfitCategoryResolutionInfo(
                kv.Key,
                action,
                kv.Value,
                wornCounts.TryGetValue(kv.Key, out var existingCount) ? existingCount : 0,
                replaceItems
                    ? "replaceItems=true requests replacement when this category already has worn entries."
                    : "replaceItems=false requests additive wear; overlapping categories may still be constrained by simulator rules."))
            .ToList();
    }

    private static void IncrementCount(Dictionary<string, int> map, string key)
    {
        if (map.TryGetValue(key, out var count))
        {
            map[key] = count + 1;
            return;
        }

        map[key] = 1;
    }

    private static async Task<Dictionary<UUID, AttachmentPoint>> CollectAttachmentPointMappingsAsync(
        GridClient client,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<UUID, AttachmentPoint>(client.Appearance.GetAttachmentsByItemId());

        // Simulator object updates are often the most reliable source for what is currently attached.
        var sim = client.Network.CurrentSim;
        if (sim != null)
        {
            foreach (var prim in sim.ObjectsPrimitives.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (prim == null || prim.NameValues == null || !prim.NameValues.Any())
                {
                    continue;
                }

                foreach (var nameValue in prim.NameValues)
                {
                    if (!nameValue.Name.Equals("AttachItemID", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var raw = nameValue.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(raw) && UUID.TryParse(raw, out var attachedItemId) && attachedItemId != UUID.Zero)
                    {
                        merged[attachedItemId] = prim.PrimData.AttachmentPoint;
                    }
                }
            }
        }

        using var cof = new LibreMetaverse.Appearance.CurrentOutfitFolder(client);
        var links = await cof.GetCurrentOutfitLinksAsync(cancellationToken).ConfigureAwait(false);
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resolved = cof.ResolveInventoryLink(link) ?? ResolveLinkedInventoryItem(client.Inventory.Store, link);
            switch (resolved)
            {
                case InventoryAttachment attachment:
                    merged[attachment.ResolvedItemID] = attachment.AttachmentPoint;
                    break;
                case InventoryObject obj:
                    merged[obj.ResolvedItemID] = obj.AttachPoint;
                    break;
            }
        }

        return merged;
    }

    public async Task<BotToolResult> AppearanceAttachItemAsync(string itemId, string? attachmentPoint, bool replace, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            var point = AttachmentPoint.Default;
            if (!string.IsNullOrWhiteSpace(attachmentPoint))
            {
                if (!Enum.TryParse<AttachmentPoint>(attachmentPoint.Trim(), true, out point))
                {
                    return BotToolResult.Fail($"attachmentPoint '{attachmentPoint}' is not valid.");
                }
            }
            else if (item is InventoryAttachment invAttachment)
            {
                point = invAttachment.AttachmentPoint;
            }

            client.Appearance.Attach(item, point, replace);
            return BotToolResult.OkResult($"Attach request sent for item {item.UUID} on point {point} (replace={replace}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> AppearanceDetachItemAsync(string itemId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Appearance.Detach(itemUuid);
            return Task.FromResult(BotToolResult.OkResult($"Detach request sent for item {itemUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> AppearanceRebakeAsync(bool forceRebake, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestSetAppearance(forceRebake).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return BotToolResult.OkResult($"Appearance update requested (forceRebake={forceRebake}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppearanceVisualParamsResult> AppearanceVisualParamsListAsync(
        string? wearable,
        string? nameContains,
        bool editableOnly,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);
            var currentValues = client.Appearance.GetCurrentParamValues();

            var wearableFilter = string.IsNullOrWhiteSpace(wearable) ? null : wearable.Trim();
            var nameFilter = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();

            var paramInfos = VisualParams.Params.Values
                .Where(param =>
                    (wearableFilter == null || string.Equals(param.Wearable, wearableFilter, StringComparison.OrdinalIgnoreCase)) &&
                    (nameFilter == null || param.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) &&
                    (!editableOnly || param.Group == 0))
                .Select(param =>
                {
                    var current = currentValues.TryGetValue(param.ParamID, out var value)
                        ? value
                        : param.DefaultValue;

                    return new AppearanceVisualParamInfo(
                        param.ParamID,
                        param.Name,
                        param.Wearable,
                        param.Group,
                        param.MinValue,
                        param.MaxValue,
                        param.DefaultValue,
                        current,
                        param.Group == 0);
                })
                .OrderBy(info => info.ParamId)
                .ToList();

            return AppearanceVisualParamsResult.OkResult(
                paramInfos,
                $"Collected {paramInfos.Count} visual parameter entries (editableOnly={editableOnly}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppearanceVisualParamSetResult> AppearanceVisualParamSetAsync(
        int? paramId,
        string? paramName,
        string? wearable,
        float value,
        bool clampToRange,
        CancellationToken cancellationToken)
    {
        if (!float.IsFinite(value))
        {
            return AppearanceVisualParamSetResult.FailResult("value must be finite.");
        }

        var resolved = ResolveVisualParam(paramId, paramName, wearable);
        if (!resolved.Ok || resolved.Param is null)
        {
            return AppearanceVisualParamSetResult.FailResult(resolved.Message);
        }

        var selected = resolved.Param.Value;
        if (selected.Group != 0)
        {
            return AppearanceVisualParamSetResult.FailResult(
                $"Visual param {selected.ParamID} ('{selected.Name}') is group {selected.Group} (driven/non-editable). Choose a group-0 driver parameter.");
        }

        var requestedValue = value;
        var appliedValue = value;
        var clamped = false;
        if (appliedValue < selected.MinValue || appliedValue > selected.MaxValue)
        {
            if (!clampToRange)
            {
                return AppearanceVisualParamSetResult.FailResult(
                    $"value {appliedValue} is out of range for param {selected.ParamID} ('{selected.Name}'): min={selected.MinValue}, max={selected.MaxValue}. Set clampToRange=true to clamp automatically.");
            }

            appliedValue = Math.Clamp(appliedValue, selected.MinValue, selected.MaxValue);
            clamped = true;
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var beforeValues = client.Appearance.GetCurrentParamValues();
            var previousValue = beforeValues.TryGetValue(selected.ParamID, out var previous)
                ? previous
                : selected.DefaultValue;

            var archetype = new GenepoolArchetype
            {
                Name = "mcp-visual-param-set",
                Params = new[]
                {
                    new ArchetypeParam
                    {
                        Id = selected.ParamID,
                        Name = selected.Name,
                        Value = appliedValue
                    }
                }
            };

            await client.Appearance.ApplyArchetype(archetype).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var afterValues = client.Appearance.GetCurrentParamValues();
            var resultingValue = afterValues.TryGetValue(selected.ParamID, out var after)
                ? after
                : appliedValue;

            var changed = Math.Abs(resultingValue - previousValue) > 0.0001f;
            var message = changed
                ? $"Updated visual param {selected.ParamID} ('{selected.Name}') from {previousValue} to {resultingValue}; force rebake requested."
                : $"Visual param {selected.ParamID} ('{selected.Name}') remains {resultingValue}; force rebake requested. If this is unexpected, refresh worn state and retry.";

            return AppearanceVisualParamSetResult.OkResult(
                selected.ParamID,
                selected.Name,
                selected.Wearable,
                previousValue,
                requestedValue,
                resultingValue,
                selected.MinValue,
                selected.MaxValue,
                clamped,
                changed,
                message);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppearanceBakeDiagnosticsResult> AppearanceBakeDiagnosticsAsync(
        bool requestCacheProbe,
        int cacheProbeTimeoutMs,
        CancellationToken cancellationToken)
    {
        if (cacheProbeTimeoutMs < 100 || cacheProbeTimeoutMs > 15000)
        {
            return AppearanceBakeDiagnosticsResult.FailResult("cacheProbeTimeoutMs must be between 100 and 15000.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var currentValues = client.Appearance.GetCurrentParamValues();
            var nonDefaultCount = 0;
            foreach (var param in VisualParams.Params.Values)
            {
                var current = currentValues.TryGetValue(param.ParamID, out var value)
                    ? value
                    : param.DefaultValue;
                if (Math.Abs(current - param.DefaultValue) > 0.0001f)
                {
                    nonDefaultCount++;
                }
            }

            var cacheProbeCompleted = false;
            var cacheProbeElapsedMs = 0;
            if (requestCacheProbe)
            {
                var sw = Stopwatch.StartNew();
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<AgentCachedBakesReplyEventArgs> onCached = (_, _) => tcs.TrySetResult(true);
                client.Appearance.CachedBakesReply += onCached;
                try
                {
                    client.Appearance.RequestCachedBakes();
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(cacheProbeTimeoutMs, token)).ConfigureAwait(false);
                    cacheProbeCompleted = completed == tcs.Task && tcs.Task.IsCompletedSuccessfully;
                }
                finally
                {
                    client.Appearance.CachedBakesReply -= onCached;
                    sw.Stop();
                    cacheProbeElapsedMs = (int)sw.ElapsedMilliseconds;
                }
            }

            var bakedTextures = BuildBakeDiagnostics(client.Appearance.MyTextures);
            var message = requestCacheProbe
                ? (cacheProbeCompleted
                    ? $"Collected bake diagnostics and cache probe reply in {cacheProbeElapsedMs}ms."
                    : $"Collected bake diagnostics; cache probe timed out after {cacheProbeElapsedMs}ms.")
                : "Collected bake diagnostics from current appearance state.";

            return AppearanceBakeDiagnosticsResult.OkResult(
                client.Appearance.ServerBakingRegion(),
                client.Appearance.ManagerBusy,
                client.Appearance.MyVisualParameters.Length,
                currentValues.Count,
                nonDefaultCount,
                requestCacheProbe,
                cacheProbeCompleted,
                cacheProbeElapsedMs,
                bakedTextures,
                message);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (bool Ok, string Message, VisualParam? Param) ResolveVisualParam(int? paramId, string? paramName, string? wearable)
    {
        if (paramId.HasValue)
        {
            if (!VisualParams.Params.TryGetValue(paramId.Value, out var byId))
            {
                return (false, $"Unknown visual param id {paramId.Value}.", null);
            }

            if (!string.IsNullOrWhiteSpace(wearable)
                && !string.Equals(byId.Wearable, wearable.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Param id {paramId.Value} is wearable='{byId.Wearable}', not '{wearable.Trim()}'.", null);
            }

            return (true, string.Empty, byId);
        }

        if (string.IsNullOrWhiteSpace(paramName))
        {
            return (false, "Provide either paramId or paramName.", null);
        }

        var name = paramName.Trim();
        var wearableFilter = string.IsNullOrWhiteSpace(wearable) ? null : wearable.Trim();
        var matches = VisualParams.Params.Values
            .Where(param => string.Equals(param.Name, name, StringComparison.OrdinalIgnoreCase)
                && (wearableFilter == null || string.Equals(param.Wearable, wearableFilter, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
        {
            return wearableFilter == null
                ? (false, $"No visual param matched name '{name}'.", null)
                : (false, $"No visual param matched name '{name}' with wearable '{wearableFilter}'.", null);
        }

        if (matches.Count > 1)
        {
            var options = string.Join(", ", matches.Select(match => $"{match.ParamID}:{match.Wearable}"));
            return (false, $"Param name '{name}' is ambiguous. Pass wearable or paramId. Matches: {options}", null);
        }

        return (true, string.Empty, matches[0]);
    }

    private static IReadOnlyList<AppearanceBakeTextureInfo> BuildBakeDiagnostics(Primitive.TextureEntry textures)
    {
        var list = new List<AppearanceBakeTextureInfo>(AppearanceManager.BAKED_TEXTURE_COUNT);
        for (var bakeIndex = 0; bakeIndex < AppearanceManager.BAKED_TEXTURE_COUNT; bakeIndex++)
        {
            var bakeType = (BakeType)bakeIndex;
            var textureIndex = (AvatarTextureIndex)AppearanceManager.BakeIndexToTextureIndex[bakeIndex];
            var face = textures.GetFace((uint)textureIndex) ?? textures.DefaultTexture;
            var textureId = face?.TextureID ?? UUID.Zero;
            var hasTexture = textureId != UUID.Zero;
            var isDefaultTexture = textureId == AppearanceManager.DEFAULT_AVATAR_TEXTURE;

            list.Add(new AppearanceBakeTextureInfo(
                bakeType.ToString(),
                textureIndex.ToString(),
                (int)textureIndex,
                textureId.ToString(),
                hasTexture,
                isDefaultTexture));
        }

        return list;
    }

    public async Task<ScriptUpdateResult> ScriptUploadAgentAsync(string source, string itemId, bool mono, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var scriptItemId))
        {
            return ScriptUpdateResult.FailResult("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sourceBytes = await ReadBinarySourceAsync(source, token).ConfigureAwait(false);
            var result = await client.Inventory.RequestUpdateScriptAgentInventoryAsync(sourceBytes, scriptItemId, mono, token).ConfigureAwait(false);

            if (!result.uploadSuccess)
            {
                return ScriptUpdateResult.FailResult($"Script upload failed: {result.uploadStatus}");
            }

            var messages = result.compileMessages?.ToList() ?? new List<string>();
            return ScriptUpdateResult.OkResult(
                result.itemID.ToString(),
                result.assetID.ToString(),
                sourceBytes.Length,
                result.uploadStatus,
                result.compileSuccess,
                messages,
                "Script upload to agent inventory completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptUpdateResult> ScriptUploadTaskAsync(
        string source,
        string itemId,
        string objectId,
        bool mono,
        bool running,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var scriptItemId))
        {
            return ScriptUpdateResult.FailResult("itemId is not a valid UUID.");
        }

        if (!UUID.TryParse(objectId, out var taskObjectId))
        {
            return ScriptUpdateResult.FailResult("objectId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sourceBytes = await ReadBinarySourceAsync(source, token).ConfigureAwait(false);
            var result = await client.Inventory
                .RequestUpdateScriptTaskAsync(sourceBytes, scriptItemId, taskObjectId, mono, running, token)
                .ConfigureAwait(false);

            if (!result.uploadSuccess)
            {
                return ScriptUpdateResult.FailResult($"Task script upload failed: {result.uploadStatus}");
            }

            var messages = result.compileMessages?.ToList() ?? new List<string>();
            return ScriptUpdateResult.OkResult(
                result.itemID.ToString(),
                result.assetID.ToString(),
                sourceBytes.Length,
                result.uploadStatus,
                result.compileSuccess,
                messages,
                "Script upload to task inventory completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ScriptCopyInventoryToTaskAsync(uint objectLocalId, string inventoryScriptItemId, bool enableScript, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(inventoryScriptItemId, out var scriptItemUuid))
        {
            return BotToolResult.Fail("inventoryScriptItemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            if (!sim.ObjectsPrimitives.ContainsKey(objectLocalId))
            {
                return BotToolResult.Fail($"Object localId={objectLocalId} is not present in simulator cache.");
            }

            var item = await ResolveInventoryItemAsync(client, scriptItemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {scriptItemUuid} was not found.");
            }

            if (item.AssetType != AssetType.LSLText || item.InventoryType != InventoryType.LSL)
            {
                return BotToolResult.Fail(
                    $"Inventory item {item.UUID} is not script-typed (assetType={item.AssetType}, inventoryType={item.InventoryType}). " +
                    "Bridge install requires a real LSL script inventory item.");
            }

            var transaction = client.Inventory.CopyScriptToTask(objectLocalId, item, enableScript, sim);
            return BotToolResult.OkResult($"Requested script copy to object {objectLocalId}; transactionId={transaction}, enableScript={enableScript}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptRunningResult> ScriptGetTaskRunningAsync(string objectId, string scriptItemId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(objectId, out var taskObjectId))
        {
            return ScriptRunningResult.FailResult("objectId is not a valid UUID.");
        }

        if (!UUID.TryParse(scriptItemId, out var scriptItemUuid))
        {
            return ScriptRunningResult.FailResult("scriptItemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var replyTask = WaitForScriptRunningReplyAsync(client, taskObjectId, scriptItemUuid, token);
            client.Inventory.RequestGetScriptRunning(taskObjectId, scriptItemUuid);

            var reply = await replyTask.ConfigureAwait(false);
            if (reply == null)
            {
                return ScriptRunningResult.FailResult("Timed out waiting for script running status reply.");
            }

            return ScriptRunningResult.OkResult(reply.ObjectID.ToString(), reply.ScriptID.ToString(), reply.IsRunning, reply.IsMono, "Retrieved script running status.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptRunningResult> ScriptSetTaskRunningAsync(string objectId, string scriptItemId, bool running, bool verifyAfterSet, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(objectId, out var taskObjectId))
        {
            return ScriptRunningResult.FailResult("objectId is not a valid UUID.");
        }

        if (!UUID.TryParse(scriptItemId, out var scriptItemUuid))
        {
            return ScriptRunningResult.FailResult("scriptItemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            Task<ScriptRunningReplyEventArgs?>? verifyTask = null;
            if (verifyAfterSet)
            {
                verifyTask = WaitForScriptRunningReplyAsync(client, taskObjectId, scriptItemUuid, token);
            }

            client.Inventory.RequestSetScriptRunning(taskObjectId, scriptItemUuid, running);

            if (!verifyAfterSet)
            {
                return ScriptRunningResult.OkResult(taskObjectId.ToString(), scriptItemUuid.ToString(), running, null, "Set script running state request sent.");
            }

            client.Inventory.RequestGetScriptRunning(taskObjectId, scriptItemUuid);
            var reply = await verifyTask!.ConfigureAwait(false);
            if (reply == null)
            {
                return ScriptRunningResult.FailResult("Set request sent, but timed out waiting for verification reply.");
            }

            return ScriptRunningResult.OkResult(reply.ObjectID.ToString(), reply.ScriptID.ToString(), reply.IsRunning, reply.IsMono, "Set script running state and verified reply.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DialogBridgeInstallResult> DialogBridgeInstallAsync(CancellationToken cancellationToken)
    {
        var client = EnsureClient();

        var resolve = await ResolveCubeBotIarItemsAsync(client, cancellationToken).ConfigureAwait(false);
        if (!resolve.Ok || resolve.Folder == null || resolve.AttachmentItem == null || resolve.AlphaItem == null)
        {
            return DialogBridgeInstallResult.FailResult(resolve.Error ?? "Cube Bot IAR inventory is not available.");
        }

        var cubeFolder = resolve.Folder;
        var attachmentItem = resolve.AttachmentItem;
        var alphaItem = resolve.AlphaItem;

        var appearanceState = await AppearanceListWornAsync(cancellationToken).ConfigureAwait(false);
        if (!appearanceState.Ok)
        {
            return DialogBridgeInstallResult.FailResult($"Failed to inspect current wearables/attachments: {appearanceState.Message}");
        }

        var alphaWorn = appearanceState.Wearables.Any(w => string.Equals(w.ItemId, alphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
        var attachmentWorn = appearanceState.Attachments.Any(a => string.Equals(a.ItemId, attachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));

        if (!alphaWorn || !attachmentWorn)
        {
            // First normalize to Current Outfit. This is the same reset strategy used during manual recovery.
            var currentOutfitFolderId = client.Inventory.FindFolderForType(FolderType.CurrentOutfit);
            if (currentOutfitFolderId != UUID.Zero)
            {
                var wearCurrentOutfit = await AppearanceWearFolderAsync(currentOutfitFolderId.ToString(), replaceItems: true, cancellationToken).ConfigureAwait(false);
                if (!wearCurrentOutfit.Ok)
                {
                    Console.WriteLine($"[dialog-bridge] Current Outfit reset failed: {wearCurrentOutfit.Message}");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] requested Current Outfit reset from folder {currentOutfitFolderId} (replaceItems=true).");
                }

                // Reset can arrive slightly later than subsequent wear/attach requests; wait for settle.
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("[dialog-bridge] Current Outfit folder not found; continuing without reset.");
            }

            appearanceState = await AppearanceListWornAsync(cancellationToken).ConfigureAwait(false);
            if (!appearanceState.Ok)
            {
                return DialogBridgeInstallResult.FailResult($"Failed to verify appearance after Current Outfit reset: {appearanceState.Message}");
            }

            alphaWorn = appearanceState.Wearables.Any(w => string.Equals(w.ItemId, alphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
            attachmentWorn = appearanceState.Attachments.Any(a => string.Equals(a.ItemId, attachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));

            if (!alphaWorn)
            {
                var wearAlpha = await AppearanceWearWearableItemAsync(alphaItem.UUID, replaceExistingSlot: true, cancellationToken).ConfigureAwait(false);
                if (!wearAlpha.Ok)
                {
                    Console.WriteLine($"[dialog-bridge] alpha wear request failed: {wearAlpha.Message}");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] requested wear of alpha '{alphaItem.Name}' ({alphaItem.UUID}) from folder '{cubeFolder.Name}'.");
                }

                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                appearanceState = await AppearanceListWornAsync(cancellationToken).ConfigureAwait(false);
                if (!appearanceState.Ok)
                {
                    return DialogBridgeInstallResult.FailResult($"Failed to verify appearance after alpha wear request: {appearanceState.Message}");
                }

                alphaWorn = appearanceState.Wearables.Any(w => string.Equals(w.ItemId, alphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
                attachmentWorn = appearanceState.Attachments.Any(a => string.Equals(a.ItemId, attachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
            }

            if (!attachmentWorn)
            {
                var attach = await AppearanceAttachItemAsync(attachmentItem.UUID.ToString(), "Spine", replace: true, cancellationToken).ConfigureAwait(false);
                if (!attach.Ok)
                {
                    Console.WriteLine($"[dialog-bridge] bridge attach request failed: {attach.Message}");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] requested attach of '{attachmentItem.Name}' ({attachmentItem.UUID}) on Spine.");
                }

                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                appearanceState = await AppearanceListWornAsync(cancellationToken).ConfigureAwait(false);
                if (!appearanceState.Ok)
                {
                    return DialogBridgeInstallResult.FailResult($"Failed to verify appearance after attachment request: {appearanceState.Message}");
                }

                alphaWorn = appearanceState.Wearables.Any(w => string.Equals(w.ItemId, alphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
                attachmentWorn = appearanceState.Attachments.Any(a => string.Equals(a.ItemId, attachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
            }

            // Do not force rebake here; on some grids this can churn appearance state and
            // temporarily drop late-applied wearables (including alpha).

            // Passive verification only. No repeated attach/wear requests.
            var verifyPasses = 4;
            for (var pass = 1; pass <= verifyPasses && (!alphaWorn || !attachmentWorn); pass++)
            {
                await Task.Delay(1300, cancellationToken).ConfigureAwait(false);
                appearanceState = await AppearanceListWornAsync(cancellationToken).ConfigureAwait(false);
                if (!appearanceState.Ok)
                {
                    Console.WriteLine($"[dialog-bridge] worn-state verification pass {pass}/{verifyPasses} failed: {appearanceState.Message}");
                    continue;
                }

                alphaWorn = appearanceState.Wearables.Any(w => string.Equals(w.ItemId, alphaItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
                attachmentWorn = appearanceState.Attachments.Any(a => string.Equals(a.ItemId, attachmentItem.UUID.ToString(), StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!attachmentWorn || !alphaWorn)
        {
            var missing = new List<string>(2);
            if (!attachmentWorn)
            {
                missing.Add($"attachment '{attachmentItem.Name}' on Spine");
            }

            if (!alphaWorn)
            {
                missing.Add($"wearable '{alphaItem.Name}'");
            }

            return DialogBridgeInstallResult.FailResult($"Bridge install incomplete; missing {string.Join(" and ", missing)}.");
        }

        UUID attachedObjectId = UUID.Zero;
        uint attachedLocalId = 0;
        var maxAttempts = 20;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (TryFindAttachedObjectForInventoryItem(client, attachmentItem.UUID, out attachedObjectId, out attachedLocalId))
            {
                break;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        if (attachedObjectId != UUID.Zero)
        {
            lock (_dialogBridgeTrustLock)
            {
                _trustedDialogBridgeObjectId = attachedObjectId;
                _trustedDialogBridgeOwnerId = client.Self.AgentID;
            }
            TrySaveDialogBridgeTrustStateToFile();
            Console.WriteLine($"[dialog-bridge] pinned trusted bridge sender to attached object {attachedObjectId} owner={client.Self.AgentID}");
        }

        var installMessage = attachedObjectId != UUID.Zero
            ? $"Bridge ready from Cube Bot IAR: item={attachmentItem.UUID}, objectLocalId={attachedLocalId}, objectId={attachedObjectId}."
            : $"Bridge ready from Cube Bot IAR: item={attachmentItem.UUID}. Attachment is worn; object pin is pending simulator cache visibility.";

        return DialogBridgeInstallResult.OkResult(
            attachedLocalId,
            attachedObjectId == UUID.Zero ? null : attachedObjectId.ToString(),
            client.Self.AgentID.ToString(),
            attachmentItem.UUID.ToString(),
            attachmentItem.AssetUUID == UUID.Zero ? null : attachmentItem.AssetUUID.ToString(),
            installMessage);
    }

    public async Task<InventoryQueryResult> InventoryListAsync(
        string? folderId,
        bool recursive,
        int maxResults,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            var root = store?.RootFolder;
            if (store == null || root == null)
            {
                return InventoryQueryResult.FailResult("Inventory store is not initialized.");
            }

            var folderUuid = root.UUID;
            if (!string.IsNullOrWhiteSpace(folderId))
            {
                if (!UUID.TryParse(folderId, out folderUuid))
                {
                    return InventoryQueryResult.FailResult("folderId is not a valid UUID.");
                }
            }

            var limit = Math.Clamp(maxResults, 1, 2000);
            var owner = client.Self.AgentID;
            var entries = new List<InventoryBase>();

            if (!store.TryGetValue(folderUuid, out var folderNode) || folderNode is not InventoryFolder folder)
            {
                return InventoryQueryResult.FailResult($"Folder {folderUuid} was not found in local inventory store.");
            }

            entries.Add(folder);

            if (recursive)
            {
                var folders = new List<InventoryFolder>();
                var items = new List<InventoryItem>();
                await client.Inventory.GetInventoryRecursiveAsync(folderUuid, owner, folders, items, token).ConfigureAwait(false);
                entries.AddRange(folders);
                entries.AddRange(items);
            }
            else
            {
                var contents = await client.Inventory
                    .FolderContentsAsync(folderUuid, owner, true, true, InventorySortOrder.ByName, token)
                    .ConfigureAwait(false);
                entries.AddRange(contents);
            }

            var materialized = new List<InventoryEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is InventoryItem item)
                {
                    materialized.Add(ToInventoryEntry(item));
                }
                else
                {
                    materialized.Add(ToInventoryEntry(entry));
                }
            }

            var ordered = materialized
                .OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            return InventoryQueryResult.OkResult(ordered, $"Returned {ordered.Count} inventory entries.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryGiveItemAsync(
        string itemId,
        string recipientAgentId,
        bool withBeamEffect,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        if (!UUID.TryParse(recipientAgentId, out var recipientUuid))
        {
            return BotToolResult.Fail("recipientAgentId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            InventoryItem? item = null;
            if (store != null && store.TryGetValue(itemUuid, out var node))
            {
                item = node as InventoryItem;
            }

            item ??= await client.Inventory.FetchItemAsync(itemUuid, client.Self.AgentID, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            client.Inventory.GiveItem(item.UUID, item.Name, item.AssetType, recipientUuid, withBeamEffect);
            return BotToolResult.OkResult($"Gave item '{item.Name}' ({item.UUID}) to {recipientUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryGiveFolderAsync(
        string folderId,
        string recipientAgentId,
        bool withBeamEffect,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(folderId, out var folderUuid))
        {
            return BotToolResult.Fail("folderId is not a valid UUID.");
        }

        if (!UUID.TryParse(recipientAgentId, out var recipientUuid))
        {
            return BotToolResult.Fail("recipientAgentId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null || !store.TryGetValue(folderUuid, out var node) || node is not InventoryFolder folder)
            {
                return BotToolResult.Fail($"Inventory folder {folderUuid} was not found in local store.");
            }

            await client.Inventory.GiveFolderAsync(folder.UUID, folder.Name, recipientUuid, withBeamEffect, token).ConfigureAwait(false);
            return BotToolResult.OkResult($"Gave folder '{folder.Name}' ({folder.UUID}) to {recipientUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryDeleteItemAsync(string itemId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            await client.Inventory.RemoveItemAsync(itemUuid, token).ConfigureAwait(false);
            return BotToolResult.OkResult($"Delete request sent for inventory item '{item.Name}' ({itemUuid}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryDeleteFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(folderId, out var folderUuid))
        {
            return BotToolResult.Fail("folderId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null || !store.TryGetValue(folderUuid, out var node) || node is not InventoryFolder folder)
            {
                return BotToolResult.Fail($"Inventory folder {folderUuid} was not found in local store.");
            }

            await client.Inventory.RemoveFolderAsync(folderUuid, token).ConfigureAwait(false);
            return BotToolResult.OkResult($"Delete request sent for inventory folder '{folder.Name}' ({folderUuid}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryDeleteManyAsync(string itemIdsCsv, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemIdsCsv))
        {
            return BotToolResult.Fail("itemIdsCsv is required (comma-separated UUIDs).");
        }

        var ids = new List<UUID>();
        var seen = new HashSet<UUID>();
        var parts = itemIdsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (!UUID.TryParse(part, out var parsed))
            {
                return BotToolResult.Fail($"Invalid item UUID '{part}'.");
            }

            if (seen.Add(parsed))
            {
                ids.Add(parsed);
            }
        }

        if (ids.Count == 0)
        {
            return BotToolResult.Fail("No valid item UUIDs were provided.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var deleted = 0;
            var missing = 0;
            foreach (var itemId in ids)
            {
                var item = await ResolveInventoryItemAsync(client, itemId, token).ConfigureAwait(false);
                if (item == null)
                {
                    missing++;
                    continue;
                }

                await client.Inventory.RemoveItemAsync(itemId, token).ConfigureAwait(false);
                deleted++;
            }

            return BotToolResult.OkResult($"Delete requests sent for {deleted} inventory item(s). Missing/not-found: {missing}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> DialogBridgeUninstallAsync(bool clearTrustPins, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, token) =>
        {
            var details = new List<string>();
            var bridgeDetached = false;

            if (TryGetPinnedBridgeObjectInCurrentSim(out var pinnedObjectId, out var pinnedLocalId))
            {
                var sim = client.Network.CurrentSim;
                Primitive? pinnedPrim = null;
                if (sim != null)
                {
                    foreach (var prim in sim.ObjectsPrimitives.Values)
                    {
                        if (prim.ID == pinnedObjectId)
                        {
                            pinnedPrim = prim;
                            break;
                        }
                    }
                }

                var appearsAttached = pinnedPrim != null && pinnedPrim.ParentID == client.Self.LocalID;
                if (appearsAttached)
                {
                    if (TryGetAttachItemIdFromPrimNameValues(pinnedPrim!, out var attachItemId))
                    {
                        client.Appearance.Detach(attachItemId);
                        details.Add($"Requested detach for worn bridge attachment item {attachItemId}.");

                    }
                    else
                    {
                        details.Add($"Pinned bridge object {pinnedObjectId} appears attached, but AttachItemID was not found in NameValues.");
                    }
                }
                else
                {
                    client.Inventory.RequestDeRezToInventory(pinnedLocalId);
                    bridgeDetached  = true;
                    details.Add($"Requested bridge prim delete (de-rez): object={pinnedObjectId}, localId={pinnedLocalId}.");
                }
            }
            else
            {
                details.Add("No pinned bridge object was found in the current simulator cache.");
            }

            if (clearTrustPins)
            {
                lock (_dialogBridgeTrustLock)
                {
                    _trustedDialogBridgeObjectId = UUID.Zero;
                    _trustedDialogBridgeOwnerId = UUID.Zero;
                }

                TrySaveDialogBridgeTrustStateToFile();
                details.Add("Cleared runtime trusted bridge object/owner pins.");
            }

            if (!bridgeDetached && !clearTrustPins)
            {
                details.Add("No uninstall actions were requested.");
            }

            return Task.FromResult(BotToolResult.OkResult(string.Join(" ", details)));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetAttachItemIdFromPrimNameValues(Primitive prim, out UUID attachItemId)
    {
        attachItemId = UUID.Zero;
        if (prim.NameValues == null || !prim.NameValues.Any())
        {
            return false;
        }

        foreach (var nameValue in prim.NameValues)
        {
            if (!nameValue.Name.Equals("AttachItemID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = nameValue.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(raw) && UUID.TryParse(raw, out attachItemId) && attachItemId != UUID.Zero)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<InventoryQueryResult> TaskInventoryListAsync(
        uint objectLocalId,
        string? objectId,
        int maxResults,
        CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return InventoryQueryResult.FailResult("No current simulator available.");
            }

            var localId = objectLocalId;
            var objectUuid = UUID.Zero;
            if (!TryResolveTaskInventoryObject(sim, objectLocalId, objectId, out objectUuid, out localId, out var resolveError))
            {
                return InventoryQueryResult.FailResult(resolveError ?? "Unable to resolve object reference.");
            }

            var entries = await client.Inventory
                .GetTaskInventoryAsync(objectUuid, localId, sim, token)
                .ConfigureAwait(false);

            var limit = Math.Clamp(maxResults, 1, 2000);
            var mapped = entries
                .Select(ToInventoryEntry)
                .OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            return InventoryQueryResult.OkResult(mapped, $"Returned {mapped.Count} task-inventory entries for object localId={localId}, objectId={objectUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TaskInventoryTakeAsync(
        uint objectLocalId,
        string taskItemId,
        string? destinationFolderId,
        string? objectId,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(taskItemId, out var taskItemUuid))
        {
            return BotToolResult.Fail("taskItemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            var localId = objectLocalId;
            var objectUuid = UUID.Zero;
            if (!TryResolveTaskInventoryObject(sim, objectLocalId, objectId, out objectUuid, out localId, out var resolveError))
            {
                return BotToolResult.Fail(resolveError ?? "Unable to resolve object reference.");
            }

            var taskEntries = await client.Inventory
                .GetTaskInventoryAsync(objectUuid, localId, sim, token)
                .ConfigureAwait(false);

            var taskItem = taskEntries.OfType<InventoryItem>().FirstOrDefault(i => i.UUID == taskItemUuid);
            if (taskItem == null)
            {
                return BotToolResult.Fail($"Task inventory item {taskItemUuid} was not found on object localId={localId}, objectId={objectUuid}.");
            }

            UUID destinationFolderUuid;
            if (string.IsNullOrWhiteSpace(destinationFolderId))
            {
                destinationFolderUuid = client.Inventory.FindFolderForType(taskItem.AssetType);
                if (destinationFolderUuid == UUID.Zero)
                {
                    return BotToolResult.Fail($"No default destination folder was found for asset type {taskItem.AssetType}.");
                }
            }
            else if (!UUID.TryParse(destinationFolderId, out destinationFolderUuid))
            {
                return BotToolResult.Fail("destinationFolderId is not a valid UUID.");
            }

            client.Inventory.MoveTaskInventory(localId, taskItem.UUID, destinationFolderUuid, sim);
            return BotToolResult.OkResult(
                $"Requested task-inventory transfer for item {taskItem.UUID} from object localId={localId}, objectId={objectUuid} to folder {destinationFolderUuid}. Server decides copy/move based on permissions.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryResolveTaskInventoryObject(
        Simulator sim,
        uint requestedLocalId,
        string? requestedObjectId,
        out UUID objectUuid,
        out uint objectLocalId,
        out string? error)
    {
        objectUuid = UUID.Zero;
        objectLocalId = 0;
        error = null;

        UUID parsedObjectId = UUID.Zero;
        var hasObjectId = !string.IsNullOrWhiteSpace(requestedObjectId);
        if (hasObjectId && !UUID.TryParse(requestedObjectId!, out parsedObjectId))
        {
            error = "objectId is not a valid UUID.";
            return false;
        }

        Primitive? prim = null;
        if (requestedLocalId != 0)
        {
            sim.ObjectsPrimitives.TryGetValue(requestedLocalId, out prim);
            if (prim == null && !hasObjectId)
            {
                error = $"Object localId={requestedLocalId} is not present in simulator cache.";
                return false;
            }
        }

        if (prim == null && hasObjectId)
        {
            prim = sim.ObjectsPrimitives.Values.FirstOrDefault(p => p != null && p.ID == parsedObjectId);
            if (prim == null)
            {
                error = $"Object objectId={parsedObjectId} is not present in current simulator cache; try moving closer or waiting for object updates.";
                return false;
            }
        }

        if (prim == null)
        {
            error = "Either objectLocalId or objectId is required.";
            return false;
        }

        if (hasObjectId && prim.ID != parsedObjectId)
        {
            error = $"objectLocalId={requestedLocalId} refers to objectId={prim.ID}, which does not match requested objectId={parsedObjectId}.";
            return false;
        }

        objectUuid = hasObjectId ? parsedObjectId : prim.ID;
        objectLocalId = prim.LocalID;
        return true;
    }

    public async Task<AssetTransferResult> AssetUploadInventoryAsync(
        string source,
        string assetType,
        string inventoryType,
        string name,
        string description,
        string? folderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return AssetTransferResult.FailResult("source is required (file path or URL).");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return AssetTransferResult.FailResult("name is required.");
        }

        if (!TryResolveUploadTypes(source, name, assetType, inventoryType, out var parsedAssetType, out var parsedInventoryType, out var typeError))
        {
            return AssetTransferResult.FailResult(typeError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var data = await ReadBinarySourceAsync(source, token).ConfigureAwait(false);
            if (data.Length == 0)
            {
                return AssetTransferResult.FailResult("Resolved source bytes are empty.");
            }

            UUID folderUuid;
            if (!string.IsNullOrWhiteSpace(folderId))
            {
                if (!UUID.TryParse(folderId, out folderUuid))
                {
                    return AssetTransferResult.FailResult("folderId is not a valid UUID.");
                }
            }
            else
            {
                folderUuid = client.Inventory.FindFolderForType(parsedAssetType);
                if (folderUuid == UUID.Zero)
                {
                    var root = client.Inventory.Store?.RootFolder;
                    if (root == null)
                    {
                        return AssetTransferResult.FailResult("No target folder found and inventory root folder is unavailable.");
                    }

                    folderUuid = root.UUID;
                }
            }

            if (parsedAssetType == AssetType.LSLText || parsedInventoryType == InventoryType.LSL)
            {
                return await UploadScriptInventoryItemAsync(client, data, name, description ?? string.Empty, folderUuid, token)
                    .ConfigureAwait(false);
            }

            var result = await client.Inventory
                .RequestCreateItemFromAssetAsync(
                    data,
                    name,
                    description ?? string.Empty,
                    parsedAssetType,
                    parsedInventoryType,
                    folderUuid,
                    Permissions.NoPermissions,
                    token)
                .ConfigureAwait(false);

            if (!result.success)
            {
                return AssetTransferResult.FailResult($"Asset upload failed: {result.status}");
            }

            return AssetTransferResult.OkResult(
                result.itemID.ToString(),
                result.assetID.ToString(),
                data.Length,
                $"Uploaded {data.Length} bytes as {parsedAssetType}/{parsedInventoryType} into folder {folderUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AssetTransferResult> UploadScriptInventoryItemAsync(
        GridClient client,
        byte[] data,
        string name,
        string description,
        UUID folderUuid,
        CancellationToken cancellationToken)
    {
        const AssetType createAssetType = AssetType.LSLText;
        const InventoryType createInventoryType = InventoryType.LSL;
        const WearableType createWearableType = (WearableType)0;
        const PermissionMask createNextOwnerMask = PermissionMask.All;
        // Viewer-created "New Script" uses a zero transaction ID for create-item.
        var createTransactionId = UUID.Zero;

        var createStopwatch = Stopwatch.StartNew();
        var createdItem = await client.Inventory
            .CreateItemAsync(
                folderUuid,
                name,
                description,
                createAssetType,
                createTransactionId,
                createInventoryType,
                createWearableType,
                createNextOwnerMask,
                cancellationToken)
            .ConfigureAwait(false);
        createStopwatch.Stop();

        if (createdItem == null)
        {
            return AssetTransferResult.FailResult($"Script item creation failed before upload (CreateItemAsync returned null, elapsed {createStopwatch.ElapsedMilliseconds}ms).");
        }

        if (createdItem.UUID == UUID.Zero)
        {
            return AssetTransferResult.FailResult("Script item creation returned an empty item UUID.");
        }

        const bool uploadMono = true;

        var upload = await client.Inventory
            .RequestUpdateScriptAgentInventoryAsync(data, createdItem.UUID, mono: uploadMono, cancellationToken)
            .ConfigureAwait(false);

        if (!upload.uploadSuccess)
        {
            return AssetTransferResult.FailResult($"Script upload failed for created item {createdItem.UUID}: {upload.uploadStatus}");
        }

        if (upload.itemID != UUID.Zero && upload.itemID != createdItem.UUID)
        {
            return AssetTransferResult.FailResult(
                $"Script upload verification failed: capability updated item {upload.itemID}, but created item was {createdItem.UUID}.");
        }

        InventoryItem? item = null;
        const int verifyAttempts = 6;
        for (var attempt = 1; attempt <= verifyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            item = await client.Inventory.FetchItemAsync(createdItem.UUID, client.Self.AgentID, cancellationToken).ConfigureAwait(false)
                ?? await client.Inventory.FetchItemHttpAsync(createdItem.UUID, client.Self.AgentID, cancellationToken).ConfigureAwait(false);

            if (item != null)
            {
                if (item.AssetType == AssetType.LSLText && item.InventoryType == InventoryType.LSL)
                {
                    break;
                }
            }

            if (attempt < verifyAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            }
        }

        if (item == null)
        {
            return AssetTransferResult.FailResult(
                $"Script upload succeeded but created item {createdItem.UUID} could not be fetched for verification. " +
                $"Returned assetID={upload.assetID}, status='{upload.uploadStatus}'.");
        }

        if (item.AssetType != AssetType.LSLText || item.InventoryType != InventoryType.LSL)
        {
            return AssetTransferResult.FailResult(
                $"Script upload verification failed: created item {createdItem.UUID} fetched as {item.AssetType}({(int)item.AssetType})/{item.InventoryType}({(int)item.InventoryType}) " +
                $"(expected {AssetType.LSLText}({(int)AssetType.LSLText})/{InventoryType.LSL}({(int)InventoryType.LSL})). capability returned assetID={upload.assetID}, status='{upload.uploadStatus}'.");
        }

        var uploadedAssetId = upload.assetID != UUID.Zero ? upload.assetID : item.AssetUUID;
        return AssetTransferResult.OkResult(
            createdItem.UUID.ToString(),
            uploadedAssetId.ToString(),
            data.Length,
            $"Uploaded {data.Length} bytes as script into folder {folderUuid} (item={createdItem.UUID}, status='{upload.uploadStatus}', compileSuccess={upload.compileSuccess}).");
    }

    private static bool TryResolveUploadTypes(
        string source,
        string name,
        string rawAssetType,
        string rawInventoryType,
        out AssetType assetType,
        out InventoryType inventoryType,
        out string error)
    {
        assetType = AssetType.Unknown;
        inventoryType = InventoryType.Unknown;
        error = string.Empty;

        var autoAssetType = string.IsNullOrWhiteSpace(rawAssetType) || string.Equals(rawAssetType.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
        var autoInventoryType = string.IsNullOrWhiteSpace(rawInventoryType) || string.Equals(rawInventoryType.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

        if (!autoAssetType)
        {
            if (!TryParseAssetType(rawAssetType, out assetType, out var assetTypeError))
            {
                error = assetTypeError;
                return false;
            }
        }

        if (!autoInventoryType)
        {
            if (!TryParseInventoryType(rawInventoryType, out inventoryType, out var inventoryTypeError))
            {
                error = inventoryTypeError;
                return false;
            }
        }

        if (!autoAssetType && !autoInventoryType)
        {
            return true;
        }

        if (!TryInferAssetAndInventoryType(source, name, out var inferredAssetType, out var inferredInventoryType))
        {
            error = "Could not infer asset type from source/name extension. Set assetType/inventoryType explicitly (or use extensions like .lsl, .txt, .jp2, .ogg, .bvh).";
            return false;
        }

        if (autoAssetType)
        {
            assetType = inferredAssetType;
        }

        if (autoInventoryType)
        {
            inventoryType = inferredInventoryType;
        }

        return true;
    }

    private static bool TryInferAssetAndInventoryType(
        string source,
        string name,
        out AssetType assetType,
        out InventoryType inventoryType)
    {
        assetType = AssetType.Unknown;
        inventoryType = InventoryType.Unknown;

        var extension = GetSourceExtension(source);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = Path.GetExtension(name ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        switch (extension.ToLowerInvariant())
        {
            case ".lsl":
                assetType = AssetType.LSLText;
                inventoryType = InventoryType.LSL;
                return true;
            case ".txt":
            case ".md":
                assetType = AssetType.Notecard;
                inventoryType = InventoryType.Notecard;
                return true;
            case ".jp2":
            case ".j2c":
            case ".j2k":
            case ".jpeg2000":
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".bmp":
            case ".tga":
                assetType = AssetType.Texture;
                inventoryType = InventoryType.Texture;
                return true;
            case ".ogg":
            case ".wav":
            case ".mp3":
                assetType = AssetType.Sound;
                inventoryType = InventoryType.Sound;
                return true;
            case ".bvh":
            case ".anim":
                assetType = AssetType.Animation;
                inventoryType = InventoryType.Animation;
                return true;
            case ".mesh":
                assetType = AssetType.Mesh;
                inventoryType = InventoryType.Mesh;
                return true;
            default:
                return false;
        }
    }

    private static string GetSourceExtension(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return Path.GetExtension(uri.AbsolutePath ?? string.Empty);
        }

        return Path.GetExtension(source);
    }

    public async Task<AssetDownloadResult> AssetDownloadAsync(
        string assetId,
        string assetType,
        string outputMode,
        string? fileNameHint,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(assetId, out var assetUuid))
        {
            return AssetDownloadResult.FailResult("assetId is not a valid UUID.");
        }

        if (!TryParseAssetType(assetType, out var parsedAssetType, out var assetTypeError))
        {
            return AssetDownloadResult.FailResult(assetTypeError);
        }

        if (!TryParseOutputMode(outputMode, out var mode, out var modeError))
        {
            return AssetDownloadResult.FailResult(modeError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var asset = await client.Assets.RequestAssetAsync(assetUuid, parsedAssetType, true, token).ConfigureAwait(false);
            if (asset?.AssetData == null)
            {
                return AssetDownloadResult.FailResult($"Unable to download asset {assetUuid} as type {parsedAssetType}.");
            }

            var payload = await BuildDownloadPayloadAsync(asset.AssetData, mode, fileNameHint, parsedAssetType, token).ConfigureAwait(false);
            return AssetDownloadResult.OkResult(
                payload.Base64,
                payload.FilePath,
                asset.AssetData.Length,
                asset.AssetID.ToString(),
                parsedAssetType.ToString(),
                "Asset download completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetDownloadResult> TextureDownloadAsync(
        string textureId,
        string outputMode,
        string? fileNameHint,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(textureId, out var textureUuid))
        {
            return AssetDownloadResult.FailResult("textureId is not a valid UUID.");
        }

        if (!TryParseOutputMode(outputMode, out var mode, out var modeError))
        {
            return AssetDownloadResult.FailResult(modeError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var texture = await client.Assets.RequestImageAsync(textureUuid, ImageType.Normal, token).ConfigureAwait(false);
            if (texture?.AssetData == null)
            {
                return AssetDownloadResult.FailResult($"Unable to download texture {textureUuid}.");
            }

            var payload = await BuildDownloadPayloadAsync(texture.AssetData, mode, fileNameHint, AssetType.Texture, token).ConfigureAwait(false);
            return AssetDownloadResult.OkResult(
                payload.Base64,
                payload.FilePath,
                texture.AssetData.Length,
                texture.AssetID.ToString(),
                AssetType.Texture.ToString(),
                "Texture download completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public BotToolResult InventoryOfferPolicyRuleAdd(
        string name,
        string action,
        string? senderAgentId,
        string? senderNameContains,
        string? assetType,
        bool? fromTask,
        string? destinationFolderId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BotToolResult.Fail("name is required.");
        }

        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedAction != "accept" && normalizedAction != "decline")
        {
            return BotToolResult.Fail("action must be 'accept' or 'decline'.");
        }

        UUID? senderUuid = null;
        if (!string.IsNullOrWhiteSpace(senderAgentId))
        {
            if (!UUID.TryParse(senderAgentId, out var parsed))
            {
                return BotToolResult.Fail("senderAgentId is not a valid UUID.");
            }

            senderUuid = parsed;
        }

        AssetType? ruleAssetType = null;
        if (!string.IsNullOrWhiteSpace(assetType))
        {
            if (!TryParseAssetType(assetType, out var parsedType, out var parseError))
            {
                return BotToolResult.Fail(parseError);
            }

            ruleAssetType = parsedType;
        }

        UUID? destinationFolder = null;
        if (!string.IsNullOrWhiteSpace(destinationFolderId))
        {
            if (!UUID.TryParse(destinationFolderId, out var parsedFolder))
            {
                return BotToolResult.Fail("destinationFolderId is not a valid UUID.");
            }

            destinationFolder = parsedFolder;
        }

        lock (_inventoryOfferLock)
        {
            var rule = new InventoryOfferPolicyRule(
                Id: ++_nextInventoryOfferRuleId,
                Name: name.Trim(),
                Action: normalizedAction,
                SenderAgentId: senderUuid,
                SenderNameContains: string.IsNullOrWhiteSpace(senderNameContains) ? null : senderNameContains.Trim(),
                AssetType: ruleAssetType,
                FromTask: fromTask,
                DestinationFolderId: destinationFolder);

            _inventoryOfferPolicyRules.Add(rule);
            TryAutoSaveInventoryOfferPolicies();
            return BotToolResult.OkResult($"Added inventory-offer policy rule #{rule.Id} ({rule.Name}) -> {rule.Action}.");
        }
    }

    public BotToolResult InventoryOfferPolicyRulesClear()
    {
        lock (_inventoryOfferLock)
        {
            _inventoryOfferPolicyRules.Clear();
            TryAutoSaveInventoryOfferPolicies();
        }

        return BotToolResult.OkResult("Cleared all inventory-offer policy rules.");
    }

    public InventoryOfferPolicyResult InventoryOfferPolicyRulesList()
    {
        lock (_inventoryOfferLock)
        {
            var rules = _inventoryOfferPolicyRules
                .Select(r => new InventoryOfferPolicyRuleInfo(
                    r.Id,
                    r.Name,
                    r.Action,
                    r.SenderAgentId?.ToString(),
                    r.SenderNameContains,
                    r.AssetType?.ToString(),
                    r.FromTask,
                    r.DestinationFolderId?.ToString()))
                .ToList();

            return InventoryOfferPolicyResult.OkResult(rules, $"Loaded {rules.Count} inventory-offer policy rules.");
        }
    }

    public InventoryOfferHistoryResult InventoryOfferHistoryList(int maxResults)
    {
        var limit = Math.Clamp(maxResults, 1, 200);

        lock (_inventoryOfferLock)
        {
            var entries = _inventoryOfferHistory
                .Reverse()
                .Take(limit)
                .ToList();

            return InventoryOfferHistoryResult.OkResult(entries, $"Returned {entries.Count} inventory-offer events.");
        }
    }

    private async Task<InventoryQueryResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<InventoryQueryResult>> action,
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
            return InventoryQueryResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceStateResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceStateResult>> action,
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
            return AppearanceStateResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AssetTransferResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AssetTransferResult>> action,
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
            return AssetTransferResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceWearFolderResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceWearFolderResult>> action,
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
            return AppearanceWearFolderResult.FailResult(replaceItems: false, ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<OutfitSaveResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<OutfitSaveResult>> action,
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
            return OutfitSaveResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AssetDownloadResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AssetDownloadResult>> action,
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
            return AssetDownloadResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<ScriptUpdateResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<ScriptUpdateResult>> action,
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
            return ScriptUpdateResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<ScriptRunningResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<ScriptRunningResult>> action,
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
            return ScriptRunningResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AttachmentTransformResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AttachmentTransformResult>> action,
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
            return AttachmentTransformResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private void OnInventoryObjectOffered(object? sender, InventoryObjectOfferedEventArgs e)
    {
        var fromAgentId = e.Offer.FromAgentID.ToString();
        var fromName = e.Offer.FromAgentName ?? string.Empty;
        var offerMessage = e.Offer.Message ?? string.Empty;
        var acceptedByHandlerOverride = IsHandlerRestricted() && IsHandlerAvatar(fromName);

        InventoryOfferPolicyRule? matchedRule = null;
        var decision = "decline";
        var destinationFolder = e.FolderID;

        lock (_inventoryOfferLock)
        {
            if (acceptedByHandlerOverride)
            {
                decision = "accept";
            }
            else
            {
                matchedRule = _inventoryOfferPolicyRules.FirstOrDefault(rule => IsInventoryOfferRuleMatch(rule, e));
                if (matchedRule != null)
                {
                    decision = matchedRule.Action;
                    if (decision == "accept" && matchedRule.DestinationFolderId.HasValue)
                    {
                        destinationFolder = matchedRule.DestinationFolderId.Value;
                    }
                }
            }

            var offerRecord = new InventoryOfferEventInfo(
                ++_nextInventoryOfferEventId,
                DateTimeOffset.UtcNow.ToString("O"),
                fromAgentId,
                fromName,
                e.AssetType.ToString(),
                e.FromTask,
                e.ObjectID.ToString(),
                offerMessage,
                decision,
                matchedRule?.Id,
                matchedRule?.Name,
                destinationFolder.ToString());

            _inventoryOfferHistory.Enqueue(offerRecord);
            while (_inventoryOfferHistory.Count > MaxInventoryOfferHistory)
            {
                _inventoryOfferHistory.Dequeue();
            }
        }

        e.Accept = decision == "accept";
        if (e.Accept)
        {
            e.FolderID = destinationFolder;
        }

        var reason = acceptedByHandlerOverride ? "handler" : "policy";
        Console.WriteLine($"[inventory-offer] from '{fromName}' ({fromAgentId}) type={e.AssetType} fromTask={e.FromTask} decision={decision} reason={reason}");

        if (e.Accept
            && _options.PromptHandlingEnabled
            && _options.PromptNotecardEnabled
            && e.AssetType == AssetType.Notecard)
        {
            var offeredObjectId = e.ObjectID;
            var offeredFromName = fromName;
            var offeredFromAgentId = e.Offer.FromAgentID;
            _ = Task.Run(() => TryInstallAgentsPromptFromOfferAsync(offeredObjectId, offeredFromName, offeredFromAgentId));
        }
    }

    private async Task TryInstallAgentsPromptFromOfferAsync(UUID offeredObjectId, string fromName, UUID fromAgentId)
    {
        if (!_options.PromptNotecardEnabled || !_options.PromptHandlingEnabled)
        {
            return;
        }

        if (_options.PromptNotecardRequireHandler)
        {
            if (!IsHandlerRestricted())
            {
                Console.WriteLine("[prompt] ignored AGENTS.md notecard offer because handler-only mode is enabled but no handler is configured.");
                return;
            }

            if (!IsHandlerAvatar(fromName))
            {
                Console.WriteLine($"[prompt] ignored AGENTS.md notecard offer from '{fromName}' because handler-only install mode is enabled.");
                return;
            }
        }

        var attempts = 0;
        while (attempts < 6)
        {
            attempts++;
            await Task.Delay(TimeSpan.FromMilliseconds(450)).ConfigureAwait(false);

            await _actionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var client = _client;
                if (client == null)
                {
                    return;
                }

                var inventoryItem = await ResolveInventoryItemAsync(client, offeredObjectId, CancellationToken.None).ConfigureAwait(false);
                if (inventoryItem == null)
                {
                    continue;
                }

                if (!string.Equals(inventoryItem.Name?.Trim(), "AGENTS.md", StringComparison.OrdinalIgnoreCase))
                {
                    // Strict mode: only AGENTS.md is accepted as a prompt notecard source.
                    return;
                }

                var notecardAsset = await client.Assets.RequestInventoryAssetAsync(
                    inventoryItem.AssetUUID,
                    inventoryItem.UUID,
                    UUID.Zero,
                    client.Self.AgentID,
                    AssetType.Notecard,
                    true,
                    UUID.Random(),
                    CancellationToken.None).ConfigureAwait(false);

                if (notecardAsset?.AssetData == null || notecardAsset.AssetData.Length == 0)
                {
                    Console.WriteLine($"[prompt] failed to download AGENTS.md notecard asset for item {inventoryItem.UUID}.");
                    return;
                }

                var notecard = new AssetNotecard(inventoryItem.AssetUUID, notecardAsset.AssetData);
                if (!notecard.Decode() || string.IsNullOrWhiteSpace(notecard.BodyText))
                {
                    Console.WriteLine($"[prompt] failed to decode AGENTS.md notecard for item {inventoryItem.UUID}.");
                    return;
                }

                SetActiveAgentsNotecardPrompt(notecard.BodyText, fromName, inventoryItem.UUID.ToString());
                Console.WriteLine($"[prompt] installed in-world AGENTS.md prompt from '{fromName}' ({fromAgentId}), item={inventoryItem.UUID}.");
                return;
            }
            catch (Exception ex)
            {
                if (attempts >= 6)
                {
                    Console.WriteLine($"[prompt] failed to install AGENTS.md notecard prompt: {ex.Message}");
                }
            }
            finally
            {
                _actionGate.Release();
            }
        }
    }

    private static bool IsInventoryOfferRuleMatch(InventoryOfferPolicyRule rule, InventoryObjectOfferedEventArgs offer)
    {
        if (rule.SenderAgentId.HasValue && rule.SenderAgentId.Value != offer.Offer.FromAgentID)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SenderNameContains))
        {
            var fromName = offer.Offer.FromAgentName ?? string.Empty;
            if (!fromName.Contains(rule.SenderNameContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (rule.AssetType.HasValue && rule.AssetType.Value != offer.AssetType)
        {
            return false;
        }

        if (rule.FromTask.HasValue && rule.FromTask.Value != offer.FromTask)
        {
            return false;
        }

        return true;
    }

    private async Task TryLoadInventoryOfferPoliciesFromConfiguredFileAsync(CancellationToken cancellationToken)
    {
        var configuredPath = ResolvePolicyFilePath(null);
        if (configuredPath == null)
        {
            return;
        }

        if (!File.Exists(configuredPath))
        {
            return;
        }

        var result = await LoadInventoryOfferPoliciesFromFileAsync(configuredPath, replaceExisting: true, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.Ok
            ? $"[inventory-offer] loaded policy rules from {configuredPath}: {result.Message}"
            : $"[inventory-offer] failed to load policy rules from {configuredPath}: {result.Message}");
    }

    private string? ResolvePolicyFilePath(string? overridePath)
    {
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? _options.InventoryOfferPolicyFile
            : overridePath;

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.GetFullPath(path.Trim());
    }

    private void TryAutoSaveInventoryOfferPolicies()
    {
        if (!_options.InventoryOfferPolicyAutoSave)
        {
            return;
        }

        var path = ResolvePolicyFilePath(null);
        if (path == null)
        {
            return;
        }

        try
        {
            var model = BuildPolicyFileModel();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(model, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[inventory-offer] auto-save failed: {ex.Message}");
        }
    }

    private async Task<BotToolResult> SaveInventoryOfferPoliciesToFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            InventoryOfferPolicyFileModel model;
            lock (_inventoryOfferLock)
            {
                model = BuildPolicyFileModel();
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(model, JsonOptions);
            await File.WriteAllTextAsync(fullPath, json, cancellationToken).ConfigureAwait(false);
            return BotToolResult.OkResult($"Saved {model.Rules.Count} inventory-offer policy rule(s) to {fullPath}.");
        }
        catch (Exception ex)
        {
            return BotToolResult.Fail($"Failed to save policy rules: {ex.Message}");
        }
    }

    private async Task<BotToolResult> LoadInventoryOfferPoliciesFromFileAsync(string fullPath, bool replaceExisting, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return BotToolResult.Fail($"Policy file does not exist: {fullPath}");
            }

            var json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var model = JsonSerializer.Deserialize<InventoryOfferPolicyFileModel>(json, JsonOptions);
            if (model == null)
            {
                return BotToolResult.Fail("Policy file is empty or invalid JSON.");
            }

            var loaded = new List<InventoryOfferPolicyRule>();
            foreach (var rule in model.Rules)
            {
                UUID? senderAgentId = null;
                if (!string.IsNullOrWhiteSpace(rule.SenderAgentId))
                {
                    if (!UUID.TryParse(rule.SenderAgentId, out var parsedSender))
                    {
                        return BotToolResult.Fail($"Invalid senderAgentId in policy file: {rule.SenderAgentId}");
                    }

                    senderAgentId = parsedSender;
                }

                UUID? destinationFolderId = null;
                if (!string.IsNullOrWhiteSpace(rule.DestinationFolderId))
                {
                    if (!UUID.TryParse(rule.DestinationFolderId, out var parsedFolder))
                    {
                        return BotToolResult.Fail($"Invalid destinationFolderId in policy file: {rule.DestinationFolderId}");
                    }

                    destinationFolderId = parsedFolder;
                }

                AssetType? assetType = null;
                if (!string.IsNullOrWhiteSpace(rule.AssetType))
                {
                    if (!TryParseAssetType(rule.AssetType, out var parsedAssetType, out var assetTypeError))
                    {
                        return BotToolResult.Fail($"Invalid assetType in policy file: {assetTypeError}");
                    }

                    assetType = parsedAssetType;
                }

                var normalizedAction = (rule.Action ?? string.Empty).Trim().ToLowerInvariant();
                if (normalizedAction != "accept" && normalizedAction != "decline")
                {
                    return BotToolResult.Fail($"Invalid action in policy file: {rule.Action}");
                }

                loaded.Add(new InventoryOfferPolicyRule(
                    Id: 0,
                    Name: string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name.Trim(),
                    Action: normalizedAction,
                    SenderAgentId: senderAgentId,
                    SenderNameContains: string.IsNullOrWhiteSpace(rule.SenderNameContains) ? null : rule.SenderNameContains.Trim(),
                    AssetType: assetType,
                    FromTask: rule.FromTask,
                    DestinationFolderId: destinationFolderId));
            }

            lock (_inventoryOfferLock)
            {
                if (replaceExisting)
                {
                    _inventoryOfferPolicyRules.Clear();
                }

                foreach (var rule in loaded)
                {
                    _inventoryOfferPolicyRules.Add(rule with { Id = ++_nextInventoryOfferRuleId });
                }
            }

            if (_options.InventoryOfferPolicyAutoSave)
            {
                TryAutoSaveInventoryOfferPolicies();
            }

            return BotToolResult.OkResult($"Loaded {loaded.Count} policy rule(s) from {fullPath}. replaceExisting={replaceExisting}.");
        }
        catch (Exception ex)
        {
            return BotToolResult.Fail($"Failed to load policy rules: {ex.Message}");
        }
    }

    private InventoryOfferPolicyFileModel BuildPolicyFileModel()
    {
        lock (_inventoryOfferLock)
        {
            var persisted = _inventoryOfferPolicyRules
                .Select(rule => new InventoryOfferPolicyRulePersisted(
                    Name: rule.Name,
                    Action: rule.Action,
                    SenderAgentId: rule.SenderAgentId?.ToString(),
                    SenderNameContains: rule.SenderNameContains,
                    AssetType: rule.AssetType?.ToString(),
                    FromTask: rule.FromTask,
                    DestinationFolderId: rule.DestinationFolderId?.ToString()))
                .ToList();

            return new InventoryOfferPolicyFileModel(1, persisted);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static async Task<ScriptRunningReplyEventArgs?> WaitForScriptRunningReplyAsync(
        GridClient client,
        UUID objectId,
        UUID scriptItemId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ScriptRunningReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, ScriptRunningReplyEventArgs e)
        {
            if (e.ObjectID == objectId && e.ScriptID == scriptItemId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Inventory.ScriptRunningReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Inventory.ScriptRunningReply -= Handler;
        }
    }

    private static async Task<TaskItemReceivedEventArgs?> WaitForTaskItemReceivedAsync(GridClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<TaskItemReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, TaskItemReceivedEventArgs e)
        {
            tcs.TrySetResult(e);
        }

        client.Inventory.TaskItemReceived += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Inventory.TaskItemReceived -= Handler;
        }
    }

    private static async Task<InventoryItem?> ResolveInventoryItemAsync(GridClient client, UUID itemId, CancellationToken cancellationToken)
    {
        var store = client.Inventory.Store;
        if (store != null && store.TryGetValue(itemId, out var node) && node is InventoryItem stored)
        {
            return stored;
        }

        return await client.Inventory.FetchItemAsync(itemId, client.Self.AgentID, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BotToolResult> AppearanceWearWearableItemAsync(UUID itemId, bool replaceExistingSlot, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var item = await ResolveInventoryItemAsync(client, itemId, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemId} was not found.");
            }

            var resolved = ResolveLinkedInventoryItem(client.Inventory.Store, item);
            if (resolved is not InventoryWearable wearable)
            {
                return BotToolResult.Fail(
                    $"Inventory item {resolved.UUID} ('{resolved.Name}') is not a wearable (assetType={resolved.AssetType}, inventoryType={resolved.InventoryType}).");
            }

            // Use COF operations so the wearable is persisted in Current Outfit links and
            // does not get dropped by subsequent outfit synchronization.
            using var cof = new LibreMetaverse.Appearance.CurrentOutfitFolder(client);
            await cof.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
            await cof.AddToOutfitAsync(wearable, replace: replaceExistingSlot, cancellationToken: token).ConfigureAwait(false);
            return BotToolResult.OkResult(
                $"Wear request sent for wearable '{wearable.Name}' ({wearable.UUID}), type={wearable.WearableType}, replace={replaceExistingSlot} via COF.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, InventoryFolder? Folder, InventoryItem? AttachmentItem, InventoryItem? AlphaItem, string? Error)> ResolveCubeBotIarItemsAsync(
        GridClient client,
        CancellationToken cancellationToken)
    {
        const string folderName = "Cube Bot IAR";
        const string attachmentName = "The Cube Bot";
        const string alphaName = "Full Body Alpha";

        var rootFolder = client.Inventory.Store?.RootFolder;
        if (rootFolder == null)
        {
            return (false, null, null, null, "Inventory root folder is not initialized.");
        }

        var folders = new List<InventoryFolder>();
        var items = new List<InventoryItem>();
        await client.Inventory.GetInventoryRecursiveAsync(rootFolder.UUID, client.Self.AgentID, folders, items, cancellationToken).ConfigureAwait(false);

        var cubeFolder = folders.FirstOrDefault(f => string.Equals(f.Name?.Trim(), folderName, StringComparison.OrdinalIgnoreCase));
        if (cubeFolder == null)
        {
            return (false, null, null, null, $"Required inventory folder '{folderName}' was not found. Import Cube-Bot-IAR.iar first.");
        }

        var descendantFolderIds = new HashSet<UUID> { cubeFolder.UUID };
        var pending = new Queue<UUID>();
        pending.Enqueue(cubeFolder.UUID);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var child in folders.Where(f => f.ParentUUID == current))
            {
                if (descendantFolderIds.Add(child.UUID))
                {
                    pending.Enqueue(child.UUID);
                }
            }
        }

        var itemsInCubeFolder = items
            .Where(i => descendantFolderIds.Contains(i.ParentUUID))
            .Select(i => ResolveLinkedInventoryItem(client.Inventory.Store, i))
            .ToList();

        var attachmentItem = itemsInCubeFolder
            .FirstOrDefault(i => string.Equals(i.Name?.Trim(), attachmentName, StringComparison.OrdinalIgnoreCase));
        if (attachmentItem == null)
        {
            return (false, cubeFolder, null, null, $"Attachment '{attachmentName}' was not found in '{folderName}'.");
        }

        var alphaItem = itemsInCubeFolder
            .FirstOrDefault(i => string.Equals(i.Name?.Trim(), alphaName, StringComparison.OrdinalIgnoreCase));
        if (alphaItem == null)
        {
            return (false, cubeFolder, attachmentItem, null, $"Wearable '{alphaName}' was not found in '{folderName}'.");
        }

        return (true, cubeFolder, attachmentItem, alphaItem, null);
    }

    private static InventoryItem ResolveLinkedInventoryItem(Inventory? store, InventoryItem item)
    {
        if (item.IsLink() && store != null && store.TryGetValue(item.ResolvedItemID, out var linked) && linked is InventoryItem linkedItem)
        {
            return linkedItem;
        }

        return item;
    }

    private static bool TryFindAttachedObjectForInventoryItem(
        GridClient client,
        UUID inventoryItemId,
        out UUID attachedObjectId,
        out uint attachedLocalId)
    {
        attachedObjectId = UUID.Zero;
        attachedLocalId = 0;

        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return false;
        }

        foreach (var prim in sim.ObjectsPrimitives.Values)
        {
            if (prim == null || prim.NameValues == null || !prim.NameValues.Any())
            {
                continue;
            }

            foreach (var nameValue in prim.NameValues)
            {
                if (!nameValue.Name.Equals("AttachItemID", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var raw = nameValue.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(raw) && UUID.TryParse(raw, out var attachedItemId) && attachedItemId == inventoryItemId)
                {
                    attachedObjectId = prim.ID;
                    attachedLocalId = prim.LocalID;
                    return true;
                }
            }
        }

        return false;
    }

    private static AttachmentTransformResult BuildAttachmentTransformResult(
        UUID itemId,
        UUID objectId,
        uint localId,
        Primitive prim,
        bool requestedUpdate,
        string message)
    {
        prim.Rotation.GetEulerAngles(out var roll, out var pitch, out var yaw);
        return AttachmentTransformResult.OkResult(
            itemId.ToString(),
            objectId.ToString(),
            localId,
            prim.PrimData.AttachmentPoint.ToString(),
            prim.Position.X,
            prim.Position.Y,
            prim.Position.Z,
            prim.Scale.X,
            prim.Scale.Y,
            prim.Scale.Z,
            roll * Utils.RAD_TO_DEG,
            pitch * Utils.RAD_TO_DEG,
            yaw * Utils.RAD_TO_DEG,
            requestedUpdate,
            message);
    }

    private bool TryGetPinnedBridgeObjectInCurrentSim(out UUID objectId, out uint localId)
    {
        objectId = UUID.Zero;
        localId = 0;

        UUID pinnedObjectId;
        lock (_dialogBridgeTrustLock)
        {
            pinnedObjectId = _trustedDialogBridgeObjectId;
        }

        if (pinnedObjectId == UUID.Zero)
        {
            return false;
        }

        var client = _client;
        var sim = client?.Network.CurrentSim;
        if (sim == null)
        {
            return false;
        }

        foreach (var prim in sim.ObjectsPrimitives.Values)
        {
            if (prim.ID != pinnedObjectId)
            {
                continue;
            }

            objectId = pinnedObjectId;
            localId = prim.LocalID;
            return true;
        }

        return false;
    }

    private static InventoryEntry ToInventoryEntry(InventoryBase entry)
    {
        if (entry is InventoryFolder folder)
        {
            return new InventoryEntry(
                folder.UUID.ToString(),
                folder.ParentUUID.ToString(),
                folder.Name,
                "folder",
                AssetType.Folder.ToString(),
                InventoryType.Folder.ToString());
        }

        if (entry is InventoryItem item)
        {
            return new InventoryEntry(
                item.UUID.ToString(),
                item.ParentUUID.ToString(),
                item.Name,
                "item",
                item.AssetType.ToString(),
                item.InventoryType.ToString());
        }

        return new InventoryEntry(
            entry.UUID.ToString(),
            entry.ParentUUID.ToString(),
            entry.Name,
            "unknown",
            AssetType.Unknown.ToString(),
            InventoryType.Unknown.ToString());
    }

    private static async Task<byte[]> ReadBinarySourceAsync(string source, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var fullPath = Path.GetFullPath(source);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Source file '{fullPath}' does not exist.", fullPath);
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string? Base64, string? FilePath)> BuildDownloadPayloadAsync(
        byte[] data,
        DownloadOutputMode mode,
        string? fileNameHint,
        AssetType assetType,
        CancellationToken cancellationToken)
    {
        var includeInline = mode is DownloadOutputMode.Base64 or DownloadOutputMode.Both;
        var includeFile = mode is DownloadOutputMode.TempFile or DownloadOutputMode.Both;

        string? base64 = null;
        string? filePath = null;

        if (includeInline)
        {
            base64 = Convert.ToBase64String(data);
        }

        if (includeFile)
        {
            var ext = GuessFileExtension(assetType);
            var safeName = string.IsNullOrWhiteSpace(fileNameHint)
                ? $"asset-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
                : string.Concat(fileNameHint.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));

            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = $"asset-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            }

            filePath = Path.Combine(Path.GetTempPath(), safeName + ext);
            await File.WriteAllBytesAsync(filePath, data, cancellationToken).ConfigureAwait(false);
        }

        return (base64, filePath);
    }

    private static string GuessFileExtension(AssetType assetType)
    {
        return assetType switch
        {
            AssetType.Texture => ".jp2",
            AssetType.Notecard => ".txt",
            AssetType.LSLText => ".lsl",
            AssetType.Animation => ".anim",
            AssetType.Sound => ".ogg",
            AssetType.Mesh => ".mesh",
            _ => ".bin"
        };
    }

    private static bool TryParseOutputMode(string raw, out DownloadOutputMode mode, out string error)
    {
        error = string.Empty;
        var normalized = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = "both";
        }

        switch (normalized)
        {
            case "base64":
            case "inline":
                mode = DownloadOutputMode.Base64;
                return true;
            case "tempfile":
            case "file":
            case "path":
                mode = DownloadOutputMode.TempFile;
                return true;
            case "both":
                mode = DownloadOutputMode.Both;
                return true;
            default:
                mode = DownloadOutputMode.Both;
                error = "outputMode must be one of: both, base64, tempfile.";
                return false;
        }
    }

    private static bool TryParseAssetType(string raw, out AssetType assetType, out string error)
    {
        assetType = AssetType.Unknown;
        error = string.Empty;

        var normalized = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "assetType is required.";
            return false;
        }

        if (Enum.TryParse<AssetType>(normalized, true, out assetType) && assetType != AssetType.Unknown)
        {
            return true;
        }

        var parsedViaUtils = Utils.StringToAssetType(normalized);
        if (parsedViaUtils != AssetType.Unknown)
        {
            assetType = parsedViaUtils;
            return true;
        }

        error = $"Unsupported assetType '{raw}'.";
        return false;
    }

    private static bool TryParseInventoryType(string raw, out InventoryType inventoryType, out string error)
    {
        inventoryType = InventoryType.Unknown;
        error = string.Empty;

        var normalized = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "inventoryType is required.";
            return false;
        }

        if (Enum.TryParse<InventoryType>(normalized, true, out inventoryType) && inventoryType != InventoryType.Unknown)
        {
            return true;
        }

        var parsedViaUtils = Utils.StringToInventoryType(normalized);
        if (parsedViaUtils != InventoryType.Unknown)
        {
            inventoryType = parsedViaUtils;
            return true;
        }

        error = $"Unsupported inventoryType '{raw}'.";
        return false;
    }

    private sealed record InventoryOfferPolicyRule(
        int Id,
        string Name,
        string Action,
        UUID? SenderAgentId,
        string? SenderNameContains,
        AssetType? AssetType,
        bool? FromTask,
        UUID? DestinationFolderId);

    private sealed record InventoryOfferPolicyFileModel(int Version, IReadOnlyList<InventoryOfferPolicyRulePersisted> Rules);

    private sealed record InventoryOfferPolicyRulePersisted(
        string Name,
        string Action,
        string? SenderAgentId,
        string? SenderNameContains,
        string? AssetType,
        bool? FromTask,
        string? DestinationFolderId);

    private enum DownloadOutputMode
    {
        Base64,
        TempFile,
        Both
    }
}

internal sealed record InventoryEntry(
    string Id,
    string ParentId,
    string Name,
    string Kind,
    string AssetType,
    string InventoryType);

internal sealed record InventoryQueryResult(bool Ok, string Message, IReadOnlyList<InventoryEntry> Entries)
{
    public static InventoryQueryResult OkResult(IReadOnlyList<InventoryEntry> entries, string message)
        => new(true, message, entries);

    public static InventoryQueryResult FailResult(string message)
        => new(false, message, Array.Empty<InventoryEntry>());
}

internal sealed record AssetTransferResult(bool Ok, string Message, string? ItemId, string? AssetId, int Bytes)
{
    public static AssetTransferResult OkResult(string itemId, string assetId, int bytes, string message)
        => new(true, message, itemId, assetId, bytes);

    public static AssetTransferResult FailResult(string message)
        => new(false, message, null, null, 0);
}

internal sealed record AssetDownloadResult(
    bool Ok,
    string Message,
    string? Base64,
    string? FilePath,
    int Bytes,
    string? AssetId,
    string? AssetType)
{
    public static AssetDownloadResult OkResult(string? base64, string? filePath, int bytes, string? assetId, string? assetType, string message)
        => new(true, message, base64, filePath, bytes, assetId, assetType);

    public static AssetDownloadResult FailResult(string message)
        => new(false, message, null, null, 0, null, null);
}

internal sealed record InventoryOfferPolicyRuleInfo(
    int Id,
    string Name,
    string Action,
    string? SenderAgentId,
    string? SenderNameContains,
    string? AssetType,
    bool? FromTask,
    string? DestinationFolderId);

internal sealed record InventoryOfferPolicyResult(bool Ok, string Message, IReadOnlyList<InventoryOfferPolicyRuleInfo> Rules)
{
    public static InventoryOfferPolicyResult OkResult(IReadOnlyList<InventoryOfferPolicyRuleInfo> rules, string message)
        => new(true, message, rules);

    public static InventoryOfferPolicyResult FailResult(string message)
        => new(false, message, Array.Empty<InventoryOfferPolicyRuleInfo>());
}

internal sealed record InventoryOfferEventInfo(
    int EventId,
    string TimestampUtc,
    string FromAgentId,
    string FromName,
    string AssetType,
    bool FromTask,
    string ObjectId,
    string Message,
    string Decision,
    int? MatchedRuleId,
    string? MatchedRuleName,
    string DestinationFolderId);

internal sealed record InventoryOfferHistoryResult(bool Ok, string Message, IReadOnlyList<InventoryOfferEventInfo> Offers)
{
    public static InventoryOfferHistoryResult OkResult(IReadOnlyList<InventoryOfferEventInfo> offers, string message)
        => new(true, message, offers);

    public static InventoryOfferHistoryResult FailResult(string message)
        => new(false, message, Array.Empty<InventoryOfferEventInfo>());
}

internal sealed record WearableInfo(string ItemId, string AssetId, string WearableType, string AssetType);

internal sealed record AttachmentInfo(string ItemId, string AttachmentPoint, string? ObjectId, uint? ObjectLocalId);

internal sealed record OutfitCategoryResolutionInfo(
    string Category,
    string Action,
    int RequestedCount,
    int CurrentlyWornCount,
    string Notes);

internal sealed record AppearanceWearFolderResult(
    bool Ok,
    string Message,
    bool ReplaceItems,
    int SourceEntryCount,
    int WearableCandidateCount,
    IReadOnlyList<OutfitCategoryResolutionInfo> CategoryResolutions)
{
    public static AppearanceWearFolderResult OkResult(
        bool replaceItems,
        int sourceEntryCount,
        int wearableCandidateCount,
        IReadOnlyList<OutfitCategoryResolutionInfo> categoryResolutions,
        string message)
        => new(true, message, replaceItems, sourceEntryCount, wearableCandidateCount, categoryResolutions);

    public static AppearanceWearFolderResult FailResult(bool replaceItems, string message)
        => new(false, message, replaceItems, 0, 0, Array.Empty<OutfitCategoryResolutionInfo>());
}

internal sealed record OutfitSaveResult(bool Ok, string Message, string? FolderId, int LinkedCount, int FailedCount)
{
    public static OutfitSaveResult OkResult(string folderId, int linkedCount, int failedCount, string message)
        => new(true, message, folderId, linkedCount, failedCount);

    public static OutfitSaveResult FailResult(string message)
        => new(false, message, null, 0, 0);
}

internal sealed record WearableDirectControlResult(
    bool Ok,
    string Message,
    string? WearableType,
    int WornCount,
    int RemovedCount,
    IReadOnlyList<string> RemovedItemIds)
{
    public static WearableDirectControlResult OkResult(
        string? wearableType,
        int wornCount,
        int removedCount,
        IReadOnlyList<string> removedItemIds,
        string message)
        => new(true, message, wearableType, wornCount, removedCount, removedItemIds);

    public static WearableDirectControlResult FailResult(string message)
        => new(false, message, null, 0, 0, Array.Empty<string>());
}

internal sealed record AttachmentPointMappingInfo(string ItemId, string ItemName, string AttachmentPoint);

internal sealed record AttachmentPointMappingResult(bool Ok, string Message, IReadOnlyList<AttachmentPointMappingInfo> Mappings)
{
    public static AttachmentPointMappingResult OkResult(IReadOnlyList<AttachmentPointMappingInfo> mappings, string message)
        => new(true, message, mappings);

    public static AttachmentPointMappingResult FailResult(string message)
        => new(false, message, Array.Empty<AttachmentPointMappingInfo>());
}

internal sealed record AttachmentObjectResolutionResult(
    bool Ok,
    string Message,
    string? ItemId,
    string? ObjectId,
    uint? ObjectLocalId,
    string? AttachmentPoint)
{
    public static AttachmentObjectResolutionResult OkResult(
        string itemId,
        string objectId,
        uint objectLocalId,
        string? attachmentPoint,
        string message)
        => new(true, message, itemId, objectId, objectLocalId, attachmentPoint);

    public static AttachmentObjectResolutionResult FailResult(string message)
        => new(false, message, null, null, null, null);
}

internal sealed record AttachmentTransformResult(
    bool Ok,
    string Message,
    string? ItemId,
    string? ObjectId,
    uint LocalId,
    string? AttachmentPoint,
    float? PositionX,
    float? PositionY,
    float? PositionZ,
    float? ScaleX,
    float? ScaleY,
    float? ScaleZ,
    float? RollDegrees,
    float? PitchDegrees,
    float? YawDegrees,
    bool RequestedUpdate)
{
    public static AttachmentTransformResult OkResult(
        string itemId,
        string objectId,
        uint localId,
        string attachmentPoint,
        float positionX,
        float positionY,
        float positionZ,
        float scaleX,
        float scaleY,
        float scaleZ,
        float rollDegrees,
        float pitchDegrees,
        float yawDegrees,
        bool requestedUpdate,
        string message)
        => new(
            true,
            message,
            itemId,
            objectId,
            localId,
            attachmentPoint,
            positionX,
            positionY,
            positionZ,
            scaleX,
            scaleY,
            scaleZ,
            rollDegrees,
            pitchDegrees,
            yawDegrees,
            requestedUpdate);

    public static AttachmentTransformResult FailResult(string message)
        => new(false, message, null, null, 0, null, null, null, null, null, null, null, null, null, null, false);
}

internal sealed record AppearanceStateResult(
    bool Ok,
    string Message,
    IReadOnlyList<WearableInfo> Wearables,
    IReadOnlyList<AttachmentInfo> Attachments)
{
    public static AppearanceStateResult OkResult(IReadOnlyList<WearableInfo> wearables, IReadOnlyList<AttachmentInfo> attachments, string message)
        => new(true, message, wearables, attachments);

    public static AppearanceStateResult FailResult(string message)
        => new(false, message, Array.Empty<WearableInfo>(), Array.Empty<AttachmentInfo>());
}

internal sealed record AppearanceVisualParamInfo(
    int ParamId,
    string Name,
    string? Wearable,
    int Group,
    float MinValue,
    float MaxValue,
    float DefaultValue,
    float CurrentValue,
    bool Editable);

internal sealed record AppearanceVisualParamsResult(bool Ok, string Message, IReadOnlyList<AppearanceVisualParamInfo> Params)
{
    public static AppearanceVisualParamsResult OkResult(IReadOnlyList<AppearanceVisualParamInfo> parameters, string message)
        => new(true, message, parameters);

    public static AppearanceVisualParamsResult FailResult(string message)
        => new(false, message, Array.Empty<AppearanceVisualParamInfo>());
}

internal sealed record AppearanceVisualParamSetResult(
    bool Ok,
    string Message,
    int? ParamId,
    string? Name,
    string? Wearable,
    float? PreviousValue,
    float? RequestedValue,
    float? AppliedValue,
    float? MinValue,
    float? MaxValue,
    bool Clamped,
    bool Changed)
{
    public static AppearanceVisualParamSetResult OkResult(
        int paramId,
        string name,
        string? wearable,
        float previousValue,
        float requestedValue,
        float appliedValue,
        float minValue,
        float maxValue,
        bool clamped,
        bool changed,
        string message)
        => new(true, message, paramId, name, wearable, previousValue, requestedValue, appliedValue, minValue, maxValue, clamped, changed);

    public static AppearanceVisualParamSetResult FailResult(string message)
        => new(false, message, null, null, null, null, null, null, null, null, false, false);
}

internal sealed record AppearanceBakeTextureInfo(
    string BakeType,
    string TextureIndex,
    int TextureIndexValue,
    string TextureId,
    bool HasTexture,
    bool IsDefaultTexture);

internal sealed record AppearanceBakeDiagnosticsResult(
    bool Ok,
    string Message,
    bool ServerBakingRegion,
    bool AppearanceManagerBusy,
    int VisualParamBytes,
    int VisualParamCount,
    int NonDefaultVisualParamCount,
    bool CacheProbeRequested,
    bool CacheProbeCompleted,
    int CacheProbeElapsedMs,
    IReadOnlyList<AppearanceBakeTextureInfo> BakedTextures)
{
    public static AppearanceBakeDiagnosticsResult OkResult(
        bool serverBakingRegion,
        bool appearanceManagerBusy,
        int visualParamBytes,
        int visualParamCount,
        int nonDefaultVisualParamCount,
        bool cacheProbeRequested,
        bool cacheProbeCompleted,
        int cacheProbeElapsedMs,
        IReadOnlyList<AppearanceBakeTextureInfo> bakedTextures,
        string message)
        => new(
            true,
            message,
            serverBakingRegion,
            appearanceManagerBusy,
            visualParamBytes,
            visualParamCount,
            nonDefaultVisualParamCount,
            cacheProbeRequested,
            cacheProbeCompleted,
            cacheProbeElapsedMs,
            bakedTextures);

    public static AppearanceBakeDiagnosticsResult FailResult(string message)
        => new(false, message, false, false, 0, 0, 0, false, false, 0, Array.Empty<AppearanceBakeTextureInfo>());
}

internal sealed record ScriptUpdateResult(
    bool Ok,
    string Message,
    string? ItemId,
    string? AssetId,
    int SourceBytes,
    string UploadStatus,
    bool? CompileSuccess,
    IReadOnlyList<string> CompileMessages)
{
    public static ScriptUpdateResult OkResult(
        string itemId,
        string assetId,
        int sourceBytes,
        string uploadStatus,
        bool? compileSuccess,
        IReadOnlyList<string> compileMessages,
        string message)
        => new(true, message, itemId, assetId, sourceBytes, uploadStatus, compileSuccess, compileMessages);

    public static ScriptUpdateResult FailResult(string message)
        => new(false, message, null, null, 0, string.Empty, null, Array.Empty<string>());
}

internal sealed record ScriptRunningResult(
    bool Ok,
    string Message,
    string? ObjectId,
    string? ScriptItemId,
    bool? Running,
    bool? IsMono)
{
    public static ScriptRunningResult OkResult(string objectId, string scriptItemId, bool running, bool? isMono, string message)
        => new(true, message, objectId, scriptItemId, running, isMono);

    public static ScriptRunningResult FailResult(string message)
        => new(false, message, null, null, null, null);
}

internal sealed record DialogBridgeInstallResult(
    bool Ok,
    string Message,
    uint ObjectLocalId,
    string? ObjectId,
    string? OwnerId,
    string? InventoryScriptItemId,
    string? InventoryScriptAssetId)
{
    public static DialogBridgeInstallResult OkResult(
        uint objectLocalId,
        string? objectId,
        string? ownerId,
        string? inventoryScriptItemId,
        string? inventoryScriptAssetId,
        string message)
        => new(true, message, objectLocalId, objectId, ownerId, inventoryScriptItemId, inventoryScriptAssetId);

    public static DialogBridgeInstallResult FailResult(string message)
        => new(false, message, 0, null, null, null, null);
}
