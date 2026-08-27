#!/usr/bin/env sh
set -eu

transport="${MCP_TRANSPORT:-http}"
transport_lc=$(printf '%s' "$transport" | tr '[:upper:]' '[:lower:]')

case "$transport_lc" in
  http|sse)
    ;;
  *)
    echo "[opensim-metaverse2mcp] Unsupported MCP_TRANSPORT: $transport" >&2
    exit 1
    ;;
esac

set -- \
  --mcp-transport "$transport_lc" \
  --mcp-host "${METAVERSE_MCP_HOST:-0.0.0.0}" \
  --mcp-port "${METAVERSE_MCP_PORT:-8999}" \
  --mcp-http-endpoint "${MCP_HTTP_ENDPOINT:-/mcp}" \
  --first-name "${OPENSIM_BOT_FIRST:-}" \
  --last-name "${OPENSIM_BOT_LAST:-}" \
  --password "${OPENSIM_BOT_PASSWORD:-}" \
  --spawner-parent "${OPENSIM_SPAWNER_PARENT:-}" \
  --spawner-level "${OPENSIM_SPAWNER_LEVEL:-}" \
  --spawner-host "${SPAWNER_HOST:-${OPENSIM_NETWORK:-${COMPOSE_PROJECT_NAME:-opensim-ai}}-spawner}" \
  --spawner-port "${SPAWNER_PORT:-8993}" \
  --wear-folder-name "${WEAR_FOLDER_NAME:-}" \
  --login-uri "${OPENSIM_LOGIN_URI:-http://opensim:9000}" \
  --start-location "${OPENSIM_LOGIN_START:-last}" \
  --login-timeout-seconds "${OPENSIM_LOGIN_TIMEOUT_SECONDS:-30}" \
  --opencode-scheme "${OPENCODE_SCHEME:-http}" \
  --opencode-host "${OPENCODE_HOST:-opensim-opencode}" \
  --opencode-port "${OPENCODE_PORT:-8998}" \
  --opencode-timeout-seconds "${OPENCODE_REQUEST_TIMEOUT_SECONDS:-60}" \
  --handler-config "${OPENSIM_HANDLER_CONFIG:-/config/handlers.json}"

if [ -n "${OPENCODE_SERVER_USERNAME:-}" ]; then
  set -- "$@" --opencode-username "${OPENCODE_SERVER_USERNAME}"
fi

if [ -n "${OPENCODE_SERVER_PASSWORD:-}" ]; then
  set -- "$@" --opencode-password "${OPENCODE_SERVER_PASSWORD}"
fi

if [ -n "${METAVERSE_MCP_HTTP_BEARER_TOKEN:-}" ]; then
  set -- "$@" --mcp-http-bearer-token "${METAVERSE_MCP_HTTP_BEARER_TOKEN}"
fi

if [ -n "${SPAWNER_TOKEN:-}" ]; then
  set -- "$@" --spawner-token "${SPAWNER_TOKEN}"
fi

if [ "${MCP_HTTP_DISALLOW_DELETE:-false}" = "true" ]; then
  set -- "$@" --mcp-http-disallow-delete
fi

if [ "${MCP_DIAGNOSTICS:-false}" = "true" ]; then
  set -- "$@" --mcp-diagnostics
fi

exec dotnet /app/opensim-metaverse2mcp.dll "$@"
