using System.Globalization;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private static readonly string[] DoorHintKeywords = new[] { "door", "gate", "entry", "entrance", "open", "lobby" };
    
    private const float WalkProgressThresholdMeters = 1.5f;
    private const float WalkStuckWindowSeconds = 6f;
    private const int WalkRecoveryMaxAttempts = 5;
    private const bool EnableWalkTeleportFallback = true;
    private readonly object _movementLock = new();
    private CancellationTokenSource? _movementAutoStopCts;
    private string? _activeFollowTaskHandle;
    private string? _activeFollowMonitorTaskHandle;
    private string? _followTargetDescription;
    private UUID _followTrackedAvatarId = UUID.Zero;
    private uint _followTrackedLocalId;
    private ulong _followAnchorSimHandle;
    private readonly SpawnerClient _followSpawnerClient;

    private enum FollowMode
    {
        None,
        WalkingToBorder,
        RunningToBorder,
        FlyingToBorder,
        TeleportingToTarget,
        AwaitingTeleportAssist,
        Walking,
        Running,
        Flying,
        HardStopped,
        HardStoppedPreserveFlight
    }

    private static readonly TimeSpan FollowTeleportAssistTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FollowTeleportVerificationTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FollowSameRegionStuckWindow = TimeSpan.FromSeconds(12);
    private static readonly float FollowSameRegionProgressEpsilonMeters = 0.9f;
    private static readonly float FollowSameRegionTeleportMinDistanceMeters = 10f;
    
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
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var currentSim = client.Network.CurrentSim;
            if (currentSim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            bool ok;
            string destinationLabel;
            Vector3 target;
            if (string.IsNullOrWhiteSpace(regionName) || string.Equals(regionName, currentSim.Name, StringComparison.OrdinalIgnoreCase))
            {
                // Clamp against the destination region's actual size
                // (varregions exceed the legacy 256m bounds).
                target = ClampLocalPosition(new Vector3(x, y, z), currentSim.SizeX, currentSim.SizeY);
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

                // GridRegion carries no size fields in LibreMetaverse 3.1.6, so
                // the destination's extent is unknown before connecting. Clamp
                // to the maximum varregion extent and let the destination
                // simulator resolve the final landing point (viewer behavior).
                target = ClampLocalPosition(new Vector3(x, y, z), MaxRegionExtent, MaxRegionExtent);
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

    public Task<BotTaskHandle> FollowAsync(string targetType, string target, float distanceBuffer, CancellationToken cancellationToken)
    {
        BotTaskHandle QueueImmediateFailure(string failureMessage)
        {
            return StartBotTask(
                "Follow target.",
                (taskHandle, _) =>
                {
                    EmitFollowCompleteEvent(taskHandle.Handle, false, failureMessage);
                    return Task.CompletedTask;
                });
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult(QueueImmediateFailure("target is required."));
        }

        var buffer = distanceBuffer <= 0f ? 3.0f : Math.Clamp(distanceBuffer, 0.5f, 50f);
        var isObject = string.Equals(targetType, "object", StringComparison.OrdinalIgnoreCase);
        var isAvatar = string.Equals(targetType, "avatar", StringComparison.OrdinalIgnoreCase);
        if (!isObject && !isAvatar)
        {
            return Task.FromResult(QueueImmediateFailure("targetType must be 'avatar' or 'object'."));
        }

        var taskDescription = $"Follow {targetType} '{target.Trim()}'.";
        var followTask = StartBotTask(
            taskDescription,
            async (taskHandle, taskCancellationToken) =>
            {
                try
                {
                    EmitFollowProgressEvent(taskHandle.Handle, "follow.starting", $"Resolving {targetType} target '{target.Trim()}' for follow.");

                    FollowResolution? resolution = null;
                    var setup = await ExecuteLockedAsync(async (client, _) =>
                    {
                        var sim = client.Network.CurrentSim;
                        if (sim == null)
                        {
                            return BotToolResult.Fail("No current simulator available.");
                        }

                        uint localId;
                        string label;
                        var trackedId = UUID.Zero;
                        string? monitorHandle = null;
                        if (isAvatar)
                        {
                            var trimmedTarget = target.Trim();
                            if (UUID.TryParse(trimmedTarget, out var explicitTargetId) && explicitTargetId != UUID.Zero)
                            {
                                trackedId = explicitTargetId;
                                localId = 0;
                                label = trackedId.ToString();

                                // If currently visible, prefer a friendly label and local ID.
                                if (TryResolveAvatarAcrossSims(client, trimmedTarget, out var resolvedSim, out var resolvedLocalId, out var resolvedLabel, out var _ignoredAvatarId))
                                {
                                    sim = resolvedSim;
                                    localId = resolvedLocalId;
                                    label = resolvedLabel;
                                }
                            }
                            else if (!TryResolveAvatarAcrossSims(client, target, out sim, out localId, out label, out trackedId))
                            {
                                return BotToolResult.Fail(
                                    $"Avatar '{target}' not found in visible simulators. Use full name or UUID.");
                            }

                            monitorHandle = (await _agentLocator.MonitorAgent(trackedId.ToString(), taskCancellationToken).ConfigureAwait(false)).Handle;
                        }
                        else if (!TryResolveObject(sim, target, out localId, out label))
                        {
                            return BotToolResult.Fail(
                                $"Object '{target}' not found in current simulator. Use name, local ID, or UUID.");
                        }

                        resolution = new FollowResolution(client, sim, isObject, trackedId, localId, label, monitorHandle);
                        StartFollowLoop(taskHandle.Handle, monitorHandle, client, sim, isObject, trackedId, localId, label);
                        return BotToolResult.OkResult($"Following {targetType} {label} (buffer {buffer:F1}m).");
                    }, taskCancellationToken).ConfigureAwait(false);

                    if (!setup.Ok || resolution == null)
                    {
                        EmitFollowCompleteEvent(taskHandle.Handle, false, setup.Message);
                        return;
                    }

                    var activeResolution = resolution;

                    EmitFollowProgressEvent(
                        taskHandle.Handle,
                        "follow.started",
                        $"Following {targetType} {activeResolution.Label} (buffer {buffer:F1}m).",
                        new Dictionary<string, string?>
                        {
                            ["targetType"] = targetType.Trim().ToLowerInvariant(),
                            ["target"] = activeResolution.Label,
                            ["bufferMeters"] = buffer.ToString("0.0", CultureInfo.InvariantCulture)
                        });

                    var followResult = await FollowLoopAsync(
                        taskHandle.Handle,
                        activeResolution.Client,
                        activeResolution.Simulator,
                        activeResolution.IsObject,
                        activeResolution.TrackedId,
                        activeResolution.LocalId,
                        activeResolution.Label,
                        activeResolution.MonitorHandle,
                        buffer,
                        taskCancellationToken).ConfigureAwait(false);

                    EmitFollowCompleteEvent(taskHandle.Handle, followResult.Ok, followResult.Message);
                }
                catch (OperationCanceledException) when (taskCancellationToken.IsCancellationRequested)
                {
                    EmitFollowCompleteEvent(taskHandle.Handle, true, "Follow cancelled.");
                }
                catch (Exception ex)
                {
                    EmitFollowCompleteEvent(taskHandle.Handle, false, $"Follow failed: {ex.Message}");
                }
            });

        cancellationToken.Register(() =>
        {
            try
            {
                CancelBotTask(followTask.Handle);
            }
            catch
            {
                // Best effort if the caller timeout/cancels before task registration settles.
            }
        });

        return Task.FromResult(followTask);
    }

    private sealed record FollowResolution(
        GridClient Client,
        Simulator Simulator,
        bool IsObject,
        UUID TrackedId,
        uint LocalId,
        string Label,
        string? MonitorHandle);

    private void StartFollowLoop(string followTaskHandle, string? monitorTaskHandle, GridClient client, Simulator sim, bool isObject, UUID trackedId, uint localId, string label)
    {
        StopFollowInternal();
        lock (_movementLock)
        {
            _activeFollowTaskHandle = followTaskHandle;
            _activeFollowMonitorTaskHandle = monitorTaskHandle;
            _followTargetDescription = $"{(isObject ? "object" : "avatar")} {label}";
            _followTrackedAvatarId = isObject ? UUID.Zero : trackedId;
            _followTrackedLocalId = localId;
            _followAnchorSimHandle = sim.Handle;
        }

        if (IsFollowDiagnosticsEnabled())
        {
            Console.WriteLine(
                $"[follow][diag] start target={label} targetUuid={(trackedId == UUID.Zero ? "(n/a)" : trackedId.ToString())} anchorSim={DescribeSimulator(sim)} currentSim={DescribeSimulator(client.Network.CurrentSim)} localId={localId}");
        }
    }

    private async Task<BotToolResult> FollowLoopAsync(
        string followTaskHandle,
        GridClient client,
        Simulator sim,
        bool isObject,
        UUID trackedId,
        uint localId,
        string label,
        string? monitorTaskHandle,
        float buffer,
        CancellationToken cancellationToken)
    {
        // Stage 0: Initialize follow state and safety trackers.
        // These variables track movement cadence, cross-region transitions,
        // and anti-stall telemetry/recovery.
        var targetSim = sim;
        var targetLocalId = localId;
        var lastPilotAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var lastDiagAt = DateTime.UtcNow - TimeSpan.FromSeconds(10);
        var crossRegionStateSince = DateTime.MinValue;
        var lastCrossRegionDistance = float.MaxValue;
        var lastCrossRegionProgressAt = DateTime.UtcNow;
        var crossRegionTeleportAttempts = 0;
        var teleportRequestSent = false;
        ulong lastKnownCrossRegionHandle = 0;
        var lastKnownCrossRegionLocal = Vector3.Zero;
        var followStartRegionHandle = sim.Handle;
        var followStartLocal = ClampLocalPosition(client.Self.SimPosition);
        var avatarLost = false;
        var targetStationary = false;
        var followMovementMode = FollowMode.None;
        bool? lastLoggedTargetFlying = null;
        FollowMode? lastAppliedLocomotionMode = null;
        var sameRegionBestDistance = float.MaxValue;
        var sameRegionLastProgressAt = DateTime.UtcNow;
        var sameRegionRecoveryAttempts = 0;
        var sameRegionLastRecoveryAt = DateTime.MinValue;

        // Stage 1: Apply locomotion mode deltas only when required.
        // This keeps network updates lower and avoids control-flag churn.
        static FollowMode ResolveLocomotionControlMode(FollowMode mode)
            => mode switch
            {
                FollowMode.Running or FollowMode.RunningToBorder => FollowMode.Running,
                FollowMode.Flying or FollowMode.FlyingToBorder => FollowMode.Flying,
                _ => FollowMode.Walking
            };

        static bool IsCrossRegionPhase(FollowMode mode)
            => mode is FollowMode.WalkingToBorder
                or FollowMode.RunningToBorder
                or FollowMode.FlyingToBorder
                or FollowMode.TeleportingToTarget
                or FollowMode.AwaitingTeleportAssist;

        static bool IsBorderWalkPhase(FollowMode mode)
            => mode is FollowMode.WalkingToBorder
                or FollowMode.RunningToBorder
                or FollowMode.FlyingToBorder;

        static FollowMode ResolveBorderFollowMode(FollowMode currentMode, FollowMode fallbackMode)
            => ResolveLocomotionControlMode(currentMode) switch
            {
                FollowMode.Running => FollowMode.RunningToBorder,
                FollowMode.Flying => FollowMode.FlyingToBorder,
                _ => ResolveLocomotionControlMode(fallbackMode) switch
                {
                    FollowMode.Running => FollowMode.RunningToBorder,
                    FollowMode.Flying => FollowMode.FlyingToBorder,
                    _ => FollowMode.WalkingToBorder
                }
            };

        void ApplyFollowLocomotionMode(FollowMode locomotionModeDesired)
        {
            var controlMode = ResolveLocomotionControlMode(locomotionModeDesired);
            if (lastAppliedLocomotionMode == controlMode)
            {
                return;
            }

            switch (controlMode)
            {
                case FollowMode.Flying:
                    client.Self.Fly(true);
                    client.Self.Movement.FastAt = false;
                    client.Self.Movement.FastLeft = false;
                    client.Self.Movement.AlwaysRun = false;
                    client.Self.Movement.SendUpdate(true);
                    break;
                case FollowMode.Running:
                    client.Self.Fly(false);
                    client.Self.Movement.FastAt = true;
                    client.Self.Movement.FastLeft = true;
                    client.Self.Movement.AlwaysRun = true;
                    client.Self.Movement.SendUpdate(true);
                    break;
                default:
                    client.Self.Fly(false);
                    client.Self.Movement.FastAt = false;
                    client.Self.Movement.FastLeft = false;
                    client.Self.Movement.AlwaysRun = false;
                    client.Self.Movement.SendUpdate(true);
                    break;
            }

            if (IsFollowDiagnosticsEnabled())
            {
                Console.WriteLine(
                    $"[follow][runmode] controls_applied target={label} requestedMode={locomotionModeDesired} appliedLocomotion={controlMode} fastAt={client.Self.Movement.FastAt} fastLeft={client.Self.Movement.FastLeft} flyNow={client.Self.Movement.Fly} alwaysRunNow={client.Self.Movement.AlwaysRun}");
            }

            lastAppliedLocomotionMode = controlMode;
        }

        // Stage 2: Hard-stop helper for hold states and cleanup transitions.
        // preserveFlight is used when we intentionally keep hover mode active.
        void EnsureFollowMovementStopped(bool preserveFlight = false)
        {
            var desiredStopMode = preserveFlight
                ? FollowMode.HardStoppedPreserveFlight
                : FollowMode.HardStopped;

            if (followMovementMode == desiredStopMode)
            {
                return;
            }

            var alreadyHardStopped = followMovementMode is FollowMode.HardStopped or FollowMode.HardStoppedPreserveFlight;

            if (IsFollowDiagnosticsEnabled())
            {
                Console.WriteLine(
                    $"[follow][flydiag] stop begin target={label} preserveFlight={preserveFlight} botFlyBefore={client.Self.Movement.Fly} hardStopped={alreadyHardStopped}");
            }

            client.Self.AutoPilotCancel();
            client.Self.Movement.ResetControlFlags();
            if (preserveFlight)
            {
                client.Self.Fly(true);
            }
            client.Self.Movement.SendUpdate(true);
            followMovementMode = desiredStopMode;
            // Reset applied locomotion cache because ResetControlFlags clears run/strafe bits.
            lastAppliedLocomotionMode = null;

            if (IsFollowDiagnosticsEnabled())
            {
                Console.WriteLine(
                    $"[follow][flydiag] stop done target={label} preserveFlight={preserveFlight} botFlyAfter={client.Self.Movement.Fly}");
            }
        }

        // Stage 3: Main follow tick loop (500ms cadence).
        // Each tick resolves target state, chooses a waypoint, evaluates
        // cross-region state-machine transitions, then drives movement.
        var completion = BotToolResult.OkResult($"Follow ended for {label}.");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);

                var botSim = client.Network.CurrentSim;
                if (botSim == null)
                {
                    // Stage 3a: Abort if we no longer have an active simulator.
                    Console.WriteLine($"[follow] no active simulator; stopping follow of {label}.");
                    EmitFollowProgressEvent(followTaskHandle, "follow.stopping", $"Lost active simulator while following {label}; stopping follow.");
                    completion = BotToolResult.Fail($"Lost active simulator while following {label}.");
                    if (IsFollowDiagnosticsEnabled())
                    {
                        Console.WriteLine(
                            $"[follow][diag] stop reason=no_current_sim target={label} lastTargetSim={DescribeSimulator(targetSim)}");
                    }
                    break;
                }

                Vector3 targetPos;
                var targetIsCrossRegion = false;
                var suppressMovementForStationary = false;
                var preserveFlightOnStop = false;
                var locomotionModeDesired = FollowMode.Walking;
                ulong crossRegionTargetHandle = 0;
                Vector3 crossRegionTargetLocal = Vector3.Zero;
                float distance;
                if (isObject)
                {
                    // Stage 3b: Object follow path (same-region only).
                    if (!ReferenceEquals(botSim, targetSim))
                    {
                        Console.WriteLine($"[follow] object {label} changed region; stopping.");
                        completion = BotToolResult.Fail($"Object {label} changed region; follow stopped.");
                        break;
                    }

                    if (!targetSim.ObjectsPrimitives.TryGetValue(targetLocalId, out var prim))
                    {
                        Console.WriteLine($"[follow] object {label} no longer in cache; stopping.");
                        completion = BotToolResult.Fail($"Object {label} is no longer in cache; follow stopped.");
                        break;
                    }

                    targetPos = prim.Position;
                    distance = Vector3.Distance(targetPos, client.Self.SimPosition);
                }
                else
                {
                    // Stage 3c: Avatar follow path (driven by AgentLocator monitor).
                    if (string.IsNullOrWhiteSpace(monitorTaskHandle)
                        || !_agentLocator.TryGetLatestStatus(monitorTaskHandle, out var monitorStatus))
                    {
                        EnsureFollowMovementStopped();
                        continue;
                    }

                    if (monitorStatus.Online == false)
                    {
                        if (!avatarLost)
                        {
                            avatarLost = true;
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.target.lost",
                                $"Lost avatar {label}; monitor reported offline.");
                        }

                        EnsureFollowMovementStopped();
                        continue;
                    }

                    if (monitorStatus.LocalId.HasValue)
                    {
                        targetLocalId = monitorStatus.LocalId.Value;
                        lock (_movementLock)
                        {
                            _followTrackedLocalId = targetLocalId;
                        }
                    }

                    if (monitorStatus.RegionHandle.HasValue)
                    {
                        lock (_movementLock)
                        {
                            _followAnchorSimHandle = monitorStatus.RegionHandle.Value;
                        }
                    }

                    if (monitorStatus.RegionHandle.HasValue && monitorStatus.Position.HasValue)
                    {
                        // Stage 3c.1: Target has concrete region+position.
                        // Resolve direct same-region or cross-region waypoint.
                        if (avatarLost)
                        {
                            avatarLost = false;
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.target.refound",
                                $"Refound avatar {label}; resuming follow.");
                        }

                        var statusRegionHandle = monitorStatus.RegionHandle.Value;
                        var statusPosition = ClampLocalPosition(monitorStatus.Position.Value);
                        if (statusRegionHandle == botSim.Handle)
                        {
                            targetSim = botSim;
                        }
                        else
                        {
                            var statusSim = client.Network.Simulators.FirstOrDefault(candidate => candidate.Handle == statusRegionHandle);
                            if (statusSim != null)
                            {
                                targetSim = statusSim;
                            }
                        }
                        var targetFlyingNow = monitorStatus.IsFlying == true;
                        if (IsFollowDiagnosticsEnabled() && lastLoggedTargetFlying != targetFlyingNow)
                        {
                            Console.WriteLine(
                                $"[follow][flydiag] target-fly target={label} targetIsFlying={monitorStatus.IsFlying?.ToString() ?? "null"} targetVelocity={monitorStatus.Velocity?.Length().ToString("0.00", CultureInfo.InvariantCulture) ?? "null"}");
                            lastLoggedTargetFlying = targetFlyingNow;
                        }
                        crossRegionTargetHandle = statusRegionHandle;
                        crossRegionTargetLocal = statusPosition;
                        lastKnownCrossRegionHandle = statusRegionHandle;
                        lastKnownCrossRegionLocal = statusPosition;

                        targetPos = ResolveFollowWaypointFromRegionHandle(
                            botSim,
                            client.Self.SimPosition,
                            statusRegionHandle,
                            statusPosition,
                            out distance,
                            out targetIsCrossRegion);

                        var deltaZ = MathF.Abs(statusPosition.Z - client.Self.SimPosition.Z);
                        var runThreshold = MathF.Max(buffer + 1.5f, buffer * 1.6f);
                        var flyCatchupThreshold = MathF.Max(runThreshold + 6f, buffer * 3f);
                        var flyCatchupZThreshold = 3.5f;
                        var beyondRunThreshold = distance > runThreshold;
                        var veryBehind = distance > flyCatchupThreshold;
                        var largeVerticalGap = deltaZ > flyCatchupZThreshold;
                        var velocityMagnitude = monitorStatus.Velocity?.Length() ?? -1f;

                        // Keep grounded chasing grounded: only enter fly mode when the target is flying.
                        // This isolates running behavior and avoids masking run-speed tests with fly escalation.
                        var flyModeDesired = !targetIsCrossRegion && targetFlyingNow;
                        var runModeDesired = !targetIsCrossRegion && beyondRunThreshold && !flyModeDesired;
                        locomotionModeDesired = flyModeDesired
                            ? FollowMode.Flying
                            : (runModeDesired ? FollowMode.Running : FollowMode.Walking);
                        preserveFlightOnStop = locomotionModeDesired == FollowMode.Flying;

                        var isStationaryNow = velocityMagnitude >= 0f && velocityMagnitude < 0.05f;
                        var holdForStationary = isStationaryNow && distance <= buffer;
                        if (isStationaryNow)
                        {
                            suppressMovementForStationary = holdForStationary;
                            if (!targetStationary)
                            {
                                targetStationary = true;
                                EmitFollowProgressEvent(
                                    followTaskHandle,
                                    "follow.target.stationary",
                                    holdForStationary
                                        ? $"Target {label} velocity is zero; holding position while follow remains active."
                                        : $"Target {label} velocity is zero; closing follow distance before holding position.");
                            }
                        }
                        else if (targetStationary)
                        {
                            targetStationary = false;
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.target.moving",
                                $"Target {label} is moving again; resuming follow movement.");
                        }
                    }
                    else
                    {
                        // Stage 3c.2: Off-region fallback path.
                        // Use last known handle/local for border pathing and
                        // teleport fallback when we cannot see direct position.
                        if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][diag] off-region target={label} source={monitorStatus.Source ?? "unknown"} online={(monitorStatus.Online.HasValue ? (monitorStatus.Online.Value ? "true" : "false") : "null")} avatarRegionHandle={(monitorStatus.AvatarRegionHandle?.ToString() ?? "null")} regionHandle={(monitorStatus.RegionHandle?.ToString() ?? "null")} cachedLocal={FormatPosition(lastKnownCrossRegionLocal)}");
                        }

                        if (!avatarLost)
                        {
                            avatarLost = true;
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.target.off_region",
                                $"Target {label} is off-region; moving toward best-known crossing path.");
                        }

                        targetStationary = false;

                        var offRegionHandle = monitorStatus.AvatarRegionHandle ?? lastKnownCrossRegionHandle;
                        if (offRegionHandle == 0)
                        {
                            EnsureFollowMovementStopped();
                            continue;
                        }

                        if (lastKnownCrossRegionLocal.Length() <= 0.001f)
                        {
                            lastKnownCrossRegionLocal = ClampLocalPosition(client.Self.SimPosition);
                        }

                        var offRegionSim = client.Network.Simulators.FirstOrDefault(candidate => candidate.Handle == offRegionHandle);
                        if (offRegionSim != null)
                        {
                            targetSim = offRegionSim;
                        }

                        crossRegionTargetHandle = offRegionHandle;
                        crossRegionTargetLocal = lastKnownCrossRegionLocal;
                        targetPos = ResolveFollowWaypointFromRegionHandle(
                            botSim,
                            client.Self.SimPosition,
                            crossRegionTargetHandle,
                            crossRegionTargetLocal,
                            out distance,
                            out targetIsCrossRegion);
                    }
                }

                if (IsFollowDiagnosticsEnabled() && (DateTime.UtcNow - lastDiagAt) >= TimeSpan.FromSeconds(3))
                {
                    // Stage 3d: periodic diagnostics snapshot.
                    Console.WriteLine(
                        $"[follow][diag] tick target={label} targetSim={DescribeSimulator(targetSim)} botSim={DescribeSimulator(botSim)} botPos={FormatPosition(client.Self.SimPosition)} waypoint={FormatPosition(targetPos)} distance={distance:F1} buffer={buffer:F1} crossRegion={targetIsCrossRegion}");
                    lastDiagAt = DateTime.UtcNow;
                }

                if (targetIsCrossRegion)
                {
                    // Stage 3e: Cross-region state machine.
                    // Walk to seam -> teleport attempts -> teleport assist.
                    if (crossRegionTargetHandle != 0)
                    {
                        lastKnownCrossRegionHandle = crossRegionTargetHandle;
                        lastKnownCrossRegionLocal = ClampLocalPosition(crossRegionTargetLocal);
                    }

                    if (!IsCrossRegionPhase(followMovementMode))
                    {
                        followMovementMode = ResolveBorderFollowMode(followMovementMode, locomotionModeDesired);
                        crossRegionStateSince = DateTime.UtcNow;
                        lastCrossRegionDistance = distance;
                        lastCrossRegionProgressAt = DateTime.UtcNow;
                        crossRegionTeleportAttempts = 0;
                        teleportRequestSent = false;
                        EmitFollowProgressEvent(
                            followTaskHandle,
                            "follow.cross_region.walk_border",
                            $"Target {label} moved cross-region; walking to border waypoint.");
                    }
                    else if ((lastCrossRegionDistance - distance) >= 1.0f)
                    {
                        lastCrossRegionDistance = distance;
                        lastCrossRegionProgressAt = DateTime.UtcNow;
                    }

                    if (IsBorderWalkPhase(followMovementMode)
                        && (DateTime.UtcNow - lastCrossRegionProgressAt) >= TimeSpan.FromSeconds(8))
                    {
                        followMovementMode = FollowMode.TeleportingToTarget;
                        crossRegionStateSince = DateTime.UtcNow;
                        client.Self.AutoPilotCancel();
                        EmitFollowProgressEvent(
                            followTaskHandle,
                            "follow.cross_region.teleport_attempt",
                            $"Border pathing stalled while following {label}; attempting teleport.");
                    }

                    if (followMovementMode == FollowMode.TeleportingToTarget
                        && crossRegionTeleportAttempts < 2
                        && lastKnownCrossRegionHandle != 0)
                    {
                        var preTeleportSim = client.Network.CurrentSim;
                        var preTeleportPos = client.Self.SimPosition;
                        crossRegionTeleportAttempts++;
                        EmitFollowProgressEvent(
                            followTaskHandle,
                            "follow.cross_region.teleport_try",
                            $"Teleport attempt {crossRegionTeleportAttempts} while following {label} (targetHandle={lastKnownCrossRegionHandle}, targetLocal={FormatPosition(lastKnownCrossRegionLocal)})."
                        );

                        if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][diag] teleport_try target={label} attempt={crossRegionTeleportAttempts} expectedHandle={lastKnownCrossRegionHandle} targetLocal={FormatPosition(lastKnownCrossRegionLocal)} preSim={DescribeSimulator(preTeleportSim)} prePos={FormatPosition(preTeleportPos)}");
                        }

                        var teleported = await client.Self.TeleportAsync(lastKnownCrossRegionHandle, lastKnownCrossRegionLocal, cancellationToken).ConfigureAwait(false);
                        if (teleported)
                        {
                            var verification = await VerifyCrossRegionTeleportAsync(
                                client,
                                lastKnownCrossRegionHandle,
                                lastKnownCrossRegionLocal,
                                preTeleportSim,
                                preTeleportPos,
                                cancellationToken).ConfigureAwait(false);

                            if (verification.Confirmed)
                            {
                                EmitFollowProgressEvent(
                                    followTaskHandle,
                                    "follow.cross_region.teleport_succeeded",
                                    $"Teleport confirmed; continuing follow of {label}. {verification.Details}");
                                followMovementMode = FollowMode.None;
                                crossRegionStateSince = DateTime.MinValue;
                                lastCrossRegionDistance = float.MaxValue;
                                lastCrossRegionProgressAt = DateTime.UtcNow;
                                continue;
                            }

                            Console.WriteLine(
                                $"[follow] teleport acknowledged but not confirmed while following {label}. {verification.Details}");
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.cross_region.teleport_unconfirmed",
                                $"Teleport returned success but location did not confirm for {label}; retrying if attempts remain. {verification.Details}");
                        }
                        else if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][diag] teleport_try_failed target={label} attempt={crossRegionTeleportAttempts} expectedHandle={lastKnownCrossRegionHandle} targetLocal={FormatPosition(lastKnownCrossRegionLocal)} message={(string.IsNullOrWhiteSpace(client.Self.TeleportMessage) ? "(none)" : client.Self.TeleportMessage)} postSim={DescribeSimulator(client.Network.CurrentSim)} postPos={FormatPosition(client.Self.SimPosition)}");
                        }

                        if (crossRegionTeleportAttempts >= 2)
                        {
                            followMovementMode = FollowMode.AwaitingTeleportAssist;
                            crossRegionStateSince = DateTime.UtcNow;
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.cross_region.awaiting_assist",
                                $"Teleport attempts failed while following {label}; requesting teleport assist.");
                        }
                    }

                    if (followMovementMode == FollowMode.AwaitingTeleportAssist
                        && !teleportRequestSent
                        && trackedId != UUID.Zero)
                    {
                        client.Self.SendTeleportLureRequest(
                            trackedId,
                            "Could you send me a teleport? I lost pathing while following you across regions.");
                        teleportRequestSent = true;
                        EmitFollowProgressEvent(
                            followTaskHandle,
                            "follow.cross_region.assist_requested",
                            $"Requested teleport assist from {label}.");
                    }

                    if (followMovementMode == FollowMode.AwaitingTeleportAssist
                        && crossRegionStateSince != DateTime.MinValue
                        && (DateTime.UtcNow - crossRegionStateSince) >= FollowTeleportAssistTimeout)
                    {
                        client.Self.AutoPilotCancel();
                        var returnedToStart = await client.Self
                            .TeleportAsync(followStartRegionHandle, followStartLocal, cancellationToken)
                            .ConfigureAwait(false);

                        if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][diag] assist_timeout_return_to_start target={label} returned={returnedToStart} startHandle={followStartRegionHandle} startLocal={FormatPosition(followStartLocal)}");
                        }

                        Console.WriteLine(
                            returnedToStart
                                ? $"[follow] teleport assist timed out; returned to follow start and stopping follow of {label}."
                                : $"[follow] teleport assist timed out; failed to return to follow start and stopping follow of {label}.");
                        completion = BotToolResult.Fail(returnedToStart
                            ? $"Teleport assist timed out while following {label}; returned to follow start and stopped."
                            : $"Teleport assist timed out while following {label}; failed return to follow start and stopped.");
                        break;
                    }
                }
                else
                {
                    // Stage 3f: Same-region reset path.
                    // Clear cross-region state and track local movement progress
                    // for anti-stall recovery.
                    if (IsCrossRegionPhase(followMovementMode))
                    {
                        followMovementMode = FollowMode.None;
                    }
                    crossRegionStateSince = DateTime.MinValue;
                    lastCrossRegionDistance = float.MaxValue;
                    lastCrossRegionProgressAt = DateTime.UtcNow;
                    crossRegionTeleportAttempts = 0;
                    teleportRequestSent = false;
                    lastKnownCrossRegionHandle = 0;

                    if (distance < sameRegionBestDistance - FollowSameRegionProgressEpsilonMeters)
                    {
                        sameRegionBestDistance = distance;
                        sameRegionLastProgressAt = DateTime.UtcNow;
                        sameRegionRecoveryAttempts = 0;
                    }
                    else if (distance <= buffer)
                    {
                        sameRegionBestDistance = float.MaxValue;
                        sameRegionLastProgressAt = DateTime.UtcNow;
                        sameRegionRecoveryAttempts = 0;
                    }
                }

                var holdPositionForTeleportAssist = followMovementMode == FollowMode.AwaitingTeleportAssist;

                if (suppressMovementForStationary)
                {
                    EnsureFollowMovementStopped(preserveFlightOnStop);
                    continue;
                }

                if (distance > buffer)
                {
                    // Stage 3g: Same-region anti-stall recovery.
                    // If we are in-region, far from target, and making no real
                    // progress for a while, perform controlled teleport recovery.
                    if (!targetIsCrossRegion
                        && distance >= FollowSameRegionTeleportMinDistanceMeters
                        && (DateTime.UtcNow - sameRegionLastProgressAt) >= FollowSameRegionStuckWindow
                        && (DateTime.UtcNow - sameRegionLastRecoveryAt) >= TimeSpan.FromSeconds(8)
                        && sameRegionRecoveryAttempts < 2)
                    {
                        sameRegionRecoveryAttempts++;
                        sameRegionLastRecoveryAt = DateTime.UtcNow;
                        client.Self.AutoPilotCancel();
                        EmitFollowProgressEvent(
                            followTaskHandle,
                            "follow.same_region.recovery_teleport_try",
                            $"Same-region follow appears stuck while tracking {label}; trying local teleport recovery {sameRegionRecoveryAttempts}/2.");

                        if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][diag] same_region_recovery_try target={label} attempt={sameRegionRecoveryAttempts} botSim={DescribeSimulator(botSim)} targetPos={FormatPosition(targetPos)} distance={distance:F2} noProgressSeconds={(DateTime.UtcNow - sameRegionLastProgressAt).TotalSeconds:F1}");
                        }

                        var recovered = await client.Self.TeleportAsync(botSim.Handle, targetPos, cancellationToken).ConfigureAwait(false);
                        if (recovered)
                        {
                            EmitFollowProgressEvent(
                                followTaskHandle,
                                "follow.same_region.recovery_teleport_ok",
                                $"Local teleport recovery succeeded while following {label}; resuming follow.");
                            sameRegionBestDistance = float.MaxValue;
                            sameRegionLastProgressAt = DateTime.UtcNow;
                            continue;
                        }

                        if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][diag] same_region_recovery_failed target={label} attempt={sameRegionRecoveryAttempts} message={(string.IsNullOrWhiteSpace(client.Self.TeleportMessage) ? "(none)" : client.Self.TeleportMessage)}");
                        }
                    }

                    // Stage 3h: Normal movement command emission.
                    // Re-issue autopilot at most once per second to avoid packet spam.
                    if (!holdPositionForTeleportAssist
                        && (DateTime.UtcNow - lastPilotAt) >= TimeSpan.FromSeconds(1))
                    {
                        var movementModeToApply = targetIsCrossRegion && IsCrossRegionPhase(followMovementMode)
                            ? followMovementMode
                            : locomotionModeDesired;

                        ApplyFollowLocomotionMode(movementModeToApply);

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
                        if (IsFollowDiagnosticsEnabled())
                        {
                            Console.WriteLine(
                                $"[follow][flydiag] move target={label} distance={distance:F2} buffer={buffer:F2} preserveFlightOnStop={preserveFlightOnStop} locomotion={locomotionModeDesired} botFly={client.Self.Movement.Fly} waypointZ={targetPos.Z:F2} botZ={client.Self.SimPosition.Z:F2}");
                        }
                        if (!targetIsCrossRegion)
                        {
                            followMovementMode = locomotionModeDesired;
                        }
                        lastPilotAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Stage 3i: Inside follow buffer: hold position.
                    EnsureFollowMovementStopped(preserveFlightOnStop);
                }
            }
            catch (OperationCanceledException)
            {
                completion = BotToolResult.OkResult($"Follow cancelled for {label}.");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[follow] error while following {label}: {ex.Message}");
                completion = BotToolResult.Fail($"Follow error while tracking {label}: {ex.Message}");
                break;
            }
        }

        // Stage 4: Final cleanup and monitor teardown.
        try
        {
            client.Self.AutoPilotCancel();
            client.Self.Movement.ResetControlFlags();
            client.Self.Movement.SendUpdate(true);
        }
        catch
        {
            // Best-effort cleanup.
        }

        lastAppliedLocomotionMode = null;
        followMovementMode = FollowMode.None;

        if (!string.IsNullOrWhiteSpace(monitorTaskHandle))
        {
            _botTaskManager.TryCancel(monitorTaskHandle, out _);
        }

        ClearFollowTrackingState(followTaskHandle);
        return completion;
    }

    private bool IsFollowDiagnosticsEnabled()
        => true;

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

    private async Task<(bool Confirmed, string Details)> VerifyCrossRegionTeleportAsync(
        GridClient client,
        ulong expectedRegionHandle,
        Vector3 requestedLocal,
        Simulator? preTeleportSim,
        Vector3 preTeleportPosition,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var lastSnapshot = BuildTeleportVerificationSnapshot(client, expectedRegionHandle, requestedLocal, preTeleportSim, preTeleportPosition);

        while (!cancellationToken.IsCancellationRequested
            && (DateTime.UtcNow - startedAt) < FollowTeleportVerificationTimeout)
        {
            var currentSim = client.Network.CurrentSim;
            var currentHandle = currentSim?.Handle ?? 0;
            var currentPosition = client.Self.SimPosition;
            var simChanged = preTeleportSim == null || currentHandle != preTeleportSim.Handle;
            var nowOnExpectedRegion = currentHandle == expectedRegionHandle;
            var movedSinceTeleport = Vector3.Distance(currentPosition, preTeleportPosition) >= 1.0f;

            if (nowOnExpectedRegion && (simChanged || movedSinceTeleport))
            {
                return (true, BuildTeleportVerificationSnapshot(client, expectedRegionHandle, requestedLocal, preTeleportSim, preTeleportPosition));
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            lastSnapshot = BuildTeleportVerificationSnapshot(client, expectedRegionHandle, requestedLocal, preTeleportSim, preTeleportPosition);
        }

        return (false, lastSnapshot);
    }

    private static string BuildTeleportVerificationSnapshot(
        GridClient client,
        ulong expectedRegionHandle,
        Vector3 requestedLocal,
        Simulator? preTeleportSim,
        Vector3 preTeleportPosition)
    {
        var currentSim = client.Network.CurrentSim;
        var currentPosition = client.Self.SimPosition;
        var distanceFromPreTeleport = Vector3.Distance(currentPosition, preTeleportPosition);
        return $"expectedHandle={expectedRegionHandle} requestedLocal={FormatPosition(requestedLocal)} preSim={DescribeSimulator(preTeleportSim)} prePos={FormatPosition(preTeleportPosition)} postSim={DescribeSimulator(currentSim)} postPos={FormatPosition(currentPosition)} movedMeters={distanceFromPreTeleport:0.00} teleportMessage={(string.IsNullOrWhiteSpace(client.Self.TeleportMessage) ? "(none)" : client.Self.TeleportMessage)}";
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

    private void EmitFollowProgressEvent(
        string handle,
        string eventType,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        var payload = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["handle"] = handle
        };

        if (attributes != null)
        {
            foreach (var pair in attributes)
            {
                payload[pair.Key] = pair.Value;
            }
        }

        EmitRuntimeEvent(
            "follow",
            eventType,
            "follow",
            message,
            payload);
    }

    private void EmitFollowCompleteEvent(string handle, bool success, string message)
    {
        _botTaskManager.TryReportCompletion(handle, success, message);
        EmitRuntimeEvent(
            "follow",
            "follow.complete",
            "follow",
            message,
            new Dictionary<string, string?>
            {
                ["handle"] = handle,
                ["success"] = success ? "true" : "false"
            });
    }

    private void ClearFollowTrackingState(string handle)
    {
        lock (_movementLock)
        {
            if (string.IsNullOrWhiteSpace(_activeFollowTaskHandle)
                || !_activeFollowTaskHandle.Equals(handle, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activeFollowTaskHandle = null;
            _activeFollowMonitorTaskHandle = null;
            _followTargetDescription = null;
            _followTrackedAvatarId = UUID.Zero;
            _followTrackedLocalId = 0;
            _followAnchorSimHandle = 0;
        }
    }

    private bool StopFollowInternal()
    {
        string? handle;
        string? monitorHandle;
        lock (_movementLock)
        {
            handle = _activeFollowTaskHandle;
            monitorHandle = _activeFollowMonitorTaskHandle;
            _activeFollowTaskHandle = null;
            _activeFollowMonitorTaskHandle = null;
            _followTargetDescription = null;
            _followTrackedAvatarId = UUID.Zero;
            _followTrackedLocalId = 0;
            _followAnchorSimHandle = 0;
        }

        if (!string.IsNullOrWhiteSpace(monitorHandle))
        {
            _botTaskManager.TryCancel(monitorHandle, out _);
        }

        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        return _botTaskManager.TryCancel(handle, out _);
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

    private const uint MaxRegionExtent = 4096;

    private Vector3 ClampLocalPosition(Vector3 pos)
    {
        // Varregion-aware bounds for operations within the current
        // region. Fall back to legacy 256m bounds when the size is unknown.
        var sim = _client?.Network?.CurrentSim;
        return ClampLocalPosition(pos, sim?.SizeX ?? 0, sim?.SizeY ?? 0);
    }

    private static Vector3 ClampLocalPosition(Vector3 pos, uint sizeX, uint sizeY)
    {
        var maxX = 255f;
        var maxY = 255f;
        if (sizeX > 0 && sizeY > 0)
        {
            maxX = Math.Max(1f, sizeX - 1f);
            maxY = Math.Max(1f, sizeY - 1f);
        }

        return new Vector3(
            Math.Clamp(pos.X, 1f, maxX),
            Math.Clamp(pos.Y, 1f, maxY),
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

internal sealed record CameraState(
    float CameraX,
    float CameraY,
    float CameraZ,
    float AtAxisX,
    float AtAxisY,
    float AtAxisZ,
    float LeftAxisX,
    float LeftAxisY,
    float LeftAxisZ,
    float UpAxisX,
    float UpAxisY,
    float UpAxisZ,
    float Far,
    float AgentX,
    float AgentY,
    float AgentZ);


internal sealed record CameraStateResult(bool Ok, string Message, CameraState? State)
{
    public static CameraStateResult FailResult(string message) => new(false, message, null);
}