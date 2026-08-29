## Build

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