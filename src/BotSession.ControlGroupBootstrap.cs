using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const int ControlGroupInviteIntervalMinutes = 1;
    private const int ControlGroupBootstrapTimeoutSeconds = 25;

    private readonly object _controlGroupStateLock = new();
    private string _controlGroupName = string.Empty;
    private UUID _controlGroupId = UUID.Zero;
    private UUID _handlerAgentId = UUID.Zero;
    private readonly Dictionary<string, UUID> _handlerAgentIdByName = new(StringComparer.OrdinalIgnoreCase);
    private int _controlGroupInviteLoopStarted;

    private string BuildControlGroupName()
    {
        var first = (_options.BotFirstName ?? string.Empty).Trim();
        var last = (_options.BotLastName ?? string.Empty).Trim();
        return $"{first} {last} C&C".Trim();
    }

    private void StartControlGroupBootstrap(GridClient client)
    {
        if (!string.IsNullOrWhiteSpace(_parentFullName))
        {
            Console.WriteLine("[group-bootstrap] parent controller mode detected; C&C group bootstrap is disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_controlGroupName))
        {
            Console.WriteLine("[group-bootstrap] control group bootstrap disabled: bot name did not produce a valid control group name.");
            return;
        }

        if (Interlocked.CompareExchange(ref _controlGroupInviteLoopStarted, 1, 0) == 0)
        {
            _ = Task.Run(() => ControlGroupInviteLoopAsync(_lifecycleCts.Token));
        }

        _ = Task.Run(() => BootstrapControlGroupOnceAsync("startup", _lifecycleCts.Token));
    }

    private async Task ControlGroupInviteLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(ControlGroupInviteIntervalMinutes), cancellationToken).ConfigureAwait(false);
                await BootstrapControlGroupOnceAsync("periodic", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[group-bootstrap] periodic control-group invite cycle failed: {ex.Message}");
            }
        }
    }

    private async Task BootstrapControlGroupOnceAsync(string reason, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            return;
        }

        var client = _client;
        if (client == null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ControlGroupBootstrapTimeoutSeconds));

        var controlGroupId = await EnsureControlGroupExistsAsync(client, timeout.Token).ConfigureAwait(false);
        if (controlGroupId == UUID.Zero)
        {
            Console.WriteLine($"[group-bootstrap] {reason}: unable to resolve or create control group '{_controlGroupName}'.");
            return;
        }

        await DeedCurrentParcelToControlGroupIfOwnedAsync(client, controlGroupId, reason, timeout.Token).ConfigureAwait(false);

        if (!IsHandlerRestricted())
        {
            if (string.Equals(reason, "startup", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[group-bootstrap] startup: handler mode is not enabled; handler auto-invite is skipped.");
            }

            return;
        }

        var handlers = await ResolveHandlerAgentsAsync(client, timeout.Token).ConfigureAwait(false);
        if (handlers.Count == 0)
        {
            Console.WriteLine($"[group-bootstrap] {reason}: no resolvable handlers found in '{_handlerConfigPath}' for control-group invite.");
            return;
        }

        foreach (var (handlerName, handlerId) in handlers)
        {
            await InviteHandlerToControlGroupIfNeededAsync(client, controlGroupId, handlerId, handlerName, reason, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task DeedCurrentParcelToControlGroupIfOwnedAsync(
        GridClient client,
        UUID controlGroupId,
        string reason,
        CancellationToken cancellationToken)
    {
        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return;
        }

        await EnsureParcelMapAsync(client, sim, forceRefresh: false, cancellationToken).ConfigureAwait(false);

        var localId = client.Parcels.GetParcelLocalID(sim, client.Self.SimPosition);
        if (localId <= 0)
        {
            Console.WriteLine($"[group-bootstrap] {reason}: unable to resolve current parcel local ID for control-group deeding.");
            return;
        }

        var parcel = await GetParcelAsync(client, sim, localId, refreshFromSimulator: true, cancellationToken).ConfigureAwait(false);
        if (parcel == null)
        {
            Console.WriteLine($"[group-bootstrap] {reason}: unable to resolve current parcel {localId} for control-group deeding.");
            return;
        }

        var botId = client.Self.AgentID;
        if (parcel.OwnerID != botId)
        {
            return;
        }

        if (parcel.GroupID == controlGroupId)
        {
            return;
        }

        // Many grids require parcel group assignment before deeding.
        parcel.GroupID = controlGroupId;
        parcel.Update(client, sim, wantReply: true);
        await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken).ConfigureAwait(false);

        client.Parcels.DeedToGroup(sim, localId, controlGroupId);

        var refreshed = await GetParcelAsync(client, sim, localId, refreshFromSimulator: true, cancellationToken).ConfigureAwait(false);
        if (refreshed != null && refreshed.OwnerID != botId)
        {
            Console.WriteLine($"[group-bootstrap] {reason}: deeded parcel {localId} to control group {controlGroupId}.");
            return;
        }

        Console.WriteLine($"[group-bootstrap] {reason}: submitted parcel {localId} deed to control group {controlGroupId}; ownership has not changed yet.");
    }

    private async Task<UUID> EnsureControlGroupExistsAsync(GridClient client, CancellationToken cancellationToken)
    {
        var memberships = await WaitForCurrentGroupsReplyAsync(client, cancellationToken).ConfigureAwait(false);
        if (memberships != null)
        {
            var existingMembership = memberships.Groups.Values.FirstOrDefault(g =>
                !string.IsNullOrWhiteSpace(g.Name)
                && g.Name.Equals(_controlGroupName, StringComparison.OrdinalIgnoreCase));

            if (existingMembership.ID != UUID.Zero)
            {
                lock (_controlGroupStateLock)
                {
                    _controlGroupId = existingMembership.ID;
                }

                return existingMembership.ID;
            }
        }

        Console.WriteLine($"[group-bootstrap] creating control group '{_controlGroupName}'...");
        var definition = new Group
        {
            Name = _controlGroupName,
            Charter = "Private control channel for bot and handler.",
            MembershipFee = 0,
            OpenEnrollment = false,
            ShowInList = true,
            AllowPublish = false,
            MaturePublish = false
        };

        client.Groups.RequestCreateGroup(definition);
        var created = await WaitForGroupCreatedReplyAsync(client, TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
        if (created != null && created.Success && created.GroupID != UUID.Zero)
        {
            lock (_controlGroupStateLock)
            {
                _controlGroupId = created.GroupID;
            }

            Console.WriteLine($"[group-bootstrap] created control group '{_controlGroupName}' ({created.GroupID}).");
            return created.GroupID;
        }

        if (created != null && !created.Success)
        {
            Console.WriteLine($"[group-bootstrap] group create reply for '{_controlGroupName}' was not successful: {created.Message}");
        }

        // Fallback: query current memberships again in case create succeeded but the reply was delayed or dropped.
        var refreshed = await WaitForCurrentGroupsReplyAsync(client, cancellationToken).ConfigureAwait(false);
        if (refreshed != null)
        {
            var createdMembership = refreshed.Groups.Values.FirstOrDefault(g =>
                !string.IsNullOrWhiteSpace(g.Name)
                && g.Name.Equals(_controlGroupName, StringComparison.OrdinalIgnoreCase));

            if (createdMembership.ID != UUID.Zero)
            {
                lock (_controlGroupStateLock)
                {
                    _controlGroupId = createdMembership.ID;
                }

                return createdMembership.ID;
            }
        }

        return UUID.Zero;
    }

    private async Task<List<(string HandlerName, UUID HandlerId)>> ResolveHandlerAgentsAsync(GridClient client, CancellationToken cancellationToken)
    {
        var configuredHandlers = GetConfiguredHandlerNames();
        if (configuredHandlers.Count == 0)
        {
            return new List<(string HandlerName, UUID HandlerId)>();
        }

        var resolved = new List<(string HandlerName, UUID HandlerId)>();
        foreach (var handlerName in configuredHandlers.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            UUID cached;
            lock (_controlGroupStateLock)
            {
                _handlerAgentIdByName.TryGetValue(handlerName, out cached);
            }

            if (cached != UUID.Zero)
            {
                resolved.Add((handlerName, cached));
                continue;
            }

            var reply = await RequestDirPeopleReplyAsync(client, handlerName, cancellationToken).ConfigureAwait(false);
            if (reply == null)
            {
                Console.WriteLine($"[group-bootstrap] directory search timed out while resolving handler '{handlerName}'.");
                continue;
            }

            var match = reply.MatchedPeople.FirstOrDefault(person =>
            {
                var fullName = NormalizeAvatarName($"{person.FirstName} {person.LastName}");
                return fullName.Equals(handlerName, StringComparison.OrdinalIgnoreCase);
            });

            if (match.AgentID == UUID.Zero)
            {
                Console.WriteLine($"[group-bootstrap] no directory match found for configured handler '{handlerName}'.");
                continue;
            }

            lock (_controlGroupStateLock)
            {
                _handlerAgentIdByName[handlerName] = match.AgentID;
                _handlerAgentId = match.AgentID;
            }

            resolved.Add((handlerName, match.AgentID));
        }

        return resolved;
    }

    private async Task InviteHandlerToControlGroupIfNeededAsync(
        GridClient client,
        UUID controlGroupId,
        UUID handlerId,
        string handlerName,
        string reason,
        CancellationToken cancellationToken)
    {
        var members = await RequestGroupMembersReplyAsync(client, controlGroupId, cancellationToken).ConfigureAwait(false);
        if (members != null && members.Members.ContainsKey(handlerId))
        {
            return;
        }

        var rolesReply = await RequestGroupRolesReplyAsync(client, controlGroupId, cancellationToken).ConfigureAwait(false);
        if (rolesReply == null)
        {
            Console.WriteLine($"[group-bootstrap] {reason}: unable to resolve roles for control group {controlGroupId}; invite skipped.");
            return;
        }

        var everyoneRole = rolesReply.Roles.Values.FirstOrDefault(r =>
            r.Name.Equals("Everyone", StringComparison.OrdinalIgnoreCase)
            || r.Title.Equals("Everyone", StringComparison.OrdinalIgnoreCase));

        var inviteRoleId = everyoneRole.ID;
        if (inviteRoleId == UUID.Zero)
        {
            // Some OpenSim grids may not surface a concrete Everyone role entry in roles replies.
            inviteRoleId = UUID.Zero;

            var roleSnapshot = string.Join(", ",
                rolesReply.Roles.Values
                    .Take(8)
                    .Select(r => $"{r.ID}:{r.Name}/{r.Title}"));

            Console.WriteLine(
                $"[group-bootstrap] {reason}: control group {controlGroupId} has no explicit 'Everyone' role; "
                + $"falling back to UUID.Zero default role (roles={rolesReply.Roles.Count}, sample=[{roleSnapshot}]).");
        }

        client.Groups.Invite(controlGroupId, new List<UUID> { inviteRoleId }, handlerId);
        Console.WriteLine($"[group-bootstrap] {reason}: invited handler '{handlerName}' ({handlerId}) to control group {controlGroupId} (role={inviteRoleId}).");
    }

    private static async Task<DirPeopleReplyEventArgs?> RequestDirPeopleReplyAsync(
        GridClient client,
        string searchText,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<DirPeopleReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = UUID.Zero;

        void Handler(object? _, DirPeopleReplyEventArgs e)
        {
            if (requestId == UUID.Zero || e.QueryID == requestId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Directory.DirPeopleReply += Handler;
        try
        {
            requestId = client.Directory.StartPeopleSearch(searchText, 0);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Directory.DirPeopleReply -= Handler;
        }
    }

    private static async Task<GroupMembersReplyEventArgs?> RequestGroupMembersReplyAsync(
        GridClient client,
        UUID groupId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupMembersReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = UUID.Zero;

        void Handler(object? _, GroupMembersReplyEventArgs e)
        {
            if (e.GroupID != groupId)
            {
                return;
            }

            if (requestId == UUID.Zero || e.RequestID == requestId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupMembersReply += Handler;
        try
        {
            requestId = client.Groups.RequestGroupMembers(groupId);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupMembersReply -= Handler;
        }
    }

    private static async Task<GroupRolesDataReplyEventArgs?> RequestGroupRolesReplyAsync(
        GridClient client,
        UUID groupId,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<GroupRolesDataReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = UUID.Zero;

        void Handler(object? _, GroupRolesDataReplyEventArgs e)
        {
            if (e.GroupID != groupId)
            {
                return;
            }

            if (requestId == UUID.Zero || e.RequestID == requestId)
            {
                tcs.TrySetResult(e);
            }
        }

        client.Groups.GroupRoleDataReply += Handler;
        try
        {
            requestId = client.Groups.RequestGroupRoles(groupId);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(12), cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            client.Groups.GroupRoleDataReply -= Handler;
        }
    }
}
