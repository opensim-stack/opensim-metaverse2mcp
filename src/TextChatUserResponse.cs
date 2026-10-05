namespace Opensim.Metaverse2Mcp;

using LibreMetaverse;
using System.Collections.Concurrent;

internal sealed class TextChatUserResponse : IUserResponseHandler
{
    // One active prompt per conversation; additional prompts are queued FIFO.
    private enum PendingPromptKind
    {
        Permission,
        Question
    }

    private sealed record PendingTextPromptReply(
        PendingPromptKind Kind,
        string SessionId,
        string RequestId,
        UUID AgentId,
        string From,
        HarnessPendingPermission? Permission,
        HarnessPendingQuestion? Question,
        DateTimeOffset ActivatedAt);

    private sealed record PendingPromptQueueEntry(
        PendingPromptKind Kind,
        string SessionId,
        string RequestId,
        HarnessPendingPermission? Permission,
        HarnessPendingQuestion? Question);

    private sealed class PendingPromptQueueState
    {
        public readonly object SyncRoot = new();
        public readonly Queue<PendingPromptQueueEntry> Queue = new();
        public readonly HashSet<string> EnqueuedRequestIds = new(StringComparer.OrdinalIgnoreCase);
        // RequestId currently awaiting a user reply.
        public string? ActiveRequestId;
    }

    private readonly IHarnessClient _harnessClient;
    private readonly Func<GridClient, UUID, string, string, string, Task<bool>> _tryHandleStarCommandAsync;
    private readonly Action<GridClient, UUID, string, string, string?> _sendImText;
    private readonly Func<GridClient?> _getActiveClient;
    private readonly Func<string, Task<(string ConversationKey, UUID AgentId, string From)?>> _resolveConversationForSessionAsync;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<HarnessPendingPermission>>> _getPendingPermissionsAsync;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<HarnessPendingQuestion>>> _getPendingQuestionsAsync;
    private readonly ConcurrentDictionary<string, PendingTextPromptReply> _pendingTextPromptReplyByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pendingPromptLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPromptQueueState> _pendingPromptQueuesByConversation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _rememberedDeniedPermissionSignatures = new(StringComparer.Ordinal);
    private int _disposed;

    public TextChatUserResponse(
        IHarnessClient harnessClient,
        Func<GridClient, UUID, string, string, string, Task<bool>> tryHandleStarCommandAsync,
        Action<GridClient, UUID, string, string, string?> sendImText,
        Func<GridClient?> getActiveClient,
        Func<string, Task<(string ConversationKey, UUID AgentId, string From)?>> resolveConversationForSessionAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<HarnessPendingPermission>>> getPendingPermissionsAsync,
        Func<string, CancellationToken, Task<IReadOnlyList<HarnessPendingQuestion>>> getPendingQuestionsAsync)
    {
        _harnessClient = harnessClient;
        _tryHandleStarCommandAsync = tryHandleStarCommandAsync;
        _sendImText = sendImText;
        _getActiveClient = getActiveClient;
        _resolveConversationForSessionAsync = resolveConversationForSessionAsync;
        _getPendingPermissionsAsync = getPendingPermissionsAsync;
        _getPendingQuestionsAsync = getPendingQuestionsAsync;
        _harnessClient.PendingPromptStateChanged += OnPendingPromptStateChanged;
    }

    public async Task<bool> TryHandleIncomingMessageAsync(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string text,
        bool whileRequestInFlight,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0
            || string.IsNullOrWhiteSpace(text)
            || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (text.StartsWith('*'))
        {
            if (whileRequestInFlight && !IsAllowedBusyStarCommand(text))
            {
                return false;
            }

            return await _tryHandleStarCommandAsync(client, senderAgentId, from, conversationKey, text).ConfigureAwait(false);
        }

        return await TryHandlePendingTextPromptReplyAsync(client, senderAgentId, from, conversationKey, text).ConfigureAwait(false);
    }

    public void NotifyPermissionPromptResolved(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string permissionId)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptActive(conversationKey, permissionId);
        ScheduleDrainPendingPrompts(client, senderAgentId, from, conversationKey);
    }

    public void NotifyQuestionPromptResolved(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string questionId)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptActive(conversationKey, questionId);
        ScheduleDrainPendingPrompts(client, senderAgentId, from, conversationKey);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _harnessClient.PendingPromptStateChanged -= OnPendingPromptStateChanged;
    }

    private void OnPendingPromptStateChanged(HarnessPendingPromptStateEvent promptStateEvent)
    {
        if (Volatile.Read(ref _disposed) != 0
            || promptStateEvent == null
            || string.IsNullOrWhiteSpace(promptStateEvent.SessionId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await HandlePendingPromptStateChangedAsync(promptStateEvent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[opencode:event] pending prompt callback error: {ex.Message}");
            }
        });
    }

    private static bool IsAllowedBusyStarCommand(string text)
        => text.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("*usage", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("*help", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("*permission", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("*question", StringComparison.OrdinalIgnoreCase);

    private bool HasActivePromptForConversation(string conversationKey)
        => _pendingTextPromptReplyByConversation.ContainsKey(conversationKey);

    private PendingPromptQueueState GetPendingPromptQueueState(string conversationKey)
        => _pendingPromptQueuesByConversation.GetOrAdd(conversationKey, _ => new PendingPromptQueueState());

    private void EnqueuePendingPromptEntries(string conversationKey, IEnumerable<PendingPromptQueueEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.RequestId)
                    || (!string.IsNullOrWhiteSpace(state.ActiveRequestId)
                        && entry.RequestId.Equals(state.ActiveRequestId, StringComparison.OrdinalIgnoreCase))
                    || state.EnqueuedRequestIds.Contains(entry.RequestId))
                {
                    // Drop duplicates for deterministic prompt ordering and idempotent event handling.
                    continue;
                }

                state.Queue.Enqueue(entry);
                state.EnqueuedRequestIds.Add(entry.RequestId);
            }
        }
    }

    private bool TryDequeueNextPendingPromptEntry(string conversationKey, out PendingPromptQueueEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return false;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            while (state.Queue.Count > 0)
            {
                var candidate = state.Queue.Dequeue();
                state.EnqueuedRequestIds.Remove(candidate.RequestId);
                if (!string.IsNullOrWhiteSpace(state.ActiveRequestId)
                    && candidate.RequestId.Equals(state.ActiveRequestId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                state.ActiveRequestId = candidate.RequestId;
                entry = candidate;
                return true;
            }
        }

        return false;
    }

    private void MarkPendingPromptActive(string conversationKey, string requestId)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            state.ActiveRequestId = requestId.Trim();
            state.EnqueuedRequestIds.Remove(state.ActiveRequestId);
        }
    }

    private void ClearPendingPromptActive(string conversationKey, string requestId)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(state.ActiveRequestId)
                && state.ActiveRequestId.Equals(requestId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                state.ActiveRequestId = null;
            }
        }
    }

    private async Task SeedPendingPromptQueueFromSnapshotAsync(string conversationKey, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var state = GetPendingPromptQueueState(conversationKey);
        lock (state.SyncRoot)
        {
            if (state.Queue.Count > 0 || !string.IsNullOrWhiteSpace(state.ActiveRequestId))
            {
                return;
            }
        }

        var permissions = await _getPendingPermissionsAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var questions = await _getPendingQuestionsAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (permissions.Count == 0 && questions.Count == 0)
        {
            return;
        }

        var entries = new List<PendingPromptQueueEntry>(permissions.Count + questions.Count);
        foreach (var permission in permissions)
        {
            if (!string.IsNullOrWhiteSpace(permission.Id))
            {
                entries.Add(new PendingPromptQueueEntry(PendingPromptKind.Permission, string.IsNullOrWhiteSpace(permission.SessionId) ? sessionId : permission.SessionId, permission.Id, permission, null));
            }
        }

        foreach (var question in questions)
        {
            if (!string.IsNullOrWhiteSpace(question.Id))
            {
                entries.Add(new PendingPromptQueueEntry(PendingPromptKind.Question, string.IsNullOrWhiteSpace(question.SessionId) ? sessionId : question.SessionId, question.Id, null, question));
            }
        }

        if (entries.Count > 0)
        {
            EnqueuePendingPromptEntries(conversationKey, entries);
        }
    }

    private void ScheduleDrainPendingPrompts(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DrainPendingPromptsAsync(client, agentId, from, conversationKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[opencode:event] pending prompt drain error: {ex.Message}");
            }
        });
    }

    private async Task DrainPendingPromptsAsync(GridClient client, UUID agentId, string from, string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || HasActivePromptForConversation(conversationKey))
        {
            return;
        }

        // Per-conversation gate prevents concurrent event snapshots from announcing multiple prompts at once.
        var promptGate = _pendingPromptLocks.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
        await promptGate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (string.IsNullOrWhiteSpace(conversationKey) || HasActivePromptForConversation(conversationKey))
            {
                return;
            }

            var sessionId = _harnessClient.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                await SeedPendingPromptQueueFromSnapshotAsync(conversationKey, sessionId).ConfigureAwait(false);
            }

            if (TryDequeueNextPendingPromptEntry(conversationKey, out var nextEntry)
                && nextEntry != null)
            {
                sessionId ??= nextEntry.SessionId;
                if (nextEntry.Kind == PendingPromptKind.Permission)
                {
                    if (nextEntry.Permission != null)
                    {
                        await OfferPermissionPromptWithFallbackAsync(client, agentId, from, conversationKey, string.IsNullOrWhiteSpace(nextEntry.Permission.SessionId) ? sessionId : nextEntry.Permission.SessionId, nextEntry.Permission).ConfigureAwait(false);
                    }
                }
                else if (nextEntry.Question != null)
                {
                    OfferQuestionPromptWithFallback(client, agentId, from, conversationKey, string.IsNullOrWhiteSpace(nextEntry.Question.SessionId) ? sessionId : nextEntry.Question.SessionId, nextEntry.Question);
                }
            }
        }
        finally
        {
            promptGate.Release();
        }
    }

    private async Task HandlePendingPromptStateChangedAsync(HarnessPendingPromptStateEvent promptStateEvent)
    {
        if (promptStateEvent == null || string.IsNullOrWhiteSpace(promptStateEvent.SessionId))
        {
            return;
        }

        var route = await _resolveConversationForSessionAsync(promptStateEvent.SessionId).ConfigureAwait(false);
        if (!route.HasValue || string.IsNullOrWhiteSpace(route.Value.ConversationKey) || route.Value.AgentId == UUID.Zero)
        {
            return;
        }

        var client = _getActiveClient();
        if (client == null)
        {
            return;
        }

        var conversationKey = route.Value.ConversationKey;
        var queueEntries = new List<PendingPromptQueueEntry>();
        foreach (var permission in promptStateEvent.PendingPermissions)
        {
            if (!string.IsNullOrWhiteSpace(permission?.Id))
            {
                queueEntries.Add(new PendingPromptQueueEntry(PendingPromptKind.Permission, string.IsNullOrWhiteSpace(permission.SessionId) ? promptStateEvent.SessionId : permission.SessionId, permission.Id, permission, null));
            }
        }

        foreach (var question in promptStateEvent.PendingQuestions)
        {
            if (!string.IsNullOrWhiteSpace(question?.Id))
            {
                queueEntries.Add(new PendingPromptQueueEntry(PendingPromptKind.Question, string.IsNullOrWhiteSpace(question.SessionId) ? promptStateEvent.SessionId : question.SessionId, question.Id, null, question));
            }
        }

        if (queueEntries.Count > 0)
        {
            EnqueuePendingPromptEntries(conversationKey, queueEntries);
        }

        await DrainPendingPromptsAsync(client, route.Value.AgentId, route.Value.From, conversationKey).ConfigureAwait(false);
    }

    private async Task OfferPermissionPromptWithFallbackAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string sessionId,
        HarnessPendingPermission permission)
    {
        if (string.IsNullOrWhiteSpace(permission.Id))
        {
            return;
        }

        var deniedSignature = BuildPermissionSignature(permission);
        if (!string.IsNullOrWhiteSpace(deniedSignature)
            && _rememberedDeniedPermissionSignatures.ContainsKey(deniedSignature))
        {
            MarkPendingPromptActive(conversationKey, permission.Id);
            try
            {
                await _harnessClient.RespondToPermissionAsync(
                    sessionId,
                    permission.Id,
                    response: "reject",
                    remember: true,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[prompt] remembered deny auto-reject failed for {conversationKey}: {ex.Message}");
                ClearPendingPromptActive(conversationKey, permission.Id);
                // Fall back to explicit user prompt when automatic rejection cannot be submitted.
                ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Permission, sessionId, permission.Id, permission: permission);
                return;
            }

            NotifyPermissionPromptResolved(client, agentId, from, conversationKey, permission.Id);
            return;
        }

        if (HasActivePromptForConversation(conversationKey))
        {
            return;
        }

        MarkPendingPromptActive(conversationKey, permission.Id);
        ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Permission, sessionId, permission.Id, permission: permission);
    }

    private void OfferQuestionPromptWithFallback(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string sessionId,
        HarnessPendingQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.Id))
        {
            return;
        }

        if (HasActivePromptForConversation(conversationKey))
        {
            return;
        }

        MarkPendingPromptActive(conversationKey, question.Id);
        ActivateTextPromptFallback(client, conversationKey, agentId, from, PendingPromptKind.Question, sessionId, question.Id, question: question);
    }

    private void ActivateTextPromptFallback(
        GridClient client,
        string conversationKey,
        UUID agentId,
        string from,
        PendingPromptKind kind,
        string sessionId,
        string requestId,
        HarnessPendingPermission? permission = null,
        HarnessPendingQuestion? question = null)
    {
        MarkPendingPromptActive(conversationKey, requestId);
        var state = new PendingTextPromptReply(
            kind,
            sessionId,
            requestId,
            agentId,
            from,
            permission,
            question,
            DateTimeOffset.UtcNow);

        _pendingTextPromptReplyByConversation[conversationKey] = state;

        var promptText = kind == PendingPromptKind.Permission
            ? BuildTextFallbackPermissionPrompt(permission ?? new HarnessPendingPermission(requestId, sessionId, string.Empty, null))
            : BuildTextFallbackQuestionPrompt(question ?? new HarnessPendingQuestion(requestId, sessionId, "Question", "Please answer.", Array.Empty<string>(), null, true));
        _sendImText(client, agentId, from, promptText, conversationKey);
    }

    private async Task<bool> TryHandlePendingTextPromptReplyAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.StartsWith('*'))
        {
            return false;
        }

        if (!_pendingTextPromptReplyByConversation.TryGetValue(conversationKey, out var state))
        {
            return false;
        }

        if (state.Kind == PendingPromptKind.Permission)
        {
            if (!TryParseSimplePermissionResponse(text, out var response, out var remember))
            {
                _sendImText(client, agentId, from,
                    "I could not understand that approval choice. Reply with: yes, no, yes always, or no always.",
                    conversationKey);
                return true;
            }

            if (!await TrySubmitPendingTextPromptReplyAsync(
                    client,
                    agentId,
                    from,
                    conversationKey,
                    promptKindName: "permission",
                    submitFailureMessage: "I could not submit that approval yet. Please reply again in a moment.",
                    notAcceptedMessage: "That approval was not accepted yet. Please reply again in a moment.",
                    submitAsync: () => _harnessClient.RespondToPermissionAsync(state.SessionId, state.RequestId, response, remember, CancellationToken.None)).ConfigureAwait(false))
            {
                return true;
            }

            var signature = BuildPermissionSignature(state.Permission);
            if (!string.IsNullOrWhiteSpace(signature))
            {
                if (response.Equals("reject", StringComparison.OrdinalIgnoreCase) && remember)
                {
                    _rememberedDeniedPermissionSignatures[signature] = 1;
                }
                else if (response.Equals("allow", StringComparison.OrdinalIgnoreCase) && remember)
                {
                    _rememberedDeniedPermissionSignatures.TryRemove(signature, out _);
                }
            }

            FinalizeSuccessfulPendingTextPromptReply(client, agentId, from, conversationKey, state);
            return true;
        }

        var resolved = text.Trim();
        if (state.Question != null)
        {
            if (!TryResolveQuestionAnswer(state.Question, text, out resolved))
            {
                _sendImText(client, agentId, from,
                    "I could not map that answer to the question options. Reply with option number or exact option text.",
                    conversationKey);
                return true;
            }
        }

        if (!await TrySubmitPendingTextPromptReplyAsync(
                client,
                agentId,
                from,
                conversationKey,
                promptKindName: "question",
                submitFailureMessage: "I could not submit that answer yet. Please reply again in a moment.",
                notAcceptedMessage: "That answer was not accepted yet. Please reply again in a moment.",
                submitAsync: () => _harnessClient.ReplyToQuestionAsync(state.SessionId, state.RequestId, new[] { resolved }, CancellationToken.None)).ConfigureAwait(false))
        {
            return true;
        }

        FinalizeSuccessfulPendingTextPromptReply(client, agentId, from, conversationKey, state);
        return true;
    }

    private async Task<bool> TrySubmitPendingTextPromptReplyAsync(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        string promptKindName,
        string submitFailureMessage,
        string notAcceptedMessage,
        Func<Task<bool>> submitAsync)
    {
        bool accepted;
        try
        {
            accepted = await submitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[prompt] failed to submit {promptKindName} reply for {conversationKey}: {ex.Message}");
            _sendImText(client, agentId, from, submitFailureMessage, conversationKey);
            return false;
        }

        if (accepted)
        {
            return true;
        }

        _sendImText(client, agentId, from, notAcceptedMessage, conversationKey);
        return false;
    }

    private void FinalizeSuccessfulPendingTextPromptReply(
        GridClient client,
        UUID agentId,
        string from,
        string conversationKey,
        PendingTextPromptReply state)
    {
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptActive(conversationKey, state.RequestId);
        ScheduleDrainPendingPrompts(client, agentId, from, conversationKey);
    }

    private static string BuildTextFallbackPermissionPrompt(HarnessPendingPermission permission)
    {
        var summary = permission.Description?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = permission.Title?.Trim();
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = "This action requires your approval.";
        }

        var lines = new List<string> { summary };
        lines.Add("Reply now with: yes, no, yes always, or no always.");
        return string.Join("\n", lines);
    }

    private static string BuildTextFallbackQuestionPrompt(HarnessPendingQuestion question)
    {
        var lines = new List<string>
        {
            $"{question.Header}: {question.Question}"
        };

        if (question.Options.Count > 0)
        {
            for (var i = 0; i < question.Options.Count; i++)
            {
                lines.Add($"{i + 1}) {question.Options[i]}");
            }
        }

        lines.Add("Your next message will be used as the answer.");
        return string.Join("\n", lines);
    }

    private static bool TryParseSimplePermissionResponse(string text, out string response, out bool remember)
    {
        response = string.Empty;
        remember = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim().ToLowerInvariant();
        var compact = normalized
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal);
        compact = string.Join(" ", compact.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (compact is "1" or "yes" or "y" or "allow")
        {
            response = "allow";
            return true;
        }

        if (compact is "3" or "yes always" or "always yes" or "yes remember" or "y always" or "allow always")
        {
            response = "allow";
            remember = true;
            return true;
        }

        if (compact is "2" or "no" or "n" or "reject" or "deny")
        {
            response = "reject";
            return true;
        }

        if (compact is "4" or "no always" or "always no" or "no remember" or "n always" or "reject always" or "deny always")
        {
            response = "reject";
            remember = true;
            return true;
        }

        return false;
    }

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

    private static string? BuildPermissionSignature(HarnessPendingPermission? permission)
    {
        if (permission == null)
        {
            return null;
        }

        var patterns = ExtractPermissionPatterns(permission.Description);
        if (patterns.Count > 0)
        {
            return "patterns:" + string.Join("|", patterns);
        }

        var summary = permission.Description;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = permission.Title;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        var compact = string.Join(" ", summary
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2))
            .ToLowerInvariant();
        compact = string.Join(" ", compact.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return compact.Length == 0 ? null : "summary:" + compact;
    }

    private static IReadOnlyList<string> ExtractPermissionPatterns(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return Array.Empty<string>();
        }

        var patterns = new List<string>();
        var collect = false;
        var lines = description.Split('\n', StringSplitOptions.TrimEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith(':'))
            {
                collect = line.Contains("pattern", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("remembered", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!collect)
            {
                continue;
            }

            if (!line.StartsWith("- ", StringComparison.Ordinal))
            {
                collect = false;
                continue;
            }

            var pattern = line[2..].Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                patterns.Add(pattern);
            }
        }

        return patterns
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }
}
