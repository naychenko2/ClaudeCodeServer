#!/usr/bin/env bash
# Уборка /tmp от мусора сборок и брошенных review-worktree.
#
# Что чистит (только СВОИ записи пользователя, старше --min-age-hours, по умолчанию 24 ч):
#  1. git-worktree основного репозитория, лежащие под /tmp: без живого процесса с cwd внутри,
#     без правок отслеженных файлов, не locked, а у отсоединённого HEAD — коммит достижим
#     из какой-либо ветки (иначе коммиты потеряются при gc, такой worktree только в отчёт);
#  2. каталоги верхнего уровня MSBuildTemp?????? (17 символов), fake-cli-*, ccs_*, tmp.*.
# Не трогает: systemd-private-*, .X11-unix и любые чужие/свежие записи.
#
# Использование:
#   ccs-tmp-cleanup.sh [--dry-run] [--min-age-hours N]
# Переменные: CCS_REPO — основной репозиторий (по умолчанию корень, где лежит скрипт),
#             CCS_TMP — чистимый каталог (по умолчанию /tmp).

set -euo pipefail

REPO="${CCS_REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
TMP="${CCS_TMP:-/tmp}"
DRY_RUN=0
MIN_AGE_H=24

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY_RUN=1 ;;
    --min-age-hours) MIN_AGE_H="${2:?нужно число часов}"; shift ;;
    -h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Неизвестный аргумент: $1" >&2; exit 64 ;;
  esac
  shift
done

MIN_AGE_MIN=$((MIN_AGE_H * 60))
log() { printf '%s\n' "$*"; }
act() { if [[ $DRY_RUN -eq 1 ]]; then log "  [dry-run] $*"; else "$@"; fi; }

log "== ccs-tmp-cleanup: $(date -Is), каталог $TMP, возраст > ${MIN_AGE_H} ч, dry-run=$DRY_RUN"
log "-- df до:"; df -h --output=source,size,used,avail,pcent "$TMP" | sed 's/^/  /'

# Живые cwd всех доступных процессов (чужие недоступны для чтения — нам они и не интересны).
LIVE_CWDS="$(for p in /proc/[0-9]*; do readlink "$p/cwd" 2>/dev/null || true; done | sort -u)"

in_use() { # $1 — каталог; 0, если у какого-то процесса cwd внутри
  local d="${1%/}"
  grep -qxF -- "$d" <<<"$LIVE_CWDS" && return 0
  grep -qF -- "$d/" <<<"$LIVE_CWDS" && return 0
  return 1
}

recently_touched() { # есть ли внутри (глубина ≤ 3, без node_modules/.git) файл новее порога
  [[ -n "$(find "$1" -maxdepth 3 \( -name node_modules -o -name .git \) -prune -o -mmin "-$MIN_AGE_MIN" -print -quit 2>/dev/null)" ]]
}

# ---------- 1. worktree ----------
log "-- review-worktree под $TMP:"
removed_wt=0; skipped_wt=0
if git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1; then
  mapfile -t WT_PATHS < <(git -C "$REPO" worktree list --porcelain | sed -n 's/^worktree //p')
  for wt in "${WT_PATHS[@]}"; do
    [[ "$wt" == "$TMP"/* ]] || continue
    reason=""
    if [[ ! -d "$wt" ]]; then reason="" # каталога нет — prune ниже подчистит запись
    elif git -C "$REPO" worktree list --porcelain | awk -v p="$wt" '$1=="worktree"{c=($2==p)} c&&$1=="locked"{f=1} END{exit !f}'; then reason="locked"
    elif in_use "$wt"; then reason="живой процесс с cwd внутри"
    elif recently_touched "$wt"; then reason="изменён менее ${MIN_AGE_H} ч назад"
    elif [[ -n "$(git -C "$wt" status --porcelain -uno 2>/dev/null)" ]]; then reason="правки отслеженных файлов"
    elif ! git -C "$wt" symbolic-ref -q HEAD >/dev/null 2>&1 \
         && [[ -z "$(git -C "$wt" branch -a --contains HEAD 2>/dev/null)" ]]; then reason="HEAD не достижим из веток"
    fi
    size="$(du -sxh "$wt" 2>/dev/null | cut -f1 || true)"
    if [[ -n "$reason" ]]; then
      log "  ПРОПУЩЕН $wt (${size:-?}) — $reason"; skipped_wt=$((skipped_wt + 1))
    else
      log "  удаляю   $wt (${size:-?})"
      act git -C "$REPO" worktree remove --force "$wt" || log "    не удалось снять $wt"
      removed_wt=$((removed_wt + 1))
    fi
  done
  act git -C "$REPO" worktree prune
  # Пустые родительские каталоги вида /tmp/gleb-rev после снятия вложенных worktree.
  for wt in "${WT_PATHS[@]}"; do
    parent="$(dirname "$wt")"
    [[ "$wt" == "$TMP"/* && "$parent" != "$TMP" && -d "$parent" ]] || continue
    [[ -z "$(ls -A "$parent" 2>/dev/null)" ]] && act rmdir "$parent" || true
  done
else
  log "  $REPO — не git-репозиторий, пропускаю"
fi
log "  итого: снято $removed_wt, пропущено $skipped_wt"

# ---------- 2. мусор сборок и тестов ----------
log "-- мусор верхнего уровня $TMP:"
for pat in 'MSBuildTemp??????' 'fake-cli-*' 'ccs_*' 'tmp.*'; do
  cnt="$(find "$TMP" -maxdepth 1 -user "$(id -u)" -name "$pat" -mmin "+$MIN_AGE_MIN" 2>/dev/null | wc -l)"
  log "  $pat: $cnt шт."
  if [[ $DRY_RUN -eq 0 && $cnt -gt 0 ]]; then
    find "$TMP" -maxdepth 1 -user "$(id -u)" -name "$pat" -mmin "+$MIN_AGE_MIN" -exec rm -rf -- {} + 2>/dev/null || true
  fi
done

log "-- df после:"; df -h --output=source,size,used,avail,pcent "$TMP" | sed 's/^/  /'
