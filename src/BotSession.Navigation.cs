using System.Globalization;
using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<BotToolResult> SitAsync(CancellationToken cancellationToken)
    {
        return await RunActionAsync("Sitting down...", c => c.Self.SitOnGround(), cancellationToken);
    }

    public async Task<BotToolResult> StandAsync(CancellationToken cancellationToken)
    {
        return await RunActionAsync("Standing up.", c => c.Self.Stand(), cancellationToken);
    }

    public async Task<BotToolResult> FlyAsync(bool enabled, CancellationToken cancellationToken)
    {
        return await RunActionAsync(enabled ? "Taking off." : "Walking now.", c => c.Self.Fly(enabled), cancellationToken);
    }

    public async Task<BotToolResult> JumpAsync(CancellationToken cancellationToken)
    {
        var result = await RunActionAsync("Jumping.", c => c.Self.Jump(true), cancellationToken);
        if (!result.Ok)
        {
            return result;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                var cl = _client;
                if (cl != null)
                {
                    cl.Self.Jump(false);
                }
            }
            catch
            {
                // Ignored: this is a best-effort reset.
            }
        });

        return result;
    }

    public async Task<BotToolResult> MoveByAsync(string direction, float meters, bool fly, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return BotToolResult.Fail("direction is required.");
        }

        if (meters <= 0f)
        {
            return BotToolResult.Fail("meters must be greater than 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var delta = ResolveDelta(direction, meters, client);
            var from = client.Self.SimPosition;
            var target = ClampLocalPosition(new Vector3(from.X + delta.X, from.Y + delta.Y, from.Z + delta.Z));
            return await MoveToLocalPositionCoreAsync(client, target, fly, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> MoveToAsync(float x, float y, float z, bool fly, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync(
            (client, token) => MoveToLocalPositionCoreAsync(client, target, fly, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportToAsync(float x, float y, float z, string? regionName, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var currentSim = client.Network.CurrentSim;
            if (currentSim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            bool ok;
            string destinationLabel;
            if (string.IsNullOrWhiteSpace(regionName) || string.Equals(regionName, currentSim.Name, StringComparison.OrdinalIgnoreCase))
            {
                destinationLabel = currentSim.Name;
                ok = await client.Self.TeleportAsync(currentSim.Name, target, token).ConfigureAwait(false);
            }
            else
            {
                var region = await client.Grid.GetGridRegionAsync(regionName, GridLayerType.Objects, token).ConfigureAwait(false);
                if (!region.HasValue)
                {
                    return BotToolResult.Fail($"Unable to resolve region '{regionName}' to a region handle.");
                }

                destinationLabel = $"{region.Value.Name} ({region.Value.RegionHandle})";
                ok = await client.Self.TeleportAsync(region.Value.RegionHandle, target, token).ConfigureAwait(false);
            }

            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(client.Self.TeleportMessage)
                    ? "Teleport failed."
                    : client.Self.TeleportMessage;
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    message,
                    new Dictionary<string, string?>
                    {
                        ["targetRegion"] = destinationLabel,
                        ["targetPosition"] = FormatVector(target)
                    });
                return BotToolResult.Fail(message);
            }

            var at = client.Self.SimPosition;
            EmitRuntimeEvent(
                "teleport",
                "teleport.succeeded",
                "opensim",
                $"Teleported to {destinationLabel} at {FormatVector(at)}.",
                new Dictionary<string, string?>
                {
                    ["targetRegion"] = destinationLabel,
                    ["position"] = FormatVector(at)
                });
            return BotToolResult.OkResult($"Teleported to {destinationLabel} at {FormatVector(at)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TeleportToRegionHandleAsync(string regionHandle, float x, float y, float z, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(regionHandle))
        {
            return BotToolResult.Fail("regionHandle is required.");
        }

        if (!ulong.TryParse(regionHandle, out var handle))
        {
            return BotToolResult.Fail("regionHandle must be an unsigned 64-bit integer.");
        }

        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Self.TeleportAsync(handle, target, token).ConfigureAwait(false);
            if (!ok)
            {
                var message = string.IsNullOrWhiteSpace(client.Self.TeleportMessage)
                    ? "Teleport failed."
                    : client.Self.TeleportMessage;
                EmitRuntimeEvent(
                    "teleport",
                    "teleport.failed",
                    "opensim",
                    message,
                    new Dictionary<string, string?>
                    {
                        ["targetRegionHandle"] = handle.ToString(),
                        ["targetPosition"] = FormatVector(target)
                    });
                return BotToolResult.Fail(message);
            }

            EmitRuntimeEvent(
                "teleport",
                "teleport.succeeded",
                "opensim",
                $"Teleported to region handle {handle} at {FormatVector(client.Self.SimPosition)}.",
                new Dictionary<string, string?>
                {
                    ["targetRegionHandle"] = handle.ToString(),
                    ["position"] = FormatVector(client.Self.SimPosition)
                });
            return BotToolResult.OkResult($"Teleported to region handle {handle} at {FormatVector(client.Self.SimPosition)}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> StopMovementAsync(CancellationToken cancellationToken)
    {
        StopFollowInternal();
        CancelMovementAutoStop();
        return await ExecuteLockedAsync((client, _) =>
        {
            client.Self.AutoPilotCancel();
            client.Self.Movement.ResetControlFlags();
            client.Self.Movement.SendUpdate(true);
            return Task.FromResult(BotToolResult.OkResult("Movement stopped (autopilot canceled, control flags reset, follow stopped)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> StartMovementAsync(string axis, bool fast, float? durationSeconds, CancellationToken cancellationToken)
    {
        if (!TryResolveMovementAxis(axis, fast, out var flags, out var axisError))
        {
            return BotToolResult.Fail(axisError);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var movement = client.Self.Movement;
            movement.AtPos = (flags & AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0;
            movement.AtNeg = (flags & AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0;
            movement.LeftPos = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) != 0;
            movement.LeftNeg = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) != 0;
            movement.UpPos = (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0;
            movement.UpNeg = (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0;
            movement.FastAt = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT) != 0;
            movement.FastLeft = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FAST_LEFT) != 0;
            movement.FastUp = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FAST_UP) != 0;
            movement.SendUpdate(true);

            var durationNote = "until StopMovement";
            if (durationSeconds.HasValue && durationSeconds.Value > 0f)
            {
                var clamped = Math.Clamp(durationSeconds.Value, 0.25f, 300f);
                ScheduleMovementAutoStop(TimeSpan.FromSeconds(clamped));
                durationNote = $"for up to {clamped:F1}s (auto-stop)";
            }

            return Task.FromResult(BotToolResult.OkResult(
                $"Continuous movement started on axis '{axis}'{(fast ? " (fast)" : string.Empty)} {durationNote}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> LookAtAsync(float x, float y, float z, CancellationToken cancellationToken)
    {
        var target = ClampLocalPosition(new Vector3(x, y, z));
        return await ExecuteLockedAsync((client, _) =>
        {
            var ok = client.Self.Movement.TurnToward(target, true);
            return Task.FromResult(ok
                ? BotToolResult.OkResult($"Turned body and camera toward {FormatVector(target)}.")
                : BotToolResult.Fail("TurnToward failed (agent updates disabled or parent prim missing)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetCameraHeadingAsync(float headingDegrees, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var headingRadians = headingDegrees * Utils.DEG_TO_RAD;
            client.Self.Movement.UpdateFromHeading(headingRadians, true);
            return Task.FromResult(BotToolResult.OkResult($"Camera heading set to {headingDegrees:F1} degrees."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<CameraStateResult> GetCameraStateAsync(CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var cam = client.Self.Movement.Camera;
        var pos = client.Self.SimPosition;
        var state = new CameraState(
            cam.Position.X, cam.Position.Y, cam.Position.Z,
            cam.AtAxis.X, cam.AtAxis.Y, cam.AtAxis.Z,
            cam.LeftAxis.X, cam.LeftAxis.Y, cam.LeftAxis.Z,
            cam.UpAxis.X, cam.UpAxis.Y, cam.UpAxis.Z,
            cam.Far,
            pos.X, pos.Y, pos.Z);
        return Task.FromResult(new CameraStateResult(true, "OK", state));
    }

    public async Task<BotToolResult> FollowAsync(string targetType, string target, float distanceBuffer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return BotToolResult.Fail("target is required.");
        }

        var buffer = distanceBuffer <= 0f ? 3.0f : Math.Clamp(distanceBuffer, 0.5f, 50f);
        var isObject = string.Equals(targetType, "object", StringComparison.OrdinalIgnoreCase);
        var isAvatar = string.Equals(targetType, "avatar", StringComparison.OrdinalIgnoreCase);
        if (!isObject && !isAvatar)
        {
            return BotToolResult.Fail("targetType must be 'avatar' or 'object'.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            uint localId;
            string label;
            var trackedId = UUID.Zero;
            if (isAvatar)
            {
                if (!TryResolveAvatarAcrossSims(client, target, out sim, out localId, out label, out trackedId))
                {
                    return Task.FromResult(BotToolResult.Fail(
                        $"Avatar '{target}' not found in visible simulators. Use full name or UUID."));
                }
            }
            else
            {
                if (!TryResolveObject(sim, target, out localId, out label))
                {
                    return Task.FromResult(BotToolResult.Fail(
                        $"Object '{target}' not found in current simulator. Use name, local ID, or UUID."));
                }
            }

            StartFollowLoop(client, sim, isObject, trackedId, localId, label, buffer);
            return Task.FromResult(BotToolResult.OkResult(
                $"Following {targetType} {label} (buffer {buffer:F1}m). Use StopFollow or StopMovement to end."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<BotToolResult> StopFollowAsync(CancellationToken cancellationToken)
    {
        var hadFollow = StopFollowInternal();
        return Task.FromResult(hadFollow
            ? BotToolResult.OkResult("Follow stopped.")
            : BotToolResult.OkResult("No active follow to stop."));
    }

    private void StartFollowLoop(GridClient client, Simulator sim, bool isObject, UUID trackedId, uint localId, string label, float buffer)
    {
        StopFollowInternal();

        var cts = new CancellationTokenSource();
        lock (_movementLock)
        {
            _followCts = cts;
            _followTargetDescription = $"{(isObject ? "object" : "avatar")} {label}";
            _followTrackedAvatarId = isObject ? UUID.Zero : trackedId;
            _followTrackedLocalId = localId;
            _followAnchorSimHandle = sim.Handle;
            _followTask = Task.Run(() => FollowLoopAsync(client, sim, isObject, trackedId, localId, label, buffer, cts.Token));
        }

        if (IsFollowDiagnosticsEnabled())
        {
            Console.WriteLine(
                $"[follow][diag] start target={label} targetUuid={(trackedId == UUID.Zero ? "(n/a)" : trackedId.ToString())} anchorSim={DescribeSimulator(sim)} currentSim={DescribeSimulator(client.Network.CurrentSim)} localId={localId}");
        }
    }

    private async Task FollowLoopAsync(
        GridClient client,
        Simulator sim,
        bool isObject,
        UUID trackedId,
        uint localId,
        string label,
        float buffer,
        CancellationToken cancellationToken)
    {
        var targetSim = sim;
        var targetLocalId = localId;
        var lastPilotAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var lastDiagAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var lastMapProbeAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var lastSpawnerProbeAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var lastSpawnerFoundAt = DateTime.MinValue;
        var lastMappedTargetAt = DateTime.MinValue;
        ulong lastMappedRegionHandle = 0;
        var lastMappedLocal = Vector3.Zero;
        var lastSpawnerRegionId = UUID.Zero;
        string? lastSpawnerRegionName = null;
        var lastAvatarSeenAt = DateTime.UtcNow;
        var lastCacheMissDiagAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                var botSim = client.Network.CurrentSim;
                if (botSim == null)
                {
                    Console.WriteLine($"[follow] no active simulator; stopping follow of {label}.");
                    if (IsFollowDiagnosticsEnabled())
                    {
                        Console.WriteLine(
                            $"[follow][diag] stop reason=no_current_sim target={label} lastTargetSim={DescribeSimulator(targetSim)}");
                    }
                    break;
                }

                Vector3 targetPos;
                var targetIsCrossRegion = false;
                ulong crossRegionTargetHandle = 0;
                Vector3 crossRegionTargetLocal = Vector3.Zero;
                float distance;
                if (isObject)
                {
                    if (!ReferenceEquals(botSim, targetSim))
                    {
                        Console.WriteLine($"[follow] object {label} changed region; stopping.");
                        break;
                    }

                    if (!targetSim.ObjectsPrimitives.TryGetValue(targetLocalId, out var prim))
                    {
                        Console.WriteLine($"[follow] object {label} no longer in cache; stopping.");
                        break;
                    }

                    targetPos = prim.Position;
                    distance = Vector3.Distance(targetPos, client.Self.SimPosition);
                }
                else
                {
                    Avatar? avatar = null;
                    if (trackedId != UUID.Zero
                        && (DateTime.UtcNow - lastSpawnerProbeAt) >= TimeSpan.FromSeconds(2))
                    {
                        lastSpawnerProbeAt = DateTime.UtcNow;
                        var locatedBySpawner = await TryLocateAgentViaSpawnerAsync(client, trackedId, cancellationToken).ConfigureAwait(false);
                        if (locatedBySpawner != null && locatedBySpawner.Found)
                        {
                            lastSpawnerFoundAt = DateTime.UtcNow;
                            lastSpawnerRegionId = locatedBySpawner.RegionId;
                            lastSpawnerRegionName = locatedBySpawner.RegionName;

                            if (locatedBySpawner.RegionHandle != 0)
                            {
                                lastMappedTargetAt = DateTime.UtcNow;
                                lastMappedRegionHandle = locatedBySpawner.RegionHandle;
                                lastMappedLocal = locatedBySpawner.Position;
                            }

                            if (locatedBySpawner.Simulator != null && !ReferenceEquals(targetSim, locatedBySpawner.Simulator))
                            {
                                targetSim = locatedBySpawner.Simulator;
                                lock (_movementLock)
                                {
                                    _followAnchorSimHandle = targetSim.Handle;
                                }
                            }

                            if (locatedBySpawner.Simulator != null)
                            {
                                avatar = locatedBySpawner.Simulator.ObjectsAvatars.Values
                                    .FirstOrDefault(candidate => candidate != null && candidate.ID == trackedId);
                                if (avatar != null)
                                {
                                    targetLocalId = avatar.LocalID;
                                    lock (_movementLock)
                                    {
                                        _followTrackedLocalId = targetLocalId;
                                    }
                                }
                            }

                            if (IsFollowDiagnosticsEnabled())
                            {
                                Console.WriteLine(
                                    $"[follow][diag] spawner_locate target={label} targetUuid={trackedId} found=true regionName={locatedBySpawner.RegionName ?? "(unknown)"} regionUuid={(locatedBySpawner.RegionId == UUID.Zero ? "(unknown)" : locatedBySpawner.RegionId.ToString())} mappedSim={DescribeSimulator(locatedBySpawner.Simulator)} mappedHandle={locatedBySpawner.RegionHandle} mappedLocal={FormatPosition(locatedBySpawner.Position)}");
                            }
                        }
                    }

                    if (trackedId != UUID.Zero
                        && TryFindAvatarByIdAcrossSims(client, trackedId, out var seenSim, out var seenAvatar)
                        && seenAvatar != null)
                    {
                        avatar = seenAvatar;
                        if (seenSim != null
                            && (!ReferenceEquals(targetSim, seenSim) || targetLocalId != seenAvatar.LocalID))
                        {
                            if (IsFollowDiagnosticsEnabled())
                            {
                                Console.WriteLine(
                                    $"[follow][diag] rebind target={label} targetUuid={trackedId} fromSim={DescribeSimulator(targetSim)} fromLocalId={targetLocalId} toSim={DescribeSimulator(seenSim)} toLocalId={seenAvatar.LocalID} toPos={FormatPosition(seenAvatar.Position)}");
                            }

                            targetSim = seenSim;
                            targetLocalId = seenAvatar.LocalID;
                            lock (_movementLock)
                            {
                                _followTrackedLocalId = targetLocalId;
                                _followAnchorSimHandle = targetSim.Handle;
                            }
                        }
                    }

                    if (avatar == null && targetSim.ObjectsAvatars.TryGetValue(targetLocalId, out var byLocalId))
                    {
                        avatar = byLocalId;
                    }

                    if (trackedId != UUID.Zero && (DateTime.UtcNow - lastMapProbeAt) >= TimeSpan.FromSeconds(2))
                    {
                        var shouldProbeMap = lastMappedTargetAt == DateTime.MinValue
                            || (DateTime.UtcNow - lastMappedTargetAt) >= TimeSpan.FromSeconds(3);
                        if (shouldProbeMap)
                        {
                            lastMapProbeAt = DateTime.UtcNow;
                            var mapProbeTimeout = TimeSpan.FromMilliseconds(2500);
                            var mapped = await TryMapFriendLocationOnceAsync(client, trackedId, mapProbeTimeout, cancellationToken).ConfigureAwait(false);
                            if (mapped != null)
                            {
                                var mappedSim = client.Network.Simulators.FirstOrDefault(s => s.Handle == mapped.RegionHandle);
                                if (mappedSim == null
                                    && TryResolveConnectedSimulator(client, lastSpawnerRegionId, lastSpawnerRegionName, out var resolvedBySpawnerIdentity))
                                {
                                    mappedSim = resolvedBySpawnerIdentity;
                                }

                                lastMappedTargetAt = DateTime.UtcNow;
                                lastMappedRegionHandle = mapped.RegionHandle;
                                lastMappedLocal = mapped.Location;

                                if (mappedSim != null && !ReferenceEquals(targetSim, mappedSim))
                                {
                                    targetSim = mappedSim;
                                    lock (_movementLock)
                                    {
                                        _followAnchorSimHandle = targetSim.Handle;
                                    }
                                }

                                if (IsFollowDiagnosticsEnabled())
                                {
                                    Console.WriteLine(
                                        $"[follow][diag] map_locate target={label} targetUuid={trackedId} mappedSim={DescribeSimulator(mappedSim)} mappedHandle={mapped.RegionHandle} mappedLocal={FormatPosition(mapped.Location)}");
                                }
                            }
                            else if (IsFollowDiagnosticsEnabled())
                            {
                                Console.WriteLine(
                                    $"[follow][diag] map_locate_no_reply target={label} targetUuid={trackedId} timeoutMs={(int)mapProbeTimeout.TotalMilliseconds} {DescribeFriendMapRights(client, trackedId)}");
                            }
                        }
                    }

                    var hasFreshMapped = lastMappedTargetAt != DateTime.MinValue
                        && (DateTime.UtcNow - lastMappedTargetAt) <= TimeSpan.FromSeconds(5);
                    var hasFreshSpawner = lastSpawnerFoundAt != DateTime.MinValue
                        && (DateTime.UtcNow - lastSpawnerFoundAt) <= TimeSpan.FromSeconds(12);

                    if (avatar == null)
                    {
                        if (hasFreshMapped)
                        {
                            crossRegionTargetHandle = lastMappedRegionHandle;
                            crossRegionTargetLocal = lastMappedLocal;
                            targetPos = ResolveFollowWaypointFromRegionHandle(
                                botSim,
                                client.Self.SimPosition,
                                lastMappedRegionHandle,
                                lastMappedLocal,
                                out distance,
                                out targetIsCrossRegion);
                        }
                        else
                        {
                            if (hasFreshSpawner)
                            {
                                if (IsFollowDiagnosticsEnabled() && (DateTime.UtcNow - lastCacheMissDiagAt) >= TimeSpan.FromSeconds(3))
                                {
                                    Console.WriteLine(
                                        $"[follow][diag] cache_miss_waiting_spawner target={label} targetUuid={(trackedId == UUID.Zero ? "(unknown)" : trackedId.ToString())} spawnerRegion={lastSpawnerRegionName ?? "(unknown)"} spawnerRegionUuid={(lastSpawnerRegionId == UUID.Zero ? "(unknown)" : lastSpawnerRegionId.ToString())} knownSims={client.Network.Simulators.Count}");
                                    lastCacheMissDiagAt = DateTime.UtcNow;
                                }

                                client.Self.AutoPilotCancel();
                                continue;
                            }

                            var missingDuration = DateTime.UtcNow - lastAvatarSeenAt;
                            if (missingDuration <= TimeSpan.FromSeconds(12))
                            {
                                if (IsFollowDiagnosticsEnabled() && (DateTime.UtcNow - lastCacheMissDiagAt) >= TimeSpan.FromSeconds(3))
                                {
                                    Console.WriteLine(
                                        $"[follow][diag] cache_miss_waiting target={label} targetUuid={(trackedId == UUID.Zero ? "(unknown)" : trackedId.ToString())} missingForMs={(int)missingDuration.TotalMilliseconds} anchorSim={DescribeSimulator(targetSim)} currentSim={DescribeSimulator(botSim)} knownSims={client.Network.Simulators.Count} {DescribeFriendMapRights(client, trackedId)}");
                                    lastCacheMissDiagAt = DateTime.UtcNow;
                                }

                                client.Self.AutoPilotCancel();
                                continue;
                            }

                            Console.WriteLine($"[follow] avatar {label} no longer in cache; stopping.");
                            if (IsFollowDiagnosticsEnabled())
                            {
                                Console.WriteLine(
                                    $"[follow][diag] cache_miss_stop target={label} targetUuid={(trackedId == UUID.Zero ? "(unknown)" : trackedId.ToString())} missingForMs={(int)missingDuration.TotalMilliseconds} anchorSim={DescribeSimulator(targetSim)} currentSim={DescribeSimulator(botSim)} knownSims={client.Network.Simulators.Count} {DescribeFriendMapRights(client, trackedId)}");
                            }
                            break;
                        }
                    }
                    else
                    {
                        lastAvatarSeenAt = DateTime.UtcNow;

                        if (hasFreshMapped)
                        {
                            crossRegionTargetHandle = lastMappedRegionHandle;
                            crossRegionTargetLocal = lastMappedLocal;
                            targetPos = ResolveFollowWaypointFromRegionHandle(
                                botSim,
                                client.Self.SimPosition,
                                lastMappedRegionHandle,
                                lastMappedLocal,
                                out distance,
                                out targetIsCrossRegion);
                        }
                        else
                        {
                            crossRegionTargetHandle = targetSim.Handle;
                            crossRegionTargetLocal = avatar.Position;
                            targetPos = ResolveFollowWaypoint(botSim, client.Self.SimPosition, targetSim, avatar.Position, out distance, out targetIsCrossRegion);
                        }
                    }
                }

                if (IsFollowDiagnosticsEnabled() && (DateTime.UtcNow - lastDiagAt) >= TimeSpan.FromSeconds(3))
                {
                    Console.WriteLine(
                        $"[follow][diag] tick target={label} targetSim={DescribeSimulator(targetSim)} botSim={DescribeSimulator(botSim)} botPos={FormatPosition(client.Self.SimPosition)} waypoint={FormatPosition(targetPos)} distance={distance:F1} buffer={buffer:F1} crossRegion={targetIsCrossRegion}");
                    lastDiagAt = DateTime.UtcNow;
                }

                if (distance > buffer)
                {
                    // Re-issue autopilot at most once per second to avoid packet spam.
                    if ((DateTime.UtcNow - lastPilotAt) >= TimeSpan.FromSeconds(1))
                    {
                        if (targetIsCrossRegion && crossRegionTargetHandle != 0)
                        {
                            AutoPilotToRegionLocal(client, crossRegionTargetHandle, crossRegionTargetLocal);
                        }
                        else
                        {
                            client.Self.AutoPilotLocal(
                                (int)MathF.Round(targetPos.X),
                                (int)MathF.Round(targetPos.Y),
                                targetPos.Z);
                        }
                        lastPilotAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    client.Self.AutoPilotCancel();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[follow] error while following {label}: {ex.Message}");
                break;
            }
        }

        try
        {
            client.Self.AutoPilotCancel();
        }
        catch
        {
            // Best-effort cleanup.
        }

        lock (_movementLock)
        {
            _followTargetDescription = null;
            _followTrackedAvatarId = UUID.Zero;
            _followTrackedLocalId = 0;
            _followAnchorSimHandle = 0;
        }
    }

    private bool IsFollowDiagnosticsEnabled()
        => true;

    private static Vector3 ResolveFollowWaypoint(
        Simulator botSim,
        Vector3 botPosition,
        Simulator targetSim,
        Vector3 targetLocalPosition,
        out float distance,
        out bool crossRegion)
    {
        crossRegion = !ReferenceEquals(botSim, targetSim);
        if (!crossRegion)
        {
            distance = Vector3.Distance(botPosition, targetLocalPosition);
            return targetLocalPosition;
        }

        var botGlobal = ToGlobalPosition(botSim.Handle, botPosition);
        var targetGlobal = ToGlobalPosition(targetSim.Handle, targetLocalPosition);
        var delta = targetGlobal - botGlobal;
        distance = delta.Length();

        // Drive toward the neighboring region edge in global direction.
        var projectedLocal = new Vector3(
            botPosition.X + delta.X,
            botPosition.Y + delta.Y,
            botPosition.Z + Math.Clamp(delta.Z, -3f, 3f));

        return ClampEdgeWaypoint(projectedLocal);
    }

    private static Vector3 ResolveFollowWaypointFromRegionHandle(
        Simulator botSim,
        Vector3 botPosition,
        ulong targetRegionHandle,
        Vector3 targetLocalPosition,
        out float distance,
        out bool crossRegion)
    {
        crossRegion = botSim.Handle != targetRegionHandle;
        if (!crossRegion)
        {
            distance = Vector3.Distance(botPosition, targetLocalPosition);
            return targetLocalPosition;
        }

        var botGlobal = ToGlobalPosition(botSim.Handle, botPosition);
        var targetGlobal = ToGlobalPosition(targetRegionHandle, targetLocalPosition);
        var delta = targetGlobal - botGlobal;
        distance = delta.Length();

        var projectedLocal = new Vector3(
            botPosition.X + delta.X,
            botPosition.Y + delta.Y,
            botPosition.Z + Math.Clamp(delta.Z, -3f, 3f));

        return ClampEdgeWaypoint(projectedLocal);
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

    private async Task<SpawnerLocatedAgent?> TryLocateAgentViaSpawnerAsync(
        GridClient client,
        UUID trackedId,
        CancellationToken cancellationToken)
    {
        DataToolResult result;
        try
        {
            result = await _followSpawnerClient
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
        if (parsed.Found && TryResolveConnectedSimulator(client, parsed.RegionId, parsed.RegionName, out simulator))
        {
            regionHandle = simulator!.Handle;
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

    private static Vector3 ClampEdgeWaypoint(Vector3 position)
    {
        return new Vector3(
            Math.Clamp(position.X, 0.25f, 255.75f),
            Math.Clamp(position.Y, 0.25f, 255.75f),
            Math.Clamp(position.Z, 0f, 4096f));
    }

    private static void AutoPilotToRegionLocal(GridClient client, ulong regionHandle, Vector3 localPosition)
    {
        Utils.LongToUInts(regionHandle, out var regionX, out var regionY);
        var localX = (uint)Math.Clamp((int)MathF.Round(localPosition.X), 0, 255);
        var localY = (uint)Math.Clamp((int)MathF.Round(localPosition.Y), 0, 255);
        client.Self.AutoPilot((ulong)regionX + localX, (ulong)regionY + localY, localPosition.Z);
    }

    private readonly record struct SpawnerAgentResponse(bool Found, UUID RegionId, string? RegionName, Vector3 Position);

    private sealed record SpawnerLocatedAgent(
        bool Found,
        UUID RegionId,
        string? RegionName,
        Vector3 Position,
        ulong RegionHandle,
        Simulator? Simulator);

    private static string DescribeFriendMapRights(GridClient client, UUID friendId)
    {
        if (friendId == UUID.Zero)
        {
            return "friendMapRights=unknown_target";
        }

        if (!client.Friends.FriendList.TryGetValue(friendId, out var friend))
        {
            return "friendMapRights=not_in_friend_list";
        }

        return $"friendMapRights=my:{friend.MyFriendRights}|their:{friend.TheirFriendRights}";
    }

    private static bool TryFindAvatarByIdAcrossSims(GridClient client, UUID avatarId, out Simulator? foundSim, out Avatar? foundAvatar)
    {
        foundSim = null;
        foundAvatar = null;
        if (avatarId == UUID.Zero)
        {
            return false;
        }

        foreach (var candidate in client.Network.Simulators)
        {
            var match = candidate.ObjectsAvatars.Values.FirstOrDefault(avatar => avatar != null && avatar.ID == avatarId);
            if (match != null)
            {
                foundSim = candidate;
                foundAvatar = match;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveAvatarAcrossSims(
        GridClient client,
        string target,
        out Simulator resolvedSim,
        out uint localId,
        out string label,
        out UUID avatarId)
    {
        localId = 0;
        label = string.Empty;
        avatarId = UUID.Zero;

        var current = client.Network.CurrentSim;
        if (current != null && TryResolveAvatar(current, target, out localId, out label, out avatarId))
        {
            resolvedSim = current;
            return true;
        }

        foreach (var candidate in client.Network.Simulators)
        {
            if (ReferenceEquals(candidate, current))
            {
                continue;
            }

            if (TryResolveAvatar(candidate, target, out localId, out label, out avatarId))
            {
                resolvedSim = candidate;
                return true;
            }
        }

        resolvedSim = current ?? client.Network.Simulators.FirstOrDefault() ?? throw new InvalidOperationException("No simulator available.");
        return false;
    }

    private bool StopFollowInternal()
    {
        CancellationTokenSource? cts;
        lock (_movementLock)
        {
            cts = _followCts;
            _followCts = null;
            _followTask = null;
            _followTargetDescription = null;
            _followTrackedAvatarId = UUID.Zero;
            _followTrackedLocalId = 0;
            _followAnchorSimHandle = 0;
        }

        if (cts == null)
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
            // Ignore cancellation races.
        }

        cts.Dispose();
        return true;
    }

    private void ScheduleMovementAutoStop(TimeSpan delay)
    {
        CancelMovementAutoStop();
        var cts = new CancellationTokenSource();
        lock (_movementLock)
        {
            _movementAutoStopCts = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                var client = _client;
                if (client != null && _connected)
                {
                    client.Self.Movement.ResetControlFlags();
                    client.Self.Movement.SendUpdate(true);
                    Console.WriteLine("[movement] auto-stop fired after configured duration.");
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when movement is stopped manually.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[movement] auto-stop error: {ex.Message}");
            }
        });
    }

    private void CancelMovementAutoStop()
    {
        CancellationTokenSource? cts;
        lock (_movementLock)
        {
            cts = _movementAutoStopCts;
            _movementAutoStopCts = null;
        }

        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Ignore cancellation races.
            }

            cts.Dispose();
        }
    }

    private static bool TryResolveMovementAxis(string axis, bool fast, out AgentManager.ControlFlags flags, out string error)
    {
        flags = AgentManager.ControlFlags.NONE;
        error = string.Empty;
        var normalized = (axis ?? string.Empty).Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "forward":
            case "forwards":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT;
                return true;
            case "back":
            case "backward":
            case "backwards":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT;
                return true;
            case "left":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_LEFT;
                return true;
            case "right":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_LEFT;
                return true;
            case "up":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_UP;
                return true;
            case "down":
                flags = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG;
                if (fast) flags |= AgentManager.ControlFlags.AGENT_CONTROL_FAST_UP;
                return true;
            default:
                error = "Unsupported axis. Use: forward, back, left, right, up, down.";
                return false;
        }
    }

    private static bool TryResolveAvatar(Simulator sim, string target, out uint localId, out string label, out UUID avatarId)
    {
        localId = 0;
        label = string.Empty;
        avatarId = UUID.Zero;

        if (UUID.TryParse(target, out var uuid))
        {
            var match = sim.ObjectsAvatars.FirstOrDefault(kvp => kvp.Value.ID == uuid);
            if (match.Value != null)
            {
                localId = match.Value.LocalID;
                label = $"{match.Value.Name} ({match.Value.ID})";
                avatarId = match.Value.ID;
                return true;
            }

            return false;
        }

        var byName = sim.ObjectsAvatars.FirstOrDefault(kvp =>
            kvp.Value != null
            && !string.IsNullOrWhiteSpace(kvp.Value.Name)
            && kvp.Value.Name.Equals(target.Trim(), StringComparison.OrdinalIgnoreCase));
        if (byName.Value != null)
        {
            localId = byName.Value.LocalID;
            label = $"{byName.Value.Name} ({byName.Value.ID})";
            avatarId = byName.Value.ID;
            return true;
        }

        return false;
    }

    private static bool TryResolveObject(Simulator sim, string target, out uint localId, out string label)
    {
        localId = 0;
        label = string.Empty;
        var trimmed = target.Trim();

        if (uint.TryParse(trimmed, out var parsedLocalId)
            && sim.ObjectsPrimitives.TryGetValue(parsedLocalId, out var byLocalId))
        {
            localId = byLocalId.LocalID;
            label = $"{byLocalId.Properties?.Name ?? "(unnamed)"} (localId {byLocalId.LocalID})";
            return true;
        }

        if (UUID.TryParse(trimmed, out var uuid))
        {
            var match = sim.ObjectsPrimitives.FirstOrDefault(kvp => kvp.Value.ID == uuid);
            if (match.Value != null)
            {
                localId = match.Value.LocalID;
                label = $"{match.Value.Properties?.Name ?? "(unnamed)"} ({match.Value.ID})";
                return true;
            }

            return false;
        }

        var byName = sim.ObjectsPrimitives.FirstOrDefault(kvp =>
            kvp.Value?.Properties?.Name != null
            && kvp.Value.Properties.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (byName.Value != null)
        {
            localId = byName.Value.LocalID;
            label = $"{byName.Value.Properties!.Name} (localId {byName.Value.LocalID})";
            return true;
        }

        return false;
    }

    private static Vector3 ResolveDelta(string direction, float meters, GridClient client)
    {
        var normalized = direction.Trim().ToLowerInvariant();
        return normalized switch
        {
            "north" => new Vector3(0f, meters, 0f),
            "south" => new Vector3(0f, -meters, 0f),
            "east" => new Vector3(meters, 0f, 0f),
            "west" => new Vector3(-meters, 0f, 0f),
            "up" => new Vector3(0f, 0f, meters),
            "down" => new Vector3(0f, 0f, -meters),
            "forward" => ScaleToLength(Flatten(client.Self.Movement.Camera.AtAxis), meters),
            "back" or "backward" => ScaleToLength(Flatten(Negate(client.Self.Movement.Camera.AtAxis)), meters),
            "left" => ScaleToLength(Flatten(client.Self.Movement.Camera.LeftAxis), meters),
            "right" => ScaleToLength(Flatten(Negate(client.Self.Movement.Camera.LeftAxis)), meters),
            _ => throw new ArgumentException("Unsupported direction. Use: north, south, east, west, up, down, forward, back, left, right")
        };
    }

    private async Task<BotToolResult> MoveToLocalPositionCoreAsync(
        GridClient client,
        Vector3 target,
        bool fly,
        CancellationToken cancellationToken)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return BotToolResult.Fail("No current simulator available.");
        }

        var from = client.Self.SimPosition;

        var distance = Vector3.Distance(from, target);
        if (distance <= 1.0f)
        {
            return BotToolResult.OkResult($"Already at {FormatVector(from)}.");
        }

        var maxStepMeters = 48f;
        var steps = Math.Max(1, (int)MathF.Ceiling(distance / maxStepMeters));

        try
        {
            client.Self.Fly(fly);

            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ratio = step / (float)steps;
                var waypoint = ClampLocalPosition(Interpolate(from, target, ratio));
                var current = client.Self.SimPosition;
                var legDistance = MathF.Max(1f, Vector3.Distance(current, waypoint));
                var timeoutSeconds = Math.Clamp((int)MathF.Ceiling(legDistance * 0.9f), 10, 40);

                client.Self.AutoPilotLocal(
                    (int)MathF.Round(waypoint.X),
                    (int)MathF.Round(waypoint.Y),
                    waypoint.Z);

                var reached = await WaitForArrivalWithRecoveryAsync(
                        client,
                        sim,
                        waypoint,
                        step == steps ? 1.5f : 2.5f,
                        TimeSpan.FromSeconds(timeoutSeconds),
                        fly,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!reached)
                {
                    if (!fly && EnableWalkTeleportFallback)
                    {
                        var recoveredByTeleport = await TryWalkTeleportFallbackAsync(client, sim, waypoint, cancellationToken).ConfigureAwait(false);
                        if (recoveredByTeleport)
                        {
                            continue;
                        }
                    }

                    var atTimeout = client.Self.SimPosition;
                    return BotToolResult.Fail(
                        $"Movement timed out on step {step}/{steps}. Current {FormatVector(atTimeout)}, waypoint {FormatVector(waypoint)}, final target {FormatVector(target)}.");
                }
            }
        }
        finally
        {
            client.Self.AutoPilotCancel();
        }

        var mode = fly ? "flying" : "walking";
        return BotToolResult.OkResult($"Moved by {mode} from {FormatVector(from)} to {FormatVector(client.Self.SimPosition)}.");
    }

    private async Task<bool> WaitForArrivalWithRecoveryAsync(
        GridClient client,
        Simulator sim,
        Vector3 target,
        float tolerance,
        TimeSpan timeout,
        bool fly,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var bestDistance = Vector3.Distance(client.Self.SimPosition, target);
        var lastProgressAt = startedAt;
        var recoveryAttempts = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var at = client.Self.SimPosition;
            var distance = Vector3.Distance(at, target);
            if (distance <= tolerance)
            {
                return true;
            }

            if ((bestDistance - distance) >= WalkProgressThresholdMeters)
            {
                bestDistance = distance;
                lastProgressAt = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - lastProgressAt) >= TimeSpan.FromSeconds(WalkStuckWindowSeconds))
            {
                recoveryAttempts++;
                if (recoveryAttempts > WalkRecoveryMaxAttempts)
                {
                    return false;
                }

                var recovered = false;
                if (!fly)
                {
                    recovered = await TryDoorInteractionRecoveryAsync(client, sim, at, target, cancellationToken).ConfigureAwait(false);
                }

                if (!recovered)
                {
                    recovered = await TryDetourRecoveryAsync(client, at, target, recoveryAttempts, cancellationToken).ConfigureAwait(false);
                }

                if (!recovered)
                {
                    return false;
                }

                client.Self.AutoPilotLocal(
                    (int)MathF.Round(target.X),
                    (int)MathF.Round(target.Y),
                    target.Z);

                bestDistance = Vector3.Distance(client.Self.SimPosition, target);
                lastProgressAt = DateTime.UtcNow;
            }

            if ((DateTime.UtcNow - startedAt) >= timeout)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TryDoorInteractionRecoveryAsync(
        GridClient client,
        Simulator sim,
        Vector3 from,
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var candidates = sim.ObjectsPrimitives.Values
            .Where(p => p != null && !p.IsAttachment)
            .Where(p => Vector3.Distance(from, p.Position) <= 7.5f)
            .Where(p => DistancePointToSegment2D(p.Position, from, target) <= 2.75f)
            .Where(IsDoorLikePrim)
            .OrderBy(p => DistancePointToSegment2D(p.Position, from, target))
            .Take(3)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        foreach (var prim in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Self.Touch(prim.LocalID);
            await Task.Delay(900, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> TryDetourRecoveryAsync(
        GridClient client,
        Vector3 from,
        Vector3 target,
        int recoveryAttempt,
        CancellationToken cancellationToken)
    {
        var toTarget = Flatten(new Vector3(target.X - from.X, target.Y - from.Y, 0f));
        var norm = toTarget.Length();
        if (norm <= 0.0001f)
        {
            return false;
        }

        toTarget /= norm;
        var left = new Vector3(-toTarget.Y, toTarget.X, 0f);
        var offset = Math.Clamp(1.5f * recoveryAttempt, 1.5f, 8f);
        var forwardBias = Math.Clamp(1.2f + (0.4f * recoveryAttempt), 1.2f, 3.5f);

        foreach (var side in new[] { 1f, -1f })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ClampLocalPosition(new Vector3(
                from.X + (left.X * offset * side) + (toTarget.X * forwardBias),
                from.Y + (left.Y * offset * side) + (toTarget.Y * forwardBias),
                MathF.Max(from.Z, target.Z - 1f)));

            client.Self.AutoPilotLocal(
                (int)MathF.Round(candidate.X),
                (int)MathF.Round(candidate.Y),
                candidate.Z);

            var reached = await WaitForArrivalAsync(
                    client,
                    candidate,
                    tolerance: 2.5f,
                    timeout: TimeSpan.FromSeconds(Math.Clamp(6 + recoveryAttempt, 6, 12)),
                    cancellationToken)
                .ConfigureAwait(false);

            if (reached)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryWalkTeleportFallbackAsync(
        GridClient client,
        Simulator sim,
        Vector3 target,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            target,
            ClampLocalPosition(new Vector3(target.X + 4f, target.Y, target.Z)),
            ClampLocalPosition(new Vector3(target.X - 4f, target.Y, target.Z)),
            ClampLocalPosition(new Vector3(target.X, target.Y + 4f, target.Z)),
            ClampLocalPosition(new Vector3(target.X, target.Y - 4f, target.Z))
        };

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var teleported = await client.Self.TeleportAsync(sim.Name, candidate, cancellationToken).ConfigureAwait(false);
            if (!teleported)
            {
                continue;
            }

            client.Self.AutoPilotLocal(
                (int)MathF.Round(target.X),
                (int)MathF.Round(target.Y),
                target.Z);

            var reached = await WaitForArrivalAsync(
                    client,
                    target,
                    tolerance: 2.5f,
                    timeout: TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);

            client.Self.AutoPilotCancel();
            if (reached)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDoorLikePrim(Primitive prim)
    {
        var name = prim.Properties?.Name ?? string.Empty;
        var description = prim.Properties?.Description ?? string.Empty;
        var touchName = prim.Properties?.TouchName ?? string.Empty;
        var searchable = $"{name} {description} {touchName}";

        var hasDoorHint = DoorHintKeywords.Any(keyword => searchable.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        var scripted = (prim.Flags & PrimFlags.Scripted) != 0;

        return hasDoorHint || scripted;
    }

    private static float DistancePointToSegment2D(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
    {
        var ax = segmentStart.X;
        var ay = segmentStart.Y;
        var bx = segmentEnd.X;
        var by = segmentEnd.Y;
        var px = point.X;
        var py = point.Y;

        var abx = bx - ax;
        var aby = by - ay;
        var abLenSq = (abx * abx) + (aby * aby);
        if (abLenSq <= 0.0001f)
        {
            return MathF.Sqrt(((px - ax) * (px - ax)) + ((py - ay) * (py - ay)));
        }

        var apx = px - ax;
        var apy = py - ay;
        var t = Math.Clamp(((apx * abx) + (apy * aby)) / abLenSq, 0f, 1f);
        var nearestX = ax + (abx * t);
        var nearestY = ay + (aby * t);
        var dx = px - nearestX;
        var dy = py - nearestY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static async Task<bool> WaitForArrivalAsync(
        GridClient client,
        Vector3 target,
        float tolerance,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            var at = client.Self.SimPosition;
            if (Vector3.Distance(at, target) <= tolerance)
            {
                return true;
            }

            if ((DateTime.UtcNow - startedAt) >= timeout)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static Vector3 ClampLocalPosition(Vector3 pos)
    {
        return new Vector3(
            Math.Clamp(pos.X, 1f, 255f),
            Math.Clamp(pos.Y, 1f, 255f),
            Math.Clamp(pos.Z, 0f, 4096f));
    }

    private static Vector3 Flatten(Vector3 source)
    {
        return new Vector3(source.X, source.Y, 0f);
    }

    private static Vector3 Negate(Vector3 source)
    {
        return new Vector3(-source.X, -source.Y, -source.Z);
    }

    private static Vector3 ScaleToLength(Vector3 source, float length)
    {
        var norm = source.Length();
        if (norm <= 0.0001f)
        {
            return new Vector3(0f, length, 0f);
        }

        var scale = length / norm;
        return new Vector3(source.X * scale, source.Y * scale, source.Z * scale);
    }

    private static Vector3 Interpolate(Vector3 from, Vector3 to, float ratio)
    {
        return new Vector3(
            from.X + ((to.X - from.X) * ratio),
            from.Y + ((to.Y - from.Y) * ratio),
            from.Z + ((to.Z - from.Z) * ratio));
    }
}
