using System.Globalization;
using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<DataToolResult> AvatarProfileGetAsync(
        string avatarId,
        bool includeAgentProfileCapability,
        int waitForReplySeconds,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(avatarId, out var avatarUuid))
        {
            return DataToolResult.FailResult("avatarId must be a valid UUID.");
        }

        if (waitForReplySeconds < 1 || waitForReplySeconds > 30)
        {
            return DataToolResult.FailResult("waitForReplySeconds must be in range 1..30.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var timeout = TimeSpan.FromSeconds(waitForReplySeconds);
            var udpReplyTask = WaitForAvatarProfileUdpReplyAsync(client, avatarUuid, timeout, token);
            client.Avatars.RequestAvatarProperties(avatarUuid);
            var udpReply = await udpReplyTask.ConfigureAwait(false);

            var capAvailable = client.Avatars.AgentProfileAvailable();
            var capRequested = includeAgentProfileCapability && capAvailable;
            var capSuccess = false;
            object? capProfile = null;

            if (capRequested)
            {
                var capReply = await client.Avatars.RequestAgentProfileAsync(avatarUuid, token).ConfigureAwait(false);
                capSuccess = capReply.success && capReply.profile != null;
                capProfile = capReply.profile;
            }

            var hasAnyProfileData = udpReply.PropertiesReceived || udpReply.InterestsReceived || capSuccess;
            if (!hasAnyProfileData)
            {
                var detail = capRequested
                    ? " No AgentProfile capability payload was returned either."
                    : string.Empty;
                return DataToolResult.FailResult($"Timed out waiting for profile/interests replies for avatar {avatarUuid}.{detail}");
            }

            var payload = new
            {
                summary = new
                {
                    avatarId = avatarUuid.ToString(),
                    udpPropertiesReceived = udpReply.PropertiesReceived,
                    udpInterestsReceived = udpReply.InterestsReceived,
                    agentProfileCapability = new
                    {
                        requested = includeAgentProfileCapability,
                        available = capAvailable,
                        attempted = capRequested,
                        success = capSuccess
                    }
                },
                profile = udpReply.PropertiesReceived && udpReply.Properties.HasValue
                    ? ToAvatarProfilePayload(udpReply.Properties.Value)
                    : null,
                interests = udpReply.InterestsReceived && udpReply.Interests.HasValue
                    ? ToAvatarInterestsPayload(udpReply.Interests.Value)
                    : null,
                agentProfile = capProfile
            };

            var message = $"Fetched avatar profile data for {avatarUuid}. " +
                          $"UDP profile={(udpReply.PropertiesReceived ? "yes" : "no")}, interests={(udpReply.InterestsReceived ? "yes" : "no")}, capability={(capSuccess ? "yes" : "no")}.";
            return DataToolResult.OkResult(message, JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> AvatarPicksListAsync(
        string avatarId,
        bool includeDetails,
        int detailWaitSeconds,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(avatarId, out var avatarUuid))
        {
            return DataToolResult.FailResult("avatarId must be a valid UUID.");
        }

        if (detailWaitSeconds < 1 || detailWaitSeconds > 30)
        {
            return DataToolResult.FailResult("detailWaitSeconds must be in range 1..30.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var timeout = TimeSpan.FromSeconds(12);
            var picksReplyTask = WaitForAvatarPicksReplyAsync(client, avatarUuid, timeout, token);
            client.Avatars.RequestAvatarPicks(avatarUuid);
            var picksReply = await picksReplyTask.ConfigureAwait(false);
            if (picksReply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for picks reply for avatar {avatarUuid}.");
            }

            var picks = picksReply.Picks
                .OrderBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kvp => kvp.Key.ToString(), StringComparer.Ordinal)
                .ToList();

            var details = new Dictionary<UUID, ProfilePick>();
            var detailMisses = new List<string>();
            if (includeDetails)
            {
                foreach (var pick in picks)
                {
                    var detailTask = WaitForPickInfoReplyAsync(client, pick.Key, TimeSpan.FromSeconds(detailWaitSeconds), token);
                    client.Avatars.RequestPickInfo(avatarUuid, pick.Key);
                    var detail = await detailTask.ConfigureAwait(false);
                    if (detail == null)
                    {
                        detailMisses.Add(pick.Key.ToString());
                        continue;
                    }

                    details[pick.Key] = detail.Pick;
                }
            }

            var rows = picks
                .Select(pick =>
                {
                    if (includeDetails && details.TryGetValue(pick.Key, out var detail))
                    {
                        return (object)new
                        {
                            pickId = pick.Key.ToString(),
                            name = pick.Value,
                            detail = new
                            {
                                creatorId = detail.CreatorID.ToString(),
                                parcelId = detail.ParcelID.ToString(),
                                description = detail.Desc,
                                snapshotId = detail.SnapshotID.ToString(),
                                user = detail.User,
                                originalName = detail.OriginalName,
                                simName = detail.SimName,
                                sortOrder = detail.SortOrder,
                                topPick = detail.TopPick,
                                enabled = detail.Enabled,
                                globalPosition = new
                                {
                                    x = detail.PosGlobal.X,
                                    y = detail.PosGlobal.Y,
                                    z = detail.PosGlobal.Z
                                }
                            }
                        };
                    }

                    return (object)new
                    {
                        pickId = pick.Key.ToString(),
                        name = pick.Value
                    };
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    avatarId = avatarUuid.ToString(),
                    count = picks.Count,
                    includeDetails,
                    detailsResolved = details.Count,
                    detailsMissing = detailMisses.Count
                },
                missingDetailPickIds = detailMisses,
                picks = rows
            };

            return DataToolResult.OkResult(
                $"Retrieved {picks.Count} pick(s) for avatar {avatarUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> AvatarClassifiedsListAsync(
        string avatarId,
        bool includeDetails,
        int detailWaitSeconds,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(avatarId, out var avatarUuid))
        {
            return DataToolResult.FailResult("avatarId must be a valid UUID.");
        }

        if (detailWaitSeconds < 1 || detailWaitSeconds > 30)
        {
            return DataToolResult.FailResult("detailWaitSeconds must be in range 1..30.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var timeout = TimeSpan.FromSeconds(12);
            var classifiedsReplyTask = WaitForAvatarClassifiedReplyAsync(client, avatarUuid, timeout, token);
            client.Avatars.RequestAvatarClassified(avatarUuid);
            var classifiedsReply = await classifiedsReplyTask.ConfigureAwait(false);
            if (classifiedsReply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for classifieds reply for avatar {avatarUuid}.");
            }

            var classifieds = classifiedsReply.Classifieds
                .OrderBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(kvp => kvp.Key.ToString(), StringComparer.Ordinal)
                .ToList();

            var details = new Dictionary<UUID, ClassifiedAd>();
            var detailMisses = new List<string>();
            if (includeDetails)
            {
                foreach (var classified in classifieds)
                {
                    var detailTask = WaitForClassifiedInfoReplyAsync(client, classified.Key, TimeSpan.FromSeconds(detailWaitSeconds), token);
                    client.Avatars.RequestClassifiedInfo(classified.Key);
                    var detail = await detailTask.ConfigureAwait(false);
                    if (detail == null)
                    {
                        detailMisses.Add(classified.Key.ToString());
                        continue;
                    }

                    details[classified.Key] = detail.Classified;
                }
            }

            var rows = classifieds
                .Select(classified =>
                {
                    if (includeDetails && details.TryGetValue(classified.Key, out var detail))
                    {
                        return (object)new
                        {
                            classifiedId = classified.Key.ToString(),
                            name = classified.Value,
                            detail = new
                            {
                                creatorId = detail.CreatorID.ToString(),
                                category = detail.Category,
                                description = detail.Desc ?? string.Empty,
                                parcelId = detail.ParcelID.ToString(),
                                parcelName = detail.ParcelName ?? string.Empty,
                                parentEstate = detail.ParentEstate,
                                snapshotId = detail.SnapShotID.ToString(),
                                simName = detail.SimName ?? string.Empty,
                                classifiedFlags = detail.ClassifiedFlags,
                                price = detail.Price,
                                creationDateUnix = detail.CreationDate,
                                creationDateUtc = ToUnixTimeUtcIso(detail.CreationDate),
                                expirationDateUnix = detail.ExpirationDate,
                                expirationDateUtc = ToUnixTimeUtcIso(detail.ExpirationDate),
                                globalPosition = new
                                {
                                    x = detail.Position.X,
                                    y = detail.Position.Y,
                                    z = detail.Position.Z
                                }
                            }
                        };
                    }

                    return (object)new
                    {
                        classifiedId = classified.Key.ToString(),
                        name = classified.Value
                    };
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    avatarId = avatarUuid.ToString(),
                    count = classifieds.Count,
                    includeDetails,
                    detailsResolved = details.Count,
                    detailsMissing = detailMisses.Count
                },
                missingDetailClassifiedIds = detailMisses,
                classifieds = rows
            };

            return DataToolResult.OkResult(
                $"Retrieved {classifieds.Count} classified(s) for avatar {avatarUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static object ToAvatarProfilePayload(Avatar.AvatarProperties profile)
    {
        return new
        {
            firstLifeText = profile.FirstLifeText,
            firstLifeImageId = profile.FirstLifeImage.ToString(),
            partnerId = profile.Partner.ToString(),
            aboutText = profile.AboutText,
            bornOn = profile.BornOn,
            charterMember = profile.CharterMember,
            profileImageId = profile.ProfileImage.ToString(),
            profileUrl = profile.ProfileURL,
            flags = profile.Flags.ToString(),
            allowPublish = profile.AllowPublish,
            maturePublish = profile.MaturePublish,
            identified = profile.Identified,
            transacted = profile.Transacted,
            online = profile.Online
        };
    }

    private static object ToAvatarInterestsPayload(Avatar.Interests interests)
    {
        return new
        {
            languagesText = interests.LanguagesText,
            skillsMask = interests.SkillsMask,
            skillsText = interests.SkillsText,
            wantToMask = interests.WantToMask,
            wantToText = interests.WantToText
        };
    }

    private static string ToUnixTimeUtcIso(uint unixSeconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<AvatarProfileUdpReply> WaitForAvatarProfileUdpReplyAsync(
        GridClient client,
        UUID avatarId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var propertiesTcs = new TaskCompletionSource<Avatar.AvatarProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interestsTcs = new TaskCompletionSource<Avatar.Interests>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProperties(object? _, AvatarPropertiesReplyEventArgs e)
        {
            if (e.AvatarID == avatarId)
            {
                propertiesTcs.TrySetResult(e.Properties);
            }
        }

        void OnInterests(object? _, AvatarInterestsReplyEventArgs e)
        {
            if (e.AvatarID == avatarId)
            {
                interestsTcs.TrySetResult(e.Interests);
            }
        }

        client.Avatars.AvatarPropertiesReply += OnProperties;
        client.Avatars.AvatarInterestsReply += OnInterests;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var allTask = Task.WhenAll(propertiesTcs.Task, interestsTcs.Task);
            await Task.WhenAny(allTask, timeoutTask).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return new AvatarProfileUdpReply(
                propertiesTcs.Task.IsCompletedSuccessfully,
                propertiesTcs.Task.IsCompletedSuccessfully ? propertiesTcs.Task.Result : null,
                interestsTcs.Task.IsCompletedSuccessfully,
                interestsTcs.Task.IsCompletedSuccessfully ? interestsTcs.Task.Result : null);
        }
        finally
        {
            client.Avatars.AvatarPropertiesReply -= OnProperties;
            client.Avatars.AvatarInterestsReply -= OnInterests;
        }
    }

    private static async Task<AvatarPicksReplyEventArgs?> WaitForAvatarPicksReplyAsync(
        GridClient client,
        UUID avatarId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AvatarPicksReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, AvatarPicksReplyEventArgs e)
        {
            if (e.AvatarID == avatarId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Avatars.AvatarPicksReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Avatars.AvatarPicksReply -= Handler;
        }
    }

    private static async Task<PickInfoReplyEventArgs?> WaitForPickInfoReplyAsync(
        GridClient client,
        UUID pickId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PickInfoReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, PickInfoReplyEventArgs e)
        {
            if (e.PickID == pickId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Avatars.PickInfoReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Avatars.PickInfoReply -= Handler;
        }
    }

    private static async Task<AvatarClassifiedReplyEventArgs?> WaitForAvatarClassifiedReplyAsync(
        GridClient client,
        UUID avatarId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AvatarClassifiedReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, AvatarClassifiedReplyEventArgs e)
        {
            if (e.AvatarID == avatarId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Avatars.AvatarClassifiedReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Avatars.AvatarClassifiedReply -= Handler;
        }
    }

    private static async Task<ClassifiedInfoReplyEventArgs?> WaitForClassifiedInfoReplyAsync(
        GridClient client,
        UUID classifiedId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ClassifiedInfoReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, ClassifiedInfoReplyEventArgs e)
        {
            if (e.ClassifiedID == classifiedId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Avatars.ClassifiedInfoReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Avatars.ClassifiedInfoReply -= Handler;
        }
    }

    private sealed record AvatarProfileUdpReply(
        bool PropertiesReceived,
        Avatar.AvatarProperties? Properties,
        bool InterestsReceived,
        Avatar.Interests? Interests);
}
