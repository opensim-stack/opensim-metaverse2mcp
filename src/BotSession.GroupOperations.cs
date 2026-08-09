using System.Globalization;
using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<DataToolResult> GroupListCurrentAsync(bool includeDetails, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var reply = await WaitForCurrentGroupsReplyAsync(client, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult("Timed out waiting for current group list.");
            }

            var groups = reply.Groups.Values
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var groupRows = includeDetails
                ? groups.Select(g => (object)new
                {
                    groupId = g.ID.ToString(),
                    name = g.Name,
                    charter = g.Charter,
                    memberTitle = g.MemberTitle,
                    openEnrollment = g.OpenEnrollment,
                    showInList = g.ShowInList,
                    acceptNotices = g.AcceptNotices,
                    listInProfile = g.ListInProfile,
                    powers = g.Powers.ToString(),
                    contribution = g.Contribution,
                    membershipFee = g.MembershipFee,
                    members = g.GroupMembershipCount,
                    roles = g.GroupRolesCount,
                    founderId = g.FounderID.ToString(),
                    insigniaId = g.InsigniaID.ToString(),
                    ownerRoleId = g.OwnerRole.ToString(),
                    allowPublish = g.AllowPublish,
                    maturePublish = g.MaturePublish
                }).ToList()
                : groups.Select(g => (object)new
                {
                    groupId = g.ID.ToString(),
                    name = g.Name,
                    title = g.MemberTitle,
                    contribution = g.Contribution,
                    acceptNotices = g.AcceptNotices,
                    listInProfile = g.ListInProfile
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    total = groups.Count,
                    activeGroupId = client.Self.ActiveGroup.ToString(),
                    membershipLimit = client.Groups.GroupMembershipLimit,
                    canJoinMoreGroups = client.Groups.CanJoinMoreGroups,
                    includeDetails
                },
                groups = groupRows
            };

            return DataToolResult.OkResult(
                $"Retrieved {groups.Count} current group memberships.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupGetProfileAsync(string groupId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var reply = await WaitForGroupProfileReplyAsync(client, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for profile of group {groupUuid}.");
            }

            var g = reply.Group;
            var payload = new
            {
                group = new
                {
                    groupId = g.ID.ToString(),
                    name = g.Name,
                    charter = g.Charter,
                    memberTitle = g.MemberTitle,
                    powers = g.Powers.ToString(),
                    contribution = g.Contribution,
                    membershipFee = g.MembershipFee,
                    members = g.GroupMembershipCount,
                    roles = g.GroupRolesCount,
                    founderId = g.FounderID.ToString(),
                    insigniaId = g.InsigniaID.ToString(),
                    ownerRoleId = g.OwnerRole.ToString(),
                    openEnrollment = g.OpenEnrollment,
                    showInList = g.ShowInList,
                    acceptNotices = g.AcceptNotices,
                    listInProfile = g.ListInProfile,
                    allowPublish = g.AllowPublish,
                    maturePublish = g.MaturePublish
                }
            };

            return DataToolResult.OkResult(
                $"Retrieved profile for group {g.Name} ({g.ID}).",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupGetMembersAsync(string groupId, bool includeDetails, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var requestId = client.Groups.RequestGroupMembers(groupUuid);
            var reply = await WaitForGroupMembersReplyAsync(client, requestId, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for members of group {groupUuid}.");
            }

            var members = reply.Members.Values.OrderBy(m => m.ID.ToString(), StringComparer.Ordinal).ToList();
            var owners = members.Count(m => m.IsOwner);

            var memberRows = includeDetails
                ? members.Select(m => (object)new
                {
                    agentId = m.ID.ToString(),
                    contribution = m.Contribution,
                    onlineStatus = m.OnlineStatus,
                    title = m.Title,
                    isOwner = m.IsOwner,
                    powers = m.Powers.ToString()
                }).ToList()
                : members.Select(m => (object)new
                {
                    agentId = m.ID.ToString(),
                    title = m.Title,
                    isOwner = m.IsOwner
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    groupId = groupUuid.ToString(),
                    requestId = requestId.ToString(),
                    count = members.Count,
                    owners,
                    includeDetails
                },
                members = memberRows
            };

            return DataToolResult.OkResult(
                $"Retrieved {members.Count} members for group {groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupGetRolesAsync(string groupId, bool includeDetails, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var requestId = client.Groups.RequestGroupRoles(groupUuid);
            var reply = await WaitForGroupRolesReplyAsync(client, requestId, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for roles of group {groupUuid}.");
            }

            var roles = reply.Roles.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var roleRows = includeDetails
                ? roles.Select(r => (object)new
                {
                    roleId = r.ID.ToString(),
                    groupId = r.GroupID.ToString(),
                    name = r.Name,
                    title = r.Title,
                    description = r.Description,
                    powers = r.Powers.ToString(),
                    members = r.Members
                }).ToList()
                : roles.Select(r => (object)new
                {
                    roleId = r.ID.ToString(),
                    name = r.Name,
                    title = r.Title,
                    members = r.Members
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    groupId = groupUuid.ToString(),
                    requestId = requestId.ToString(),
                    count = roles.Count,
                    includeDetails
                },
                roles = roleRows
            };

            return DataToolResult.OkResult(
                $"Retrieved {roles.Count} roles for group {groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupGetRoleMembersAsync(string groupId, bool includeDetails, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var requestId = client.Groups.RequestGroupRolesMembers(groupUuid);
            var reply = await WaitForGroupRoleMembersReplyAsync(client, requestId, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for role-member map of group {groupUuid}.");
            }

            var mappings = reply.RolesMembers.ToList();
            var grouped = mappings
                .GroupBy(p => p.Key)
                .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal)
                .Select(g => new
                {
                    roleId = g.Key.ToString(),
                    memberCount = g.Count(),
                    members = includeDetails ? g.Select(x => x.Value.ToString()) : Array.Empty<string>()
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    groupId = groupUuid.ToString(),
                    requestId = requestId.ToString(),
                    mappings = mappings.Count,
                    roles = grouped.Count,
                    includeDetails
                },
                roles = grouped
            };

            return DataToolResult.OkResult(
                $"Retrieved {mappings.Count} role-member mappings for group {groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupGetTitlesAsync(string groupId, bool includeDetails, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var requestId = client.Groups.RequestGroupTitles(groupUuid);
            var reply = await WaitForGroupTitlesReplyAsync(client, requestId, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for titles of group {groupUuid}.");
            }

            var titles = reply.Titles.Values.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var selected = titles.Count(t => t.Selected);
            var titleRows = includeDetails
                ? titles.Select(t => (object)new
                {
                    roleId = t.RoleID.ToString(),
                    title = t.Title,
                    selected = t.Selected
                }).ToList()
                : titles.Select(t => (object)new
                {
                    title = t.Title,
                    selected = t.Selected
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    groupId = groupUuid.ToString(),
                    requestId = requestId.ToString(),
                    count = titles.Count,
                    selected,
                    includeDetails
                },
                titles = titleRows
            };

            return DataToolResult.OkResult(
                $"Retrieved {titles.Count} titles for group {groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupSetActiveAsync(string groupId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.ActivateGroup(groupUuid);
            return Task.FromResult(BotToolResult.OkResult($"Active group change submitted: {groupUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupSetActiveTitleAsync(string groupId, string roleId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (!UUID.TryParse(roleId, out var roleUuid))
        {
            return BotToolResult.Fail("roleId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.ActivateTitle(groupUuid, roleUuid);
            return Task.FromResult(BotToolResult.OkResult($"Active group title change submitted (group={groupUuid}, role={roleUuid})."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupRoleCreateAsync(string groupId, GroupRoleUpdateInput input, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (!TryBuildGroupRole(groupUuid, UUID.Zero, input, out var role, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.CreateRole(groupUuid, role);
            return Task.FromResult(BotToolResult.OkResult($"Group role create submitted for group={groupUuid}, name='{role.Name}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupRoleUpdateAsync(string groupId, string roleId, GroupRoleUpdateInput input, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (!UUID.TryParse(roleId, out var roleUuid))
        {
            return BotToolResult.Fail("roleId must be a valid UUID.");
        }

        if (!TryBuildGroupRole(groupUuid, roleUuid, input, out var role, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.UpdateRole(role);
            return Task.FromResult(BotToolResult.OkResult($"Group role update submitted for role={roleUuid} in group={groupUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupRoleDeleteAsync(string groupId, string roleId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (!UUID.TryParse(roleId, out var roleUuid))
        {
            return BotToolResult.Fail("roleId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.DeleteRole(groupUuid, roleUuid);
            return Task.FromResult(BotToolResult.OkResult($"Group role delete submitted for role={roleUuid} in group={groupUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupRoleAddMemberAsync(
        string groupId,
        string roleId,
        string memberAgentId,
        bool verifyAfterSubmit,
        int verifyWaitSeconds,
        CancellationToken cancellationToken)
    {
        if (!TryParseRoleMembershipInput(groupId, roleId, memberAgentId, out var groupUuid, out var roleUuid, out var memberUuid, out var error))
        {
            return BotToolResult.Fail(error);
        }

        if (verifyAfterSubmit && (verifyWaitSeconds < 1 || verifyWaitSeconds > 60))
        {
            return BotToolResult.Fail("verifyWaitSeconds must be in range 1..60 when verifyAfterSubmit is true.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            client.Groups.AddToRole(groupUuid, roleUuid, memberUuid);
            if (!verifyAfterSubmit)
            {
                return BotToolResult.OkResult(
                    $"Group role add-member submitted (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=submitted_unverified (LLUDP fire-and-forget).");
            }

            var verified = await WaitForRoleMembershipStateAsync(
                client,
                groupUuid,
                roleUuid,
                memberUuid,
                shouldBeMember: true,
                TimeSpan.FromSeconds(verifyWaitSeconds),
                token).ConfigureAwait(false);

            return verified switch
            {
                true => BotToolResult.OkResult(
                    $"Group role add-member confirmed (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=confirmed."),
                false => BotToolResult.Fail(
                    $"Group role add-member did not verify before timeout (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=submitted_unverified_or_denied."),
                _ => BotToolResult.Fail(
                    $"Group role add-member submitted, but no verification reply arrived (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=submitted_unverified_no_reply.")
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupRoleRemoveMemberAsync(
        string groupId,
        string roleId,
        string memberAgentId,
        bool verifyAfterSubmit,
        int verifyWaitSeconds,
        CancellationToken cancellationToken)
    {
        if (!TryParseRoleMembershipInput(groupId, roleId, memberAgentId, out var groupUuid, out var roleUuid, out var memberUuid, out var error))
        {
            return BotToolResult.Fail(error);
        }

        if (verifyAfterSubmit && (verifyWaitSeconds < 1 || verifyWaitSeconds > 60))
        {
            return BotToolResult.Fail("verifyWaitSeconds must be in range 1..60 when verifyAfterSubmit is true.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            client.Groups.RemoveFromRole(groupUuid, roleUuid, memberUuid);
            if (!verifyAfterSubmit)
            {
                return BotToolResult.OkResult(
                    $"Group role remove-member submitted (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=submitted_unverified (LLUDP fire-and-forget).");
            }

            var verified = await WaitForRoleMembershipStateAsync(
                client,
                groupUuid,
                roleUuid,
                memberUuid,
                shouldBeMember: false,
                TimeSpan.FromSeconds(verifyWaitSeconds),
                token).ConfigureAwait(false);

            return verified switch
            {
                true => BotToolResult.OkResult(
                    $"Group role remove-member confirmed (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=confirmed."),
                false => BotToolResult.Fail(
                    $"Group role remove-member did not verify before timeout (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=submitted_unverified_or_denied."),
                _ => BotToolResult.Fail(
                    $"Group role remove-member submitted, but no verification reply arrived (group={groupUuid}, role={roleUuid}, member={memberUuid}). classification=submitted_unverified_no_reply.")
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupInviteUserAsync(string groupId, GroupInviteInput invite, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        if (invite == null)
        {
            return DataToolResult.FailResult("invite payload is required.");
        }

        if (!UUID.TryParse(invite.TargetAgentId, out var targetAgentId))
        {
            return DataToolResult.FailResult("invite.targetAgentId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var roleIds = ParseUuidList(invite.RoleIdsCsv);
            if (roleIds.Count == 0 && invite.UseEveryoneRoleIfEmpty)
            {
                var requestId = client.Groups.RequestGroupRoles(groupUuid);
                var rolesReply = await WaitForGroupRolesReplyAsync(client, requestId, groupUuid, token).ConfigureAwait(false);
                if (rolesReply != null)
                {
                    var everyoneRole = rolesReply.Roles.Values.FirstOrDefault(r =>
                        r.Name.Equals("Everyone", StringComparison.OrdinalIgnoreCase)
                        || r.Title.Equals("Everyone", StringComparison.OrdinalIgnoreCase));

                    if (everyoneRole.ID != UUID.Zero)
                    {
                        roleIds.Add(everyoneRole.ID);
                    }
                }
            }

            if (roleIds.Count == 0)
            {
                return DataToolResult.FailResult(
                    "No role IDs were resolved for invite. Provide invite.roleIdsCsv or enable useEveryoneRoleIfEmpty where a resolvable Everyone role exists.");
            }

            client.Groups.Invite(groupUuid, roleIds, targetAgentId);
            var payload = new
            {
                classification = "submitted_unverified",
                transport = "lludp_fire_and_forget",
                reason = "Group invitation send has no immediate per-invite success/failure reply event in current surfaced API path.",
                groupId = groupUuid.ToString(),
                targetAgentId = targetAgentId.ToString(),
                roleIds = roleIds.Select(r => r.ToString()).ToArray()
            };

            return DataToolResult.OkResult(
                $"Group invite submitted for target={targetAgentId} in group={groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupBanListGetAsync(string groupId, bool includeDetails, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var reply = await WaitForBannedAgentsReplyAsync(client, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for group ban list for {groupUuid}.");
            }

            if (!reply.Success)
            {
                return DataToolResult.FailResult(
                    $"Group ban list request did not succeed for {groupUuid}. Capability may be unavailable or permission may be insufficient.");
            }

            var banned = (reply.BannedAgents ?? new Dictionary<UUID, DateTime>())
                .OrderByDescending(kvp => kvp.Value)
                .ToList();

            var rows = includeDetails
                ? banned.Select(kvp => (object)new
                {
                    agentId = kvp.Key.ToString(),
                    bannedAtUtc = kvp.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                }).ToList()
                : banned.Select(kvp => (object)new
                {
                    agentId = kvp.Key.ToString()
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    groupId = groupUuid.ToString(),
                    count = banned.Count,
                    includeDetails
                },
                bannedAgents = rows
            };

            return DataToolResult.OkResult(
                $"Retrieved {banned.Count} banned agents for group {groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupBanSetAsync(
        string groupId,
        GroupBanActionInput request,
        bool verifyAfterSubmit,
        int verifyWaitSeconds,
        CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        if (request == null)
        {
            return DataToolResult.FailResult("ban request payload is required.");
        }

        if (!TryParseGroupBanAction(request.Action, out var action, out var actionError))
        {
            return DataToolResult.FailResult(actionError);
        }

        var agentIds = ParseUuidList(request.AgentIdsCsv);
        if (agentIds.Count == 0)
        {
            return DataToolResult.FailResult("request.agentIdsCsv must contain at least one valid UUID.");
        }

        if (verifyAfterSubmit && (verifyWaitSeconds < 1 || verifyWaitSeconds > 60))
        {
            return DataToolResult.FailResult("verifyWaitSeconds must be in range 1..60 when verifyAfterSubmit is true.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Groups.RequestBanActionAsync(groupUuid, action, agentIds.ToArray(), token).ConfigureAwait(false);
            if (!verifyAfterSubmit)
            {
                var payload = new
                {
                    classification = "submitted_unverified",
                    transport = "caps_post_fire_and_forget",
                    action = action.ToString(),
                    groupId = groupUuid.ToString(),
                    agents = agentIds.Select(x => x.ToString()).ToArray(),
                    reason = "Ban action submit completed, but read-back verification was not requested."
                };

                return DataToolResult.OkResult(
                    $"Group ban action submitted ({action}) for {agentIds.Count} agents in group {groupUuid}.",
                    JsonSerializer.Serialize(payload, JsonOptions));
            }

            var expectedBanned = action == GroupBanAction.Ban;
            var verification = await WaitForGroupBanStateAsync(
                client,
                groupUuid,
                agentIds,
                expectedBanned,
                TimeSpan.FromSeconds(verifyWaitSeconds),
                token).ConfigureAwait(false);

            var classification = verification switch
            {
                GroupBanVerificationStatus.Confirmed => "confirmed",
                GroupBanVerificationStatus.NotConfirmed => "submitted_unverified_or_denied",
                GroupBanVerificationStatus.NoReply => "submitted_unverified_no_reply",
                GroupBanVerificationStatus.CapabilityOrPermissionUnavailable => "verification_unavailable",
                _ => "submitted_unverified"
            };

            var payloadVerified = new
            {
                classification,
                action = action.ToString(),
                groupId = groupUuid.ToString(),
                agents = agentIds.Select(x => x.ToString()).ToArray(),
                verifyAfterSubmit,
                verifyWaitSeconds
            };

            return verification switch
            {
                GroupBanVerificationStatus.Confirmed => DataToolResult.OkResult(
                    $"Group ban action confirmed ({action}) for {agentIds.Count} agents in group {groupUuid}.",
                    JsonSerializer.Serialize(payloadVerified, JsonOptions)),
                GroupBanVerificationStatus.CapabilityOrPermissionUnavailable => DataToolResult.FailResult(
                    $"Group ban action submitted ({action}) but verification failed due to capability/permission limits for group {groupUuid}."),
                GroupBanVerificationStatus.NoReply => DataToolResult.FailResult(
                    $"Group ban action submitted ({action}) but no verification reply arrived for group {groupUuid} within {verifyWaitSeconds}s."),
                _ => DataToolResult.FailResult(
                    $"Group ban action submitted ({action}) but verification did not confirm expected state for all agents in group {groupUuid}.")
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupNoticesListAsync(string groupId, bool includeDetails, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return DataToolResult.FailResult("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var reply = await WaitForGroupNoticesReplyAsync(client, groupUuid, token).ConfigureAwait(false);
            if (reply == null)
            {
                return DataToolResult.FailResult($"Timed out waiting for notices list of group {groupUuid}.");
            }

            var notices = reply.Notices.OrderByDescending(n => n.Timestamp).ToList();
            var noticeRows = includeDetails
                ? notices.Select(n => (object)new
                {
                    noticeId = n.NoticeID.ToString(),
                    fromName = n.FromName,
                    subject = n.Subject,
                    hasAttachment = n.HasAttachment,
                    attachmentAssetType = n.AssetType.ToString(),
                    unixTimestamp = n.Timestamp,
                    createdAtUtc = DateTimeOffset.FromUnixTimeSeconds(n.Timestamp).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                }).ToList()
                : notices.Select(n => (object)new
                {
                    noticeId = n.NoticeID.ToString(),
                    subject = n.Subject,
                    hasAttachment = n.HasAttachment
                }).ToList();

            var payload = new
            {
                summary = new
                {
                    groupId = groupUuid.ToString(),
                    count = notices.Count,
                    includeDetails
                },
                notices = noticeRows
            };

            return DataToolResult.OkResult(
                $"Retrieved {notices.Count} notices for group {groupUuid}.",
                JsonSerializer.Serialize(payload, JsonOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupNoticeSendAsync(string groupId, GroupNoticeInput input, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (input == null)
        {
            return BotToolResult.Fail("notice payload is required.");
        }

        var subject = input.Subject?.Trim() ?? string.Empty;
        var message = input.Message?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(subject))
        {
            return BotToolResult.Fail("notice.subject is required.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("notice.message is required.");
        }

        var notice = new GroupNotice
        {
            Subject = subject,
            Message = message,
            AttachmentID = UUID.Zero,
            OwnerID = UUID.Zero
        };

        var hasAttachmentItem = !string.IsNullOrWhiteSpace(input.AttachmentItemId);
        var hasAttachmentOwner = !string.IsNullOrWhiteSpace(input.AttachmentOwnerId);
        if (hasAttachmentItem || hasAttachmentOwner)
        {
            if (!hasAttachmentItem || !hasAttachmentOwner)
            {
                return BotToolResult.Fail("notice.attachmentItemId and notice.attachmentOwnerId must both be provided when sending an attachment.");
            }

            if (!UUID.TryParse(input.AttachmentItemId!, out var attachmentItemId))
            {
                return BotToolResult.Fail("notice.attachmentItemId must be a valid UUID.");
            }

            if (!UUID.TryParse(input.AttachmentOwnerId!, out var attachmentOwnerId))
            {
                return BotToolResult.Fail("notice.attachmentOwnerId must be a valid UUID.");
            }

            notice.AttachmentID = attachmentItemId;
            notice.OwnerID = attachmentOwnerId;
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.SendGroupNotice(groupUuid, notice);
            return Task.FromResult(BotToolResult.OkResult($"Group notice send submitted for group={groupUuid}, subject='{subject}'."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupChatJoinAsync(string groupId, int waitForJoinSeconds, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (waitForJoinSeconds < 0 || waitForJoinSeconds > 60)
        {
            return BotToolResult.Fail("waitForJoinSeconds must be in range 0..60.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            client.Self.RequestJoinGroupChat(groupUuid);
            if (waitForJoinSeconds == 0)
            {
                return BotToolResult.OkResult($"Group chat join submitted for group session {groupUuid}. (not waiting for confirmation)");
            }

            var reply = await WaitForGroupChatJoinedAsync(client, groupUuid, TimeSpan.FromSeconds(waitForJoinSeconds), token).ConfigureAwait(false);
            if (reply == null)
            {
                return BotToolResult.Fail($"Group chat join submitted for {groupUuid}, but no confirmation arrived within {waitForJoinSeconds}s.");
            }

            return reply.Success
                ? BotToolResult.OkResult($"Joined group chat session {reply.SessionID} ({reply.SessionName}).")
                : BotToolResult.Fail($"Group chat join failed for {groupUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupChatLeaveAsync(string groupId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Self.RequestLeaveGroupChat(groupUuid);
            return Task.FromResult(BotToolResult.OkResult($"Group chat leave submitted for session {groupUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupChatSendAsync(string groupId, string message, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return BotToolResult.Fail("message is required.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            if (!client.Self.GroupChatSessions.ContainsKey(groupUuid))
            {
                return Task.FromResult(BotToolResult.Fail($"No active group chat session is tracked for {groupUuid}. Join first."));
            }

            client.Self.InstantMessageGroup(groupUuid, message);
            return Task.FromResult(BotToolResult.OkResult($"Group chat message sent to session {groupUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataToolResult> GroupChatSessionsListAsync(bool includeDetails, CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync((client, _) =>
        {
            var sessions = client.Self.GroupChatSessions
                .OrderBy(kvp => kvp.Key.ToString(), StringComparer.Ordinal)
                .Select(kvp => new
                {
                    sessionId = kvp.Key.ToString(),
                    memberCount = kvp.Value.Count,
                    members = includeDetails
                        ? kvp.Value.Select(m => (object)new
                        {
                            agentId = m.AvatarKey.ToString(),
                            canVoiceChat = m.CanVoiceChat,
                            isModerator = m.IsModerator,
                            muteText = m.MuteText,
                            muteVoice = m.MuteVoice
                        })
                        : Array.Empty<object>()
                })
                .ToList();

            var payload = new
            {
                summary = new
                {
                    count = sessions.Count,
                    includeDetails
                },
                sessions
            };

            return Task.FromResult(DataToolResult.OkResult(
                $"Retrieved {sessions.Count} active group chat sessions.",
                JsonSerializer.Serialize(payload, JsonOptions)));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupChatAcceptInviteAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(sessionId, out var sessionUuid))
        {
            return BotToolResult.Fail("sessionId must be a valid UUID.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            await client.Self.ChatterBoxAcceptInviteAsync(sessionUuid, token).ConfigureAwait(false);
            return BotToolResult.OkResult($"Accepted chat-session invite for session {sessionUuid}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupCreateAsync(GroupCreateInput input, int waitForCreateSeconds, CancellationToken cancellationToken)
    {
        if (input == null)
        {
            return BotToolResult.Fail("group payload is required.");
        }

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return BotToolResult.Fail("group.name is required.");
        }

        if (waitForCreateSeconds < 0 || waitForCreateSeconds > 60)
        {
            return BotToolResult.Fail("waitForCreateSeconds must be in range 0..60.");
        }

        if (!TryBuildGroupFromInput(input, out var group, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            client.Groups.RequestCreateGroup(group);
            if (waitForCreateSeconds == 0)
            {
                return BotToolResult.OkResult($"Group create submitted for '{group.Name}'. (not waiting for confirmation)");
            }

            var reply = await WaitForGroupCreatedReplyAsync(client, TimeSpan.FromSeconds(waitForCreateSeconds), token).ConfigureAwait(false);
            if (reply == null)
            {
                return BotToolResult.Fail($"Group create submitted for '{group.Name}', but no create reply arrived within {waitForCreateSeconds}s.");
            }

            return reply.Success
                ? BotToolResult.OkResult($"Group created successfully: {reply.GroupID} ({reply.Message}).")
                : BotToolResult.Fail($"Group create failed: {reply.Message}");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> GroupUpdateAsync(string groupId, GroupCreateInput input, CancellationToken cancellationToken)
    {
        if (!UUID.TryParse(groupId, out var groupUuid))
        {
            return BotToolResult.Fail("groupId must be a valid UUID.");
        }

        if (input == null)
        {
            return BotToolResult.Fail("group payload is required.");
        }

        if (!TryBuildGroupFromInput(input, out var group, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            client.Groups.UpdateGroup(groupUuid, group);
            return Task.FromResult(BotToolResult.OkResult($"Group update submitted for {groupUuid}."));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryBuildGroupRole(UUID groupId, UUID roleId, GroupRoleUpdateInput input, out GroupRole role, out string error)
    {
        role = default;
        error = string.Empty;

        if (input == null)
        {
            error = "role payload is required.";
            return false;
        }

        var name = input.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "role.name is required.";
            return false;
        }

        if (!TryParseGroupPowers(input.Powers, out var powers, out error))
        {
            return false;
        }

        role = new GroupRole
        {
            GroupID = groupId,
            ID = roleId,
            Name = name,
            Title = input.Title?.Trim() ?? string.Empty,
            Description = input.Description?.Trim() ?? string.Empty,
            Powers = powers
        };

        return true;
    }

    private static bool TryBuildGroupFromInput(GroupCreateInput input, out Group group, out string error)
    {
        group = default;
        error = string.Empty;

        if (!TryParseOptionalUuid(input.InsigniaId, out var insigniaId, out error, "group.insigniaId"))
        {
            return false;
        }

        group = new Group
        {
            Name = input.Name?.Trim() ?? string.Empty,
            Charter = input.Charter?.Trim() ?? string.Empty,
            InsigniaID = insigniaId,
            MembershipFee = input.MembershipFee,
            OpenEnrollment = input.OpenEnrollment,
            ShowInList = input.ShowInList,
            AllowPublish = input.AllowPublish,
            MaturePublish = input.MaturePublish
        };

        return true;
    }

    private static bool TryParseGroupPowers(string? raw, out GroupPowers powers, out string error)
    {
        powers = GroupPowers.None;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            powers = (GroupPowers)numeric;
            return true;
        }

        var tokens = trimmed.Split(new[] { ',', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        GroupPowers result = GroupPowers.None;
        foreach (var token in tokens)
        {
            if (!Enum.TryParse<GroupPowers>(token, true, out var parsed))
            {
                error = $"Invalid group power token '{token}'. Use enum names (comma-separated) or numeric bitmask.";
                return false;
            }

            result |= parsed;
        }

        powers = result;
        return true;
    }

    private static bool TryParseRoleMembershipInput(
        string groupId,
        string roleId,
        string memberAgentId,
        out UUID groupUuid,
        out UUID roleUuid,
        out UUID memberUuid,
        out string error)
    {
        groupUuid = UUID.Zero;
        roleUuid = UUID.Zero;
        memberUuid = UUID.Zero;
        error = string.Empty;

        if (!UUID.TryParse(groupId, out groupUuid))
        {
            error = "groupId must be a valid UUID.";
            return false;
        }

        if (!UUID.TryParse(roleId, out roleUuid))
        {
            error = "roleId must be a valid UUID.";
            return false;
        }

        if (!UUID.TryParse(memberAgentId, out memberUuid))
        {
            error = "memberAgentId must be a valid UUID.";
            return false;
        }

        return true;
    }

    private static bool TryParseOptionalUuid(string? value, out UUID result, out string error, string fieldName)
    {
        result = UUID.Zero;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!UUID.TryParse(value, out result))
        {
            error = $"{fieldName} must be a valid UUID when provided.";
            return false;
        }

        return true;
    }

    private static bool TryParseGroupBanAction(string? value, out GroupBanAction action, out string error)
    {
        action = GroupBanAction.Ban;
        error = string.Empty;

        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "ban":
                action = GroupBanAction.Ban;
                return true;
            case "unban":
                action = GroupBanAction.Unban;
                return true;
            default:
                error = "request.action must be one of: ban, unban.";
                return false;
        }
    }

    private static List<UUID> ParseUuidList(string? csv)
    {
        var result = new List<UUID>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return result;
        }

        var seen = new HashSet<UUID>();
        var tokens = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (!UUID.TryParse(token, out var id))
            {
                continue;
            }

            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static async Task<bool?> WaitForRoleMembershipStateAsync(
        GridClient client,
        UUID groupId,
        UUID roleId,
        UUID memberId,
        bool shouldBeMember,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var hadAnyReply = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestId = client.Groups.RequestGroupRolesMembers(groupId);
            var perAttemptBudget = deadline - DateTimeOffset.UtcNow;
            if (perAttemptBudget <= TimeSpan.Zero)
            {
                break;
            }

            var perAttemptTimeout = perAttemptBudget > TimeSpan.FromSeconds(4)
                ? TimeSpan.FromSeconds(4)
                : perAttemptBudget;

            using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            replyCts.CancelAfter(perAttemptTimeout);

            var reply = await WaitForGroupRoleMembersReplyAsync(client, requestId, groupId, replyCts.Token).ConfigureAwait(false);
            if (reply != null)
            {
                hadAnyReply = true;
                var contains = reply.RolesMembers.Any(x => x.Key == roleId && x.Value == memberId);
                if (contains == shouldBeMember)
                {
                    return true;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var pause = remaining > TimeSpan.FromMilliseconds(700)
                ? TimeSpan.FromMilliseconds(700)
                : remaining;
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }

        return hadAnyReply ? false : null;
    }

    private static async Task<CurrentGroupsEventArgs?> WaitForCurrentGroupsReplyAsync(GridClient client, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<CurrentGroupsEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, CurrentGroupsEventArgs e)
        {
            tcs.TrySetResult(e);
        }

        client.Groups.CurrentGroups += Handler;
        try
        {
            client.Groups.RequestCurrentGroups();
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.CurrentGroups -= Handler;
        }
    }

    private static async Task<GroupProfileEventArgs?> WaitForGroupProfileReplyAsync(GridClient client, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupProfileEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupProfileEventArgs e)
        {
            if (e.Group.ID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupProfile += Handler;
        try
        {
            client.Groups.RequestGroupProfile(groupId);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupProfile -= Handler;
        }
    }

    private static async Task<GroupMembersReplyEventArgs?> WaitForGroupMembersReplyAsync(GridClient client, UUID requestId, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupMembersReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupMembersReplyEventArgs e)
        {
            if (e.RequestID == requestId && e.GroupID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupMembersReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupMembersReply -= Handler;
        }
    }

    private static async Task<GroupRolesDataReplyEventArgs?> WaitForGroupRolesReplyAsync(GridClient client, UUID requestId, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupRolesDataReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupRolesDataReplyEventArgs e)
        {
            if (e.RequestID == requestId && e.GroupID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupRoleDataReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupRoleDataReply -= Handler;
        }
    }

    private static async Task<GroupRolesMembersReplyEventArgs?> WaitForGroupRoleMembersReplyAsync(GridClient client, UUID requestId, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupRolesMembersReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupRolesMembersReplyEventArgs e)
        {
            if (e.RequestID == requestId && e.GroupID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupRoleMembersReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupRoleMembersReply -= Handler;
        }
    }

    private static async Task<GroupTitlesReplyEventArgs?> WaitForGroupTitlesReplyAsync(GridClient client, UUID requestId, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupTitlesReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupTitlesReplyEventArgs e)
        {
            if (e.RequestID == requestId && e.GroupID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupTitlesReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupTitlesReply -= Handler;
        }
    }

    private static async Task<GroupNoticesListReplyEventArgs?> WaitForGroupNoticesReplyAsync(GridClient client, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupNoticesListReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupNoticesListReplyEventArgs e)
        {
            if (e.GroupID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupNoticesListReply += Handler;
        try
        {
            client.Groups.RequestGroupNoticesList(groupId);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupNoticesListReply -= Handler;
        }
    }

    private static async Task<GroupChatJoinedEventArgs?> WaitForGroupChatJoinedAsync(GridClient client, UUID groupId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupChatJoinedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupChatJoinedEventArgs e)
        {
            if (e.SessionID == groupId || e.TmpSessionID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Self.GroupChatJoined += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Self.GroupChatJoined -= Handler;
        }
    }

    private static async Task<GroupCreatedReplyEventArgs?> WaitForGroupCreatedReplyAsync(GridClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupCreatedReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, GroupCreatedReplyEventArgs e)
        {
            tcs.TrySetResult(e);
        }

        client.Groups.GroupCreatedReply += Handler;
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupCreatedReply -= Handler;
        }
    }

    private static async Task<BannedAgentsEventArgs?> WaitForBannedAgentsReplyAsync(GridClient client, UUID groupId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<BannedAgentsEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, BannedAgentsEventArgs e)
        {
            if (e.GroupID == groupId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.BannedAgents += Handler;
        try
        {
            await client.Groups.RequestBannedAgentsAsync(groupId, cancellationToken).ConfigureAwait(false);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.BannedAgents -= Handler;
        }
    }

    private static async Task<GroupBanVerificationStatus> WaitForGroupBanStateAsync(
        GridClient client,
        UUID groupId,
        IReadOnlyList<UUID> targetAgents,
        bool shouldBeBanned,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var hadReply = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var perAttemptBudget = deadline - DateTimeOffset.UtcNow;
            if (perAttemptBudget <= TimeSpan.Zero)
            {
                break;
            }

            var perAttemptTimeout = perAttemptBudget > TimeSpan.FromSeconds(4)
                ? TimeSpan.FromSeconds(4)
                : perAttemptBudget;

            using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            replyCts.CancelAfter(perAttemptTimeout);
            var reply = await WaitForBannedAgentsReplyAsync(client, groupId, replyCts.Token).ConfigureAwait(false);
            if (reply != null)
            {
                hadReply = true;
                if (!reply.Success)
                {
                    return GroupBanVerificationStatus.CapabilityOrPermissionUnavailable;
                }

                var bannedSet = new HashSet<UUID>((reply.BannedAgents ?? new Dictionary<UUID, DateTime>()).Keys);
                var allMatch = targetAgents.All(id => bannedSet.Contains(id) == shouldBeBanned);
                if (allMatch)
                {
                    return GroupBanVerificationStatus.Confirmed;
                }
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var pause = remaining > TimeSpan.FromMilliseconds(700)
                ? TimeSpan.FromMilliseconds(700)
                : remaining;
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }

        return hadReply
            ? GroupBanVerificationStatus.NotConfirmed
            : GroupBanVerificationStatus.NoReply;
    }
}

internal sealed record GroupRoleUpdateInput(
    string Name,
    string? Title,
    string? Description,
    string? Powers);

internal sealed record GroupNoticeInput(
    string Subject,
    string Message,
    string? AttachmentItemId,
    string? AttachmentOwnerId);

internal sealed record GroupCreateInput(
    string Name,
    string? Charter,
    string? InsigniaId,
    int MembershipFee,
    bool OpenEnrollment,
    bool ShowInList,
    bool AllowPublish,
    bool MaturePublish);

internal sealed record GroupInviteInput(
    string TargetAgentId,
    string? RoleIdsCsv,
    bool UseEveryoneRoleIfEmpty);

internal sealed record GroupBanActionInput(
    string Action,
    string AgentIdsCsv);

internal enum GroupBanVerificationStatus
{
    Confirmed,
    NotConfirmed,
    NoReply,
    CapabilityOrPermissionUnavailable
}
