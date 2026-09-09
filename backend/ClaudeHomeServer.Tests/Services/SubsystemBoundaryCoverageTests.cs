using ClaudeHomeServer.Services.Composition;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож полноты таблицы <see cref="SubsystemBoundaryTests.Boundaries"/>:
/// каждая реализация <see cref="IAppSubsystem"/> в сборке имеет строку в таблице,
/// а вертикали без подсистемы (например, <c>ClaudeHomeServer.Services.Watchdog</c>)
/// перечислены явно. Без этой проверки новая подсистема/вертикаль, забытая в
/// таблице, проходит молча — рефлексия <see cref="SubsystemBoundaryTests"/>
/// смотрит только типы из namespace, перечисленных в Boundaries, и свежедобавленная
/// вертикаль остаётся за бортом.
///
/// Живой пример: <c>ClaudeHomeServer.Services.Watchdog</c> — 5 файлов в
/// <c>backend/ClaudeHomeServer/Services/Watchdog/</c>, ни одного <c>IAppSubsystem</c>,
/// но неймспейс вертикально оформленный и подключается в Program.cs как
/// обычные <c>AddSingleton</c>/<c>AddHostedService</c>. До этой проверки вертикаль
/// в Boundaries отсутствовала — сторож границ по ней не работал, и любая новая
/// зависимость из Watchdog в другую вертикаль прошла бы мимо.
/// </summary>
public class SubsystemBoundaryCoverageTests
{
    // Форс-загрузка всех вертикальных сборок (Main плюс вынесенные .Reader/.Yandex/.Video).
    // Подробности см. в комментарии к статическому конструктору SubsystemBoundaryTests.
    static SubsystemBoundaryCoverageTests()
    {
        _ = typeof(ClaudeHomeServer.Services.Video.VideoSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Yandex.YandexSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Reader.ReaderService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.CodeGraph.CodeGraphSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Skills.SkillsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Git.GitSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Notes.NotesSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Tts.TtsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Personas.PersonaDraftService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Diagnostics.FileLog).Assembly;
        _ = typeof(ClaudeHomeServer.Services.WebSearch.PerplexitySearchService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Changelog.ChangelogSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Docs.DocsIndexService).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Knowledge.KnowledgeSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Modules.ModuleRegistry).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Spend.SpendSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Tasks.TasksSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.Backgrounds.BackgroundsSubsystem).Assembly;
        _ = typeof(ClaudeHomeServer.Services.ProjectIcons.ProjectIconsSubsystem).Assembly;
    }

    [Fact]
    public void AllIAppSubsystemImplementations_HaveBoundaryEntry()
    {
        // Раньше typeof(VideoSubsystem).Assembly давал единственную сборку ClaudeHomeServer.dll,
        // где жили все реализации IAppSubsystem. После выделения Core (и в будущем — отдельных
        // вертикальных сборок) нужен перебор ВСЕХ ClaudeHomeServer.* сборок: без него страж
        // увидит только Main (одну из многих) и решит, что остальных реализаций не
        // существует — забудешь добавить вертикаль в Boundaries, тест пройдёт зелёным.
        // Главная сборка `ClaudeHomeServer` (имя без суффикса — этап 0/1 ещё не вынес
        // вертикали, и она содержит почти все реализации) тоже входит в выборку.
        // Тестовая сборка `ClaudeHomeServer.Tests` намеренно исключена: там живут
        // `StubSubsystem`/`FakeSubsystem` (тестовые стабы IAppSubsystem), они нам
        // не нужны в реестре продовых вертикалей. Сейчас тестовых сборок уже четыре
        // (см. комментарий в SubsystemBoundaryTests), и фильтр по `*.Tests` это
        // учитывает.
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && !name.EndsWith(".Tests", StringComparison.Ordinal);
            })
            .ToList();

        var allTypes = assemblies.SelectMany(a => a.GetTypes()).ToList();

        // Защита от вакуумного прохода: если фильтр сборок или порядок загрузки сломается,
        // набор станет пустым и сторож пройдёт зелёным, ничего не проверив (доказано мутацией
        // в ревью 23d353d7: без `name == "ClaudeHomeServer"` — 17/17 зелёных при нуле типов).
        assemblies.Should().HaveCountGreaterThanOrEqualTo(2,
            "сторож должен видеть минимум ClaudeHomeServer и ClaudeHomeServer.Core");
        allTypes.Should().NotBeEmpty(
            "сборки должны отдавать типы (ориентир замера — ~4230) — иначе проверка полноты " +
            "таблицы Boundaries ничего не проверяет");

        // Не-интерфейсные реализации IAppSubsystem, не абстрактные. Distinct по namespace —
        // две реализации из одного namespace (теоретически) не должны раздувать отчёт.
        var subsystemTypes = allTypes
            .Where(t => typeof(IAppSubsystem).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .ToList();

        subsystemTypes.Should().NotBeEmpty(
            "реализации IAppSubsystem обязаны найтись (ориентир замера — 14) — пустой набор " +
            "означает сломанный фильтр сборок, а не отсутствие подсистем");

        var subsystemNamespaces = subsystemTypes
            .Select(t => t.Namespace!)
            .Where(ns => !string.IsNullOrEmpty(ns))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Namespace корневой вертикали из таблицы Boundaries. Берём первый элемент массива
        // Boundaries (там object[] из [0] = VerticalBoundary record).
        var boundaryRoots = SubsystemBoundaryTests.Boundaries
            .Select(o => ((SubsystemBoundaryTests.VerticalBoundary)o[0]).NamespaceRoot)
            .ToHashSet(StringComparer.Ordinal);

        // Вертикали без подсистемы. Список явный: если появится ещё одна «вертикаль
        // с неймспейсом, но без IAppSubsystem», добавляется строка сюда, и тест сразу
        // это отразит. Сегодня — пусто: Watchdog и Terminal уже имеют строки в
        // `SubsystemBoundaryTests.Boundaries` (это «вертикали с подсистемой», но
        // без `IAppSubsystem` — попадают в общую таблицу, не в этот список).
        // Покрытие = пересечение с `boundaryRoots`, отдельный список не нужен
        // (был дубль — убран в волне 4B шаг 1, см. отчёт).
        var verticalOnlyNamespaces = new HashSet<string>(StringComparer.Ordinal);

        var allExpected = new HashSet<string>(boundaryRoots, StringComparer.Ordinal);
        foreach (var ns in verticalOnlyNamespaces) allExpected.Add(ns);

        var missingSubsystems = subsystemNamespaces
            .Where(ns => !allExpected.Contains(ns))
            .ToList();

        missingSubsystems.Should().BeEmpty(
            "каждая реализация IAppSubsystem в сборке должна быть в SubsystemBoundaryTests.Boundaries " +
            "(см. комментарий `// Reader/Images/Tts/Git/Deploy/...` в том файле). " +
            "Если подсистема новая — добавь её в Boundaries с собственным allow-list. " +
            "Если это вертикаль БЕЗ IAppSubsystem — заведи строку в Boundaries всё равно: " +
            "`verticalOnlyNamespaces` ниже оставлен пустым сознательно (см. там), " +
            "чтобы не маскировать удаление строки из Boundaries. " +
            "Отсутствуют:\n" + string.Join("\n", missingSubsystems));
    }

    /// <summary>
    /// Сторож полноты <c>SubsystemBoundaryTests.Boundaries</c> на уровне неймспейсов:
    /// ЛЮБОЙ неймспейс <c>ClaudeHomeServer.Services.*</c>, в котором есть хотя бы
    /// один «настоящий» тип (top-level, не nested, не compiler-generated), обязан
    /// быть покрыт строкой в таблице. Иначе новая подсистема/вертикаль, забытая в
    /// таблице, проходит молча — рефлексия <see cref="SubsystemBoundaryTests"/>
    /// сканирует только типы из namespace, перечисленных в Boundaries, и свежее
    /// подразделение остаётся без границ.
    ///
    /// Живой прецедент: до этой проверки пять неймспейсов — <c>Services.Llm</c>,
    /// <c>Services.Docs</c>, <c>Services.Turn</c>, <c>Services.Prompts</c>,
    /// <c>Services.Execution</c> (~35 089 строк) — жили без строк в Boundaries и
    /// без какого-либо сторожа вообще. Шаг 0 волны 4 фиксирует эту серую зону.
    ///
    /// Заведомые исключения (<see cref="ExcludedNamespaces"/>) — общая «спинка» из
    /// списка <c>SharedAllowedPrefixes</c>: у этих неймспейсов нет своего стоража
    /// «вертикаль → мир», они часть платформы (см. шапку <see cref="SubsystemBoundaryTests"/>).
    /// </summary>
    [Fact]
    public void AllServicesNamespaces_AreCoveredByBoundaryOrExclusion()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name;
                return name is not null
                    && (name == "ClaudeHomeServer" || name.StartsWith("ClaudeHomeServer.", StringComparison.Ordinal))
                    && !name.EndsWith(".Tests", StringComparison.Ordinal);
            })
            .ToList();

        var allTypes = assemblies.SelectMany(a => a.GetTypes()).ToList();

        // Защита от вакуумного прохода: если фильтр сборок или порядок загрузки сломается,
        // набор станет пустым и сторож пройдёт зелёным, ничего не проверив.
        assemblies.Should().HaveCountGreaterThanOrEqualTo(2,
            "сторож должен видеть минимум ClaudeHomeServer и ClaudeHomeServer.Core");
        allTypes.Should().NotBeEmpty(
            "сборки должны отдавать типы — иначе проверка полноты неймспейсов ничего не проверяет");

        // Все top-level namespace Services.*, в которых есть хотя бы один «настоящий» тип.
        // Отбрасываем nested-типы (`Foo+Bar` — часть родителя, считается через родителя)
        // и compiler-generated типы (`<` в имени — async state-машины, лямбды, кэши
        // анонимных типов). Иначе покрытие «всплывёт» из мусорных имён и заставит
        // заводить строки на namespace, в котором ничего публичного нет.
        var namespacesWithTypes = allTypes
            .Where(t => t.Namespace != null
                        && t.Namespace.StartsWith("ClaudeHomeServer.Services.", StringComparison.Ordinal))
            .Where(t => !(t.FullName?.Contains('+') ?? false))
            .Where(t => !(t.Name?.Contains('<') ?? false))
            .Select(t => t.Namespace!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        namespacesWithTypes.Should().NotBeEmpty(
            "в ClaudeHomeServer.Services.* должны быть namespace с типами (ориентир замера — ~25) — " +
            "иначе проверка границ ничего не проверяет");

        // Заведомые исключения: «спинка» из SharedAllowedPrefixes. Эти неймспейсы
        // НЕ имеют своих сторожей, потому что их типы — общий код сервисов
        // (HTTP-утилиты, контракт IAppSubsystem, MCP-секреты), и они перечислены
        // в AllowedNamespacePrefixes каждой вертикали. Добавлять сюда вертикаль —
        // значит сознательно выводить её из-под контроля границ; повод завести
        // отдельную задачу, а не правило в тесте.
        // `ClaudeHomeServer.Services.Mcp` (и его потомки `.Catalog`, `.Http`) — общий
        // слой MCP-инфраструктуры (ADR-014): «спинка» для всех вертикалей, не
        // самостоятельная вертикаль. Сознательный default-deny-exception: под
        // Services.Mcp живут служебные пакеты вроде McpToolsetRegistry, McpSecretStore,
        // McpCatalogClient — ими пользуются многие вертикали через префикс.
        var excludedNamespaces = new[] {
            "ClaudeHomeServer.Services.Http",         // HTTP-утилиты, QuietHttpLogger
            "ClaudeHomeServer.Services.Composition",  // контракт IAppSubsystem
            "ClaudeHomeServer.Services.Mcp",          // MCP-инфраструктура (ADR-014)
        };

        // Все namespace, покрытые через SubsystemBoundaryTests.Boundaries (по полю
        // NamespaceRoot) + вертикали без подсистемы (Watchdog). Граница снимается
        // не по одному типу, а по корню: всё `Services.Llm.*` живёт под
        // `Services.Llm`, поэтому одной строки в Boundaries хватает.
        var boundaryRoots = SubsystemBoundaryTests.Boundaries
            .Select(o => ((SubsystemBoundaryTests.VerticalBoundary)o[0]).NamespaceRoot)
            .ToHashSet(StringComparer.Ordinal);

        // Дубль `verticalOnlyNamespaces` для Watchdog/Terminal убран (волна 4B шаг 1):
        // обе вертикали уже имеют строки в `Boundaries`, отдельная запись тут
        // маскировала бы удаление строки из `Boundaries`. То же для Team (этап 4,
        // шаг 2в) — вертикаль штаба без IAppSubsystem получила собственную строку
        // в `Boundaries`, и отдельная запись в этом списке маскировала бы её
        // удаление. Пустой список остаётся как явный сигнал «вертикалей без
        // строки в Boundaries нет».
        var verticalOnlyNamespaces = new HashSet<string>(StringComparer.Ordinal);

        var coveredNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in boundaryRoots) coveredNamespaces.Add(ns);
        foreach (var ns in verticalOnlyNamespaces) coveredNamespaces.Add(ns);

        // Префиксное сопоставление: namespace `Services.CodeGraph.Roslyn` покрывается
        // корнем `Services.CodeGraph`, `Services.Llm.Claude` — корнем `Services.Llm`.
        // То же для исключений: `Services.Mcp.Catalog` и `Services.Mcp.Http` покрыты
        // исключением `Services.Mcp`. Без префиксного сопоставления вложенные
        // неймспейсы ложно светились бы «непокрытыми» и тест не зеленел бы.
        bool IsCovered(string ns) =>
            coveredNamespaces.Any(p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal));

        bool IsExcluded(string ns) =>
            excludedNamespaces.Any(p => ns == p || ns.StartsWith(p + ".", StringComparison.Ordinal));

        var uncovered = namespacesWithTypes
            .Where(ns => !IsCovered(ns) && !IsExcluded(ns))
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToList();

        uncovered.Should().BeEmpty(
            "каждый namespace ClaudeHomeServer.Services.* с типами обязан иметь строку в " +
            "SubsystemBoundaryTests.Boundaries (см. комментарии у Video/Reader/.../Llm). " +
            "Если вертикаль новая — добавь её в Boundaries со своим allow-list " +
            "(вертикаль без IAppSubsystem — тоже отдельная строка). " +
            "`verticalOnlyNamespaces` ниже сознательно пуст и не для этого: он " +
            "маскировал бы удаление строки из Boundaries, доказано мутацией в ревью " +
            "этапа 4 (MAJOR 1, сентябрь 2026) — 36/36 зелёных при заведомом ребре " +
            "`Services.Team → Services.Video`, default-deny не срабатывал. " +
            "Если namespace — общая «спинка» — добавь в `excludedNamespaces` (и подумай, " +
            "правда ли это спинка). Не покрыты:\n" + string.Join("\n", uncovered));
    }
}