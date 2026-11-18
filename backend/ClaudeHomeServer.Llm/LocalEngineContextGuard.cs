namespace ClaudeHomeServer.Services.Llm;

// Сторож при старте: NumCtx именованного движка меньше NumCtx мест, назначенных ему
// (разбор 8d66a3d3 §2–3: на бою LocalEngines:gemma:NumCtx = 32768, а места wiki-ingest и
// wiki-nightly ждут 65536 — потолок движка молча резал их вход, и об этом никто не узнавал).
//
// Сторож проверяет КОНФИГ, а не движка: живая сверка с /v1/models max_model_len или
// /props n_ctx — отдельный шаг (образец — LocalEndpointProbe). Здесь он лишь говорит
// прямым текстом то, что сейчас происходит молча: какой движок какому месту не по размеру.
//
// Две стороны назначения места движком — обе под проверкой:
//   • маршрут: LocalEngines:*:Actions / оверрайд админа → LocalActionRouter.Resolve +
//     EngineOf (место реально пойдёт на движок);
//   • список исполнителей места (LocalActionExecutorsStore, W2a): место с
//     ParallelExecutors делит работу между движками — тот же потолок проверяется и
//     здесь. «local» (основная локаль) пропускается: у неё нет LocalEngineSpec.NumCtx,
//     её окно — профиль вызова, а не слот движка.
//
// Сервер при этом не падает: строки идут в LogWarning и в Mismatches синглтона (показ
// в интерфейсе админа — отдельный шаг). Исключение из StartAsync не выходит наружу:
// упавший StartAsync гасит весь хост, а сторож обязан только предупредить.
//
// Проход один при старте: сторы читаются снимками (оба неизменяемы), Mismatches
// присваивается один раз целым списком — читатели видят либо «ещё не проверял» (пусто),
// либо полный результат, полумементированного состояния нет.
public sealed class LocalEngineContextGuard(
    LocalActionRouter router,
    LocalActionExecutorsStore executors,
    NamedLocalEngines engines,
    ILogger<LocalEngineContextGuard> log) : IHostedService
{
    // Найденные несоответствия; пусто — проверки ещё не было или их нет. Присваивается
    // один раз целым списком в StartAsync.
    public IReadOnlyList<string> Mismatches { get; private set; } = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mismatches = Check(CollectPairs());
            Mismatches = mismatches;
            foreach (var m in mismatches)
                log.LogWarning("Сторож контекстных окон: {Mismatch}", m);
            if (mismatches.Count > 0)
                log.LogWarning("Сторож контекстных окон: движков с потолком ниже их мест: {Count}",
                    mismatches.Count);
        }
        catch (Exception ex)
        {
            // Любое исключение чтения (стор, роутер) — warning, не падение: хост жив,
            // Mismatches остаётся пустым, несопоставленное видно отсутствием строк.
            log.LogWarning(ex,
                "Сторож контекстных окон не дошёл до конца: NumCtx движков с NumCtx их мест не сверен");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Чистая часть: пары (место, NumCtx места, движок, NumCtx движка) → строки о тех,
    /// у кого потолок движка меньше запроса места. Ничего не читает и ничего не пишет.
    /// NumCtx движка null — у движка нет потолка, пара пропускается. Одна и та же пара
    /// (место, движок), названная дважды — из маршрута и из списка исполнителей —
    /// даёт ОДНУ строку: предупреждение про одно и то же расхождение не удваивается.
    /// Ключи пар — канонические (из каталога и из LocalEngine.Name), сравнение Ordinal.
    /// </summary>
    internal static IReadOnlyList<string> Check(
        IEnumerable<(string ActionKey, int ActionNumCtx, string EngineName, int? EngineNumCtx)> pairs)
    {
        var seen = new HashSet<(string, string)>();
        var result = new List<string>();
        foreach (var (actionKey, actionNumCtx, engineName, engineNumCtx) in pairs)
        {
            // Нет потолка у движка — резать нечем, расхождения нет.
            if (engineNumCtx is not { } ceiling) continue;
            if (ceiling < actionNumCtx && seen.Add((actionKey, engineName)))
                result.Add($"движок {engineName}: NumCtx {ceiling} меньше {actionNumCtx} " +
                           $"у места {actionKey} — вход места будет урезан или отказан");
        }
        return result;
    }

    // Пары по всем местам каталога: NumCtx места — из роутера (профиль + пер-местные
    // лимиты каталога, та же склейка, что берёт CheapTextRunner), движки — маршрута
    // и списка исполнителей места.
    private IEnumerable<(string, int, string, int?)> CollectPairs()
    {
        foreach (var action in LocalActionCatalog.All)
        {
            var key = action.Key;
            var actionNumCtx = router.ProfileFor(key).NumCtx;

            if (router.EngineOf(router.Resolve(key)) is { } routeEngine)
                yield return (key, actionNumCtx, routeEngine.Name, routeEngine.Spec.NumCtx);

            if (executors.TryGet(key)?.Executors is not { } list) continue;
            foreach (var e in list)
            {
                if (string.Equals(e.Engine, LocalActionOverridesStore.LocalRoute,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (engines.Get(e.Engine) is { } engine)
                    yield return (key, actionNumCtx, engine.Name, engine.Spec.NumCtx);
            }
        }
    }
}
