# REST API

> Справочник эндпоинтов. Актуальный источник правды — контроллеры в
> [backend/ClaudeHomeServer/Controllers/](../../backend/ClaudeHomeServer/Controllers/);
> при расхождении верить коду и чинить этот файл.

Все эндпоинты (кроме `/api/auth/ping`) и SignalR-хаб защищены `[Authorize]` —
доступ только по API-ключу. `ping` дополнительно под rate-limit (`Auth:PingRateLimit`,
по умолчанию 10/мин на IP). См. [remote-access.md](../operations/remote-access.md).

```
POST /api/auth/ping             { serverUrl, apiKey } → { ok } | 401 | 429  (ключ + rate-limit)
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
GET                 /api/watchdogs                     → { sessions[], projects[] }  (id чатов и проектов владельца с АКТИВНЫМИ сторожами, флаг chat-watchdogs: выключен — пустые списки; смена состава приходит событием watchdogs_changed)
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
