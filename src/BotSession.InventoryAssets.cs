using System.Net.Http;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Packets;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private readonly object _inventoryOfferLock = new();
    private readonly Queue<InventoryOfferEventInfo> _inventoryOfferHistory = new();
    private readonly List<InventoryOfferPolicyRule> _inventoryOfferPolicyRules = new();
    private int _nextInventoryOfferRuleId;
    private int _nextInventoryOfferEventId;

    private const int MaxInventoryOfferHistory = 200;
    private const int StartupSetupProvisionStateIdle = 0;
    private const int StartupSetupProvisionStateRunning = 1;
    private const int StartupSetupProvisionStateCompleted = 2;
    private const int StartupSetupProvisionArtificialDelayMs = 10_000;
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

    public async Task<AppearanceStateResult> AppearanceListWornAsync(
        bool includeAttachmentsAndWearables,
        bool includeCurrentOutfit,
        CancellationToken cancellationToken)
    {
        if (!includeAttachmentsAndWearables && !includeCurrentOutfit)
        {
            return AppearanceStateResult.FailResult("At least one source must be enabled: includeAttachmentsAndWearables or includeCurrentOutfit.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestAgentWornAsync(token).ConfigureAwait(false);

            var wearables = await CollectWornWearablesAsync(
                    client,
                    includeAttachmentsAndWearables,
                    includeCurrentOutfit,
                    token)
                .ConfigureAwait(false);

            var staleAttachmentMappings = 0;
            var attachments = (await CollectAttachmentPointMappingsAsync(
                        client,
                        token,
                        includeAttachmentsAndWearables,
                        includeCurrentOutfit)
                    .ConfigureAwait(false))
                .Select(a =>
                {
                    if (!TryFindAttachedObjectForInventoryItem(client, a.Key, out var objectId, out var localId))
                    {
                        staleAttachmentMappings++;
                        return null;
                    }

                    return new AttachmentInfo(
                        a.Key.ToString(),
                        a.Value.ToString(),
                        objectId.ToString(),
                        localId);
                })
                .Where(a => a != null)
                .Select(a => a!)
                .OrderBy(a => a.AttachmentPoint, StringComparer.Ordinal)
                .ThenBy(a => a.ItemId, StringComparer.Ordinal)
                .ToList();

            var mode = includeAttachmentsAndWearables && includeCurrentOutfit
                ? "merged mode (runtime + COF)"
                : includeAttachmentsAndWearables
                    ? "runtime-only mode"
                    : "COF-only mode";
            var message = staleAttachmentMappings > 0
                ? $"Collected currently worn wearables and live attachments in {mode} (ignored {staleAttachmentMappings} stale attachment mapping(s))."
                : $"Collected currently worn wearables and live attachments in {mode}.";
            return AppearanceStateResult.OkResult(wearables, attachments, message);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> AppearanceGetCurrentOutfitAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var cofManager = GetSharedCurrentOutfitFolder(client);
            var currentLinks = await cofManager.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
            var cofFolder = cofManager.COF;

            InventoryItem? outfitLink = null;
            foreach (var link in currentLinks)
            {
                if (IsCurrentOutfitFolderLink(link))
                {
                    outfitLink = link;
                    break;
                }
            }

            var outfitFolderId = UUID.Zero;
            if (outfitLink != null)
            {
                if (outfitLink.AssetUUID != UUID.Zero)
                {
                    outfitFolderId = outfitLink.AssetUUID;
                }
                else if (outfitLink.ResolvedItemID != UUID.Zero)
                {
                    outfitFolderId = outfitLink.ResolvedItemID;
                }
            }

            var store = client.Inventory.Store;
            string? outfitFolderName = null;
            if (outfitFolderId != UUID.Zero
                && store != null
                && store.TryGetValue(outfitFolderId, out var maybeFolder)
                && maybeFolder is InventoryFolder resolvedFolder)
            {
                outfitFolderName = resolvedFolder.Name;
            }

            var payload = new
            {
                cofFolderId = cofFolder?.UUID.ToString(),
                cofFolderName = cofFolder?.Name,
                currentOutfitLinkItemId = outfitLink?.UUID.ToString(),
                currentOutfitLinkItemName = outfitLink?.Name,
                currentOutfitFolderId = outfitFolderId == UUID.Zero ? null : outfitFolderId.ToString(),
                currentOutfitFolderName = outfitFolderName,
                cofLinkCount = currentLinks.Count,
                hasCurrentOutfitFolderLink = outfitLink != null
            };

            var message = outfitLink == null
                ? "Resolved COF links, but no outfit folder-link is currently present."
                : outfitFolderName == null
                    ? "Resolved current outfit folder-link, but folder metadata is not in local cache."
                    : $"Resolved current outfit folder '{outfitFolderName}' ({outfitFolderId}).";

            return DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppearanceWearFolderResult> AppearanceWearFolderAsync(string folderId, bool replaceItems, bool removeExistingItems, CancellationToken cancellationToken)
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
            var resolvedItemsByParent = new List<(InventoryItem Item, UUID ParentFolderId)>(entries.Count);
            foreach (var entry in entries)
            {
                if (entry is InventoryItem item && item.IsLink() && store != null && store.TryGetValue(item.ResolvedItemID, out var linked))
                {
                    resolved.Add(linked);
                    if (linked is InventoryItem linkedItem)
                    {
                        resolvedItemsByParent.Add((linkedItem, item.ParentUUID));
                    }
                }
                else
                {
                    resolved.Add(entry);
                    if (entry is InventoryItem directItem)
                    {
                        resolvedItemsByParent.Add((directItem, directItem.ParentUUID));
                    }
                }
            }

            var companionNotecardsByFolderAndName = resolvedItemsByParent
                .Where(pair => IsNotecardInventoryItem(pair.Item))
                .GroupBy(pair => (pair.ParentFolderId, NormalizeCompanionIniLookupKey(pair.Item.Name)))
                .ToDictionary(group => group.Key, group => group.First().Item);

            var wearTargets = new List<InventoryBase>(resolved.Count);
            var pendingAttachmentTransforms = new List<(InventoryItem Item, WearAttachmentIniConfig Config)>();
            foreach (var resolvedEntry in resolvedItemsByParent)
            {
                token.ThrowIfCancellationRequested();

                var item = resolvedEntry.Item;
                if (IsNotecardInventoryItem(item))
                {
                    // Never attempt to wear notecards from outfit folders.
                    continue;
                }

                if (item is not InventoryWearable && item is not InventoryObject)
                {
                    continue;
                }

                if (item is InventoryObject attachment)
                {
                    var config = await TryResolveWearAttachmentIniConfigAsync(
                            client,
                            item,
                            resolvedEntry.ParentFolderId,
                            companionNotecardsByFolderAndName,
                            token)
                        .ConfigureAwait(false);
                    if (config != null)
                    {
                        if (config.AttachPoint.HasValue)
                        {
                            attachment.AttachPoint = config.AttachPoint.Value;
                        }

                        if (config.HasTransformValues)
                        {
                            pendingAttachmentTransforms.Add((item, config));
                        }
                    }
                }

                wearTargets.Add(item);
            }

            var resolvedItems = wearTargets.OfType<InventoryItem>().ToList();
            if (resolvedItems.Count == 0)
            {
                return AppearanceWearFolderResult.FailResult(
                    replaceItems,
                    $"No wearable or attachment items were found in folder {folderUuid}. sourceEntries={entries.Count}. Nothing to wear.");
            }

            string removeSummary = string.Empty;
            if (removeExistingItems)
            {
                var clear = await RemoveCurrentlyWornItemsAsync(client, token).ConfigureAwait(false);
                removeSummary = $", removedWearables={clear.RemovedWearableCount}, detachedAttachments={clear.DetachedAttachmentCount}";
            }

            var categoryResolutions = await BuildOutfitCategoryResolutionsAsync(client, resolvedItems, replaceItems, token).ConfigureAwait(false);
            await client.Appearance.WearOutfitAsync(wearTargets, replaceItems).ConfigureAwait(false);

            foreach (var pending in pendingAttachmentTransforms)
            {
                token.ThrowIfCancellationRequested();
                await TryApplyWearAttachmentIniTransformAsync(client, pending.Item, pending.Config, token).ConfigureAwait(false);
            }

            var overlapCount = categoryResolutions.Count(r => r.CurrentlyWornCount > 0);
            var mode = replaceItems ? "replace" : "add";
            return AppearanceWearFolderResult.OkResult(
                replaceItems,
                entries.Count,
                resolvedItems.Count,
                categoryResolutions,
                $"Requested {mode} wearables/attachments from folder {folderUuid}: sourceEntries={entries.Count}, wearableCandidates={resolvedItems.Count}, overlappingCategories={overlapCount}, removeExistingItems={removeExistingItems}{removeSummary}, wearPath=appearance-only.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppearanceWearFolderResult> AppearanceWearOutfitAsync(
        string outfitName,
        CancellationToken cancellationToken)
    {
        var overallStopwatch = Stopwatch.StartNew();
        var normalizedOutfitName = (outfitName ?? string.Empty).Trim();
        Console.WriteLine($"[appearance-wear-outfit] start outfitName='{outfitName ?? ""}' normalized='{normalizedOutfitName}' canceled={cancellationToken.IsCancellationRequested}");
        if (string.IsNullOrWhiteSpace(normalizedOutfitName))
        {
            Console.WriteLine("[appearance-wear-outfit] invalid input: outfitName is empty.");
            return AppearanceWearFolderResult.FailResult(replaceItems: true, "outfitName is required.");
        }

        try
        {
            var result = await ExecuteLockedAsync(async (client, token) =>
            {
                var lockedStopwatch = Stopwatch.StartNew();
                Console.WriteLine("[appearance-wear-outfit] execute-locked begin");

                var store = client.Inventory.Store;
                var root = store?.RootFolder;
                if (store == null || root == null)
                {
                    Console.WriteLine("[appearance-wear-outfit] inventory store/root not initialized");
                    return AppearanceWearFolderResult.FailResult(replaceItems: true, "Inventory store is not initialized.");
                }

                Console.WriteLine($"[appearance-wear-outfit] inventory root ready rootId={root.UUID} rootName='{root.Name}' elapsedMs={lockedStopwatch.ElapsedMilliseconds}");

                if (!TryResolveOutfitsRootFolder(client, store, out var outfitsRootFolder, out var resolveError))
                {
                    Console.WriteLine($"[appearance-wear-outfit] outfits root resolution failed: {resolveError}");
                    return AppearanceWearFolderResult.FailResult(replaceItems: true, resolveError);
                }

                Console.WriteLine($"[appearance-wear-outfit] outfits root resolved folderId={outfitsRootFolder.UUID} name='{outfitsRootFolder.Name}' elapsedMs={lockedStopwatch.ElapsedMilliseconds}");
                Console.WriteLine($"[appearance-wear-outfit] requesting outfit folder contents owner={client.Self.AgentID} recursive=false");
                var folderContentsStopwatch = Stopwatch.StartNew();
                var entries = await client.Inventory
                    .FolderContentsAsync(outfitsRootFolder.UUID, client.Self.AgentID, true, false, InventorySortOrder.ByName, token)
                    .ConfigureAwait(false);
                Console.WriteLine($"[appearance-wear-outfit] outfit folder contents retrieved entries={entries.Count} elapsedMs={folderContentsStopwatch.ElapsedMilliseconds}");

                var folders = entries.OfType<InventoryFolder>().ToList();
                var knownOutfitNames = new HashSet<string>(
                    folders
                        .Select(folder => folder.Name?.Trim())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Cast<string>(),
                    StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"[appearance-wear-outfit] folder candidates under outfits root count={folders.Count}");

                var matches = folders
                    .Where(folder => string.Equals(folder.Name?.Trim(), normalizedOutfitName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                Console.WriteLine($"[appearance-wear-outfit] name match search normalized='{normalizedOutfitName}' matches={matches.Count}");

                if (matches.Count == 0)
                {
                    var knownNames = folders
                        .Select(folder => folder.Name?.Trim())
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .Take(25)
                        .ToList();
                    var knownList = knownNames.Count == 0 ? "none" : string.Join(", ", knownNames);
                    Console.WriteLine($"[appearance-wear-outfit] no outfit name match found. sampleKnown={knownList}");
                    return AppearanceWearFolderResult.FailResult(
                        replaceItems: true,
                        $"Outfit '{normalizedOutfitName}' was not found under '{outfitsRootFolder.Name}'. Known outfit folders: {knownList}.");
                }

                if (matches.Count > 1)
                {
                    var candidates = string.Join(", ", matches.Select(folder => $"{folder.UUID}").OrderBy(id => id, StringComparer.Ordinal));
                    Console.WriteLine($"[appearance-wear-outfit] ambiguous outfit name matches={matches.Count} candidates={candidates}");
                    return AppearanceWearFolderResult.FailResult(
                        replaceItems: true,
                        $"Outfit '{normalizedOutfitName}' is ambiguous under '{outfitsRootFolder.Name}' ({matches.Count} matches). Candidate folder UUIDs: {candidates}.");
                }

                var match = matches[0];
                Console.WriteLine($"[appearance-wear-outfit] selected outfit folderId={match.UUID} name='{match.Name}' elapsedMs={lockedStopwatch.ElapsedMilliseconds}");

                var cofManager = GetSharedCurrentOutfitFolder(client);
                var cofFolder = cofManager.COF;
                Console.WriteLine($"[appearance-wear-outfit] COF manager ready cofFolderId={cofFolder?.UUID.ToString() ?? "<null>"} cofFolderName='{cofFolder?.Name ?? "<null>"}'");

                if (cofFolder == null)
                {
                    Console.WriteLine("[appearance-wear-outfit] COF is null; forcing COF initialization via GetCurrentOutfitLinksAsync...");
                    var initStopwatch = Stopwatch.StartNew();
                    var bootstrapLinks = await cofManager.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                    cofFolder = cofManager.COF;
                    Console.WriteLine($"[appearance-wear-outfit] COF bootstrap complete links={bootstrapLinks.Count} cofFolderId={cofFolder?.UUID.ToString() ?? "<null>"} elapsedMs={initStopwatch.ElapsedMilliseconds}");
                }

                if (cofFolder == null)
                {
                    Console.WriteLine("[appearance-wear-outfit] COF initialization failed; aborting replace path.");
                    return AppearanceWearFolderResult.FailResult(
                        replaceItems: true,
                        "Current Outfit Folder (COF) is not initialized; cannot replace outfit right now.");
                }

                Console.WriteLine($"[appearance-wear-outfit] collecting wearable/object targets from outfit folderId={match.UUID}");
                var outfitEntries = await client.Inventory
                    .FolderContentsAsync(match.UUID, client.Self.AgentID, true, true, InventorySortOrder.ByName, token)
                    .ConfigureAwait(false);
                Console.WriteLine($"[appearance-wear-outfit] outfit contents loaded entries={outfitEntries.Count}");

                var storeForResolve = client.Inventory.Store;
                var targetItems = new List<InventoryItem>();
                var seenTargetItemIds = new HashSet<UUID>();
                foreach (var entry in outfitEntries)
                {
                    token.ThrowIfCancellationRequested();

                    if (entry is not InventoryItem item)
                    {
                        continue;
                    }

                    var resolved = ResolveLinkedInventoryItem(storeForResolve, item);
                    if (resolved is not InventoryWearable && resolved is not InventoryObject && resolved.AssetType != AssetType.Gesture)
                    {
                        continue;
                    }

                    if (seenTargetItemIds.Add(resolved.UUID))
                    {
                        targetItems.Add(resolved);
                    }
                }

                if (targetItems.Count == 0)
                {
                    Console.WriteLine("[appearance-wear-outfit] no wearable/object targets found in outfit folder.");
                    return AppearanceWearFolderResult.FailResult(
                        replaceItems: true,
                        $"Outfit '{match.Name}' ({match.UUID}) contains no wearable/object/gesture items to wear.");
                }

                Console.WriteLine($"[appearance-wear-outfit] replacing current COF outfit with item-link set targets={targetItems.Count}");
                var clearStopwatch = Stopwatch.StartNew();
                var clear = await RemoveCurrentlyWornItemsAsync(client, token).ConfigureAwait(false);
                Console.WriteLine($"[appearance-wear-outfit] cleared current wearables removedWearables={clear.RemovedWearableCount} detachedAttachments={clear.DetachedAttachmentCount} elapsedMs={clearStopwatch.ElapsedMilliseconds}");

                var addStopwatch = Stopwatch.StartNew();
                await cofManager.AddToOutfitAsync(targetItems, replace: true, cancellationToken: token).ConfigureAwait(false);
                Console.WriteLine($"[appearance-wear-outfit] AddToOutfitAsync completed targetCount={targetItems.Count} elapsedMs={addStopwatch.ElapsedMilliseconds}");

                var pruneStopwatch = Stopwatch.StartNew();
                var linksBeforePrune = await cofManager.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                var folderLinks = linksBeforePrune.Where(IsCurrentOutfitFolderLink).ToList();
                var folderLinksToRemove = folderLinks
                    .Where(link =>
                    {
                        var linkedFolderId = GetCurrentOutfitFolderLinkTargetId(link);
                        return linkedFolderId == UUID.Zero || linkedFolderId != match.UUID;
                    })
                    .ToList();

                if (folderLinksToRemove.Count > 0)
                {
                    await cofManager.RemoveFromOutfitAsync(folderLinksToRemove, token).ConfigureAwait(false);
                }

                var directCofFolders = await GetCurrentOutfitChildFoldersAsync(client, cofFolder.UUID, token).ConfigureAwait(false);
                var directOutfitFolders = directCofFolders
                    .Where(folder => IsLikelyOutfitFolderLink(folder, knownOutfitNames))
                    .ToList();
                var directMatchingFolders = directOutfitFolders
                    .Where(folder => IsFolderNameMatch(folder.Name, match.Name))
                    .OrderBy(folder => folder.UUID.ToString(), StringComparer.Ordinal)
                    .ToList();

                var directFoldersToKeep = directMatchingFolders.Take(1).Select(folder => folder.UUID).ToHashSet();
                var directFoldersToRemove = directOutfitFolders
                    .Where(folder => !directFoldersToKeep.Contains(folder.UUID))
                    .ToList();

                foreach (var folder in directFoldersToRemove)
                {
                    await client.Inventory.RemoveFolderAsync(folder.UUID, token).ConfigureAwait(false);
                }

                Console.WriteLine($"[appearance-wear-outfit] COF folder-link prune complete itemLinksBefore={folderLinks.Count} itemLinksRemoved={folderLinksToRemove.Count} directFoldersBefore={directOutfitFolders.Count} directFoldersRemoved={directFoldersToRemove.Count} elapsedMs={pruneStopwatch.ElapsedMilliseconds}");

                var ensureFolderLinkStopwatch = Stopwatch.StartNew();
                var linksAfterPrune = await cofManager.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                var matchingFolderLinks = linksAfterPrune
                    .Where(IsCurrentOutfitFolderLink)
                    .Where(link => GetCurrentOutfitFolderLinkTargetId(link) == match.UUID)
                    .ToList();
                var directMatchingFoldersAfterPrune = (await GetCurrentOutfitChildFoldersAsync(client, cofFolder.UUID, token).ConfigureAwait(false))
                    .Where(folder => IsLikelyOutfitFolderLink(folder, knownOutfitNames))
                    .Where(folder => IsFolderNameMatch(folder.Name, match.Name))
                    .OrderBy(folder => folder.UUID.ToString(), StringComparer.Ordinal)
                    .ToList();

                var linkCreateAttempts = 0;
                var folderLinkConfirmed = matchingFolderLinks.Count > 0 || directMatchingFoldersAfterPrune.Count > 0;
                if (!folderLinkConfirmed)
                {
                    Console.WriteLine($"[appearance-wear-outfit] no COF folder-link for outfitId={match.UUID}; creating one now");

                    linkCreateAttempts++;
                    SendCurrentOutfitFolderLinkCreatePacket(client, cofFolder.UUID, match.UUID, match.Name, InventoryType.Category);
                    folderLinkConfirmed = await WaitForCurrentOutfitFolderLinkPresenceAsync(client, cofFolder.UUID, match.UUID, match.Name, knownOutfitNames, token).ConfigureAwait(false);

                    // One extra direct refresh check helps with delayed inventory propagation.
                    if (!folderLinkConfirmed)
                    {
                        await Task.Delay(250, token).ConfigureAwait(false);
                        folderLinkConfirmed = await WaitForCurrentOutfitFolderLinkPresenceAsync(client, cofFolder.UUID, match.UUID, match.Name, knownOutfitNames, token).ConfigureAwait(false);
                    }
                }

                var finalEnsureLinks = await cofManager.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                matchingFolderLinks = finalEnsureLinks
                    .Where(IsCurrentOutfitFolderLink)
                    .Where(link => GetCurrentOutfitFolderLinkTargetId(link) == match.UUID)
                    .ToList();
                var finalDirectMatchingFolders = (await GetCurrentOutfitChildFoldersAsync(client, cofFolder.UUID, token).ConfigureAwait(false))
                    .Where(folder => IsLikelyOutfitFolderLink(folder, knownOutfitNames))
                    .Where(folder => IsFolderNameMatch(folder.Name, match.Name))
                    .OrderBy(folder => folder.UUID.ToString(), StringComparer.Ordinal)
                    .ToList();
                folderLinkConfirmed = matchingFolderLinks.Count > 0 || finalDirectMatchingFolders.Count > 0;

                if (!folderLinkConfirmed)
                {
                    Console.WriteLine($"[appearance-wear-outfit] COF folder-link not observed yet for outfitId={match.UUID} after attempts={linkCreateAttempts}; continuing (server may apply asynchronously)");
                }

                if (matchingFolderLinks.Count > 1)
                {
                    var keep = matchingFolderLinks
                        .OrderByDescending(link => link.CreationDate)
                        .ThenBy(link => link.UUID.ToString(), StringComparer.Ordinal)
                        .First();
                    var duplicates = matchingFolderLinks
                        .Where(link => link.UUID != keep.UUID)
                        .ToList();
                    await cofManager.RemoveFromOutfitAsync(duplicates, token).ConfigureAwait(false);
                    matchingFolderLinks = new List<InventoryItem> { keep };
                    Console.WriteLine($"[appearance-wear-outfit] removed duplicate COF folder-links duplicates={duplicates.Count} keptLinkItemId={keep.UUID}");
                }

                if (finalDirectMatchingFolders.Count > 1)
                {
                    var duplicateDirectFolders = finalDirectMatchingFolders.Skip(1).ToList();
                    foreach (var duplicateFolder in duplicateDirectFolders)
                    {
                        await client.Inventory.RemoveFolderAsync(duplicateFolder.UUID, token).ConfigureAwait(false);
                    }
                    finalDirectMatchingFolders = finalDirectMatchingFolders.Take(1).ToList();
                    Console.WriteLine($"[appearance-wear-outfit] removed duplicate direct COF outfit folders duplicates={duplicateDirectFolders.Count} keptFolderId={finalDirectMatchingFolders[0].UUID}");
                }

                Console.WriteLine($"[appearance-wear-outfit] COF folder-link ensure complete confirmed={folderLinkConfirmed} itemLinksKept={matchingFolderLinks.Count} directFoldersKept={finalDirectMatchingFolders.Count} attempts={linkCreateAttempts} elapsedMs={ensureFolderLinkStopwatch.ElapsedMilliseconds}");

                var refreshStopwatch = Stopwatch.StartNew();
                var updatedLinks = await cofManager.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                Console.WriteLine($"[appearance-wear-outfit] post-wear COF links total={updatedLinks.Count} folderLinks={updatedLinks.Count(IsCurrentOutfitFolderLink)} elapsedMs={refreshStopwatch.ElapsedMilliseconds}");

                Console.WriteLine($"[appearance-wear-outfit] execute-locked success elapsedMs={lockedStopwatch.ElapsedMilliseconds}");
                return AppearanceWearFolderResult.OkResult(
                    replaceItems: true,
                    sourceEntryCount: entries.Count,
                    wearableCandidateCount: targetItems.Count,
                    categoryResolutions: Array.Empty<OutfitCategoryResolutionInfo>(),
                    message: folderLinkConfirmed
                        ? $"Applied outfit '{match.Name}' ({match.UUID}) by rebuilding COF wearable/object links (targets={targetItems.Count}, removedWearables={clear.RemovedWearableCount}, detachedAttachments={clear.DetachedAttachmentCount})."
                        : $"Applied outfit '{match.Name}' ({match.UUID}) by rebuilding COF wearable/object links (targets={targetItems.Count}, removedWearables={clear.RemovedWearableCount}, detachedAttachments={clear.DetachedAttachmentCount}). COF outfit folder-link create was requested but not confirmed yet." );
            }, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"[appearance-wear-outfit] end ok={result.Ok} elapsedMs={overallStopwatch.ElapsedMilliseconds} message='{result.Message}'");
            return result;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[appearance-wear-outfit] canceled elapsedMs={overallStopwatch.ElapsedMilliseconds}");
            throw;
        }
    }

    public async Task<DataToolResult> AppearanceListOutfitsAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            var root = store?.RootFolder;
            if (store == null || root == null)
            {
                return DataToolResult.FailResult("Inventory store is not initialized.");
            }

            if (!TryResolveOutfitsRootFolder(client, store, out var outfitsRootFolder, out var resolveError))
            {
                return DataToolResult.FailResult(resolveError);
            }

            var entries = await client.Inventory
                .FolderContentsAsync(outfitsRootFolder.UUID, client.Self.AgentID, true, false, InventorySortOrder.ByName, token)
                .ConfigureAwait(false);

            var outfits = entries
                .OfType<InventoryFolder>()
                .Select(folder => new
                {
                    name = folder.Name,
                    folderId = folder.UUID.ToString()
                })
                .OrderBy(folder => folder.name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(folder => folder.folderId, StringComparer.Ordinal)
                .ToList();

            var payload = new
            {
                outfitsFolderId = outfitsRootFolder.UUID.ToString(),
                outfitsFolderName = outfitsRootFolder.Name,
                outfitCount = outfits.Count,
                outfits
            };

            var message = outfits.Count == 0
                ? $"No outfit folders were found under '{outfitsRootFolder.Name}'."
                : $"Found {outfits.Count} outfit folder(s) under '{outfitsRootFolder.Name}'.";
            return DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    private void QueueStartupSetupProvisioning(string trigger)
    {
        if (_lifecycleCts.IsCancellationRequested)
        {
            return;
        }

        if (!_connected)
        {
            Console.WriteLine($"[provisioning] startup provisioning trigger '{trigger}' ignored while disconnected.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunStartupSetupProvisioningIfNeededAsync(trigger, _lifecycleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifecycleCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[provisioning] startup provisioning task failed ({trigger}): {ex.Message}");
            }
        });
    }

    private async Task RunStartupSetupProvisioningIfNeededAsync(string trigger, CancellationToken cancellationToken)
    {
        var priorState = Interlocked.CompareExchange(
            ref _startupSetupProvisionState,
            StartupSetupProvisionStateRunning,
            StartupSetupProvisionStateIdle);
        if (priorState == StartupSetupProvisionStateRunning)
        {
            Console.WriteLine($"[provisioning] startup provisioning already running, skipping trigger '{trigger}'.");
            return;
        }

        if (priorState == StartupSetupProvisionStateCompleted)
        {
            Console.WriteLine($"[provisioning] startup provisioning already completed for this login, skipping trigger '{trigger}'.");
            return;
        }

        var completed = false;
        var setupFolderName = string.IsNullOrWhiteSpace(_options.WearFolderName)
            ? "Setup"
            : _options.WearFolderName.Trim();

        try
        {
            Console.WriteLine($"[provisioning] startup provisioning queued from '{trigger}' for setup folder '{setupFolderName}'.");

            var readinessDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var ready = false;
            while (DateTime.UtcNow < readinessDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var client = _connected ? _client : null;
                if (client == null
                    || client.Network.CurrentSim == null
                    || client.Self.AgentID == UUID.Zero
                    || client.Inventory?.Store?.RootFolder == null)
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ready = true;
                break;
            }

            if (!ready)
            {
                Console.WriteLine($"[provisioning] startup provisioning for '{setupFolderName}' timed out waiting for readiness; waiting for another login/sim event.");
                return;
            }

            // Give remote inventory indexing a moment to catch up after login/sim-change.
            Console.WriteLine($"[provisioning] waiting {StartupSetupProvisionArtificialDelayMs / 1000}s before setup-folder lookup.");
            await Task.Delay(StartupSetupProvisionArtificialDelayMs, cancellationToken).ConfigureAwait(false);

            var activeClient = _client;
            if (activeClient == null)
            {
                Console.WriteLine("[provisioning] startup provisioning aborted because client became unavailable.");
                return;
            }

            var outcome = await TryRunStartupSetupProvisioningCoreAsync(activeClient, setupFolderName, cancellationToken).ConfigureAwait(false);
            if (outcome.Completed)
            {
                completed = true;
                return;
            }

            if (outcome.RetryLater)
            {
                Console.WriteLine($"[provisioning] startup provisioning for '{setupFolderName}' requested retry; waiting for next sim/login event.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(
                ref _startupSetupProvisionState,
                completed ? StartupSetupProvisionStateCompleted : StartupSetupProvisionStateIdle);
        }
    }

    private async Task<(bool Completed, bool RetryLater)> TryRunStartupSetupProvisioningCoreAsync(
        GridClient client,
        string setupFolderName,
        CancellationToken cancellationToken)
    {
        var setupFolderId = UUID.Zero;
        var setupFolderDisplayName = setupFolderName;
        var provisioningFolderId = UUID.Zero;
        var provisioningFolderDisplayName = string.Empty;
        var provisioningFolderMovedToObjects = false;
        var movedFromParentId = UUID.Zero;

        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = client.Inventory.Store;
            var rootFolder = store?.RootFolder;
            if (store == null || rootFolder == null)
            {
                return (false, true);
            }

            List<InventoryBase> rootContents;
            try
            {
                rootContents = await client.Inventory
                    .FolderContentsAsync(rootFolder.UUID, client.Self.AgentID, true, true, InventorySortOrder.ByName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[provisioning] could not read root inventory while looking for setup folder '{setupFolderName}': {ex.Message}");
                return (false, true);
            }

            var setupFolder = rootContents
                .OfType<InventoryFolder>()
                .FirstOrDefault(folder => string.Equals(folder.Name?.Trim(), setupFolderName, StringComparison.OrdinalIgnoreCase));
            if (setupFolder == null)
            {
                Console.WriteLine($"[provisioning] no setup folder '{setupFolderName}' found in root inventory; startup provisioning is complete.");
                return (true, false);
            }

            setupFolderId = setupFolder.UUID;
            setupFolderDisplayName = string.IsNullOrWhiteSpace(setupFolder.Name) ? setupFolderName : setupFolder.Name!;

            List<InventoryBase> setupContents;
            try
            {
                setupContents = await client.Inventory
                    .FolderContentsAsync(setupFolder.UUID, client.Self.AgentID, true, true, InventorySortOrder.ByName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[provisioning] could not read setup folder '{setupFolderDisplayName}' ({setupFolderId}): {ex.Message}");
                return (false, true);
            }

            var agentsNotecard = setupContents
                .OfType<InventoryItem>()
                .FirstOrDefault(item => IsNotecardInventoryItem(item)
                    && string.Equals(item.Name?.Trim(), "AGENTS.md", StringComparison.OrdinalIgnoreCase));
            if (agentsNotecard != null)
            {
                var notecardsFolderId = client.Inventory.FindFolderForType(FolderType.Notecard);
                if (notecardsFolderId == UUID.Zero)
                {
                    Console.WriteLine($"[provisioning] notecards folder is not ready yet; will retry startup provisioning for '{setupFolderDisplayName}'.");
                    return (false, true);
                }

                if (agentsNotecard.ParentUUID != notecardsFolderId)
                {
                    Console.WriteLine($"[provisioning] moving companion notecard '{agentsNotecard.Name}' ({agentsNotecard.UUID}) to Notecards ({notecardsFolderId}).");
                    await client.Inventory.MoveItemAsync(agentsNotecard.UUID, notecardsFolderId, cancellationToken).ConfigureAwait(false);
                }
            }

            var provisioningFolder = setupContents.OfType<InventoryFolder>().FirstOrDefault();
            if (provisioningFolder != null)
            {
                provisioningFolderId = provisioningFolder.UUID;
                provisioningFolderDisplayName = provisioningFolder.Name ?? string.Empty;

                var objectsFolderId = client.Inventory.FindFolderForType(FolderType.Object);
                if (objectsFolderId == UUID.Zero)
                {
                    Console.WriteLine($"[provisioning] objects folder is not ready yet; will retry startup provisioning for '{setupFolderDisplayName}'.");
                    return (false, true);
                }

                if (provisioningFolder.ParentUUID != objectsFolderId)
                {
                    movedFromParentId = provisioningFolder.ParentUUID;
                    provisioningFolderMovedToObjects = true;
                    Console.WriteLine($"[provisioning] moving provisioning folder '{provisioningFolderDisplayName}' ({provisioningFolderId}) to Objects ({objectsFolderId}).");
                    await client.Inventory.MoveFolderAsync(provisioningFolderId, objectsFolderId, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _actionGate.Release();
        }

        if (provisioningFolderId != UUID.Zero)
        {
            var wearResult = await AppearanceWearFolderAsync(
                    provisioningFolderId.ToString(),
                    replaceItems: true,
                    removeExistingItems: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!wearResult.Ok)
            {
                Console.WriteLine($"[provisioning] startup wear-folder apply failed for '{provisioningFolderDisplayName}' ({provisioningFolderId}): {wearResult.Message}");
                if (provisioningFolderMovedToObjects && movedFromParentId != UUID.Zero)
                {
                    await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await client.Inventory.MoveFolderAsync(provisioningFolderId, movedFromParentId, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"[provisioning] restored provisioning folder '{provisioningFolderDisplayName}' ({provisioningFolderId}) to setup folder after failed wear attempt.");
                    }
                    catch (Exception rollbackEx)
                    {
                        Console.WriteLine($"[provisioning] failed to restore provisioning folder '{provisioningFolderDisplayName}' ({provisioningFolderId}) after wear failure: {rollbackEx.Message}");
                    }
                    finally
                    {
                        _actionGate.Release();
                    }
                }

                return (false, true);
            }

            Console.WriteLine($"[provisioning] applied startup wear-folder '{provisioningFolderDisplayName}' ({provisioningFolderId}) from setup folder '{setupFolderDisplayName}'.");
        }
        else
        {
            Console.WriteLine($"[provisioning] setup folder '{setupFolderDisplayName}' found but contains no child folder to wear.");
        }

        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trashFolderId = client.Inventory.FindFolderForType(FolderType.Trash);
            if (trashFolderId == UUID.Zero)
            {
                Console.WriteLine($"[provisioning] startup cleanup skipped: could not resolve Trash folder for setup folder '{setupFolderDisplayName}' ({setupFolderId}).");
                return (true, false);
            }

            await client.Inventory.MoveFolderAsync(setupFolderId, trashFolderId, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[provisioning] moved setup folder '{setupFolderDisplayName}' ({setupFolderId}) to Trash.");
        }
        finally
        {
            _actionGate.Release();
        }

        return (true, false);
    }

    private async Task<(int RemovedWearableCount, int DetachedAttachmentCount)> RemoveCurrentlyWornItemsAsync(
        GridClient client,
        CancellationToken cancellationToken)
    {
        var removedWearables = 0;
        var cof = GetSharedCurrentOutfitFolder(client);
        {
            var allWearables = new List<InventoryItem>();
            foreach (var wearableType in Enum.GetValues<WearableType>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                List<InventoryItem> wornOfType;
                try
                {
                    wornOfType = await cof.GetWornAtAsync(wearableType, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                if (wornOfType.Count == 0)
                {
                    continue;
                }

                allWearables.AddRange(wornOfType);
            }

            if (allWearables.Count > 0)
            {
                var uniqueWearables = allWearables
                    .GroupBy(w => w.UUID)
                    .Select(group => group.First())
                    .ToList();

                await cof.RemoveFromOutfitAsync(uniqueWearables, cancellationToken).ConfigureAwait(false);
                removedWearables = uniqueWearables.Count;
            }
        }

        var detachedAttachments = 0;
        var attachmentsByItem = await CollectAttachmentPointMappingsAsync(client, cancellationToken).ConfigureAwait(false);
        foreach (var attachmentItemId in attachmentsByItem.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Appearance.Detach(attachmentItemId);
            detachedAttachments++;
        }

        return (removedWearables, detachedAttachments);
    }

    private static bool IsNotecardInventoryItem(InventoryItem item)
        => item.AssetType == AssetType.Notecard || item.InventoryType == InventoryType.Notecard;

    private static string NormalizeCompanionIniLookupKey(string? itemName)
        => (itemName ?? string.Empty).Trim().ToLowerInvariant();

    private static string BuildCompanionIniName(string itemName)
        => $"{itemName}.ini";

    private async Task<WearAttachmentIniConfig?> TryResolveWearAttachmentIniConfigAsync(
        GridClient client,
        InventoryItem attachmentItem,
        UUID parentFolderId,
        IReadOnlyDictionary<(UUID ParentFolderId, string ItemName), InventoryItem> companionNotecardsByFolderAndName,
        CancellationToken cancellationToken)
    {
        var itemName = attachmentItem.Name?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        var iniName = NormalizeCompanionIniLookupKey(BuildCompanionIniName(itemName));
        if (!companionNotecardsByFolderAndName.TryGetValue((parentFolderId, iniName), out var configNotecard))
        {
            return null;
        }

        try
        {
            var notecardAsset = await client.Assets.RequestInventoryAssetAsync(
                configNotecard.AssetUUID,
                configNotecard.UUID,
                UUID.Zero,
                client.Self.AgentID,
                AssetType.Notecard,
                true,
                UUID.Random(),
                cancellationToken).ConfigureAwait(false);

            if (notecardAsset?.AssetData == null || notecardAsset.AssetData.Length == 0)
            {
                Console.WriteLine($"[appearance] companion ini notecard '{configNotecard.Name}' for '{attachmentItem.Name}' has no asset data.");
                return null;
            }

            var notecard = new AssetNotecard(configNotecard.AssetUUID, notecardAsset.AssetData);
            if (!notecard.Decode() || string.IsNullOrWhiteSpace(notecard.BodyText))
            {
                Console.WriteLine($"[appearance] companion ini notecard '{configNotecard.Name}' for '{attachmentItem.Name}' could not be decoded.");
                return null;
            }

            if (!TryParseWearAttachmentIniConfig(notecard.BodyText, out var parsedConfig, out var parseWarnings))
            {
                Console.WriteLine($"[appearance] companion ini notecard '{configNotecard.Name}' for '{attachmentItem.Name}' has no usable settings.");
                return null;
            }

            if (parseWarnings.Count > 0)
            {
                Console.WriteLine($"[appearance] companion ini warnings for '{attachmentItem.Name}': {string.Join(" | ", parseWarnings)}");
            }

            return parsedConfig;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[appearance] failed to load companion ini for '{attachmentItem.Name}': {ex.Message}");
            return null;
        }
    }

    private static bool TryParseWearAttachmentIniConfig(
        string bodyText,
        out WearAttachmentIniConfig config,
        out List<string> warnings)
    {
        var attachPoint = default(AttachmentPoint?);
        float? offsetX = null;
        float? offsetY = null;
        float? offsetZ = null;
        float? rotateX = null;
        float? rotateY = null;
        float? rotateZ = null;
        float? scaleX = null;
        float? scaleY = null;
        float? scaleZ = null;
        warnings = new List<string>();

        var lines = bodyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "attach":
                    if (Enum.TryParse<AttachmentPoint>(value, true, out var parsedPoint))
                    {
                        attachPoint = parsedPoint;
                    }
                    else if (!string.IsNullOrWhiteSpace(value))
                    {
                        warnings.Add($"attach='{value}' is not a valid AttachmentPoint.");
                    }
                    break;
                case "offsetx":
                    TryParseIniSingle("offsetX", value, ref offsetX, warnings);
                    break;
                case "offsety":
                    TryParseIniSingle("offsetY", value, ref offsetY, warnings);
                    break;
                case "offsetz":
                    TryParseIniSingle("offsetZ", value, ref offsetZ, warnings);
                    break;
                case "rotatex":
                    TryParseIniSingle("rotateX", value, ref rotateX, warnings);
                    break;
                case "rotatey":
                    TryParseIniSingle("rotateY", value, ref rotateY, warnings);
                    break;
                case "rotatez":
                    TryParseIniSingle("rotateZ", value, ref rotateZ, warnings);
                    break;
                case "scalex":
                    TryParseIniSingle("scaleX", value, ref scaleX, warnings);
                    break;
                case "scaley":
                    TryParseIniSingle("scaleY", value, ref scaleY, warnings);
                    break;
                case "scalez":
                    TryParseIniSingle("scaleZ", value, ref scaleZ, warnings);
                    break;
            }
        }

        config = new WearAttachmentIniConfig(attachPoint, offsetX, offsetY, offsetZ, rotateX, rotateY, rotateZ, scaleX, scaleY, scaleZ);
        return config.AttachPoint.HasValue || config.HasTransformValues;
    }

    private static void TryParseIniSingle(string key, string rawValue, ref float? target, ICollection<string> warnings)
    {
        if (float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            target = parsed;
            return;
        }

        warnings.Add($"{key}='{rawValue}' is not a valid number.");
    }

    private async Task TryApplyWearAttachmentIniTransformAsync(
        GridClient client,
        InventoryItem attachmentItem,
        WearAttachmentIniConfig config,
        CancellationToken cancellationToken)
    {
        if (!config.HasTransformValues)
        {
            return;
        }

        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return;
        }

        UUID objectId = UUID.Zero;
        uint localId = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryFindAttachedObjectForInventoryItem(client, attachmentItem.UUID, out objectId, out localId))
            {
                break;
            }

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }

        if (localId == 0 || !sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
        {
            Console.WriteLine($"[appearance] companion ini transform for '{attachmentItem.Name}' deferred: attached object not visible yet.");
            return;
        }

        if (config.OffsetX.HasValue || config.OffsetY.HasValue || config.OffsetZ.HasValue)
        {
            var targetPosition = new Vector3(
                config.OffsetX ?? prim.Position.X,
                config.OffsetY ?? prim.Position.Y,
                config.OffsetZ ?? prim.Position.Z);
            targetPosition = ClampLocalPosition(targetPosition);
            client.Objects.SetPosition(sim, localId, targetPosition, childOnly: false);
            Console.WriteLine($"[appearance] transforming to position '{targetPosition}'.");
        }

        if (config.ScaleX.HasValue || config.ScaleY.HasValue || config.ScaleZ.HasValue)
        {
            var targetScale = new Vector3(
                config.ScaleX ?? prim.Scale.X,
                config.ScaleY ?? prim.Scale.Y,
                config.ScaleZ ?? prim.Scale.Z);
            targetScale = ClampScale(targetScale);
            client.Objects.SetScale(sim, localId, targetScale, false, false);
            Console.WriteLine($"[appearance] transforming to scale '{targetScale}'.");
        }

        if (config.RotateX.HasValue || config.RotateY.HasValue || config.RotateZ.HasValue)
        {
            prim.Rotation.GetEulerAngles(out var currentRoll, out var currentPitch, out var currentYaw);
            var targetRoll = (config.RotateX ?? (currentRoll * Utils.RAD_TO_DEG)) * Utils.DEG_TO_RAD;
            var targetPitch = (config.RotateY ?? (currentPitch * Utils.RAD_TO_DEG)) * Utils.DEG_TO_RAD;
            var targetYaw = (config.RotateZ ?? (currentYaw * Utils.RAD_TO_DEG)) * Utils.DEG_TO_RAD;
            var targetRotation = Quaternion.CreateFromEulers(targetRoll, targetPitch, targetYaw);
            client.Objects.SetRotation(sim, localId, targetRotation, childOnly: false);
            Console.WriteLine($"[appearance] transforming to rotation '{targetRotation}'.");
        }
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
                Console.WriteLine($"[appearance] saving current outfit to folder '{folderName}' under parent folder '{parentFolderId}'.");
                if (!UUID.TryParse(parentFolderId, out var parsedParentId))
                {
                    return OutfitSaveResult.FailResult("parentFolderId is not a valid UUID.");
                }

                if (!store.TryGetValue(parsedParentId, out var parentNode) || parentNode is not InventoryFolder)
                {
                    return OutfitSaveResult.FailResult($"Parent folder {parsedParentId} was not found in local inventory store.");
                }

                parentId = parsedParentId;
                Console.WriteLine($"[appearance] using parent folder {parentId} as parent for new outfit folder.");
            }
            else
            {
                Console.WriteLine($"[appearance] saving current outfit to folder '{folderName}' under default My Outfits/Outfits root.");
                if (TryResolveOutfitsRootFolder(client, store, out var outfitsRootFolder, out var resolveError))
                {
                    parentId = outfitsRootFolder.UUID;
                    Console.WriteLine($"[appearance] using outfits root folder {parentId} ('{outfitsRootFolder.Name}') as parent for new outfit folder.");
                }
                else
                {
                    Console.WriteLine($"[appearance] failed to resolve outfits root folder ({resolveError}); using inventory root {parentId} as parent for new outfit folder.");
                }
            }

            var destinationFolderId = client.Inventory.CreateFolder(parentId, folderName.Trim(), FolderType.None);
            var cof = GetSharedCurrentOutfitFolder(client);
            var currentLinks = await cof.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);

            Console.WriteLine($"[appearance] preparing to save current outfit links to folder '{destinationFolderId}'.");

            var linkTargets = new List<InventoryItem>();
            var seen = new HashSet<UUID>();
            foreach (var link in currentLinks)
            {
                Console.WriteLine($"[appearance] resolving link for item '{link.Name}' ({link.UUID}).");
                var resolved = ResolveLinkedInventoryItem(store, link);

                if (link.InventoryType == InventoryType.Folder || resolved.InventoryType == InventoryType.Folder)
                {
                    Console.WriteLine($"[appearance] skipping COF folder link '{link.Name}' ({link.UUID}).");
                    continue;
                }

                if (resolved is not InventoryWearable && resolved is not InventoryObject && resolved.AssetType != AssetType.Gesture)
                {
                    Console.WriteLine($"[appearance] skipping non-outfit link target '{resolved.Name}' ({resolved.UUID}), assetType={resolved.AssetType}, inventoryType={resolved.InventoryType}.");
                    continue;
                }

                if (seen.Add(resolved.UUID))
                {
                    Console.WriteLine($"[appearance] resolved link for item '{resolved.Name}' ({resolved.UUID}).");
                    linkTargets.Add(resolved);
                }
                else
                {
                    Console.WriteLine($"[appearance] failed to resolve link for item '{link.Name}' ({link.UUID}).");
                }
            }

            var linkedCount = 0;
            var failedCount = 0;
            foreach (var target in linkTargets)
            {
                token.ThrowIfCancellationRequested();

                Console.WriteLine($"[appearance] creating link for item '{target.Name}' ({target.UUID}) in folder '{destinationFolderId}'.");

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

            if (linkedCount == 0)
            {
                var reason = linkTargets.Count == 0
                    ? "No currently worn outfit links were available to save."
                    : $"All link creation requests failed ({failedCount}/{linkTargets.Count}).";

                return OutfitSaveResult.FailResult(
                    $"Failed to save current outfit links to folder '{folderName.Trim()}' ({destinationFolderId}). {reason}");
            }

            return OutfitSaveResult.OkResult(
                destinationFolderId.ToString(),
                linkedCount,
                failedCount,
                failedCount == 0
                    ? $"Saved current outfit links to folder '{folderName.Trim()}' ({destinationFolderId}). Linked={linkedCount}, failed={failedCount}."
                    : $"Saved current outfit links to folder '{folderName.Trim()}' ({destinationFolderId}) with partial failures. Linked={linkedCount}, failed={failedCount}." );
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

            var cof = GetSharedCurrentOutfitFolder(client);
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
            var cof = GetSharedCurrentOutfitFolder(client);
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

    private async Task<List<OutfitCategoryResolutionInfo>> BuildOutfitCategoryResolutionsAsync(
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

    private async Task<Dictionary<UUID, AttachmentPoint>> CollectAttachmentPointMappingsAsync(
        GridClient client,
        CancellationToken cancellationToken,
        bool includeAttachmentsAndWearables = true,
        bool includeCurrentOutfit = true)
    {
        var merged = includeAttachmentsAndWearables
            ? new Dictionary<UUID, AttachmentPoint>(client.Appearance.GetAttachmentsByItemId())
            : new Dictionary<UUID, AttachmentPoint>();

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

        if (includeCurrentOutfit)
        {
            var cof = GetSharedCurrentOutfitFolder(client);
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

    public async Task<BotToolResult> AppearanceRebakeAsync(bool? forceRebake, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Appearance.RequestSetAppearance(forceRebake.HasValue && forceRebake.Value).ConfigureAwait(false);
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
            return BuildScriptUpdateResult(
                client,
                result.itemID.ToString(),
                result.assetID.ToString(),
                sourceBytes.Length,
                result.uploadStatus,
                result.compileSuccess,
                messages,
                "Script upload to agent inventory completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptUpdateResult> ScriptContentUploadAgentAsync(string scriptContent, string itemId, bool mono, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var scriptItemId))
        {
            return ScriptUpdateResult.FailResult("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sourceBytes = Encoding.UTF8.GetBytes(scriptContent ?? string.Empty);
            var result = await client.Inventory.RequestUpdateScriptAgentInventoryAsync(sourceBytes, scriptItemId, mono, token).ConfigureAwait(false);

            if (!result.uploadSuccess)
            {
                return ScriptUpdateResult.FailResult($"Script upload failed: {result.uploadStatus}");
            }

            var messages = result.compileMessages?.ToList() ?? new List<string>();
            return BuildScriptUpdateResult(
                client,
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
            return BuildScriptUpdateResult(
                client,
                result.itemID.ToString(),
                result.assetID.ToString(),
                sourceBytes.Length,
                result.uploadStatus,
                result.compileSuccess,
                messages,
                "Script upload to task inventory completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScriptUpdateResult> ScriptContentUploadTaskAsync(
        string scriptContent,
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
            var sourceBytes = Encoding.UTF8.GetBytes(scriptContent ?? string.Empty);
            var result = await client.Inventory
                .RequestUpdateScriptTaskAsync(sourceBytes, scriptItemId, taskObjectId, mono, running, token)
                .ConfigureAwait(false);

            if (!result.uploadSuccess)
            {
                return ScriptUpdateResult.FailResult($"Task script upload failed: {result.uploadStatus}");
            }

            var messages = result.compileMessages?.ToList() ?? new List<string>();
            return BuildScriptUpdateResult(
                client,
                result.itemID.ToString(),
                result.assetID.ToString(),
                sourceBytes.Length,
                result.uploadStatus,
                result.compileSuccess,
                messages,
                "Script upload to task inventory completed.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private ScriptUpdateResult BuildScriptUpdateResult(
        GridClient client,
        string itemId,
        string assetId,
        int sourceBytes,
        string uploadStatus,
        bool? compiledHint,
        IReadOnlyList<string> compileMessages,
        string message)
    {
        if (ShouldForceOpenSimCompiledHint(client, uploadStatus, compiledHint, compileMessages))
        {
            return ScriptUpdateResult.OkResult(
                itemId,
                assetId,
                sourceBytes,
                uploadStatus,
                true,
                compileMessages,
                "OpenSimulator with YEngine script compilation results are unavailable, verify success independently");
        }

        return ScriptUpdateResult.OkResult(itemId, assetId, sourceBytes, uploadStatus, compiledHint, compileMessages, message);
    }

    private bool ShouldForceOpenSimCompiledHint(
        GridClient client,
        string uploadStatus,
        bool? compiledHint,
        IReadOnlyList<string> compileMessages)
    {
        return IsCurrentServerOpenSimulator(client)
            && string.Equals(uploadStatus, "complete", StringComparison.OrdinalIgnoreCase)
            && compiledHint == false
            && (compileMessages.Count == 0);
    }

    private bool IsCurrentServerOpenSimulator(GridClient client)
    {
        if (Uri.TryCreate(_options.BotLoginUri, UriKind.Absolute, out var loginUri))
        {
            var host = loginUri.Host ?? string.Empty;
            if (host.EndsWith("secondlife.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("lindenlab.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // This bot targets OpenSimulator-style grids; non-Linden login hosts are treated as OpenSim.
            return true;
        }

        var loginMessage = _lastLoginMessage ?? string.Empty;
        if (loginMessage.Contains("OpenSim", StringComparison.OrdinalIgnoreCase)
            || loginMessage.Contains("OpenSimulator", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var simName = client.Network.CurrentSim?.Name ?? string.Empty;
        return simName.Contains("OpenSim", StringComparison.OrdinalIgnoreCase)
            || simName.Contains("OpenSimulator", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<BotToolResult> ScriptCopyInventoryToTaskAsync(
        uint objectLocalId,
        string inventoryScriptItemId,
        bool enableScript,
        bool forceOverwrite,
        CancellationToken cancellationToken)
    {
        return await CopyInventoryItemToTaskAsync(
            objectLocalId,
            inventoryScriptItemId,
            expectedKind: "script",
            enableScript,
            forceOverwrite,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> NotecardCopyInventoryToTaskAsync(
        uint objectLocalId,
        string inventoryNotecardItemId,
        bool forceOverwrite,
        CancellationToken cancellationToken)
    {
        return await CopyInventoryItemToTaskAsync(
            objectLocalId,
            inventoryNotecardItemId,
            expectedKind: "notecard",
            enableScript: false,
            forceOverwrite,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<BotToolResult> CopyInventoryItemToTaskAsync(
        uint objectLocalId,
        string inventoryItemId,
        string expectedKind,
        bool enableScript,
        bool forceOverwrite,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(inventoryItemId, out var inventoryItemUuid))
        {
            return BotToolResult.Fail(expectedKind == "script"
                ? "inventoryScriptItemId is not a valid UUID."
                : "inventoryNotecardItemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            if (!sim.ObjectsPrimitives.TryGetValue(objectLocalId, out var targetPrim) || targetPrim == null)
            {
                return BotToolResult.Fail($"Object localId={objectLocalId} is not present in simulator cache.");
            }

            var item = await ResolveInventoryItemAsync(client, inventoryItemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {inventoryItemUuid} was not found.");
            }

            var isScript = item.AssetType == AssetType.LSLText && item.InventoryType == InventoryType.LSL;
            var isNotecard = item.AssetType == AssetType.Notecard && item.InventoryType == InventoryType.Notecard;
            var kind = isScript ? "script" : isNotecard ? "notecard" : "unsupported";

            if (!string.Equals(kind, expectedKind, StringComparison.Ordinal))
            {
                return BotToolResult.Fail(
                    expectedKind == "script"
                        ? $"Inventory item {item.UUID} is not script-typed (assetType={item.AssetType}, inventoryType={item.InventoryType})."
                        : $"Inventory item {item.UUID} is not notecard-typed (assetType={item.AssetType}, inventoryType={item.InventoryType}).");
            }

            var removedCount = 0;
            if (forceOverwrite)
            {
                var taskEntries = await client.Inventory
                    .GetTaskInventoryAsync(targetPrim.ID, objectLocalId, sim, token)
                    .ConfigureAwait(false);

                var duplicates = taskEntries
                    .OfType<InventoryItem>()
                    .Where(taskItem =>
                        taskItem.UUID != UUID.Zero
                        && taskItem.AssetType == item.AssetType
                        && taskItem.InventoryType == item.InventoryType
                        && string.Equals(taskItem.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var duplicate in duplicates)
                {
                    client.Inventory.RemoveTaskInventory(objectLocalId, duplicate.UUID, sim);
                }

                removedCount = duplicates.Count;
            }

            var transaction = kind == "script"
                ? client.Inventory.CopyScriptToTask(objectLocalId, item, enableScript, sim)
                : client.Inventory.UpdateTaskInventory(objectLocalId, item, sim, only_mod_meta: true);

            return BotToolResult.OkResult(
                $"Requested {kind} copy to object {objectLocalId}; transactionId={transaction}, forceOverwrite={forceOverwrite}, removed={removedCount}" +
                (kind == "script" ? $", enableScript={enableScript}." : "."));
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

    public Task<BotTaskHandle> InventoryListAsync(
        string? folderIdOrPath,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var resultHandle = BuildInventoryListResultHandle(folderIdOrPath, recursive);
        var description = recursive
            ? $"Inventory list folder '{folderIdOrPath ?? "<root>"}' recursively."
            : $"Inventory list folder '{folderIdOrPath ?? "<root>"}'.";
            
        Console.WriteLine($"[inventory-list] start handle='{resultHandle}' recursive={recursive} canceled={cancellationToken.IsCancellationRequested}");

        return Task.FromResult(StartBotTask(
            description,
            async (taskHandle, taskCancellationToken) =>
            {
                try
                {
                    RegisterInventoryListTaskHandle(taskHandle.Handle, resultHandle);
                    Console.WriteLine($"[inventory-list] task started handle='{taskHandle.Handle}' resultHandle='{resultHandle}'");
                    EmitInventoryListProgressEvent(taskHandle.Handle, "Starting inventory list retrieval.", 5);

                    if (TryGetInventoryListResult(resultHandle, out _, out _))
                    {
                        EmitInventoryListCompleteEvent(taskHandle.Handle, resultHandle, true, $"Inventory list already cached as '{resultHandle}'.");
                        return;
                    }

                    Console.WriteLine($"[inventory-list] retrieving inventory list for folder '{folderIdOrPath ?? "<root>"}' recursive={recursive}");
                    var result = await FetchInventoryListAsync(folderIdOrPath, recursive, taskCancellationToken).ConfigureAwait(false);
                    if (!result.Ok)
                    {
                        Console.WriteLine($"[inventory-list] retrieval failed for folder '{folderIdOrPath ?? "<root>"}' recursive={recursive}: {result.Message}");
                        EmitInventoryListCompleteEvent(taskHandle.Handle, resultHandle, false, result.Message);
                        return;
                    }

                    Console.WriteLine($"[inventory-list] retrieval complete for folder '{folderIdOrPath ?? "<root>"}' recursive={recursive}; materialized {result.Entries.Count} entries");
                    StoreInventoryListResult(taskHandle.Handle, resultHandle, result);
                    Console.WriteLine($"[inventory-list] result cached for handle='{resultHandle}'");
                    EmitInventoryListCompleteEvent(
                        taskHandle.Handle,
                        resultHandle,
                        true,
                        $"Inventory list retrieval complete; materialized {result.Entries.Count} entries for result handle '{resultHandle}'.");
                }
                catch (OperationCanceledException) when (taskCancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine($"[inventory-list] retrieval cancelled for folder '{folderIdOrPath ?? "<root>"}' recursive={recursive}");
                    EmitInventoryListCompleteEvent(taskHandle.Handle, resultHandle, false, "Inventory list task was cancelled.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[inventory-list] retrieval failed for folder '{folderIdOrPath ?? "<root>"}' recursive={recursive}: {ex.GetType().Name}: {ex.Message}");
                    EmitInventoryListCompleteEvent(taskHandle.Handle, resultHandle, false, $"Inventory list task failed: {ex.Message}");
                }
            }));
    }

    public Task<InventoryQueryResult> InventoryListRetrieveAsync(
        string resultHandle,
        int maxResults,
        int pageSize,
        string? nameContains,
        string? type,
        string? createdAfterUtc,
        string? createdBeforeUtc,
        string? creatorId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var normalizedResultHandle = (resultHandle ?? string.Empty).Trim();
        var overallStopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[inventory-list-retrieve] start handle='{normalizedResultHandle}' maxResults={maxResults} pageSize={pageSize} cursor='{cursor ?? ""}' nameContains='{nameContains ?? ""}' type='{type ?? ""}' creatorId='{creatorId ?? ""}' createdAfterUtc='{createdAfterUtc ?? ""}' createdBeforeUtc='{createdBeforeUtc ?? ""}' canceled={cancellationToken.IsCancellationRequested}");

        try
        {
            if (!TryParseOptionalUtc(createdAfterUtc, "createdAfterUtc", out var createdAfter, out var createdAfterError))
            {
                Console.WriteLine($"[inventory-list-retrieve] invalid createdAfterUtc='{createdAfterUtc ?? ""}': {createdAfterError ?? "createdAfterUtc is invalid."}");
                return Task.FromResult(InventoryQueryResult.FailResult(createdAfterError ?? "createdAfterUtc is invalid."));
            }

            if (!TryParseOptionalUtc(createdBeforeUtc, "createdBeforeUtc", out var createdBefore, out var createdBeforeError))
            {
                Console.WriteLine($"[inventory-list-retrieve] invalid createdBeforeUtc='{createdBeforeUtc ?? ""}': {createdBeforeError ?? "createdBeforeUtc is invalid."}");
                return Task.FromResult(InventoryQueryResult.FailResult(createdBeforeError ?? "createdBeforeUtc is invalid."));
            }

            if (createdAfter.HasValue && createdBefore.HasValue && createdAfter.Value > createdBefore.Value)
            {
                Console.WriteLine($"[inventory-list-retrieve] invalid utc range createdAfterUtc={createdAfter.Value:O} createdBeforeUtc={createdBefore.Value:O}");
                return Task.FromResult(InventoryQueryResult.FailResult("createdAfterUtc must be earlier than or equal to createdBeforeUtc."));
            }

            UUID? creatorUuid = null;
            if (!string.IsNullOrWhiteSpace(creatorId))
            {
                if (!UUID.TryParse(creatorId, out var parsedCreatorUuid))
                {
                    Console.WriteLine($"[inventory-list-retrieve] invalid creatorId='{creatorId}'");
                    return Task.FromResult(InventoryQueryResult.FailResult("creatorId is not a valid UUID."));
                }

                creatorUuid = parsedCreatorUuid;
            }

            if (!TryDecodeInventoryCursor(cursor, out var cursorOffset, out var cursorError))
            {
                Console.WriteLine($"[inventory-list-retrieve] invalid cursor='{cursor ?? ""}': {cursorError ?? "cursor is invalid."}");
                return Task.FromResult(InventoryQueryResult.FailResult(cursorError ?? "cursor is invalid."));
            }

            var normalizedNameContains = string.IsNullOrWhiteSpace(nameContains) ? null : nameContains.Trim();
            var normalizedType = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
            var effectivePageSize = Math.Clamp(pageSize <= 0 ? 200 : pageSize, 1, 500);

            if (!TryGetInventoryListResult(normalizedResultHandle, out var storedResult, out var storeError))
            {
                Console.WriteLine($"[inventory-list-retrieve] lookup failed handle='{normalizedResultHandle}': {storeError}");
                return Task.FromResult(InventoryQueryResult.FailResult(storeError));
            }

            var filtered = FilterInventoryEntries(
                storedResult.Entries,
                normalizedNameContains,
                normalizedType,
                createdAfter,
                createdBefore,
                creatorUuid,
                cursorOffset,
                maxResults,
                effectivePageSize,
                out var filterError);

            if (filterError != null)
            {
                Console.WriteLine($"[inventory-list-retrieve] filter failed handle='{normalizedResultHandle}': {filterError}");
                return Task.FromResult(InventoryQueryResult.FailResult(filterError));
            }

            Console.WriteLine($"[inventory-list-retrieve] complete handle='{normalizedResultHandle}' matched={filtered.TotalMatched} page={filtered.Entries.Count} elapsedMs={overallStopwatch.ElapsedMilliseconds}");
            return Task.FromResult(filtered);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[inventory-list-retrieve] failed elapsedMs={overallStopwatch.ElapsedMilliseconds} error={ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(InventoryQueryResult.FailResult(ex.Message));
        }
    }

    public Task<BotToolResult> InventoryListClearAsync(string taskHandle)
    {
        if (string.IsNullOrWhiteSpace(taskHandle))
        {
            return Task.FromResult(BotToolResult.Fail("taskHandle is required."));
        }

        var handle = taskHandle.Trim();
        return Task.FromResult(RemoveInventoryListResult(handle)
            ? BotToolResult.OkResult($"Cleared materialized inventory list results for handle '{handle}'.")
            : BotToolResult.Fail($"No materialized inventory list results were found for handle '{handle}'."));
    }

    private async Task<InventoryQueryResult> FetchInventoryListAsync(
        string? folderIdOrPath,
        bool recursive,
        CancellationToken cancellationToken)
    {
        var overallStopwatch = Stopwatch.StartNew();
        Console.WriteLine($"[inventory-list] start folderIdOrPath='{folderIdOrPath ?? ""}' recursive={recursive} canceled={cancellationToken.IsCancellationRequested}");

        try
        {
            return await ExecuteLockedAsync(async (client, token) =>
            {
                var lockStopwatch = Stopwatch.StartNew();
                Console.WriteLine("[inventory-list] execute-locked begin");

                var store = client.Inventory.Store;
                var root = store?.RootFolder;
                if (store == null || root == null)
                {
                    Console.WriteLine("[inventory-list] inventory store/root not initialized");
                    return InventoryQueryResult.FailResult("Inventory store is not initialized.");
                }

                if (!TryResolveInventoryFolderUuid(client, store, folderIdOrPath, allowEmptyForRoot: true, parameterName: "folderIdOrPath", out var folderUuid, out var folderResolveError))
                {
                    Console.WriteLine($"[inventory-list] folder resolve failed folderIdOrPath='{folderIdOrPath ?? ""}': {folderResolveError}");
                    return InventoryQueryResult.FailResult(folderResolveError);
                }

                var owner = client.Self.AgentID;
                var entries = new List<InventoryBase>();
                Console.WriteLine($"[inventory-list] querying folder={folderUuid} owner={owner} recursive={recursive}");

                if (!TryGetInventoryFolderFromStore(store, folderUuid, out var folder))
                {
                    Console.WriteLine($"[inventory-list] folder not found in local store folder={folderUuid}");
                    return InventoryQueryResult.FailResult($"Folder {folderUuid} was not found in local inventory store.");
                }

                Console.WriteLine($"[inventory-list] folder found in local store folder={folderUuid} name='{folder.Name}'");
                entries.Add(folder);

                if (recursive)
                {
                    Console.WriteLine($"[inventory-list] performing recursive inventory fetch for folder={folderUuid}");
                    var folders = new List<InventoryFolder>();
                    var items = new List<InventoryItem>();
                    await client.Inventory.GetInventoryRecursiveAsync(folderUuid, owner, folders, items, token).ConfigureAwait(false);
                    entries.AddRange(folders);
                    entries.AddRange(items);
                    Console.WriteLine($"[inventory-list] recursive fetch completed folders={folders.Count} items={items.Count} totalEntries={entries.Count}");
                }
                else
                {
                    Console.WriteLine($"[inventory-list] performing non-recursive inventory fetch for folder={folderUuid}");
                    var contents = await client.Inventory
                        .FolderContentsAsync(folderUuid, owner, true, true, InventorySortOrder.ByName, token)
                        .ConfigureAwait(false);
                    entries.AddRange(contents);
                    Console.WriteLine($"[inventory-list] folder contents fetch completed children={contents.Count} totalEntries={entries.Count}");
                }

                var materialized = new List<InventoryEntry>(entries.Count);
                foreach (var entry in entries)
                {
                    materialized.Add(entry is InventoryItem item ? ToInventoryEntry(item) : ToInventoryEntry(entry));
                }

                Console.WriteLine($"[inventory-list] materialized={materialized.Count} elapsedMs={lockStopwatch.ElapsedMilliseconds}");
                return InventoryQueryResult.OkResult(
                    materialized,
                    $"Materialized {materialized.Count} inventory entries for folder {folderUuid}.");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"[inventory-list] canceled elapsedMs={overallStopwatch.ElapsedMilliseconds} canceledByCaller={cancellationToken.IsCancellationRequested} error={ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[inventory-list] failed elapsedMs={overallStopwatch.ElapsedMilliseconds} error={ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            Console.WriteLine($"[inventory-list] end elapsedMs={overallStopwatch.ElapsedMilliseconds}");
        }
    }

    private static InventoryQueryResult FilterInventoryEntries(
        IReadOnlyList<InventoryEntry> entries,
        string? nameContains,
        string? type,
        DateTimeOffset? createdAfter,
        DateTimeOffset? createdBefore,
        UUID? creatorUuid,
        int cursorOffset,
        int maxResults,
        int pageSize,
        out string? error)
    {
        error = null;
        var limit = Math.Clamp(maxResults <= 0 ? 25 : maxResults, 1, 10000);
        var filtered = entries
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Where(e => MatchesInventoryFilter(
                e,
                nameContains,
                type,
                createdAfter,
                createdBefore,
                creatorUuid))
            .Take(limit)
            .ToList();

        if (cursorOffset > filtered.Count)
        {
            error = $"cursor offset {cursorOffset} is beyond available results ({filtered.Count}).";
            return InventoryQueryResult.FailResult(error);
        }

        var page = filtered
            .Skip(cursorOffset)
            .Take(pageSize)
            .ToList();

        var nextOffset = cursorOffset + page.Count;
        var hasMore = nextOffset < filtered.Count;
        var nextCursor = hasMore ? EncodeInventoryCursor(nextOffset) : null;

        return InventoryQueryResult.OkResult(
            page,
            $"Returned {page.Count} inventory entries (offset={cursorOffset}, matched={filtered.Count}, pageSize={pageSize}, hasMore={hasMore.ToString().ToLowerInvariant()}).",
            nextCursor,
            hasMore,
            filtered.Count);
    }

    private void StoreInventoryListResult(string taskHandle, string resultHandle, InventoryQueryResult result)
    {
        lock (_inventoryListResultLock)
        {
            _inventoryListResultsByHandle[resultHandle] = result;
            _inventoryListResultHandleByTaskHandle[taskHandle] = resultHandle;

            if (!_inventoryListResultOrder.Contains(resultHandle, StringComparer.OrdinalIgnoreCase))
            {
                _inventoryListResultOrder.Enqueue(resultHandle);
            }

            while (_inventoryListResultOrder.Count > _inventoryListResultCacheLimit)
            {
                var expired = _inventoryListResultOrder.Dequeue();
                _inventoryListResultsByHandle.Remove(expired);
                var expiredTasks = _inventoryListResultHandleByTaskHandle
                    .Where(pair => string.Equals(pair.Value, expired, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var taskKey in expiredTasks)
                {
                    _inventoryListResultHandleByTaskHandle.Remove(taskKey);
                }
            }
        }
    }

    private bool TryGetInventoryListResult(string handle, out InventoryQueryResult result, out string error)
    {
        result = InventoryQueryResult.FailResult("Inventory list result not found.");
        error = "Inventory list result not found.";

        lock (_inventoryListResultLock)
        {
            if (_inventoryListResultsByHandle.TryGetValue(handle, out var stored))
            {
                result = stored;
                error = string.Empty;
                return true;
            }

            if (_inventoryListResultHandleByTaskHandle.TryGetValue(handle, out var canonicalHandle)
                && _inventoryListResultsByHandle.TryGetValue(canonicalHandle, out stored))
            {
                result = stored;
                error = string.Empty;
                return true;
            }
        }

        error = $"No materialized inventory list results were found for handle '{handle}'.";
        return false;
    }

    private bool RemoveInventoryListResult(string handle)
    {
        lock (_inventoryListResultLock)
        {
            var canonicalHandle = handle;
            if (!_inventoryListResultsByHandle.ContainsKey(handle)
                && !_inventoryListResultHandleByTaskHandle.TryGetValue(handle, out canonicalHandle))
            {
                _inventoryListDiscardedHandles.Add(handle);
                return false;
            }

            _inventoryListDiscardedHandles.Add(handle);
            _inventoryListDiscardedHandles.Add(canonicalHandle);
            _inventoryListResultsByHandle.Remove(canonicalHandle);
            _inventoryListResultOrder.Clear();
            foreach (var key in _inventoryListResultsByHandle.Keys.ToList())
            {
                _inventoryListResultOrder.Enqueue(key);
            }

            var taskAliases = _inventoryListResultHandleByTaskHandle
                .Where(pair => string.Equals(pair.Value, canonicalHandle, StringComparison.OrdinalIgnoreCase) || string.Equals(pair.Key, handle, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var taskKey in taskAliases)
            {
                _inventoryListResultHandleByTaskHandle.Remove(taskKey);
            }

            return true;
        }
    }

    private static string BuildInventoryListResultHandle(string? folderIdOrPath, bool recursive)
    {
        var normalizedFolder = string.IsNullOrWhiteSpace(folderIdOrPath)
            ? "<root>"
            : folderIdOrPath.Trim().Replace('\\', '/');

        var signature = $"{normalizedFolder}|recursive={recursive.ToString().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        return $"inventory-list:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private void RegisterInventoryListTaskHandle(string taskHandle, string resultHandle)
    {
        lock (_inventoryListResultLock)
        {
            _inventoryListResultHandleByTaskHandle[taskHandle] = resultHandle;
        }
    }

    public async Task<BotToolResult> InventoryCreateFolderAsync(
        string? parentFolderIdOrPath,
        string name,
        string? preferredType,
        CancellationToken cancellationToken)
    {
        var normalizedName = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BotToolResult.Fail("name is required.");
        }

        if (!TryParseFolderType(preferredType, out var parsedType, out var typeError))
        {
            return BotToolResult.Fail(typeError ?? "preferredType is invalid.");
        }

        return await ExecuteLockedAsync((client, token) =>
        {
            var store = client.Inventory.Store;
            var rootFolder = store?.RootFolder;
            if (store == null || rootFolder == null)
            {
                return Task.FromResult(BotToolResult.Fail("Inventory store is not initialized."));
            }

            if (!TryResolveInventoryFolderUuid(client, store, parentFolderIdOrPath, allowEmptyForRoot: true, parameterName: "parentFolderIdOrPath", out var parentUuid, out var resolveError))
            {
                return Task.FromResult(BotToolResult.Fail(resolveError));
            }

            var createdId = client.Inventory.CreateFolder(parentUuid, normalizedName, parsedType);
            return Task.FromResult(BotToolResult.OkResult(
                $"Created folder '{normalizedName}' ({createdId}) under parent {parentUuid} with preferredType={parsedType}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryRenameFolderAsync(string folderIdOrPath, string newName, CancellationToken cancellationToken)
    {
        var normalizedName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BotToolResult.Fail("newName is required.");
        }

        return await ExecuteLockedAsync((client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null)
            {
                return Task.FromResult(BotToolResult.Fail("Inventory store is not initialized."));
            }

            if (!TryResolveInventoryFolderUuid(client, store, folderIdOrPath, allowEmptyForRoot: false, parameterName: "folderIdOrPath", out var folderUuid, out var resolveError))
            {
                return Task.FromResult(BotToolResult.Fail(resolveError));
            }

            if (!TryGetInventoryFolderFromStore(store, folderUuid, out var folder))
            {
                return Task.FromResult(BotToolResult.Fail($"Inventory folder {folderUuid} was not found in local store."));
            }

            var oldName = folder.Name;
            if (string.Equals(oldName, normalizedName, StringComparison.Ordinal))
            {
                return Task.FromResult(BotToolResult.OkResult($"Folder '{oldName}' ({folderUuid}) already has that name."));
            }

            client.Inventory.UpdateFolderProperties(folder.UUID, folder.ParentUUID, normalizedName, folder.PreferredType);
            return Task.FromResult(BotToolResult.OkResult($"Rename request sent for folder '{oldName}' ({folderUuid}) -> '{normalizedName}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryRenameItemAsync(string itemId, string newName, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        var normalizedName = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BotToolResult.Fail("newName is required.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            var oldName = item.Name;
            if (string.Equals(oldName, normalizedName, StringComparison.Ordinal))
            {
                return BotToolResult.OkResult($"Item '{oldName}' ({itemUuid}) already has that name.");
            }

            item.Name = normalizedName;
            client.Inventory.RequestUpdateItem(item);
            return BotToolResult.OkResult($"Rename request sent for item '{oldName}' ({itemUuid}) -> '{normalizedName}'.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryMoveFolderAsync(string folderIdOrPath, string destinationParentFolderIdOrPath, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            var rootFolder = store?.RootFolder;
            if (store == null || rootFolder == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, folderIdOrPath, allowEmptyForRoot: false, parameterName: "folderIdOrPath", out var folderUuid, out var folderResolveError))
            {
                return BotToolResult.Fail(folderResolveError);
            }

            if (!TryResolveInventoryFolderUuid(client, store, destinationParentFolderIdOrPath, allowEmptyForRoot: false, parameterName: "destinationParentFolderIdOrPath", out var destinationParentUuid, out var destinationResolveError))
            {
                return BotToolResult.Fail(destinationResolveError);
            }

            if (!TryGetInventoryFolderFromStore(store, folderUuid, out var folder))
            {
                return BotToolResult.Fail($"Inventory folder {folderUuid} was not found in local store.");
            }

            if (!TryGetInventoryFolderFromStore(store, destinationParentUuid, out var destinationParent))
            {
                return BotToolResult.Fail($"Destination parent folder {destinationParentUuid} was not found in local store.");
            }

            if (folder.UUID == rootFolder.UUID)
            {
                return BotToolResult.Fail("Inventory root folder cannot be moved.");
            }

            if (folder.UUID == destinationParent.UUID)
            {
                return BotToolResult.Fail("A folder cannot be moved under itself.");
            }

            if (IsFolderDescendant(store, folder.UUID, destinationParent.UUID))
            {
                return BotToolResult.Fail("A folder cannot be moved under one of its descendants.");
            }

            await client.Inventory.MoveFolderAsync(folder.UUID, destinationParent.UUID, token).ConfigureAwait(false);
            return BotToolResult.OkResult(
                $"Move request sent for folder '{folder.Name}' ({folder.UUID}) -> parent '{destinationParent.Name}' ({destinationParent.UUID}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryMoveItemAsync(string itemId, string destinationFolderIdOrPath, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, destinationFolderIdOrPath, allowEmptyForRoot: false, parameterName: "destinationFolderIdOrPath", out var destinationFolderUuid, out var destinationResolveError))
            {
                return BotToolResult.Fail(destinationResolveError);
            }

            if (!TryGetInventoryFolderFromStore(store, destinationFolderUuid, out var destinationFolder))
            {
                return BotToolResult.Fail($"Destination folder {destinationFolderUuid} was not found in local store.");
            }

            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            await client.Inventory.MoveItemAsync(item.UUID, destinationFolder.UUID, token).ConfigureAwait(false);
            return BotToolResult.OkResult($"Move request sent for item '{item.Name}' ({item.UUID}) -> folder '{destinationFolder.Name}' ({destinationFolder.UUID}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryMoveManyAsync(string itemIdsCsv, string destinationFolderIdOrPath, CancellationToken cancellationToken)
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
            var store = client.Inventory.Store;
            if (store == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, destinationFolderIdOrPath, allowEmptyForRoot: false, parameterName: "destinationFolderIdOrPath", out var destinationFolderUuid, out var destinationResolveError))
            {
                return BotToolResult.Fail(destinationResolveError);
            }

            if (!TryGetInventoryFolderFromStore(store, destinationFolderUuid, out var destinationFolder))
            {
                return BotToolResult.Fail($"Destination folder {destinationFolderUuid} was not found in local store.");
            }

            var moveMap = new Dictionary<UUID, UUID>();
            var missing = 0;
            var alreadyInDestination = 0;
            foreach (var itemId in ids)
            {
                var item = await ResolveInventoryItemAsync(client, itemId, token).ConfigureAwait(false);
                if (item == null)
                {
                    missing++;
                    continue;
                }

                if (item.ParentUUID == destinationFolder.UUID)
                {
                    alreadyInDestination++;
                    continue;
                }

                moveMap[item.UUID] = destinationFolder.UUID;
            }

            if (moveMap.Count == 0)
            {
                return BotToolResult.OkResult(
                    $"No move requests sent. Missing/not-found: {missing}. Already in destination: {alreadyInDestination}.");
            }

            await client.Inventory.MoveItemsAsync(moveMap, token).ConfigureAwait(false);
            return BotToolResult.OkResult(
                $"Move requests sent for {moveMap.Count} inventory item(s) to folder '{destinationFolder.Name}' ({destinationFolder.UUID}). Missing/not-found: {missing}. Already in destination: {alreadyInDestination}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryCopyItemAsync(
        string itemId,
        string destinationFolderIdOrPath,
        string? newName,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        var normalizedNewName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, destinationFolderIdOrPath, allowEmptyForRoot: false, parameterName: "destinationFolderIdOrPath", out var destinationFolderUuid, out var destinationResolveError))
            {
                return BotToolResult.Fail(destinationResolveError);
            }

            if (!TryGetInventoryFolderFromStore(store, destinationFolderUuid, out var destinationFolder))
            {
                return BotToolResult.Fail($"Destination folder {destinationFolderUuid} was not found in local store.");
            }

            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            var copyName = normalizedNewName ?? item.Name;
            var copied = await client.Inventory.CopyItemAsync(item.UUID, destinationFolder.UUID, copyName, token).ConfigureAwait(false);
            if (copied is not InventoryItem copiedItem)
            {
                return BotToolResult.Fail($"Copy request for item {item.UUID} did not return a created inventory item.");
            }

            return BotToolResult.OkResult(
                $"Copied item '{item.Name}' ({item.UUID}) to folder '{destinationFolder.Name}' ({destinationFolder.UUID}) as '{copiedItem.Name}' ({copiedItem.UUID}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryLinkItemAsync(
        string itemId,
        string destinationFolderIdOrPath,
        string? linkName,
        string? linkDescription,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(itemId, out var itemUuid))
        {
            return BotToolResult.Fail("itemId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, destinationFolderIdOrPath, allowEmptyForRoot: false, parameterName: "destinationFolderIdOrPath", out var destinationFolderUuid, out var destinationResolveError))
            {
                return BotToolResult.Fail(destinationResolveError);
            }

            if (!TryGetInventoryFolderFromStore(store, destinationFolderUuid, out var destinationFolder))
            {
                return BotToolResult.Fail($"Destination folder {destinationFolderUuid} was not found in local store.");
            }

            var item = await ResolveInventoryItemAsync(client, itemUuid, token).ConfigureAwait(false);
            if (item == null)
            {
                return BotToolResult.Fail($"Inventory item {itemUuid} was not found.");
            }

            var effectiveLinkName = string.IsNullOrWhiteSpace(linkName) ? item.Name : linkName.Trim();
            var effectiveLinkDescription = string.IsNullOrWhiteSpace(linkDescription) ? item.Description : linkDescription.Trim();

            var createdLink = await client.Inventory.CreateLinkAsync(
                destinationFolder.UUID,
                item.UUID,
                effectiveLinkName,
                effectiveLinkDescription,
                item.InventoryType,
                UUID.Random(),
                token).ConfigureAwait(false);

            if (createdLink == null)
            {
                return BotToolResult.Fail($"Failed to create link for item {item.UUID} in folder {destinationFolder.UUID}.");
            }

            return BotToolResult.OkResult(
                $"Created link '{createdLink.Name}' ({createdLink.UUID}) in folder '{destinationFolder.Name}' ({destinationFolder.UUID}) -> item '{item.Name}' ({item.UUID}).");
        }, cancellationToken).ConfigureAwait(false);
    }
    
    public static string GetBasePath(string path) {
        var pathParts = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if(pathParts.Count == 0) {
            return string.Empty;
        }
        else {
            return pathParts[pathParts.Count - 1];
        }
    }
    
    public static string GetParentPath(string path) {
        var pathParts = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if(pathParts.Count < 2) {
            return string.Empty;
        }
        else {
            pathParts.RemoveAt(pathParts.Count - 1);
            return string.Join('/', pathParts);
        }
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
        string folderIdOrPath,
        string recipientAgentId,
        bool withBeamEffect,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(recipientAgentId, out var recipientUuid))
        {
            return BotToolResult.Fail("recipientAgentId is not a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, folderIdOrPath, allowEmptyForRoot: false, parameterName: "folderIdOrPath", out var folderUuid, out var resolveError))
            {
                return BotToolResult.Fail(resolveError);
            }

            if (!TryGetInventoryFolderFromStore(store, folderUuid, out var folder))
            {
                return BotToolResult.Fail($"Inventory folder {folderUuid} was not found in local store.");
            }

            Console.WriteLine($"[inventory] Giving folder {folder.Name} ({folder.UUID}) to {recipientUuid}.");
                
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

    public async Task<BotToolResult> InventoryDeleteFolderAsync(string folderIdOrPath, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var store = client.Inventory.Store;
            if (store == null)
            {
                return BotToolResult.Fail("Inventory store is not initialized.");
            }

            if (!TryResolveInventoryFolderUuid(client, store, folderIdOrPath, allowEmptyForRoot: false, parameterName: "folderIdOrPath", out var folderUuid, out var folderResolveError))
            {
                return BotToolResult.Fail(folderResolveError);
            }

            await client.Inventory
                .MoveFolderAsync(folderUuid, client.Inventory.FindFolderForType(FolderType.Trash), token)
                .ConfigureAwait(false);

            if (TryGetInventoryFolderFromStore(store, folderUuid, out var folder))
            {
                return BotToolResult.OkResult($"Moved inventory folder '{folder.Name}' ({folderUuid}) to Trash.");
            }

            return BotToolResult.OkResult($"Moved inventory folder '{folderUuid}' to Trash.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> InventoryEmptyTrashAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Inventory.EmptyTrashAsync(token).ConfigureAwait(false);
            return BotToolResult.OkResult("Trash empty request sent.");
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
        string? destinationFolderIdOrPath,
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
            if (string.IsNullOrWhiteSpace(destinationFolderIdOrPath))
            {
                destinationFolderUuid = client.Inventory.FindFolderForType(taskItem.AssetType);
                if (destinationFolderUuid == UUID.Zero)
                {
                    return BotToolResult.Fail($"No default destination folder was found for asset type {taskItem.AssetType}.");
                }
            }
            else
            {
                var store = client.Inventory.Store;
                if (store == null)
                {
                    return BotToolResult.Fail("Inventory store is not initialized.");
                }

                if (!TryResolveInventoryFolderUuid(client, store, destinationFolderIdOrPath, allowEmptyForRoot: false, parameterName: "destinationFolderIdOrPath", out destinationFolderUuid, out var destinationResolveError))
                {
                    return BotToolResult.Fail(destinationResolveError);
                }
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
        string? folderIdOrPath,
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
            if (!string.IsNullOrWhiteSpace(folderIdOrPath))
            {
                var store = client.Inventory.Store;
                if (store == null)
                {
                    return AssetTransferResult.FailResult("Inventory store is not initialized.");
                }

                if (!TryResolveInventoryFolderUuid(client, store, folderIdOrPath, allowEmptyForRoot: false, parameterName: "folderIdOrPath", out folderUuid, out var folderResolveError))
                {
                    return AssetTransferResult.FailResult(folderResolveError);
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

    public Task<(bool Exists, string? FolderId, string? Error)> TryResolveFolderPathAsync(IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (segments == null || segments.Count == 0)
        {
            return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((false, null, "Inventory folder path must contain at least one segment."));
        }

        if (_client?.Inventory?.Store?.RootFolder == null)
        {
            return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((false, null, "Inventory root folder is not initialized."));
        }

        var normalizedSegments = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Select(segment => segment.Trim())
            .ToList();

        if (normalizedSegments.Count == 0)
        {
            return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((false, null, "Inventory folder path must contain at least one segment."));
        }

        if (normalizedSegments[0].Equals("Inventory", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSegments.RemoveAt(0);
        }

        if (normalizedSegments.Count == 0)
        {
            return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((true, _client.Inventory.Store.RootFolder.UUID.ToString(), null));
        }

        var found = _client.Inventory.LocalFind(_client.Inventory.Store.RootFolder.UUID, normalizedSegments.ToArray(), 0, true);
        var matchedFolders = found.OfType<InventoryFolder>().ToList();
        if (matchedFolders.Count == 1)
        {
            return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((true, matchedFolders[0].UUID.ToString(), null));
        }

        if (matchedFolders.Count == 0)
        {
            return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((false, null, null));
        }

        return Task.FromResult<(bool Exists, string? FolderId, string? Error)>((false, null, $"Inventory folder path '{string.Join("/", normalizedSegments)}' is ambiguous ({matchedFolders.Count} matches). Use a folder UUID instead."));
    }

    public async Task<BotToolResult?> EnsureFolderPathExistsAsync(IReadOnlyList<string> segments,
        CancellationToken cancellationToken)
    {
        string? parentFolderId = null;
        var segmentPath = new List<string>(segments.Count);

        foreach (var segment in segments)
        {
            segmentPath.Add(segment);
            var listing = await ListFolderAsync(parentFolderId).ConfigureAwait(false);
            if (!listing.Ok)
            {
                return BotToolResult.Fail($"Failed to list inventory folder while preparing import path: {listing.Message}");
            }

            Console.WriteLine($"[folders] ensure-path inspect segment='{segment}' parent='{parentFolderId ?? "<root>"}' listingFolders={DescribeFolderEntries(listing)}");

            var child = listing.Entries.FirstOrDefault(e =>
                e.Kind == "folder" &&
                string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Id, parentFolderId, StringComparison.OrdinalIgnoreCase));

            if (child != null)
            {
                parentFolderId = child.Id;
                continue;
            }

            var createResult = await InventoryCreateFolderAsync(parentFolderId, segment, null, cancellationToken).ConfigureAwait(false);
            if (!createResult.Ok)
            {
                return BotToolResult.Fail($"Failed to create inventory folder '{segment}' in '{string.Join("/", segments)}': {createResult.Message}");
            }

            var createdFolderId = TryExtractCreatedFolderId(createResult.Message);
            Console.WriteLine($"[folders] ensure-path created segment='{segment}' parent='{parentFolderId ?? "<root>"}' createdFolderId='{createdFolderId ?? "<unknown>"}' createMessage='{createResult.Message}'");

            // Local inventory cache can lag after folder creation; retry resolution with short backoff.
            const int resolveAttempts = 8;
            const int resolveDelayMs = 250;
            InventoryQueryResult? lastAfterCreate = null;
            string? resolvedChildId = null;

            for (var attempt = 1; attempt <= resolveAttempts; attempt++)
            {
                var afterCreate = await ListFolderAsync(parentFolderId).ConfigureAwait(false);
                if (!afterCreate.Ok)
                {
                    return BotToolResult.Fail($"Created folder '{segment}', but failed to verify it in inventory: {afterCreate.Message}");
                }

                lastAfterCreate = afterCreate;
                var createdChild = afterCreate.Entries.FirstOrDefault(e =>
                    e.Kind == "folder" &&
                    string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(e.Id, parentFolderId, StringComparison.OrdinalIgnoreCase));

                if (createdChild != null)
                {
                    resolvedChildId = createdChild.Id;
                    Console.WriteLine($"[folders] ensure-path resolved segment='{segment}' as folderId='{resolvedChildId}' after attempt={attempt}");
                    break;
                }

                // Fallback: if create returned a UUID, probe that folder directly and continue if it resolves.
                if (!string.IsNullOrWhiteSpace(createdFolderId))
                {
                    var directProbe = await ListFolderAsync(createdFolderId).ConfigureAwait(false);
                    if (directProbe.Ok)
                    {
                        resolvedChildId = createdFolderId;
                        Console.WriteLine($"[folders] ensure-path fallback-resolved segment='{segment}' from createResult UUID='{resolvedChildId}' on attempt={attempt}");
                        break;
                    }

                    Console.WriteLine($"[folders] ensure-path fallback probe failed for createdFolderId='{createdFolderId}' attempt={attempt}: {directProbe.Message}");
                }

                Console.WriteLine($"[folders] ensure-path unresolved segment='{segment}' attempt={attempt}/{resolveAttempts} parent='{parentFolderId ?? "<root>"}' listingFolders={DescribeFolderEntries(afterCreate)}");

                if (attempt < resolveAttempts)
                {
                    await Task.Delay(resolveDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedChildId))
            {
                var attemptedPath = string.Join("/", segmentPath);
                var listingDebug = lastAfterCreate == null
                    ? "<no listing captured>"
                    : DescribeFolderEntries(lastAfterCreate);

                return BotToolResult.Fail(
                    $"Created folder '{segment}' while preparing '{string.Join("/", segments)}', but it could not be resolved in local inventory after retries. " +
                    $"Attempted path='{attemptedPath}', parentFolderId='{parentFolderId ?? "<root>"}', createdFolderId='{createdFolderId ?? "<unknown>"}', visibleFolders={listingDebug}");
            }

            parentFolderId = resolvedChildId;
        }

        return null;
    }

    private async Task<InventoryQueryResult> ListFolderAsync(string? parentFolderId, CancellationToken cancellationToken = default)
    {
        var listing = await FetchInventoryListAsync(parentFolderId, false, cancellationToken).ConfigureAwait(false);
        return listing.Ok ? InventoryQueryResult.OkResult(listing.Entries, listing.Message) : listing;
    }

    private static string DescribeFolderEntries(InventoryQueryResult listing, int maxEntries = 12)
    {
        if (listing.Entries.Count == 0)
        {
            return "<empty>";
        }

        var sample = listing.Entries
            .Where(e => e.Kind == "folder")
            .Take(maxEntries)
            .Select(e => $"{e.Name} ({e.Id})")
            .ToList();

        if (sample.Count == 0)
        {
            return "<no-folders>";
        }

        var suffix = listing.Entries.Count > sample.Count ? $" ... +{listing.Entries.Count - sample.Count} more" : string.Empty;
        return string.Join(", ", sample) + suffix;
    }

    private static string? TryExtractCreatedFolderId(string createMessage)
    {
        if (string.IsNullOrWhiteSpace(createMessage))
        {
            return null;
        }

        var open = createMessage.IndexOf('(');
        if (open < 0)
        {
            return null;
        }

        var close = createMessage.IndexOf(')', open + 1);
        if (close <= open + 1)
        {
            return null;
        }

        var candidate = createMessage.Substring(open + 1, close - open - 1).Trim();
        return UUID.TryParse(candidate, out var parsed) ? parsed.ToString() : null;
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
        EmitRuntimeEvent(
            "general",
            "inventory.offer.decision",
            "opensim",
            $"Inventory offer from {fromName} was {decision}.",
            new Dictionary<string, string?>
            {
                ["fromAgentId"] = fromAgentId,
                ["fromName"] = fromName,
                ["assetType"] = e.AssetType.ToString(),
                ["fromTask"] = e.FromTask.ToString(),
                ["decision"] = decision,
                ["reason"] = reason,
                ["matchedRuleId"] = matchedRule?.Id.ToString(CultureInfo.InvariantCulture),
                ["matchedRuleName"] = matchedRule?.Name,
                ["destinationFolderId"] = destinationFolder.ToString(),
                ["objectId"] = e.ObjectID.ToString(),
                ["message"] = offerMessage
            });

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
                Console.WriteLine("[prompt] ignored AGENTS.md notecard offer because handler-only mode is enabled but no handler/parent controller is configured.");
                return;
            }

            if (!IsHandlerAvatar(fromName))
            {
                Console.WriteLine($"[prompt] ignored AGENTS.md notecard offer from '{fromName}' because handler/parent-only install mode is enabled.");
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

    private static bool TryGetInventoryFolderFromStore(Inventory store, UUID folderId, out InventoryFolder folder)
    {
        folder = default!;
        if (!store.TryGetValue(folderId, out var node) || node is not InventoryFolder typed)
        {
            return false;
        }

        folder = typed;
        return true;
    }

    private static bool TryResolveInventoryFolderUuid(
        GridClient client,
        Inventory store,
        string? folderIdOrPath,
        bool allowEmptyForRoot,
        string parameterName,
        out UUID folderUuid,
        out string error)
    {
        folderUuid = UUID.Zero;
        error = string.Empty;

        if (store.RootFolder == null)
        {
            error = "Inventory root folder is not initialized.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(folderIdOrPath))
        {
            if (!allowEmptyForRoot)
            {
                error = $"{parameterName} is required.";
                return false;
            }

            folderUuid = store.RootFolder.UUID;
            return true;
        }

        var normalized = folderIdOrPath.Trim();
        if (UUID.TryParse(normalized, out var parsedFolderId))
        {
            if (!TryGetInventoryFolderFromStore(store, parsedFolderId, out _))
            {
                error = $"Inventory folder {parsedFolderId} was not found in local store.";
                return false;
            }

            folderUuid = parsedFolderId;
            return true;
        }

        var segments = normalized
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (segments.Count > 0 && segments[0].Equals("Inventory", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }

        if (segments.Count == 0)
        {
            if (!allowEmptyForRoot)
            {
                    error = $"{parameterName} must include at least one folder name when using path syntax.";
                return false;
            }

            folderUuid = store.RootFolder.UUID;
            return true;
        }

        var found = client.Inventory.LocalFind(store.RootFolder.UUID, segments.ToArray(), 0, true);
        var matchedFolders = found.OfType<InventoryFolder>().ToList();
        if (matchedFolders.Count == 1)
        {
            folderUuid = matchedFolders[0].UUID;
            return true;
        }

        if (matchedFolders.Count == 0)
        {
            error = $"Inventory folder path '{folderIdOrPath}' was not found in local store.";
            return false;
        }

        error = $"Inventory folder path '{folderIdOrPath}' is ambiguous ({matchedFolders.Count} matches). Use a folder UUID instead.";
        return false;
    }

    private static bool TryResolveOutfitsRootFolder(
        GridClient client,
        Inventory store,
        out InventoryFolder outfitsRootFolder,
        out string error)
    {
        outfitsRootFolder = default!;
        error = string.Empty;

        var typedFolderId = client.Inventory.FindFolderForType(FolderType.MyOutfits);
        if (typedFolderId != UUID.Zero && TryGetInventoryFolderFromStore(store, typedFolderId, out outfitsRootFolder))
        {
            return true;
        }

        var root = store.RootFolder;
        if (root == null)
        {
            error = "Inventory root folder is not initialized.";
            return false;
        }

        List<InventoryBase> rootContents;
        try
        {
            rootContents = store.GetContents(root.UUID);
        }
        catch (Exception ex)
        {
            error = $"Failed reading inventory root folder from local store: {ex.Message}";
            return false;
        }

        var candidates = rootContents
            .OfType<InventoryFolder>()
            .Where(folder =>
                folder.PreferredType == FolderType.MyOutfits
                || string.Equals(folder.Name?.Trim(), "My Outfits", StringComparison.OrdinalIgnoreCase)
                || string.Equals(folder.Name?.Trim(), "Outfits", StringComparison.OrdinalIgnoreCase))
            .GroupBy(folder => folder.UUID)
            .Select(group => group.First())
            .ToList();

        if (candidates.Count == 1)
        {
            outfitsRootFolder = candidates[0];
            return true;
        }

        if (candidates.Count == 0)
        {
            error = "Could not locate the outfits root folder (expected folder type MyOutfits or root folder named 'My Outfits'/'Outfits').";
            return false;
        }

        error = $"Outfits root folder is ambiguous ({candidates.Count} matches). Candidate UUIDs: {string.Join(", ", candidates.Select(folder => folder.UUID.ToString()))}.";
        return false;
    }

    private static bool IsFolderDescendant(Inventory store, UUID ancestorFolderId, UUID possibleDescendantFolderId)
    {
        var visited = new HashSet<UUID>();
        var current = possibleDescendantFolderId;

        while (current != UUID.Zero && visited.Add(current))
        {
            if (!TryGetInventoryFolderFromStore(store, current, out var folder))
            {
                return false;
            }

            if (folder.ParentUUID == ancestorFolderId)
            {
                return true;
            }

            current = folder.ParentUUID;
        }

        return false;
    }

    private static bool TryParseFolderType(string? raw, out FolderType folderType, out string? error)
    {
        folderType = FolderType.None;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (Enum.TryParse<FolderType>(raw.Trim(), true, out var parsed))
        {
            folderType = parsed;
            return true;
        }

        error = $"Unsupported preferredType '{raw}'.";
        return false;
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

            // Apply directly through AppearanceManager to avoid COF/CAPS fetch instability
            // being on the critical path for actually wearing the item.
            client.Appearance.AddToOutfit(wearable, replaceExistingSlot);
            await client.Appearance.RequestSetAppearance(forceRebake: true).ConfigureAwait(false);

            // Best effort COF persistence so subsequent sessions still discover the link.
            try
            {
                var cof = GetSharedCurrentOutfitFolder(client);
                await cof.GetCurrentOutfitLinksAsync(token).ConfigureAwait(false);
                await cof.AddToOutfitAsync(wearable, replace: replaceExistingSlot, cancellationToken: token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[appearance] COF sync for wearable '{wearable.Name}' failed after apply: {ex.Message}");
            }

            return BotToolResult.OkResult(
                $"Wear request sent for wearable '{wearable.Name}' ({wearable.UUID}), type={wearable.WearableType}, replace={replaceExistingSlot}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<(
        bool Ok,
        InventoryFolder? SetupFolder,
        InventoryFolder? ImportedFolder,
        IReadOnlyList<InventoryItem> WearableItems,
        IReadOnlyList<InventoryItem> AttachmentItems,
        string? Error)> ResolveSetupProvisioningItemsAsync(
        GridClient client,
        string? setupFolderName,
        CancellationToken cancellationToken)
    {
        static Task<(bool Ok, InventoryFolder? SetupFolder, InventoryFolder? ImportedFolder, IReadOnlyList<InventoryItem> WearableItems, IReadOnlyList<InventoryItem> AttachmentItems, string? Error)> Result(
            bool ok,
            InventoryFolder? setupFolder,
            InventoryFolder? importedFolder,
            IReadOnlyList<InventoryItem> wearableItems,
            IReadOnlyList<InventoryItem> attachmentItems,
            string? error)
            => Task.FromResult<(bool Ok, InventoryFolder? SetupFolder, InventoryFolder? ImportedFolder, IReadOnlyList<InventoryItem> WearableItems, IReadOnlyList<InventoryItem> AttachmentItems, string? Error)>((
                ok,
                setupFolder,
                importedFolder,
                wearableItems,
                attachmentItems,
                error));

        var effectiveSetupFolderName = string.IsNullOrWhiteSpace(setupFolderName)
            ? "Setup"
            : setupFolderName.Trim();

        var inventoryStore = client.Inventory.Store;
        if (inventoryStore == null)
        {
            return Result(false, null, null, Array.Empty<InventoryItem>(), Array.Empty<InventoryItem>(), "Inventory store is not initialized.");
        }

        var rootFolder = inventoryStore.RootFolder;
        if (rootFolder == null)
        {
            return Result(false, null, null, Array.Empty<InventoryItem>(), Array.Empty<InventoryItem>(), "Inventory root folder is not initialized.");
        }

        List<InventoryBase> rootContents;
        try
        {
            rootContents = inventoryStore.GetContents(rootFolder.UUID);
        }
        catch (Exception ex)
        {
            return Result(false, null, null, Array.Empty<InventoryItem>(), Array.Empty<InventoryItem>(),
                $"Failed reading inventory root folder from local store: {ex.Message}");
        }

        var setupCandidates = rootContents
            .OfType<InventoryFolder>()
            .Where(f => string.Equals(f.Name?.Trim(), effectiveSetupFolderName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (setupCandidates.Count == 0)
        {
            return Result(false, null, null, Array.Empty<InventoryItem>(), Array.Empty<InventoryItem>(),
                $"Required root inventory folder '{effectiveSetupFolderName}' was not found in local store.");
        }

        if (setupCandidates.Count > 1)
        {
            return Result(false, null, null, Array.Empty<InventoryItem>(), Array.Empty<InventoryItem>(),
                $"Root inventory folder name '{effectiveSetupFolderName}' is ambiguous ({setupCandidates.Count} matches).");
        }

        var setupFolder = setupCandidates[0];

        var provisioningFolder = setupFolder;

        var descendantFolderIds = new HashSet<UUID> { provisioningFolder.UUID };
        var discoveredItems = new List<InventoryItem>();
        var pending = new Queue<UUID>();
        pending.Enqueue(provisioningFolder.UUID);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Dequeue();
            List<InventoryBase> contents;
            try
            {
                contents = inventoryStore.GetContents(current);
            }
            catch
            {
                continue;
            }

            foreach (var node in contents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (node is InventoryFolder childFolder)
                {
                    if (descendantFolderIds.Add(childFolder.UUID))
                    {
                        pending.Enqueue(childFolder.UUID);
                    }

                    continue;
                }

                if (node is InventoryItem inventoryItem)
                {
                    discoveredItems.Add(inventoryItem);
                }
            }
        }

        var itemsInSetupImport = discoveredItems
            .Select(i => ResolveLinkedInventoryItem(inventoryStore, i))
            .ToList();

        var wearableItems = itemsInSetupImport
            .OfType<InventoryWearable>()
            .Cast<InventoryItem>()
            .ToList();
        var attachmentItems = itemsInSetupImport
            .Where(i => i.AssetType == AssetType.Object)
            .ToList();

        return Result(true, setupFolder, provisioningFolder, wearableItems, attachmentItems, null);
    }

    private async Task<List<WearableInfo>> CollectWornWearablesAsync(
        GridClient client,
        bool includeAttachmentsAndWearables,
        bool includeCurrentOutfit,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, WearableInfo>(StringComparer.OrdinalIgnoreCase);

        void AddWearable(WearableInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.ItemId))
            {
                return;
            }

            if (!merged.TryGetValue(info.ItemId, out var existing))
            {
                merged[info.ItemId] = info;
                return;
            }

            if (string.IsNullOrWhiteSpace(existing.AssetId) && !string.IsNullOrWhiteSpace(info.AssetId))
            {
                merged[info.ItemId] = info;
            }
        }

        if (includeAttachmentsAndWearables)
        {
            foreach (var wearable in client.Appearance.GetWearables())
            {
                AddWearable(new WearableInfo(
                    wearable.ItemID.ToString(),
                    wearable.AssetID.ToString(),
                    wearable.WearableType.ToString(),
                    wearable.AssetType.ToString(),
                    "appearance-snapshot"));
            }
        }

        if (includeCurrentOutfit)
        {
            try
            {
                // Merge COF links when available, but do not fail worn-state collection if
                // FetchInventory2/CAPS is unstable.
                var cof = GetSharedCurrentOutfitFolder(client);
                var links = await cof.GetCurrentOutfitLinksAsync(cancellationToken).ConfigureAwait(false);
                foreach (var link in links)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var resolved = ResolveLinkedInventoryItem(client.Inventory.Store, link);
                    if (resolved is InventoryWearable wearable)
                    {
                        AddWearable(new WearableInfo(
                            wearable.UUID.ToString(),
                            wearable.AssetUUID.ToString(),
                            wearable.WearableType.ToString(),
                            wearable.AssetType.ToString(),
                            "cof-link-resolved"));
                        continue;
                    }

                    if (link.InventoryType != InventoryType.Wearable || link.ResolvedItemID == UUID.Zero)
                    {
                        continue;
                    }

                    AddWearable(new WearableInfo(
                        link.ResolvedItemID.ToString(),
                        string.Empty,
                        "unknown",
                        "unknown",
                        "cof-link-unresolved"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[appearance] COF wearable inspection failed; using appearance snapshot only: {ex.Message}");
            }
        }

        return merged.Values
            .OrderBy(w => w.WearableType, StringComparer.Ordinal)
            .ThenBy(w => w.ItemId, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCurrentOutfitFolderLink(InventoryItem item)
    {
        return item.IsLink()
            && (item.AssetType == AssetType.LinkFolder
                || item.InventoryType == InventoryType.Category
                || item.InventoryType == InventoryType.Folder);
    }

    private static UUID GetCurrentOutfitFolderLinkTargetId(InventoryItem item)
    {
        return item.AssetUUID != UUID.Zero ? item.AssetUUID : item.ResolvedItemID;
    }

    private static void SendCurrentOutfitFolderLinkCreatePacket(
        GridClient client,
        UUID cofFolderId,
        UUID outfitFolderId,
        string? outfitName,
        InventoryType inventoryType)
    {
        var packet = new LinkInventoryItemPacket
        {
            AgentData =
            {
                AgentID = client.Self.AgentID,
                SessionID = client.Self.SessionID
            },
            InventoryBlock =
            {
                CallbackID = 0,
                FolderID = cofFolderId,
                TransactionID = UUID.Random(),
                OldItemID = outfitFolderId,
                Type = (sbyte)AssetType.LinkFolder,
                InvType = (sbyte)inventoryType,
                Name = Utils.StringToBytes(outfitName ?? string.Empty),
                Description = Utils.StringToBytes(string.Empty)
            }
        };

        client.Network.SendPacket(packet);
        Console.WriteLine($"[appearance-wear-outfit] sent LinkInventoryItemPacket cofId={cofFolderId} outfitId={outfitFolderId} invType={inventoryType}");
    }

    private async Task<List<InventoryFolder>> GetCurrentOutfitChildFoldersAsync(
        GridClient client,
        UUID cofFolderId,
        CancellationToken cancellationToken)
    {
        var entries = await client.Inventory
            .FolderContentsAsync(cofFolderId, client.Self.AgentID, true, true, InventorySortOrder.ByName, cancellationToken)
            .ConfigureAwait(false);

        return entries
            .OfType<InventoryFolder>()
            .Where(folder => folder.ParentUUID == cofFolderId)
            .ToList();
    }

    private static bool IsLikelyOutfitFolderLink(InventoryFolder folder, IReadOnlySet<string> knownOutfitNames)
    {
        return knownOutfitNames.Contains((folder.Name ?? string.Empty).Trim());
    }

    private static bool IsFolderNameMatch(string? left, string? right)
    {
        return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> WaitForCurrentOutfitFolderLinkPresenceAsync(
        GridClient client,
        UUID cofFolderId,
        UUID outfitFolderId,
        string outfitName,
        IReadOnlySet<string> knownOutfitNames,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = await client.Inventory
                .FolderContentsAsync(cofFolderId, client.Self.AgentID, true, false, InventorySortOrder.ByName, cancellationToken)
                .ConfigureAwait(false);

            var links = await GetSharedCurrentOutfitFolder(client).GetCurrentOutfitLinksAsync(cancellationToken).ConfigureAwait(false);
            var matches = links
                .Where(IsCurrentOutfitFolderLink)
                .Where(link => GetCurrentOutfitFolderLinkTargetId(link) == outfitFolderId)
                .ToList();
            if (matches.Count > 0)
            {
                return true;
            }

            var directFolders = await GetCurrentOutfitChildFoldersAsync(client, cofFolderId, cancellationToken).ConfigureAwait(false);
            var hasMatchingDirectFolder = directFolders
                .Where(folder => IsLikelyOutfitFolderLink(folder, knownOutfitNames))
                .Any(folder => IsFolderNameMatch(folder.Name, outfitName));
            if (hasMatchingDirectFolder)
            {
                return true;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsWearableItemPresent(
        InventoryItem candidate,
        IReadOnlySet<string> wornWearableIds)
    {
        return wornWearableIds.Contains(candidate.UUID.ToString());
    }

    private static string GetWearableMatchMode(
        InventoryItem candidate,
        IReadOnlySet<string> wornWearableIds)
    {
        return wornWearableIds.Contains(candidate.UUID.ToString()) ? "item" : "missing";
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
                InventoryType.Folder.ToString(),
                null,
                null);
        }

        if (entry is InventoryItem item)
        {
            return new InventoryEntry(
                item.UUID.ToString(),
                item.ParentUUID.ToString(),
                item.Name,
                "item",
                item.AssetType.ToString(),
                item.InventoryType.ToString(),
                item.CreatorID == UUID.Zero ? null : item.CreatorID.ToString(),
                item.CreationDate == default
                    ? null
                    : item.CreationDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        return new InventoryEntry(
            entry.UUID.ToString(),
            entry.ParentUUID.ToString(),
            entry.Name,
            "unknown",
            AssetType.Unknown.ToString(),
            InventoryType.Unknown.ToString(),
            null,
            null);
    }

    private static bool MatchesInventoryFilter(
        InventoryEntry entry,
        string? nameContains,
        string? type,
        DateTimeOffset? createdAfter,
        DateTimeOffset? createdBefore,
        UUID? creatorUuid)
    {
        if (!string.IsNullOrWhiteSpace(nameContains)
            && entry.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(type)
            && !entry.Kind.Equals(type, StringComparison.OrdinalIgnoreCase)
            && !entry.AssetType.Equals(type, StringComparison.OrdinalIgnoreCase)
            && !entry.InventoryType.Equals(type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var needsItemMetadata = createdAfter.HasValue || createdBefore.HasValue || creatorUuid.HasValue;
        if (needsItemMetadata && !entry.Kind.Equals("item", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (creatorUuid.HasValue)
        {
            if (string.IsNullOrWhiteSpace(entry.CreatorId)
                || !entry.CreatorId.Equals(creatorUuid.Value.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (createdAfter.HasValue || createdBefore.HasValue)
        {
            if (string.IsNullOrWhiteSpace(entry.CreatedUtc)
                || !DateTimeOffset.TryParse(entry.CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var createdAt))
            {
                return false;
            }

            if (createdAfter.HasValue && createdAt < createdAfter.Value)
            {
                return false;
            }

            if (createdBefore.HasValue && createdAt > createdBefore.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseOptionalUtc(string? value, string fieldName, out DateTimeOffset? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedValue))
        {
            error = $"{fieldName} must be a valid ISO-8601 timestamp.";
            return false;
        }

        parsed = parsedValue;
        return true;
    }

    private static bool TryDecodeInventoryCursor(string? cursor, out int offset, out string? error)
    {
        offset = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        if (int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset) && parsedOffset >= 0)
        {
            offset = parsedOffset;
            return true;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            const string prefix = "offset:";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(raw[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedOffset)
                && parsedOffset >= 0)
            {
                offset = parsedOffset;
                return true;
            }
        }
        catch (FormatException)
        {
            // Deliberately ignored: invalid base64 should produce a single validation error below.
        }

        error = "cursor is invalid. Use the NextCursor value from a prior InventoryList response.";
        return false;
    }

    private static string EncodeInventoryCursor(int offset)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"offset:{offset}"));
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
    }    private async Task<WearableDirectControlResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<WearableDirectControlResult>> action,
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
            return WearableDirectControlResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AttachmentPointMappingResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AttachmentPointMappingResult>> action,
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
            return AttachmentPointMappingResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AttachmentObjectResolutionResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AttachmentObjectResolutionResult>> action,
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
            return AttachmentObjectResolutionResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceVisualParamsResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceVisualParamsResult>> action,
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
            return AppearanceVisualParamsResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceVisualParamSetResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceVisualParamSetResult>> action,
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
            return AppearanceVisualParamSetResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async Task<AppearanceBakeDiagnosticsResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AppearanceBakeDiagnosticsResult>> action,
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
            return AppearanceBakeDiagnosticsResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }
}

internal sealed record InventoryEntry(
    string Id,
    string ParentId,
    string Name,
    string Kind,
    string AssetType,
    string InventoryType,
    string? CreatorId,
    string? CreatedUtc);

internal sealed record InventoryQueryResult(
    bool Ok,
    string Message,
    IReadOnlyList<InventoryEntry> Entries,
    string? NextCursor,
    bool HasMore,
    int TotalMatched)
{
    public static InventoryQueryResult OkResult(
        IReadOnlyList<InventoryEntry> entries,
        string message,
        string? nextCursor = null,
        bool hasMore = false,
        int? totalMatched = null)
        => new(true, message, entries, nextCursor, hasMore, totalMatched ?? entries.Count);

    public static InventoryQueryResult FailResult(string message)
        => new(false, message, Array.Empty<InventoryEntry>(), null, false, 0);
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

internal sealed record WearableInfo(string ItemId, string AssetId, string WearableType, string AssetType, string Source);

internal sealed record AttachmentInfo(string ItemId, string AttachmentPoint, string? ObjectId, uint? ObjectLocalId);

internal sealed record WearAttachmentIniConfig(
    AttachmentPoint? AttachPoint,
    float? OffsetX,
    float? OffsetY,
    float? OffsetZ,
    float? RotateX,
    float? RotateY,
    float? RotateZ,
    float? ScaleX,
    float? ScaleY,
    float? ScaleZ)
{
    public bool HasTransformValues =>
        OffsetX.HasValue || OffsetY.HasValue || OffsetZ.HasValue
        || RotateX.HasValue || RotateY.HasValue || RotateZ.HasValue
        || ScaleX.HasValue || ScaleY.HasValue || ScaleZ.HasValue;
}

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
    bool? CompiledHint,
    IReadOnlyList<string> CompileMessages)
{
    public static ScriptUpdateResult OkResult(
        string itemId,
        string assetId,
        int sourceBytes,
        string uploadStatus,
        bool? compiledHint,
        IReadOnlyList<string> compileMessages,
        string message)
        => new(true, message, itemId, assetId, sourceBytes, uploadStatus, compiledHint, compileMessages);

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
