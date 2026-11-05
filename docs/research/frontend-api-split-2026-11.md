# Карта разноса api.ts и types/index.ts (2026-11)

Вход для шагов S5-1…S5-N. Образец уже сделан в S5-0: домен «Локальные модели».

## Образец (S5-0)

- Типы: `lib/api.ts` (бывшие строки 32–441) → `features/localModel/types.ts`; `lib/api.ts` делает `export type * from '../features/localModel/types'`, все импорты снаружи прежние.
- Методы: блок `localModel: {…}` → `features/localModel/api.ts` (`localModelApi`, использует `request` из `lib/offline`, а не `api`, — иначе цикл); в `api` подмешивается `...localModelApi`, адрес `api.localModel.*` не меняется.
- Правило при выносе следующего домена: те же два файла в `features/<домен>/`, тот же `export type *` и spread, затем снизить потолок в `lib/__tests__/apiFileBudget.test.ts`.

## Важно: где лежат типы

Не все типы доменов в `types/index.ts`: у ряда доменов DTO объявлены прямо в `lib/api.ts` (до объекта `api`, строки 20–560: Architecture, Deploy, Power, RemoteCommands, Plugin*, Module, Dify…). Их выносить вместе с методами домена.

## 1. Домены `api` (объект `api`, `lib/api.ts` на момент S5-0)

Диапазоны — строки между ключами верхнего уровня объекта `api`; импортёры — число файлов (кроме api.ts), где встречается `api.<домен>`. Файлы-импортёры тестами тоже считаются.

| Домен (ключ `api`) | Строки в api.ts | Строк | Целевой файл | Файлов-импортёров `api.<домен>` |
|---|---|---|---|---|
| `auth` | 562–588 | 27 | `features/auth/api.ts` | 8 |
| `push` | 589–596 | 8 | `features/push/api.ts` | 1 |
| `users` | 597–607 | 11 | `features/users/api.ts` | 2 |
| `settings` | 608–615 | 8 | `features/settings/api.ts` | 6 |
| `meModelTiers` | 616–621 | 6 | `features/meModelTiers/api.ts` | 3 |
| `adminUserModelTiers` | 622–626 | 5 | `features/adminUserModelTiers/api.ts` | 2 |
| `usage` | 627–643 | 17 | `features/usage/api.ts` | 7 |
| `spend` | 644–659 | 16 | `features/spend/api.ts` | 6 |
| `localActions` | 660–679 | 20 | `features/localActions/api.ts` | 1 |
| `backup` | 680–685 | 6 | `features/backup/api.ts` | 1 |
| `fal` | 686–689 | 4 | `features/fal/api.ts` | 3 |
| `glif` | 690–696 | 7 | `features/glif/api.ts` | 3 |
| `yandex` | 697–704 | 8 | `features/yandex/api.ts` | 2 |
| `imageGeneration` | 705–715 | 11 | `features/imageGeneration/api.ts` | 2 |
| `modules` | 716–721 | 6 | `features/modules/api.ts` | 1 |
| `subsystemModules` | 722–728 | 7 | `features/subsystemModules/api.ts` | 1 |
| `mcp` | 729–790 | 62 | `features/mcp/api.ts` | 4 |
| `devices` | 791–807 | 17 | `features/devices/api.ts` | 4 |
| `plugins` | 808–916 | 109 | `features/plugins/api.ts` | 23 |
| `providers` | 917–925 | 9 | `features/providers/api.ts` | 4 |
| `models` | 926–973 | 48 | `features/models/api.ts` | 8 |
| `video` | 974–998 | 25 | `features/video/api.ts` | 2 |
| `power` | 999–1009 | 11 | `features/power/api.ts` | 2 |
| `remoteCommands` | 1010–1032 | 23 | `features/remoteCommands/api.ts` | 2 |
| `deploy` | 1033–1046 | 14 | `features/deploy/api.ts` | 3 |
| `externalPreview` | 1047–1058 | 12 | `features/externalPreview/api.ts` | 1 |
| `deployJournal` | 1059–1062 | 4 | `features/deployJournal/api.ts` | 1 |
| `featureFlags` | 1063–1076 | 14 | `features/featureFlags/api.ts` | 1 |
| `specialties` | 1077–1123 | 47 | `features/specialties/api.ts` | 5 |
| `projects` | 1124–1406 | 283 | `features/projects/api.ts` | 53 |
| `home` | 1407–1409 | 3 | `features/home/api.ts` | 4 |
| `board` | 1410–1425 | 16 | `features/board/api.ts` | 3 |
| `projectGroups` | 1426–1440 | 15 | `features/projectGroups/api.ts` | 8 |
| `spheres` | 1441–1452 | 12 | `features/spheres/api.ts` | 5 |
| `tasks` | 1453–1564 | 112 | `features/tasks/api.ts` | 36 |
| `notes` | 1565–1674 | 110 | `features/notes/api.ts` | 22 |
| `personas` | 1675–1986 | 312 | `features/personas/api.ts` | 36 |
| `briefing` | 1987–1992 | 6 | `features/briefing/api.ts` | 1 |
| `search` | 1993–1997 | 5 | `features/search/api.ts` | 1 |
| `onboarding` | 1998–2007 | 10 | `features/onboarding/api.ts` | 1 |
| `sessions` | 2008–2096 | 89 | `features/sessions/api.ts` | 26 |
| `wall` | 2097–2108 | 12 | `features/wall/api.ts` | 5 |
| `quickPhrases` | 2109–2116 | 8 | `features/quickPhrases/api.ts` | 1 |
| `chats` | 2117–2325 | 209 | `features/chats/api.ts` | 30 |
| `watchdogs` | 2326–2330 | 5 | `features/watchdogs/api.ts` | 2 |
| `history` | 2331–2347 | 17 | `features/history/api.ts` | 4 |
| `docs` | 2348–2444 | 97 | `features/docs/api.ts` | 8 |
| `dossiers` | 2445–2498 | 54 | `features/dossiers/api.ts` | 3 |
| `telemetry` | 2499–2520 | 22 | `features/telemetry/api.ts` | 3 |
| `files` | 2521–2636 | 116 | `features/files/api.ts` | 22 |
| `hostFiles` | 2637–2642 | 6 | `features/hostFiles/api.ts` | 1 |
| `git` | 2643–2769 | 127 | `features/git/api.ts` | 12 |
| `knowledge` | 2770–2795 | 26 | `features/knowledge/api.ts` | 2 |
| `knowledgeBases` | 2796–2841 | 46 | `features/knowledgeBases/api.ts` | 7 |
| `skills` | 2842–2891 | 50 | `features/skills/api.ts` | 7 |
| `workflow` | 2892–2903 | 12 | `features/workflow/api.ts` | 1 |
| `sync` | 2904–2914 | 11 | `features/sync/api.ts` | 2 |
| `tts` | 2915–2929 | 15 | `features/tts/api.ts` | 2 |
| `plans` | 2930–2957 | 28 | `features/plans/api.ts` | 2 |
| `reader` | 2958–2974 | 17 | `features/reader/api.ts` | 1 |
| `higgsfield` | 2975–2981 | 7 | `features/higgsfield/api.ts` | 2 |

| _вне объекта: импорты, типы Architecture/Deploy/Power/RemoteCommands/Plugin*/Module/Dify, хелперы_ | 1–561 | 561 | по доменам при выносе (типы едут вместе с методами) | — |
| _хвост: Higgsfield, Reader и закрытие объекта_ | 2983–3046 | 64 | `features/higgsfield`, `features/reader` | — |

Сумма: 561 + 2 421 (ключи 562–2982) + 64 = 3 046 = длина файла. Для `types/index.ts` диапазоны таблицы раздела 2 идут встык с 1 по 4 337.

Крупнейшие: `projects`, `personas`, `chats`, `git`, `files`, `tasks`, `notes`, `docs`, `sessions` — резать по поддоменам, ≤ 4 файла на шаг.

## 2. Домены `types/index.ts` (4 337 строк)

Границы — по секционным комментариям `// ===`; между ними лежат несекционированные группы (проекты, сессии, задачи, файлы).

| Домен | Строки в types/index.ts | Целевой файл |
|---|---|---|
| Git в «Файлах» | 1084–1845 | `features/git/types.ts` |
| Генерация картинок | 1846–1918 | `features/imageGeneration/types.ts` |
| Командная реализация (КР, пульс, перезапуск) | 1919–2464 | `features/team/types.ts` |
| Продуктовая история (changelog) | 2465–2514 | `features/changelog/types.ts` |
| Заметки | 2515–2681 | `features/notes/types.ts` |
| Знания (Dify) | 2682–2740 | `features/knowledge/types.ts` |
| Персоны, привязки, память | 2741–3336 | `features/personas/types.ts` |
| Уведомления | 3337–3387 | `features/notifications/types.ts` |
| Spend Analytics | 3388–3727 | `features/spend/types.ts` |
| Каталог MCP | 3728–3922 | `features/mcp/types.ts` |
| Инциденты телеметрии | 3923–4006 | `features/telemetry/types.ts` |
| Видео | 4007–4131 | `features/video/types.ts` |
| Карта плана | 4132–4181 | `features/plans/types.ts` |
| Уборка карты (CLAUDE.md) | 4182–4337 | `features/mapHygiene/types.ts` |
| Начало файла (проекты, теги, темы, документация, область) | 1–1083 | по поддоменам, нарезать при выносе `projects`/`docs` |

Импортёров `types/index.ts` по графу мало (17 файлов — остальные берут через `../types`; проверять grep'ом `from '.*types'` перед выносом).

## 3. Порядок шагов

1. Малые и независимые: `push`, `users`, `wall`, `quickPhrases`, `tts`, `plans`, `reader`, `higgsfield` — по 2–3 домена на шаг.
2. Средние: `video`, `power`/`remoteCommands`/`deploy`, `plugins`, `mcp`, `spend`, `knowledge*`.
3. Крупные по одному: `notes`, `tasks`, `git`, `docs`, `personas`, `chats`/`sessions`, `projects`, `files`.

## 4. Защёлка

`frontend/src/lib/__tests__/apiFileBudget.test.ts` держит потолок строк `api.ts` и `types/index.ts`. Потолок только снижается: каждый шаг выноса в конце пересчитывает его вниз.
