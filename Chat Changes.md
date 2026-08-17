# Chat Improvement

## Chat Modes

We current only support IM chat. I now want to support ...

* Local Chat
* IM Chat
- Friend Chat (IM'ing with a friend)
* Group Chat

I am not sure if there is an existing identifier we can use for "chat key"

## General Behaviour

* Each chat session should have its own BotSession object.
* This implies chat session has its own Opencode session. 
* This implies we must now store current opencode session configuration per user and per chat "key"
* Only one chat session can be active for a single bot at a time though. If a request in one  session is made before the request in another has finished, the user will be told sorry, but the bot is busy.

## General Security

* The bot must always get permission from its handler to add anyone else to its C&C group
* A bot can only befriend either their handler or people in their C&C group.
* Only the handler can run star commands in any chat session type.

## Local Chat

The chat channel everyone can speak in.

* The AI only responds to instructions by its handler or anyone it's C&C group
* To everyone else, it should polity refuse.
* Chat `key` is `local-chat`

## IM Chat

Direct  agent to agent communication. 

* The AI only responds to instructions by its handler or anyone it's C&C group

* To everyone else, it should polity refuse.

* Chat `key` is `im-<uuid-of-sender>` 

### Friends

The AI will respond to friends, but currently friends must be handler or in C&C group, so same rules apply.

## Group Chat

Chat between a defined group of users. 

* A bot will accept any instruction when chatting in its own C&C group.

* A bot may join other groups and chat in them.

* A bot will only accept instructions from its  handler or others in its C&C group if it is chatting in any other group than its own C&C group.

* Chat `key` is `group-<group-uuid>`.

## Other Considerations

We now support (one half of) a voice chat system. Each chat (I think) can have its own voice channel (needs checking). The Local Chat would equate to  "spacial" chat I believe.

As usual, check at end of work all documentation is added.
