namespace ClaudeHomeServer.Services.Turn;

// Подключение реестра IPromptSectionContributor к шине: один фильтр на каждого
// контрибьютора с Order контрибьютора. Контракт фильтра:
//   - IsEnabled ложь → BuildAsync не дёргается (gate по per-session условиям —
//     notesMcp/memoryMcp/Persona и т.д.; без предиката регрессия golden-фикстуры 4);
//   - BuildAsync вернул null → секция не добавляется (контрибьютор сам решает, есть ли
//     что показывать; то же, что прежний inline-провайдер с пустым результатом);
//   - исключения ВНУТРИ BuildAsync проглатываются (прежнее поведение — recall/граф/
//     привязки не должны ронять ход); падение самого фильтра-обёртки — прокидываем,
//     цепочка контракта Filter не нарушена.
//
// Per-owner изоляция — через PromptSessionContext.OwnerId, который контрибьютор сам
// учитывает в IsEnabled/BuildAsync (например, PersonaMemoryService требует свой
// ownerId и personaId). Шина общая, изоляция — содержимым события, не отдельной шиной.
public static class PromptSectionContributorsRegistration
{
    public static void RegisterAll(
        ITurnEventBus bus, IEnumerable<IPromptSectionContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(contributors);

        // Этап 5, шаг 6 (инверсия контрибьюторов промпта): после инверсии контрибьюторы
        // приходят из РАЗНЫХ подсистем, и порядок в `IEnumerable<>` от DI определяется
        // порядком регистрации подсистем (`AddSubsystems`), а НЕ `Order`'ом контрибьютора.
        // Здесь мы намеренно НЕ сортируем: единственная точка сортировки — шина
        // (`TurnEventBus.ApplyAsync` упорядочивает подписчиков по Order перед прогоном,
        // TurnEventBus.cs:128). Дублировать сортировку тут значило бы сделать сторож
        // порядка декоративным: мутация одной точки маскировалась бы второй, и тест
        // остался бы зелёным на сломанном коде. Порядок подписки роли не играет —
        // Order каждого контрибьютора уходит в `bus.OnFilter` ниже.
        foreach (var c in contributors)
        {
            // Замыкаем контрибьютора: фильтр-обёртка вызывает его на каждом событии
            bus.OnFilter<PromptAssembling>(c.Order, async (e, next) =>
            {
                if (c.IsEnabled(e.Session))
                {
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
    }
}