using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private void OnInstantMessage(object? sender, InstantMessageEventArgs e)
    {
        var client = _client;
        if (client == null || e.IM.FromAgentID == client.Self.AgentID)
        {
            return;
        }

        var from = e.IM.FromAgentName;
        var text = e.IM.Message?.Trim() ?? string.Empty;
        var isDialogBridgePayload = text.StartsWith(LslDialogBridgeReplyPrefix + "|", StringComparison.OrdinalIgnoreCase);
        if (e.IM.Dialog != InstantMessageDialog.MessageFromAgent
            && e.IM.Dialog != InstantMessageDialog.SessionSend
            && e.IM.Dialog != InstantMessageDialog.MessageFromObject
            && !isDialogBridgePayload)
        {
            // Diagnostic visibility for viewer/system IM dialogs (including voice-call control signals).
            Console.WriteLine($"[im][diag] ignored dialog={e.IM.Dialog} group={e.IM.GroupIM} session={e.IM.IMSessionID} from={from} to={e.IM.ToAgentID} text={SanitizeImLogText(text)}");
            return;
        }

        Console.WriteLine($"[im] ({e.IM.Dialog}, group={e.IM.GroupIM}, session={e.IM.IMSessionID}, to={e.IM.ToAgentID}) {from}: {SanitizeImLogText(text)}");
        EmitRuntimeEvent(
            "general",
            "chat.im.received",
            "opensim",
            $"IM received from {from}.",
            new Dictionary<string, string?>
            {
                ["fromAgentId"] = e.IM.FromAgentID.ToString(),
                ["fromName"] = from,
                ["dialog"] = e.IM.Dialog.ToString(),
                ["isGroup"] = e.IM.GroupIM.ToString(),
                ["sessionId"] = e.IM.IMSessionID.ToString(),
                ["text"] = SanitizeImLogText(text)
            });

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (e.IM.Dialog == InstantMessageDialog.MessageFromObject || isDialogBridgePayload)
        {
            var bridgeSenderObjectId = e.IM.FromAgentID;
            if (isDialogBridgePayload && e.IM.IMSessionID != UUID.Zero)
            {
                // For object-origin payloads, IMSessionID carries the object UUID in OpenSim.
                bridgeSenderObjectId = e.IM.IMSessionID;
            }

            _ = Task.Run(async () =>
            {
                await TryHandleLslDialogBridgeReplyAsync(client, bridgeSenderObjectId, e.IM.FromAgentName, text).ConfigureAwait(false);
            });
            return;
        }

        if (IsLikelyTypingIndicator(e.IM, text))
        {
            Console.WriteLine($"[im] typing indicator ignored for {from} ({e.IM.Dialog}).");
            return;
        }

        if (IsDuplicateImEvent(e.IM.FromAgentID, text, e.IM.Timestamp))
        {
            Console.WriteLine($"[im] duplicate suppressed for {from} ({e.IM.Dialog}).");
            return;
        }

        var isGroupIm = IsGroupSessionMessage(e.IM);
        if (isGroupIm && !_receiveChatAllowedTypes.Contains(ChatType.Normal))
        {
            Console.WriteLine($"[group] ignored ({e.IM.Dialog}, group={e.IM.GroupIM}) {from}: modality filtered by LOCAL_CHAT_ALLOWED_TYPES (mapped as Normal)");
            return;
        }

        var groupSessionId = ResolveGroupSessionId(e.IM);
        var conversationKey = isGroupIm
            ? BuildGroupConversationKey(groupSessionId)
            : BuildImConversationKey(e.IM.FromAgentID);
        RegisterConversationRoute(
            conversationKey,
            isGroupIm ? ConversationChannel.Group : ConversationChannel.Im,
            isGroupIm ? groupSessionId : e.IM.FromAgentID,
            e.IM.FromAgentID,
            from);
        lock (_recentImSpeakerLock)
        {
            _lastImSpeakerAgentId = e.IM.FromAgentID;
            _lastImSpeakerName = from;
            _lastImConversationKey = conversationKey;
        }

        _ = Task.Run(() => HandleIncomingConversationMessageAsync(
            client,
            e.IM.FromAgentID,
            from,
            conversationKey,
            text,
            isGroupIm ? $"OpenSim group chat with {from}" : $"OpenSim IM with {from}"));
    }

    private static bool IsGroupSessionMessage(InstantMessage message)
        => message.GroupIM || (message.Dialog == InstantMessageDialog.SessionSend && message.IMSessionID != UUID.Zero);

    private static UUID ResolveGroupSessionId(InstantMessage message)
    {
        if (message.IMSessionID != UUID.Zero)
        {
            return message.IMSessionID;
        }

        return message.ToAgentID;
    }

    private static string BuildImConversationKey(UUID senderAgentId)
        => $"im-{senderAgentId}";

    private static string BuildGroupConversationKey(UUID groupId)
        => $"group-{groupId}";

    private void RegisterConversationRoute(
        string conversationKey,
        ConversationChannel channel,
        UUID replyTargetId,
        UUID speakerAgentId,
        string speakerName)
    {
        _conversationRouteByKey[conversationKey] = new ConversationRoute(channel, replyTargetId, speakerAgentId, speakerName);
        if (speakerAgentId != UUID.Zero)
        {
            _conversationAgentByKey[conversationKey] = speakerAgentId;
            _conversationKeyBySpeakerAgent[speakerAgentId] = conversationKey;
        }

        _conversationNameByKey[conversationKey] = speakerName;
    }

    private async Task HandleIncomingConversationMessageAsync(
        GridClient client,
        UUID senderAgentId,
        string from,
        string conversationKey,
        string text,
        string title)
    {
        var priorAmbientConversationKey = _ambientConversationKey.Value;
        _ambientConversationKey.Value = conversationKey;
        var channelLabel = GetConversationChannelLabel(conversationKey);
        if (!TryNormalizeConversationTextForRouting(conversationKey, text, out var routedText))
        {
            Console.WriteLine($"[{channelLabel}] ignored message without wake word from {from} ({conversationKey}).");
            _ambientConversationKey.Value = priorAmbientConversationKey;
            return;
        }

        var access = await EvaluateConversationAccessAsync(client, senderAgentId, from, conversationKey, CancellationToken.None).ConfigureAwait(false);
        if (!access.Allowed)
        {
            Console.WriteLine($"[{channelLabel}] denied sender {from} ({senderAgentId}) for {conversationKey}: {access.Reason}");
            SendImText(client, senderAgentId, from, access.DenialMessage, conversationKey);
            _ambientConversationKey.Value = priorAmbientConversationKey;
            return;
        }

        var gate = _conversationLocks.GetOrAdd(conversationKey, _ => new SemaphoreSlim(1, 1));
        CancellationTokenSource? inFlightRequestCts = null;
        var globalGateHeld = false;
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            try
            {
                if (routedText.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase)
                    || routedText.StartsWith("*usage", StringComparison.OrdinalIgnoreCase)
                    || routedText.StartsWith("*help", StringComparison.OrdinalIgnoreCase)
                    || routedText.StartsWith("*dialog", StringComparison.OrdinalIgnoreCase)
                    || routedText.StartsWith("*dialogs", StringComparison.OrdinalIgnoreCase)
                    || routedText.StartsWith("*permission", StringComparison.OrdinalIgnoreCase)
                    || routedText.StartsWith("*question", StringComparison.OrdinalIgnoreCase))
                {
                    var handledBusyCommand = await TryHandleStarCommandAsync(client, senderAgentId, from, conversationKey, routedText).ConfigureAwait(false);
                    if (handledBusyCommand)
                    {
                        return;
                    }
                }

                var handledBusyDialog = TryHandlePendingScriptDialogBeforeRouting(client, senderAgentId, from, conversationKey, routedText);
                if (handledBusyDialog)
                {
                    return;
                }

                var handledBusyPromptReply = await TryHandlePendingTextPromptReplyBeforeRoutingAsync(
                    client,
                    senderAgentId,
                    from,
                    conversationKey,
                    routedText).ConfigureAwait(false);
                if (handledBusyPromptReply)
                {
                    return;
                }

                if (routedText.StartsWith("*cancel", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCancelCommandAsync(client, senderAgentId, from, conversationKey).ConfigureAwait(false);
                    return;
                }

                try
                {
                    Console.WriteLine($"[{channelLabel}] overlapping message while previous request is still in flight for {from} ({conversationKey}).");
                    SendImText(client, senderAgentId, from, "I am still working on your previous request. You can send *cancel to abort while waiting.", conversationKey);
                }
                catch
                {
                    // Ignore failures while trying to report overlap state.
                }

                return;
            }
            finally
            {
                _ambientConversationKey.Value = priorAmbientConversationKey;
            }
        }

        var startedAt = Stopwatch.StartNew();
        try
        {
            if (_opencodeChat == null)
            {
                SendImText(client, senderAgentId, from, "AI chat is currently disabled by configuration.", conversationKey);
                return;
            }

            if (routedText.StartsWith('*'))
            {
                var handled = await TryHandleStarCommandAsync(client, senderAgentId, from, conversationKey, routedText).ConfigureAwait(false);
                if (handled)
                {
                    return;
                }
            }

            var handledDialog = TryHandlePendingScriptDialogBeforeRouting(client, senderAgentId, from, conversationKey, routedText);
            if (handledDialog)
            {
                return;
            }

            var handledPromptReply = await TryHandlePendingTextPromptReplyBeforeRoutingAsync(
                client,
                senderAgentId,
                from,
                conversationKey,
                routedText).ConfigureAwait(false);
            if (handledPromptReply)
            {
                return;
            }

            if (!routedText.StartsWith('*') && !await _globalConversationGate.WaitAsync(0).ConfigureAwait(false))
            {
                SendImText(
                    client,
                    senderAgentId,
                    from,
                    "Sorry, I am currently busy with another chat session. Please wait and try again shortly.",
                    conversationKey);
                return;
            }

            globalGateHeld = !routedText.StartsWith('*');

            TryBindRestoredOpencodeSessionToConversation(conversationKey);
            var sendOptions = BuildSendOptions(conversationKey, senderAgentId, from);
            using var requestCts = new CancellationTokenSource();
            inFlightRequestCts = requestCts;
            _inFlightRequestCtsByConversation.AddOrUpdate(
                conversationKey,
                requestCts,
                (_, previous) =>
                {
                    try
                    {
                        previous.Cancel();
                    }
                    catch
                    {
                        // Best effort: old inflight token may already be disposed.
                    }

                    previous.Dispose();
                    return requestCts;
                });

            using var inFlightQuestionWatchCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token);
            var inFlightQuestionWatchTask = Task.Run(() =>
                NotifyPendingQuestionDuringInFlightRequestAsync(
                    client,
                    senderAgentId,
                    from,
                    conversationKey,
                    inFlightQuestionWatchCts.Token));

            Console.WriteLine($"[{channelLabel}] routing to opencode: from={from} conversation={conversationKey} textLength={routedText.Length} model={(sendOptions?.ModelId ?? "(default)")}");
            var reply = await _opencodeChat.SendMessageAsync(
                conversationKey: conversationKey,
                title: title,
                message: routedText,
                options: sendOptions,
                cancellationToken: requestCts.Token).ConfigureAwait(false);
            if (reply.Usage != null)
            {
                _latestUsageByConversation[conversationKey] = reply.Usage;
            }

            TrySaveOpencodeSessionStateForConversation(conversationKey);
            startedAt.Stop();
            Console.WriteLine($"[{channelLabel}] opencode reply received in {startedAt.ElapsedMilliseconds}ms: from={from} conversation={conversationKey} replyLength={reply.Text.Length}");

            var responseText = reply.IsConfirmationPrompt
                ? reply.Text + "\n\nReply with yes or no to continue."
                : reply.Text;

            if (reply.PendingPermissions != null && reply.PendingPermissions.Count > 0)
            {
                var latestPermission = reply.PendingPermissions
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Id));
                if (latestPermission != null)
                {
                    await OfferPermissionPromptWithFallbackAsync(client, senderAgentId, from, conversationKey, latestPermission.SessionId, latestPermission).ConfigureAwait(false);
                }
            }
            else
            {
                var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                if (!string.IsNullOrWhiteSpace(currentSessionId))
                {
                    var eventFirstPermissions = await GetPendingPermissionsEventFirstAsync(currentSessionId, CancellationToken.None).ConfigureAwait(false);
                    if (eventFirstPermissions.Count > 0)
                    {
                        var latestPermission = eventFirstPermissions[0];
                        if (!_announcedPendingPermissionByConversation.TryGetValue(conversationKey, out var announcedPermissionId)
                            || !announcedPermissionId.Equals(latestPermission.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            await OfferPermissionPromptWithFallbackAsync(client, senderAgentId, from, conversationKey, currentSessionId, latestPermission).ConfigureAwait(false);
                        }
                    }
                }
            }

            if (reply.PendingQuestions != null && reply.PendingQuestions.Count > 0)
            {
                var latestQuestion = reply.PendingQuestions
                    .FirstOrDefault(q => !string.IsNullOrWhiteSpace(q.Id));
                if (latestQuestion != null)
                {
                    await OfferQuestionPromptWithFallbackAsync(client, senderAgentId, from, conversationKey, latestQuestion.SessionId, latestQuestion).ConfigureAwait(false);
                }
            }
            else
            {
                var currentSessionId = _opencodeChat.GetConversationSessionId(conversationKey);
                if (!string.IsNullOrWhiteSpace(currentSessionId))
                {
                    var polledQuestions = await GetPendingQuestionsEventFirstAsync(currentSessionId, CancellationToken.None).ConfigureAwait(false);
                    if (polledQuestions.Count > 0)
                    {
                        if (!_announcedPendingQuestionByConversation.TryGetValue(conversationKey, out var announcedQuestionId)
                            || !announcedQuestionId.Equals(polledQuestions[0].Id, StringComparison.OrdinalIgnoreCase))
                        {
                            await OfferQuestionPromptWithFallbackAsync(client, senderAgentId, from, conversationKey, currentSessionId, polledQuestions[0]).ConfigureAwait(false);
                        }
                    }
                }
            }

            SendImText(client, senderAgentId, from, responseText, conversationKey);

            StopTypingIndicatorIfActive();

            inFlightQuestionWatchCts.Cancel();
            try
            {
                await inFlightQuestionWatchTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore watcher cancellation or transient polling errors.
            }

            _ = Task.Run(() => NotifyPendingQuestionIfAppearsAsync(client, senderAgentId, from, conversationKey));
        }
        catch (OperationCanceledException) when (inFlightRequestCts?.IsCancellationRequested == true)
        {
            startedAt.Stop();
            Console.WriteLine($"[{channelLabel}] opencode request canceled by user after {startedAt.ElapsedMilliseconds}ms: from={from} conversation={conversationKey}");
            SendImText(client, senderAgentId, from, "Canceled the current request.", conversationKey);
        }
        catch (OperationCanceledException ex) when (IsLikelyBackendTimeout(ex))
        {
            startedAt.Stop();
            Console.WriteLine($"[{channelLabel}] opencode timeout after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
            _opencodeChat?.ResetConversation(conversationKey);
            SendImText(client, senderAgentId, from, "The AI is taking longer than expected and timed out. Please try again in a moment.", conversationKey);
        }
        catch (Exception ex)
        {
            startedAt.Stop();
            if (IsLikelyBackendTimeout(ex))
            {
                Console.WriteLine($"[{channelLabel}] opencode timeout after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
                _opencodeChat?.ResetConversation(conversationKey);
                SendImText(client, senderAgentId, from, "The AI is taking longer than expected and timed out. Please try again in a moment.", conversationKey);
                return;
            }

            Console.WriteLine($"[{channelLabel}] failed to route to opencode after {startedAt.ElapsedMilliseconds}ms: {ex.Message}");
            _opencodeChat?.ResetConversation(conversationKey);
            SendImText(client, senderAgentId, from, "Sorry, I could not reach the AI service right now.", conversationKey);
        }
        finally
        {
            if (inFlightRequestCts != null
                && _inFlightRequestCtsByConversation.TryGetValue(conversationKey, out var currentInFlightCts)
                && ReferenceEquals(currentInFlightCts, inFlightRequestCts))
            {
                _inFlightRequestCtsByConversation.TryRemove(conversationKey, out _);
            }

            var activeSessionId = _opencodeChat?.GetConversationSessionId(conversationKey);
            if (!string.IsNullOrWhiteSpace(activeSessionId))
            {
                MarkOpencodeSessionIdle(activeSessionId);
            }

            if (globalGateHeld)
            {
                _globalConversationGate.Release();
            }

            StopTypingIndicatorIfActive();
            gate.Release();
            _ambientConversationKey.Value = priorAmbientConversationKey;
        }
    }

    private static string GetConversationChannelLabel(string conversationKey)
    {
        if (conversationKey.StartsWith("group-", StringComparison.OrdinalIgnoreCase))
        {
            return "group";
        }

        if (conversationKey.Equals(LocalChatConversationKey, StringComparison.OrdinalIgnoreCase))
        {
            return "local";
        }

        return "im";
    }

    private bool TryNormalizeConversationTextForRouting(string conversationKey, string text, out string routedText)
    {
        routedText = text.Trim();
        if (conversationKey.StartsWith("im-", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(routedText);
        }

        // Always allow explicit star commands from non-IM channels. Authorization is enforced later.
        if (routedText.StartsWith('*'))
        {
            return true;
        }

        if (TryConsumeWakeWordPrefix(routedText, allowShortBotWakeWord: IsControlGroupConversation(conversationKey), out var remainder))
        {
            routedText = remainder;
            return !string.IsNullOrWhiteSpace(routedText);
        }

        return false;
    }

    private async Task<(bool Allowed, string Reason, string DenialMessage)> EvaluateConversationAccessAsync(
        GridClient client,
        UUID senderAgentId,
        string senderName,
        string conversationKey,
        CancellationToken cancellationToken)
    {
        if (!IsHandlerRestricted())
        {
            return (false, "handler_not_configured", "Sorry, I am currently locked down until a handler or parent controller is configured.");
        }

        if (conversationKey.StartsWith("group-", StringComparison.OrdinalIgnoreCase)
            && IsControlGroupConversation(conversationKey))
        {
            // In bot C&C group chat, membership already scopes participants.
            return (true, "control_group_chat", string.Empty);
        }

        if (IsHandlerAgent(senderAgentId, senderName))
        {
            return (true, "handler", string.Empty);
        }

        if (await IsAgentInControlGroupAsync(client, senderAgentId, cancellationToken).ConfigureAwait(false))
        {
            return (true, "control_group_member", string.Empty);
        }

        return (
            false,
            "not_handler_or_control_group_member",
            "Sorry, I can only accept instructions from my handler/parent controller or avatars in my C&C group.");
    }

    private bool IsControlGroupConversation(string conversationKey)
    {
        if (!conversationKey.StartsWith("group-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var groupIdText = conversationKey["group-".Length..].Trim();
        if (!UUID.TryParse(groupIdText, out var groupId) || groupId == UUID.Zero)
        {
            return false;
        }

        lock (_controlGroupStateLock)
        {
            return _controlGroupId != UUID.Zero && _controlGroupId == groupId;
        }
    }

    private string? ResolveConversationKeyForSpeaker(UUID agentId)
    {
        if (agentId == UUID.Zero)
        {
            return null;
        }

        return _conversationKeyBySpeakerAgent.TryGetValue(agentId, out var conversationKey)
            ? conversationKey
            : null;
    }

    private static bool IsLikelyTypingIndicator(InstantMessage message, string text)
    {
        if (!text.Equals("typing", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Some viewers emit IM typing state as a pseudo-message with payload metadata.
        return message.BinaryBucket != null && message.BinaryBucket.Length > 0;
    }

    private bool IsDuplicateImEvent(UUID fromAgentId, string text, DateTime timestamp)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedText = text.Trim();
        var timestampKey = timestamp.Ticks > 0 ? timestamp.Ticks.ToString() : "no-ts";
        var key = $"{fromAgentId}:{timestampKey}:{normalizedText}";
        var duplicateWindow = TimeSpan.FromSeconds(8);

        if (_recentImEvents.TryGetValue(key, out var seenAt) && now - seenAt <= duplicateWindow)
        {
            return true;
        }

        _recentImEvents[key] = now;

        // Opportunistic cleanup to avoid unbounded growth for long-running sessions.
        foreach (var entry in _recentImEvents)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(5))
            {
                _recentImEvents.TryRemove(entry.Key, out _);
            }
        }

        return false;
    }

    private bool IsDuplicateDialogBridgeReply(string conversationKey, string requestId, string answer)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{conversationKey}:{requestId}:{answer}";
        var duplicateWindow = TimeSpan.FromSeconds(10);

        if (_recentDialogBridgeReplies.TryGetValue(key, out var seenAt) && now - seenAt <= duplicateWindow)
        {
            return true;
        }

        _recentDialogBridgeReplies[key] = now;

        foreach (var entry in _recentDialogBridgeReplies)
        {
            if (now - entry.Value > TimeSpan.FromMinutes(5))
            {
                _recentDialogBridgeReplies.TryRemove(entry.Key, out _);
            }
        }

        return false;
    }

    private void OnChatFromSimulator(object? sender, ChatEventArgs e)
    {
        var client = _client;
        if (client == null || e.SourceID == client.Self.AgentID)
        {
            return;
        }

        var text = e.Message?.Trim() ?? string.Empty;
        if (text.StartsWith(LslDialogBridgeReplyPrefix + "|", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[chat] ({e.SourceType}/{e.Type}) {e.FromName}: {SanitizeImLogText(text)}");
            _ = Task.Run(async () =>
            {
                await TryHandleLslDialogBridgeReplyAsync(client, e.SourceID, e.FromName, text).ConfigureAwait(false);
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(text)
            || e.SourceType != ChatSourceType.Agent
            || e.SourceID == UUID.Zero)
        {
            return;
        }

        if (!_receiveChatAllowedTypes.Contains(e.Type))
        {
            Console.WriteLine($"[local] ignored ({e.SourceType}/{e.Type}) {e.FromName}: modality filtered by LOCAL_CHAT_ALLOWED_TYPES");
            return;
        }

        var from = string.IsNullOrWhiteSpace(e.FromName) ? e.SourceID.ToString() : e.FromName.Trim();
        RegisterConversationRoute(
            LocalChatConversationKey,
            ConversationChannel.Local,
            UUID.Zero,
            e.SourceID,
            from);
        lock (_recentImSpeakerLock)
        {
            _lastImSpeakerAgentId = e.SourceID;
            _lastImSpeakerName = from;
            _lastImConversationKey = LocalChatConversationKey;
        }

        Console.WriteLine($"[local] ({e.SourceType}/{e.Type}) {from}: {SanitizeImLogText(text)}");
        EmitRuntimeEvent(
            "general",
            "chat.local.received",
            "opensim",
            $"Local chat received from {from}.",
            new Dictionary<string, string?>
            {
                ["fromAgentId"] = e.SourceID.ToString(),
                ["fromName"] = from,
                ["chatType"] = e.Type.ToString(),
                ["sourceType"] = e.SourceType.ToString(),
                ["text"] = SanitizeImLogText(text)
            });
        _ = Task.Run(() => HandleIncomingConversationMessageAsync(
            client,
            e.SourceID,
            from,
            LocalChatConversationKey,
            text,
            $"OpenSim local chat with {from}"));
    }
}
