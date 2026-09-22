# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **Держи этот файл компактным.** CLAUDE.md загружается в контекст КАЖДОЙ сессии — это
> карта проекта, а не энциклопедия. Детальные описания подсистем живут в `docs/`;
> здесь — команды, архитектура, инварианты и короткие выжимки со ссылками. Новое крупное
> описание — сразу отдельным файлом в нужном разделе `docs/` плюс выжимка со ссылкой
> отсюда (формат — как у существующих разделов). Не раздувать этот файл обратно.
>
> Раздел выбирается по роли документа: `architecture/` — как устроено, `operations/` —
> как запускать, `adr/` — почему решили так, `research/` — исследования и срезы во
> времени, `design/` — конвенция и прототипы. Карта корпуса — [docs/README.md](docs/README.md).

## Команды

> **Стандарт: сборка и прогон — в dev-контейнере**, а не на хосте (песочница для Claude плюс
> воспроизводимое окружение). Первый запуск и устройство контейнера —
> [docs/operations/docker.md](docs/operations/docker.md).

```powershell
# Контейнер (из корня проекта) — основной путь
docker compose -f docker-compose.claude.yml up -d --build              # сборка + запуск, :5000
docker compose -f docker-compose.claude.yml up -d --build claude-server # пересборка после правок
docker logs -f claude-server
```

```powershell
# Хостовый запуск (справочно, для быстрых локальных итераций)
cd backend; dotnet build
cd backend; dotnet run --project ClaudeHomeServer   # порт 5000
cd frontend; npm run dev       # порт 5173
cd frontend; npm run build     # production-сборка (tsc -b + vite)
```

Хостовый дев-стенд поднимаем **только через `dotnet run`** (или с явным
`ASPNETCORE_ENVIRONMENT=Development`): порождённые процессы наследуют `Production`, а там
`Kestrel:Endpoints` уводит стенд на занятый боевым инстансом :80, и `ASPNETCORE_URLS` это не
чинит. Стенд :5000 раздаёт `frontend/dist` ПОСЛЕДНЕЙ сборки (`wwwroot` в репозитории не
живёт). Разбор и фоновый запуск —
[docs/operations/dev-stand-host.md](docs/operations/dev-stand-host.md).

## Среда исполнения пользователей (local / container)

Изоляция per-**пользователь**: `User.ExecutionEnvironment` = `local` | `container` (общая
docker-песочница `cc-sandbox`); бэкенд всегда НА ХОСТЕ. Все запуски идут через
`ILauncherFactory.ForOwner(ownerId)`, системные one-shot — всегда local; домашние папки —
[UserHomeResolver.cs](backend/ClaudeHomeServer/Services/UserHomeResolver.cs). Инварианты:

- Бэкенд работает ТОЛЬКО с хостовыми путями, `IPathMapper` переводит их в контейнерные в
  момент запуска; смена `ExecutionEnvironment` при существующих чатах запрещена; токен
  подписки доставляется в песочницу per-exec, а не запекается при создании контейнера.
- Local-процессы изолированы по памяти systemd-scope'ом в `ccs-agents.slice`
  (`Execution:Isolation`): реюз узлов MSBuild внутри scope, сторож сирот при старте, прогрев
  свежего worktree — всё в `sandbox.md`.
- **Основная защита от OOM — жёсткий потолок cgroup на `ccs-agents.slice`**, а не гейт: лимит
  наследуется ВСЕМУ поддереву scope, включая сборки, которые агент запускает внутри хода своим
  Bash (ровно они дали инцидент 21.09). `BuildConcurrencyGate` считает только spec с меткой
  `ProcessSpec.Heavy` — дополнение, не замена лимитов; счётчик **единственный на процесс**,
  второй семафор где-то ещё делает потолок неправдой.
- **`MemoryHigh` не ставим ни на slice, ни per-scope** (разбор 2026-09-22): под systemd-oomd
  дроссель сам становится источником PSI-давления и убивает наш же scope. Почему именно так —
  [sandbox.md](docs/architecture/sandbox.md) и [deploy/systemd/README.md](deploy/systemd/README.md),
  там же правило расчёта машинно-специфичных значений; unit-файлы и drop-in'ы — в
  [deploy/systemd/](deploy/systemd/).

**Наблюдение за деревом файлов не подписывается на служебные каталоги.** `FileSystemWatcher`
с `IncludeSubdirectories` на Linux ставит inotify-слежку на КАЖДЫЙ каталог, а чёрные списки
проекта (`FileWatcherOptions.IgnoreDirs`, `TreeExcludes`) режут только события: на этом
репозитории выходило 16 390 слежек на наблюдателя при 438 неслужебных каталогах. Поэтому
наблюдение идёт через Core-примитив
[RecursiveDirectoryWatcher](backend/ClaudeHomeServer.Core/Services/Files/RecursiveDirectoryWatcher.cs)
— ОДИН экземпляр inotify на наблюдателя (разбиение дерева на поддеревья с отдельным
`FileSystemWatcher` на каждое дало бы 178 экземпляров при дефолтном потолке ядра 128),
подписка по перечню каталогов с обрезкой служебных и потолком `MaxWatches` (4000) как
гарантией на чужом дереве. Ватчер хода переведён (439 слежек вместо 16 390),
`FileWatcherService` — ещё нет. sysctl остаётся запасом, а не лекарством.

**Перед правками в `Services/Execution/`, `SandboxManager`, `UserHomeResolver` — прочитай
[docs/architecture/sandbox.md](docs/architecture/sandbox.md)** (монтирования, interrupt, MCP из песочницы, overrides).

## Архитектура

```
Browser (React 18 + TypeScript)  →  SignalR WebSocket  →  ASP.NET Core 10 (:5000)
```

Слои бэкенда — `Controllers/`, `Hubs/SessionHub`, `Services/`,
`ClaudeHomeServer.Llm` (отдельная сборка — слой LLM-провайдеров),
`Protocol/ServerMessage` (record-типы WS-событий); фронт — `pages/`, `components/`, `hooks/`,
`lib/` (`api.ts`, `signalr.ts`, `design.ts`), `types/`. Состав файлов смотри в дереве репозитория.

## Дизайн-система

**Полная конвенция — [docs/design/guidelines.md](docs/design/guidelines.md), обязательна
для ЛЮБЫХ изменений UI**; живой эталон — витрина UI-кита (`#/ui-kit` в dev). Макеты Claude
Design и их состав — [docs/design/audit.md](docs/design/audit.md). Железные правила:

- Цвета — только токены `C.*` из [design.ts](frontend/src/lib/design.ts); сырой hex в `.tsx` —
  дефект; значения тем — в [theme.css](frontend/src/lib/theme.css) (новый цвет — в ОБЕ темы).
  Размеры — из шкал `FS`, `SP`, `R`, `SHADOW`, `Z`, `MODAL_W`.
- Контролы — только из `frontend/src/components/ui/`; самодельные кнопки/модалки из div — дефект.
- **Проверяется линтом:** `cd frontend; npm run lint:design` обязан быть зелёным. Гейт стоит
  **перед коммитом**, а не после каждой правки; по ходу работы хватает `npx tsc -b`.
- Каждый экран живёт на мобиле (`useIsMobile`); ширины считать в CSS-пикселях, а не в
  паспортных — [docs/design/target-devices.md](docs/design/target-devices.md).
- Для заметного UI перед коммитом **предложить** прогон через субагента `designer` и
  дождаться ответа.

## LLM-провайдеры (ClaudeHomeServer.Llm)

Основной рантайм — claude CLI (`Llm/Claude/ClaudeSession`); сторонние провайдеры
(DeepSeek, GLM) подключаются env-оверрайдами процесса на каждый ход. Мимо CLI ходит только
локальный движок (`ILocalLlmClient` — Ollama или llama-server): фоновые one-shot действия и
разговор с исполнителем «Локальная» идут прямым HTTP-вызовом. Конфиг — секция
`LlmProviders`, ключи в appsettings.Local.json (пустой `ApiKey` = провайдер выключен);
резолв, цены и возможности — `LlmProviderRegistry`. Фоновые one-shot действия (теги, сводки,
память, changelog…) считаются дёшево по маршруту `LocalActionRouter` + `CheapTextRunner`;
исполнителя каждого места выбирает админ в диалоге «Поставщики моделей». Инварианты:

- Профили CLI `data/claude-profiles/{key}` изолируют OAuth-логин `~/.claude` — иначе CLI
  пошлёт чужому эндпоинту токен подписки (401); креденшалы в профили не копируются никогда.
- На КАЖДОМ запуске из унаследованного env вычищаются `ANTHROPIC_*`, `CLAUDE_CONFIG_DIR` и
  прочее: маршрут CLI задаёт только сервер, `CLAUDE_CODE_OAUTH_TOKEN` не трогается.
- `BareMode` гасит автозагрузку CLAUDE.md проекта **недокументированной** переменной
  `CLAUDE_CODE_DISABLE_CLAUDE_MDS=1`, а не флагом CLI: обновление CLI может убрать её молча,
  поэтому в `ClaudeSession` стоит сторож на аномальный размер входа первого хода. Хуки, LSP,
  плагины и авто-память при этом живые.
- У container-владельцев пара «bind-mount `SystemPrompts` ↔ правило `DockerPathMapper`»
  обязана быть синхронной: расхождение рантайм не ловит, оно даёт exit=1 и ложный
  `Unreachable` у КАЖДОГО container-владельца при зелёных тестах.
- Нестабильные секции промпта (recall заметок и памяти, досье, привязки персоны, граф кода)
  при `RecallInTurnText` едут хвостом хода, а не системным блоком: иначе они обнуляют prefix
  cache всей истории следом за собой.
- Слот (strong/medium/weak) разворачивает в модель только
  `UserModelTierResolver.ModelFor(tier, ownerId)` — единственная точка склейки, дублировать
  её логику нельзя.
- **Фолбэк хода — только по цепочке**, автоподбора нет; `None` (неопознанная или
  содержательная ошибка) фолбэк НЕ запускает, но ход обязан завершиться `error`, а не штатным
  finished. Классификатор (`TurnErrorClassifier`) работает с СЫРЫМ текстом: по русской
  формулировке 429/529 не распознаются. Отказ ВЫХОДА В СЕТЬ отделён от отказа провайдера:
  мёртвый канал наружу лечится повтором той же пары, соседняя модель пойдёт тем же прокси.

**Перед правками в `ClaudeHomeServer.Llm/` — прочитай
[docs/architecture/llm-providers.md](docs/architecture/llm-providers.md)** (BareMode и карта
`CLAUDE-local.md`, `RecallInTurnText`, смена провайдера у начатого чата, тексты отказов,
паспорта ходов и разведение отказов сети); слоты и таблица назначений —
[model-presets-and-tiers.md](docs/features/model-presets-and-tiers.md), цепочки фолбэка —
[ADR-007](docs/adr/ADR-007-model-preset-chains.md) §4.

## Генератор картинок (Services/Images)

Аватар персоны рисует слой драйверов `IImageGenerator` (fal.ai, glif) за роутером
`ImageGenerationService`; провайдера и модель выбирает админ отдельно для каждого места
(`ImagePlaces` — сейчас одно: `persona-avatar`). Инвариант тот же, что у моделей: **явно
выбранного провайдера не подменяем**, переход на другого — только в «Автоматически». Не
нарисовалось — сущность живёт на инициалах, картинку догоняет очередь `ImageBackfillService`.
Детали — [docs/features/image-generation.md](docs/features/image-generation.md).

## Раздел «Видео» (Services/Video)

Эфиры телеканалов и лента подписок YouTube за общим `IVideoProvider`; живой кадр рисуется оверлеем над страницами (панель, центральный остров, плавающее окно).

Инварианты и подробности — [backend/ClaudeHomeServer.Video/CLAUDE.md](backend/ClaudeHomeServer.Video/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Значок проекта (Services/ProjectIcons)

Иконка проекта — **не картинка**: модель отдаёт имя иконки из белого списка lucide
(`LucideGlyphs`), разметки от модели не приходит никогда; любой сбой молча оставляет инициалы.
Контракт ответа, двухходовая схема подбора, белый список и форма хранения —
[ADR-009](docs/adr/ADR-009-project-icon-glyph.md); тексты интерфейса —
[docs/features/project-icon-glyphs.md](docs/features/project-icon-glyphs.md).

## Уборка карты проекта (Services/Docs)

Кнопка в настройках проекта проверяет корневой `CLAUDE.md` (размер, длинные секции, мёртвые
ссылки, вложенные карты) и предлагает, что прибрать; за флагом `project-map-hygiene`. Запись —
только по явной отметке человека, а **вынос секции кнопкой не делается никогда** (цена ошибки —
потеря знания, оплаченного инцидентами), регулярной автоматики тоже нет. Устройство двух фаз —
[docs/features/project-map-hygiene.md](docs/features/project-map-hygiene.md), план —
[project-map-hygiene-plan-2026-09.md](docs/research/project-map-hygiene-plan-2026-09.md).

## Внутренние подсистемы (Services/Composition)

Внутренние границы продукта — **подсистемы**, контракт
[`IAppSubsystem.cs`](backend/ClaudeHomeServer.Core/Services/Composition/IAppSubsystem.cs)
(`Key`/`Title`/`Register` плюс `AddSubsystems`). Не путать с **внешними модулями** YARP
(`Services/Modules`, `IModule`/`ModuleRegistry`): те живут в отдельном процессе за
реверс-прокси, эти — внутри Microsoft DI, без выгрузки и hot-plug. Общая инфраструктура —
`ClaudeHomeServer.Core` (ноль `PackageReference`), направление ссылок
`Main → Vertical → Core`; швы заводятся по фактической потребности, не впрок.

Курс — Этап 5: вынести в отдельные `.csproj` ВСЕ вертикали, критерий успеха — в `Services/`
не остаётся ни одной папки вертикали, Main = композиция + контроллеры + ядро сессий. Статус,
список вынесенного, метрика и разбор промахов оценки —
[ADR-014](docs/adr/ADR-014-internal-subsystems.md). Состав таблицы `Boundaries` в карте не
держим намеренно: источник правды — таблица в коде, а устаревший список хуже его отсутствия.

**Правило зависимостей:** вертикаль зависит от спины (`Microsoft.*`, `Models`,
`Services.Http`/`Composition`/`Mcp`) и от явных швов (например, `IDesktopChatDirectory`), но
НИКОГДА от другой вертикали напрямую. Нужна связь — два пути: событие `TurnEventBus`
([ADR-013](docs/adr/ADR-013-turn-event-bus.md)) либо явный интерфейс-шов. Держат правило
сторожа `SubsystemBoundaryTests` и `SubsystemBoundaryCoverageTests` (default-deny +
точечный allow-list, источник правды — сборка) плюс регрессия IL-скана
`IlBoundaryRegressionTests`.

**Реестр, который наполняют разные слои, а читает REST-гейт, кладётся в спину, а не внутрь
питающей вертикали** — иначе из него вырастает цикл между вертикалями (так и родился
`Services/TranscriptRoots.cs`: реестр внутри `WorkflowAgentParser` дал цикл `Llm ⇄ Execution`).

Грабли, на которых сторож остаётся зелёным при сломанной границе:

- **Дубль записи в `Boundaries` не ловит ни один сторож**: `ToHashSet()` по `NamespaceRoot`
  схлопывает вторую запись, и покрытие считается выполненным. Видно только по расхождению
  числа тестов.
- **Без форс-загрузки сборок сторож проходит вакуумно** (мутацией доказано: 17/17 зелёных при
  нулевом наборе) — новая вертикаль обязана добавить строку `typeof(...).Assembly` в
  статические конструкторы сторожей.
- **Объём выноса меряется заглушками до сходимости, а не одной пробной сборкой**: Roslyn
  встаёт на фазе объявлений и не идёт в тела методов (на `Llm` первый прогон обещал 14 ошибок
  вместо настоящих 90). Скрытое чаще оказывается **спиной**, а она лечится переносом в Core с
  сохранением namespace, а не швом: ссылка `Vertical → Main` невозможна по построению.
- Чего IL-скан не видит и не увидит: `Reflection.Emit`, `Type.GetType(string)` и рефлексия,
  рантайм-резолвы по атрибутам. Устройство сторожей целиком — ADR-014, раздел «Устройство
  сторожей границ».

**Отключаемость подсистемы:** тумблер `Subsystems:{Key}:Enabled` (нет секции = включена),
единственная точка чтения — `SubsystemGate.IsEnabled`, инлайновых `config.GetValue` не
заводить. У выключенной подсистемы `Register` не вызывается, поэтому **обязательный параметр
конструктора от отключаемой вертикали вне её самой — дефект**; легальны три формы (`?.`/`?? []`,
ранний выход по `is null`, честный 503 с причиной), 500 и необработанное исключение — нет.
Гейт ставится **на регистрацию**: пост-хок удаление дескрипторов не видит регистрацию через
`ImplementationFactory`. `ApplicationPart` вертикали под `Microsoft.NET.Sdk.Web` подключается
MSBuild сам — изоляция маршрутов делается только **удалением** части в `Program.cs` через
`ConfigureApplicationPartManager`. Выводы пилота (Notes) — ADR-014, раздел «Пилот
отключаемости».

**Штаб (`Services/Team`) вынесен из `SessionManager`, но отдельным `.csproj` не выносится** —
десятки обращений к базовым операциям ядра, фасад на них дороже оставляемого. Швов ровно
**четыре** (`ITeamSessionDirectory`, `ITeamHistoryStore`, `ITeamRunState`, `ITeamTurnIntake`
в `Services/Team/TeamCoreSeams.cs`): новые не заводим, достраиваем существующие. Фактура —
[team-di-migration-2026-09.md](docs/research/team-di-migration-2026-09.md), решение и его
цена — ADR-014.

## Claude Code CLI subprocess

`ClaudeSession` запускает: `claude --print --output-format stream-json --input-format stream-json --include-partial-messages --permission-prompt-tool stdio [--resume <id>]`

WorkingDirectory = `project.RootPath`. Маппинг `stream-json` → `ServerMessage` —
[llm-providers.md](docs/architecture/llm-providers.md), раздел «Маппинг stream-json».

## MCP-серверы продукта (mcp/*)

Транспорта два. Продуктовые серверы живут в Kestrel по **MCP-over-HTTP**
([ADR-012](docs/adr/ADR-012-mcp-over-http-transport.md)): тулсет в `Services/Mcp/Http` плюс
общий `POST /mcp/{name}[/{хвост}]`, node-процесса нет вовсе. Хвост маршрута несёт контекст вызова
(сессия-вызыватель, у `memory` — персона и проект), по нему тулсет живьём резолвит
проект/персону/привязки; владелец берётся из claim `sub` сервисного JWT, не из маршрута.
На stdio остался только `desktop` (capability-токен, ADR-008);
у `watch` и `websearch` stdio-ветки отката нет вовсе. Замороженные `mcp/*-server/index.js` —
ветки отката под `Mcp:HttpTransport=false`.

- **Состав `tools/list` не зависит от ХОДА** (от свойств сессии — может): он входит в
  сигнатуру запуска CLI, и любая зависимость от свойства хода перезапускает процесс со ВСЕМИ
  MCP-серверами («Stream closed», «No such tool available»). Ограничения по ходу — на
  бэкенде (`[DenyOnDelegatedTurn]`, `DelegatedTurnGate` fail-closed); сторож —
  `McpToolsetStabilityTests`.
- **Транспорт — только `http` на адресе из `ResolveTasksApiUrl`**: по https CLI молча теряет
  инструмент. `NO_PROXY` бэкенд ставит на каждый ход, иначе локальный запрос уедет в прокси;
  заголовки контекста шлёт КЛИЕНТ — что не положено в `headers` конфига, до бэкенда не доедет.
- **Не плодить однокоренные имена инструментов** с пересекающейся семантикой (`execute` vs
  `complete` — LLM путает).

**Личный реестр MCP-серверов** (внешние серверы владельца) — **allow-list, единственная
модель**: сервер не едет никуда, пока не включён в проекте чата ИЛИ выдан персоне чата; вне
проекта — по `AllowOutsideProjects`. Каталог по официальному реестру (флаг `mcp-catalog`) —
источник предложения, а не доверия: ссылка на него хранится как `McpCatalogRef`, а НЕ новым
значением enum `Source` (откат кода унёс бы `mcp-servers.json` всех владельцев в `.corrupt`),
и allow-list рантайм-флагов `uvx` ПУСТ — `--from`/`--with`/`--index*` подменяют источник пакета.

Состав инструментов, `websearch`, живучесть stdio-цикла и грабли HTTPS-деплоя («fetch failed»
у всех инструментов при живом бэкенде → явный `McpTasksApiUrl`) —
[docs/architecture/mcp-servers.md](docs/architecture/mcp-servers.md); реестр, каталог и вход
по OAuth — [docs/architecture/mcp-registry.md](docs/architecture/mcp-registry.md).

## Заметки и Знания (Dify RAG)

Заметки — Obsidian-совместимый markdown-vault (`[[wikilinks]]`, backlinks, граф): настоящие
`.md` в личном vault `data/notes/{userId}` + `notes/` проектов. Знания — менеджер
Dify-датасетов, и Dify тут источник истины. Ключ Dify общий на инстанс, поэтому единственная
граница между владельцами — **проверка релевантности датасета на КАЖДОМ `{id}`-эндпоинте**
(иначе 403); контуры Dev/Prod на одном Dify разводит `Dify:Namespace` — без него дев-стенд
лезет в боевые датасеты.
**Перед правками — прочитай [docs/architecture/knowledge.md](docs/architecture/knowledge.md)**
(синк по хешам, lifecycle-каскады, реконсайлер упавших документов).

## Интеграция с мессенджерами (Max / Telegram) — не реализовано

Полноценный чат с Claude через мессенджер делать **не надо** — он не отрендерит
diff/артефакты/виджеты; оправдывает интеграцию только уведомление о завершении задач и ответ
на permission-запросы. **Max для ботов закрыт** (только верифицированные юрлица РФ).
Исследование и архитектура — [docs/research/messenger-integration.md](docs/research/messenger-integration.md).

## Персоны

«Персоны = контакты, Чаты = разговоры»: персона — отдельная per-owner сущность
(`data/personas.json`, не .md-агент); чат с персоной = `Session.PersonaId`, слой персоны
пересобирается каждый ход. Инварианты: у задач `PersonaId != null ⇒ Assignee = Claude`
(`TaskManager.NormalizePersonaAssignee`); доступы — `Persona.Access` →
`PersonaAccessPolicy` (disallowed-инструменты), `Persona.Tools` гейтит tasks/notes/web.

**Перед правками в персонах (промпт, память, групповые чаты, пантеон OmO, аватары, MCP
personas/memory) — прочитай [docs/architecture/personas.md](docs/architecture/personas.md).**

## Десктопный агент (ClaudeHomeServer.Desktop, за флагом `desktop-agent`)

Руки песочницы на машине пользователя: MCP-сервер `desktop` плюс WPF-клиент, за флагом `desktop-agent`.

Инварианты и подробности — [backend/ClaudeHomeServer.Desktop/CLAUDE.md](backend/ClaudeHomeServer.Desktop/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Механики OmO в чатах

Тексты — переводы oh-my-openagent ([docs/omo/adoption.md](docs/omo/adoption.md)); рантайм —
`OmoPrompts*.cs` в `ClaudeHomeServer.Prompts` (генерируются скриптом docs/omo/gen-omo-prompts.ps1).
Главное — цикл «до готово» (флаг `work-loop`). Детали —
[docs/architecture/features.md](docs/architecture/features.md), раздел «Механики OmO».

## REST API

Все эндпоинты (кроме `/api/auth/login`) и SignalR-хаб `/hubs/session` — под `[Authorize]`;
схема — **JWT Bearer**, токен выдаёт `POST /api/auth/login` по паре `{ username, password }`
(вход под rate-limit). Новая ручка без `[Authorize]` — дыра наружу.
Справочник эндпоинтов, методы хаба и события — [docs/architecture/api.md](docs/architecture/api.md)
(источник правды — контроллеры); удалённый доступ —
[docs/operations/remote-access.md](docs/operations/remote-access.md).

## Выкатка на бой из веб-морды (`Services/Deploy`)

Пункт меню «Выкатить на бой» — сигнал трей-раннеру именованным событием Windows (не путать с выкаткой прода из чата, ADR-010).

Инварианты и подробности — [backend/ClaudeHomeServer.Deploy/CLAUDE.md](backend/ClaudeHomeServer.Deploy/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Питание машины из веб-морды (`Services/Power`)

Пункт меню аватара «Питание компьютера» гасит, перезагружает или усыпляет машину, на которой
крутится продукт; команду отдаёт САМ бэкенд, трей-раннер тут ни при чём. Замков три и они
независимы: роль admin, `PowerControl:Enabled` (false по умолчанию) и платформа —
**скрывать пункт в UI без серверной проверки нельзя**, веб-морда торчит наружу.

Инварианты и подробности — [backend/ClaudeHomeServer/Services/Power/CLAUDE.md](backend/ClaudeHomeServer/Services/Power/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Observability (OpenTelemetry)

OpenTelemetry в двух режимах: dev → Aspire Dashboard, prod → SigNoz; алерты приезжают в уведомления, инциденты разбираются детерминированным кодом.

Инварианты и подробности — [backend/ClaudeHomeServer/Telemetry/CLAUDE.md](backend/ClaudeHomeServer/Telemetry/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Реализовано

Ядро: вход по паре `{username, password}` с JWT (раздел «REST API»), проекты, сессии, чат
(вложения/голос/режимы ⚡📋❓), файловый менеджер с diff/revert, empty states.

Поверх ядра: виджеты в чате (sandbox-iframe + строгая CSP), артефакты сессии, продуктовая
история «Что нового», плагин oh-my-claudecode, задачи v3, бэкапы каталога `data`, панель
«Документация», панель «Сервисы» (дев-серверы проекта в iframe через прокси `/preview/**` —
**ключи и эндпоинты остались `preview`**, переименована только подпись), ридер ссылок
([ADR-005](docs/adr/ADR-005-link-reader-server.md)), голосовой режим чата и разговор без рук,
архив чатов, контекстные замечания к плану, серверные сторожа чатов.

Инварианты, которые по коду и по дереву репозитория не восстановишь:

- **Голосовой формат держат ДВЕ точки промпта** — секция `voice-mode` и оговорка последним
  блоком слоя персоны: без второй слот «Формат ответов» персоны перебивает правило, слой
  клеится после секций.
- **Маркер `<voice>` режется только ВНЕ блоков кода и остаётся в транскрипте**: иначе
  `--resume` теряет пример формата, а ответ, показывающий маркер примером, обрывается на
  экране навсегда — у всех, включая тех, у кого стиль выключен.
- **В петле разговора распознавание и озвучка не пересекаются никогда** — иначе эхо.
- Коды `POST /api/tts` 503 `not_configured` / 502 `upstream` — контракт фолбэка на голос
  браузера, менять осознанно.
- **Долгое ожидание внешнего события — только серверный сторож `watch_start`**: Monitor и
  `run_in_background` харнесса живут внутри процесса CLI и умирают вместе с ним
  ([ADR-013](docs/adr/ADR-013-server-chat-watchdogs.md)).

Детали каждой фичи — [docs/architecture/features.md](docs/architecture/features.md).

## Фич-флаги (feature toggles)

Dark launch: фича коммитится выключенной и включается per-user в меню «Экспериментальные
функции». Реестр (source of truth) — в коде: `FeatureFlagCatalog.All`
([Models/FeatureFlag.cs](backend/ClaudeHomeServer.Core/Models/FeatureFlag.cs)), там же у
каждого ключа title и описание; хранение — override в `data/users.json`; фронт — стор
[lib/featureFlags.ts](frontend/src/lib/featureFlags.ts), хук `useFeature(FLAGS.key)`.
Перечня флагов карта не держит: он протухает быстрее всего остального.
**Пометки «за флагом …» в доках — исторические**, актуальный состав только в каталоге кода
(ADR-013, например, до сих пор описывает сторожей чатов как флажных — флаг снят 2026-09-01).

Флаги, снятые после dark launch, работают у всех безусловно: ассистент по умолчанию и
знакомство ([onboarding-intro.md](docs/architecture/onboarding-intro.md),
[project-onboarding-v2.md](docs/features/project-onboarding-v2.md)), фон проекта
([ADR-008](docs/adr/ADR-008-project-background-generation.md),
[project-backgrounds.md](docs/features/project-backgrounds.md)), карточка доклада о
завершённой задаче ([task-completion-report.md](docs/features/task-completion-report.md)),
серверные сторожа чатов ([ADR-013](docs/adr/ADR-013-server-chat-watchdogs.md)) и встроенная
интеграция Higgsfield — её доставка в ход, предохранитель «Отключить» и отсутствие тумблера
в интерфейсе разобраны в [mcp-registry.md](docs/architecture/mcp-registry.md).

**Как добавить новый флаг (3 шага):**
1. Бэк: добавить строку в `FeatureFlagCatalog.All` (`key`, `title`, `description`, `Default: false`, `stage`).
2. Фронт: добавить ключ в const `FLAGS` в `lib/featureFlags.ts`.
3. Обернуть фичу: `{ useFeature(FLAGS.myFeature) && <MyFeature /> }`.

Тумблер в модалке появится сам (рендерится из каталога). Ключи дублируются в двух местах
(C#-каталог и TS-`FLAGS`) — при переименовании править оба.

## Агенты (.claude/agents/)

Содержимое папки в .gitignore: туда `PersonaAgentFileSync` синкает персон-консультантов
владельца (у каждого пользователя свои, между машинами конфликтуют). Версионируются только
общие проектные агенты — точечные `!`-исключения в .gitignore:

| Агент | Роль |
|---|---|
| `scout` | разведка по коду: «где в коде X», «кто ещё зовёт Y» — возвращает список файл:строка, читает вместо тебя, не засоряя твой контекст |
| `designer` | ревью UI-изменений по [docs/design/guidelines.md](docs/design/guidelines.md); запускается только по явному согласию — перед коммитом заметного UI его предлагают, а не вызывают молча |

## Конфигурация

Машинно-специфичные значения (локальные пути, секреты, локальные URL) **не правим в
отслеживаемых `appsettings*.json`** — только в `backend/ClaudeHomeServer/appsettings.Local.json`
(в `.gitignore`, у каждого свой; образец — `appsettings.Local.example.json`). Порядок загрузки
и подключение — [conventions.md](docs/architecture/conventions.md), раздел «Конфигурация».

## Соглашения

Полная версия с разбором граблей — [docs/architecture/conventions.md](docs/architecture/conventions.md).
Главное:

- **ВАЖНО: CI гоняет тесты на Linux** (`ubuntu-latest`), а разработка идёт на Windows — тесты
  обязаны быть платформонезависимыми: пути от `Path.GetTempPath()` + `Path.Combine`, ожидание
  **события** через `TaskCompletionSource`, а не `Task.Delay`. На итеративную правку —
  `dotnet test --filter "FullyQualifiedName~<Набор>"`, полный прогон перед коммитом/PR.
- **Одна папка — один проект на владельца** (`ProjectManager.EnsureRootFree`, 400 при повторе):
  датасет Dify ключуется по `RootPath`. У разных владельцев общая папка допустима.
- **Удаление чата уносит и транскрипт claude CLI** во всех профилях. Инвариант: только файл с
  точным именем `{csid}.jsonl`, никогда по маске и никогда сама папка (один `~/.claude` делят
  все инстансы плюс интерактивные сессии пользователя). `resumeSessionId` валидируется белым
  списком `^[A-Za-z0-9_-]{1,128}$` — иначе `".."` снёс бы всю папку `data`.
- **`UpdatedAt` не двигают ни настройки чата, ни архивация/возврат** — по нему идут сортировка,
  секции дерева и непрочитанность; признак архива производный
  (`IsArchived = ArchivedAt != null && UpdatedAt <= ArchivedAt`), поэтому любая активность
  возвращает чат из архива сама. Срок временного чата считается от `Session.ExpiryAnchor`.
- **Новое хранилище → сверься с бэкапом**: всё в `data/` попадает в архив по умолчанию, секреты —
  в `BackupPaths.SecretFileNames`, сторы вне `data/` — в `BackupCore.CopyDataTo`, ломающее
  изменение формата = инкремент `BackupSchema.Version`.
- **Единственные точки, которые нельзя дублировать своей реализацией:** `SafePath.Join` —
  защита от path traversal (из вертикали звать Core-примитив напрямую, обращение через
  форвардеры `FileService` сторож границ ловит как ссылку на чужую вертикаль); `MarkdownFence` —
  разбор ```-заборов; `AddQuietHttpClient` — HTTP-клиент к опциональной зависимости (дефолтный
  логгер печатает каждый провал как Error со стектрейсом и забивает консоль).
- Хранилище проектов — `data/projects.json`; метаданные сессий — `data/sessions.json`, история
  чата — `data/sessions/{claudeSessionId}/history.json`, resume через `--resume`.
- Комментарии в коде по-русски.

## Коммиты

**Conventional Commits** `type(scope): описание`, сообщения **по-русски**, атомарно — одно
логическое изменение на коммит; трейлер `Co-Authored-By: <модель> <noreply@<домен вендора>>`
(таблица вендоров — в [conventions.md](docs/architecture/conventions.md)).

`commit`/`push` — только по явной просьбе. **Исключение — цепочка задач-исполнителей в общем
worktree** (задача трекера с явно заданным `worktreePath`): исполнитель коммитит свои файлы
локально сам, как только его критерии зелёные, не дожидаясь просьбы (`push`, PR и мерж — всё
равно по просьбе). Причина — инцидент 2026-09-01: untracked-файлы не защищены ни git reflog,
ни `fsck`, и окно до коммита равно окну необратимой потери.
