# Карта разноса api.ts и types/index.ts (2026-11)

Вход для оставшихся шагов. Часть `api` (раздел 1) завершена: все ключи доменов вынесены из `lib/api.ts` (175 строк по состоянию на 07.11 — импорты, реэкспорты и сборка объекта из spread'ов). Часть типов (раздел 2) завершена частично: по состоянию на 07.11 выполнены шаги P10, P11 и P12 — домены «Аккаунты провайдеров», «Лимиты/квоты/баланс/расходы» и «Аккаунт» (Me, AuthState, UserProfile, настройки и биллинг) вынесены в `features/`; остальное — неисполненные строки таблицы раздела 2 (шаги P13–P15, «попутно» и хребет).

Числа пересняты 07.11 с текущих файлов ветки `refactor/opt-frontend-api`, а не со старого снимка: `lib/api.ts` — 175 строк, `types/index.ts` — 1 714 строк (после P12, переснято 07.11). Номера строк в таблице раздела 2 — снимок `1727f6419` до P10/P11/P12 (в нём ещё живёт домен «Аккаунт»: `Me` 1944, `AuthState` 1976, `UserProfile` 1992): справка по состоянию на 07.11, а не текущее состояние файла. Страж актуальности чисел: для `lib/api.ts` — потолок строк в защёлке `lib/__tests__/apiFileBudget.test.ts`, равный фактической длине файла; для `types/index.ts` — контракт по именам (раздел 4): строки файла больше не держатся. Команды снятия чисел — в отчёте этой доработки.

Числа импортёров — скриптом по разбору `import … from` (путь резолвится до `types/index.ts`); **из счёта исключён весь каталог `types/`**, а не только сам `types/index.ts` — в том числе `types/featureRun.ts`, который импортирует `./index`. Граф кода тут не годится: `codegraph_neighbors` по TS-файлам межфайловых связей не строит («узел не найден»).

## Образец (S5-0)

- Типы: `lib/api.ts` (бывшие строки 32–441) → `features/localModel/types.ts`; `lib/api.ts` делает `export type * from '../features/localModel/types'`, все импорты снаружи прежние.
- Методы: блок `localModel: {…}` → `features/localModel/api.ts` (`localModelApi`, использует `request` из `lib/offline`, а не `api`, — иначе цикл); в `api` подмешивается `...localModelApi`, адрес `api.localModel.*` не меняется.
- Домен «Аккаунты провайдеров» (S5-1): ключи `fal`, `glif`, `yandex`, `higgsfield` и типы `HiggsfieldStatus`/`HiggsfieldAdminStatus` → `features/providerAccounts/`; в `lib/api.ts` — spread `...providerAccountsApi` и `export type *`. Адреса `api.fal.*`, `api.glif.*`, `api.yandex.*`, `api.higgsfield.*` прежние.
- Домен «Аккаунт» (S5-2, `a7a2b7b7b`): ключи `auth`, `push`, `users`, `settings`, `meModelTiers`, `adminUserModelTiers` и тип `ModelTiers` → `features/account/`; в `lib/api.ts` — spread `...accountApi` и `export type *`. Адреса `api.auth.*` и прочие прежние; `Me`/`UserProfile`/`AppSettings` остаются в `types/index.ts`.

## Правила для следующих шагов

1. Те же два файла в `features/<домен>/` (`api.ts` + `types.ts`), тот же `export type *` и spread; затем для шага `api` — снизить потолок в `lib/__tests__/apiFileBudget.test.ts`, для шага типов — убрать вынесенные имена из `lib/__tests__/typesIndexExports.ts` (раздел 4).
2. **`export type *` не переносит значения.** Если в `features/<домен>/types.ts` появится константа (а не только тип), реэкспорт `export type *` её потеряет МОЛЧА: потребитель получит ошибку импорта, а не внятное «нет такой константы». Поэтому значения (константы, enum-ы со значениями, функции) в `types.ts` домена не объявлять; либо реэкспортировать их отдельно через `export { X } from …` (или `export *`), либо держать в `api.ts` домена.
3. Защёлка (`apiFileBudget.test.ts`) держит только горячие точки: для `lib/api.ts` — потолок строк, для `types/index.ts` — контракт по именам (раздел 4). Файлы вынесенных доменов (крупнейшие: `features/localModel/types.ts` — 411 строк, `features/personas/types.ts` — 402, `features/personas/api.ts` — 347) под потолок не попадают — их рост ничем не ограничен. Это по замыслу (сторож — горячие точки), но помнить: разнесённый домен может разрастись; потолок на него заводить отдельным правилом, если он стал заметным.

## Важно: где лежат типы

Все DTO, жившие в шапке `lib/api.ts` (до объекта `api`), вынесены вместе с методами своих доменов. В `lib/api.ts` остались только `setStoredToken` (68–73) и реэкспорт `FeatureSummaryResult` (75–76) — последний уже вынесен в `features/tasks/types.ts` (домен «Задачи»). В доменах «Проекты» и «Аккаунт» раздела 2 есть значения, а не типы (`const ProjectFeature`, `function displayNameOf`) — при их выносе применять правило 2.

## 1. Домены `api` — завершено

Все ключи объекта `api` вынесены из `lib/api.ts` (175 строк по состоянию на 07.11; потолок строк держит защёлка, раздел 4). Файл приведён к сборке; в нём осталось:

| Что | Строки |
|---|---|
| шапка-комментарий (потолок роста) | 1–3 |
| импорты api-модулей фич (api-модулей фич + типы из `../types` + `writeStoredToken`) | 4–37 |
| реэкспорты типов вынесенных доменов (`export type *`) и хелперов (`panelEntryPath`, `setGitSessionContext`/`getGitSessionContext`) | 39–66 |
| `setStoredToken` | 68–73 |
| реэкспорт `FeatureSummaryResult` (DTO «Резюме фичи»; тип живёт в `features/tasks/types.ts`) | 75–76 |
| объект `api` — только spread'ы с комментариями-указателями | 78–175 |

Фактический расклад вынесенных доменов (ключ → файл):

| Целевой файл | Ключи `api` |
|---|---|
| `features/account/api.ts` | `auth`, `push`, `users`, `settings`, `meModelTiers`, `adminUserModelTiers` |
| `features/spend/api.ts` | `usage`, `spend` |
| `features/models/api.ts` | `providers`, `models`, `localActions` |
| `features/localModel/api.ts` | `localModel` |
| `features/hostControl/api.ts` | `power`, `remoteCommands`, `backup`, `hostFiles` |
| `features/deploy/api.ts` | `deploy`, `deployJournal` |
| `features/providerAccounts/api.ts` | `fal`, `glif`, `yandex`, `higgsfield` |
| `features/imageGeneration/api.ts` | `imageGeneration` |
| `features/modules/api.ts` | `modules`, `subsystemModules`, `featureFlags` |
| `features/mcp/api.ts` | `mcp` |
| `features/desktop/api.ts` | `devices` |
| `features/plugins/api.ts` | `plugins` |
| `features/video/api.ts` | `video` |
| `features/projects/api.ts` | `projects`, `externalPreview` |
| `features/specialties/api.ts` | `specialties` |
| `features/home/api.ts` | `home`, `briefing`, `search`, `onboarding`, `wall`, `quickPhrases`, `history` |
| `features/spheres/api.ts` | `projectGroups`, `spheres` |
| `features/tasks/api.ts` | `board`, `tasks` |
| `features/notes/api.ts` | `notes` |
| `features/personas/api.ts` | `personas` |
| `features/sessions/api.ts` | `sessions` |
| `features/chat/api.ts` | `chats`, `watchdogs`, `workflow`, `tts` |
| `features/docs/api.ts` | `docs` |
| `features/dossiers/api.ts` | `dossiers` |
| `features/telemetry/api.ts` | `telemetry` |
| `features/files/api.ts` | `files`, `sync` |
| `features/git/api.ts` | `git` |
| `features/knowledge/api.ts` | `knowledge`, `knowledgeBases` |
| `features/skills/api.ts` | `skills` |
| `features/plan/api.ts` | `plans` |
| `features/reader/api.ts` | `reader` |

Несколько ключей сели не туда, что сулила старая таблица: `briefing`, `search`, `onboarding`, `wall`, `quickPhrases`, `history` — в `features/home/api.ts` (мелкие домены «Дома»), `workflow` — в `features/chat/api.ts`. Последнее незакрытое — `FeatureSummaryResult` — вынесено в `features/tasks/types.ts` (домен «Задачи»); в `lib/api.ts` от него остался только реэкспорт (75–76).

## 2. Домены `types/index.ts` (2 036 строк, снимок `1727f6419` до P10/P11/P12)

Граница диапазона (одна на весь раздел): диапазон начинается с первой строки шапки домена — блока комментариев `//`, примыкающих непосредственно к первой строке экспорта домена (если шапки нет — со строки экспорта; если перед экспортом стоит маркер секции `// --- … ---` — с маркера) — и заканчивается строкой перед началом следующего диапазона. Пустые строки между диапазонами достаются предыдущему. Строки реэкспорта вынесенных доменов (`export type * from …`) и примыкающие к ним пустые строки ни в какой диапазон не входят.

Комментарий `// === Типизированные поля «Модели и расход» (models-spend-data-spec) ===` стоит на строке 1478 — ВНУТРИ интерфейса `ProviderBalanceInfo` (1465–1498). Это пометка полей внутри типа, а не граница домена: отдельной строкой его выделять нечего, домен «Баланс провайдера» покрывает 1465–1498 целиком.

`ServerMessage` (1076–1305) — строка хребта, не домен (см. «Решение 07.11» под таблицей): его импортируют 26 файлов фронта прямо из `types/index.ts`. Уезжать вместе с Git-типами ему нельзя — те уже вынесены.

Импортёров `types/index.ts` — **534 файла** (269 вне `features/`, 265 в `features/`). Проверено той же методикой (разбор импортов с резолвом пути до `types/index.ts`) на HEAD ветки `863fcc1cc`. На снимке `1727f6419` было 538: после P10/P11/P12 четыре файла — `features/account/api.ts`, `features/providerAccounts/api.ts`, `features/reader/api.ts`, `features/spend/api.ts` — перестали тянуть `types/index.ts` напрямую и берут типы из своего доменного файла. Прежнее разбиение 271/267 не воспроизводится этой методикой ни на снимке (269/269), ни на HEAD (269/265). Из счёта исключён весь каталог `types/`. Числа в таблице — по доменам; один файл может импортировать типы из нескольких доменов, поэтому сумма по доменам больше 534.

Оставшиеся домены (номера строк — на 07.11, якорь — имя экспорта):

| Домен | Строки в types/index.ts | Экспортов | Целевой файл | Файлов-импортёров |
|---|---|---|---|---|
| Проекты: теги, темы, фон, возможности, значок (`const ProjectFeature` — значение, правило 2) | 8–242 | 16 | `features/projects/types.ts` — P14b, последним | 120 |
| Граф кода | 243–282 | 7 | `features/projects/types.ts` — P14a | 10 |
| Документы и область | 283–455 | 19 | попутно, когда фича трогает домен | 9 |
| Доска агентов (BoardItem) | 458–471 | 1 | `features/tasks/types.ts` — P13, сразу после свежего мержа master | 4 |
| Системный промпт (SystemPromptPart) | 472–477 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 2 |
| Группы проектов и сферы, память сфер | 478–519 | 10 | попутно, когда фича трогает домен | 22 |
| Слот моделей (ModelTierValue; начинается с маркера `// --- Задачи ---` на 520) | 520–526 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 4 |
| Задачи, дефекты, критерии приёмки, предгейт (+ `ChatRef` из master: ссылка на чат задачи, читает его только `Task`; вне снимка 07.11) | 527–790 | 20 | `features/tasks/types.ts` — P13, сразу после свежего мержа master | 76 |
| Настройки, биллинг, фич-флаги | 791–816 | 3 | `AppSettings`, `ClaudeBilling` → `features/account/types.ts` — P12, выполнено (`b6c04287e`/`d407055a0`); `FeatureFlagDefinition` — попутно, когда фича трогает домен | 10 |
| Сессии, картинки, контекст чата | 817–989 | 7 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 95 |
| Главная (сводка) | 990–1023 | 2 | попутно, когда фича трогает домен | 12 |
| Файл проекта (FileEntry) | 1024–1035 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 7 |
| Workflow-агенты (WorkflowAgent*, StoredWorkflowProgress) | 1038–1075 | 3 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 2 |
| ServerMessage (протокол хода по WS) | 1076–1305 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11), см. «Решение 07.11» под таблицей | 26 |
| Промпты и CLI-слои | 1306–1363 | 4 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 2 |
| Usage (счётчики токенов) | 1364–1370 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 1 |
| Проекты: сервисы, превью, запуск | 1371–1426 | 4 | `features/projects/types.ts` — P14a | 8 |
| Лимиты и квоты провайдеров (`ProviderFallbackOption`, `ProviderQuotaWindowUnit`, `ProviderQuotaWindow`) | 1427–1464 | 3 | `features/spend/types.ts` — P11, выполнено (`2824b3efe`) | 9 |
| RateLimitInfo (окно лимита подписки из `rate_limit_event`, близнец варианта `ServerMessage` `rate_limit`; читают `chatReducer`/`useSession`/`lib/rateLimit`) | 1441 (был в диапазоне «Лимиты и квоты», 1427–1464; по состоянию на 07.11 — 1412) | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11); P11 его сознательно не перенёс — это окно лимита подписки из события CLI, а не тип расхода провайдера, `features/spend/` его не использует | 5 |
| Баланс провайдера (ProviderBalanceInfo) | 1465–1498 | 1 | `features/spend/types.ts` — P11 (его возвращает `features/models/api.ts`, но граф у него — с типами расхода, см. «Решение 07.11» под таблицей), выполнено (`2824b3efe`) | 4 |
| Расходы, подписки, снимки использования | 1499–1640 | 8 | `features/spend/types.ts` — P11, выполнено (`2824b3efe`) | 12 |
| Аккаунты и расход fal/glif/yandex (`HiggsfieldStatus`/`HiggsfieldAdminStatus` уже вынесены) | 1641–1688 | 7 | `features/providerAccounts/types.ts` — P10, выполнено (`fca7c9f88`) | 0 |
| Механики OmO (WorkLoop) | 1691–1704 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 4 |
| Элементы ленты чата (Tool*, ChatItem) | 1707–1889 | 5 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 70 |
| Скиллы и агенты | 1890–1939 | 6 | попутно, когда фича трогает домен | 14 |
| Аккаунт (Me, AuthState, UserProfile; `displayNameOf` — функция, правило 2) | 1940–2002 | 4 | `features/account/types.ts` — P12, выполнено (`b6c04287e`/`d407055a0`) | 25 |
| Участники общего проекта (из master, 07.11): роли и состав участников общего проекта, сводка участников в карточке проекта, коды отказов операций членства (фича «Общие проекты», флаг `shared-projects`). Имена: `ProjectMemberRole`, `ProjectRoleView`, `ProjectMember`, `ProjectOwnerRef`, `ProjectMembersView`, `ProjectMemberSummaryRef`, `ProjectMembersSummary`, `MEMBER_REFUSAL` (значение — правило 2: реэкспортить отдельно), `MemberRefusalCode`. Код домена — модуль экрана `features/projects/members/` (`membersLogic.ts`, `MembersList.tsx`, `MembersSection.tsx`, `MessageAuthorLine.tsx`, `messageAuthor.ts` и их тесты), REST — блок участников в `projectsApi` (`features/projects/api.ts`) | — | 9 | `features/projects/types.ts` — P15, после P14b; не `members/types.ts`: `Project` читает `ProjectMembersSummary` и `ProjectRoleView`, граф один; своего `api.ts` у `members/` нет | — |

Целевые файлы шагов P13/P14a/P14b/P15 уже существуют и не пусты — эти шаги дописывают в них, а не создают с нуля: в `features/tasks/types.ts` уже 2 имени (`BoardColumn` из `1727f6419`, `FeatureSummaryResult` из `fca32c15a`), в `features/projects/types.ts` — 5 (`GlyphCandidate`, `Architecture*` — они пришли из api-части `lib/api.ts`, а не из `types/index.ts`).

### Решение 07.11

Якорь нарезки — имя верхнеуровневого экспорта, а не диапазон строк: шаг проверяется разностью множеств имён `types/index.ts` до правки и после неё плюс `tsc -b`, а номера строк в таблице — только справка на 07.11, не задание. Защёлка S5-A держит замороженный набор имён верхнеуровневых объявлений (без `export type *`), а не число строк: шаги переноса могут из этого набора только убирать имена, а имена, пришедшие с мержем master, добавляет единственный шаг сверки S5-A-MERGE — и у каждого такого имени уже есть адресат в таблице выше. Хребет и вынесенные домены ссылаются друг на друга только через `import type`; значения по этому циклу не возят (правило 2). При раскладке правила идут по порядку: сначала связный граф типов не рвётся между доменами, и только при равенстве тип идёт к своему `api.ts` — первое правило сильнее. Поэтому `ProviderBalanceInfo` и `PlacePresetRef`, которых возвращает `features/models/api.ts`, уходят в `features/spend/types.ts`: они связаны с типами расхода; отдельного домена `features/models/` не заводим — это срез REST, а не домен, и его типы идут через реэкспорт `../../types` (прецедент: `ModelPreviewResponse` и `PresetUsageResponse` возвращает тот же `models/api.ts`, а живут они в `features/specialties/types.ts`). Зеркальный случай — `features/projects/members/`: это модуль экрана без своего REST, а не домен; его типы связаны с `Project` и живут в `features/projects/types.ts`. `ServerMessage` — рукописное зеркало `Core/Protocol`, перенос его по доменам ничего не даёт: рычаг — кодоген из record-типов бэка либо `ServerMessage = база & варианты по доменам` со сторожем сверки имён с бэком; это решение об источнике правды контракта — отдельный ADR в бэклог, вне фичи.

Типы `ToolProgress`/`ToolStage`/`ToolRunTotals`/`ToolRunFailure` в старой карте шли строкой «Командная реализация»; при выносе КР (S5-23) они остались в файле и теперь отнесены к «Элементам ленты чата» — по фактическому соседству и назначению. `SyncMark` из старой строки «Синхронизация и WorkflowAgent» вынесён в `features/git/types.ts`.

Сумма: 1 987 (диапазоны доменов) + 7 (шапка файла, строки 1–7: импорты, в т.ч. локальный импорт `BoardColumn` для внутреннего употребления, и реэкспорт `Mode`) + 8 (строки реэкспорта с примыкающими пустыми между доменами: 456–457, 1036–1037, 1689–1690, 1705–1706) + 34 (остаток 2003–2036: 17 реэкспортов, 16 пустых и 1 eslint-комментарий) = 2 036 = длина файла (снимок `1727f6419` до P10/P11/P12). Нарезку вести по ключам, а не по сумме.

Вынесено (запись; реэкспорт-строки в `types/index.ts` — 456, 1036, 1689, 1705, 2003, 2006, 2008, 2010, 2012, 2014, 2016, 2018, 2020, 2022, 2024, 2026, 2028, 2030, 2032, 2034, 2036; строка `HiggsfieldStatus`/`HiggsfieldAdminStatus` реэкспорт-строки не имеет):

| Домен | Целевой файл |
|---|---|
| Git в разделе «Файлы» | `features/git/types.ts` (включая `SyncMark`) |
| Генерация картинок | `features/imageGeneration/types.ts` |
| Командная реализация (КР, пульс, перезапуск) | `features/team/types.ts` (`Tool*` остались, см. выше) |
| Продуктовая история (changelog) | `features/changelog/types.ts` |
| Заметки | `features/notes/types.ts` |
| Знания (Dify) | `features/knowledge/types.ts` |
| Скелеты персоны + персоны, аватары, голос, автоматизации, привязки, память | `features/personas/types.ts` (включая `TeamMemberDraft`) |
| Специальности и пресеты | `features/specialties/types.ts` |
| Уведомления | `features/notifications/types.ts` |
| Аналитика расхода токенов (Spend Analytics) | `features/spend/types.ts` |
| Бэкапы | `features/hostControl/types.ts` |
| Ридер (Reader) | `features/reader/types.ts` |
| Реестр MCP + Каталог MCP | `features/mcp/types.ts` |
| Инциденты телеметрии | `features/telemetry/types.ts` |
| Видео | `features/video/types.ts` |
| Устройства и локальные руки | `features/desktop/types.ts` |
| Карта плана | `features/plan/types.ts` |
| Уборка карты (CLAUDE.md) | `features/mapHygiene/types.ts` |
| `HiggsfieldStatus`/`HiggsfieldAdminStatus` (часть домена fal/glif/yandex) | `features/providerAccounts/types.ts` |
| Истории решений (dossier) | `features/dossiers/types.ts` |
| Быстрые фразы (QuickPhrase) | `features/quickPhrases/types.ts` |
| Колонка доски проекта (BoardColumn) | `features/tasks/types.ts` |

## 3. Порядок шагов

Часть `api` (раздел 1) закрыта. Осталось: типы раздела 2 — неисполненные строки таблицы раздела 2 (шаги P13–P15, «попутно» и хребет); домены выполненных шагов P10, P11 и P12 вынесены в `features/` и в перечень не входят. По размеру: `Задачи` (264 строк), `Проекты` (235), `ServerMessage` (230), лента чата (183), `Сессии` (173), `Документы` (173); по импортёрам: `Проекты` (120), `Сессии` (95), `Задачи` (76), лента чата (70), `Скиллы` (14). Крупные резать по поддоменам, ≤ 4 файлов на шаг.

Порядок линии: `P10 → S5-A → P11 → P12 → мерж master → S5-A-MERGE → P13 → P14a → P14b → P15`.

## 4. Защёлка

`frontend/src/lib/__tests__/apiFileBudget.test.ts` (с коммита `493a9d9fd`, заменившего старый счётчик строк) держит два разных контракта.

- **`lib/api.ts` — по строкам.** Потолок равен фактической длине файла (175 по состоянию на 07.11), сравнение строгое — ловит и рост файла, и задирание потолка руками. После выноса домена пересчитай потолок вниз; поднимать нельзя: новые методы — сразу в `features/<домен>/api.ts`. Счёт строк здесь годится: файл — почти только сборка (плюс `setStoredToken`), его длина и есть число импортов, реэкспортов и spread'ов.
- **`types/index.ts` — по именам, а не по строкам.** Замороженный набор имён верхнеуровневых объявлений (`export interface|type|const|function|enum X`; реэкспорты доменов `export type *` в сверку не входят), сравнение строгое — со списком в `frontend/src/lib/__tests__/typesIndexExports.ts`. Поле в существующем типе растёт свободно: строки файла больше не держатся. Новое объявление — красное: новый тип пиши сразу в `features/<домен>/types.ts`. Счёт в списке несущественен — сверка идёт по именам (112 имён по состоянию на 07.11, после P12).
- **Пополнение списка** — правило в разделе 2, «Решение 07.11»: шаги переноса могут только убирать имена из списка; имена, пришедшие с мержем master, добавляет единственный шаг сверки S5-A-MERGE.
- Новые файлы в `features/<домен>/` под потолок не попадают (см. «Правила для следующих шагов», п. 3).
