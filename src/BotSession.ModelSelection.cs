namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private async Task<string> ResolvePinnedModelIdAsync(string requestedModel, string? preferredProviderId, CancellationToken cancellationToken)
    {
        var normalized = NormalizeLooseQuery(requestedModel);
        var slash = normalized.IndexOf('/');
        var providerHint = slash > 0 ? normalized[..slash] : null;
        var effectiveProviderFilter = !string.IsNullOrWhiteSpace(providerHint)
            ? providerHint
            : (string.IsNullOrWhiteSpace(preferredProviderId) ? null : NormalizeLooseQuery(preferredProviderId));

        var models = await _opencodeChat!.ListModelsAsync(effectiveProviderFilter, cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                effectiveProviderFilter == null
                    ? "No models are currently reported by Opencode. Try *models."
                    : $"Provider '{effectiveProviderFilter}' returned no models. Try *providers configured and *models {effectiveProviderFilter}.");
        }

        var candidates = models
            .Where(m => ModelIdMatchesRequested(m, normalized, effectiveProviderFilter))
            .ToList();

        if (candidates.Count == 0)
        {
            var suggested = string.Join(", ",
                models.Take(5).Select(m => BuildCanonicalModelId(m.Id, m.Provider, effectiveProviderFilter)));
            var scopeHint = effectiveProviderFilter == null ? string.Empty : $" for provider '{effectiveProviderFilter}'";
            var modelsHint = effectiveProviderFilter == null ? "*models" : $"*models {effectiveProviderFilter}";
            var suggestionHint = string.IsNullOrWhiteSpace(suggested) ? string.Empty : $" Example IDs: {suggested}";
            throw new InvalidOperationException($"Model '{normalized}' is not available{scopeHint}. Try {modelsHint}.{suggestionHint}");
        }

        if (candidates.Count > 1 && effectiveProviderFilter == null && slash < 0)
        {
            var distinctCanonical = candidates
                .Select(m => BuildCanonicalModelId(m.Id, m.Provider, null))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            var sample = string.Join(", ", distinctCanonical);
            throw new InvalidOperationException(
                $"Model '{normalized}' is available from multiple providers. Use a fully qualified ID (provider/model), e.g. {sample}");
        }

        var matched = candidates[0];
        return BuildCanonicalModelId(matched.Id, matched.Provider, effectiveProviderFilter);
    }

    private static bool ModelIdMatchesRequested(OpencodeModelSummary model, string requestedModel, string? providerHint)
    {
        var canonical = BuildCanonicalModelId(model.Id, model.Provider, providerHint);
        if (canonical.Equals(requestedModel, StringComparison.OrdinalIgnoreCase)
            || model.Id.Equals(requestedModel, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var slash = canonical.IndexOf('/');
        if (slash > 0 && slash < canonical.Length - 1)
        {
            var leaf = canonical[(slash + 1)..];
            return leaf.Equals(requestedModel, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string BuildCanonicalModelId(string modelId, string? providerId, string? providerHint)
    {
        var trimmedModel = modelId.Trim();
        if (trimmedModel.Contains('/'))
        {
            return trimmedModel;
        }

        var provider = !string.IsNullOrWhiteSpace(providerId)
            ? providerId.Trim()
            : providerHint;
        return string.IsNullOrWhiteSpace(provider) ? trimmedModel : $"{provider}/{trimmedModel}";
    }

    private string? GetStartupDefaultModelId()
    {
        var configuredModel = _options.OpencodeInitialModel?.Trim();
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            return null;
        }

        if (configuredModel.Contains('/'))
        {
            return configuredModel;
        }

        var configuredProvider = _options.OpencodeInitialProvider?.Trim();
        return string.IsNullOrWhiteSpace(configuredProvider)
            ? configuredModel
            : $"{configuredProvider}/{configuredModel}";
    }

    private static string? GetStartupDefaultProviderId(string? startupModelId)
    {
        if (string.IsNullOrWhiteSpace(startupModelId))
        {
            return null;
        }

        var slash = startupModelId.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        return startupModelId[..slash];
    }

    private static OpencodeProviderSummary? FindProviderByNameOrId(IReadOnlyList<OpencodeProviderSummary> providers, string query)
    {
        var q = query.Trim();
        var exact = providers.FirstOrDefault(p =>
            p.Id.Equals(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        return providers.FirstOrDefault(p =>
            p.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
