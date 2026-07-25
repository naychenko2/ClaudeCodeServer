# MCP-серверы продукта (mcp/*)

> Подробная документация. Выжимка и общий механизм подключения — в [CLAUDE.md](../CLAUDE.md),
> раздел «MCP-серверы». Читать перед правками в `mcp/*/index.js` и `BuildTurnMcpConfig`.

Все серверы — по одному файлу `mcp/{имя}-server/index.js`: чистый Node (stdio JSON-RPC,
**без зависимостей**, npm install не нужен). Подключение per-ход: `ClaudeSession.BuildTurnMcpConfig`
каждый ход собирает временный MCP-конфиг (серверы из `McpConfigPath` + продуктовые) и передаёт
env с адресом API и сервисным JWT владельца. Серверы других подсистем описаны в своих доках:
notes/memory/personas — [personas.md](personas.md) и [knowledge.md](knowledge.md), widgets —
[features.md](features.md).

## Сервер задач (mcp/tasks-server)

Один файл [mcp/tasks-server/index.js](../mcp/tasks-server/index.js). Инструменты: `tasks_list`,
`tasks_search`, `tasks_get`, `tasks_create`, `tasks_update`, `tasks_complete`, `tasks_delete`,
`tasks_add_subtask`, `tasks_toggle_subtask`, `tasks_run_executor`, `tasks_suggest_meta`,
`tasks_normalize_title`, `tasks_find_duplicate`.

Подключение автоматическое: `BuildTurnMcpConfig` передаёт env
`TASKS_API_URL` (адрес Kestrel или конфиг `McpTasksApiUrl`), `TASKS_API_TOKEN`
(сервисный JWT владельца сессии, `JwtService.IssueServiceToken`), `TASKS_PROJECT_ID`
(пусто = чат вне проекта → контекст личных задач). В системный промпт добавляется
подсказка об инструментах. Задачи per-owner: токен владельца ограничивает доступ его задачами.

## Инвариант: состав инструментов не зависит от хода

**Набор `tools/list` MCP-сервера обязан быть одинаковым на всех ходах сессии.** Ключи серверов
и отпечаток состава их инструментов (`shapes` в `BuildTurnMcpConfig`) входят в сигнатуру запуска
CLI. Стоит составу зависеть от свойства хода — глубины делегирования, текста, флага подавления —
сигнатура начинает «мерцать», и каждый переход между обычным и делегированным ходом **убивает
процесс claude вместе со всеми MCP-серверами**: незавершённые вызовы падают
`Tool permission request failed: Stream closed`, а инструменты то появляются, то исчезают
(`No such tool available`). Наступали трижды: `WORKSPACE_WRITE` по интенту хода,
`PERSONAS_WRITE`/`MENTIONS` по тексту, `TASKS_EXECUTE` и срезание секций `chats`/`destructive`
по `agentDepth`.

Ограничения по ходу живут на бэкенде: MCP-серверы шлют заголовок `X-Caller-Session-Id`
(id сессии, в которой работает модель), а `[DenyOnDelegatedTurn]` спрашивает у `SessionManager`
глубину ИДУЩЕГО хода этой сессии и отвечает 403 с внятным для модели текстом. Помечены:
`TasksController.Execute` (плюс защита от цикла «доклад → запуск → доклад»),
`SessionMessagesController.PostMessage`, `PersonasController.Ask`, `FilesController.Delete`,
`ChatsController.Delete`, `SessionsController.Delete`. Глубину для целевого чата
`chats_send` бэкенд тоже считает сам — из env она протухала при переиспользовании прогона.

Сторож инварианта — `McpToolsetStabilityTests`: следит, чтобы в теле `BuildTurnMcpConfig`
не появилось обращений к состоянию хода. Подсказки в системном промпте под запрет не попадают
(в сигнатуру не входят) — там как раз уместно предупредить модель, что действие на этом ходу
вернёт отказ.

**`alwaysLoad: true`** стоит у всех продуктовых серверов: при ленивом подключении первый вызов
в ходе падает «No such tool available» (claude-code#19282), а аккаунт-коннекторы claude.ai
переводят CLI в режим deferred-tools, где ленивый сервер вовсе прячет инструменты от модели.

## Таймауты, ретраи и классы ошибок

Общий `api()` каждого сервера (код одинаковый — серверы намеренно не делят модули, см. правило
«один файл без зависимостей»):

- **Таймаут** `AbortSignal.timeout(timeoutMs)`, по умолчанию 60с (notifications — 30с). Без него
  подвисший бэкенд держал вызов до дефолта undici (~300с). Инструментам, за которыми стоит модель
  или внешний сервис, передаётся `timeoutMs: LLM_TIMEOUT_MS` (180с): `tasks_suggest_meta`,
  `tasks_normalize_title`, `tasks_find_duplicate`, `notes_suggest_title`, `files_document_summary`,
  `files_document_extract`, `files_to_markdown`, `knowledge_index`. У `persona_ask` — 300с
  (это целый ход Claude), у `chats_send` таймаута нет вовсе (бэкенд сам отвечает 202).
- **Ретраи** `RETRY_DELAYS_MS = [300, 900]` на 408/425/429/5xx и сетевые сбои — но только для
  GET. Мутации повторяются лишь при ошибке соединения (`ECONNREFUSED`/`ENOTFOUND`/`EAI_AGAIN`),
  когда запрос заведомо не дошёл: слепой повтор POST задвоил бы задачу или запустил второго
  исполнителя. Раньше ретраев не было ни одного, поэтому секундная недоступность бэкенда
  (рестарт, деплой) давала серию красных карточек — в истории прода 18 таких за 4 сессии.
- **Класс ошибки** — `describeError()`: «Временный сбой… повтори», «Сейчас занято… повтори позже»,
  «Отказ… повторять бессмысленно». Без явного класса модель не отличала запрет от временного
  сбоя и бросала начатое после первой же карточки. Регресс — `McpServerRetryTests`.

## Пустое тело успешного ответа

ASP.NET на запись отвечает `Ok()` без объекта — 200 с пустым телом. `res.json()` кидает на нём
«Unexpected end of JSON input», и удавшаяся запись возвращается модели как ошибка (та повторяет
её второй раз). Разбор тела во всех серверах — общий `parseBody()`: 204 и пустое тело → `null`,
не-JSON при успешном статусе → сырой текст. Регресс — `McpServerEmptyBodyTests`.

## Принцип именования MCP-инструментов

Имена инструментов в одном MCP-сервере не должны быть однокоренными или отличаться
на 2-5 букв при пересекающейся семантике — LLM путает их. Разносите корни:
`run_executor` vs `complete`, а не `execute` vs `complete`; `get` vs `search` приемлемо,
если семантика очевидна и не пересекается. Критерий: если человек может спутать два
инструмента по названию в списке из 10+ — LLM тоже спутает.

## Грабли HTTPS-деплоя

Все MCP-прокси (tasks/notes/memory/wsp/personas) — node-процессы, ходящие в бэкенд обычным
`fetch` с той же машины. Если Kestrel слушает ТОЛЬКО https, `ResolveTasksApiUrl` подставит
`https://localhost:<порт>`, и node упрётся в `ERR_TLS_CERT_ALTNAME_INVALID` — боевой серт
выписан на внешний домен, `localhost`/`127.0.0.1` в SAN нет. Наружу это выглядит как
«fetch failed» у всех инструментов разом, при полностью живом бэкенде. Лечение: поднять
отдельный http-эндпоинт на `127.0.0.1` и прописать `McpTasksApiUrl` явно (так сделано в
`appsettings.Production80.json`). Автовыбор адреса предпочитает http https-у, но это лишь
подпорка — при единственном https-эндпоинте спасает только явный `McpTasksApiUrl`.
