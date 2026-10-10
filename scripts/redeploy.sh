#!/usr/bin/env bash
# Rebuilds the Mattiverse container and redeploys the dev stack (the project's live-reload / test workflow).
#   ./scripts/redeploy.sh [--fresh] [--port 8020]
set -euo pipefail

fresh=0
port=8020
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh) fresh=1 ;;
    --port) port="$2"; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

cd "$(dirname "$0")/.."
export MATTIVERSE_CHANNEL=local
export MATTIVERSE_BUILD_DATE="$(date -u +%Y%m%d)"
export MATTIVERSE_PORT="$port"

if [[ $fresh -eq 1 ]]; then
  echo "Removing the stack and its volume ..."
  docker compose -f docker-compose.dev.yml down -v
fi

docker compose -f docker-compose.dev.yml up -d --build --remove-orphans

echo
echo "Mattiverse local-${MATTIVERSE_BUILD_DATE} is running on http://localhost:${port}"
echo "Dev login: tester / Tester123!   |   SQLite web: http://localhost:8021"
