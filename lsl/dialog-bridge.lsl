// dialog-bridge.lsl
// Receives dialog requests from the bot on a private channel and shows llDialog to a target avatar.
// Sends the selected label back to the bot via llInstantMessage as:
//   dlgrep|<conversationKey>|<requestId>|<answer>
// Sends request acceptance ack as:
//   dlgack|<conversationKey>|<requestId>|<mode>
// mode is one of: dialog, textbox
// requestId is opaque to this script and may be either:
//   - question id
//   - permission id tagged as perm:<permissionId>
// Expects strict request payload format:
//   dlgreq|conversation|requestId|target|replyTarget|header|prompt|optionCount|opt1|opt2|...
// Text input request payload:
//   txtreq|conversation|requestId|target|replyTarget|header|prompt
// Hover text control payload:
//   hovreq|targetObjectId|mode|text
// mode: set or clear
// Mood control payload:
//   moodreq|targetObjectId|emotion

integer REQUEST_CHANNEL = -919191;
integer EMOTER_CHANNEL = -919192;

// Stride: avatarKey, requestId, conversationKey, botId, optionsEncoded
list gRequests;

integer findRequestIndex(key avatar, string questionId)
{
    integer i;
    integer count = llGetListLength(gRequests);
    for (i = 0; i < count; i += 5)
    {
        if ((key)llList2String(gRequests, i) == avatar
            && llList2String(gRequests, i + 1) == questionId)
        {
            return i;
        }
    }
    return -1;
}

list buildNumberButtons(integer count)
{
    list out = [];
    integer i;
    integer limit = count;
    if (limit > 12)
    {
        limit = 12;
    }

    for (i = 1; i <= limit; ++i)
    {
        out += [(string)i];
    }

    return out;
}

string buildDialogBody(string title, string prompt, list options)
{
    string body = title + "\n" + prompt;
    integer i;
    integer count = llGetListLength(options);
    integer limit = count;
    if (limit > 12)
    {
        limit = 12;
    }

    for (i = 0; i < limit; ++i)
    {
        body += "\n" + (string)(i + 1) + ") " + llList2String(options, i);
    }

    if (count > limit)
    {
        body += "\n(Only first " + (string)limit + " options are shown.)";
    }

    return body;
}

string decode(string token)
{
    return llUnescapeURL(token);
}

list decodeOptions(string encoded)
{
    list raw = llParseStringKeepNulls(encoded, [";"], []);
    list out = [];
    integer i;
    integer count = llGetListLength(raw);
    for (i = 0; i < count; ++i)
    {
        string item = decode(llList2String(raw, i));
        if (item != "")
        {
            out += [item];
        }
    }
    return out;
}

default
{
    state_entry()
    {
        llListen(REQUEST_CHANNEL, "", NULL_KEY, "");
    }

    // Handles both bot requests and avatar button replies on the same channel.
    listen(integer channel, string name, key id, string message)
    {
        if (channel != REQUEST_CHANNEL)
        {
            return;
        }

        list parts = llParseStringKeepNulls(message, ["|"], []);
        string prefix = llList2String(parts, 0);
        if (prefix == "hovreq" && llGetListLength(parts) >= 3)
        {
            string targetObjectId = decode(llList2String(parts, 1));
            string mode = llToLower(decode(llList2String(parts, 2)));
            string hoverText = "";
            if (llGetListLength(parts) >= 4)
            {
                hoverText = decode(llList2String(parts, 3));
            }

            if (targetObjectId != "" && targetObjectId != (string)llGetKey())
            {
                return;
            }

            if (mode == "clear")
            {
                llSetText("", <1.0, 1.0, 1.0>, 0.0);
                return;
            }

            if (mode == "set")
            {
                if (hoverText == "")
                {
                    hoverText = "Thinking...";
                }

                llSetText(hoverText, <1.0, 1.0, 1.0>, 1.0);
                return;
            }
        }

        if (prefix == "moodreq" && llGetListLength(parts) >= 3)
        {
            string targetObjectId2 = decode(llList2String(parts, 1));
            string emotion = llToLower(llStringTrim(decode(llList2String(parts, 2)), STRING_TRIM));

            if (targetObjectId2 != "" && targetObjectId2 != (string)llGetKey())
            {
                return;
            }

            if (emotion == "")
            {
                return;
            }

            // Bridge and emoter run in the same attachment object.
            llMessageLinked(LINK_SET, EMOTER_CHANNEL, emotion, NULL_KEY);
            return;
        }

        if (prefix == "dlgreq" && llGetListLength(parts) >= 9)
        {
            string conversationKey = decode(llList2String(parts, 1));
            // Opaque request id; can represent a question or a permission request.
            string questionId = decode(llList2String(parts, 2));
            key targetAvatar = (key)decode(llList2String(parts, 3));
            key replyTarget = (key)decode(llList2String(parts, 4));
            string header = decode(llList2String(parts, 5));
            string prompt = decode(llList2String(parts, 6));
            if (prompt == "")
            {
                prompt = "Choose an option:";
            }

            integer expectedCount = (integer)llList2String(parts, 7);
            list optionTokens = llList2List(parts, 8, -1);
            if (expectedCount <= 0 || llGetListLength(optionTokens) != expectedCount)
            {
                // Reject truncated or malformed payloads to avoid showing incomplete choices.
                return;
            }

            string optionsEncoded = llDumpList2String(optionTokens, ";");

            list buttons = decodeOptions(optionsEncoded);
            if (targetAvatar == NULL_KEY || replyTarget == NULL_KEY || llGetListLength(buttons) == 0)
            {
                return;
            }

            integer idx = findRequestIndex(targetAvatar, questionId);
            if (idx >= 0)
            {
                gRequests = llDeleteSubList(gRequests, idx, idx + 4);
            }
            gRequests += [(string)targetAvatar, questionId, conversationKey, (string)replyTarget, optionsEncoded];

            string title = header;
            if (title == "")
            {
                title = "Question";
            }

            list numberButtons = buildNumberButtons(llGetListLength(buttons));
            string body = buildDialogBody(title, prompt, buttons);
            llDialog(targetAvatar, body, numberButtons, REQUEST_CHANNEL);
            llInstantMessage(replyTarget,
                "dlgack|"
                + llEscapeURL(conversationKey) + "|"
                + llEscapeURL(questionId) + "|dialog");
            return;
        }

        if (prefix == "txtreq" && llGetListLength(parts) >= 7)
        {
            string conversationKey3 = decode(llList2String(parts, 1));
            string questionId3 = decode(llList2String(parts, 2));
            key targetAvatar3 = (key)decode(llList2String(parts, 3));
            key replyTarget3 = (key)decode(llList2String(parts, 4));
            string header3 = decode(llList2String(parts, 5));
            string prompt3 = decode(llList2String(parts, 6));
            if (prompt3 == "")
            {
                prompt3 = "Type your response:";
            }

            if (targetAvatar3 == NULL_KEY || replyTarget3 == NULL_KEY)
            {
                return;
            }

            integer idx3 = findRequestIndex(targetAvatar3, questionId3);
            if (idx3 >= 0)
            {
                gRequests = llDeleteSubList(gRequests, idx3, idx3 + 4);
            }

            gRequests += [(string)targetAvatar3, questionId3, conversationKey3, (string)replyTarget3, ""];

            string title3 = header3;
            if (title3 == "")
            {
                title3 = "Question";
            }

            llTextBox(targetAvatar3, title3 + "\n" + prompt3, REQUEST_CHANNEL);
            llInstantMessage(replyTarget3,
                "dlgack|"
                + llEscapeURL(conversationKey3) + "|"
                + llEscapeURL(questionId3) + "|textbox");
            return;
        }

        integer i;
        integer count = llGetListLength(gRequests);
        for (i = 0; i < count; i += 5)
        {
            key avatar = (key)llList2String(gRequests, i);
            if (avatar == id)
            {
                string questionId2 = llList2String(gRequests, i + 1);
                string conversationKey2 = llList2String(gRequests, i + 2);
                key botId = (key)llList2String(gRequests, i + 3);
                string optionsEncoded2 = llList2String(gRequests, i + 4);
                list answerOptions = decodeOptions(optionsEncoded2);

                string selectedAnswer = message;
                integer selectedIndex = (integer)message;
                if (selectedIndex >= 1 && selectedIndex <= llGetListLength(answerOptions))
                {
                    selectedAnswer = llList2String(answerOptions, selectedIndex - 1);
                }

                // Echo the original requestId back unchanged so bot-side routing can
                // distinguish question replies from permission replies (perm:<id>).
                string payload = "dlgrep|"
                    + llEscapeURL(conversationKey2) + "|"
                    + llEscapeURL(questionId2) + "|"
                    + llEscapeURL(selectedAnswer);

                llInstantMessage(botId, payload);
                gRequests = llDeleteSubList(gRequests, i, i + 4);
                return;
            }
        }
    }
}