# Карта разноса api.ts и types/index.ts (2026-11)

Вход для оставшихся шагов. Часть `api` (раздел 1) завершена: все ключи доменов вынесены из `lib/api.ts` (175 строк по состоянию на 07.11 — импорты, реэкспорты и сборка объекта из spread'ов). Часть типов (раздел 2) завершена по запланированным шагам: 07.11 — P10, P11, P12 (домены «Аккаунты провайдеров», «Лимиты/квоты/баланс/расходы», «Аккаунт»), 08.11 — P13 (`8abf5a0cb`), P14a (`8972ecacb`), P14b (`1f1a39b0b`), P15 (`6df6463c1` — новые имена master сразу в домены, минуя хребет). Все эти домены вынесены в `features/`. Остаются строки «попутно» (Документы и область, Группы проектов и сферы, Главная (сводка), Скиллы и агенты, `FeatureFlagDefinition`) и хребет.

Числа пересняты 08.11 с текущих файлов ветки `refactor/opt-frontend-api` (HEAD `e05aa89bd`), а не со старого снимка: `lib/api.ts` — 175 строк (с 07.11 не менялся), `types/index.ts` — 1 129 строк (после P13/P14/P15; 07.11, после P12, было 1 714). Номера строк в таблице раздела 2 — снимок `1727f6419` до P10/P11/P12 (в нём ещё живёт домен «Аккаунт»: `Me` 1944, `AuthState` 1976, `UserProfile` 1992): справка, а не текущее состояние файла; диапазоны таблицы сверены с этим снимком 08.11 и верны для него. Текущие якоря живых строк (08.11): `ModelTierValue` 239, `FeatureFlagDefinition` 242, `Session` 289, `FileEntry` 463, `ServerMessage` 516, `UsageInfo` 808, `RateLimitInfo` 816, `ChatItem` 897. Страж актуальности чисел: для `lib/api.ts` — потолок строк в защёлке `lib/__tests__/apiFileBudget.test.ts`, равный фактической длине файла; для `types/index.ts` — контракт по именам (раздел 4): строки файла больше не держатся. Команды снятия чисел — в отчёте этой доработки.

**Одна методика счёта (зафиксирована 07.11; до этого карта совмещала две: общий счёт — regex без корня `src`, доменные числа — с ним, из-за чего числа не воспроизводили друг друга.)** Импортёр `types/index.ts` — любой файл `.ts`/`.tsx` из `frontend/src`, чей спецификатор импорта резолвится в `types/index.ts`, **включая `'./types'` из корня `src`** (`App.tsx`, `App.deeplink.test.ts`, `App.accessRevoked.test.ts`); тестовые файлы считаются на равных с остальными (прецедент: `providerLimit.test.ts` в строке «Лимиты»). **Из счёта исключены: весь каталог `types/`**, а не только сам `types/index.ts` — в том числе `types/featureRun.ts`, который импортирует `./index`. Направленных в `types/index.ts` нерезолвяющихся импортов («фантомов») больше нет: единственный такой — сломанный `'../../../types'` в `features/plugins/manifest.test.ts` — исправлен этой правкой на `'../../types'` (он был невидим `tsc` — тестовые файлы исключены из проверки `tsconfig.app.json` — и невидим vitest: `import type` стирается; карточка дефекта «Сломанный импорт»); файл — реальный импортёр, больше не исключение. Общий счёт командой: `git grep -lE "from '(\.\./)+types(/index)?'" -- frontend/src | grep -v '^frontend/src/types/'` (594) плюс `git grep -lE "from '\./types'" -- frontend/src | grep -E '^frontend/src/[^/]+\.tsx?$'` (3 — корневых файла) = **597** (переснято 08.11 на кончике ветки `1f1a39b0b` с этой правкой; прежние числа 533+2=535 — снимок `025f96cc8` на 07.11: после него мерж master и шаги P13/P14, числа поехали). Доменные числа — разбор именованных импортов (включая многострочные) с тем же резолвом; имена доменов — из диапазонов снимка раздела 2. Команда, воспроизводящая резолв для домена «Элементы ленты чата» (имена `ChatItem`, `ToolProgress`, `ToolStage`, `ToolRunTotals`, `ToolRunFailure`): `git grep -lE '\b(ChatItem|ToolProgress|ToolStage|ToolRunTotals|ToolRunFailure)\b' -- frontend/src | grep -v '^frontend/src/types/' | xargs perl -0777 -ne 'my $f=$ARGV; my $p=$f; $p=~s{^frontend/src/}{}; my @seg=split m{/}, $p; pop @seg; my $d=@seg; my $pre=$d ? ("../" x $d)."types" : "./types"; print "$f\n" if /(?:import|export)\s+(?:type\s+)?\{[^}]*\b(?:ChatItem|ToolProgress|ToolStage|ToolRunTotals|ToolRunFailure)\b[^}]*\}\s*from\s*\x27\Q$pre\E(?:\/index)?\x27/s;'` — префикс спецификатора вычисляется из глубины каталога файла (`'../' x глубина` для вложенных, `./` для корневых), поэтому нерезолвяющийся путь не считается; сверена с независимой python-реализацией той же методики (множества совпали). Граф кода тут не годится: `codegraph_neighbors` по TS-файлам межфайловых связей не строит («узел не найден»).

## Образец (S5-0)

- Типы: `lib/api.ts` (бывшие строки 32–441) → `features/localModel/types.ts`; `lib/api.ts` делает `export type * from '../features/localModel/types'`, все импорты снаружи прежние.
- Методы: блок `localModel: {…}` → `features/localModel/api.ts` (`localModelApi`, использует `request` из `lib/offline`, а не `api`, — иначе цикл); в `api` подмешивается `...localModelApi`, адрес `api.localModel.*` не меняется.
- Домен «Аккаунты провайдеров» (S5-1): ключи `fal`, `glif`, `yandex`, `higgsfield` и типы `HiggsfieldStatus`/`HiggsfieldAdminStatus` → `features/providerAccounts/`; в `lib/api.ts` — spread `...providerAccountsApi` и `export type *`. Адреса `api.fal.*`, `api.glif.*`, `api.yandex.*`, `api.higgsfield.*` прежние.
- Домен «Аккаунт» (S5-2, `a7a2b7b7b`): ключи `auth`, `push`, `users`, `settings`, `meModelTiers`, `adminUserModelTiers` и тип `ModelTiers` → `features/account/`; в `lib/api.ts` — spread `...accountApi` и `export type *`. Адреса `api.auth.*` и прочие прежние; `Me`/`UserProfile`/`AppSettings` на тот шаг остаются в `types/index.ts` (вынесены позже, P12).

## Правила для следующих шагов

1. Те же два файла в `features/<домен>/` (`api.ts` + `types.ts`), тот же `export type *` и spread; затем для шага `api` — снизить потолок в `lib/__tests__/apiFileBudget.test.ts`, для шага типов — убрать вынесенные имена из `lib/__tests__/typesIndexExports.ts` (раздел 4).
2. **`export type *` не переносит значения.** Если в `features/<домен>/types.ts` появится константа (а не только тип), реэкспорт `export type *` её потеряет МОЛЧА: потребитель получит ошибку импорта, а не внятное «нет такой константы». Поэтому значения (константы, enum-ы со значениями, функции) в `types.ts` домена не объявлять; либо реэкспортировать их отдельно через `export { X } from …` (или `export *`), либо держать в `api.ts` домена.
3. Защёлка (`apiFileBudget.test.ts`) держит только горячие точки: для `lib/api.ts` — потолок строк, для `types/index.ts` — контракт по именам (раздел 4). Файлы вынесенных доменов (крупнейшие: `features/localModel/types.ts` — 411 строк, `features/personas/types.ts` — 402, `features/personas/api.ts` — 347) под потолок не попадают — их рост ничем не ограничен. Это по замыслу (сторож — горячие точки), но помнить: разнесённый домен может разрастись; потолок на него заводить отдельным правилом, если он стал заметным.

## Важно: где лежат типы

Все DTO, жившие в шапке `lib/api.ts` (до объекта `api`), вынесены вместе с методами своих доменов. В `lib/api.ts` остались только `setStoredToken` (68–73) и реэкспорт `FeatureSummaryResult` (75–76) — последний уже вынесен в `features/tasks/types.ts` (домен «Задачи»). В доменах «Проекты» и «Аккаунт» раздела 2 есть значения, а не типы (`const ProjectFeature`, `function displayNameOf`, `const MEMBER_REFUSAL`); при их выносе (P12/P14b/P15) правило 2 соблюдено: в `types/index.ts` — именованные реэкспорты (`displayNameOf` 1121, `MEMBER_REFUSAL` 1126, `ProjectFeature` 1129), внешний импорт потребителей не меняется.

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

## 2. Домены `types/index.ts` (2 036 строк, снимок `1727f6419` до P10/P11/P12; текущий файл — 1 129 строк, строки P10–P15 из него вынесены)

Граница диапазона (одна на весь раздел): диапазон начинается с первой строки шапки домена — блока комментариев `//`, примыкающих непосредственно к первой строке экспорта домена (если шапки нет — со строки экспорта; если перед экспортом стоит маркер секции `// --- … ---` — с маркера) — и заканчивается строкой перед началом следующего диапазона. Пустые строки между диапазонами достаются предыдущему. Строки реэкспорта вынесенных доменов (`export type * from …`) и примыкающие к ним пустые строки ни в какой диапазон не входят.

Комментарий `// === Типизированные поля «Модели и расход» (models-spend-data-spec) ===` стоит на строке 1478 — ВНУТРИ интерфейса `ProviderBalanceInfo` (1465–1498). Это пометка полей внутри типа, а не граница домена: отдельной строкой его выделять нечего, домен «Баланс провайдера» покрывает 1465–1498 целиком.

`ServerMessage` (1076–1305) — строка хребта, не домен (см. «Решение 07.11» под таблицей): его импортируют 27 файлов фронта прямо из `types/index.ts`. Уезжать вместе с Git-типами ему нельзя — те уже вынесены.

Импортёров `types/index.ts` — **597 файлов** (287 вне `features/`, 310 в `features/`). Проверено зафиксированной методикой (см. преамбулу) на кончике ветки `1f1a39b0b` с правкой фантома (08.11). Прежние числа 535 (271/264) — снимок HEAD `025f96cc8` на 07.11 (кодово идентичен `863fcc1cc`): после него мерж master и шаги P13/P14, числа поехали. Прежние числа 534 (269/265) — regex-методика: в ней нет корневых файлов, но есть фантом `features/plugins/manifest.test.ts` (исправлен 08.11, см. преамбулу). Прежнее разбиение 271/267 не воспроизводится ни одной методикой. На снимке `1727f6419` regex-методика давала 538: после P10/P11/P12 четыре файла — `features/account/api.ts`, `features/providerAccounts/api.ts`, `features/reader/api.ts`, `features/spend/api.ts` — перестали тянуть `types/index.ts` напрямую и берут типы из своего доменного файла; для `features/reader/api.ts` это сделал не P10/P11/P12, а `847f07ba6` («импорт типов ридера — из своего файла»). Числа в таблице — по доменам; один файл может импортировать типы из нескольких доменов, поэтому сумма по доменам больше 597.

Домены таблицы (номера строк — снимок `1727f6419`, якорь — имя экспорта; колонка «файлов-импортёров»: переснята 08.11 и соответствует текущему дереву для строк, чьи типы ещё в `types/index.ts`, и для строки «Лимиты и квоты провайдеров» — её четыре импортера берут уже перенесённые типы через реэкспорт в `types/index.ts`; остальные числа выполненных строк — на 07.11 и уже не актуальны; полный пересчёт 08.11 по перенесённым именам даёт «Проекты» 191, «Задачи» 95 — реэкспорт в `types/index.ts` остаётся допустимым источником перенесённых типов, и счёт растёт с новыми потребителями):

| Домен | Строки в types/index.ts | Экспортов | Целевой файл | Файлов-импортёров |
|---|---|---|---|---|
| Проекты: теги, темы, фон, возможности, значок (`const ProjectFeature` — значение, правило 2) | 8–242 | 16 | `features/projects/types.ts` — P14b, выполнено (`1f1a39b0b`) | 121 |
| Граф кода | 243–282 | 7 | `features/projects/types.ts` — P14a, выполнено (`8972ecacb`) | 10 |
| Документы и область | 283–455 | 19 | попутно, когда фича трогает домен | 9 |
| Доска агентов (BoardItem) | 458–471 | 1 | `features/tasks/types.ts` — P13, выполнено (`8abf5a0cb`) | 4 |
| Системный промпт (SystemPromptPart) | 472–477 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 2 |
| Группы проектов и сферы, память сфер | 478–519 | 10 | попутно, когда фича трогает домен | 22 |
| Слот моделей (ModelTierValue; начинается с маркера `// --- Задачи ---` на 520) | 520–526 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 5 (переснято 08.11) |
| Задачи, дефекты, критерии приёмки, предгейт (+ `ChatRef` из master: ссылка на чат задачи, читает его только `Task`; вне снимка 07.11) | 527–790 | 20 | `features/tasks/types.ts` — P13, выполнено (`8abf5a0cb`); `ChatRef` — `6df6463c1` | 77 |
| Настройки, биллинг, фич-флаги | 791–816 | 3 | `AppSettings`, `ClaudeBilling` → `features/account/types.ts` — P12, выполнено (`b6c04287e`/`d407055a0`); `FeatureFlagDefinition` — попутно, когда фича трогает домен | 2 для оставшегося `FeatureFlagDefinition` (переснято 08.11); 10 — вся строка на 07.11 |
| Сессии, картинки, контекст чата | 817–989 | 7 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 101 (переснято 08.11) |
| Главная (сводка) | 990–1023 | 2 | попутно, когда фича трогает домен | 12 |
| Файл проекта (FileEntry) | 1024–1035 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 8 (переснято 08.11) |
| Workflow-агенты (WorkflowAgent*, StoredWorkflowProgress) | 1038–1075 | 3 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 2 |
| ServerMessage (протокол хода по WS) | 1076–1305 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11), см. «Решение 07.11» под таблицей | 27 |
| Промпты и CLI-слои | 1306–1363 | 4 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 2 |
| Usage (счётчики токенов) (`UsageInfo`) | 1364–1370 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 1 (`lib/__tests__/context.test.ts`) |
| Проекты: сервисы, превью, запуск | 1371–1426 | 4 | `features/projects/types.ts` — P14a, выполнено (`8972ecacb`) | 8 |
| Лимиты и квоты провайдеров (`ProviderFallbackOption`, `ProviderQuotaWindowUnit`, `ProviderQuotaWindow`) | 1427–1439 + 1451–1464 (1440–1450 — `RateLimitInfo`, отдельная строка; по правилу границы `:79` шапка `RateLimitInfo` 1440 и шапка `ProviderQuotaWindowUnit` 1451 начинают свои диапазоны) | 3 | `features/spend/types.ts` — P11, выполнено (`2824b3efe`) | 4 (`components/chat/ChatItemView.tsx`, `lib/providerLimit.ts`, `lib/__tests__/providerLimit.test.ts`, `features/modelsSpend/QuotaWindow.tsx`). Простой grep по трём именам даёт 7 — трое из них не импортёры: `types/index.ts` (весь каталог `types/` исключён методикой `:7`), `features/spend/types.ts` (объявляет эти типы, `import` в файле нет), `features/codegraph/__tests__/fixtures/real-snapshot.json` (JSON-снимок графа бэка, `import … from` в нём нет) |
| RateLimitInfo (окно лимита подписки из `rate_limit_event`, близнец варианта `ServerMessage` `rate_limit`; читатели ядра чата — `chatReducer`/`useSession`/`lib/rateLimit`, ещё два — панель кита и тест `rateLimit`; полный список — в колонке «Файлов-импортёров», счёт по методике `:7`) | 1440–1450 (шапка 1440, тело 1441–1449, пустая 1450 — по правилу границы `:79`; был в диапазоне «Лимиты и квоты», 1427–1464; по состоянию на 07.11 — 1412) | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11); P11 его сознательно не перенёс — это окно лимита подписки из события CLI, а не тип расхода провайдера, `features/spend/` его не использует | 5 (`lib/chatReducer.ts`, `hooks/useSession.ts`, `lib/rateLimit.ts`, `dev/RingPillKitSection.tsx`, `lib/__tests__/rateLimit.test.ts`) |
| Баланс провайдера (ProviderBalanceInfo) | 1465–1498 | 1 | `features/spend/types.ts` — P11 (его возвращает `features/models/api.ts`, но граф у него — с типами расхода, см. «Решение 07.11» под таблицей), выполнено (`2824b3efe`) | 4 |
| Расходы, подписки, снимки использования | 1499–1640 | 8 | `features/spend/types.ts` — P11, выполнено (`2824b3efe`) | 12 |
| Аккаунты и расход fal/glif/yandex (`HiggsfieldStatus`/`HiggsfieldAdminStatus` уже вынесены) | 1641–1688 | 7 | `features/providerAccounts/types.ts` — P10, выполнено (`fca7c9f88`) | 0 |
| Механики OmO (WorkLoop) | 1691–1704 | 1 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 4 |
| Элементы ленты чата (Tool*, ChatItem) | 1707–1889 | 5 | хребет, не домен: остаётся в `types/index.ts` (решение 07.11) | 73 (переснято 08.11, команда — в преамбуле; было 70 на 07.11: +1 — правка фантома, +2 — новые потребители с мержа и фич 07.11–08.11) |
| Скиллы и агенты | 1890–1939 | 6 | попутно, когда фича трогает домен | 15 (переснято 08.11) |
| Аккаунт (Me, AuthState, UserProfile; `displayNameOf` — функция, правило 2) | 1940–2002 | 4 | `features/account/types.ts` — P12, выполнено (`b6c04287e`/`d407055a0`) | 25 |
| Участники общего проекта (из master, 07.11): роли и состав участников общего проекта, сводка участников в карточке проекта, коды отказов операций членства (фича «Общие проекты», флаг `shared-projects`). Имена: `ProjectMemberRole`, `ProjectRoleView`, `ProjectMember`, `ProjectOwnerRef`, `ProjectMembersView`, `ProjectMemberSummaryRef`, `ProjectMembersSummary`, `MEMBER_REFUSAL` (значение — правило 2: реэкспортить отдельно), `MemberRefusalCode`. Код домена — модуль экрана `features/projects/members/` (`membersLogic.ts`, `MembersList.tsx`, `MembersSection.tsx`, `MessageAuthorLine.tsx`, `messageAuthor.ts` и их тесты), REST — блок участников в `projectsApi` (`features/projects/api.ts`) | — | 9 | `features/projects/types.ts` — выполнено (`6df6463c1`): новые имена master пошли сразу в домен, минуя хребет; не `members/types.ts`: `Project` читает `ProjectMembersSummary` и `ProjectRoleView`, граф один; своего `api.ts` у `members/` нет, REST-блок — в `projectsApi` (`1ff2464e0`) | — |

Целевые файлы заполнены этими шагами (состояние на 08.11): в `features/tasks/types.ts` — 24 имени (контракт задач и дефектов строк «Задачи…» и «Доска агентов», плюс ранее вынесенные `BoardColumn` (`1727f6419`) и `FeatureSummaryResult` (`fca32c15a`), `ChatRef`); в `features/projects/types.ts` — 41 имя (карточка Project, граф кода, сервисы превью, участники; ещё 5 — `GlyphCandidate`, `Architecture*` — пришли ранее из api-части `lib/api.ts`, а не из `types/index.ts`).

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

Часть `api` (раздел 1) закрыта. Шаги типов P10–P15 выполнены (P13 `8abf5a0cb`, P14a `8972ecacb`, P14b `1f1a39b0b`, P15 `6df6463c1`), их домены вынесены в `features/` и в перечень не входят. Осталось: строки «попутно» и хребет. По размеру: `ServerMessage` (230, хребет), лента чата (183), `Документы` (173), `Сессии` (173, хребет), `Скиллы` (50), `Группы/сферы` (42), `Главная` (34); по импортёрам (переснято 08.11): лента чата (73; на 07.11 — 70), `Сессии` (101), `Группы/сферы` (22), `Скиллы` (15), `Главная` (12), `Документы` (9). Числа импортёров выполненных доменов в таблице — на 07.11; полный пересчёт 08.11 (разбор `b5be7ebf`): перенесённые имена через общий `types/index.ts` тянут **456 файлов** — 207 «своих» (файл живёт в домене этих имён), 52 кросс-фичевых, 197 каркасных (`components/`, `lib/`, `hooks/`, корень); ещё 140 файлов берут из `types/index.ts` только хребет. По доменам: «Проекты» 191, «Задачи» 95.

**Цель линии типов (разрез Александра, `d358bb59`; формулировка S5-B, 08.11).** В `types/index.ts` живёт только хребет контракта — замороженный набор имён (S5-A, 64 имени, `lib/__tests__/typesIndexExports.ts`); доменные типы живут в `features/<X>/types.ts`; новый домен в общий файл не реэкспортируется (S5-B): перечень спецификаторов `export type *` заморожен защёлкой `lib/__tests__/domainReexports.test.ts` — 24 строки в `types/index.ts` и 10 в `lib/api.ts` (08.11) — и может только убывать. **Фасады фич не заводим** (решение Александра): по его анализу 10 из 24 доменов — оболочки из 1–2 файлов (пересчёт 08.11 по текущему дереву даёт 9: `changelog`, `mapHygiene`, `quickPhrases` — по 1 файлу; `account`, `dossiers`, `hostControl`, `imageGeneration`, `providerAccounts`, `reader` — по 2; `git` дорос до 3 с тестом), фасад поверх такого домена стал бы второй точкой правды. Публичная грань домена — его `types.ts`: правило `no-cross-feature-import` с S5-B разрешает внешний импорт из `features/<X>/types.ts` наравне с `index.ts` (см. `eslint-rules/no-cross-feature-import.js`). **Перенос 456 импортёров (разбор `b5be7ebf`, выше) вынесен из фичи в отдельную бэклог-линию «Адреса типов» (AT-0…AT-4, 5 шагов кодмодом)**: строка `import` у потребителя не влияет на цену новой фичи, а её объявления уже вынесены — готовность фичи не ждёт переезда импортёров. Хребет 64 имени остаётся в `types/index.ts`; `ServerMessage` — отдельный ADR про источник правды контракта, в бэклог (см. «Решение 07.11»). Свои файлы домена читают типы из `./types`, а не через хребет (так сделано в `features/spend/`, 08.11). Общий счёт после мержа master и шагов P13/P14 вырос с 535 до 597. Крупные резать по поддоменам, ≤ 4 файлов на шаг.

Порядок линии (исполнен 08.11): `P10 → S5-A → P11 → P12 → мерж master → S5-A-MERGE` (`6df6463c1`, правило пополнения — `e0cc4c55c`) `→ P13` (`8abf5a0cb`) `→ P14a` (`8972ecacb`) `→ P14b` (`1f1a39b0b`); отдельный шаг P15 не понадобился — `6df6463c1`: участники пошли в `features/projects/types.ts` сразу из master, минуя хребет.

## 4. Защёлка

`frontend/src/lib/__tests__/apiFileBudget.test.ts` (с коммита `493a9d9fd`, заменившего старый счётчик строк) держит два разных контракта.

- **`lib/api.ts` — по строкам.** Потолок равен фактической длине файла (175 по состоянию на 07.11), сравнение строгое — ловит и рост файла, и задирание потолка руками. После выноса домена пересчитай потолок вниз; поднимать нельзя: новые методы — сразу в `features/<домен>/api.ts`. Счёт строк здесь годится: файл — почти только сборка (плюс `setStoredToken`), его длина и есть число импортов, реэкспортов и spread'ов.
- **`types/index.ts` — по именам, а не по строкам.** Замороженный набор имён верхнеуровневых объявлений (`export interface|type|const|function|enum X`; реэкспорты доменов `export type *` в сверку не входят), сравнение строгое — со списком в `frontend/src/lib/__tests__/typesIndexExports.ts`. Поле в существующем типе растёт свободно: строки файла больше не держатся. Новое объявление — красное: новый тип пиши сразу в `features/<домен>/types.ts`. Счёт в списке несущественен — сверка идёт по именам (64 имени по состоянию на 08.11, после P13/P14; 112 было на 07.11, после P12).
- **Пополнение списка** — правило в разделе 2, «Решение 07.11»: шаги переноса могут только убирать имена из списка; имена, пришедшие с мержем master, добавляет единственный шаг сверки S5-A-MERGE.
- **Реэкспорты домена (S5-B, с 08.11)** — `frontend/src/lib/__tests__/domainReexports.test.ts` со списком данных в `typesIndexExports.ts`: фактические строки `export type * from '…'` в `types/index.ts` (24) и `lib/api.ts` (10) должны быть подмножеством замороженного перечня. Новый реэкспорт домена в общий файл — красный; удаление — свободное (при удалении убери строку и из перечня). Новый домен не реэкспортируется вовсе — цель и адрес потребителей: раздел 3, «Цель линии типов».
- Новые файлы в `features/<домен>/` под потолок не попадают (см. «Правила для следующих шагов», п. 3).
