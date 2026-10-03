# LSL Dialog Bridge (Prototype)

This prototype lets `opensim-metaverse2mcp` show Opencode multiple-choice questions as in-world `llDialog` popups.

## Files

- `lsl/dialog-bridge.lsl` - source used to build the bridge content packaged in `Cube-Bot-IAR.iar`.

## How it works

1. Bot emits request on private chat channel `-919191`:
   - `dlgreq|<conversationKey>|<requestId>|<targetAvatar>|<header>|<prompt>|<optionCount>|<opt1>|<opt2>|...`
2. LSL script shows `llDialog(targetAvatar, ...)` with those buttons.
3. User clicks a button.
4. LSL script sends IM back to bot object endpoint:
   - `dlgrep|<conversationKey>|<questionId>|<answer>`
5. Bot maps this to pending Opencode question and sends the answer.

## Setup

1. Import `Cube-Bot-IAR.iar` for the bot account.
2. Confirm folder `Cube Bot IAR` exists in bot inventory and contains:
   - attachment `The Cube Bot`
   - wearable `Full Body Alpha`
3. Run `*bridge install` (or let region-enter auto-provision run when enabled).
4. The install flow wears the folder content as needed and attaches `The Cube Bot` to `Spine`.
5. Trigger a workflow that asks a multiple-choice question.

## Notes

- This is intentionally minimal and currently routes to the latest active IM conversation.
- It does not yet include auth/signing; keep the bridge attachment controlled and trusted.
- Question and permission prompts are delivered primarily via in-world dialog bridge events; use `*question` / `*permission` commands as manual fallback controls.