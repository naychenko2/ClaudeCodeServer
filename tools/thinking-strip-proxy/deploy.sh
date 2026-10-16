#!/usr/bin/env bash
# Выкладка боевого thinking-strip-proxy в каталог /opt/ccs/proxy (решение Андрея 2026-10-16).
#
#   deploy.sh [ref]            выложить ref (по умолчанию master) и перезапустить юнит
#   deploy.sh --rollback [id]  вернуться на предыдущую версию (или на releases/<id>)
#   deploy.sh --list           показать выложенные версии
#
# Почему так. Прежде юнит запускал proxy.py прямо из основной рабочей копии, и любой
# `git checkout` в ней менял код на бою (16.10 на бою оказался непроверенный compactor.py ветки).
# Теперь на бою живёт только содержимое каталога выкладки; рабочая копия на него не влияет.
#
# Раскладка:  $DEPLOY_ROOT/releases/<UTC-время>-<sha>/   неизменяемые версии
#             $DEPLOY_ROOT/current  -> releases/...      то, что запускает юнит
#             $DEPLOY_ROOT/previous -> releases/...      куда откатываться
# Переключение — атомарная подмена симлинка (ln + mv -T), версия никогда не лежит
# полуразобранной. Берётся именно коммит ref через git archive, а не рабочее дерево.
#
# Переменные: DEPLOY_ROOT (/opt/ccs/proxy), KEEP (5 версий), STATS_URL, ALLOW_NON_MASTER=1
# (выложить не master — только для разовой проверки, в README объяснено, почему не надо).
# Ключ --no-systemd — только разложить файлы и переключить симлинк (для проверки без боя).
set -euo pipefail

DEPLOY_ROOT="${DEPLOY_ROOT:-/opt/ccs/proxy}"
KEEP="${KEEP:-5}"
STATS_URL="${STATS_URL:-http://127.0.0.1:18021/__proxy/stats}"
SYSTEM_UNIT_DIR=/etc/systemd/system
USER_UNIT_DIR="$HOME/.config/systemd/user"
UNIT=thinking-strip-proxy
SRC_DIR=tools/thinking-strip-proxy

die() { echo "ОШИБКА: $*" >&2; exit 1; }
say() { echo "== $*"; }

# Каталог репозитория берём от самого скрипта, а не от cwd: запуск из любого места.
REPO="$(git -C "$(dirname "$(readlink -f "$0")")" rev-parse --show-toplevel)"

NO_SYSTEMD=0
MODE=deploy
ARG=""
for a in "$@"; do
  case "$a" in
    --no-systemd) NO_SYSTEMD=1 ;;
    --rollback)   MODE=rollback ;;
    --list)       MODE=list ;;
    -h|--help)    sed -n '2,22p' "$0"; exit 0 ;;
    -*)           die "неизвестный ключ $a" ;;
    *)            ARG="$a" ;;
  esac
done

target_of() { readlink -f "$DEPLOY_ROOT/$1" 2>/dev/null || true; }

list_releases() {
  local cur prev
  cur="$(target_of current)"; prev="$(target_of previous)"
  [ -d "$DEPLOY_ROOT/releases" ] || { echo "(выкладок нет)"; return; }
  for d in $(ls -1d "$DEPLOY_ROOT"/releases/* 2>/dev/null | sort -r); do
    local mark=""
    [ "$d" = "$cur" ] && mark=" <- current"
    [ "$d" = "$prev" ] && mark=" <- previous"
    echo "$(basename "$d")$mark"
  done
}

# Подмена симлинка атомарно: новый временный линк, затем переименование поверх старого.
swap_link() { # имя цель
  ln -sfn "$2" "$DEPLOY_ROOT/.$1.new"
  mv -T "$DEPLOY_ROOT/.$1.new" "$DEPLOY_ROOT/$1"
}

# Юниты кладём в систему только если содержимое отличается, со страховочной копией старого.
install_file() { # источник приёмник sudo|user
  local src="$1" dst="$2" how="$3"
  if [ -e "$dst" ] && cmp -s "$src" "$dst"; then return 0; fi
  say "обновляю $dst"
  if [ "$how" = sudo ]; then
    [ -e "$dst" ] && sudo cp -p "$dst" "$dst.bak-$(date +%Y%m%d-%H%M%S)"
    sudo install -m 0644 "$src" "$dst"
  else
    [ -e "$dst" ] && cp -p "$dst" "$dst.bak-$(date +%Y%m%d-%H%M%S)"
    install -m 0644 "$src" "$dst"
  fi
}

install_units() { # каталог выложенной версии
  local rel="$1"
  sudo mkdir -p "$SYSTEM_UNIT_DIR/$UNIT.service.d"
  install_file "$rel/$UNIT.service" "$SYSTEM_UNIT_DIR/$UNIT.service" sudo
  for f in "$rel/$UNIT.service.d/"*.conf; do
    [ -e "$f" ] || continue
    install_file "$f" "$SYSTEM_UNIT_DIR/$UNIT.service.d/$(basename "$f")" sudo
  done
  # prefix-keeper — пользовательский юнит; ставим, только если он уже заведён у пользователя
  # (новый таймер без решения человека не включаем).
  if [ -e "$USER_UNIT_DIR/prefix-keeper.service" ]; then
    install_file "$rel/prefix-keeper.service" "$USER_UNIT_DIR/prefix-keeper.service" user
    [ -e "$rel/prefix-keeper.timer" ] && install_file "$rel/prefix-keeper.timer" "$USER_UNIT_DIR/prefix-keeper.timer" user
    systemctl --user daemon-reload
  fi
  sudo systemctl daemon-reload
}

# Проверка боя после рестарта: stats отвечает, а процесс запущен ИЗ каталога выкладки.
health_check() {
  local i body pid exe
  for i in $(seq 1 20); do
    if body="$(curl -fsS --max-time 3 "$STATS_URL" 2>/dev/null)"; then break; fi
    body=""; sleep 1
  done
  [ -n "$body" ] || { echo "stats не ответил за 20 с" >&2; return 1; }
  pid="$(systemctl show "$UNIT" -p MainPID --value)"
  [ "${pid:-0}" != 0 ] || { echo "у юнита нет MainPID" >&2; return 1; }
  exe="$(tr '\0' ' ' < "/proc/$pid/cmdline")"
  case "$exe" in
    *"$DEPLOY_ROOT/current/proxy.py"*) ;;
    *) echo "процесс запущен не из каталога выкладки: $exe" >&2; return 1 ;;
  esac
  echo "$body" | python3 -I -c '
import json, sys
s = json.load(sys.stdin)
print("stats: prune=%s compact_preserve=%s compact=%s requests=%s" % (
    s.get("prune"), s.get("compact_preserve"), s.get("compact"), s.get("requests")))'
  echo "процесс: pid=$pid $exe"
}

restart_and_check() {
  [ "$NO_SYSTEMD" = 1 ] && { say "--no-systemd: юнит не трогаю"; return 0; }
  install_units "$(target_of current)"
  say "перезапуск $UNIT"
  sudo systemctl restart "$UNIT"
  health_check
}

do_rollback() {
  local want="$ARG" dest
  if [ -n "$want" ]; then
    dest="$DEPLOY_ROOT/releases/$want"
    [ -d "$dest" ] || die "нет версии $want (см. --list)"
  else
    dest="$(target_of previous)"
    [ -n "$dest" ] && [ -d "$dest" ] || die "previous не задан — откатываться некуда"
  fi
  local cur; cur="$(target_of current)"
  [ "$dest" != "$cur" ] || die "current уже указывает на $(basename "$dest")"
  say "откат: $(basename "$cur") -> $(basename "$dest")"
  swap_link previous "$cur"
  swap_link current "$dest"
  restart_and_check
}

do_deploy() {
  local ref="${ARG:-master}" sha stamp rel tmp
  if [ "$ref" != master ] && [ "${ALLOW_NON_MASTER:-0}" != 1 ]; then
    die "боевая выкладка берёт только master (получено «$ref»); ALLOW_NON_MASTER=1 — осознанное исключение"
  fi
  sha="$(git -C "$REPO" rev-parse --verify --quiet "$ref^{commit}")" || die "ref «$ref» не найден"
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  rel="$DEPLOY_ROOT/releases/$stamp-${sha:0:9}"
  tmp="$DEPLOY_ROOT/releases/.tmp-$stamp-$$"
  mkdir -p "$DEPLOY_ROOT/releases"
  trap 'rm -rf "$tmp"' EXIT

  say "ref=$ref sha=$sha -> $rel"
  mkdir "$tmp"
  # --strip-components=2 снимает tools/thinking-strip-proxy/, исключаем всё, что бою не нужно.
  git -C "$REPO" archive "$sha" "$SRC_DIR" | tar -x -C "$tmp" --strip-components=2 \
    --exclude='bench' --exclude='e2e' --exclude='__pycache__' --exclude='.omc' \
    --exclude='test_*.py' --exclude='*.pyc'
  [ -f "$tmp/proxy.py" ] && [ -f "$tmp/compactor.py" ] || die "в выкладке нет proxy.py/compactor.py"
  echo "$sha" > "$tmp/VERSION"
  # Синтаксис проверяем ДО переключения: битый файл не должен даже стать current.
  python3 -I -m py_compile "$tmp/proxy.py" "$tmp/compactor.py" "$tmp/prefix_keeper.py"
  find "$tmp" -name __pycache__ -prune -exec rm -rf {} +
  chmod -R a-w "$tmp"; chmod u+w "$tmp"
  mv "$tmp" "$rel"
  trap - EXIT

  local cur; cur="$(target_of current)"
  [ -n "$cur" ] && swap_link previous "$cur"
  swap_link current "$rel"
  say "current -> $(basename "$rel")${cur:+, previous -> $(basename "$cur")}"

  if ! restart_and_check; then
    echo "ПРОВЕРКА ПРОВАЛЕНА" >&2
    if [ -n "$cur" ] && [ "$NO_SYSTEMD" = 0 ]; then
      say "автоматический откат на $(basename "$cur")"
      swap_link current "$cur"
      swap_link previous "$rel"
      sudo systemctl restart "$UNIT"
      health_check || echo "после отката проверка тоже не прошла — смотреть journalctl -u $UNIT" >&2
    fi
    exit 1
  fi

  # Чистим старые версии, кроме current и previous.
  local keep_cur keep_prev n=0
  keep_cur="$(target_of current)"; keep_prev="$(target_of previous)"
  for d in $(ls -1d "$DEPLOY_ROOT"/releases/[0-9]* 2>/dev/null | sort -r); do
    n=$((n + 1))
    [ "$n" -le "$KEEP" ] && continue
    [ "$d" = "$keep_cur" ] || [ "$d" = "$keep_prev" ] && continue
    chmod -R u+w "$d"; rm -rf "$d"
  done
  say "готово: $sha"
}

case "$MODE" in
  list)     list_releases ;;
  rollback) do_rollback ;;
  deploy)   do_deploy ;;
esac
