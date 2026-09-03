using LibreMetaverse;

namespace Opensim.Metaverse2Mcp;

internal sealed partial class BotSession
{
    private bool TryOfferQuestionViaLslDialogBridge(GridClient client, string conversationKey, OpencodePendingQuestion question)
    {
        if (question.Options.Count == 0)
        {
            // Free-text prompt: use llTextBox through the bridge when custom answers are allowed.
            if (question.AllowsCustom != false)
            {
                return TryOfferQuestionTextInputViaLslDialogBridge(client, conversationKey, question);
            }

            Console.WriteLine($"[dialog-bridge] skip offer: no options/custom input disabled for question {question.Id}.");
            return false;
        }

        if (!_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            || targetAgentId == UUID.Zero)
        {
            Console.WriteLine($"[dialog-bridge] skip offer: no target agent mapped for conversation {conversationKey}.");
            return false;
        }

        // Payload format:
        // dlgreq|conversation|requestId|target|replyTarget|header|prompt|optionCount|opt1|opt2|...
        var header = question.Header?.Trim() ?? string.Empty;
        var prompt = BuildCompactQuestionDialogPrompt(question);
        var payload = BuildLslDialogBridgeRequestPayloadWithinLimit(
            conversationKey,
            question.Id,
            targetAgentId,
            client.Self.AgentID,
            header,
            prompt,
            question.Options,
            out var wasCompacted);
        if (wasCompacted)
        {
            Console.WriteLine($"[dialog-bridge] compacted question payload for {question.Id}: {payload.Length} chars.");
        }

        SendDialogBridgePing(client, conversationKey, question.Id, "question");
        client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        if (payload.Length > LslDialogBridgeMaxPayloadLength)
        {
            Console.WriteLine($"[dialog-bridge] warning: payload length {payload.Length} may be truncated by simulator chat limits.");
        }
        Console.WriteLine(
            $"[dialog-bridge] offered question via channel {LslDialogBridgeRequestChannel}: conversation={conversationKey} question={question.Id} options={question.Options.Count} target={targetAgentId} payloadLength={payload.Length}");
        return true;
    }

    private bool TryOfferQuestionTextInputViaLslDialogBridge(GridClient client, string conversationKey, OpencodePendingQuestion question)
    {
        if (!_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            || targetAgentId == UUID.Zero)
        {
            Console.WriteLine($"[dialog-bridge] skip text offer: no target agent mapped for conversation {conversationKey}.");
            return false;
        }

        var header = question.Header?.Trim() ?? string.Empty;
        var prompt = BuildCompactQuestionDialogPrompt(question);
        var payload = BuildLslDialogBridgeTextRequestPayload(
            conversationKey,
            question.Id,
            targetAgentId,
            client.Self.AgentID,
            header,
            prompt);

        SendDialogBridgePing(client, conversationKey, question.Id, "question-text");
        client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        if (payload.Length > LslDialogBridgeMaxPayloadLength)
        {
            Console.WriteLine($"[dialog-bridge] warning: text payload length {payload.Length} may be truncated by simulator chat limits.");
        }

        Console.WriteLine(
            $"[dialog-bridge] offered question text input via channel {LslDialogBridgeRequestChannel}: conversation={conversationKey} question={question.Id} target={targetAgentId} payloadLength={payload.Length}");
        return true;
    }

    private bool TryOfferPermissionViaLslDialogBridge(GridClient client, string conversationKey, OpencodePendingPermission permission)
    {
        var permissionId = permission.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(permissionId))
        {
            Console.WriteLine("[dialog-bridge] skip offer: permission id is missing.");
            return false;
        }

        if (!_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            || targetAgentId == UUID.Zero)
        {
            Console.WriteLine($"[dialog-bridge] skip offer: no target agent mapped for conversation {conversationKey}.");
            return false;
        }

        var header = BuildPermissionDialogHeader(permission);
        var prompt = BuildCompactPermissionDialogPrompt(permission);
        // Tag permission request IDs so dialog replies can be routed deterministically.
        var bridgeRequestId = LslDialogBridgePermissionRequestPrefix + permissionId;
        var payload = BuildLslDialogBridgeRequestPayloadWithinLimit(
            conversationKey,
            bridgeRequestId,
            targetAgentId,
            client.Self.AgentID,
            header,
            prompt,
            LslPermissionDialogOptions,
            out var wasCompacted);
        if (wasCompacted)
        {
            Console.WriteLine($"[dialog-bridge] compacted permission payload for {permissionId}: {payload.Length} chars.");
        }

        SendDialogBridgePing(client, conversationKey, bridgeRequestId, "permission");
        client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        if (payload.Length > LslDialogBridgeMaxPayloadLength)
        {
            Console.WriteLine($"[dialog-bridge] warning: payload length {payload.Length} may be truncated by simulator chat limits.");
        }

        Console.WriteLine(
            $"[dialog-bridge] offered permission via channel {LslDialogBridgeRequestChannel}: conversation={conversationKey} permission={permissionId} target={targetAgentId} payloadLength={payload.Length}");
        return true;
    }

    private async Task<bool> TryHandleLslDialogBridgeReplyAsync(GridClient client, UUID senderObjectId, string senderName, string text)
    {
        if (TryParseLslDialogBridgePong(text, out var pingNonce, out var pingProto))
        {
            if (!IsTrustedDialogBridgeSender(client, senderObjectId, senderName, string.Empty))
            {
                return false;
            }

            Console.WriteLine($"[dialog-bridge] ping ack: nonce={pingNonce} proto={pingProto} sender={senderObjectId}");
            return true;
        }

        if (TryParseLslDialogBridgeAck(text, out var ackConversationKey, out var ackRequestId, out var ackMode))
        {
            if (!IsTrustedDialogBridgeSender(client, senderObjectId, senderName, ackConversationKey))
            {
                return false;
            }

            Console.WriteLine($"[dialog-bridge] ui ack: conversation={ackConversationKey} request={ackRequestId} mode={ackMode} sender={senderObjectId}");
            return true;
        }

        if (!TryParseLslDialogBridgeReply(text, out var conversationKey, out var requestId, out var answer))
        {
            Console.WriteLine("[dialog-bridge] ignored object IM: not a dialog-bridge reply payload.");
            return false;
        }

        if (!IsTrustedDialogBridgeSender(client, senderObjectId, senderName, conversationKey))
        {
            return false;
        }

        QueueBridgeAgentsPromptProbe(senderObjectId, senderName);

        if (IsDuplicateDialogBridgeReply(conversationKey, requestId, answer))
        {
            Console.WriteLine($"[dialog-bridge] duplicate reply suppressed: conversation={conversationKey} request={requestId} answer={answer}");
            return true;
        }

        Console.WriteLine($"[dialog-bridge] received reply payload: conversation={conversationKey} request={requestId} answer={answer}");

        if (_opencodeChat == null || string.IsNullOrWhiteSpace(conversationKey) || string.IsNullOrWhiteSpace(requestId))
        {
            Console.WriteLine("[dialog-bridge] dropped reply: opencode chat unavailable or payload missing conversation/request id.");
            return false;
        }

        var sessionId = _opencodeChat.GetConversationSessionId(conversationKey);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            Console.WriteLine($"[dialog-bridge] dropped reply: no active opencode session for conversation {conversationKey}.");
            return false;
        }

        if (await TryHandleLslDialogBridgePermissionReplyAsync(client, conversationKey, sessionId, requestId, answer).ConfigureAwait(false))
        {
            ClearPendingPromptWait(conversationKey);
            _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);
            return true;
        }

        var resolvedAnswer = await ResolveLslDialogBridgeAnswerAsync(sessionId, requestId, answer).ConfigureAwait(false);
        var ok = await _opencodeChat.ReplyToQuestionAsync(sessionId, requestId, new[] { resolvedAnswer }, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[dialog-bridge] forwarded reply to opencode: session={sessionId} question={requestId} success={ok} answer={resolvedAnswer}");
        _latestPendingQuestionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingQuestionByConversation.TryRemove(conversationKey, out _);
        ClearPendingPromptWait(conversationKey);
        _pendingTextPromptReplyByConversation.TryRemove(conversationKey, out _);

        if (_conversationAgentByKey.TryGetValue(conversationKey, out var agentId)
            && agentId != UUID.Zero)
        {
            var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
                ? displayName
                : "handler";

            if (!ok)
            {
                SendImText(client, agentId, from,
                    "I sent your dialog choice, but Opencode did not return an explicit success flag.");
            }
        }

        return true;
    }

    private bool IsTrustedDialogBridgeSender(GridClient client, UUID senderObjectId, string senderName, string conversationKey)
    {
        if (senderObjectId == UUID.Zero)
        {
            Console.WriteLine("[dialog-bridge] dropped reply: sender object UUID missing.");
            return false;
        }

        UUID pinnedObjectId;
        UUID pinnedOwnerId;
        bool requireTrustedSender;
        lock (_dialogBridgeTrustLock)
        {
            pinnedObjectId = _trustedDialogBridgeObjectId;
            pinnedOwnerId = _trustedDialogBridgeOwnerId;
            requireTrustedSender = _lslDialogBridgeRequireTrustedSender;
        }

        var ownerResolved = TryGetObjectOwnerIdFromCache(client, senderObjectId, out var senderOwnerId);
        var objectMatchesPin = pinnedObjectId != UUID.Zero && senderObjectId == pinnedObjectId;
        if (pinnedObjectId != UUID.Zero && senderObjectId != pinnedObjectId)
        {
            Console.WriteLine($"[dialog-bridge] dropped reply: untrusted object {senderObjectId} (expected {pinnedObjectId}) sender='{senderName}'.");
            return false;
        }

        if (pinnedOwnerId != UUID.Zero)
        {
            if (!ownerResolved)
            {
                if (objectMatchesPin)
                {
                    Console.WriteLine($"[dialog-bridge] warning: owner not resolved for pinned object {senderObjectId}; accepting due to object pin match.");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] dropped reply: owner not resolved for object {senderObjectId} while trusted owner pin is enabled ({pinnedOwnerId}).");
                    return false;
                }
            }
            else if (senderOwnerId != pinnedOwnerId)
            {
                if (objectMatchesPin)
                {
                    Console.WriteLine($"[dialog-bridge] warning: owner mismatch for pinned object {senderObjectId}. got={senderOwnerId} expected={pinnedOwnerId}; accepting due to object pin match.");
                }
                else
                {
                    Console.WriteLine($"[dialog-bridge] dropped reply: owner mismatch for object {senderObjectId}. got={senderOwnerId} expected={pinnedOwnerId}");
                    return false;
                }
            }
        }

        if (!requireTrustedSender)
        {
            return true;
        }

        if (pinnedObjectId == UUID.Zero)
        {
            var shouldPersistTrustState = false;
            lock (_dialogBridgeTrustLock)
            {
                if (_trustedDialogBridgeObjectId == UUID.Zero)
                {
                    _trustedDialogBridgeObjectId = senderObjectId;
                    if (_trustedDialogBridgeOwnerId == UUID.Zero && ownerResolved)
                    {
                        _trustedDialogBridgeOwnerId = senderOwnerId;
                    }

                    Console.WriteLine($"[dialog-bridge] pinned trusted bridge sender from first valid reply: object={_trustedDialogBridgeObjectId} owner={_trustedDialogBridgeOwnerId} conversation={conversationKey}");
                    shouldPersistTrustState = true;
                }
                else if (_trustedDialogBridgeObjectId != senderObjectId)
                {
                    Console.WriteLine($"[dialog-bridge] dropped reply: sender object changed during pinning race. got={senderObjectId} pinned={_trustedDialogBridgeObjectId}");
                    return false;
                }
            }

            if (shouldPersistTrustState)
            {
                TrySaveDialogBridgeTrustStateToFile();
            }
        }

        return true;
    }

    private static bool TryGetObjectOwnerIdFromCache(GridClient client, UUID objectId, out UUID ownerId)
    {
        ownerId = UUID.Zero;
        var sim = client.Network.CurrentSim;
        if (sim == null)
        {
            return false;
        }

        foreach (var prim in sim.ObjectsPrimitives.Values)
        {
            if (prim.ID != objectId)
            {
                continue;
            }

            if (prim.Properties?.OwnerID is UUID resolvedOwner && resolvedOwner != UUID.Zero)
            {
                ownerId = resolvedOwner;
                return true;
            }

            return false;
        }

        return false;
    }

    private async Task<bool> TryHandleLslDialogBridgePermissionReplyAsync(
        GridClient client,
        string conversationKey,
        string sessionId,
        string requestId,
        string answer)
    {
        var permissionId = requestId.Trim();
        var taggedPermissionRequest = false;
        if (permissionId.StartsWith(LslDialogBridgePermissionRequestPrefix, StringComparison.OrdinalIgnoreCase))
        {
            taggedPermissionRequest = true;
            permissionId = permissionId[LslDialogBridgePermissionRequestPrefix.Length..].Trim();
        }

        var isPermissionId = IsCanonicalPermissionRequestId(permissionId);
        if (!isPermissionId)
        {
            var pending = await GetPendingPermissionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var match = pending.FirstOrDefault(p => p.Id.Equals(permissionId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                if (!taggedPermissionRequest)
                {
                    return false;
                }

                match = pending.FirstOrDefault();
                if (match == null)
                {
                    Console.WriteLine($"[dialog-bridge] tagged permission reply could not resolve pending permission for session={sessionId} request={requestId}");
                    return false;
                }
            }

            permissionId = match.Id;
        }

        if (!TryParseSimplePermissionResponse(answer, out var response, out var remember))
        {
            Console.WriteLine($"[dialog-bridge] permission reply not understood for {permissionId}: '{answer}'");
            if (_conversationAgentByKey.TryGetValue(conversationKey, out var agentId) && agentId != UUID.Zero)
            {
                var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
                    ? displayName
                    : "handler";
                SendImText(client, agentId, from,
                    "I could not understand that approval choice. Reply with: 1) yes, 2) no, 3) yes always, 4) no always.");
            }

            return true;
        }

        var ok = await _opencodeChat!.RespondToPermissionAsync(sessionId, permissionId, response, remember, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"[dialog-bridge] forwarded permission reply to opencode: session={sessionId} permission={permissionId} success={ok} response={response} remember={remember}");
        _latestPendingPermissionByConversation.TryRemove(conversationKey, out _);
        _announcedPendingPermissionByConversation.TryRemove(conversationKey, out _);

        if (_conversationAgentByKey.TryGetValue(conversationKey, out var targetAgentId)
            && targetAgentId != UUID.Zero)
        {
            var from = _conversationNameByKey.TryGetValue(conversationKey, out var displayName)
                ? displayName
                : "handler";

            if (!ok)
            {
                SendImText(client, targetAgentId, from,
                    "I could not confirm that approval was accepted. If needed, try again.");
            }
        }

        return true;
    }

    private async Task<string> ResolveLslDialogBridgeAnswerAsync(string sessionId, string questionId, string answer)
    {
        var trimmedAnswer = answer?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedAnswer))
        {
            return string.Empty;
        }

        var pending = await GetPendingQuestionsEventFirstAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        var question = pending.FirstOrDefault(q => q.Id.Equals(questionId, StringComparison.OrdinalIgnoreCase));
        if (question == null || question.Options.Count == 0)
        {
            return trimmedAnswer;
        }

        if (TryResolveQuestionAnswer(question, trimmedAnswer, out var resolvedAnswer))
        {
            return resolvedAnswer;
        }

        var onceDecoded = DecodeDialogToken(trimmedAnswer);
        if (!onceDecoded.Equals(trimmedAnswer, StringComparison.Ordinal)
            && TryResolveQuestionAnswer(question, onceDecoded, out resolvedAnswer))
        {
            return resolvedAnswer;
        }

        foreach (var option in question.Options)
        {
            if (option.StartsWith(trimmedAnswer, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }

            var encodedOption = EncodeDialogToken(option);
            if (encodedOption.StartsWith(trimmedAnswer, StringComparison.OrdinalIgnoreCase)
                || trimmedAnswer.StartsWith(encodedOption, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return onceDecoded;
    }

    private static bool TryParseLslDialogBridgeReply(string text, out string conversationKey, out string requestId, out string answer)
    {
        conversationKey = string.Empty;
        requestId = string.Empty;
        answer = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length < 4 || !parts[0].Equals(LslDialogBridgeReplyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        conversationKey = DecodeDialogToken(parts[1]);
        requestId = DecodeDialogToken(parts[2]);
        answer = DecodeDialogToken(parts[3]);
        return !string.IsNullOrWhiteSpace(conversationKey)
            && !string.IsNullOrWhiteSpace(requestId)
            && !string.IsNullOrWhiteSpace(answer);
    }

    private static bool TryParseLslDialogBridgeAck(string text, out string conversationKey, out string requestId, out string mode)
    {
        conversationKey = string.Empty;
        requestId = string.Empty;
        mode = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length < 4 || !parts[0].Equals(LslDialogBridgeAckPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        conversationKey = DecodeDialogToken(parts[1]);
        requestId = DecodeDialogToken(parts[2]);
        mode = DecodeDialogToken(parts[3]);
        return !string.IsNullOrWhiteSpace(conversationKey)
            && !string.IsNullOrWhiteSpace(requestId)
            && !string.IsNullOrWhiteSpace(mode);
    }

    private static bool TryParseLslDialogBridgePong(string text, out string nonce, out string proto)
    {
        nonce = string.Empty;
        proto = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length < 3 || !parts[0].Equals(LslDialogBridgePongPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        nonce = DecodeDialogToken(parts[1]);
        proto = DecodeDialogToken(parts[2]);
        return !string.IsNullOrWhiteSpace(nonce) && !string.IsNullOrWhiteSpace(proto);
    }

    private static void SendDialogBridgePing(GridClient client, string conversationKey, string requestId, string kind)
    {
        var nonce = $"{kind}:{conversationKey}:{requestId}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var payload = string.Join("|", new[]
        {
            LslDialogBridgePingPrefix,
            EncodeDialogToken(nonce),
            EncodeDialogToken(client.Self.AgentID.ToString())
        });

        client.Self.Chat(payload, LslDialogBridgeRequestChannel, ChatType.Shout);
        Console.WriteLine($"[dialog-bridge] sent ping: nonce={nonce} channel={LslDialogBridgeRequestChannel}");
    }

    private static string BuildPermissionDialogHeader(OpencodePendingPermission permission)
    {
        var title = permission.Title?.Trim() ?? string.Empty;
        var hasHumanTitle = !string.IsNullOrWhiteSpace(title)
            && !title.StartsWith("per", StringComparison.OrdinalIgnoreCase)
            && !title.StartsWith("que", StringComparison.OrdinalIgnoreCase);
        return hasHumanTitle ? title : "Approval required";
    }

    private static string BuildPermissionDialogPrompt(OpencodePendingPermission permission)
    {
        var primary = GetPermissionPrimaryText(permission, out _);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return CompactPermissionSummary(primary, maxChars: 180, maxTokens: 18);
        }

        return "Choose whether to allow this action.";
    }

    private static string BuildCompactQuestionDialogPrompt(OpencodePendingQuestion question)
    {
        var prompt = question.Question?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return "Choose an option:";
        }

        var firstLine = prompt.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            firstLine = prompt;
        }

        const int maxLength = 120;
        return firstLine!.Length <= maxLength
            ? firstLine
            : firstLine[..(maxLength - 3)] + "...";
    }

    private static string BuildLslDialogBridgeRequestPayloadWithinLimit(
        string conversationKey,
        string requestId,
        UUID targetAgentId,
        UUID replyTargetAgentId,
        string header,
        string prompt,
        IReadOnlyList<string> options,
        out bool wasCompacted)
    {
        wasCompacted = false;
        var normalizedHeader = header?.Trim() ?? string.Empty;
        var normalizedPrompt = prompt?.Trim() ?? string.Empty;

        var payload = BuildLslDialogBridgeRequestPayload(
            conversationKey,
            requestId,
            targetAgentId,
            replyTargetAgentId,
            normalizedHeader,
            normalizedPrompt,
            options);
        if (payload.Length <= LslDialogBridgeMaxPayloadLength)
        {
            return payload;
        }

        wasCompacted = true;

        // First shed prompt verbosity while keeping header context.
        normalizedPrompt = CompactForBridge(normalizedPrompt, 80);
        payload = BuildLslDialogBridgeRequestPayload(conversationKey, requestId, targetAgentId, replyTargetAgentId, normalizedHeader, normalizedPrompt, options);
        if (payload.Length <= LslDialogBridgeMaxPayloadLength)
        {
            return payload;
        }

        // If still too large, reduce both header and prompt until payload fits.
        normalizedHeader = CompactForBridge(normalizedHeader, 36);
        normalizedPrompt = CompactForBridge(normalizedPrompt, 36);
        payload = BuildLslDialogBridgeRequestPayload(conversationKey, requestId, targetAgentId, replyTargetAgentId, normalizedHeader, normalizedPrompt, options);
        if (payload.Length <= LslDialogBridgeMaxPayloadLength)
        {
            return payload;
        }

        // Last-resort minimal body to preserve operability over strict prompt fidelity.
        return BuildLslDialogBridgeRequestPayload(conversationKey, requestId, targetAgentId, replyTargetAgentId, "Approval required", "Choose an option.", options);
    }

    private static string CompactForBridge(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        var candidate = string.IsNullOrWhiteSpace(firstLine) ? text.Trim() : firstLine;
        if (candidate.Length <= maxLength)
        {
            return candidate;
        }

        return candidate[..Math.Max(1, maxLength - 3)] + "...";
    }

    private static string BuildCompactPermissionDialogPrompt(OpencodePendingPermission permission)
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

        // Collapse whitespace so multi-line shell snippets become a short, readable one-liner.
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

    private static string BuildLslDialogBridgeRequestPayload(
        string conversationKey,
        string requestId,
        UUID targetAgentId,
        UUID replyTargetAgentId,
        string header,
        string prompt,
        IReadOnlyList<string> options)
    {
        var payloadParts = new List<string>
        {
            LslDialogBridgeRequestPrefix,
            EncodeDialogToken(conversationKey),
            EncodeDialogToken(requestId),
            EncodeDialogToken(targetAgentId.ToString()),
            EncodeDialogToken(replyTargetAgentId.ToString()),
            EncodeDialogToken(header),
            EncodeDialogToken(prompt),
            options.Count.ToString()
        };
        payloadParts.AddRange(options.Select(EncodeDialogToken));
        return string.Join("|", payloadParts);
    }

    private static string BuildLslDialogBridgeTextRequestPayload(
        string conversationKey,
        string requestId,
        UUID targetAgentId,
        UUID replyTargetAgentId,
        string header,
        string prompt)
    {
        return string.Join("|", new[]
        {
            LslDialogBridgeTextRequestPrefix,
            EncodeDialogToken(conversationKey),
            EncodeDialogToken(requestId),
            EncodeDialogToken(targetAgentId.ToString()),
            EncodeDialogToken(replyTargetAgentId.ToString()),
            EncodeDialogToken(header),
            EncodeDialogToken(prompt)
        });
    }

    private static string EncodeDialogToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Keep payload small for simulator chat transport: escape only delimiter-critical chars.
        return value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);
    }

    private static string DecodeDialogToken(string value)
        => Uri.UnescapeDataString(value ?? string.Empty);

    private static bool TryResolveQuestionAnswer(OpencodePendingQuestion question, string text, out string answer)
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
