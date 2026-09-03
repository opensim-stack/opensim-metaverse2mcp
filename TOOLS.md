# Tools

## Tool Surface

The server publishes tools including:

- `GetStatus`
- `BotList`
- `BotGet`
- `BotCreate`
- `BotStart`
- `BotStop`
- `BotRestart`
- `BotDelete`
- `Sit`
- `Stand`
- `Fly`
- `Jump`
- `AnimationStart`
- `AnimationStop`
- `AnimationsList`
- `ActiveAnimations`
- `Chat`
- `ChatWhisper`
- `ChatShout`
- `SendInstantMessage`
- `Voice`
- `QueryVoice`
- `Voices`
- `Say`
- `MoveBy`
- `WalkTo`
- `FlyTo`
- `TeleportTo`
- `TeleportToRegionHandle`
- `StopMovement`
- `StartMovement`
- `LookAt`
- `SetCameraHeading`
- `GetCameraState`
- `Follow`
- `StopFollow`
- `PrimCreate`
- `PrimSetPosition`
- `PrimSetScale`
- `PrimSetRotation`
- `PrimSetTexture`
- `PrimSetFaceParams`
- `PrimNudgeFaceUv`
- `PrimApplyUvPreset`
- `PrimTileUv`
- `PrimTileUvNonUniform`
- `PrimSetName`
- `PrimSetDescription`
- `PrimLink`
- `PrimUnlink`
- `PrimClone`
- `PrimInspect`
- `PrimFindByName`
- `PrimListNearby`
- `PrimRequestPayPrice`
- `PrimBuy`
- `WalletGetBalance`
- `Pay`
- `PrimSelect`
- `PrimDeselect`
- `PrimDelete`
- `PrimDeleteMany`
- `InventoryList`
- `InventoryGiveItem`
- `InventoryGiveFolder`
- `TaskInventoryList`
- `TaskInventoryTake`
- `AssetUploadInventory`
- `AssetDownload`
- `TextureDownload`
- `MeshInspectGltf`
- `MeshUploadGltf`
- `InventoryOfferPolicyRuleAdd`
- `InventoryOfferPolicyRulesList`
- `InventoryOfferPolicyRulesClear`
- `InventoryOfferHistoryList`
- `EventStreamSubscribe`
- `EventStreamUnsubscribe`
- `EventStreamPoll`
- `EventStreamHistory`
- `EventStreamStats`
- `InventoryOfferPolicyRulesSave`
- `InventoryOfferPolicyRulesLoad`
- `AppearanceListWorn`
- `AppearanceWearFolder`
- `AppearanceAttachItem`
- `AppearanceDetachItem`
- `AppearanceRebake`
- `AppearanceVisualParamsList`
- `AppearanceVisualParamSet`
- `AppearanceBakeDiagnostics`
- `ScriptUploadAgent`
- `ScriptUploadTask`
- `ScriptCopyInventoryToTask`
- `ScriptGetTaskRunning`
- `ScriptSetTaskRunning`
- `EnvGetRegion`
- `EnvGetParcel`
- `EnvResetRegion`
- `EnvResetParcel`
- `EnvSetRegionRaw`
- `EnvSetParcelRaw`
- `EnvGetLegacy`
- `EnvSetLegacyRaw`
- `EnvResetLegacy`

Chat notes:
- `Chat` accepts optional `chatType` (case-insensitive) and defaults to `Normal`.
- Supported `chatType` values are `Whisper`, `Normal`, `Shout`, `StartTyping`, `StopTyping`, `Debug`, `OwnerSay`, `RegionSayTo`, `RegionSay`.
- `ChatWhisper` and `ChatShout` are thin wrappers that set `chatType` to `Whisper`/`Shout` respectively.
- Receive-side local/group chat filtering is env-only via `LOCAL_CHAT_ALLOWED_TYPES` (comma/pipe/space-separated `ChatType` names, default `Normal`).
- Incoming local chat modalities not in `LOCAL_CHAT_ALLOWED_TYPES` are logged and ignored before AI routing.
- Incoming group chat (`SessionSend`) is filtered using the same allow-list and treated as `Normal` modality.
- In this stage, only avatar-to-bot IM is routed to Opencode.
- Optional handler mode: when `OPENSIM_HANDLER_CONFIG` points to a handler JSON file (default `/config/handlers.json`), only listed handler avatars may control the bot; others get a friendly deny reply.
- Prompt layering is enabled by default with this precedence (low -> high): built-in bridge prompt, project `AGENTS.md`, then in-world `AGENTS.md` notecard.
- In-world prompt install is strict and handler-gated by default: only notecards named `AGENTS.md` are eligible, and when `PROMPT_NOTECARD_REQUIRE_HANDLER=true`, only avatars listed in the handler JSON (or the configured parent controller) can install/replace it.
- IM supports "star commands" (prefixed with `*`) for live AI configuration per avatar conversation:
  - `*help` (summary of unique commands)
  - `*help <command>` (detailed variants for one command)
  - `*help all` (detailed variants for all commands)
  - `*status`
  - `*cancel` (abort the current in-flight AI request for this IM)
  - `*prompt status` (show prompt layer state)
  - `*prompt show [effective|builtin|project|notecard]` (preview prompt text)
  - `*prompt clear-notecard` (remove active in-world prompt layer)
  - `*prompt reload-project` (re-read project `AGENTS.md`)
  - `*permission list` (list pending policy permission requests)
  - `*permission allow <permission-id> [remember]` (approve a pending permission)
  - `*permission deny <permission-id> [remember]` (reject a pending permission)
  - `*question list` (list pending question prompts from Opencode tools)
  - `*question answer <question-id> <text>` (answer a pending question)
  - `*question reject <question-id>` (reject a pending question)
  - `*providers` (all available providers from Opencode)
  - `*providers configured` (only configured/active providers)
  - `*models [provider]` (live list from Opencode server)
  - `*auth methods [provider]` (list provider auth methods)
  - `*auth <provider-id> api <api-key>` (store provider API key over Opencode HTTP API)
  - `*auth <provider-id> oauth [method-index]` (start OAuth/device flow)
  - `*auth <provider-id> oauth-complete [method-index] [code]` (complete OAuth flow)
  - `*session list` (list all Opencode sessions)
  - `*session create [title] [--no-select]` (create a new Opencode session; selected for this IM by default)
  - `*session use <session-id>` / `*session select <session-id>` (switch this IM to an existing session)
  - `*session status` (show session status map for all sessions)
  - `*session current` (show the active session id mapped to this IM)
  - `*session details <session-id|current>` (show full session JSON)
  - `*session children <session-id|current>` (list child sessions)
  - `*session patch-title <session-id|current> <new-title>` (rename a session)
  - `*session summarize <session-id|current> [provider/model]` (request session summary)
  - `*session abort <session-id|current>` (abort a running session)
  - `*session delete <session-id|current> [--force]` (delete a session; confirmation required)
  - `*session delete --all [--force]` (delete all sessions; confirmation required)
  - `*projects` (list all Opencode projects)
  - `*project current` (show the current Opencode project)
  - `*configure <provider-name-or-id>` (select provider and auto-pick a model)
  - `*configure model <provider/model-id>`
  - `*configure thinking <low|medium|high|off>`
  - `*configure reset` / `*reset`
- Bridge install behavior: `*bridge install` now uses pre-imported inventory content from folder `Cube Bot IAR` (attachment `The Cube Bot` and wearable `Full Body Alpha`).
- Bridge attachment point: install attaches `The Cube Bot` to `Spine` (legacy RightEar/Skull fallback flow was removed).
- Region-enter behavior: startup auto-provision checks whether `The Cube Bot` is already worn before reinstalling.
- Session switch behavior: `*session create` selects the created session for this IM by default; pass `--no-select` to keep the previous active session.
- Session switch validation: `*session use`/`*session select` validates the target session exists before switching.
- Busy-request behavior: if the bot is still processing a previous request, it will prompt you to use `*cancel`.
- Permission-request behavior: policy prompts can be answered with `yes`/`no` (mapped to latest pending request), or explicitly with `*permission allow|deny <permission-id> [remember]`.
- Question-request behavior: when Opencode emits question prompts (`question.asked`), the bot now auto-shows a friendly prompt in IM, and plain text replies are treated as answers when possible. You can still use `*question list`, `*question answer`, or `*question reject`.
- Event listener behavior: the bridge now keeps `/event` and `/global/event` listeners on to maintain pending permission/question state without extra runtime toggles.
- OAuth behavior: `*auth <provider> oauth-complete` now reports pending (instead of hard failing) when callback is accepted but provider activation has not propagated yet; complete browser approval and retry.
- Delete confirmation behavior: run `*session delete <id>` first to get a safety prompt, then rerun with `--force`.
- Bulk delete confirmation behavior: run `*session delete --all` first to get a safety prompt, then rerun with `--force`.
- Inventory offers from configured handler avatars are always accepted (policy rules are bypassed for handler offers).
- TODO: add local chat and group chat routing.
- Voice routing note: `VOICE_BACKEND=webrtc` is the supported backend for Piper WAV injection in this service.
- TODO: add a security policy to control which users the AI may respond to.

UV preset notes:
- `PrimApplyUvPreset` supports: `fit`, `reset`, `tile2x2`, `tile4x4`, `flipU`, `flipV`, `rotate90`, `rotate180`, `rotate270`, `center`.
- `PrimTileUv` sets U/V repeat to the same numeric tiling factor (`NxN`).
- `PrimTileUvNonUniform` sets independent U/V repeat values.

Movement notes:
- `WalkTo`/`FlyTo` use stepped autopilot waypoints for improved reliability over larger distances.
- `TeleportTo` resolves named regions to handles before teleporting for stricter targeting.

Animation notes:
- `AnimationStart`/`AnimationStop` accept either a built-in animation name (e.g. `DANCE1`, `WAVE`, `CLAP`, `SIT`) or a raw animation UUID.
- `AnimationsList` returns all built-in animation names/UUIDs from LibreMetaverse's `Animations` class.
- `ActiveAnimations` returns the bot's currently signaled animations with their sequence IDs.

Environment notes:
- `EnvGetRegion`/`EnvGetParcel` return a structured result with `PayloadJson` containing the LLSD object as JSON.
- `EnvSetRegionRaw`/`EnvSetParcelRaw` accept `payloadFormat` of `auto`, `json`, or `xml`.
- For EEP raw set, payload can be either a direct `EnvironmentData` map or a wrapper object containing `environment`.
- `EnvSetLegacyRaw` expects a legacy `EnvironmentSettings` LLSD map payload.

Inventory and asset notes:
- `AssetUploadInventory` accepts either a local file path or an `http/https` URL as `source`.
- `AssetUploadInventory` accepts `assetType`/`inventoryType` as explicit values or `auto` (inferred from source/name extension such as `.lsl`, `.txt`, `.jp2`, `.ogg`, `.bvh`).
- `AssetDownload` and `TextureDownload` use `outputMode`: `both` (default), `base64`, or `tempfile`.
- `MeshInspectGltf` preflights `.glb`/`.gltf` content and reports what can upload, what will be skipped, and texture ingest/transcode diagnostics.
- `MeshUploadGltf` uploads mesh assets from local paths or HTTP sources and returns created inventory/asset IDs plus conversion warnings when present.
- For production workflows, run `MeshInspectGltf` in strict mode before `MeshUploadGltf` to prevent partial uploads.
- Incoming inventory offers are policy-driven: first matching rule decides `accept` or `decline`; unmatched offers are declined by default.
- `TaskInventoryTake` requests transfer from object (task) inventory into avatar inventory; server permissions determine copy-vs-move behavior.
- Cross-avatar "take/copy" is offer-based: you can receive what another avatar offers, but cannot arbitrarily pull from another avatar inventory.
- `InventoryOfferPolicyRulesSave`/`InventoryOfferPolicyRulesLoad` persist policy rules as JSON; startup auto-load occurs when `INVENTORY_OFFER_POLICY_FILE` exists.

Event stream notes:
- Hybrid default: use `EventStreamSubscribe` once, then `EventStreamPoll` with a cursor and non-zero `waitMs` for reactive long-poll behavior.
- Channels are split as `general`, `object`, and `teleport` (`all` is accepted as shorthand).
- Event filtering supports `eventTypes`, `radiusMeters`, `objectIds`, `objectLocalIds`, and `chatSources` on subscribe and poll.
- Buffers are bounded per channel; when full, oldest events are trimmed.
- `EventStreamPoll` includes trim diagnostics (`CursorTrimmed`, `Trimmed*`) so clients can detect loss and recover.
- `EventStreamHistory` returns a short retained window (`lastSeconds`) for debugging/replay without changing subscription cursors.
- `EventStreamStats` reports current buffer occupancy and cumulative trim counters.

Appearance and script notes:
- `AppearanceWearFolder` expects a folder containing wearable/attachment items (or links to them) and delegates to `Appearance.WearOutfitAsync`.
- `AppearanceAttachItem` can use an explicit `attachmentPoint`, or falls back to the item's default point when available.
- `AppearanceVisualParamsList` exposes slider metadata (ranges/default/group/wearable) plus current values.
- `AppearanceVisualParamSet` edits one group-0 slider parameter and requests a rebake/update.
- `AppearanceBakeDiagnostics` returns baked texture slot IDs and optional cache-probe latency diagnostics.
- `ScriptUploadAgent`/`ScriptUploadTask` return compile status and compiler messages when the grid reports them.
- `ScriptSetTaskRunning` can verify state by requesting `ScriptRunningReply` after sending the state change.

## Common workflows

Use these as practical MCP call sequences when building assistants/agents on top of this server.

### 1) Wear an outfit folder and adjust attachments

1. Find the folder UUID for your outfit with `InventoryList`.
2. Apply the outfit with `AppearanceWearFolder(folderId, replaceItems=true)`.
3. Check current state with `AppearanceListWorn`.
4. Optionally attach/detach specific items with `AppearanceAttachItem` / `AppearanceDetachItem`.
5. If the grid needs it, request final update with `AppearanceRebake(forceRebake=true)`.

Suggested tool flow:

```text
InventoryList(folderId="", recursive=true, maxResults=500)
AppearanceWearFolder(folderId="<outfit-folder-uuid>", replaceItems=true)
AppearanceListWorn()
AppearanceAttachItem(itemId="<attachment-item-uuid>", attachmentPoint="RightHand", replace=true)
AppearanceDetachItem(itemId="<attachment-item-uuid>")
AppearanceRebake(forceRebake=true)
```

### 2) Upload script, push to object, and verify running state

1. Update an existing agent script item from local path/URL with `ScriptUploadAgent`.
2. Copy that script into object task inventory with `ScriptCopyInventoryToTask`.
3. List task inventory (`TaskInventoryList`) to confirm script item IDs on the object.
4. Start/stop and verify script state using `ScriptSetTaskRunning(..., verifyAfterSet=true)`.
5. Query at any time with `ScriptGetTaskRunning`.

Suggested tool flow:

```text
ScriptUploadAgent(source="https://example.invalid/MyScript.lsl", itemId="<agent-script-item-uuid>", mono=true)
ScriptCopyInventoryToTask(objectLocalId=123456, inventoryScriptItemId="<agent-script-item-uuid>", enableScript=true)
TaskInventoryList(objectLocalId=123456, objectId="<object-uuid>", maxResults=200)
ScriptSetTaskRunning(objectId="<object-uuid>", scriptItemId="<task-script-item-uuid>", running=true, verifyAfterSet=true)
ScriptGetTaskRunning(objectId="<object-uuid>", scriptItemId="<task-script-item-uuid>")
```

Alternative import example when you only have a source file/URL and want the server to infer type:

```text
AssetUploadInventory(source="./go-away.lsl", assetType="auto", inventoryType="auto", name="Go Away", description="Touch says Go away!", folderId="")
```

### 3) Manage inventory-offer policy rules with persistence

1. Optionally configure policy persistence file via env/CLI.
2. Add rules with `InventoryOfferPolicyRuleAdd` (first match wins).
3. Inspect active rules and decisions with `InventoryOfferPolicyRulesList` and `InventoryOfferHistoryList`.
4. Save/load explicitly with `InventoryOfferPolicyRulesSave` and `InventoryOfferPolicyRulesLoad`.

Policy file configuration example:

```bash
export INVENTORY_OFFER_POLICY_FILE="./inventory-offer-policy.json"
export INVENTORY_OFFER_POLICY_AUTOSAVE="true"
```

Suggested tool flow:

```text
InventoryOfferPolicyRuleAdd(name="accept-textures-from-builder", action="accept", senderAgentId="<avatar-uuid>", senderNameContains="", assetType="Texture", fromTask=null, destinationFolderId="<textures-folder-uuid>")
InventoryOfferPolicyRuleAdd(name="decline-task-offers", action="decline", senderAgentId="", senderNameContains="", assetType="", fromTask=true, destinationFolderId="")
InventoryOfferPolicyRulesList()
InventoryOfferHistoryList(maxResults=50)
InventoryOfferPolicyRulesSave(filePath="")
InventoryOfferPolicyRulesLoad(filePath="", replaceExisting=true)
```

## Environment payload templates

Use these with `EnvSetRegionRaw` or `EnvSetParcelRaw` and `payloadFormat: "json"`.

Minimal direct `EnvironmentData` payload:

```json
{
  "day_length": 14400,
  "day_offset": 57600,
  "flags": 0,
  "day_cycle": {
    "type": "daycycle",
    "tracks": []
  }
}
```

Equivalent wrapper payload (`environment` key):

```json
{
  "environment": {
    "day_length": 14400,
    "day_offset": 0,
    "flags": 1,
    "day_cycle": {
      "name": "CrazyEEP",
      "type": "daycycle",
      "frames": {
        "914279448676717175": {
          "type": "water",
          "blur_multiplier": 0.12,
          "fresnel_offset": 0.2,
          "fresnel_scale": 0.9,
          "normal_scale": [
            6,
            6,
            6
          ],
          "normal_map": "822ded49-9a6c-f61c-cb89-6df54f42cdf4",
          "scale_above": 0.06,
          "scale_below": 0.35,
          "underwater_fog_mod": 0.1,
          "water_fog_color": [
            0,
            1,
            0.85
          ],
          "water_fog_density": 40,
          "wave1_direction": [
            1.8,
            -1.6
          ],
          "wave2_direction": [
            -1.7,
            -1.2
          ],
          "transparent_texture": "2bfd3884-7e27-69b9-ba3a-3e673f680004"
        },
        "15123771676403276959": {
          "type": "sky",
          "ambient": [
            1.8,
            0.2,
            1.4
          ],
          "cloud_color": [
            0.2,
            1.3,
            0.6
          ],
          "cloud_pos_density1": [
            1,
            0.9,
            1
          ],
          "cloud_pos_density2": [
            1,
            0.4,
            0.2
          ],
          "cloud_scroll_rate": [
            0.8,
            0.4
          ],
          "cloud_shadow": 0.05,
          "gamma": 1.4,
          "glow": [
            25,
            0.001,
            -0.52
          ],
          "legacy_haze": {
            "ambient": [
              1.5,
              0.4,
              1.3
            ],
            "blue_density": [
              0.08,
              0.35,
              0.95
            ],
            "blue_horizon": [
              1,
              0.15,
              0.02
            ],
            "density_multiplier": 0.0009,
            "distance_multiplier": 0.4,
            "haze_density": 0.2,
            "haze_horizon": 0.02
          },
          "moon_brightness": 0.1,
          "star_brightness": 80,
          "sunlight_color": [
            2.8,
            0.35,
            0.2,
            1
          ],
          "sun_scale": 2.5,
          "moon_scale": 0.6,
          "sun_arc_radians": 0.002,
          "sky_bottom_radius": 6360,
          "sky_top_radius": 6420,
          "planet_radius": 6360,
          "dome_offset": 0.96,
          "dome_radius": 15000,
          "max_y": 1605,
          "mie_config": [
            {
              "anisotropy": 0.95,
              "constant_term": 0,
              "exp_scale": -0.0007,
              "exp_term": 1,
              "linear_term": 0,
              "width": 0
            }
          ],
          "rayleigh_config": [
            {
              "constant_term": 0,
              "exp_scale": -0.00007,
              "exp_term": 1,
              "linear_term": 0,
              "width": 0
            }
          ],
          "absorption_config": [
            {
              "constant_term": 0.8,
              "exp_scale": 0,
              "exp_term": 0,
              "linear_term": 0,
              "width": 0
            },
            {
              "constant_term": 0.7,
              "exp_scale": 0,
              "exp_term": 0,
              "linear_term": -0.0001,
              "width": 0
            }
          ],
          "sun_rotation": [
            0,
            -0.8,
            0,
            0.6
          ],
          "moon_rotation": [
            0,
            0.8,
            0,
            0.6
          ],
          "halo_id": "12149143-f599-91a7-77ac-b52a3c0f59cd",
          "rainbow_id": "11b4c57c-56b3-04ed-1f82-2004363882e4",
          "bloom_id": "3c59f7fe-9dc8-47f9-8aaf-a9dd1fbc3bef",
          "cloud_id": "1dc1368f-e8fe-f02d-a08d-9d9f11c1af6b",
          "sun_id": "00000000-0000-0000-0000-000000000000",
          "moon_id": "d07f6eed-b96a-47cd-b51d-400ad4a1c428",
          "ice_level": 0,
          "moisture_level": 0,
          "droplet_radius": 800
        }
      },
      "tracks": [
        [
          {
            "key_keyframe": 0,
            "key_name": "914279448676717175"
          }
        ],
        [
          {
            "key_keyframe": 0,
            "key_name": "15123771676403276959"
          }
        ],
        [],
        [],
        []
      ]
    }
  }
}
```

Legacy payload starter for `EnvSetLegacyRaw`:

```json
{
  "type": "WL",
  "sky": {},
  "water": {}
}
```

Tip: call `EnvGetRegion` or `EnvGetLegacy` first and use the returned `PayloadJson` as your edit baseline.

## Health endpoint

- `GET /healthz` returns runtime status plus bot location.
