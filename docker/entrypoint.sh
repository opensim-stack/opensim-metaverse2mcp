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
  --mcp-host "${MCP_HOST:-0.0.0.0}" \
  --mcp-port "${MCP_PORT:-8999}" \
  --mcp-http-endpoint "${MCP_HTTP_ENDPOINT:-/mcp}" \
  --first-name "${OPENSIM_LOGIN_FIRSTNAME:-}" \
  --last-name "${OPENSIM_LOGIN_LASTNAME:-}" \
  --password "${OPENSIM_LOGIN_PASSWORD:-}" \
  --login-uri "${OPENSIM_LOGIN_URI:-http://opensim:9000}" \
  --start-location "${OPENSIM_LOGIN_START:-last}" \
  --login-timeout-seconds "${BOT_LOGIN_TIMEOUT_SECONDS:-30}" \
  --opencode-chat-enabled "${OPENCODE_CHAT_ENABLED:-true}" \
  --opencode-scheme "${OPENCODE_SCHEME:-http}" \
  --opencode-host "${OPENCODE_HOST:-opensim-opencode}" \
  --opencode-port "${OPENCODE_PORT:-8998}" \
  --opencode-timeout-seconds "${OPENCODE_REQUEST_TIMEOUT_SECONDS:-60}" \
  --handler-first-name "${OPENCODE_HANDLER_FIRSTNAME:-}" \
  --handler-last-name "${OPENCODE_HANDLER_LASTNAME:-}"

if [ -n "${OPENCODE_USERNAME:-}" ]; then
  set -- "$@" --opencode-username "${OPENCODE_USERNAME}"
fi

if [ -n "${OPENCODE_PASSWORD:-}" ]; then
  set -- "$@" --opencode-password "${OPENCODE_PASSWORD}"
elif [ -n "${OPENCODE_SERVER_PASSWORD:-}" ]; then
  set -- "$@" --opencode-password "${OPENCODE_SERVER_PASSWORD}"
fi

if [ -n "${MCP_HTTP_BEARER_TOKEN:-}" ]; then
  set -- "$@" --mcp-http-bearer-token "${MCP_HTTP_BEARER_TOKEN}"
fi

if [ "${MCP_HTTP_DISALLOW_DELETE:-false}" = "true" ]; then
  set -- "$@" --mcp-http-disallow-delete
fi

if [ "${MCP_DIAGNOSTICS:-false}" = "true" ]; then
  set -- "$@" --mcp-diagnostics
fi

exec dotnet /app/opensim-metaverse2mcp.dll "$@"
