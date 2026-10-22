#!/usr/bin/env bash
# Уборка /tmp от мусора сборок и брошенных review-worktree.
#
# Что чистит (только СВОИ записи пользователя, старше --min-age-hours, по умолчанию 24 ч):
#  1. git-worktree основного репозитория, лежащие под /tmp: без живого процесса с cwd внутри,
#     без правок отслеженных файлов, не locked, а у отсоединённого HEAD — коммит достижим
#     из какой-либо ветки (иначе коммиты потеряются при gc, такой worktree только в отчёт);
#  1б. влитые worktree ВНЕ /tmp (напр. в ~/Sources, по 7–15 ГБ из-за bin/obj) — только если
#     ВСЁ сразу: HEAD — предок master (ветка влита), git status пуст (в т. ч. нет неотслеживаемых),
#     нет живого процесса с cwd внутри, ничего внутри не новее порога, последний коммит старше
#     --merged-age-days (3), путь не значится в WorktreePath незакрытой задачи из CCS_TASKS_FILE,
#     не locked, не основной checkout. Ветка не удаляется. Остальные — только строкой в лог.
#  2. каталоги верхнего уровня MSBuildTemp?????? (17 символов), fake-cli-*, ccs_*, tmp.*.
# Не трогает: systemd-private-*, .X11-unix и любые чужие/свежие записи.
#
# Использование:
#   ccs-tmp-cleanup.sh [--dry-run] [--min-age-hours N] [--merged-age-days N]
# Переменные: CCS_REPO — основной репозиторий (по умолчанию корень, где лежит скрипт),
#             CCS_TMP — чистимый каталог (по умолчанию /tmp),
#             CCS_TASKS_FILE — трекер задач (по умолчанию /srv/ccs/data/tasks.json).

set -euo pipefail

REPO="${CCS_REPO:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
TMP="${CCS_TMP:-/tmp}"
TASKS_FILE="${CCS_TASKS_FILE:-/srv/ccs/data/tasks.json}"
DRY_RUN=0
MIN_AGE_H=24
MERGED_AGE_D=3

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY_RUN=1 ;;
    --min-age-hours) MIN_AGE_H="${2:?нужно число часов}"; shift ;;
    --merged-age-days) MERGED_AGE_D="${2:?нужно число суток}"; shift ;;
    -h|--help) sed -n '2,21p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
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
is_locked() { # $1 — путь worktree
  git -C "$REPO" worktree list --porcelain | awk -v p="$1" '$1=="worktree"{c=($2==p)} c&&$1=="locked"{f=1} END{exit !f}'
}

# Пути worktree незакрытых задач (Status != done). Нет файла или он не разбирается — правило 1б
# отключается целиком: без списка нельзя отличить брошенное дерево от дерева задачи в работе.
OPEN_TASK_PATHS=""; TASKS_OK=0
if [[ -r "$TASKS_FILE" ]] && OPEN_TASK_PATHS="$(jq -r '.[] | select(.Status != 2 and .Status != "done") | .WorktreePath // empty' "$TASKS_FILE" 2>/dev/null)"; then
  TASKS_OK=1
fi

bound_to_open_task() { # $1 — каталог; 0, если путь задачи равен ему или лежит внутри
  local d="${1%/}" p
  while IFS= read -r p; do
    p="${p%/}"; [[ -n "$p" ]] || continue
    [[ "$p" == "$d" || "$p" == "$d"/* ]] && return 0
  done <<<"$OPEN_TASK_PATHS"
  return 1
}

# Причина, по которой дерево ВНЕ /tmp снимать нельзя (пусто — можно). Порядок: от дешёвого к дорогому.
merged_wt_skip_reason() { # $1 — каталог worktree
  local wt="$1" ct age_d
  if is_locked "$wt"; then echo "locked"; return; fi
  if in_use "$wt"; then echo "живой процесс с cwd внутри"; return; fi
  if bound_to_open_task "$wt"; then echo "привязано к незакрытой задаче"; return; fi
  if ! git -C "$wt" merge-base --is-ancestor HEAD refs/heads/master 2>/dev/null; then echo "не влито в master"; return; fi
  if [[ -n "$(git -C "$wt" status --porcelain 2>/dev/null)" ]]; then echo "есть правки или неотслеживаемые файлы"; return; fi
  ct="$(git -C "$wt" log -1 --format=%ct HEAD 2>/dev/null || echo 0)"
  age_d=$(( ($(date +%s) - ct) / 86400 ))
  if (( age_d < MERGED_AGE_D )); then echo "последний коммит ${age_d} сут. назад (< ${MERGED_AGE_D})"; return; fi
  if recently_touched "$wt"; then echo "изменён менее ${MIN_AGE_H} ч назад"; return; fi
}

log "-- worktree основного репозитория (под $TMP — по возрасту, вне $TMP — только влитые):"
removed_wt=0; skipped_wt=0
if git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1; then
  mapfile -t WT_PATHS < <(git -C "$REPO" worktree list --porcelain | sed -n 's/^worktree //p')
  MAIN_WT="${WT_PATHS[0]}" # первая запись списка — основной checkout, его не трогаем никогда
  if [[ $TASKS_OK -eq 0 ]]; then
    log "  ВНИМАНИЕ: $TASKS_FILE недоступен или не разбирается — деревья вне $TMP не снимаю"
  fi
  for wt in "${WT_PATHS[@]}"; do
    [[ "$wt" != "$MAIN_WT" ]] || continue
    reason=""
    if [[ "$wt" == "$TMP"/* ]]; then
      if [[ ! -d "$wt" ]]; then reason="" # каталога нет — prune ниже подчистит запись
      elif is_locked "$wt"; then reason="locked"
      elif in_use "$wt"; then reason="живой процесс с cwd внутри"
      elif recently_touched "$wt"; then reason="изменён менее ${MIN_AGE_H} ч назад"
      elif [[ -n "$(git -C "$wt" status --porcelain -uno 2>/dev/null)" ]]; then reason="правки отслеженных файлов"
      elif ! git -C "$wt" symbolic-ref -q HEAD >/dev/null 2>&1 \
           && [[ -z "$(git -C "$wt" branch -a --contains HEAD 2>/dev/null)" ]]; then reason="HEAD не достижим из веток"
      fi
    else
      [[ -d "$wt" ]] || continue
      if [[ $TASKS_OK -eq 0 ]]; then reason="нет списка задач"; else reason="$(merged_wt_skip_reason "$wt")"; fi
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
