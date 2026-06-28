# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Команды

```powershell
# Backend (из корня проекта)
cd backend; dotnet build
cd backend; dotnet run --project ClaudeHomeServer   # порт 5000

# Frontend (из корня проекта)
cd frontend; npm run dev       # порт 5173
cd frontend; npx tsc --noEmit # проверка типов
cd frontend; npm run build     # production-сборка

# Запускать вместе: backend на :5000, frontend на :5173
# Vite проксирует /api и /hubs (WebSocket) на :5000
```

## Архитектура

```
Browser (React 18 + TypeScript)
    │ SignalR WebSocket
    ▼
ASP.NET Core 9 (:5000)
 ├── Controllers/
 │    ├── AuthController      POST /api/auth/ping
 │    ├── ProjectsController  CRUD /api/projects
 │    ├── SessionsController  /api/projects/{id}/sessions
 │    └── FilesController     /api/projects/{id}/files/*
 ├── Hubs/SessionHub          SignalR /hubs/session
 ├── Services/
 │    ├── ProjectManager      in-memory + data/projects.json
 │    ├── SessionManager      реестр сессий + IHubContext broadcast
 │    ├── ClaudeSession       Process-обёртка claude.exe
 │    └── FileService         файловый менеджер (SafeJoin защита)
 └── Protocol/ServerMessage   record-типы WS-событий

Frontend (src/):
  pages/      LoginPage, ProjectListPage, WorkspacePage
  components/ ChatPanel, Composer, SessionList, FileExplorer,
              FileViewer, EmptyState, StatusBadge, NewSessionDialog
  hooks/      useSession (SignalR + ChatItem state)
  lib/        api.ts (REST), signalr.ts, design.ts (цветовые токены)
  types/      index.ts (Project, Session, ChatItem, ServerMessage, AuthState)
```

## Дизайн-макеты

Claude Design проект: `52adb1f7-312b-4f25-8c47-2bccfca9df94`

Ключевые файлы:
- `Claude Code Desktop.dc.html` — десктопные макеты (все состояния)
- `shots/desktop-files.png`, `shots/01-desktop-file.png`, `shots/02-desktop-file.png` — скриншоты

## Дизайн-система

Цвета из `frontend/src/lib/design.ts`:
- `accent: #D97757` — ОСНОВНОЙ цвет кнопок и активных состояний
- `bgMain: #F4F0E8` — фон страниц
- `bgPanel: #EDE7DA` — боковые панели
- `border: #E0D7C8` — границы

Шрифты: PT Serif (заголовки), Hanken Grotesk (UI), JetBrains Mono (код)
Стили: только inline-objects, без Tailwind/CSS-modules

## Claude Code CLI subprocess

`ClaudeSession` запускает: `claude --print --output-format stream-json --input-format stream-json --include-partial-messages --permission-prompt-tool stdio [--resume <id>]`

WorkingDirectory = `project.RootPath`

**stream-json → WebSocket маппинг:**
- `system { session_id }` → `session_started`
- `assistant text_delta` → `text_delta`
- `assistant thinking` → `thinking_delta`
- `assistant tool_use` → `tool_use`
- `user tool_result` → `tool_result`
- `sdk_control_request` → `permission_request` (ждём → пишем `control_response` в stdin)
- `result` → `result` + `exited`

## REST API

Все эндпоинты (кроме `/api/auth/ping`) и SignalR-хаб защищены `[Authorize]` —
доступ только по API-ключу. `ping` дополнительно под rate-limit (`Auth:PingRateLimit`,
по умолчанию 10/мин на IP). См. [docs/remote-access.md](docs/remote-access.md).

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
```

## SignalR Hub `/hubs/session`

Клиент вызывает: `JoinSession`, `LeaveSession`, `SendMessage`, `RespondPermission`, `Interrupt`
Сервер шлёт событие `message` с объектом `ServerMessage` (поле `type`).

## Реализовано

- Auth: реальная аутентификация по API-ключу — `[Authorize]` на всех API + хабе.
  Ключ из `Auth:ApiKey` (env/config) или автоген в `data/auth-key.txt` (печатается в консоль).
  Клиент: `Authorization: Bearer` (REST), `?access_token=` (WS); 401 → авто-логаут.
  Удалённый доступ (Tailscale + HTTPS): [docs/remote-access.md](docs/remote-access.md)
- Проекты: CRUD, редактирование, выход
- Сессии: создание с именем/режимом/моделью, редактирование названия и модели (шапка чата + список), статусы (starting/active/waiting/finished/error)
- Чат: Composer (вложения, режим ⚡/📋/❓, голосовой ввод, стоп, «Claude печатает…»)
- Сообщения: text, thinking, tool_use (spinner), permission_request, file_changed, result, error+retry
- Empty states: нет сессий, пустой чат с подсказками, пустая папка, нет результатов поиска
- Файловый менеджер: дерево, поиск, просмотр/редактирование, diff/revert, бинарные, изображения, loading
- Несохранённые изменения: диалог при закрытии файла

## Агенты (.claude/agents/)

| Агент | Роль |
|---|---|
| `project-manager` | PM, принимает решения вместо пользователя |
| `frontend-dev` | React/TypeScript (frontend/src/) |
| `backend-dev` | C#/.NET (backend/) |
| `architect` | кросс-слойный дизайн |
| `analyst` | анализ макетов Claude Design `52adb1f7-312b-4f25-8c47-2bccfca9df94` |
| `designer` | дизайн-система и стили |
| `dotnet-builder` | сборка и починка .NET |

## Конфигурация

Машинно-специфичные значения (локальные пути `DefaultProjectsPath`/`McpConfigPath`,
секреты, локальные URL) **не правим в отслеживаемых `appsettings*.json`** — там лежат
общие дефолты. Свои значения кладём в `backend/ClaudeHomeServer/appsettings.Local.json`
(в `.gitignore`, не коммитится, у каждого свой). Образец —
`appsettings.Local.example.json`: скопировать в `appsettings.Local.json` и вписать своё.

Порядок загрузки (последний переопределяет): `appsettings.json` →
`appsettings.{Environment}.json` → `appsettings.Local.json`. Подключается в
[Program.cs](backend/ClaudeHomeServer/Program.cs) сразу после `CreateBuilder`.

## Соглашения

- Хранилище проектов: `data/projects.json` рядом с executable
- Сессии только in-memory; resume через `--resume <claude-session-id>`
- Path traversal защита: `FileService.SafeJoin` — все пути через неё
- git diff/revert через `git` CLI; если не git-репо — возвращает null
- Комментарии в коде по-русски
