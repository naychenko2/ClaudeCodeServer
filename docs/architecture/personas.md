# Персоны

> Подробная документация подсистемы. Выжимка и инварианты — в [CLAUDE.md](../../CLAUDE.md),
> раздел «Персоны». Читать перед правками в `PersonaManager`, `PersonaPromptBuilder`,
> памяти персон, групповых чатах, `features/personas/` и MCP-серверах personas/memory.

Концепция **«Персоны = контакты, Чаты = разговоры»** (фич-флаг `personas`): глобальный раздел
«Персоны» (хаб-таб) и вкладка «Команда» внутри проекта — **только настройка** (профиль + память);
разговор с агентом живёт среди обычных чатов и везде помечен его лицом (аватар/роль/цвет).
Персона — **отдельная сущность** (JSON-стор, не .md-агент): роль (главная в отображении:
«Роль (Имя)»), имя, характер, аватар, модель/усилие, зона, приветствие, долгая память.
Изоляция per-owner (как задачи/заметки).

- **Модель**: [Persona.cs](../../backend/ClaudeHomeServer/Models/Persona.cs) — `Persona`
  (Name, Role, Handle, Description, SystemPrompt, Model/Effort, Scope `Global|Project`, ProjectId,
  Avatar `{Kind initials|image, Color, ImageFile}`, Voice `{Voice, Role, Speed}`, Greeting,
  MemoryEnabled) +
  `PersonaMemoryEntry` (Type `Semantic|Episodic|Procedural`, Text, Tags, Salience). Хранилище —
  `data/personas.json`, ассеты (аватары) — `data/personas/{id}/`.
- **CRUD**: [PersonaManager.cs](../../backend/ClaudeHomeServer/Services/PersonaManager.cs) — per-owner
  (`Get(id, userId)` проверяет OwnerId), генерация уникального slug-`Handle`.
  [PersonasController.cs](../../backend/ClaudeHomeServer/Controllers/PersonasController.cs) —
  `/api/personas/*` (CRUD с фильтрами `?scope=context|project|global&projectId=`, `{id}/chats`,
  `{id}/memory*`, `{id}/avatar*`, `ai/character` — LLM-генерация/улучшение характера с
  уточняющим промптом); realtime — `PersonasChangedMessage` (created/updated/deleted/memory).
- **Голос**: `PUT /api/personas/{id}/voice` — `{voice, role?, speed?}`; пустой объект снимает
  голос, и персона снова говорит голосом инстанса. Отдельным эндпоинтом, а не полем в
  `Update`: там уже больше двадцати позиционных параметров. Валидация строгая (400 на
  незнакомый голос, на амплуа, которого у голоса нет, и на темп вне 0.1–3.0) — в отличие от
  пути озвучки, где всё кривое молча вырождается в дефолт (`VoiceResolver`, см.
  [features.md](features.md), раздел «Голосовой режим чата»). Белый список голосов и их
  амплуа — `TtsVoiceCatalog`, замерено прямыми запросами к SpeechKit.
- **Чат с персоной**: `Session.PersonaId`; `SessionManager.CreatePersonaChatAsync` маршрутизирует
  по зоне — **глобальная** персона → чат вне проекта (scope = все данные владельца, `ProjectId=null`
  в tasks/notes MCP), **проектная** → сессия в её проекте (scope = только проект). Характер персоны
  инжектится в системный промпт как персональный слой ([ClaudeSession.cs](../../backend/ClaudeHomeServer/Services/Llm/Claude/ClaudeSession.cs),
  приоритет над .md-агентом); персона-слой (промпт+память) восстанавливается и после рестарта
  (`BuildPersonaLayer` в `EnsureProcessAsync`). Назначение/смена собеседника —
  `SessionManager.SetPersona` (`POST /chats/{id}/persona`, `POST .../sessions/{sid}/persona`),
  разрешена и ПО ХОДУ разговора: слой пересобирается каждый ход, транскрипт продолжается
  через `--resume`; модель персоны применяется только при том же провайдере;
  `Session.PersonaSwitched` добавляет в промпт оговорку про чужие прошлые ответы, на фронте
  локальный разделитель «Теперь отвечает: …». Scope НЕ требует спец-логики — он
  предопределён типом сессии.
- **Шаблоны ролей** ([personaTemplates.ts](../../frontend/src/features/personas/personaTemplates.ts)):
  6 готовых ролей с промптами-контрактами (Ревьюер, Планировщик, Аналитик, Ментор, Секретарь,
  Дизайнер) — сетка карточек на экране создания (PersonaQuickCreate), выбор предзаполняет
  PersonaForm (`initial`), включая дефолтные возможности, модель и усилие.
- **Пантеон OmO — подключаемая команда** (built-in-подход, как у самих OmO): каталог —
  [OmoPantheonCatalog.cs](../../backend/ClaudeHomeServer/Services/Prompts/OmoPantheonCatalog.cs)
  (8 ролей с ПОЛНЫМИ переведёнными промптами, по договорённости с авторами —
  [omo-adoption.md](../omo/adoption.md)): Оркестратор (Сизиф), Мастер (Гефест),
  Планировщик (Прометей), Координатор (Атлант), Аналитик (Метида), Ревьюер (Мом),
  Консультант (Оракул), Библиотекарь (Клио); регламенты — сгенерированный partial
  `OmoPantheonCatalog.Instructions.cs` (docs/omo/gen-omo-prompts.ps1 из переводов).
  Модель роль задаёт **уровнем** (`PantheonTemplate.ModelTier` → `Persona.ModelTier`), а не
  конкретной моделью: зашитый Claude-алиас уводил персону и её исполнителя задач на Claude
  даже на инстансе, целиком переведённом на стороннего провайдера (B3 приёмки «Командной
  реализации»). Пины старых подключений переносятся на уровень при загрузке
  (`PersonaManager.MigratePantheonModelPins`, только персоны с `TemplateKey`).
  `GET /api/personas/pantheon` — карточки + connectedPersonaId по `Persona.TemplateKey`;
  `POST /api/personas/pantheon/connect` {keys?} — идемпотентно создаёт ГЛОБАЛЬНЫЕ персоны
  с готовыми именами (советники — readOnly). **Роли видны всегда**: в селекторах собеседника,
  групповых чатах и диалоге «Обсудить с командой» отдельная группа «Пантеон OmO»
  (роли пантеона — персоны с непустым `templateKey`, см. [PersonaList.tsx](../../frontend/src/features/personas/PersonaList.tsx));
  при выборе роль тихо материализуется (`materializePantheon` → connect по ключу) — явной
  кнопки подключения нет. **Авто-обновление регламентов**: `Persona.TemplateInstructionsHash` —
  SHA-256 поставленной из каталога инструкции; при старте (`RefreshPantheonInstructions`)
  нетронутые (hash совпадает) подтягиваются из каталога, правленные пользователем — «пришпилены».
  Карточки-шаблоны в PersonaQuickCreate остаются вторым путём (кастомная копия с
  предзаполненным именем). Отключение роли = обычное удаление персоны.
- **Возможности per-persona** (`Persona.Tools`: ключи `tasks`/`notes`/`web`; null = без
  ограничений, полный набор нормализуется в null): гейт tasks/notes MCP при сборке
  LlmSessionContext; выключенный `web` добавляет WebSearch/WebFetch в
  `ExtraDisallowedTools` (поверх `Claude:DisallowedTools`). UI — секция «Возможности»
  (3 тумблера) в PersonaForm.
- **Рубильники MCP-серверов персоны** (`PersonaBindingsService.ServerKeys`: `personas`,
  `consultants`, `codegraph`, `widgets`; `notifications` — там же, но с дефолтом по роли,
  см. ниже): выключаются Off-привязкой
  (`type: tool`, `target: <ключ>`) и только ею — фолбэка на `Persona.Tools` у этих ключей нет
  (`ServerToolEnabled`, в отличие от `EffectiveToolEnabled`), иначе персона с суженным списком
  возможностей разом лишилась бы серверов, которые сегодня получает безусловно. Гейты стоят при
  сборке `LlmSessionContext`: `personas` снимает personas-server целиком (вместе с `persona_ask`),
  `consultants` — pmem-серверы консультантов, `--add-dir` с их .md-агентами и подсказку
  со списком коллег, `codegraph` — сервер графа и его выжимку в промпте, `notifications`
  и `widgets` — свои серверы. Подсказки в промпте условны от наличия сервера, отдельно
  их гейтить не нужно. Два инварианта: решение зависит ТОЛЬКО от персоны (состав `tools/list`
  не смеет мерцать между ходами — сторож `McpToolsetStabilityTests`), а в **групповом чате**
  ключи `consultants` и `personas` игнорируются (`ConsultantsEnabled`/`PersonasEnabled`) —
  спикер обязан уметь спросить коллег по чату, а `BuildGroupChatHint` безусловно отсылает
  к блоку о консультациях из personas-server.
- **Секции-надстройки с пресетом по роли** (`PersonaBindingsService.PresetKeys`: `git`, `kb`,
  `personas-manage`, `personas-automation`, `notes-annotations`, `browser`; решение —
  `SectionEnabled`): наборы инструментов,
  которые раньше ехали пакетом со своей базовой секцией и стоили контекста всем персонам
  подряд. `git` (wsp `git_*`) — надстройка над секцией `files`, `kb` (wsp `kb_*`) — над
  `knowledge`: без базовой секции не монтируются вовсе. `notes-annotations` (env
  `NOTES_ANNOTATIONS`) — модуль notes-server: комментарии к markdown-документам плюс редкие
  операции заметок (дневник, граф, обратные ссылки, удаление, промоут чекбокса, резолв
  `[[ссылки]]`, подсказка заголовка); ядро заметок остаётся у всех. Дефолт — выключен
  (0 вызовов за 14 дней наблюдений, см. «Диета состава» в [mcp-servers.md](mcp-servers.md)).
  `personas-manage`
  (`personas_create/update/delete/bindings_set/generate_avatar/ai_team`, env `PERSONAS_MANAGE`)
  и `personas-automation` (`personas_automation_*`, env `PERSONAS_AUTOMATION`, самые тяжёлые
  схемы `triggerArgs`) — модули personas-server поверх его ядра (`personas_list`/`personas_get`,
  привязки, `persona_ask`), которое остаётся у всех, у кого сервер включён. Порядок решения:
  явная Tool-привязка → `Persona.Tools`, **если он знает хоть один из этих ключей** (тогда
  остаётся белым списком; легаси-список из `tasks`/`notes`/`web` о них не подозревал и пресет
  не убивает) → **пресет по `Persona.Specialty`** (`SpecialtySections`):
  `Executor`/`Reviewer`/`Tester` → `git` (у ревьюера и тестировщика `Access=ReadOnly` отбирает
  ещё и `Bash`, так что wsp-git — единственный оставшийся канал к диффу),
  `Tester` вдобавок → `browser`,
  `Librarian` → `kb`, `Coordinator`/`Secretary` → `personas-manage` + `personas-automation`,
  остальные — только ядро. Не персонная сессия пресетов не знает — обычный чат получает всё.
  **Источник решения различим** (`SectionOrigin`: `Off`/`Preset`/`Explicit`): пресет по роли
  даёт wsp-git только на ЧТЕНИЕ (`git_status`/`git_diff`/`git_log`), а запись истории
  (`git_stage`/`git_commit`, секция `git_write`) добавляет лишь явно включённый ключ `git` —
  коммитят обычно через Bash, а ролям `ReadOnly` запись не нужна по определению.
  **Сервер уведомлений** (`NotificationsEnabled`) устроен так же: Off-привязка → `Persona.Tools`
  → модуль автоматизации по роли; персоне без роли его даёт только явная привязка. Секции `chats`
  в пресете намеренно нет: в ней живёт `chats_report_up` — канал отчёта исполнителя вверх
  по делегированию. Тот же инвариант, что у рубильников: решение зависит ТОЛЬКО от персоны
  (сторож — `McpToolsetStabilityTests`).
  Ключ `browser` — особый: инструменты браузера приходят не из нашего MCP-конфига, а двумя
  внешними каналами — плагином CLI `playwright@claude-plugins-official`
  (`mcp__plugin_playwright_playwright__*`; в профили провайдеров он разъехался синком
  `LlmProviderRegistry.SyncUserProfile`, копирующим `~/.claude/plugins`) и коннектором аккаунта
  claude.ai `microsoft/playwright-mcp`. Поэтому выключение закрывает оба: плагин гасится
  `enabledPlugins` в файле `--settings` хода (`ClaudeRuntimeSettings`), инструменты обоих —
  масками в `--disallowedTools` (`ClaudeSession.BrowserTools`). В песочнице `--settings`
  не передаётся вовсе, там работают только маски. Оба рычага действуют на ВЕСЬ процесс хода,
  поэтому решение берётся по персоне главной сессии: вызванный из неё файловый сабагент-консультант
  наследует её режим браузера, а не свой (то же ограничение, что у pmem-серверов).
- **@упоминания (флаг `persona-mentions`)**: надстройка над MCP персон — при включённом
  флаге и наличии других персон в контексте personas-server получает env `PERSONAS_MENTIONS=1`
  (регистрирует инструмент `persona_ask`) и `PERSONAS_SELF_ID`, а в промпт добавляется
  подсказка со списком «@handle — Роль (Имя)» (`BuildPersonasContext.MentionsHint`).
  `POST /api/personas/ask` — one-shot
  ответ персоны от её лица (слой `BuildPersonaPrompt` + recall памяти + выжимки
  Always-привязок + модель и effort персоны, `OneShotClaudeRunner`; таймаут
  `Persona:AskTimeoutMs`, дефолт 120с; после ответа консультация уходит в память
  персоны — `PersonaMemoryAutolearnService.LearnFromConsultation`, фокус не трогается).
  Анти-рекурсия по построению: one-shot без MCP, глубина делегирования 1. Фронт: автокомплит `@` в Composer
  ([MentionsDropdown.tsx](../../frontend/src/components/MentionsDropdown.tsx)). Handle персон
  транслитерируется из кириллицы (PersonaManager.Slugify).
- **Механики «Обсудить с командой»** (поверх @упоминаний): реестр и сборка текста хода —
  [teamMechanics.ts](../../frontend/src/features/team/teamMechanics.ts) (`buildTeamTurnText`),
  бэкенд и протокол не участвуют. Механики: дискуссия через @упоминания, workflow-скрипты
  с персонами в ролях (`/panel-of-experts`, `/review-consilium`, `/red-team`,
  `/team-implement` — participants/executors = handle персон) и `/oh-my-claudecode:*`
  (персоны туда подставляются хинтом OmcPersonaRouting); доступность механики гейтится
  наличием скилла (`requiredSkill`) и, если задан, фич-флагом (`featureFlag`).
  **Исключение — «Командная реализация»** (`implementMode`, флаг `team-implement-mode`):
  единственная механика без workflow-хода — она включает режим чата-штаба REST-вызовом
  (`PUT /chats/{id}/team-implement`), дальше работа идёт задачами на персон-исполнителей
  (см. [features.md](features.md#командная-реализация--режим-чата-флаг-team-implement-mode)).
  Прежний быстрый ход `/team-implement` остался под именем **«Командный спринт»** (id
  механики `implement` не менялся — по нему детектятся старые ходы в лентах). Старые серверные механики «Совещание» (P7) и
  «Конвейер пантеона» УДАЛЕНЫ вместе с DiscussTeamDialog/MeetingView/PipelineView;
  legacy `meeting_phase`/`pipeline_phase` в старых историях молча пропускаются
  (ChatHistoryService).
- **Контракт характера (P1) + дисциплина (P2)**: `Persona.Contract` (character/tone/mustDo/
  mustNot/outputFormat/speechExamples/instructions — слоты вместо единого текста; legacy
  `SystemPrompt` остаётся у персон без контракта; `instructions` — длинный регламент роли
  отдельной секцией «## Инструкция», в PersonaForm свёрнут при пустом). Единый сборщик
  промпта — [PersonaPromptBuilder.cs](../../backend/ClaudeHomeServer/Services/PersonaPromptBuilder.cs):
  идентичность + секции контракта + дисциплинарная обвязка по провайдеру модели секциями
  из model-веток OmO (Claude — краткость + прагматизм наименьшего изменения; DeepSeek —
  полный набор + самопроверка и намерение хода; GLM — калибровка пяти сбоев + outcome-first,
  без секции достоверности).
- **Память v2 (P3)**: скоринг взвешенной суммой ([PersonaMemoryScorer.cs](../../backend/ClaudeHomeServer/Services/PersonaMemoryScorer.cs)),
  reinforcement (Touch при recall) и **рабочий фокус** «что я сейчас делаю» (одна ячейка,
  в recall первым блоком; `GET/DELETE {id}/focus`). Autolearn выставляет salience и фокус;
  фоновая консолидация (LLM-merge дублей + вытеснение) — за флагом `persona-memory-consolidation`
  ([PersonaMemoryConsolidationService.cs](../../backend/ClaudeHomeServer/Services/PersonaMemoryConsolidationService.cs)).
- **Профили доступа (P6)**: `Persona.Access` — `full` | `readOnly` (смотрит и советует, ничего
  не меняет) | `custom` (свой список `DisallowedTools`); в disallowed-инструменты сессии их
  превращает [PersonaAccessPolicy.cs](../../backend/ClaudeHomeServer/Services/PersonaAccessPolicy.cs)
  (`BuildExtraDisallowed`, поверх ограничений `Tools`).
- **Персона-исполнитель задач**: `TaskItem.PersonaId` — задача выполняется силами Claude «от лица»
  персоны (её характер/модель/память). Инвариант `PersonaId != null ⇒ Assignee = Claude`
  (`TaskManager.NormalizePersonaAssignee` в Create/Update); `SpawnNextOccurrence` переносит
  `PersonaId` (регулярная задача не теряет исполнителя). Запуск — `TaskExecutionService` (сессия
  AcceptEdits с `personaId`, 6-секционный контракт `BuildPersonaPrompt`, уведомления от лица;
  деградация без персоны). Валидация — `TaskPersonaValidator` (персона владельца; проектная — только
  свой проект). **Три канала назначения** вокруг одного поля `personaId`: (1) UI — единый пикер
  «Исполнитель» ([ExecutorPicker.tsx](../../frontend/src/features/tasks/ExecutorPicker.tsx)) в форме и
  диалоге создания; (2) REST — `personaId` в POST/PUT задач + фильтр `GET /api/tasks?personaId=`;
  (3) MCP — `personaId` в `tasks_create`/`tasks_update` + `personas_list` для id (подсказка в
  промпте tasks-server при подключённом personas-server). Вкладка **«Задачи»** в студии персоны
  ([PersonaTasksPanel.tsx](../../frontend/src/features/personas/PersonaTasksPanel.tsx)) — отфильтрованный
  вид реальных задач (те же `TaskCard`), клик открывает задачу в её разделе (`openTaskInSection` →
  событие `cc-open-url`), кнопка «Поручить задачу» = `NewTaskDialog` с предзаполненным исполнителем;
  факт-чип «Задачи» на Обзоре. **Проактивность («пишет первой» по расписанию) удалена** — сценарий
  утреннего брифа покрывается регулярной задачей с персоной-исполнителем.
- **Групповой чат (флаг `persona-group-chats`)**: `Session.Participants` (2-4 id персон,
  первая — ведущая), `Session.PersonaId` = активный спикер. Создание —
  `SessionManager.CreateGroupChatAsync` (`POST /api/chats/group`; зона — по ведущей, как у
  CreatePersonaChatAsync), состав — `PUT /api/chats/{id}/participants` (спикер сохраняется,
  если остался, иначе ведущая). Роутинг хода — [GroupChatRouter.cs](../../backend/ClaudeHomeServer/Services/GroupChatRouter.cs)
  (первый @handle участника в тексте → спикер, остальные → AlsoMentioned; без упоминаний —
  текущий/ведущая) в `SendMessageAsync` до пересоздания процесса: `SwitchSpeaker` (общее ядро
  с SetPersona) + `speaker_changed` клиентам (разделитель «Теперь отвечает: …», рендер общий
  с companion_switched; в истории derive по смене personaId — `normalizeHistory`).
  Промпт спикера получает групповую надстройку (`BuildGroupChatHint`: участники + «говори
  только за себя, остальных спрашивай persona_ask»), mentions-режим MCP персон в группе
  включён всегда (независимо от `persona-mentions`), MentionsHint — по участникам.
  UI: мультивыбор «Групповой чат…» в CompanionSelector (чекбоксы 2-4, метка «ведущая»,
  предупреждение про разных провайдеров), стек аватаров в ChatHeaderBar (активный — с
  цветным кольцом), участники первыми в @автокомплите.
- **Долгая память** (типизация 2026 semantic/episodic/procedural):
  [PersonaMemoryService.cs](../../backend/ClaudeHomeServer/Services/PersonaMemoryService.cs) — записи в
  `data/persona-memory.json` (источник правды) + семантический слой в Dify-датасет
  `{username}:persona:{handle}` (дифф по хешам, дебаунс 15с; без Dify — полнотекст-fallback).
  Retrieval со скорингом `relevance × recency(полураспад 30д) × typeWeight(0.6/0.3/0.1) × salience`.
  Auto-recall в системный промпт каждого хода (`BuildPersonaRecallProvider`, независим от заметок).
- **MCP**: [mcp/memory-server/index.js](../../mcp/memory-server/index.js) (без зависимостей) —
  memory_remember/search/list/forget; подключение как tasks/notes (env `MEMORY_API_URL/TOKEN/PERSONA_ID`
  в `BuildTurnMcpConfig` + подсказка в промпт). Явный write-path: персона сама решает, что запомнить.
- **MCP персон**: [mcp/personas-server/index.js](../../mcp/personas-server/index.js) (без зависимостей) —
  personas_list/get/create/update/delete/generate_avatar (CRUD персон из любого чата; создание
  глобальных и проектных — дефолтный projectId из сессии). Подключение как tasks/notes
  (env `PERSONAS_API_URL/TOKEN/PROJECT_ID` в `BuildTurnMcpConfig` + подсказка в промпт), но только
  при включённом у владельца флаге `personas` (`SessionManager.BuildPersonasContext`).
  generate_avatar = avatar/generate `{count:1}` + select первого кандидата.
  personas_automation_list/create/update/delete/test — CRUD правил проактивности (тонкая
  обёртка над `/api/personas/{id}/automation*`, без доп. флага и без самоограничений: персона
  может настраивать проактивность любой персоне, включая себя); значения enum триггера/веса
  действия — camelCase (`gitCommit`/`taskStatus`/`gate`/`work`, см. `JsonStringEnumConverter` в Program.cs).
- **Аватар**: инициалы+цвет (палитра `AGENT_COLORS`) базой; фото-генерация через fal.ai —
  [FalImageService.cs](../../backend/ClaudeHomeServer/Services/FalImageService.cs) (`Fal:ApiKey`, модель
  `Fal:ImageModel`, дефолт `fal-ai/flux/schnell`; для фото-аватаров задают `flux/dev`). Генерация
  возвращает 1-4 **кандидата** (`POST {id}/avatar/generate` {prompt?,count?} → candidates во временную
  папку, аватар НЕ меняется), пользователь выбирает (`POST {id}/avatar/select`), отдача — `GET {id}/avatar`
  (access_token в query для `<img>`). Плюс **загрузка своего фото** с кропом и зумом
  (`POST {id}/avatar/upload` — original + cropped + параметры кропа; валидация по magic bytes)
  и перекроп сохранённого оригинала без перезагрузки файла (`POST {id}/avatar/recrop`,
  `GET {id}/avatar/original`).
- **Авто-память** (флаг `persona-memory-autolearn`): [PersonaMemoryAutolearnService.cs](../../backend/ClaudeHomeServer/Services/PersonaMemoryAutolearnService.cs) —
  IHostedService на `SessionManager.OnSessionMessage`; по завершении хода персонной сессии one-shot
  извлекает факты (semantic) и итог (episodic) из транскрипта и сохраняет в память (дедуп в `Remember`).
- **Фронт**: [features/personas/](../../frontend/src/features/personas/) — PersonasPage (глобальный раздел,
  только `scope=global`): сайдбар PersonaList | центр «Студия-профиль»; редактор
  [PersonaForm.tsx](../../frontend/src/features/personas/PersonaForm.tsx) — одна колонка 680 в стиле
  TaskEditForm: hero-аватар 80 (инлайн-генерация 4 кандидатов + цвет), безрамочная serif-«Роль»,
  Характер во всю ширину (липкая панель пресетов + ✨Сгенерировать/✨Улучшить с уточняющим
  промптом-поповером, autoGrow без скролла), Поведение (модель/усилие/зона/приветствие),
  Память-summary (счётчики + «Открыть память»); действия — в [PersonaToolbar.tsx](../../frontend/src/features/personas/PersonaToolbar.tsx)
  (общий Toolbar: Профиль|Память, Поговорить, ⋯-меню с Удалить, Сохранить + dirty-индикатор).
  В проекте — вкладка «Команда» (`leftTab='agents'` WorkspacePage): список в сайдбаре
  ([ProjectPersonasPanel.tsx](../../frontend/src/features/personas/ProjectPersonasPanel.tsx)), форма — в
  контентной зоне. Идентификация в чатах: плашки ChatList/SessionList (аватар+«Роль (Имя)»+цвет),
  агент в тулбаре чата (ChatHeaderBar: аватар+роль/имя+зона+полоса цвета), аватар у реплик
  (PersonaContext→ChatItemView), приветствие (PersonaGreeting). Запуск чата: «Поговорить» из
  студии, [CompanionSelector](../../frontend/src/components/CompanionSelector.tsx) в композере пустого чата
  (группы «Команда проекта»/«Глобальные»), пилюли «Поговорить с…» в empty state (в проекте
  команда сразу, глобальные за «+N ещё»). Стор [lib/personas.ts](../../frontend/src/lib/personas.ts)
  (realtime personas_changed; `personaLabel`/`personaTitleLines` — единый формат «Роль (Имя)»).
- **Ассистент по умолчанию и знакомство** (флаг `default-personas-onboarding`): при первом
  входе система сама заводит человеку обычную персону «Ассистент» (роль «Личный помощник»,
  Coordinator + Full + `personas-manage` + автопривязки, аватар-инициалы) и делает её
  дефолтной — правило «новый чат человека всегда с персоной» не нарушается ни на секунду.
  Полноэкранных гейтов первого входа и открытия проекта нет; вместо них тихое приглашение
  в трёх местах (карточка в разделе «Персоны», точка `ui/IntroDot` у аватара ассистента,
  строка в настройках проекта), которое гаснет, когда человек законно обзавёлся своим
  ассистентом — прошёл знакомство, доработал заготовку руками, назначил дефолтом другую
  персону или удалил её. Знакомство — та же сессия `OnboardingKind`, но она **дорабатывает**
  заготовку (`personas_update` + `personas_generate_avatar`, финал `personas_set_default`),
  а не создаёт вторую персону. Инвариант: провижн (`DefaultAssistantProvisioner.EnsureAsync`)
  вызывается только на записях и **никогда** в `GET /api/auth/me`. Подробности —
  [onboarding-intro.md](onboarding-intro.md).
- **Флаги**: `personas` (раздел + чат + память + аватар + персона-исполнитель задач + вкладка
  «Задачи»), `persona-memory-autolearn` (авто-извлечение фактов из диалога),
  `persona-memory-consolidation` (фоновая уборка памяти), `persona-mentions`
  (@упоминания + persona_ask + «Обсудить с командой»), `persona-group-chats` (групповые чаты).
