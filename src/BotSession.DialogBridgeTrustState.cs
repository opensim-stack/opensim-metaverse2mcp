using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private static readonly JsonSerializerOptions DialogBridgeTrustStateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private void TryLoadDialogBridgeTrustStateFromFile()
    {
        var fullPath = ResolveDialogBridgeTrustStateFilePath();
        if (fullPath == null || !File.Exists(fullPath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(fullPath);
            var model = JsonSerializer.Deserialize<DialogBridgeTrustStateModel>(raw, DialogBridgeTrustStateJsonOptions);
            if (model == null)
            {
                Console.WriteLine($"[dialog-bridge] trust-state file ignored (empty/invalid JSON): {fullPath}");
                return;
            }

            UUID parsedObjectId = UUID.Zero;
            UUID parsedOwnerId = UUID.Zero;
            if (!string.IsNullOrWhiteSpace(model.TrustedObjectId)
                && UUID.TryParse(model.TrustedObjectId, out var objectId)
                && objectId != UUID.Zero)
            {
                parsedObjectId = objectId;
            }

            if (!string.IsNullOrWhiteSpace(model.TrustedOwnerId)
                && UUID.TryParse(model.TrustedOwnerId, out var ownerId)
                && ownerId != UUID.Zero)
            {
                parsedOwnerId = ownerId;
            }

            var appliedObject = false;
            var appliedOwner = false;
            lock (_dialogBridgeTrustLock)
            {
                if (_trustedDialogBridgeObjectId == UUID.Zero && parsedObjectId != UUID.Zero)
                {
                    _trustedDialogBridgeObjectId = parsedObjectId;
                    appliedObject = true;
                }

                if (_trustedDialogBridgeOwnerId == UUID.Zero && parsedOwnerId != UUID.Zero)
                {
                    _trustedDialogBridgeOwnerId = parsedOwnerId;
                    appliedOwner = true;
                }
            }

            if (appliedObject || appliedOwner)
            {
                Console.WriteLine($"[dialog-bridge] loaded persisted trust pin(s): object={_trustedDialogBridgeObjectId} owner={_trustedDialogBridgeOwnerId} from {fullPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dialog-bridge] failed to load trust-state file '{fullPath}': {ex.Message}");
        }
    }

    private void TrySaveDialogBridgeTrustStateToFile()
    {
        var fullPath = ResolveDialogBridgeTrustStateFilePath();
        if (fullPath == null)
        {
            return;
        }

        try
        {
            UUID trustedObjectId;
            UUID trustedOwnerId;
            bool requireTrustedSender;
            lock (_dialogBridgeTrustLock)
            {
                trustedObjectId = _trustedDialogBridgeObjectId;
                trustedOwnerId = _trustedDialogBridgeOwnerId;
                requireTrustedSender = _lslDialogBridgeRequireTrustedSender;
            }

            var model = new DialogBridgeTrustStateModel(
                Version: 1,
                TrustedObjectId: trustedObjectId == UUID.Zero ? null : trustedObjectId.ToString(),
                TrustedOwnerId: trustedOwnerId == UUID.Zero ? null : trustedOwnerId.ToString(),
                RequireTrustedSender: requireTrustedSender,
                UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("O"));

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(model, DialogBridgeTrustStateJsonOptions);
            File.WriteAllText(fullPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dialog-bridge] failed to save trust-state file '{fullPath}': {ex.Message}");
        }
    }

    private string? ResolveDialogBridgeTrustStateFilePath()
    {
        if (string.IsNullOrWhiteSpace(_options.LslDialogBridgeTrustStateFile))
        {
            return null;
        }

        var template = _options.LslDialogBridgeTrustStateFile.Trim();
        var botUuid = ResolveCurrentBotUuid();
        if (botUuid != null)
        {
            template = template.Replace("{bot_uuid}", botUuid.Value.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        return Path.GetFullPath(template);
    }

    private UUID? ResolveCurrentBotUuid()
    {
        var client = _client;
        if (client?.Self.AgentID is UUID liveAgentId && liveAgentId != UUID.Zero)
        {
            return liveAgentId;
        }

        return null;
    }

    private sealed record DialogBridgeTrustStateModel(
        int Version,
        string? TrustedObjectId,
        string? TrustedOwnerId,
        bool RequireTrustedSender,
        string UpdatedAtUtc);
}
