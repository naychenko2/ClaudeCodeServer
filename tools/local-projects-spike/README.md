# Спайк ADR-016: CLI на «устройстве» через LLM-шлюз и сайдкар

Одноразовый код к [ADR-016](../../docs/adr/ADR-016-local-projects.md), в продукт не встраивается
и в `ClaudeHomeServer.slnx` не входит. Итоги — [отчёт](../../docs/research/local-projects-spike-2026-09.md).
Node 22, без зависимостей.

| Файл | Сторона | Роль |
|---|---|---|
| `gateway.mjs` | сервер | LLM-шлюз (`/llm/*` → подписка / DeepSeek / MiniMax) и прокси MCP (`/mcp/*` → дев-стенд с JWT); токены хода |
| `device-agent.mjs` | устройство | ретранслятор stdio по TCP loopback + сайдкар на `127.0.0.1` |
| `frames.mjs` | обе | кадры трубы `[канал][длина][данные]` |
| `relay-client.mjs` | сервер | «процесс» для `ClaudeSession`: его stdio = stdio удалённого CLI; `kill <TurnId>` |
| `driver.mjs` | сервер | сценарии критериев: `sub`, `stream`, `resume <sid>`, `deepseek`, `minimax`, `mcp`, `mcpBeacon`, `interrupt`, `kill` |
| `check-device.mjs` | сервер | обыск каталога устройства и env агента на секреты (критерий 5) |

## Запуск

Рабочий каталог — `/tmp/ccs-spike` (там же `ids.txt` с id проекта и сессии дев-стенда и файл
с JWT дев-стенда). Дев-стенд поднимается из этого worktree с отдельным `DataPath`:

```bash
cd backend/ClaudeHomeServer
env -i HOME=$HOME PATH=/usr/bin:/bin ASPNETCORE_ENVIRONMENT=Development \
  DataPath=/tmp/ccs-spike/devdata/projects.json Urls=http://127.0.0.1:5000 \
  dotnet bin/Debug/net10.0/ClaudeHomeServer.dll
```

Шлюз получает пути к файлам секретов через env (`GW_OAUTH_FILE`, `GW_PROVIDERS_FILE`,
`GW_BACKEND_JWT_FILE`) и читает их на старте — значения в argv и env не попадают. Агент
устройства стартует с `env -i` (только `PATH`, `HOME`), CLI — с env, собранным с нуля.
