using System.Collections.Concurrent;
using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private sealed record PendingTeleportMessage(
        UUID FromAgentId,
        string FromAgentName,
        UUID SessionId,
        string Message,
        InstantMessageDialog Dialog,
        DateTimeOffset ReceivedAtUtc);

    private readonly ConcurrentDictionary<UUID, PendingTeleportMessage> _pendingTeleportOffersByAgent = new();
    private readonly ConcurrentDictionary<UUID, PendingTeleportMessage> _pendingTeleportRequestsByAgent = new();
    private readonly object _socialImHookLock = new();
    private GridClient? _socialImHookClient;

    public async Task<DataToolResult> FriendListAsync(bool includeDetails, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var friends = client.Friends.FriendList.Values
                .OrderBy(f => string.IsNullOrWhiteSpace(f.Name) ? f.UUID.ToString() : f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var onlineCount = friends.Count(f => f.IsOnline);
            var rows = includeDetails
                ? friends.Select(f => (object)new
                {
                    agentId = f.UUID.ToString(),
                    name = f.Name,
                    isOnline = f.IsOnline,
                    canSeeMeOnline = f.CanSeeMeOnline,
                    canSeeMeOnMap = f.CanSeeMeOnMap,
                    canModifyMyObjects = f.CanModifyMyObjects,
                    canSeeThemOnline = f.CanSeeThemOnline,
                    canSeeThemOnMap = f.CanSeeThemOnMap,
                    canModifyTheirObjects = f.CanModifyTheirObjects,
                    theirFriendRights = f.TheirFriendRights.ToString(),
                    myFriendRights = f.MyFriendRights.ToString()
                }).ToList()
                : friends.Select(f => (object)new
                {
                    agentId = f.UUID.ToString(),
                    name = f.Name,
                    isOnline = f.IsOnline
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    total = friends.Count,
                    online = onlineCount,
                    offline = friends.Count - onlineCount,
                    pendingOffers = client.Friends.FriendRequests.Count,
                    includeDetails
                },
                friends = rows
            };

            return Task.FromResult(DataToolResult.OkResult(
                $"Retrieved {friends.Count} friend(s) ({onlineCount} online).",
                JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> FriendOffersListAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var offers = client.Friends.FriendRequests
                .OrderBy(kvp => kvp.Key.ToString(), StringComparer.Ordinal)
                .Select(kvp =>
                {
                    var fromAgentId = kvp.Key;
                    var sessionId = kvp.Value;
                    var name = client.Friends.FriendList.TryGetValue(fromAgentId, out var friend)
                        ? friend.Name
                        : string.Empty;

                    return (object)new
                    {
                        fromAgentId = fromAgentId.ToString(),
                        fromAgentName = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name,
                        sessionId = sessionId.ToString()
                    };
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    count = offers.Count
                },
                offers
            };

            return Task.FromResult(DataToolResult.OkResult(
                $"Retrieved {offers.Count} pending friendship offer(s).",
                JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> FriendOfferSendAsync(string targetAgentId, string? message, int waitForResponseSeconds, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(targetAgentId, out var targetAgentUuid))
        {
            return BotToolResult.Fail("targetAgentId must be a valid UUID.");
        }

        if (waitForResponseSeconds < 0 || waitForResponseSeconds > 60)
        {
            return BotToolResult.Fail("waitForResponseSeconds must be in range 0..60.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var text = string.IsNullOrWhiteSpace(message)
                ? "Do ya wanna be my buddy?"
                : message.Trim();

            if (waitForResponseSeconds == 0)
            {
                client.Friends.OfferFriendship(targetAgentUuid, text);
                return BotToolResult.OkResult($"Friendship offer sent to {targetAgentUuid}. (not waiting for response)");
            }

            var responseTask = WaitForFriendshipResponseAsync(client, targetAgentUuid, TimeSpan.FromSeconds(waitForResponseSeconds), token);
            client.Friends.OfferFriendship(targetAgentUuid, text);

            var response = await responseTask.ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail($"Friendship offer sent to {targetAgentUuid}, but no accept/decline response arrived within {waitForResponseSeconds}s.");
            }

            var label = string.IsNullOrWhiteSpace(response.AgentName) ? response.AgentID.ToString() : response.AgentName;
            return response.Accepted
                ? BotToolResult.OkResult($"Friendship offer accepted by {label} ({response.AgentID}).")
                : BotToolResult.Fail($"Friendship offer declined by {label} ({response.AgentID}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> FriendOfferRespondAsync(string fromAgentId, string action, bool useCapabilities, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(fromAgentId, out var fromAgentUuid))
        {
            return BotToolResult.Fail("fromAgentId must be a valid UUID.");
        }

        var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedAction != "accept" && normalizedAction != "decline")
        {
            return BotToolResult.Fail("action must be one of: accept, decline.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            if (!client.Friends.FriendRequests.TryGetValue(fromAgentUuid, out var requestSessionId))
            {
                return BotToolResult.Fail($"No pending friendship offer found for agent {fromAgentUuid}.");
            }

            if (normalizedAction == "accept")
            {
                if (useCapabilities)
                {
                    await client.Friends.AcceptFriendshipViaCapAsync(fromAgentUuid, token).ConfigureAwait(false);
                }
                else
                {
                    client.Friends.AcceptFriendship(fromAgentUuid, requestSessionId);
                }

                var accepted = client.Friends.FriendList.ContainsKey(fromAgentUuid);
                return accepted
                    ? BotToolResult.OkResult($"Accepted friendship offer from {fromAgentUuid}.")
                    : BotToolResult.OkResult($"Friendship accept submitted for {fromAgentUuid}. Verify with FriendList/FriendOffersList if needed.");
            }

            if (useCapabilities)
            {
                await client.Friends.DeclineFriendshipViaCapAsync(fromAgentUuid, token).ConfigureAwait(false);
            }
            else
            {
                client.Friends.DeclineFriendship(fromAgentUuid, requestSessionId);
            }

            var stillPending = client.Friends.FriendRequests.ContainsKey(fromAgentUuid);
            return stillPending
                ? BotToolResult.OkResult($"Friendship decline submitted for {fromAgentUuid}. Offer may clear asynchronously.")
                : BotToolResult.OkResult($"Declined friendship offer from {fromAgentUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> FriendRemoveAsync(string friendAgentId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(friendAgentId, out var friendAgentUuid))
        {
            return BotToolResult.Fail("friendAgentId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            if (!client.Friends.FriendList.ContainsKey(friendAgentUuid))
            {
                return Task.FromResult(BotToolResult.Fail($"Agent {friendAgentUuid} is not currently in the friend list."));
            }

            client.Friends.TerminateFriendship(friendAgentUuid);
            return Task.FromResult(BotToolResult.OkResult($"Friendship termination submitted for {friendAgentUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> FriendSetRightsAsync(
        string friendAgentId,
        bool canSeeOnline,
        bool canSeeOnMap,
        bool canModifyObjects,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(friendAgentId, out var friendAgentUuid))
        {
            return BotToolResult.Fail("friendAgentId must be a valid UUID.");
        }

        if (canSeeOnMap && !canSeeOnline)
        {
            return BotToolResult.Fail("canSeeOnMap requires canSeeOnline=true.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            if (!client.Friends.FriendList.ContainsKey(friendAgentUuid))
            {
                return Task.FromResult(BotToolResult.Fail($"Agent {friendAgentUuid} is not currently in the friend list."));
            }

            var rights = FriendRights.None;
            if (canSeeOnline)
            {
                rights |= FriendRights.CanSeeOnline;
            }

            if (canSeeOnMap)
            {
                rights |= FriendRights.CanSeeOnMap;
            }

            if (canModifyObjects)
            {
                rights |= FriendRights.CanModifyObjects;
            }

            client.Friends.GrantRights(friendAgentUuid, rights);
            return Task.FromResult(BotToolResult.OkResult($"Friend rights update submitted for {friendAgentUuid}: rights={rights}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> FriendMapLocateAsync(string friendAgentId, int waitForReplySeconds, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(friendAgentId, out var friendAgentUuid))
        {
            return DataToolResult.FailResult("friendAgentId must be a valid UUID.");
        }

        if (waitForReplySeconds < 0 || waitForReplySeconds > 60)
        {
            return DataToolResult.FailResult("waitForReplySeconds must be in range 0..60.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            if (waitForReplySeconds == 0)
            {
                client.Friends.MapFriend(friendAgentUuid);
                var submittedPayload = JsonSerializer.Serialize(new
                {
                    friendAgentId = friendAgentUuid.ToString(),
                    waiting = false
                }, JsonOptions);
                return DataToolResult.OkResult($"Friend map locate request submitted for {friendAgentUuid}. (not waiting for reply)", submittedPayload);
            }

            var responseTask = WaitForFriendFoundReplyAsync(client, friendAgentUuid, TimeSpan.FromSeconds(waitForReplySeconds), token);
            client.Friends.MapFriend(friendAgentUuid);

            var response = await responseTask.ConfigureAwait(false);
            if (response == null)
            {
                return DataToolResult.FailResult($"Friend map locate request submitted for {friendAgentUuid}, but no location reply arrived within {waitForReplySeconds}s.");
            }

            Utils.LongToUInts(response.RegionHandle, out var regionX, out var regionY);
            var globalX = regionX + response.Location.X;
            var globalY = regionY + response.Location.Y;

            var payload = new
            {
                friend = new
                {
                    agentId = response.AgentID.ToString(),
                    regionHandle = response.RegionHandle,
                    regionHandleHex = $"0x{response.RegionHandle:X16}",
                    regionCornerX = regionX,
                    regionCornerY = regionY,
                    localPosition = new
                    {
                        x = response.Location.X,
                        y = response.Location.Y,
                        z = response.Location.Z
                    },
                    globalPosition = new
                    {
                        x = globalX,
                        y = globalY,
                        z = response.Location.Z
                    }
                }
            };

            return DataToolResult.OkResult(
                $"Mapped friend {response.AgentID} at local {FormatVector(response.Location)}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportOfferSendAsync(string targetAgentId, string? message, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(targetAgentId, out var targetAgentUuid))
        {
            return BotToolResult.Fail("targetAgentId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            EnsureSocialImHookRegistered(client);

            if (string.IsNullOrWhiteSpace(message))
            {
                client.Self.SendTeleportLure(targetAgentUuid);
            }
            else
            {
                client.Self.SendTeleportLure(targetAgentUuid, message.Trim());
            }

            return Task.FromResult(BotToolResult.OkResult($"Teleport offer sent to {targetAgentUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportRequestSendAsync(string targetAgentId, string? message, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(targetAgentId, out var targetAgentUuid))
        {
            return BotToolResult.Fail("targetAgentId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            EnsureSocialImHookRegistered(client);

            if (string.IsNullOrWhiteSpace(message))
            {
                client.Self.SendTeleportLureRequest(targetAgentUuid);
            }
            else
            {
                client.Self.SendTeleportLureRequest(targetAgentUuid, message.Trim());
            }

            return Task.FromResult(BotToolResult.OkResult($"Teleport request sent to {targetAgentUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> TeleportOffersListAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            EnsureSocialImHookRegistered(client);

            var offers = _pendingTeleportOffersByAgent.Values
                .OrderByDescending(x => x.ReceivedAtUtc)
                .Select(x => (object)new
                {
                    fromAgentId = x.FromAgentId.ToString(),
                    fromAgentName = x.FromAgentName,
                    sessionId = x.SessionId.ToString(),
                    message = x.Message,
                    dialog = x.Dialog.ToString(),
                    receivedAtUtc = x.ReceivedAtUtc
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    count = offers.Count
                },
                offers
            };

            return Task.FromResult(DataToolResult.OkResult(
                $"Retrieved {offers.Count} pending teleport offer(s).",
                JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> TeleportRequestsListAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            EnsureSocialImHookRegistered(client);

            var requests = _pendingTeleportRequestsByAgent.Values
                .OrderByDescending(x => x.ReceivedAtUtc)
                .Select(x => (object)new
                {
                    fromAgentId = x.FromAgentId.ToString(),
                    fromAgentName = x.FromAgentName,
                    sessionId = x.SessionId.ToString(),
                    message = x.Message,
                    dialog = x.Dialog.ToString(),
                    receivedAtUtc = x.ReceivedAtUtc
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    count = requests.Count
                },
                requests
            };

            return Task.FromResult(DataToolResult.OkResult(
                $"Retrieved {requests.Count} pending teleport request(s).",
                JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportOfferRespondAsync(string requesterAgentId, string sessionId, bool accept, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(requesterAgentId, out var requesterAgentUuid))
        {
            return BotToolResult.Fail("requesterAgentId must be a valid UUID.");
        }

        if (!UUID.TryParse(sessionId, out var sessionUuid))
        {
            return BotToolResult.Fail("sessionId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, token) =>
        {
            EnsureSocialImHookRegistered(client);

            client.Self.TeleportLureRespond(requesterAgentUuid, sessionUuid, accept);
            if (_pendingTeleportOffersByAgent.TryGetValue(requesterAgentUuid, out var pending)
                && pending.SessionId == sessionUuid)
            {
                _pendingTeleportOffersByAgent.TryRemove(requesterAgentUuid, out var _removed);
            }

            if (_pendingTeleportRequestsByAgent.TryGetValue(requesterAgentUuid, out var pendingRequest)
                && pendingRequest.SessionId == sessionUuid)
            {
                _pendingTeleportRequestsByAgent.TryRemove(requesterAgentUuid, out var _removed);
            }

            var action = accept ? "accepted" : "declined";
            return Task.FromResult(BotToolResult.OkResult($"Teleport offer {action} for requester {requesterAgentUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureSocialImHookRegistered(GridClient client)
    {
        lock (_socialImHookLock)
        {
            if (ReferenceEquals(_socialImHookClient, client))
            {
                return;
            }

            client.Self.IM += OnSocialInstantMessage;
            _socialImHookClient = client;
        }
    }

    private void OnSocialInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        var im = e.IM;
        if (im.FromAgentID == UUID.Zero)
        {
            return;
        }

        var message = im.Message?.Trim() ?? string.Empty;
        var entry = new PendingTeleportMessage(
            im.FromAgentID,
            im.FromAgentName,
            im.IMSessionID,
            message,
            im.Dialog,
            DateTimeOffset.UtcNow);

        switch (im.Dialog)
        {
            case InstantMessageDialog.RequestTeleport:
            case InstantMessageDialog.GodLikeRequestTeleport:
                _pendingTeleportOffersByAgent[im.FromAgentID] = entry;
                break;
            case InstantMessageDialog.RequestLure:
                _pendingTeleportRequestsByAgent[im.FromAgentID] = entry;
                break;
        }
    }

    private static async Task<FriendshipResponseEventArgs?> WaitForFriendshipResponseAsync(
        GridClient client,
        UUID agentId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<FriendshipResponseEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, FriendshipResponseEventArgs e)
        {
            if (e.AgentID == agentId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Friends.FriendshipResponse += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Friends.FriendshipResponse -= Handler;
        }
    }

    private static async Task<FriendFoundReplyEventArgs?> WaitForFriendFoundReplyAsync(
        GridClient client,
        UUID friendId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<FriendFoundReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, FriendFoundReplyEventArgs e)
        {
            if (e.AgentID == friendId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Friends.FriendFoundReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Friends.FriendFoundReply -= Handler;
        }
    }
}
