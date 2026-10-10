#!/usr/bin/env bash
# Перенос ручных LoRA в пространство владельца: models/loras/<имя> -> models/loras/ccs/<ownerKey>/<имя>.
# Нужен после выкатки фильтра видимости (LoraNamespace.VisibleTo): LoRA вне ccs/<ownerKey>/ не видны и не подключаются.
#
# Запуск с сервера (скрипт читается из stdin, WSL на gpuhost):
#   ssh -o BatchMode=yes gpuhost 'wsl -e bash -s -- --owner-key <32 hex>' < migrate-manual-loras.sh        # dry-run
#   ssh -o BatchMode=yes gpuhost 'wsl -e bash -s -- --owner-key <32 hex> --apply' < migrate-manual-loras.sh
#   ssh -o BatchMode=yes gpuhost 'wsl -e bash -s -- --rollback <журнал>' < migrate-manual-loras.sh
#
# Только перенос (mv), ничего не удаляется и не перезаписывается; существующая цель = пропуск.
set -euo pipefail

LORAS_DIR="$HOME/ComfyUI/models/loras"   # корень «photo» в extra_model_paths.yaml: там лежат ручные LoRA
OWNER_KEY=""
MODE="dry-run"
ROLLBACK=""
ONLY=()

usage() {
  cat <<'U'
Параметры:
  --owner-key <32 hex>   ключ владельца (LoraNamespace.OwnerKey: Guid без дефисов, нижний регистр)
  --dry-run              только план (по умолчанию)
  --apply                выполнить перенос и записать журнал отката
  --only <имя>           переносить только этот верхний элемент (можно повторять); по умолчанию — все
  --loras-dir <путь>     каталог loras (по умолчанию ~/ComfyUI/models/loras)
  --rollback <журнал>    обратный перенос по журналу (mv назад; занятое место — пропуск)
U
}

while [ $# -gt 0 ]; do
  case "$1" in
    --owner-key) OWNER_KEY="${2:-}"; shift 2 ;;
    --dry-run) MODE="dry-run"; shift ;;
    --apply) MODE="apply"; shift ;;
    --only) ONLY+=("${2:-}"); shift 2 ;;
    --loras-dir) LORAS_DIR="${2:-}"; shift 2 ;;
    --rollback) ROLLBACK="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Неизвестный параметр: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[ -d "$LORAS_DIR" ] || { echo "Нет каталога $LORAS_DIR" >&2; exit 2; }

if [ -n "$ROLLBACK" ]; then
  [ -f "$ROLLBACK" ] || { echo "Нет журнала $ROLLBACK" >&2; exit 2; }
  while IFS=$'\t' read -r from to; do
    [ -n "$to" ] || continue
    if [ ! -e "$to" ]; then echo "ПРОПУСК (нет источника отката): $to"; continue; fi
    if [ -e "$from" ]; then echo "ПРОПУСК (место занято): $from"; continue; fi
    mv -n -- "$to" "$from" && echo "ОТКАТ: $to -> $from"
  done < "$ROLLBACK"
  exit 0
fi

[[ "$OWNER_KEY" =~ ^[0-9a-f]{32}$ ]] || { echo "--owner-key обязателен: ровно 32 символа [0-9a-f]" >&2; exit 2; }

TARGET_ROOT="$LORAS_DIR/ccs/$OWNER_KEY"
STAMP="$(date +%Y%m%d-%H%M%S)"
JOURNAL="$LORAS_DIR/.ccs-migration/journal-$OWNER_KEY-$STAMP.tsv"
# Сегмент имени допустим для LoraNamespace.IsSegmentChar: A-Za-z0-9 . _ -
VALID='^[A-Za-z0-9._-]+$'

echo "Режим: $MODE; владелец: $OWNER_KEY"
echo "Источник: $LORAS_DIR"
echo "Цель:     $TARGET_ROOT"
echo "--- план ---"

planned=0; skipped=0
shopt -s nullglob dotglob
for src in "$LORAS_DIR"/*; do
  name="$(basename "$src")"
  case "$name" in ccs|.*|put_loras_here) continue ;; esac
  if [ ${#ONLY[@]} -gt 0 ]; then
    hit=0; for o in "${ONLY[@]}"; do [ "$o" = "$name" ] && hit=1; done
    [ $hit -eq 1 ] || { echo "ПРОПУСК (не выбран --only): $name"; skipped=$((skipped+1)); continue; }
  fi
  if ! [[ "$name" =~ $VALID ]]; then
    echo "ПРОПУСК (имя вне [A-Za-z0-9._-], после переноса было бы невидимо): $name"; skipped=$((skipped+1)); continue
  fi
  dest="$TARGET_ROOT/$name"
  if [ -e "$dest" ]; then
    echo "ПРОПУСК (цель уже есть): $name -> $dest"; skipped=$((skipped+1)); continue
  fi
  if [ -d "$src" ]; then
    bad="$(find "$src" -mindepth 1 -printf '%P\n' | grep -vE '^[A-Za-z0-9._/-]+$' || true)"
    if [ -n "$bad" ]; then
      echo "ПРОПУСК (во вложенных именах недопустимые символы): $name"; echo "$bad" | sed 's/^/    /'; skipped=$((skipped+1)); continue
    fi
    n="$(find "$src" -type f | wc -l)"
    echo "ПЕРЕНОС: $src/ ($n файл.) -> $dest/"
  else
    echo "ПЕРЕНОС: $src -> $dest"
  fi
  planned=$((planned+1))
  if [ "$MODE" = "apply" ]; then
    mkdir -p "$TARGET_ROOT" "$(dirname "$JOURNAL")"
    mv -n -- "$src" "$dest"
    printf '%s\t%s\n' "$src" "$dest" >> "$JOURNAL"
  fi
done

echo "--- итог: к переносу $planned, пропущено $skipped ---"
if [ "$MODE" = "apply" ]; then
  [ -f "$JOURNAL" ] && echo "Журнал отката: $JOURNAL  (откат: --rollback <журнал>)"
else
  echo "Ничего не изменено (dry-run). Для применения добавь --apply."
fi
