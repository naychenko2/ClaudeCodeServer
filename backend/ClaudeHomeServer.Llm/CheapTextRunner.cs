namespace ClaudeHomeServer.Services.Llm;

// Реализация ICheapTextRunner (контракт и DTO живут в Core, чтобы вертикали могли
// зависеть от шва без ProjectReference на Main).
// namedEngines — реестр именованных движков (LocalEngines:*): нужен прогону на названный
// движок (RunOnLocalEngineDetailedAsync / IsLocalEngineReadyAsync) — там исполнитель задан
// аргументом, и маршрут места его не выбирает — и подписи движка в списке исполнителей
// (LocalExecutorsFor). executorsStore — стор списка исполнителей места
// (data/local-action-executors.json): единственный источник этого списка (место без
// строки в нём идёт по своему маршруту, как до фичи). Оба параметра опциональны, как у
// LocalActionRouter: в DI — синглтоны, в тестах передаются руками.
public sealed class CheapTextRunner(
    LocalActionRouter router, ILocalLlmClient ollama, CloudCheapClient cloud, IOneShotRunner claude,
    ILogger<CheapTextRunner> log, ITierModelResolver? appSettings = null,
    UserModelTierResolver? userTiers = null, ModelAssignmentResolver? assignment = null,
    EngineCallLedger? ledger = null, NamedLocalEngines? namedEngines = null,
    LocalActionExecutorsStore? executorsStore = null)
    : ICheapTextRunner
{
    public bool UsesLocal(string actionKey) => router.UsesLocal(actionKey);

    // Место с картинками на входе текстовым раннером не ходит никогда: текстовая модель
    // «разметила» бы фото вслепую (ADR-027 §2). Пишем warning и сообщаем вызывающему.
    private bool RefuseImageAction(string actionKey)
    {
        if (LocalActionCatalog.Find(actionKey)?.Input != ActionInput.Images) return false;
        log.LogWarning(
            "cheap-runner: действие {Action} принимает картинки — текстовый раннер его не выполняет",
            actionKey);
        return true;
    }

    // Маршрут действия для текстового раннера. face-pose — не LLM-маршрут (ракурс считает
    // local-media, а не этот раннер): если он всё же пришёл (стор правили руками), идём по
    // дефолтному слоту места и пишем warning, как с битой ссылкой на пресет.
    private ActionRoute ResolveRoute(string actionKey)
    {
        var route = router.Resolve(actionKey);
        if (route.Kind != RouteKind.FacePose) return route;
        log.LogWarning(
            "cheap-runner: действие {Action} получило маршрут face-pose — он не для текстового раннера, иду по дефолтному слоту места",
            actionKey);
        var action = LocalActionCatalog.Find(actionKey);
        return new ActionRoute(RouteKind.Tier, null, route.Source,
            action is null ? ModelTier.Medium : LocalActionCatalog.EffectiveDefaultTier(action));
    }

    // Та же проверка, что и первые два шага RunFreeAsync: прямой адаптер → локаль.
    // Не вызывает — нужно только знать «пройдёт ли», не тратить деньги/время.
    // Условия (см. CheapTextRunner.RunFreeAsync):
    //   • direct-модель агрератора — CloudCheapClient.IsDirectRoute;
    //   • локаль — LocalStepApplies (Kind=Local ИЛИ Kind=Model при DefaultLocal=true)
    //     И живой ILocalLlmClient.
    // Занятость движка ходом сюда НЕ входит намеренно: это вопрос «настроен ли бесплатный
    // путь вообще», а не «свободен ли он в эту секунду». Иначе возможность места мигала бы
    // в UI у каждого хода на локали.
    public bool HasFreeRoute(string actionKey)
    {
        var route = ResolveRoute(actionKey);

        if (route.Kind == RouteKind.Model && !string.IsNullOrWhiteSpace(route.Model)
            && CloudCheapClient.IsDirectRoute(route.Model))
        {
            return true;
        }

        if (route.Engine is not null) return router.EngineOf(route)?.Enabled == true;
        return LocalStepApplies(actionKey, route.Kind) && ollama.Enabled;
    }

    // Исполнимый ли маршрут по фактическому выбору: локаль — только с настроенным движком,
    // слот/модель/claude — всегда (исполнитель есть). FacePose — не текстовый раннер.
    public bool HasRoute(string actionKey)
    {
        var route = ResolveRoute(actionKey);
        return route.Kind switch
        {
            RouteKind.Local => route.Engine is null ? ollama.Enabled : router.EngineOf(route)?.Enabled == true,
            RouteKind.FacePose => false,
            _ => true,
        };
    }

    // Описание маршрута для лога/события: kind + конкретная модель или слот, если это
    // слот/модель. claude/local — без модели (дефолты маршрута). Пример: «model=nemotron:free»,
    // «tier=strong», «claude», «local». Используется из TeamPlanningService — там, где
    // раньше лог показывал только «не ответил».
    public string DescribeRoute(string actionKey, string? fallbackModel)
    {
        var route = ResolveRoute(actionKey);
        return route.Kind switch
        {
            RouteKind.Model when !string.IsNullOrWhiteSpace(route.Model) =>
                CloudCheapClient.IsDirectRoute(route.Model)
                    ? $"direct:{CloudCheapClient.StripPrefix(route.Model!)}"
                    : $"model={route.Model}",
            RouteKind.Tier => $"tier={route.Tier?.ToString().ToLowerInvariant() ?? "medium"}",
            RouteKind.Local => route.Engine is null ? "local" : $"local:{route.Engine}",
            _ => string.IsNullOrWhiteSpace(fallbackModel) ? "claude" : $"claude({fallbackModel})",
        };
    }

    // Таймаут ОБЛАЧНЫХ шагов цепочки (выбранная модель через CLI, direct-адаптер,
    // финальный claude): эффективный пер-местный лимит (каталог → профиль), а не
    // локальный TimeoutMs — тот калибровался под Ollama и облачную сильную модель
    // на сложной задаче обрывал.
    private TimeSpan CloudTimeoutFor(string actionKey) =>
        TimeSpan.FromMilliseconds(router.CloudTimeoutMsFor(actionKey));

    // Потолок вывода для ОБЛАЧНЫХ шагов (direct-адаптер, RunDetailedAsync). Локальный
    // NumPredict — это num_predict Ollama (бережёт память GPU), для облачного маршрута он
    // мал: модель обрезала крупный JSON-план на полуслове, симптом неотличим от таймаута
    // (прод 2026-08-05). Лимит — от роутера: пер-местное значение каталога, если место его
    // задало, иначе профильный CloudNumPredict — единственная точка склейки, не дублируем
    // здесь выбор «каталог → профиль».
    internal int CloudNumPredictFor(string actionKey) =>
        router.CloudNumPredictFor(actionKey);

    // При маршруте-слоте исполнитель — модель слота ВЛАДЕЛЬЦА действия (сильная/средняя/слабая
    // из личного per-user слота, с откатом на глобальный AppSettings), а не модель действия из
    // его конфига. Пустой слот (и у пользователя, и в AppSettings) откатывается к модели действия
    // (обычно haiku): маршрут-слот теперь дефолтный для всех действий, и без такого отката
    // ненастроенный инстанс гонял бы теги и заголовки на дорогом дефолте CLI. Склейка личного и
    // глобального слота — через UserModelTierResolver, единая точка как в ModelAssignmentResolver
    // (агентная ветка); ownerId == null/неизвестный → поведение прежнее (общий слот).
    // Слот-МАРКЕР (preset:{id} или tier:* — слот-пресет) — не модель: отданный в CLI как есть,
    // он ронял ход невнятным no-model (дефект места project-icon). Маркер разворачивается в
    // конкретную модель первого шага цепочки той же точкой, что и маршрут места —
    // ModelAssignmentResolver.ExpandModelChain. Пустой разворот (битая ссылка пресета) —
    // модель действия + warning; без assignment (ручная сборка в тестах) — прежнее поведение.
    private string? EffectiveFallback(ActionRoute route, string? fallbackModel, string? ownerId)
    {
        if (route.Kind != RouteKind.Tier) return fallbackModel;
        var tier = route.Tier ?? ModelTier.Medium;
        var slot = userTiers?.ModelFor(tier, ownerId) ?? appSettings?.TierModel(tier);
        if (assignment is null || !IsMarkerRoute(slot)) return slot ?? fallbackModel;
        var expanded = assignment.ExpandModelChain(slot, ownerId).FirstOrDefault();
        if (expanded is not null) return expanded;
        log.LogWarning(
            "cheap-runner: слот {Slot} (уровень {Tier}) развернулся пусто — действие идёт на модель действия",
            slot, tier);
        return fallbackModel;
    }

    // Значение, которому нужен разворот в конкретную модель: ссылка на пресет или уровень.
    private static bool IsMarkerRoute(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (LocalActionOverridesStore.IsPresetRoute(value)
            || LocalActionOverridesStore.ParseTierRoute(value) is not null);

    // Цепочка одинакова для всех действий: выбранный исполнитель → локальная модель →
    // claude. Последний шаг умышленно без страховки: если упал и он, исключение уходит
    // наверх, и потребитель обрабатывает отказ как раньше (у каждого своя деградация).
    // RunAsync всегда доходит до claude, поэтому null не возвращает (null → пустая строка).
    public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var (text, _) = await RunChainCoreAsync(actionKey, prompt, fallbackModel, ownerId, jsonFormat, ct,
            allowCloudFallback: true);
        return text ?? string.Empty;
    }

    // Та же цепочка, что RunAsync, но БЕЗ финального claude-шага, когда маршрут места —
    // локальная модель (дефолт или явный выбор админа): сбой/пустой ответ локали не уводит
    // содержимое в облако молча. Для мест, чьи данные (реплики чатов, память) не должны
    // уходить наружу без явного выбора облака. Явный выбор облака/слота/модели — цепочка
    // полная, как в RunAsync. null — ни выбранное, ни локаль не дали ответа (и облака по
    // маршруту нет).
    public async Task<string?> RunLocalOrChosenAsync(string actionKey, string prompt, string? ownerId = null,
        object? jsonFormat = null, CancellationToken ct = default)
    {
        var (text, _) = await RunChainCoreAsync(actionKey, prompt, fallbackModel: null, ownerId, jsonFormat, ct,
            allowCloudFallback: false);
        return text;
    }

    // Та же цепочка и та же ранняя развилка, что у RunLocalOrChosenAsync, но отдаёт и расход:
    // usage шага локали (CostUsd 0 — локаль бесплатна) либо usage провайдера для выбранной
    // модели и облачного шага. Нужен ночному разбору вики и ступени 2 отбора чатов: им нужна
    // фактическая нагрузка GPU локали, а не только текст. null — ни выбранное, ни локаль не
    // дали ответа (и облака по маршруту нет).
    public async Task<OneShotResult?> RunLocalOrChosenDetailedAsync(string actionKey, string prompt,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var (text, usage) = await RunChainCoreAsync(actionKey, prompt, fallbackModel: null, ownerId,
            jsonFormat, ct, allowCloudFallback: false);
        return text is null ? null : new OneShotResult(text, usage, 0);
    }

    // Общая цепочка RunAsync / RunLocalOrChosenAsync / RunLocalOrChosenDetailedAsync:
    // выбранный исполнитель → локальная модель → (опционально) claude. Возвращает пару
    // «текст + usage»: строковые варианты берут из неё Text, detailed — всё. Копии цепочки
    // нет намеренно: инвариант «локальный маршрут без облачного фолбэка» обязан жить в одном
    // месте (в RunDetailedAsync он скопирован — известное место расхождения, вне этого шага).
    // allowCloudFallback=false — финальный claude-шаг пропускается, когда
    // маршрут места локальный (дефолт или явный выбор): тогда сбой локали не уводит данные в
    // облако молча. Для явного выбора облака/слота/модели allowCloudFallback=true (RunAsync).
    private async Task<(string? Text, OneShotUsage? Usage)> RunChainCoreAsync(string actionKey, string prompt,
        string? fallbackModel, string? ownerId, object? jsonFormat, CancellationToken ct, bool allowCloudFallback)
    {
        if (RefuseImageAction(actionKey))
            throw new InvalidOperationException(
                $"Действие «{actionKey}» работает с картинками и не выполняется текстовым раннером");
        var route = ResolveRoute(actionKey);

        // Шаг 1 — выбранная админом модель. Два транспорта: префикс "direct:" → прямой
        // HTTP-адаптер (быстро, для бесплатных моделей агрегатора), иначе → провайдер через
        // claude CLI. Оба при неудаче отдают null и уходят дальше по цепочке.
        if (route.Kind == RouteKind.Model && !string.IsNullOrWhiteSpace(route.Model))
        {
            if (CloudCheapClient.IsDirectRoute(route.Model))
            {
                // Прямого адаптера метрик не несёт (CloudTextResult их не имеет) — usage null,
                // как в RunDetailedAsync.
                var direct = await TryDirectAsync(actionKey, route.Model!, prompt, ownerId, ct);
                if (direct is not null) return (direct, null);
            }
            else
            {
                var picked = await TryDetailedModelAsync(actionKey, route.Model!, prompt, ownerId,
                    CloudTimeoutFor(actionKey), ct);
                if (picked is not null) return (picked.Text, picked.Usage);
            }
        }

        // Шаг 2 — локальная модель. Идёт и как основной путь (Kind=Local), и как страховка
        // выбранной модели; при Kind=Claude локаль пропускаем, иначе выбор «claude» ничем
        // не отличался бы от «локаль». Для «сильных» действий (DefaultLocal=false) локаль
        // пропускаем и как страховку: слабая модель на сложной задаче даёт мусор, который ещё
        // и «сойдёт» за успех и оборвёт цепочку до качественного claude.
        // Именованный движок (LocalEngines:*) — свой шаг со своей страховкой (direct-модель),
        // занятость основного движка ходом к нему не относится: он на другой карте/машине.
        // Вызов движка журналируется в EngineCallLedger (шаг Б5, разрез v2 §4): запись
        // заводится до пробы готовности — «ждёт в очереди»; звено ставит RunEngineAsync,
        // когда запрос реально уходит в движок. Учёт — только цепочки с движком: явная
        // модель админа (шаг 1) и места не на движке в журнал не входят.
        EngineCallHandle? call = null;
        // Режим слота этого шага — по режиму цепочки (см. AwaitsSlot): без облачной страховки
        // шаг локали ждёт свободный слот в общем счётчике, а не отказывается.
        var awaitSlot = AwaitsSlot(allowCloudFallback, route.Kind);
        try
        {
            if (router.EngineOf(route) is { } engine)
            {
                call = ledger?.Begin(engine.Name, actionKey);
                var (viaEngine, engineUsage) = await RunEngineAsync(engine, actionKey, prompt, jsonFormat,
                    ownerId, ct, call, awaitSlot);
                if (!string.IsNullOrWhiteSpace(viaEngine))
                {
                    call?.End(EngineCallOutcome.Engine);
                    return (viaEngine, engineUsage);
                }
                // Страховка движка — облако: в режиме «без облака» (RunLocalOrChosen*) её нет.
                if (allowCloudFallback && engine.Spec.FallbackRoute is { } insurance)
                {
                    call?.Step(EngineCallLink.Insurance);
                    var insured = await TryDirectAsync(actionKey, insurance, prompt, ownerId, ct);
                    if (insured is not null)
                    {
                        call?.End(EngineCallOutcome.Insurance);
                        return (insured, null);
                    }
                }
            }
            else if (LocalStepReady(actionKey, route.Kind, allowCloudFallback))
            {
                var (local, localUsage) = await RunLocalAsync(actionKey, prompt, jsonFormat, ownerId, ct,
                    awaitSlot);
                if (!string.IsNullOrWhiteSpace(local)) return (local, localUsage);
                log.LogDebug("cheap-runner: действие {Action} — фолбэк с Ollama на claude", actionKey);
            }

            // Шаг 3 — claude: маршрут-слот берёт модель слота, иначе модель действия из его конфига.
            // Таймаут — облачный (не локальный). При обрыве по таймауту повторяем ОДИН раз:
            // модель не дала ничего, повтор дешевле потерянной работы человека сверху
            // (прод 2026-08-04: планировщик «Командной реализации»). Внешняя отмена ct —
            // не сбой, по ней не повторяем. Прочие ошибки — как раньше, наверх без страховки.
            // Режим RunLocalOrChosenAsync (allowCloudFallback=false): шаг пропускается, когда
            // маршрут места локальный — сбой/пусто локали не уводит данные в облако молча.
            // Пустой ответ финального шага — тоже «без ответа» (корзина missed).
            if (!allowCloudFallback && route.Kind == RouteKind.Local) return (null, null);
            // Звено Claude — только у цепочек, которые реально дошли до облачного шага:
            // в режиме «без облака» цепочка с движком закончится без ответа, и «кто сейчас
            // на модели» не должна показывать Claude у места, которое в облако не уходит
            // (ревью Б5, п.4: раньше Step стоял до раннего выхода).
            call?.Step(EngineCallLink.Claude);
            var effFallback = EffectiveFallback(route, fallbackModel, ownerId);
            var cloudTimeout = CloudTimeoutFor(actionKey);
            var model = claude.NormalizeModel(effFallback);
            try
            {
                // Detailed-вариант того же вызова: текст идентичен RunAsync (у OneShotClaudeRunner
                // оба метода ведут в один RunCliAsync), но usage провайдера доходит до
                // detailed-потребителя цепочки.
                var answer = await claude.RunDetailedAsync(prompt, model, timeout: cloudTimeout, ct: ct,
                    ownerId: ownerId, label: actionKey);
                call?.End(string.IsNullOrWhiteSpace(answer.Text)
                    ? EngineCallOutcome.Missed : EngineCallOutcome.Claude);
                return (answer.Text, answer.Usage);
            }
            catch (LlmTimeoutException ex) when (!ct.IsCancellationRequested)
            {
                // В сообщении исключения — применённый лимит и фактическая длительность:
                // отказ по времени без этих цифр в логе не разбирается. Параметр зовётся
                // {Reason}, а НЕ {Message}: generic-имя message санитайзер телеметрии дропает
                // как пользовательский текст (PiiRules.DropSubstrings), и в SigNoz приезжал
                // шаблон с плейсхолдером вместо цифр — то есть ровно то, ради чего строка писалась
                log.LogWarning("cheap-runner: действие {Action} — {Reason}; повторяю один раз",
                    actionKey, ex.Message);
                var answer = await claude.RunDetailedAsync(prompt, model, timeout: cloudTimeout, ct: ct,
                    ownerId: ownerId, label: actionKey);
                call?.End(string.IsNullOrWhiteSpace(answer.Text)
                    ? EngineCallOutcome.Missed : EngineCallOutcome.Claude);
                return (answer.Text, answer.Usage);
            }
        }
        finally
        {
            // Любой выход без явного End (исключение финального шага, внешняя отмена,
            // промах цепочки) закрывает вызов как «без ответа»: открытый вызов не должен
            // остаться в «кто сейчас на модели» призраком. Уже закрытый End — no-op.
            call?.End(EngineCallOutcome.Missed);
        }
    }

    // Прямой HTTP-адаптер (CloudCheapClient) на бесплатной модели агрегатора. maxTokens и
    // timeout — из профиля действия. null — 429/ошибка/пусто/адаптер не настроен → дальше по цепочке.
    // ОБРЕЗАННЫЙ по лимиту ответ НЕ считаем успехом: на структурных JSON это эквивалент
    // таймаута, иначе цепочка отдаст мусор, который у планировщика оборвёт ParsePlan. Уже
    // залогировано в CloudCheapClient (с maxTokens и label), здесь — одна строка контекста
    // для grep'а по «упал дальше по цепочке».
    private async Task<string?> TryDirectAsync(string actionKey, string route, string prompt,
        string? ownerId, CancellationToken ct)
    {
        var model = CloudCheapClient.StripPrefix(route);
        try
        {
            // Таймаут — облачный: локальный потолок профиля калибровался под Ollama.
            // Потолок вывода — облачный (CloudNumPredict, не NumPredict): см. CloudNumPredictFor.
            var result = await cloud.GenerateDetailedAsync(
                model, prompt, CloudTimeoutFor(actionKey), CloudNumPredictFor(actionKey),
                ownerId, label: actionKey, ct);
            if (result.Truncated)
            {
                log.LogWarning(
                    "cheap-runner: действие {Action} — прямой вызов {Model} обрезан по лимиту вывода, дальше по цепочке",
                    actionKey, model);
                return null;
            }
            if (!string.IsNullOrWhiteSpace(result.Text)) return result.Text;
            log.LogDebug("cheap-runner: действие {Action} — прямой вызов {Model} пуст/недоступен", actionKey, model);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "cheap-runner: действие {Action} — прямой вызов {Model} упал, дальше по цепочке",
                actionKey, model);
        }
        return null;
    }

    // Идёт ли шаг локальной модели. Kind=Local — явный выбор админа, уважаем всегда.
    // Kind=Model — локаль лишь как страховка выбранной модели, и только для действий, где
    // локаль вообще уместна (DefaultLocal=true): «сильным» слабая модель не поможет, а её
    // «успешный» мусорный ответ оборвал бы цепочку до качественного claude.
    internal static bool LocalStepApplies(string actionKey, RouteKind kind) =>
        kind == RouteKind.Local
        || (kind == RouteKind.Model && LocalActionCatalog.Find(actionKey)?.DefaultLocal == true);

    // Одно место, где названо исключение ночи (WN-4): цепочка БЕЗ облачной страховки на
    // локальном маршруте (allowCloudFallback=false и Kind=Local, RunLocalOrChosen*) не имеет
    // куда отступать, поэтому она и шаг локали не пропускает, и слот берёт с ожиданием.
    // Обычные фоновые действия (теги, сводки, заголовки, чат-субъекты) к правилу «фон не лезет
    // в занятый движок» относятся как раньше: их цепочка продолжается облачным шагом.
    private static bool AwaitsSlot(bool allowCloudFallback, RouteKind kind) =>
        !allowCloudFallback && kind == RouteKind.Local;

    // Идёт ли шаг локали ПРЯМО СЕЙЧАС: маршрут её допускает (LocalStepApplies), движок настроен
    // и на нём не идёт ход исполнителя. Последнее — правило «фон не лезет в занятый движок»
    // (LocalActionRouter.LocalBlockedByTurn): фоновый запрос кэшем не пользуется, а KV-бюджет
    // занимает и вытесняет префикс чата (живой прогон 2026-09-23). Действие не теряется —
    // оно идёт дальше по своей цепочке, к облачному шагу.
    // Исключение — режим «без облака» (AwaitsSlot): там облачного шага нет, пропуск = потеря
    // вызова, поэтому занятость не применяется — идём в движок (вариант А, 06.11), и слот
    // берётся с ожиданием в общей очереди (WN-4): отказ по занятости был повтором ночи,
    // а не её работой.
    private bool LocalStepReady(string actionKey, RouteKind kind, bool allowCloudFallback)
    {
        if (!LocalStepApplies(actionKey, kind) || !ollama.Enabled) return false;
        if (AwaitsSlot(allowCloudFallback, kind)) return true;
        if (!router.LocalBlockedByTurn) return true;
        log.LogDebug(
            "cheap-runner: действие {Action} — на локальном движке идёт ход, шаг локали пропускаю",
            actionKey);
        return false;
    }

    private Task<(string? Text, OneShotUsage? Usage)> RunLocalAsync(string actionKey, string prompt,
        object? jsonFormat, string? ownerId, CancellationToken ct, bool awaitSlot) =>
        RunOnClientAsync(ollama, actionKey, prompt, jsonFormat, ownerId, router.ProfileFor(actionKey),
            ct, awaitSlot);

    // Шаг именованного движка: сначала проба готовности (движок под арбитром GPU бывает
    // выгружен штатно — отказ обязан быть мгновенным, а не таймаутом), затем вызов по профилю
    // места с контекстом не выше окна движка (NumCtx спецификации — потолок обрезки промпта).
    private async Task<(string? Text, OneShotUsage? Usage)> RunEngineAsync(LocalEngine engine,
        string actionKey, string prompt, object? jsonFormat, string? ownerId, CancellationToken ct,
        EngineCallHandle? call = null, bool awaitSlot = false)
    {
        if (!engine.Enabled) return (null, null);
        if (!await engine.IsReadyAsync(ct))
        {
            log.LogDebug("cheap-runner: действие {Action} — движок {Engine} не готов, иду дальше по цепочке",
                actionKey, engine.Name);
            return (null, null);
        }
        var spec = router.ProfileFor(actionKey);
        if (engine.Spec.NumCtx is { } cap && cap < spec.NumCtx) spec = spec with { NumCtx = cap };
        // Вызов вошёл в звено движка: проба готовности позади, запрос уходит сейчас
        // (в журнале — строка «кто сейчас на модели» со звеном, а не «ждёт в очереди»).
        call?.Step(EngineCallLink.Engine);
        var (text, usage) = await RunOnClientAsync(engine.Client, actionKey, prompt, jsonFormat, ownerId,
            spec, ct, awaitSlot);
        if (string.IsNullOrWhiteSpace(text))
            log.LogDebug("cheap-runner: действие {Action} — движок {Engine} не ответил, иду дальше по цепочке",
                actionKey, engine.Name);
        return (text, usage);
    }

    // Шаг одного клиента: тот же выбор транспорта, что раньше (свободная генерация без схемы,
    // ChatJson со схемой), но detailed-варианты — чтобы usage того же ответа дошёл до
    // цепочки. Локальные метрики переводятся в DTO шва: CostUsd 0 (бесплатно), кеша у
    // локального вызова нет, Model — та, что реально отвечала (TextModel: GenerateText без
    // model берёт его же, ChatJson вызывается с ним). Метрик не было — null: вызов успешен,
    // потребитель прибавляет 0. Значение возвращается из этого вызова, в поле клиента или
    // раннера не кладётся: параллельные вызовы (ночь + ступень 2 + changelog) перемешались бы.
    // awaitSlot — режим слота (см. AwaitsSlot): обычный вызов или вызов, ждущий свободный слот
    // в общем счётчике. Выбор транспорта (свободная генерация / ChatJson со схемой) от этого
    // не зависит: у движка своей очереди нет, ждёт обёртка.
    private static async Task<(string? Text, OneShotUsage? Usage)> RunOnClientAsync(
        ILocalLlmClient client, string actionKey, string prompt, object? jsonFormat, string? ownerId,
        CheapProfileSpec spec, CancellationToken ct, bool awaitSlot = false)
    {
        var (text, usage) = jsonFormat is null
            ? await (awaitSlot
                ? client.GenerateTextAwaitingSlotAsync(
                    prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
                    numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId, label: actionKey, ct)
                : client.GenerateTextDetailedAsync(
                    prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
                    numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId, label: actionKey, ct))
            : await (awaitSlot
                ? client.ChatJsonAwaitingSlotAsync(
                    systemPrompt: "", userPrompt: prompt, jsonFormat: jsonFormat, ct,
                    model: client.TextModel, timeoutMs: spec.TimeoutMs,
                    numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId, label: actionKey)
                : client.ChatJsonDetailedAsync(
                    systemPrompt: "", userPrompt: prompt, jsonFormat: jsonFormat, ct,
                    model: client.TextModel, timeoutMs: spec.TimeoutMs,
                    numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId, label: actionKey));
        return usage is null
            ? (text, null)
            : (text, new OneShotUsage(usage.InputTokens, 0, 0, usage.OutputTokens, CostUsd: 0, client.TextModel));
    }

    // Та же цепочка «выбранное → локаль → claude», но с расходом. Текст локали/адаптера
    // оборачивается в OneShotResult без usage (бесплатно — стоимости нет); у claude/провайдера
    // usage приходит из RunDetailedAsync. Последний шаг без страховки — как в RunAsync.
    public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
        string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
        int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        if (RefuseImageAction(actionKey))
            throw new InvalidOperationException(
                $"Действие «{actionKey}» работает с картинками и не выполняется текстовым раннером");
        var route = ResolveRoute(actionKey);
        // Таймаут по маршруту: явный override потребителя (changelog), иначе ОБЛАЧНЫЙ
        // потолок профиля — вызов заканчивается на claude даже у действий на локали.
        var effTimeout = timeout ?? CloudTimeoutFor(actionKey);
        // Потолок вывода: явный override потребителя (changelog), иначе облачный дефолт
        // профиля. NumPredict — для Ollama и не подходит для облака: модель обрезала бы
        // крупный JSON на полуслове (прод 2026-08-05).
        var effMaxTokens = maxTokens ?? CloudNumPredictFor(actionKey);

        // Шаг 1 — выбранная модель. Direct → прямой адаптер (usage нет), иначе провайдер через
        // claude CLI (usage есть).
        if (route.Kind == RouteKind.Model && !string.IsNullOrWhiteSpace(route.Model))
        {
            if (CloudCheapClient.IsDirectRoute(route.Model))
            {
                var model = CloudCheapClient.StripPrefix(route.Model!);
                try
                {
                    var text = await cloud.GenerateTextAsync(model, prompt, effTimeout, effMaxTokens,
                        ownerId, label: actionKey, ct);
                    if (!string.IsNullOrWhiteSpace(text)) return new OneShotResult(text, null, 0);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogWarning(ex, "cheap-runner (detailed): {Action} — прямой вызов {Model} упал", actionKey, model);
                }
            }
            else
            {
                var picked = await TryDetailedModelAsync(actionKey, route.Model!, prompt, ownerId, effTimeout, ct);
                if (picked is not null) return picked;
            }
        }

        // Шаг 2 — локальная модель (usage нет). Для «сильных» действий локаль-страховка пропускается — см. RunAsync.
        // Именованный движок со своей direct-страховкой — как в RunChainCoreAsync, включая
        // журналирование в EngineCallLedger: цепочек две, и учёт обязан быть в обеих
        // (риск из разреза §3: локальная модель дописывает вызов только в одну).
        EngineCallHandle? call = null;
        try
        {
            if (router.EngineOf(route) is { } engine)
            {
                call = ledger?.Begin(engine.Name, actionKey);
                var viaEngine = (await RunEngineAsync(engine, actionKey, prompt, jsonFormat, ownerId, ct, call)).Text;
                if (!string.IsNullOrWhiteSpace(viaEngine))
                {
                    call?.End(EngineCallOutcome.Engine);
                    // usage локали здесь не берётся намеренно: контракт RunDetailedAsync для
                    // локального шага — usage null (чangelog и прочие места, WC-4c).
                    return new OneShotResult(viaEngine, null, 0);
                }
                if (engine.Spec.FallbackRoute is { } insurance)
                {
                    call?.Step(EngineCallLink.Insurance);
                    var insured = await TryDirectAsync(actionKey, insurance, prompt, ownerId, ct);
                    if (insured is not null)
                    {
                        call?.End(EngineCallOutcome.Insurance);
                        return new OneShotResult(insured, null, 0);
                    }
                }
                call?.Step(EngineCallLink.Claude);
            }
            // Цепочка RunDetailedAsync всегда заканчивается на claude-шаге — режим «без облака»
            // сюда не доходит, занятость пропускает шаг как раньше.
            else if (LocalStepReady(actionKey, route.Kind, allowCloudFallback: true))
            {
                // awaitSlot=false: здесь есть облачный шаг, и отказ по занятости — отступ, а не
                // потеря работы (см. AwaitsSlot).
                var local = (await RunLocalAsync(actionKey, prompt, jsonFormat, ownerId, ct,
                    awaitSlot: false)).Text;
                // usage локали здесь не берётся — тот же контракт, что у шага движка выше.
                if (!string.IsNullOrWhiteSpace(local)) return new OneShotResult(local, null, 0);
            }

            // Шаг 3 — claude: маршрут-слот берёт модель слота, иначе модель действия из его конфига.
            // Один повтор при таймауте — как в RunAsync (см. там). Пустой Text — «без ответа».
            var effFallback = EffectiveFallback(route, fallbackModel, ownerId);
            var claudeModel = claude.NormalizeModel(effFallback);
            try
            {
                var answer = await claude.RunDetailedAsync(prompt, claudeModel, effTimeout, ct, ownerId,
                    label: actionKey);
                call?.End(string.IsNullOrWhiteSpace(answer.Text)
                    ? EngineCallOutcome.Missed : EngineCallOutcome.Claude);
                return answer;
            }
            catch (LlmTimeoutException ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("cheap-runner (detailed): действие {Action} — {Reason}; повторяю один раз",
                    actionKey, ex.Message);
                var answer = await claude.RunDetailedAsync(prompt, claudeModel, effTimeout, ct, ownerId,
                    label: actionKey);
                call?.End(string.IsNullOrWhiteSpace(answer.Text)
                    ? EngineCallOutcome.Missed : EngineCallOutcome.Claude);
                return answer;
            }
        }
        finally
        {
            // Как в RunChainCoreAsync: любой выход без явного End — закрытие «без ответа»,
            // открытый вызов не остаётся в «кто сейчас на модели» призраком.
            call?.End(EngineCallOutcome.Missed);
        }
    }

    // Выбранная провайдерская модель через claude CLI, с расходом. null — шаг не удался.
    // Зовётся обеими цепочками (RunChainCoreAsync и RunDetailedAsync), поэтому лог без
    // пометки «detailed»: она приписывала бы вызову цепочку, которой он не делал.
    private async Task<OneShotResult?> TryDetailedModelAsync(string actionKey, string model, string prompt,
        string? ownerId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var run = await claude.RunDetailedAsync(prompt, model, timeout, ct, ownerId, label: actionKey);
            if (!string.IsNullOrWhiteSpace(run.Text)) return run;
            log.LogDebug("cheap-runner: действие {Action} — модель {Model} вернула пустой ответ", actionKey, model);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "cheap-runner: действие {Action} — модель {Model} недоступна, иду дальше по цепочке",
                actionKey, model);
        }
        return null;
    }

    // Исполнители-движки места (ICheapTextRunner.LocalExecutorsFor, разрез §2): список
    // берётся из стора админа, порядок списка — приоритет (свободный слот первого получает
    // чат раньше). Parallel — слоты движка после урезания по maxTotal места (раунд-робин,
    // см. ниже): сумма по списку не больше потолка места. Пусто — исполнителей нет:
    // вызывающий идёт по своему маршруту
    // (RunLocalOrChosen*), а не «куда-нибудь» — конфиг, который назначает месту один
    // движок, здесь подмены не даёт. Title — подпись движка для владельца, та же сборка,
    // что у LocalActionRouter.EngineLabel (Title → модель → имя): человеку нужно видеть,
    // КУДА он отправляет ночь, а не технический ключ. У основной локали подписи движка нет —
    // её именем становится модель, на которую она реально ходит (TextModel), а без неё —
    // сам ключ «local».
    public IReadOnlyList<LocalEngineExecutor> LocalExecutorsFor(string actionKey)
    {
        if (executorsStore?.TryGet(actionKey) is not { } cfg) return [];

        // Сначала — живые движки, потом урезание по maxTotal: слоты выпавшего исполнителя
        // не должны доставаться живым меньше (порядок этих двух шагов и есть смысл правки).
        var candidates = new List<(string Engine, int Parallel)>(cfg.Executors.Count);
        foreach (var e in cfg.Executors)
        {
            var engine = e.Engine.Trim();
            // Стор живые имена уже пропустил; здесь — защита от расхождения двух снимков
            // (стор перечитан, реестр движков ещё нет): несуществующий движок в список не идёт.
            if (!IsMainLocalEngine(engine) && namedEngines?.Get(engine) is null) continue;
            candidates.Add((engine, e.Parallel));
        }

        // Потолок слотов места (maxTotal стора) применяет сам шов: координатор ночи (W4)
        // заводит ровно столько окон, сколько ему отдали, и не знает, что их больше.
        // Урезание — раунд-робином, а не жадно: по одному слоту каждому исполнителю по кругу,
        // пока бюджет или потолок своего движка не кончился. Жадный проход (первому по списку
        // — всё, что можно) оставлял бы последнего исполнителя без слота при живом бюджете
        // очереди: порядок списка — приоритет выдачи чата, а не право занять все слоты.
        var slots = new int[candidates.Count];
        for (var budget = cfg.MaxTotal; budget > 0;)
        {
            var progressed = false;
            for (var i = 0; i < candidates.Count && budget > 0; i++)
            {
                if (slots[i] >= candidates[i].Parallel) continue; // потолок своего движка
                slots[i]++;
                budget--;
                progressed = true;
            }
            // Все упёрлись в свой потолок: остаток бюджета никому не достался.
            if (!progressed) break;
        }

        var list = new List<LocalEngineExecutor>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            // Нулевой остаток — исполнитель выпадает: строка без слота читалась бы
            // вызывающим как «есть куда положить чат».
            if (slots[i] == 0) continue;
            list.Add(new LocalEngineExecutor(candidates[i].Engine, EngineTitle(candidates[i].Engine),
                slots[i]));
        }
        return list;
    }

    private string EngineTitle(string engine)
    {
        if (IsMainLocalEngine(engine))
            return !string.IsNullOrWhiteSpace(ollama.TextModel) ? ollama.TextModel
                : !string.IsNullOrWhiteSpace(ollama.Model) ? ollama.Model : engine;
        return namedEngines?.Get(engine)?.Spec is { } s ? s.Title ?? s.Model ?? s.Name : engine;
    }

    // Ключ основной локальной модели — то же значение, что маршрут места «local»
    // (LocalActionOverridesStore.LocalRoute): исполнитель ночи называется тем же словом,
    // которым админ называет маршрут.
    private static bool IsMainLocalEngine(string? engine) =>
        string.Equals(engine, LocalActionOverridesStore.LocalRoute, StringComparison.OrdinalIgnoreCase);

    // Готов ли названный движок принять работу ПРЯМО СЕЙЧАС, без самого вызова (разрез «ночь
    // вики на нескольких локальных исполнителях» §3: проба готовности перед выдачей — ночь не
    // готовит разбор чата, который некуда положить). Именованный движок — LocalEngine.IsReadyAsync
    // (проба ReadyUrl с кэшем 1 с: дежурная модель под арбитром GPU выгружается штатно, отказ
    // обязан быть мгновенным, а не таймаутом); движок без ReadyUrl — считаем готовым, решит сам
    // вызов. Основная локаль: «движка нет» — не настроенный LocalLlm, «занят ходом исполнителя»
    // — признак роутера (фоновые слоты его не взводят, ход — единственный, кто его взводит).
    // Неизвестный движок — false.
    public async Task<bool> IsLocalEngineReadyAsync(string engine, CancellationToken ct = default)
    {
        if (IsMainLocalEngine(engine)) return ollama.Enabled && !router.LocalBlockedByTurn;
        var named = namedEngines?.Get(engine);
        if (named is null || !named.Enabled) return false;
        return await named.IsReadyAsync(ct);
    }

    // Вызов на ОДНОМ названном движке, с расходом (ICheapTextRunner.RunOnLocalEngineDetailedAsync,
    // И3 пути 2 и 4). Отличие от RunLocalOrChosenDetailedAsync — нет шага «выбранная модель»
    // (исполнитель задан аргументом, а не маршрутом места), нет страховки FallbackRoute этого
    // движка, нет финального claude-шага: сам метод до облака не доходит никогда. Профиль и
    // ожидание слота — те же, что у пути «без облака» (AwaitsSlot/WN-4): следующего шага нет,
    // и отказ по занятости был бы потерей работы. null — движок неизвестен, выключен, не готов
    // или не ответил; исключение движка — тоже null (в логе с причиной): подмены нет, решение
    // за вызывающим (ночь — «выбытие исполнителя», W5). Наружу пробрасываются только два
    // случая: отмена по ct (конец прохода, а не отказ одного чата — та же семантика, что у
    // цепочки) и LocalEngineSlotRefusalException («движок занят» и «модель дала пустой ответ»
    // — разные причины итога ночи, WN-4; вызывающий различает их именно по типу).
    // Usage локального шага — OneShotUsage с CostUsd 0 (тот же контракт, что у цепочки).
    public async Task<OneShotResult?> RunOnLocalEngineDetailedAsync(string engine, string actionKey,
        string prompt, string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        // Как во всех методах раннера: действие с картинками на входе текстовый раннер не
        // выполняет (ADR-027 §2) — здесь это ещё и ошибка вызывающего, подмены нет.
        if (RefuseImageAction(actionKey))
            throw new InvalidOperationException(
                $"Действие «{actionKey}» работает с картинками и не выполняется текстовым раннером");
        try
        {
            if (IsMainLocalEngine(engine))
            {
                if (!ollama.Enabled) return null;
                var (localText, localUsage) = await RunLocalAsync(actionKey, prompt, jsonFormat, ownerId, ct,
                    awaitSlot: true);
                return string.IsNullOrWhiteSpace(localText)
                    ? null : new OneShotResult(localText, localUsage, 0);
            }

            var named = namedEngines?.Get(engine);
            if (named is null)
            {
                log.LogWarning("cheap-runner: действие {Action} послано на неизвестный движок {Engine}",
                    actionKey, engine);
                return null;
            }
            // Журнал — тот же EngineCallLedger, что у цепочек к движку: текущий путь к
            // именованному движку (RunLocalOrChosenDetailedAsync) в «кто сейчас на модели»
            // виден, и прогон ночи на движке не должен исчезнуть из дежурства.
            var call = ledger?.Begin(named.Name, actionKey);
            try
            {
                // RunEngineAsync сам делает и Enabled-проверку, и пробу готовности:
                // «выключен» и «не готов» — тот же null, без вызова движка.
                var (text, usage) = await RunEngineAsync(named, actionKey, prompt, jsonFormat, ownerId, ct,
                    call, awaitSlot: true);
                if (string.IsNullOrWhiteSpace(text))
                {
                    call?.End(EngineCallOutcome.Missed);
                    return null;
                }
                call?.End(EngineCallOutcome.Engine);
                return new OneShotResult(text, usage, 0);
            }
            finally
            {
                // Как в цепочке: любой выход без явного End закрывает вызов «без ответа».
                call?.End(EngineCallOutcome.Missed);
            }
        }
        catch (LocalEngineSlotRefusalException)
        {
            // Отказ допуска — не сбой движка: модель не звалась ни разу, и вызывающий обязан
            // назвать причину сам (WikiIngestService ловит этот тип вокруг цепочки сегодня;
            // новый путь ловит его тем же catch).
            throw;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "cheap-runner: действие {Action} — движок {Engine} дал сбой, подмены нет",
                actionKey, engine);
            return null;
        }
    }
}
