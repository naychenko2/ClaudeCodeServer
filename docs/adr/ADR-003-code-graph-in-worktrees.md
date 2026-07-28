# ADR-003: Code Graph в чатах с отдельным worktree

**Статус:** Черновик (на согласовании)
**Дата:** 2026-07-28
**Принимающие решение:** Андрей (владелец продукта), Александр (архитектор)
**Реализация:** backend — Денис
**Связанные артефакты:** [ADR-002](ADR-002-code-graph-god-nodes-in-prompt.md),
[CodeGraphService](../../backend/ClaudeHomeServer/Services/CodeGraph/CodeGraphService.cs),
[GraphPersistence](../../backend/ClaudeHomeServer/Services/CodeGraph/GraphPersistence.cs),
[CodeGraphController](../../backend/ClaudeHomeServer/Controllers/CodeGraphController.cs),
[FileWatcherService](../../backend/ClaudeHomeServer/Services/FileWatcherService.cs),
[mcp/codegraph-server](../../mcp/codegraph-server/index.js)

## Контекст

Граф кода доходит до агента двумя каналами: пассивный slice top-10 god-узлов в системном
промпте (ADR-002) и активные MCP-инструменты `codegraph_find` / `codegraph_neighbors` /
`codegraph_hubs` (коммит `ea2bafd3`). Оба канала ключуются путём: граф лежит в
`data/code-graphs/{SHA256(NormalizePath(rootPath))}/graph.json`.

Пользователь регулярно работает в чатах с отдельным git worktree (`Session.WorktreePath`).
Такой чат ведёт разработку в СВОЁМ дереве, а граф видит от ОСНОВНОГО — со всеми вытекающими:
новые типы не находятся, удалённые всё ещё «есть», изменённые зависимости показываются
устаревшими. Чем активнее правки, тем сильнее граф дезинформирует.

### Что выяснено исследованием кода

**1. Worktree чата лежит ВНЕ дерева проекта.** `SessionManager.SetWorktreeAsync:2566` создаёт
его в `{home}/.worktrees/{projSlug}/{branch}` — комментарий на 2559 прямо фиксирует «вне
дерева главной репы». Это НЕ `.claude/worktrees/` внутри проекта.

> Важно не спутать: `.claude/worktrees/` — это worktree, создаваемые скиллом `worktree` /
> oh-my-claudecode ВНУТРИ проекта. Именно они давали 58 мусорных узлов, вычищенных
> коммитом `023b828c` (фильтр `IgnoredDirectories` в `InvalidateIncremental`). К
> `Session.WorktreePath` тот фикс отношения не имеет.

**2. Ни один из трёх контуров обновления графа не работает для worktree:**

| Контур | Основное дерево | Worktree чата |
|---|---|---|
| FileWatcher → инкремент (дебаунс 15с) | работает | **не работает** — `FileSystemWatcher` слушает `Project.RootPath`, worktree физически вне поддерева |
| `InvalidateIncremental` | работает | **не работает** — `IsInsideRoot(rootPath, f)` отсекает пути вне корня |
| `GET → isStale → StartRebuildIfIdle` | работает | работал бы, но graph.json для worktree никогда не создаётся |

**3. Каналы графа расходятся по путям.** `BuildCodeGraphProvider` (slice промпта) получает
`rootPath` уже ПОСЛЕ `EffectiveRoot` — то есть путь worktree, где графа нет, и молча
возвращает `null` (slice в промпт не попадает). `BuildCodeGraphContext` (MCP) передаёт
`session.ProjectId`, контроллер резолвит его в `project.RootPath` — то есть инструменты
смотрят в основное дерево. Один и тот же чат получает данные из разных мест.

**4. Нагрузка от watcher'ов ничтожна.** `FileSystemWatcher` — один OS-handle
(`ReadDirectoryChangesW` / inotify), в простое CPU ≈ 0. Десяток worktree-чатов — десяток
handle'ов. Реальный риск не в нагрузке, а в **дефолтном буфере 8 КБ**: массовая операция в
worktree (`git checkout`, `npm install`) переполняет его, срабатывает `Error` →
`RecreateWatcher`, часть событий теряется. Страховка от этого уже есть — `isStale` по mtime.

## Решение

Строить для worktree **отдельный полноценный граф** с тем же жизненным циклом, что у
основного. Изоляция по пути уже работает: ключ хранения — хеш от `rootPath`, поэтому два
графа не пересекаются автоматически.

Отвергнутые альтернативы:

- **Оверлей поверх основного графа (дельта по `git diff`).** Не даёт выигрыша: чтобы извлечь
  межфайловые рёбра (`Calls`, `Implements` — 1227 из 2809), Roslyn нужна полная компиляция,
  а она строится из всех `.cs` сразу. Синтаксического парсинга дельты хватило бы только на
  рёбра внутри файла — огрызок, который хуже отсутствия. Итого: та же цена, что у полной
  сборки, плюс зависимость от `git` в рантайме и гонка «ветка ушла вперёд, дельта протухла».
- **Оставить как есть, пометив ограничение в промпте.** Дёшево, но превращает граф в
  справочник «где что лежало» — для активной разработки в worktree это полурабочий инструмент.

### Уровень 1 — граф worktree доступен (MVP)

1. **`CodeGraphController`** — опциональный query-параметр `?rootPath=` на всех эндпоинтах
   (`GET /`, `/find`, `/neighbors`, `/hubs`, `POST /build`). Без параметра — поведение
   идентично текущему. С параметром: путь принимается, только если существует на диске **и**
   принадлежит владельцу проекта — то есть либо равен `project.RootPath`, либо это worktree
   одной из сессий этого проекта (`sessions.GetByProject(id)` → `WorktreePath`). Произвольный
   путь с диска не принимается никогда: иначе получаем чтение чужих деревьев по HTTP.

2. **`mcp/codegraph-server/index.js`** — новый env `CODEGRAPH_ROOT_PATH`. Если задан,
   добавляется `&rootPath=…` ко всем трём запросам. Состав `tools/list` не меняется —
   инвариант стабильности сохранён.

3. **`ClaudeSession.BuildTurnMcpConfig`** — передавать `CodeGraphMcpContext.RootPath`
   (расширить record) в env. Значение — `EffectiveRoot` сессии, то есть worktree или корень
   проекта.

4. **`SessionManager.BuildCodeGraphContext`** — принимать `rootPath` и класть в контекст.
   Вызов на 1636 и 2271 уже имеет `rootPath` после `EffectiveRoot` — передать его.

5. **`CodeGraphPromptProvider`** — починить молчаливый провал slice: если для worktree-пути
   графа ещё нет, отдавать slice основного дерева с пометкой, что он от главной ветки.
   Пустой промпт хуже приблизительного.

6. **Первичная сборка.** `QueryAsync`/`Get` уже зовут `StartRebuildIfIdle(rootPath)` при
   отсутствии снимка — для worktree это сработает само, надо лишь убедиться, что путь
   доезжает. Первый запрос в новом worktree отдаёт 404 + `X-CodeGraph-Building`, через
   ~60 с граф готов.

### Уровень 2 — автообновление графа worktree

7. **`FileWatcherService`** — метод `WatchPath(string key, string rootPath)` /
   `UnwatchPath(string key)` для произвольного пути, не привязанного к `projectId`. Внутри —
   тот же `Entry`, тот же дебаунс 400 мс, тот же `Flush` → `NotifyCodeGraph(entry.Root, …)`.
   Ключ — `worktree:{sessionId}`. Ref-count не нужен: путь worktree уникален по ветке, а
   ветка уникализируется при создании (`SetWorktreeAsync:2555`), поэтому связь 1:1.
   Для этих watcher'ов поднять `InternalBufferSize` до 64 КБ — защита от потери событий на
   `git checkout` / `npm install`.

8. **Ленивое включение.** Watcher создаётся НЕ при создании worktree, а при первом
   обращении к его графу (`CodeGraphService.StartRebuildIfIdle` / `GetSnapshotAsync` с
   worktree-путём). Чат в worktree, где графом не пользуются, не платит ничего.

9. **Снятие.** `UnwatchPath` при удалении сессии (`DeleteAsync`, там же где
   `WorktreeRemoveAsync`) и при выключении worktree (`SetWorktreeAsync`, ветка `else`).
   Плюс автоснятие по бездействию: если по графу worktree не было запросов N минут (предлагаю
   30), watcher снимается и возвращается лениво при следующем обращении — это не даёт
   накапливать handle'ы от забытых чатов.

### Хранение и уборка

**Решено: `data/code-graphs/{hash}` + явная уборка** при выключении/удалении worktree.
Изоляция уже обеспечена ключом (хеш от нормализованного пути), поэтому `GraphPersistence`
не меняется вовсе; добавляется только вызов `Invalidate(worktreePath)` в двух местах, где
worktree удаляется: `SetWorktreeAsync` (ветка `else`) и `DeleteAsync` (рядом с
`WorktreeRemoveAsync`).

Исходная идея владельца — хранить граф внутри worktree, чтобы он уходил вместе с деревом —
отклонена из-за побочного эффекта: файл в корне worktree окажется untracked, и гейт
«в отдельном дереве есть несохранённые изменения» (`SetWorktreeAsync:2588-2593`) начнёт
срабатывать на него при попытке выключить worktree. Обходные пути (служебная папка
`{main}/.git/worktrees/{name}/`, либо запись в `.git/info/exclude`) работают, но связывают
`GraphPersistence` с внутренней структурой git ради экономии 200 КБ.

Остаточный риск: worktree, удалённый руками мимо CCS, оставит сироту ~1 МБ. Принят
осознанно — сборщика сирот не делаем (та же логика, что у транскриптов CLI в CLAUDE.md).

## Последствия

- Каждый worktree-чат получает честный граф своего дерева; цена — первичная сборка ~60 с и
  ~1 МБ на диске.
- Основной граф не затрагивается: без `?rootPath=` всё поведение прежнее.
- Появляется первый watcher, не привязанный к SignalR-коннекту, — он живёт по правилам
  графа, а не UI. Это осознанное расширение контракта `FileWatcherService`.
- `?rootPath=` — новая поверхность атаки, закрывается белым списком (корень проекта либо
  worktree его сессий), а не проверкой существования пути.

## Проверка

- `dotnet build`; `dotnet test --filter "FullyQualifiedName~CodeGraph|~Mcp"` — включая
  `McpToolsetStabilityTests`.
- Новый тест: `?rootPath=` с чужим путём → 403/400; с worktree своей сессии → 200.
- Новый тест: `InvalidateIncremental` по worktree-пути доходит до `UpdateAsync`.
- Ручной сценарий: чат с worktree → `codegraph_find` нового типа, созданного только в
  worktree → находится; тот же запрос из чата основного дерева → не находится.
- Ручной сценарий: правка `.cs` в worktree → через ≤16 с `codegraph_neighbors` отражает
  новую связь.
