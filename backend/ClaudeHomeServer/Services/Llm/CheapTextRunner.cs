namespace ClaudeHomeServer.Services.Llm;

// Единая точка «дешёвого» текстового one-shot вызова для фоновых действий. Скрывает от
// потребителя выбор исполнителя: если действие сконфигурировано на локаль (LocalActionRouter)
// и Ollama доступна — идёт бесплатный локальный вызов; при недоступности/ошибке/пустом ответе
// откатывается на существующий путь claude (OneShotClaudeRunner). Действие не на локали —
// сразу claude. Контракт RunAsync намеренно совпадает с прежним прямым вызовом раннера
// (единый prompt → строка ответа), чтобы потребители разбирали ответ теми же парсерами.
public interface ICheapTextRunner
{
    bool UsesLocal(string actionKey);

    // Краткое описание маршрута действия для лога и события: «kind=model/tier/local/claude»,
    // конкретная модель или слот если применимо. Нужно диагностике в местах, где чейн
    // разводится (планировщик «Командной реализации», сводка «Что нового»): без этого в
    // логе «не ответил» без версий и провайдера, и разбор каждого сбоя — раскопки.
    string DescribeRoute(string actionKey, string? fallbackModel);

    // actionKey — ключ из LocalActionCatalog (определяет маршрут и профиль вызова).
    // fallbackModel — модель claude для существующего пути (как раньше читалась из конфига).
    // ownerId — владелец для среды исполнения claude-пути (Ollama ходит по HTTP независимо).
    // jsonFormat — формат ответа для действий со СТРОГИМ JSON-контрактом: строка "json"
    // (просто «отвечай валидным JSON») либо полноценная JSON-схема. Локальный путь тогда идёт
    // в ChatJsonAsync (structured output Ollama) вместо свободной генерации: без этого мелкая
    // модель регулярно оборачивает JSON в прозу, парсер падает и действие всё равно уходит в
    // фолбэк на claude — экономии не возникает. Обычно достаточно "json": форму ответа задаёт
    // текст промпта, а схему пришлось бы держать в синхроне с моделями руками. На claude-путь
    // не влияет (там контракт задаётся промптом, как и раньше).
    Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default);

    // Только локаль, БЕЗ фолбэка на платный claude. null — локаль выключена/недоступна/пусто.
    // Для необязательных «украшений» (суть уведомления), где платный вызов нежелателен.
    Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default);

    // Бесплатная часть цепочки: прямой адаптер агрегатора → локальная модель, БЕЗ claude.
    // Шире RunLocalOnlyAsync (та требует именно Kind=Local и живой Ollama) и уже RunAsync
    // (та в конце всегда платит claude). Для «украшений», которые нужны на каждом шаге
    // сценария и потому не должны стоить денег, но и локалью не ограничены.
    // null — бесплатного маршрута нет либо он не дал ответа; вызывающий деградирует молча.
    Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
        CancellationToken ct = default);

    // То же, что RunAsync, но с расходом вызова (OneShotResult.Usage) — для действий, которым
    // важна стоимость (сводка «Что нового»). На claude-пути usage приходит как раньше; на
    // локали и прямом адаптере usage=null (бесплатно — стоимости нет, что для них корректно).
    // timeout/maxTokens перекрывают профиль действия — для тяжёлых задач с собственными лимитами
    // (changelog: свой большой таймаут, длинный JSON-ответ). null → значения из профиля.
    // jsonFormat — как в RunAsync: structured output локального шага (LLM-канал модулей
    // передаёт сюда responseFormat из §10.3 — просьбу, а не гарантию).
    Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
        object? jsonFormat = null, CancellationToken ct = default);
}

public sealed class CheapTextRunner(
    LocalActionRouter router, ILocalLlmClient ollama, CloudCheapClient cloud, IOneShotRunner claude,
    ILogger<CheapTextRunner> log, AppSettingsService? appSettings = null,
    UserModelTierResolver? userTiers = null, ModelAssignmentResolver? assignment = null)
    : ICheapTextRunner
{
    public bool UsesLocal(string actionKey) => router.UsesLocal(actionKey);

    // Описание маршрута для лога/события: kind + конкретная модель или слот, если это
    // слот/модель. claude/local — без модели (дефолты маршрута). Пример: «model=nemotron:free»,
    // «tier=strong», «claude», «local». Используется из TeamPlanningService — там, где
    // раньше лог показывал только «не ответил».
    public string DescribeRoute(string actionKey, string? fallbackModel)
    {
        var route = router.Resolve(actionKey);
        return route.Kind switch
        {
            RouteKind.Model when !string.IsNullOrWhiteSpace(route.Model) =>
                CloudCheapClient.IsDirectRoute(route.Model)
                    ? $"direct:{CloudCheapClient.StripPrefix(route.Model!)}"
                    : $"model={route.Model}",
            RouteKind.Tier => $"tier={route.Tier?.ToString().ToLowerInvariant() ?? "medium"}",
            RouteKind.Local => "local",
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
    // (прод 2026-08-05). CloudNumPredict — отдельное поле профиля (дефолты в каталоге).
    internal int CloudNumPredictFor(string actionKey) =>
        router.ProfileFor(actionKey).CloudNumPredict;

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
    public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var route = router.Resolve(actionKey);

        // Шаг 1 — выбранная админом модель. Два транспорта: префикс "direct:" → прямой
        // HTTP-адаптер (быстро, для бесплатных моделей агрегатора), иначе → провайдер через
        // claude CLI. Оба при неудаче отдают null и уходят дальше по цепочке.
        if (route.Kind == RouteKind.Model && !string.IsNullOrWhiteSpace(route.Model))
        {
            var picked = CloudCheapClient.IsDirectRoute(route.Model)
                ? await TryDirectAsync(actionKey, route.Model!, prompt, ownerId, ct)
                : await TryModelAsync(actionKey, route.Model!, prompt, ownerId, ct);
            if (picked is not null) return picked;
        }

        // Шаг 2 — локальная модель. Идёт и как основной путь (Kind=Local), и как страховка
        // выбранной модели; при Kind=Claude локаль пропускаем, иначе выбор «claude» ничем
        // не отличался бы от «локаль». Для «сильных» действий (DefaultLocal=false) локаль
        // пропускаем и как страховку: слабая модель на сложной задаче даёт мусор, который ещё
        // и «сойдёт» за успех и оборвёт цепочку до качественного claude.
        if (LocalStepApplies(actionKey, route.Kind) && ollama.Enabled)
        {
            var local = await RunLocalAsync(actionKey, prompt, jsonFormat, ownerId, ct);
            if (!string.IsNullOrWhiteSpace(local)) return local;
            log.LogDebug("cheap-runner: действие {Action} — фолбэк с Ollama на claude", actionKey);
        }

        // Шаг 3 — claude: маршрут-слот берёт модель слота, иначе модель действия из его конфига.
        // Таймаут — облачный (не локальный). При обрыве по таймауту повторяем ОДИН раз:
        // модель не дала ничего, повтор дешевле потерянной работы человека сверху
        // (прод 2026-08-04: планировщик «Командной реализации»). Внешняя отмена ct —
        // не сбой, по ней не повторяем. Прочие ошибки — как раньше, наверх без страховки.
        var effFallback = EffectiveFallback(route, fallbackModel, ownerId);
        var cloudTimeout = CloudTimeoutFor(actionKey);
        var model = claude.NormalizeModel(effFallback);
        try
        {
            return await claude.RunAsync(prompt, model, timeout: cloudTimeout, ct: ct, ownerId: ownerId,
                label: actionKey);
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
            return await claude.RunAsync(prompt, model, timeout: cloudTimeout, ct: ct, ownerId: ownerId,
                label: actionKey);
        }
    }

    // Вызов выбранной модели. null — шаг не удался (провайдер не настроен, ошибка CLI,
    // таймаут или пустой ответ), вызывающий идёт дальше по цепочке. Отмену ct наверх НЕ
    // проглатываем: это не сбой модели, а осознанный обрыв — фолбэчить по нему бессмысленно.
    private async Task<string?> TryModelAsync(string actionKey, string model, string prompt,
        string? ownerId, CancellationToken ct)
    {
        try
        {
            var text = await claude.RunAsync(prompt, model, timeout: CloudTimeoutFor(actionKey),
                ct: ct, ownerId: ownerId, label: actionKey);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            log.LogDebug("cheap-runner: действие {Action} — модель {Model} вернула пустой ответ", actionKey, model);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "cheap-runner: действие {Action} — модель {Model} недоступна, иду дальше по цепочке",
                actionKey, model);
        }
        return null;
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

    private Task<string?> RunLocalAsync(string actionKey, string prompt, object? jsonFormat,
        string? ownerId, CancellationToken ct)
    {
        var spec = router.ProfileFor(actionKey);
        return jsonFormat is null
            ? ollama.GenerateTextAsync(
                prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
                numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId, label: actionKey, ct)
            : ollama.ChatJsonAsync(
                systemPrompt: "", userPrompt: prompt, jsonFormat: jsonFormat, ct,
                model: ollama.TextModel, timeoutMs: spec.TimeoutMs,
                numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId, label: actionKey);
    }

    // Бесплатная цепочка: direct-модель агрегатора → локаль. Провайдерские модели через CLI
    // сюда НЕ берём, даже когда они :free: бесплатность модели по её id не определить, а
    // ошибиться здесь — значит начать молча платить за фоновое «украшение».
    public async Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
        CancellationToken ct = default)
    {
        var route = router.Resolve(actionKey);

        if (route.Kind == RouteKind.Model && CloudCheapClient.IsDirectRoute(route.Model))
        {
            var picked = await TryDirectAsync(actionKey, route.Model!, prompt, ownerId: null, ct);
            if (!string.IsNullOrWhiteSpace(picked)) return picked;
        }

        if (LocalStepApplies(actionKey, route.Kind) && ollama.Enabled)
        {
            var local = await RunLocalAsync(actionKey, prompt, jsonFormat, ownerId: null, ct);
            if (!string.IsNullOrWhiteSpace(local)) return local;
        }

        return null;
    }

    public async Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
    {
        if (!router.UsesLocal(actionKey)) return null;
        var spec = router.ProfileFor(actionKey);
        var local = await ollama.GenerateTextAsync(
            prompt, model: null, timeout: TimeSpan.FromMilliseconds(spec.TimeoutMs),
            numPredict: spec.NumPredict, numCtx: spec.NumCtx, ownerId: null, label: actionKey, ct);
        return string.IsNullOrWhiteSpace(local) ? null : local;
    }

    // Та же цепочка «выбранное → локаль → claude», но с расходом. Текст локали/адаптера
    // оборачивается в OneShotResult без usage (бесплатно — стоимости нет); у claude/провайдера
    // usage приходит из RunDetailedAsync. Последний шаг без страховки — как в RunAsync.
    public async Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
        string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
        int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        var route = router.Resolve(actionKey);
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
        if (LocalStepApplies(actionKey, route.Kind) && ollama.Enabled)
        {
            var local = await RunLocalAsync(actionKey, prompt, jsonFormat, ownerId, ct);
            if (!string.IsNullOrWhiteSpace(local)) return new OneShotResult(local, null, 0);
        }

        // Шаг 3 — claude: маршрут-слот берёт модель слота, иначе модель действия из его конфига.
        // Один повтор при таймауте — как в RunAsync (см. там).
        var effFallback = EffectiveFallback(route, fallbackModel, ownerId);
        var claudeModel = claude.NormalizeModel(effFallback);
        try
        {
            return await claude.RunDetailedAsync(prompt, claudeModel, effTimeout, ct, ownerId, label: actionKey);
        }
        catch (LlmTimeoutException ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("cheap-runner (detailed): действие {Action} — {Reason}; повторяю один раз",
                actionKey, ex.Message);
            return await claude.RunDetailedAsync(prompt, claudeModel, effTimeout, ct, ownerId, label: actionKey);
        }
    }

    // Выбранная провайдерская модель через claude CLI, с расходом. null — шаг не удался.
    private async Task<OneShotResult?> TryDetailedModelAsync(string actionKey, string model, string prompt,
        string? ownerId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var run = await claude.RunDetailedAsync(prompt, model, timeout, ct, ownerId, label: actionKey);
            if (!string.IsNullOrWhiteSpace(run.Text)) return run;
            log.LogDebug("cheap-runner (detailed): {Action} — модель {Model} вернула пустой ответ", actionKey, model);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "cheap-runner (detailed): {Action} — модель {Model} недоступна", actionKey, model);
        }
        return null;
    }
}
