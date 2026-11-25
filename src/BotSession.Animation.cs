using System.Collections.Concurrent;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    // Last animation set the simulator reported for each avatar, including which object
    // started each animation. The agent's own SignaledAnimations has no source information.
    private readonly ConcurrentDictionary<UUID, IReadOnlyList<Animation>> _avatarAnimationsByAgentId = new();

    private void OnAvatarAnimationForSenses(object? sender, AvatarAnimationEventArgs e)
    {
        if (_avatarAnimationsByAgentId.Count > 2000)
        {
            _avatarAnimationsByAgentId.Clear();
        }

        _avatarAnimationsByAgentId[e.AvatarID] = e.Animations?.ToList() ?? new List<Animation>();
    }

    public async Task<BotToolResult> AnimationStartAsync(string animation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return BotToolResult.Fail("animation is required.");
        }

        if (!TryResolveAnimation(animation, out var animationId, out var resolvedName, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await RunActionAsync(
            $"Started animation {resolvedName}.",
            c => c.Self.AnimationStart(animationId, true),
            cancellationToken);
    }

    public async Task<BotToolResult> AnimationStopAsync(string animation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return BotToolResult.Fail("animation is required.");
        }

        if (!TryResolveAnimation(animation, out var animationId, out var resolvedName, out var error))
        {
            return BotToolResult.Fail(error);
        }

        return await RunActionAsync(
            $"Stopped animation {resolvedName}.",
            c => c.Self.AnimationStop(animationId, true),
            cancellationToken);
    }

    public Task<AnimationListResult> AnimationsListAsync(CancellationToken cancellationToken)
    {
        return ExecuteLockedAsync((client, _) =>
        {
            var entries = Animations.ToDictionary()
                .Select(kvp => new AnimationInfo(kvp.Value, kvp.Key.ToString()))
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(AnimationListResult.OkResult(entries, $"Listed {entries.Count} built-in animations."));
        }, cancellationToken);
    }

    public Task<AnimationListResult> ActiveAnimationsAsync(string? agentId, bool resolveNames, CancellationToken cancellationToken)
    {
        var targetId = UUID.Zero;
        if (!string.IsNullOrWhiteSpace(agentId) && !UUID.TryParse(agentId.Trim(), out targetId))
        {
            return Task.FromResult(AnimationListResult.FailResult("agentId is not a valid UUID."));
        }

        return ExecuteLockedAsync(async (client, token) =>
        {
            var isSelf = targetId == UUID.Zero || targetId == client.Self.AgentID;
            if (isSelf)
            {
                targetId = client.Self.AgentID;
            }

            _avatarAnimationsByAgentId.TryGetValue(targetId, out var reported);
            var sourceByAnimation = (reported ?? Array.Empty<Animation>())
                .GroupBy(a => a.AnimationID)
                .ToDictionary(g => g.Key, g => g.Last().AnimationSourceObjectID);

            List<(UUID Id, int Sequence)> active;
            if (isSelf)
            {
                active = client.Self.SignaledAnimations.Select(kvp => (kvp.Key, kvp.Value)).ToList();
            }
            else if (reported != null)
            {
                active = reported.Select(a => (a.AnimationID, a.AnimationSequence)).ToList();
            }
            else
            {
                return AnimationListResult.FailResult(
                    $"No animation report has been received for avatar {targetId} yet. The simulator sends one when the avatar is in view and its animations change.");
            }

            // Resolve the objects that started the animations, loading their names when missing.
            var sim = client.Network.CurrentSim;
            var sourceIds = sourceByAnimation.Values.Where(id => id != UUID.Zero && id != targetId).ToHashSet();
            var sourcePrims = sim == null
                ? new Dictionary<UUID, Primitive>()
                : sim.ObjectsPrimitives.Values
                    .Where(p => p != null && sourceIds.Contains(p.ID))
                    .GroupBy(p => p.ID)
                    .ToDictionary(g => g.Key, g => g.First());
            var customNames = new Dictionary<UUID, string>();
            if (sim != null && sourcePrims.Count > 0)
            {
                // The bot's own attachments only answer after being selected (see the attachment resolver).
                if (sourcePrims.Values.Any(p => p.Properties == null && p.ParentID == client.Self.LocalID))
                {
                    await EnsureOwnAttachmentPropertiesAsync(client, token).ConfigureAwait(false);
                }

                await EnsurePrimNamesAsync(client, sim, sourcePrims.Values, token).ConfigureAwait(false);

                if (resolveNames)
                {
                    foreach (var prim in sourcePrims.Values)
                    {
                        foreach (var pair in await GetAnimationNamesInObjectAsync(client, sim, prim, token).ConfigureAwait(false))
                        {
                            customNames[pair.Key] = pair.Value;
                        }
                    }
                }
            }

            var builtIn = Animations.ToDictionary();
            var entries = active
                .Select(a =>
                {
                    var known = builtIn.TryGetValue(a.Id, out var builtInName);
                    sourceByAnimation.TryGetValue(a.Id, out var sourceId);
                    var hasSource = sourceId != UUID.Zero && sourceId != targetId;
                    Primitive? sourcePrim = null;
                    if (hasSource)
                    {
                        sourcePrims.TryGetValue(sourceId, out sourcePrim);
                    }

                    var customName = !known && customNames.TryGetValue(a.Id, out var resolved) ? resolved : null;
                    return new AnimationInfo(
                        known ? builtInName! : customName ?? a.Id.ToString(),
                        a.Id.ToString(),
                        a.Sequence,
                        known,
                        hasSource ? sourceId.ToString() : null,
                        sourcePrim?.Properties?.Name,
                        sourcePrim?.LocalID,
                        sourcePrim?.IsAttachment);
                })
                .OrderBy(a => a.SequenceId)
                .ToList();

            var custom = entries.Count(e => !e.BuiltIn);
            var named = entries.Count(e => !e.BuiltIn && e.Name != e.AnimationId);
            var who = isSelf ? "the bot" : "avatar " + targetId;
            return AnimationListResult.OkResult(
                entries,
                $"Found {entries.Count} active animation(s) for {who}: {entries.Count - custom} built-in, {custom} custom ({named} named). "
                + "Custom animations have no name on the wire; a name is shown only when the object that started the animation lets this avatar see the asset ids of its contents (objects it owns or can modify). "
                + "The source object is only reported for the bot's own animations.");
        }, cancellationToken);
    }

    private static readonly TimeSpan AnimationNameCacheLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<UUID, (DateTimeOffset At, IReadOnlyDictionary<UUID, string> Names)> _animationNamesByObjectId = new();

    // Animation asset id -> item name for the animations inside an object, when the simulator
    // discloses the asset ids (it zeroes them for objects this avatar has no rights on).
    private async Task<IReadOnlyDictionary<UUID, string>> GetAnimationNamesInObjectAsync(
        GridClient client,
        Simulator sim,
        Primitive prim,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_animationNamesByObjectId.TryGetValue(prim.ID, out var cached) && now - cached.At < AnimationNameCacheLifetime)
        {
            return cached.Names;
        }

        var names = new Dictionary<UUID, string>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var entries = await client.Inventory
                .GetTaskInventoryAsync(prim.ID, prim.LocalID, sim, timeout.Token)
                .ConfigureAwait(false);
            foreach (var item in entries.OfType<InventoryItem>())
            {
                if (item.AssetType == AssetType.Animation && item.AssetUUID != UUID.Zero)
                {
                    names[item.AssetUUID] = item.Name;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Contents did not arrive in time; report the animation by id.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[animations] could not list contents of {prim.ID}: {ex.Message}");
        }

        _animationNamesByObjectId[prim.ID] = (now, names);
        return names;
    }

    private static bool TryResolveAnimation(string input, out UUID animationId, out string resolvedName, out string error)
    {
        animationId = UUID.Zero;
        resolvedName = input;
        error = string.Empty;

        var trimmed = input.Trim();

        if (UUID.TryParse(trimmed, out animationId))
        {
            resolvedName = trimmed;
            return true;
        }

        var dict = Animations.ToDictionary();
        var match = dict.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, trimmed, StringComparison.OrdinalIgnoreCase));

        if (match.Key != UUID.Zero)
        {
            animationId = match.Key;
            resolvedName = match.Value;
            return true;
        }

        error = $"Animation '{trimmed}' is not a valid UUID or built-in animation name.";
        return false;
    }

    private async Task<AnimationListResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<AnimationListResult>> action,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = EnsureClient();
            return await action(client, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return AnimationListResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }
}

internal sealed record AnimationInfo(
    string Name,
    string AnimationId,
    int? SequenceId = null,
    bool BuiltIn = true,
    string? SourceObjectId = null,
    string? SourceObjectName = null,
    uint? SourceObjectLocalId = null,
    bool? SourceIsAttachment = null);

internal sealed record AnimationListResult(
    bool Ok,
    string Message,
    IReadOnlyList<AnimationInfo> Animations)
{
    public static AnimationListResult OkResult(IReadOnlyList<AnimationInfo> animations, string message)
        => new(true, message, animations);

    public static AnimationListResult FailResult(string message)
        => new(false, message, Array.Empty<AnimationInfo>());
}