#!/usr/bin/env bash
#
# worktree-setup.sh — подготовка свежего git worktree к разработке.
#
# Зачем: `frontend/node_modules` в worktree пустая — каталог в .gitignore,
# npm-зависимости лежат только в основном дереве. Node-экстрактор кодеграфа
# (frontend/scripts/codegraph-extractor.mjs) грузит `typescript` через
# createRequire и резолвит его вверх по дереву: без node_modules в worktree
# он не находит пакет, снапшот пуст, и CodeGraph.Tests падает
# (Frontend_AvatarMenu_References_SegmentedControl_Through_Index_Reexport).
#
# Что делает: symlink `frontend/node_modules` -> node_modules основного дерева
# (первая запись `git worktree list` — primary checkout). Идемпотентен:
# существующий корректный symlink не трогает, реальную папку (npm ci в
# worktree) не затирает.
#
# Использование: scripts/worktree-setup.sh [путь к worktree]
#   Без аргумента — текущий каталог. Путь к основному дереву не хардкодится:
#   берётся из `git worktree list`, поэтому скрипт переносим между машинами.

set -euo pipefail

WT="${1:-.}"
cd "$WT"

# Основной (primary) worktree — первая запись `git worktree list --porcelain`.
MAIN_TREE="$(git worktree list --porcelain | awk '/^worktree /{print $2; exit}')"

if [ -z "$MAIN_TREE" ]; then
  echo "ошибка: не определён основной worktree (git worktree list пуст)" >&2
  exit 1
fi

SRC="$MAIN_TREE/frontend/node_modules"
DST="$WT/frontend/node_modules"

if [ ! -d "$SRC" ]; then
  echo "ошибка: $SRC не существует — сначала npm ci в основном дереве" >&2
  exit 1
fi

if [ -L "$DST" ]; then
  if [ "$(readlink "$DST")" = "$SRC" ]; then
    echo "ок: $DST -> $SRC (уже стоит)"
    exit 0
  fi
  # Symlink на чужое — заменяем на основной путь.
  rm "$DST"
elif [ -d "$DST" ]; then
  # Реальная папка: npm ci уже прогоняли в этом worktree — не затираем.
  echo "ок: $DST — реальная папка, symlink не ставим"
  exit 0
fi

ln -s "$SRC" "$DST"
echo "ок: $DST -> $SRC"
