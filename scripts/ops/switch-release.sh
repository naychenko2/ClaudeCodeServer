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
#
# Расширения для выкатки из чата (ADR-010, карточка хода):
#   - CCS_DEPLOY_JOURNAL=<путь к deploy-journal.py>
#     CCS_DEPLOY_STATE_PATH=<путь к deploy-state.json>
#     включают журналирование: функции step / journal_phase пишут фазы и шаги.
#     Без них скрипт ведёт себя как раньше (ручной запуск).
#   - APP / STAGING / RELEASES — переопределение жёстких путей для прогона
#     на временном каталоге.
#   - CCS_SYSTEMCTL — переопределение команды systemctl (для прогона с подставным
#     бинарником в тестах).
#   - HEALTH_URL — адрес health-гейта (по умолчанию http://127.0.0.1/api/health).
#   - HEALTH_TRIES / HEALTH_SLEEP — параметры health-гейта (по умолчанию 18×5 с).
#
# Контракт с оркестратором deploy-linux.sh: скрипт НЕ закрывает result журнала
# сам — он только пишет шаги и переключает фазу (switching → verifying). Финальный
# status (succeeded/rolled_back/failed) по коду выхода ставит оркестратор.
#
# Коды выхода:
#   0 — health прошёл, прод переключён
#   1 — health провален, автооткат выполнен
#   2 — staging неполный, прод не тронут
#   3 — не удалось сохранить текущий билд, прод не тронут
#   4 — flock на deploy.lock не получен (другая выкатка идёт)
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
# Ошибки удаления не фатальны: выкатка к этому моменту уже удалась. Поэтому оба вызова —
# в подоболочке: ошибка раскрытия внутри прервала бы составную команду вызывающего вместе
# с её exit 0, и удачная выкатка ушла бы в ветку отката (или --rotate — в самоотвязку).
rotate_releases() {
  local dir=$1 keep=$2 dry=$3 protect=${4:-}
  case "$keep" in ''|*[!0-9]*) keep=5 ;; esac
  keep=$((10#$keep))   # «08» иначе читается как восьмеричное и роняет $((…)) ошибкой раскрытия
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
  ( rotate_releases "${CCS_RELEASES_DIR:-/opt/ccs/releases}" "${CCS_RELEASES_KEEP:-$RELEASES_KEEP}" "$dry" )
  exit 0
fi

# Самоотвязка в отдельный юнит. setsid/nohup НЕ спасают: они меняют сессию, но не
# cgroup, а бэкенд при остановке гасит scope агентов в ccs-agents.slice целиком —
# скрипт умирал сразу после stop, прод оставался лежать (2026-09-28 08:59, лог 085902).
# Поэтому любой запуск (setsid, nohup, напрямую) сам перезапускается через systemd-run
# и сразу возвращает управление; прогресс — в /opt/ccs/deploy-*.log.
#
# Имя юнита уникально по pid родительского bash (`$$`): два ручных запуска подряд
# никогда не подерут один юнит. Метка времени в STAMP ниже читается человеком, но
# защитой от коллизий она не работает — два запуска в одну секунду дали бы один юнит.
if [ -z "${CCS_SWITCH_DETACHED:-}" ]; then
  unit="ccs-switch-$$"
  echo "switch-release: перезапускаюсь отдельным юнитом $unit (journalctl --user -u $unit, лог — /opt/ccs/deploy-*.log)"
  exec systemd-run --user --quiet --collect --unit="$unit" \
    --setenv=CCS_SWITCH_DETACHED=1 "$(readlink -f "$0")" "$@"
fi

# Журналирование фазы/шага. Без CCS_DEPLOY_JOURNAL — no-op (ручной запуск).
# python3 -I — граница привилегий: через env shebang не пробрасывает -I на Linux,
# поэтому идём через явный интерпретатор.
journal() {
  if [ -n "${CCS_DEPLOY_JOURNAL:-}" ] && [ -n "${CCS_DEPLOY_STATE_PATH:-}" ]; then
    python3 -I "$CCS_DEPLOY_JOURNAL" "$CCS_DEPLOY_STATE_PATH" "$@" || true
  fi
}
journal_phase() {
  journal set-phase "$1"
}
# step <name> <status>. Тот же name переписывается, а не плодит дубль — удобно,
# если шаг временно выставил промежуточный статус (например, stop ok, потом health fail).
# Длительность шага (мс) — время с прошлой отметки __mark, её же и сдвигаем: шаги
# идут встык, поэтому каждому достаётся своя доля без пересечений.
__mark=$(( $(date +%s%N) / 1000000 ))
step() {
  local now
  now=$(( $(date +%s%N) / 1000000 ))
  journal set-step "$1" "$2" "$(( now - __mark ))"
  __mark=$now
}

# Конфигурация путей и команд — через env для прогона на временном каталоге.
APP="${APP:-/opt/ccs/app}"
STAGING="${STAGING:-/opt/ccs/staging}"
RELEASES="${RELEASES:-/opt/ccs/releases}"
SYSTEMCTL_CMD="${CCS_SYSTEMCTL:-systemctl --user}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1/api/health}"
HEALTH_HOST="${HEALTH_HOST:-localhost}"
HEALTH_TRIES="${HEALTH_TRIES:-18}"          # 18 × 5 c = 90 секунд, как в ADR-010
HEALTH_SLEEP="${HEALTH_SLEEP:-5}"
STAMP="${STAMP:-$(date +%Y%m%d-%H%M%S)}"
PREV="$RELEASES/$STAMP"
LOG="${LOG:-/opt/ccs/deploy-$STAMP.log}"

# Лок на тот же файл, что держит deploy-linux.sh. Ручной запуск не должен пересечься
# с выкаткой из чата: тогда либо этот скрипт не возьмёт лок (exit 4), либо оркестратор
# дождётся завершения ручного прогона через свой flock.
#
# При вызове из deploy-linux.sh родитель уже держит flock — иначе тот же OFD дважды
# залочить нельзя (flock реентерабелен через fork, но второй flock на том же fd после
# exec 9>file получает EAGAIN: bash считает redirect на тот же файл noop'ом). В этом
# случае CCS_DEPLOY_LOCK_FD приходит через env и flock не переоткрывается.
if [ -n "${CCS_DEPLOY_LOCK_FD:-}" ]; then
  # Родитель (deploy-linux.sh) уже держит flock — нам остаётся только пользоваться тем же
  # OFD. Здесь дополнительной проверки не делаем: родитель при выходе снимет блокировку.
  :
else
  LOCK="${RELEASES}/deploy.lock"
  exec 9>"$LOCK"
  if ! flock -x -n 9; then
    # Лог ещё не настроен — пишем в stderr, оркестратор увидит в своём журнале.
    echo "switch-release: deploy.lock занят — выкатка уже идёт, выход" >&2
    exit 4
  fi
fi

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

journal_phase switching

mkdir -p "$RELEASES"
__mark=$(( $(date +%s%N) / 1000000 ))   # отсчёт длительности шагов идёт от начала снимка, не от проверок выше
log "сохраняю текущий билд целиком в $PREV"
cp -a "$APP" "$PREV" || { log "ОТКАЗ: не удалось сохранить текущий билд"; exit 3; }
log "сохранено: $(du -sh "$PREV" | cut -f1)"
step snapshot ok

log "останавливаю ccs.service (здесь умирает вызывающий агент)"
$SYSTEMCTL_CMD stop ccs.service
sleep 2
step stop ok

# wwwroot чистим отдельно: старые ассеты с хешами в именах иначе копятся вечно,
# а index.html ссылается только на новые.
log "обновляю wwwroot"
rm -rf "$APP/wwwroot"
cp -a "$STAGING/wwwroot" "$APP/wwwroot"

log "копирую файлы сборки поверх (конфиги и данные не трогаю)"
cp -a "$STAGING"/. "$APP"/
step swap ok

log "поднимаю ccs.service"
$SYSTEMCTL_CMD start ccs.service
step start ok

# Health-гейт: юнит может подняться, а приложение упасть на старте —
# поэтому спрашиваем именно HTTP, а не systemctl is-active.
journal_phase verifying

ok=0
for i in $(seq 1 $HEALTH_TRIES); do
  sleep $HEALTH_SLEEP
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 -H "Host: $HEALTH_HOST" "$HEALTH_URL" || echo 000)
  log "health $i/$HEALTH_TRIES: $code"
  # 204 отдаёт /api/health, 401 — любая ручка под [Authorize]: живость доказывают оба
  if [ "$code" = "200" ] || [ "$code" = "204" ] || [ "$code" = "401" ]; then ok=1; break; fi
done

if [ "$ok" = "1" ]; then
  step health ok
  # build-id.txt НЕ перезаписываем: он приезжает из publish-linux.sh вместе с sha/ref/builtAt,
  # а голая метка времени стирала привязку выкатки к коммиту (прод 2026-09-22)
  log "=== ГОТОВО: $(head -2 "$APP/build-id.txt" | tr '\n' ' ') отвечает. Предыдущая — $PREV ==="
  # Только после удачного health: при откате (ветка ниже) ничего не удаляем.
  # Свежий снимок $PREV — точка отката этой выкатки — защищён явно.
  ( rotate_releases "$RELEASES" "$RELEASES_KEEP" 0 "$STAMP" )
  exit 0
fi

step health fail
# Сигнализируем оркестратору, на какой снимок вернулись: deploy-linux.sh при exit 1
# прочитает state.rollbackTarget и передаст его в finalize как result.releaseId.
journal set-rollback-target "$STAMP"
log "!!! health не прошёл за ${HEALTH_TRIES}×${HEALTH_SLEEP} с — возвращаю предыдущую сборку"
$SYSTEMCTL_CMD stop ccs.service
sleep 2
rm -rf "$APP"
cp -a "$PREV" "$APP"
$SYSTEMCTL_CMD start ccs.service
sleep $HEALTH_SLEEP
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 "$HEALTH_URL" || echo 000)
log "=== ОТКАЧЕНО на $STAMP, health после отката: $code ==="
exit 1