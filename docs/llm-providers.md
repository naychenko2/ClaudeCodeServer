# LLM-провайдеры (Services/Llm)

> Подробная документация подсистемы. Выжимка и инварианты — в [CLAUDE.md](../CLAUDE.md),
> раздел «LLM-провайдеры». Читать перед правками в `Services/Llm/`, конфиге `LlmProviders`,
> фоновых one-shot действиях и всём, что касается запуска claude CLI.

Единственный рантайм — claude CLI (`Llm/Claude/ClaudeSession`). Сторонние провайдеры
с Anthropic-совместимым эндпоинтом (DeepSeek, GLM) подключаются env-оверрайдами
процесса на каждый ход: `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`,
`ANTHROPIC_MODEL`/`ANTHROPIC_DEFAULT_OPUS|SONNET_MODEL` (= модель сессии),
`ANTHROPIC_DEFAULT_HAIKU_MODEL`/`CLAUDE_CODE_SUBAGENT_MODEL` (= `SmallModel`),
плюс `ExtraEnv` провайдера (у GLM — `API_TIMEOUT_MS`). Весь функционал CLI (скиллы,
субагенты, workflow, план, compact, MCP, permissions, resume) работает одинаково
у всех провайдеров.

- **Конфиг** — секция `LlmProviders` (словарь key → провайдер): `DisplayName`,
  `AnthropicBaseUrl` (для CLI), `ApiBaseUrl` (нативный API — баланс, GET /models),
  `ApiKey` (в appsettings.Local.json; пустой = провайдер выключен и модели скрыты),
  `SmallModel`, `Balance` (вид источника баланса: `deepseek` = GET /user/balance;
  пусто — без баланса, как у GLM), `QueryModelsApi`, `SupportsImages`, `Models`
  (Id/DisplayName/ContextWindow/цены $ за 1M — по ним считается стоимость хода).
- **`LlmProviderRegistry`** — резолв провайдера из `Session.Model` (по каталогу
  моделей, затем по префиксу ключа; провайдер не персистится), `CapabilitiesFor`,
  `BuildCliEnv`, `ComputeCost` (на стороннем эндпоинте total_cost_usd от CLI
  считается по ценам Anthropic — пересчитываем по ценам конфига; без цен — null).
- **Guard**: смена провайдера у начатой сессии — 400 (транскрипт живёт у эндпоинта).
- **Слоты моделей и таблица назначений** (уровень 2 и 3 диалога «Поставщики моделей»,
  админские и глобальные). Инстанс держит три именованные модели —
  `AppSettings.ModelTierStrong|Medium|Weak` (`AppSettingsService.TierModel(tier)`; пустой слот
  → null, решает CLI). Каждое место применения — запись `LocalActionCatalog`; выбор админа
  живёт в `LocalActionOverridesStore` значением `tier:strong|medium|weak` | id модели |
  `local` (легаси `claude`/`default` читаются как средняя). Дефолт записи — `Tier` или
  профиль сложности (`LocalActionCatalog.EffectiveDefaultTier`: Small/Text → слабая,
  Large → средняя).
  - **Агентные места** (`Agentic: true`, группа «Чаты и персоны»): `chat-new`, `chat-persona`,
    `tasks-executor`, `subagent-consultant`, `modules-llm`. Резолвит
    `ModelAssignmentResolver.Resolve(usageKey, explicitModel)`: явная модель → назначение
    админа → слот каталога → null. Локаль и `direct:`-модели им непригодны (нужны
    инструменты CLI) — отклоняются в админском API и игнорируются при резолве. Пресеты
    автоподбора агентные места не трогают.
  - **Точки применения**: `SessionManager.ResolveDefaultModel` (создание чата/чата персоны/
    группового; ключ места — `UsageKeyFor`: исполнитель задач → персона → чат; кроме resume,
    у него своя модель и провайдер), `PersonaAskService`, `PersonaAgentFileSync` (пин тира
    в frontmatter сабагента) и **шлюз на границе запуска процесса** —
    `ClaudeSession.EffectiveModel` (ключ места из сессии) и `OneShotClaudeRunner.ResolveModel`
    (генеричный one-shot без контекста места → слот «средняя»).
  - Шлюз резолвит модель ДО `BuildCliEnv`, иначе модель стороннего провайдера из слота уехала
    бы на эндпоинт Anthropic; `NormalizeModel` — ПОСЛЕ, чтобы слот на провайдер без ключа
    деградировал в дефолт CLI, а не ронял вызов. В сессии значение НЕ фиксируется:
    `Model = null` резолвится на каждом ходу, поэтому смена настройки подхватывается без
    пересоздания чата. У персон и групповых чатов пустая `Persona.Model` наследует назначение —
    включая назначение персоны в неначатый чат (`SwitchSpeaker` не затирает подставленную
    модель). Пины пантеона OmO — явные модели, они сильнее назначений.
  - Миграция v1→v2: одиночная `DefaultChatModel` из `app-settings.json` при загрузке переезжает
    в слот «средняя» и очищается (одноразово, `AppSettingsService.Load`).
- **Профили CLI** — `data/claude-profiles/{key}` (CLAUDE_CONFIG_DIR): изоляция от
  OAuth-логина ~/.claude (иначе CLI шлёт провайдеру токен подписки → 401); туда же
  докладываются общие настройки пользователя по белому списку (CLAUDE.md,
  settings.json, rules/skills/agents/commands; креденшалы — никогда), источник —
  `ClaudeUserProfileDir` (дефолт ~/.claude), троттлинг 5 мин.
- **Иммунитет к системному окружению** — маршрут CLI задаёт только сервер. На КАЖДОМ запуске
  claude (ход чата, one-shot, каталог моделей) из унаследованного окружения выбрасываются
  `ANTHROPIC_BASE_URL/AUTH_TOKEN/API_KEY/MODEL/DEFAULT_*`, `CLAUDE_CONFIG_DIR`,
  `CLAUDE_CODE_SUBAGENT_MODEL`, `CLAUDE_CODE_AUTO_COMPACT_WINDOW`
  (`LlmProviderRegistry.ProviderEnvKeys` → `ProcessSpec.ClearEnv` → `Remove` в
  `LocalProcessRunner` ДО применения `Env`, так что оверрайд провайдера всегда сильнее).
  Иначе глобально заданная переменная (мастер-рубильник «весь Claude Code на GLM», забытый
  `setx`) молча увела бы чат «на Claude» к чужому эндпоинту с токеном подписки. Выброс
  пишется в лог по разу на ключ; `CLAUDE_CODE_OAUTH_TOKEN` не трогается (на нём вход по
  подписке); вернуть наследование — `Claude:InheritSystemEnv=true`. В docker-среде
  вычищать нечего: окружение контейнера собирается с нуля и уезжает через `-e`.
- **Баланс** — `ProviderBalanceService`, `GET /api/providers/{key}/balance|usage`
  (кэш 5 мин; снапшоты 8 дней в data/provider-usage-{key}.json, legacy
  deepseek-usage.json читается) — попап контекст-бейджа шапки чата + вкладка
  провайдера на экране «Использование».
- **Каталог моделей** — `ModelCatalogService`: записи `Models` конфига + при
  `QueryModelsApi` опрос `GET {ApiBaseUrl}/models` (новые модели с дефолтами).

Возможности провайдера (`LlmCapabilities`: displayName/plan/compact/mcp/effort/images/…)
отдаются фронту в блоке `providers` из `GET /api/models` и в `session_started`;
у CLI-провайдеров всё как у Claude, кроме `SupportsImages` (из конфига; DeepSeek — false).
UI скрывает недоступное (`useModelCaps` в `lib/models.ts`), брендинг (assistantName,
плашка стоимости/баланса, группы ModelPicker) — по `displayName`. Общие хелперы:
`TurnFileWatcher` (file_changed на время хода), `AttachmentInliner` (инлайн вложений),
`TasksServerLocator`. Модель Claude-исполнителя задач — `Tasks:ExecutorModel`; AI-генерация
описания/подзадач — `Tasks:AiModel`; сводки «Что нового» — `Changelog:Model` (везде
валидна модель любого провайдера: one-shot идёт через claude --print с теми же env).
Локальные one-shot — с `--safe-mode` (CLI 2.1.169+): юзерские кастомизации ~/.claude
(CLAUDE.md, скиллы, плагины, хуки) не грузятся в контекст — минус ~половина входных
токенов на вызов; CLAUDE_CONFIG_DIR память НЕ отсекает, а `--bare` ломает OAuth.
Плюс **`--no-session-persistence` во ВСЕХ средах** (работает только с `--print` — ровно
режим one-shot): транскрипт одноразового вызова мертв с рождения (`--resume` по нему никто
не делает), а CLI писал по `.jsonl` на вызов — замер дал ~287 файлов за сутки на одном
инстансе, и лежали они до плановой уборки CLI (~30 дней). Ходов чата это НЕ касается: там
транскрипт и есть память разговора. Состав флагов собирает `OneShotClaudeRunner.BuildArgs`
(вынесен из запуска, чтобы покрываться тестом — CLI валидирует аргументы и падает с кодом 1
на незнакомом, роняя разом все фоновые задачи). На такой отказ есть **авто-деградация**:
`LooksLikeUnknownSessionFlag` распознает «unknown option --no-session-persistence» в выводе,
и раннер повторяет вызов без флага, запомнив это до конца жизни процесса (образ песочницы
собирается отдельно от хоста и может нести CLI старее — работающие фоновые задачи важнее
экономии файлов). Ручной дублер — `Claude:PersistOneShotSessions=true`.

## Бесплатные модели для фоновых задач (Ollama + OpenRouter)

Фоновые one-shot задачи (классификация, извлечение JSON, теги, суммаризация, память —
НЕ чаты) можно считать бесплатно вместо платного Claude — **тремя** исполнителями по
цепочке деградации: локальная Ollama, бесплатная модель OpenRouter (прямой HTTP-адаптер),
и как последний рубеж — claude CLI. Ollama идёт прямым HTTP (`OllamaClient.GenerateTextAsync`,
`/api/chat`, `think:false`), OpenRouter — прямым HTTP (`CloudCheapClient`, OpenAI-совместимый
`/chat/completions`), оба мимо claude CLI (старт CLI ~15с убил бы смысл «быстро и часто»).
Маршрутизация — per-action, исполнителя выбирает админ:
- **Каталог** — [LocalActionCatalog.cs](../backend/ClaudeHomeServer/Services/Llm/LocalActionCatalog.cs):
  все фоновые действия (ключ, группа, профиль вызова small/text/large, `DefaultLocal` —
  рекомендация). **changelog** («Что нового») входит — идёт через `RunDetailedAsync` (сохраняет
  usage/стоимость на claude-пути; на бесплатной модели usage=null, стоимость 0). НЕ входят:
  задача-исполнитель (агентная сессия, не one-shot), fal.ai (картинки), persona-ask (нужен
  `effort` персоны — всегда claude).
- **Бесплатные модели OpenRouter** — КУРИРУЕМЫЙ короткий список в конфиге (не полный `/models`:
  там 300+ моделей, много мусора и перегруженных upstream-провайдером): **агентские** для чата —
  `LlmProviders:openrouter:Models` (обычный путь провайдера через claude CLI); **для прямого
  адаптера** [CloudCheapClient.cs](../backend/ClaudeHomeServer/Services/Llm/CloudCheapClient.cs)
  (HTTP, только фоновые) — `OpenRouter:DirectModels`, `ModelCatalogService.AppendOpenRouterDirect`
  добавляет их с префиксом `direct:` и `provider=openrouter-direct`. Два транспорта различаются
  в маршруте префиксом `direct:` (модель без него — через провайдер/CLI, с ним — через адаптер).
  ВАЖНО: у `:free` лимит 20 запросов/мин и 50/сутки на аккаунт (1000/сутки после разовой покупки
  кредитов на $10), плюс **upstream rate-limit провайдера модели** (429 посреди стрима — модель
  показывает thinking, но text не доходит; в чате выглядит как «висит»). Потому в список включены
  только проверенные на стабильный streaming (Nemotron 3 Ultra/Super, Laguna S 2.1, North Mini
  Code; Gemma/Muse Spark исключены как нестабильные). В агентском ModelPicker (чат/сессия/персона)
  `direct:`-модели СКРЫТЫ (проп `includeDirect`) — там нужны агентские вызовы.
- **Роутер** — [LocalActionRouter.cs](../backend/ClaudeHomeServer/Services/Llm/LocalActionRouter.cs):
  `Resolve(key)` → `ActionRoute(Kind, Model, Source, Tier)`, где `Kind` — исполнитель ПЕРВОГО шага
  (`Local` | `Claude` | `Tier` со слотом | `Model` c id конкретной модели провайдера), а приоритет
  источников — **выбор админа → `Ollama:Actions` конфига → `DefaultLocal` каталога** (политика A —
  при настроенном Ollama рекомендованные действия начинаются с локали).
  **Дефолт для остальных — `Tier` со слотом по профилю сложности** (Small/Text → слабая,
  Large → средняя): слово «по умолчанию» означает одни и те же три модели в чатах и в фоне.
  Зашитая в потребителе модель действия (обычно `"haiku"`) при этом не исчезает — она фолбэк
  на случай пустого слота (`CheapTextRunner.EffectiveFallback`), чтобы ненастроенный инстанс
  не гонял теги и заголовки на дорогой модели.
  `Source` (`default|config|admin`) — по нему UI показывает, что переопределено, и даёт сброс.
  `UsesLocal(key)` = `Ollama.Enabled && Kind == Local` (нужен `RunLocalOnlyAsync` и ранжиру).
  Профиль (`num_ctx`/`num_predict`/timeout) — из каталога, переопределяется `Ollama:Profiles`.
  `num_ctx` важен: дефолт Ollama (~4k) молча режет большой вход.
- **Цепочка исполнения** (одна для всех действий): **выбранное → локальная модель → claude**.
  Выбранная модель с префиксом `direct:` идёт через `CloudCheapClient` (прямой HTTP-адаптер),
  без префикса — через провайдер (claude CLI). Шаг считается неудавшимся при исключении/429/
  пустом ответе (адаптер на 429 бесплатной модели тихо отдаёт null), шаг локали — при недоступности
  Ollama. **Последний шаг без страховки**: отказ claude уходит наверх исключением, и потребитель
  деградирует как раньше. Отмену `CancellationToken` по цепочке НЕ фолбэчим — это не сбой модели.
  При `Kind=Claude` шаг локали пропускается, иначе выбор «Claude» не отличался бы от «локаль».
- **Выбор админа** — [LocalActionOverridesStore.cs](../backend/ClaudeHomeServer/Services/Llm/LocalActionOverridesStore.cs):
  `data/local-actions.json` (путь от `DataPath`), значение — `"local"` | `"claude"` | id модели;
  снимок в неизменяемом словаре заменяется целиком при записи. Старый формат (`bool`: true=локаль,
  false=claude) мигрируется при чтении. Роутер — singleton, но читает стор на каждом вызове,
  поэтому выбор действует **сразу, без рестарта**. API — `PUT|DELETE /api/admin/local-actions/{key}`
  (`[Authorize(Roles = "admin")]`); настройка глобальная, поэтому не per-user. PUT валидирует
  модель по `ModelCatalogService` и настроенности провайдера — опечатка в id иначе всплыла бы
  только при первом фоновом вызове.
- **Раннер** — [CheapTextRunner.cs](../backend/ClaudeHomeServer/Services/Llm/CheapTextRunner.cs)
  (`ICheapTextRunner.RunAsync(actionKey, prompt, fallbackModel?, ownerId?, jsonFormat?)` +
  `RunDetailedAsync(...)` для changelog — тот же маршрут, но с usage и override таймаута/лимита):
  локаль по профилю; `jsonFormat` (обычно строка `"json"`) уводит локальный путь в
  `OllamaClient.ChatJsonAsync` — без него мелкая модель оборачивает JSON прозой, парсер падает
  и действие всё равно уходит в фолбэк; `direct:`-маршрут → `CloudCheapClient.GenerateTextAsync`
  (прямой HTTP); при недоступности/ошибке/429/пустом ответе — **фолбэк дальше по цепочке до
  `OneShotClaudeRunner`**. Ollama и OpenRouter выключены → сразу claude (нулевая регрессия).
  Потребители (NotesAiService, ChatTaskExtractionService, MemoryWriteResolver, TaskAiService,
  SessionSummaryService, GitAiService, Skill*Service, Persona/TeamMemory autolearn+consolidate,
  DailyBriefingService, OllamaActionRankService, ChangelogService) передают свой `actionKey` —
  разбирают ответ теми же парсерами, что и раньше ответ claude.
- **Конфиг** — секция `Ollama` (`Model`, опц. `TextModel`, `BaseUrl`, `KeepAlive`,
  `Actions` — словарь ключ→bool, `Profiles`); секция `OpenRouter` (`Provider`, `AgenticMinContext`,
  `DirectMinContext`) + провайдер `LlmProviders:openrouter` (ключ/эндпоинт). Пустой `Ollama:Model`
  = локаль выключена; ненастроенный провайдер openrouter = облачный адаптер выключен.
- **UI** — вкладка «Локально» на экране «Использование» показывает ТОЛЬКО локальную модель Ollama
  (какая, адрес, сколько действий на ней). Слоты и назначения — в админском диалоге
  [ModelProvidersModal.tsx](../frontend/src/components/ModelProvidersModal.tsx) («Поставщики
  моделей», пункт меню профиля, только `cc_role=admin`): уровень 2 «Три модели» (слоты
  сильная/средняя/слабая), уровень 3 «Кто что выполняет» — строки мест по разделам, в дропдауне
  сверху три слота, затем «Локальная» (скрыта у агентных мест), затем полный `ModelPicker`
  (у агентных — без `direct:`-моделей); у переопределённых — кнопка сброса к конфигу.
  Применяется на лету (оптимистично, с откатом). Пункт «По умолчанию» в пикерах чата/персон
  подписывается моделью СВОЕГО места — фронт берёт резолвнутые назначения из `GET /api/models`
  (поле `assignments`), слоты и оверрайды он не знает.
