integer gChannel;
integer gListenHandle;
key gPendingUser;

list gOptions = [
    "Hello",
    "How are you?",
    "Goodbye"
];

openDialog(key user)
{
    if (gListenHandle)
    {
        llListenRemove(gListenHandle);
    }

    // Use a private negative channel to receive dialog button clicks.
    gChannel = -1 - (integer)llFrand(2000000000.0);
    gListenHandle = llListen(gChannel, "", user, "");
    gPendingUser = user;

    llDialog(user, "Pick a message to say in local chat:", gOptions, gChannel);
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
        openDialog(user);
    }

    listen(integer channel, string name, key id, string message)
    {
        if (channel != gChannel || id != gPendingUser)
        {
            return;
        }

        if (llListFindList(gOptions, [message]) != -1)
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
