using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

// opensim-ai-docker#7: appearance auto-repair. Region crossings can pollute the
// server-side visual-parameter set (healthy avatar reads ~77 non-default
// params, corrupted reads ~256) while plain rebakes do not clear it. The
// verified repair is re-wearing the complete-outfit folder with
// replaceItems:true, waiting ~12s, then forcing a rebake. This partial adds:
//   - a persisted per-agent healthy baseline (count + outfit folder id)
//     under /workspace/state,
//   - appearance_health_check (compare live count vs baseline, rebaseline),
//   - appearance_auto_repair (the verified repair sequence with
//     before/after counts),
//   - an automatic post-region-change check that detects inflation and runs
//     the repair with cooldown + in-flight guards so it cannot flap.
internal sealed partial class BotSession
{
    private const int AppearanceInflationMargin = 8;
    private const int AppearanceRepairSettleSeconds = 12;
    private const int AppearancePostRebakeSettleSeconds = 3;
    private const int AppearancePostSimChangeDelaySeconds = 10;
    private const int AppearanceAutoRepairCooldownSeconds = 300;
    // Corrupted sessions historically read ~256 non-default params; warn when
    // a rebaseline stores a count in that family so a corrupted state is not
    // silently blessed as healthy.
    private const int AppearanceRebaselineWarnCeiling = 160;

    private static readonly JsonSerializerOptions AppearanceBaselineJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _appearanceBaselineLock = new();
    private int _appearanceAutoRepairInFlight;
    private DateTime _appearanceLastAutoRepairUtc = DateTime.MinValue;
    private bool _appearanceNoBaselineLogged;

    private sealed record AppearanceBaselineStateModel(
        int Version,
        string? AgentId,
        int? BaselineNonDefaultVisualParamCount,
        string? OutfitFolderId);



    public async Task<AppearanceHealthCheckResult> AppearanceHealthCheckAsync(
        bool rebaseline,
        string? outfitFolderId,
        CancellationToken cancellationToken)
    {
        var diag = await AppearanceBakeDiagnosticsAsync(false, 3000, cancellationToken).ConfigureAwait(false);
        if (!diag.Ok)
        {
            return AppearanceHealthCheckResult.FailResult(diag.Message);
        }

        var live = diag.NonDefaultVisualParamCount;
        var agentId = ResolveCurrentBotUuid()?.ToString() ?? string.Empty;
        var state = LoadAppearanceBaselineState(agentId);

        if (rebaseline || state == null)
        {
            if (!rebaseline && state == null)
            {
                // No silent seeding: a fresh login can still carry the polluted
                // param set (upstream evidence), so the first baseline must be
                // stored deliberately after the avatar is verified healthy.
                return new AppearanceHealthCheckResult(
                    true,
                    $"No baseline stored yet. Current nonDefaultVisualParamCount={live}. " +
                    "Verify the avatar renders correctly, then call with rebaseline=true to seed the baseline.",
                    false,
                    null,
                    live,
                    false,
                    null,
                    false);
            }

            var folderId = outfitFolderId ?? state?.OutfitFolderId;
            if (string.IsNullOrWhiteSpace(folderId) || !UUID.TryParse(folderId, out _))
            {
                return AppearanceHealthCheckResult.FailResult(
                    "Cannot seed baseline without a complete-outfit folder. Pass a valid outfitFolderId " +
                    "(the folder the repair re-wears); it is stored alongside the baseline.");
            }

            SaveAppearanceBaselineState(new AppearanceBaselineStateModel(1, agentId, live, folderId));
            var warning = live > AppearanceRebaselineWarnCeiling
                ? $" WARNING: count {live} is in the corrupted family (~256); confirm the avatar really renders correctly before trusting this baseline."
                : string.Empty;
            Console.WriteLine($"[appearance-autorepair] baseline seeded: agent={agentId} count={live} outfitFolder={folderId}{warning}");
            return new AppearanceHealthCheckResult(
                true,
                $"Baseline stored: nonDefaultVisualParamCount={live}, outfitFolderId={folderId}.{warning}",
                true,
                live,
                live,
                false,
                folderId,
                true);
        }

        var baseline = state.BaselineNonDefaultVisualParamCount ?? -1;
        var inflated = live > baseline + AppearanceInflationMargin;
        var message = inflated
            ? $"APPEARANCE POLLUTED: nonDefaultVisualParamCount={live} exceeds baseline {baseline} by {live - baseline} " +
                $"(margin {AppearanceInflationMargin}). Run appearance_auto_repair (or pass outfitFolderId and re-wear)."
            : $"Appearance healthy: nonDefaultVisualParamCount={live} within margin {AppearanceInflationMargin} of baseline {baseline}.";
        return new AppearanceHealthCheckResult(
            true,
            message,
            true,
            baseline,
            live,
            inflated,
            state.OutfitFolderId,
            false);
    }

    public async Task<AppearanceAutoRepairResult> AppearanceAutoRepairAsync(
        bool force,
        string? outfitFolderId,
        CancellationToken cancellationToken)
    {
        var diag = await AppearanceBakeDiagnosticsAsync(false, 3000, cancellationToken).ConfigureAwait(false);
        if (!diag.Ok)
        {
            return AppearanceAutoRepairResult.FailResult(diag.Message);
        }

        var agentId = ResolveCurrentBotUuid()?.ToString() ?? string.Empty;
        var state = LoadAppearanceBaselineState(agentId);
        if (state == null || state.BaselineNonDefaultVisualParamCount is not > 0)
        {
            return AppearanceAutoRepairResult.FailResult(
                "No baseline stored. Run appearance_health_check with rebaseline=true and a valid outfitFolderId first.");
        }

        var baseline = state.BaselineNonDefaultVisualParamCount!.Value;
        var folderId = outfitFolderId ?? state.OutfitFolderId;
        if (string.IsNullOrWhiteSpace(folderId) || !UUID.TryParse(folderId, out _))
        {
            return AppearanceAutoRepairResult.FailResult(
                "No valid complete-outfit folder available. Pass outfitFolderId (the manual fix re-wears the avatar's complete-outfit folder).");
        }

        var before = diag.NonDefaultVisualParamCount;
        var inflatedBefore = before > baseline + AppearanceInflationMargin;
        if (!inflatedBefore && !force)
        {
            return new AppearanceAutoRepairResult(
                true,
                $"No repair needed: nonDefaultVisualParamCount={before} within margin {AppearanceInflationMargin} of baseline {baseline}.",
                true,
                baseline,
                before,
                before,
                false,
                true,
                Array.Empty<string>());
        }

        Console.WriteLine($"[appearance-autorepair] manual repair starting: before={before} baseline={baseline} outfitFolder={folderId} force={force}");

        // The verified sequence (opensim-ai-docker#7):
        // re-wear complete outfit with replaceItems:true, settle ~12s, force
        // rebake. Steps run through the public session methods (each takes
        // the action gate); internal delays use CancellationToken.None so a
        // caller disconnect cannot abort the sequence half-applied.
        var actions = new List<string>();
        var wear = await AppearanceWearFolderAsync(folderId, true, CancellationToken.None).ConfigureAwait(false);
        if (!wear.Ok)
        {
            return AppearanceAutoRepairResult.FailResult($"Re-wear of outfit folder {folderId} failed: {wear.Message}");
        }
        actions.Add($"appearance_wear_folder(folderId={folderId}, replaceItems=true) -> {wear.Message}");
        Console.WriteLine("[appearance-autorepair] re-wear complete; settling " + AppearanceRepairSettleSeconds + "s before rebake");

        await Task.Delay(TimeSpan.FromSeconds(AppearanceRepairSettleSeconds), CancellationToken.None).ConfigureAwait(false);
        actions.Add($"waited {AppearanceRepairSettleSeconds}s for outfit to settle");

        var rebake = await AppearanceRebakeAsync(true, CancellationToken.None).ConfigureAwait(false);
        if (!rebake.Ok)
        {
            return AppearanceAutoRepairResult.FailResult($"Force rebake failed after re-wear: {rebake.Message}");
        }
        actions.Add("appearance_rebake(forceRebake=true) -> " + rebake.Message);

        await Task.Delay(TimeSpan.FromSeconds(AppearancePostRebakeSettleSeconds), CancellationToken.None).ConfigureAwait(false);
        actions.Add($"waited {AppearancePostRebakeSettleSeconds}s after rebake");

        var afterDiag = await AppearanceBakeDiagnosticsAsync(false, 3000, CancellationToken.None).ConfigureAwait(false);
        if (!afterDiag.Ok)
        {
            return AppearanceAutoRepairResult.FailResult($"Post-repair diagnostics failed: {afterDiag.Message}. Actions so far: {string.Join(" | ", actions)}");
        }
        actions.Add("appearance_bake_diagnostics -> nonDefaultVisualParamCount=" + afterDiag.NonDefaultVisualParamCount);

        var after = afterDiag.NonDefaultVisualParamCount;
        var succeeded = after <= baseline + AppearanceInflationMargin;
        Console.WriteLine($"[appearance-autorepair] repair finished: before={before} after={after} baseline={baseline} succeeded={succeeded}");
        return new AppearanceAutoRepairResult(
            true,
            succeeded
                ? $"Appearance repair succeeded: nonDefaultVisualParamCount {before} -> {after} (baseline {baseline})."
                : $"Appearance repair ran but count is still elevated: {before} -> {after} (baseline {baseline}). Check the outfit folder id and retry.",
            true,
            baseline,
            before,
            after,
            true,
            succeeded,
            actions);
    }

    // Guards shared by the automatic path: single-flight + cooldown so a
    // polluted avatar crossing regions repeatedly cannot trigger overlapping
    // or looping repair cycles.
    private async Task<(int before, int after, bool succeeded)?> RunAppearanceAutoRepairWithGuardsAsync()
    {
        if (Interlocked.CompareExchange(ref _appearanceAutoRepairInFlight, 1, 0) != 0)
        {
            Console.WriteLine("[appearance-autorepair] repair already in flight; skipping");
            return null;
        }

        try
        {
            lock (_appearanceBaselineLock)
            {
                if (DateTime.UtcNow - _appearanceLastAutoRepairUtc < TimeSpan.FromSeconds(AppearanceAutoRepairCooldownSeconds))
                {
                    Console.WriteLine("[appearance-autorepair] cooldown active; skipping");
                    return null;
                }
            }

            var repair = await AppearanceAutoRepairAsync(false, null, CancellationToken.None).ConfigureAwait(false);
            if (!repair.Ok)
            {
                Console.WriteLine($"[appearance-autorepair] automatic repair failed: {repair.Message}");
                return null;
            }

            lock (_appearanceBaselineLock)
            {
                _appearanceLastAutoRepairUtc = DateTime.UtcNow;
            }

            return (repair.BeforeCount, repair.AfterCount, repair.RepairSucceeded);
        }
        finally
        {
            Volatile.Write(ref _appearanceAutoRepairInFlight, 0);
        }
    }

    // opensim-ai-docker#7: automatic post-region-change check. SimChanged covers both
    // cross-region teleports and neighbor crossings, which is exactly when the
    // server-side visual-param pollution has been observed. Fire-and-forget:
    // never block the network event loop.
    private void RunAppearancePostSimChangeCheck()
    {
        if (!_options.AppearanceAutoRepairEnabled)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Give the simulator time to push appearance state to the new
                // region before reading param values.
                await Task.Delay(TimeSpan.FromSeconds(AppearancePostSimChangeDelaySeconds), CancellationToken.None).ConfigureAwait(false);

                var agentId = ResolveCurrentBotUuid()?.ToString() ?? string.Empty;
                var state = LoadAppearanceBaselineState(agentId);
                if (state == null || state.BaselineNonDefaultVisualParamCount is not > 0)
                {
                    if (!_appearanceNoBaselineLogged)
                    {
                        _appearanceNoBaselineLogged = true;
                        Console.WriteLine("[appearance-autorepair] no baseline stored; post-region-change checks inactive until appearance_health_check seeds one");
                    }
                    return;
                }
                _appearanceNoBaselineLogged = false;

                var baseline = state.BaselineNonDefaultVisualParamCount!.Value;
                var check = await AppearanceHealthCheckAsync(false, null, CancellationToken.None).ConfigureAwait(false);
                if (!check.Ok)
                {
                    Console.WriteLine($"[appearance-autorepair] post-region-change health check failed: {check.Message}");
                    return;
                }

                if (!check.Inflated)
                {
                    Console.WriteLine($"[appearance-autorepair] post-region-change check: healthy (live={check.LiveNonDefaultVisualParamCount} baseline={baseline})");
                    return;
                }

                Console.WriteLine($"[appearance-autorepair] pollution detected after region change: live={check.LiveNonDefaultVisualParamCount} baseline={baseline}");
                EmitRuntimeEvent(
                    "general",
                    "appearance.corruption_detected",
                    "opensim",
                    $"Visual-param pollution detected: nonDefaultVisualParamCount={check.LiveNonDefaultVisualParamCount} vs baseline {baseline}.",
                    new Dictionary<string, string?>
                    {
                        ["liveCount"] = check.LiveNonDefaultVisualParamCount.ToString(),
                        ["baselineCount"] = baseline.ToString()
                    });

                var repaired = await RunAppearanceAutoRepairWithGuardsAsync().ConfigureAwait(false);
                if (repaired == null)
                {
                    return;
                }

                EmitRuntimeEvent(
                    "general",
                    "appearance.repair_completed",
                    "opensim",
                    $"Appearance auto-repair finished: count {repaired.Value.before} -> {repaired.Value.after} (baseline {baseline}).",
                    new Dictionary<string, string?>
                    {
                        ["beforeCount"] = repaired.Value.before.ToString(),
                        ["afterCount"] = repaired.Value.after.ToString(),
                        ["baselineCount"] = baseline.ToString(),
                        ["succeeded"] = repaired.Value.succeeded.ToString()
                    });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[appearance-autorepair] post-region-change check error: {ex.Message}");
            }
        });
    }

    private AppearanceBaselineStateModel? LoadAppearanceBaselineState(string agentId)
    {
        lock (_appearanceBaselineLock)
        {
            var fullPath = ResolveAppearanceBaselineStateFilePath();
            if (fullPath == null || !File.Exists(fullPath))
            {
                return null;
            }

            try
            {
                var raw = File.ReadAllText(fullPath);
                var model = JsonSerializer.Deserialize<AppearanceBaselineStateModel>(raw, AppearanceBaselineJsonOptions);
                if (model == null)
                {
                    Console.WriteLine($"[appearance-autorepair] baseline file ignored (empty/invalid JSON): {fullPath}");
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(model.AgentId)
                    && !string.Equals(model.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[appearance-autorepair] baseline file belongs to agent {model.AgentId}, not {agentId}; ignoring");
                    return null;
                }

                return model;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[appearance-autorepair] failed to load baseline file {fullPath}: {ex.Message}");
                return null;
            }
        }
    }

    private void SaveAppearanceBaselineState(AppearanceBaselineStateModel model)
    {
        lock (_appearanceBaselineLock)
        {
            var fullPath = ResolveAppearanceBaselineStateFilePath();
            if (fullPath == null)
            {
                Console.WriteLine("[appearance-autorepair] no baseline state file configured; baseline kept in memory only");
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(fullPath, JsonSerializer.Serialize(model, AppearanceBaselineJsonOptions));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[appearance-autorepair] failed to write baseline file {fullPath}: {ex.Message}");
            }
        }
    }

    private string? ResolveAppearanceBaselineStateFilePath()
    {
        var template = _options.AppearanceBaselineStateFile?.Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var botUuid = ResolveCurrentBotUuid();
        if (botUuid != null)
        {
            template = template.Replace("{bot_uuid}", botUuid.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return Path.GetFullPath(template);
    }
}

internal sealed record AppearanceHealthCheckResult(
    bool Ok,
    string Message,
    bool HasBaseline,
    int? BaselineNonDefaultVisualParamCount,
    int LiveNonDefaultVisualParamCount,
    bool Inflated,
    string? OutfitFolderId,
    bool BaselineSaved)
{
    public static AppearanceHealthCheckResult FailResult(string message)
        => new(false, message, false, null, -1, false, null, false);
}
internal sealed record AppearanceAutoRepairResult(
    bool Ok,
    string Message,
    bool BaselineKnown,
    int? BaselineNonDefaultVisualParamCount,
    int BeforeCount,
    int AfterCount,
    bool RepairAttempted,
    bool RepairSucceeded,
    IReadOnlyList<string> ActionsTaken)
{
    public static AppearanceAutoRepairResult FailResult(string message)
        => new(false, message, false, null, -1, -1, false, false, Array.Empty<string>());
}
