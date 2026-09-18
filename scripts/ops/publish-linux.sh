#!/usr/bin/env bash
# Сборка и публикация сервера на Linux-хосте — аналог фаз building/switching из
# deploy-agent.ps1 (без трея, ConPtyBridge и schtasks).
#
#   scripts/ops/publish-linux.sh [PUBLISH_DIR]      # по умолчанию /opt/ccs/app
#
# Фронт и mcp-dify должны быть собраны заранее (npm run build, npm run build в mcp-dify)
# либо передай BUILD_FRONTEND=1 / BUILD_DIFY=1. Машинно-специфичные файлы в PUBLISH_DIR
# (appsettings.Local.json, .mcp.json, logs/, data/) не трогаются.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PUBLISH_DIR="${1:-/opt/ccs/app}"
STAGING="${PUBLISH_DIR%/}.staging"

if [[ "${BUILD_FRONTEND:-0}" == 1 ]]; then (cd "$REPO/frontend" && npm ci --no-audit --no-fund && npm run build:quiet); fi
if [[ "${BUILD_DIFY:-0}" == 1 ]]; then (cd "$REPO/mcp-dify" && npm ci --no-audit --no-fund && npm run build); fi

[[ -f "$REPO/frontend/dist/index.html" ]] || { echo "фронт не собран: нет frontend/dist/index.html" >&2; exit 1; }

rm -rf "$STAGING"
mkdir -p "$STAGING"

echo "== publish-backend"
dotnet publish "$REPO/backend/ClaudeHomeServer/ClaudeHomeServer.csproj" -c Release -o "$STAGING" --nologo -v quiet
# Динамические модули ModuleLoader резолвит по пути — без них старт падает (см. deploy-agent.ps1)
for mod in notes/ClaudeHomeServer.Notes.dll spend/ClaudeHomeServer.Spend.dll; do
  [[ -f "$STAGING/modules/$mod" ]] || { echo "нет modules/$mod после publish" >&2; exit 1; }
done

echo "== pty-bridge"
gcc -O2 -static -o "$STAGING/pty-bridge" "$REPO/backend/pty-bridge/pty-bridge.c" -lutil

echo "== frontend -> wwwroot"
rsync -a --delete "$REPO/frontend/dist/" "$STAGING/wwwroot/"

echo "== mcp-dify"
if [[ -f "$REPO/mcp-dify/dist/index.js" ]]; then
  mkdir -p "$STAGING/mcp-dify"
  rsync -a --delete "$REPO/mcp-dify/dist/" "$STAGING/mcp-dify/dist/"
  rsync -a --delete "$REPO/mcp-dify/node_modules/" "$STAGING/mcp-dify/node_modules/"
  cp "$REPO/mcp-dify/package.json" "$STAGING/mcp-dify/"
else
  echo "  пропущено: нет mcp-dify/dist/index.js" >&2
fi

echo "== build-id"
sha="$(git -C "$REPO" rev-parse --short=8 HEAD)"
ref="$(git -C "$REPO" rev-parse --abbrev-ref HEAD)"
dirty=False; [[ -n "$(git -C "$REPO" status --porcelain)" ]] && dirty=True
printf '%s\nsha=%s\nref=%s\ndirty=%s\nbuiltAt=%s\n' \
  "$(date +%Y%m%d-%H%M%S)" "$sha" "$ref" "$dirty" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$STAGING/build-id.txt"

echo "== switch -> $PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"
rsync -a --delete \
  --exclude 'appsettings.Local.json' --exclude 'appsettings.Local.json.*' --exclude '.mcp.json' \
  --exclude '/logs/' --exclude '/data/' \
  "$STAGING/" "$PUBLISH_DIR/"
rm -rf "$STAGING"
echo "готово: $(head -2 "$PUBLISH_DIR/build-id.txt" | tr '\n' ' ')"
