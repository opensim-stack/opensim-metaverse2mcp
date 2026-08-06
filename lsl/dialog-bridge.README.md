# LSL Dialog Bridge (Prototype)

This prototype lets `opensim-metaverse2mcp` show Opencode multiple-choice questions as in-world `llDialog` popups.

## Files

- `lsl/dialog-bridge.lsl` - in-world helper script.

## How it works

1. Bot emits request on private chat channel `-919191`:
   - `dlgreq|<conversationKey>|<questionId>|<targetAvatar>|<header>|<question>|<opt1;opt2;...>`
2. LSL script shows `llDialog(targetAvatar, ...)` with those buttons.
3. User clicks a button.
4. LSL script sends IM back to bot object endpoint:
   - `dlgrep|<conversationKey>|<questionId>|<answer>`
5. Bot maps this to pending Opencode question and sends the answer.

## Setup

1. Rez a prim in region.
2. Drop `dialog-bridge.lsl` into the prim inventory.
3. Ensure the bot avatar is in the same region and within chat/listen range of the bridge object.
4. Trigger a workflow that asks a multiple-choice question.

## Notes

- This is intentionally minimal and currently routes to the latest active IM conversation.
- It does not yet include auth/signing; keep the bridge object controlled and trusted.
- Question prompts still appear in IM as fallback in case dialog delivery fails.
