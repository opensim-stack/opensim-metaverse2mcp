using System.Net.Http;
using System.Text.Json;
using LibreMetaverse;

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

            var attachments = client.Appearance.GetAttachmentsByItemId()
                .Select(a => new AttachmentInfo(a.Key.ToString(), a.Value.ToString()))
                .OrderBy(a => a.AttachmentPoint, StringComparer.Ordinal)
                .ThenBy(a => a.ItemId, StringComparer.Ordinal)
                .ToList();

            return AppearanceStateResult.OkResult(wearables, attachments, "Collected currently worn wearables and attachments.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> AppearanceWearFolderAsync(string folderId, bool replaceItems, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(folderId, out var folderUuid))
        {
            return BotToolResult.Fail("folderId is not a valid UUID.");
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

            await client.Appearance.WearOutfitAsync(resolved, replaceItems).ConfigureAwait(false);
            return BotToolResult.OkResult($"Requested wear outfit from folder {folderUuid} ({resolved.Count} items), replaceItems={replaceItems}.");
        }, cancellationToken).ConfigureAwait(false);
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

            if (item.AssetType != AssetType.LSLText)
            {
                return BotToolResult.Fail($"Item {item.UUID} is not an LSL script (assetType={item.AssetType}).");
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
            var entries = new List<InventoryEntry>();

            if (!store.TryGetValue(folderUuid, out var folderNode) || folderNode is not InventoryFolder folder)
            {
                return InventoryQueryResult.FailResult($"Folder {folderUuid} was not found in local inventory store.");
            }

            entries.Add(ToInventoryEntry(folder));

            if (recursive)
            {
                var folders = new List<InventoryFolder>();
                var items = new List<InventoryItem>();
                await client.Inventory.GetInventoryRecursiveAsync(folderUuid, owner, folders, items, token).ConfigureAwait(false);
                entries.AddRange(folders.Select(ToInventoryEntry));
                entries.AddRange(items.Select(ToInventoryEntry));
            }
            else
            {
                var contents = await client.Inventory
                    .FolderContentsAsync(folderUuid, owner, true, true, InventorySortOrder.ByName, token)
                    .ConfigureAwait(false);
                entries.AddRange(contents.Select(ToInventoryEntry));
            }

            var ordered = entries
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

            if (!sim.ObjectsPrimitives.TryGetValue(objectLocalId, out var prim))
            {
                return InventoryQueryResult.FailResult($"Object localId={objectLocalId} is not present in simulator cache.");
            }

            var objectUuid = prim.ID;
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                if (!UUID.TryParse(objectId, out objectUuid))
                {
                    return InventoryQueryResult.FailResult("objectId is not a valid UUID.");
                }
            }

            var entries = await client.Inventory
                .GetTaskInventoryAsync(objectUuid, objectLocalId, sim, token)
                .ConfigureAwait(false);

            var limit = Math.Clamp(maxResults, 1, 2000);
            var mapped = entries
                .Select(ToInventoryEntry)
                .OrderBy(e => e.Kind, StringComparer.Ordinal)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToList();

            return InventoryQueryResult.OkResult(mapped, $"Returned {mapped.Count} task-inventory entries for object {objectLocalId}.");
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

            if (!sim.ObjectsPrimitives.TryGetValue(objectLocalId, out var prim))
            {
                return BotToolResult.Fail($"Object localId={objectLocalId} is not present in simulator cache.");
            }

            var objectUuid = prim.ID;
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                if (!UUID.TryParse(objectId, out objectUuid))
                {
                    return BotToolResult.Fail("objectId is not a valid UUID.");
                }
            }

            var taskEntries = await client.Inventory
                .GetTaskInventoryAsync(objectUuid, objectLocalId, sim, token)
                .ConfigureAwait(false);

            var taskItem = taskEntries.OfType<InventoryItem>().FirstOrDefault(i => i.UUID == taskItemUuid);
            if (taskItem == null)
            {
                return BotToolResult.Fail($"Task inventory item {taskItemUuid} was not found on object {objectLocalId}.");
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

            client.Inventory.MoveTaskInventory(objectLocalId, taskItem.UUID, destinationFolderUuid, sim);
            return BotToolResult.OkResult(
                $"Requested task-inventory transfer for item {taskItem.UUID} from object {objectLocalId} to folder {destinationFolderUuid}. Server decides copy/move based on permissions.");
        }, cancellationToken).ConfigureAwait(false);
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

    private static async Task<InventoryItem?> ResolveInventoryItemAsync(GridClient client, UUID itemId, CancellationToken cancellationToken)
    {
        var store = client.Inventory.Store;
        if (store != null && store.TryGetValue(itemId, out var node) && node is InventoryItem stored)
        {
            return stored;
        }

        return await client.Inventory.FetchItemAsync(itemId, client.Self.AgentID, cancellationToken).ConfigureAwait(false);
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

internal sealed record AttachmentInfo(string ItemId, string AttachmentPoint);

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
