using System.Text.Json;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private readonly object _hoverStateLock = new();
    private const int HoverBusyUpdateMinimumIntervalMs = 600;

    public async Task<BotToolResult> SetBotMoodAsync(string emotion, CancellationToken cancellationToken)
    {
        var normalizedEmotion = NormalizeMoodName(emotion);
        if (string.IsNullOrWhiteSpace(normalizedEmotion))
        {
            return BotToolResult.Fail("emotion is required and must contain letters, numbers, '-' or '_'.");
        }

        return await ExecuteLockedAsync((client, _) =>
        {
            var message = $"[mood] {normalizedEmotion}";
            client.Self.Chat(message, 3645376, ChatType.Normal);
            Console.WriteLine($"[mood] broadcast: {normalizedEmotion}");
            return Task.FromResult(BotToolResult.OkResult($"Broadcast mood change on say channel: {message}"));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DataToolResult> BotMoodListAsync(bool includeUtilityTextures, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var moodNames = new List<string>
        {
            "neutral",
            "happy",
            "sad",
            "angry",
            "surprised",
            "confused",
            "excited",
            "tired"
        };

        var utilityNames = new[] { "base", "cross" };
        if (includeUtilityTextures)
        {
            moodNames.AddRange(utilityNames);
        }

        var payload = JsonSerializer.Serialize(new
        {
            includeUtilityTextures,
            moodCount = moodNames.Count,
            utilityTextures = utilityNames,
            moodNames
        });

        return Task.FromResult(DataToolResult.OkResult($"Returned {moodNames.Count} available mood name(s).", payload));
    }

    private static string NormalizeMoodName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        if (chars.Length == 0)
        {
            return string.Empty;
        }

        var normalized = new string(chars).ToLowerInvariant();
        return normalized.Length <= 48 ? normalized : normalized[..48];
    }

    private void UpdateBusyHoverText(bool incrementDots)
    {
        lock (_hoverStateLock)
        {
            if (incrementDots)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastHoverBusyUpdateAt).TotalMilliseconds < HoverBusyUpdateMinimumIntervalMs)
                {
                    return;
                }

                _lastHoverBusyUpdateAt = now;
                _busyHoverDots = (_busyHoverDots % 4) + 1;
            }
            else if (_busyHoverDots <= 0)
            {
                _busyHoverDots = 1;
            }
        }
    }

    private void ClearBusyHoverText()
    {
        lock (_hoverStateLock)
        {
            _busyHoverDots = 0;
            _lastHoverBusyUpdateAt = DateTimeOffset.MinValue;
        }
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

    private static string BuildCompactPermissionDialogPrompt(HarnessPendingPermission permission)
    {
        var description = permission.Description?.Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            return CompactPermissionSummary(GetPermissionPrimaryText(permission, out _));
        }

        var lines = description
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var firstPattern = lines
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l[2..].Trim() : l.Trim())
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)
                && !l.EndsWith(":", StringComparison.Ordinal)
                && !l.StartsWith("remembered", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(firstPattern))
        {
            return CompactPermissionSummary(firstPattern);
        }

        return CompactPermissionSummary(lines[0]);
    }

    private static string CompactPermissionSummary(string? rawText, int maxChars = 120, int maxTokens = 10)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var firstLine = rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var candidate = string.IsNullOrWhiteSpace(firstLine) ? rawText.Trim() : firstLine;

        candidate = string.Join(" ", candidate.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (LooksLikePermissionRequestId(candidate))
        {
            return "Approval required";
        }

        if (maxTokens > 0)
        {
            var tokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > maxTokens)
            {
                candidate = string.Join(" ", tokens.Take(maxTokens)) + " ...";
            }
        }

        if (maxChars > 0 && candidate.Length > maxChars)
        {
            candidate = candidate[..Math.Max(1, maxChars - 3)] + "...";
        }

        return candidate;
    }

    private static bool LooksLikePermissionRequestId(string value)
        => !string.IsNullOrWhiteSpace(value)
            && (value.StartsWith("per_", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("que_", StringComparison.OrdinalIgnoreCase));

    private static bool TryResolveQuestionAnswer(HarnessPendingQuestion question, string text, out string answer)
    {
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim();
        var normalized = raw.ToLowerInvariant();
        var options = question.Options ?? Array.Empty<string>();

        if (options.Count > 0)
        {
            if (int.TryParse(normalized, out var optionIndex)
                && optionIndex >= 1
                && optionIndex <= options.Count)
            {
                answer = options[optionIndex - 1];
                return true;
            }

            var exact = options.FirstOrDefault(o => o.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exact))
            {
                answer = exact;
                return true;
            }

            if (normalized is "yes" or "y")
            {
                var yesOption = options.FirstOrDefault(o => o.Contains("yes", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(yesOption))
                {
                    answer = yesOption;
                    return true;
                }
            }

            if (normalized is "no" or "n")
            {
                var noOption = options.FirstOrDefault(o => o.Contains("no", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(noOption))
                {
                    answer = noOption;
                    return true;
                }
            }
        }

        if (question.AllowsCustom != false)
        {
            answer = raw;
            return true;
        }

        return false;
    }
}