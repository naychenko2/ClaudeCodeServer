namespace ClaudeHomeServer.Services.Architecture;

/// <summary>Связь сгенерированного элемента с другим (по ключу цели).</summary>
public sealed record GeneratedConnection(string TargetKey, string Label);

/// <summary>
/// Элемент сгенерированной модели. Key — путь элемента (каталог контейнера,
/// «контейнер::компонент»): по нему строится стабильный id и идёт слияние.
/// </summary>
public sealed record GeneratedElement(
    string Key,
    string Name,
    string? Description,
    string? Technology,
    string? ParentKey,
    IReadOnlyList<GeneratedConnection> Connections,
    // Внешняя система-кандидат L1: при создании получает external и тег «кандидат»
    bool External = false);

/// <summary>
/// Стартовая модель уровней система/контейнер/компонент плюс внешние системы-кандидаты L1
/// (соседи системы проекта на уровне systems).
/// </summary>
public sealed record GeneratedModel(
    GeneratedElement System,
    IReadOnlyList<GeneratedElement> Containers,
    IReadOnlyList<GeneratedElement> Components,
    IReadOnlyList<GeneratedElement>? Externals = null);

/// <summary>
/// Чистая функция «входы → стартовая модель»: без диска и часов, детерминирована
/// (все перечисления упорядочены), с потолками, чтобы канва Viaduct жила.
/// </summary>
public static class ArchitectureModelBuilder
{
    public const string SystemKey = "system";
    public const int MaxContainers = 60;
    public const int MaxComponentsPerContainer = 15;
    public const int MaxConnectionsPerComponent = 5;

    private const string DependsLabel = "зависит от";
    private const string UsesLabel = "использует";

    /// <summary>Тег невыверенной внешней системы: генератор ставит при создании и больше не трогает.</summary>
    public const string CandidateTag = "кандидат";

    /// <summary>Потолок строки источника в описании кандидата (как у описаний тулсета).</summary>
    public const int MaxCandidateDescription = 120;

    /// <summary>Ключ внешней системы-кандидата (по нему — стабильный id gen-system-…).</summary>
    public static string ExternalKey(string name) => "external:" + ArchitectureExternalScanner.NormalizeName(name);

    public static string ComponentKey(string container, string component) => container + "::" + component;

    public static GeneratedModel Build(ArchitectureInput input)
    {
        var units = input.Units;
        var folding = new ArchitecturePathFolding(units.Select(u => u.RelDir));
        var unitByDir = units
            .Where(u => u.RelDir.Length > 0)
            .ToDictionary(u => u.RelDir, StringComparer.OrdinalIgnoreCase);

        bool IsExcludedContainer(string key) =>
            unitByDir.TryGetValue(key, out var u) ? u.IsTest : ArchitecturePathFolding.IsTestPath(key);

        // 1. Свёртка типов: id типа → (контейнер, компонент).
        var placement = new Dictionary<string, (string Container, string Component)>(StringComparer.Ordinal);
        foreach (var t in input.Snapshot.Types)
        {
            if (string.IsNullOrWhiteSpace(t.SourceFile)) continue;
            var file = ArchitecturePathFolding.Normalize(t.SourceFile);
            if (ArchitecturePathFolding.IsIgnored(file) || ArchitecturePathFolding.IsTestPath(file)) continue;
            var place = folding.Fold(file);
            if (IsExcludedContainer(place.Container)) continue;
            placement[t.Id] = place;
        }

        var typesPerContainer = placement.Values
            .GroupBy(p => p.Container, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // 2. Контейнеры: все нетестовые проекты-маркеры + каталоги, где нашёлся код.
        var containerKeys = units.Where(u => !u.IsTest && u.RelDir.Length > 0).Select(u => u.RelDir)
            .Concat(typesPerContainer.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(k => typesPerContainer.GetValueOrDefault(k))
            .ThenBy(k => k, StringComparer.Ordinal)
            .Take(MaxContainers)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var containerSet = containerKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Подсистемы по местам: контейнер → заголовки, компонент → заголовки.
        var titlesByContainer = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var titlesByComponent = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (typeId, title) in input.SubsystemTitles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!placement.TryGetValue(typeId, out var p)) continue;
            Add(titlesByContainer, p.Container, title);
            Add(titlesByComponent, ComponentKey(p.Container, p.Component), title);
        }

        // 3. Компоненты: крупнейшие папки в контейнере, не больше потолка.
        var componentKeys = placement.Values
            .Where(p => containerSet.Contains(p.Container))
            .GroupBy(p => ComponentKey(p.Container, p.Component), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Key: g.Key, g.First().Container, g.First().Component, Count: g.Count()))
            .GroupBy(c => c.Container, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g
                .OrderByDescending(c => c.Count)
                .ThenBy(c => c.Component, StringComparer.Ordinal)
                .Take(MaxComponentsPerContainer))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
        var componentSet = componentKeys.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 4. Связи из рёбер графа, агрегированные до уровней.
        var containerEdges = new Dictionary<(string, string), int>();
        var componentEdges = new Dictionary<(string, string), int>();
        foreach (var e in input.Snapshot.Edges)
        {
            if (!placement.TryGetValue(e.Source, out var s) || !placement.TryGetValue(e.Target, out var t)) continue;
            if (!string.Equals(s.Container, t.Container, StringComparison.OrdinalIgnoreCase))
            {
                Bump(containerEdges, (s.Container, t.Container));
                continue;
            }
            var sk = ComponentKey(s.Container, s.Component);
            var tk = ComponentKey(t.Container, t.Component);
            if (!string.Equals(sk, tk, StringComparison.OrdinalIgnoreCase)) Bump(componentEdges, (sk, tk));
        }

        var containers = containerKeys.Select(key =>
        {
            unitByDir.TryGetValue(key, out var unit);
            var titles = titlesByContainer.GetValueOrDefault(key);
            var connections = new List<GeneratedConnection>();
            if (unit is not null)
                connections.AddRange(unit.References
                    .Where(r => containerSet.Contains(r) && !string.Equals(r, key, StringComparison.OrdinalIgnoreCase))
                    .Select(r => new GeneratedConnection(Canon(containerKeys, r), DependsLabel)));
            // Рёбра кода — только там, где нет ссылок проектов (они — правда для .NET).
            if (unit is null || unit.Technology != "csharp")
                connections.AddRange(containerEdges
                    .Where(p => Eq(p.Key.Item1, key) && containerSet.Contains(p.Key.Item2))
                    .OrderByDescending(p => p.Value).ThenBy(p => p.Key.Item2, StringComparer.Ordinal)
                    .Select(p => new GeneratedConnection(Canon(containerKeys, p.Key.Item2), UsesLabel)));

            return new GeneratedElement(
                key,
                unit?.Name ?? ArchitecturePathFolding.DisplayName(key),
                titles is { Count: 1 } ? titles[0] : null,
                // Без проекта-маркера технологию не угадываем — пусть решит человек.
                unit?.Technology,
                SystemKey,
                connections.DistinctBy(c => c.TargetKey, StringComparer.OrdinalIgnoreCase).ToList());
        }).ToList();

        var components = componentKeys.Select(c =>
        {
            var titles = titlesByComponent.GetValueOrDefault(c.Key);
            var connections = componentEdges
                .Where(p => Eq(p.Key.Item1, c.Key) && componentSet.Contains(p.Key.Item2))
                .OrderByDescending(p => p.Value).ThenBy(p => p.Key.Item2, StringComparer.Ordinal)
                .Take(MaxConnectionsPerComponent)
                .Select(p => new GeneratedConnection(p.Key.Item2, UsesLabel))
                .ToList();
            var container = containers.First(x => Eq(x.Key, c.Container));
            return new GeneratedElement(
                c.Key,
                // Файлы прямо в корне контейнера: «(корень)» у всех выглядел в навигаторе
                // дублями — называем по контейнеру. Ключ (и стабильный id) прежний
                c.Component == ArchitecturePathFolding.RootName && c.Container != ArchitecturePathFolding.RootName
                    ? container.Name
                    : c.Component,
                titles is null ? null : string.Join("; ", titles),
                container.Technology,
                container.Key,
                connections);
        }).ToList();

        // 5. Внешние системы-кандидаты L1 и связь «система проекта → кандидат».
        var externals = (input.Externals ?? [])
            .Take(ArchitectureExternalScanner.MaxExternalSystems)
            .Select(c => new GeneratedElement(
                ExternalKey(c.Name),
                c.Name,
                Truncate("Найдено: " + c.Source, MaxCandidateDescription),
                c.Technology,
                null,
                [],
                External: true))
            .DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var system = new GeneratedElement(SystemKey, input.SystemName, null, null, null,
            externals.Select(e => new GeneratedConnection(e.Key, UsesLabel)).ToList());
        return new GeneratedModel(system, containers, components, externals);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string Canon(List<string> keys, string key) =>
        keys.First(k => Eq(k, key));

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(value)) list.Add(value);
    }

    private static void Bump(Dictionary<(string, string), int> map, (string, string) key) =>
        map[key] = map.GetValueOrDefault(key) + 1;
}
