# Карта разноса api.ts и types/index.ts (2026-11)

Вход для оставшихся шагов. Часть `api` (раздел 1) завершена: все ключи доменов вынесены из `lib/api.ts` (189 строк — импорты, реэкспорты и сборка объекта из spread'ов). Часть типов (раздел 2) завершена частично: 18 доменов вынесены в `features/` (плюс отдельные типы: `HiggsfieldStatus`/`HiggsfieldAdminStatus` → providerAccounts, `SyncMark` → git), 28 остались.

Числа пересняты 07.11 с текущих файлов ветки `refactor/opt-frontend-api`, а не со старого снимка: `lib/api.ts` — 189 строк, `types/index.ts` — 2 090 строк. Команды снятия чисел — в отчёте задачи. Страж актуальности чисел — защёлка `lib/__tests__/apiFileBudget.test.ts` (потолки 189 и 2 090, равны фактическим длинам файлов): если числа поедут — тест заметит.

Числа импортёров — скриптом по разбору `import … from` (путь резолвится до `types/index.ts`); **из счёта исключён весь каталог `types/`**, а не только сам `types/index.ts` — в том числе `types/featureRun.ts`, который импортирует `./index`. Граф кода тут не годится: `codegraph_neighbors` по TS-файлам межфайловых связей не строит («узел не найден»).

## Образец (S5-0)

- Типы: `lib/api.ts` (бывшие строки 32–441) → `features/localModel/types.ts`; `lib/api.ts` делает `export type * from '../features/localModel/types'`, все импорты снаружи прежние.
- Методы: блок `localModel: {…}` → `features/localModel/api.ts` (`localModelApi`, использует `request` из `lib/offline`, а не `api`, — иначе цикл); в `api` подмешивается `...localModelApi`, адрес `api.localModel.*` не меняется.
- Домен «Аккаунты провайдеров» (S5-1): ключи `fal`, `glif`, `yandex`, `higgsfield` и типы `HiggsfieldStatus`/`HiggsfieldAdminStatus` → `features/providerAccounts/`; в `lib/api.ts` — spread `...providerAccountsApi` и `export type *`. Адреса `api.fal.*`, `api.glif.*`, `api.yandex.*`, `api.higgsfield.*` прежние.
- Домен «Аккаунт» (S5-2, `a7a2b7b7b`): ключи `auth`, `push`, `users`, `settings`, `meModelTiers`, `adminUserModelTiers` и тип `ModelTiers` → `features/account/`; в `lib/api.ts` — spread `...accountApi` и `export type *`. Адреса `api.auth.*` и прочие прежние; `Me`/`UserProfile`/`AppSettings` остаются в `types/index.ts`.

## Правила для следующих шагов

1. Те же два файла в `features/<домен>/` (`api.ts` + `types.ts`), тот же `export type *` и spread, затем снизить потолок в `lib/__tests__/apiFileBudget.test.ts`.
2. **`export type *` не переносит значения.** Если в `features/<домен>/types.ts` появится константа (а не только тип), реэкспорт `export type *` её потеряет МОЛЧА: потребитель получит ошибку импорта, а не внятное «нет такой константы». Поэтому значения (константы, enum-ы со значениями, функции) в `types.ts` домена не объявлять; либо реэкспортировать их отдельно через `export { X } from …` (или `export *`), либо держать в `api.ts` домена.
3. Защёлка (`apiFileBudget.test.ts`) держит потолок только для `lib/api.ts` и `types/index.ts`. Файлы вынесенных доменов (крупнейшие: `features/localModel/types.ts` — 411 строк, `features/personas/types.ts` — 402, `features/personas/api.ts` — 347) под потолок не попадают — их рост ничем не ограничен. Это по замыслу (сторож — горячие точки), но помнить: разнесённый домен может разрастись; потолок на него заводить отдельным правилом, если он стал заметным.

## Важно: где лежат типы

Все DTO, жившие в шапке `lib/api.ts` (до объекта `api`), вынесены вместе с методами своих доменов. В `lib/api.ts` остались только `setStoredToken` (68–73) и `FeatureSummaryResult` (75–90) — последний надо выносить вместе с методами домена «Резюме фичи» (то же правило). В доменах «Проекты» и «Аккаунт» раздела 2 есть значения, а не типы (`const ProjectFeature`, `function displayNameOf`) — при их выносе применять правило 2.

## 1. Домены `api` — завершено

Все ключи объекта `api` вынесены из `lib/api.ts` (189 строк). Файл приведён к сборке; в нём осталось:

| Что | Строки |
|---|---|
| шапка-комментарий (потолок роста) | 1–3 |
| импорты api-модулей фич | 4–37 |
| реэкспорты типов вынесенных доменов (`export type *`) и хелперов (`panelEntryPath`, `setGitSessionContext`/`getGitSessionContext`) | 39–66 |
| `setStoredToken` | 68–73 |
| `FeatureSummaryResult` (DTO «Резюме фичи», не вынесен) | 75–90 |
| объект `api` — только spread'ы с комментариями-указателями | 92–189 |

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

Несколько ключей сели не туда, что сулила старая таблица: `briefing`, `search`, `onboarding`, `wall`, `quickPhrases`, `history` — в `features/home/api.ts` (мелкие домены «Дома»), `workflow` — в `features/chat/api.ts`. Единственное незакрытое — `FeatureSummaryResult` (75–90).

## 2. Домены `types/index.ts` (2 090 строк)

Граница диапазона (одна на весь раздел): диапазон начинается с первой строки шапки домена — блока комментариев `//`, примыкающих непосредственно к первой строке экспорта домена (если шапки нет — со строки экспорта; если перед экспортом стоит маркер секции `// --- … ---` — с маркера) — и заканчивается строкой перед началом следующего диапазона. Пустые строки между диапазонами достаются предыдущему. Строки реэкспорта вынесенных доменов (`export type * from …`) и примыкающие к ним пустые строки ни в какой диапазон не входят.

Комментарий `// === Типизированные поля «Модели и расход» (models-spend-data-spec) ===` стоит на строке 1528 — ВНУТРИ интерфейса `ProviderBalanceInfo` (1515–1548). Это пометка полей внутри типа, а не граница домена: отдельной строкой его выделять нечего, домен «Баланс провайдера» покрывает 1515–1548 целиком.

`ServerMessage` (1126–1355) — отдельный домен: его импортируют 26 файлов фронта прямо из `types/index.ts`. Уезжать вместе с Git-типами ему нельзя — те уже вынесены.

Импортёров `types/index.ts` — **538 файлов** (271 вне `features/`, 267 в `features/`). Проверено скриптом по разбору импортов на текущем состоянии ветки. Из счёта исключён весь каталог `types/`. Числа в таблице — по доменам; один файл может импортировать типы из нескольких доменов, поэтому сумма по доменам больше 538.

Оставшиеся домены:

| Домен | Строки в types/index.ts | Экспортов | Целевой файл | Файлов-импортёров |
|---|---|---|---|---|
| Проекты: теги, темы, фон, возможности, значок (`const ProjectFeature` — значение, правило 2) | 7–241 | 16 | не назначено | 120 |
| Колонка доски проекта | 242–250 | 1 | не назначено | 5 |
| Граф кода | 251–290 | 7 | не назначено | 10 |
| Документы и область | 291–463 | 19 | не назначено | 9 |
| Истории решений (dossier) | 464–507 | 3 | не назначено | 2 |
| Доска агентов (BoardItem) | 508–521 | 1 | не назначено | 4 |
| Системный промпт (SystemPromptPart) | 522–527 | 1 | не назначено | 2 |
| Группы проектов и сферы, память сфер | 528–569 | 10 | не назначено | 22 |
| Слот моделей (ModelTierValue; начинается с маркера `// --- Задачи ---` на 570) | 570–576 | 1 | не назначено | 4 |
| Задачи, дефекты, критерии приёмки, предгейт | 577–840 | 20 | не назначено | 76 |
| Настройки, биллинг, фич-флаги | 841–866 | 3 | не назначено | 11 |
| Сессии, картинки, контекст чата | 867–1039 | 7 | не назначено | 95 |
| Главная (сводка) | 1040–1073 | 2 | не назначено | 12 |
| Файл проекта (FileEntry) | 1074–1085 | 1 | не назначено | 7 |
| Workflow-агенты (WorkflowAgent*, StoredWorkflowProgress) | 1088–1125 | 3 | не назначено | 2 |
| ServerMessage (протокол хода по WS) | 1126–1355 | 1 | не назначено | 26 |
| Промпты и CLI-слои | 1356–1413 | 4 | не назначено | 2 |
| Usage (счётчики токенов) | 1414–1420 | 1 | `features/spend/types.ts` — назначено старым планом (S5-21), не сделано | 1 |
| Проекты: сервисы, превью, запуск | 1421–1476 | 4 | не назначено | 8 |
| Лимиты и квоты провайдеров | 1477–1514 | 4 | не назначено | 9 |
| Баланс провайдера (ProviderBalanceInfo) | 1515–1548 | 1 | не назначено | 4 |
| Расходы, подписки, снимки использования | 1549–1690 | 8 | `features/spend/types.ts` — назначено старым планом (S5-21), не сделано | 13 |
| Аккаунты и расход fal/glif/yandex (`HiggsfieldStatus`/`HiggsfieldAdminStatus` уже вынесены) | 1691–1738 | 7 | `features/providerAccounts/types.ts` — назначено старым планом (S5-1), не сделано | 1 |
| Механики OmO (WorkLoop) | 1741–1754 | 1 | не назначено | 4 |
| Элементы ленты чата (Tool*, ChatItem) | 1757–1939 | 5 | не назначено | 70 |
| Скиллы и агенты | 1940–1989 | 6 | не назначено | 14 |
| Аккаунт (Me, AuthState, UserProfile; `displayNameOf` — функция, правило 2) | 1990–2052 | 4 | не назначено | 26 |
| Быстрые фразы (QuickPhrase) | 2076–2083 | 1 | не назначено | 2 |

Типы `ToolProgress`/`ToolStage`/`ToolRunTotals`/`ToolRunFailure` в старой карте шли строкой «Командная реализация»; при выносе КР (S5-23) они остались в файле и теперь отнесены к «Элементам ленты чата» — по фактическому соседству и назначению. `SyncMark` из старой строки «Синхронизация и WorkflowAgent» вынесён в `features/git/types.ts`.

Сумма: 2 048 (диапазоны доменов) + 6 (шапка файла, строки 1–6: импорты и реэкспорт `Mode`) + 18 (строки реэкспорта вынесенных доменов) + 18 (пустые строки, примыкающие к ним) = 2 090 = длина файла. Нарезку вести по ключам, а не по сумме.

Вынесено (запись; реэкспорт-строки в `types/index.ts` — 1086, 1739, 1755, 2053–2074, 2084–2090):

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

## 3. Порядок шагов

Часть `api` (раздел 1) закрыта. Осталось: типы раздела 2. По размеру: `Задачи` (264 строк), `Проекты` (235), `ServerMessage` (230), лента чата (183), `Сессии` (173), `Документы` (173), `Расходы` (142); по импортёрам: `Проекты` (120), `Сессии` (95), `Задачи` (76), лента чата (70), `Скиллы` (14), `Расходы` (13). Крупные резать по поддоменам, ≤ 4 файлов на шаг.

## 4. Защёлка

`frontend/src/lib/__tests__/apiFileBudget.test.ts` держит потолок строк `api.ts` и `types/index.ts` — сейчас 189 и 2 090, равные фактическим длинам файлов. Потолок только снижается: каждый шаг выноса в конце пересчитывает его вниз. Новые файлы в `features/<домен>/` под потолок не попадают (см. «Правила для следующих шагов», п. 3).
