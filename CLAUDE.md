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

> **Стандарт: сборка и тестирование — в dev-контейнере.** По умолчанию собираем и
> прогоняем приложение в контейнере (песочница для Claude + единое воспроизводимое
> окружение), а не на хосте. Подробности — [docs/operations/docker.md](docs/operations/docker.md).

```powershell
# Контейнер (из корня проекта) — основной путь
copy .env.example .env                       # один раз: пути, CLAUDE_EGRESS_PROXY
docker compose -f docker-compose.claude.yml up -d --build   # сборка + запуск, http://localhost:5000
docker exec -it claude-server claude login   # один раз: вход по подписке
docker logs -f claude-server                  # логи
docker compose -f docker-compose.claude.yml up -d --build claude-server  # пересборка после правок
```

```powershell
# Хостовый запуск (справочно, для быстрых локальных итераций)
cd backend; dotnet build
cd backend; dotnet run --project ClaudeHomeServer   # порт 5000
cd frontend; npm run dev       # порт 5173
cd frontend; npm run build     # production-сборка (tsc -b + vite)
# Vite (:5173) проксирует /api и /hubs (WebSocket) на :5000. Стенд :5000 раздаёт именно
# frontend/dist ПОСЛЕДНЕЙ сборки: после правок .tsx нужен npm run build (или сиди на :5173);
# wwwroot в репо не живёт — прод получает его из dist агентом выкатки. Детали: docs/operations/dev-stand-host.md
```

Хостовый дев-стенд поднимаем **только через `dotnet run`** (или с явным
`ASPNETCORE_ENVIRONMENT=Development`): порождённые процессы наследуют `Production`, а там
`Kestrel:Endpoints` уводит стенд на занятый боевым инстансом :80, и `ASPNETCORE_URLS` это не
чинит. Разбор и команда фонового запуска — [docs/operations/dev-stand-host.md](docs/operations/dev-stand-host.md).

## Среда исполнения пользователей (local / container)

Изоляция per-**пользователь**: `User.ExecutionEnvironment` = `local` (процессы на машине
сервера) | `container` (общая docker-песочница `cc-sandbox`); бэкенд всегда НА ХОСТЕ (Windows).
Все точки запуска процессов идут через `IProcessLauncher` / `ILauncherFactory.ForOwner(ownerId)`
(драйверы `LocalProcessRunner` / `DockerProcessRunner`); системные one-shot — всегда local.
Пути: бэкенд работает ТОЛЬКО с хостовыми, `IPathMapper` переводит в контейнерные в момент
запуска. Домашние папки юзеров — единая точка
[UserHomeResolver.cs](backend/ClaudeHomeServer/Services/UserHomeResolver.cs).

**Инварианты:** смена `ExecutionEnvironment` при существующих чатах запрещена; токен подписки
доставляется в песочницу per-exec, а не запекается при создании контейнера.

**Перед правками в `Services/Execution/`, `SandboxManager`, `UserHomeResolver` — прочитай
[docs/architecture/sandbox.md](docs/architecture/sandbox.md)** (монтирования, interrupt, MCP из песочницы, overrides).

## Архитектура

```
Browser (React 18 + TypeScript)  →  SignalR WebSocket  →  ASP.NET Core 10 (:5000)
```

Слои бэкенда — `Controllers/`, `Hubs/SessionHub`, `Services/` (в т.ч. `Llm/` — слой LLM-провайдеров),
`Protocol/ServerMessage` (record-типы WS-событий); фронт — `pages/`, `components/`, `hooks/`,
`lib/` (`api.ts`, `signalr.ts`, `design.ts`), `types/`. Состав файлов смотри в дереве репозитория.
## Дизайн-макеты

Claude Design проект: `52adb1f7-312b-4f25-8c47-2bccfca9df94`. Ключевые файлы:
`Claude Code Desktop.dc.html` (десктопные макеты, все состояния), `shots/*.png`.

## Дизайн-система

**Полная конвенция — [docs/design/guidelines.md](docs/design/guidelines.md), обязательна
для ЛЮБЫХ изменений UI.** Живой эталон — витрина UI-кита: в dev по `#/ui-kit` без
авторизации, исходник [UiKitPage.tsx](frontend/src/dev/UiKitPage.tsx) (в production-бандл
не попадает). Железные правила:

- Цвета — только токены `C.*` из [design.ts](frontend/src/lib/design.ts); сырой hex в `.tsx` —
  дефект; значения тем — в [theme.css](frontend/src/lib/theme.css) (новый цвет — в ОБЕ темы).
- **Проверяется линтом:** `cd frontend; npm run lint:design` обязан быть зелёным. Гейт стоит
  **перед коммитом**, а не после каждой правки; по ходу работы хватает `npx tsc -b`.
- Размеры — из шкал `FS`, `SP`, `R`, `SHADOW`, `Z`, `MODAL_W`.
- Контролы — только из `frontend/src/components/ui/`; самодельные кнопки/модалки из div — дефект.
- Accent-дисциплина: оранжевый `C.accent` — только главное действие и активные состояния.
- Шрифты: PT Serif (заголовки), Hanken Grotesk (UI), JetBrains Mono (код); иконки — lucide-react.
- Стили: только inline-objects, без Tailwind/CSS-modules. Каждый экран живёт на мобиле (`useIsMobile`).
- **Приоритетные устройства и их ширины** (интерфейс, виджеты, картинки) —
  [docs/design/target-devices.md](docs/design/target-devices.md); считать в CSS-пикселях,
  а не в паспортных: нижний ориентир 360 CSS, Fold 8 в развороте — пограничный для `MOBILE_MAX`.
- Новый раздел — по «Рецепту нового раздела» из гайда (эталон — KnowledgePage); для заметного
  UI перед коммитом **предложить** прогон через субагента `designer` и дождаться ответа.

## LLM-провайдеры (Services/Llm)

Единственный рантайм — claude CLI (`Llm/Claude/ClaudeSession`); сторонние провайдеры
(DeepSeek, GLM) подключаются env-оверрайдами процесса на каждый ход. Конфиг — секция
`LlmProviders`, ключи в appsettings.Local.json (пустой `ApiKey` = провайдер выключен);
резолв, цены и возможности — `LlmProviderRegistry`. Инварианты:

- Смена провайдера у чата разрешена и после начала разговора: транскрипт CLI локальный и
  переезжает в профиль цели (`MigrateProviderAsync` — общая точка настроек чата и кнопки
  «Продолжить на …»). Отказ — если ход виден как идущий (проверка best-effort: лока между
  ней и копированием файла нет) либо чат десктопный, а цель — сторонний провайдер (кадры
  рабочего стола наружу не отдаём); сорвавшийся перенос
  НАЙДЕННОГО транскрипта тоже 400, а «переносить нечего» — штатный случай.
- Профили CLI `data/claude-profiles/{key}` изолируют OAuth-логин ~/.claude (иначе CLI пошлёт
  чужому эндпоинту токен подписки → 401); креденшалы в профили не копируются никогда.
- Иммунитет к системному окружению: на КАЖДОМ запуске из унаследованного env вычищаются
  `ANTHROPIC_*`, `CLAUDE_CONFIG_DIR` и пр. (`ProviderEnvKeys` → `ClearEnv`); маршрут CLI задаёт
  только сервер, `CLAUDE_CODE_OAUTH_TOKEN` не трогается.
- One-shot вызовы — всегда `--safe-mode` + `--no-session-persistence` (состав флагов —
  `OneShotClaudeRunner.BuildArgs`, под тестом).
- **`BareMode` + `SystemPromptFile`** в `LlmProviderConfig` — отключают автозагрузку CLAUDE.md,
  хуков, LSP, плагинов и авто-памяти; CLI получает ТОЛЬКО короткую карту через
  `--system-prompt-file`. Для локальных моделей с малым окном это снижает вход с ~95 000 до
  ~2 400 токенов (замер 2026-09-05). Файл карты (`backend/ClaudeHomeServer/SystemPrompts/CLAUDE-local.md`,
  несколько КБ) поставляется с продуктом. **Цепочка резолва** per-project:
  `<корень проекта>/docs/CLAUDE-local.md` (приоритет, потолок 16 КБ — выше
  отступаем к серверной) → серверный дефолт от `AppContext.BaseDirectory` (прокинут
  через `LlmSessionContext.ContentRootPath`) → оба флага снимаются с warning. Резолв от
  `AppContext.BaseDirectory` — лечение ревью 2026-09-05: иначе в любом чужом проекте ход
  падает с exit=1. Если файл не найден — оба флага снимаются с warning в stderr
  («SystemPromptFile не найден»), ход идёт обычным путём (CLI сам подтянет CLAUDE.md
  проекта); SafeJoin ловится, ход не валится (тоже с warning). `--bare` ломает
  OAuth-авторизацию CLI, но это безопасно СТРУКТУРНО: BareMode включается только для
  не-родного провайдера (реестр находит через `ResolveByModel`), а у не-родных OAuth нет
  (ставят `ANTHROPIC_API_KEY` из `BuildCliEnv`). Состав стабилен в пределах сессии
  (`McpToolsetStabilityTests`). **У container-владельцев карта живёт на паре, которую
  обязаны держать синхронно:** bind-mount `<BaseDirectory>/SystemPrompts:/app/SystemPrompts`
  (`SandboxManager`, факт наличия каталога входит в `ConfigHash`) и одноимённое правило
  `DockerPathMapper` — расхождение рантайм не ловит, оно даёт exit=1 и ложный `Unreachable`
  у каждого container-владельца; связаны тестом в `DockerPathMapperTests`.
  Тесты — `ClaudeSessionBareArgsTests`.
- **Три слота моделей (strong/medium/weak) + глобальная таблица назначений.** Слот личный
  per-user поверх глобального инстанса; каждое МЕСТО применения модели — строка каталога
  `LocalActionCatalog`. Слот разрешается в модель **по владельцу действия** через
  `UserModelTierResolver.ModelFor(tier, ownerId)` — единственную точку склейки, дублировать её
  логику нельзя. Агентным местам (`Agentic: true`) локаль и `direct:`-модели недоступны.
  Приоритет: явная модель сущности → её `ModelTier` → назначение места. Шлюзы на границе
  запуска — `ClaudeSession.EffectiveModel` и `OneShotClaudeRunner.ResolveModel`.
- **Фолбэк хода — только по цепочке**, автоподбора нет: ротация подписок пула (тихо) → шаги
  цепочки (маркер в ленте) → честная ошибка. Классы ошибок (`TurnErrorClassifier`): RateLimit /
  UsageLimit / ProviderError / Unreachable / ContextOverflow / **AuthFailure**; `None` —
  неопознанная/содержательная ошибка: фолбэк НЕ запускается, но ход обязан завершиться `error`
  (не штатным finished). AuthFailure (401/протухший OAuth/ключ) в пуле Claude триггерит тихую
  ротацию уровня 1 (свой токен у другой подписки), у стороннего провайдера — шаг цепочки;
  пометка `MarkAuthDead` обратима: снимается по `rate_limit_event` от подписки (доказательство
  аутентификации) в обработчике хода и в идл-пинге warmup.
- **Отказ ВЫХОДА В СЕТЬ отделён от отказа провайдера.** `Unreachable` покрывает оба, но лечатся
  они противоположно: мёртвый эндпоинт вендора — сменой пары, мёртвый общий канал наружу —
  только повтором (соседняя модель пойдёт тем же прокси). Разводит их проба `EgressProbe`
  (TCP-коннект к `HTTP(S)_PROXY`, а если переменных нет — к `Sandbox:Proxy`: у
  container-владельцев прокси песочницы в env бэкенда не попадает),
  спрашиваемая ТОЛЬКО при `Unreachable` и ДО кулдауна.
  Канал лёг → один повтор той же пары через 5 с, без траты шагов цепочки и без
  `MarkUnavailable`; не помог → `TurnFailureText.EgressDown` вместо перебора мёртвых шагов.
  Прокси не задан/отвечает — поведение прежнее (fail-open).
- **Паспорта ходов** — `TurnRunLog` по образцу `SubagentRunLog`: исход (`success` | `failed` |
  `egress_down` | `interrupted` | `cancelled` | `crashed`), пары «модель × провайдер», попытки,
  подмены, класс ошибки. Память + `data/logs/turn-runs-*.jsonl`, отдаёт `GET /api/turns/runs`
  (админ). `FallbackLlmSessionAdapter` в `finally` цикла только ПУБЛИКУЕТ событие
  `turn/completed` с паспортом ([ADR-013](docs/adr/ADR-013-turn-event-bus.md)), а пишет
  его единственный подписчик — `SessionManager.HandleTurnCompleted`.
- **Красная карточка ошибки — только у хода, который реально не состоялся.** Промежуточная
  ошибка попытки задерживается вместе с её result: подмена состоялась → карточки нет,
  сырой текст сворачивается в «Подробности» маркера подмены; подмены не было → ошибка уходит
  строго перед финальным result. Тексты для человека — одна точка `Services/Llm/TurnFailureText`
  (сырой `ex.Message`/ответ CLI живёт в `ErrorMessage.Details` и в логе полным `{ex}`);
  классификатор работает с СЫРЫМ текстом, по русской формулировке 529 не распознаётся.

Фоновые one-shot действия (теги, сводки, память, changelog…) считаются дёшево по маршруту
`LocalActionRouter` + `CheapTextRunner`; исполнителя каждого места выбирает админ в диалоге
«Поставщики моделей», выбор действует сразу.

**Перед правками в `Services/Llm/` — прочитай
[docs/architecture/llm-providers.md](docs/architecture/llm-providers.md)**; слоты и таблица
назначений — [model-presets-and-tiers.md](docs/features/model-presets-and-tiers.md), цепочки
фолбэка и ёмкость контекста — [ADR-007](docs/adr/ADR-007-model-preset-chains.md) §4.

## Генератор картинок (Services/Images)

Аватар персоны рисует слой драйверов `IImageGenerator` (fal.ai — синхронный,
glif — `compose_project` + опрос джобы) за роутером `ImageGenerationService`. Провайдера
(Автоматически | fal.ai | glif) и модель выбирает админ **отдельно для каждого места**
(`ImagePlaces` — сейчас одно: `persona-avatar`) — секция «Картинки» вкладки «Применение»
(`GET/PUT /api/image-generation`, стор `data/image-generation.json` поверх секции конфига
`Images`). Модель выбирается только у fal; в «Автоматически» (порядок `glif` → `fal`) и у glif
её подбирает сам генератор. Инвариант тот же, что у моделей:
**явно выбранного провайдера не подменяем**, переход на другого — только в «Автоматически».
Не нарисовалось (сервис не настроен, отказ) — сущность живёт на инициалах, а картинку догоняет
очередь `ImageBackfillService` (`data/image-backfill.json`, событие `image_backfilled`).
Детали — [docs/features/image-generation.md](docs/features/image-generation.md).

## Раздел «Видео» (Services/Video)

Эфиры телеканалов и лента подписок YouTube за общим `IVideoProvider`; живой кадр рисуется оверлеем над страницами (панель, центральный остров, плавающее окно).

Инварианты и подробности — [backend/ClaudeHomeServer/Services/Video/CLAUDE.md](backend/ClaudeHomeServer/Services/Video/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Значок проекта (Services/ProjectIcons)

Иконка проекта — **не картинка**: модель по названию проекта отдаёт имя иконки из белого
списка lucide (`LucideGlyphs`, серверная копия полного набора установленного lucide-react),
разметки от модели не приходит никогда. Подбор двухходовый («меню вместо памяти»): ход 1 —
слова-понятия, сервер отбирает по ним реальные имена, ход 2 — выбор из этого короткого меню;
ноль годных — ровно один повтор с перечислением отбракованных, без цикла. Место модели —
`project-icon` в `LocalActionCatalog`, любой сбой молча оставляет инициалы. Контракт ответа,
схема ходов, белый список и форма хранения — [ADR-009](docs/adr/ADR-009-project-icon-glyph.md);
тексты интерфейса — [docs/features/project-icon-glyphs.md](docs/features/project-icon-glyphs.md).

## Внутренние подсистемы (Services/Composition)

Внутренние границы продукта — **подсистемы**, контракт
[`IAppSubsystem.cs`](backend/ClaudeHomeServer.Core/Services/Composition/IAppSubsystem.cs)
с `Key`/`Title`/`Register` и `AddSubsystems`. Не путать с **внешними модулями**
YARP (`Services/Modules`, `IModule`/`ModuleRegistry`) — те живут в отдельном
процессе за реверс-прокси, эти — внутри Microsoft DI, без выгрузки и hot-plug.

**Общая инфраструктура — `ClaudeHomeServer.Core`** (проект добавлен коммитом
`14e474de`): контракт `IAppSubsystem` + хелперы `AddSubsystems`/`UseSubsystems`,
`QuietHttpLogger`, `JsonFileStore`, `McpSecretStore`, `SsrfGuard`. Ноль
`PackageReference`, направление ссылок `Main → Vertical → Core`. Швы
(`ISessionDirectory` и пр.) заводятся по фактической потребности, не впрок.

**Реестры с разнонаправленными писателями/читателями — спина, а не питающая
вертикаль.** Реестр, который наполняют разные слои, а читает REST-гейт,
кладётся в спину, а не внутрь питающей вертикали. Этому правилу обязан своим
появлением `Services/TranscriptRoots.cs` (волна 4): реестр корней транскриптов
лежал внутри `WorkflowAgentParser`, который наполняли `Llm`, `Execution` и
константа, а читал `WorkflowController` — оттуда вырос цикл `Llm ⇄ Execution`,
и разрез через `TranscriptRoots` в спине стал лечением, а не записью в allow-list.

**Курс после пилота физической изоляции** ([ADR-014](docs/adr/ADR-014-internal-subsystems.md),
раздел «Курс после пилота»): существующие **20** реализаций `IAppSubsystem`
(33 записи в `Boundaries`) не переносим (профиль правок против — 89% коммитов
трогают только спину, выигрыш Δ −50% не окупается), НО **новая подсистема
рождается отдельным `.csproj`**, если её потребности закрываются существующими
швами `Core` плюс максимум 1–2 новыми узкими интерфейсами. Иначе — честный
сигнал, что фича про спину, и она остаётся в Main. Сторожа границ перестроены
на перебор нескольких сборок (`AppDomain.CurrentDomain.GetAssemblies()` с
фильтром `ClaudeHomeServer` и `ClaudeHomeServer.*`, минус `*.Tests`) и
защищены от вакуумного прохода явными ассертами — иначе фильтр или порядок
загрузки молча отдают пустой набор (доказано мутацией в ревью: 17/17 зелёных
при нулевом наборе).

**Правило зависимостей:** вертикаль зависит от спины (`Microsoft.*`,
`Models`, `Services.Http`/`Composition`/`Mcp`) и от явных швов (например,
`IDesktopChatDirectory`), но НИКОГДА от другой вертикали напрямую.
Нужна связь — два пути: событие `TurnEventBus` ([ADR-013](docs/adr/ADR-013-turn-event-bus.md))
или явный интерфейс-шов. Удерживается двумя сторожами:
- `SubsystemBoundaryTests` (рефлексия по сборке, источник правды — сборка,
  не текст): default-deny + точечный allow-list. Контракт разделяет
  «префикс поддерева» и «точное имя namespace»: `AllowedNamespacePrefixes`
  открывает поддеревья (`X.` и `X`), `AllowedExactNamespaces` — ровно
  указанный тип по `FullName` (полезно для nested-типов вроде
  `SsrfGuard+AddressCheck` и для точечных синглтонов из корня `Services`
  типа `PersonaManager`/`SessionManager`). Покрытие — все 34 записи в
  `Boundaries` после волны 4 и выноса штаба: 20 реализаций `IAppSubsystem`
  (`Backgrounds, Changelog, CodeGraph, Deploy, Dossiers, Git, Images,
  Knowledge, Llm, Memory, Notes, ProjectIcons, ProjectServices, Reader,
  Skills, Spend, Tasks, Tts, Video, Yandex`) плюс `Watchdog` и 13 других
  вертикалей без подсистемы (`Auth, Backup, Desktop, Diagnostics, Docs,
  Execution, Modules, Personas, Prompts, Team, Terminal, TriggerSources,
  Turn`).
- `SubsystemBoundaryCoverageTests`: каждая реализация `IAppSubsystem` в
  сборке должна иметь строку в `Boundaries`; вертикали без подсистемы
  перечисляются явно — список выше. Ловит «новая подсистема/вертикаль
  забыта в таблице» — без этого теста свежее подразделение проходило молча,
  и границы по нему не работали.

Известное ограничение сторожей (задача `8beee75e`, волна 1, 2026-09-06,
**закрыто**): оба сторожа теперь читают IL-опкоды тел методов через
`BoundaryIlScanner` (`MethodBody.GetILAsByteArray()` + `Module.ResolveMember`),
generic-аргументы инстанцированных методов (включая
`sp.GetRequiredService<T>()`) и типы локальных переменных. Видимы:

- статические вызовы из тел методов (`DeployHost → GitService.IsGitRepo`,
  `DeployHost → Backup.InstanceLock.TryAcquireDeploy`,
  `ReaderService → SsrfGuard.*`);
- DI-резолвы (`Modules → Services.UserStore` через `GetRequiredService<T>`
  в `ModuleGatewayMiddleware.cs:84`);
- вызовы внутри async-методов и лямбд (обход nested-типов
  `<...>d__NN`/`<>c__DisplayClass` обязателен — без него теряются 3 из 7
  известных швов, а сторож остаётся на вид рабочим: проверено мутацией).

Что НЕ видно (расширение до IL не закрывает):
- тела методов, написанных на IL-ассемблере напрямую (в проекте таких нет,
  но гипотетически — `Reflection.Emit`-генерация);
- динамическая подмена типов через `Type.GetType(string)` или рефлексию
  (`Assembly.LoadFrom`, `Activator.CreateInstance`);
- рантайм-резолвы по атрибутам/маркерам (`[FromKeyedServices]`,
  `[Inject]`-подобные — в проекте нет).

Регрессия обхода nested-типов зафиксирована в `IlBoundaryRegressionTests` —
7 известных швов проверяются на видимость при каждом прогоне.

**Что гейт ловит и чего не ловит (по факту, не по моему первоначальному
утверждению):** подмена обхода вложенных типов в
`BoundaryIlScanner.AllMethodsWithNested` роняет ровно `IlBoundaryRegressionTests`
— больше НИЧЕГО. Сторожа остаются зелёными, потому что у них второй путь сбора типов
через рефлексию полей (`CollectReferencedTypes` в `SubsystemBoundaryTests.cs:1804`
и `RootSubsystemBoundaryTests.cs:469`). Исходная дыра «сторож выглядит рабочим,
а декоративен» сместилась с тела сканера на выбор пути в самом стороже: подмена
вызова `CollectAllReferencedTypes` на наивный `GetMethods(DeclaredOnly)`
в коде сторожа — все 40 тестов остаются зелёными. Чтобы закрыть эту дверь,
надо либо убрать второй путь (и заменить его целиком IL-сканером), либо заставить
теорию сторожей идти через ту же функцию, что и регрессия — тогда подмена ломает
обоих. Это не сделано.
(задача `57b5e9bc`, шаг 5).

Что НЕ сторожит:
- **тела методов, написанные на IL-ассемблере напрямую** (в проекте таких нет,
  но гипотетически — `Reflection.Emit`-генерация);
- **динамическая подмена типов через `Type.GetType(string)` или рефлексию**
  (`Assembly.LoadFrom`, `Activator.CreateInstance`): место, где вирусный код
  мог бы подсунуть тип с произвольным списком зависимостей;
- **рантайм-резолвы по атрибутам/маркерам** (`[FromKeyedServices]`,
  `[Inject]`-подобные — в проекте нет).

**Вертикаль `Services/Team` (штаб, «Командная реализация») — вынесена из
`SessionManager` 2026-09-06**, семью волнами переезда: `TeamCoordinator`,
`TeamStateService`, `TeamPlanService`, `TeamDecisionService`,
`TeamBudgetService`, `TeamEnableService`, `TeamTurnCompletionService` плюс
спутники (`TeamWaveService`, промпты, сторож волны). Ядро **10 124 → 8 999
строк**; штабного кода в нём не осталось — только owning-обёртки.

Швов ровно **четыре** (`ITeamSessionDirectory`, `ITeamHistoryStore`,
`ITeamRunState`, `ITeamTurnIntake` в `Services/Team/TeamCoreSeams.cs`): новые
не заводим, достраиваем существующие. Правило пережило проверку: в шаге 2г-4
пятый шов завели, ревью показало, что он дублирует `ITeamHistoryStore` — четыре
метода вернули туда, пятый выбросили как дубликат уже имевшегося. Цена
решения — **10 фасадных обёрток** в ядре (их зовут `ChatsController`,
`SessionHub`, `SessionMessagingService`, `DenyOnDelegatedTurnAttribute`) и пять
публичных методов ядра (`GetById`, `GetOwned`, `ResolveOwnerId`,
`ReportUpAsync`, `BroadcastAsync`).

**Шаг 2г-4 (обёртки → DI):** четыре `Func`-свойства штаба с ядра сняты —
обработчики волны живут в `TeamCoordinator`, ядро отдаёт его одной ссылкой.
Обёрток в ядре 27 → 21. `HasLiveDelegatedTasks` осознанно остаётся `Func`:
его ставит `TaskExecutionService` (чужая сторона, цикл настоящий), а прямая
ссылка на `TaskManager` из спины уронила бы сторож границ.

**Отдельным `.csproj` Team не выносится** (проверено по критерию ADR-014):
73 обращения к типу `SessionManager` из вертикали, швы тянут `Models` и
`Protocol` из Main, на `InternalsVisibleTo` стоят все тесты. Остаток обёрток
снимается только вместе с переводом вертикали на DI: `TeamWaveService` —
синглтон DI, а читающие сервисы создаёт ядро в своём конструкторе, и общего
адреса, кроме `SessionManager`, у них нет. Фактура и разбивка шага 2г-4 —
[docs/research/team-di-migration-2026-09.md](docs/research/team-di-migration-2026-09.md),
план выноса — [session-core-split-2026-09.md](docs/research/session-core-split-2026-09.md).

**Разведка по кандидатам** ([ADR-014](docs/adr/ADR-014-internal-subsystems.md)):
**Video — пилот** ✅ (`VideoSubsystem`, 1 контроллер, 0 hosted, 1 исходящая
на `McpSecretStore`, 1 входящая мягкая на `Models/User.FavoriteVideoChannels`),
**Desktop — отложен, но это настоящая цель** ⏳ (4 контроллера, ~3378 строк,
три god-объекта: `SessionManager.cs:564` сам делает `new DesktopCapabilityTokenService`,
`JwtService.cs:251,266` знает про `DesktopCaller`, `ClaudeSession.cs:1498`
инъектит MCP-сервер `desktop`; готовые швы `IDesktopChatDirectory`/
`IDesktopDeviceDirectory`/`IDesktopHandsNotifier`/`IDesktopCallCanceller` —
за них и тянуть), **Backup — вычеркнут** ❌ (инфраструктурный срез поперёк
всех, `BackupValidation` десериализует 6 чужих моделей, `BackupSchema.Version`
— глобальный счётчик формата всех сторов, `BackupCli.TryHandle` работает
в `Program.cs:40` ДО построения DI). Сборки и `AssemblyLoadContext` отвергнуты:
Microsoft DI не выгружает контейнер по конструкции, отдельные сборки ломают
`InternalsVisibleTo` (на нём стоят все тесты), в .NET сборка — не бесплатная
папка как в pnpm-монорепе.

**Метрика успеха:** два ряда, оба замерены после волны 4 (текущий `HEAD` ветки
`feature/subsystems-wave4`):

- **`Program.cs` = 1464 строк / 144 регистрации.** Было после волны 3
  1544 / 207, merge-base `b8ce8f85` от `master` давал 1616 / 246.
  Δ от «после волны 3» = −80 строк, −63 регистрации; от `master`
  Δ = −152 строки, −102 регистрации.
  Считается так: `wc -l backend/ClaudeHomeServer/Program.cs` для строк и
  `grep -c 'builder\.Services\.Add' backend/ClaudeHomeServer/Program.cs`
  для регистраций.
- **`Services/*.cs` (верхний уровень, без подкаталогов) = 24 704 строк
  / 63 файла** (после выноса штаба, 2026-09-06; сразу после волны 4 было
  27 724 / 69). Было после волны 3 43 482 / 123, merge-base `b8ce8f85`
  от `master` давал те же 43 482 / 123 (метрика корня в эту базу не
  измерялась ранее). Δ от «после волны 3» = −18 778 строк, −60 файлов.
  Считается так:
  `find backend/ClaudeHomeServer/Services -maxdepth 1 -name '*.cs' | wc -l`
  для числа файлов и
  `find backend/ClaudeHomeServer/Services -maxdepth 1 -name '*.cs' | xargs wc -l | tail -1`
  для строк.

Полный разбор баз и способа подсчёта — в
[ADR-014](docs/adr/ADR-014-internal-subsystems.md), раздел «Метрика успеха».
Цель по `Program.cs` — уход под 1000 строк и 150 регистраций по мере
выделения следующих вертикалей. Цель по корню `Services` с этапа 1
снята и перенесена на этап 4 (причина — в ADR-014).

Вторая метрика — среднее число файлов, которое трогает новая фича:
с подсистемами фича = 0 правок в `Program.cs` + 1 файл подсистемы +
файлы раздела, цель — устойчиво ниже 3.

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

## MCP-серверы продукта (mcp/*)

Транспорта два. Продуктовые серверы живут в Kestrel по **MCP-over-HTTP** ([ADR-012](docs/adr/ADR-012-mcp-over-http-transport.md),
фаза 2 завершена): тулсет в `Services/Mcp/Http` + общий `POST /mcp/{name}[/{хвост}]`,
node-процесса нет вовсе (замер: 30 продуктовых node-процессов на проде до фазы 2 → 0 после).
Переехали все девять: `widgets` (фаза 1), `memory` со всеми `pmem_<handle>` (волна 1: один
тулсет на все ключи, персона и проект едут хвостом `/mcp/memory/{personaId}/{projectId}`),
`tasks`/`notes`/`personas` (волна 2: хвост `/mcp/{name}/{sessionId}` — сессия-вызыватель, по
ней тулсет живьём резолвит проект/персону/привязки, изолированные по владельцу из claim `sub`),
`wsp`/`codegraph`/`notifications` (волна 3: тот же хвост-сессия; у `wsp` `projectId` — параметр
инструмента, поэтому владение и зона проверяются на КАЖДЫЙ вызов одной формулой, пути — только
через `FileService.SafeJoin`, а рабочее дерево графа кода резолвится живьём из сессии и в
маршрут не едет) и `dify` (волна 4: объявление перенесено в код из внешнего конфига — источник
правды ключа/адреса секция `Dify` appsettings, тулсет ходит во внешний Dify напрямую через
`KnowledgeService`, ключ не покидает бэкенд; доступ режется релевантностью датасетов
пользователю — `KnowledgeBaseCatalogService`, как REST). На stdio остался только `desktop`
(capability-токен, отдельный канал — ADR-008). Замороженные `mcp/*-server/index.js` и
`mcp-dify/src` — ветки отката (`Mcp:HttpTransport=false` возвращает всё на stdio прежним env).
Подключение per-ход общее: `ClaudeSession.BuildTurnMcpConfig` собирает временный MCP-конфиг
(http-узлы с сервисным JWT владельца в `Authorization`; у stdio-веток отката — прежний env
`*_API_URL`/токен); данные per-owner — токен ограничивает доступ (эндпоинт под `[Authorize]`,
владелец — из claim `sub`).

- **Правило именования:** не плодить однокоренные имена с пересекающейся семантикой
  (`execute` vs `complete` — LLM путает).
- **HTTP-транспорт: только `http` на адресе из `ResolveTasksApiUrl`,** иначе fail-closed на
  stdio плюс WARN (по https CLI молча теряет инструмент); `NO_PROXY` ставит бэкенд на каждый
  ход, иначе локальный запрос уедет в прокси; заголовки контекста шлёт КЛИЕНТ — что не
  положено в `headers` конфига, до бэкенда не доедет. Рубильник отката — `Mcp:HttpTransport`.
- **Инвариант: состав `tools/list` не зависит от хода** — он входит в сигнатуру запуска CLI,
  и любая его зависимость от свойств хода перезапускает процесс со всеми MCP-серверами
  («Stream closed», «No such tool available»). Состав МОЖЕТ зависеть от свойств сессии
  (проект чата, персона, привязки — их тулсеты волны 2 резолвят живьём по хвосту-сессии).
  Ограничения по ходу — на бэкенде: REST — `X-Caller-Session-Id` + `[DenyOnDelegatedTurn]`,
  http-тулсеты — тот же `DelegatedTurnGate` fail-closed (MVC-фильтр на `McpTransportController`
  не применяется); сторож — `McpToolsetStabilityTests`.
- Состав режется по фактическому спросу: редко звавшиеся наборы уходят за tool-ключ с дефолтом
  «выключено», а не удаляются.
- **Диагностика:** `GET /api/mcp/calls` (админ) — счётчики вызовов, доля отказов и последние
  сбои по каждому инструменту.

Состав инструментов, живучесть stdio-цикла и грабли HTTPS-деплоя («fetch failed» у всех
инструментов при живом бэкенде → явный `McpTasksApiUrl`) —
[docs/architecture/mcp-servers.md](docs/architecture/mcp-servers.md).

**Личный реестр MCP-серверов** (раздел «MCP-серверы») — внешние серверы владельца со статусом,
пробой, входом по OAuth и доступом по проектам/персонам. Каскад доступности — **allow-list,
единственная модель**: сервер не едет никуда, пока не включён в проекте чата ИЛИ выдан персоне
чата; чат вне проекта без персоны — по `McpServerRecord.AllowOutsideProjects`. Чистое OR-правило
— `McpDelivery.ShouldDeliver`. Известное ограничение: полный цикл входа по OAuth не проверялся на
реальном сервере.

**Каталог MCP-серверов** (флаг `mcp-catalog`) — поиск по официальному реестру
`registry.modelcontextprotocol.io` + предзаполнение формы из декларации `server.json`.
Каталог — источник предложения, а не доверия: маппер режет всё неподдержанное (remotes только
https, npm с точным semver и `runtimeHint: npx|пусто`, pypi → `uvx` с именем по правилам PyPI
(PEP 503) и ПУСТЫМ allow-list рантайм-флагов — `--from`/`--with`/`--index*` подменяют источник
пакета, как `--registry` у npx; отказ секрета в argv/URL, чёрный список env), каталожная
запись заводится выключенной и несёт `McpCatalogRef` (без нового enum-значения Source — откат
кода не должен уносить стор в `.corrupt`). Гейты: SSRF-фильтр пробы, пока `Url` совпадает с
импортированным, и два подтверждения с полной строкой запуска у stdio-записи local-владельца —
проба (`{ confirmed: true }`, тело опциональное) и включение в проекте (`mcpCatalogConfirmed`).
Ревизия импортированных записей — отдельный `POST /api/mcp/catalog/revision`: плашка
«отозван» ТОЛЬКО по явному `status: deprecated/deleted` в разобранном ответе (404/таймаут/5xx
→ «проверить не удалось», сторож в тестах — лежащий preview-сервис не выключает рабочие
серверы), кэш суток на имя, только имена владельца с `CatalogRef`. Пустой `Mcp:Catalog:BaseUrl`
— каталог выключен. Подробности — раздел «Каталог MCP-серверов»
в [docs/architecture/mcp-registry.md](docs/architecture/mcp-registry.md), план и грабли
контракта — [docs/research/mcp-catalog-plan.md](docs/research/mcp-catalog-plan.md), тексты
интерфейса и продуктовые решения — [docs/features/mcp-catalog.md](docs/features/mcp-catalog.md).

## Заметки и Знания (Dify RAG)

Заметки — Obsidian-совместимый markdown-vault (`[[wikilinks]]`, backlinks, граф): настоящие
`.md` в личном vault `data/notes/{userId}` + `notes/` проектов; семантика — Dify-датасет
`{username}:notes` (без `Dify:ApiKey` тихо выключена). Знания — менеджер Dify-датасетов
(Dify — источник истины; каждый `{id}`-эндпоинт проверяет релевантность юзеру, иначе 403).
Файлы проектов синкаются дифф-по-хешам с дебаунсом 15с (`ProjectKnowledgeSyncService`) +
lifecycle-каскады. Контуры Dev/Prod на одном Dify разводит `Dify:Namespace`.
Документы, упавшие на индексации (статус `error`), лечит фоновый реконсайлер
(`Dify:Reconcile:Mode` = off | observe | heal, **дефолт off**): находит их у участников синка,
сбрасывает хеши — штатный синк пересоздаёт из источника истины; несопоставимые со сторами
(сироты, ручные документы) только показываются, не лечатся.
**Перед правками — прочитай [docs/architecture/knowledge.md](docs/architecture/knowledge.md).**

## Интеграция с мессенджерами (Max / Telegram) — не реализовано

Оправдывающий сценарий: CCS крутится на сервере, юзер не за компьютером, нужно знать о
завершении задач или реагировать на permission-запросы. Полноценный чат с Claude через
мессенджер делать **не надо** — он не отрендерит diff/артефакты/виджеты. **Max для ботов
закрыт** (только верифицированные юрлица РФ). Исследование, архитектура интеграции и решение
по ботам — [docs/research/messenger-integration.md](docs/research/messenger-integration.md).

## Персоны

«Персоны = контакты, Чаты = разговоры»: персона — отдельная per-owner сущность
(`data/personas.json`, не .md-агент) с ролью/характером/аватаром/моделью/зоной/долгой памятью.
Чат с персоной = `Session.PersonaId`: слой персоны (`PersonaPromptBuilder` + recall памяти)
пересобирается каждый ход и переживает рестарт; зона определяет scope чата. Инварианты:

- У задач `PersonaId != null ⇒ Assignee = Claude` (`TaskManager.NormalizePersonaAssignee`).
- Доступы: `Persona.Access` (full/readOnly/custom) → `PersonaAccessPolicy` формирует
  disallowed-инструменты; `Persona.Tools` гейтит tasks/notes/web.

**Перед правками в персонах (промпт, память, групповые чаты, пантеон OmO, аватары, MCP
personas/memory) — прочитай [docs/architecture/personas.md](docs/architecture/personas.md).**

## Десктопный агент (Services/Desktop, за флагом `desktop-agent`)

Руки песочницы на машине пользователя: MCP-сервер `desktop` плюс WPF-клиент, за флагом `desktop-agent`.

Инварианты и подробности — [backend/ClaudeHomeServer/Services/Desktop/CLAUDE.md](backend/ClaudeHomeServer/Services/Desktop/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Механики OmO в чатах

Тексты — переводы oh-my-openagent ([docs/omo/adoption.md](docs/omo/adoption.md)); рантайм —
`Services/Prompts/OmoPrompts*.cs` (генерируются скриптом docs/omo/gen-omo-prompts.ps1).
Главное — цикл «до готово» (флаг `work-loop`): тумблер в композере, протокол маркера
`<promise>ГОТОВО</promise>`, автопродолжение хода до маркера/лимита, затем верификационный
ход. Детали — [docs/architecture/features.md](docs/architecture/features.md), раздел «Механики OmO».

## REST API

Все эндпоинты (кроме `/api/auth/ping`) и SignalR-хаб — под `[Authorize]` (API-ключ);
`ping` дополнительно под rate-limit (`Auth:PingRateLimit`, дефолт 10/мин на IP).
Полный справочник эндпоинтов — [docs/architecture/api.md](docs/architecture/api.md) (источник правды — контроллеры).
Значения фич-флагов фронт получает из `GET /api/auth/me` (поле `featureFlags`).
Удалённый доступ — [docs/operations/remote-access.md](docs/operations/remote-access.md).

## Выкатка на бой из веб-морды (`Services/Deploy`)

Пункт меню «Выкатить на бой» — сигнал трей-раннеру именованным событием Windows (не путать с выкаткой прода из чата, ADR-010).

Инварианты и подробности — [backend/ClaudeHomeServer/Services/Deploy/CLAUDE.md](backend/ClaudeHomeServer/Services/Deploy/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## SignalR Hub `/hubs/session`

Клиент вызывает: `JoinSession`, `LeaveSession`, `SendMessage`, `RespondPermission`, `Interrupt`
Сервер шлёт событие `message` с объектом `ServerMessage` (поле `type`).

## Observability (OpenTelemetry)

OpenTelemetry в двух режимах: dev → Aspire Dashboard, prod → SigNoz; алерты приезжают в уведомления, инциденты разбираются детерминированным кодом.

Инварианты и подробности — [backend/ClaudeHomeServer/Telemetry/CLAUDE.md](backend/ClaudeHomeServer/Telemetry/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Реализовано

Ядро: auth по API-ключу, проекты, сессии, чат (вложения/голос/режимы ⚡📋❓), файловый
менеджер с diff/revert, empty states.

Поверх ядра: виджеты в чате (sandbox-iframe + строгая CSP), артефакты сессии, продуктовая
история «Что нового» (AI-сводка коммитов по дням), плагин oh-my-claudecode, задачи v3
(напоминания, регулярные, web push, Claude/персона-исполнитель), бэкапы каталога `data`
(расписание + `exe --backup/--restore/--inspect`, меню трея, виджет на главной), панель
«Документация» (корпус md с деревом, оглавлением, поиском, обратными ссылками и типами
документов со свойствами), панель «Сервисы» (дев-серверы проекта в iframe через прокси
`/preview/**` — **ключи и эндпоинты остались `preview`**, переименована только подпись),
ридер ссылок (панель «Чтение»: `POST /api/reader/read` и `GET /api/reader/image` под общим
SsrfGuard — [ADR-005](docs/adr/ADR-005-link-reader-server.md)).

**Голосовой режим чата** (`Session.VoiceMode`, одна кнопка `AudioLines` в композере — на месте
«Отправить» при пустом поле): ответ приходит коротким и читается вслух. Формат держат ДВЕ точки
промпта — секция `voice-mode` и оговорка последним блоком слоя персоны (без неё слот «Формат
ответов» персоны перебивает правило, слой клеится после секций). **Стилей два**
(`Session.VoiceStyle`): `talk` — всё вышеописанное; `digest` — ответ
обычный и полный, а вслух идёт выжимка из блока `<voice>` в его КОНЦЕ (в начале она тянула бы
ответ за собой — авторегрессия). Стиль НЕ настраивается: выводится из ширины экрана
(`voiceStyleFor`), на разговор без рук не влияет — петля работает везде; сервер хранит
последнее выставленное значение для выбора секции промпта. Маркер вырезается безусловно и в
речи, и в ленте — но только ВНЕ блоков кода и остаётся в транскрипте (иначе `--resume` теряет
пример формата, а ответ с маркером в примере обрывался бы на экране). Тот же блок просят и
БЕЗ озвучки у длинных ответов (`LongAnswerSectionText`): плашка «Коротко» в ленте + кнопка
разового чтения. Озвучка — `POST /api/tts`
(Yandex SpeechKit API v3, конфиг `Yandex:SpeechKit`, роль сервисного аккаунта
`ai.speechkit-tts.user`); коды 503 `not_configured` / 502 `upstream` — контракт фолбэка на голос
браузера, менять осознанно. **Деньги Яндекса считаются сами** (источник трат `tts`, рубли в
`SpendRecord.CostRub` отдельно от долларов): v3 тарифицируется ЗА ЗАПРОС, счётчик берёт
принятые Яндексом запросы — включая ушедшие до обрыва неудавшегося синтеза. Разбивки по
услугам Billing API не отдаёт в принципе, он умеет только остаток на счёте
(`GET /api/yandex/account`, IAM-токен из ключа сервисного аккаунта, роль
`billing.accounts.viewer` НА БИЛЛИНГ-АККАУНТЕ, баланс — только админу). **Чей голос — одна точка, `VoiceResolver`** (голос персоны → голос
инстанса): фронт шлёт `personaId`, владельца проверяет сервер, а любое кривое значение молча
вырождается в дефолт — отказ увёл бы на голос браузера остаток фразы. Ход читается одним
голосом (захват при старте хода: пакеты синтезируются заранее). Голос персоны выбирается в её
карточке (блок «Голос»: список с прослушиванием, амплуа, темп, подбор моделью — место
`persona-voice`); белый список из 16 имён и их амплуа — `TtsVoiceCatalog`, наружу идут
канонические имена. Прослушивание — ОТДЕЛЬНЫЙ путь без фолбэка на голос браузера (в форме
выбора голоса он был бы ложью) и под запретом, пока озвучивается ответ.
**Кто говорит — видно:** пока звучит ответ, у аватара её реплики расходятся кольца цветом
персоны и тем же цветом светится сияние композера; источник один (`activeSpeaker` в
ChatPanel, гейт по фазе озвучки), чужую реплику подсветка не трогает.
Та же кнопка запускает **режим разговора** (hands-free): петля
«сказал → пауза 1–2.8 с по хвосту фразы (`pendingDelayFor`) → отправка → ход → ответ вслух →
снова слушаю», автомат чистым редьюсером
в `hooks/useHandsFree.ts`. Инвариант петли — РАСПОЗНАВАНИЕ и озвучка не пересекаются никогда
(эхо): Web Speech открыт только в фазах слушания, фаза озвучки ведётся в `ChatPanel` с токеном
вызова, `speak()` резолвится по реальному концу звука на ОБОИХ путях (сервер и голос браузера).
Под озвучкой дополнительно слушает VAD-канал (`lib/bargeVad.ts`) — только ради перебивания
(двухступенчато: приглушить → оборвать), на WebKit/iOS выключен
([voice-barge-in.md](docs/features/voice-barge-in.md)).
Во время хода в углу композера только «Стоп» (прерывает ход, оставаясь в разговоре).
Разговору можно назначить **локального исполнителя** (место `chat-voice` → «Локальная»):
ходы идут прямым HTTP-вызовом локального движка (Ollama или llama-server, см.
`LocalLlm:Provider`) мимо claude CLI **потоком** (куски по границе предложения — озвучка
стартует до конца ответа), фронт не меняется.
Подробности — раздел «Голосовой режим чата» в [features.md](docs/architecture/features.md).

**Архив чатов** — прячет чат, а не удаляет: история, метаданные и `claudeSessionId` целы,
возврат одной кнопкой. Признак ПРОИЗВОДНЫЙ: `IsArchived = ArchivedAt != null && UpdatedAt <=
ArchivedAt`, без `UnarchivedAt` и без мутатора «снять архив» — любая активность возвращает
чат сама; **архивация и возврат не двигают `UpdatedAt`**. Один эндпоинт
`PUT /api/chats/{id}/archived` (409 при живом ходе/фоновых агентах), событие ленты
`chat_archived`. При архивации транскрипт копируется в `data/archived-transcripts`
(десктопным чатам нельзя), при возврате целевой путь резолвится НА МОМЕНТ ВОЗВРАТА.
Карточка архивного чата в списке обычная, действия — в контекстном меню: «Вернуть из
архива» и «Сохранить в заметки». Сводка чата (место `chat-digest`, `POST
/api/chats/{id}/digest`, кэш с инвалидацией активностью) на сервере ЖИВА, но из UI не
вызывается — показывать её после уборки подвала негде. Автоправило
«убирать чаты без активности N дней» — за флагом `chat-auto-archive`: пачка с
`ArchiveBatchId` и откатом ровно одного прохода; порог посферный (проект/вне проектов), а
накопившиеся залежи разгребает само сохранение порога — отдельной кнопки прохода нет.
Не путать: архив ≠ временный чат (тот удаляется по сроку) ≠ чип
«Завершён» (бывш. «Готово», ключ `done` — чаты выполненных задач). Известное ограничение:
`MigrateProviderAsync` про архивные копии транскриптов не знает и даст 400 — учить не надо,
случай редкий. Подробности — раздел «Архив чатов» в [features.md](docs/architecture/features.md).

**Контекстные замечания к плану и разворот схемой** (флаг `visual-plan`): замечание к плану
оставляется прямо на разделе и уходит планировщику с адресом `(заголовок, номер вхождения)` —
пара, а не строка: в планах встречаются по два раздела «Тесты»/«Дизайн»/«Файлы», и по одному
тексту они склеились бы в одну группу. Замечания эфемерны (живут от клика до отправки), текст
плана первичен, карта производна; разворот схемой строится дешёвым one-shot’ом `plan-map` по
кнопке (по умолчанию облачный исполнитель, `CheapProfile.Large`, `DefaultLocal: false`),
кэш по SHA-256 текста, потолок 5 блоков с флагами внимания. Отправка идёт существующим
контрактом карточки плана «отклонить с комментарием» — отдельного эндпоинта для замечаний
нет, и при накопленных замечаниях главная кнопка становится «доработка», а не одобрение.
Подробности — раздел «Контекстные замечания к плану и разворот схемой» в
[features.md](docs/architecture/features.md).

**Серверные сторожа чатов**: `watch_start` декларирует «дождись условия и разбуди этот
чат» — цикл опроса (тик 5 с, стор `data/watchdogs.json`) живёт в бэкенде и переживает
ходы, рестарты и смерть процесса CLI, тогда как Monitor и `run_in_background` харнесса
умирают вместе с процессом. Терминальное событие будит чат одним системным ходом
(`preempt: false` — встаёт в очередь), промежуточных нет. Тулсет `watch` — http-only,
stdio-ветки отката нет. Присутствие сторожей видно в UI без поллинга (снимок
`GET /api/watchdogs` + событие `watchdogs_changed`, стор `lib/watchdogPresence.ts`):
значок и живой статус в карточке чата, точки на рельсе проектов, кнопках «Чаты» и стене.
Фактура смертей мониторов и решение —
[ADR-013](docs/adr/ADR-013-server-chat-watchdogs.md), раздел «Серверные сторожа чатов» в
[features.md](docs/architecture/features.md).

Детали каждой фичи — [docs/architecture/features.md](docs/architecture/features.md).

## Фич-флаги (feature toggles)

Dark launch: фича коммитится выключенной и включается per-user в меню «Экспериментальные
функции». Реестр (source of truth) — в коде: `FeatureFlagCatalog.All`
([Models/FeatureFlag.cs](backend/ClaudeHomeServer/Models/FeatureFlag.cs)); хранение —
override в `data/users.json`; фронт — стор [lib/featureFlags.ts](frontend/src/lib/featureFlags.ts),
хук `useFeature(FLAGS.key)`. Большинство старых флажных фич включены безусловно
(2026-08); в каталоге **восемь флагов**: `workspace-destructive` (постоянный предохранитель от
необратимого удаления), `change-dossiers-recall` (история решений по коду — подсказки
персонам и выгрузка отдельной веткой, [ADR-004](docs/adr/ADR-004-change-dossiers.md)),
`desktop-agent` (руки на машине пользователя: тип чата «Десктопный», тумблер грани в
проекте, канал устройств — см. раздел «Десктопный агент»), `specialty-prompt-sections`
(настраиваемые секции промпта и типовые умения по специальности персоны),
`chat-auto-archive` (автоправило архива чатов; ручной архив, режим «Архивные» в списке
чатов — отдельного раздела нет — и сводка карточки работают без флага — см. раздел
«Архив чатов»), `mcp-catalog` (поиск MCP-серверов по официальному реестру и
предзаполнение формы — см. раздел «Личный реестр MCP-серверов»), `visual-plan`
(контекстные замечания к плану и разворот схемой) и `chat-context` (материалы —
файл/ссылка/задача — закрепляются за чатом явной кнопкой: полоса вкладок у чата плюс
тул `context_list`; инварианты — раздел «Контекст чата» в
[features.md](docs/architecture/features.md)).
Пометки «за флагом …» в доках — исторические; актуальный состав — в коде каталога.

Работают безусловно, без тумблера (флаги сняты 2026-08-21): **ассистент по умолчанию и
знакомство** — заготовка персоны заводится при первом входе, знакомство приходит
приглашением, а не обязательным экраном; проектное знакомство v2 раскладывает каркас папок
и правил по подтверждению карточкой в ленте
([docs/architecture/onboarding-intro.md](docs/architecture/onboarding-intro.md),
[docs/features/project-onboarding-v2.md](docs/features/project-onboarding-v2.md));
**фон проекта** — рисунок и цвет подбираются моделью по смыслу проекта, контракт генерации
без разметки и форма хранения — [ADR-008](docs/adr/ADR-008-project-background-generation.md),
тексты интерфейса — [docs/features/project-backgrounds.md](docs/features/project-backgrounds.md);
**карточка доклада о завершённой задаче** в чате постановщика вместе с новым промптом
реакции — [docs/features/task-completion-report.md](docs/features/task-completion-report.md);
**серверные сторожа чатов** (флаг снят 2026-09-01, через две недели после dark launch) —
см. раздел выше и [ADR-013](docs/adr/ADR-013-server-chat-watchdogs.md).

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
| `designer` | ревью UI-изменений по [docs/design/guidelines.md](docs/design/guidelines.md); запускается только по явному согласию — перед коммитом заметного UI его предлагают, а не вызывают молча |

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

Полная версия с разбором граблей — [docs/architecture/conventions.md](docs/architecture/conventions.md).
Главное:

- **ВАЖНО: CI гоняет тесты на Linux** (`ubuntu-latest`), а разработка идёт на Windows — тесты
  обязаны быть платформонезависимыми. Две ловушки: **пути** (строить от `Path.GetTempPath()` +
  `Path.Combine`, Windows-литералы на Linux считаются относительными) и **тайминги** (раннер
  слабее, ThreadPool голодает — ждать **событие** через `TaskCompletionSource` +
  `Task.WhenAny`, а не `Task.Delay`).
- **Категории тестов.** Большинство — юниты 1–50ms. Медленные: Controllers
  (`WebApplicationFactory`), `GitServiceTests` (`[Trait("Category", "Slow")]`), интеграционные MCP.
  Отдельно `[Trait("Category", "Dns")]` — тестам нужен настоящий резолв внешних имён, и на машине
  с Proxifier они валятся пачкой (среда, не регрессия): локально гоняй
  `dotnet test --filter "Category!=Dns"`. Фоновый автосейв `SessionManager` (внутри него sweep)
  в тестах выключен ключом `Session:AutoSaveSeconds = 0` — иначе фон меняет статусы между
  ассертами. На итеративную правку — `dotnet test --filter "FullyQualifiedName~<Набор>"`;
  полный прогон — перед коммитом/PR.
- **Одна папка — один проект на владельца** (`ProjectManager.EnsureRootFree`, 400 при повторе):
  датасет Dify ключуется по `RootPath`. У разных владельцев общая папка допустима.
- **Удаление чата уносит и транскрипт claude CLI** во всех профилях. Инвариант: только файл с
  точным именем `{csid}.jsonl`, никогда по маске и никогда сама папка (один `~/.claude` делят
  все инстансы плюс интерактивные сессии пользователя). `resumeSessionId` валидируется белым
  списком `^[A-Za-z0-9_-]{1,128}$` — иначе `".."` снёс бы всю папку `data`.
- **Настройки чата не двигают `UpdatedAt`** (по нему идут сортировка, секции дерева и
  непрочитанность); срок временного чата считается от `Session.ExpiryAnchor`.
- **Архивация и возврат не двигают `UpdatedAt`** (инвариант архива чатов): признак архива
  производный (`IsArchived = ArchivedAt != null && UpdatedAt <= ArchivedAt`), поэтому любая
  активность возвращает чат из архива сама, а возврат не всплывает наверх списка и не метит
  чат непрочитанным.
- **Новое хранилище → сверься с бэкапом.** Всё в `data/` попадает в архив по умолчанию:
  кеш/логи — в исключения, секреты — в `BackupPaths.SecretFileNames`, сторы вне `data/` — в
  `BackupCore.CopyDataTo`, не-JSON стор — свой способ снимка, критичный стор — в
  `BackupValidation.Validate`. Ломающее изменение формата = инкремент `BackupSchema.Version`.
- **HTTP-клиент к опциональной зависимости — через `AddQuietHttpClient`**
  ([QuietHttpLogger.cs](backend/ClaudeHomeServer.Core/Services/Http/QuietHttpLogger.cs)): дефолтный
  логгер печатает каждый провал как Error со стектрейсом и забивает консоль.
- Path traversal защита: `FileService.SafeJoin` — все пути через неё.
- Хранилище проектов — `data/projects.json`; метаданные сессий — `data/sessions.json`, история
  чата — `data/sessions/{claudeSessionId}/history.json`, resume через `--resume`.
- Комментарии в коде по-русски.

## Коммиты

- **Conventional Commits**: `type(scope): описание` (feat/fix/perf/docs/refactor/build/chore/ci/test/style).
- **Язык сообщений — русский** (в отличие от общего дефолта на английском).
- Трейлер `Co-Authored-By: <модель> <noreply@<домен-вендора>>` — где `<модель>` это
  та, что реально делала коммит (напр. «Claude Opus 4.8», «GLM 5.2»), а не фиксированная
  версия. Домен noreply берётся по вендору модели: Anthropic → `noreply@anthropic.com`,
  ZhipuAI (GLM) → `noreply@z.ai`. Без «Claude» в начале, если модель не от Anthropic.
- Атомарность: одно логическое изменение — один коммит.
- `commit`/`push` — только по явной просьбе.
- **Исключение — цепочка задач-исполнителей в общем worktree** (задача трекера с явно
  заданным `worktreePath`): исполнитель коммитит СВОИ файлы этой задачи локально сам,
  атомарно, сразу как её собственные критерии (тесты + review-consilium) зелёные — не
  дожидаясь отдельной просьбы. `push`, PR и мерж в `master` — по-прежнему только по
  явной просьбе. Причина: untracked-файлы не защищены ни git reflog, ни `fsck` — окно
  между «работа готова» и «закоммичена» в общем worktree равно окну необратимой потери
  (инцидент 2026-09-01: полностью отревьюенная работа пропала бесследно между хопами
  задач).
