## Build

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