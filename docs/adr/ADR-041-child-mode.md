# ADR-041: детский режим — профиль пользователя и allow-list

- **Статус:** принято 2026-11-17. Решения Андрея от 17.11 — в разделе «Решения Андрея». Код волны 1 идёт веткой `feat/kids-mode-w1` (эпик 45af8178).
- **Вход:** разрез волны 1 — [kids-mode-wave1-cut-2026-11.md](../research/kids-mode-wave1-cut-2026-11.md) (там же блоки Б1–Б8 и обоснования по коду). Образцы точек правды — `ProjectCapabilities` (ADR-016) и `AdminByStoreHandler`.
- **Область:** волна 1 — профиль режима, жёсткий гейт на уровне пользователя, окна и лимиты, «Уроки» по API-ключу с голосом, онбординг правил, минимальный стоп-сигнал. Студия, кабинет родителя и фильтр модерации — волна 2.

## Контекст

Ребёнок работает с продуктом как отдельный пользователь, но может только малую часть того, что может взрослый: разговор с закреплённым тьютором в пространстве «Уроки», в разрешённые окна времени, в пределах бюджета и только по API-ключу организации, а не по потребительской подписке (меры Anthropic для несовершеннолетних). Пользовательских контроллеров около ста двадцати, и они прибавляются каждый месяц, поэтому перечень запретов протухнет на первой новой ручке.

## Решения

1. **Ребёнок = `user` + `User.ChildMode : ChildProfile?`, новой роли нет.** Роль проверяется строкой `[Authorize(Roles="admin")]` в 27 файлах, а сервисный токен хода всегда несёт роль `user` — новая роль разъехалась бы с ними. Профиль включает только админ ручкой `PUT /api/users/{id}/child-profile`; профиль проверяется **по стору**, а не по claim, поэтому сразу действует и для токена хода. Поле добавочное, `BackupSchema.Version` не меняется.
2. **Единственная точка правды — `IChildModePolicy.For(ownerId)` в Core**, fail-closed: профиль есть, но не читается или не валиден — снимок «запрещено всё». Инлайновых `user.ChildMode?.Enabled` не заводим. Фич-флаг, если появится, прячет только интерфейс включения у админа: выключенный флаг не снимает с ребёнка ни одного запрета.
3. **Ограничения — allow-list с отказом по умолчанию**: метаданные эндпоинта `[ChildAllowed]` (middleware после `UseAuthorization` и до `UseModuleGateway`), методы хаба (`ChildHubFilter`), пары «MCP-сервер, инструмент» (`IChildModeGate`), встроенные инструменты CLI. Последний рубеж (И1-бис) — сверка закреплённых полей чата при запуске хода: персона, провайдер, модель, `ProjectId == null`, режим. Расхождение — ход не стартует, подстановки по-тихому нет.
4. **Лимиты и бюджет проверяются в точке запуска хода** (И3), а не на входах: так покрыты `SendMessage`, автопродолжение, ретраи и фолбэк. Начатый ход доживает до конца (перерасход в один ход). Бюджет — через Core-шов `IOwnerSpendReader`; подсистема Spend выключена — ход ребёнка отказывается. Время в пространстве считается присутствием (heartbeat), а не длительностью ходов.
5. **Модель «Уроков» — отдельная запись пула `ClaudeSubscriptionPool` с `ApiKey` и без `OAuthToken`** (`ChildProfile.ProviderKey`). Подписка вычищается из env (`CLAUDE_CODE_OAUTH_TOKEN`), из доставки в песочницу, из фолбэка (цепочка детского хода пуста) и из фоновых действий (И2). Нет ключа — отказ запуска, а не откат на подписку.
6. **Окна времени — один Core-хелпер `DailyTimeWindow`**, на него же переходит `PersonaAutomationService.InQuietWindow`; второй реализации нет.
7. **Чтения переписки родителем в волне 1 нет.** `SessionStore.GetOwned` остаётся строгим, общего админ-обхода не будет никогда. В волне 2 — отдельный `GetForGuardian` только при открытом стоп-сигнале и с видимой ребёнку пометкой.

### Отвергнуто

- Роль `child` — расходится с 27 строковыми проверками и сервисным токеном хода.
- Перечень запретов — протухает на первой новой ручке.
- Маршрут через пресеты и слоты — цепочка фолбэка ведёт на подписку.
- Общий админ-доступ к чужим чатам.
- Перенос `apply-transcript` онбординга в детские правила — он переписывает персону; детская ручка пишет только факт согласия.
- Открыть ребёнку инструмент `Read` ради вложений — вложения разбирает сервер (решение Андрея 5).

## Решения Андрея (2026-11-17)

1. **Стоп-сигнал — в волне 1.** Два источника: маркер тьютора в ответе и кнопка «Мне неприятно» в оболочке. Оба ведут в одну ручку `POST /api/child/stop-signal` и шлют уведомление админу и опекунам (`GuardianUserIds`) **без доступа к переписке**: в уведомлении — кто, когда, пространство и источник, но не текст.
2. **Веб-поиск в «Уроках» выключен:** MCP-сервер `websearch` вне детского allow-list, встроенные `WebFetch`/`WebSearch` CLI — тоже, пока нет фильтра выхода.
3. **Окна без `User.TimeZone` считаются по таймзоне инстанса** `ChildMode:DefaultTimeZone` = `Europe/Moscow`, не по UTC.
4. **Без микрофона — голосового ввода нет:** браузерное распознавание (`voiceInput.ts`, Chrome отправляет звук в Google) у ребёнка не включается, микрофона в оболочке нет. Озвучка ответов (TTS) остаётся и входит в бюджет.
5. **Вложения — вариант «б»:** узкая ручка `POST /api/child/chats/{id}/attachments`, allow-list типов по сигнатуре файла, PDF превращается в текст на сервере, инструмент `Read` ребёнку не открывается. Картинка доходит до модели image-блоком, текст — инлайном (`AttachmentInliner`, как у обычных вложений).
6. **Миниатюра картинки в пузыре — для всех пользователей** (решение 17.11, после плана волны). Приложенная картинка показывается в пузыре сообщения миниатюрой, у взрослых и у ребёнка одинаково; не-картинка остаётся ярлыком с именем. Источник — одна читающая ручка №12 (вариант «а» Александра): одна граница доступа и одна точка URL на фронте. Ресайза на сервере в волне 1 нет: оригинал до `maxAttachmentBytes`, `loading="lazy"`, размер ограничен CSS; `?w=` можно добавить за той же ручкой без смены контракта.

## Инвариант И1 и таблица путей

**И1.** Ребёнок через любой путь может только то, что явно разрешено детским allow-list.

| # | Путь входа | Где закрывается | Блок |
|---|---|---|---|
| 1 | REST-контроллеры (≈120, MVC, включая контроллеры динамических модулей и `Plugins`) | `ChildModeGateMiddleware` после `UseAuthorization` и до `UseModuleGateway`: пропускает только эндпоинты с `[ChildAllowed]`, остальным 403 `{code:"child_mode"}` | Б2 |
| 2 | Minimal API (`app.MapGet/MapPost`) | тот же middleware | Б2 |
| 3 | Внешние модули YARP (`UseModuleGateway`, `MapReverseProxy`) | тот же middleware: стоит раньше, у прокси нет `[ChildAllowed]` | Б2 |
| 4 | Превью дев-серверов `/preview/**` | тот же middleware | Б2 |
| 5 | Хаб `/hubs/terminal` | отказ на подключении | Б2 |
| 6 | Хаб `/hubs/devices` | отказ на подключении | Б2 |
| 7 | Хаб `/hubs/session`: `SendMessage`, `SetMode`, `CompactSession` и прочие | `ChildHubFilter` пускает только `SendMessage` и подписки; `mode` и `auto` у ребёнка сервер игнорирует, режим — из профиля | Б2 |
| 8 | WebDAV (`WebDavHandler`, своя аутентификация) | отказ по `IChildModePolicy` в обработчике | Б2 |
| 9 | MCP-over-HTTP `POST /mcp/{name}` | третий гейт рядом с `whitelist` и `actorGate`: `IChildModeGate.Denied(server, tool, ownerId)`, fail-closed | Б3 |
| 10 | Состав MCP-серверов хода (`TurnLaunchContextFactory.Compose`, `BuiltinMcpContexts`) | объявляются только разрешённые серверы; внешние серверы личного реестра — нет | Б3 |
| 11 | Встроенные инструменты CLI (Bash, Edit, Write, Read, Glob, Grep, Task, Skill, WebFetch, WebSearch…) | детский слой в `PersonaLayerBuilder.BuildExtraDisallowed` складывается с запретами персоны и этапа; если CLI умеет разрешающий список (`--tools`) — он, иначе запреты со сторожем против списка инструментов текущей версии CLI | Б3 |
| 12 | Запросы разрешений (`DecidePermissionAsync`, `AutoAllowTools`) | вне allow-list — автоотказ без карточки | Б3 |
| 13 | Плагины, навыки, хуки, CLAUDE.md | `BareMode` и пустой `IPluginDirSource` | Б3 |
| 14 | Смена модели, провайдера, effort (`PUT api/chats/{id}`, `migrate-provider`, `PUT api/me/model-tiers`) | закрыто п.1; голос — узкая `PUT /api/child/chats/{id}/voice` | Б2, Б6 |
| 15 | Смена персоны чата, создание и правка персон | закрыто п.1, повторно — И1-бис | Б2, Б3 |
| 16 | Создание чатов и проектов (`POST api/chats`, `api/projects/{id}/sessions`, `api/projects`) | закрыто п.1; детский чат — только `POST /api/child/spaces/{space}/chat` | Б2, Б6 |
| 17 | Файлы, git, плагины, MCP-реестр | закрыто п.1 | Б2 |
| 18 | Фоновые действия над детскими чатами (теги, сводки, память, вики) | маршрут выключен или принудительно локальный; автоматика персон детского владельца не создаётся | Б3 |
| 19 | Цикл «до готово» и автопродолжение хода | закрыто п.1 и п.7, пускает ли ход — И3 | Б5 |
| 20 | Загрузка вложений | `files/upload` (`ChatsController`, `FilesController`, `PersonalFilesController`) закрыт п.1; `POST /api/child/chats/{id}/attachments` открыт, ограничен allow-list типов и размером | Б2, Б6 |
| 21 | Веб-поиск | `websearch` вне детского allow-list (п.9–10), `WebFetch`/`WebSearch` CLI — п.11 | Б3 |
| 22 | Чтение вложений чата (миниатюры) | `GET /api/chats/{id}/attachments/raw` (№12) — `[ChildAllowed]`, у ребёнка только свой чат с `ChildSpace`; файл — только учтённый в `Session.Attachments` этого чата; `files/stream` и `PersonalFilesController` закрыты п.1 | Б2, Б6 |

**И1-бис** — сверка полей чата при запуске хода (решение 3). **Правило `tools/list`:** состав зависит только от владельца (профиль — свойство пользователя, не хода), включение профиля один раз перезапускает процесс CLI.

**Сторожа:** `ChildModeEndpointGuardTests` (перебор `EndpointDataSource`, детский токен получает 403 везде без `[ChildAllowed]`, сам список `[ChildAllowed]` заморожен в тесте), `ChildModeMcpGuardTests` (каждый инструмент каждого тулсета против allow-list), мутация — снятый middleware валит сторож.

## Инвариант И2: ход ребёнка никогда не идёт по подписке

| Путь | Где закрывается |
|---|---|
| Наследуемый `CLAUDE_CODE_OAUTH_TOKEN` (чистка env его оставляет) | для детского хода вычищается, ставится изолированный `CLAUDE_CONFIG_DIR` |
| Выбор подписки из пула | `ProviderKey` — запись только с `ApiKey`; без ключа — отказ запуска |
| Доставка токена подписки в песочницу (`DockerProcessRunner`) | детскому владельцу не доставляется |
| Фолбэк хода (ADR-007) | цепочка пуста: отказ провайдера = ошибка хода |
| Шлюз `/gw` (`UpstreamSelector.IsSubscription`) | локальных проектов у ребёнка нет; сторож — детский владелец не получает `IsSubscription = true` |
| Фоновые one-shot | локальная модель или выключено (И1 п.18) |

Свидетельство Б4 — на живом CLI: запись `api-request` в транскрипте и строка расхода по этому ключу в консоли Anthropic.

## Инвариант И3: вне окна, сверх лимита и сверх бюджета новый ход не стартует

Проверка — в точке запуска хода (решение 4). Порядок причин одинаков для статуса и для отказа хода: `rules_pending` → `budget_exhausted` → `out_of_window` → `time_up` → `warn` → `ok`.

## Контракт /api/child/*

От этого раздела фронт (Б7) стартует параллельно с бэком; бэк-шаги обязаны совпасть с ним буквально. Расхождение правится здесь, а не в коде одной стороны.

**Общие правила.**
- Все ручки — `[Authorize]` + `[ChildAllowed]`. Вызов не детским пользователем — 403 `{code:"not_child"}` (кроме админской ручки профиля и `auth/me`).
- Тело ошибки везде `{ code: string, message: string }`; `message` — текст для ребёнка по-русски, фронт может показывать его как есть. Отказ middleware на ручке вне allow-list — 403 `{code:"child_mode", message}`.
- `space` — строка-ключ пространства. В волне 1 известен только `"lessons"`; другой ключ — 400 `unknown_space`, ключ без записи в профиле — 403 `space_disabled`.
- Время — ISO 8601 с явным смещением таймзоны ребёнка (`User.TimeZone`, иначе `ChildMode:DefaultTimeZone`).
- `{id}` чата — только детский чат этого же ребёнка (`Session.ChildSpace != null`, владелец — вызывающий); иначе 404 `chat_not_found` (не 403: существование чужого чата не раскрываем).

**DTO.**

```ts
type ChildState = 'ok' | 'warn' | 'time_up' | 'out_of_window' | 'rules_pending' | 'budget_exhausted';

interface ChildStatus {
  state: ChildState;            // старшая причина по порядку И3
  space: string;                // "lessons"
  minutesLeft: number | null;   // остаток дневного лимита пространства; null — лимита нет
  windowEndsAt: string | null;  // конец текущего окна; null — окон нет или сейчас вне окна
  nextWindowAt: string | null;  // начало ближайшего окна, когда state = out_of_window; иначе null
  warnMinutes: number;          // порог предупреждения из профиля (по умолчанию 5)
  maxAttachmentBytes: number;   // ChildMode:MaxAttachmentBytes, по умолчанию 10 МБ
  attachmentTypes: string[];    // MIME из allow-list: image/png, image/jpeg, image/webp, application/pdf, text/plain
  rulesAgreedAt: string | null;
}

interface ChildProfileDto {     // тело PUT child-profile и поле ответа
  enabled: boolean;
  guardianUserIds: string[];
  providerKey: string;          // запись пула с ApiKey
  model: string;
  spaces: Record<string, { windows: { from: string; to: string; weekdays?: number[] }[]; dailyMinutes?: number | null }>;
  dailyBudgetUsd?: number | null;
  monthlyBudgetUsd?: number | null;
  warnMinutes?: number;         // по умолчанию 5
  resetRules?: boolean;         // только во входе: сбросить RulesAgreedAt/RulesSummary
}
```

`from`/`to` — `"HH:mm"`, `to < from` — окно через полночь; `weekdays` — ISO 1..7, нет поля — каждый день.

**Ручки.**

| # | Метод и путь | Запрос | Ответ 2xx | Отказы |
|---|---|---|---|---|
| 1 | `GET /api/child/status?space=lessons` | `space` необязателен, по умолчанию `lessons` | 200 `ChildStatus` | 400 `unknown_space`, 403 `space_disabled`, 403 `not_child`, 503 `policy_unavailable` (профиль не читается — fail-closed) |
| 2 | `POST /api/child/heartbeat` | `{ space: string }` | 200 `ChildStatus` (тот же, что у status — экономит второй запрос) | 400 `unknown_space`, 403 `space_disabled`, 403 `not_child`, 503 `policy_unavailable` (профиль не читается или минута присутствия не записана — fail-closed) |
| 3 | `POST /api/child/spaces/{space}/chat` | тело пустое | 201 `{ sessionId: string }` — новый чат с закреплёнными персоной-тьютором, провайдером, моделью, `ChildSpace`, `ProjectId = null` | 400 `unknown_space`, 403 `space_disabled`, 409 `{code: <state>}` при `rules_pending`, `budget_exhausted`, `out_of_window`, `time_up` |
| 4 | `GET /api/child/spaces/{space}/chats` | — | 200 `{ chats: { sessionId, name, updatedAt }[] }` — детские чаты пространства, новые сверху | 400 `unknown_space`, 403 `space_disabled` |
| 5 | `PUT /api/child/chats/{id}/voice` | `{ enabled: boolean, style?: "talk" \| "digest" }` | 200 `{ voiceMode: boolean, voiceStyle: string }` | 400 `invalid_style`, 404 `chat_not_found` |
| 6 | `POST /api/child/rules/start` | тело пустое | 200 `{ sessionId }` — существующий незавершённый чат онбординга или новый (`OnboardingKinds.ChildRules`), идемпотентно | 409 `rules_already_agreed`, 503 `policy_unavailable` |
| 7 | `POST /api/child/rules/agree` | `{ sessionId: string, summary: string }`, summary 1..2000 символов | 200 `{ rulesAgreedAt: string }`; пишет **только** `RulesAgreedAt` и `RulesSummary`, лимиты не трогает никогда | 400 `summary_empty`, 400 `summary_too_long`, 404 `not_rules_chat` (не онбординг этого ребёнка), 409 `rules_already_agreed` |
| 8 | `POST /api/child/chats/{id}/attachments` | `multipart/form-data`, поле `file` | 200 `{ path: string, kind: "image" \| "text", name: string }` — `path` относительный (`.cc-attachments/…`), его фронт передаёт в `SendMessage(attachedPaths)`; у PDF `path` указывает на извлечённый `.txt` | 413 `too_large` (> `maxAttachmentBytes`), 415 `unsupported_type` (сигнатура вне allow-list; расширение и `Content-Type` не учитываются), 422 `pdf_no_text` (PDF без текстового слоя или битый), 422 `empty_file`, 404 `chat_not_found` |
| 9 | `POST /api/child/stop-signal` | `{ sessionId?: string }` | 202 `{ accepted: true }`; уведомление админу и опекунам без текста переписки. Тот же путь вызывает разбор маркера тьютора на сервере (источник `tutor`), ручка — источник `child` | 404 `chat_not_found` (передан чужой или не детский `sessionId`), 429 `too_frequent` (не чаще раза в минуту на чат; повтор не теряется, а схлопывается) |
| 10 | `PUT /api/users/{id}/child-profile` | `ChildProfileDto` или `{ enabled: false }` для выключения; только `[Authorize(Roles="admin")]` + `AdminByStore`, без `[ChildAllowed]` | 200 `{ profile: ChildProfileDto, tutorPersonaId: string }` | 400 `not_container` (`ExecutionEnvironment != container`), 400 `provider_not_api_key` (`providerKey` не резолвится в запись пула с `ApiKey`), 400 `invalid_window`, 400 `invalid_space`, 400 `guardian_not_found`, 404 `user_not_found`, 403 — не админ |
| 11 | `GET /api/auth/me` | — | существующий ответ плюс `isChild: boolean` и `childSpaces: string[]` (ключи включённых пространств; у не ребёнка — пустой массив) | без изменений |
| 12 | `GET /api/chats/{id}/attachments/raw?path=` | `path` — относительный путь вложения ровно в той форме, что вернула загрузка (`.cc-attachments/…`); JWT для `<img>` — `?access_token=` | 200 — байты картинки, `Content-Type` по сигнатуре (`image/png`, `image/jpeg`, `image/webp`, `image/gif`), `X-Content-Type-Options: nosniff`, `Content-Security-Policy: sandbox`, `Cache-Control: private, max-age=86400`. Только чтение, нового хранилища нет. Общая ручка для всех пользователей, ребёнку открыта `[ChildAllowed]` | 404 `chat_not_found` (чат не виден вызывающему; у ребёнка — не свой или без `ChildSpace`), 404 `file_not_found` — одинаково для всех случаев: путь не равен записи `Session.Attachments` (точное сравнение после нормализации, не префикс), `..` или выход за корень (`SafePath.Join` от `GetChatRoot`), чужой автор `private/{author}/…` (`PrivateAttachments.IsForeign`), сигнатура не картинка, файла нет. 415 не отдаётся: ответ не раскрывает, что файл есть |

**Существующие ручки и методы в allow-list ребёнка** (получают `[ChildAllowed]`, состав замораживается сторожем Б2): `POST /api/auth/login`, `GET /api/auth/me`, `POST /api/tts` (расход — в бюджет), чтение своего детского чата `GET /api/chats/{id}`, `GET /api/chats/{id}/history` и `GET /api/chats/{id}/attachments/raw` (№12; только `ChildSpace`-чаты — проверка в ручке), хаб `/hubs/session` с методами из п.7 таблицы И1. Всё прочее — 403 `child_mode`.

## Последствия

- Новая ручка у взрослого не открывается ребёнку сама: чтобы открыть, нужна правка замороженного списка в сторожа — осознанное решение с ревью.
- Новое хранилище `data/child-usage/{owner}.json` (журнал присутствия) — сверка с бэкапом обязательна в Б5.
- Профиль меняет состав `tools/list` владельца — один перезапуск CLI на включение.
- Студия волны 2 — добавление пар `image-editor`/`local-media` в детский allow-list и квота, без нового гейта; кабинет родителя опирается на `GuardianUserIds`.

## Хвосты волны 1 (доработка документов в конце волны)

- **Миниатюра картинки в пузыре** (решение 6, шаги KM-B7 и KM-F13): описать ручку №12 в `docs/architecture/api.md`, а показ миниатюры в пузыре — в `docs/architecture/features.md` (раздел о вложениях чата); сторож гейта И1 — строка №12 в замороженном списке. Вложения, взятые из дерева проекта (не загрузкой), и старые чаты без `Session.Attachments` остаются ярлыком, бэкфилла нет.
