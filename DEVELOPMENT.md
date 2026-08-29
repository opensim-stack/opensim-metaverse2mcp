# Development

If you are working on `opensim-metaverse2mcp` you may find it more convenient to run directly instead of via docker, or using a local Docker image.

## Requirements

- .NET SDK 8.0+ to build
- Runtime: .NET 8 (`mcr.microsoft.com/dotnet/aspnet:8.0` for container)

## Run (local)

Set required bot credentials and run:

```bash
export OPENSIM_BOT_FIRST="Bot"
export OPENSIM_BOT_LAST="User"
export OPENSIM_BOT_PASSWORD="botpassword"
export OPENSIM_LOGIN_URI="http://localhost:9000"

export METAVERSE_MCP_TRANSPORT="http"
export METAVERSE_MCP_HOST="0.0.0.0"
export METAVERSE_MCP_PORT="8999"
export METAVERSE_MCP_HTTP_ENDPOINT="/mcp"

export OPENCODE_SCHEME="http"
export OPENCODE_HOST="localhost"
export OPENCODE_PORT="8998"
export SPAWNER_HOST="opensim-ai-spawner"
export SPAWNER_PORT="8993"
# optional bearer token if OPENSIM_SPAWNER_TOKEN is set on opensim-spawner:
# export SPAWNER_TOKEN=""
# voice routing (optional)
export VOICE_ROUTING_ENABLED="true"
export VOICE_BACKEND="webrtc"
export PIPER_SCHEME="http"
export PIPER_HOST="opensim-ai-piper-1"
export PIPER_PORT="8995"
export PIPER_DEFAULT_VOICE="en_US-lessac-medium"
# optional Basic auth:
# export OPENCODE_SERVER_USERNAME="opencode"
# export OPENCODE_SERVER_PASSWORD="change-me"

dotnet run --project ./src/opensim-metaverse2mcp.csproj -c Release
```

### CLI override example

```bash
dotnet run --project ./src/opensim-metaverse2mcp.csproj -c Release -- \
  --first-name Bot \
  --last-name User \
  --password botpassword \
  --login-uri http://localhost:9000 \
  --mcp-host 0.0.0.0 \
  --mcp-port 8999 \
  --mcp-http-endpoint /mcp
```

```bash
dotnet restore ./src/opensim-metaverse2mcp.csproj
dotnet build ./src/opensim-metaverse2mcp.csproj -c Release
```

## Docker

Build:

```bash
docker build -t opensim-metaverse2mcp:local .
```

Run:

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
  opensim-metaverse2mcp:local
```