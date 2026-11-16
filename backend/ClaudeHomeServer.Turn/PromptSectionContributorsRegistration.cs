namespace ClaudeHomeServer.Services.Turn;

// Подключение реестра IPromptSectionContributor к шине: один фильтр на каждого
// контрибьютора с Order контрибьютора. Контракт фильтра:
//   - IsEnabled ложь → BuildAsync не дёргается (гейт по per-session условиям —
//     notesMcp/memoryMcp/Persona и т.д.; без предиката регрессия golden-фикстуры 4);
//   - BuildAsync вернул null → секция не добавляется (контрибьютор сам решает, есть ли
//     что показывать; то же, что прежний inline-провайдер с пустым результатом);
//   - исключения ВНУТРИ BuildAsync проглатываются (прежнее поведение — recall/граф/
//     привязки не должны ронять ход); падение самого фильтра-обёртки — прокидываем,
//     цепочка контракта Filter не нарушена.
//
// Per-owner изоляция — через PromptSessionContext.OwnerId, который контрибьютор сам
// учитывает в IsEnabled/BuildAsync (например, PersonaMemoryService требует свой ownerId
// и personaId). Шина общая, изоляция — содержимым события, не отдельной шиной.
//
// Порядок (сторож PromptSectionContributorsDiTests): DI отдаёт контрибьюторов в порядке
// регистрации подсистем, а не по Order; единственная точка сортировки ЦЕПОЧКИ — шина
// (TurnEventBus.ApplyAsync упорядочивает подписчиков по Order перед прогоном,
// TurnEventBus.cs:79). Дублировать сортировку здесь значило бы сделать сторож порядка
// декоративным. Порядок подписки роли не играет — Order каждого уходит в bus.OnFilter ниже.
//
// TS-18 (параллельная сборка recall): соседние по Order ParallelSafe-контрибьюторы
// (сейчас — пара recall-notes + recall-memory, ~73 % времени сборки промпта у local,
// разбор b8f329c6) встают в цепочку ОДНИМ фильтром на группу: их BuildAsync выполняются
// одновременно, склейка — по Order, а не по порядку завершения. Внутренние таймауты
// секций (Notes:AutoRecallTimeoutMs, Persona:RecallTimeoutMs) не тронуты; явного
// общего потолка группе не добавлен: wall-time группы ограничен максимумом BuildAsync
// её членов вместо прежней их суммы (отказ от фиксированного потолка: он резал бы
// нестандартные конфиги, которые раньше досчитывали до своего внутреннего таймаута).
// Соседство обязательно: между членами группы нет Order чужого контрибьютора — иначе
// склеенная группа обогнала бы его в цепочке и в Sections.
public static class PromptSectionContributorsRegistration
{
    public static void RegisterAll(
        ITurnEventBus bus, IEnumerable<IPromptSectionContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(contributors);

        var all = contributors.ToList();

        // Группировка: ParallelSafe-члены, соседние по Order (между ними нет Order
        // чужого контрибьютора), образуют одну группу; одиночка без соседа остаётся
        // обычным фильтром. Порядок в цепочке группы — минимальный Order её членов.
        var groups = new List<List<IPromptSectionContributor>>();
        var singles = new List<IPromptSectionContributor>();
        var parallel = all.Where(c => c.ParallelSafe).OrderBy(c => c.Order).ToList();
        var run = new List<IPromptSectionContributor>();
        foreach (var c in parallel)
        {
            if (run.Count > 0 && all.Any(o => o.Order > run[^1].Order && o.Order < c.Order))
            {
                Emit(run, all, groups, singles);
                run = [];
            }
            run.Add(c);
        }
        Emit(run, all, groups, singles);
        singles.AddRange(all.Where(c => !c.ParallelSafe));

        foreach (var c in singles)
            RegisterSingle(bus, c);
        foreach (var g in groups)
            RegisterGroup(bus, g);
    }

    private static void Emit(
        List<IPromptSectionContributor> run,
        List<IPromptSectionContributor> all,
        List<List<IPromptSectionContributor>> groups,
        List<IPromptSectionContributor> singles)
    {
        if (run.Count == 0) return;
        if (run.Count == 1) { singles.AddRange(run); return; }
        // Порядок запуска внутри группы — порядок получения реестра (DI-порядок
        // неканоничен); единственная точка порядка — склейка по Order в RegisterGroup.
        run.Sort((a, b) => all.IndexOf(a).CompareTo(all.IndexOf(b)));
        groups.Add(run);
    }

    private static void RegisterSingle(
        ITurnEventBus bus, IPromptSectionContributor c)
    {
        // Замыкаем контрибьютора: фильтр-обёртка вызывает его на каждом событии
        bus.OnFilter<PromptAssembling>(c.Order, async (e, next) =>
        {
            if (c.IsEnabled(e.Session))
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                PromptSectionContribution? contrib = null;
                try
                {
                    contrib = await c.BuildAsync(e.Session, e.TurnText);
                }
                catch
                {
                    // Исключение внутри BuildAsync — секция не добавится, но фильтр-цепочка
                    // идёт дальше (прежнее поведение inline-провайдеров: catch и null).
                    // Падение фильтра-обёртки (НЕ BuildAsync) роняло бы ход, но сюда
                    // мы не доходим: исключения из BuildAsync глотаются нами.
                    contrib = null;
                }
                // Замер для паспорта хода: какой контрибьютор держит сборку промпта
                e.ContributorTimingsMs[c.Key] =
                    (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (contrib is not null)
                {
                    foreach (var s in contrib.Sections)
                        if (!string.IsNullOrWhiteSpace(s.Text))
                            e.Sections.Add(s);
                    if (contrib.ManifestItems is not null)
                        foreach (var item in contrib.ManifestItems)
                            e.ManifestItems.Add(item);
                }
            }
            await next();
        }, $"PromptSectionContributor:{c.Key}");
    }

    // TS-18: один фильтр на параллельную группу. Гейты — синхронно, как раньше;
    // BuildAsync членов — одновременно; Sections/ManifestItems/ContributorTimingsMs
    // пишет только этот поток после WhenAll: гонок за общее изменяемое состояние события
    // нет, члены группы не делят ничего, кроме неизменяемого PromptSessionContext.
    private static void RegisterGroup(
        ITurnEventBus bus, List<IPromptSectionContributor> group)
    {
        bus.OnFilter<PromptAssembling>(group.Min(c => c.Order), async (e, next) =>
        {
            var active = group.Where(c => c.IsEnabled(e.Session)).ToList();
            if (active.Count > 0)
            {
                var results = await Task.WhenAll(active.Select(async c =>
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    PromptSectionContribution? contrib = null;
                    try
                    {
                        contrib = await c.BuildAsync(e.Session, e.TurnText);
                    }
                    catch
                    {
                        // Как раньше: исключение члена — пустая секция этого члена;
                        // сосед по группе и цепочка не тронуты.
                        contrib = null;
                    }
                    return (c, contrib,
                        (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }));

                // Замеры пишет этот поток (не задачи): словарь события без гонок.
                foreach (var (c, _, elapsed) in results)
                    e.ContributorTimingsMs[c.Key] = elapsed;

                // Порядок секций — по Order контрибьютора, а не по порядку завершения
                // (сторож: ParallelGroup_CompletingInReverseOrder_SectionsStayInOrder).
                foreach (var (c, contrib, _) in results.OrderBy(r => r.c.Order))
                {
                    if (contrib is null) continue;
                    foreach (var s in contrib.Sections)
                        if (!string.IsNullOrWhiteSpace(s.Text))
                            e.Sections.Add(s);
                    if (contrib.ManifestItems is not null)
                        foreach (var item in contrib.ManifestItems)
                            e.ManifestItems.Add(item);
                }
            }
            await next();
        }, $"PromptSectionContributorGroup:{string.Join("+", group.Select(c => c.Key))}");
    }
}
