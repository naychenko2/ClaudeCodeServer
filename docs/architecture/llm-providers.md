# LLM-провайдеры (Services/Llm)

> Подробная документация подсистемы. Выжимка и инварианты — в [CLAUDE.md](../../CLAUDE.md),
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
- **Слоты моделей и таблица назначений** (уровень 2 и 3 диалога «Поставщики моделей»).
  Слоты **двухуровневые**: личный per-user слот (`User.ModelTierStrong|Medium|Weak`) поверх
  глобального инстанса (`AppSettings.ModelTierStrong|Medium|Weak`,
  `AppSettingsService.TierModel(tier)`; пустой слот → null, решает CLI). Таблица назначений
  мест — **глобальная** (админ решает, каким слотом идёт каждое место), но слот в ней
  разрешается в модель **по владельцу действия** через
  `UserModelTierResolver.ModelFor(tier, ownerId)` — единственную точку склейки личного и
  глобального слота (её вызывают и `ModelAssignmentResolver`, и `CheapTextRunner`; дублировать
  её логику нельзя). Каждое место применения — запись `LocalActionCatalog`; выбор админа
  живёт в `LocalActionOverridesStore` значением `tier:strong|medium|weak` | id модели |
  `local` (легаси `claude`/`default` читаются как средняя). Дефолт записи — `Tier` или
  профиль сложности (`LocalActionCatalog.EffectiveDefaultTier`: Small/Text → слабая,
  Large → средняя).
  - **Агентные места** (`Agentic: true`, группа «Чаты и персоны»): `chat-new`, `chat-persona`,
    `tasks-executor`, `subagent-consultant`, `modules-llm`. Резолвит
    `ModelAssignmentResolver.Resolve(usageKey, explicitModel, ownerId)`: явная модель → назначение
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
  - **Уровень у задачи и персоны.** Кроме назначения места, слот можно попросить точечно:
    `TaskItem.ModelTier` (ставит постановщик — обычно персона через MCP `tasks_create`) и
    `Persona.ModelTier`. Цепочку выбирает `TaskExecutionService.ResolveExecutorModel`:
    уровень задачи → конкретная `Persona.Model` → уровень персоны → null (место решает само).
    Уровень едет в `SessionManager.CreateAsync` маркером `tier:*`, который разворачивает
    `ModelAssignmentResolver.Resolve` (склейка слотов не дублируется — она в
    `UserModelTierResolver`). В `Session.Model` оседает уже конкретная модель, поэтому маркер
    не попадает ни в `--model`, ни на wire; пустой слот — как будто уровень не задавали.
    **Приоритет намеренный:** уровень задачи сильнее явной `Persona.Model` — задача более
    частный контекст, чем персона, и постановщик вправе поднять уровень под конкретную
    работу. Побочный эффект принят: персона, настроенная на стороннего провайдера, в такой
    задаче уедет на модель Claude-слота.
  - **Где уровень персоны применяется.** Везде, где персона получает модель, — через
    `ModelAssignmentResolver.PersonaModel(persona, ownerId)` (своя модель → уровень → null):
    чат с персоной и групповой чат (`SessionManager.CreatePersonaChatAsync` /
    `CreateGroupChatAsync`), назначение персоны в существующий чат (`SwitchSpeaker`),
    разовый вопрос (`PersonaAskService`) и пин тира в frontmatter файла сабагента
    (`PersonaAgentFileSync`). `ownerId` во всех точках резолвится по владельцу действия
    (в сессиях — только `SessionManager.ResolveOwnerId`: у проектной сессии `Session.OwnerId`
    равен null, владелец живёт у проекта, иначе личный слот подменялся бы глобальным).
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
  deepseek-usage.json читается; точки чужой валюты отбрасываются — смена ряда
  USD→% у Kimi) — попап контекст-бейджа шапки чата + вкладка
  провайдера на экране «Использование». Источники по ключу `Balance`:
  `deepseek`/`moonshot`/`openrouter` — деньги; `glm` — квота Coding Plan
  (%, 5-ч окно); `kimi` — квота Kimi for Coding, `GET {ApiBaseUrl}/usages`
  (недокументир.: основное — самое короткое окно из `limits[]` = 5-ч, недельное
  `usage` уезжает в `Secondary*`-поля `ProviderBalance`; числа приходят строками).
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

## Пул подписок Claude и опрос usage

`ClaudeSubscriptionPool` (секция `ClaudeSubscriptions`) — несколько аккаунтов Claude на
одном сервере: новые чаты роутятся на аккаунт с высшим тарифом среди «в ротации»
(утилизация 5h-окна ниже `SoftThreshold`, дефолт 0.8), при равенстве — наименее
загруженный; исчерпанные (`rejected`/100% без overage) выведены до `resetsAt`
(`MarkExhausted`/`IsExhausted`). Пустой пул (ни одной подписки в конфиге) = локальный вход
`~/.claude/.credentials.json` под ключом `claude` (`PrimaryKey`).

- **Исчерпание считается только по известным окнам** — `ClaudeSubscriptionPool.IsExhaustionWindow`
  (белый список `five_hour`, `seven_day`) — и это ЕДИНСТВЕННАЯ точка проверки для всех трёх
  мест, которые правят состояние пула: обработчик `rate_limit_event` в `SessionManager`,
  `SubscriptionUsageWarmupService.RecordAndGuard`, `ClaudeSubscriptionPool.RestoreFromSnapshots`.
  Наблюдаемые в проде типы событий: `five_hour` и `seven_day` — базовые окна подписки;
  `seven_day_overage_included` — **транзитное**, приходит и со `status="rejected"`, хотя ходы
  на том же аккаунте продолжают проходить через полминуты (по нему НЕ маркируем);
  `seven_day_opus`/`sonnet`/… и `extra_usage` — из OAuth-опроса, всегда `allowed`. Статусы:
  `allowed`, `allowed_warning` (рабочий, не отказ — просто окно близко к краю), `rejected`.
  Неизвестное окно пишется в `UsageService` для экрана, но пул не трогает ни в какую сторону
  (ни бан, ни снятие бана). Инцидент 2026-08-02: одно `seven_day_overage_included` +
  `rejected` вывело живой аккаунт из ротации на пять суток и воскресало на каждом рестарте
  из снапшотов.
- **Самолечение на ходу** — `rate_limit_event` известного окна со `status != "rejected"` и
  утилизацией < 1 снимает пометку исчерпания с аккаунта чата (`pool.Reset`), зеркало
  `RecordAndGuard`: прошедший ход — сильнейший сигнал жизни. Идл-пинг такие аккаунты не
  проверяет (у них есть активность), поэтому без этого ложная пометка висела до `resetsAt`.
  Компромисс: `allowed` по 5h-окну снимет пометку и при реально выбранном недельном —
  следующий ход тут же перемаркирует.
- **Все аккаунты исчерпаны** — `Pick` берёт не случайного из наименее загруженных (при
  `rejected` CLI утилизацию не присылает, у всех выходит 0), а того, чьё окно сбросится
  раньше; сроки в пределах минуты считаются равными и решаются загрузкой.
- **Три источника снимков `UsageSnapshot`** (поле `Source`; подпись «источник · возраст»
  на вкладке аккаунта экрана «Использование», `UsageSnapshot.source` на фронте):
  `turn` — `rate_limit_event` живого хода чата (`SessionManager`); `probe` — идл-пинг
  простаивающего аккаунта (ниже); `oauth` — периодический опрос
  `SubscriptionOAuthUsageService`. `null` — снимки до появления поля (обратная
  совместимость со старым `usage.json`).
- **Идл-пинг** (`SubscriptionUsageWarmupService` + `SubscriptionActivityTracker`) —
  доступность/ротация держится на пробных ходах `--model haiku` по ПРОСТАИВАЮЩИМ
  аккаунтам, а не на протухающих OAuth access-токенах. `SubscriptionActivityTracker.Touch`
  отмечает момент последней ФАКТИЧЕСКОЙ активности по ключу — живой ход (`SessionManager`
  на `rate_limit_event`) или сама ПОПЫТКА пинга (не успех — иначе сбойный аккаунт
  долбился бы каждую минуту вместо раза в порог); OAuth-снимки таймер простоя не
  сбрасывают (поллер best-effort и не значит, что аккаунтом реально пользуются сейчас).
  Тик раз в минуту отбирает простаивающих дольше `ClaudeSubscriptions:IdlePingMinutes`
  (дефолт 5, `0` — механизм выключен), плюс стартовый прогрев по всем аккаунтам
  (`WarmupOnStartup`, дефолт true) и таймаут одного пробинга `WarmupTimeoutMs`
  (дефолт 60000 мс). Заменяет прежний периодический переопрос ВСЕХ выведенных аккаунтов
  (ключ `RecheckIntervalMinutes` — убран, код его больше не читает).
- **`SubscriptionOAuthUsageService`** — не про доступность/ротацию, а про точные проценты
  ВСЕХ окон разом (5h/7d/per-model/перерасход с временем сброса), в отличие от
  `rate_limit_event`, который приходит только по факту хода или пинга: опрос
  `GET api.anthropic.com/api/oauth/usage` раз в `ClaudeUsage:PollMinutes` (fallback —
  легаси `ClaudeSubscriptions:UsagePollMinutes`, дефолт 10), рефреш протухших
  access-токенов профилей по refresh-токену, backoff по 429. Роль в связке с идл-пингом
  не изменилась (best-effort источник, идл-таймер не трогает) — уточнилась только с
  появлением поля `Source`. Статус опроса per-аккаунт (`ok`/`unauthorized`/`error`) —
  блок `pollStatuses` в `/api/usage`.
- **`LoginCommandFor(key)`** — готовая PowerShell-команда
  `$env:CLAUDE_CONFIG_DIR = "…"; claude login` в изолированный профиль аккаунта пула
  (`{ProfilesDir}/sub-{key}`), поле `loginCommand` у `SubscriptionUsage` в `/api/usage`.
  `null` — у аккаунта нет файлового профиля, куда логин имел бы смысл (primary НЕ в
  пуле, а его токен реально берётся из env/конфига — вход в файл профиля их не
  перекроет). Отдаётся всегда, не только при `unauthorized` — фронт сам решает, когда
  показать кнопку копирования. Экран «Использование»: плашка «нужен claude login» на
  вкладке аккаунта показывает команду моноширинным блоком с кнопкой «Скопировать»
  (текст меняется на «Скопировано» на 1.5 с, паттерн как в чате).

## Бесплатные модели для фоновых задач (Ollama + OpenRouter)

Фоновые one-shot задачи (классификация, извлечение JSON, теги, суммаризация, память —
НЕ чаты) можно считать бесплатно вместо платного Claude — **тремя** исполнителями по
цепочке деградации: локальная Ollama, бесплатная модель OpenRouter (прямой HTTP-адаптер),
и как последний рубеж — claude CLI. Ollama идёт прямым HTTP (`OllamaClient.GenerateTextAsync`,
`/api/chat`, `think:false`), OpenRouter — прямым HTTP (`CloudCheapClient`, OpenAI-совместимый
`/chat/completions`), оба мимо claude CLI (старт CLI ~15с убил бы смысл «быстро и часто»).
Маршрутизация — per-action, исполнителя выбирает админ:
- **Каталог** — [LocalActionCatalog.cs](../../backend/ClaudeHomeServer/Services/Llm/LocalActionCatalog.cs):
  все фоновые действия (ключ, группа, профиль вызова small/text/large, `DefaultLocal` —
  рекомендация). **changelog** («Что нового») входит — идёт через `RunDetailedAsync` (сохраняет
  usage/стоимость на claude-пути; на бесплатной модели usage=null, стоимость 0). НЕ входят:
  задача-исполнитель (агентная сессия, не one-shot), fal.ai (картинки), persona-ask (нужен
  `effort` персоны — всегда claude).
- **Бесплатные модели OpenRouter** — КУРИРУЕМЫЙ короткий список в конфиге (не полный `/models`:
  там 300+ моделей, много мусора и перегруженных upstream-провайдером): **агентские** для чата —
  `LlmProviders:openrouter:Models` (обычный путь провайдера через claude CLI); **для прямого
  адаптера** [CloudCheapClient.cs](../../backend/ClaudeHomeServer/Services/Llm/CloudCheapClient.cs)
  (HTTP, только фоновые) — `OpenRouter:DirectModels`, `ModelCatalogService.AppendOpenRouterDirect`
  добавляет их с префиксом `direct:` и `provider=openrouter-direct`. Два транспорта различаются
  в маршруте префиксом `direct:` (модель без него — через провайдер/CLI, с ним — через адаптер).
  ВАЖНО: у `:free` лимит 20 запросов/мин и 50/сутки на аккаунт (1000/сутки после разовой покупки
  кредитов на $10), плюс **upstream rate-limit провайдера модели** (429 посреди стрима — модель
  показывает thinking, но text не доходит; в чате выглядит как «висит»). Потому в список включены
  только проверенные на стабильный streaming (Nemotron 3 Ultra/Super, Laguna S 2.1, North Mini
  Code; Gemma/Muse Spark исключены как нестабильные). В агентском ModelPicker (чат/сессия/персона)
  `direct:`-модели СКРЫТЫ (проп `includeDirect`) — там нужны агентские вызовы.
- **Роутер** — [LocalActionRouter.cs](../../backend/ClaudeHomeServer/Services/Llm/LocalActionRouter.cs):
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
  Профиль (`num_ctx`/`num_predict`/таймауты) — из каталога, переопределяется `Ollama:Profiles`.
  `num_ctx` важен: дефолт Ollama (~4k) молча режет большой вход.
  **Таймаутов в профиле два — по маршруту** (`TimeoutMsFor(key)` роутера): `TimeoutMs` — локальный,
  калибровался под Ollama; `CloudTimeoutMs` — для облачных шагов (выбранная модель через CLI,
  `direct:`-адаптер, финальный claude), заметно больше (Small 120 с / Text 180 с / Large 300 с):
  облачная сильная модель на сложной задаче отвечает дольше локали, и локальный потолок её
  обрывал (прод 2026-08-04: планировщик «Командной реализации»).
- **Цепочка исполнения** (одна для всех действий): **выбранное → локальная модель → claude**.
  Выбранная модель с префиксом `direct:` идёт через `CloudCheapClient` (прямой HTTP-адаптер),
  без префикса — через провайдер (claude CLI). Шаг считается неудавшимся при исключении/429/
  пустом ответе (адаптер на 429 бесплатной модели тихо отдаёт null), шаг локали — при недоступности
  Ollama. **Последний шаг без страховки**: отказ claude уходит наверх исключением, и потребитель
  деградирует как раньше. Единственная страховка — **один повтор при таймауте**
  (`LlmTimeoutException` из `OneShotClaudeRunner`): обрыв по времени не приговор, повтор дешевле
  потерянной работы человека сверху; прочие ошибки и внешняя отмена ct повторяются не будут.
  Отмену `CancellationToken` по цепочке НЕ фолбэчим — это не сбой модели.
  При `Kind=Claude` шаг локали пропускается, иначе выбор «Claude» не отличался бы от «локаль».
- **Выбор админа** — [LocalActionOverridesStore.cs](../../backend/ClaudeHomeServer/Services/Llm/LocalActionOverridesStore.cs):
  `data/local-actions.json` (путь от `DataPath`), значение — `"local"` | `"claude"` | id модели;
  снимок в неизменяемом словаре заменяется целиком при записи. Старый формат (`bool`: true=локаль,
  false=claude) мигрируется при чтении. Роутер — singleton, но читает стор на каждом вызове,
  поэтому выбор действует **сразу, без рестарта**. API — `PUT|DELETE /api/admin/local-actions/{key}`
  (`[Authorize(Roles = "admin")]`); таблица назначений глобальная (не per-user) — но слот в
  ней разрешается в модель по владельцу действия (см. выше). PUT валидирует
  модель по `ModelCatalogService` и настроенности провайдера — опечатка в id иначе всплыла бы
  только при первом фоновом вызове.
- **Раннер** — [CheapTextRunner.cs](../../backend/ClaudeHomeServer/Services/Llm/CheapTextRunner.cs)
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
  [ModelProvidersModal.tsx](../../frontend/src/components/ModelProvidersModal.tsx) («Поставщики
  моделей», пункт меню профиля, только `cc_role=admin`): уровень 2 «Три модели» (слоты
  сильная/средняя/слабая), уровень 3 «Кто что выполняет» — строки мест по разделам, в дропдауне
  сверху три слота, затем «Локальная» (скрыта у агентных мест), затем полный `ModelPicker`
  (у агентных — без `direct:`-моделей); у переопределённых — кнопка сброса к конфигу.
  Применяется на лету (оптимистично, с откатом). Пункт «По умолчанию» в пикерах чата/персон
  подписывается моделью СВОЕГО места — фронт берёт резолвнутые назначения из `GET /api/models`
  (поле `assignments`), слоты и оверрайды он не знает.
