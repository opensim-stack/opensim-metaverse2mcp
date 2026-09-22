using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Runtime;

namespace Opensim.Metaverse2Mcp;

internal static class ThreadDumpCollector
{
    private const string CollectorFlag = "--thread-dump-collector";

    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (!args.Contains(CollectorFlag, StringComparer.Ordinal))
        {
            return null;
        }

        var result = await ExecuteAsync(args).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(result);
        await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        return result.Ok ? 0 : 1;
    }

    private static Task<ThreadDumpCollectorResponse> ExecuteAsync(string[] args)
    {
        if (!TryReadIntArg(args, "--pid", out var pid) || pid <= 0)
        {
            return Task.FromResult(ThreadDumpCollectorResponse.Fail("Invalid or missing --pid argument."));
        }

        var maxFrames = 128;
        if (TryReadIntArg(args, "--max-frames", out var parsedMaxFrames))
        {
            maxFrames = parsedMaxFrames;
        }

        if (maxFrames is < 1 or > 512)
        {
            return Task.FromResult(ThreadDumpCollectorResponse.Fail("--max-frames must be between 1 and 512."));
        }

        var blockedOnly = TryReadBoolArg(args, "--blocked-only", out var parsedBlockedOnly) && parsedBlockedOnly;

        try
        {
            using var dataTarget = DataTarget.AttachToProcess(pid, suspend: false);
            var clr = dataTarget.ClrVersions.FirstOrDefault();
            if (clr is null)
            {
                return Task.FromResult(ThreadDumpCollectorResponse.Fail("Unable to locate CLR runtime for target process."));
            }

            using var runtime = clr.CreateRuntime();
            var threadDumps = new List<object>();
            var totalLiveThreads = 0;
            foreach (var thread in runtime.Threads)
            {
                if (!thread.IsAlive)
                {
                    continue;
                }

                totalLiveThreads++;

                var frames = new List<object>();
                var frameMethods = new List<string>();
                foreach (var frame in thread.EnumerateStackTrace().Take(maxFrames))
                {
                    var method = frame.Method;
                    var methodName = method?.Signature ?? method?.Name ?? "<unknown>";
                    frameMethods.Add(methodName);
                    frames.Add(new
                    {
                        instructionPointer = $"0x{frame.InstructionPointer:x}",
                        stackPointer = $"0x{frame.StackPointer:x}",
                        frameKind = frame.Kind.ToString(),
                        method = methodName
                    });
                }

                var blockingSignals = GetBlockingSignals(thread.LockCount, thread.CurrentException?.Message, frameMethods);
                if (blockedOnly && blockingSignals.Count == 0)
                {
                    continue;
                }

                threadDumps.Add(new
                {
                    managedThreadId = thread.ManagedThreadId,
                    osThreadId = thread.OSThreadId,
                    isAlive = thread.IsAlive,
                    isFinalizer = thread.IsFinalizer,
                    lockCount = thread.LockCount,
                    currentException = thread.CurrentException?.Message,
                    blockingSignals,
                    frames
                });
            }

            var payload = new
            {
                utc = DateTimeOffset.UtcNow,
                process = new
                {
                    pid,
                    processName = TryGetProcessName(pid)
                },
                blockedOnly,
                totalLiveThreadCount = totalLiveThreads,
                threadCount = threadDumps.Count,
                maxFramesPerThread = maxFrames,
                threads = threadDumps
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var message = blockedOnly
                ? $"Captured filtered managed stack dump for {threadDumps.Count} likely blocked/waiting thread(s) out of {totalLiveThreads} live thread(s)."
                : $"Captured managed stack dump for {threadDumps.Count} thread(s).";

            return Task.FromResult(ThreadDumpCollectorResponse.OkResult(message, payloadJson));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ThreadDumpCollectorResponse.Fail($"Collector failed: {ex.Message}"));
        }
    }

    private static bool TryReadIntArg(string[] args, string name, out int value)
    {
        value = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
            {
                continue;
            }

            return int.TryParse(args[i + 1], out value);
        }

        return false;
    }

    private static bool TryReadBoolArg(string[] args, string name, out bool value)
    {
        value = false;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
            {
                continue;
            }

            return bool.TryParse(args[i + 1], out value);
        }

        return false;
    }

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private static List<string> GetBlockingSignals(uint lockCount, string? currentException, IReadOnlyList<string> frameMethods)
    {
        var signals = new List<string>();
        if (lockCount > 0)
        {
            signals.Add($"lock-count:{lockCount}");
        }

        if (!string.IsNullOrWhiteSpace(currentException))
        {
            signals.Add("current-exception");
        }

        if (frameMethods.Count == 0)
        {
            return signals;
        }

        var waitKeywords = new[]
        {
            "Monitor.Wait",
            "WaitHandle.Wait",
            "WaitOne",
            "Thread.Join",
            "SemaphoreSlim.Wait",
            "ManualResetEventSlim.Wait",
            "Task.SpinThenBlockingWait",
            "Task.InternalWait",
            "Task.Wait",
            "ReaderWriterLock",
            "Mutex"
        };

        if (frameMethods.Any(method => waitKeywords.Any(keyword => method.Contains(keyword, StringComparison.Ordinal))))
        {
            signals.Add("wait-frame");
        }

        var topFrame = frameMethods[0];
        var sameAsTop = frameMethods.Count(method => string.Equals(method, topFrame, StringComparison.Ordinal));
        if (sameAsTop >= 3)
        {
            signals.Add("repeated-top-frame");
        }

        return signals;
    }
}

internal sealed record ThreadDumpCollectorResponse(bool Ok, string Message, string? PayloadJson, string? Error)
{
    public static ThreadDumpCollectorResponse OkResult(string message, string payloadJson)
        => new(true, message, payloadJson, null);

    public static ThreadDumpCollectorResponse Fail(string message)
        => new(false, message, null, message);
}
