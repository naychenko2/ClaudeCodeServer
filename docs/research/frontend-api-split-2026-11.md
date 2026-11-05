# Карта разноса api.ts и types/index.ts (2026-11)

Вход для шагов S5-1…S5-N. Образец уже сделан в S5-0: домен «Локальные модели».

Диапазоны в обоих разделах пересчитаны по ФАКТУ на 05.11 (после S5-1): строки в `lib/api.ts` и в `frontend/src/types/index.ts` — как в репозитории, а не как в снимке на момент S5-0. Числа импортёров — скриптом по разбору `import … from` (путь резолвится до `types/index.ts`; сам `types/index.ts` не считается). Граф кода тут не годится: `codegraph_neighbors` по TS-файлам межфайловых связей не строит («узел не найден»).

## Образец (S5-0)

- Типы: `lib/api.ts` (бывшие строки 32–441) → `features/localModel/types.ts`; `lib/api.ts` делает `export type * from '../features/localModel/types'`, все импорты снаружи прежние.
- Методы: блок `localModel: {…}` → `features/localModel/api.ts` (`localModelApi`, использует `request` из `lib/offline`, а не `api`, — иначе цикл); в `api` подмешивается `...localModelApi`, адрес `api.localModel.*` не меняется.
- Домен «Аккаунты провайдеров» (S5-1): ключи `fal`, `glif`, `yandex`, `higgsfield` и типы `HiggsfieldStatus`/`HiggsfieldAdminStatus` → `features/providerAccounts/`; в `lib/api.ts` — spread `...providerAccountsApi` (строка 689) и `export type *`. Адреса `api.fal.*`, `api.glif.*`, `api.yandex.*`, `api.higgsfield.*` прежние.

## Правила для следующих шагов

1. Те же два файла в `features/<домен>/` (`api.ts` + `types.ts`), тот же `export type *` и spread, затем снизить потолок в `lib/__tests__/apiFileBudget.test.ts`.
2. **`export type *` не переносит значения.** Если в `features/<домен>/types.ts` появится константа (а не только тип), реэкспорт `export type *` её потеряет МОЛЧА: потребитель получит ошибку импорта, а не внятное «нет такой константы». Поэтому значения (константы, enum-ы со значениями, функции) в `types.ts` домена не объявлять; либо реэкспортировать их отдельно через `export { X } from …` (или `export *`), либо держать в `api.ts` домена.
3. Защёлка (`apiFileBudget.test.ts`) держит потолок только для `lib/api.ts` и `types/index.ts`. Новые `features/<домен>/{api,types}.ts` (сейчас 155 и 411 строк) под потолок не попадают — их рост ничем не ограничен. Это по замыслу (сторож — горячие точки), но помнить: разнесённый домен может разрастись; потолок на него заводить отдельным правилом, если он стал заметным.

## Важно: где лежат типы

Не все типы доменов в `types/index.ts`: у ряда доменов DTO объявлены прямо в `lib/api.ts` (до объекта `api`, строки 1–563: Architecture, Deploy, Power, RemoteCommands, Plugin*, Module, Dify, ModelTiers, GlyphCandidate, FeatureSummaryResult…). Их выносить вместе с методами домена.

## 1. Домены `api` (объект `api`, `lib/api.ts` на 05.11)

Диапазоны — от строки ключа до строки перед следующим ключом верхнего уровня объекта `api`; импортёры — число файлов (кроме `api.ts`), где встречается `api.<домен>`. Файлы-импортёры тестами тоже считаются. Ключи `fal`, `glif`, `yandex`, `higgsfield` уже вынесены в `features/providerAccounts/` (S5-1) — в объекте их нет, вместо них spread `...providerAccountsApi` (строка 689); в таблице они не перечислены.

| Домен (ключ `api`) | Строки в api.ts | Строк | Целевой файл | Файлов-импортёров `api.<домен>` |
|---|---|---|---|---|
| `auth` | 565–591 | 27 | `features/account/api.ts` | 8 |
| `push` | 592–599 | 8 | `features/account/api.ts` | 1 |
| `users` | 600–610 | 11 | `features/account/api.ts` | 2 |
| `settings` | 611–618 | 8 | `features/account/api.ts` | 6 |
| `meModelTiers` | 619–624 | 6 | `features/account/api.ts` | 3 |
| `adminUserModelTiers` | 625–629 | 5 | `features/account/api.ts` | 2 |
| `usage` | 630–646 | 17 | `features/spend/api.ts` | 7 |
| `spend` | 647–662 | 16 | `features/spend/api.ts` | 6 |
| `localActions` | 663–682 | 20 | `features/models/api.ts` | 1 |
| `backup` | 683–694 | 12 | `features/hostControl/api.ts` | 1 |
| `imageGeneration` | 695–705 | 11 | `features/imageGeneration/api.ts` | 2 |
| `modules` | 706–711 | 6 | `features/modules/api.ts` | 1 |
| `subsystemModules` | 712–718 | 7 | `features/modules/api.ts` | 1 |
| `mcp` | 719–780 | 62 | `features/mcp/api.ts` | 4 |
| `devices` | 781–797 | 17 | `features/desktop/api.ts` | 4 |
| `plugins` | 798–906 | 109 | `features/plugins/api.ts` | 23 |
| `providers` | 907–915 | 9 | `features/models/api.ts` | 4 |
| `models` | 916–963 | 48 | `features/models/api.ts` | 8 |
| `video` | 964–988 | 25 | `features/video/api.ts` | 2 |
| `power` | 989–999 | 11 | `features/hostControl/api.ts` | 2 |
| `remoteCommands` | 1000–1022 | 23 | `features/hostControl/api.ts` | 2 |
| `deploy` | 1023–1036 | 14 | `features/deploy/api.ts` | 3 |
| `externalPreview` | 1037–1048 | 12 | `features/projects/api.ts` | 1 |
| `deployJournal` | 1049–1052 | 4 | `features/deploy/api.ts` | 1 |
| `featureFlags` | 1053–1066 | 14 | `features/modules/api.ts` | 1 |
| `specialties` | 1067–1113 | 47 | `features/specialties/api.ts` | 5 |
| `projects` | 1114–1396 | 283 | `features/projects/api.ts` | 53 |
| `home` | 1397–1399 | 3 | `features/home/api.ts` | 4 |
| `board` | 1400–1415 | 16 | `features/tasks/api.ts` | 3 |
| `projectGroups` | 1416–1430 | 15 | `features/spheres/api.ts` | 8 |
| `spheres` | 1431–1442 | 12 | `features/spheres/api.ts` | 5 |
| `tasks` | 1443–1554 | 112 | `features/tasks/api.ts` | 36 |
| `notes` | 1555–1664 | 110 | `features/notes/api.ts` | 22 |
| `personas` | 1665–1976 | 312 | `features/personas/api.ts` | 36 |
| `briefing` | 1977–1987 | 11 | не назначено | 1 |
| `onboarding` | 1988–1997 | 10 | не назначено | 1 |
| `sessions` | 1998–2086 | 89 | `features/sessions/api.ts` | 26 |
| `wall` | 2087–2098 | 12 | `features/chat/api.ts` | 5 |
| `quickPhrases` | 2099–2106 | 8 | `features/chat/api.ts` | 1 |
| `chats` | 2107–2315 | 209 | `features/chat/api.ts` | 30 |
| `watchdogs` | 2316–2320 | 5 | `features/chat/api.ts` | 2 |
| `history` | 2321–2337 | 17 | `features/chat/api.ts` | 4 |
| `docs` | 2338–2434 | 97 | `features/docs/api.ts` | 8 |
| `dossiers` | 2435–2488 | 54 | `features/dossiers/api.ts` | 3 |
| `telemetry` | 2489–2510 | 22 | `features/telemetry/api.ts` | 3 |
| `files` | 2511–2626 | 116 | `features/files/api.ts` | 22 |
| `hostFiles` | 2627–2632 | 6 | `features/hostControl/api.ts` | 1 |
| `git` | 2633–2759 | 127 | `features/git/api.ts` | 12 |
| `knowledge` | 2760–2785 | 26 | `features/knowledge/api.ts` | 2 |
| `knowledgeBases` | 2786–2831 | 46 | `features/knowledge/api.ts` | 7 |
| `skills` | 2832–2881 | 50 | `features/skills/api.ts` | 7 |
| `workflow` | 2882–2893 | 12 | `features/team/api.ts` | 1 |
| `sync` | 2894–2904 | 11 | `features/files/api.ts` | 2 |
| `tts` | 2905–2919 | 15 | `features/chat/api.ts` | 2 |
| `plans` | 2920–2947 | 28 | `features/plan/api.ts` | 2 |
| `reader` | 2948–2960 | 13 | `features/reader/api.ts` | 1 |

| _вне объекта: импорты, DTO Architecture/Deploy/Power/RemoteCommands/Plugin*/Module/Dify, хелперы `setGitSessionContext`/`getGitSessionContext`/`panelEntryPath` и plugin-хелперы_ | 1–563 | 563 | по доменам при выносе (типы едут вместе с методами) | — |
| _хвост: пять хелперов Ридера (`checkReaderEmbeddable`, `normalizeReaderError`, `READER_ERROR_CODES`, тип `ReaderReadResult`, `readReaderPage`); закрытие объекта `};` — на строке 2961, в хвост НЕ входит_ | 2962–3013 | 52 | `features/reader` | — |

Сумма: 563 (вне объекта) + 2 398 (объект `api`, строки 564–2961) + 52 (хвост) = 3 013 = длина файла. Ширины блоков в таблице (строки между ключами) в сумму не входят — это комментарии и пустые строки между доменами; нарезку вести по ключам, а не по сумме.

Крупнейшие: `projects`, `personas`, `chats`, `git`, `files`, `tasks`, `notes`, `docs`, `sessions` — резать по поддоменам, ≤ 4 файлов на шаг.

## 2. Домены `types/index.ts` (4 337 строк)

Границы — по факту: домены выделены по экспортам и смыслу, а не только по секционным комментариям `// ===`. Комментарий `// === Типизированные поля «Модели и расход» (models-spend-data-spec) ===` стоит на строке 1635 — ВНУТРИ интерфейса `ProviderBalanceInfo` (1625–1654). Это пометка полей внутри типа, а не граница домена: отдельной строкой его выделять нечего, домен «Баланс провайдера» покрывает 1621–1655 целиком.

`ServerMessage` (1234–1462) — отдельный домен: его импортируют 27 файлов фронта прямо из `types/index.ts` (ещё 5 файлов упоминают имя, но импортируют из другого места). Уезжать вместе с 9 Git-типами ему нельзя.

Импортёров `types/index.ts` — **509 файлов** (271 вне `features/`, 238 в `features/`), а не «мало»: почти все берут через `from '../types'`, а не через прямой `types/index`. Проверено grep'ом по разбору импортов; граф кода по TS-файлам межфайловых связей не строит («узел не найден»). Числа в таблице — по доменам; один файл может импортировать типы из нескольких доменов, поэтому сумма по доменам больше 509.

| Домен | Строки в types/index.ts | Экспортов | Целевой файл | Файлов-импортёров |
|---|---|---|---|---|
| Проекты: теги, темы, фон, возможности, значок | 5–239 | 16 | не назначено | 121 |
| Колонка доски проекта | 240–248 | 1 | не назначено | 5 |
| Граф кода | 249–289 | 7 | не назначено | 10 |
| Документы и область | 290–462 | 19 | не назначено | 9 |
| Истории решений (dossier) | 463–505 | 3 | не назначено | 2 |
| Доска агентов (BoardItem) | 506–519 | 1 | не назначено | 4 |
| Системный промпт (SystemPromptPart) | 520–525 | 1 | не назначено | 2 |
| Группы проектов и сферы, память сфер | 526–572 | 10 | не назначено | 22 |
| Слот моделей (ModelTierValue) | 573–575 | 1 | не назначено | 2 |
| Задачи, дефекты, критерии приёмки, предгейт | 576–839 | 20 | не назначено | 73 |
| Настройки, биллинг, фич-флаги | 840–865 | 3 | не назначено | 10 |
| Сессии, картинки, контекст чата | 866–1038 | 7 | не назначено | 91 |
| Главная (сводка) | 1039–1071 | 2 | не назначено | 12 |
| Файл проекта (FileEntry) | 1072–1083 | 1 | не назначено | 7 |
| Git в разделе «Файлы» | 1084–1188 | 10 | `features/git/types.ts` (S5-23) | 10 |
| Синхронизация и WorkflowAgent | 1190–1233 | 4 | `features/sync/types.ts` + `features/team/types.ts` — не назначено | 2 |
| ServerMessage (протокол хода по WS) | 1234–1462 | 1 | не назначено | 27 |
| Промпты и CLI-слои | 1463–1520 | 4 | не назначено | 2 |
| Usage (счётчики токенов) | 1521–1527 | 1 | `features/spend/types.ts` (S5-21) | 1 |
| Проекты: сервисы, превью, запуск | 1528–1596 | 5 | не назначено | 11 |
| Лимиты и квоты провайдеров | 1597–1620 | 3 | не назначено | 7 |
| Баланс провайдера (ProviderBalanceInfo) | 1621–1655 | 1 | не назначено | 4 |
| Расходы, подписки, снимки использования | 1656–1798 | 8 | `features/spend/types.ts` (S5-21) | 12 |
| Аккаунты и расход fal/glif/yandex | 1799–1845 | 7 | `features/providerAccounts/types.ts` (S5-1) | 1 |
| Генерация картинок | 1846–1905 | 7 | `features/imageGeneration/types.ts` (S5-15) | 6 |
| Механики OmO (WorkLoop) | 1906–1918 | 1 | не назначено | 4 |
| Командная реализация (КР, пульс, перезапуск) | 1919–2219 | 24 | `features/team/types.ts` (S5-23) | 22 |
| Элементы ленты чата (ChatItem) | 2220–2352 | 1 | не назначено | 68 |
| Скиллы и агенты | 2353–2405 | 6 | не назначено | 14 |
| Аккаунт (Me, AuthState, UserProfile) | 2406–2464 | 4 | не назначено | 26 |
| Продуктовая история (changelog) | 2465–2514 | 5 | `features/changelog/types.ts` (S5-15) | 3 |
| Заметки | 2515–2681 | 16 | `features/notes/types.ts` (S5-21) | 27 |
| Знания (Dify) | 2682–2740 | 9 | `features/knowledge/types.ts` (S5-16) | 9 |
| Скелеты персоны (scope, доступ, пол) | 2741–2758 | 3 | `features/personas/types.ts` (S5-22) | 7 |
| Специальности и пресеты | 2759–2954 | 19 | `features/specialties/types.ts` (S5-22) | 27 |
| Персоны, аватары, голос, автоматизации, привязки, память | 2955–3336 | 33 | `features/personas/types.ts` (S5-22) | 80 |
| Уведомления | 3337–3387 | 4 | `features/notifications/types.ts` (S5-16) | 5 |
| Аналитика расхода токенов (Spend Analytics) | 3388–3546 | 14 | `features/spend/types.ts` (S5-21) | 8 |
| Бэкапы | 3547–3579 | 3 | `features/hostControl/types.ts` (S5-20) | 2 |
| Ридер (Reader) | 3580–3601 | 3 | `features/reader/types.ts` (S5-19) | 3 |
| Реестр MCP-серверов | 3602–3727 | 10 | `features/mcp/types.ts` (S5-20) | 7 |
| Каталог MCP | 3728–3922 | 14 | `features/mcp/types.ts` (S5-20) | 7 |
| Инциденты телеметрии | 3923–4000 | 8 | `features/telemetry/types.ts` (S5-17) | 2 |
| Быстрые фразы (QuickPhrase) | 4001–4006 | 1 | не назначено | 2 |
| Видео | 4007–4075 | 8 | `features/video/types.ts` (S5-18) | 11 |
| Устройства и локальные руки | 4076–4131 | 4 | `features/desktop/types.ts` (S5-18) | 6 |
| Карта плана | 4132–4181 | 6 | `features/plan/types.ts` (S5-17) | 7 |
| Уборка карты (CLAUDE.md) | 4182–4337 | 13 | `features/mapHygiene/types.ts` (S5-19) | 4 |

Диапазоны идут встык с 5 по 4 337 (строки 1–4 — шапка файла). «Не назначено» — домен ещё не закреплён за шагом; нарезка S5-1…S5-32 идёт по якорям-комментариям, а не по этой таблице, но при выборе целевого файла смотреть сюда.

## 3. Порядок шагов

1. Малые и независимые: `push`, `users`, `wall`, `quickPhrases`, `tts`, `plans`, `reader` — по 2–3 домена на шаг.
2. Средние: `video`, `power`/`remoteCommands`/`deploy`, `plugins`, `mcp`, `spend`, `knowledge*`.
3. Крупные по одному: `notes`, `tasks`, `git`, `docs`, `personas`, `chats`/`sessions`, `projects`, `files`.

## 4. Защёлка

`frontend/src/lib/__tests__/apiFileBudget.test.ts` держит потолок строк `api.ts` и `types/index.ts`. Потолок только снижается: каждый шаг выноса в конце пересчитывает его вниз. Новые файлы в `features/<домен>/` под потолок не попадают (см. «Правила для следующих шагов», п. 3).
