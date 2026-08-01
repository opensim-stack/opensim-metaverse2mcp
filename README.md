# opensim-metaverse2mcp

[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-bithatch%2Fopensim--metaverse2mcp-2496ED?logo=docker&logoColor=white)](https://hub.docker.com/repository/docker/bithatch/opensim-metaverse2mcp)

`opensim-metaverse2mcp` is a LibreMetaverse-based OpenSim bot that exposes bot actions as MCP tools over **Streamable HTTP**.

The server logs in the bot on startup (no separate login tool), then serves MCP at a configurable HTTP endpoint.

## What it does

- Uses `LibreMetaverse` (`3.1.3`) to connect to OpenSim/SL-compatible grids.
- Routes avatar IM conversations to Opencode server sessions with [NOpenCode](https://github.com/ylvict/NOpenCode).
- Uses MCP tools for avatar actions that the AI can invoke via Opencode.
- Exposes MCP tools with the official C# MCP libraries:
  - `ModelContextProtocol`
  - `ModelContextProtocol.AspNetCore`
- Supports config via environment variables and CLI args (CLI overrides env).

## Requirements

- .NET SDK 8.0+ to build
- Runtime: .NET 8 (`mcr.microsoft.com/dotnet/aspnet:8.0` for container)

## Build

```bash
dotnet restore ./src/opensim-metaverse2mcp.csproj
dotnet build ./src/opensim-metaverse2mcp.csproj -c Release
```

## Run (local)

Set required bot credentials and run:

```bash
export OPENSIM_LOGIN_FIRSTNAME="BotFirst"
export OPENSIM_LOGIN_LASTNAME="BotLast"
export OPENSIM_LOGIN_PASSWORD="BotPassword"
export OPENSIM_LOGIN_URI="http://localhost:9000"

export MCP_TRANSPORT="http"
export MCP_HOST="0.0.0.0"
export MCP_PORT="8999"
export MCP_HTTP_ENDPOINT="/mcp"

export OPENCODE_CHAT_ENABLED="true"
export OPENCODE_SCHEME="http"
export OPENCODE_HOST="localhost"
export OPENCODE_PORT="8998"
# optional Basic auth:
# export OPENCODE_USERNAME="opencode"
# export OPENCODE_PASSWORD="change-me"

dotnet run --project ./src/opensim-metaverse2mcp.csproj -c Release
```

### CLI override example

```bash
dotnet run --project ./src/opensim-metaverse2mcp.csproj -c Release -- \
  --first-name BotFirst \
  --last-name BotLast \
  --password BotPassword \
  --login-uri http://localhost:9000 \
  --mcp-host 0.0.0.0 \
  --mcp-port 8999 \
  --mcp-http-endpoint /mcp
```

## Environment Variables

### Bot login (required)

- `OPENSIM_LOGIN_FIRSTNAME`
- `OPENSIM_LOGIN_LASTNAME`
- `OPENSIM_LOGIN_PASSWORD`

### Bot login (optional)

- `OPENSIM_LOGIN_URI` (default: `http://opensim:9000`)
- `OPENSIM_LOGIN_START` (default: `last`)
- `BOT_LOGIN_TIMEOUT_SECONDS` (default: `30`)

### MCP server

- `MCP_TRANSPORT` (`http` or `sse`; default: `http`)
- `MCP_HOST` (default: `0.0.0.0`)
- `MCP_PORT` (default: `8999`)
- `MCP_HTTP_ENDPOINT` (default: `/mcp`)
- `MCP_HTTP_BEARER_TOKEN` (optional)
- `MCP_HTTP_DISALLOW_DELETE` (`true`/`false`, default: `false`)
- `MCP_DIAGNOSTICS` (`true`/`false`, default: `false`)
- `INVENTORY_OFFER_POLICY_FILE` (optional JSON file path)
- `INVENTORY_OFFER_POLICY_AUTOSAVE` (`true`/`false`, default: `true`)

### Opencode chat bridge

- `OPENCODE_CHAT_ENABLED` (`true`/`false`, default: `true`)
- `OPENCODE_SCHEME` (`http` or `https`, default: `http`)
- `OPENCODE_HOST` (default: `opensim-opencode`)
- `OPENCODE_PORT` (default: `8998`)
- `OPENCODE_USERNAME` (optional Basic auth username)
- `OPENCODE_PASSWORD` (optional Basic auth password)
- `OPENCODE_SERVER_PASSWORD` (optional fallback alias for `OPENCODE_PASSWORD`)
- `OPENCODE_REQUEST_TIMEOUT_SECONDS` (default: `60`)

Notes:
- `MCP_TRANSPORT=sse` enables legacy SSE compatibility in the MCP HTTP transport.
- This server always runs MCP over HTTP (streamable transport), not stdio.

## Tool Surface

The server publishes tools including:

- `GetStatus`
- `Sit`
- `Stand`
- `Fly`
- `Jump`
- `Dance`
- `Chat`
- `SendInstantMessage`
- `MoveBy`
- `WalkTo`
- `FlyTo`
- `TeleportTo`
- `TeleportToRegionHandle`
- `StopMovement`
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
- `InventoryOfferPolicyRuleAdd`
- `InventoryOfferPolicyRulesList`
- `InventoryOfferPolicyRulesClear`
- `InventoryOfferHistoryList`
- `InventoryOfferPolicyRulesSave`
- `InventoryOfferPolicyRulesLoad`
- `AppearanceListWorn`
- `AppearanceWearFolder`
- `AppearanceAttachItem`
- `AppearanceDetachItem`
- `AppearanceRebake`
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
- In this stage, only avatar-to-bot IM is routed to Opencode.
- TODO: add local chat and group chat routing.
- TODO: add a security policy to control which users the AI may respond to.

UV preset notes:
- `PrimApplyUvPreset` supports: `fit`, `reset`, `tile2x2`, `tile4x4`, `flipU`, `flipV`, `rotate90`, `rotate180`, `rotate270`, `center`.
- `PrimTileUv` sets U/V repeat to the same numeric tiling factor (`NxN`).
- `PrimTileUvNonUniform` sets independent U/V repeat values.

Movement notes:
- `WalkTo`/`FlyTo` use stepped autopilot waypoints for improved reliability over larger distances.
- `TeleportTo` resolves named regions to handles before teleporting for stricter targeting.

Environment notes:
- `EnvGetRegion`/`EnvGetParcel` return a structured result with `PayloadJson` containing the LLSD object as JSON.
- `EnvSetRegionRaw`/`EnvSetParcelRaw` accept `payloadFormat` of `auto`, `json`, or `xml`.
- For EEP raw set, payload can be either a direct `EnvironmentData` map or a wrapper object containing `environment`.
- `EnvSetLegacyRaw` expects a legacy `EnvironmentSettings` LLSD map payload.

Inventory and asset notes:
- `AssetUploadInventory` accepts either a local file path or an `http/https` URL as `source`.
- `AssetDownload` and `TextureDownload` use `outputMode`: `both` (default), `base64`, or `tempfile`.
- Incoming inventory offers are policy-driven: first matching rule decides `accept` or `decline`; unmatched offers are declined by default.
- `TaskInventoryTake` requests transfer from object (task) inventory into avatar inventory; server permissions determine copy-vs-move behavior.
- Cross-avatar "take/copy" is offer-based: you can receive what another avatar offers, but cannot arbitrarily pull from another avatar inventory.
- `InventoryOfferPolicyRulesSave`/`InventoryOfferPolicyRulesLoad` persist policy rules as JSON; startup auto-load occurs when `INVENTORY_OFFER_POLICY_FILE` exists.

Appearance and script notes:
- `AppearanceWearFolder` expects a folder containing wearable/attachment items (or links to them) and delegates to `Appearance.WearOutfitAsync`.
- `AppearanceAttachItem` can use an explicit `attachmentPoint`, or falls back to the item's default point when available.
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

## Docker

Build:

```bash
docker build -t opensim-metaverse2mcp:local .
```

Run:

```bash
docker run --rm \
  -e OPENSIM_LOGIN_FIRSTNAME=BotFirst \
  -e OPENSIM_LOGIN_LASTNAME=BotLast \
  -e OPENSIM_LOGIN_PASSWORD=BotPassword \
  -e OPENSIM_LOGIN_URI=http://host.docker.internal:9000 \
  -e MCP_TRANSPORT=http \
  -e MCP_HOST=0.0.0.0 \
  -e MCP_PORT=8999 \
  -e MCP_HTTP_ENDPOINT=/mcp \
  -p 8999:8999 \
  opensim-metaverse2mcp:local
```

### Build and publish multiarch image

Create/use a buildx builder once:

```bash
docker buildx create --name multiarch --use
docker buildx inspect --bootstrap
```

Build and push Linux AMD64 + ARM64:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t bithatch/opensim-metaverse2mcp:latest \
  -t bithatch/opensim-metaverse2mcp:$(date +%Y%m%d) \
  --push \
  .
```