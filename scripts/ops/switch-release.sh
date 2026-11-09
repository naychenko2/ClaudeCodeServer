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
# Машинные файлы (appsettings.Local.json*, .mcp.json, backups-secrets, logs/, data/)
# из staging НЕ копируются даже если лежат там: устаревшая копия в staging уже
# затёрла боевой конфиг (прод 2026-10-14). Список исключений — как в publish-linux.sh.
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
      # Снимок ушёл с диска — уберём и его из списка releases, иначе deploy_status
      # предложит откат на несуществующий каталог. declare -F: в ручном --rotate
      # функция journal ещё не объявлена (блок --rotate раньше её определения) — no-op.
      if declare -F journal >/dev/null; then journal drop-release "$n"; fi
    else
      log "ротация: не удалось удалить $n"
    fi
  done
  return 0
}

# Копирование сборки поверх приложения БЕЗ --delete и без машинных файлов app.
# Исключения те же, что в publish-linux.sh (switch_server), плюс backups-secrets:
# файлы, которые принадлежат машине, а не сборке, из staging не берём никогда.
# Якорь «/» — только верхний уровень каталога; appsettings.Local.json* и .mcp.json
# без якоря, как в publish-linux.sh. --ignore-times: прежний cp -a перезаписывал всегда,
# а быстрая проверка rsync (размер + mtime) пропустила бы файл одинакового размера и секунды.
copy_build_over() {
  local src=$1 dst=$2
  rsync -a --ignore-times \
    --exclude 'appsettings.Local.json' --exclude 'appsettings.Local.json.*' --exclude '.mcp.json' \
    --exclude '/backups-secrets' --exclude '/logs/' --exclude '/data/' \
    "$src"/ "$dst"/
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

# Имя контейнера песочницы — из прод-конфига (Sandbox:ContainerName, дефолт cc-sandbox).
# На одной машине живут dev (cc-sandbox) и prod (cc-sandbox-prod): имя берём из
# appsettings.Local.json прода, иначе снесём чужой контейнер. Конфиг не читается —
# дефолт. python3 -I — та же граница привилегий, что у journal().
sandbox_container_name() {
  local cfg="$APP/appsettings.Local.json"
  [ -f "$cfg" ] || { printf 'cc-sandbox'; return; }
  local name
  name=$(python3 -I -c '
import json, sys
try:
    d = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(0)
n = (d.get("Sandbox") or {}).get("ContainerName")
print(n if isinstance(n, str) and n else "")
' "$cfg" 2>/dev/null)
  printf '%s' "${name:-cc-sandbox}"
}

# Снести контейнер песочницы: docker rm -f. Терпим «контейнера нет» (свежий инстанс)
# и отсутствие docker (машин без песочницы): в обоих случаях сервер поднимет контейнер
# сам (SandboxManager.EnsureRunningAsync) при первом container-ходе.
remove_sandbox_container() {
  local name="$1"
  if ! command -v docker >/dev/null 2>&1; then
    log "песочница: docker не найден — контейнер $name не трогаю (поднимет сервер)"
    return 0
  fi
  docker rm -f "$name" >/dev/null 2>&1 || log "песочница: контейнер $name не найден (свежий инстанс) — ок"
  return 0
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
# Прод слушает 80/443 (Kestrel:Endpoints), а НЕ 5000, и требует разрешённый Host
# (AllowedHosts). Пути /api/auth/ping не существует вовсе — старая проба давала 000
# на живом сервере и откатила УДАВШУЮСЯ выкатку (прод 2026-09-22, лог 162323).
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1/api/health}"
HEALTH_HOST="${HEALTH_HOST:-localhost}"
HEALTH_TRIES="${HEALTH_TRIES:-18}"          # 18 × 5 c = 90 секунд, как в ADR-010
HEALTH_SLEEP="${HEALTH_SLEEP:-5}"
STAMP="${STAMP:-$(date +%Y%m%d-%H%M%S)}"
PREV="$RELEASES/$STAMP"
LOG="${LOG:-/opt/ccs/deploy-$STAMP.log}"
# Бэкап данных перед переключением (шаг data-backup): бинарник ТЕКУЩЕГО прода из $APP
# снимает data/ при живом сервере (json-сторы атомарны, SQLite — online-backup API).
# Таймаут — секунды, дефолт 2400 (как -BackupTimeoutSec Windows-агента; 26.08 занял 834 с,
# объём растёт). Провал = отказ без изменений прода (код 3, как «не удалось сохранить билд»).
BACKUP_TIMEOUT="${BACKUP_TIMEOUT:-2400}"
# Песочница: имя контейнера из прод-конфига (Sandbox:ContainerName); на проде cc-sandbox-prod,
# дев-контейнер cc-sandbox не трогаем. SKIP_SANDBOX=1 — заявка не пересобирает/не пересоздаёт.
SANDBOX_CONTAINER="$(sandbox_container_name)"
SKIP_SANDBOX="${SKIP_SANDBOX:-0}"

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

# Без rsync копирование сборки невозможно, а выяснять это после остановки сервиса поздно.
command -v rsync >/dev/null || { log "ОТКАЗ: rsync не установлен — прод не трогаем"; exit 1; }

journal_phase switching

mkdir -p "$RELEASES"
__mark=$(( $(date +%s%N) / 1000000 ))   # отсчёт длительности шагов идёт от начала снимка, не от проверок выше

# Бэкап данных ДО остановки: при живом сервере это безопасно (json-сторы атомарны,
# SQLite снимается online-backup API). Провал снимка останавливает выкатку: молча
# отказавшая страховка хуже отсутствующей (как Windows-агент). Бинарник ТЕКУЩЕГО прода
# из $APP с его appsettings.Local.json; окружение Production — как у ccs.service.
if [ -f "$APP/ClaudeHomeServer.dll" ]; then
  log "бэкап данных: dotnet ClaudeHomeServer.dll --backup (таймаут ${BACKUP_TIMEOUT} с)"
  if ( cd "$APP" && ASPNETCORE_ENVIRONMENT=Production timeout "$BACKUP_TIMEOUT" \
        dotnet ClaudeHomeServer.dll --backup >>"$LOG" 2>&1 ); then
    step data-backup ok
  else
    log "ОТКАЗ: бэкап данных не уложился/упал (таймаут ${BACKUP_TIMEOUT} с) — прод не трогаем"
    exit 3
  fi
else
  step data-backup skipped
  log "бэкап: первый деплой (ClaudeHomeServer.dll ещё нет) — пропускаю"
fi

log "останавливаю ccs.service (здесь умирает вызывающий агент)"
$SYSTEMCTL_CMD stop ccs.service
sleep 2
step stop ok

# Прод-контейнер песочницы держит bind-mount на $APP/SystemPrompts: пока он жив,
# cp -a снимка может зацепить каталог, которым контейнер владеет. Гасим ДО снимка
# (имя из прод-конфига, dev-контейнер не трогаем); сервер поднимет его сам.
if [ "$SKIP_SANDBOX" = "1" ]; then
  step stop-sandbox skipped
else
  remove_sandbox_container "$SANDBOX_CONTAINER"
  step stop-sandbox ok
fi

log "сохраняю текущий билд целиком в $PREV"
cp -a "$APP" "$PREV" || { log "ОТКАЗ: не удалось сохранить текущий билд"; exit 3; }
log "сохранено: $(du -sh "$PREV" | cut -f1)"
step snapshot ok
# Снимок в список releases для отката: id=метка, sha=из build-id.txt снимка (предыдущая
# сборка, на которую вернёт откат), path=каталог снимка. Журнал переживает выкатки,
# запись переживёт и пересохранение сервером (DeployState.Releases).
snap_sha=$(grep -E '^sha=' "$PREV/build-id.txt" 2>/dev/null | head -1 | cut -d= -f2)
journal set-release "$STAMP" "${snap_sha:-}" "$PREV"

# wwwroot чистим отдельно: старые ассеты с хешами в именах иначе копятся вечно,
# а index.html ссылается только на новые.
log "обновляю wwwroot"
rm -rf "$APP/wwwroot"
cp -a "$STAGING/wwwroot" "$APP/wwwroot"

log "копирую файлы сборки поверх (конфиги и данные не трогаю)"
ok=0
if copy_build_over "$STAGING" "$APP"; then
  step swap ok

  # Переход на новый образ песочницы: контейнер уже снесён на stop-sandbox, сносим ещё
  # раз (идемпотентно) — сервер поднимет его заново из свежего образа claude-sandbox:latest
  # при первом container-ходе (SandboxManager.EnsureRunningAsync).
  if [ "$SKIP_SANDBOX" = "1" ]; then
    step sandbox-container skipped
  else
    remove_sandbox_container "$SANDBOX_CONTAINER"
    step sandbox-container ok
  fi

  log "поднимаю ccs.service"
  $SYSTEMCTL_CMD start ccs.service
  step start ok

  # Health-гейт: юнит может подняться, а приложение упасть на старте —
  # поэтому спрашиваем именно HTTP, а не systemctl is-active.
  journal_phase verifying

  for i in $(seq 1 $HEALTH_TRIES); do
    sleep $HEALTH_SLEEP
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 -H "Host: $HEALTH_HOST" "$HEALTH_URL" || echo 000)
    log "health $i/$HEALTH_TRIES: $code"
    # 204 отдаёт /api/health, 401 — любая ручка под [Authorize]: живость доказывают оба
    if [ "$code" = "200" ] || [ "$code" = "204" ] || [ "$code" = "401" ]; then ok=1; break; fi
  done
else
  # Недокопированный app со старым бинарником поднимать нельзя — идём сразу в откат.
  log "!!! rsync завершился с ошибкой — app недокопирован"
fi

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
log "!!! выкатка не удалась (health за ${HEALTH_TRIES}×${HEALTH_SLEEP} с или копирование) — возвращаю предыдущую сборку"
$SYSTEMCTL_CMD stop ccs.service
sleep 2
rm -rf -- "${APP:?}"
cp -a "$PREV" "$APP"
$SYSTEMCTL_CMD start ccs.service
sleep $HEALTH_SLEEP
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 4 "$HEALTH_URL" || echo 000)
log "=== ОТКАЧЕНО на $STAMP, health после отката: $code ==="
exit 1