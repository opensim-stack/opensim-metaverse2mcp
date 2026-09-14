using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{

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

    public Task<AnimationListResult> ActiveAnimationsAsync(CancellationToken cancellationToken)
    {
        return ExecuteLockedAsync((client, _) =>
        {
            var dict = Animations.ToDictionary();
            var entries = client.Self.SignaledAnimations
                .Select(kvp =>
                {
                    var name = dict.TryGetValue(kvp.Key, out var n) ? n : null;
                    return new AnimationInfo(name ?? kvp.Key.ToString(), kvp.Key.ToString(), kvp.Value);
                })
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult(AnimationListResult.OkResult(entries, $"Found {entries.Count} active animations."));
        }, cancellationToken);
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

internal sealed record AnimationInfo(string Name, string AnimationId, int? SequenceId = null);

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