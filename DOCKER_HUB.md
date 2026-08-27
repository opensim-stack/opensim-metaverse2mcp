# opensim-metaverse2mcp

`opensim-metaverse2mcp` is an MCP server that logs in an OpenSim bot and exposes in-world actions as tools over HTTP MCP.

It is intended to be used as part of the **OpenSim Stack** project:
**"A docker stack to get an AI integrated virtual world up and running in minutes."**

## What This Image Does

- Connects a bot account to your OpenSim region at startup
- Exposes bot control and building/environment tools via MCP over HTTP
- Supports movement, chat/IM, teleport, prim creation/editing, and environment controls

## Quick Start

Run the container with your bot credentials and OpenSim login URI:

```bash
docker run --rm \
  -e OPENSIM_BOT_FIRST=But \
  -e OPENSIM_BOT_LAST=User \
  -e OPENSIM_BOT_PASSWORD=botpassword \
  -e OPENSIM_LOGIN_URI=http://host.docker.internal:9000 \
  -e METAVERSE_MCP_TRANSPORT=http \
  -e METAVERSE_MCP_HOST=0.0.0.0 \
  -e METAVERSE_MCP_PORT=8999 \
  -e METAVERSE_MCP_HTTP_ENDPOINT=/mcp \
  -p 8999:8999 \
  bithatch/opensim-metaverse2mcp:latest
```

Then connect your MCP client to:

- `http://localhost:8999/mcp`

## Project Links

- Main AI Stack (`opensim-ai-docker`): https://github.com/opensim-stack/opensim-ai-docker
- `opensim-metaverse2mcp` on GitHub: https://github.com/opensim-stack/opensim-metaverse2mcp
- Related MCP server (`opensim-console2mcp`):
  - GitHub: https://github.com/opensim-stack/opensim-console2mcp
  - Docker Hub: https://hub.docker.com/repository/docker/bithatch/opensim-console2mcp/general
