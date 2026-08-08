// dialog-bridge.lsl
// Receives dialog requests from the bot on a private channel and shows llDialog to a target avatar.
// Sends the selected label back to the bot via llInstantMessage as:
//   dlgrep|<conversationKey>|<requestId>|<answer>
// requestId is opaque to this script and may be either:
//   - question id
//   - permission id tagged as perm:<permissionId>
// Expects strict request payload format:
//   dlgreq|conversation|requestId|target|header|prompt|optionCount|opt1|opt2|...
// Hover text control payload:
//   hovreq|targetObjectId|mode|text
// mode: set or clear

integer REQUEST_CHANNEL = -919191;

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

        if (prefix == "dlgreq" && llGetListLength(parts) >= 8)
        {
            string conversationKey = decode(llList2String(parts, 1));
            // Opaque request id; can represent a question or a permission request.
            string questionId = decode(llList2String(parts, 2));
            key targetAvatar = (key)decode(llList2String(parts, 3));
            string header = decode(llList2String(parts, 4));
            string prompt = decode(llList2String(parts, 5));
            if (prompt == "")
            {
                prompt = "Choose an option:";
            }

            integer expectedCount = (integer)llList2String(parts, 6);
            list optionTokens = llList2List(parts, 7, -1);
            if (expectedCount <= 0 || llGetListLength(optionTokens) != expectedCount)
            {
                // Reject truncated or malformed payloads to avoid showing incomplete choices.
                return;
            }

            string optionsEncoded = llDumpList2String(optionTokens, ";");

            list buttons = decodeOptions(optionsEncoded);
            if (targetAvatar == NULL_KEY || llGetListLength(buttons) == 0)
            {
                return;
            }

            integer idx = findRequestIndex(targetAvatar, questionId);
            if (idx >= 0)
            {
                gRequests = llDeleteSubList(gRequests, idx, idx + 4);
            }
            gRequests += [(string)targetAvatar, questionId, conversationKey, (string)id, optionsEncoded];

            string title = header;
            if (title == "")
            {
                title = "Question";
            }

            list numberButtons = buildNumberButtons(llGetListLength(buttons));
            string body = buildDialogBody(title, prompt, buttons);
            llDialog(targetAvatar, body, numberButtons, REQUEST_CHANNEL);
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
                // Transport fallback: some simulator paths are more reliable over directed chat.
                llRegionSayTo(botId, 0, payload);
                gRequests = llDeleteSubList(gRequests, i, i + 4);
                return;
            }
        }
    }
}