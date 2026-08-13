using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<DataToolResult> PrimRequestPayPriceAsync(
        uint localId,
        string? objectId,
        int waitTimeoutMs,
        CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Clamp(waitTimeoutMs, 250, 15000);

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            UUID targetObjectId;
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                if (!UUID.TryParse(objectId, out targetObjectId) || targetObjectId == UUID.Zero)
                {
                    return DataToolResult.FailResult("objectId must be a valid non-zero UUID when provided.");
                }
            }
            else
            {
                if (!sim.ObjectsPrimitives.TryGetValue(localId, out var prim))
                {
                    return DataToolResult.FailResult($"Prim {localId} not found in current simulator cache.");
                }

                targetObjectId = prim.ID;
            }

            var tcs = new TaskCompletionSource<PayPriceReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnPayPriceReply(object? _, PayPriceReplyEventArgs e)
            {
                if (!ReferenceEquals(e.Simulator, sim) || e.ObjectID != targetObjectId)
                {
                    return;
                }

                tcs.TrySetResult(e);
            }

            client.Objects.PayPriceReply += OnPayPriceReply;
            try
            {
                client.Objects.RequestPayPrice(sim, targetObjectId);

                var timeoutTask = Task.Delay(timeoutMs, token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                if (completed != tcs.Task)
                {
                    return DataToolResult.FailResult(
                        $"Timed out waiting for pay price reply for object {targetObjectId} after {timeoutMs}ms.");
                }

                var reply = await tcs.Task.ConfigureAwait(false);
                var payload = new
                {
                    simulator = sim.Name,
                    localId,
                    objectId = reply.ObjectID.ToString(),
                    defaultPrice = reply.DefaultPrice,
                    buttonPrices = reply.ButtonPrices
                };

                return DataToolResult.OkResult(
                    $"Received pay price reply for object {reply.ObjectID}.",
                    JsonSerializer.Serialize(payload, JsonOptions));
            }
            finally
            {
                client.Objects.PayPriceReply -= OnPayPriceReply;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> PrimBuyAsync(
        uint localId,
        string saleType,
        int price,
        string? categoryFolderId,
        string? activeGroupId,
        CancellationToken cancellationToken)
    {
        if (price < 0)
        {
            return BotToolResult.Fail("price must be >= 0.");
        }

        if (!Enum.TryParse<SaleType>(saleType?.Trim() ?? string.Empty, true, out var parsedSaleType))
        {
            return BotToolResult.Fail("Invalid saleType. Use: Original, Copy, or Contents.");
        }

        if (parsedSaleType == SaleType.Not)
        {
            return BotToolResult.Fail("saleType must be Original, Copy, or Contents for purchases.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            if (!sim.ObjectsPrimitives.ContainsKey(localId))
            {
                return Task.FromResult(BotToolResult.Fail($"Prim {localId} not found in current simulator cache."));
            }

            var groupId = client.Self.ActiveGroup;
            if (!string.IsNullOrWhiteSpace(activeGroupId))
            {
                if (!UUID.TryParse(activeGroupId, out groupId))
                {
                    return Task.FromResult(BotToolResult.Fail("activeGroupId must be a valid UUID when provided."));
                }
            }

            var destinationFolder = client.Inventory.FindFolderForType(AssetType.Object);
            if (!string.IsNullOrWhiteSpace(categoryFolderId))
            {
                if (!UUID.TryParse(categoryFolderId, out destinationFolder))
                {
                    return Task.FromResult(BotToolResult.Fail("categoryFolderId must be a valid UUID when provided."));
                }
            }

            client.Objects.BuyObject(sim, localId, parsedSaleType, price, groupId, destinationFolder);
            return Task.FromResult(BotToolResult.OkResult(
                $"Buy request sent for localId={localId} saleType={parsedSaleType} price={price} destinationFolder={destinationFolder}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> WalletGetBalanceAsync(bool refresh, int waitTimeoutMs, CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Clamp(waitTimeoutMs, 250, 15000);

        return await ExecuteLockedAsync(async (client, token) =>
        {
            if (!refresh)
            {
                var cachedPayload = new
                {
                    refreshed = false,
                    balance = client.Self.Balance
                };
                return DataToolResult.OkResult(
                    "Returned cached wallet balance.",
                    JsonSerializer.Serialize(cachedPayload, JsonOptions));
            }

            var tcs = new TaskCompletionSource<BalanceEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnMoneyBalance(object? _, BalanceEventArgs e)
            {
                tcs.TrySetResult(e);
            }

            client.Self.MoneyBalance += OnMoneyBalance;
            try
            {
                client.Self.RequestBalance();
                var timeoutTask = Task.Delay(timeoutMs, token);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
                if (completed != tcs.Task)
                {
                    return DataToolResult.FailResult(
                        $"Timed out waiting for wallet balance update after {timeoutMs}ms. Cached balance={client.Self.Balance}.");
                }

                var result = await tcs.Task.ConfigureAwait(false);
                var payload = new
                {
                    refreshed = true,
                    balance = result.Balance
                };
                return DataToolResult.OkResult(
                    "Wallet balance refreshed.",
                    JsonSerializer.Serialize(payload, JsonOptions));
            }
            finally
            {
                client.Self.MoneyBalance -= OnMoneyBalance;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> PayAsync(
        string targetType,
        string targetId,
        int amount,
        string? description,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return BotToolResult.Fail("targetType is required. Use: avatar, object, or group.");
        }

        if (!UUID.TryParse(targetId, out var targetUuid) || targetUuid == UUID.Zero)
        {
            return BotToolResult.Fail("targetId must be a valid non-zero UUID.");
        }

        if (amount <= 0)
        {
            return BotToolResult.Fail("amount must be greater than 0.");
        }

        var normalized = targetType.Trim().ToLowerInvariant();
        var transactionType = MoneyTransactionType.Gift;
        var flags = TransactionFlags.None;

        switch (normalized)
        {
            case "avatar":
            case "agent":
                transactionType = MoneyTransactionType.Gift;
                flags = TransactionFlags.None;
                break;
            case "object":
            case "prim":
                transactionType = MoneyTransactionType.PayObject;
                flags = TransactionFlags.None;
                break;
            case "group":
                transactionType = MoneyTransactionType.Gift;
                flags = TransactionFlags.DestGroup;
                break;
            default:
                return BotToolResult.Fail("Invalid targetType. Use: avatar, object, or group.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var memo = description ?? string.Empty;
            client.Self.GiveMoney(targetUuid, amount, memo, transactionType, flags);
            return Task.FromResult(BotToolResult.OkResult(
                $"Payment request sent: targetType={normalized}, targetId={targetUuid}, amount={amount}."));
        }, cancellationToken).ConfigureAwait(false);
    }
}
