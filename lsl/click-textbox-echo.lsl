integer gChannel;
integer gListenHandle;
key gPendingUser;

openTextBox(key user)
{
    if (gListenHandle)
    {
        llListenRemove(gListenHandle);
    }

    // Use a private negative channel to receive textbox input.
    gChannel = -1 - (integer)llFrand(2000000000.0);
    gListenHandle = llListen(gChannel, "", user, "");
    gPendingUser = user;

    llTextBox(user, "Type a message to say in local chat:", gChannel);
}

default
{
    state_entry()
    {
        gChannel = 0;
        gListenHandle = 0;
        gPendingUser = NULL_KEY;
    }

    touch_start(integer total_number)
    {
        key user = llDetectedKey(0);
        openTextBox(user);
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != gChannel || id != gPendingUser)
        {
            return;
        }

        if (llStringLength(message) > 0)
        {
            llSay(0, message);
        }

        llListenRemove(gListenHandle);
        gListenHandle = 0;
        gPendingUser = NULL_KEY;
    }

    changed(integer change)
    {
        if (change & CHANGED_OWNER)
        {
            llResetScript();
        }
    }
}
