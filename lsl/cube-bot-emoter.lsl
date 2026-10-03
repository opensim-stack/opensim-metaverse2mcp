integer CONTROL_CHANNEL = 3645376;
integer LEGACY_EMOTER_CHANNEL = -919192;
float BUSY_TIMER_SECONDS = 0.6;

string BASE = "base";
string CROSS = "cross";

integer gTypingActive = FALSE;
integer gTypingDots = 0;

key textureByName(string name)
{
    if (llGetInventoryType(name) == INVENTORY_TEXTURE)
    {
        return llGetInventoryKey(name);
    }
    return NULL_KEY;
}

applyFaces()
{
    key base = textureByName(BASE);
    key cross = textureByName(CROSS);

    llSetLinkPrimitiveParamsFast(LINK_THIS,
    [
        PRIM_TEXTURE, 0, base, <1,1,0>, ZERO_VECTOR, 0.0,
        PRIM_TEXTURE, 5, cross, <1,1,0>, ZERO_VECTOR, 0.0,
        PRIM_TEXTURE, 6, cross, <1,1,0>, ZERO_VECTOR, 0.0
    ]);
}

setMood(string rawMood)
{
    string mood = llToLower(llStringTrim(rawMood, STRING_TRIM));
    key tex = textureByName(mood);
    if (tex == NULL_KEY)
    {
        llOwnerSay("No texture named '" + mood + "' in my inventory.");
        return;
    }

    applyFaces();

    llSetLinkPrimitiveParamsFast(LINK_THIS,
    [
        PRIM_TEXTURE, 1, tex, <1,1,0>, ZERO_VECTOR, 0.0,
        PRIM_TEXTURE, 2, tex, <1,1,0>, ZERO_VECTOR, 0.0,
        PRIM_TEXTURE, 3, tex, <1,1,0>, ZERO_VECTOR, 0.0,
        PRIM_TEXTURE, 4, tex, <1,1,0>, ZERO_VECTOR, 0.0
    ]);
}

setHoverText(string text)
{
    llSetText(text, <1.0, 1.0, 1.0>, 1.0);
}

clearHoverText()
{
    llSetText("", <1.0, 1.0, 1.0>, 0.0);
}

string dots(integer count)
{
    if (count <= 1) return ".";
    if (count == 2) return "..";
    if (count == 3) return "...";
    return "....";
}

startTypingHover()
{
    gTypingActive = TRUE;
    gTypingDots = 1;
    setHoverText("Thinking" + dots(gTypingDots));
    llSetTimerEvent(BUSY_TIMER_SECONDS);
}

stopTypingHover()
{
    gTypingActive = FALSE;
    gTypingDots = 0;
    llSetTimerEvent(0.0);
    clearHoverText();
}

integer startsWith(string value, string prefix)
{
    return llSubStringIndex(value, prefix) == 0;
}

handleProtocolMessage(string message)
{
    string trimmed = llStringTrim(message, STRING_TRIM);
    string lower = llToLower(trimmed);

    if (startsWith(lower, "[mood]"))
    {
        setMood(llGetSubString(trimmed, 6, -1));
        return;
    }

    if (startsWith(lower, "[typing]"))
    {
        string mode = llToLower(llStringTrim(llGetSubString(trimmed, 8, -1), STRING_TRIM));
        if (mode == "start")
        {
            startTypingHover();
            return;
        }

        if (mode == "stop")
        {
            stopTypingHover();
        }
        return;
    }

    // Optional explicit hover control for manual testing/fallbacks.
    if (startsWith(lower, "[hover]"))
    {
        string payload = llStringTrim(llGetSubString(trimmed, 7, -1), STRING_TRIM);
        string payloadLower = llToLower(payload);
        if (payloadLower == "clear" || payload == "")
        {
            stopTypingHover();
            return;
        }

        gTypingActive = FALSE;
        gTypingDots = 0;
        llSetTimerEvent(0.0);
        setHoverText(payload);
        return;
    }

    // Backward compatibility: treat bare payloads as mood names.
    setMood(trimmed);
}

default
{
    state_entry()
    {
        applyFaces();
        stopTypingHover();
        llListen(CONTROL_CHANNEL, "", NULL_KEY, "");
    }

    timer()
    {
        if (!gTypingActive)
        {
            llSetTimerEvent(0.0);
            return;
        }

        gTypingDots = (gTypingDots % 4) + 1;
        setHoverText("Thinking" + dots(gTypingDots));
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != CONTROL_CHANNEL)
        {
            return;
        }

        handleProtocolMessage(message);
    }

    link_message(integer sender_num, integer num, string str, key id)
    {
        if (num != CONTROL_CHANNEL && num != LEGACY_EMOTER_CHANNEL)
        {
            return;
        }

        // Legacy support for the old bridge script forwarding via link messages.
        handleProtocolMessage(str);
    }
}