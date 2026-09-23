# Карта фич-флагов

> Реестр (source of truth) — в коде: `FeatureFlagCatalog.All`
> ([Models/FeatureFlag.cs](../../backend/ClaudeHomeServer.Core/Models/FeatureFlag.cs));
> ключи дублируются во фронте — const `FLAGS`
> ([lib/featureFlags.ts](../../frontend/src/lib/featureFlags.ts)). Механика: per-user
> override в `data/users.json` поверх дефолтов каталога, эффективные значения приходят
> с бэка (`/api/auth/me`, `/api/feature-flags`) и раздаются компонентам хуком
> `useFeature(FLAGS.key)`. Механику и инструкцию «как добавить флаг» держит раздел
> «Dark launch» [CLAUDE.md](../../CLAUDE.md).
>
> Механика резолва — [FeatureFlagService.cs](../../backend/ClaudeHomeServer/Services/FeatureFlagService.cs):
> каталог PLUS динамические флаги `module-{id}` (дефолт `true`) из `ModuleRegistry` —
> тумблеры внешних модулей рендерятся рядом, но в статический каталог не входят.
>
> Карта снята 2026-09-23: по каждому флагу каталога сверено, что он гейтит в коде и есть
> ли его ключ во фронте. Ссылка-пометка «за флагом …» в другом документе НЕ гарантия
> актуальности: снятые после dark launch флаги работают у всех безусловно, а упоминания
> о них в доках остаются (пример и предупреждение — [CLAUDE.md](../../CLAUDE.md), раздел
> «Dark launch», и [«Упоминания снятых флагов в документах»](#упоминания-снятых-флагов-в-документах)
> ниже).

## Сводная таблица

| Ключ | Что гейтит (кратко) | Дефолт | Стадия | Ключ во фронте `FLAGS` |
|---|---|---|---|---|
| `workspace-destructive` | файлы и чаты: необратимое `files_delete`/`chats_delete` у workspace-server | false | dev | есть |
| `change-dossiers-recall` | recall-промпт и MCP-инструменты по паспортам изменений | false | dev | есть |
| `desktop-agent` | вся грань: тип чата «Десктопный», реестр устройств, `desktop_*` в ход | false | dev | есть |
| `specialty-prompt-sections` | секции промпта специальности + UI каталога в настройках | false | dev | есть |
| `chat-auto-archive` | только автоправило архивации чатов (ручной архив — без флага) | false | dev | есть |
| `visual-plan` | контекстные замечания к разделам плана | false | dev | есть |
| `mcp-catalog` | поиск по реестру MCP и предзаполнение формы | false | dev | есть |
| `chat-context` | материалы-вкладки у чата + серверный `context_list` и подсказка хода | false | dev | есть |
| `chat-branch` | кнопка «Ветвление» под шагом чата | false | dev | есть |
| `project-map-hygiene` | «Проверить карту проекта» + `review`/`apply` для CLAUDE.md | false | dev | есть |

Все десять ключей каталога присутствуют во фронтовом `FLAGS` (10/10, совпадают по
значениям) — расхождений на момент снятия карты нет.

## Разделы по флагам

### `workspace-destructive` — Разрушающие операции агента

- **Описание (из каталога):** Claude может БЕЗВОЗВРАТНО удалять файлы проектов и чаты
  через инструменты рабочего пространства (files_delete, chats_delete) — только по явной
  просьбе. Персоне дополнительно нужна возможность «Удаление (опасно)».
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** сервер — секция `destructive` workspace-сервера (безвозвратные
  `files_delete` / `chats_delete`) монтируется в ход, только когда совпали: флаг владельца,
  tool-ключ `destructive` у персоны и профиль не «Только чтение»
  ([SessionManager.cs:3501-3504](../../backend/ClaudeHomeServer/Services/SessionManager.cs));
  регистрация терминала (PTY) по комментарию связана с тем же гейтом
  ([Program.cs:769, :777](../../backend/ClaudeHomeServer/Program.cs)). **Во фронте
  `useFeature` не найден** — ключ существует только в реестре `FLAGS`; гейт полностью
  серверный.
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.workspaceDestructive`).
- **Источник:** не указан (пометка «секция destructive workspace-server» —
  [Models/FeatureFlag.cs](../../backend/ClaudeHomeServer.Core/Models/FeatureFlag.cs);
  ADR или раздел CLAUDE.md, где решение записано, не найдены).

### `change-dossiers-recall` — История решений: подсказки персонам и выгрузка в репозиторий

- **Описание (из каталога):** Персоны видят историю решений по коду, который правят,
  и не предлагают повторно то, что уже отвергли. А саму историю можно выгрузить
  в репозиторий отдельной веткой — отправка только по вашей кнопке.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:**
  - **Бэк:** MCP-инструменты `dossier_lookup` / `dossier_get` memory-сервера — гейт
    [SessionManager.cs:1213](../../backend/ClaudeHomeServer/Services/SessionManager.cs)
    (состав входит в отпечаток `tools/list`, живые список —
    [MemoryToolset.cs:407-408](../../backend/ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs));
    пассивная секция паспортов в recall-промпте персоны
    ([PersonaRecallContributor.cs:87-88](../../backend/ClaudeHomeServer.Turn/PersonaRecallContributor.cs));
    эндпоинты `export/status`, `export`, `export/push`, `import` отвечают 404 без флага
    ([DossiersController.cs:138, :157, :184, :219](../../backend/ClaudeHomeServer/Controllers/DossiersController.cs));
    авто-выгрузка/авто-импорт проверяют флаг в момент операции
    ([DossierAutoExporter.cs:101](../../backend/ClaudeHomeServer.Dossiers/DossierAutoExporter.cs),
    [DossierAutoImporter.cs:91](../../backend/ClaudeHomeServer.Dossiers/DossierAutoImporter.cs)).
  - **Фронт:** панель «История решений» — кнопки выгрузки/импорта ветки `ccs/dossiers/v1`
    ([DossierHistoryPanel.tsx:442](../../frontend/src/pages/workspace/DossierHistoryPanel.tsx));
    строка «Автоимпорт истории решений» в диалоге проекта
    ([EditDialog.tsx:266](../../frontend/src/features/projects/dialogs/EditDialog.tsx)).
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.changeDossiersRecall`).
- **Источник:** [ADR-004](../adr/ADR-004-change-dossiers.md), §8 «Фич-флаги и выкатка»
  (флаг №2 двухфлаговой выкатки; этап 1 — флаг `change-dossiers` — из каталога уже
  снят).

### `desktop-agent` — Десктопный агент

- **Описание (из каталога):** Агент из песочницы может смотреть на экран вашего
  компьютера и действовать в окнах — через приложение AI Home Desktop на этой машине.
  Работает только в чате типа «Десктопный» и только пока вы сами начали сеанс
  с устройства. Действия идут вне песочницы, с вашими правами.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** вся грань целиком:
  - **Бэк:** создание чата типа «Десктопный» без флага — 400
    ([SessionsController.cs:39-40](../../backend/ClaudeHomeServer/Controllers/SessionsController.cs));
    включение грани в проекте — 400 (выключение — всегда)
    ([ProjectsController.cs:304-305](../../backend/ClaudeHomeServer/Controllers/ProjectsController.cs));
    доставка `desktop_*` в ход требует флаг владельца И `DesktopAgentEnabled` проекта И
    tool-ключ `desktop` у персоны
    ([SessionManager.cs:1191-1193](../../backend/ClaudeHomeServer/Services/SessionManager.cs));
    capability-токены реестра устройств проверяют флаг на каждый вызов
    ([DesktopAccessGate.cs:224-225](../../backend/ClaudeHomeServer.Desktop/DesktopAccessGate.cs)).
  - **Фронт:** кнопка создания «Десктопного» чата
    ([SessionList.tsx:193](../../frontend/src/components/SessionList.tsx)); пункт «Устройства»
    в меню ([HubHeader.tsx:182](../../frontend/src/components/HubHeader.tsx)); грань
    «Десктопный агент» в настройках проекта ([EditDialog.tsx:244](../../frontend/src/features/projects/dialogs/EditDialog.tsx)).
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.desktopAgent`).
- **Источник:** [ADR-008-desktop-agent.md](../adr/ADR-008-desktop-agent.md) и раздел
  «Десктопный агент» [CLAUDE.md](../../CLAUDE.md).

### `specialty-prompt-sections` — Инструкции и типовые умения для ролей

- **Описание (из каталога):** Настраиваемые секции промпта и типовые умения по
  специальности персоны: история решений, граф кода, процессы роли. Включите,
  чтобы увидеть блок в настройках специальностей.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:**
  - **Бэк:** сценарные секции промпта специальности доходят до хода только под флагом
    ([PromptSectionsContributor.cs:54-55](../../backend/ClaudeHomeServer.Turn/PromptSectionsContributor.cs));
    блок досье выносится из recall-секции в отдельную `dossier-recall` (единай dark launch
    с prompt-sections) — [PersonaRecallContributor.cs:102](../../backend/ClaudeHomeServer.Turn/PersonaRecallContributor.cs).
  - **Фронт:** **`useFeature` не найден** — UI каталога секций в настройках специальностей
    рендерится без гейта; флаг работает только на стороне сборки промпта.
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.specialtyPromptSections`).
- **Источник:** [ADR-011](../adr/ADR-011-lsp-and-codegraph-roles.md) (флаг
  упоминается как условие действия правила) и план
  [specialty-prompt-sections-spec.md](../mockups/specialty-prompt-sections-spec.md).

### `chat-auto-archive` — Автоправило архива чатов

- **Описание (из каталога):** Чаты без сообщений дольше выбранного срока сами
  убираются в архив — список остаётся чистым, а разговор сохраняется целиком.
  Закреплённые чаты и чаты с активными задачами остаются на месте. Ручной архив
  и раздел «Архив» работают и без этого тумблера.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** только **автоправило** и его настройки (ручной архив, раздел
  «Архив» и сводка карточки — без флага):
  - **Бэк:** фоновый проход и «Применить сейчас» проверяют флаг владельца
    ([ChatArchiveService.cs:91, :106](../../backend/ClaudeHomeServer/Services/ChatArchiveService.cs));
    персональный порог `PUT /api/chats/archive-days` и запуск
    `POST /api/chats/archive-run` — 400 без флага
    ([ChatsController.cs:84, :98](../../backend/ClaudeHomeServer/Controllers/ChatsController.cs));
    проектный порог `PUT /api/projects/{id}/archive-days` и запуск
    `POST /api/projects/{id}/archive-run` — 400 без флага
    ([ProjectsController.cs:323, :344](../../backend/ClaudeHomeServer/Controllers/ProjectsController.cs));
    hosted-тик сервиса регистрируется в
    [Program.cs:762-765](../../backend/ClaudeHomeServer/Program.cs).
  - **Фронт:** блок настройки `ArchiveSettings` в диалоге проекта
    ([EditDialog.tsx:248](../../frontend/src/features/projects/dialogs/EditDialog.tsx)).
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.chatAutoArchive`).
- **Источник:** план «Архив чатов» v4 (локальный файл, ссылка в шапке
  [archive-chats-proposal.md](../mockups/archive-chats-proposal.md)); продуктовое
  описание — [archive-chats.md](archive-chats.md). ADR или раздел CLAUDE.md
  с записанным решением — не найдены.

### `visual-plan` — Замечания к плану и разворот схемой

- **Описание (из каталога):** Замечания к плану оставляются прямо на разделе
  и уходят планировщику с его адресом — больше не нужно угадывать, какое место
  имеется в виду. Часть B (разворот схемой) — под тем же флагом.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** слой контекстных замечаний и переключатель «Текстом / Схемой»:
  [PlanSection.tsx:82](../../frontend/src/components/artifacts/PlanSection.tsx) (замечания
  в карточке артефакта плана), [PlanReviewView.tsx:86](../../frontend/src/components/chat/PlanReviewView.tsx)
  (карточка плана в чате), [TeamPlanView.tsx:496](../../frontend/src/components/chat/TeamPlanView.tsx)
  (командный план). Серверного гейта **нет**: `POST /api/plans/map`
  ([PlansController.cs](../../backend/ClaudeHomeServer/Controllers/PlansController.cs))
  работает при любом значении флага — гейт только на кнопках фронта.
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.visualPlan`).
- **Источник:** секция [features.md](../architecture/features.md) «Контекстные
  замечания к плану и разворот схемы (флаг `visual-plan`)»; ADR или раздел
  CLAUDE.md — не найдены.

### `mcp-catalog` — Каталог MCP-серверов

- **Описание (из каталога):** Поиск сторонних серверов с инструментами (MCP)
  по открытому реестру сообщества: команда, адрес и имена ключей подставятся
  в форму сами — вам останется вписать свой ключ. AI Home не проверяет код
  этих серверов, смотрите, кто автор.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** бэк — `GET /api/mcp/catalog/search` и `POST /api/mcp/catalog/revision`
  ([McpCatalogController.cs:32, :62](../../backend/ClaudeHomeServer/Controllers/McpCatalogController.cs))
  отвечают 404 при выключенном флаге; приём `CatalogRef` при создании записи
  ([McpServersController.cs:143](../../backend/ClaudeHomeServer/Controllers/McpServersController.cs))
  — 400. Гейты безопасности у уже подключённых записей (подтверждение пробы/включения,
  SSRF) работают **всегда** — безопасность не зависит от тумблера. Фронт — кнопка
  «Найти сервер» в модалке MCP-серверов
  ([McpServersModal.tsx:27](../../frontend/src/features/mcp/McpServersModal.tsx)),
  вход во вкладку каталога.
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.mcpCatalog`).
- **Источник:** раздел [CLAUDE.md](../../CLAUDE.md) о личном реестре MCP-серверов
  (пометка «Каталог по официальному реестру (флаг `mcp-catalog`)») и план
  [mcp-catalog-plan.md](../research/mcp-catalog-plan.md); продуктовое описание —
  [mcp-catalog.md](mcp-catalog.md).

### `chat-context` — Контекст чата

- **Описание (из каталога):** Файлы, ссылки и задачи можно закрепить за чатом
  кнопкой «в контекст чата»: они остаются на месте после закрытия окна, видны
  вкладками справа, а Claude может свериться с ними сам.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** бэк — единственная серверная проверка
  ([SessionManager.cs:3443](../../backend/ClaudeHomeServer/Services/SessionManager.cs),
  `ChatContextEnabled`): инструмент `context_list` в составе wsp-сервера и подсказка хода
  гейтятся флагом **владельца** (не хода) — иначе состав `tools/list` мерцал бы между
  ходами (инвариант, сторож `McpToolsetStabilityTests`); эндпоинты `GET/PUT
  {sessionId}/context` гейтом **не** закрыты. Фронт — вкладки/чипы материалов у чата:
  [WorkspacePage.tsx:1640](../../frontend/src/pages/WorkspacePage.tsx) (мобильные чипы),
  [DesktopWorkspace.tsx:276](../../frontend/src/pages/workspace/DesktopWorkspace.tsx)
  (полоса контекста в сплите), кнопка «в контекст чата»
  ([useContextButton.ts:28](../../frontend/src/features/chatContext/useContextButton.ts)
  — файл, ссылка, задача).
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.chatContext`).
- **Источник:** секция [features.md](../architecture/features.md) «Контекст чата
  (флаг `chat-context`)» (механика: вкладка, `Session.Context`, инварианты
  `tools/list`); ADR или раздел CLAUDE.md — не найдены (идея пересажена из
  ADR-0018 внешнего репозитория gpb-event-assistant).

### `chat-branch` — Ветвление чата

- **Описание (из каталога):** Кнопка «Ветвление» под шагом чата создаёт новый
  чат с копией истории до этого места — можно переспросить иначе, не теряя
  исходный разговор.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** фронт — кнопки «Ветвление» под сообщением и под ответом
  ([ChatItemView.tsx:430, :473](../../frontend/src/components/chat/ChatItemView.tsx));
  API-вызов — [api.ts:1512, :1530](../../frontend/src/lib/api.ts). Серверного гейта
  **нет**: `POST /api/chats/{id}/branch` работает при любом значении флага — гейт
  только на кнопках.
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.chatBranch`).
- **Источник:** план [chat-branching-2026-09.md](../research/chat-branching-2026-09.md)
  §7; ADR или раздел CLAUDE.md — не найдены.

### `project-map-hygiene` — Уборка карты проекта

- **Описание (из каталога):** Кнопка в настройках проекта проверит CLAUDE.md:
  размер, длинные секции, мёртвые ссылки, вложенные карты — и предложит, что
  прибрать. Часть правок кнопкой «Применить», часть — работой для чата.
- **Дефолт / стадия:** false / dev.
- **Что за флагом:** бэк — [ProjectMapController.cs:49](../../backend/ClaudeHomeServer/Controllers/ProjectMapController.cs)
  (`FeatureEnabled()`): ручные **записи** `POST .../map-hygiene/review` (:81) и
  `POST .../map-hygiene/apply` (:108) отвечают 404 при выключенном флаге — `apply`
  пишет в CLAUDE.md, и открытой ручкой записи при выключенной фиче быть не должно;
  `GET .../map-hygiene/scan` гейтом **не** закрыт. Фронт — аккордеон «Проверить карту
  проекта» в настройках проекта ([EditDialog.tsx:252](../../frontend/src/features/projects/dialogs/EditDialog.tsx))
  и блок уборки карты в диалоге снапшота промпта
  ([PromptSnapshotDialog.tsx:452](../../frontend/src/features/chat/PromptSnapshotDialog.tsx)).
- **Ключ во фронте `FLAGS`:** есть (`FLAGS.projectMapHygiene`).
- **Источник:** раздел [CLAUDE.md](../../CLAUDE.md) о карте проекта (пометка
  «за флагом `project-map-hygiene`») и план
  [project-map-hygiene-plan-2026-09.md](../research/project-map-hygiene-plan-2026-09.md);
  продуктовое описание — [project-map-hygiene.md](project-map-hygiene.md).

## Упоминания снятых флагов в документах

Проходка: `grep -rn "за флагом" docs/ CLAUDE.md` (2026-09-23), каждое упоминание
сверено с `FeatureFlagCatalog.All` (10 ключей) и `FLAGS` фронта (10 ключей).
Снятые флаги — те, кого в обоих реестрах уже нет, а пометка осталась:

| Пометка (файл:строка) | Ключ | Статус |
|---|---|---|
| [ADR-006-reader-embed-check.md:25](../adr/ADR-006-reader-embed-check.md) | `link-reader` | **снят** — ридер работает по умолчанию; подтверждено паттерном «фича дожила до снятия флага» ([adr-index.md:207-209](../architecture/adr-index.md), `link-reader` в числе снятых) и списком снятых флагов в [CLAUDE.md](../../CLAUDE.md) |
| [ADR-008-project-background-generation.md:19](../adr/ADR-008-project-background-generation.md) | `project-backgrounds` | **снят** — фоны работают по умолчанию; подтверждено [adr-index.md:103](../architecture/adr-index.md) и [CLAUDE.md](../../CLAUDE.md) |
| [edit-project-compact-proposal.md:33, :92](../mockups/edit-project-compact-proposal.md) | (без ключа: «фон — только владельцу за флагом») | **снят** — то же `project-backgrounds` (секция фона проекта) |
| [ADR-013-server-chat-watchdogs.md:1, :3, :174](../adr/ADR-013-server-chat-watchdogs.md) | `chat-watchdogs` | **снят** 2026-09-01 — нет ни в каталоге, ни в `FLAGS`; сторожа работают по умолчанию. Подтверждено [adr-index.md:159, :211](../architecture/adr-index.md) и [claude-md-cleanup-2026-09.md:338](../research/claude-md-cleanup-2026-09.md) |
| [mcp-allowlist-plan.md:123, :125, :199, :230, :244, :254, :267](../research/mcp-allowlist-plan.md) | `mcp-allowlist` | **снят** — allow-list стал единственной моделью ([CLAUDE.md](../../CLAUDE.md), раздел о личном реестре; «deny-модель умерла вместе с флагом mcp-allowlist» — [McpToolsetStabilityTests.cs:166](../../backend/ClaudeHomeServer.Tests/Services/McpToolsetStabilityTests.cs)) |
| [personas.md:191](../architecture/personas.md) | `persona-memory-consolidation` | **нет в текущем каталоге** — имя упоминается только в комментариях кода ([PersonaMemoryConsolidationService.cs:7](../../backend/ClaudeHomeServer.Memory/PersonaMemoryConsolidationService.cs), [PersonaMemoryAutolearnService.cs:150](../../backend/ClaudeHomeServer.Memory/PersonaMemoryAutolearnService.cs)); поимённой проверки ключа в коде не найдено |

Пометки на **живые** флаги (сверка прошла успешно, в каталоге есть):
`mcp-catalog` ([mcp-catalog.md:85](mcp-catalog.md)), `chat-auto-archive`
([archive-chats-proposal.md:6](../mockups/archive-chats-proposal.md),
[features.md:1895](../architecture/features.md), [archive-chats.md:193, :260](archive-chats.md)),
`project-map-hygiene` ([project-map-hygiene-plan-2026-09.md:3](../research/project-map-hygiene-plan-2026-09.md),
[CLAUDE.md:182](../../CLAUDE.md)), `specialty-prompt-sections`
([specialties-personalization.md:121](../product/specialties-personalization.md)),
`desktop-agent` ([CLAUDE.md:320, :322](../../CLAUDE.md)).

Безымяннные «опция за флагом» (ключ в тексте не назван, гипотетический будущий
тумблер, не снятый флаг): approval-режим Chrome 144 —
[ADR-008-desktop-agent.md:269](../adr/ADR-008-desktop-agent.md) и
«турбо-режим» браузера — [browser-channel.md:84](../research/browser-channel.md).

Кроме пометок «за флагом», тот же класс расхождения: [ADR-004](../adr/ADR-004-change-dossiers.md)
§8/«Этап 1» описывает флаг `change-dossiers` (захват + просмотр паспортов) — в
текущем каталоге его уже нет (остался только `change-dossiers-recall`); ADR
остался в статусе «Черновик (на согласовании)».
