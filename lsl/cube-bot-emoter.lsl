integer CHANNEL = -919192;

string BASE = "base";
string CROSS = "cross";

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

default
{
    state_entry()
    {
        applyFaces();
    }

    link_message(integer sender_num, integer num, string str, key id)
    {
        if (num != CHANNEL)
        {
            return;
        }

        setMood(str);
    }
}