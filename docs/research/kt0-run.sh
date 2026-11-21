#!/usr/bin/env bash
# КТ0: один прогон живого CLI внутри cc-sandbox через gateway текущей сессии-оператора.
# Окружение берётся из env вызывающего (ANTHROPIC_* сессии); localhost:80 -> адрес docker0.
# Использование:
#   [KT0_POLICY=allow|deny] [KT0_DENY_TOOLS=Read,...] [KT0_EXTRA="--setting-sources user"] \
#   bash docs/research/kt0-run.sh <log-имя> <prompt> [доп. аргументы CLI...]
set -euo pipefail
LOG="$1"; PROMPT="$2"; shift 2
# host.docker.internal — как у продукта (SandboxManager.cs:16, :191); он же в AllowedHosts сервера.
GW="${ANTHROPIC_BASE_URL//localhost:80/host.docker.internal:80}"
# shellcheck disable=SC2086
docker exec -w /projects/kt0_20261119/A \
  -e ANTHROPIC_BASE_URL="$GW" \
  -e ANTHROPIC_CUSTOM_HEADERS="$ANTHROPIC_CUSTOM_HEADERS" \
  -e ANTHROPIC_API_KEY="$ANTHROPIC_API_KEY" \
  -e ANTHROPIC_AUTH_TOKEN="$ANTHROPIC_AUTH_TOKEN" \
  -e ANTHROPIC_MODEL="$ANTHROPIC_MODEL" \
  -e ANTHROPIC_DEFAULT_SONNET_MODEL="$ANTHROPIC_DEFAULT_SONNET_MODEL" \
  -e ANTHROPIC_DEFAULT_HAIKU_MODEL="$ANTHROPIC_DEFAULT_HAIKU_MODEL" \
  -e ANTHROPIC_DEFAULT_OPUS_MODEL="$ANTHROPIC_DEFAULT_OPUS_MODEL" \
  -e ANTHROPIC_DEFAULT_FABLE_MODEL="$ANTHROPIC_DEFAULT_FABLE_MODEL" \
  -e CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
  cc-sandbox node /projects/kt0_20261119/kt0-driver.mjs \
  --log "/projects/kt0_20261119/logs/$LOG.log" \
  --prompt "$PROMPT" \
  --policy "${KT0_POLICY:-allow}" \
  --deny-tools "${KT0_DENY_TOOLS:-}" \
  --timeout 150000 \
  -- /opt/claude-host/versions/2.1.325 --print --verbose \
  --output-format stream-json --input-format stream-json \
  --permission-prompt-tool stdio --permission-mode default \
  --tools Read,Glob,Grep,Write,Edit \
  ${KT0_EXTRA:-} "$@"
