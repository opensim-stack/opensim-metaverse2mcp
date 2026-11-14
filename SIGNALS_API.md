# Signals API

This is a proposal for a "Signals" API that will allow an in-world bot to have specialised interaction with objects and avatars. It can be thought of as a simplified MCP protocol that is easy to implement in OpenSimulator and SecondLife. The intention is to allow in-world bots access to information that it would otherwise have trouble discerning efficiently.

## Basics

 * Our chosen public communication channel for discovery is -44448888.
 * This is currently only for safe unauthenticated queries and actions, i.e. stuff a normal player would be able to see with their own eyes, or hear, receive chat, or interact with public menus. A later API revision may account for trusted actions.
 * The initial API is a "discovery" protocol only that can return generic details to test the idea. We may later wish to refine this later with some fixed schemas for objects of certain types.
 * Initial cache max age for objects will be 5 minutes (300 seconds).

## Discovery API

The bot will need to know about any objects that have this interaction ability. To achive this, in world objects will have an LSL script that broadcasts on a fixed known channel it's existence.  Contained within the response will be enough information for metaverse2mcp (and thus the bot) to communicate with it.

 * The in world object is constantly broadcasting an "IAH" (I am here) message. Most objects will want maximum reach, so `llRegionSay` should be used, but there are cases where shorter range such as llSay or even llWhisper could be applicable (bot has stand very close to hear the object). Broadcast should happen relatively infrequently at a rate slightly higher than the max age of cached objects in metaverse2mcp, recommend `MAX_AGE - (MAX_AGE * 0.15) - (RND() * ( MAX_AGE * 0.15 ))`. This message is a pipe '|' separated set of values. "<protocolVersion>|<uri>". protocolVersion for the purposes of the prototype will be  "1".  uri will describe how metaverse2mcp will then further communicate with the object. See below for URI types. 
 * metaverse2mcp constantly watches on our chosen fixed channel. When it hears an "IAH" from a particular object, it should cache the details it can obtain. The cached detail should contain the protocol version and URI received and the object sender key, and ideally at this stage we should also retrieve object details of that sender such as getting the object name and description and its location in world (including the region), and most importantly its owner. We will also need to record when the IAH was last heard, and have a background task that clears out objects that have not broadcasted any IAH within our configured max cache age. The cache should be keyed by the sender ID.
 * A new tool  must be implemented in metaverse2mcp "SignalsNearby" (`signals_nearby`) that takes an optional agent UUID and an option radius. The tool will only return signal capable objects that are in the same sim as the bot and when it is supplioed, are owned by the provided agent UUID. The response will include all the cached basic details that we collect with our monitoring. 
 * A 2nd new tool is required, "SignalsDescription" (`signals_description`). This is the 2nd stage tool that will allow the bot to query the semantics of the API the object wants to expose. This takes the mandatory object sender key as returned by the nearby objects (and should be stable so the harness shouldn't need to query it multiple times), and then connects to retrieve objects schema or protocol description.
 * The bot can then use existing MCP chat tools, or its own HTTP access to access the API as described by the schema.

### URIs

Three primary supported URI schemes.

 * `http://...../SIGNALS.json` if the object has an OpenAPI compatible schema it can generate, or `http://...../SIGNALS.md` for a text based protocol description for simpler objects. In this case, the object in question would (using LSL scripts) setup its own HTTP server that listens for requests for that URL and serves the schema or text description. This should be easy to implement, its just a normal HTTP request.
 * `https://...../SIGNALS.json` if the object has an OpenAPI compatible schema it can generate, or `https://...../SIGNALS.md` for a text based protocol description for simpler objects. In this case, the object in question would (using LSL scripts) setup its own HTTP server that listens for requests for that URL and serves the schema or text description. This should be easy to implement, its just a normal HTTP request.
 * `chat://<channel>/SIGNALS.json` if the object has an OpenAPI compatible schema it can generate, or `chat://<channel>/SIGNALS.md` for a text based protocol description for simpler objects. In this case, the object in question would (using LSL scripts) setup its own chat listener that listens for requests for messages are "SIGNALS.json" or "SIGNALS.md" and respond with the schema or text description. The client side (i.e. metaverse2mcp) would have to send the chat request, and wait itself for a short amount of time for the response (say 5 seconds maximum). Only accept responses whose sender key equals the requested object's key, then take the first of those. When sending, the original sender ID should be targeted.



