# Среда исполнения пользователей (local / container)

> Подробная документация подсистемы. Выжимка и инварианты — в [CLAUDE.md](../CLAUDE.md),
> раздел «Среда исполнения». Читать перед правками в `Services/Execution/`, `SandboxManager`,
> `UserHomeResolver` и всём, что касается запуска процессов пользователей.

Изоляция per-**пользователь**, а не per-приложение: у `User.ExecutionEnvironment`
(`local` | `container`, задаётся админом при создании) два режима. **local** — процессы
пользователя (claude, терминал, dev-серверы, npx skills) запускаются на машине сервера
с полным доступом. **container** — всё исполняется в общей docker-песочнице `cc-sandbox`.
Модель предполагает бэкенд НА ХОСТЕ (Windows), а не в контейнере.

- **Слой запуска** — [Services/Execution/](../backend/ClaudeHomeServer/Services/Execution/):
  `IProcessLauncher` (`ProcessSpec` → `Process`) с драйверами `LocalProcessRunner`
  (Process.Start, как раньше) и `DockerProcessRunner` (`docker exec -i cc-sandbox
  /app/run-turn.sh <turnId> …`, stdio stream-json насквозь). Резолв по владельцу —
  `ILauncherFactory.ForOwner(ownerId)`. Все 6 точек запуска (ClaudeSession,
  OneShotClaudeRunner, ModelCatalogService, TerminalService, DevServerService,
  SkillsCliService) идут через него; системные one-shot (changelog, каталог моделей) —
  всегда local.
- **Пути** — `IPathMapper`: бэкенд ВСЕГДА работает с хостовыми путями (projects.json
  хранит `C:\…`), а процессы container-юзера — с контейнерными; перевод в момент
  запуска (`DockerPathMapper`, аналог SafeJoin — путь вне монтирований → ошибка).
  Точки монтирования: `Sandbox:ProjectsRoot`→`/projects`, `data/sandbox-profiles`→
  `/sandbox-profiles` (per-user CLAUDE_CONFIG_DIR + транскрипты resume, видны бэкенду
  через `WorkflowAgentParser.AddAllowedRoot`), `data/sandbox-tmp`→`/turn-tmp`
  (MCP-конфиги хода, one-shot cwd).
- **Interrupt** — `run-turn.sh` пишет pgid хода в `/tmp/turns/{turnId}.pid`;
  `DockerProcessRunner.Kill` добивает группу изнутри (`kill -KILL -- -pgid`), т.к.
  убийство docker-клиента на хосте не трогает процесс в контейнере.
- **MCP из песочницы** — `*_API_URL` = `Sandbox:McpApiUrl` (`host.docker.internal:5000`,
  Kestrel хоста) через `ResolveTasksApiUrl(ownerId)`; node-серверы `mcp/*/index.js`
  лежат в образе под `/app/mcp` (переписываются в `BuildTurnMcpConfig`).
- **Корни проектов разведены**: local-юзеры — `DefaultProjectsPath`, container-юзеры —
  `Sandbox:ProjectsRoot` (в песочницу монтируется только он). Единая точка резолва —
  [UserHomeResolver.cs](../backend/ClaudeHomeServer/Services/UserHomeResolver.cs): домашняя
  папка юзера = `{база по среде}/{логин}`, внутри неё живут проекты без явного пути, `Chats`
  и корни файловых триггеров. Все четыре потребителя (`ProjectManager.Create`,
  `SessionManager.ResolveChatRoot`, `PersonaAgentFileSync.ChatRoot`, `AutomationRootResolver`)
  ходят через него.
  **Override**: `Projects:UserHomeOverrides` (словарь логин → абсолютный путь, в
  appsettings.Local.json) снимает прослойку `{логин}` — на однопользовательском инстансе
  можно работать прямо в общей папке (`"admin": "C:\\GIT"`). Путь обязан быть абсолютным, а у
  container-юзеров — лежать СТРОГО внутри `Sandbox:ProjectsRoot` (сам корень общий для всех
  изолированных, домом одного быть не может); негодный override игнорируется с warning. Уже
  созданные проекты не затрагиваются (`RootPath` абсолютный), а у чатов вне проекта меняется
  cwd — старые такие чаты остаются в прежней папке и могут потерять `--resume`.
  Существующую папку в проект подключают без всего этого: `POST /api/projects` с явным
  `rootPath` (на фронте — «Добавить проект» → «Существующий»).
- **Guard**: смена `ExecutionEnvironment` при существующих чатах запрещена (разные корни
  и профили; `SessionManager.HasSessionsOwnedBy`). **SandboxManager** держит один общий
  контейнер (docker CLI, `sleep infinity`, ленивый `EnsureRunningAsync`, пересоздание при
  смене образа/параметров по label-хешу). Конфиг — секция `Sandbox` (машинно-специфичный
  `ProjectsRoot` — в appsettings.Local.json). Образ песочницы:
  `docker build --target sandbox -t claude-sandbox -f backend/ClaudeHomeServer/Dockerfile .`
- **Переход на per-user контейнеры** позже без переделки: имя контейнера параметризовано,
  меняется только `SandboxManager`/фабрика драйвера.
