using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed class AgentLocator
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MonitorDelayOffline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MonitorDelayOnlineNoLocation = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MonitorDelayOnlineDifferentSim = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FriendMapTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AvatarNameSearchTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SingleSnapshotFriendMapTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ExternalFallbackProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly HashSet<UUID> FlyingAnimationIds = new()
    {
        Animations.FLY,
        Animations.FLYSLOW,
        Animations.HOVER,
        Animations.HOVER_UP,
        Animations.HOVER_DOWN,
    };
    private static readonly HashSet<UUID> GroundedAnimationIds = new()
    {
        Animations.STAND,
        Animations.STAND_1,
        Animations.STAND_2,
        Animations.STAND_3,
        Animations.STAND_4,
        Animations.WALK,
        Animations.RUN,
        Animations.STRIDE,
        Animations.TURNLEFT,
        Animations.TURNRIGHT,
        Animations.CROUCH,
        Animations.CROUCHWALK,
        Animations.SIT,
        Animations.SIT_FEMALE,
        Animations.SIT_GENERIC,
        Animations.SIT_GROUND,
        Animations.LAND,
        Animations.MEDIUM_LAND,
    };
    private readonly BotSession _bot;
    private readonly SpawnerClient _spawnerClient;
    private readonly bool _allowExternalFallback;
    private SpawnerLocatedAgent? _cachedExternalFallbackResult;
    private readonly ConcurrentDictionary<string, AgentMonitorSnapshot> _latestStatusByHandle = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastExternalProbeAtUtc = DateTime.MinValue;

    public AgentLocator(BotSession bot, SpawnerClient spawnerClient)
    {
        _bot = bot;
        _spawnerClient = spawnerClient;
        _allowExternalFallback = string.Equals(
            Environment.GetEnvironmentVariable("AGENT_MONITOR_EXTERNAL_FALLBACK"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private void LogDiag(string message)
    {
        Console.WriteLine($"[agent-locator][diag] {message}");
    }

    private void LogDiag(string? correlationId, string message)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            LogDiag(message);
            return;
        }

        LogDiag($"corr={correlationId} {message}");
    }

    public async Task<BotTaskHandle> MonitorAgent(string targetAgentId, CancellationToken cancellationToken, string? correlationId = null)
    {
        
        /* TODO there may be a better way to do this. AvatarManager, RequestTrackAgent, but I'm not sure how it works. 
         */
         
        BotTaskHandle QueueImmediateFailure(string failureMessage)
        {
            return _bot.StartBotTask(
                "Monitor agent.",
                (taskHandle, _) =>
                {
                    EmitStatusChangedEvent(taskHandle.Handle, UUID.Zero, AgentMonitorStatus.Unknown, failureMessage);
                    return Task.CompletedTask;
                });
        }

        if (string.IsNullOrWhiteSpace(targetAgentId))
        {
            return QueueImmediateFailure("targetAgentId is required.");
        }

        if (!UUID.TryParse(targetAgentId.Trim(), out var targetId) || targetId == UUID.Zero)
        {
            return QueueImmediateFailure("targetAgentId must be a valid non-zero UUID.");
        }

        // Perform one best-effort capture on the caller thread so initial status is available immediately.
        
        if (_bot.TryGetConnectedClientSnapshot(out var connectedClient) && connectedClient != null)
        {
            var initialStatus = await CaptureInitialStatusBestEffortAsync(targetId, connectedClient, cancellationToken, correlationId).ConfigureAwait(false);
    
            var task = _bot.StartBotTask(
                $"Monitor agent '{targetId}'.",
                async (taskHandle, taskCancellationToken) =>
                {

                    try
                    {
                        AgentMonitorStatus? previous = initialStatus;
                        var lastStatus = initialStatus;
        
                        _latestStatusByHandle[taskHandle.Handle] = ToSnapshot(targetId, initialStatus);
                        while (!taskCancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                var current = await CaptureStatusAsync(targetId, connectedClient, previous, taskCancellationToken, correlationId).ConfigureAwait(false);
                                current = current with
                                {
                                    HeadingDegrees = TryComputeHeading(previous, current)
                                };

                                _latestStatusByHandle[taskHandle.Handle] = ToSnapshot(targetId, current);
                                lastStatus = current;

                                if (!AreEquivalent(previous, current))
                                {
                                    EmitStatusChangedEvent(taskHandle.Handle, targetId, current, "Agent monitor status changed.");
                                    previous = current;
                                }
                            }
                            catch (OperationCanceledException) when (taskCancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception)
                            {
                                // Keep monitor alive across transient cache/network races.
                            }

                            // Progressive back-off: slower polling when target is offline or not locally resolvable.
                            var delay = GetMonitorDelay(lastStatus, connectedClient.Network.CurrentSim?.Handle);
                            await Task.Delay(delay, taskCancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _latestStatusByHandle.TryRemove(taskHandle.Handle, out _);
                    }
                });
                

            _latestStatusByHandle[task.Handle] = ToSnapshot(targetId, initialStatus);
            EmitStatusChangedEvent(task.Handle, targetId, initialStatus, "Agent monitor initialized.");
    
            cancellationToken.Register(() =>
            {
                try
                {
                    var cancelResult = _bot.CancelBotTask(task.Handle);
                }
                catch (Exception ex)
                {
                    // Best effort if caller cancellation races with task registration.
                    Console.WriteLine($"[agent-monitor] caller-cancel-error handle={task.Handle} target={targetId} message={ex.Message}");
                }
            });

            return task;
        }
        else
        {
            throw new InvalidOperationException("No connected client snapshot available for agent monitoring.");
        }
    }

    public bool TryGetLatestStatus(string monitorHandle, out AgentMonitorSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(monitorHandle))
        {
            snapshot = default!;
            return false;
        }

        return _latestStatusByHandle.TryGetValue(monitorHandle.Trim(), out snapshot!);
    }

    public async Task<(bool Ok, UUID AgentId, string? ErrorMessage)> ResolveAgentIdByNameAsync(
        string first,
        string last,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return (false, UUID.Zero, "first and last are required.");
        }
        var normalizedFirst = first.Trim();
        var normalizedLast = last.Trim();
        var query = $"{normalizedFirst} {normalizedLast}";

        if (!_bot.TryGetConnectedClientSnapshot(out var connectedClient) || connectedClient == null)
        {
            return (false, UUID.Zero, "No connected client snapshot available for avatar name search.");
        }

        var queryId = UUID.Random();
        var reply = await WaitForAvatarPickerReplyAsync(connectedClient, query, queryId, AvatarNameSearchTimeout, cancellationToken).ConfigureAwait(false);
        if (reply == null)
        {
            return (false, UUID.Zero, $"Timed out waiting for avatar name search reply for '{query}'.");
        }

        foreach (var candidate in reply.Avatars)
        {
            if (candidate.Key == UUID.Zero)
            {
                continue;
            }

            if (string.Equals(candidate.Value, query, StringComparison.OrdinalIgnoreCase))
            {
                return (true, candidate.Key, null);
            }
        }

        return (false, UUID.Zero, $"Agent '{query}' could not be found.");
    }

    private static async Task<AvatarPickerReplyEventArgs?> WaitForAvatarPickerReplyAsync(
        GridClient client,
        string query,
        UUID queryId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AvatarPickerReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, AvatarPickerReplyEventArgs e)
        {
            if (e.QueryID == queryId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Avatars.AvatarPickerReply += Handler;
        try
        {
            client.Avatars.RequestAvatarNameSearch(query, queryId);
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Avatars.AvatarPickerReply -= Handler;
        }
    }

    public async Task<(bool Ok, AgentMonitorSnapshot? Snapshot, string? ErrorMessage)> ReadSingleMonitorSnapshotAsync(
        UUID targetId,
        CancellationToken cancellationToken)
    {
        if (targetId == UUID.Zero)
        {
            return (false, null, "targetId must be a valid non-zero UUID.");
        }

        var correlationId = Guid.NewGuid().ToString("N")[..8];

        if (!_bot.TryGetConnectedClientSnapshot(out var connectedClient) || connectedClient == null)
        {
            return (false, null, "No connected client snapshot available for agent monitor lookup.");
        }

        try
        {
            var status = await CaptureStatusAsync(
                targetId,
                connectedClient,
                previous: null,
                cancellationToken,
                correlationId,
                friendMapTimeoutOverride: SingleSnapshotFriendMapTimeout,
                unresolvedAsOffline: true).ConfigureAwait(false);

            var snapshot = ToSnapshot(targetId, status);
            LogDiag(correlationId, $"single-read resolved target={targetId} online={(snapshot.Online.HasValue ? (snapshot.Online.Value ? "true" : "false") : "unknown")} regionHandle={(snapshot.RegionHandle?.ToString(CultureInfo.InvariantCulture) ?? "n/a")} pos={(snapshot.Position?.ToString() ?? "n/a")}");
            return (true, snapshot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDiag(correlationId, $"single-read failed target={targetId} message={ex.Message}");
            return (false, null, "Failed to read agent monitor snapshot.");
        }
    }

    private async Task<AgentMonitorStatus> CaptureInitialStatusBestEffortAsync(UUID targetId, GridClient client, CancellationToken cancellationToken, string? correlationId = null)
    {
        try
        {
            return await CaptureStatusAsync(targetId, client, previous: null, cancellationToken, correlationId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return AgentMonitorStatus.Unknown;
        }
    }

    private static AgentMonitorSnapshot ToSnapshot(UUID targetId, AgentMonitorStatus status)
    {
        return new AgentMonitorSnapshot(
            targetId,
            status.Online,
            status.RegionName,
            status.RegionHandle,
            status.Position,
            status.IsFlying,
            status.Velocity,
            status.HeadingDegrees,
            status.LocalId);
    }

    private async Task<AgentMonitorStatus> CaptureStatusAsync(
        UUID targetId,
        GridClient client,
        AgentMonitorStatus? previous,
        CancellationToken cancellationToken,
        string? correlationId = null,
        TimeSpan? friendMapTimeoutOverride = null,
        bool unresolvedAsOffline = false)
    {
        var status = AgentMonitorStatus.Unknown;

        var currentSim = client.Network.CurrentSim;
        var expectedLocalId = previous?.LocalId;
            
        AgentMonitorStatus? candidate = null;
            
        // Branch: friend-map
        var friendMapTimeout = friendMapTimeoutOverride ?? FriendMapTimeout;
        var mapReply = await TryMapFriendLocationOnceAsync(client, targetId, friendMapTimeout, cancellationToken).ConfigureAwait(false);
        if (mapReply != null)
        {
            LogDiag(correlationId, $"friend-map hit target={targetId} regionHandle={mapReply.RegionHandle} pos={FormatVector(mapReply.Location)}");
            candidate = status with
            {
                Online = true,
                RegionName = RegionNameFromHandle(client, mapReply.RegionHandle),
                RegionHandle = mapReply.RegionHandle,
                Position = mapReply.Location,
                IsFlying = null,
                Velocity = null,
                HeadingDegrees = null
            };
        }
        else
        {
            LogDiag(correlationId, $"friend-map miss target={targetId}");
        }

        // Branch: friend-presence fallback (online visibility can be granted even when map visibility is denied).
        var friendOnlineHint = TryResolveFriendOnlinePresence(client, targetId);
        if (candidate == null && friendOnlineHint.HasValue)
        {
            LogDiag(correlationId, $"friend-presence hint target={targetId} online={(friendOnlineHint.Value ? "true" : "false")}");
            candidate = status with
            {
                Online = friendOnlineHint.Value,
                RegionName = null,
                RegionHandle = null,
                Position = null,
                IsFlying = null,
                Velocity = null,
                HeadingDegrees = null,
                LocalId = null
            };
        }
        

        // Branch: external fallback with throttled probing + cached result reuse.
        if (candidate == null && _allowExternalFallback)
        {
            if (DateTime.UtcNow - _lastExternalProbeAtUtc >= ExternalFallbackProbeInterval)
            {
                _lastExternalProbeAtUtc = DateTime.UtcNow;
                var spawnerResult = await TryLocateAgentViaSpawnerAsync(client, targetId, cancellationToken).ConfigureAwait(false);
                if (spawnerResult != null)
                {
                    _cachedExternalFallbackResult = spawnerResult;
                }
            }

            if (_cachedExternalFallbackResult != null)
            {
                if (!_cachedExternalFallbackResult.Found)
                {
                    LogDiag(correlationId, $"external-fallback offline target={targetId}");
                    return status with
                    {
                        Online = false
                    };
                }

                var cached = _cachedExternalFallbackResult;
                var cachedHandle = cached.RegionHandle == 0 ? null : (ulong?)cached.RegionHandle;
                var cachedRegionName = ResolveRegionName(cached.RegionName, cached.Simulator, cached.RegionHandle);
                LogDiag(correlationId, $"external-fallback hit target={targetId} found={cached.Found} regionHandle={(cachedHandle?.ToString(CultureInfo.InvariantCulture) ?? "n/a")} pos={FormatVector(cached.Position)}");
                return status with
                {
                    Online = true,
                    RegionName = cachedRegionName,
                    RegionHandle = cachedHandle,
                    Position = cached.Position,
                    IsFlying = null,
                    Velocity = null,
                    HeadingDegrees = null,
                    LocalId = null
                };
            }
        }
        
        if (currentSim != null && TryFindAvatarByIdInSim(currentSim, targetId, expectedLocalId, out var foundAvatar))
        {
            // Branch: current-sim cache hit, dont take as authorative unless friend map matches region
            var knownPosition = foundAvatar?.Position;
            
            if(candidate == null || ( candidate.RegionHandle == null || candidate.RegionHandle == currentSim?.Handle))
            {
                LogDiag(correlationId, $"current-sim hit target={targetId} sim={(currentSim?.Name ?? "(unknown)")} localId={(foundAvatar?.LocalID.ToString(CultureInfo.InvariantCulture) ?? "n/a")} pos={(knownPosition.HasValue ? FormatVector(knownPosition.Value) : "n/a")}");
                return new AgentMonitorStatus(
                    Online: true,
                    RegionName: RegionNameFromHandle(client, foundAvatar?.RegionHandle ?? 0),
                    RegionHandle: foundAvatar?.RegionHandle,
                    Position: knownPosition,
                    IsFlying: foundAvatar == null ? null : ReadFlyingSignal(foundAvatar),
                    Velocity: foundAvatar?.Velocity,
                    HeadingDegrees: null,
                    LocalId: foundAvatar?.LocalID);
                
            }
            else 
            {
                // Branch: potentially stale current-sim cache hit, but we have a friend-map or spawner candidate that is off-region.  In this case, we will return the candidate as authoritative and clear the LocalId since it is not valid in the candidate's region.
                LogDiag(correlationId, $"current-sim stale target={targetId} sim={(currentSim?.Name ?? "(unknown)")} candidateRegionHandle={(candidate.RegionHandle?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}");
                return candidate with
                {
                    LocalId = null
                };
            }
        }

        if (candidate != null)
        {
            // Critical: if map/spawner resolved the avatar in another region and there is no current-sim cache hit,
            // return that candidate instead of downgrading to Unknown.
            LogDiag(correlationId, $"candidate-only return target={targetId} regionHandle={(candidate.RegionHandle?.ToString(CultureInfo.InvariantCulture) ?? "n/a")} pos={(candidate.Position.HasValue ? FormatVector(candidate.Position.Value) : "n/a")}");
            return candidate with
            {
                LocalId = null
            };
        }
        
        // Branch: connected but target not found anywhere this cycle.
        LogDiag(correlationId, $"unresolved target={targetId}");
        return status with
        {
            Online = unresolvedAsOffline
                ? (friendOnlineHint ?? false)
                : friendOnlineHint,
            LocalId = null
        };
    }

    private static bool? TryResolveFriendOnlinePresence(GridClient client, UUID targetId)
    {
        if (targetId == UUID.Zero)
        {
            return null;
        }

        if (!client.Friends.FriendList.TryGetValue(targetId, out var friend) || friend == null)
        {
            return null;
        }

        // Without online-visibility rights, IsOnline cannot be trusted for this target.
        return friend.CanSeeThemOnline ? friend.IsOnline : null;
    }

    private static TimeSpan GetMonitorDelay(AgentMonitorStatus status, ulong? currentSimHandle)
    {
        if (status.Online == false)
        {
            return MonitorDelayOffline;
        }

        if (status.Online == true)
        {
            var regionHandle = status.RegionHandle;
            var hasLocation = regionHandle.HasValue
                && regionHandle.Value > 0
                && status.Position.HasValue;

            if (!hasLocation)
            {
                return MonitorDelayOnlineNoLocation;
            }

            if (currentSimHandle.HasValue && regionHandle.HasValue && regionHandle.Value == currentSimHandle.Value)
            {
                return MonitorInterval;
            }

            return MonitorDelayOnlineDifferentSim;
        }

        return MonitorDelayOnlineNoLocation;
    }

    private static bool? ReadFlyingSignal(Avatar avatar)
    {
        if ((avatar.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0)
        {
            return true;
        }

        if ((avatar.Flags & PrimFlags.Flying) != 0)
        {
            return true;
        }

        var fromAnimations = TryReadFlyingSignalFromAnimations(avatar);
        if (fromAnimations.HasValue)
        {
            return fromAnimations.Value;
        }

        // If we can see the avatar but have no explicit flying evidence, treat it as not flying.
        return false;
    }

    private static bool? TryReadFlyingSignalFromAnimations(Avatar avatar)
    {
        List<Animation>? snapshot = null;
        lock (avatar)
        {
            if (avatar.Animations == null || avatar.Animations.Count == 0)
            {
                return null;
            }

            snapshot = avatar.Animations.ToList();
        }

        var sawGrounded = false;
        foreach (var animation in snapshot)
        {
            if (FlyingAnimationIds.Contains(animation.AnimationID))
            {
                return true;
            }

            if (GroundedAnimationIds.Contains(animation.AnimationID))
            {
                sawGrounded = true;
            }
        }

        return sawGrounded ? false : null;
    }

    private static bool TryFindAvatarByIdInSim(
        Simulator simulator,
        UUID avatarId,
        uint? expectedLocalId,
        out Avatar? foundAvatar)
    {
        foundAvatar = null;
        if (avatarId == UUID.Zero)
        {
            return false;
        }
        
        
        var matches = simulator.ObjectsAvatars.Values
            .Where(avatar => avatar != null && avatar.ID == avatarId)
            .ToList();
        var allLocalIds = string.Join(",", matches.Select(avatar => avatar.LocalID.ToString(CultureInfo.InvariantCulture)));

        if (matches.Count == 0)
        {
            return false;
        }

        var currentRegionMatches = matches
            .Where(avatar => avatar.RegionHandle == simulator.Handle)
            .ToList();
        if (currentRegionMatches.Count > 0)
        {
            foundAvatar = SelectDeterministicAvatar(currentRegionMatches, expectedLocalId, out var matchedExpected);

            // If we expected a concrete LocalId in this sim and it is missing,
            // do not silently fallback here; allow off-region resolution to run.
            if (expectedLocalId.HasValue && !matchedExpected)
            {
                foundAvatar = null;
                return false;
            }

            return foundAvatar != null;
        }

        return false;
    }

    private static Avatar? SelectDeterministicAvatar(IReadOnlyList<Avatar> matches, uint? expectedLocalId, out bool matchedExpected)
    {
        matchedExpected = false;
        if (matches.Count == 0)
        {
            return null;
        }

        if (expectedLocalId.HasValue)
        {
            var expected = matches.FirstOrDefault(avatar => avatar.LocalID == expectedLocalId.Value);
            if (expected != null)
            {
                matchedExpected = true;
                return expected;
            }
        }

        return matches
            .OrderBy(avatar => avatar.LocalID)
            .FirstOrDefault();
    }

    private static float? TryComputeHeading(AgentMonitorStatus? previous, AgentMonitorStatus current)
    {
        if (previous == null || current.Position == null || previous.Position == null)
        {
            return null;
        }

        if (current.RegionHandle == null || previous.RegionHandle == null)
        {
            return null;
        }

        var currentGlobal = ToGlobalPosition(current.RegionHandle.Value, current.Position.Value);
        var previousGlobal = ToGlobalPosition(previous.RegionHandle.Value, previous.Position.Value);
        var deltaX = currentGlobal.X - previousGlobal.X;
        var deltaY = currentGlobal.Y - previousGlobal.Y;

        if (Math.Abs(deltaX) < 0.001f && Math.Abs(deltaY) < 0.001f)
        {
            return null;
        }

        var heading = MathF.Atan2(deltaY, deltaX) * (180f / MathF.PI);
        if (heading < 0f)
        {
            heading += 360f;
        }

        return heading;
    }

    private static string? RegionNameFromHandle(GridClient client, ulong regionHandle)
    {
        
        var byName = client.Network.Simulators.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate.Name)
            && candidate.Handle == regionHandle);
            
        if(byName == null)
        {
            return regionHandle > 0
                ? regionHandle.ToString(CultureInfo.InvariantCulture)
                : null;
        }
        else
        {
            return byName.Name;
        }
    }


    private static string? ResolveRegionName(string? preferredRegionName, Simulator? simulator, ulong regionHandle)
    {
        if (!string.IsNullOrWhiteSpace(preferredRegionName))
        {
            return preferredRegionName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(simulator?.Name))
        {
            return simulator!.Name;
        }

        return regionHandle > 0
            ? regionHandle.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private void EmitStatusChangedEvent(string handle, UUID targetId, AgentMonitorStatus status, string message)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["handle"] = handle,
            ["targetAgentId"] = targetId == UUID.Zero ? null : targetId.ToString(),
            ["online"] = status.Online.HasValue ? (status.Online.Value ? "true" : "false") : null,
            ["region"] = status.RegionName,
            ["regionHandle"] = status.RegionHandle?.ToString(CultureInfo.InvariantCulture),
            ["x"] = status.Position?.X.ToString("0.###", CultureInfo.InvariantCulture),
            ["y"] = status.Position?.Y.ToString("0.###", CultureInfo.InvariantCulture),
            ["z"] = status.Position?.Z.ToString("0.###", CultureInfo.InvariantCulture),
            ["isFlying"] = status.IsFlying.HasValue ? (status.IsFlying.Value ? "true" : "false") : null,
            ["velocityX"] = status.Velocity?.X.ToString("0.###", CultureInfo.InvariantCulture),
            ["velocityY"] = status.Velocity?.Y.ToString("0.###", CultureInfo.InvariantCulture),
            ["velocityZ"] = status.Velocity?.Z.ToString("0.###", CultureInfo.InvariantCulture),
            ["headingDegrees"] = status.HeadingDegrees?.ToString("0.###", CultureInfo.InvariantCulture),
            ["localId"] = status.LocalId?.ToString(CultureInfo.InvariantCulture)
        };

        _bot.EmitAgentMonitorRuntimeEvent(
            "agentMonitor.statusChanged",
            message,
            attributes);
    }

    private static bool AreEquivalent(AgentMonitorStatus previous, AgentMonitorStatus current)
    {
        return previous.Online == current.Online
            && string.Equals(previous.RegionName, current.RegionName, StringComparison.OrdinalIgnoreCase)
            && previous.RegionHandle == current.RegionHandle
            && NullableVectorEquals(previous.Position, current.Position)
            && previous.IsFlying == current.IsFlying
            && NullableVectorEquals(previous.Velocity, current.Velocity)
            && NullableFloatEquals(previous.HeadingDegrees, current.HeadingDegrees)
            && previous.LocalId == current.LocalId;
    }

    private static bool NullableVectorEquals(Vector3? left, Vector3? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return Math.Abs(left.Value.X - right.Value.X) < 0.001f
            && Math.Abs(left.Value.Y - right.Value.Y) < 0.001f
            && Math.Abs(left.Value.Z - right.Value.Z) < 0.001f;
    }

    private static bool NullableFloatEquals(float? left, float? right)
    {
        if (left == null || right == null)
        {
            return left == right;
        }

        return Math.Abs(left.Value - right.Value) < 0.001f;
    }

    private static async Task<FriendFoundReplyEventArgs?> TryMapFriendLocationOnceAsync(
        GridClient client,
        UUID friendId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var replyTask = WaitForFriendFoundReplyAsync(client, friendId, timeout, cancellationToken);
            client.Friends.MapFriend(friendId);
            return await replyTask.ConfigureAwait(false);
        }
        catch
        {
            return null;
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

    private async Task<SpawnerLocatedAgent?> TryLocateAgentViaSpawnerAsync(
        GridClient? client,
        UUID trackedId,
        CancellationToken cancellationToken)
    {
        DataToolResult result;
        try
        {
            result = await _spawnerClient
                .FindAgentByUuidAsync(trackedId.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        if (!result.Ok || !TryParseSpawnerAgentResponse(result.PayloadJson, out var parsed))
        {
            return null;
        }

        Simulator? simulator = null;
        ulong regionHandle = 0;
        if (parsed.Found && client != null && TryResolveConnectedSimulator(client, parsed.RegionId, parsed.RegionName, out simulator))
        {
            regionHandle = simulator!.Handle;
        }
        else if (parsed.Found && client != null && !string.IsNullOrWhiteSpace(parsed.RegionName))
        {
            try
            {
                var region = await client.Grid
                    .GetGridRegionAsync(parsed.RegionName, GridLayerType.Objects, cancellationToken)
                    .ConfigureAwait(false);
                if (region.HasValue)
                {
                    regionHandle = region.Value.RegionHandle;
                    simulator = client.Network.Simulators.FirstOrDefault(candidate => candidate.Handle == regionHandle);
                }
            }
            catch
            {
                // Best effort only; location lookup should not fail the monitor task.
            }
        }

        return new SpawnerLocatedAgent(
            parsed.Found,
            parsed.RegionId,
            parsed.RegionName,
            parsed.Position,
            regionHandle,
            simulator);
    }

    private static bool TryResolveConnectedSimulator(
        GridClient client,
        UUID regionId,
        string? regionName,
        out Simulator? simulator)
    {
        simulator = null;
        if (regionId != UUID.Zero)
        {
            simulator = client.Network.Simulators.FirstOrDefault(candidate => candidate.ID == regionId);
        }

        if (simulator == null && !string.IsNullOrWhiteSpace(regionName))
        {
            simulator = client.Network.Simulators.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Name)
                && candidate.Name.Equals(regionName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return simulator != null;
    }

    private static Simulator? TryFindSimulatorByHandle(GridClient client, ulong regionHandle)
    {
        if (regionHandle == 0)
        {
            return null;
        }

        return client.Network.Simulators.FirstOrDefault(candidate => candidate.Handle == regionHandle);
    }

    private static bool TryParseSpawnerAgentResponse(string? payloadJson, out SpawnerAgentResponse response)
    {
        response = default;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            if (!root.TryGetProperty("found", out var foundElement))
            {
                return false;
            }

            var found = foundElement.ValueKind == JsonValueKind.True;
            if (!found)
            {
                response = new SpawnerAgentResponse(false, UUID.Zero, null, Vector3.Zero);
                return true;
            }

            var regionId = UUID.Zero;
            if (root.TryGetProperty("regionUuid", out var regionUuidElement)
                && regionUuidElement.ValueKind == JsonValueKind.String)
            {
                var regionUuidText = regionUuidElement.GetString();
                if (!string.IsNullOrWhiteSpace(regionUuidText))
                {
                    UUID.TryParse(regionUuidText, out regionId);
                }
            }

            var regionName = root.TryGetProperty("regionName", out var regionNameElement)
                && regionNameElement.ValueKind == JsonValueKind.String
                ? regionNameElement.GetString()
                : null;

            if (!TryReadJsonSingle(root, "posX", out var posX)
                || !TryReadJsonSingle(root, "posY", out var posY)
                || !TryReadJsonSingle(root, "posZ", out var posZ))
            {
                return false;
            }

            response = new SpawnerAgentResponse(true, regionId, regionName, new Vector3(posX, posY, posZ));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadJsonSingle(JsonElement container, string propertyName, out float value)
    {
        value = 0f;
        if (!container.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetSingle(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static Vector3 ToGlobalPosition(ulong regionHandle, Vector3 localPosition)
    {
        Utils.LongToUInts(regionHandle, out var regionX, out var regionY);
        return new Vector3(regionX + localPosition.X, regionY + localPosition.Y, localPosition.Z);
    }

    private static string FormatVector(Vector3 value)
        => $"<{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}>";

    private readonly record struct SpawnerAgentResponse(bool Found, UUID RegionId, string? RegionName, Vector3 Position);

    private sealed record SpawnerLocatedAgent(
        bool Found,
        UUID RegionId,
        string? RegionName,
        Vector3 Position,
        ulong RegionHandle,
        Simulator? Simulator);

    private sealed record AgentMonitorStatus(
        bool? Online,
        string? RegionName,
        ulong? RegionHandle,
        Vector3? Position,
        bool? IsFlying,
        Vector3? Velocity,
        float? HeadingDegrees,
        uint? LocalId)
    {
        public static AgentMonitorStatus Unknown { get; } = new(null, null, null, null, null, null, null, null);
    }
}

internal sealed record AgentMonitorSnapshot(
    UUID TargetId,
    bool? Online,
    string? RegionName,
    ulong? RegionHandle,
    Vector3? Position,
    bool? IsFlying,
    Vector3? Velocity,
    float? HeadingDegrees,
    uint? LocalId);
