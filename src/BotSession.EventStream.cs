using System.Collections.Concurrent;
using System.Globalization;
using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private const int EventStreamMaxGeneral = 1000;
    private const int EventStreamMaxObject = 2000;
    private const int EventStreamMaxTeleport = 500;
    private const int EventStreamObjectMinIntervalMs = 250;

    private readonly object _eventStreamLock = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamGeneral = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamObject = new();
    private readonly Queue<RuntimeEventInfo> _eventStreamTeleport = new();
    private readonly SemaphoreSlim _eventStreamSignal = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<string, EventStreamSubscriptionState> _eventSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _objectEventThrottle = new(StringComparer.OrdinalIgnoreCase);
    private long _eventStreamSequence;
    private int _eventStreamTrimmedTotal;
    private int _eventStreamTrimmedGeneral;
    private int _eventStreamTrimmedObject;
    private int _eventStreamTrimmedTeleport;

    public EventStreamSubscriptionResult EventStreamSubscribe(
        string? channels,
        string? eventTypes,
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources)
    {
        var channelSet = ParseEventChannels(channels, out var channelError) ?? DefaultEventChannels();
        if (channelError != null)
        {
            return EventStreamSubscriptionResult.FailResult(channelError);
        }

        var eventTypeSet = ParseEventTypes(eventTypes);
        var filter = ParseEventFilter(radiusMeters, objectIds, objectLocalIds, chatSources, out var filterError);
        if (filterError != null)
        {
            return EventStreamSubscriptionResult.FailResult(filterError);
        }

        var cursor = GetCurrentEventCursor();
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var state = new EventStreamSubscriptionState(
            id,
            channelSet,
            eventTypeSet,
            filter,
            cursor,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        _eventSubscriptions[id] = state;

        return EventStreamSubscriptionResult.OkResult(
            id,
            cursor.ToString(CultureInfo.InvariantCulture),
            channelSet.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            eventTypeSet?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            filter.RadiusMeters,
            filter.ObjectIds?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            filter.ObjectLocalIds?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            filter.ChatSources?.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Subscription created.");
    }

    public BotToolResult EventStreamUnsubscribe(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            return BotToolResult.Fail("subscriptionId is required.");
        }

        return _eventSubscriptions.TryRemove(subscriptionId.Trim(), out _)
            ? BotToolResult.OkResult("Subscription removed.")
            : BotToolResult.Fail("Subscription was not found.");
    }

    public async Task<EventStreamPollResult> EventStreamPollAsync(
        string? subscriptionId,
        string? cursor,
        string? channels,
        string? eventTypes,
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources,
        int maxResults,
        int waitMs,
        CancellationToken cancellationToken)
    {
        var effectiveMax = Math.Clamp(maxResults, 1, 500);
        var effectiveWaitMs = Math.Clamp(waitMs, 0, 30000);

        EventStreamSubscriptionState? subscription = null;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            _eventSubscriptions.TryGetValue(subscriptionId.Trim(), out subscription);
            if (subscription == null)
            {
                return EventStreamPollResult.FailResult("Subscription was not found.");
            }
        }

        var channelSet = ParseEventChannels(channels, out var channelError)
            ?? subscription?.Channels
            ?? DefaultEventChannels();
        if (channelError != null)
        {
            return EventStreamPollResult.FailResult(channelError);
        }

        var eventTypeSet = ParseEventTypes(eventTypes) ?? subscription?.EventTypes;
        var hasFilterOverride = HasAnyEventFilterInput(radiusMeters, objectIds, objectLocalIds, chatSources);
        var overrideFilter = ParseEventFilter(radiusMeters, objectIds, objectLocalIds, chatSources, out var filterError);
        if (filterError != null)
        {
            return EventStreamPollResult.FailResult(filterError);
        }

        var effectiveFilter = hasFilterOverride
            ? overrideFilter
            : (subscription?.Filter ?? EventStreamFilterSpec.None);
        if (effectiveFilter.RadiusMeters.HasValue && !TryGetCurrentSimPosition(out var _))
        {
            return EventStreamPollResult.FailResult("radiusMeters filtering requires a connected bot with current position.");
        }

        var afterId = ParseEventCursor(cursor, out var cursorError);
        if (cursorError != null)
        {
            return EventStreamPollResult.FailResult(cursorError);
        }

        if (afterId == null)
        {
            afterId = subscription?.Cursor ?? 0L;
        }

        var started = DateTimeOffset.UtcNow;
        EventStreamReadSnapshot snapshot;
        while (true)
        {
            snapshot = ReadEventSnapshot(afterId.Value, channelSet, eventTypeSet, effectiveFilter, effectiveMax);
            if (snapshot.Events.Count > 0 || effectiveWaitMs == 0)
            {
                break;
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            var remainingMs = effectiveWaitMs - (int)elapsed.TotalMilliseconds;
            if (remainingMs <= 0)
            {
                break;
            }

            try
            {
                var signaled = await _eventStreamSignal.WaitAsync(Math.Min(remainingMs, 2000), cancellationToken).ConfigureAwait(false);
                if (!signaled)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (subscription != null)
        {
            _eventSubscriptions[subscription.Id] = subscription with
            {
                Cursor = snapshot.NextCursor,
                LastAccessAtUtc = DateTimeOffset.UtcNow
            };
        }

        var message = snapshot.CursorTrimmed
            ? "Some older events were trimmed before this cursor; returned all currently retained matching events."
            : $"Returned {snapshot.Events.Count} event(s).";

        return EventStreamPollResult.OkResult(
            snapshot.Events,
            snapshot.NextCursor.ToString(CultureInfo.InvariantCulture),
            snapshot.TrimmedTotal,
            snapshot.TrimmedGeneral,
            snapshot.TrimmedObject,
            snapshot.TrimmedTeleport,
            snapshot.CursorTrimmed,
            message);
    }

    public EventStreamHistoryResult EventStreamHistory(
        string? channels,
        string? eventTypes,
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources,
        int lastSeconds,
        int maxResults)
    {
        var channelSet = ParseEventChannels(channels, out var channelError) ?? DefaultEventChannels();
        if (channelError != null)
        {
            return EventStreamHistoryResult.FailResult(channelError);
        }

        var eventTypeSet = ParseEventTypes(eventTypes);
        var filter = ParseEventFilter(radiusMeters, objectIds, objectLocalIds, chatSources, out var filterError);
        if (filterError != null)
        {
            return EventStreamHistoryResult.FailResult(filterError);
        }

        if (filter.RadiusMeters.HasValue && !TryGetCurrentSimPosition(out var _))
        {
            return EventStreamHistoryResult.FailResult("radiusMeters filtering requires a connected bot with current position.");
        }

        var effectiveWindowSeconds = Math.Clamp(lastSeconds, 1, 1800);
        var effectiveMax = Math.Clamp(maxResults, 1, 500);
        var notBeforeUtc = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(effectiveWindowSeconds);
        var snapshot = ReadEventSnapshot(0, channelSet, eventTypeSet, filter, effectiveMax, notBeforeUtc);

        return EventStreamHistoryResult.OkResult(
            snapshot.Events,
            snapshot.NextCursor.ToString(CultureInfo.InvariantCulture),
            effectiveWindowSeconds,
            snapshot.TrimmedTotal,
            snapshot.TrimmedGeneral,
            snapshot.TrimmedObject,
            snapshot.TrimmedTeleport,
            $"Returned {snapshot.Events.Count} event(s) from the last {effectiveWindowSeconds} second(s).");
    }

    public EventStreamStatsResult EventStreamStats()
    {
        lock (_eventStreamLock)
        {
            return EventStreamStatsResult.OkResult(
                _eventStreamGeneral.Count,
                _eventStreamObject.Count,
                _eventStreamTeleport.Count,
                _eventStreamTrimmedTotal,
                _eventStreamTrimmedGeneral,
                _eventStreamTrimmedObject,
                _eventStreamTrimmedTeleport,
                _eventSubscriptions.Count,
                _eventStreamSequence.ToString(CultureInfo.InvariantCulture),
                "Current event stream buffer stats.");
        }
    }

    private void EmitRuntimeEvent(
        string channel,
        string eventType,
        string source,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        var normalizedChannel = NormalizeEventChannel(channel);
        var normalizedType = string.IsNullOrWhiteSpace(eventType) ? "unknown" : eventType.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();

        RuntimeEventInfo envelope;
        lock (_eventStreamLock)
        {
            var eventId = ++_eventStreamSequence;
            envelope = new RuntimeEventInfo(
                eventId,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                normalizedChannel,
                normalizedType,
                normalizedSource,
                string.IsNullOrWhiteSpace(message) ? normalizedType : message.Trim(),
                attributes ?? new Dictionary<string, string?>());

            var queue = GetEventQueueByChannel(normalizedChannel);
            queue.Enqueue(envelope);
            TrimEventQueueIfNeeded(normalizedChannel, queue);
        }

        // Wake long-poll listeners waiting for new events.
        _eventStreamSignal.Release();
    }

    private void OnWorldObjectUpdateForEventStream(object? sender, PrimEventArgs e)
    {
        var prim = e.Prim;
        if (prim == null)
        {
            return;
        }

        var objectId = prim.ID;
        if (objectId == UUID.Zero)
        {
            return;
        }

        var throttleKey = objectId.ToString();
        var now = DateTimeOffset.UtcNow;
        if (_objectEventThrottle.TryGetValue(throttleKey, out var lastSeen)
            && (now - lastSeen).TotalMilliseconds < EventStreamObjectMinIntervalMs)
        {
            return;
        }

        _objectEventThrottle[throttleKey] = now;
        if (_objectEventThrottle.Count > 8000)
        {
            var cutoff = now - TimeSpan.FromMinutes(5);
            foreach (var item in _objectEventThrottle)
            {
                if (item.Value < cutoff)
                {
                    _objectEventThrottle.TryRemove(item.Key, out _);
                }
            }
        }

        EmitRuntimeEvent(
            "object",
            "object.updated",
            "opensim",
            $"Object update for {objectId}.",
            new Dictionary<string, string?>
            {
                ["objectId"] = objectId.ToString(),
                ["localId"] = prim.LocalID.ToString(CultureInfo.InvariantCulture),
                ["name"] = string.IsNullOrWhiteSpace(prim.Properties?.Name) ? null : prim.Properties?.Name,
                ["simulator"] = e.Simulator?.Name,
                ["position"] = $"{prim.Position.X:0.###},{prim.Position.Y:0.###},{prim.Position.Z:0.###}"
            });
    }

    private static string NormalizeEventChannel(string channel)
    {
        var normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "object" => "object",
            "teleport" => "teleport",
            _ => "general"
        };
    }

    private static HashSet<string> DefaultEventChannels()
        => new(new[] { "general", "object", "teleport" }, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string>? ParseEventTypes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw
            .Split(new[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return parsed.Count == 0 ? null : parsed;
    }

    private static HashSet<string>? ParseEventChannels(string? raw, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var requested = raw
            .Split(new[] { ',', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .ToArray();

        if (requested.Length == 0)
        {
            return null;
        }

        if (requested.Any(value => value == "all"))
        {
            return DefaultEventChannels();
        }

        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in requested)
        {
            if (channel == "general" || channel == "object" || channel == "teleport")
            {
                accepted.Add(channel);
                continue;
            }

            error = "channels must use: general, object, teleport, or all.";
            return null;
        }

        if (accepted.Count == 0)
        {
            error = "At least one valid channel is required.";
            return null;
        }

        return accepted;
    }

    private static long? ParseEventCursor(string? cursor, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        if (!long.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            error = "cursor must be a non-negative integer string.";
            return null;
        }

        return parsed;
    }

    private long GetCurrentEventCursor()
    {
        lock (_eventStreamLock)
        {
            return _eventStreamSequence;
        }
    }

    private EventStreamReadSnapshot ReadEventSnapshot(
        long afterId,
        HashSet<string> channels,
        HashSet<string>? eventTypes,
        EventStreamFilterSpec filter,
        int maxResults,
        DateTimeOffset? notBeforeUtc = null)
    {
        lock (_eventStreamLock)
        {
            var origin = filter.RadiusMeters.HasValue && TryGetCurrentSimPosition(out var currentPosition)
                ? currentPosition
                : (Vector3?)null;
            var matchingAll = EnumerateChannelEvents(channels)
                .OrderBy(e => e.EventId)
                .Where(e => e.EventId > afterId)
                .Where(e => EventTypeMatches(e.EventType, eventTypes))
                .Where(e => EventMatchesFilter(e, filter, origin, notBeforeUtc))
                .ToList();

            var matching = notBeforeUtc.HasValue
                ? (matchingAll.Count > maxResults
                    ? matchingAll.Skip(matchingAll.Count - maxResults).ToList()
                    : matchingAll)
                : matchingAll.Take(maxResults).ToList();

            var nextCursor = matching.Count > 0 ? matching[^1].EventId : afterId;
            var oldestMatchingRetainedId = matchingAll
                .Select(e => e.EventId)
                .DefaultIfEmpty(0)
                .Min();
            var cursorTrimmed = oldestMatchingRetainedId > 0 && afterId > 0 && afterId < oldestMatchingRetainedId;

            return new EventStreamReadSnapshot(
                matching,
                nextCursor,
                cursorTrimmed,
                _eventStreamTrimmedTotal,
                _eventStreamTrimmedGeneral,
                _eventStreamTrimmedObject,
                _eventStreamTrimmedTeleport);
        }
    }

    private IEnumerable<RuntimeEventInfo> EnumerateChannelEvents(HashSet<string> channels)
    {
        if (channels.Contains("general"))
        {
            foreach (var item in _eventStreamGeneral)
            {
                yield return item;
            }
        }

        if (channels.Contains("object"))
        {
            foreach (var item in _eventStreamObject)
            {
                yield return item;
            }
        }

        if (channels.Contains("teleport"))
        {
            foreach (var item in _eventStreamTeleport)
            {
                yield return item;
            }
        }
    }

    private static bool EventTypeMatches(string eventType, HashSet<string>? filters)
    {
        return filters == null || filters.Count == 0 || filters.Contains(eventType);
    }

    private static bool EventMatchesFilter(
        RuntimeEventInfo eventInfo,
        EventStreamFilterSpec filter,
        Vector3? origin,
        DateTimeOffset? notBeforeUtc)
    {
        if (notBeforeUtc.HasValue
            && TryParseEventTimestamp(eventInfo.TimestampUtc, out var parsedTs)
            && parsedTs < notBeforeUtc.Value)
        {
            return false;
        }

        if (filter.ObjectIds != null && filter.ObjectIds.Count > 0)
        {
            if (!eventInfo.Attributes.TryGetValue("objectId", out var objectId)
                || string.IsNullOrWhiteSpace(objectId)
                || !filter.ObjectIds.Contains(objectId.Trim()))
            {
                return false;
            }
        }

        if (filter.ObjectLocalIds != null && filter.ObjectLocalIds.Count > 0)
        {
            if (!eventInfo.Attributes.TryGetValue("localId", out var localId)
                || string.IsNullOrWhiteSpace(localId)
                || !filter.ObjectLocalIds.Contains(localId.Trim()))
            {
                return false;
            }
        }

        if (filter.ChatSources != null && filter.ChatSources.Count > 0)
        {
            var fromAgentId = eventInfo.Attributes.TryGetValue("fromAgentId", out var sourceAgent) ? sourceAgent : null;
            var fromName = eventInfo.Attributes.TryGetValue("fromName", out var sourceName) ? sourceName : null;
            var sourceType = eventInfo.Attributes.TryGetValue("sourceType", out var sourceKind) ? sourceKind : null;

            var sourceMatched = filter.ChatSources.Contains(eventInfo.Source)
                || (!string.IsNullOrWhiteSpace(fromAgentId) && filter.ChatSources.Contains(fromAgentId.Trim()))
                || (!string.IsNullOrWhiteSpace(fromName) && filter.ChatSources.Contains(fromName.Trim()))
                || (!string.IsNullOrWhiteSpace(sourceType) && filter.ChatSources.Contains(sourceType.Trim()));
            if (!sourceMatched)
            {
                return false;
            }
        }

        if (!filter.RadiusMeters.HasValue)
        {
            return true;
        }

        if (!origin.HasValue)
        {
            return false;
        }

        if (!TryParseEventPosition(eventInfo, out var eventPosition))
        {
            return false;
        }

        var distance = Vector3.Distance(origin.Value, eventPosition);
        return distance <= filter.RadiusMeters.Value;
    }

    private static bool TryParseEventTimestamp(string raw, out DateTimeOffset timestamp)
    {
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static bool TryParseEventPosition(RuntimeEventInfo eventInfo, out Vector3 position)
    {
        position = Vector3.Zero;
        if (!eventInfo.Attributes.TryGetValue("position", out var rawPosition)
            || string.IsNullOrWhiteSpace(rawPosition))
        {
            eventInfo.Attributes.TryGetValue("targetPosition", out rawPosition);
        }

        if (string.IsNullOrWhiteSpace(rawPosition))
        {
            return false;
        }

        var parts = rawPosition.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        return true;
    }

    private static bool HasAnyEventFilterInput(float? radiusMeters, string? objectIds, string? objectLocalIds, string? chatSources)
    {
        return radiusMeters.HasValue
            || !string.IsNullOrWhiteSpace(objectIds)
            || !string.IsNullOrWhiteSpace(objectLocalIds)
            || !string.IsNullOrWhiteSpace(chatSources);
    }

    private EventStreamFilterSpec ParseEventFilter(
        float? radiusMeters,
        string? objectIds,
        string? objectLocalIds,
        string? chatSources,
        out string? error)
    {
        error = null;

        float? normalizedRadius = null;
        if (radiusMeters.HasValue)
        {
            if (radiusMeters.Value <= 0f)
            {
                error = "radiusMeters must be greater than 0 when provided.";
                return EventStreamFilterSpec.None;
            }

            normalizedRadius = radiusMeters.Value;
        }

        var parsedObjectIds = ParseDelimitedSet(objectIds);
        if (parsedObjectIds != null)
        {
            foreach (var objectId in parsedObjectIds)
            {
                if (!UUID.TryParse(objectId, out _))
                {
                    error = "objectIds must contain valid UUID values.";
                    return EventStreamFilterSpec.None;
                }
            }
        }

        HashSet<string>? parsedLocalIds = null;
        var localIdTokens = ParseDelimitedSet(objectLocalIds);
        if (localIdTokens != null)
        {
            parsedLocalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in localIdTokens)
            {
                if (!uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLocalId))
                {
                    error = "objectLocalIds must contain unsigned integer values.";
                    return EventStreamFilterSpec.None;
                }

                parsedLocalIds.Add(parsedLocalId.ToString(CultureInfo.InvariantCulture));
            }
        }

        var parsedChatSources = ParseDelimitedSet(chatSources);
        return new EventStreamFilterSpec(normalizedRadius, parsedObjectIds, parsedLocalIds, parsedChatSources);
    }

    private static HashSet<string>? ParseDelimitedSet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw
            .Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return parsed.Count == 0 ? null : parsed;
    }

    private bool TryGetCurrentSimPosition(out Vector3 position)
    {
        position = Vector3.Zero;
        var client = _client;
        if (!_connected || client == null)
        {
            return false;
        }

        position = client.Self.SimPosition;
        return true;
    }

    private Queue<RuntimeEventInfo> GetEventQueueByChannel(string channel)
    {
        return channel switch
        {
            "object" => _eventStreamObject,
            "teleport" => _eventStreamTeleport,
            _ => _eventStreamGeneral,
        };
    }

    private void TrimEventQueueIfNeeded(string channel, Queue<RuntimeEventInfo> queue)
    {
        var limit = channel switch
        {
            "object" => EventStreamMaxObject,
            "teleport" => EventStreamMaxTeleport,
            _ => EventStreamMaxGeneral,
        };

        while (queue.Count > limit)
        {
            queue.Dequeue();
            _eventStreamTrimmedTotal++;
            switch (channel)
            {
                case "object":
                    _eventStreamTrimmedObject++;
                    break;
                case "teleport":
                    _eventStreamTrimmedTeleport++;
                    break;
                default:
                    _eventStreamTrimmedGeneral++;
                    break;
            }
        }
    }

    private sealed record EventStreamReadSnapshot(
        IReadOnlyList<RuntimeEventInfo> Events,
        long NextCursor,
        bool CursorTrimmed,
        int TrimmedTotal,
        int TrimmedGeneral,
        int TrimmedObject,
        int TrimmedTeleport);

    private sealed record EventStreamSubscriptionState(
        string Id,
        HashSet<string> Channels,
        HashSet<string>? EventTypes,
        EventStreamFilterSpec Filter,
        long Cursor,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastAccessAtUtc);
}

internal sealed record EventStreamFilterSpec(
    float? RadiusMeters,
    HashSet<string>? ObjectIds,
    HashSet<string>? ObjectLocalIds,
    HashSet<string>? ChatSources)
{
    public static EventStreamFilterSpec None { get; } = new(null, null, null, null);
}

internal sealed record RuntimeEventInfo(
    long EventId,
    string TimestampUtc,
    string Channel,
    string EventType,
    string Source,
    string Message,
    IReadOnlyDictionary<string, string?> Attributes);

internal sealed record EventStreamSubscriptionResult(
    bool Ok,
    string Message,
    string? SubscriptionId,
    string? Cursor,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string>? EventTypes,
    float? RadiusMeters,
    IReadOnlyList<string>? ObjectIds,
    IReadOnlyList<string>? ObjectLocalIds,
    IReadOnlyList<string>? ChatSources)
{
    public static EventStreamSubscriptionResult OkResult(
        string subscriptionId,
        string cursor,
        IReadOnlyList<string> channels,
        IReadOnlyList<string>? eventTypes,
        float? radiusMeters,
        IReadOnlyList<string>? objectIds,
        IReadOnlyList<string>? objectLocalIds,
        IReadOnlyList<string>? chatSources,
        string message)
        => new(true, message, subscriptionId, cursor, channels, eventTypes, radiusMeters, objectIds, objectLocalIds, chatSources);

    public static EventStreamSubscriptionResult FailResult(string message)
        => new(false, message, null, null, Array.Empty<string>(), null, null, null, null, null);
}

internal sealed record EventStreamPollResult(
    bool Ok,
    string Message,
    string NextCursor,
    int TrimmedTotal,
    int TrimmedGeneral,
    int TrimmedObject,
    int TrimmedTeleport,
    bool CursorTrimmed,
    IReadOnlyList<RuntimeEventInfo> Events)
{
    public static EventStreamPollResult OkResult(
        IReadOnlyList<RuntimeEventInfo> events,
        string nextCursor,
        int trimmedTotal,
        int trimmedGeneral,
        int trimmedObject,
        int trimmedTeleport,
        bool cursorTrimmed,
        string message)
        => new(true, message, nextCursor, trimmedTotal, trimmedGeneral, trimmedObject, trimmedTeleport, cursorTrimmed, events);

    public static EventStreamPollResult FailResult(string message)
        => new(false, message, "0", 0, 0, 0, 0, false, Array.Empty<RuntimeEventInfo>());
}

internal sealed record EventStreamStatsResult(
    bool Ok,
    string Message,
    int GeneralBufferCount,
    int ObjectBufferCount,
    int TeleportBufferCount,
    int TrimmedTotal,
    int TrimmedGeneral,
    int TrimmedObject,
    int TrimmedTeleport,
    int SubscriptionCount,
    string CurrentCursor)
{
    public static EventStreamStatsResult OkResult(
        int generalBufferCount,
        int objectBufferCount,
        int teleportBufferCount,
        int trimmedTotal,
        int trimmedGeneral,
        int trimmedObject,
        int trimmedTeleport,
        int subscriptionCount,
        string currentCursor,
        string message)
        => new(
            true,
            message,
            generalBufferCount,
            objectBufferCount,
            teleportBufferCount,
            trimmedTotal,
            trimmedGeneral,
            trimmedObject,
            trimmedTeleport,
            subscriptionCount,
            currentCursor);

    public static EventStreamStatsResult FailResult(string message)
        => new(false, message, 0, 0, 0, 0, 0, 0, 0, 0, "0");
}

internal sealed record EventStreamHistoryResult(
    bool Ok,
    string Message,
    string NextCursor,
    int WindowSeconds,
    int TrimmedTotal,
    int TrimmedGeneral,
    int TrimmedObject,
    int TrimmedTeleport,
    IReadOnlyList<RuntimeEventInfo> Events)
{
    public static EventStreamHistoryResult OkResult(
        IReadOnlyList<RuntimeEventInfo> events,
        string nextCursor,
        int windowSeconds,
        int trimmedTotal,
        int trimmedGeneral,
        int trimmedObject,
        int trimmedTeleport,
        string message)
        => new(true, message, nextCursor, windowSeconds, trimmedTotal, trimmedGeneral, trimmedObject, trimmedTeleport, events);

    public static EventStreamHistoryResult FailResult(string message)
        => new(false, message, "0", 0, 0, 0, 0, 0, Array.Empty<RuntimeEventInfo>());
}
