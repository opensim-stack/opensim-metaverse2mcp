using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<DataToolResult> DirectorySearchPeopleAsync(string query, int queryStart, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DataToolResult.FailResult("query is required.");
        }

        if (queryStart < 0)
        {
            return DataToolResult.FailResult("queryStart must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var normalizedQuery = query.Trim();
            var queryId = client.Directory.StartPeopleSearch(normalizedQuery, queryStart);
            var reply = await WaitForDirPeopleReplyAsync(client, queryId, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for people search reply (queryId={queryId}).");
            }

            var rows = reply.MatchedPeople
                .OrderBy(x => x.LastName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.FirstName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.AgentID.ToString(), StringComparer.Ordinal)
                .Select(x => (object)new
                {
                    agentId = x.AgentID.ToString(),
                    firstName = x.FirstName,
                    lastName = x.LastName,
                    fullName = string.IsNullOrWhiteSpace(x.LastName) ? x.FirstName : $"{x.FirstName} {x.LastName}",
                    online = x.Online
                })
                .ToList();

            var hasMore = rows.Count >= 100;
            var payload = new
            {
                summary = new
                {
                    kind = "people",
                    query = normalizedQuery,
                    queryId = queryId.ToString(),
                    count = rows.Count
                },
                pagination = new
                {
                    queryStart,
                    pageSize = 100,
                    hasMore,
                    nextQueryStart = hasMore ? queryStart + 1 : (int?)null
                },
                results = rows
            };

            return DataToolResult.OkResult(
                $"People search returned {rows.Count} result(s).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> DirectorySearchGroupsAsync(string query, int queryStart, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DataToolResult.FailResult("query is required.");
        }

        if (queryStart < 0)
        {
            return DataToolResult.FailResult("queryStart must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var normalizedQuery = query.Trim();
            var queryId = client.Directory.StartGroupSearch(normalizedQuery, queryStart);
            var reply = await WaitForDirGroupsReplyAsync(client, queryId, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for group search reply (queryId={queryId}).");
            }

            var rows = reply.MatchedGroups
                .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.GroupID.ToString(), StringComparer.Ordinal)
                .Select(x => (object)new
                {
                    groupId = x.GroupID.ToString(),
                    name = x.GroupName,
                    members = x.Members
                })
                .ToList();

            var hasMore = rows.Count >= 100;
            var payload = new
            {
                summary = new
                {
                    kind = "groups",
                    query = normalizedQuery,
                    queryId = queryId.ToString(),
                    count = rows.Count
                },
                pagination = new
                {
                    queryStart,
                    pageSize = 100,
                    hasMore,
                    nextQueryStart = hasMore ? queryStart + 1 : (int?)null
                },
                results = rows
            };

            return DataToolResult.OkResult(
                $"Group search returned {rows.Count} result(s).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> DirectorySearchPlacesAsync(string query, int queryStart, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return DataToolResult.FailResult("query is required.");
        }

        if (queryStart < 0)
        {
            return DataToolResult.FailResult("queryStart must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var normalizedQuery = query.Trim();
            var queryId = client.Directory.StartDirPlacesSearch(normalizedQuery, queryStart);
            var reply = await WaitForDirPlacesReplyAsync(client, queryId, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for places search reply (queryId={queryId}).");
            }

            var rows = reply.MatchedParcels
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ID.ToString(), StringComparer.Ordinal)
                .Select(x => (object)new
                {
                    parcelId = x.ID.ToString(),
                    name = x.Name,
                    actualArea = x.ActualArea,
                    salePrice = x.SalePrice,
                    forSale = x.ForSale,
                    auction = x.Auction,
                    dwell = x.Dwell
                })
                .ToList();

            var hasMore = rows.Count >= 100;
            var payload = new
            {
                summary = new
                {
                    kind = "places",
                    query = normalizedQuery,
                    queryId = queryId.ToString(),
                    count = rows.Count
                },
                pagination = new
                {
                    queryStart,
                    pageSize = 100,
                    hasMore,
                    nextQueryStart = hasMore ? queryStart + 1 : (int?)null
                },
                results = rows
            };

            return DataToolResult.OkResult(
                $"Places search returned {rows.Count} result(s).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> DirectorySearchLandAsync(
        string landType,
        int queryStart,
        int maxPrice,
        int minArea,
        CancellationToken cancellationToken)
    {
        if (!TryParseLandType(landType, out var typeFlags))
        {
            return DataToolResult.FailResult("landType must be one of: any, mainland, estate, auction.");
        }

        if (queryStart < 0)
        {
            return DataToolResult.FailResult("queryStart must be >= 0.");
        }

        if (maxPrice < 0)
        {
            return DataToolResult.FailResult("maxPrice must be >= 0.");
        }

        if (minArea < 0)
        {
            return DataToolResult.FailResult("minArea must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var queryFlags = DirectoryManager.DirFindFlags.SortAsc
                | DirectoryManager.DirFindFlags.PerMeterSort
                | DirectoryManager.DirFindFlags.IncludePG
                | DirectoryManager.DirFindFlags.IncludeMature
                | DirectoryManager.DirFindFlags.IncludeAdult;

            if (maxPrice > 0)
            {
                queryFlags |= DirectoryManager.DirFindFlags.LimitByPrice;
            }

            if (minArea > 0)
            {
                queryFlags |= DirectoryManager.DirFindFlags.LimitByArea;
            }

            var replyTask = WaitForDirLandReplyAsync(client, token);
            client.Directory.StartLandSearch(queryFlags, typeFlags, maxPrice, minArea, queryStart);

            var reply = await replyTask.ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult("Timed out waiting for land search reply.");
            }

            var rows = reply.DirParcels
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ID.ToString(), StringComparer.Ordinal)
                .Select(x => (object)new
                {
                    parcelId = x.ID.ToString(),
                    name = x.Name,
                    actualArea = x.ActualArea,
                    salePrice = x.SalePrice,
                    forSale = x.ForSale,
                    auction = x.Auction,
                    dwell = x.Dwell
                })
                .ToList();

            var hasMore = rows.Count >= 100;
            var normalizedLandType = landType.Trim().ToLowerInvariant();
            var payload = new
            {
                summary = new
                {
                    kind = "land",
                    landType = normalizedLandType,
                    count = rows.Count,
                    maxPrice,
                    minArea
                },
                pagination = new
                {
                    queryStart,
                    pageSize = 100,
                    hasMore,
                    nextQueryStart = hasMore ? queryStart + 100 : (int?)null
                },
                results = rows
            };

            return DataToolResult.OkResult(
                $"Land search returned {rows.Count} result(s).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParseLandType(string? value, out DirectoryManager.SearchTypeFlags result)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "":
            case "any":
                result = DirectoryManager.SearchTypeFlags.Any;
                return true;
            case "mainland":
                result = DirectoryManager.SearchTypeFlags.Mainland;
                return true;
            case "estate":
                result = DirectoryManager.SearchTypeFlags.Estate;
                return true;
            case "auction":
                result = DirectoryManager.SearchTypeFlags.Auction;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static async Task<DirPeopleReplyEventArgs?> WaitForDirPeopleReplyAsync(
        GridClient client,
        UUID queryId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DirPeopleReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, DirPeopleReplyEventArgs e)
        {
            if (e.QueryID == queryId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Directory.DirPeopleReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Directory.DirPeopleReply -= Handler;
        }
    }

    private static async Task<DirGroupsReplyEventArgs?> WaitForDirGroupsReplyAsync(
        GridClient client,
        UUID queryId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DirGroupsReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, DirGroupsReplyEventArgs e)
        {
            if (e.QueryID == queryId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Directory.DirGroupsReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Directory.DirGroupsReply -= Handler;
        }
    }

    private static async Task<DirPlacesReplyEventArgs?> WaitForDirPlacesReplyAsync(
        GridClient client,
        UUID queryId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DirPlacesReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, DirPlacesReplyEventArgs e)
        {
            if (e.QueryID == queryId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Directory.DirPlacesReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Directory.DirPlacesReply -= Handler;
        }
    }

    private static async Task<DirLandReplyEventArgs?> WaitForDirLandReplyAsync(
        GridClient client,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DirLandReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, DirLandReplyEventArgs e)
        {
            tcs.TrySetResult(e);
        }

        client.Directory.DirLandReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Directory.DirLandReply -= Handler;
        }
    }
}
