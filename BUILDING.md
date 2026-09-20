# Building

Build and publish a multiarch image.

## Local Build

```bash
docker build -t opensim-metaverse2mcp:local .
```

### Run Local

```bash
docker run --rm \
  -e OPENSIM_BOT_FIRST=Governor \
  -e OPENSIM_BOT_LAST=Bot \
  -e OPENSIM_BOT_PASSWORD=botpassword \
  -e OPENSIM_LOGIN_URI=http://host.docker.internal:9000 \
  -e METAVERSE_MCP_TRANSPORT=http \
  -e METAVERSE_MCP_HOST=0.0.0.0 \
  -e METAVERSE_MCP_PORT=8999 \
  -e METAVERSE_MCP_HTTP_ENDPOINT=/mcp \
  -p 8999:8999 \
  -v config:/config \
  opensim-metaverse2mcp:local
```

## Publish

### Setup

Create/use a buildx builder once:

```bash
docker buildx create --name multiarch --use
docker buildx inspect --bootstrap
```

### Build

Build and push Linux AMD64 + ARM64:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t bithatch/opensim-metaverse2mcp:latest \
  -t bithatch/opensim-metaverse2mcp:$(date +%Y%m%d) \
  --push \
  .
```

## Automated Publish (GitHub Actions)

This repository includes `.github/workflows/docker-publish.yml` to automatically build and push a multiarch image to Docker Hub.

### Triggers

- Pushes to `master` or `main` when `Dockerfile`, `docker/**`, `src/**`, `lsl/**`, `.dockerignore`, or the workflow itself changes
- Git tags matching `v*`
- Manual `workflow_dispatch`

### Required Repository Secrets

- `DOCKERHUB_USERNAME`: Docker Hub username or org robot account name
- `DOCKERHUB_TOKEN`: Docker Hub access token (recommended) or password

### Published Platforms and Tags

- Platforms: `linux/amd64`, `linux/arm64`
- Tags (default branch): `latest`, `YYYYMMDD`, and `sha-<commit>`
- Tags (tag builds): `<git-tag>` and `sha-<commit>`