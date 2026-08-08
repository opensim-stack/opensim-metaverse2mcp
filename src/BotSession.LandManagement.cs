using System.Text.Json;
using System.IO;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const float MinimumTerrainCoverageForMutation = 0.999f;

    public async Task<DataToolResult> ParcelGetCurrentAsync(bool includeAccessLists, bool forceRefresh, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            await EnsureParcelMapAsync(client, sim, forceRefresh, token).ConfigureAwait(false);

            var localId = client.Parcels.GetParcelLocalID(sim, client.Self.SimPosition);
            if (localId <= 0)
            {
                return DataToolResult.FailResult("Unable to resolve current parcel local ID from simulator parcel map.");
            }

            return await ParcelGetByLocalIdCoreAsync(client, sim, localId, includeAccessLists, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> ParcelGetByLocalIdAsync(int localId, bool includeAccessLists, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (localId <= 0)
        {
            return DataToolResult.FailResult("localId must be greater than 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            await EnsureParcelMapAsync(client, sim, forceRefresh, token).ConfigureAwait(false);
            return await ParcelGetByLocalIdCoreAsync(client, sim, localId, includeAccessLists, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ParcelSetInfoAsync(
        int localId,
        string? name,
        string? description,
        string? musicUrl,
        string? mediaUrl,
        CancellationToken cancellationToken)
    {
        if (localId <= 0)
        {
            return BotToolResult.Fail("localId must be greater than 0.");
        }

        if (name == null && description == null && musicUrl == null && mediaUrl == null)
        {
            return BotToolResult.Fail("At least one field must be provided (name, description, musicUrl, mediaUrl).");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            var parcel = await GetParcelAsync(client, sim, localId, refreshFromSimulator: true, token).ConfigureAwait(false);
            if (parcel == null)
            {
                return BotToolResult.Fail($"Parcel localId={localId} was not found.");
            }

            var updatedFields = new List<string>(4);
            if (name != null)
            {
                parcel.Name = name;
                updatedFields.Add("name");
            }

            if (description != null)
            {
                parcel.Desc = description;
                updatedFields.Add("description");
            }

            if (musicUrl != null)
            {
                parcel.MusicURL = musicUrl;
                updatedFields.Add("musicUrl");
            }

            if (mediaUrl != null)
            {
                parcel.Media.MediaURL = mediaUrl;
                updatedFields.Add("mediaUrl");
            }

            parcel.Update(client, sim, wantReply: true);
            return BotToolResult.OkResult($"Parcel {localId} update submitted ({string.Join(", ", updatedFields)}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ParcelSetLandingAsync(
        int localId,
        string landingType,
        float? x,
        float? y,
        float? z,
        float? lookAtX,
        float? lookAtY,
        float? lookAtZ,
        CancellationToken cancellationToken)
    {
        if (localId <= 0)
        {
            return BotToolResult.Fail("localId must be greater than 0.");
        }

        if (!TryParseLandingType(landingType, out var landing, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (landing == LandingType.LandingPoint && (!x.HasValue || !y.HasValue || !z.HasValue))
        {
            return BotToolResult.Fail("x, y, and z are required when landingType is LandingPoint.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            var parcel = await GetParcelAsync(client, sim, localId, refreshFromSimulator: true, token).ConfigureAwait(false);
            if (parcel == null)
            {
                return BotToolResult.Fail($"Parcel localId={localId} was not found.");
            }

            parcel.Landing = landing;

            if (x.HasValue && y.HasValue && z.HasValue)
            {
                parcel.UserLocation = ClampLocalPosition(new Vector3(x.Value, y.Value, z.Value));
            }

            if (lookAtX.HasValue && lookAtY.HasValue && lookAtZ.HasValue)
            {
                parcel.UserLookAt = ClampLocalPosition(new Vector3(lookAtX.Value, lookAtY.Value, lookAtZ.Value));
            }

            parcel.Update(client, sim, wantReply: true);
            return BotToolResult.OkResult(
                $"Parcel {localId} landing update submitted (landingType={landing}, location={FormatVector(parcel.UserLocation)}, lookAt={FormatVector(parcel.UserLookAt)}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> ParcelAccessListGetAsync(int localId, string listType, CancellationToken cancellationToken)
    {
        if (localId <= 0)
        {
            return DataToolResult.FailResult("localId must be greater than 0.");
        }

        if (!TryParseAccessListScope(listType, out var requestedScope, out var parseError))
        {
            return DataToolResult.FailResult(parseError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            var accessReply = await WaitForParcelAccessListReplyAsync(client, sim, localId, requestedScope, token).ConfigureAwait(false);
            if (accessReply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for access list reply for parcel {localId}.");
            }

            var allow = new List<ParcelAccessEntryInfo>();
            var ban = new List<ParcelAccessEntryInfo>();
            foreach (var entry in accessReply.AccessList)
            {
                var info = new ParcelAccessEntryInfo(
                    entry.AgentID.ToString(),
                    entry.Time.ToUniversalTime().ToString("O"),
                    entry.Flags.ToString());

                if (entry.Flags.HasFlag(AccessList.Access))
                {
                    allow.Add(info);
                }

                if (entry.Flags.HasFlag(AccessList.Ban))
                {
                    ban.Add(info);
                }
            }

            var payload = new
            {
                simulator = sim.Name,
                localId,
                requestedScope = requestedScope.ToString(),
                allowList = allow,
                banList = ban
            };

            return DataToolResult.OkResult(
                $"Retrieved parcel access list for localId={localId} (allow={allow.Count}, ban={ban.Count}).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ParcelEjectUserAsync(string targetAgentId, bool ban, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(targetAgentId, out var targetId))
        {
            return BotToolResult.Fail("targetAgentId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, token) =>
        {
            client.Parcels.EjectUser(targetId, ban);
            return Task.FromResult(BotToolResult.OkResult(
                ban
                    ? $"Eject+ban request sent for avatar {targetId}."
                    : $"Eject request sent for avatar {targetId}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ParcelJoinAsync(
        float west,
        float south,
        float east,
        float north,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeParcelBounds(west, south, east, north, out var normalized, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await ExecuteLockedAsync((client, cancellation) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Parcels.ParcelJoin(sim, normalized.West, normalized.South, normalized.East, normalized.North);
            return Task.FromResult(BotToolResult.OkResult(
                $"Parcel join request sent for bounds <{normalized.West:F2},{normalized.South:F2}>..<{normalized.East:F2},{normalized.North:F2}>."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ParcelSubdivideAsync(
        float west,
        float south,
        float east,
        float north,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeParcelBounds(west, south, east, north, out var normalized, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            client.Parcels.ParcelSubdivide(sim, normalized.West, normalized.South, normalized.East, normalized.North);
            return Task.FromResult(BotToolResult.OkResult(
                $"Parcel subdivide request sent for bounds <{normalized.West:F2},{normalized.South:F2}>..<{normalized.East:F2},{normalized.North:F2}>."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> ParcelPermissionDiagnosticsAsync(int? localId, bool forceRefresh, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            await EnsureParcelMapAsync(client, sim, forceRefresh, token).ConfigureAwait(false);

            int resolvedLocalId;
            if (localId.HasValue)
            {
                if (localId.Value <= 0)
                {
                    return DataToolResult.FailResult("localId must be greater than 0 when provided.");
                }

                resolvedLocalId = localId.Value;
            }
            else
            {
                resolvedLocalId = client.Parcels.GetParcelLocalID(sim, client.Self.SimPosition);
                if (resolvedLocalId <= 0)
                {
                    return DataToolResult.FailResult("Unable to resolve current parcel local ID from simulator parcel map.");
                }
            }

            var parcel = await GetParcelAsync(client, sim, resolvedLocalId, refreshFromSimulator: true, token).ConfigureAwait(false);
            if (parcel == null)
            {
                return DataToolResult.FailResult($"Parcel localId={resolvedLocalId} was not found.");
            }

            var agentId = client.Self.AgentID;
            var activeGroupId = client.Self.ActiveGroup;

            var isParcelOwner = parcel.OwnerID == agentId;
            var isGroupOwnedByActiveGroup =
                parcel.IsGroupOwned
                && parcel.GroupID != UUID.Zero
                && parcel.GroupID == activeGroupId;
            var activeGroupMatchesParcelGroup =
                parcel.GroupID != UUID.Zero
                && parcel.GroupID == activeGroupId;

            var warnings = new List<string>();
            if (!isParcelOwner && !activeGroupMatchesParcelGroup)
            {
                warnings.Add("Agent is neither parcel owner nor currently using the parcel group; parcel update/join/subdivide calls are likely to be denied unless estate privileges apply.");
            }

            if (!parcel.Flags.HasFlag(ParcelFlags.AllowTerraform))
            {
                warnings.Add("Parcel has AllowTerraform disabled; terrain edits may be denied for non-privileged users.");
            }

            if (parcel.Flags.HasFlag(ParcelFlags.UseAccessGroup) && parcel.GroupID == UUID.Zero)
            {
                warnings.Add("Parcel access is group-restricted but parcel group ID is zero; verify simulator-side parcel configuration.");
            }

            var payload = new
            {
                simulator = sim.Name,
                localId = parcel.LocalID,
                agentId = agentId.ToString(),
                activeGroupId = activeGroupId.ToString(),
                ownerId = parcel.OwnerID.ToString(),
                groupId = parcel.GroupID.ToString(),
                isGroupOwned = parcel.IsGroupOwned,
                roleHints = new
                {
                    isParcelOwner,
                    activeGroupMatchesParcelGroup,
                    isGroupOwnedByActiveGroup
                },
                parcelFlags = parcel.Flags.ToString(),
                featureHints = new
                {
                    allowTerraform = parcel.Flags.HasFlag(ParcelFlags.AllowTerraform),
                    useAccessGroup = parcel.Flags.HasFlag(ParcelFlags.UseAccessGroup),
                    useAccessList = parcel.Flags.HasFlag(ParcelFlags.UseAccessList),
                    useBanList = parcel.Flags.HasFlag(ParcelFlags.UseBanList),
                    allowDeedToGroup = parcel.Flags.HasFlag(ParcelFlags.AllowDeedToGroup),
                    createObjects = parcel.Flags.HasFlag(ParcelFlags.CreateObjects),
                    createGroupObjects = parcel.Flags.HasFlag(ParcelFlags.CreateGroupObjects)
                },
                warnings
            };

            var message = warnings.Count == 0
                ? "Collected parcel permission diagnostics with no obvious local-state blockers."
                : $"Collected parcel permission diagnostics with {warnings.Count} warning(s).";

            return DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> TerrainHeightmapSampleAsync(int stepMeters, CancellationToken cancellationToken)
    {
        if (stepMeters < 1 || stepMeters > 64)
        {
            return DataToolResult.FailResult("stepMeters must be between 1 and 64.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(DataToolResult.FailResult("No current simulator available."));
            }

            EnsureTerrainStreamingEnabled(client);
            if (!IsTerrainCacheAllocated(sim))
            {
                return Task.FromResult(DataToolResult.FailResult(
                    "Terrain cache is not initialized for this simulator session. StoreLandPatches must be enabled before login/sim connect; relog the bot (or reconnect/teleport to a fresh sim session) and retry."));
            }

            var step = Math.Clamp(stepMeters, 1, 64);
            var samples = new List<TerrainHeightSample>();
            var missing = 0;
            var failedLookups = 0;
            for (var y = 0; y <= 255; y += step)
            {
                for (var x = 0; x <= 255; x += step)
                {
                    if (TryGetTerrainHeightSafe(sim, x, y, out var height, out var faulted))
                    {
                        samples.Add(new TerrainHeightSample(x, y, height));
                    }
                    else
                    {
                        missing++;
                        if (faulted)
                        {
                            failedLookups++;
                        }
                    }
                }
            }

            if (samples.Count == 0)
            {
                return Task.FromResult(DataToolResult.FailResult(
                    "No terrain samples were available from simulator cache. Terrain streaming may not be populated yet for this session."));
            }

            var payload = new
            {
                simulator = sim.Name,
                stepMeters = step,
                sampleCount = samples.Count,
                missingCount = missing,
                failedLookups,
                samples
            };

            var message = missing == 0
                ? $"Collected {samples.Count} terrain height samples."
                : $"Collected {samples.Count} terrain height samples ({missing} points missing; terrain patch cache may be incomplete).";

            return Task.FromResult(DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> TerrainHeightmapExportRawAsync(string? outputPath, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, cancellation) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(DataToolResult.FailResult("No current simulator available."));
            }

            EnsureTerrainStreamingEnabled(client);
            if (!IsTerrainCacheAllocated(sim))
            {
                return Task.FromResult(DataToolResult.FailResult(
                    "Terrain cache is not initialized for this simulator session. StoreLandPatches must be enabled before login/sim connect; relog the bot (or reconnect/teleport to a fresh sim session) and retry."));
            }

            var width = 256;
            var height = 256;
            var bytes = new byte[width * height * sizeof(float)];
            var cursor = 0;
            var missing = 0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!TryGetTerrainHeightSafe(sim, x, y, out var sampleHeight, out _))
                    {
                        // Keep export deterministic; unresolved points are encoded as 0.0f.
                        sampleHeight = 0f;
                        missing++;
                    }

                    var encoded = BitConverter.GetBytes(sampleHeight);
                    Buffer.BlockCopy(encoded, 0, bytes, cursor, sizeof(float));
                    cursor += sizeof(float);
                }
            }

            var total = width * height;
            var coverage = 1f - (missing / (float)total);
            if (missing == total)
            {
                return Task.FromResult(DataToolResult.FailResult(
                    "Terrain cache is empty in this session (0% coverage). RAW export from 'current' would be all zeros; use a known .r32 source file/URL instead or relog/reseed terrain cache."));
            }

            var resolvedPath = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(Path.GetTempPath(), $"terrain-{sim.Name}-{DateTime.UtcNow:yyyyMMddHHmmss}.r32")
                : Path.GetFullPath(outputPath.Trim());

            var directory = Path.GetDirectoryName(resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(resolvedPath, bytes);

            var payload = new
            {
                simulator = sim.Name,
                width,
                height,
                bytes = bytes.Length,
                coverage,
                missingCount = missing,
                filePath = resolvedPath
            };

            var message = missing == 0
                ? $"Exported RAW terrain heightmap to {resolvedPath}."
                : $"Exported RAW terrain heightmap to {resolvedPath} with {missing} unresolved sample(s) encoded as 0.0f (coverage={coverage:P1}).";

            return Task.FromResult(DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TerrainHeightmapImportRawAsync(string source, string? fileNameHint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return BotToolResult.Fail("source is required (file path or URL).");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            var data = await ReadBinarySourceAsync(source, token).ConfigureAwait(false);
            if (data.Length == 0)
            {
                return BotToolResult.Fail("Resolved source bytes are empty.");
            }

            var expectedBytes = 256 * 256 * sizeof(float);
            if (data.Length != expectedBytes)
            {
                return BotToolResult.Fail($"Terrain RAW payload must be exactly {expectedBytes} bytes (256x256 float32), but got {data.Length} bytes.");
            }

            var uploadName = string.IsNullOrWhiteSpace(fileNameHint)
                ? $"terrain-{DateTime.UtcNow:yyyyMMddHHmmss}.r32"
                : fileNameHint.Trim();
            if (!uploadName.EndsWith(".r32", StringComparison.OrdinalIgnoreCase))
            {
                uploadName += ".r32";
            }

            var transactionId = client.Estate.UploadTerrain(data, uploadName);
            return BotToolResult.OkResult($"Terrain upload requested (transactionId={transactionId}, bytes={data.Length}, fileName={uploadName}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> TerrainPatchCacheVerifyAsync(
        int stepMeters,
        float minimumCoverageRatio,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (stepMeters < 1 || stepMeters > 64)
        {
            return DataToolResult.FailResult("stepMeters must be between 1 and 64.");
        }

        if (minimumCoverageRatio <= 0f || minimumCoverageRatio > 1f)
        {
            return DataToolResult.FailResult("minimumCoverageRatio must be > 0 and <= 1.");
        }

        if (timeoutSeconds < 1 || timeoutSeconds > 120)
        {
            return DataToolResult.FailResult("timeoutSeconds must be between 1 and 120.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return DataToolResult.FailResult("No current simulator available.");
            }

            EnsureTerrainStreamingEnabled(client);
            if (!IsTerrainCacheAllocated(sim))
            {
                return DataToolResult.FailResult(
                    "Terrain cache is not initialized for this simulator session. StoreLandPatches must be enabled before login/sim connect; relog the bot (or reconnect/teleport to a fresh sim session) and retry.");
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
            var attempt = 0;
            int samples = 0;
            int missing = 0;
            int faulted = 0;
            float coverage = 0f;

            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                attempt++;

                samples = 0;
                missing = 0;
                faulted = 0;
                for (var y = 0; y <= 255; y += stepMeters)
                {
                    for (var x = 0; x <= 255; x += stepMeters)
                    {
                        if (TryGetTerrainHeightSafe(sim, x, y, out _, out var thisFaulted))
                        {
                            samples++;
                        }
                        else
                        {
                            missing++;
                            if (thisFaulted)
                            {
                                faulted++;
                            }
                        }
                    }
                }

                var total = samples + missing;
                coverage = total == 0 ? 0f : samples / (float)total;
                if (coverage >= minimumCoverageRatio)
                {
                    var okPayload = new
                    {
                        simulator = sim.Name,
                        stepMeters,
                        attempt,
                        coverage,
                        minimumCoverageRatio,
                        samples,
                        missing,
                        faulted
                    };

                    return DataToolResult.OkResult(
                        $"Terrain cache verification passed (coverage={coverage:P1}, attempts={attempt}).",
                        JsonSerializer.Serialize(okPayload, JsonOptions));
                }

                await Task.Delay(500, token).ConfigureAwait(false);
            }

            var payload = new
            {
                simulator = sim.Name,
                stepMeters,
                attempt,
                coverage,
                minimumCoverageRatio,
                samples,
                missing,
                faulted
            };

            return DataToolResult.FailResult(
                $"Terrain cache verification timed out (coverage={coverage:P1}, required={minimumCoverageRatio:P1}). payload={JsonSerializer.Serialize(payload, JsonOptions)}");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> TerrainPatchDiffRawAsync(
        string sourceA,
        string sourceB,
        float minDeltaMeters,
        int maxSamples,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceA) || string.IsNullOrWhiteSpace(sourceB))
        {
            return DataToolResult.FailResult("sourceA and sourceB are required (file path, URL, or 'current').");
        }

        if (minDeltaMeters < 0f)
        {
            return DataToolResult.FailResult("minDeltaMeters must be >= 0.");
        }

        if (maxSamples < 0 || maxSamples > 1000)
        {
            return DataToolResult.FailResult("maxSamples must be between 0 and 1000.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var left = await ResolveTerrainGridSourceAsync(client, sourceA, token).ConfigureAwait(false);
            if (!left.Ok || left.Heights == null)
            {
                return DataToolResult.FailResult($"Unable to resolve sourceA: {left.Error}");
            }

            var right = await ResolveTerrainGridSourceAsync(client, sourceB, token).ConfigureAwait(false);
            if (!right.Ok || right.Heights == null)
            {
                return DataToolResult.FailResult($"Unable to resolve sourceB: {right.Error}");
            }

            SummarizeTerrainDiff(left.Heights, right.Heights, minDeltaMeters, maxSamples,
                out var changedPoints, out var maxAbsDelta, out var avgAbsDelta, out var samples);

            var payload = new
            {
                sourceA,
                sourceB,
                minDeltaMeters,
                changedPoints,
                maxAbsDelta,
                avgAbsDelta,
                sourceAStats = new { left.Missing, left.Faulted },
                sourceBStats = new { right.Missing, right.Faulted },
                samples
            };

            return DataToolResult.OkResult(
                $"Terrain diff complete: changedPoints={changedPoints}, maxAbsDelta={maxAbsDelta:F3}m, avgAbsDelta={avgAbsDelta:F3}m.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TerrainPatchApplyOffsetAsync(
        float west,
        float south,
        float east,
        float north,
        float deltaMeters,
        float? minHeight,
        float? maxHeight,
        string? fileNameHint,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeParcelBounds(west, south, east, north, out var bounds, out var boundsError))
        {
            return BotToolResult.Fail(boundsError);
        }

        if (!float.IsFinite(deltaMeters))
        {
            return BotToolResult.Fail("deltaMeters must be a finite number.");
        }

        if (minHeight.HasValue && maxHeight.HasValue && minHeight.Value > maxHeight.Value)
        {
            return BotToolResult.Fail("minHeight must be <= maxHeight.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return Task.FromResult(BotToolResult.Fail("No current simulator available."));
            }

            EnsureTerrainStreamingEnabled(client);

            if (!TryCaptureTerrainGrid(sim, out var heights, out var missing, out var faulted))
            {
                return Task.FromResult(BotToolResult.Fail("Unable to capture terrain grid from cache for patch apply."));
            }

            var total = heights.Length;
            var coverage = 1f - (missing / (float)total);
            if (coverage < MinimumTerrainCoverageForMutation)
            {
                return Task.FromResult(BotToolResult.Fail(
                    $"Terrain cache coverage is too low for safe patch apply (coverage={coverage:P2}, required>={MinimumTerrainCoverageForMutation:P2}). Use terrain_patch_apply_offset_raw with a known .r32 source, or relog/reseed terrain cache first."));
            }

            var minX = Math.Clamp((int)MathF.Floor(bounds.West), 0, 255);
            var minY = Math.Clamp((int)MathF.Floor(bounds.South), 0, 255);
            var maxX = Math.Clamp((int)MathF.Ceiling(bounds.East) - 1, 0, 255);
            var maxY = Math.Clamp((int)MathF.Ceiling(bounds.North) - 1, 0, 255);

            if (maxX < minX || maxY < minY)
            {
                return Task.FromResult(BotToolResult.Fail("Normalized bounds resolved to an empty terrain patch."));
            }

            var changedPoints = 0;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var index = (y * 256) + x;
                    var value = heights[index] + deltaMeters;
                    if (minHeight.HasValue)
                    {
                        value = MathF.Max(value, minHeight.Value);
                    }

                    if (maxHeight.HasValue)
                    {
                        value = MathF.Min(value, maxHeight.Value);
                    }

                    heights[index] = value;
                    changedPoints++;
                }
            }

            var raw = EncodeTerrainHeightsToRaw(heights);
            var uploadName = string.IsNullOrWhiteSpace(fileNameHint)
                ? $"terrain-patch-offset-{DateTime.UtcNow:yyyyMMddHHmmss}.r32"
                : fileNameHint.Trim();
            if (!uploadName.EndsWith(".r32", StringComparison.OrdinalIgnoreCase))
            {
                uploadName += ".r32";
            }

            var transactionId = client.Estate.UploadTerrain(raw, uploadName);

            return Task.FromResult(BotToolResult.OkResult(
                $"Terrain patch offset upload requested (transactionId={transactionId}, changedPoints={changedPoints}, bounds=<{bounds.West:F2},{bounds.South:F2}>..<{bounds.East:F2},{bounds.North:F2}>, delta={deltaMeters:F3}m, coverage={coverage:P2}, sourceMissing={missing}, sourceFaulted={faulted})."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TerrainPatchApplyOffsetRawAsync(
        string source,
        float west,
        float south,
        float east,
        float north,
        float deltaMeters,
        float? minHeight,
        float? maxHeight,
        string? fileNameHint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return BotToolResult.Fail("source is required (file path, URL, or 'current').");
        }

        if (!TryNormalizeParcelBounds(west, south, east, north, out var bounds, out var boundsError))
        {
            return BotToolResult.Fail(boundsError);
        }

        if (!float.IsFinite(deltaMeters))
        {
            return BotToolResult.Fail("deltaMeters must be a finite number.");
        }

        if (minHeight.HasValue && maxHeight.HasValue && minHeight.Value > maxHeight.Value)
        {
            return BotToolResult.Fail("minHeight must be <= maxHeight.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            var baseTerrain = await ResolveTerrainGridSourceAsync(client, source, token).ConfigureAwait(false);
            if (!baseTerrain.Ok || baseTerrain.Heights == null)
            {
                return BotToolResult.Fail($"Unable to resolve terrain source: {baseTerrain.Error}");
            }

            var total = baseTerrain.Heights.Length;
            var coverage = 1f - (baseTerrain.Missing / (float)total);
            if (string.Equals(source.Trim(), "current", StringComparison.OrdinalIgnoreCase) && coverage < MinimumTerrainCoverageForMutation)
            {
                return BotToolResult.Fail(
                    $"Terrain cache coverage is too low for safe patch apply from 'current' (coverage={coverage:P2}, required>={MinimumTerrainCoverageForMutation:P2}). Provide a known .r32 source file/URL instead.");
            }

            var minX = Math.Clamp((int)MathF.Floor(bounds.West), 0, 255);
            var minY = Math.Clamp((int)MathF.Floor(bounds.South), 0, 255);
            var maxX = Math.Clamp((int)MathF.Ceiling(bounds.East) - 1, 0, 255);
            var maxY = Math.Clamp((int)MathF.Ceiling(bounds.North) - 1, 0, 255);

            if (maxX < minX || maxY < minY)
            {
                return BotToolResult.Fail("Normalized bounds resolved to an empty terrain patch.");
            }

            var changedPoints = 0;
            var heights = baseTerrain.Heights;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var index = (y * 256) + x;
                    var value = heights[index] + deltaMeters;
                    if (minHeight.HasValue)
                    {
                        value = MathF.Max(value, minHeight.Value);
                    }

                    if (maxHeight.HasValue)
                    {
                        value = MathF.Min(value, maxHeight.Value);
                    }

                    heights[index] = value;
                    changedPoints++;
                }
            }

            var raw = EncodeTerrainHeightsToRaw(heights);
            var uploadName = string.IsNullOrWhiteSpace(fileNameHint)
                ? $"terrain-patch-offset-{DateTime.UtcNow:yyyyMMddHHmmss}.r32"
                : fileNameHint.Trim();
            if (!uploadName.EndsWith(".r32", StringComparison.OrdinalIgnoreCase))
            {
                uploadName += ".r32";
            }

            var transactionId = client.Estate.UploadTerrain(raw, uploadName);
            return BotToolResult.OkResult(
                $"Terrain patch offset upload requested (transactionId={transactionId}, changedPoints={changedPoints}, bounds=<{bounds.West:F2},{bounds.South:F2}>..<{bounds.East:F2},{bounds.North:F2}>, delta={deltaMeters:F3}m, source={source}, coverage={coverage:P2}, sourceMissing={baseTerrain.Missing}, sourceFaulted={baseTerrain.Faulted}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> TerrainTerraformAsync(
        int? localId,
        float? west,
        float? south,
        float? east,
        float? north,
        string action,
        string brushSize,
        int seconds,
        CancellationToken cancellationToken)
    {
        if (!TryParseTerraformAction(action, out var terraformAction, out var actionError))
        {
            return BotToolResult.Fail(actionError);
        }

        if (!TryParseTerraformBrushSize(brushSize, out var terraformBrushSize, out var brushError))
        {
            return BotToolResult.Fail(brushError);
        }

        var clampedSeconds = Math.Clamp(seconds, 1, 120);

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return BotToolResult.Fail("No current simulator available.");
            }

            EnsureTerrainStreamingEnabled(client);

            int targetLocalId;
            float westBound;
            float southBound;
            float eastBound;
            float northBound;
            string targetDescription;

            if (localId.HasValue && localId.Value > 0)
            {
                var parcel = await GetParcelAsync(client, sim, localId.Value, refreshFromSimulator: true, token).ConfigureAwait(false);
                if (parcel == null)
                {
                    return BotToolResult.Fail($"Parcel localId={localId.Value} was not found.");
                }

                targetLocalId = localId.Value;
                westBound = Math.Clamp(parcel.AABBMin.X, 0f, 256f);
                southBound = Math.Clamp(parcel.AABBMin.Y, 0f, 256f);
                eastBound = Math.Clamp(parcel.AABBMax.X, 0f, 256f);
                northBound = Math.Clamp(parcel.AABBMax.Y, 0f, 256f);
                targetDescription = $"parcel localId={localId.Value}";
            }
            else
            {
                if (!west.HasValue || !south.HasValue || !east.HasValue || !north.HasValue)
                {
                    return BotToolResult.Fail(
                        "For area terraforming, localId must be provided or west/south/east/north must all be set.");
                }

                if (!TryNormalizeParcelBounds(west.Value, south.Value, east.Value, north.Value, out var normalizedBounds, out var boundsError))
                {
                    return BotToolResult.Fail(boundsError);
                }

                targetLocalId = -1;
                westBound = normalizedBounds.West;
                southBound = normalizedBounds.South;
                eastBound = normalizedBounds.East;
                northBound = normalizedBounds.North;
                targetDescription =
                    $"bbox<{westBound:F2},{southBound:F2}>..<{eastBound:F2},{northBound:F2}>";
            }

            var centerX = (westBound + eastBound) * 0.5f;
            var centerY = (southBound + northBound) * 0.5f;
            var referenceHeight = client.Self.SimPosition.Z;
            var heightSource = "agent-z-fallback";
            if (TryGetTerrainHeightSafe(sim, (int)MathF.Round(centerX), (int)MathF.Round(centerY), out var sampledHeight, out _))
            {
                referenceHeight = sampledHeight;
                heightSource = "terrain-cache";
            }

            try
            {
                client.Parcels.Terraform(
                    sim,
                    targetLocalId,
                    westBound,
                    southBound,
                    eastBound,
                    northBound,
                    terraformAction,
                    terraformBrushSize,
                    clampedSeconds,
                    referenceHeight);
            }
            catch (IndexOutOfRangeException)
            {
                return BotToolResult.Fail(
                    "Terraform failed due to incomplete terrain cache indexing in this simulator session. Try TerrainHeightmapSample first, then retry, or relog bot session.");
            }

            return BotToolResult.OkResult(
                $"Terraform request sent for {targetDescription}: action={terraformAction}, brush={terraformBrushSize}, seconds={clampedSeconds}, referenceHeight={referenceHeight:F2} ({heightSource}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> EstateGetInfoAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var reply = await WaitForEstateUpdateInfoReplyAsync(client, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult("Timed out waiting for estate info reply.");
            }

            var payload = new
            {
                estateName = reply.EstateName,
                estateOwner = reply.EstateOwner.ToString(),
                estateId = reply.EstateID,
                flags = reply.Flags.ToString(),
                sunHour = reply.SunHour
            };

            return DataToolResult.OkResult("Retrieved estate info.", JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> EstateGetCovenantAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var reply = await WaitForEstateCovenantReplyAsync(client, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult("Timed out waiting for estate covenant reply.");
            }

            var payload = new
            {
                covenantId = reply.CovenantID.ToString(),
                estateName = reply.EstateName,
                estateOwnerId = reply.EstateOwnerID.ToString(),
                timestamp = reply.Timestamp
            };

            return DataToolResult.OkResult("Retrieved estate covenant metadata.", JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> EstateRestartRegionAsync(int delaySeconds, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            client.Estate.RestartRegion(delaySeconds);
            return Task.FromResult(BotToolResult.OkResult($"Region restart requested with delay={delaySeconds}s (sim clamps to 30..240)."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> EstateCancelRestartAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            client.Estate.CancelRestart();
            return Task.FromResult(BotToolResult.OkResult("Region restart cancellation requested."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> EstateBroadcastMessageAsync(string message, bool estateWide, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            if (estateWide)
            {
                client.Estate.EstateMessage(message);
            }
            else
            {
                client.Estate.SimulatorMessage(message);
            }

            return Task.FromResult(BotToolResult.OkResult(
                estateWide
                    ? "Estate-wide message request sent."
                    : "Region-wide message request sent."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> EstateRestartScheduleGetAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var schedule = await client.Estate.GetRegionRestartScheduleAsync(token).ConfigureAwait(false);
            if (schedule == null)
            {
                return DataToolResult.FailResult("No restart schedule found or capability unavailable.");
            }

            var payload = new
            {
                isDaily = schedule.IsDaily,
                days = schedule.Days.ToString(),
                timeUtc = schedule.Time.ToString("c")
            };

            return DataToolResult.OkResult("Retrieved region restart schedule.", JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> EstateRestartScheduleSetAsync(string mode, string? daysCsv, string timeUtc, CancellationToken cancellationToken)
    {
        if (!TryParseScheduleMode(mode, out var normalizedMode, out var modeError))
        {
            return BotToolResult.Fail(modeError);
        }

        if (!TimeSpan.TryParse(timeUtc, out var time))
        {
            return BotToolResult.Fail("timeUtc must be a valid TimeSpan string (for example '03:30:00' or '03:30').");
        }

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            return BotToolResult.Fail("timeUtc must be within 00:00:00 and 23:59:59.");
        }

        RegionRestartSchedule schedule;
        if (normalizedMode == "daily")
        {
            schedule = new RegionRestartSchedule
            {
                IsDaily = true,
                Days = RegionRestartDays.All,
                Time = time
            };
        }
        else
        {
            var parsedDays = ParseRestartDays(daysCsv);
            schedule = new RegionRestartSchedule
            {
                IsDaily = false,
                Days = normalizedMode == "off" ? RegionRestartDays.None : parsedDays,
                Time = time
            };

            if (normalizedMode == "weekly" && parsedDays == RegionRestartDays.None)
            {
                return BotToolResult.Fail("daysCsv is required for weekly mode (for example 'mon,wed,fri').");
            }
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Estate.SetRegionRestartScheduleAsync(schedule, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Failed to set region restart schedule (capability unavailable or permission denied).");
            }

            return BotToolResult.OkResult(
                normalizedMode == "off"
                    ? "Region restart schedule clear request submitted."
                    : $"Region restart schedule updated (mode={normalizedMode}, days={schedule.Days}, timeUtc={schedule.Time:c}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DataToolResult> ParcelGetByLocalIdCoreAsync(
        GridClient client,
        Simulator sim,
        int localId,
        bool includeAccessLists,
        CancellationToken cancellationToken)
    {
        var parcel = await GetParcelAsync(client, sim, localId, refreshFromSimulator: true, cancellationToken).ConfigureAwait(false);
        if (parcel == null)
        {
            return DataToolResult.FailResult($"Parcel localId={localId} was not found.");
        }

        var allow = Array.Empty<ParcelAccessEntryInfo>();
        var ban = Array.Empty<ParcelAccessEntryInfo>();

        if (includeAccessLists)
        {
            var accessReply = await WaitForParcelAccessListReplyAsync(client, sim, localId, AccessList.Both, cancellationToken).ConfigureAwait(false);
            if (accessReply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for access list reply for parcel {localId}.");
            }

            var allowList = new List<ParcelAccessEntryInfo>();
            var banList = new List<ParcelAccessEntryInfo>();
            foreach (var entry in accessReply.AccessList)
            {
                var info = new ParcelAccessEntryInfo(
                    entry.AgentID.ToString(),
                    entry.Time.ToUniversalTime().ToString("O"),
                    entry.Flags.ToString());
                if (entry.Flags.HasFlag(AccessList.Access))
                {
                    allowList.Add(info);
                }

                if (entry.Flags.HasFlag(AccessList.Ban))
                {
                    banList.Add(info);
                }
            }

            allow = allowList.ToArray();
            ban = banList.ToArray();
        }

        var payload = new
        {
            simulator = sim.Name,
            localId = parcel.LocalID,
            name = parcel.Name,
            description = parcel.Desc,
            ownerId = parcel.OwnerID.ToString(),
            groupId = parcel.GroupID.ToString(),
            area = parcel.Area,
            salePrice = parcel.SalePrice,
            landingType = parcel.Landing.ToString(),
            userLocation = new { x = parcel.UserLocation.X, y = parcel.UserLocation.Y, z = parcel.UserLocation.Z },
            userLookAt = new { x = parcel.UserLookAt.X, y = parcel.UserLookAt.Y, z = parcel.UserLookAt.Z },
            musicUrl = parcel.MusicURL,
            mediaUrl = parcel.Media.MediaURL,
            flags = parcel.Flags.ToString(),
            includeAccessLists,
            allowList = allow,
            banList = ban
        };

        return DataToolResult.OkResult(
            includeAccessLists
                ? $"Fetched parcel {parcel.LocalID} including access/ban list data."
                : $"Fetched parcel {parcel.LocalID}.",
            JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static async Task EnsureParcelMapAsync(GridClient client, Simulator sim, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh && sim.Parcels.Count > 0)
        {
            return;
        }

        await client.Parcels.RequestAllSimParcelsAsync(sim, refresh: forceRefresh, delay: TimeSpan.FromMilliseconds(80), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Parcel?> GetParcelAsync(
        GridClient client,
        Simulator sim,
        int localId,
        bool refreshFromSimulator,
        CancellationToken cancellationToken)
    {
        if (sim.Parcels.TryGetValue(localId, out var cached) && cached != null && !refreshFromSimulator)
        {
            return cached;
        }

        var reply = await WaitForParcelPropertiesReplyAsync(client, sim, localId, cancellationToken).ConfigureAwait(false);
        if (reply?.Parcel != null)
        {
            return reply.Parcel;
        }

        if (sim.Parcels.TryGetValue(localId, out cached))
        {
            return cached;
        }

        return null;
    }

    private static async Task<ParcelPropertiesEventArgs?> WaitForParcelPropertiesReplyAsync(
        GridClient client,
        Simulator sim,
        int localId,
        CancellationToken cancellationToken)
    {
        var sequenceId = Random.Shared.Next(1, int.MaxValue);
        var tcs = new TaskCompletionSource<ParcelPropertiesEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, ParcelPropertiesEventArgs e)
        {
            if (ReferenceEquals(e.Simulator, sim)
                && e.Parcel != null
                && e.Parcel.LocalID == localId
                && e.SequenceID == sequenceId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Parcels.ParcelProperties += Handler;
        try
        {
            client.Parcels.RequestParcelProperties(sim, localId, sequenceId);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Parcels.ParcelProperties -= Handler;
        }
    }

    private static async Task<ParcelAccessListReplyEventArgs?> WaitForParcelAccessListReplyAsync(
        GridClient client,
        Simulator sim,
        int localId,
        AccessList flags,
        CancellationToken cancellationToken)
    {
        var sequenceId = Random.Shared.Next(1, int.MaxValue);
        var tcs = new TaskCompletionSource<ParcelAccessListReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, ParcelAccessListReplyEventArgs e)
        {
            if (ReferenceEquals(e.Simulator, sim) && e.LocalID == localId && e.SequenceID == sequenceId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Parcels.ParcelAccessListReply += Handler;
        try
        {
            client.Parcels.RequestParcelAccessList(sim, localId, flags, sequenceId);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Parcels.ParcelAccessListReply -= Handler;
        }
    }

    private static async Task<EstateUpdateInfoReplyEventArgs?> WaitForEstateUpdateInfoReplyAsync(GridClient client, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<EstateUpdateInfoReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, EstateUpdateInfoReplyEventArgs e)
        {
            tcs.TrySetResult(e);
        }

        client.Estate.EstateUpdateInfoReply += Handler;
        try
        {
            client.Estate.RequestInfo();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Estate.EstateUpdateInfoReply -= Handler;
        }
    }

    private static async Task<EstateCovenantReplyEventArgs?> WaitForEstateCovenantReplyAsync(GridClient client, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<EstateCovenantReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, EstateCovenantReplyEventArgs e)
        {
            tcs.TrySetResult(e);
        }

        client.Estate.EstateCovenantReply += Handler;
        try
        {
            client.Estate.RequestCovenant();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                return null;
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            client.Estate.EstateCovenantReply -= Handler;
        }
    }

    private static bool TryParseAccessListScope(string value, out AccessList scope, out string error)
    {
        scope = AccessList.Both;
        error = string.Empty;

        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "":
            case "both":
                scope = AccessList.Both;
                return true;
            case "access":
            case "allow":
            case "allowlist":
            case "whitelist":
                scope = AccessList.Access;
                return true;
            case "ban":
            case "banlist":
            case "blacklist":
                scope = AccessList.Ban;
                return true;
            default:
                error = "listType must be one of: both, access, ban.";
                return false;
        }
    }

    private async Task<(bool Ok, float[]? Heights, string Error, int Missing, int Faulted)> ResolveTerrainGridSourceAsync(
        GridClient client,
        string source,
        CancellationToken cancellationToken)
    {
        var normalized = source.Trim();
        if (string.Equals(normalized, "current", StringComparison.OrdinalIgnoreCase))
        {
            var sim = client.Network.CurrentSim;
            if (sim == null)
            {
                return (false, null, "No current simulator available.", 0, 0);
            }

            EnsureTerrainStreamingEnabled(client);
            if (!TryCaptureTerrainGrid(sim, out var heights, out var missing, out var faulted))
            {
                return (false, null, "Failed to capture current terrain grid from cache.", missing, faulted);
            }

            return (true, heights, string.Empty, missing, faulted);
        }

        var data = await ReadBinarySourceAsync(source, cancellationToken).ConfigureAwait(false);
        if (!TryDecodeRawTerrainHeights(data, out var decoded, out var error))
        {
            return (false, null, error, 0, 0);
        }

        return (true, decoded, string.Empty, 0, 0);
    }

    private static void EnsureTerrainStreamingEnabled(GridClient client)
    {
        try
        {
            if (!client.Settings.World.StoreLandPatches)
            {
                client.Settings.World.StoreLandPatches = true;
            }
        }
        catch
        {
            // Best effort only; terrain operations still provide local fallbacks.
        }
    }

    private static bool TryCaptureTerrainGrid(Simulator sim, out float[] heights, out int missingCount, out int faultedCount)
    {
        heights = new float[256 * 256];
        if (!IsTerrainCacheAllocated(sim))
        {
            missingCount = heights.Length;
            faultedCount = heights.Length;
            return false;
        }

        missingCount = 0;
        faultedCount = 0;
        var captured = 0;

        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var index = (y * 256) + x;
                if (TryGetTerrainHeightSafe(sim, x, y, out var value, out var faulted))
                {
                    heights[index] = value;
                    captured++;
                }
                else
                {
                    heights[index] = 0f;
                    missingCount++;
                    if (faulted)
                    {
                        faultedCount++;
                    }
                }
            }
        }

        return captured > 0;
    }

    private static bool TryDecodeRawTerrainHeights(byte[] data, out float[] heights, out string error)
    {
        heights = Array.Empty<float>();
        error = string.Empty;

        var expectedBytes = 256 * 256 * sizeof(float);
        if (data.Length != expectedBytes)
        {
            error = $"Terrain RAW payload must be exactly {expectedBytes} bytes (256x256 float32), but got {data.Length} bytes.";
            return false;
        }

        heights = new float[256 * 256];
        Buffer.BlockCopy(data, 0, heights, 0, expectedBytes);
        return true;
    }

    private static byte[] EncodeTerrainHeightsToRaw(float[] heights)
    {
        var bytes = new byte[heights.Length * sizeof(float)];
        Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void SummarizeTerrainDiff(
        float[] sourceA,
        float[] sourceB,
        float minDeltaMeters,
        int maxSamples,
        out int changedPoints,
        out float maxAbsDelta,
        out float avgAbsDelta,
        out List<TerrainPatchDeltaSample> samples)
    {
        changedPoints = 0;
        maxAbsDelta = 0f;
        avgAbsDelta = 0f;
        samples = new List<TerrainPatchDeltaSample>(Math.Max(0, maxSamples));

        var totalAbs = 0d;
        for (var i = 0; i < sourceA.Length && i < sourceB.Length; i++)
        {
            var delta = sourceB[i] - sourceA[i];
            var abs = MathF.Abs(delta);
            if (abs < minDeltaMeters)
            {
                continue;
            }

            changedPoints++;
            totalAbs += abs;
            if (abs > maxAbsDelta)
            {
                maxAbsDelta = abs;
            }

            if (samples.Count < maxSamples)
            {
                var x = i % 256;
                var y = i / 256;
                samples.Add(new TerrainPatchDeltaSample(x, y, sourceA[i], sourceB[i], delta));
            }
        }

        if (changedPoints > 0)
        {
            avgAbsDelta = (float)(totalAbs / changedPoints);
        }
    }

    private static bool TryGetTerrainHeightSafe(Simulator sim, int x, int y, out float height, out bool faulted)
    {
        height = 0f;
        faulted = false;

        if (!IsTerrainCacheAllocated(sim))
        {
            faulted = true;
            return false;
        }

        try
        {
            return sim.TerrainHeightAtPoint(x, y, out height);
        }
        catch (IndexOutOfRangeException)
        {
            faulted = true;
            return false;
        }
    }

    private static bool IsTerrainCacheAllocated(Simulator sim)
    {
        return sim.Terrain != null && sim.Terrain.Length > 0;
    }

    private static bool TryNormalizeParcelBounds(
        float west,
        float south,
        float east,
        float north,
        out ParcelBounds bounds,
        out string error)
    {
        bounds = default;
        error = string.Empty;

        if (!float.IsFinite(west) || !float.IsFinite(south) || !float.IsFinite(east) || !float.IsFinite(north))
        {
            error = "Parcel bounds must be finite numbers.";
            return false;
        }

        var normalizedWest = Math.Clamp(MathF.Min(west, east), 0f, 256f);
        var normalizedEast = Math.Clamp(MathF.Max(west, east), 0f, 256f);
        var normalizedSouth = Math.Clamp(MathF.Min(south, north), 0f, 256f);
        var normalizedNorth = Math.Clamp(MathF.Max(south, north), 0f, 256f);

        if (normalizedEast <= normalizedWest || normalizedNorth <= normalizedSouth)
        {
            error = "Parcel bounds must define a non-empty rectangle inside 0..256 region coordinates.";
            return false;
        }

        bounds = new ParcelBounds(normalizedWest, normalizedSouth, normalizedEast, normalizedNorth);
        return true;
    }

    private static bool TryParseLandingType(string value, out LandingType landingType, out string error)
    {
        landingType = LandingType.None;
        error = string.Empty;

        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = "landingType is required (none, landingpoint, direct).";
            return false;
        }

        if (Enum.TryParse<LandingType>(normalized, true, out landingType))
        {
            return true;
        }

        error = "landingType must be one of: none, landingpoint, direct.";
        return false;
    }

    private static bool TryParseTerraformAction(string value, out TerraformAction action, out string error)
    {
        action = TerraformAction.Level;
        error = string.Empty;

        var normalized = (value ?? string.Empty).Trim();
        if (Enum.TryParse<TerraformAction>(normalized, true, out action))
        {
            return true;
        }

        error = "action must be one of: level, raise, lower, smooth, noise, revert.";
        return false;
    }

    private static bool TryParseTerraformBrushSize(string value, out TerraformBrushSize brushSize, out string error)
    {
        brushSize = TerraformBrushSize.Medium;
        error = string.Empty;

        var normalized = (value ?? string.Empty).Trim();
        if (Enum.TryParse<TerraformBrushSize>(normalized, true, out brushSize))
        {
            return true;
        }

        error = "brushSize must be one of: small, medium, large.";
        return false;
    }

    private static bool TryParseScheduleMode(string value, out string mode, out string error)
    {
        mode = string.Empty;
        error = string.Empty;

        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "daily":
            case "weekly":
            case "off":
                mode = normalized;
                return true;
            default:
                error = "mode must be one of: daily, weekly, off.";
                return false;
        }
    }

    private static RegionRestartDays ParseRestartDays(string? daysCsv)
    {
        if (string.IsNullOrWhiteSpace(daysCsv))
        {
            return RegionRestartDays.None;
        }

        var result = RegionRestartDays.None;
        var tokens = daysCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "sun":
                case "sunday":
                    result |= RegionRestartDays.Sunday;
                    break;
                case "mon":
                case "monday":
                    result |= RegionRestartDays.Monday;
                    break;
                case "tue":
                case "tues":
                case "tuesday":
                    result |= RegionRestartDays.Tuesday;
                    break;
                case "wed":
                case "wednesday":
                    result |= RegionRestartDays.Wednesday;
                    break;
                case "thu":
                case "thur":
                case "thurs":
                case "thursday":
                    result |= RegionRestartDays.Thursday;
                    break;
                case "fri":
                case "friday":
                    result |= RegionRestartDays.Friday;
                    break;
                case "sat":
                case "saturday":
                    result |= RegionRestartDays.Saturday;
                    break;
            }
        }

        return result;
    }
}

internal sealed record DataToolResult(bool Ok, string Message, string? PayloadJson)
{
    public static DataToolResult OkResult(string message, string payloadJson) => new(true, message, payloadJson);
    public static DataToolResult FailResult(string message) => new(false, message, null);
}

internal sealed record ParcelAccessEntryInfo(string AgentId, string AddedAtUtc, string EntryType);

internal sealed record TerrainHeightSample(int X, int Y, float Height);

internal sealed record TerrainPatchDeltaSample(int X, int Y, float Before, float After, float Delta);

internal readonly record struct ParcelBounds(float West, float South, float East, float North);
