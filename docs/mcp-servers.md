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
