#!/usr/bin/env bash
# Оркестратор выкатки на Linux (ADR-010). Запускается через systemd-run --user --collect
# отдельным юнитом, без argv: все параметры заявки едут журналом ($CCS_RELEASES_DIR/deploy-state.json).
#
# Контракт вызова (фиксированный):
#   systemd-run --user --collect --quiet --unit=ccs-deploy-<id> \
#     -p ManagedOOMPreference=avoid \
#     --setenv=CCS_RELEASES_DIR=<dir> \
#     <AgentDir>/scripts/ops/deploy-linux.sh
#
# Что делает:
#   1. Берёт flock -x на $CCS_RELEASES_DIR/deploy.lock — единый писатель журнала и лока.
#   2. Читает заявку сервера из current в deploy-state.json.
#   3. Проверяет, что relcheck чистый (иначе зависит от request.allowDirty).
#   4. git checkout --detach <ref|master> в relcheck и пишет фактический sha в журнал.
#   5. Запускает BUILD_FRONTEND=<0|1 по skipFrontend> PUBLISH_DIR=<staging> publish-linux.sh.
#   6. Инлайн вызывает CCS_SWITCH_DETACHED=1 ... switch-release.sh (мы уже в своём юните,
#      самоотвязка switch'у не нужна).
#   7. По коду выхода switch ставит succeeded/rolled_back/failed.
#
# Инварианты:
#   - Скрипт не исполняет сам себя из дерева, которое переключает git checkout. На самом
#     верху он копирует себя во временный файл и делает exec — иначе checkout подменил бы
#     код скрипта посреди bash.
#   - Любой выход, в том числе аварийный trap EXIT, закрывает result и переносит запись
#     в history. Нормальное завершение выставляет __deploy_done=1 и явно вызывает
#     journal_finalize — trap после этого no-op.
#   - skipSandbox на Linux игнорируется — sandbox-контейнер на проде не поднимается, флаг
#     остался для совместимости с Windows-контуром и контрактом REST.
#   - setsid/nohup не используются. Процесс уже вне ccs-agents.slice (его поднял systemd-run),
#     и смерть сервера на stop его не задевает (инцидент 2026-09-28 08:59, лог 085902).
#
# Переменные окружения:
#   CCS_RELEASES_DIR  обязательно — каталог, где лежат deploy-state.json и deploy.lock
#   CCS_REPO_DIR      путь к relcheck (по умолчанию <AgentDir>-relcheck рядом со скриптом)
#   CCS_APP_DIR       путь к каталогу приложения (по умолчанию /opt/ccs/app)
#   CCS_STAGING_DIR   путь к staging-каталогу (по умолчанию /opt/ccs/staging)
set -Euo pipefail

# Самоexec. Сначала копия во временный файл и exec — ТОЛЬКО ТАК. Если оставить код ниже
# работать из исходного файла, то git checkout на основной ветке подменит его посреди bash,
# и поведение станет неопределённым (контракт выкатки ADR-010, раздел «Самообновление агента
# на лету»). Файл создаётся в /tmp, потому что он обязан быть не в дереве, которое мы
# переключаем; /tmp не зависит ни от relcheck, ни от PublishDir.
#
# Одновременно сохраняем путь к исходному скрипту — после exec $0 указывает на временный
# файл, и dirname от него не даёт настоящий каталог скрипта. Без CCS_DEPLOY_ORIG мы бы не
# нашли ни publish-linux.sh, ни deploy-journal.py.
if [ -z "${CCS_DEPLOY_REEXEC:-}" ]; then
  ORIG_SCRIPT="$(readlink -f "$0")"
  tmp=$(mktemp /tmp/ccs-deploy-XXXXXX.sh)
  cp "$ORIG_SCRIPT" "$tmp"
  chmod +x "$tmp"
  export CCS_DEPLOY_REEXEC=1
  export CCS_DEPLOY_ORIG="$ORIG_SCRIPT"
  exec "$tmp" "$@"
fi

# Конфигурация.
RELEASES_DIR="${CCS_RELEASES_DIR:?CCS_RELEASES_DIR обязателен}"
STATE_PATH="$RELEASES_DIR/deploy-state.json"
LOCK="$RELEASES_DIR/deploy.lock"
AGENT_DIR="$(cd "$(dirname "$CCS_DEPLOY_ORIG")/../.." && pwd)"
REPO="${CCS_REPO_DIR:-${AGENT_DIR%/}-relcheck}"
APP="${CCS_APP_DIR:-/opt/ccs/app}"
STAGING="${CCS_STAGING_DIR:-/opt/ccs/staging}"
JOURNAL_HELPER="${CCS_DEPLOY_JOURNAL:-$(dirname "$CCS_DEPLOY_ORIG")/deploy-journal.py}"

# flock -x держим ДО всего — иначе параллельная выкатка могла бы писать в журнал
# одновременно с нами. Тот же файл держит и switch-release.sh: при ручном запуске он
# открывает свой fd 9, при вызове из нас — пользуется унаследованным (мы передаём
# номер fd через env). Без передачи fd switch-release.sh сделал бы свой exec 9>file
# на уже открытый fd, bash оптимизировал бы redirect в noop, а flock -n получил бы
# EAGAIN (тот же OFD уже залочен нами).
exec 9>"$LOCK"
if ! flock -x -n 9; then
  echo "deploy-linux: deploy.lock занят — выкатка уже идёт, выход" >&2
  exit 0
fi
# Передаём fd в дочерний процесс. fd 9 уже открыт на deploy.lock и держит flock — при
# fork+exec он копируется в switch-release.sh вместе с блокировкой, и тот не должен
# брать свой.
export CCS_DEPLOY_LOCK_FD=9

# Аварийный выход: если скрипт упал до явного finalize (например, journal не читался),
# trap EXIT закроет current как failed. Нормальный путь ставит __deploy_done=1 — тогда
# trap no-op. journal_finalize идемпотентен (повторный вызов при current=None не пишет).
__deploy_done=0
trap '__finalize_on_exit' EXIT
__finalize_on_exit() {
  if [ "$__deploy_done" = "0" ]; then
    "$JOURNAL_HELPER" "$STATE_PATH" finalize failed \
      "deploy-linux завершился аварийно (ловушка EXIT)" 2>/dev/null || true
  fi
}

log()  { echo "[$(date +%H:%M:%S)] $*"; }
warn() { echo "[$(date +%H:%M:%S)] warning: $*" >&2; }

# Хелперы журнала. Любой сбой журнала — предупреждение в stderr, не роняем выкатку:
# прод важнее карточки в чате. Если карточка нужна — сервер всё ещё владеет файлом
# и при следующей побудке запишет заново.
journal_phase() {
  "$JOURNAL_HELPER" "$STATE_PATH" set-phase "$1" 2>/dev/null \
    || warn "journal set-phase $1 не удался"
}
journal_step() {
  "$JOURNAL_HELPER" "$STATE_PATH" set-step "$1" "$2" 2>/dev/null \
    || warn "journal set-step $1 $2 не удался"
}
journal_sha() {
  "$JOURNAL_HELPER" "$STATE_PATH" set-sha "$1" 2>/dev/null \
    || warn "journal set-sha $1 не удался"
}
journal_finalize() {
  local status="$1" message="${2:-}"
  if [ -n "$message" ]; then
    "$JOURNAL_HELPER" "$STATE_PATH" finalize "$status" "$message" 2>/dev/null \
      || warn "journal finalize $status не удался"
  else
    "$JOURNAL_HELPER" "$STATE_PATH" finalize "$status" 2>/dev/null \
      || warn "journal finalize $status не удался"
  fi
  __deploy_done=1
}

# Чтение заявки. current уже записан сервером: id, kind=deploy, phase=queued, ref, sha,
# dirty, request, initiatedBy, startedAt. Агент добавляет phase/sha/steps/result.
deploy_id=$(python3 -c "
import json, sys
s = json.load(open('$STATE_PATH'))
cur = s.get('current')
if not cur:
  sys.exit('current пуст — сервер не оставил заявку')
print(cur['id'])
" 2>&1) || {
  warn "чтение заявки: $deploy_id"
  journal_finalize failed "журнал не читается"
  exit 0
}

request_json=$(python3 -c "
import json
print(json.dumps(json.load(open('$STATE_PATH'))['current']['request']))
")

log "deploy $deploy_id start: $request_json"

# Параметры заявки.
ref=$(echo "$request_json" | python3 -c "import sys,json; r=json.load(sys.stdin); print(r.get('ref') or 'master')")
skipFrontend=$(echo "$request_json" | python3 -c "import sys,json; print(str(json.load(sys.stdin).get('skipFrontend', False)).lower())")
allowDirty=$(echo "$request_json" | python3 -c "import sys,json; print(str(json.load(sys.stdin).get('allowDirty', False)).lower())")

log "deploy $deploy_id: ref=$ref skipFrontend=$skipFrontend allowDirty=$allowDirty"

# Грязное дерево relcheck. Сервер делал snapshot основной репы, но relcheck — отдельная
# копия, она могла стать грязной между snapshot и стартом агента. Контракт требует
# перепроверить здесь.
if [ ! -e "$REPO" ]; then
  journal_finalize failed "relcheck не существует: $REPO"
  exit 0
fi
if [ ! -e "$REPO/.git" ]; then
  journal_finalize failed "relcheck не git репа: $REPO"
  exit 0
fi

dirty=$(git -C "$REPO" status --porcelain 2>/dev/null) || dirty=""
if [ -n "$dirty" ]; then
  if [ "$allowDirty" != "true" ]; then
    journal_finalize failed "relcheck грязный и allowDirty не задан"
    exit 0
  fi
  log "deploy $deploy_id: relcheck грязный, allowDirty — едем"
fi

# Переход в нужный ref. detached HEAD — норма для relcheck.
journal_phase building
if ! git -C "$REPO" checkout --detach "$ref" 2>&1; then
  journal_finalize failed "git checkout --detach $ref не удался"
  exit 0
fi
actual_sha=$(git -C "$REPO" rev-parse --short=8 HEAD)
log "deploy $deploy_id: HEAD=$actual_sha"
journal_sha "$actual_sha"

# Сборка. Параметры:
#   BUILD_FRONTEND=1 — если НЕ skipFrontend
#   BUILD_DIFY=1     — mcp-dify на проде собирается рядом с сервером
#   PUBLISH_DIR=$STAGING — staging-каталог, который читает switch-release.sh
if [ "$skipFrontend" = "true" ]; then
  build_frontend=0
else
  build_frontend=1
fi

log "deploy $deploy_id: publish-linux.sh (BUILD_FRONTEND=$build_frontend PUBLISH_DIR=$STAGING)"
if ! env BUILD_FRONTEND="$build_frontend" BUILD_DIFY=1 PUBLISH_DIR="$STAGING" \
        "$AGENT_DIR/scripts/ops/publish-linux.sh" 2>&1; then
  journal_step build fail
  journal_finalize failed "publish-linux.sh упал"
  exit 0
fi
journal_step build ok

# Переключение. CCS_SWITCH_DETACHED=1 — мы уже в своём юните systemd-run, повторная
# самоотвязка не нужна и была бы петлёй. Журнал и пути передаём переменными.
log "deploy $deploy_id: switch-release.sh"
CCS_SWITCH_DETACHED=1 \
CCS_DEPLOY_JOURNAL="$JOURNAL_HELPER" \
CCS_DEPLOY_STATE_PATH="$STATE_PATH" \
APP="$APP" STAGING="$STAGING" RELEASES="$RELEASES_DIR" \
LOG="$RELEASES_DIR/deploy-$deploy_id.log" \
"$AGENT_DIR/scripts/ops/switch-release.sh"
switch_rc=$?
log "deploy $deploy_id: switch-release.sh вернул $switch_rc"

# Интерпретация кода выхода: 0 → succeeded, 1 → rolled_back, остальное → failed.
case "$switch_rc" in
  0) journal_finalize succeeded ;;
  1) journal_finalize rolled_back ;;
  *) journal_finalize failed "switch-release.sh вернул код $switch_rc" ;;
esac
exit 0