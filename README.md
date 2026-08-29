# opensim-metaverse2mcp

[![Docker Hub](https://img.shields.io/badge/Docker%20Hub-bithatch%2Fopensim--metaverse2mcp-2496ED?logo=docker&logoColor=white)](https://hub.docker.com/repository/docker/bithatch/opensim-metaverse2mcp)

`opensim-metaverse2mcp` is a LibreMetaverse-based OpenSim bot that exposes bot actions as MCP tools over **Streamable HTTP**.

The server logs in the bot on startup (no separate login tool), then serves MCP at a configurable HTTP endpoint.

*This is part of the [opensim-stack](https://opensim-stack.github.io/) and is intended to be used in conjunction with other parts of the stack. See [Docs](https://opensim-stack.github.io/docs/index.html) for full details.*

**For Issues And Discussions see main project [opensim-ai-docker](https://github.com/opensim-stack/opensim-ai-docker)**

## What it does

- Uses `LibreMetaverse` (`3.1.3`) to connect to OpenSim/SL-compatible grids.
- Routes avatar IM conversations to Opencode server sessions with [NOpenCode](https://github.com/ylvict/NOpenCode).
- Uses MCP tools for avatar actions that the AI can invoke via Opencode.
- Exposes MCP tools with the official C# MCP libraries:
  - `ModelContextProtocol`
  - `ModelContextProtocol.AspNetCore`
- Supports config via environment variables and CLI args (CLI overrides env).
- Gives the Bot a voice via Piper and WebRTC (Janus) - Other stack parts required

## Volumes

| Name | Description |
| ---- | ----------- |
| /config | Various configuration files |

## Configuration

The Bot can only be instructed by a *Handler*. You must define who the bot handler is (i.e. you), in `/config/handlers.json`. E.g if your name is *Alice McSim* 

```json
[ {
  "botFirst" : "*",
  "botLast" : "*",
  "handlerFirst" : "Alice",
  "handlerLast" : "McSim"
} ]
```

## Run

Assuming you are in the same directory as where a `config` directory also exists with the above *Handler* configuration.

```bash
docker run --rm \
  -e OPENSIM_BOT_FIRST=Governor \
  -e OPENSIM_BOT_LAST=Bot \
  -e OPENSIM_BOT_PASSWORD=botpassword \
  -e OPENSIM_LOGIN_URI=http://host.docker.internal:9000 \
  -e MCP_TRANSPORT=http \
  -e MCP_HOST=0.0.0.0 \
  -e MCP_PORT=8999 \
  -e MCP_HTTP_ENDPOINT=/mcp \
  -p 8999:8999 \
  -v config:/config \
  bithatch/opensim-metaverse2mcp:latest
```

## Environment Variables

### Bot login (required)

- `OPENSIM_BOT_FIRST`
- `OPENSIM_BOT_LAST`
- `OPENSIM_BOT_PASSWORD`

### Bot login (optional)

- `OPENSIM_LOGIN_URI` (default: `http://opensim:9000`)
- `OPENSIM_LOGIN_START` (default: `last`)
- `OPENSIM_LOGIN_TIMEOUT_SECONDS` (default: `30`)

### Spawner API integration

- `SPAWNER_HOST` (default: `opensim-ai-spawner`)
- `SPAWNER_PORT` (default: `8993`)
- `SPAWNER_TOKEN` (optional bearer token for spawner auth)
- Bot-management API path is fixed to `/api/bot` (for example `GET /api/bot`, `POST /api/bot/{first}/{last}`).

### MCP server

- `METAVERSE_MCP_TRANSPORT` (`http` or `sse`; default: `http`)
- `METAVERSE_MCP_HOST` (default: `0.0.0.0`)
- `METAVERSE_MCP_PORT` (default: `8999`)
- `METAVERSE_MCP_HTTP_ENDPOINT` (default: `/mcp`)
- `METAVERSE_MCP_HTTP_BEARER_TOKEN` (optional)
- `METAVERSE_MCP_HTTP_DISALLOW_DELETE` (`true`/`false`, default: `false`)
- `METAVERSE_MCP_DIAGNOSTICS` (`true`/`false`, default: `false`)
- `INVENTORY_OFFER_POLICY_FILE` (optional JSON file path)
- `INVENTORY_OFFER_POLICY_AUTOSAVE` (`true`/`false`, default: `true`)

### Opencode chat bridge

- `OPENCODE_SCHEME` (`http` or `https`, default: `http`)
- `OPENCODE_HOST` (default: `opensim-opencode`)
- `OPENCODE_PORT` (default: `8998`)
- `OPENCODE_SERVER_USERNAME` (optional Basic auth username)
- `OPENCODE_SERVER_PASSWORD` (optional Basic auth password)
- `OPENCODE_REQUEST_TIMEOUT_SECONDS` (default: `1800`)

### Voice routing (Piper + grid voice)

- `VOICE_ROUTING_ENABLED` (`true`/`false`, default: `false`)
- `VOICE_BACKEND` (`webrtc`, default: `webrtc`)
- `PIPER_SCHEME` (`http` or `https`, default: `http`)
- `PIPER_HOST` (default: `opensim-ai-piper-1`)
- `PIPER_PORT` (default: `8995`)
- `PIPER_TTS_PATH` (default: `/tts`)
- `PIPER_VOICES_PATH` (default: `/voices`)
- `PIPER_TIMEOUT_SECONDS` (default: `60`)
- `PIPER_DEFAULT_VOICE` (default: `en_US-lessac-medium`)

### Layered prompt handling

- `PROMPT_HANDLING_ENABLED` (`true`/`false`, default: `true`)
- `PROMPT_BUILTIN_ENABLED` (`true`/`false`, default: `true`)
- `PROMPT_PROJECT_AGENTS_ENABLED` (`true`/`false`, default: `true`)
- `PROMPT_PROJECT_AGENTS_FILE` (default: `AGENTS.md`)
- `PROMPT_NOTECARD_ENABLED` (`true`/`false`, default: `true`)
- `PROMPT_NOTECARD_REQUIRE_HANDLER` (`true`/`false`, default: `true`)
- `PROMPT_MAX_CHARS` (default: `16000`, minimum effective clamp: `512`)

This repository includes a starter project prompt file at `AGENTS.md`.

Notes:
- `MCP_TRANSPORT=sse` enables legacy SSE compatibility in the MCP HTTP transport.
- This server always runs MCP over HTTP (streamable transport), not stdio.

## More

 * [TOOLS.md](TOOLS.md) - More information about the MCP tools exposed themselves
 * [DEVELOPMENT.md](DEVELOPMENT.md) - If you want to make changes to the MCP server, if you want to run without Docker etc.
 * [BUILDING.md](BUILDING.md) - Other building instructions.
 