using System.Globalization;
using System.Text.Json;
using System.Collections.Concurrent;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed class AgentLocator
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FriendMapTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ExternalFallbackProbeInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AvatarUpdateFreshnessWindow = TimeSpan.FromSeconds(3);
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

    public async Task<BotTaskHandle> MonitorAgent(string targetAgentId, CancellationToken cancellationToken)
    {
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

        Console.WriteLine($"[agent-monitor] request target={targetId} callerCancelled={cancellationToken.IsCancellationRequested}");

        // Perform one best-effort capture on the caller thread so initial status is available immediately.
        var initialStatus = await CaptureInitialStatusBestEffortAsync(targetId, cancellationToken).ConfigureAwait(false);

        var task = _bot.StartBotTask(
            $"Monitor agent '{targetId}'.",
            async (taskHandle, taskCancellationToken) =>
            {
                AgentMonitorStatus? previous = initialStatus;
                using var avatarUpdates = new AvatarUpdateTracker(targetId);

                Console.WriteLine($"[agent-monitor] task-start handle={taskHandle.Handle} target={targetId} taskCancelled={taskCancellationToken.IsCancellationRequested}");

                _latestStatusByHandle[taskHandle.Handle] = ToSnapshot(targetId, initialStatus);

                try
                {
                    while (!taskCancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var current = await CaptureStatusAsync(targetId, avatarUpdates, previous, taskCancellationToken).ConfigureAwait(false);
                            current = current with
                            {
                                HeadingDegrees = TryComputeHeading(previous, current)
                            };

                            _latestStatusByHandle[taskHandle.Handle] = ToSnapshot(targetId, current);

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
                        catch (Exception ex)
                        {
                            // Keep monitor alive across transient cache/network races.
                            Console.WriteLine($"[agent-monitor] transient-error handle={taskHandle.Handle} target={targetId} type={ex.GetType().Name} message={ex.Message}");
                        }

                        await Task.Delay(MonitorInterval, taskCancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Console.WriteLine($"[agent-monitor] task-exit handle={taskHandle.Handle} target={targetId} cancelled={taskCancellationToken.IsCancellationRequested}");
                    _latestStatusByHandle.TryRemove(taskHandle.Handle, out _);
                }
            });

        _latestStatusByHandle[task.Handle] = ToSnapshot(targetId, initialStatus);
        EmitStatusChangedEvent(task.Handle, targetId, initialStatus, "Agent monitor initialized.");
        Console.WriteLine($"[agent-monitor] started handle={task.Handle} target={targetId} callerCancelled={cancellationToken.IsCancellationRequested} initialSource={initialStatus.Source ?? "unknown"} initialOnline={(initialStatus.Online.HasValue ? (initialStatus.Online.Value ? "true" : "false") : "null")}");

        cancellationToken.Register(() =>
        {
            try
            {
                var cancelResult = _bot.CancelBotTask(task.Handle);
                Console.WriteLine($"[agent-monitor] caller-cancel handle={task.Handle} target={targetId} tokenCancelled={cancellationToken.IsCancellationRequested} cancelOk={cancelResult.Ok} message={cancelResult.Message}");
            }
            catch (Exception ex)
            {
                // Best effort if caller cancellation races with task registration.
                Console.WriteLine($"[agent-monitor] caller-cancel-error handle={task.Handle} target={targetId} message={ex.Message}");
            }
        });

        return task;
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

    private async Task<AgentMonitorStatus> CaptureInitialStatusBestEffortAsync(UUID targetId, CancellationToken cancellationToken)
    {
        using var avatarUpdates = new AvatarUpdateTracker(targetId);
        try
        {
            return await CaptureStatusAsync(targetId, avatarUpdates, previous: null, cancellationToken).ConfigureAwait(false);
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
            status.Source,
            status.LocalId,
            status.AvatarUpdateSeen,
            status.AvatarUpdateSim,
            status.CacheVsAvatarUpdate);
    }

    private async Task<AgentMonitorStatus> CaptureStatusAsync(
        UUID targetId,
        AvatarUpdateTracker avatarUpdates,
        AgentMonitorStatus? previous,
        CancellationToken cancellationToken)
    {
        var status = AgentMonitorStatus.Unknown;

        GridClient? client = null;
        if (_bot.TryGetConnectedClientSnapshot(out var connectedClient) && connectedClient != null)
        {
            client = connectedClient;
            avatarUpdates.Attach(client);

            var currentSim = connectedClient.Network.CurrentSim;
            var latestUpdate = avatarUpdates.TryGetLatest(out var sampledUpdate) ? sampledUpdate : null;
            var preferredLocalId = previous?.LocalId;
            var resolvedOffRegion = ResolveOffRegionLocation(
                connectedClient,
                currentSim,
                targetId,
                preferredLocalId,
                latestUpdate);

            // AvatarUpdate is authoritative for region transitions. If it says the
            // target is in a different region right now, ignore stale in-sim cache data.
            if (currentSim != null && IsFreshOffRegionAvatarUpdate(latestUpdate, currentSim))
            {
                return new AgentMonitorStatus(
                    Online: true,
                    RegionName: resolvedOffRegion.RegionName,
                    RegionHandle: resolvedOffRegion.RegionHandle,
                    Position: resolvedOffRegion.Position,
                    IsFlying: null,
                    Velocity: null,
                    HeadingDegrees: null,
                    Source: "avatarUpdate.offRegion",
                    LocalId: latestUpdate!.LocalId == 0 ? null : latestUpdate.LocalId,
                    AvatarUpdateSeen: true,
                    AvatarUpdateSim: latestUpdate.SimulatorName,
                    CacheVsAvatarUpdate: "cache:stale|avatarUpdate:otherRegion");
            }

            // Keep off-region AvatarUpdate authoritative even after freshness expiry
            // until we see evidence that the target is truly back in this simulator.
            // This avoids stale sim-cache ghosts pinning follow at the border.
            if (currentSim != null && IsOffRegionAvatarUpdate(latestUpdate, currentSim, requireFresh: false))
            {
                return new AgentMonitorStatus(
                    Online: true,
                    RegionName: resolvedOffRegion.RegionName,
                    RegionHandle: resolvedOffRegion.RegionHandle,
                    Position: resolvedOffRegion.Position,
                    IsFlying: null,
                    Velocity: null,
                    HeadingDegrees: null,
                    Source: "avatarUpdate.offRegion.sticky",
                    LocalId: latestUpdate!.LocalId == 0 ? null : latestUpdate.LocalId,
                    AvatarUpdateSeen: true,
                    AvatarUpdateSim: latestUpdate.SimulatorName,
                    CacheVsAvatarUpdate: "cache:blocked|avatarUpdate:otherRegion");
            }

            if (TryBuildStatusFromAvatarUpdate(currentSim, sampledUpdate, out var fromAvatarUpdate))
            {
                if (ShouldTrustAvatarUpdateCurrentRegion(
                    currentSim,
                    targetId,
                    preferredLocalId,
                    previous,
                    latestUpdate,
                    fromAvatarUpdate))
                {
                    return fromAvatarUpdate;
                }
            }
            
            if (currentSim != null && TryFindAvatarByIdInSim(currentSim, targetId, preferredLocalId, out var foundAvatar))
            {
                var knownPosition = foundAvatar?.Position;
                return new AgentMonitorStatus(
                    Online: true,
                    RegionName: RegionNameFromHandle(client, foundAvatar?.RegionHandle ?? 0),
                    RegionHandle: foundAvatar?.RegionHandle,
                    Position: knownPosition,
                    IsFlying: foundAvatar == null ? null : ReadFlyingSignal(foundAvatar),
                    Velocity: foundAvatar?.Velocity,
                    HeadingDegrees: null,
                    Source: "simCache.currentRegion",
                    LocalId: foundAvatar?.LocalID,
                    AvatarUpdateSeen: latestUpdate != null,
                    AvatarUpdateSim: latestUpdate?.SimulatorName,
                    CacheVsAvatarUpdate: DescribeCacheVsAvatarUpdate(currentSim, knownPosition, latestUpdate));
            }

            if (TryFindAvatarByIdAcrossSims(connectedClient, targetId, preferredLocalId, out _, out var offRegionAvatar))
            {
                return new AgentMonitorStatus(
                    Online: true,
                    RegionName: RegionNameFromHandle(client, offRegionAvatar?.RegionHandle ?? 0),
                    RegionHandle: offRegionAvatar?.RegionHandle,
                    Position: null,
                    IsFlying: null,
                    Velocity: null,
                    HeadingDegrees: null,
                    Source: "simCache.offRegion",
                    LocalId: offRegionAvatar?.LocalID,
                    AvatarUpdateSeen: latestUpdate != null,
                    AvatarUpdateSim: latestUpdate?.SimulatorName,
                    CacheVsAvatarUpdate: "cache:offRegion");
            }

            if (latestUpdate != null)
            {
                return new AgentMonitorStatus(
                    Online: true,
                    RegionName: resolvedOffRegion.RegionName,
                    RegionHandle: resolvedOffRegion.RegionHandle,
                    Position: resolvedOffRegion.Position,
                    IsFlying: null,
                    Velocity: null,
                    HeadingDegrees: null,
                    Source: "avatarUpdate.offRegion",
                    LocalId: null,
                    AvatarUpdateSeen: true,
                    AvatarUpdateSim: latestUpdate.SimulatorName,
                    CacheVsAvatarUpdate: "cache:lost|avatarUpdate:offRegion");
            }
        }
        else
        {
            avatarUpdates.Attach(null);
        }

        if (_allowExternalFallback
            && client != null
            && DateTime.UtcNow - _lastExternalProbeAtUtc >= ExternalFallbackProbeInterval)
        {
            _lastExternalProbeAtUtc = DateTime.UtcNow;

            var spawnerResult = await TryLocateAgentViaSpawnerAsync(client, targetId, cancellationToken).ConfigureAwait(false);
            if (spawnerResult != null)
            {
                if (!spawnerResult.Found)
                {
                    return status with
                    {
                        Online = false,
                        Source = "spawner"
                    };
                }

                return status with
                {
                    Online = true,
                    Source = "spawner.offRegion"
                };
            }
        }
        
        var mapReply = await TryMapFriendLocationOnceAsync(client, targetId, FriendMapTimeout, cancellationToken).ConfigureAwait(false);
        if (mapReply != null)
        {
            return status with
            {
                Online = true,
                Source = "friendMap.offRegion"
            };
        }

        if (client == null)
        {
            return status with { Source = "disconnected" };
        }

        var hadAvatarUpdate = avatarUpdates.TryGetLatest(out var lastUpdate);
        return status with
        {
            Source = "simCache.lost",
            LocalId = null,
            AvatarUpdateSeen = hadAvatarUpdate,
            AvatarUpdateSim = hadAvatarUpdate ? lastUpdate?.SimulatorName : null,
            CacheVsAvatarUpdate = hadAvatarUpdate ? "cache:lost|avatarUpdate:stale" : "cache:lost"
        };
    }

    private static bool TryBuildStatusFromAvatarUpdate(
        Simulator? currentSim,
        AvatarUpdateSample? latestUpdate,
        out AgentMonitorStatus status)
    {
        status = AgentMonitorStatus.Unknown;
        if (currentSim == null || latestUpdate == null)
        {
            return false;
        }

        if (latestUpdate.SimulatorHandle != currentSim.Handle)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - latestUpdate.SeenAtUtc > AvatarUpdateFreshnessWindow)
        {
            return false;
        }

        status = new AgentMonitorStatus(
            Online: true,
            RegionName: latestUpdate.SimulatorHandle == 0 ? string.IsNullOrWhiteSpace(currentSim.Name) ? null : currentSim.Name : latestUpdate.SimulatorName,
            RegionHandle: latestUpdate.SimulatorHandle == 0 ? currentSim.Handle : latestUpdate.SimulatorHandle,
            Position: latestUpdate.Position,
            IsFlying: latestUpdate.IsFlying,
            Velocity: latestUpdate.Velocity,
            HeadingDegrees: null,
            Source: "avatarUpdate.currentRegion",
            LocalId: latestUpdate.LocalId == 0 ? null : latestUpdate.LocalId,
            AvatarUpdateSeen: true,
            AvatarUpdateSim: latestUpdate.SimulatorName,
            CacheVsAvatarUpdate: "cache:miss|avatarUpdate:currentRegion");
        return true;
    }

    private static bool ShouldTrustAvatarUpdateCurrentRegion(
        Simulator? currentSim,
        UUID targetId,
        uint? preferredLocalId,
        AgentMonitorStatus? previous,
        AvatarUpdateSample? latestUpdate,
        AgentMonitorStatus candidate)
    {
        if (currentSim == null)
        {
            return false;
        }

        var wasOffRegion = previous?.Source?.Contains("offRegion", StringComparison.OrdinalIgnoreCase) == true;
        if (!wasOffRegion)
        {
            return true;
        }

        // During region handoff, stale current-sim AvatarUpdate samples can briefly
        // look valid. Require corroboration from the sim cache before declaring the
        // target back in current region.
        if (TryFindAvatarByIdInSim(currentSim, targetId, preferredLocalId, out _))
        {
            return true;
        }

        if (latestUpdate != null)
        {
            var age = DateTimeOffset.UtcNow - latestUpdate.SeenAtUtc;
            if (age <= TimeSpan.FromMilliseconds(750)
                && latestUpdate.SimulatorHandle == currentSim.Handle
                && candidate.LocalId.HasValue
                && candidate.LocalId.Value != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFreshOffRegionAvatarUpdate(AvatarUpdateSample? latestUpdate, Simulator currentSim)
        => IsOffRegionAvatarUpdate(latestUpdate, currentSim, requireFresh: true);

    private static bool IsOffRegionAvatarUpdate(AvatarUpdateSample? latestUpdate, Simulator currentSim, bool requireFresh)
    {
        if (latestUpdate == null)
        {
            return false;
        }

        if (requireFresh && DateTimeOffset.UtcNow - latestUpdate.SeenAtUtc > AvatarUpdateFreshnessWindow)
        {
            return false;
        }

        // Prefer explicit simulator handle comparisons when available.
        if (latestUpdate.SimulatorHandle != 0)
        {
            return latestUpdate.SimulatorHandle != currentSim.Handle;
        }

        // Fallback to simulator name when handle is not populated in the event.
        if (string.IsNullOrWhiteSpace(latestUpdate.SimulatorName) || string.IsNullOrWhiteSpace(currentSim.Name))
        {
            return false;
        }

        return !latestUpdate.SimulatorName.Equals(currentSim.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string? DescribeCacheVsAvatarUpdate(
        Simulator currentSim,
        Vector3? cachePosition,
        AvatarUpdateSample? latestUpdate)
    {
        if (latestUpdate == null)
        {
            return "cache:hit|avatarUpdate:none";
        }

        if (latestUpdate.SimulatorHandle != currentSim.Handle)
        {
            return "cache:hit|avatarUpdate:otherRegion";
        }

        if (cachePosition == null)
        {
            return "cache:hit|avatarUpdate:currentRegion";
        }

        var delta = (cachePosition.Value - latestUpdate.Position).Length();
        return $"cache:hit|avatarUpdate:currentRegion|deltaMeters={delta:0.###}";
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
        uint? preferredLocalId,
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
        if (matches.Count == 0)
        {
            return false;
        }

        var currentRegionMatches = matches
            .Where(avatar => avatar.RegionHandle == simulator.Handle)
            .ToList();
        if (currentRegionMatches.Count > 0)
        {
            foundAvatar = SelectDeterministicAvatar(currentRegionMatches, preferredLocalId);
            return foundAvatar != null;
        }

        var unknownRegionMatches = matches
            .Where(avatar => avatar.RegionHandle == 0)
            .ToList();
        var hasConflictingKnownRegion = matches.Any(avatar => avatar.RegionHandle != 0 && avatar.RegionHandle != simulator.Handle);
        if (unknownRegionMatches.Count > 0 && !hasConflictingKnownRegion)
        {
            foundAvatar = SelectDeterministicAvatar(unknownRegionMatches, preferredLocalId);
            return foundAvatar != null;
        }

        return foundAvatar != null;
    }

    private static Avatar? SelectDeterministicAvatar(IReadOnlyList<Avatar> matches, uint? preferredLocalId)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (preferredLocalId.HasValue)
        {
            var preferred = matches.FirstOrDefault(avatar => avatar.LocalID == preferredLocalId.Value);
            if (preferred != null)
            {
                return preferred;
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

    private static (ulong? RegionHandle, Vector3? Position, string? RegionName) ResolveOffRegionLocation(
        GridClient client,
        Simulator? currentSim,
        UUID targetId,
        uint? preferredLocalId,
        AvatarUpdateSample? latestUpdate)
    {
        if (latestUpdate != null)
        {
            if (latestUpdate.SimulatorHandle != 0
                && (currentSim == null || latestUpdate.SimulatorHandle != currentSim.Handle))
            {
                var byHandle = TryFindSimulatorByHandle(client, latestUpdate.SimulatorHandle);
                return (
                    latestUpdate.SimulatorHandle,
                    latestUpdate.Position,
                    ResolveRegionName(latestUpdate.SimulatorName, byHandle, latestUpdate.SimulatorHandle));
            }

            if (!string.IsNullOrWhiteSpace(latestUpdate.SimulatorName))
            {
                var byName = client.Network.Simulators.FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Name)
                    && candidate.Name.Equals(latestUpdate.SimulatorName, StringComparison.OrdinalIgnoreCase)
                    && (currentSim == null || candidate.Handle != currentSim.Handle));
                if (byName != null)
                {
                    return (byName.Handle, latestUpdate.Position, byName.Name);
                }
            }
        }

        if (TryFindAvatarByIdAcrossSims(client, targetId, preferredLocalId, out var seenSim, out var seenAvatar)
            && seenSim != null
            && (currentSim == null || seenSim.Handle != currentSim.Handle))
        {
            var avatarHandle = seenAvatar?.RegionHandle ?? 0;
            var handle = avatarHandle != 0 ? avatarHandle : seenSim.Handle;
            return (
                handle == 0 ? null : handle,
                seenAvatar?.Position ?? latestUpdate?.Position,
                ResolveRegionName(latestUpdate?.SimulatorName, seenSim, handle));
        }

        return (null, latestUpdate?.Position, latestUpdate?.SimulatorName);
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
            ["source"] = status.Source,
            ["localId"] = status.LocalId?.ToString(CultureInfo.InvariantCulture),
            ["avatarUpdateSeen"] = status.AvatarUpdateSeen.HasValue ? (status.AvatarUpdateSeen.Value ? "true" : "false") : null,
            ["avatarUpdateSim"] = status.AvatarUpdateSim,
            ["cacheVsAvatarUpdate"] = status.CacheVsAvatarUpdate
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
            && string.Equals(previous.Source, current.Source, StringComparison.OrdinalIgnoreCase)
            && previous.LocalId == current.LocalId
            && previous.AvatarUpdateSeen == current.AvatarUpdateSeen
            && string.Equals(previous.AvatarUpdateSim, current.AvatarUpdateSim, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.CacheVsAvatarUpdate, current.CacheVsAvatarUpdate, StringComparison.OrdinalIgnoreCase);
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

    private static bool TryFindAvatarByIdAcrossSims(
        GridClient client,
        UUID avatarId,
        uint? preferredLocalId,
        out Simulator? foundSim,
        out Avatar? foundAvatar)
    {
        foundSim = null;
        foundAvatar = null;
        if (avatarId == UUID.Zero)
        {
            return false;
        }

        var orderedSims = new List<Simulator>();
        if (client.Network.CurrentSim != null)
        {
            orderedSims.Add(client.Network.CurrentSim);
        }

        foreach (var sim in client.Network.Simulators)
        {
            if (client.Network.CurrentSim != null && sim.Handle == client.Network.CurrentSim.Handle)
            {
                continue;
            }

            orderedSims.Add(sim);
        }

        foreach (var candidate in orderedSims)
        {
            if (!TryFindAvatarByIdInSim(candidate, avatarId, preferredLocalId, out var match))
            {
                continue;
            }

            if (match != null)
            {
                foundSim = candidate;
                foundAvatar = match;
                return true;
            }
        }

        return false;
    }

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
        string? Source,
        uint? LocalId,
        bool? AvatarUpdateSeen,
        string? AvatarUpdateSim,
        string? CacheVsAvatarUpdate)
    {
        public static AgentMonitorStatus Unknown { get; } = new(null, null, null, null, null, null, null, null, null, null, null, null);
    }

    private sealed class AvatarUpdateTracker : IDisposable
    {
        private readonly UUID _targetId;
        private readonly object _lock = new();
        private GridClient? _client;
        private AvatarUpdateSample? _latest;

        public AvatarUpdateTracker(UUID targetId)
        {
            _targetId = targetId;
        }

        public void Attach(GridClient? client)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_client, client))
                {
                    return;
                }

                if (_client != null)
                {
                    _client.Objects.AvatarUpdate -= OnAvatarUpdate;
                    _client.Objects.TerseObjectUpdate -= OnTerseObjectUpdate;
                }

                _client = client;
                if (_client != null)
                {
                    _client.Objects.AvatarUpdate += OnAvatarUpdate;
                    _client.Objects.TerseObjectUpdate += OnTerseObjectUpdate;
                }
            }
        }

        public bool TryGetLatest(out AvatarUpdateSample? sample)
        {
            lock (_lock)
            {
                sample = _latest;
                return sample != null;
            }
        }

        public void Dispose()
        {
            Attach(null);
        }

        private void OnAvatarUpdate(object? _, AvatarUpdateEventArgs e)
        {
            var avatar = e.Avatar;
            if (avatar == null || avatar.ID != _targetId)
            {
                return;
            }

            var simulator = e.Simulator;
            var sample = new AvatarUpdateSample(
                DateTimeOffset.UtcNow,
                simulator?.Handle ?? 0,
                simulator?.Name,
                avatar.Position,
                avatar.Velocity,
                ReadFlyingSignal(avatar),
                avatar.LocalID,
                e.IsNew);

            lock (_lock)
            {
                _latest = sample;
            }
        }

        private void OnTerseObjectUpdate(object? _, TerseObjectUpdateEventArgs e)
        {
            var prim = e.Prim;
            var avatar = prim as Avatar;

            // Terse updates are frequent and may arrive without a resolved avatar reference.
            // Resolve by local ID when possible so the target sample stays fresh while moving.
            if (avatar == null || avatar.ID != _targetId)
            {
                if (e.Simulator?.ObjectsAvatars.TryGetValue(e.Update.LocalID, out var resolved) == true
                    && resolved != null
                    && resolved.ID == _targetId)
                {
                    avatar = resolved;
                }
                else
                {
                    return;
                }
            }

            var simulator = e.Simulator;
            var sample = new AvatarUpdateSample(
                DateTimeOffset.UtcNow,
                simulator?.Handle ?? 0,
                simulator?.Name,
                e.Update.Position,
                e.Update.Velocity,
                ReadFlyingSignal(avatar),
                e.Update.LocalID,
                IsNew: false);

            lock (_lock)
            {
                _latest = sample;
            }
        }
    }

    private sealed record AvatarUpdateSample(
        DateTimeOffset SeenAtUtc,
        ulong SimulatorHandle,
        string? SimulatorName,
        Vector3 Position,
        Vector3 Velocity,
        bool? IsFlying,
        uint LocalId,
        bool IsNew);
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
    string? Source,
    uint? LocalId,
    bool? AvatarUpdateSeen,
    string? AvatarUpdateSim,
    string? CacheVsAvatarUpdate);