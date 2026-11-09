namespace ClaudeHomeServer.Services.CodeGraph;

// Таблица «каталог → сторож» для подраздела «Сторожа по пути» хвоста «Контракты»: какие тесты и проверки
// краснеют, если правка в каталоге нарушит контракт слоя. Модель узкого контекста про них не спрашивает —
// сторож приходит в постановку сам, а не от Марка на гейте после ревью.
// Источник правды — эта таблица; существование классов и методов проверяет тест КлассыИМетодыИзContractGuardMap_СуществуютВИсходникахСвоихПроектов (ConsumersTailContributorTests)
// (опечатка в имени сторожа даёт красный тест, а не молчаливо пустую подсказку).
internal static class ContractGuardMap
{
    // TestClass/TestMethod — имя теста в коде, TestProject — тестовый проект, где этот класс объявлен
    // (для проверки существования: ищем только в нём); у проверок без класса (npm-скрипты, ui_probe)
    // все три null, Title — человекочитаемое имя как оно пойдёт в хвост.
    internal sealed record Guard(string Title, string? TestClass = null, string? TestMethod = null, string? TestProject = null)
    {
        public static Guard Test(string testProject, string testClass, string? method = null) =>
            new(method is null ? testClass : $"{testClass}.{method}", testClass, method, testProject);
    }

    private const string GuardsTests = "ClaudeHomeServer.Guards.Tests";
    private const string MainTests = "ClaudeHomeServer.Tests";

    internal sealed record Entry(string Prefix, IReadOnlyList<Guard> Guards);

    internal static readonly IReadOnlyList<Entry> Entries =
    [
        new("backend/ClaudeHomeServer.Core/",
        [
            Guard.Test(GuardsTests, "SubsystemBoundaryTests", "CoreDll_СодержитТолькоРазрешённыеНеймспейсы"),
            Guard.Test(MainTests, "CorePackageReferenceGuardTests"),
        ]),
        new("backend/ClaudeHomeServer.",
        [
            Guard.Test(GuardsTests, "SubsystemBoundaryTests"),
            Guard.Test(GuardsTests, "SubsystemBoundaryCoverageTests"),
        ]),
        new("backend/ClaudeHomeServer/Controllers/",
        [
            Guard.Test(MainTests, "AnonymousEndpointsGuardTests"),
        ]),
        new("frontend/",
        [
            new("npm run lint:design"),
            new("ui_probe 360 и 1280 по маршруту правки"),
        ]),
    ];

    // Тестовые проекты и TestKit — не слой продукта: сторожей границ на них нет
    private static bool IsTestProject(string path)
    {
        var parts = path.Split('/');
        return parts.Length > 1 && parts[0] == "backend"
            && (parts[1].EndsWith(".Tests", StringComparison.Ordinal)
                || parts[1].EndsWith(".TestKit", StringComparison.Ordinal));
    }

    /// <summary>Сторожа файла: (каталог-ключ, сторож) по всем подошедшим строкам таблицы, без повторов.</summary>
    internal static IReadOnlyList<(string Prefix, Guard Guard)> For(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        if (IsTestProject(p)) return [];
        var result = new List<(string, Guard)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries.OrderByDescending(e => e.Prefix.Length))
        {
            if (!p.StartsWith(entry.Prefix, StringComparison.Ordinal)) continue;
            foreach (var g in entry.Guards)
                if (seen.Add(g.Title)) result.Add((entry.Prefix, g));
        }
        return result;
    }
}
