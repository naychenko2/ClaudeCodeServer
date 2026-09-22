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

Изоляция local-процессов по памяти (`Execution:Isolation`, systemd-scope в `ccs-agents.slice`):
узлы MSBuild и компилятор переиспользуются ВНУТРИ scope (соль — имя юнита), scope гасится
по выходу процесса; прежние запреты реюза (`MSBUILDDISABLENODEREUSE` и др.) — за
`BuildNodeReuse=false`. Гашение висит на событии `Exited` и умирает вместе с бэкендом,
поэтому scope упавшего инстанса подметает `ScopeOrphanSweeper` при старте: имя юнита несёт
отпечаток владельца (`ccs-run-<pid в hex>-<guid>.scope`), гасятся только те, чей владелец
мёртв. Свежий worktree чата/задачи прогревается фоновой сборкой тестов
(`Execution:WarmupBuild`).

Пределы памяти заданы **per-scope**, а число одновременных scope ограничивает отдельный
потолок `BuildConcurrencyGate` (`Execution:Isolation:MaxConcurrentBuilds`, дефолт 2, явный
`0` — без ограничения): инцидент oomd 2026-09-21, `ccs-agents.slice` держал 18,1 GB в четырёх
прогонах разом. Счётчик **единственный на процесс** — второй семафор где-то ещё делает потолок
неправдой. Под него идут только spec с явной меткой `ProcessSpec.Heavy` (сегодня — прогрев);
`git status`/`diff` проходят насквозь, очередь из них парализовала бы git-бар. Сборку, которую
агент запускает ВНУТРИ хода своим Bash, потолок не видит — она потомок процесса claude CLI, а
не отдельный запуск: ход слот не занимает и не ждёт его никогда, поэтому дедлок «ход ждёт слот
прогрева» невозможен по конструкции.

**Основная защита от OOM — не гейт, а жёсткий потолок cgroup на `ccs-agents.slice`**
(`MemoryMax` + `MemorySwapMax=0` + `ManagedOOMMemoryPressure=kill`, плюс `ManagedOOMPreference=omit`
на самом `ccs.service`). Лимит cgroup наследуется ВСЕМУ поддереву scope — включая сборки,
которые агент запускает внутри хода своим Bash, то есть ровно те, что дали инцидент 21 сен
(четыре scope по 4–5 GB, внутри `testhost.dll`, прогревов среди них не было). `BuildConcurrencyGate`
закрывает только запуски с меткой `ProcessSpec.Heavy` — сегодня это один прогрев worktree, —
и потому остаётся дополнением, а не заменой лимитов. **`MemoryHigh` не ставим ни на slice, ни
per-scope** (разбор 2026-09-22): дроссель гонит сборку в reclaim и swap, стойло считается
PSI-давлением и суммируется вверх до `user@1000.service`, где systemd-oomd по дефолту Ubuntu
убивает при 50 % — жертвой выходит наш же scope. После установки `MemoryHigh` 21.09 oomd убил
ещё четыре scope за вечер, счётчик `memory.events high` на slice — 1,9 млн. Сессионный порог
поднят до 80 % root-drop-in'ом на `user@.service`; число узлов MSBuild для любой сборки под
`backend/` (в том числе из Bash агента) режет `backend/Directory.Build.rsp` (`-maxcpucount:6`):
20 тестовых проектов на 24 ядрах давали до 20 testhost разом. Unit-файлы и drop-in'ы
версионированы в [deploy/systemd/](deploy/systemd/): user-часть раскладывает
`install-user-units.sh` (он же чистит перебивающие drop-in'ы из `user.control/`), root-часть
(oomd на `user@.service`, лимиты inotify — бэкенд выедал 61 тыс. из 65 тыс. watch'ей по
дефолту) — `install-system-tuning.sh`. Сами значения машинно-специфичны — правило расчёта
под конкретную машину в [deploy/systemd/README.md](deploy/systemd/README.md).

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

## LLM-провайдеры (ClaudeHomeServer.Llm)

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
- **`BareMode` + `SystemPromptFile`** в `LlmProviderConfig` — отключают ТОЛЬКО автозагрузку
  CLAUDE.md проекта; хуки, LSP, плагины и авто-память остаются живыми. Флага `--bare` в
  аргументах CLI больше нет (снят 2026-09-15, `BuildBareModeArgs` его не собирает) — вместо
  него `RunTurnAsync` при реально применённом BareMode ставит переменную окружения
  `CLAUDE_CODE_DISABLE_CLAUDE_MDS=1`. Переменная недокументирована в CLI (её нет в
  `claude --help`) и может пропасть/переименоваться при обновлении — сторож на аномальный
  размер входа на первом ходу уже стоит в `ClaudeSession` (`_bareModeWatchdogFired`). Порог
  **производный, а не константа**: ожидаемая база = карта (байты/3) + фиксированные
  `BareModeFixedOverheadTokens` (тулсеты и MCP) + текст хода, тревога — при превышении базы
  на `BareModeAlarmMarginTokens`. Прежняя константа 15 000 подбиралась под карту в ~2 КБ и
  протухла вместе с её ростом: 58 ложных тревог за 16–21.09. CLI получает ТОЛЬКО короткую
  карту через `--system-prompt-file`. Для локальных моделей с малым окном это снижает вход
  с ~95 000 до ~16 000 токенов (замер 2026-09-21 по первым ходам `local-qwen`: 15 802–18 091
  при карте 13,7 КБ; прежние ~2 400 — замер 2026-09-05, когда карта была на порядок меньше).
  Файл карты (`backend/ClaudeHomeServer/SystemPrompts/CLAUDE-local.md`,
  несколько КБ) поставляется с продуктом. **Цепочка резолва** per-project:
  `<корень проекта>/docs/CLAUDE-local.md` (приоритет, потолок 16 КБ — выше
  отступаем к серверной) → серверный дефолт от `AppContext.BaseDirectory` (прокинут
  через `LlmSessionContext.ContentRootPath`) → оба флага снимаются с warning. Резолв от
  `AppContext.BaseDirectory` — лечение ревью 2026-09-05: иначе в любом чужом проекте ход
  падает с exit=1. Если файл не найден — оба флага снимаются с warning в stderr
  («SystemPromptFile не найден»), ход идёт обычным путём (CLI сам подтянет CLAUDE.md
  проекта); SafeJoin ловится, ход не валится (тоже с warning). Хуки серверных сессий гасит
  отдельный, не связанный с BareMode механизм — `ClaudeRuntimeSettings.HooksOffArgs`
  (`--settings` с `disableAllHooks`) — только на Windows-хосте, где хуки плагинов
  открывают мелькающие окна консоли; на Linux хуки живые. Состав
  MCP-инструментов стабилен в пределах сессии (`McpToolsetStabilityTests`). **У
  container-владельцев карта живёт на паре, которую обязаны держать синхронно:** bind-mount
  `<BaseDirectory>/SystemPrompts:/app/SystemPrompts` (`SandboxManager`, факт наличия каталога
  входит в `ConfigHash`) и одноимённое правило `DockerPathMapper` — расхождение рантайм не
  ловит, оно даёт exit=1 и ложный `Unreachable` у каждого container-владельца; связаны тестом
  в `DockerPathMapperTests`. Тесты — `ClaudeSessionBareArgsTests`.
- **`RecallInTurnText`** в `LlmProviderConfig` — нестабильные секции промпта (recall заметок
  и памяти, досье, привязки персоны, граф кода) доставляются хвостом хода, вклейкой в его
  текст, а не системным блоком. Причина: prefix cache движка ищет совпадение от начала
  контекста и обрывается на первом изменившемся байте, а эти секции пересчитываются под
  текст КАЖДОГО хода и стоят в середине системного блока — то есть обнуляют кэш всей
  истории следом за собой (замер по снимкам промптов прода 2026-09-16: у чатов локальной
  модели совпадало 8 356 символов из 17 274, обрыв на `recall-notes` в 22 случаях из 38).
  Хвост кэш не рвёт: текст хода и так новый. Неизменившаяся склейка повторно НЕ шлётся —
  она уже в транскрипте и доедет по `--resume` (`ClaudeSession._lastTurnRecallHash`,
  память процесса). Точка решения одна — локальная `Add` в `RunTurnAsync`: при включённой
  ручке `stable: false` уходит в буфер с `Kind="turn"`, а `Combine` берёт только `"system"`.
  Дефолт false, включено только у `local-qwen`: у облачных своя механика (`cache_control`).
  Сторож — `ClaudeSessionPromptSectionsOrderTests`.
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

**Перед правками в `ClaudeHomeServer.Llm/` — прочитай
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

Инварианты и подробности — [backend/ClaudeHomeServer.Video/CLAUDE.md](backend/ClaudeHomeServer.Video/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Значок проекта (Services/ProjectIcons)

Иконка проекта — **не картинка**: модель по названию проекта отдаёт имя иконки из белого
списка lucide (`LucideGlyphs`, серверная копия полного набора установленного lucide-react),
разметки от модели не приходит никогда. Подбор двухходовый («меню вместо памяти»): ход 1 —
слова-понятия, сервер отбирает по ним реальные имена, ход 2 — выбор из этого короткого меню;
ноль годных — ровно один повтор с перечислением отбракованных, без цикла. Место модели —
`project-icon` в `LocalActionCatalog`, любой сбой молча оставляет инициалы. Контракт ответа,
схема ходов, белый список и форма хранения — [ADR-009](docs/adr/ADR-009-project-icon-glyph.md);
тексты интерфейса — [docs/features/project-icon-glyphs.md](docs/features/project-icon-glyphs.md).

## Уборка карты проекта (Services/Docs)

Кнопка в настройках проекта проверяет корневой `CLAUDE.md`: размер, длинные секции, мёртвые
ссылки, вложенные карты — и предлагает, что прибрать. Две фазы: детерминированный сканер
(факты, без модели и без трат) и формулировки модели **по отчёту сканера, а не по самому
файлу** — карта в окно не влезает. Механические правки формирует сканер: из ответа модели
читаются ровно `id`, `severity`, `modelSays`, остальных полей в схеме разбора нет вовсе.
Запись — только по явной отметке человека; **вынос секции кнопкой не делается никогда**
(цена ошибки — потеря знания, оплаченного инцидентами), регулярной автоматики тоже нет (нет
критерия «знание не потеряно»). Разбор ```-заборов — один примитив `MarkdownFence` в Core,
новый построчный разбор markdown обязан звать его. За флагом `project-map-hygiene`
(`Default: false`). Подробности —
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

Серверы, рождённые сразу в Kestrel (stdio-ветки отката нет вовсе, при негодном для http
адресе или выключенном рубильнике ходу не объявляются): `watch` (сторожа чатов, ADR-013)
и **`websearch`** — веб-поиск для чатов, заведён ради локальной модели: под BareMode CLI
получает явный allow-list `--tools`, в который `WebSearch` и `WebFetch` не входят. Два
инструмента, сознательно без третьего:
`web_search` — запрос в Perplexity Sonar с ответом и ЦИТАТАМИ-ссылками (ключ живёт только
в секции `Perplexity` appsettings и наружу не уезжает; замер 2026-09-06 показал, что через
egress-прокси доступен только Perplexity, а google/bing/brave/SearXNG — нет, поэтому готовые
MCP веб-поиска у нас молча не работают), `web_read` — чтение страницы существующим
`ReaderService` вместе с его SsrfGuard и пер-владельческой квотой (ADR-005). Пустой
`Perplexity:ApiKey` = сервера у хода нет (единственный рубильник, схемы окно не занимают);
трата пишется источником `websearch` в токенах, деньги — только если в конфиге проставлены
цены плана. Локальным профилям по умолчанию НЕ выдан: включается ключом `websearch`
в `KeepMcpServers`.

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

## Десктопный агент (ClaudeHomeServer.Desktop, за флагом `desktop-agent`)

Руки песочницы на машине пользователя: MCP-сервер `desktop` плюс WPF-клиент, за флагом `desktop-agent`.

Инварианты и подробности — [backend/ClaudeHomeServer.Desktop/CLAUDE.md](backend/ClaudeHomeServer.Desktop/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Механики OmO в чатах

Тексты — переводы oh-my-openagent ([docs/omo/adoption.md](docs/omo/adoption.md)); рантайм —
`Services/Prompts/OmoPrompts*.cs` (генерируются скриптом docs/omo/gen-omo-prompts.ps1).
Главное — цикл «до готово» (флаг `work-loop`): тумблер в композере, протокол маркера
`<promise>ГОТОВО</promise>`, автопродолжение хода до маркера/лимита, затем верификационный
ход. Детали — [docs/architecture/features.md](docs/architecture/features.md), раздел «Механики OmO».

## REST API

Все эндпоинты (кроме `/api/auth/login`) и SignalR-хаб — под `[Authorize]`; схема —
**JWT Bearer**, токен выдаёт `POST /api/auth/login` по паре `{ username, password }`.
Вход дополнительно под rate-limit (политика `auth-login`, ключ `Auth:LoginRateLimit`,
дефолт 10/мин, партиция по адресу клиента; отказ — 429 с `Retry-After`).
Полный справочник эндпоинтов — [docs/architecture/api.md](docs/architecture/api.md) (источник правды — контроллеры).
Значения фич-флагов фронт получает из `GET /api/auth/me` (поле `featureFlags`).
Удалённый доступ — [docs/operations/remote-access.md](docs/operations/remote-access.md).

## Выкатка на бой из веб-морды (`Services/Deploy`)

Пункт меню «Выкатить на бой» — сигнал трей-раннеру именованным событием Windows (не путать с выкаткой прода из чата, ADR-010).

Инварианты и подробности — [backend/ClaudeHomeServer/Services/Deploy/CLAUDE.md](backend/ClaudeHomeServer/Services/Deploy/CLAUDE.md): файл подхватывается сам при работе с этой папкой; при правках со стороны фронтенда открой его руками.

## Питание машины из веб-морды (`Services/Power`)

Пункт меню аватара «Питание компьютера» гасит, перезагружает или усыпляет машину, на которой
крутится продукт: она стоит дома, а ходят в неё снаружи. Команду отдаёт САМ бэкенд
(`shutdown /s|/r /f /t 0`, сон — `SetSuspendState` из powrprof.dll за швом `IPowerActions`,
Linux-CI об эти вызовы не спотыкается), трей-раннер тут ни при чём — кнопка работает и при
мёртвом трее.

Замков три и они независимы: `[Authorize(Roles = "admin")]`, `PowerControl:Enabled` (false по
умолчанию, живёт в машинном `appsettings.Local.json` вне git) и платформа (не Windows —
`available: false`, пункта нет). Скрывать пункт в UI без серверной проверки нельзя: веб-морда
торчит наружу.

**Отсчёт ведёт сервер, а не `shutdown /t`.** Так все три действия отменяются одинаково (у сна
встроенной отсрочки нет вовсе), отмена не гоняется с `shutdown /a` за право первой дойти до
системы, а обратный отсчёт видят ВСЕ окна, а не только то, из которого нажали. Цена — отсчёт не
переживает смерть процесса, и это ровно нужное поведение: упавший бэкенд не должен гасить машину
«по памяти». Отсрочка (`DelaySeconds`, дефолт 60) — единственное окно, в которое можно
передумать: пункт меню задевается промахом, а разбудить погашенную машину из соседней комнаты
уже невозможно. Ключ `/f` там же по необходимости: несохранённый документ иначе остановил бы
завершение работы экраном «программа не даёт завершить работу», а человека у монитора нет —
тихий отказ хуже потерянного черновика.

## SignalR Hub `/hubs/session`

Клиент вызывает: `JoinSession`, `LeaveSession`, `SendMessage`, `RespondPermission`, `Interrupt`
Сервер шлёт событие `message` с объектом `ServerMessage` (поле `type`).

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
([Models/FeatureFlag.cs](backend/ClaudeHomeServer.Core/Models/FeatureFlag.cs)); хранение —
override в `data/users.json`; фронт — стор [lib/featureFlags.ts](frontend/src/lib/featureFlags.ts),
хук `useFeature(FLAGS.key)`. Большинство старых флажных фич включены безусловно
(2026-08); в каталоге **десять флагов**: `workspace-destructive` (постоянный предохранитель от
необратимого удаления), `change-dossiers-recall` (история решений по коду — подсказки
персонам и выгрузка отдельной веткой, [ADR-004](docs/adr/ADR-004-change-dossiers.md)),
`desktop-agent` (руки на машине пользователя: тип чата «Десктопный», тумблер грани в
проекте, канал устройств — см. раздел «Десктопный агент»), `specialty-prompt-sections`
(настраиваемые секции промпта и типовые умения по специальности персоны),
`chat-auto-archive` (автоправило архива чатов; ручной архив, режим «Архивные» в списке
чатов — отдельного раздела нет — и сводка карточки работают без флага — см. раздел
«Архив чатов»), `mcp-catalog` (поиск MCP-серверов по официальному реестру и
предзаполнение формы — см. раздел «Личный реестр MCP-серверов»), `visual-plan`
(контекстные замечания к плану и разворот схемой), `chat-context` (материалы —
файл/ссылка/задача — закрепляются за чатом явной кнопкой: полоса вкладок у чата плюс
тул `context_list`; инварианты — раздел «Контекст чата» в
[features.md](docs/architecture/features.md)), `chat-branch` (новый чат с копией истории
оригинала до выбранного шага) и `project-map-hygiene` (уборка карты проекта: секция в
настройках, модалка с фактами сканера и вход «Прибраться» из снимка промпта — тумблер
закрывает ОБА входа, `review`/`apply` под ним отвечают 404; см. раздел «Уборка карты
проекта»).
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
см. раздел выше и [ADR-013](docs/adr/ADR-013-server-chat-watchdogs.md);
**встроенная интеграция Higgsfield** (флаг снят 2026-09-08: OAuth-токен продлевается сам,
повторный вход не нужен) — вход живёт в разделе «MCP-серверы», доставка сервера в ход идёт
по записи реестра (рубильник `Enabled` + RO-гейт + живой токен). Доступный человеку
предохранитель — кнопка **«Выйти»** в карточке: `Logout` чистит токены, `EnsureFresh`
возвращает null, сервер снимается с хода с WARN. Рубильник `Enabled` из UI не
переключается (`McpServerList` рисует `Toggle` только для своих записей).

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
- Path traversal защита: примитив спины `SafePath.Join`
  ([SafePath.cs](backend/ClaudeHomeServer.Core/Services/SafePath.cs)) — все пути через неё.
  `FileService.SafeJoin`/`SafeJoinPublic` остались тонкими форвардерами (десятки вызывающих,
  переименование ничего не дало бы). Из вертикали зови Core-примитив напрямую: обращение
  через `FileService` — ссылка на чужую вертикаль, сторож границ её ловит.
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
