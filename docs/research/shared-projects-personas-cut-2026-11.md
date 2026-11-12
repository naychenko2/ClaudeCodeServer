# Разрез: проектные персоны общего проекта — волна 3 (2026-11-05)

Автор — Александр (тимлид/архитектор), задача-разрез `faf91d99`. Основание — дефект на проде 05.11:
в общем проекте «Росмарафон» (`99e46960`, владелец `a300cc8f`, редактор `de3f0208`) участник не
видит команду проекта, а «Новый чат» падает `POST /api/personas/b8161c0c…/chats` → 404 и молчит в UI.

Решение уже принято в [основном разрезе](shared-projects-cut-2026-10.md) (§2.1, §2.4, таблица
«Персоны» в §6): **проектные персоны едины**, редактор правит их и привязки, кроме MCP и плагинов.
В коде волн 1–2 это не доделано: человеческие REST-пути отбирают персону по `OwnerId == UserId`
(вызывающий), а не по праву на проект. Агентские пути (MCP под токеном арендатора, слой персоны в
ходе) ключуются арендатором и уже работают — их волна 3 почти не трогает.

Работающее правило в коде (после волны 3) — [personas.md](../architecture/personas.md),
раздел «Персоны в общем проекте».

## 1. Рекомендация в одном абзаце

Завести **одну точку** «персона → область запроса» рядом с `IProjectAccess.ResolveSession`:
`IProjectAccess.ResolvePersona(callerUserId, personaId, right) → RequestScope?`. Проектная персона
резолвится через доступ к своему проекту, глобальная и сферная — только по владению (fail-closed).
Дальше **ключ хранилищ — `scope.TenantOwnerId`**, как у задач. Фильтр действия — новое значение
`ScopeSource.Persona` у существующего `[ProjectScope]`, а не новый атрибут. Инлайновые
`_personas.Get(id, UserId)` в человеческих путях уходят, сторож не даёт их вернуть.

## 2. Правило доступа

`ResolvePersona(caller, personaId, right)`:

1. Персоны нет или `caller` пуст → `null` (404, «нет» и «чужое» неотличимы).
2. `persona.Scope == Project` и `persona.ProjectId` указывает на живой проект →
   `Resolve(caller, persona.ProjectId, right)`; дополнительно `persona.OwnerId == scope.TenantOwnerId`
   (иначе `null`: персона «проекта» с чужим владельцем — битая запись, а не доступ).
3. Иначе (глобальная, сферная, проектная с исчезнувшим проектом) → только
   `persona.OwnerId == caller`, роль `Owner`; участнику — `null`. **Глобальные и сферные персоны
   владельца участнику не видны никогда**, в том числе через список.

**Решение Андрея 12.11 (волна 4):** в общем проекте редактор проектную персону только смотрит (`Read`) и разговаривает с ней (`RunTurn`); правка карточки, памяти и привязок — `Configure`, только владелец. Агент участника — ровно права своего человека. Автоматическая память из чатов редактора продолжает пополняться (`PersonaMemoryAutolearnService`).

Матрица (права — существующий `ProjectRight`, новых значений не заводим):

| Действие над проектной персоной общего проекта | Право | Владелец | Редактор | Читатель |
|---|---|---|---|---|
| Видеть персону, аватар, привязки, правила автоматики, память, фокус, её чаты (кроме чужих приватных) | `Read` | ✓ | ✓ | ✓ |
| Новый чат с персоной, групповой чат, смена собеседника в своём чате | `RunTurn` | ✓ | ✓ | — |
| Создать проектную персону в этом проекте; править карточку (имя, роль, характер, тон, приветствие, голос, аватар); память персоны — записать/исправить/забыть/подтвердить, сбросить фокус | `Configure` | ✓ | — | — |
| Привязки: добавить/изменить/удалить (в том числе MCP и плагины); подбор привязок ИИ | `Configure` (сужение §4, И-4 осталось вторым рубежом) | ✓ | — | — |
| Привязки MCP и плагинов, выдача MCP-сервера персоне, установка навыка персоне | `Configure` | ✓ | — | — |
| Поля зоны и денег: `Scope`, `ProjectId`, `SphereId`, профиль доступа `Access`, модель/уровень/усилие/матрица уровней | `Configure` | ✓ | — | — |
| Удалить персону; сделать руководителем проекта (`make-default`) | `Configure` | ✓ | — | — |
| Правила автоматики: создать/изменить/удалить/тест/подбор | `Configure` | ✓ | — | — |
| Память ↔ заметка (`to-note`, `from-note`) — ходит в личный vault ключа | `Configure` | ✓ | — | — |

Почему так, а не «редактор может всё, кроме MCP»:

- **Удаление** уносит память персоны и рвёт собеседника у чужих чатов — необратимо, это `Configure`
  по определению права.
- **Модель и профиль доступа** — деньги и права арендатора; участнику строки модели уже выключены
  (S2a-F3a), персона не должна стать обходом.
- **Автоматика** запускает ходы по расписанию за счёт арендатора с неизвестным инициатором (основной
  разрез, «Исполнители задач и автоматика»). Открывать её редактору — отдельное решение, не дефект.
- **`to-note`/`from-note`** с ключом арендатора писали бы в личный vault владельца — прямая утечка.

Читатель «пишет в чаты, если у него есть право хода»: сегодня `RunTurn` у читателя нет, поэтому
новый чат читателю — отказ (403, а не 404: персону он видит). Если матрица ролей когда-нибудь даст
читателю `RunTurn`, правка не понадобится — проверка идёт по праву, а не по роли.

## 3. Таблица путей входа

Обозначения: **Р** — правило резолва (`RP(right)` = `ResolvePersona` с правом; `RJ(right)` =
`Resolve(caller, projectId, right)` по проекту из запроса/тела); **К** — ключ дальнейших вызовов
хранилищ (`T` = `scope.TenantOwnerId`); **Шаг** — какой шаг плана закрывает путь (§6).
Строки «без изменений» перечислены намеренно: путь проверен, ключ там уже арендатор или пространство
личное.

### 3.1 REST `PersonasController` (`backend/ClaudeHomeServer/Controllers/PersonasController.cs`)

| Путь | Сейчас | Р | К | Шаг |
|---|---|---|---|---|
| `GET /api/personas` без scope (стор фронта `lib/personas.ts`) | `GetByOwner(UserId)` :113 | свои + проектные персоны проектов, где caller участник (`Read`) | — | B2 |
| `GET ?scope=project&projectId` (Командный центр, `DeleteDialog`) | `GetProjectTeam(UserId,…)` :107 | `RJ(Read)` | `T` | B2 |
| `GET ?scope=context&projectId` (композер `ChatPanel.tsx:690`, `GroupParticipantsPopover`, `ExecutorPicker`, `TaskEditForm`, `DossierHistoryPanel`) | `GetForContextWithExtras(UserId,…)` :103 | `RJ(Read)`; участнику — только команда проекта (глобальных нет, в общем проекте их рамка всё равно режет) | `T` | B2 |
| `GET ?scope=sphere`, `?scope=global` | по владельцу | без изменений (сферы и глобальные — личное) | — | — |
| `POST /match` с `projectId` | `GetForContext(UserId,…)` :127 | `RJ(Read)`, кандидаты — команда проекта | `T` | B2 |
| `GET /{id}` | `Get(id, UserId)` :200 | `RP(Read)` | — | B1 |
| `POST /` (создать) и `POST /ai/quick-create` с `scope=project` | `ValidProject(UserId,…)` в `PersonasCrudService` :71, контроллер :510 | `RJ(Configure)`; `OwnerId` новой персоны = `T`, не caller | `T` | B3 |
| `POST /ai/team` | `AiTeamAsync(UserId, projectId,…)` :581 | `RJ(Configure)` | `T` | B3 |
| `PUT /{id}` | `UpdateAsync(UserId,…)` :210 | `RP(Configure)` + отказ 403 на поля зоны/денег без `Configure` (§2) | `T` | B3 |
| `DELETE /{id}` | `DeleteAsync(UserId,…)` :214 | `RP(Configure)` | `T` | B1 |
| `POST /{id}/make-default` | :222 | `RP(Configure)` | `T` | B1 |
| `GET /{id}/chats` | `Get` :228 + `GetPersonaChats(UserId,…)` | `RP(Read)`; список — `GetPersonaChats(T, id, viewer: caller)` (И-5) | `T` | B4 |
| **`POST /{id}/chats`** (дефект) | `Get` :236 + `CreatePersonaChatAsync(UserId,…)` :248 | `RP(RunTurn)`; `CreatePersonaChatAsync(T,…, createdByUserId: caller)` | `T` | B4 |
| `POST /{id}/avatar/generate`, `/avatar/select` | `_crud.*(UserId,…)` :283, :299 | `RP(Configure)` | `T` | B3 |
| `GET /{id}/avatar`, `/avatar/original`, `/avatar/candidate/{file}` | `Get` :289, :308, :347 | `RP(Read)` | — | B1 |
| `POST /{id}/avatar/upload`, `/avatar/recrop` | `Get` :366/:396, `SetAvatar*(id, UserId,…)` :385/:410 | `RP(Configure)` | `T` | B3 |
| `PUT /{id}/voice` | :631 | `RP(Configure)` | `T` | B3 |
| `GET /{id}/bindings` | `Get` :590 | `RP(Read)` | — | B1 |
| `POST/PUT/DELETE /{id}/bindings[/{bindingId}]` | `Get` + `ValidateAsync(UserId,…)` + `UpdateBindings(id, UserId,…)` :596–:680 | `RP(Configure)` (решение 12.11; И-4 — второй рубеж) | `T` | B5 |
| `POST /{id}/bindings/suggest`, `/generate`, `/apply-defaults` | :1276–:1340 | `RP(Configure)` (решение 12.11) | `T` | B5 |
| `GET /binding-targets?type&personaId` | по `UserId` :1093–:1160 | если персона чужого арендатора — только цели этого проекта (И-4), иначе как сейчас | `T` | B5 |
| `GET/POST/PUT/DELETE /{id}/automation…`, `/test`, `/suggest`, `/generate` | `Get` :689–:834, `UpdateRules(id, UserId,…)` | чтение `RP(Read)`, всё прочее `RP(Configure)` | `T` | B1 |
| `GET /{id}/memory`, `/memory/search`, `/memory/recall`, `GET /{id}/focus` | `Get` + `_memory.*(UserId,…)` :1429–:1480 | `RP(Read)` | `T` | B1 |
| `POST /{id}/memory`, `PUT/DELETE /{id}/memory/{entryId}`, `/confirm`, `DELETE /{id}/focus` | :1461–:1525 | `RP(Configure)` | `T` | B1 |
| `POST /{id}/memory/{entryId}/to-note`, `/memory/from-note` | :1532, :1543 | `RP(Configure)` | `T` | B1 |
| `POST /ask` | агентский путь (`DenyOnDelegatedTurn`, токен арендатора) | без изменений | — | — |
| `GET/POST /pantheon…`, `/avatar/caps`, `/ai/voice/candidates`, `/ai/character`, `/bindings/ai-condition`, `/knowledge-search` | личное пространство caller | без изменений | — | — |
| Рассылка `PersonasChangedMessage` — `Broadcast` :88 (`Group("user_"+UserId)`) и `PersonasCrudService.Broadcast` :57 (`ToOwner(userId)`) | только вызывающему | проектная персона → `ToProject(persona.ProjectId)` **и** `ToOwner(T)`; прочие — как сейчас | — | B6 |

### 3.2 Чаты и задачи вне `PersonasController`

| Путь | Сейчас | Р | К | Шаг |
|---|---|---|---|---|
| `POST /api/projects/{pid}/sessions` без `personaId` (`SessionsController.cs:71`) | руководитель по `Get(leadId, UserId)` → не нашёлся → `provisioner.EnsureAsync(UserId)` заводит **глобального** ассистента участника → `GlobalPersonaInShared` 400 | руководитель по `T`; в общем проекте у не-владельца провижна нет — 400 `no_project_persona` с текстом «В проекте нет руководителя — попросите владельца назначить» | `T` | B4 |
| `POST /api/projects/{pid}/sessions` с `personaId` (`SessionsController.cs:94–95`) | двойной поиск `Get(id, UserId) ?? Get(id, tenantId)` — вторая точка правды | `RP(RunTurn)` и сверка `persona.ProjectId == pid` для проектной | `T` | B4 |
| `POST /api/projects/{pid}/sessions/{sid}/persona` (`SessionsController.cs:198`) → `SessionManager.SetPersona` (`ResolveOwnerId != ownerId` → null) | участнику 404 | `[ProjectScope(Right = RunTurn)]`, персона `RP(Read)` в этом проекте, вызов `SetPersona(sid, T,…)`; менять собеседника можно в чате, автор которого caller, владельцу — в любом (как прочие настройки чата, S2a-10b `owner_settings`) | `T` | B4 |
| `POST /api/chats/{id}/persona` (`ChatsController.cs:417`) | чаты вне проекта | без изменений (личное) | — | — |
| `POST /api/chats/group`, `PUT /api/chats/{id}/participants` (`ChatsController.cs:176`, `:207`) → `CreateGroupChatAsync`/`SetParticipants` (`ValidateParticipants(ownerId,…)`) | по `UserId` | ведущая — проектная персона: `RP(RunTurn)`, все участники — того же проекта и того же `T`; `CreateGroupChatAsync(T,…, createdByUserId: caller)` | `T` | B4 |
| `SessionManager.CreatePersonaChatAsync` (`SessionManager.cs:4377`) | проектная персона, но `project.OwnerId != ownerId` → **молча** создаёт чат ВНЕ проекта | проектная персона с проектом чужого владельца — исключение, а не фолбэк в личный чат (И-6) | — | B4 |
| `POST /api/tasks`, `PUT /api/tasks/{id}`, `POST /api/projects/{pid}/tasks` с `PersonaId`/`CreatedByPersonaId` (`TasksController.cs:111`, `:120`, `:614`; `TaskPersonaValidator.Error(personas, UserId,…)`) | редактор не может назначить задачу проектной персоне | персона резолвится `RP(Read)` в проекте задачи, валидатор — с `T` | `T` | B7 |
| `GET /api/models/…?personaId` (`ModelsController.cs:133`) — модель для превью в композере | `Get(personaId, UserId)` | `RP(Read)`; ключ `T` | `T` | B7 |
| `GET /api/models/…` по задаче (`ModelsController.cs:121`) | отказ раньше на `task.OwnerId != ownerId` | без изменений (вне волны: превью модели исполнителя задачи участнику) | — | — |
| `POST /api/skills/suggest` с `PersonaId`, `POST /api/personas/{id}/skills` (`SkillsController.cs:148`, `:182`) | по `UserId` | навыки — библиотека владельца: `RP(Configure)` | `T` | B5 |
| `ProjectsController.cs:52` (`DefaultPersonaId` в DTO проекта) | уже по `p.OwnerId` | без изменений — **отсюда участник и получает id руководителя, с которым потом падает** | — | — |
| `ProjectsController.TeamMemoryWriteAllowed` (`:178`) | заголовок персоны-вызывающего по `UserId` | без изменений: человек ходит без заголовка, агент — под токеном арендатора | — | — |
| `McpServersController.cs:51`, `OnboardingController`, `AuthController`, `DefaultAssistantProvisioner` | личное пространство | без изменений | — | — |
| **исполнитель задачи и онбординг:** `TaskExecutionService` (чат исполнителя) и `OnboardingController.cs:120` (онбординг проекта ведёт личную дефолт-персону владельца) | чат исполнителя создаётся `SessionManager.CreateAsync` без рамки S2a-8 — сама рамка это оговаривает (`SessionManager.cs:5310`): глобальная персона владельца садится в чат общего проекта | — | — | **хвост, вне волны** (вердикт финала a6f43197): рамка S2a-8 — `EnsurePersonasVisibleInFrame` рядом с `OutOfZoneRefusal` (`TaskExecutionService.cs:355`) и отказ глобальной персоне в валидаторе для проекта с узкой рамкой (тест `SharedProjectPersonaTaskTests.cs:250-254` перевернуть — следует из принятого S2a-8); онбординг — вести руководителем проекта или отказывать (решение Андрея, вне волны) |

### 3.3 Хаб, MCP, фон

| Путь | Сейчас | Решение волны 3 |
|---|---|---|
| `SessionHub` | методов, адресующих персону по id вызывающего, нет (`RespondTeamPlanAsync` — штаб, `Configure`) | без изменений |
| MCP `personas_list/get/bindings_list/automation_list/mcp_list` (`PersonasToolset.cs`; `personas_suggest_bindings` с 12.11 — `OwnerOnly`) | токен арендатора; узкая рамка режет глобальных и сферных (S2a-3b) | без изменений |
| MCP `personas_create/update/delete/bindings_set/mcp_grant/automation_*/generate_avatar/set_default/persona_ask/ai_team` | `ActorToolRights`: `OwnerOnly` для актора-участника | **без изменений в волне 3**: агент участника остаётся уже человека-редактора (правило «агент не больше человека» соблюдено). Решение Андрея 12.11: остаётся `OwnerOnly`; `personas_suggest_bindings` тоже переведён в `OwnerOnly` (W4-A1) |
| MCP `memory`: `memory_*` персоны, `team_memory_*` | токен арендатора; запись `OwnerOnly` для участника | без изменений |
| MCP `tasks_create`/`tasks_update`: `TaskPersonaGate` по актору | валидатор звался прямо по ключу арендатора, мимо `ResolvePersona` (`TasksToolset.cs:262-267`, `:443` до правки): агент актора-участника принимал глобальную персону владельца — И-1/И-3, найдено финалом a6f43197 | в обоих местах звать `TaskPersonaGate.Denied(…, callerUserId: TurnActorId(session.Id, context.OwnerId), …)` — тот же гейт, что REST (`TasksController.cs:1847`); тест — актор-участник + глобальная персона владельца дают отказ. Сделано, `c034655df`. **Исключение `selfPersonaId`** (принято финалом dd9d9213): это `session.PersonaId` (`TasksToolset.cs:111`), он идёт только в метку автора `CreatedByPersonaId` (`TasksToolset.cs:313`); персона чата проверена гейтом при его создании (`SessionsController.cs:101`, `ResolvePersona` с правом `RunTurn`), а пробуждение постановщика идёт в тот же `LinkedSessionId` (`TaskExecutionService.cs:1754`, `:2195`). Перевод на актора не принимается: редактор потерял бы право ставить задачи в общем проекте без руководителя — такой чат ведёт личный ассистент владельца (`TasksToolset.cs:271-277`) |
| Слой персоны в ходе (`PersonaLayerContributor`), `@`-упоминания (`MentionTriggerSource`), автоматика (`PersonaAutomationService`), исполнитель задач (`TaskExecutionService`), штаб (`Services/Team/*`) | ключ — владелец сессии/задачи = арендатор | без изменений: проверено, что ключ уже `T` |
| Агентские .md-файлы персон (`PersonaAgentFileSync`), `McpToolWhitelist`, `LocalExecutorRoster` | по владельцу | без изменений (личное пространство арендатора) |

### 3.4 Фронт

| Место | Сейчас | Нужно | Шаг |
|---|---|---|---|
| `lib/personas.ts` — стор `api.personas.list()` (74 потребителя: шапки чатов, аватары, Командный центр) | только свои персоны | серверная правка B2 чинит без изменений кода; realtime — после B6 | — |
| `features/personas/TeamCommandCenter.tsx:67–68` | `usePersonas().filter(isProjectTeam)` — пусто у участника | чинится B2; спрятать у не-владельца «Сделать руководителем» и ИИ-сборку команды для читателя; у редактора ИИ-команда остаётся | F1 |
| `features/personas/ProjectPersonasPanel.tsx`, `PersonasPage.tsx`, форма персоны, вкладки привязок/автоматики/памяти | рассчитаны на владельца | гейт по `project.myRole`: читатель — всё только для чтения и без «Новый чат»; редактор — без удаления, руководителя, автоматики, MCP/плагин-привязок, полей модели/доступа/зоны, `to-note`/`from-note` | F1 |
| `components/SessionList.tsx:1325` — `onNew={() => { void createNew(); }}` | отказ `createChatWithContextPersona` — необработанный reject, **в UI тишина** | ловить ошибку и показывать `showToast('Новый чат', <текст сервера>)`; `OnboardingRequiredError` — штатный путь онбординга, как в других местах | F2 |
| Остальные потребители `createChatWithContextPersona` (`lib/ai/startChat.ts`, `features/home/QuickActions.tsx:118`, `RecentSessionsWidget.tsx`) | проверить, что отказ виден | тот же обработчик; новый код ошибки `no_project_persona` — текст сервера | F2 |
| Выбор персоны в композере (`ChatPanel.tsx:690`) | пустой список у участника | чинится B2; смена собеседника — по правилу B4 (в чужом чате пункт выключен с подсказкой, как «Переименовать» в S1-F9) | F1 |

## 4. Инварианты под угрозой

- **И-1. Одна точка правды «персона → область».** Только `IProjectAccess.ResolvePersona`; ни
  инлайнового `persona.OwnerId == UserId` для проектной персоны, ни двойного поиска «по caller,
  потом по арендатору» (как сейчас `SessionsController.cs:94–95` — его убрать, а не размножить).
- **И-2. Ключ — арендатор.** После резолва все вызовы `PersonaManager`, `PersonaMemoryService`,
  `PersonaBindingsService`, `PersonasCrudService`, `SessionManager` получают `scope.TenantOwnerId`,
  `CallerUserId` идёт только в автора (`createdByUserId`) — ловушка ключа (основной разрез §6.6).
- **И-3. Fail-closed для личного.** Глобальная и сферная персона владельца участнику не видна ни в
  одном списке и ни по одному `{id}`: 404, как несуществующая.
- **И-4. Привязки не выносят личное владельца.** С ключом арендатора `ValidateAsync` пропустил бы
  цели из личного пространства владельца (`Notes=personal`, датасеты, другие его проекты), а
  `binding-targets` показал бы их редактору. Правило для не-владельца: добавлять и менять можно
  только цели этого проекта (`Project`/`ProjectPath`/`ProjectPersonas`/`ProjectTasks` = этот
  `projectId`, `Notes` = этот `projectId`, `Knowledge` = датасет проекта, `Tool` без `mcp:`,
  `Skill` без `plugin:`); существующие привязки владельца к личным целям редактор видит, но не
  меняет и не удаляет. Точка — один метод в `PersonaBindingsService`, а не проверки по контроллеру.
- **И-5. Чужие приватные чаты.** `GetPersonaChats` фильтрует `VisibleTo(s, viewer)` — после перехода
  на ключ арендатора `viewer` обязан остаться `caller`; иначе участник увидит приватные чаты владельца
  с этой персоной.
- **И-6. Нет тихого фолбэка в личный чат.** `CreatePersonaChatAsync`: проектная персона, чей проект
  принадлежит не `ownerId`, — исключение. Сегодня это молча создаёт чат ВНЕ проекта у того, кто
  передал `ownerId`; с ключом арендатора это стало бы чатом в личном пространстве владельца от лица
  участника.
- **И-7. Владелец персоны не меняется.** Создание редактором — `OwnerId = T`; правка не может
  менять `Scope/ProjectId/SphereId` (иначе редактор переносит персону в другой проект владельца,
  `ValidProject(T,…)` это пропустит).
- **И-8. Рассылки.** `personas_changed` по проектной персоне — в проект, иначе стор участника
  протухает до перезагрузки; события личных персон в проект не уходят.

## 5. Сторож

Два слоя, оба в `ClaudeHomeServer.Tests`:

1. **Покрытие маршрутов (default-deny), как `ActorToolRights`.** Тест рефлексией перебирает все
   действия `PersonasController` с `{id}` в шаблоне и требует у каждого действующий
   `[ProjectScope(From = ScopeSource.Persona, Right = …)]` (`ProjectScopeAttribute.For`). Новое
   действие без разметки — красный тест. Отдельный список «личных» маршрутов без `{id}` (пантеон,
   голос, ai/character…) — явный allow-list с причиной.
2. **Запрет инлайна.** Текстовый сторож по `Controllers/*.cs`: вызовы
   `_personas./personas.(Get|GetProjectTeam|GetForContext|GetForContextWithExtras|GetByHandle)(…, UserId…)`
   допустимы только в allow-list «личных» мест (пантеон, онбординг, `ChatsController` для чатов вне
   проекта, `McpServersController`). Новая строка вне списка — красный тест с подсказкой «через
   `ResolvePersona`».
   Слепые места сторожа честно (реализация `PersonaScopeGuardTests.cs:186-193, :208`): сторож видит
   **только пять методов** `PersonaManager` из перечня — других вызовов он не видит, в том числе через
   `_crud.*` (правка карточки, голос, выбор аватара, `ValidateAsync`/`UpdateBindings`, путь создания);
   **только `Controllers/*.cs`** — один уровень каталога `backend/ClaudeHomeServer/Controllers`,
   вызовы в `Services/` и `Hubs/` вне области; **только получатели `personas`/`_personas`** — поле или
   локальная переменная с другим именем невидимы; аргумент-ключ — только слово `userId` (`tenantId`,
   `OwnerId` нарушением не считаются). Смысл непокрытых участков держит поведенческая матрица (п.3),
   а не текстовый сторож.
3. **Поведенческая матрица** (`[Trait("Category","Host")]`, образец —
   `SharedProjectChatCreateTests`, `SharedProjectTaskWriteRightsTests`): владелец/редактор/читатель/
   посторонний × проектная персона общего проекта × {GET `{id}`, GET список `scope=project`, POST
   `{id}/chats`, PUT `{id}`, PUT поле модели, DELETE, POST привязка `mcp:*`, POST привязка
   `Notes=personal`, POST `to-note`} плюс «глобальная персона владельца участнику — 404 и нет в
   списке». Мутация: вернуть `Get(id, UserId)` в `CreateChat` — матрица обязана покраснеть.

## 6. Шаги для плана (бэк — Денис, фронт — Кира)

Порядок: B1 → (B2, B3, B4, B5, B7 параллельно, все от B1) → B6 → F1, F2 (F2 независим, можно сразу).

| Шаг | Что | Файлы | Готово, когда |
|---|---|---|---|
| **B1. Точка доступа и разметка** | `ResolvePersona` в `IProjectAccess` + реализация; `ScopeSource.Persona` в фильтре (ключ `id`); разметка всех `{id}`-действий `PersonasController` правами из §3.1; сторож-покрытие и текстовый сторож (§5 п.1–2) | `ClaudeHomeServer.Core/Services/Access/IProjectAccess.cs`, `ProjectScopeAttribute.cs`; `ClaudeHomeServer/Services/ProjectAccessService.cs` (+ `PersonaManager` в конструктор); `Controllers/PersonasController.cs` (замена `Get(id, UserId)` на `GetRequestScope()` и ключ `T` в память/автоматику/аватары); тесты в `ClaudeHomeServer.Tests/Services/Access/`, `…/Controllers/` | `ResolvePersona` покрыт юнитами (4 роли × 3 вида персоны); сторожа зелёные и краснеют на мутации |
| **B2. Списки** | `GET /api/personas` (без scope, `project`, `context`), `POST /match`: команда проекта по `RJ(Read)` и ключу `T`; безскоупный список — свои + проектные персоны проектов, где caller участник | `Controllers/PersonasController.cs` :95–:155; `Services/PersonaManager.cs` (`GetProjectTeam`, `GetForContext*` — вызываются с `T`; метод «проектные персоны проектов участника» — через `ProjectManager`/`ProjectAccess`, не новой выборкой по `OwnerId`) | у редактора и читателя Командный центр и композер показывают 4 персоны «Росмарафона»; глобальных владельца нет |
| **B3. Создание и правка карточки** | `Create`/`quick-create`/`ai/team` в чужой проект по `WriteContent` с `OwnerId = T`; `Update` с отказом 403 `owner_settings` на поля зоны/денег; голос, аватар | `Services/PersonasCrudService.cs` (`CreateAsync` :71, `UpdateAsync` :198–:201, аватар, голос), `Controllers/PersonasController.cs` :503–:581 | матрица §5 п.3 по PUT и полям модели |
| **B4. Чаты с персоной** | `POST/GET {id}/chats` с ключом `T` и `createdByUserId`; `GetPersonaChats(T, id, viewer)`; И-6 в `CreatePersonaChatAsync`; `SessionsController.Create` — руководитель по `T`, без провижна глобального для участника, код `no_project_persona`; убрать двойной поиск :94–95; смена собеседника в проектном чате; групповые чаты | `Services/SessionManager.cs` (`CreatePersonaChatAsync` :4355, `GetPersonaChats` :4530, `SetPersona` :5299, `CreateGroupChatAsync`, `SetParticipants`, `ValidateParticipants`); `Controllers/SessionsController.cs` :57–:110, :198; `Controllers/ChatsController.cs` :176, :207; `Controllers/PersonasController.cs` :225–:260 | на проде-сценарии: редактор жмёт «Новый чат» → чат в проекте, `OwnerId = a300cc8f`, `CreatedByUserId = de3f0208`, собеседник — Глеб Маршалов; читатель — 403 |
| **B5. Привязки и навыки** | И-4 одним методом в `PersonaBindingsService`; MCP/плагин — `Configure`; `binding-targets`/`suggest`/`generate`/`apply-defaults` сужены для не-владельца; навыки — `Configure` | `Services/PersonaBindingsService.cs` (`ValidateAsync` :1133), `Controllers/PersonasController.cs` :587–:680, :1093–:1420; `Controllers/SkillsController.cs` :148, :182 | матрица: редактор `mcp:*` → 403, `Notes=personal` → 400, проектная заметка → 200 |
| **B6. Рассылки** | `personas_changed` (и `memory`-событие) проектной персоны — `ToProject` + владельцу | `Controllers/PersonasController.cs` :87–:89, `Services/PersonasCrudService.cs` :57 | тест доставки с `JoinProject` (образец S1-5) |
| **B7. Задачи и модели** | персона-исполнитель/автор задачи резолвится в проекте задачи с ключом `T`; превью модели персоны | `Controllers/TasksController.cs` :109–:122, :612–:616; `TaskPersonaValidator`; `Controllers/ModelsController.cs` :133 | редактор назначает задачу проектной персоне |
| **F1. Гейты UI по роли** | Командный центр, панель и страница персон, форма персоны, вкладки привязок/автоматики/памяти, смена собеседника в композере — по `project.myRole` (§3.4) | `features/personas/TeamCommandCenter.tsx`, `ProjectPersonasPanel.tsx`, `PersonasPage.tsx`, форма и вкладки персоны (`features/personas/*`), `components/ChatPanel.tsx` | читатель не видит активных кнопок записи; редактор — без owner-only; `npm run lint:design` зелёный |
| **F2. Отказ нового чата не молчит** | обработка ошибки в `SessionList.onNew` и остальных потребителях `createChatWithContextPersona` | `components/SessionList.tsx:422–:428, :1325`, `lib/defaultPersona.ts`, `lib/ai/startChat.ts`, `features/home/QuickActions.tsx`, `features/home/RecentSessionsWidget.tsx` | vitest: отказ API → тост с текстом сервера |

Размер: бэк — M (B1 и B4 — основная масса), фронт — S. Локальным исполнителям резать по шагам
как выше: каждый шаг видит свои файлы целиком, швы между ними — B1 (резолвер) и И-2 (ключ `T`).

## 7. Открытые вопросы к Андрею (с рекомендацией по умолчанию)

Решений Андрея от 05.11 именно по этим трём вопросам в памяти команды и карточках волны нет
(записи 05.11 — о приватных вложениях, токене на ход участника и удалении задачи). Статус по
состоянию кода на 06.11 — ниже каждого пункта; где решения нет, вопрос остаётся **открыт**.

1. **Агент редактора правит персону через MCP?** **Закрыт: нет (решение Андрея 12.11).** В коде работает по умолчанию
   волны 3: `OwnerOnly` для актора-участника (таблица прав `ActorRightGate.cs:141-147`,
   единый гейт тулсетов `McpTransportController.cs:261`). Финал
   a6f43197 (06.11): блокером не считает — агент редактора и так уже человека-редактора;
   закрыть совсем — одна строка в `NonOwnerBindingEditDenied`.
2. **Редактор заводит автоматику персоны?** **Закрыт: нет (решение Андрея 12.11).** В коде работает по умолчанию:
   автоматика — `Configure`, только владельцу (таблица прав сторожа `PersonaScopeGuardTests.cs:59-66`,
   матрица §2 выше).
3. **Участник без руководителя проекта:** 400 `no_project_persona` с понятным текстом, а не
   глобальный ассистент участника (его в общем проекте всё равно отбивает рамка). По умолчанию — так.
   **В коде закрыто по умолчанию** (W3-B4b, коммит f915559ce: `SessionsController.cs:208`, ревью
   082a5124 принято; тексты фронта — W3-F2a). **Решения Андрея нет** — формально открыт.
