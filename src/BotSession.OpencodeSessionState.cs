using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private static readonly JsonSerializerOptions OpencodeSessionStateJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const string OpencodeSessionStateDirectory = "/config/opensim-metaverse2mcp";

    private void TryLoadOpencodeSessionStateFromFile()
    {
        if (_opencodeChat == null)
        {
            return;
        }

        var fullPath = ResolveOpencodeSessionStateFilePath();
        if (fullPath == null || !File.Exists(fullPath))
        {
            return;
        }

        try
        {
            var raw = File.ReadAllText(fullPath);
            var model = JsonSerializer.Deserialize<OpencodeSessionStateModel>(raw, OpencodeSessionStateJsonOptions);
            if (model == null)
            {
                Console.WriteLine($"[opencode] session-state file ignored (empty/invalid JSON): {fullPath}");
                return;
            }

            var loadedSessionId = string.IsNullOrWhiteSpace(model.SessionId) ? null : model.SessionId.Trim();
            var loadedProviderId = string.IsNullOrWhiteSpace(model.ProviderId) ? null : model.ProviderId.Trim();
            var loadedProviderName = string.IsNullOrWhiteSpace(model.ProviderName) ? null : model.ProviderName.Trim();
            var loadedModelId = string.IsNullOrWhiteSpace(model.ModelId) ? null : model.ModelId.Trim();
            var loadedThinkingLevel = string.IsNullOrWhiteSpace(model.ThinkingLevel) ? null : model.ThinkingLevel.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(loadedProviderId)
                && !string.IsNullOrWhiteSpace(loadedModelId)
                && loadedModelId!.Contains('/'))
            {
                var slash = loadedModelId.IndexOf('/');
                if (slash > 0)
                {
                    loadedProviderId = loadedModelId[..slash];
                }
            }

            lock (_opencodeSessionStateLock)
            {
                _restoredOpencodeSessionId = loadedSessionId;
                var loadedConfig = new ImConversationConfig
                {
                    ProviderId = loadedProviderId,
                    ProviderName = loadedProviderName,
                    ModelId = loadedModelId,
                    ThinkingLevel = loadedThinkingLevel
                };
                _persistedOpencodeDefaultConfig = IsConversationConfigEmpty(loadedConfig) ? null : loadedConfig;
            }

            if (!string.IsNullOrWhiteSpace(loadedSessionId)
                || !string.IsNullOrWhiteSpace(loadedProviderId)
                || !string.IsNullOrWhiteSpace(loadedModelId)
                || !string.IsNullOrWhiteSpace(loadedThinkingLevel))
            {
                Console.WriteLine($"[opencode] loaded persisted session state: sessionId={loadedSessionId ?? "(none)"} provider={loadedProviderId ?? "(none)"} model={loadedModelId ?? "(none)"} thinking={loadedThinkingLevel ?? "(default)"} file={fullPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[opencode] failed to load session-state file '{fullPath}': {ex.Message}");
        }
    }

    private void TrySaveOpencodeSessionStateForConversation(string conversationKey, ImConversationConfig? configuredOverride = null)
    {
        if (_opencodeChat == null)
        {
            return;
        }

        var fullPath = ResolveOpencodeSessionStateFilePath();
        if (fullPath == null)
        {
            return;
        }

        try
        {
            var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
            ImConversationConfig? activeConfig = configuredOverride;
            if (activeConfig == null)
            {
                _imConversationConfigs.TryGetValue(conversationKey, out activeConfig);
            }

            var persistedSnapshot = GetPersistedDefaultConversationConfigSnapshot();
            var mergedConfig = new ImConversationConfig
            {
                ProviderId = activeConfig?.ProviderId ?? persistedSnapshot?.ProviderId,
                ProviderName = activeConfig?.ProviderName ?? persistedSnapshot?.ProviderName,
                ModelId = activeConfig?.ModelId ?? persistedSnapshot?.ModelId,
                ThinkingLevel = activeConfig?.ThinkingLevel ?? persistedSnapshot?.ThinkingLevel
            };

            lock (_opencodeSessionStateLock)
            {
                _persistedOpencodeDefaultConfig = IsConversationConfigEmpty(mergedConfig)
                    ? null
                    : CloneConversationConfig(mergedConfig);
                _restoredOpencodeSessionId = string.IsNullOrWhiteSpace(currentSessionId) ? null : currentSessionId.Trim();
            }

            var model = new OpencodeSessionStateModel(
                Version: 1,
                SessionId: string.IsNullOrWhiteSpace(currentSessionId) ? null : currentSessionId.Trim(),
                ProviderId: string.IsNullOrWhiteSpace(mergedConfig.ProviderId) ? null : mergedConfig.ProviderId.Trim(),
                ProviderName: string.IsNullOrWhiteSpace(mergedConfig.ProviderName) ? null : mergedConfig.ProviderName.Trim(),
                ModelId: string.IsNullOrWhiteSpace(mergedConfig.ModelId) ? null : mergedConfig.ModelId.Trim(),
                ThinkingLevel: string.IsNullOrWhiteSpace(mergedConfig.ThinkingLevel) ? null : mergedConfig.ThinkingLevel.Trim().ToLowerInvariant(),
                UpdatedAtUtc: DateTimeOffset.UtcNow.ToString("O"));

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(model, OpencodeSessionStateJsonOptions);
            File.WriteAllText(fullPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[opencode] failed to save session-state file '{fullPath}': {ex.Message}");
        }
    }

    private string? ResolveOpencodeSessionStateFilePath()
    {
        var botUuid = ResolveCurrentBotUuid();
        if (botUuid == null || botUuid == UUID.Zero)
        {
            return null;
        }

        return Path.Combine(OpencodeSessionStateDirectory, $"{botUuid.Value}-session.json");
    }

    private void TryBindRestoredOpencodeSessionToConversation(string conversationKey)
    {
        if (_opencodeChat == null || string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        var existingSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (!string.IsNullOrWhiteSpace(existingSessionId))
        {
            return;
        }

        string? restoredSessionId;
        lock (_opencodeSessionStateLock)
        {
            restoredSessionId = _restoredOpencodeSessionId;
            _restoredOpencodeSessionId = null;
        }

        if (string.IsNullOrWhiteSpace(restoredSessionId))
        {
            return;
        }

        _opencodeChat.SetConversationSessionId(conversationKey, restoredSessionId);
        Console.WriteLine($"[opencode] restored persisted session mapping for conversation '{conversationKey}' -> {restoredSessionId}");
    }

    private ImConversationConfig? GetPersistedDefaultConversationConfigSnapshot()
    {
        lock (_opencodeSessionStateLock)
        {
            return CloneConversationConfig(_persistedOpencodeDefaultConfig);
        }
    }

    private void SetPersistedDefaultConversationConfig(ImConversationConfig? config)
    {
        lock (_opencodeSessionStateLock)
        {
            _persistedOpencodeDefaultConfig = IsConversationConfigEmpty(config)
                ? null
                : CloneConversationConfig(config);
        }
    }

    private static bool IsConversationConfigEmpty(ImConversationConfig? config)
    {
        return config == null
            || (string.IsNullOrWhiteSpace(config.ProviderId)
                && string.IsNullOrWhiteSpace(config.ProviderName)
                && string.IsNullOrWhiteSpace(config.ModelId)
                && string.IsNullOrWhiteSpace(config.ThinkingLevel));
    }

    private static ImConversationConfig? CloneConversationConfig(ImConversationConfig? source)
    {
        if (source == null)
        {
            return null;
        }

        return new ImConversationConfig
        {
            ProviderId = source.ProviderId,
            ProviderName = source.ProviderName,
            ModelId = source.ModelId,
            ThinkingLevel = source.ThinkingLevel
        };
    }

    private void LogRetryStatusEvent(string sessionId, string? message)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var normalizedSessionId = sessionId.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? "(no retry details provided by Opencode event payload)"
            : message.Trim();

        Console.WriteLine($"[opencode] session.status retry: session={normalizedSessionId} message={normalizedMessage}");
    }

    private sealed record OpencodeSessionStateModel(
        int Version,
        string? SessionId,
        string? ProviderId,
        string? ProviderName,
        string? ModelId,
        string? ThinkingLevel,
        string UpdatedAtUtc);

}
