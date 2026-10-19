# Свежий worktree: подготовка к работе

Фичи и задачи исполнителей идут в отдельных `git worktree` (ветка + каталог
`<репо>-<имя>`). Дерево чистое: `frontend/node_modules` в .gitignore, npm-зависимости
и собранный `frontend/dist` — только в основном (primary) checkout.

## node_modules: symlink, не npm ci

Node-экстрактор кодеграфа (`frontend/scripts/codegraph-extractor.mjs`) грузит
`typescript` через `createRequire` и резолвит пакет вверх по дереву. Без
`frontend/node_modules` в worktree он его не находит, TS-снапшот пуст, и
`CodeGraph.Tests` падает (`Frontend_AvatarMenu_References_SegmentedControl_…`).

`npm ci` в worktree — медленный путь (lock-файла в ветке нет, установка с нуля).
Быстрый — symlink на node_modules основного дерева:

```bash
scripts/worktree-setup.sh [путь к worktree]   # без аргумента — текущий каталог
```

Скрипт идемпотентен: существующий корректный symlink не трогает, реальную папку
(`npm ci` уже прогоняли) не затирает. Путь основного дерева берёт из
`git worktree list` (первая запись — primary checkout), хардкода нет.

## Чек-лист нового worktree

1. `git worktree add <каталог> <ветка>` (или задача трекера с `worktreePath`).
2. `scripts/worktree-setup.sh <каталог>` — symlink `frontend/node_modules`.
3. Бэкенд: `cd backend && dotnet build ClaudeHomeServer.slnx`.
4. Фронт (только если трогается): `cd frontend && npm ci` — либо полагаться на
   symlink из п. 2, если достаточно `typescript`/экстрактора.

## Когда symlink не нужен

- Тесты, не поднимающие TS-граф (большинство `*.Tests`) — node_modules не нужен.
- `CodeGraph.Tests` — symlink обязателен (тест `…Through_Index_Reexport` —
  «Node», в CI Linux без npm-зависимостей он исключён фильтром `Category!=Node`).
