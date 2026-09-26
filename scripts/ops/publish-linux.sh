#!/usr/bin/env bash
# Сборка и публикация сервера на Linux-хосте — аналог фаз building/switching из
# deploy-agent.ps1 (без трея, ConPtyBridge и schtasks) — плюс сборка релизов агента
# устройства (ADR-016, задача AD-2: 1.<rev-count>.0, sha8 — в InformationalVersion, win-x64 zip, linux-x64 tar.gz).
#
#   scripts/ops/publish-linux.sh [PUBLISH_DIR]
#
# Фронт и mcp-dify должны быть собраны заранее (npm run build, npm run build в mcp-dify)
# либо передай BUILD_FRONTEND=1 / BUILD_DIFY=1.
#
# Переменные:
#   PUBLISH_DIR        куда положить сервер (по умолчанию /opt/ccs/app)
#   AGENT_RELEASES     каталог релизов агента ($(dirname PUBLISH_DIR)/agent-releases)
#   AGENT_ONLY=1       собрать только релиз агента (режим дев-стенда Веры)
#   SKIP_AGENT=1       пропустить сборку и упаковку агента
#   AGENT_KEEP=4       сколько версий хранить в каталоге релизов (default: 3 прошлых + текущая)
#
# Машинно-специфичные файлы в PUBLISH_DIR (appsettings.Local.json, .mcp.json, logs/, data/)
# не трогаются. Каталог релизов агента лежит ВНЕ PUBLISH_DIR — rsync --delete его не трогает.
# Ошибка сборки агента не валит выкатку сервера: предупреждение в лог, в STAGING копируется
# указатель прошлой выкатки (или последней валидной версии из AGENT_RELEASES).
#
# Прод-выкатку сам запускать НЕ надо: скрипт в проде зовёт трей-раннер (Services/Deploy).
set -Euo pipefail
shopt -s nullglob
# set -e сознательно НЕ включаем: ошибки сборки агента не должны ронять выкатку сервера.

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-${1:-/opt/ccs/app}}"
STAGING="${PUBLISH_DIR%/}.staging"
AGENT_RELEASES="${AGENT_RELEASES:-$(dirname "$PUBLISH_DIR")/agent-releases}"
AGENT_PROJECT="${AGENT_PROJECT:-$REPO/backend/ClaudeHomeServer.DeviceAgent/ClaudeHomeServer.DeviceAgent.csproj}"
AGENT_BUILD="${TMPDIR:-/tmp}/ccs-agent-build.$$-$RANDOM"
AGENT_KEEP="${AGENT_KEEP:-4}"

# RID → расширение архива. Шаг задачи AD-2.
AGENT_RIDS=(win-x64 linux-x64)

log()  { printf '%s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

# Версия выкатки (Р1). Чистое дерево → 1.N.0, грязное → 1.N.0-dirty.<TS>. Хвост +sha8
# едет только в InformationalVersion: в манифесте и в пути каталога версия каноническая,
# иначе AgentReleaseCatalog сочтёт указатель повреждённым.
agent_version() {
  local rev ts
  rev=$(git -C "$REPO" rev-list --count HEAD)
  if [[ -n "$(git -C "$REPO" status --porcelain)" ]]; then
    ts=$(date -u +%Y%m%d%H%M%S)
    printf '1.%s.0-dirty.%s' "$rev" "$ts"
  else
    printf '1.%s.0' "$rev"
  fi
}

# Публикация сервера: фронт, бэкенд, pty-bridge, mcp-dify, build-id. Точно как раньше.
publish_server() {
  if [[ "${BUILD_FRONTEND:-0}" == 1 ]]; then (cd "$REPO/frontend" && npm ci --no-audit --no-fund && npm run build:quiet); fi
  if [[ "${BUILD_DIFY:-0}" == 1 ]]; then (cd "$REPO/mcp-dify" && npm ci --no-audit --no-fund && npm run build); fi

  [[ -f "$REPO/frontend/dist/index.html" ]] || fail "фронт не собран: нет frontend/dist/index.html"

  rm -rf "$STAGING"
  mkdir -p "$STAGING"

  log "== publish-backend"
  # RID обязателен: без него нативка SkiaSharp едет под все платформы (~0,4 ГБ)
  dotnet publish "$REPO/backend/ClaudeHomeServer/ClaudeHomeServer.csproj" -c Release -r linux-x64 --self-contained false -o "$STAGING" --nologo -v quiet
  # Динамические модули ModuleLoader резолвит по пути — без них старт падает (см. deploy-agent.ps1).
  for mod in notes/ClaudeHomeServer.Notes.dll spend/ClaudeHomeServer.Spend.dll image-editor/ClaudeHomeServer.ImageEditor.dll; do
    [[ -f "$STAGING/modules/$mod" ]] || fail "нет modules/$mod после publish"
  done

  log "== pty-bridge"
  gcc -O2 -static -o "$STAGING/pty-bridge" "$REPO/backend/pty-bridge/pty-bridge.c" -lutil

  log "== frontend -> wwwroot"
  rsync -a --delete "$REPO/frontend/dist/" "$STAGING/wwwroot/"

  log "== mcp-dify"
  if [[ -f "$REPO/mcp-dify/dist/index.js" ]]; then
    mkdir -p "$STAGING/mcp-dify"
    rsync -a --delete "$REPO/mcp-dify/dist/" "$STAGING/mcp-dify/dist/"
    rsync -a --delete "$REPO/mcp-dify/node_modules/" "$STAGING/mcp-dify/node_modules/"
    cp "$REPO/mcp-dify/package.json" "$STAGING/mcp-dify/"
  else
    log "  пропущено: нет mcp-dify/dist/index.js"
  fi

  log "== build-id"
  sha="$(git -C "$REPO" rev-parse --short=8 HEAD)"
  ref="$(git -C "$REPO" rev-parse --abbrev-ref HEAD)"
  dirty=False; [[ -n "$(git -C "$REPO" status --porcelain)" ]] && dirty=True
  printf '%s\nsha=%s\nref=%s\ndirty=%s\nbuiltAt=%s\n' \
    "$(date +%Y%m%d-%H%M%S)" "$sha" "$ref" "$dirty" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$STAGING/build-id.txt"
}

# Переключение rsync → PUBLISH_DIR. Машинно-специфичные файлы и логи не трогаем.
switch_server() {
  log "== switch -> $PUBLISH_DIR"
  mkdir -p "$PUBLISH_DIR"
  rsync -a --delete \
    --exclude 'appsettings.Local.json' --exclude 'appsettings.Local.json.*' --exclude '.mcp.json' \
    --exclude '/logs/' --exclude '/data/' \
    "$STAGING/" "$PUBLISH_DIR/"
  rm -rf "$STAGING"
  log "готово: $(head -2 "$PUBLISH_DIR/build-id.txt" | tr '\n' ' ')"
}

# Публикация агента под один RID. На выходе — путь к архиву или пустая строка при ошибке.
publish_one_agent() {
  local rid="$1" version="$2" sha8="$3"
  local out_dir="$AGENT_BUILD/$rid"
  local publish_props=( -p:Version="$version" -p:InformationalVersion="${version}+${sha8}" )

  rm -rf "$out_dir"

  if ! (cd "$REPO" && dotnet publish "$AGENT_PROJECT" -c Release --nologo -v quiet \
        -r "$rid" --self-contained -o "$out_dir" "${publish_props[@]}"); then
    return 1
  fi

  case "$rid" in
    win-x64)
      [[ -f "$out_dir/ai-home-agent.exe" ]] || return 1
      # Без моста ConPTY терминал агента молча уходит в упрощённый фолбэк (без цветов и
      # Clear-Host) у всех клиентов — такой выпуск не выпускаем
      local f
      for f in ConPtyBridge.exe ConPtyBridge.dll; do
        [[ -f "$out_dir/$f" ]] || { log "нет $f в publish агента win-x64" >&2; return 1; }
      done
      ;;
    linux-x64)
      [[ -f "$out_dir/ai-home-agent" ]] || return 1
      ;;
    *) return 1 ;;
  esac
}

# Упаковка выхода publish в архив. tar.gz для linux-x64 сохраняет права; для надёжности
# ставим +x на apphost и .so до архивации.
pack_one_agent() {
  local rid="$1" version="$2"
  local out_dir="$AGENT_BUILD/$rid" archive_name archive_path
  case "$rid" in
    win-x64)   archive_name="agent-${version}-${rid}.zip" ;;
    linux-x64) archive_name="agent-${version}-${rid}.tar.gz" ;;
    *) fail "неизвестный RID: $rid" ;;
  esac
  archive_path="$AGENT_RELEASES/$version/$archive_name"

  case "$rid" in
    win-x64)
      (cd "$out_dir" && zip -qr "$archive_path" .) || return 1
      ;;
    linux-x64)
      # dotnet apphost уже +x; .so в linux-саморасхвате прибавят +x по факту, но не помешает.
      find "$out_dir" -type f \( -name '*.so' -o -name '*.so.*' -o -name 'ai-home-agent' \) -exec chmod +x {} + 2>/dev/null || true
      chmod +x "$out_dir/ai-home-agent" 2>/dev/null || true
      [[ -f "$out_dir/pty-bridge" ]] && chmod +x "$out_dir/pty-bridge"
      (cd "$out_dir" && tar -czf "$archive_path" .) || return 1
      ;;
  esac
  printf '%s\n' "$archive_path"
}

# Запись JSON манифеста (он же указатель) для текущей версии.
write_pointer() {
  local version="$1" archive_win="$2" sha_win="$3" size_win="$4" \
                    archive_lin="$5" sha_lin="$6" size_lin="$7"
  local out_json

  out_json=$(jq -n \
    --arg version "$version" \
    --arg win_file "$archive_win" --argjson win_size "$size_win" --arg win_sha "$sha_win" \
    --arg lin_file "$archive_lin" --argjson lin_size "$size_lin" --arg lin_sha "$sha_lin" \
    '{
      version: $version,
      archives: {
        "win-x64":   { file: $win_file, size: $win_size,   sha256: $win_sha },
        "linux-x64": { file: $lin_file, size: $lin_size,   sha256: $lin_sha }
      }
    }')

  printf '%s\n' "$out_json" > "$AGENT_RELEASES/$version/manifest.json"
  cp "$AGENT_RELEASES/$version/manifest.json" "$STAGING/agent-release.json"
}

# Выпуск релиза агента целиком (win-x64 + linux-x64 → manifest.json → указатель в staging).
publish_agent_release() {
  local version="$1" sha8="$2"
  local manifest_dir="$AGENT_RELEASES/$version"
  local fail_rids=()
  local win_path="" lin_path=""
  local win_sha="" lin_sha="" win_size="" lin_size=""
  local win_file="" lin_file=""

  mkdir -p "$manifest_dir" "$STAGING"

  for rid in "${AGENT_RIDS[@]}"; do
    log "== agent-publish $rid"
    if ! publish_one_agent "$rid" "$version" "$sha8"; then
      warn "сборка агента под $rid провалилась — пропускаю"
      fail_rids+=("$rid")
      continue
    fi

    log "== agent-pack $rid"
    local archive_path
    if ! archive_path=$(pack_one_agent "$rid" "$version"); then
      warn "упаковка агента под $rid не удалась"
      fail_rids+=("$rid")
      continue
    fi

    local hash size
    hash=$(sha256sum "$archive_path" | awk '{print $1}')
    size=$(stat -c '%s' "$archive_path")

    case "$rid" in
      win-x64)   win_path="$archive_path"; win_sha="$hash"; win_size="$size"; win_file=$(basename "$archive_path") ;;
      linux-x64) lin_path="$archive_path"; lin_sha="$hash"; lin_size="$size"; lin_file=$(basename "$archive_path") ;;
    esac
  done

  # Если оба RID упали — каталог для текущей версии не нужен, удаляем и копируем указатель прошлой.
  if [[ -z "$win_path" && -z "$lin_path" ]]; then
    rmdir "$manifest_dir" 2>/dev/null && log "  - пустой каталог $version удалён"
    warn "обе сборки агента ($version) провалились — указатель прошлой выкатки"
    fall_back_pointer "$version"
    return 0
  fi

  # Если один RID упал — записываем манифест с тем, что есть (Частично — лучше, чем ничего).
  # Решение Р3 про отказ не пишет, но и не должно случиться: частичная выкатка всё равно
  # полезна, пока указатель прошлой версии не затрёт её.
  write_pointer "$version" \
    "${win_file:-}" "${win_sha:-}" "${win_size:-0}" \
    "${lin_file:-}" "${lin_sha:-}" "${lin_size:-0}"

  if (( ${#fail_rids[@]} > 0 )); then
    warn "релиз $version неполный: ${fail_rids[*]} упали; манифест записан частично"
  else
    log "== agent-release готово: $version"
  fi

  rotate_releases "$version"
}

# Указатель прошлой валидной версии (для случая «обе сборки упали»).
fall_back_pointer() {
  local cur_ver="$1"
  local prev_dir
  prev_dir=$(find "$AGENT_RELEASES" -mindepth 2 -maxdepth 2 -name manifest.json \
            -not -path "*/$cur_ver/*" -printf '%h\n' 2>/dev/null \
            | sort -r | head -n 1)

  if [[ -z "$prev_dir" || ! -f "$prev_dir/manifest.json" ]]; then
    # В рабочем режиме может быть ещё указатель в PUBLISH_DIR с прошлой выкатки.
    if [[ -f "$PUBLISH_DIR/agent-release.json" ]]; then
      cp "$PUBLISH_DIR/agent-release.json" "$STAGING/agent-release.json"
      warn "использован указатель $PUBLISH_DIR/agent-release.json (агент не раздаётся новой версии)"
    else
      warn "прошлой валидной версии не нашлось — без указателя"
    fi
    return 0
  fi

  cp "$prev_dir/manifest.json" "$STAGING/agent-release.json"
  warn "использован указатель прошлой версии: $(basename "$prev_dir")"
}

# Ротация: оставляем AGENT_KEEP (default 4) каталогов с manifest.json, остальные удаляем.
rotate_releases() {
  local cur_ver="$1"
  local versions=()
  local i entry
  shopt -s nullglob
  for entry in "$AGENT_RELEASES"/*/; do
    [[ -f "$entry/manifest.json" ]] && versions+=("${entry%/}")
  done
  shopt -u nullglob
  (( ${#versions[@]} <= AGENT_KEEP )) && return 0

  # Обратный сорт по версиям (sort -V): новые коммиты дают больший N, а лексикографический
  # сорт поставил бы 1.999.0 выше 1.1000.0.
  IFS=$'\n' read -r -d '' -a sorted < <(printf '%s\n' "${versions[@]}" | sort -rV && printf '\0')
  unset IFS

  # Первые AGENT_KEEP (свежайшие) оставляем, остальные удаляем.
  for ((i = AGENT_KEEP; i < ${#sorted[@]}; i++)); do
    local dir="${sorted[$i]}"
    rm -rf "$dir"
    log "  - rotate: удалён старый релиз $(basename "$dir")"
  done
}

main() {
  if [[ ! -d "$AGENT_RELEASES" ]]; then
    mkdir -p "$AGENT_RELEASES"
  fi

  # trap на EXIT — ловит любой ранний выход: SKIP_AGENT=1 после publish_server (staging
  # создан, переключения не было), или set -E-ловушку из bash-функции. После успешного
  # switch_server и AGENT_BUILD уже удалены — `rm -rf` тихо отработает по несуществующим.
  trap 'rm -rf "$AGENT_BUILD" "$STAGING"' EXIT

  log "publish-linux.sh: PUBLISH_DIR=$PUBLISH_DIR AGENT_RELEASES=$AGENT_RELEASES"

  # Режим AGENT_ONLY: пропускаем сборку/выкатку сервера — только релиз агента.
  if [[ "${AGENT_ONLY:-0}" != 1 ]]; then
    publish_server
  else
    log "AGENT_ONLY=1: сервер пропущен"
    rm -rf "$STAGING"
    mkdir -p "$STAGING"
  fi

  # Агент: либо целиком, либо выходим.
  if [[ "${SKIP_AGENT:-0}" == 1 ]]; then
    log "SKIP_AGENT=1: релиз агента пропущен"
    exit 0
  fi

  local version sha8
  sha8=$(git -C "$REPO" rev-parse --short=8 HEAD)
  version=$(agent_version)
  log "agent-release: version=$version"

  mkdir -p "$AGENT_BUILD"

  publish_agent_release "$version" "$sha8"

  # Только теперь переключаем staging → PUBLISH_DIR. Так agent-release.json, лежащий в staging,
  # уезжает в PUBLISH_DIR вместе со всем сервером.
  if [[ "${AGENT_ONLY:-0}" != 1 ]]; then
    switch_server
  else
    # Сервер не переключается, staging снесёт ловушка EXIT — указатель кладём прямо в
    # PUBLISH_DIR: его подхватит стенд с этим каталогом содержимого или DeviceAgent:ReleasePointerPath.
    mkdir -p "$PUBLISH_DIR"
    cp "$STAGING/agent-release.json" "$PUBLISH_DIR/agent-release.json"
    log "указатель: $PUBLISH_DIR/agent-release.json"
  fi
}

main "$@"
