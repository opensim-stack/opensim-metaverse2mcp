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

default
{
    state_entry()
    {
        llListen(CHANNEL, "", NULL_KEY, "");
        applyFaces();
    }

    listen(integer channel, string name, key id, string message)
    {
        key tex = textureByName(message);
        if (tex == NULL_KEY)
        {
            llOwnerSay("No texture named '" + message + "' in my inventory.");
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
}