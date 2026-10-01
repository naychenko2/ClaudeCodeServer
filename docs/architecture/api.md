# REST API

> Справочник эндпоинтов. Актуальный источник правды — контроллеры в
> [backend/ClaudeHomeServer/Controllers/](../../backend/ClaudeHomeServer/Controllers/);
> при расхождении верить коду и чинить этот файл.

Все эндпоинты (кроме `/api/auth/login`) и SignalR-хаб защищены `[Authorize]` —
схема **JWT Bearer**. Вход дополнительно под rate-limit (политика `auth-login`,
ключ `Auth:LoginRateLimit`, по умолчанию 10/мин, партиция по адресу клиента).
См. [remote-access.md](../operations/remote-access.md).

```
POST /api/auth/login            { username, password } → { token, expiresAt, username, displayName } | 400 | 401 | 429
GET/POST/PUT/DELETE /api/projects
GET/POST/DELETE     /api/projects/{id}/sessions       POST body: { mode, name?, resumeSessionId?, model? }
PUT                 /api/projects/{id}/sessions/{sid} body: { name?, model? } → обновлённая сессия
GET                 /api/projects/{id}/sessions/{sid}/history  ?limit=&before=  → [msg…] | { messages, hasMore, cursor }
    без параметров — полный плоский массив (прежний контракт); с limit и/или before — страница:
    tail (последние limit, дефолт 100) + hasMore + cursor (индекс старейшего в пачке; null на конце).
    before — индекс, ДО которого (эксклюзивно) отдать сообщения; несуществующий → 400.
GET                 /api/projects/{id}/files          ?path=
GET                 /api/projects/{id}/files/search   ?q=
GET/PUT             /api/projects/{id}/files/content  ?path=  → { content, isBinary, isImage, base64?, ... }
GET                 /api/projects/{id}/files/diff     ?path=  → { diff }
POST                /api/projects/{id}/files/changed-by  { paths[] } → { files: { <path>: [{sessionId,name,external}] } }
    для присланных путей — какие ЕЩЁ чаты проекта их меняли (панель «Изменения»); ключи ответа — ровно строки paths;
    external=true — файл менялся чатом только вне заявленного хода (в бейдж не идёт, в фильтр «только файлы чата» идёт)
POST                /api/projects/{id}/files/revert   { path }
POST                /api/projects/{id}/files/create   { path }
POST                /api/projects/{id}/files/mkdir    { path }
POST                /api/projects/{id}/files/rename   { oldPath, newPath }
DELETE              /api/projects/{id}/files          ?path=
GET                 /api/projects/{id}/docs                   → индекс документации (область: файлы корня + папки, дефолт README.md + docs/**)
GET                 /api/projects/{id}/docs/doc       ?path=  → { content, links, backlinks } | 404 вне области
GET                 /api/projects/{id}/docs/search    ?q=     → совпадения с фрагментами
GET                 /api/projects/{id}/docs/scope             → { selected{folders,rootFiles,types}, folderCandidates[], rootFileCandidates[], typeGroups[], defaults{} }
PUT                 /api/projects/{id}/docs/scope     { folders, rootFiles, types } → та же форма (у каждой оси null — дефолт, [] — «ничего отсюда»)
POST                /api/projects/{id}/preset         { presetKey } → { created[], skipped[{path,reason}] } | 400 | 404 | 409  (каркас знакомства v2: ключ каталога или "none"=отказ; 400 — пустой/неизвестный ключ, 404 — чужой проект или флаг выключен, 409 — уже применён/отклонён/проект до фичи)
GET                 /api/home/summary                 ?recent=    → { active[], recent[] }  (дашборд «Домой»: сессии по всем проектам + чаты, с именами проектов)
GET                 /api/history/days                 ?sinceDays= → [{ date, commitCount, cached }]  (по всем проектам, без LLM)
GET                 /api/history/day/{date}                       → { date, items[] }  (продуктовая AI-сводка дня, кеш)
GET                 /api/history/new-count            ?since=iso  → { count } (новые коммиты во всех проектах после даты; для бейджа)
GET                 /api/feature-flags                → { definitions[], values{} }  (реестр + эффективные значения юзера)
PUT                 /api/feature-flags/{key}          { enabled } → { values{} }      (override per-user; ключ валидируется по каталогу)
GET                 /api/subsystem-modules            → { items[{ id, remoteUrl, exposedModule }] }  (MF-remote подсистем; remoteUrl с ?v={хеш remoteEntry.js на диске} — меняется с каждой сборкой модуля, сам remoteEntry.js отдаётся no-cache, /{имя}-remote/assets/** — immutable)
GET                 /api/modules                      → { items[] }  (внешние YARP-модули; remoteEntry с ?v= — хеш файла из нашей статики, иначе версия манифеста)
PUT                 /api/auth/timezone                { timeZone }  (IANA-зона устройства — для напоминаний)
GET                 /api/tasks                        ?from=&to=&q=&status=&priority=&assignee=&projectId=&personal=&personaId=  (все задачи владельца с фильтрами; personaId — поручения персоне)
POST                /api/tasks/{id}/execute           → Task  (запуск Claude-исполнителя; personaId у задачи → от лица персоны)
GET                 /api/push/vapid-public-key        → { publicKey }
POST                /api/push/subscribe|unsubscribe   { endpoint, p256dh?, auth? }  (web-push подписки устройств)
GET/POST/PUT/DELETE /api/personas                     (CRUD персон; ?scope=context&projectId= — доступные в контексте)  [флаг personas]
GET                 /api/personas/pantheon             → { templates[] } (каталог пантеона OmO + connectedPersonaId)
POST                /api/personas/pantheon/connect     { keys? } → Persona[]  (идемпотентно подключить команду глобально)
GET/POST            /api/personas/{id}/chats          POST body { mode?, resumeSessionId?, name?, projectId? } → Session (чат от лица персоны; projectId — контекст проекта: глобальная персона получает чат В нём)
GET/POST            /api/personas/{id}/memory         ?type=  / body { type, text, tags? } → записи памяти
GET                 /api/personas/{id}/memory/search  ?q=&topK=  → hits (relevance×recency×type)
DELETE              /api/personas/{id}/memory/{entryId}
GET                 /api/image-generation              → { providers[], places[] }
    настройка генератора картинок инстанса ПО МЕСТАМ; читает любой авторизованный.
    providers[] = { key, displayName, enabled, models[{id,displayName,description}] }
    places[] = { key (project-icon|persona-avatar), title, provider (режим auto|fal|glif),
                 activeProvider (кто пойдёт следующим запросом; null — генерация недоступна),
                 enabled, model (эффективная у активного), models{ключ провайдера: эффективная модель} }
PUT                 /api/image-generation              { places: {"<место>": { provider?, models? }} } → та же форма | 400 | 403 (не admin)
    патч-семантика: поле не прислали — оставить, "" — сброс к слою ниже; места вне places не трогаются.
    Валидация всех мест до применения (иначе запрос сохранился бы наполовину). 400 — пустой places,
    неизвестное место, неизвестный провайдер, ненастроенный провайдер при ЯВНОМ выборе
    (фолбэка у него нет), неизвестная модель провайдера
GET                 /api/projects/icon/caps            → { generate, provider, providerName, model }  (доступна ли генерация и чем нарисуют)
POST                /api/projects/{id}/icon/generate   { prompt?, count? } → { candidates: [файл…] } | 400 | 404 | 502
POST                /api/projects/icon/generate-preview { name?, prompt?, count? } → { candidates: [{ dataUrl }] } | 400 | 502
    кандидаты до создания проекта — инлайн data-url, на диск ничего не пишется (заявку ставить не на что)
GET                 /api/personas/avatar/caps          → { generate, provider, providerName, model }
POST                /api/personas/{id}/avatar/generate { prompt?, count? } → { candidates: [файл…] } | 400 | 404 | 502
    count 1..4 (дефолт 4); 400 — генерация не настроена (у {id}-ручек заодно ставится заявка догоняющей
    генерации), 502 — провайдер не вернул картинок. Провайдера и модель выбирает роутер по настройке
    /api/image-generation; аватар/иконка НЕ меняются до выбора кандидата (…/select)
GET                 /api/personas/{id}/avatar          → картинка (access_token в query для <img>)
POST                /api/personas/ask                  { handle, question, context? } → { handle, name, role, answer }
                                                       (one-shot ответ персоны от её лица; флаг persona-mentions; дёргается MCP personas-server)
POST                /api/chats/group                   { personaIds[], mode?, name? } → Session  (групповой чат, флаг persona-group-chats)
PUT                 /api/chats/{id}/participants       { personaIds[] } → Session  (состав группы; спикер сохраняется, иначе ведущая)
PUT                 /api/chats/{id}/loop               { enabled } → Session  (цикл «до готово», флаг work-loop; работает и для проектных сессий)
PUT                 /api/chats/{id}/read               → 204  (отметка прочтения: Session.lastReadAt, синк непрочитанности между устройствами; не двигает updatedAt; работает и для проектных сессий)
GET                 /api/watchdogs                     → { sessions[], projects[] }  (id чатов и проектов владельца с АКТИВНЫМИ сторожами; смена состава приходит событием watchdogs_changed)
PUT/DELETE          /api/admin/local-actions/{key}     { enabled } → { key, enabled, source }  (маршрут фонового действия локаль/claude; только admin; DELETE — сброс к конфигу/дефолту)
GET                 /api/admin/backup                  → { enabled, effectivePath, secretsPath, intervalHours, lastSuccessAt, lastError, recent[3] }  (только admin; настройки правятся в конфиге, не отсюда)
POST                /api/admin/backup/run              → { file, createdAt, summary }  (ручной снимок; только admin. Восстановление — не через API: exe --restore или меню трея)
GET/POST/DELETE     /api/knowledge                     (базы знаний Dify: список релевантных + CRUD; раздел «Знания»)
GET                 /api/knowledge/{id}                → база знаний + документы
POST                /api/knowledge                     { title, description?, visibility: personal|public } → { id, title, visibility }
DELETE              /api/knowledge/{id}                → 204 (только deletable — самостоятельные/публичные; 403 для привязанных)
POST                /api/knowledge/{id}/documents      { name, text } → документ (текст)
POST                /api/knowledge/{id}/documents/file (multipart file) → документ (файл)
DELETE              /api/knowledge/{id}/documents/{docId}  → 204
GET                 /api/knowledge/{id}/search?q=&topK=&method=semantic|fulltext → { items[] }
```

Эффективные значения флагов также возвращаются в `GET /api/auth/me` (поле `featureFlags`),
чтобы фронт получал их тем же запросом, что и при старте. Подробнее — раздел «Фич-флаги»
в [CLAUDE.md](../../CLAUDE.md).

## Редактор картинок (модуль `imageeditor`)

Ручки модуля [ClaudeHomeServer.ImageEditor](../../backend/ClaudeHomeServer.ImageEditor/CLAUDE.md),
решения — [ADR-019](../adr/ADR-019-image-editor-v3-in-chat.md). Гейт у всех один: флаг
`image-editor` выключен или проект чужой — `404`; модуль выключен конфигом — ручек нет (`404`).
Ошибки — `{ error, code }`.

```
GET                 /api/projects/{id}/image-editor/catalog              → поставщики, модели, caps
POST                /api/projects/{id}/image-editor/quote                → котировка (цена или время и очередь)
POST                /api/projects/{id}/image-editor/jobs                 multipart; sessionId? + threadId? [+ versionId?] — запуск в нить чата
                                                                         от версии (нет — текущая; чужая — 404 version_not_found) → 202 задача
GET/DELETE          /api/projects/{id}/image-editor/jobs/{jobId}         → задача / отмена
GET                 /api/projects/{id}/image-editor/jobs/{jobId}/variants/{n}  → картинка варианта
POST                /api/projects/{id}/image-editor/save                 { …, sessionId?, threadId? } → файл; занятое имя — 409 name_taken + suggestion
GET                 /api/projects/{id}/image-editor/save/check           → свободно ли имя
POST                /api/projects/{id}/image-editor/transform            правка без ИИ → шаг; нет растра — 503 raster_unavailable
GET                 /api/projects/{id}/image-editor/steps/{stepId}       → картинка шага
GET/POST/PUT/DELETE /api/projects/{id}/image-editor/characters[/{slug}]  персонажи проекта; фото — …/characters/{slug}/photos/{file}
GET/PUT             /api/projects/{id}/image-editor/prefs                { provider, model, count, matchSourceSize, characterSlug } —
                                                                         выбор в полосе «Картинки» проекта (нет — null, null, 2, true, null);
                                                                         удалённый персонаж читается как null; PUT → image_prefs_changed
```

**Нити чата** (`ThreadsController`, база `/api/projects/{id}/image-editor/sessions/{sid}/threads`).
Чужой, несуществующий и чат другого проекта неотличимы — `404 chat_not_found`; нить не этого чата —
`404 thread_not_found`. Каждая мутация несёт `revision`, от которой считал клиент: устарела —
`409 revision_conflict` с актуальным `state`. Ответ любой мутации — полное состояние, как у `GET`.
Смена фокуса `sessions.json` не пишет и `updatedAt` чата не двигает.

```
GET                 …/threads                          → { focus, revision, threads[] }
POST                …/threads                          { file? | draftFolder?, revision } — взять картинку в работу
                                                       (ровно одно; нить по файлу уже есть — фокус на неё)
PUT                 …/threads/focus                    { threadId | null, revision } — выбрать картинку или снять выбор
DELETE              …/threads/{threadId}?revision=     убрать нить без шагов, версий и идущих запусков; иначе — 400
PUT                 …/threads/{threadId}/current       { versionId, stepId?, revision } — «продолжить от версии»: она
                                                       текущая, нить в работе; ничего не удаляет; чужая — 404 version_not_found
POST                …/threads/{threadId}/steps         { stepId, revision } — правка без ИИ (шаг из …/transform) в текущую
                                                       версию; новой версии нет; чужой шаг — 404 step_not_found
PUT                 …/threads/{threadId}/settings      { settings, revision } — поставщик, модель, число вариантов
POST                …/threads/{threadId}/take          { jobId + variant | stepId, revision } — «Взять»   ┐ только нити
POST                …/threads/{threadId}/dismiss       { jobId, revision } — «Не брать»                   │ до 27.09 со
POST                …/threads/{threadId}/rollback      { stepId | null, revision } — откат стопки         ┘ стопками, иначе 400
```

**Версии (изменение 27.09 к ADR-019).** Каждый вариант каждого запуска ИИ — версия нити сам, «Взять»
нет. У нити: `versions[]` — `{ id, number, jobId, variant, baseVersionId, baseStepId, steps[],
currentStepId, createdAt }` по порядку, первая всегда исходник (`id: "origin"`, `number: 0`, `jobId:
null`); `currentVersionId` — версия «в работе»; `launches[]` — `{ jobId, baseVersionId, baseStepId, at,
status: running | done | failed | cancelled | interrupted, initiator: human | agent, prompt }`.
Картинка версии — шаг `currentStepId` (`GET …/steps/{id}`); у исходника без правок — файл нити, а у
старой нити — её текущий шаг стопки (`currentStepId` нити). Старые поля (`stacks`, `currentStackId`,
`currentStepId`, `pendingJobId`, `interruptedJobId`) остаются у нитей до 27.09.

Запуск в нить кладёт внизу ленты якорь `module_record { module: "imageeditor", recordType:
"image_launch_versions", data: { threadId, jobId, prompt, provider, model, count, estimate,
initiator, baseVersionId } }` — один на запуск; его версии — `versions[]` с этим `jobId`, они
появляются по завершении задачи вместе с `image_thread_changed` (раньше `image_edit_completed`).
Якорь нити `image_thread` несёт `{ threadId, versionId: "origin" }` (у старых — `{ threadId, stackId }`).

`save` — только человек: у агента такого MCP-инструмента нет (ADR-019, решение 1). Ручек чата
картинки v2 (`image-editor/chats*`) больше нет.

## Редактор звука (модуль `audioeditor`)

Ручки модуля [ClaudeHomeServer.AudioEditor](../../backend/ClaudeHomeServer.AudioEditor/CLAUDE.md),
решения — [ADR-021](../adr/ADR-021-audio-editor-and-generation-panel.md). Гейт один на все
(`AudioEditScopeGate`): флаг `audio-editor` выключен, проект или чат чужой — `404`; модуль выключен
конфигом — ручек нет (`404`). Ошибки — `{ error, code }`; коды: `invalid_request` — 400,
`provider_unavailable`, `name_taken`, `revision_conflict`, `voice_clone_stale`, `voice_clone_missing` —
409 (у протухшего клона ещё `recreate` — котировка пересоздания), `quote_not_found`, `thread_not_found`,
`version_not_found`, `file_not_found`, `voice_not_found` — 404, `too_many_jobs`, `heavy_busy` — 429,
`unavailable`, `dsp_unavailable` — 503.

Области две, тела ручек общие (`AudioEditorEndpoints`): проект — база
`/api/projects/{id}/audio-editor`, чат проекта — `…/sessions/{sid}`; личный чат вне проекта — база
`/api/audio-editor/chats/{sid}` с теми же хвостами. У личной области нет сохранения в проект, «Голосов»,
local и путей файлов проекта — отказ до диска.

```
GET                 …/catalog                          → поставщики, модели, caps, причины серых
GET                 …/prefs                            → { voice, music, process } — выбор в полосе по режимам
PUT                 …/prefs/{mode}                     { operation, provider, model, count, fields, inputs } → audio_prefs_changed
POST                …/quote                            { mode, operation?, provider?, model?, count?, voiceKind?, sessionId?, threadId?,
                                                       text?, prompt?, lyrics?, durationSec?, fields? } → котировка на 10 минут
POST                …/jobs                             multipart: quoteId, sessionId? + threadId? [+ baseVersionId?], text, prompt,
                                                       lyrics, language, durationSec, startSec, endSec, seed, params, reference |
                                                       referencePath, clips | clipPaths, voiceModelPath, voiceIndexPath, voice
                                                       (voice:<slug>) → 202 задача; тело до 500 МиБ
GET/DELETE          …/jobs/{jobId}                     → задача / отмена
GET                 /api/audio-editor/schema?provider=&model=&op=   → схема «Дополнительно» модели
```

Запуск — только по котировке и ровно на её паре «поставщик + модель». Текст, подводка, слова,
длительность и итог `params` запуска обязаны совпасть с котированными: иначе `400 invalid_request`
«Котировка не соответствует запросу — запросите цену заново», котировка при этом не сгорает.

**Нити чата** (база чата: `…/sessions/{sid}` у проекта, `/api/audio-editor/chats/{sid}` у личного).
Мутации несут `revision`: устарела — `409 revision_conflict`; ответ мутации — полное состояние.

```
GET                 …/state                            → { threads, catalog, prefs } одним запросом
GET                 …/threads                          → { focus, revision, threads[] }
POST                …/threads                          { file? | draftFolder?, mode?, revision } — взять звук в работу
PUT                 …/threads/focus                    { threadId | null, revision }
DELETE              …/threads/{threadId}?revision=     убрать нить без версий и идущих запусков
PUT                 …/threads/{threadId}/settings      { settings, revision } — режим, операция, поставщик, модель, поля, входы
PUT                 …/threads/{threadId}/current       { versionId, revision } — версия «в работе»
GET                 …/threads/{threadId}/versions/{versionId}/files/{role}[?download=true]   файл версии по роли (Range)
GET                 …/threads/{threadId}/versions/{versionId}/peaks                          пики волны
POST                …/threads/{threadId}/edit          правка без ИИ (trim | gainFade | normalize | convert) → новая версия
POST                …/threads/{threadId}/mix           { stems[], baseVersionId?, format?, revision? } — свести стемы → новая версия
POST                …/concat                           { pieces[], joint?, joints?, normalizeLoudness?, name?, format? } → новая нить
POST                …/threads/{threadId}/save          { versionId, mode?, folder?, fileName? } — только проект; занятое имя — 409 name_taken
```

**«Голоса»** — только серверный проект, база `/api/projects/{id}/audio-editor/voices`:

```
GET/POST            …/voices                           → список / создать из образцов (multipart, до 5 по 50 МБ)
POST                …/voices/rvc                       создать из пары RVC проекта
GET/PATCH/DELETE    …/voices/{slug}
POST                …/voices/{slug}/samples            добавить образцы; DELETE …/samples/{file} — убрать
POST                …/voices/{slug}/recreate?provider=minimax[&quoteId=]   две фазы: без quoteId — котировка, с ним — 202 задача
GET                 …/voices/{slug}/files/{file}       образец или файл модели из манифеста
```

События в группу владельца: `audio_edit_progress`, `audio_edit_completed`, `audio_edit_failed` (у отказа —
`retryQuote` соседа), `audio_thread_changed`, `audio_prefs_changed`. Агенту — MCP-сервер `audio-editor`:
`audio_state`, `audio_focus`, `audio_new`, `audio_voices`, `audio_suggest_prompt`, а при
`AudioEditor:AgentLaunch` ещё `audio_generate`, `audio_concat`, `audio_cancel`; сохранения у агента нет.

## SignalR-хаб `/hubs/session`

Вторая половина контракта с фронтом: REST отдаёт состояние, хаб — живой ход. Источник правды —
[Hubs/SessionHub.cs](../../backend/ClaudeHomeServer/Hubs/SessionHub.cs); хаб под тем же
`[Authorize]`, что и REST.

**Клиент вызывает** (основные методы; полный список — в коде):

```
JoinSession(sessionId)                      → подписка на чат; в ответ Caller получает догоняющие
                                              события (статус хода, незавершённое сообщение, recall)
LeaveSession(sessionId)                     → отписка
SendMessage(sessionId, text,                → отправить ход; возвращает id созданного сообщения
            attachedPaths?, mode?, auto?)
RespondPermission(sessionId, requestId,     → ответ на permission_request (ждёт его CLI, см. ниже)
                  behavior)
Interrupt(sessionId)                        → прервать идущий ход
```

Рядом живут подписки на другие каналы того же хаба (`JoinProject`/`LeaveProject`,
`JoinUser`/`LeaveUser`, `JoinPreviewLog`/`LeavePreviewLog`) и ходовые ответы штаба
(`RespondTeamPlan`, `RespondTeamEscalation`), плюс `CompactSession`.

**Сервер шлёт** единственное событие — `message` с объектом
[`ServerMessage`](../../backend/ClaudeHomeServer.Core/Protocol/ServerMessage.cs), где вид
события различается полем `type` (`text_delta`, `thinking_delta`, `tool_use`, `tool_result`,
`permission_request`, `result`, `exited` и т.д.). Один канал с дискриминатором, а не метод на
каждое событие: фронт разбирает поток в одном месте, а вертикаль может завести свой record и
отправить его через `IHubContext<SessionHub>`, ни от кого не завися (см.
[ADR-014](../adr/ADR-014-internal-subsystems.md), раздел про `ClaudeHomeServer.Protocol`).

Записи модулей в ленте чата идут типом `module_record` (`{ module, recordType, data, fallback }`,
та же форма лежит в `history.json`); незнакомый `recordType` или выключенный модуль — лента рисует
`fallback`. Модуль пишет их вне хода через шов `IChatFeed`; модель их не видит. Редактор картинок
шлёт владельцу `image_edit_progress` / `image_edit_completed` / `image_edit_failed` и
`image_thread_changed` (нити чата сменились; потерянное событие догоняется `GET …/threads`) и
`image_prefs_changed { projectId, prefs }` (выбор в полосе «Картинки» проекта; догоняется `GET …/prefs`).
Метода `SendImageChatMessage` и события `image_chat_state` (чат картинки v2) больше нет.
