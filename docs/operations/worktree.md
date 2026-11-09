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

## Диск: артефакты worktree, уборка и тревога

Инцидент 25–26.10.2026: корень дошёл до 99 %. 69–120 worktree × ~15 ГБ `bin/obj` (352 ГБ) плюс
~45 ГБ временных каталогов тестов в `/tmp`. Страховок три, в порядке силы:

1. **Вынос артефактов (opt-in).** `CCS_BUILD_ROOT=/home/an/.cache/ccs-build` в окружении
   сборки → `backend/Directory.Build.props` кладёт `bin`/`obj` каждого проекта в
   `$CCS_BUILD_ROOT/<имя-каталога-worktree>/<проект>/{bin,obj}`, раскладка внутри прежняя.
   Дерево остаётся лёгким, артефакты удаляются одним `rm -rf` по имени. Каталог должен быть
   на отдельном диске: `/home/an/llm/kvdisk` принадлежит root, поэтому один раз руками
   `sudo mkdir /home/an/llm/kvdisk/ccs-build && sudo chown an: /home/an/llm/kvdisk/ccs-build &&
   ln -s /home/an/llm/kvdisk/ccs-build /home/an/.cache/ccs-build`.
   **По умолчанию выключен**: ~49 тестов и код сервера (`Program.cs`, `ModuleLoader`,
   `DockerPathMapper`…) ищут корень репозитория подъёмом от `AppContext.BaseDirectory`
   (`bin/Debug/net10.0` → `.git`), а при вынесенном `bin` подъём корня не найдёт. Включать
   глобально можно только после отвязки этого поиска; стенды `ccs-qa-*` с путём
   `…/bin/Debug/net10.0/ClaudeHomeServer.dll` при включении надо переписать на новый путь.
   Прогрев свежего worktree (`WorktreeBuildWarmup`) проверяет `obj/` в дереве — при выносе
   он будет считать дерево «не собиравшимся» и греть повторно.
2. **Снос при закрытии.** `GitService.WorktreeRemoveAsync` после `git worktree remove` стирает
   и вынесенный каталог `$CCS_BUILD_ROOT/<имя>` (`WorktreeBuildArtifacts.CleanExternal`).
3. **Ночная уборка** (`WorktreeCleanupService`, секция `WorktreeCleanup`, включена по
   умолчанию). Раз в сутки в окне 02–06: для worktree без процессов (скан `/proc`: cwd или путь
   в командной строке) и без активности 24 ч (`HEAD`/`index` gitdir, времена каталогов
   артефактов) удаляются `backend/*/{bin,obj}`, `frontend/dist`, свой `frontend/node_modules`
   (симлинк на основное дерево не трогается) и вынесенный каталог. Дерево, у которого ветка
   влита в HEAD основного, статус чистый, нет открытых задач с этим `worktreePath` и нет
   блокировки, снимается `git worktree remove` без `--force`. Основное дерево не трогается
   никогда. Затем `git worktree prune` и чистка `/tmp` по префиксам старше 2 ч (сокеты
   `clr-debug-pipe-*`/`dotnet-diagnostic-*` — только осиротевшие). Итог — строка в
   `data/worktree-cleanup.log` и в лог сервера. Каждые 30 мин проверяется свободное место
   корня: меньше `MinFreeGb` (100) — уведомление админам с кулдауном 12 ч.

Остаточный риск: процесс другого пользователя с нечитаемым cwd виден только по командной строке
(число таких процессов пишется в журнал). Ретенция бэкапов: «дневные» — по одному (самому
свежему) архиву на каждый из последних 7 дней, а не 7 последних файлов (6 архивов в сутки от
выкаток вытесняли историю). Бэкапы на старте сервер не делает: лишние снимки дают `deploy80.ps1`
и трей (`exe --backup`). Перенос старых архивов на `/mnt/users/ccs-backups` — задаётся
`Backup:Path` в `appsettings.Local.json`.
