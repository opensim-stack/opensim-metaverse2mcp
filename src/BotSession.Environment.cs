using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    public async Task<EnvironmentToolResult> GetRegionEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetRegionEnvironmentAsync(token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult("Unable to fetch region environment (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult("Fetched region environment.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentToolResult> GetParcelEnvironmentAsync(int parcelId, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return EnvironmentToolResult.FailResult("parcelId must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetParcelEnvironmentAsync(parcelId, token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult($"Unable to fetch parcel environment for parcelId={parcelId} (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult($"Fetched parcel environment for parcelId={parcelId}.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetRegionEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.ResetRegionEnvironmentAsync(token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Region environment reset failed or was rejected.");
            }

            return BotToolResult.OkResult("Region environment reset requested successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetParcelEnvironmentAsync(int parcelId, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return BotToolResult.Fail("parcelId must be >= 0.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.ResetParcelEnvironmentAsync(parcelId, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail($"Parcel environment reset failed or was rejected for parcelId={parcelId}.");
            }

            return BotToolResult.OkResult($"Parcel environment reset requested for parcelId={parcelId}.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentToolResult> GetLegacyEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var environment = await client.Environment.GetLegacyEnvironmentAsync(token).ConfigureAwait(false);
            if (environment == null)
            {
                return EnvironmentToolResult.FailResult("Unable to fetch legacy environment (capability unavailable or request failed).");
            }

            var payloadJson = OSDParser.SerializeJsonString(environment.Serialize(), preserveDefaults: true);
            return EnvironmentToolResult.OkResult("Fetched legacy environment.", payloadJson);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetLegacyEnvironmentRawAsync(string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.SetLegacyEnvironmentAsync(map, token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Legacy environment set failed or was rejected.");
            }

            return BotToolResult.OkResult("Legacy environment update posted successfully.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetRegionEnvironmentRawAsync(string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        if (!TryBuildEnvironmentDataFromPayloadMap(map, out var environment, out var environmentError))
        {
            return BotToolResult.Fail(environmentError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var response = await client.Environment.SetRegionEnvironmentAsync(environment, token).ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail("Region environment update failed (capability unavailable or request failed).");
            }

            if (!response.Success)
            {
                var detail = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : $" Detail: {response.Message}";
                return BotToolResult.Fail($"Region environment update was rejected.{detail}");
            }

            return BotToolResult.OkResult($"Region environment updated successfully (version={response.Version}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> SetParcelEnvironmentRawAsync(int parcelId, string payload, string payloadFormat, CancellationToken cancellationToken)
    {
        if (parcelId < 0)
        {
            return BotToolResult.Fail("parcelId must be >= 0.");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return BotToolResult.Fail("payload is required.");
        }

        if (!TryParseLlsdPayload(payload, payloadFormat, out var parsed, out var parseError))
        {
            return BotToolResult.Fail(parseError);
        }

        if (parsed is not OSDMap map)
        {
            return BotToolResult.Fail("payload must deserialize to an LLSD map/object at the root.");
        }

        if (!TryBuildEnvironmentDataFromPayloadMap(map, out var environment, out var environmentError))
        {
            return BotToolResult.Fail(environmentError);
        }

        return await ExecuteLockedAsync(async (client, token) =>
        {
            var response = await client.Environment.SetParcelEnvironmentAsync(parcelId, environment, token).ConfigureAwait(false);
            if (response == null)
            {
                return BotToolResult.Fail($"Parcel environment update failed for parcelId={parcelId} (capability unavailable or request failed).");
            }

            if (!response.Success)
            {
                var detail = string.IsNullOrWhiteSpace(response.Message) ? string.Empty : $" Detail: {response.Message}";
                return BotToolResult.Fail($"Parcel environment update was rejected for parcelId={parcelId}.{detail}");
            }

            return BotToolResult.OkResult($"Parcel environment updated for parcelId={parcelId} (version={response.Version}).");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BotToolResult> ResetLegacyEnvironmentAsync(CancellationToken cancellationToken)
    {
        return await ExecuteLockedAsync(async (client, token) =>
        {
            var ok = await client.Environment.SetLegacyEnvironmentAsync(new OSDMap(), token).ConfigureAwait(false);
            if (!ok)
            {
                return BotToolResult.Fail("Legacy environment reset failed or was rejected.");
            }

            return BotToolResult.OkResult("Legacy environment reset posted using an empty LLSD map.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentToolResult> ExecuteLockedAsync(
        Func<GridClient, CancellationToken, Task<EnvironmentToolResult>> action,
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
            return EnvironmentToolResult.FailResult(ex.Message);
        }
        finally
        {
            _actionGate.Release();
        }
    }
    
    private static bool TryBuildEnvironmentDataFromPayloadMap(OSDMap payloadMap, out EnvironmentData environment, out string error)
    {
        environment = new EnvironmentData();
        error = string.Empty;

        // Accept either a direct EnvironmentData map or an ExtEnvironment-style wrapper map
        // containing an "environment" map.
        OSDMap? environmentMap = null;
        if (payloadMap.TryGetValue("environment", out var wrappedEnvironment))
        {
            environmentMap = wrappedEnvironment as OSDMap;
            if (environmentMap == null)
            {
                error = "payload contains an 'environment' key, but its value is not an LLSD map/object.";
                return false;
            }
        }
        else
        {
            environmentMap = payloadMap;
        }

        try
        {
            environment.Deserialize(environmentMap);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to deserialize EnvironmentData payload: {ex.Message}";
            return false;
        }
    }
}

internal sealed record EnvironmentToolResult(bool Ok, string Message, string? PayloadJson)
{
    public static EnvironmentToolResult OkResult(string message, string payloadJson) => new(true, message, payloadJson);
    public static EnvironmentToolResult FailResult(string message) => new(false, message, null);
}