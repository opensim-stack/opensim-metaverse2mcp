using LibreMetaverse;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opensim.Metaverse2Mcp;

/// <summary>
/// Cheap "senses" for AI agents: child prims of an object, avatars nearby, and a
/// name search over the already-cached inventory skeleton. None of these talk to
/// the simulator; they read the client's caches so they return instantly.
/// </summary>
internal sealed partial class BotSession
{
    private static readonly JsonSerializerOptions SenseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public async Task<PrimQueryResult> ListPrimChildrenAsync(uint localId, bool recursive, int maxResults, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(PrimQueryResult.FailResult("No current simulator available."));
            }

            var limit = Math.Clamp(maxResults, 1, 2000);
            var at = client.Self.SimPosition;
            var byParent = sim.ObjectsPrimitives.Values
                .Where(p => p != null && p.ParentID != 0)
                .GroupBy(p => p.ParentID)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.LocalID).ToList());

            var result = new List<PrimSummary>();
            var queue = new Queue<uint>();
            queue.Enqueue(localId);
            var seen = new HashSet<uint> { localId };
            while (queue.Count > 0 && result.Count < limit)
            {
                var parent = queue.Dequeue();
                if (!byParent.TryGetValue(parent, out var kids))
                {
                    continue;
                }

                foreach (var kid in kids)
                {
                    if (result.Count >= limit)
                    {
                        break;
                    }

                    result.Add(ToPrimSummary(kid, at));
                    if (recursive && seen.Add(kid.LocalID))
                    {
                        queue.Enqueue(kid.LocalID);
                    }
                }
            }

            var isAvatar = sim.ObjectsAvatars.Values.Any(a => a != null && a.LocalID == localId);
            var subject = isAvatar ? "avatar" : "prim";
            return Task.FromResult(PrimQueryResult.OkResult(
                result,
                $"Found {result.Count} child prim(s) of {subject} {localId}{(recursive ? " (recursive)" : string.Empty)}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> ListNearbyAvatarsAsync(float radiusMeters, int maxResults, CancellationToken cancellationToken)
    {
        if (radiusMeters <= 0f)
        {
            return DataToolResult.FailResult("radiusMeters must be greater than 0.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(DataToolResult.FailResult("No current simulator available."));
            }

            var limit = Math.Clamp(maxResults, 1, 500);
            var radius = Math.Clamp(radiusMeters, 0.1f, 4096f);
            var at = client.Self.SimPosition;
            var selfId = client.Self.AgentID;

            var rows = new List<NearbyAvatarInfo>();
            foreach (var av in sim.ObjectsAvatars.Values)
            {
                if (av == null)
                {
                    continue;
                }

                // Seated avatars report a position relative to their seat.
                var pos = av.Position;
                string? sittingOn = null;
                if (av.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(av.ParentID, out var seat) && seat != null)
                {
                    pos = seat.Position + av.Position * seat.Rotation;
                    sittingOn = seat.Properties?.Name ?? seat.LocalID.ToString();
                }

                var distance = av.ID == selfId ? 0f : Vector3.Distance(at, pos);
                if (distance > radius)
                {
                    continue;
                }

                var name = (av.Name ?? string.Empty).Trim();
                rows.Add(new NearbyAvatarInfo(
                    av.ID.ToString(),
                    av.LocalID,
                    string.IsNullOrWhiteSpace(name) ? null : name,
                    string.IsNullOrWhiteSpace(av.GroupName) ? null : av.GroupName,
                    pos.X,
                    pos.Y,
                    pos.Z,
                    distance,
                    av.ID == selfId,
                    av.ParentID != 0,
                    sittingOn,
                    av.Attachments?.Count ?? 0));
            }

            rows = rows.OrderBy(r => r.DistanceMeters).ThenBy(r => r.LocalId).Take(limit).ToList();
            var payload = JsonSerializer.Serialize(new
            {
                simulator = sim.Name,
                selfLocalId = rows.FirstOrDefault(r => r.IsSelf)?.LocalId,
                count = rows.Count,
                avatars = rows
            }, SenseJsonOptions);

            return Task.FromResult(DataToolResult.OkResult(
                $"Found {rows.Count} avatar(s) within {radius:F1}m (including self).",
                payload));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> SearchInventoryCacheAsync(
        string nameContains,
        int maxResults,
        string? typeFilter,
        string? folderIdOrPath,
        CancellationToken cancellationToken)
    {
        var needle = (nameContains ?? string.Empty).Trim();
        if (needle.Length == 0)
        {
            return DataToolResult.FailResult("nameContains is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var store = client.Inventory.Store;
            var root = store?.RootFolder;
            if (store == null || root == null)
            {
                return Task.FromResult(DataToolResult.FailResult("Inventory root is not loaded yet."));
            }

            var start = root;
            if (!string.IsNullOrWhiteSpace(folderIdOrPath))
            {
                var resolved = ResolveCachedFolder(store, root, folderIdOrPath.Trim());
                if (resolved == null)
                {
                    return Task.FromResult(DataToolResult.FailResult($"Folder '{folderIdOrPath}' was not found in the cached inventory."));
                }

                start = resolved;
            }

            var limit = Math.Clamp(maxResults, 1, 2000);
            var type = string.IsNullOrWhiteSpace(typeFilter) ? null : typeFilter.Trim();
            var matches = new List<InventorySearchHit>();
            var foldersScanned = 0;
            var emptyFolders = 0;

            var stack = new Stack<(InventoryFolder Folder, string Path)>();
            stack.Push((start, start.Name));
            var visited = new HashSet<UUID>();
            while (stack.Count > 0)
            {
                var (folder, path) = stack.Pop();
                if (!visited.Add(folder.UUID))
                {
                    continue;
                }

                foldersScanned++;
                var contents = store.GetContents(folder.UUID);
                if (contents == null || contents.Count == 0)
                {
                    emptyFolders++;
                    continue;
                }

                foreach (var entry in contents)
                {
                    switch (entry)
                    {
                        case InventoryFolder sub:
                            stack.Push((sub, path + "/" + sub.Name));
                            if (MatchesName(sub.Name, needle) && TypeMatches("Folder", "Category", type))
                            {
                                matches.Add(new InventorySearchHit(sub.UUID.ToString(), sub.Name, "folder", "Folder", "Category", path, folder.UUID.ToString(), null));
                            }
                            break;
                        case InventoryItem item:
                            if (MatchesName(item.Name, needle) && TypeMatches(item.AssetType.ToString(), item.InventoryType.ToString(), type))
                            {
                                matches.Add(new InventorySearchHit(
                                    item.UUID.ToString(),
                                    item.Name,
                                    "item",
                                    item.AssetType.ToString(),
                                    item.InventoryType.ToString(),
                                    path,
                                    folder.UUID.ToString(),
                                    item.CreationDate == default ? null : item.CreationDate.ToUniversalTime().ToString("O")));
                            }
                            break;
                    }
                }
            }

            var ordered = matches
                .OrderBy(h => h.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var page = ordered.Take(limit).ToList();

            var payload = JsonSerializer.Serialize(new
            {
                query = needle,
                type,
                startFolder = start.Name,
                foldersScanned,
                foldersWithoutCachedContents = emptyFolders,
                matched = ordered.Count,
                returned = page.Count,
                note = emptyFolders > 0
                    ? "Some folders have no cached contents yet; run inventory_list on them (or recursively) and search again if an item is missing."
                    : null,
                results = page
            }, SenseJsonOptions);

            return Task.FromResult(DataToolResult.OkResult(
                $"Matched {ordered.Count} cached inventory entr{(ordered.Count == 1 ? "y" : "ies")} for '{needle}' across {foldersScanned} folder(s); returned {page.Count}.",
                payload));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesName(string? name, string needle)
        => !string.IsNullOrEmpty(name) && name.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool TypeMatches(string assetType, string inventoryType, string? filter)
        => filter == null
           || string.Equals(assetType, filter, StringComparison.OrdinalIgnoreCase)
           || string.Equals(inventoryType, filter, StringComparison.OrdinalIgnoreCase);

    private static InventoryFolder? ResolveCachedFolder(Inventory store, InventoryFolder root, string idOrPath)
    {
        if (UUID.TryParse(idOrPath, out var uuid))
        {
            return store.TryGetValue(uuid, out var byId) ? byId as InventoryFolder : null;
        }

        var current = root;
        foreach (var segment in idOrPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var contents = store.GetContents(current.UUID);
            var next = contents?.OfType<InventoryFolder>()
                .FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}

internal sealed record NearbyAvatarInfo(
    string AgentId,
    uint LocalId,
    string? Name,
    string? GroupTitle,
    float PositionX,
    float PositionY,
    float PositionZ,
    float DistanceMeters,
    bool IsSelf,
    bool IsSitting,
    string? SittingOn,
    int AttachmentCount);

internal sealed record InventorySearchHit(
    string Id,
    string Name,
    string Kind,
    string AssetType,
    string InventoryType,
    string FolderPath,
    string ParentId,
    string? CreatedUtc);
