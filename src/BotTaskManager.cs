using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace Opensim.Metaverse2Mcp;

internal sealed class BotTaskHandle
{
    private int _cancelled;
    private readonly object _completionLock = new();
    private bool _completionReported;
    private bool _completionSuccess;
    private string? _completionMessage;

    public BotTaskHandle(string handle, string description, bool cancelled = false)
    {
        Handle = handle;
        Description = description;
        if (cancelled)
        {
            _cancelled = 1;
        }
    }

    [JsonPropertyName("handle")]
    public string Handle { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled
    {
        get => Volatile.Read(ref _cancelled) == 1;
        set => Volatile.Write(ref _cancelled, value ? 1 : 0);
    }

    public void ReportCompletion(bool success, string? message)
    {
        lock (_completionLock)
        {
            _completionReported = true;
            _completionSuccess = success;
            _completionMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
    }

    public (bool Reported, bool Success, string? Message) ReadCompletion()
    {
        lock (_completionLock)
        {
            return (_completionReported, _completionSuccess, _completionMessage);
        }
    }
}

internal sealed record BotTaskInfo(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("startedAtUtc")] string StartedAtUtc,
    [property: JsonPropertyName("completedAtUtc")] string? CompletedAtUtc,
    [property: JsonPropertyName("message")] string Message);

internal sealed class BotTaskManager : IDisposable
{
    private const int MaxRecentHistory = 200;
    private readonly CancellationToken _lifecycleToken;
    private readonly ConcurrentDictionary<string, BotTaskState> _activeTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();
    private readonly Dictionary<string, BotTaskInfo> _recentByHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentOrder = new();

    public BotTaskManager(CancellationToken lifecycleToken)
    {
        _lifecycleToken = lifecycleToken;
    }

    public BotTaskHandle Start(string description, Func<BotTaskHandle, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? "Background task"
            : description.Trim();

        var id = Guid.NewGuid().ToString("D");
        var handle = new BotTaskHandle(id, normalizedDescription);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleToken);
        var state = new BotTaskState(handle, linkedCts, DateTimeOffset.UtcNow);

        _activeTasks[id] = state;

        _ = RunTaskAsync(state, work);
        return handle;
    }

    public IReadOnlyList<BotTaskHandle> ListActive()
    {
        return _activeTasks.Values
            .Select(state => new BotTaskHandle(state.Handle.Handle, state.Handle.Description, state.Handle.Cancelled))
            .OrderBy(state => state.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(state => state.Handle, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool TryCancel(string handle, out BotTaskHandle? taskHandle)
    {
        taskHandle = null;
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        if (!_activeTasks.TryGetValue(handle.Trim(), out var state))
        {
            return false;
        }

        state.Handle.Cancelled = true;
        try
        {
            state.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The task completed while cancellation was being requested.
        }

        taskHandle = state.Handle;
        return true;
    }

    public bool TryGet(string handle, out BotTaskInfo? taskInfo)
    {
        taskInfo = null;
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        var key = handle.Trim();
        if (_activeTasks.TryGetValue(key, out var active))
        {
            var status = active.Handle.Cancelled ? "cancelling" : "running";
            var message = active.Handle.Cancelled ? "Cancellation requested." : "Task is running.";
            taskInfo = new BotTaskInfo(
                active.Handle.Handle,
                active.Handle.Description,
                active.Handle.Cancelled,
                status,
                active.StartedAtUtc.ToString("O"),
                null,
                message);
            return true;
        }

        lock (_historyLock)
        {
            return _recentByHandle.TryGetValue(key, out taskInfo);
        }
    }

    public bool TryReportCompletion(string handle, bool success, string? message)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        if (!_activeTasks.TryGetValue(handle.Trim(), out var state))
        {
            return false;
        }

        state.Handle.ReportCompletion(success, message);
        return true;
    }

    public void Dispose()
    {
        foreach (var state in _activeTasks.Values)
        {
            state.Handle.Cancelled = true;
            try
            {
                state.Cancellation.Cancel();
            }
            catch
            {
                // Best effort during shutdown.
            }
        }
    }

    private async Task RunTaskAsync(BotTaskState state, Func<BotTaskHandle, CancellationToken, Task> work)
    {
        var completedAtUtc = DateTimeOffset.UtcNow;
        var completionStatus = "completed";
        var completionMessage = "Task completed.";

        try
        {
            await work(state.Handle, state.Cancellation.Token).ConfigureAwait(false);

            var reported = state.Handle.ReadCompletion();
            if (reported.Reported)
            {
                completionStatus = reported.Success ? "succeeded" : "failed";
                completionMessage = string.IsNullOrWhiteSpace(reported.Message)
                    ? (reported.Success ? "Task completed successfully." : "Task failed.")
                    : reported.Message;
            }
            else if (state.Handle.Cancelled || state.Cancellation.IsCancellationRequested)
            {
                completionStatus = "cancelled";
                completionMessage = "Task was cancelled.";
            }
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            completionStatus = "cancelled";
            completionMessage = "Task was cancelled.";
        }
        catch (Exception ex)
        {
            completionStatus = "failed";
            completionMessage = ex.Message;
            Console.WriteLine($"[bot-task] task {state.Handle.Handle} crashed: {ex.Message}");
        }
        finally
        {
            completedAtUtc = DateTimeOffset.UtcNow;
            _activeTasks.TryRemove(state.Handle.Handle, out _);
            state.Cancellation.Dispose();

            RecordHistory(new BotTaskInfo(
                state.Handle.Handle,
                state.Handle.Description,
                state.Handle.Cancelled,
                completionStatus,
                state.StartedAtUtc.ToString("O"),
                completedAtUtc.ToString("O"),
                completionMessage));
        }
    }

    private void RecordHistory(BotTaskInfo taskInfo)
    {
        lock (_historyLock)
        {
            if (!_recentByHandle.ContainsKey(taskInfo.Handle))
            {
                _recentOrder.Enqueue(taskInfo.Handle);
            }

            _recentByHandle[taskInfo.Handle] = taskInfo;

            while (_recentOrder.Count > MaxRecentHistory)
            {
                var expired = _recentOrder.Dequeue();
                _recentByHandle.Remove(expired);
            }
        }
    }

    private sealed record BotTaskState(BotTaskHandle Handle, CancellationTokenSource Cancellation, DateTimeOffset StartedAtUtc);
}
