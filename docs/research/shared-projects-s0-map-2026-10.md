# S0-3 · Карта: разметка вызовов и инвентаризация ключей — «Общие проекты»

Дата: 2026-10-28. Автор: Тимур. Ветка `feat/shared-projects`, снимок `3c877a2c0` (дерево чистое; master, от которого она ветвилась, отличается только документами фичи). Шаг S0-3 плана [shared-projects-plan-2026-10.md](shared-projects-plan-2026-10.md) §5, разрез — [shared-projects-cut-2026-10.md](shared-projects-cut-2026-10.md) §6.6 и И.4. Кода в шаге нет — документ.

Номера строк — на снимке; после шагов S0-4…S0-7 они поедут, поэтому живым источником остаются команды из раздела «Как считал». Тесты, моки и `obj/` в таблицы не входят (кроме явно названных).

## 0. Что нашёл сверх постановки (читать первым)

1. **Число 41 у `GetByOwner` не воспроизводится.** Метод с таким именем объявлен у 8 типов (проекты, персоны, задачи, сферы, MCP-реестр, MCP-статусы, устройства, вочдоги), строк с `GetByOwner(` во всём дереве — 167. Вызовов именно `ProjectManager`/`IProjectManager` в продуктовом коде — **24** (таблица 1А), плюс 3 объявления/форвардер. Откуда взялось 41 в разрезе §6.6, установить не удалось (похоже на грубый grep по слову `projects`, захвативший тесты и соседние типы). Разметка ниже сделана по типу получателя, а не по имени метода.
2. **Число 98 у `GetOwned` — тоже не 98.** `SessionManager.GetOwned` в продуктовом коде зовут **83** раз (таблица 1Б); ещё 10 вызовов `GetOwned` (5 в `SpheresController`, 5 внутри `SphereManager`) принадлежат `SphereManager` — это другой метод, он в таблицу не входит (сферы остаются личными, §2.5 разреза).
3. **У плана нет шага-переводчика для 9 файлов MCP-слоя с проверками владения проекта.** S0-7 берёт Tasks/Memory/McpCallContextSeams (и Notes, где сравнений нет, см. п. 4), `WorkspaceToolset` уходит в S2a-3, а `CodeGraphToolset`, `DifyToolset`, `LocalMediaToolset`, `ProjectRunCaller`, `LocalMediaProjectAccess`, `ImageEditorToolset`, `AudioEditorToolset`, `VideoEditorToolset` в таблице 2 есть, а шага нет (9 строк-файлов, 9 сравнений). Кроме того, `ArchitectureToolset` (`ClaudeHomeServer.Architecture/Services/Architecture/`, `ArchitectureToolset.cs:281`) лежит **вне путей охвата сторожа** (нет сегмента `/Mcp/`), так что S0-G его не увидит. Нужен либо S0-7b, либо явное «вне S0» с причиной, и расширение охвата S0-G на `Architecture`. Это вопрос В1 к А1.
4. **`NotesToolset` в S0-7 не содержит ни одного сравнения `OwnerId`** — только `GetOwned` (таблица 1Б). Его строк в allow-list `S0-G` не будет, критерий 1 шага S0-7 для него пуст; перевод там — это ключ `GetOwned`/`personas.Get`, а не сравнение.
5. **Ловушка ключа в MCP повторяет один и тот же шаблон.** Во всех MCP-тулсетах `context.OwnerId` — это `sub` вызывающего (ADR-012), а ключ хранилищ и персон дальше берётся из него же (`personas.Get(pid, context.OwnerId)`, `bindings.*Enabled(context.OwnerId, …)`). Если участник начнёт ход в общем проекте, `context.OwnerId` станет его собственным id, и все эти обращения полезут в его персоны и привязки, а не арендатора. Подробно — раздел 1Б, колонка «Ключ дальше».

## 1А. `ProjectManager.GetByOwner` — разметка каждого вызова

«Видимость» — результат попадёт участнику общего проекта (станет `GetAccessible(userId)`). «Владение» — арендаторская операция: идёт «по хозяину данных» (каскады, ключ датасетов/файлов, фон по владельцу), остаётся `GetByOwner`.

| # | Файл:строка | Вердикт | Что делает вызов |
|---|---|---|---|
| 1 | `ClaudeHomeServer.Knowledge/UserKnowledgeCascade.cs:61` | владение | каскад удаления пользователя: чистит датасеты его проектов |
| 2 | `ClaudeHomeServer.Knowledge/ProjectKnowledgeSyncService.cs:277` | владение | резервная чистка записей БЗ осиротевших проектов владельца; датасеты ключуются `{username}:` |
| 3 | `ClaudeHomeServer.Notes/Services/Notes/NotesService.cs:73` | видимость | источники заметок: `notes/` каждого проекта в списке (участник обязан видеть заметки общего проекта) |
| 4 | `ClaudeHomeServer.Backgrounds/Services/Backgrounds/ProjectBackgroundBackfill.cs:70` | владение | фоновая догенерация фона по владельцу (платит владелец) |
| 5 | `ClaudeHomeServer/WebDav/WebDavHandler.cs:127` | видимость | поиск проекта по имени папки в WebDAV пользователя |
| 6 | `ClaudeHomeServer/WebDav/WebDavHandler.cs:327` | видимость | листинг корня WebDAV: проекты с файлами на сервере |
| 7 | `ClaudeHomeServer/Controllers/ProjectsController.cs:131` | видимость | `GET /api/projects` — список проектов |
| 8 | `ClaudeHomeServer/Controllers/PersonasController.cs:991` | видимость | каталог целей для правил автоматизации персон (проекты) |
| 9 | `ClaudeHomeServer/Controllers/PersonasController.cs:1155` | видимость | выпадающий список проектов (`type=project`) |
| 10 | `ClaudeHomeServer/Services/PersonaAgentFileSync.cs:236` | владение | синк `.claude/agents` по всем проектам пользователя; ключ папки `{owner}/{dirKey}` |
| 11 | `ClaudeHomeServer/Services/PersonaAgentFileSync.cs:391` | владение | то же: перепроверка набора файлов агентов владельца |
| 12 | `ClaudeHomeServer/Services/PersonaAgentFileSync.cs:410` | владение | то же |
| 13 | `ClaudeHomeServer/Services/PersonaAgentFileSync.cs:450` | владение | то же (по `persona.OwnerId`) |
| 14 | `ClaudeHomeServer/Services/ChatArchiveService.cs:107` | владение | фоновый автоархив чатов по владельцу |
| 15 | `ClaudeHomeServer/Controllers/HomeController.cs:29` | видимость | «Домой»: имена проектов для сводки |
| 16 | `ClaudeHomeServer/Controllers/TasksController.cs:1111` | видимость | проект эпика при чтении состава фичи: «принадлежит текущему владельцу, fail-closed» — станет «доступен» |
| 17 | `ClaudeHomeServer/Services/DailyBriefingService.cs:252` | видимость | git-сводка проектов в дайджесте пользователя |
| 18 | `ClaudeHomeServer/Services/PersonaBindingsService.cs:662` | владение | каталог датасетов Dify для привязок персон владельца (датасеты `{username}:`) |
| 19 | `ClaudeHomeServer/Services/ChatSubjectSweeper.cs:96` | владение | фоновая чистка тем чатов по владельцу |
| 20 | `ClaudeHomeServer/Services/PersonasCrudService.cs:280` | владение | проекты, где персона — дефолтная: запрет удаления (персоны ключуются OwnerId) |
| 21 | `ClaudeHomeServer/Services/PersonasCrudService.cs:822` | владение | каталог целей для правил персоны (проекты владельца персоны) |
| 22 | `ClaudeHomeServer/Services/SphereManager.cs:57` | владение | `ProjectsOf(sphere)` — сферы личные (разрез §2.5), остаётся |
| 23 | `ClaudeHomeServer/Services/Composition/ProjectManagerAdapter.cs:33` | форвардер | шов `IProjectManager` → `ProjectManager`; следует за решением по самому методу |
| 24 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:408` | видимость | MCP `workspace`: список проектов |

Итого вызовов: **24** = видимость 10, владение 13, форвардер 1. Объявления: `Services/ProjectManager.cs:70`, `Core/Services/IProjectManager.cs:23`, `DeviceAgent/Composition/AgentHub.cs:25` (локальный каталог агента устройства — к общим проектам не относится).
Вызовы в тестах (в таблицу не входят): `ProjectManagerTests.cs:130-132`, `ProjectManagerLocalProjectTests.cs:70`, `ProjectManagerAdapterTests.cs:53`, `ProjectManagerAdapterMutationCheckTests.cs:27`.

**Замечание к вердикту «владение».** `PersonaAgentFileSync` и `PersonasCrudService:822` ходят «по проектам владельца персоны». Когда у общего проекта появятся проектные персоны участников, граница «чья персона» станет вопросом (разрез §2.4); пока вердикт «владение» верный и безопасный (участник их не увидит — fail-closed).

## 1Б. `SessionManager.GetOwned` — разметка и ключ вызывающего

Колонка «Ключ дальше» отвечает на вопрос ловушки §6.6 п. 2: каким идентификатором вызывающий после `GetOwned` лезет в хранилища и в `SessionManager`. `UserId` / `context.OwnerId` — это id вызывающего (`sub`); `ownerId` / `userId` в сервисах — то, что принёс вызывающий (в большинстве мест это тоже `sub`). **«ЛОВУШКА»** — после доступа к чужому (через участие) чату дальше используется ключ вызывающего там, где нужен арендатор (`SessionManager.ResolveOwnerId(s)`).

| # | Файл:строка | Вердикт | Ключ дальше (после `GetOwned`) |
|---|---|---|---|
| 1 | `ClaudeHomeServer.ImageEditor/Mcp/ImageEditorToolset.cs:902` | видимость | `context.OwnerId` → проект `found.OwnerId != context.OwnerId` (таблица 2); нити/пути редактора ключуются `{owner}`: ЛОВУШКА |
| 2 | `ClaudeHomeServer.Notes/Services/Mcp/NotesToolset.cs:391` | видимость | `personas.Get(pid, context.OwnerId)`, `bindings.EffectiveToolEnabled(context.OwnerId, …)`, `broadcaster.ToOwner(ownerId)`: ЛОВУШКА (персона и привязки — арендатора) |
| 3 | `ClaudeHomeServer.Plugins/Mcp/PluginsToolset.cs:296` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 4 | `ClaudeHomeServer.AudioEditor/Mcp/AudioEditorToolset.cs:710` | видимость | `context.OwnerId` → проект `found.OwnerId != context.OwnerId` (таблица 2); нити/пути редактора ключуются `{owner}`: ЛОВУШКА |
| 5 | `ClaudeHomeServer.Architecture/Services/Architecture/ArchitectureToolset.cs:269` | видимость | `found.OwnerId != context.OwnerId`; `personas.Get(pid, context.OwnerId)`, `toolGate.IsServerToolEnabled(context.OwnerId…)`: ЛОВУШКА |
| 6 | `ClaudeHomeServer/Controllers/GitController.cs:29` | видимость | `s.ProjectId`/путь проекта; ключ хранилищ не нужен |
| 7 | `ClaudeHomeServer/Controllers/OnboardingController.cs:65` | владение | `UserId` → `users.*`, `personas.Get(aid, UserId)`, `LockFor("apply:"+UserId)` — онбординг самого пользователя, чат вне проекта; остаётся |
| 8 | `ClaudeHomeServer/Controllers/OnboardingController.cs:107` | видимость | `users.GetById(UserId)`, `personas.Get(defaultId, UserId)` — персона-ассистент вызывающего; для проектного онбординга ключ ассистента остаётся личным (не ловушка) |
| 9 | `ClaudeHomeServer/Controllers/OnboardingController.cs:157` | владение | `UserId` → `users.*`, `personas.Get(aid, UserId)`, `LockFor("apply:"+UserId)` — онбординг самого пользователя, чат вне проекта; остаётся |
| 10 | `ClaudeHomeServer/Controllers/OnboardingController.cs:198` | владение | `UserId` → `users.*`, `personas.Get(aid, UserId)`, `LockFor("apply:"+UserId)` — онбординг самого пользователя, чат вне проекта; остаётся |
| 11 | `ClaudeHomeServer/Controllers/McpSessionContextController.cs:37` | видимость | `resolver.Resolve(session, UserId)` — разбор MCP-контекста чата по ключу вызывающего: ЛОВУШКА (MCP-серверы арендатора — §4.3) |
| 12 | `ClaudeHomeServer/Controllers/SessionMessagesController.cs:28` | видимость | вспомогательный `OwnedSession`; дальше действия с `UserId` в теле контроллера — проверить |
| 13 | `ClaudeHomeServer/Controllers/ChatsController.cs:119` | видимость | `UserId` → читает чат; проверить, что дальше берёт `ResolveOwnerId` |
| 14 | `ClaudeHomeServer/Controllers/ChatsController.cs:222` | видимость | `DropWindow1MAsync(id, UserId)` — внутри снова `GetOwned(id, UserId)`: ЛОВУШКА (ключ вызывающего) |
| 15 | `ClaudeHomeServer/Controllers/ChatsController.cs:261` | видимость | `SetArchivedAsync(id, UserId, …)`: ЛОВУШКА (ключ вызывающего) |
| 16 | `ClaudeHomeServer/Controllers/ChatsController.cs:279` | видимость | `digest.BuildDigestAsync(UserId, id)` → `ChatDigestService` берёт `ResolveOwnerId(session)` сам (65) — безопасно |
| 17 | `ClaudeHomeServer/Controllers/ChatsController.cs:311` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 18 | `ClaudeHomeServer/Controllers/ChatsController.cs:332` | видимость | `StartTeamImplement…(…, UserId, …)`: ЛОВУШКА (ключ вызывающего в командном режиме) |
| 19 | `ClaudeHomeServer/Controllers/ChatsController.cs:360` | видимость | `StopTeamImplementAsync(id, UserId)`: ЛОВУШКА |
| 20 | `ClaudeHomeServer/Controllers/ChatsController.cs:370` | видимость | `SetTeamImplementAutoAsync(…, UserId)`: ЛОВУШКА |
| 21 | `ClaudeHomeServer/Controllers/ChatsController.cs:387` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 22 | `ClaudeHomeServer/Controllers/ChatsController.cs:421` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 23 | `ClaudeHomeServer/Controllers/ChatsController.cs:439` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 24 | `ClaudeHomeServer/Controllers/ChatsController.cs:467` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 25 | `ClaudeHomeServer/Controllers/ChatsController.cs:493` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 26 | `ClaudeHomeServer/Controllers/ChatsController.cs:509` | видимость | только проверка доступа и `session`-поля, в хранилища не ходит (проверить тело при переводе в S0-4) |
| 27 | `ClaudeHomeServer/Controllers/ChatsController.cs:684` | видимость | `sessions.GetChatRoot(id, UserId)` — ключ вызывающего: ЛОВУШКА |
| 28 | `ClaudeHomeServer/Controllers/BoardController.cs:20` | видимость | вспомогательный `OwnedSession`; доска чата, ключ — сессия |
| 29 | `ClaudeHomeServer/Controllers/MyWallController.cs:79` | видимость | `ownerId` из sub; «Моя стена» — личная выборка, дальше `s` читается |
| 30 | `ClaudeHomeServer/Controllers/ChatContextController.cs:92` | видимость | `new ContextScope(UserId, session, project)` — ключ стора `chat-context/{owner}/…` из `UserId`: ЛОВУШКА (главная, §6.6 п. 2) |
| 31 | `ClaudeHomeServer/Controllers/FilesController.cs:157` | видимость | доступ к файлам по `s.ProjectId`; ключ хранилищ не используется |
| 32 | `ClaudeHomeServer/Services/ChatHandoffService.cs:87` | видимость | `userId` → `CreateHandoffChatAsync(source, userId)`, LLM-вызов `ownerId: userId`: ЛОВУШКА |
| 33 | `ClaudeHomeServer/Services/ChatHandoffService.cs:131` | видимость | `userId` → `CreateHandoffChatAsync(source, userId)`, LLM-вызов `ownerId: userId`: ЛОВУШКА |
| 34 | `ClaudeHomeServer/Services/SessionMessagingService.cs:58` | видимость | `ownerId` → `GetActiveTurnDelegation/ReportBlockerAsync/ReportUpAsync(…, ownerId)`: ЛОВУШКА при переписке между чатами общего проекта |
| 35 | `ClaudeHomeServer/Services/SessionMessagingService.cs:79` | видимость | `ownerId` → `GetActiveTurnDelegation/ReportBlockerAsync/ReportUpAsync(…, ownerId)`: ЛОВУШКА при переписке между чатами общего проекта |
| 36 | `ClaudeHomeServer/Services/SessionMessagingService.cs:171` | видимость | `ownerId` → `GetActiveTurnDelegation/ReportBlockerAsync/ReportUpAsync(…, ownerId)`: ЛОВУШКА при переписке между чатами общего проекта |
| 37 | `ClaudeHomeServer/Services/PersonasCrudService.cs:89` | владение | `userId` → `users.GetById`, `personas.*`, `SetOnboardingCreatedPersona(csid, userId)`, `Broadcast(userId, …)` — онбординг-чат пользователя; остаётся (проверка в 341 `project.OwnerId != userId` — таблица 2) |
| 38 | `ClaudeHomeServer/Services/PersonasCrudService.cs:169` | владение | `userId` → `users.GetById`, `personas.*`, `SetOnboardingCreatedPersona(csid, userId)`, `Broadcast(userId, …)` — онбординг-чат пользователя; остаётся (проверка в 341 `project.OwnerId != userId` — таблица 2) |
| 39 | `ClaudeHomeServer/Services/PersonasCrudService.cs:221` | владение | `userId` → `users.GetById`, `personas.*`, `SetOnboardingCreatedPersona(csid, userId)`, `Broadcast(userId, …)` — онбординг-чат пользователя; остаётся (проверка в 341 `project.OwnerId != userId` — таблица 2) |
| 40 | `ClaudeHomeServer/Services/PersonasCrudService.cs:325` | владение | `userId` → `users.GetById`, `personas.*`, `SetOnboardingCreatedPersona(csid, userId)`, `Broadcast(userId, …)` — онбординг-чат пользователя; остаётся (проверка в 341 `project.OwnerId != userId` — таблица 2) |
| 41 | `ClaudeHomeServer/Services/ChatContinueService.cs:90` | видимость | `ownerId` → `Schedule(ownerId, …)`, новый чат: ЛОВУШКА |
| 42 | `ClaudeHomeServer/Services/Composition/PromptSeamAdapters.cs:152` | форвардер | `ownerId` как пришёл — шов `IMcpSessionAccessor`; следует за решением по методу |
| 43 | `ClaudeHomeServer/Services/Team/TeamTurnCompletionService.cs:374` | владение | `ownerId` задачи/хода (арендатор по построению: исполнение «от проекта», §4.1); отчёт `ReportUp(…, ownerId)` |
| 44 | `ClaudeHomeServer/Services/Team/TeamBudgetService.cs:217` | владение | `ownerId` задачи — арендатор; чтение цепочки родителей по `parentId` |
| 45 | `ClaudeHomeServer/Services/Team/TeamBudgetService.cs:222` | владение | `ownerId` задачи — арендатор; чтение цепочки родителей по `parentId` |
| 46 | `ClaudeHomeServer/Services/Watchdog/WatchdogEnvironment.cs:23` | владение | `ownerId` записи вочдога — арендатор по построению; профиль `homeResolver.Resolve(users.GetById(w.OwnerId))` |
| 47 | `ClaudeHomeServer/Services/Mcp/Http/TurnContextToolset.cs:196` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 48 | `ClaudeHomeServer/Services/Mcp/Http/HiggsfieldToolset.cs:491` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 49 | `ClaudeHomeServer/Services/Mcp/Http/CodeGraphToolset.cs:167` | видимость | `project.OwnerId != context.OwnerId`; `personas.Get`, `bindings.ServerToolEnabled(context.OwnerId…)`: ЛОВУШКА |
| 50 | `ClaudeHomeServer/Services/Mcp/Http/PersonasToolset.cs:460` | видимость | `personas.Get(pid, context.OwnerId)`, `sessions.PersonasEnabled(context.OwnerId…)`: ЛОВУШКА |
| 51 | `ClaudeHomeServer/Services/Mcp/Http/LocalMediaToolset.cs:421` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 52 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:262` | видимость | `personas.Get(pid, context.OwnerId)`, `BuildWorkspacePlan(context.OwnerId, session.ProjectId…)`: ЛОВУШКА |
| 53 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1381` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 54 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1449` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 55 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1475` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 56 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1501` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 57 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1541` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 58 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1610` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 59 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1728` | видимость | чат-операции от `context.OwnerId` (`UpdateAsync/SetArchivedAsync/Schedule/SendAsync/ReportUpAsync(context.OwnerId…)`): ЛОВУШКА |
| 60 | `ClaudeHomeServer/Services/Mcp/Http/WebSearchToolset.cs:262` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 61 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:784` | видимость | `personas.Get(pid, context.OwnerId)`, `sessions.TasksMcpEnabled(context.OwnerId…)`: ЛОВУШКА |
| 62 | `ClaudeHomeServer/Services/Mcp/Http/NotificationsToolset.cs:167` | видимость | `personas.Get`, `bindings.NotificationsEnabled(context.OwnerId…)`: уведомления личные — ключ остаётся `sub`, ловушка только для персоны |
| 63 | `ClaudeHomeServer/Services/Mcp/Http/ProjectRunCaller.cs:44` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 64 | `ClaudeHomeServer/Services/Mcp/Http/WatchToolset.cs:144` | видимость | `context.OwnerId` — ключ расхода/нитей/сторожей; в теле проверить по месту (ЛОВУШКА, если пишет в `{owner}`-хранилище) |
| 65 | `ClaudeHomeServer/Services/Mcp/Http/DifyToolset.cs:379` | видимость | `users.GetById(context.OwnerId).Username` → имя датасета `{username}:…`; сверка `owner == context.OwnerId`: ЛОВУШКА (датасеты арендатора) |
| 66 | `ClaudeHomeServer/Services/Mcp/Http/McpToolWhitelist.cs:46` | видимость | `assignments.Resolve(…, context.OwnerId)`, `personas.Get(pid, context.OwnerId)`: ЛОВУШКА (слоты моделей арендатора, §4.3) |
| 67 | `ClaudeHomeServer/Services/SessionManager.cs:2018` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 68 | `ClaudeHomeServer/Services/SessionManager.cs:2033` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 69 | `ClaudeHomeServer/Services/SessionManager.cs:2342` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 70 | `ClaudeHomeServer/Services/SessionManager.cs:2408` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 71 | `ClaudeHomeServer/Services/SessionManager.cs:2519` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 72 | `ClaudeHomeServer/Services/SessionManager.cs:2556` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 73 | `ClaudeHomeServer/Services/SessionManager.cs:3056` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 74 | `ClaudeHomeServer/Services/SessionManager.cs:3253` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 75 | `ClaudeHomeServer/Services/SessionManager.cs:3582` | видимость | `ownerId` → `CreatePersonaChatAsync/CreateChatAsync(ownerId…)` — клон чата создаётся на ключ вызывающего: ЛОВУШКА (клон должен лечь на арендатора) |
| 76 | `ClaudeHomeServer/Services/SessionManager.cs:3665` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 77 | `ClaudeHomeServer/Services/SessionManager.cs:3716` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 78 | `ClaudeHomeServer/Services/SessionManager.cs:5895` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 79 | `ClaudeHomeServer/Services/SessionManager.cs:5898` | видимость | внутренний метод; `ownerId` — параметр вызывающего, дальше `_sessions`-запись по `sessionId` без ключа. Ловушка только в вызывающих |
| 80 | `ClaudeHomeServer/Services/SessionManager.cs:6809` | владение | `userId` → фоновый LLM-вызов (`ownerId: userId`) — платит арендатор, ключ обязан быть арендатором: ЛОВУШКА |
| 81 | `ClaudeHomeServer/Services/SessionManager.cs:6843` | владение | `userId` → фоновый LLM-вызов (`ownerId: userId`) — платит арендатор, ключ обязан быть арендатором: ЛОВУШКА |
| 82 | `ClaudeHomeServer/Services/SessionManager.cs:11027` | объявление | `ResolveOwnerId(s) == ownerId` — единственная точка сравнения; станет `GetAccessible` |
| 83 | `ClaudeHomeServer/Services/ChatDigestService.cs:56` | видимость | после `GetOwned` берёт `ResolveOwnerId(session)` (65) для `_ownerGates` и LLM — ключ уже арендатор; образцовый вызов |
| 84 | `ClaudeHomeServer/Services/ChatDigestService.cs:75` | видимость | после `GetOwned` берёт `ResolveOwnerId(session)` (65) для `_ownerGates` и LLM — ключ уже арендатор; образцовый вызов |
| 85 | `ClaudeHomeServer.VideoEditor/Mcp/VideoEditorToolset.cs:748` | видимость | `context.OwnerId` → проект `found.OwnerId != context.OwnerId` (таблица 2); нити/пути редактора ключуются `{owner}`: ЛОВУШКА |

Итого строк: **85** (включая объявление `SessionManager.cs:11027` и форвардер `PromptSeamAdapters.cs:152`; вызовов без них — 83; объявление шва — `McpCallContextSeams.cs:23`, форвардерская сигнатура `PromptSeamAdapters.cs:151`). По вердикту: видимость 70, владение 13, форвардер 1, объявление 1. Из них с пометкой ЛОВУШКА (ключ вызывающего уходит в хранилище/персону/`SessionManager` без `ResolveOwnerId`): **42**.
Шов `IMcpSessionAccessor.GetOwned` (`Core/Services/Mcp/Http/McpCallContextSeams.cs:23`) — тот же метод через адаптер; его потребители — тулсеты из таблицы выше. Вызовы в тестах (`SessionManagerTests.cs:393-394, 11075, 11094`, моки `IMcpSessionAccessor` в ~10 тестовых проектах) в таблицу не входят.

**Главный вывод по 1Б.** Ловушка ключа массовая и не локальная: в Main это `ChatContextController` (стор `chat-context/{UserId}/…` — эталонный пример из разреза), команда `ChatsController` (6 действий передают `UserId` в `SessionManager.*`), `SessionMessagingService`, `ChatHandoffService`, `ChatContinueService`; в MCP — одна и та же пара «`GetOwned` + `personas.Get(pid, context.OwnerId)`». Любой перевод вызывающего с `GetOwned(id, UserId)` на `GetAccessible` без одновременной подмены ключа на `ResolveOwnerId(s)` создаёт именно ту дыру из §6.6 п. 2. Рекомендую: `GetAccessible` отдаёт не `Session`, а пару `(Session, TenantOwnerId)`, чтобы забыть ключ было нельзя (см. вопрос В2).

## 2. Инлайн-сравнения `OwnerId (==|!=) <id вызывающего>`

Охват — как у сторожа S0-G: `**/Controllers/**`, `**/Services/Mcp/**`, `**/Mcp/**` вертикалей, `Hubs/**`. Вердикты: **перевод** — станет `IProjectAccess`/`RequestScope`; **остаётся** — личное/админское; **проверить** — зависит от решения. Группа A — `Controllers/` в `ClaudeHomeServer` (Main), B — `Controllers/` вертикалей `ClaudeHomeServer.*`.

| Группа/шаг | Файл | Строки | Кол-во | Вердикт |
|---|---|---|---|---|
| A | `ClaudeHomeServer/Controllers/ChatSubjectsController.cs` | 25 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/CodeGraphController.cs` | 78, 128, 223 | 3 | перевод |
| A | `ClaudeHomeServer/Controllers/DeviceAgentTicketController.cs` | 39 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/DocsController.cs` | 29 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/DossiersController.cs` | 50, 109, 120, 148 | 4 | перевод |
| A | `ClaudeHomeServer/Controllers/FilesController.cs` | 38, 49 | 2 | перевод |
| A | `ClaudeHomeServer/Controllers/GitController.cs` | 57 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/KnowledgeController.cs` | 41 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/ModelsController.cs` | 119, 371 | 2 | проверить (задача/проект в админ-списке) |
| A | `ClaudeHomeServer/Controllers/OnboardingController.cs` | 92 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/PersonasController.cs` | 1091, 1414 | 2 | перевод |
| A | `ClaudeHomeServer/Controllers/PreviewController.cs` | 45 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/ProjectBackgroundsController.cs` | 30 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/ProjectMapController.cs` | 51, 73, 95 | 3 | перевод |
| A | `ClaudeHomeServer/Controllers/ProjectPresetsController.cs` | 30 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/ProjectRelayController.cs` | 101 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/ProjectsController.cs` | 123, 137, 149, 180, 188, 201, 218, 230, 295, 371, 436, 452, 471, 481, 491, 520, 604, 637, 661, 687, 708 | 21 | перевод |
| A | `ClaudeHomeServer/Controllers/SessionsController.cs` | 28 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/SkillsController.cs` | 37 | 1 | перевод |
| A | `ClaudeHomeServer/Controllers/TasksController.cs` | 40, 162, 274, 368, 375, 391, 604, 1521, 1566 | 9 | перевод |
| A | `ClaudeHomeServer/Controllers/TelemetryController.cs` | 68, 70 | 2 | проверить (проекты в телеметрии: `OwnerId is null || == ownerId`) |
| B | `ClaudeHomeServer.Architecture/Controllers/ArchitectureController.cs` | 194 | 1 | перевод |
| B | `ClaudeHomeServer.AudioEditor/Controllers/AudioEditScopeGate.cs` | 22 | 1 | перевод |
| B | `ClaudeHomeServer.ImageEditor/Controllers/ImageEditScopeGate.cs` | 21 | 1 | перевод |
| B | `ClaudeHomeServer.Plugins/Controllers/PluginBridgeController.cs` | 184 | 1 | перевод |
| B | `ClaudeHomeServer.Spend/Controllers/SpendController.cs` | 90 | 1 | проверить (расход задачи, админ) |
| B | `ClaudeHomeServer.VideoEditor/Controllers/VideoEditScopeGate.cs` | 22 | 1 | перевод |
| S0-6 | `ClaudeHomeServer/Hubs/TerminalHub.cs` | 25 | 1 | перевод |
| S0-7 | `ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs` | 482 | 1 | перевод |
| S0-7 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs` | 121, 216, 301, 351, 588, 723, 856 | 7 | перевод |
| S2a-3 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs` | 218, 294, 580, 663 | 4 | перевод (по плану) |
| нет шага | `ClaudeHomeServer.AudioEditor/Mcp/AudioEditorToolset.cs` | 713 | 1 | перевод |
| нет шага | `ClaudeHomeServer.ImageEditor/Mcp/ImageEditorToolset.cs` | 905 | 1 | перевод |
| нет шага | `ClaudeHomeServer.VideoEditor/Mcp/VideoEditorToolset.cs` | 751 | 1 | перевод |
| нет шага | `ClaudeHomeServer/Services/Mcp/Http/CodeGraphToolset.cs` | 180 | 1 | перевод |
| нет шага | `ClaudeHomeServer/Services/Mcp/Http/DifyToolset.cs` | 388 | 1 | перевод |
| нет шага | `ClaudeHomeServer/Services/Mcp/Http/LocalMediaToolset.cs` | 441 | 1 | перевод |
| нет шага | `ClaudeHomeServer/Services/Mcp/Http/ProjectRunCaller.cs` | 66 | 1 | перевод |
| нет шага | `ClaudeHomeServer/Services/Mcp/LocalMediaProjectAccess.cs` | 18, 31 | 2 | перевод |
| — | `ClaudeHomeServer/Controllers/ChatsController.cs` | 36 | 1 | остаётся (чат вне проекта: `ProjectId is null`) |
| A | `ClaudeHomeServer/Controllers/ChatsController.cs` | 63 | 1 | перевод |
| — | `ClaudeHomeServer/Controllers/LocalModelStatsController.cs` | 121, 465, 473, 480, 488, 570, 593, 615 | 8 | остаётся (анонимизация чужого, админ-панель) |
| — | `ClaudeHomeServer/Controllers/MediaHistoryController.cs` | 126 | 1 | остаётся (история генераций, `Origin.OwnerId`, админ-исключение ADR-030) |
| — | `ClaudeHomeServer/Controllers/ProjectGroupsController.cs` | 46, 70 | 2 | остаётся (сферы личные) |
| — | `ClaudeHomeServer/Controllers/TtsController.cs` | 106 | 1 | остаётся (личный чат: `session.OwnerId`) |

Итого строк в охвате сторожа: **102**. По шагам: A — 61, B — 6, S0-6 — 1, S0-7 — 8, S2a-3 — 4, нет шага — 9, — — 13.
Для сверки с формулировкой разреза: узкий шаблон сторожа `\.OwnerId\s*(==|!=)\s*UserId` даёт **64** совпадений в охвате; таблица выше шире (учтены `userId`/`ownerId`/`context.OwnerId`, `string.Equals`, обратный порядок, `is { OwnerId: var … }`-паттерны), поэтому allow-list S0-G должен строиться по таблице, а не по узкому шаблону: иначе сторож пропустит `context.OwnerId`-сравнения в MCP. Это вопрос Г1 к А1/Кузьме.
Исключены как не относящиеся к владельцу проекта/задачи: `HiggsfieldOAuthService.cs:334,398` (сравнение с `ServiceOwnerId`), `McpOAuthService.cs:274` (OAuth-ожидание); строки-комментарии (`LocalModelStatsController.cs:115,410`, `MediaHistoryController.cs:51,92`).

**Группы A и B для S0-4/S0-5.**
- Группа A (S0-4, Main `Controllers/`): `ChatSubjectsController.cs`, `CodeGraphController.cs`, `DeviceAgentTicketController.cs`, `DocsController.cs`, `DossiersController.cs`, `FilesController.cs`, `GitController.cs`, `KnowledgeController.cs`, `ModelsController.cs`, `OnboardingController.cs`, `PersonasController.cs`, `PreviewController.cs`, `ProjectBackgroundsController.cs`, `ProjectMapController.cs`, `ProjectPresetsController.cs`, `ProjectRelayController.cs`, `ProjectsController.cs`, `SessionsController.cs`, `SkillsController.cs`, `TasksController.cs`, `TelemetryController.cs` — 21 файлов.
- Группа B (S0-5, вертикали): `ClaudeHomeServer.Architecture/Controllers/ArchitectureController.cs`, `ClaudeHomeServer.AudioEditor/Controllers/AudioEditScopeGate.cs`, `ClaudeHomeServer.ImageEditor/Controllers/ImageEditScopeGate.cs`, `ClaudeHomeServer.Plugins/Controllers/PluginBridgeController.cs`, `ClaudeHomeServer.Spend/Controllers/SpendController.cs`, `ClaudeHomeServer.VideoEditor/Controllers/VideoEditScopeGate.cs` — 6 файлов. Из них `*ScopeGate.cs` (Image/Audio/Video) — это не контроллеры, а шлюзы, которые зовут и контроллеры, и сервисы вертикалей.
- Файлы Main с вердиктом «остаётся» (в S0-4 не входят, в allow-list S0-G остаются навсегда или помечаются исключением): `ChatsController.cs`, `LocalModelStatsController.cs`, `MediaHistoryController.cs`, `ProjectGroupsController.cs`, `TtsController.cs`.
- Объём S0-4 (A) — 21 из 61 строк группы A приходятся на `ProjectsController`. `ChatsController` одновременно в списке «остаётся» (строка 36, чат вне проекта) и в A (строка 63, проверка проекта). Предлагаю в S0-4 резать на два коммита: `ProjectsController` отдельно (он же задаёт форму базы), остальные — вторым.

### 2.1 Сравнения вне охвата сторожа (сервисы и сторы)

Это не эндпоинты: сервис проверяет владельца своего хранилища. Шага-переводчика в плане для них нет и быть не должно — они **определяют арендатора**, а не доступ. Но часть из них — проверки доступа в сервисах (`PersonaBindingsService`, `SessionManager`, `TaskExecutionService`) — осядет в «нет шага», если ими забыть заняться. Список (файл — кол-во строк): 

| Файл | Строк |
|---|---|
| `ClaudeHomeServer.Devices/DeviceRegistry.cs` | 9 |
| `ClaudeHomeServer.Tasks/TaskManager.cs` | 8 |
| `ClaudeHomeServer/Services/PersonaBindingsService.cs` | 8 |
| `ClaudeHomeServer/Services/PersonaManager.cs` | 8 |
| `ClaudeHomeServer.Images/Services/LocalMedia/LocalMediaJobStore.cs` | 6 |
| `ClaudeHomeServer.VideoEditor/Jobs/VideoEditJobService.cs` | 5 |
| `ClaudeHomeServer/Services/SphereManager.cs` | 5 |
| `ClaudeHomeServer.AudioEditor/Jobs/AudioEditJobService.cs` | 4 |
| `ClaudeHomeServer.ImageEditor/Jobs/ImageEditJobService.cs` | 4 |
| `ClaudeHomeServer.Spend/SpendAnalyticsService.cs` | 4 |
| `ClaudeHomeServer/Services/SessionManager.cs` | 4 |
| `ClaudeHomeServer/Services/TaskExecutionService.cs` | 4 |
| `ClaudeHomeServer.Devices/AgentTicketService.cs` | 3 |
| `ClaudeHomeServer.Watchdog/WatchdogStore.cs` | 3 |
| `ClaudeHomeServer/Services/PersonasCrudService.cs` | 3 |
| `ClaudeHomeServer/Services/ProjectManager.cs` | 3 |
| `ClaudeHomeServer.AudioEditor/Threads/LocalAudioAdopter.cs` | 2 |
| `ClaudeHomeServer.Devices/DeviceConnectionRegistry.cs` | 2 |
| `ClaudeHomeServer.ImageEditor/Characters/LoraTrainingFinisher.cs` | 2 |
| `ClaudeHomeServer.ImageEditor/Prefs/ImageProjectPrefs.cs` | 2 |
| `ClaudeHomeServer.ImageEditor/Threads/LocalImageAdopter.cs` | 2 |
| `ClaudeHomeServer.Tasks/TaskBridge.cs` | 2 |
| `ClaudeHomeServer.VideoEditor/Assembly/FilmBuildRegistry.cs` | 2 |
| `ClaudeHomeServer.Architecture/Services/Architecture/ArchitectureToolset.cs` | 1 |
| `ClaudeHomeServer.DeviceAgent/Composition/AgentHub.cs` | 1 |
| `ClaudeHomeServer.Devices/DeviceExecChannel.cs` | 1 |
| `ClaudeHomeServer.Dossiers/DossierAutoExportGate.cs` | 1 |
| `ClaudeHomeServer.Dossiers/DossierAutoExporter.cs` | 1 |
| `ClaudeHomeServer.Dossiers/DossierCaptureService.cs` | 1 |
| `ClaudeHomeServer.ImageEditor/Characters/CharacterPhotoLabeling.cs` | 1 |
| `ClaudeHomeServer.Images/Services/ImageBackfillService.cs` | 1 |
| `ClaudeHomeServer.Images/Services/ImageBackfillStore.cs` | 1 |
| `ClaudeHomeServer.Images/Services/LocalMedia/LocalMediaService.Lora.cs` | 1 |
| `ClaudeHomeServer.Images/Services/LocalMedia/Stats/ComfyStats.cs` | 1 |
| `ClaudeHomeServer.Knowledge/ProjectKnowledgeSyncService.cs` | 1 |
| `ClaudeHomeServer.Knowledge/UserKnowledgeCascade.cs` | 1 |
| `ClaudeHomeServer.Notes/Services/Notes/NoteTaskSyncService.cs` | 1 |
| `ClaudeHomeServer.Notes/Services/Notes/NotesService.Annotations.cs` | 1 |
| `ClaudeHomeServer.Notes/Services/Notes/NotesService.cs` | 1 |
| `ClaudeHomeServer.ProjectServices/Services/ProjectServices/ExternalPreviewRouter.cs` | 1 |
| `ClaudeHomeServer.Tasks/Projector/FeatureTypical.cs` | 1 |
| `ClaudeHomeServer.VideoEditor/Films/FilmFrameFollower.cs` | 1 |
| `ClaudeHomeServer.VideoEditor/Films/FilmMusicComposer.cs` | 1 |
| `ClaudeHomeServer/Program.cs` | 1 |
| `ClaudeHomeServer/Services/ChatArchiveService.cs` | 1 |
| `ClaudeHomeServer/Services/Composition/ArchitectureAgentLauncherAdapter.cs` | 1 |
| `ClaudeHomeServer/Services/Composition/ProjectSummaryLookup.cs` | 1 |
| `ClaudeHomeServer/Services/MediaHistory/MediaHistoryIndex.cs` | 1 |
| `ClaudeHomeServer/Services/SessionContextResolver.cs` | 1 |
| `ClaudeHomeServer/Services/TaskPregateService.cs` | 1 |

Итого вне охвата: **122** строк в 50 файлах. Вместе с охватом сторожа — 224 (разрез говорил «≈220 в 32 файлах»).

## 3. Инвентаризация хранилищ с ключом `{ownerId}`

Колонка «Перекладка» отвечает на вопрос И.4 п. 2: нужно ли при передаче владения менять ключ/путь. «Да» — ключ содержит `ownerId` и данные проекта должны пойти за арендатором. «Нет» — личное, остаётся у пользователя. «Поле» — достаточно записи в самой структуре. Пункты 1–13 — перечень И.4 п. 2 (нити картинок/звука/видео разнесены на 3а–3в); пункты 14–32 — **найдено сверх перечня**.

| # | Хранилище | Путь / ключ | Файл:строка | Кто пишет | Перекладка |
|---|---|---|---|---|---|
| 1 | `Project.OwnerId` | `data/projects.json`, поле `OwnerId` | `Core/Models/Project.cs:103` | `ProjectManager` (Create/Update) | Поле: одна запись, под замком проекта |
| 2 | Контекст чата | `data/chat-context/{ownerId}/{sessionId}.json` | `Core/Services/ChatContext/ChatContextStore.cs:24,156`; `ChatContextLifecycle.cs:31` | `ChatContextStore` (REST `ChatContextController`, MCP `turn-context`) | Да — перенос каталога сессий проекта; ловушка ключа №1 (`ChatContextController` строит `ContextScope(UserId…)`) |
| 3а | Нити картинок | `data/image-threads/{ownerId}/{sessionId}.json` | `ImageEditor/Threads/ImageThreadStore.cs:19,514` | `ImageThreadStore`/`ImageThreadService` | Да |
| 3б | Нити звука | `data/audio-threads/{ownerId}/{sessionId}.json` | `AudioEditor/Threads/AudioThreadStore.cs:21,328` | `AudioThreadStore` | Да |
| 3в | Нити видео | `data/video-threads/{ownerId}/{sessionId}.json` | `VideoEditor/Scenes/VideoThreadStore.cs:19,285` | `VideoThreadStore` | Да |
| 4 | Фильмы видео | `data/video-films/{ownerId}/{projectId}/{filmKey}.json` | `VideoEditor/Films/FilmSideStore.cs:12,72` | `FilmSideStore` | Да — только подкаталог проекта; каталог владельца общий |
| 5 | Журнал фичи | `data/feature-journal/{ownerId}/{featureId}.jsonl` | `Tasks/Journal/FeatureJournalStore.cs:10,182` | `FeatureJournalStore.Append` | Да — по featureId задач проекта |
| 6 | Досье изменений | `data/dossiers/{ownerId}/{projectId}.json` + `.archive.jsonl`; `dossiers/discussions.json`, `state.json` с ключом `{owner}:{project}[:{session|hash}]` | `Dossiers/DossierStore.cs:72,433,436,438`; `DossierDiscussionStore.cs:33,37`; `DossierCaptureState.cs:22,29` | `DossierStore`, `DossierCaptureService` | Да — файлы + переименование ключей во внутренних словарях и в `discussions/state` (ключ `{owner}:…` внутри файла, не только путь) |
| 7 | Память команды | `data/team-memory.json`, `team-memory-knowledge.json`; ключ записи `{ownerId}:{projectId}` | `Memory/TeamMemoryService.cs:21`; `Memory/MemoryShelf.cs:824` | `MemoryShelf` (REST, MCP `memory`, автообучение) | Да — смена ключа, плюс `AllScopes()` у консолидатора |
| 8 | Задачи | `data/tasks.json`, `TaskItem.OwnerId` | `Core/Models/TaskItem.cs:11`; `Tasks/TaskManager.cs:335` | `TaskManager` | Поле во всех задачах проекта (`ProjectId`), не по всему `OwnerId` |
| 9 | Персоны проекта | `data/personas.json`, `Persona.OwnerId` (+ `data/persona-memory.json`, ключ `{owner}:{persona}`) | `Core/Models/Persona.cs:86`; `Memory/PersonaMemoryService.cs:96,153` | `PersonaManager`, `PersonaMemoryService` | Да для проектных персон (зона проекта); память персоны едет с персоной; глобальные персоны остаются |
| 10 | События проекта | SQLite `data/project-events.db`, колонка `OwnerId` | `Core/Models/ProjectEvent.cs:17`; `Services/ProjectEventLogService.cs:34,132,151` | `ProjectEventLogService` | Поле во всех строках проекта; историю не переписываем (И.4 п. 2) |
| 11 | Датасеты Dify | Имена `{username}:{projectName}`, `{username}:team:{projectName}`, `{username}:dossiers:{projectName}`, `{username}:persona:…` | `Knowledge/KnowledgeService.cs:402`; `Memory/TeamMemoryService.cs:28,146`; `Dossiers/DossierStore.cs:369`; `Knowledge/UserKnowledgeCascade.cs:8` | `KnowledgeService`, `TeamMemoryService`, `DossierStore` | Да — переименование по образцу `RenameProjectDatasetAsync`; ключ контроля доступа к датасету — **префикс имени** (`KnowledgeAccess.cs:9`), не поле |
| 12 | LoRA проекта | `models/loras/ccs/{ownerKey}/{slug}-{runId}/`, `ownerKey` = Guid без дефисов | `Core/Services/ImageEditor/LoraNamespace.cs:8,58-73`; `Images/Services/LocalMedia/LocalMediaService.Lora.cs:70,306` | обучение LoRA (`LocalMediaService`) | Да — перенос файлов; видимость завязана на `LoraNamespace.VisibleTo` (ADR-026) |
| 13 | Профили песочницы (транскрипты container-владельца) | `{ProfilesHostDir}/{ownerId}/{key}/…` | `Services/SessionManager.cs:2324,2673`; `Execution/…/DockerProcessRunner.cs:287` | `DockerProcessRunner`, `SessionManager` | Да — копирование файлов чатов проекта, иначе `--resume` ломается; только при совпадении `ExecutionEnvironment` |
| 14 | Фото обучения персонажей | `data/<Folder>/{owner}/{project}/…` | `ImageEditor/Characters/CharacterTrainingPhotos.cs:169` | `CharacterTrainingPhotos` | **Сверх перечня.** Да (идёт с LoRA проекта) |
| 15 | Настройки редактора картинок | `data/image-editor-prefs/{ownerId}/{projectId}.json` | `ImageEditor/Prefs/ImageProjectPrefs.cs:50,111` | `ImageProjectPrefs` | **Сверх перечня.** Да (проектные настройки) |
| 16 | Настройки редактора звука | `data/audio-editor-prefs/{ownerId}/{scopeKey}.json` | `AudioEditor/Prefs/AudioModePrefs.cs:68,106` | `AudioModePrefs` | **Сверх перечня.** Да, если `scopeKey` проектный |
| 17 | Настройки редактора видео | `data/video-editor-prefs/{ownerId}/{scopeKey}.json` | `VideoEditor/Prefs/VideoPrefsStore.cs:15,54` | `VideoPrefsStore` | **Сверх перечня.** Да, если `scopeKey` проектный |
| 18 | Рабочие каталоги задач редактора картинок | `{Root}/{ownerId}/{jobId}/…` | `ImageEditor/Jobs/ImageEditWorkspace.cs:90,193` | `ImageEditWorkspace` | **Сверх перечня.** Нет: временные; передача возможна только без идущих задач (предусловие И.4 п. 1) |
| 19 | Рабочие каталоги задач звука | `{Root}/{ownerId}/{jobId}/{variant}/…` | `AudioEditor/Jobs/AudioEditWorkspace.cs:25,41,53` | `AudioEditWorkspace` | **Сверх перечня.** Нет (временные) |
| 20 | Рабочие каталоги задач видео и кадры | `{Root}/{ownerId}/{jobId}/…`, `{Root}/{ownerId}/frames/…` | `VideoEditor/Jobs/VideoEditWorkspace.cs:25,76,99,107` | `VideoEditWorkspace` | **Сверх перечня.** Кадры (`frames/`) — **да**: проектные ресурсы, на них ссылаются сцены; задачи — нет |
| 21 | Файлы агентов персон | `{baseDir}/{ownerId}/{dirKey}/.claude/agents/{handle}.md` | `Services/PersonaAgentFileSync.cs:298,459,462` | `PersonaAgentFileSync` | **Сверх перечня.** Да (пересинхронизируется от персон нового арендатора; старое удалить) |
| 22 | Worktree базы | `WorktreesBase(ownerId, project)` | `Services/SessionManager.cs:2679,9104` | `SessionManager` (создание worktree) | **Сверх перечня.** Проверить: если путь содержит `ownerId`, живые worktree проекта надо переносить |
| 23 | Временные файлы песочницы | `{TmpHostDir}/{ownerId}/…` | `Execution/Services/Execution/DockerProcessRunner.cs:62` | `DockerProcessRunner` | **Сверх перечня.** Нет (временные) |
| 24 | Сессии (метаданные чатов) | `data/sessions.json`, `Session.OwnerId` (у проектного чата `null`, владелец берётся из проекта) | `Core/Models/Session.cs:372`; `Services/SessionManager.cs:11027` | `SessionManager` | **Сверх перечня.** Нет для проектных чатов (поле `null`) — это и есть причина, почему ключ арендатора = `ResolveOwnerId`; **да** для личных |
| 25 | Сторожа чатов (вочдоги) | `WatchdogStore`, `WatchdogRecord.OwnerId` | `Watchdog/WatchdogStore.cs:48,70` | `WatchdogStore` | **Сверх перечня.** Поле; предусловие «нет идущих ходов» покрывает |
| 26 | Расход | `data/spend/…`, `SpendRecord.OwnerId` | `Spend/SpendStore.cs:198,203` | `SpendStore` | **Сверх перечня.** Нет: исторические записи не переписываем (И.4 п. 2); агрегаты ключуются кортежем с `OwnerId` |
| 27 | Задачи локальной медиа-генерации | `LocalMediaJobStore`, `OwnerId` | `Core/…` запись `LocalJobOrigin`; `Images/Services/LocalMedia/LocalMediaJobStore.cs` | `LocalMediaService` | **Сверх перечня.** Нет: журнал не переписываем, история видна админу (ADR-030) |
| 28 | Уведомления | `data/notifications/{userId}.json` | `Services/NotificationStore.cs:303` | `NotificationStore` | **Сверх перечня.** Нет — личное |
| 29 | Личные заметки, шаблоны | `data/notes/{userId}/…` | `Notes/Services/Notes/NotesService.cs:69,735,1208` | `NotesService` | **Сверх перечня.** Нет — личное; заметки проекта лежат в `<root>/notes`, ключа нет |
| 30 | Группы/сферы | `data/project-groups.json` (`Sphere.OwnerId`) | `Core/Models/Sphere.cs:15`; `Services/SphereManager.cs:30` | `SphereManager` | **Сверх перечня.** Нет — сфера личная; проект выпадает из неё (И.4 п. 3) |
| 31 | Устройства | `DeviceRegistry`, `DesktopDevice.OwnerId` | `Devices/DeviceRegistry.cs:60` | `DeviceRegistry` | **Сверх перечня.** Нет — локальные проекты общими не бывают |
| 32 | MCP-реестр и статусы | `mcp-servers.json` (`McpRegistry.GetByOwner`), статусы `McpStatusStore` | `Services/Mcp/McpRegistry.cs:76`; `McpStatusStore.cs:84` | `McpRegistry` | **Сверх перечня.** Нет — личные серверы владельца; передача: настройки старого владельца из проекта уходят (И.4 п. 3), риск §4.3 |

В перечне И.4 п. 2 названо 13 позиций (нити — одной), 15 при разнесении нитей на три; постановка шага говорит «14» — расхождение только в способе счёта, **все названные присутствуют** в строках 1–13 со ссылкой файл:строка. Сверх перечня найдено **19** хранилищ (14–32). Для передачи владения существенны: 14–17, 20 (кадры), 21 и 22 (проверить); остальные — «нет» с обоснованием.

Особо для И.4/S5: ключ часто сидит **внутри файла**, а не только в пути (`dossiers/discussions.json`, `state.json`, `team-memory.json`, `persona-memory.json`, in-memory словари `{owner}/{featureId}` у `FeatureJournalStore`, кэши `DossierRecallService.cs:370`). Перекладка каталога без правки внутренних ключей даст тихий разъезд — ровно «ловушка ключа в пакетном виде». Тест из §6.6 («после передачи владелец и участник видят те же ревизии всех хранилищ») нужно строить поверх этой таблицы: по строке на хранилище.

## 4. Рассылки: `ToOwner` и сырые группы `user_…`

Вердикт: **→ToProject** — событие про проект, должно идти всем участникам (`ToProject(projectId, …)`); **остаётся** — личное; **развилка** — вызывающий сам выбирает группу по `ProjectId`; **→ToProject если проектное** — зависит от сущности (у задачи/персоны/заметки без проекта остаётся личным). Для задач и персон нужна проверка по payload, поэтому в S2c ставится условный помощник «`ToTenantOrProject(entity)`».

### 4а. `ToOwner` (78 вызовов; объявления: `ISessionBroadcaster.cs:52`, `SessionHubBroadcaster.cs:56`, `AgentHub.cs:162`)

| # | Файл:строка | Сообщение | Вердикт |
|---|---|---|---|
| 1 | `ClaudeHomeServer.Core/Services/ChatContext/ChatContextBroadcaster.cs:27` | ChatContextChangedMessage | →ToProject (контекст чата проекта) |
| 2 | `ClaudeHomeServer.Images/Services/ImageBackfillService.cs:285` | ImageBackfilledMessage | остаётся (аватары/значки — личные сущности) |
| 3 | `ClaudeHomeServer.Images/Services/ImageBackfillService.cs:288` | PersonasChanged | →ToProject для проектных персон и памяти персоны проекта; глобальные остаются |
| 4 | `ClaudeHomeServer.Memory/TeamMemoryAutolearnService.cs:155` | TeamMemoryChanged | →ToProject |
| 5 | `ClaudeHomeServer.ImageEditor/Threads/ImageThreadService.cs:802` | ImageThreadChangedMessage | →ToProject (общие нити/настройки проекта) |
| 6 | `ClaudeHomeServer.ImageEditor/Jobs/ImageEditJobService.cs:842` | прогресс/итог задачи редактора | →ToProject (задача редактора проекта; в общем проекте её видят остальные) |
| 7 | `ClaudeHomeServer.ImageEditor/Prefs/ImageProjectPrefs.cs:242` | ImageProjectPrefsChangedMessage | →ToProject (общие нити/настройки проекта) |
| 8 | `ClaudeHomeServer.ImageEditor/Prefs/ImageProjectPrefs.cs:269` | ImageProjectPrefsChangedMessage | →ToProject (общие нити/настройки проекта) |
| 9 | `ClaudeHomeServer.Notes/Services/Mcp/NotesToolset.cs:415` | NotesChanged | →ToProject для заметок проекта; личные остаются |
| 10 | `ClaudeHomeServer.Plugins/Services/PluginDraftWatcher.cs:191` | PluginDraftChangedMessage | остаётся (личное: плагины владельца) |
| 11 | `ClaudeHomeServer.Plugins/Services/PluginSecretsStore.cs:98` | PluginsChangedMessage | остаётся (личное: плагины владельца) |
| 12 | `ClaudeHomeServer.Plugins/Services/PluginUninstall.cs:96` | PluginsChangedMessage | остаётся (личное: плагины владельца) |
| 13 | `ClaudeHomeServer.Plugins/Services/PluginStore.cs:160` | PluginsChangedMessage | остаётся (личное: плагины владельца) |
| 14 | `ClaudeHomeServer.AudioEditor/Jobs/AudioEditJobService.cs:856` | прогресс/итог задачи редактора | →ToProject (задача редактора проекта; в общем проекте её видят остальные) |
| 15 | `ClaudeHomeServer.AudioEditor/Jobs/AudioJobThreads.cs:239` | AudioThreadChangedMessage | →ToProject (общие нити/настройки проекта) |
| 16 | `ClaudeHomeServer.AudioEditor/Prefs/AudioModePrefs.cs:150` | AudioPrefsChangedMessage | →ToProject (общие нити/настройки проекта) |
| 17 | `ClaudeHomeServer.ProjectServices/Services/ProjectServices/DevServerService.cs:724` | DevServer* | →ToProject (панель «Сервисы» проекта; `project.OwnerId` — уже проектный) |
| 18 | `ClaudeHomeServer/Controllers/TasksController.cs:101` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 19 | `ClaudeHomeServer/Controllers/TasksController.cs:314` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 20 | `ClaudeHomeServer/Controllers/TasksController.cs:497` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 21 | `ClaudeHomeServer/Controllers/TasksController.cs:505` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 22 | `ClaudeHomeServer/Controllers/TasksController.cs:1003` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 23 | `ClaudeHomeServer/Controllers/TasksController.cs:1008` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 24 | `ClaudeHomeServer/Controllers/TasksController.cs:1052` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 25 | `ClaudeHomeServer/Controllers/TasksController.cs:1378` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 26 | `ClaudeHomeServer/Controllers/TasksController.cs:1573` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 27 | `ClaudeHomeServer/Services/SessionSummaryService.cs:105` | NotesChanged | остаётся (сводка чата пишется в личные заметки) |
| 28 | `ClaudeHomeServer/Services/PersonasCrudService.cs:58` | PersonasChanged | →ToProject для проектных персон; глобальные остаются |
| 29 | `ClaudeHomeServer/Services/GitAutoCommitService.cs:80` | GitStatusChanged | →ToProject |
| 30 | `ClaudeHomeServer/Services/ChatContinueService.cs:196` | Notification | остаётся (личное) |
| 31 | `ClaudeHomeServer/Services/ChatContinueService.cs:256` | NotesChanged | →ToProject для заметок проекта; личные остаются |
| 32 | `ClaudeHomeServer/Services/Composition/NotesHubNotifier.cs:22` | NotesChanged | →ToProject для заметок проекта; личные остаются |
| 33 | `ClaudeHomeServer/Services/Composition/NotesHubNotifier.cs:29` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся (шов передаёт только id задачи — расширить) |
| 34 | `ClaudeHomeServer/Services/Composition/ArchitectureAgentLauncherAdapter.cs:59` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 35 | `ClaudeHomeServer/Services/Team/TeamWaveService.cs:335` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 36 | `ClaudeHomeServer/Services/Team/TeamWaveService.cs:466` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 37 | `ClaudeHomeServer/Services/Team/TeamWaveService.cs:763` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 38 | `ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs:580` | PersonasChanged | →ToProject для проектных персон и памяти персоны проекта; глобальные остаются |
| 39 | `ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs:584` | TeamMemoryChanged | →ToProject |
| 40 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:609` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 41 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:686` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 42 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1160` | KnowledgeChanged | →ToProject, если датасет проекта; личные БЗ остаются |
| 43 | `ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs:1262` | GitStatusChanged | →ToProject |
| 44 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:294` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 45 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:485` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 46 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:492` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 47 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:554` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 48 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:557` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 49 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:658` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 50 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:684` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 51 | `ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs:714` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 52 | `ClaudeHomeServer/Services/TaskExecutionService.cs:355` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 53 | `ClaudeHomeServer/Services/TaskExecutionService.cs:423` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 54 | `ClaudeHomeServer/Services/TaskExecutionService.cs:460` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 55 | `ClaudeHomeServer/Services/TaskExecutionService.cs:489` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 56 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1118` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 57 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1267` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 58 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1281` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 59 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1389` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 60 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1481` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 61 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1549` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 62 | `ClaudeHomeServer/Services/TaskExecutionService.cs:1732` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 63 | `ClaudeHomeServer/Services/TaskExecutionService.cs:2011` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 64 | `ClaudeHomeServer/Services/TaskExecutionService.cs:2055` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 65 | `ClaudeHomeServer/Services/NotificationService.cs:46` | Notification | остаётся (личное) |
| 66 | `ClaudeHomeServer/Services/DefaultAssistantProvisioner.cs:121` | PersonasChanged/… | остаётся (онбординг пользователя) |
| 67 | `ClaudeHomeServer/Services/DefaultAssistantProvisioner.cs:123` | PersonasChanged/… | остаётся (онбординг пользователя) |
| 68 | `ClaudeHomeServer/Services/SessionManager.cs:2373` | Chat*/status | развилка (уже `ProjectId`→`ToProject`, иначе `ToOwner`) — остаётся |
| 69 | `ClaudeHomeServer/Services/SessionManager.cs:6927` | Chat*/status | развилка (уже `ProjectId`→`ToProject`, иначе `ToOwner`) — остаётся |
| 70 | `ClaudeHomeServer/Services/SessionManager.cs:9918` | Chat*/status | развилка (уже `ProjectId`→`ToProject`, иначе `ToOwner`) — остаётся |
| 71 | `ClaudeHomeServer/Services/SessionManager.cs:11021` | Chat*/status | развилка (уже `ProjectId`→`ToProject`, иначе `ToOwner`) — остаётся |
| 72 | `ClaudeHomeServer/Services/SessionManager.cs:11045` | Chat*/status | развилка (уже `ProjectId`→`ToProject`, иначе `ToOwner`) — остаётся |
| 73 | `ClaudeHomeServer.Tasks/TaskSchedulerService.cs:163` | TaskChanged | →ToProject, если у задачи `ProjectId`; иначе остаётся |
| 74 | `ClaudeHomeServer.VideoEditor/Jobs/VideoEditJobService.cs:919` | прогресс/итог задачи редактора | →ToProject (задача редактора проекта; в общем проекте её видят остальные) |
| 75 | `ClaudeHomeServer.VideoEditor/Jobs/VideoJobThreads.cs:181` | VideoFilmChangedMessage | →ToProject (общие нити/настройки проекта) |
| 76 | `ClaudeHomeServer.VideoEditor/Jobs/VideoJobThreads.cs:194` | VideoFilmChangedMessage | →ToProject (общие нити/настройки проекта) |
| 77 | `ClaudeHomeServer.VideoEditor/Prefs/VideoPrefsStore.cs:113` | VideoPrefsChangedMessage | →ToProject (общие нити/настройки проекта) |
| 78 | `ClaudeHomeServer.Watchdog/WatchdogNotifier.cs:68` | WatchdogSnapshot | остаётся для `user_`-группы (чаты вне проектов); для чатов проекта — добавить `ToProject` |

Итого вызовов: **78** (+ 3 объявления). Сверка с grep: `grep -rn --include=*.cs "ToOwner(" backend | grep -v obj | grep -vE "Tests?[^/]*/|TestKit"` = 81 строк, из них 3 объявления.

Сводка по вердиктам: безусловно «→ToProject» — всё про проектные нити/настройки/задачи редакторов/DevServer/контекст чата/Git/память команды; условно «→ToProject, если проектное» — `TaskChanged` (все вызовы в `TasksController`, `TasksToolset`, `TaskExecutionService`, `TeamWaveService`, `TaskSchedulerService`, `WorkspaceToolset`, `NotesHubNotifier`, `ArchitectureAgentLauncherAdapter`), `NotesChanged`, `PersonasChanged`, `KnowledgeChanged`; «остаётся» — уведомления, плагины, сводки в личные заметки, онбординг, аватарные догенерации. Пять вызовов в `SessionManager` уже имеют правильную развилку и не трогаются.

### 4б. Сырые группы `user_…` (13 литералов)

Объявления и подписки (не рассылки, менять нельзя без согласования с S1-хабом):

| # | Файл:строка | Что это | Вердикт |
|---|---|---|---|
| 1 | `ClaudeHomeServer/Hubs/SessionHub.cs:199` | подписка соединения на `user_{userId}` | остаётся — личная группа; проектные подписки отдельным `project_{id}` (S0-6/S1-хаб) |
| 2 | `ClaudeHomeServer/Hubs/SessionHub.cs:203` | отписка от `user_{userId}` | остаётся |
| 3 | `ClaudeHomeServer/Services/Composition/SessionHubBroadcaster.cs:32` | `OwnerGroupPrefix = "user_"` — единственная точка сборки префикса для `ToOwner` | остаётся |
| 4 | `ClaudeHomeServer.DeviceAgent/Composition/AgentHub.cs:151` | `OwnerGroup(ownerId) => "user_" + ownerId` — дубль префикса в агенте устройства | остаётся (локальные проекты общими не бывают) |

Сырые рассылки (мимо `ISessionBroadcaster`):

| # | Файл:строка | Сообщение | Вердикт |
|---|---|---|---|
| 1 | `Controllers/KnowledgeBasesController.cs:33` | KnowledgeChanged(action, datasetId) | →ToProject, если датасет проекта; пользовательские БЗ остаются |
| 2 | `Controllers/PersonasController.cs:83` | PersonasChanged | →ToProject для проектных персон; глобальные остаются |
| 3 | `Controllers/GitController.cs:75` | GitStatusChanged(projectId) | →ToProject |
| 4 | `Controllers/KnowledgeController.cs:36` | KnowledgeChanged("doc_changed", datasetId) | →ToProject (знания проекта) |
| 5 | `Controllers/OnboardingController.cs:264` | онбординг-сообщение | остаётся (личное) |
| 6 | `Controllers/ProjectsController.cs:31` | TeamMemoryChanged(action, projectId, entryId) | →ToProject |
| 7 | `Controllers/ProjectsController.cs:422` | событие проекта (после смены настроек) | →ToProject |
| 8 | `Controllers/ProjectsController.cs:586` | событие проекта/персон | →ToProject для проектных персон; проверить сообщение |
| 9 | `Services/Composition/KnowledgeHubNotifier.cs:14` | KnowledgeChanged (шов Knowledge) | →ToProject, если датасет проекта; иначе остаётся |

Всего 4 + 9 = **13** литералов, как в разрезе. Для строк 422 и 586 `ProjectsController` сообщение в самой строке не видно (многострочный вызов): вердикт предварительный, уточняется в S2c-0 при переводе.

**Условие корректности (для S2c/S1):** `ToProject` адресует группу `project_{id}`, в которую `SessionHub` пускает только участников (`OwnsProject` → `IProjectAccess`, S0-6). Пока S0-6 не сделан, заменять `ToOwner`→`ToProject` нельзя: группа проекта доступна по старой проверке владельца.

## 5. Как считал (команды)

Из корня worktree, `cd backend`; везде исключаются `obj/`, тестовые проекты (`*Tests*/`) и `ClaudeHomeServer.TestKit`.

```bash
FILT='grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit"'
# 1А: вызовы GetByOwner у проектов (после чего классификация по типу получателя вручную)
grep -rnE --include=*.cs "GetByOwner\(" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit"      # 167 строк, 8 типов
grep -rnE --include=*.cs "(_?projects|projectManager)\.GetByOwner\(" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit"
# 1Б: GetOwned
grep -rnE --include=*.cs "\bGetOwned\(" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit"      # минус SphereManager/SpheresController
# 2: инлайн-сравнения
grep -rnE --include=*.cs "OwnerId\s*(==|!=)|(==|!=)\s*[A-Za-z_.()]*OwnerId\b|Equals\([^)]*OwnerId" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit" | grep -E "/Controllers/|/Services/Mcp/|/Mcp/|/Hubs/"
# 3: хранилища
grep -rnE --include=*.cs "Path\.Combine\([^;]*(ownerId|OwnerId|userId|owner)\b" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit"
# 4: рассылки
grep -rnE --include=*.cs "\bToOwner\(" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit"           # 81 строка = 78 вызовов + 3 объявления
grep -rnE --include=*.cs "\"user_\"|user_\{" . | grep -v /obj/ | grep -vE "Tests?[^/]*/|TestKit" | grep -v "//"
```

Таблицы 1А, 1Б, 2, 4 собраны скриптом из этих grep (классификация — таблицей правил в скрипте, не на глаз); таблица 3 собрана вручную по `Path.Combine` с ключом владельца плюс прямому чтению сторов И.4. **Расхождение с числами разреза** (41/98) зафиксировано в разделе 0: считалось по типу получателя, а не по имени метода.

## 6. Вопросы к сверке А1 (Александр)

### Форма базы контроллера (S0-2): базовый класс или фильтр действия

Факты из таблиц, на которых стоит выбор:
- Контроллеров с проектным доступом в группе A — 21 файлов, в B — 6; каждый имеет собственное свойство `UserId => User.FindFirstValue(Sub)` (78 копий по разрезу §6.2).
- Не-проектные эндпоинты внутри тех же контроллеров (личное, админ) остаются на `sub`: `ChatsController` (чат вне проекта), `TasksController` (задачи без проекта), `PersonasController`, `KnowledgeBasesController`. Один контроллер = смесь проектных и личных действий.
- Три вида получения проекта: из `{id}` в маршруте (`ProjectsController`, `Dossiers`, `ProjectMap`…), из `sessionId` (чаты: `GetOwned` + `ProjectId`), из тела/запроса (`TasksController`: `bodyProjectId`, `targetProjectId`).
- Контроллеры вертикалей (B: Architecture, Plugins, Spend и три `*ScopeGate` для Image/Audio/Video) не могут наследоваться от класса в Main (правило границ: вертикаль не ссылается на Main), а `IProjectAccess`/`RequestScope` лежат в Core.

Мои вопросы:
1. **Базовый класс** (`ProjectScopedControllerBase`) годится для A, но не для B (контроллеры вертикалей в разных сборках). Класс придётся класть в **Core** — допустимо ли тянуть туда `ControllerBase`/`Microsoft.AspNetCore.*` при условии «ноль PackageReference» (`FrameworkReference` на `Microsoft.AspNetCore.App`)? Если нет — база только для A, а B работает через фильтр.
2. **Фильтр действия** (атрибут `[ProjectScope(From = Route|Session|Body)]`) закрывает и A, и B единым механизмом и оставляет личные эндпоинты нетронутыми. Цена: `RequestScope` кладётся в `HttpContext.Items`, и забыть атрибут на проектном эндпоинте можно — защищает матрица на двух пользователях (§6.6) и сторож S0-G. Предлагаю **фильтр + доступ к `RequestScope` через расширение `HttpContext`**, а базу не заводить. Согласны?
3. Где живёт «забыть нельзя» (ловушка ключа): `IProjectAccess` не должен отдавать ключ хранилища из `CallerUserId` (критерий 3 S0-2). Подтвердить, что `RequestScope.TenantOwnerId` — единственный ключ хранилищ в контроллерах, и сторож (S0-G) расширяется на шаблон `UserId` в вызовах стора, а не только на сравнение `OwnerId`.
4. Перевод `ProjectsController` (21 сравнение) делать первым и отдельно: он «чемпион» по числу точек и задаёт эталон для остальных 40+ файлов.

### Прочее

- **В1.** Шаг для 9 MCP-тулсетов и `*ScopeGate` без шага (раздел 0, п. 3): отдельный S0-7b (я возьму) или вынести в S2a?
- **В2.** Форма `GetAccessible` для сессий: отдавать `(Session, TenantOwnerId)`, а не `Session`, чтобы ключ арендатора шёл вместе с доступом (лечит ловушку 1Б: у 42 строк таблицы 1Б есть пометка ЛОВУШКА).
- **Г1.** S0-G: allow-list строить по таблице 2 (шире узкого шаблона из §6.6) — иначе MCP-сравнения по `context.OwnerId` останутся вне сторожа.
- **Г2.** 41/98 из разреза не воспроизвелись (раздел 0, п. 1–2): подтверждаем реальные 24 + 3 и 94/96? Это меняет оценки S0-4/S0-5 в меньшую сторону.
- **Д1.** Хранилища 14–17, 20 (кадры), 21 и 22 в таблице 3 не были в перечне И.4: дополнить перечень (и тест «те же ревизии») или подтвердить «нет»?
- **Е1.** `ToProject` нельзя включать до S0-6 (раздел 4): порядок S2c после S0-6 в плане соблюдён — подтвердить.

## S2c-0 · OnlyOffice: соредактирование и точка записи

Дата: 2026-10-29. Автор: Тимур. Шаг S2c-0 плана (§S2c), «чего не знаю» из разреза §9. Кода продукта в шаге нет — чтение кода и опыт на отдельном стенде. Номера строк — на снимке `70b9fbd9b` + незакоммиченные правки ветки.

### Короткий вывод

1. **Соредактирование у OnlyOffice DS есть**, пока у двух вкладок **один `document.key`**: правки сливаются на лету (опыт ниже, сценарий 2).
2. **Наше подключение ключ делает хрупким**: он считается от пути и времени изменения файла на диске, поэтому любая запись мимо DS (агент, WebDAV, `office-callback` соседней сессии) отправляет следующего открывшего в **параллельную** DS-сессию. Параллельные сессии не сливаются — выигрывает последний `callback` (сценарий 3а).
3. **Единственная точка записи файла из редактора — `office-callback`, и у неё нет никакого предусловия**: ни по хешу, ни по времени (`FilesController.cs:724`). Запись внешним процессом при открытой сессии тоже теряется (сценарий 3б). Для Office-файлов «конфликта 409» сегодня нет: тихая перезапись.

### 1. Путь записи файла из редактора документов

| Шаг | Где | Что происходит / какие проверки |
|---|---|---|
| Открытие | `frontend/src/components/OfficeViewer.tsx:238-242` | `GET …/files/office-config?path&mode&cacheKey` с Bearer-токеном человека; затем грузит `api.js` с `serverUrl` и создаёт `DocsAPI.DocEditor` (`:272-282`). Сам редактор ничего не пишет на диск. |
| Конфиг | `FilesController.cs:581-583` `OfficeConfig`, `[ProjectScope(Right = Write)]` | Доступ к проекту и право записи — на входе, до чтения файла. Путь — `FileService.SafeJoinPublic(root, path)` (`:589`). |
| Токен | `FilesController.cs:600`, `JwtService.cs:105-122` | `IssueOfficeToken(UserId, projectId, path)`: подписанный JWT, срок сутки, claims `sub` (кто открыл), `oo_pid`, `oo_path`. Выдаётся **того, кто открыл**; в нём нет «роли» и ревизии. |
| Ключ документа | `FilesController.cs:604-613` | `key = SHA256(projectId/path/mtimeTicks[/cacheKey])[..20] + "-" + mode`. **Пользователя в ключе нет** — два человека на одном файле с одним mtime получают один ключ. |
| Адреса | `FilesController.cs:617-622` | `url` = `office-download?path&token`, `callbackUrl` = `office-callback?path&token` (только `mode=edit`; Visio всегда `view`, `:593-595`). |
| Запись | `FilesController.cs:684-740` `OfficeCallback`, `[AllowAnonymous]` | DS сам зовёт его POST'ом. Проверки: (а) `jwt.ValidateOfficeToken(token, projectId, path)` (`:693`, `JwtService.cs:127-153`) — подпись, срок, привязка к проекту и пути, возвращает `sub`; (б) `status ∈ {2, 6}` и есть `url`; (в) `IsAllowedCallbackSource(url)` (`:717`, SSRF: хост из `OnlyOffice:ServerUrl` / `AllowedCallbackHosts`); (г) `GetProjectByOfficeToken(projectId, ownerId)` (`:721`, `:51-56`) — **владелец токена обязан иметь право `Write` в проекте** (`access.Resolve(..., Write)`), права проверяются в момент записи, не выдачи. |
| На диск | `FilesController.cs:724` → `FileService.WriteFileBytes` (`FileService.cs:430-437`) | `SafeJoin` + `OpenForWrite` + `Write` на месте (без временного файла) + `NotifyMutated`. **Предусловия нет** (ни хеша, ни mtime, ни проверки «файл менялся с момента открытия»). Проверка владельца — только п. (г), на уровне проекта; на уровне файла/ревизии — никакой. |
| Служебное | `FilesController.cs:745-756` `OfficeDiscard`, `:763-800` `OfficeForceSave`, `:815-` `OfficeVersion` | `discard` помечает ключ «выбросить» **и откатывает весь файл через `files.RevertFile` (git)** (`:752`); `force-save` шлёт `forcesave` в Command API DS и ждёт callback до 10 с. Реестр открытых сессий `_activeEditKeys` — **одна ячейка на файл** (`:615`): второй открывший затирает ключ первого. |

**Соседний путь — WebDAV (Word/Excel настольные), не OnlyOffice**, но он пишет в те же файлы: `WebDavHandler.cs:153` → `HandlePutAsync` (`:596-686`): тело во временный файл рядом, `File.Move(tmp, abs, overwrite: true)` (`:652`). Проверки: проект ищется среди **своих** проектов (`:127` `GetByOwner(davUserId)` — участнику общего проекта он сегодня не виден), путь — `SafeJoinPublic`. `If-Match`, токен блокировки и ETag **не сверяются** (LOCK ведёт реестр `_locks`, PUT его не читает), ответ 201/204. Для S2c-1 это второй писатель без предусловия.

### 2. Ручной опыт: две вкладки одного документа

**Стенд.** Продуктового OnlyOffice на машине нет: порт `:8090` занят `llama.cpp` (`Server: llama.cpp`), контейнера `onlyoffice-ds` нет, образа не было. Поэтому подняли **отдельный** DS на `:18090` (`onlyoffice/documentserver:latest`, версия из лога `10.0.0 build 169`, `JWT_ENABLED=false` и `production-linux.json` из репозитория — как в `docker-compose.yml`) и крошечный стенд-хост на `:18091` (Python), который повторяет только то, что делает наш `OfficeConfig`/`OfficeCallback`: отдаёт docx, строит те же `document`/`editorConfig.callbackUrl`, на `status 2|6` скачивает `url` и перезаписывает файл без проверок. Два браузерных контекста Playwright (Chromium, отдельные контексты = два человека A и B), в каждом печать в документе. **Это не наш бэкенд и не наш фронт**: подтверждается поведение DS и наша схема «ключ → callback → запись», а не работа `FilesController` целиком. Контейнер, стенд и образ после опыта удалены.

Лог сервера-хоста (`HH:MM:SS` — местное время; `CB` — входящий callback DS):

```
=== Сценарий 1 (первый прогон): гонка с рестартом docservice — в зачёт не идёт ===
  (DS перезапустил docservice за секунду до подключения B; у A «ошибка документа», итог 'BASE BBBtext')

=== Сценарий 2: один key=k2-edit (так делает наш OfficeConfig для двух открывших с одним mtime) ===
11:22:41 CB /cb?u=A status=1 key=k2-edit users=['A']
11:23:04 CB /cb?u=B status=1 key=k2-edit users=['A', 'B']
11:23:31 CB /cb?u=A status=1 key=k2-edit users=['B']          # A закрыл вкладку
11:23:40 CB /cb?u=B status=2 key=k2-edit users=['B']          # B вышел последним
11:23:40   saved 25262 bytes (by cb /cb?u=B)
ИТОГ ФАЙЛА: ['BASE AAAtext BBBtext']                          # обе правки на месте

=== Сценарий 3а: два разных key (файл на диске сменил mtime между открытиями) ===
11:24:19 CB /cb?u=A status=1 key=k3a-edit users=['A']
11:24:41 CB /cb?u=B status=1 key=k3b-edit users=['B']          # не знает про A
11:25:12 CB /cb?u=A status=2 key=k3a-edit users=['A']          # A закрыл → файл = версия A
11:25:12   saved 25257 bytes (by cb /cb?u=A)
11:25:37 CB /cb?u=B status=2 key=k3b-edit users=['B']          # B закрыл → перезаписал версию A
11:25:37   saved 25257 bytes (by cb /cb?u=B)
ИТОГ ФАЙЛА: ['BASE BBBtext']                                  # правка A потеряна, ошибки нет

=== Сценарий 3б: внешняя запись при открытой сессии ===
11:25:58 CB /cb?u=A status=1 key=k4-edit users=['A']
EXTERNAL write of doc.docx (agent/WebDAV) while A is open      # файл на диске: 'BASE EXTERNAL-EDIT-BY-AGENT'
11:26:28 CB /cb?u=A status=2 key=k4-edit users=['A']
11:26:28   saved 25257 bytes (by cb /cb?u=A)
ИТОГ ФАЙЛА: ['BASE AAAtext']                                  # внешняя правка потеряна, ошибки нет
```

Что из этого следует:

- **Соредактирование есть** (сценарий 2): DS сам сливает правки по ключу; в UI у A и B видны оба пользователя. Пока ключ общий, конфликта «по содержимому» внутри документа не бывает.
- **`callbackUrl` у каждого свой, статус 1 идёт каждому на его адрес, а итоговое сохранение (`status 2`) — одному: тому, кто вышел последним.** В нашей схеме это токен последнего ушедшего: запись пройдёт под его правами, а автор в `FilesController` (`ownerId`) будет «последний вышедший», а не «автор правок». Приписывать правки пользователю по токену нельзя.
- Параллельные сессии (разные ключи) — **не конфликт, а потеря**: DS их не сводит, а наш `OfficeCallback` пишет «как есть».

### 3. Что это значит для S2c-1

S2c-1 (план: `If-Match: <sha256>` → `409 file_changed` в `FilesController` для записи файла) **Office-файлов не закроет**: у них своя точка записи (`office-callback`, `:724`), вызывает её не человек, а DS без заголовков; DS не умеет `If-Match` и не показывает человеку тело 409. Что нужно решить отдельно (пересмотр объёма S2c-1 или S2c-1b — решает постановщик):

1. **Где ставить предусловие.** В `office-config` (`:600-613`) запомнить базу открытия: хеш (или mtime+размер) файла по `docKey`. В `OfficeCallback` перед `:724` сравнить базу с диском. Совпало — пишем как сейчас. Не совпало — **не перезаписывать**: сохранить версию DS рядом копией, а в ответ DS отдать `{"error":0}` (иначе DS будет повторять callback; `{"error":1}` он трактует как сбой сохранения). Копия — в духе решения 28.10 «Сохранить мою копией» (`<имя> (<Имя участника>).<расширение>`); имя участника по токену ненадёжно (см. вывод выше про «последний вышедший») — брать из списка `users` в теле callback.
2. **Как выглядит конфликт в Office-файле.** Содержимое сливать нельзя и показывать построчный дифф бессмысленно: это zip с XML. Остаются «карточки версий» (как в макете для двоичных): «в проекте» (автор/время из `GitService.LastCommitAsync`) и «ваша из редактора» с кнопками «Оставить мою копией» / «Принять версию проекта». Показывается **после** закрытия редактора (по `office-version`/событию файла), а не посреди сессии: DS уже принял правки человека, и отказ в callback он ему не сообщит.
3. **Ключ документа.** Пока ключ зависит от mtime, внешняя запись уводит следующего в параллельную сессию. Варианты: ключ от `projectId+path` + «поколение», которое растёт только когда меняется содержимое **не из DS** (иначе сохранение DS меняет mtime и новая вкладка рвёт живую сессию); либо оставить как есть и ловить конфликт предусловием (п. 1). Второе дешевле и не меняет поведение сессии.
4. **Побочный риск общего проекта, не конфликт.** `OfficeDiscard` откатывает **весь файл** через git (`:752`): «Отмена» у A стирает всё, что B уже сохранил в той же сессии и что успело попасть на диск. В общем проекте нужен запрет на `discard`, пока в сессии больше одного участника, либо отказ от git-отката в пользу «не писать на диск» (в сессии DS он и так не пишет до callback). Реестр `_activeEditKeys` (одна ячейка на файл, `:615`) для двух человек тоже не годится: `force-save` и `discard` адресуются только последнему открывшему.
5. **WebDAV PUT** (`WebDavHandler.cs:596-686`) — второй писатель без предусловия: нужен `If-Match` (ETag там уже выдаётся в GET, `:574`) и проверка токена блокировки, иначе Word-пользователь тихо перезапишет правки редактора. В общем проекте WebDAV участнику не виден вовсе (`:127`) — это решается шагом S0, а не S2c.

**Чего не проверено.** Работа нашего `FilesController` против живого DS на этом стенде (нужен собранный фронт и стенд проекта, а DS в продуктовом `:8090` не поднят); поведение DS при `JWT_ENABLED=true` и при перезапуске docservice посреди сессии (сценарий 1 показал гонку, повторять не стали); обработка `{"error":1}` ответа callback (по документации DS повторяет, не прогонялось); лимиты числа одновременных редакторов.
