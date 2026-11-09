#!/usr/bin/env bash
# Регресс-тест на блокер B1 из задачи ffc15630-2bc2-4f5e-af2e-9a9ed2a86c6c:
# deploy-linux.sh ДОЛЖЕН звать publish-linux.sh из $REPO (relcheck), а не из
# $AGENT_DIR (основное дерево). Иначе в staging уезжает sha и код основного
# дерева, а не выкатываемого ref, и sha в build-id.txt расходится с журналом.
#
# Сценарий:
#   1. Поднимает под /tmp две разные git-копии: $SBX/agent (грязная) и $SBX/relcheck.
#   2. В оба кладёт шим publish-linux.sh, который пишет в $STAGING/build-id.txt
#      sha своего REPO (т.е. от dirname скрипта).
#   3. Копирует deploy-linux.sh, deploy-journal.py, switch-release.sh в $SBX/agent/scripts/ops
#      и в $SBX/relcheck/scripts/ops (relcheck-версия нужна для $REPO/scripts/ops/publish-linux.sh).
#   4. Ставит шимы systemctl/systemd-run в $SBX/shim, чтобы switch-release.sh не
#      задел боевой /opt/ccs и реальный systemd.
#   5. Пишет заявку в $SBX/releases/deploy-state.json и вызывает deploy-linux.sh
#      из $SBX/agent/scripts/ops с $CCS_REPO_DIR=$SBX/relcheck.
#   6. Проверяет: sha в $STAGING/build-id.txt равен sha relcheck, не sha agent.
#   7. Мутация: делает копию deploy-linux.sh с инверсией фикса (REPO → AGENT_DIR)
#      и прогоняет её. sha в build-id.txt должен разойтись с sha relcheck
#      (= sha agent). Тест краснеет.
#
# Запуск:
#   bash scripts/ops/test-deploy-linux.sh
#   (идёт в /tmp/ccs-deploy-test, чистит каталог под собой)

set -uo pipefail
# set -e не включаем: проверки assert_xxx делают exit 1 сами.

SBX=/tmp/ccs-deploy-test
AGENT="$SBX/agent"
RELCHECK="$SBX/relcheck"
RELEASES="$SBX/releases"
STAGING="$SBX/staging"
APP="$SBX/app"
SHIM="$SBX/shim"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

red()   { printf '\033[31m%s\033[0m\n' "$*"; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }
blue()  { printf '\033[34m%s\033[0m\n' "$*"; }
note()  { printf '  %s\n' "$*"; }

fail() { red "FAIL: $*"; exit 1; }
ok()   { green "OK: $*"; }

assert_eq() {
  local got="$1" want="$2" what="$3"
  if [ "$got" = "$want" ]; then
    ok "$what == $got"
  else
    red "FAIL: $what"
    note "  got:  $got"
    note "  want: $want"
    exit 1
  fi
}

# Шимы systemctl и systemd-run в $SHIM: ничего не делают, не трогают боевой /opt/ccs.
# switch-release.sh сам себя не detach'ит (CCS_SWITCH_DETACHED=1) и не зовёт
# реальный systemctl/systemd-run — но проверяет коды возврата и пишет лог.
make_shims() {
  mkdir -p "$SHIM"
  cat > "$SHIM/systemctl" <<'SHIM_EOF'
#!/usr/bin/env bash
echo "shim-systemctl: $*" >> "$(dirname "$0")/../shim.log"
exit 0
SHIM_EOF
  cat > "$SHIM/systemd-run" <<'SHIM_EOF'
#!/usr/bin/env bash
echo "shim-systemd-run: $*" >> "$(dirname "$0")/../shim.log"
exit 99
SHIM_EOF
  chmod +x "$SHIM/systemctl" "$SHIM/systemd-run"
}

# Шим publish-linux.sh: пишет build-id.txt с sha своего REPO. REPO скрипт
# вычисляет по своему пути (`$(dirname "$0")/../..`) — отсюда и инвариант B1:
# если оркестратор зовёт $REPO/scripts/ops/publish-linux.sh, sha в build-id.txt
# будет sha relcheck; если $AGENT_DIR — sha agent.
make_publish_shim() {
  local target="$1" label="$2"
  mkdir -p "$target/scripts/ops"
  cat > "$target/scripts/ops/publish-linux.sh" <<SHIM_EOF
#!/usr/bin/env bash
# Шим: пишет build-id.txt с sha своего REPO (label=$label).
set -uo pipefail
SCRIPT_DIR="\$(cd "\$(dirname "\$0")" && pwd)"
REPO="\$(cd "\$SCRIPT_DIR/../.." && pwd)"
sha="\$(git -C "\$REPO" rev-parse --short=8 HEAD)"
ref="\$(git -C "\$REPO" rev-parse --abbrev-ref HEAD)"
out="\${PUBLISH_DIR:-\$REPO/staging}"
mkdir -p "\$out"
printf 'shim-build-from=%s\nsha=%s\nref=%s\ndirty=False\n' "$label" "\$sha" "\$ref" \\
  > "\$out/build-id.txt"
echo "shim-publish: label=$label sha=\$sha repo=\$REPO"
# Модули, которые switch-release.sh проверяет в staging. Без них switch откажет
# на гейте «staging неполный» и пойдёт в 2 (rolled_back). Шим рисует то, что
# нужно, чтобы switch дошёл до health-гейта и вернул 0.
for d in ClaudeHomeServer.dll wwwroot modules modules/notes modules/spend; do
  case "\$d" in
    *.dll) mkdir -p "\$out/\$(dirname "\$d")" ;;
    *)     mkdir -p "\$out/\$d" ;;
  esac
done
SHIM_EOF
  chmod +x "$target/scripts/ops/publish-linux.sh"
}

# Делает пустой git-репо с одним коммитом. label идёт в коммит-сообщение и в
# шим, чтобы при диагностике сразу видно, кто это.
make_repo() {
  local dir="$1" label="$2"
  rm -rf "$dir"
  mkdir -p "$dir"
  git -C "$dir" -c init.defaultBranch=main init -q
  git -C "$dir" -c user.email=test@test -c user.name=test commit --allow-empty -q -m "init $label"
}

# В $AGENT намеренно кладём грязный файл — это «основное дерево» во время выкатки
# (между snapshot и стартом агента). Проверяет, что оркестратор не тянет sha
# из этого дерева.
dirty_up_agent() {
  echo "dirty-edit" >> "$AGENT/UNCOMMITTED.txt"
  git -C "$AGENT" status --porcelain
}

# Копирует deploy-linux.sh, deploy-journal.py, switch-release.sh в scripts/ops.
# Один раз — в $AGENT (это и есть AgentDir, как на проде), второй — в $RELCHECK
# (для $REPO/scripts/ops/publish-linux.sh).
copy_scripts() {
  local dst="$1"
  mkdir -p "$dst/scripts/ops"
  install -m 755 "$REPO_ROOT/scripts/ops/deploy-linux.sh"  "$dst/scripts/ops/"
  install -m 755 "$REPO_ROOT/scripts/ops/deploy-journal.py" "$dst/scripts/ops/"
  install -m 755 "$REPO_ROOT/scripts/ops/switch-release.sh" "$dst/scripts/ops/"
}

# Пишет заявку в $RELEASES/deploy-state.json (минимальный контракт, который
# читает deploy-linux.sh). kind=deploy, phase=queued, ref=HEAD relcheck, request
# с skipFrontend=False, allowDirty=False.
write_request() {
  local ref="$1"
  mkdir -p "$RELEASES"
  python3 -I - <<PY
import json, os
state = {
    "current": {
        "id": "test-0001",
        "kind": "deploy",
        "phase": "queued",
        "ref": "$ref",
        "request": {"ref": "$ref", "skipFrontend": True, "allowDirty": True, "skipSandbox": True},
        "initiatedBy": "test",
        "startedAt": "2026-10-12T00:00:00Z"
    },
    "history": []
}
os.makedirs("$RELEASES", exist_ok=True)
with open("$RELEASES/deploy-state.json", "w") as f:
    json.dump(state, f)
PY
}

# Прогон оркестратора. $1 — какой deploy-linux.sh запускать (по умолчанию $AGENT).
run_orchestrator() {
  local script="${1:-$AGENT/scripts/ops/deploy-linux.sh}"
  # Подменяем switch-release.sh в $AGENT на «нулевой» вариант, чтобы он
  # не звал systemctl/systemd-run и не делал ничего странного: его зовёт
  # оркестратор после сборки. Без этого подмена systemctl/systemd-run
  # теряет смысл (наш шим exit 99 для systemd-run, а switch его не зовёт,
  # но systemctl stop/start — зовёт, и шим systemctl молча ок).
  # Нам важен build-id.txt — он пишется в $STAGING шимом publish-linux.sh.
  # Скрипт switch-release.sh дальше всё равно упадёт на health-гейте, но
  # build-id.txt уже на месте. Мы проверяем именно его.
  CCS_RELEASES_DIR="$RELEASES" \
  CCS_REPO_DIR="$RELCHECK" \
  CCS_APP_DIR="$APP" \
  CCS_STAGING_DIR="$STAGING" \
  CCS_SWITCH_DETACHED=1 \
  PATH="$SHIM:$PATH" \
  HEALTH_URL="http://127.0.0.1:1/" HEALTH_TRIES=1 HEALTH_SLEEP=0 \
  LOG="$RELEASES/deploy-test.log" \
  RELEASES="$RELEASES" \
  APP="$APP" STAGING="$STAGING" \
  CCS_DEPLOY_JOURNAL="$AGENT/scripts/ops/deploy-journal.py" \
  CCS_DEPLOY_STATE_PATH="$RELEASES/deploy-state.json" \
  CCS_DEPLOY_LOCK_FD= \
  "$script" > "$RELEASES/orchestrator.log" 2>&1 || true
  return 0
}

# Создаёт мутант: копию deploy-linux.sh, где фикс B1 откатили обратно к
# $AGENT_DIR/scripts/ops/publish-linux.sh. Кладём её в $AGENT/scripts/ops/,
# чтобы самоexec нашёл $CCS_DEPLOY_ORIG=…/$AGENT/scripts/ops/deploy-linux.sh,
# откуда dirname+.. даст ровно $AGENT_DIR (как в проде).
make_mutant() {
  local mutant="$AGENT/scripts/ops/deploy-linux.mutant.sh"
  sed 's|"$REPO/scripts/ops/publish-linux.sh"|"$AGENT_DIR/scripts/ops/publish-linux.sh"|g' \
    "$AGENT/scripts/ops/deploy-linux.sh" > "$mutant"
  chmod +x "$mutant"
  echo "$mutant"
}

main() {
  blue "==> ccs-deploy-test: регресс на блокер B1"
  rm -rf "$SBX"
  mkdir -p "$SBX"

  make_shims
  note "shim systemctl/systemd-run в $SHIM (не трогают боевой /opt/ccs)"

  make_repo "$AGENT" "agent-main-tree"
  note "AgentDir = $AGENT  sha=$(git -C "$AGENT" rev-parse --short=8 HEAD)"
  dirty_up_agent
  note "AgentDir грязный: $(git -C "$AGENT" status --porcelain | head -1)"

  make_repo "$RELCHECK" "relcheck"
  note "Relcheck = $RELCHECK  sha=$(git -C "$RELCHECK" rev-parse --short=8 HEAD)"

  # Чтобы sha различались между agent и relcheck — добавим ещё один коммит в relcheck.
  git -C "$RELCHECK" -c user.email=test@test -c user.name=test commit --allow-empty -q -m "extra relcheck"
  RELCHECK_SHA=$(git -C "$RELCHECK" rev-parse --short=8 HEAD)
  AGENT_SHA=$(git -C "$AGENT" rev-parse --short=8 HEAD)
  note "после extra коммита: relcheck=$RELCHECK_SHA  agent=$AGENT_SHA"
  if [ "$RELCHECK_SHA" = "$AGENT_SHA" ]; then
    fail "sha agent и relcheck совпали — тест не имеет смысла"
  fi

  make_publish_shim "$AGENT" "agent"
  make_publish_shim "$RELCHECK" "relcheck"
  note "шимы publish-linux.sh: agent-шимер пишет $AGENT_SHA, relcheck-шимер пишет $RELCHECK_SHA"

  copy_scripts "$AGENT"
  copy_scripts "$RELCHECK"
  note "скрипты deploy-linux.sh/deploy-journal.py/switch-release.sh скопированы в оба дерева"

  # --- Позитивный тест: фикс стоит, build-id.txt == sha relcheck ---
  rm -rf "$RELEASES" "$STAGING" "$APP"
  write_request "HEAD"
  blue "==> прогон оркестратора с фиксом B1 (ожидаем build-id.txt = $RELCHECK_SHA)"
  run_orchestrator

  if [ ! -f "$STAGING/build-id.txt" ]; then
    red "FAIL: $STAGING/build-id.txt не появился"
    note "лог оркестратора:"
    sed 's/^/  | /' "$RELEASES/orchestrator.log" | head -20
    note "журнал deploy-state.json:"
    sed 's/^/  | /' "$RELEASES/deploy-state.json" | head -20
    exit 1
  fi
  STAGE_SHA=$(grep -E '^sha=' "$STAGING/build-id.txt" | head -1 | cut -d= -f2)
  STAGE_FROM=$(grep -E '^shim-build-from=' "$STAGING/build-id.txt" | head -1 | cut -d= -f2)
  note "build-id.txt: shim-build-from=$STAGE_FROM sha=$STAGE_SHA"
  assert_eq "$STAGE_FROM" "relcheck" "источник сборки (должен быть relcheck)"
  assert_eq "$STAGE_SHA" "$RELCHECK_SHA" "sha в build-id.txt (должен быть sha relcheck)"
  if [ "$STAGE_SHA" = "$AGENT_SHA" ]; then
    fail "sha в build-id.txt совпал с sha agent — фикс B1 не сработал"
  fi

  # Шаг sandbox-image: заявка skipSandbox=True → оркестратор не зовёт docker, а
  # записывает шаг как skipped. Проверяем, что он в итоге выкатки (history[0].steps).
  sb_status=$(python3 -I -c '
import json, sys
d = json.load(open(sys.argv[1]))
rec = (d.get("history") or [None])[0] or d.get("current") or {}
step = next((s for s in (rec.get("steps") or []) if s.get("name") == "sandbox-image"), None)
print(step["status"] if step else "missing")
' "$RELEASES/deploy-state.json" 2>/dev/null)
  if [ "$sb_status" = "skipped" ]; then
    ok "sandbox-image: шаг записан как skipped (skipSandbox из заявки)"
  else
    red "FAIL: sandbox-image шаг = '$sb_status' (ждали skipped)"
    exit 1
  fi

  # --- Мутация: фикс откатили, build-id.txt == sha agent, и тест краснеет ---
  blue "==> мутация: фикс B1 откатываем (REPO → AGENT_DIR), ожидаем build-id.txt = $AGENT_SHA"
  MUTANT=$(make_mutant)
  rm -rf "$RELEASES" "$STAGING" "$APP"
  write_request "HEAD"
  run_orchestrator "$MUTANT"

  if [ ! -f "$STAGING/build-id.txt" ]; then
    red "FAIL (мутация): $STAGING/build-id.txt не появился"
    sed 's/^/  | /' "$RELEASES/orchestrator.log" | head -20
    exit 1
  fi
  MUT_SHA=$(grep -E '^sha=' "$STAGING/build-id.txt" | head -1 | cut -d= -f2)
  MUT_FROM=$(grep -E '^shim-build-from=' "$STAGING/build-id.txt" | head -1 | cut -d= -f2)
  note "build-id.txt (мутант): shim-build-from=$MUT_FROM sha=$MUT_SHA"

  if [ "$MUT_FROM" = "relcheck" ]; then
    fail "мутация не сработала: build-id.txt всё ещё от relcheck ($MUT_SHA). Тест не различает хорошее и плохое."
  fi
  if [ "$MUT_SHA" != "$AGENT_SHA" ]; then
    red "FAIL: мутация должна была привести к sha=$AGENT_SHA (источник agent), а получили $MUT_SHA"
    note "мутант не привёл к ожидаемому эффекту — проверка бесполезна, ломай дальше"
    exit 1
  fi
  green "OK: мутация подтверждена (sha=$MUT_SHA, источник $MUT_FROM) — фикс B1 удерживается только пока REPO"

  blue "==> итого: позитив зелёный, мутация красная, фикс B1 имеет смысл"
}

main "$@"
