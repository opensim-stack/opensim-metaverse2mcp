using System.Collections.Concurrent;
using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const int PendingScriptDialogCacheLimit = 200;
    private const int PendingScriptPermissionCacheLimit = 200;

    private sealed record PendingScriptDialog(
        string Handle,
        UUID ObjectId,
        string ObjectName,
        UUID OwnerId,
        string OwnerName,
        UUID ImageId,
        int Channel,
        string Message,
        IReadOnlyList<string> ButtonLabels,
        DateTimeOffset ReceivedAtUtc);

    private sealed record PendingScriptPermissionRequest(
        string Handle,
        Simulator Simulator,
        UUID TaskId,
        UUID ItemId,
        string ObjectName,
        string ObjectOwnerName,
        ScriptPermission RequestedPermissions,
        DateTimeOffset ReceivedAtUtc);

    private readonly ConcurrentDictionary<string, PendingScriptDialog> _pendingScriptDialogsByHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pendingScriptDialogOrder = new();
    private readonly object _pendingScriptDialogLock = new();
    private readonly ConcurrentDictionary<string, PendingScriptPermissionRequest> _pendingScriptPermissionsByHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pendingScriptPermissionOrder = new();
    private readonly object _pendingScriptPermissionLock = new();
    private readonly object _scriptDialogHookLock = new();
    private GridClient? _scriptDialogHookClient;

    public async Task<DataToolResult> ListScriptDialogsAsync(string? pendingDialogHandle, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _token) =>
        {
            EnsureScriptDialogHookRegistered(client);

            var normalizedHandle = (pendingDialogHandle ?? string.Empty).Trim();
            var listAll = string.IsNullOrWhiteSpace(normalizedHandle)
                || string.Equals(normalizedHandle, "all", StringComparison.OrdinalIgnoreCase)
                || normalizedHandle == "*";

            var dialogs = listAll
                ? _pendingScriptDialogsByHandle.Values.OrderByDescending(x => x.ReceivedAtUtc).ToList()
                : (_pendingScriptDialogsByHandle.TryGetValue(normalizedHandle, out var pending)
                    ? new List<PendingScriptDialog> { pending }
                    : new List<PendingScriptDialog>());

            var payload = new
            {
                summary = new
                {
                    count = dialogs.Count,
                    filtered = !listAll,
                    pendingDialogHandle = listAll ? null : normalizedHandle
                },
                dialogs = dialogs.Select(dialog => (object)new
                {
                    pendingDialogHandle = dialog.Handle,
                    objectId = dialog.ObjectId.ToString(),
                    objectName = dialog.ObjectName,
                    ownerId = dialog.OwnerId.ToString(),
                    ownerName = dialog.OwnerName,
                    imageId = dialog.ImageId.ToString(),
                    channel = dialog.Channel,
                    message = dialog.Message,
                    buttonCount = dialog.ButtonLabels.Count,
                    buttons = dialog.ButtonLabels,
                    receivedAtUtc = dialog.ReceivedAtUtc
                }).ToList()
            };

            var message = listAll
                ? $"Retrieved {dialogs.Count} pending script dialog(s)."
                : (dialogs.Count == 0
                    ? $"No pending script dialog found for handle '{normalizedHandle}'."
                    : $"Retrieved pending script dialog '{normalizedHandle}'.");

            return Task.FromResult(DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ScriptDialogChoiceAsync(string pendingDialogHandle, int buttonIndex, CancellationToken cancellationToken)
    {
        var normalizedHandle = (pendingDialogHandle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedHandle))
        {
            return BotToolResult.Fail("pendingDialogHandle is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            EnsureScriptDialogHookRegistered(client);

            if (!_pendingScriptDialogsByHandle.TryGetValue(normalizedHandle, out var pendingDialog))
            {
                return Task.FromResult(BotToolResult.Fail($"No pending script dialog found for handle '{normalizedHandle}'."));
            }

            if (buttonIndex == -1)
            {
                _pendingScriptDialogsByHandle.TryRemove(normalizedHandle, out var _removedCanceled);
                return Task.FromResult(BotToolResult.OkResult($"Canceled pending script dialog '{normalizedHandle}' without sending a choice."));
            }

            if (buttonIndex < 0 || buttonIndex >= pendingDialog.ButtonLabels.Count)
            {
                return Task.FromResult(BotToolResult.Fail($"buttonIndex must be in range 0..{Math.Max(0, pendingDialog.ButtonLabels.Count - 1)} for handle '{normalizedHandle}', or -1 to cancel."));
            }

            var selectedLabel = pendingDialog.ButtonLabels[buttonIndex];
            client.Self.ReplyToScriptDialog(pendingDialog.Channel, buttonIndex, selectedLabel, pendingDialog.ObjectId);
            _pendingScriptDialogsByHandle.TryRemove(normalizedHandle, out var _removedChosen);

            return Task.FromResult(BotToolResult.OkResult(
                $"Sent script dialog choice '{selectedLabel}' (index {buttonIndex}) for handle '{normalizedHandle}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> ListScriptAnimationPermissionRequestsAsync(string? pendingPermissionHandle, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _token) =>
        {
            EnsureScriptDialogHookRegistered(client);

            var normalizedHandle = (pendingPermissionHandle ?? string.Empty).Trim();
            var listAll = string.IsNullOrWhiteSpace(normalizedHandle)
                || string.Equals(normalizedHandle, "all", StringComparison.OrdinalIgnoreCase)
                || normalizedHandle == "*";

            var requests = listAll
                ? _pendingScriptPermissionsByHandle.Values.OrderByDescending(x => x.ReceivedAtUtc).ToList()
                : (_pendingScriptPermissionsByHandle.TryGetValue(normalizedHandle, out var pending)
                    ? new List<PendingScriptPermissionRequest> { pending }
                    : new List<PendingScriptPermissionRequest>());

            var payload = new
            {
                summary = new
                {
                    count = requests.Count,
                    filtered = !listAll,
                    pendingPermissionHandle = listAll ? null : normalizedHandle
                },
                requests = requests.Select(request => (object)new
                {
                    pendingPermissionHandle = request.Handle,
                    taskId = request.TaskId.ToString(),
                    itemId = request.ItemId.ToString(),
                    objectName = request.ObjectName,
                    objectOwnerName = request.ObjectOwnerName,
                    simulator = request.Simulator.Name,
                    requestedPermissions = request.RequestedPermissions.ToString(),
                    requestedPermissionsMask = (int)request.RequestedPermissions,
                    includesTriggerAnimation = (request.RequestedPermissions & ScriptPermission.TriggerAnimation) != 0,
                    receivedAtUtc = request.ReceivedAtUtc
                }).ToList()
            };

            var message = listAll
                ? $"Retrieved {requests.Count} pending animation permission request(s)."
                : (requests.Count == 0
                    ? $"No pending animation permission request found for handle '{normalizedHandle}'."
                    : $"Retrieved pending animation permission request '{normalizedHandle}'.");

            return Task.FromResult(DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ScriptAnimationPermissionRespondAsync(string pendingPermissionHandle, bool allow, CancellationToken cancellationToken)
    {
        var normalizedHandle = (pendingPermissionHandle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedHandle))
        {
            return BotToolResult.Fail("pendingPermissionHandle is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            EnsureScriptDialogHookRegistered(client);

            if (!_pendingScriptPermissionsByHandle.TryGetValue(normalizedHandle, out var pendingRequest))
            {
                return Task.FromResult(BotToolResult.Fail($"No pending animation permission request found for handle '{normalizedHandle}'."));
            }

            var grantedPermissions = allow ? ScriptPermission.TriggerAnimation : ScriptPermission.None;
            client.Self.ScriptQuestionReply(pendingRequest.Simulator, pendingRequest.ItemId, pendingRequest.TaskId, grantedPermissions);
            _pendingScriptPermissionsByHandle.TryRemove(normalizedHandle, out var _removedRequest);

            var action = allow ? "accepted" : "declined";
            return Task.FromResult(BotToolResult.OkResult(
                $"Animation permission request '{normalizedHandle}' {action} for object '{pendingRequest.ObjectName}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureScriptDialogHookRegistered(GridClient client)
    {
        lock (_scriptDialogHookLock)
        {
            if (ReferenceEquals(_scriptDialogHookClient, client))
            {
                return;
            }

            client.Self.ScriptDialog += OnScriptDialogReceived;
            client.Self.ScriptQuestion += OnScriptQuestionReceived;
            _scriptDialogHookClient = client;
        }
    }

    private void OnScriptDialogReceived(object? sender, ScriptDialogEventArgs e)
    {
        var buttonLabels = (e.ButtonLabels ?? new List<string>())
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();

        var ownerName = $"{e.FirstName} {e.LastName}".Trim();
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            ownerName = "(unknown)";
        }

        var objectName = string.IsNullOrWhiteSpace(e.ObjectName) ? "(unknown)" : e.ObjectName;
        var pending = new PendingScriptDialog(
            $"script-dialog:{Guid.NewGuid():N}",
            e.ObjectID,
            objectName,
            e.OwnerID,
            ownerName,
            e.ImageID,
            e.Channel,
            (e.Message ?? string.Empty).Trim(),
            buttonLabels,
            DateTimeOffset.UtcNow);

        StorePendingScriptDialog(pending);

        EmitRuntimeEvent(
            "general",
            "script.dialog.received",
            "opensim",
            $"Script dialog received from '{objectName}'.",
            new Dictionary<string, string?>
            {
                ["pendingDialogHandle"] = pending.Handle,
                ["objectId"] = pending.ObjectId.ToString(),
                ["objectName"] = pending.ObjectName,
                ["ownerId"] = pending.OwnerId.ToString(),
                ["ownerName"] = pending.OwnerName,
                ["imageId"] = pending.ImageId.ToString(),
                ["channel"] = pending.Channel.ToString(),
                ["message"] = pending.Message,
                ["buttons"] = string.Join("|", pending.ButtonLabels),
                ["buttonCount"] = pending.ButtonLabels.Count.ToString()
            });
    }

    private void OnScriptQuestionReceived(object? sender, ScriptQuestionEventArgs e)
    {
        if ((e.Questions & ScriptPermission.TriggerAnimation) == 0)
        {
            return;
        }

        var pending = new PendingScriptPermissionRequest(
            $"script-permission:{Guid.NewGuid():N}",
            e.Simulator,
            e.TaskID,
            e.ItemID,
            string.IsNullOrWhiteSpace(e.ObjectName) ? "(unknown)" : e.ObjectName,
            string.IsNullOrWhiteSpace(e.ObjectOwnerName) ? "(unknown)" : e.ObjectOwnerName,
            e.Questions,
            DateTimeOffset.UtcNow);

        StorePendingScriptPermissionRequest(pending);

        EmitRuntimeEvent(
            "general",
            "script.permission.animation.requested",
            "opensim",
            $"Animation permission requested by '{pending.ObjectName}'.",
            new Dictionary<string, string?>
            {
                ["pendingPermissionHandle"] = pending.Handle,
                ["taskId"] = pending.TaskId.ToString(),
                ["itemId"] = pending.ItemId.ToString(),
                ["objectName"] = pending.ObjectName,
                ["objectOwnerName"] = pending.ObjectOwnerName,
                ["simulator"] = pending.Simulator.Name,
                ["requestedPermissions"] = pending.RequestedPermissions.ToString(),
                ["requestedPermissionsMask"] = ((int)pending.RequestedPermissions).ToString(),
                ["includesTriggerAnimation"] = "true"
            });
    }

    private void StorePendingScriptDialog(PendingScriptDialog pending)
    {
        lock (_pendingScriptDialogLock)
        {
            _pendingScriptDialogsByHandle[pending.Handle] = pending;
            _pendingScriptDialogOrder.Enqueue(pending.Handle);

            while (_pendingScriptDialogsByHandle.Count > PendingScriptDialogCacheLimit
                   && _pendingScriptDialogOrder.TryDequeue(out var oldestHandle))
            {
                _pendingScriptDialogsByHandle.TryRemove(oldestHandle, out _);
            }
        }
    }

    private void StorePendingScriptPermissionRequest(PendingScriptPermissionRequest pending)
    {
        lock (_pendingScriptPermissionLock)
        {
            _pendingScriptPermissionsByHandle[pending.Handle] = pending;
            _pendingScriptPermissionOrder.Enqueue(pending.Handle);

            while (_pendingScriptPermissionsByHandle.Count > PendingScriptPermissionCacheLimit
                   && _pendingScriptPermissionOrder.TryDequeue(out var oldestHandle))
            {
                _pendingScriptPermissionsByHandle.TryRemove(oldestHandle, out _);
            }
        }
    }
}
