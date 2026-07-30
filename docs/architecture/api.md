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
GET                 /api/projects/{id}/files          ?path=
GET                 /api/projects/{id}/files/search   ?q=
GET/PUT             /api/projects/{id}/files/content  ?path=  → { content, isBinary, isImage, base64?, ... }
GET                 /api/projects/{id}/files/diff     ?path=  → { diff }
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
POST                /api/personas/{id}/avatar/generate { prompt? } → Persona  (AI-аватар через fal)
GET                 /api/personas/{id}/avatar          → картинка (access_token в query для <img>)
POST                /api/personas/ask                  { handle, question, context? } → { handle, name, role, answer }
                                                       (one-shot ответ персоны от её лица; флаг persona-mentions; дёргается MCP personas-server)
POST                /api/chats/group                   { personaIds[], mode?, name? } → Session  (групповой чат, флаг persona-group-chats)
PUT                 /api/chats/{id}/participants       { personaIds[] } → Session  (состав группы; спикер сохраняется, иначе ведущая)
PUT                 /api/chats/{id}/loop               { enabled } → Session  (цикл «до готово», флаг work-loop; работает и для проектных сессий)
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
