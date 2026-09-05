# ClaudeHomeServer — краткая карта для локальной модели

BareMode-резолв берёт `<корень проекта>/docs/CLAUDE-local.md` с приоритетом
над серверным дефолтом — это специфика нашего репозитория, в чужие проекты
она не уезжает. Держится под 8 КБ. Полная карта — корневой `CLAUDE.md` и
`docs/architecture/`, `docs/features/`, `docs/adr/`.

## Команды

```powershell
cd backend; dotnet build
cd backend; dotnet test --filter "Category!=Dns"
cd frontend; npm run build
```

CI гоняет тесты на Linux (`ubuntu-latest`), разработка на Windows — тесты
обязаны быть платформонезависимыми: пути через `Path.GetTempPath()` +
`Path.Combine`, тайминги через `TaskCompletionSource` + `Task.WhenAny`,
а не `Task.Delay`.

## Архитектура

Браузер (React 18 + TypeScript) → SignalR WebSocket → ASP.NET Core 10
(:5000). Слои бэкенда: `Controllers/`, `Hubs/SessionHub`, `Services/`
(в т.ч. `Llm/`), `Protocol/ServerMessage`. Фронт: `pages/`,
`components/`, `hooks/`, `lib/` (`api.ts`, `signalr.ts`, `design.ts`),
`types/`. Хранилище: `data/projects.json`, `data/sessions.json`,
`data/sessions/{claudeSessionId}/history.json`; resume через `--resume`.

Подсистемы (`IAppSubsystem`, `Core`) изолируют вертикали; границы держит
`SubsystemBoundaryTests` (рефлексия по сборке, default-deny + allow-list
в `Boundaries`) и `SubsystemBoundaryCoverageTests` (каждая реализация
должна быть в таблице). Вертикаль зависит только от спины
(`Microsoft.*`, `Models`, `Services.Http`/`Composition`/`Mcp`) и явных
швов — не от другой вертикали напрямую.

## Инварианты

- `FileService.SafeJoin` — все пути через неё, иначе pathtraversal.
- Per-owner: токен подписки доставляется в песочницу per-exec, а не
  запекается при создании контейнера; OAuth-логин CLI живёт в профилях
  `data/claude-profiles/{key}`; env хода вычищается от `ANTHROPIC_*`,
  `CLAUDE_CONFIG_DIR` (`ProviderEnvKeys` → `ClearEnv`).
- Состав `tools/list` НЕ зависит от хода (входит в сигнатуру запуска
  CLI), МОЖЕТ зависеть от свойств сессии. Сторож: `McpToolsetStabilityTests`.
- Удаление чата уносит транскрипт строго по имени `{csid}.jsonl`, не
  по маске, не саму папку (один `~/.claude` делят все инстансы).
- `UpdatedAt` не двигают ни настройки чата, ни архивация (признак
  архива производный: `IsArchived = ArchivedAt != null && UpdatedAt <= ArchivedAt`).
- На малых окнах `CLAUDE_CODE_MAX_OUTPUT_TOKENS` режется — иначе CLI
  резервирует 32 000, vLLM отвергает запрос.
- Одна папка — один проект на владельца (`ProjectManager.EnsureRootFree`,
  400 при повторе); датасет Dify ключуется по `RootPath`. У разных
  владельцев общая папка допустима.
- Новое хранилище → сверься с бэкапом: всё в `data/` попадает в архив
  по умолчанию; критичный стор — в `BackupValidation.Validate`;
  ломающее изменение формата = инкремент `BackupSchema.Version`.
- Дизайн-система — токены `C.*` из `lib/design.ts`; сырой hex в `.tsx` —
  дефект; гейт перед коммитом `cd frontend; npm run lint:design`.
- HTTP-клиент к опциональной зависимости — `AddQuietHttpClient` из
  `ClaudeHomeServer.Core`, иначе дефолтный логгер печатает провалы как
  Error со стектрейсом и забивает консоль.
- BareMode включается через `LlmProviderConfig.BareMode=true` +
  `SystemPromptFile`; карта ищется по цепочке
  `<projectRoot>/docs/CLAUDE-local.md` → серверный `SystemPrompts/CLAUDE-local.md`.
  Проводка `ContentRootPath = AppContext.BaseDirectory` в `SessionManager`
  обязательна (`BareModeContentRootPathWiringTests` как сторож).

## Слой Services/Llm

- `ClaudeSession` — единственный рантайм CLI
  (`Services/Llm/Claude/`); сторонние провайдеры (DeepSeek, GLM) —
  env-оверрайды на ход. Команда:
  `claude --print --output-format stream-json --input-format stream-json
   --include-partial-messages --permission-prompt-tool stdio [--resume <id>]`
- Слоты strong/medium/weak + таблица назначений (`LocalActionCatalog`);
  резолв в одной точке `UserModelTierResolver.ModelFor`.
- Фолбэк — цепочка, без автоподбора. Классы ошибок: `TurnErrorClassifier`
  (RateLimit / UsageLimit / ProviderError / Unreachable / ContextOverflow /
  AuthFailure / None). `EgressProbe` разводит мёртвый эндпоинт (шаг
  цепочки) и мёртвый общий канал (повтор через 5 с).
- Паспорта ходов: `TurnRunLog` пишет `FallbackLlmSessionAdapter` в
  `finally` цикла; источник один.

## Соглашения

- Комментарии в коде по-русски.
- Conventional Commits: `тип(область): описание` на русском, в
  повелительном наклонении, с маленькой буквы, без точки. Атомарный
  коммит = одно логическое изменение.
- Коммит — только по явной просьбе; пуш и PR — тоже.
- Трейлер `Co-Authored-By: <модель> <noreply@…>` обязателен; домен
  по вендору (anthropic → `@anthropic.com`, z.ai → `@z.ai`).

## Правило

Нет проверки = не готово. Перед завершением — фактическая сборка/тесты,
цифры в итоге реальные. Делегировал — проверь сам.
