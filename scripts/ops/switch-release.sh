#!/usr/bin/env bash
# Переключение прода на собранный staging с автоматическим откатом.
#
# Работает ОТДЕЛЬНЫМ user-юнитом (самоотвязка ниже): агент, который его
# инициирует, живёт в scope ccs-agents.slice и умирает на шаге остановки —
# а откатывать при провале после этого было бы некому.
#
# Схема по мотивам ADR-010: сохранить текущий билд → обновить → поднять →
# health-гейт → при провале вернуть предыдущий и поднять снова.
#
# ВАЖНО: файлы сборки копируются ПОВЕРХ, папка приложения НЕ пересоздаётся.
# Внутри неё живёт то, чего нет в staging и что потерять нельзя:
# appsettings.Local.json (там DataPath и ключи), backups-secrets, .mcp.json,
# mcp-dify, pty-bridge. Полная подмена каталога унесла бы их вместе со сборкой.
set -uo pipefail

# Сколько последних снимков держать в /opt/ccs/releases: снимок ≈ 170 МБ, диск
# прода небольшой, поэтому ротация идёт после каждой удачной выкатки.
# Менять число — правкой этой строки: в detached-режиме окружение вызывающего
# в юнит не попадает.
RELEASES_KEEP=5

# Ротация снимков: оставить $keep самых свежих, остальные удалить.
# Порядок — по ИМЕНИ каталога (метка YYYYMMDD-HHMMSS), а не по mtime: cp -a переносит
# mtime папки приложения, и дата каталога снимка не равна моменту снимка.
# Трогает только каталоги со строгим именем-меткой прямо внутри $dir (симлинки и всё
# прочее — мимо); $protect — имя, которое не удаляется ни при каких $keep.
# Ошибки удаления не фатальны: выкатка к этому моменту уже удалась.
rotate_releases() {
  local dir=$1 keep=$2 dry=$3 protect=${4:-}
  case "$keep" in ''|*[!0-9]*) keep=5 ;; esac
  [ "$keep" -ge 1 ] || keep=5   # ноль унёс бы и свежий снимок — откатываться стало бы не на что
  local names=() n i total cut
  while IFS= read -r n; do names+=("$n"); done < <(
    find "$dir" -mindepth 1 -maxdepth 1 -type d -regextype posix-extended \
      -regex '.*/[0-9]{8}-[0-9]{6}' -printf '%f\n' 2>/dev/null | sort)
  total=${#names[@]}
  cut=$((total - keep))
  if [ "$cut" -le 0 ]; then
    log "ротация: снимков $total, держим $keep — удалять нечего"
    return 0
  fi
  log "ротация: снимков $total, держим $keep, к удалению $cut"
  for ((i = 0; i < cut; i++)); do
    n=${names[$i]}
    if [ "$n" = "$protect" ]; then
      log "ротация: $n защищён — пропускаю"
      continue
    fi
    if [ "$dry" = "1" ]; then
      log "ротация (сухой прогон): удалил бы $n ($(du -sh "$dir/$n" 2>/dev/null | cut -f1))"
    elif rm -rf -- "${dir:?}/$n"; then
      log "ротация: удалён $n"
    else
      log "ротация: не удалось удалить $n"
    fi
  done
  return 0
}

# Разовый запуск ротации без выкатки: switch-release.sh --rotate [--dry-run].
# Каталог — CCS_RELEASES_DIR (для проверок на временном каталоге), по умолчанию боевой.
if [ "${1:-}" = "--rotate" ]; then
  log() { echo "$*"; }
  dry=0; [ "${2:-}" = "--dry-run" ] && dry=1
  rotate_releases "${CCS_RELEASES_DIR:-/opt/ccs/releases}" "${CCS_RELEASES_KEEP:-$RELEASES_KEEP}" "$dry"
  exit 0
fi

# Самоотвязка в отдельный юнит. setsid/nohup НЕ спасают: они меняют сессию, но не
# cgroup, а бэкенд при остановке гасит scope агентов в ccs-agents.slice целиком —
# скрипт умирал сразу после stop, прод оставался лежать (2026-09-28 08:59, лог 085902).
# Поэтому любой запуск (setsid, nohup, напрямую) сам перезапускается через systemd-run
# и сразу возвращает управление; прогресс — в /opt/ccs/deploy-*.log.
if [ -z "${CCS_SWITCH_DETACHED:-}" ]; then
  unit="ccs-switch-$(date +%Y%m%d-%H%M%S)"
  echo "switch-release: перезапускаюсь отдельным юнитом $unit (journalctl --user -u $unit, лог — /opt/ccs/deploy-*.log)"
  exec systemd-run --user --quiet --collect --unit="$unit" \
    --setenv=CCS_SWITCH_DETACHED=1 "$(readlink -f "$0")" "$@"
fi

APP=/opt/ccs/app
STAGING=/opt/ccs/staging
RELEASES=/opt/ccs/releases
STAMP=$(date +%Y%m%d-%H%M%S)
PREV="$RELEASES/$STAMP"
LOG=/opt/ccs/deploy-$STAMP.log
# Прод слушает 80/443 (Kestrel:Endpoints), а НЕ 5000, и требует разрешённый Host
# (AllowedHosts). Пути /api/auth/ping не существует вовсе — старая проба давала 000
# на живом сервере и откатила УДАВШУЮСЯ выкатку (прод 2026-09-22, лог 162323).
HEALTH_URL=http://127.0.0.1/api/health
HEALTH_HOST=localhost
HEALTH_TRIES=18          # 18 × 5 c = 90 секунд, как в ADR-010
HEALTH_SLEEP=5

log() { echo "[$(date +%H:%M:%S)] $*" >> "$LOG"; }

log "=== выкатка $STAMP ==="

# Гейт ДО всякой остановки: неполный staging — не выкатываем вовсе.
# Грабля ADR-014: dotnet publish -o молча терял dll динамических модулей,
# и прод падал на старте. Проверяем их наличие явно.
for must in ClaudeHomeServer.dll wwwroot modules modules/notes modules/spend; do
  if [ ! -e "$STAGING/$must" ]; then
    log "ОТКАЗ: в staging нет $must — прод не трогаем"
    exit 2
  fi
done
log "staging проверен: dll, wwwroot, modules/notes, modules/spend на месте"

mkdir -p "$RELEASES"
log "сохраняю текущий билд целиком в $PREV"
cp -a "$APP" "$PREV" || { log "ОТКАЗ: не удалось сохранить текущий билд"; exit 3; }
log "сохранено: $(du -sh "$PREV" | cut -f1)"

log "останавливаю ccs.service (здесь умирает вызывающий агент)"
systemctl --user stop ccs.service
sleep 2

# wwwroot чистим отдельно: старые ассеты с хешами в именах иначе копятся вечно,
# а index.html ссылается только на новые.
log "обновляю wwwroot"
rm -rf "$APP/wwwroot"
cp -a "$STAGING/wwwroot" "$APP/wwwroot"

log "копирую файлы сборки поверх (конфиги и данные не трогаю)"
cp -a "$STAGING"/. "$APP"/

log "поднимаю ccs.service"
systemctl --user start ccs.service

# Health-гейт: юнит может подняться, а приложение упасть на старте —
# поэтому спрашиваем именно HTTP, а не systemctl is-active.
ok=0
for i in $(seq 1 $HEALTH_TRIES); do
  sleep $HEALTH_SLEEP
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 -H "Host: $HEALTH_HOST" "$HEALTH_URL" || echo 000)
  log "health $i/$HEALTH_TRIES: $code"
  # 204 отдаёт /api/health, 401 — любая ручка под [Authorize]: живость доказывают оба
  if [ "$code" = "200" ] || [ "$code" = "204" ] || [ "$code" = "401" ]; then ok=1; break; fi
done

if [ "$ok" = "1" ]; then
  # build-id.txt НЕ перезаписываем: он приезжает из publish-linux.sh вместе с sha/ref/builtAt,
  # а голая метка времени стирала привязку выкатки к коммиту (прод 2026-09-22)
  log "=== ГОТОВО: $(head -2 "$APP/build-id.txt" | tr '\n' ' ') отвечает. Предыдущая — $PREV ==="
  # Только после удачного health: при откате (ветка ниже) ничего не удаляем.
  # Свежий снимок $PREV — точка отката этой выкатки — защищён явно.
  rotate_releases "$RELEASES" "$RELEASES_KEEP" 0 "$STAMP"
  exit 0
fi

log "!!! health не прошёл за 90 с — возвращаю предыдущую сборку"
systemctl --user stop ccs.service
sleep 2
rm -rf "$APP"
cp -a "$PREV" "$APP"
systemctl --user start ccs.service
sleep $HEALTH_SLEEP
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 "$HEALTH_URL" || echo 000)
log "=== ОТКАЧЕНО на $STAMP, health после отката: $code ==="
exit 1
