using System.Reflection;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож G8 (ADR-016, план §4): раннер в проектном контексте выбирается только через
/// <see cref="ILauncherFactory.ForProject"/>. <c>ForOwner</c> не знает про устройство проекта —
/// вызов в проектном месте молча исполнил бы локальный проект на сервере.
///
/// IL-скан всех сборок продукта: каждый вызов <c>ForOwner</c> обязан стоять в allow-list —
/// логический метод (тип верхнего уровня + метод; async-машины и замыкания сводятся к
/// своему методу) и ТОЧНОЕ число вызовов в нём. Новый вызов, в том числе в уже разрешённом
/// методе, краснеет. В allow-list — только места без проекта: системные one-shot, чат вне
/// проекта, среда владельца (реестр MCP, файлы хоста, профиль CLI) и файловая группа за
/// guard'ом 3.3, которую локальный проект не проходит.
/// </summary>
public class LauncherProjectContextGuardTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, (int Count, string Why)> Allowed = new()
    {
        ["ClaudeHomeServer.Services.Llm.OneShotClaudeRunner.RunCliAsync"] = (1, "системный one-shot, проекта нет"),
        ["ClaudeHomeServer.Services.Mcp.McpProbeService.ProbeStdioAsync"] = (1, "проба сервера из личного реестра MCP владельца"),
        ["ClaudeHomeServer.Controllers.HostFilesController.GetContent"] = (1, "файл хоста по пути владельца, вне проекта"),
        ["ClaudeHomeServer.Services.SessionManager.ResolveTasksApiUrl"] = (1,
            "адрес MCP для среды владельца; у раннера устройства адрес переписывается на сайдкар"),
        ["ClaudeHomeServer.Services.SessionManager.CwdForOwner"] = (1,
            "путь транскрипта в серверном профиле CLI; у локального проекта транскрипт на устройстве"),
        ["ClaudeHomeServer.Services.SessionManager.StartNewSessionAsync"] = (1, "ветка чата вне проекта"),
        ["ClaudeHomeServer.Services.SessionManager.EnsureProcessCoreAsync"] = (1, "ветка чата вне проекта"),
        ["ClaudeHomeServer.Services.Watchdog.WatchdogCommandRunner.RunAsync"] = (1, "сторож чата вне проекта"),
        ["ClaudeHomeServer.Services.TaskExecutionService.ResolveCategoryProfilesPath"] = (1, "задача без проекта"),
        // Файловая группа (ADR-016 §4): вход для локального проекта отказывает guard 3.3 (G1)
        // до GitService, поэтому сюда доходят только серверные файлы владельца
        ["ClaudeHomeServer.Services.Git.GitService.RunAsync"] = (1, "файловая группа за guard'ом 3.3"),
        ["ClaudeHomeServer.Services.Git.GitService.HostGitPath"] = (1, "файловая группа за guard'ом 3.3"),
        ["ClaudeHomeServer.Services.Git.GitService.WorktreeAddAsync"] = (1, "файловая группа за guard'ом 3.3"),
        ["ClaudeHomeServer.Services.Git.GitService.WorktreeListAsync"] = (1, "файловая группа за guard'ом 3.3"),
        ["ClaudeHomeServer.Services.Git.GitService.WorktreeRemoveAsync"] = (1, "файловая группа за guard'ом 3.3"),
    };

    private static readonly Lazy<Dictionary<string, int>> Hits = new(Scan);

    // Все сборки продукта из каталога тестов — загрузкой, а не AppDomain.GetAssemblies():
    // сборка, тип которой ещё не трогали, в домене не видна, и сторож прошёл бы вакуумно
    private static IEnumerable<Assembly> ProductAssemblies()
    {
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "ClaudeHomeServer*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
            Assembly asm;
            try { asm = Assembly.Load(AssemblyName.GetAssemblyName(path)); }
            catch { continue; }
            yield return asm;
        }
    }

    // Async-машина «Foo+<BarAsync>d__12», замыкание «Foo+<>c__DisplayClass3_0.<BarAsync>b__0»,
    // локальная функция «<BarAsync>g__Baz|3_0» — всё сводится к «Foo.BarAsync»
    internal static string LogicalCaller(MethodBase method)
    {
        var type = method.DeclaringType!;
        string? logical = GeneratedOwner(method.Name);
        while (type.DeclaringType is not null)
        {
            logical ??= GeneratedOwner(type.Name);
            type = type.DeclaringType;
        }
        return $"{type.FullName}.{logical ?? method.Name}";
    }

    private static string? GeneratedOwner(string name)
    {
        if (!name.StartsWith('<')) return null;
        var close = name.IndexOf('>');
        return close > 1 ? name[1..close] : null;
    }

    private static Dictionary<string, int> Scan()
    {
        var forOwner = typeof(ILauncherFactory).GetMethod(nameof(ILauncherFactory.ForOwner))!;
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var asm in ProductAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
            // Вложенные типы обходит сам AllMethodsWithNested — берём только верхний уровень
            foreach (var type in types.Where(t => t.DeclaringType is null))
                foreach (var method in BoundaryIlScanner.AllMethodsWithNested(type))
                    foreach (var called in BoundaryIlScanner.CalledMethods(method))
                    {
                        if (!IsForOwner(called, forOwner)) continue;
                        var key = LogicalCaller(method);
                        hits[key] = hits.GetValueOrDefault(key) + 1;
                    }
        }
        return hits;
    }

    // Вызов через интерфейс либо напрямую у реализации (LauncherFactory.ForOwner)
    private static bool IsForOwner(MethodBase called, MethodInfo forOwner) =>
        called == forOwner
        || called.Name == forOwner.Name
           && called.DeclaringType is { } t && t != typeof(ILauncherFactory)
           && typeof(ILauncherFactory).IsAssignableFrom(t);

    [Fact]
    public void ForOwner_ТолькоВМестахБезПроекта()
    {
        foreach (var (key, n) in Hits.Value.OrderBy(h => h.Key)) output.WriteLine($"{key}: {n}");

        var violations = Hits.Value
            .Where(h => !Allowed.TryGetValue(h.Key, out var a) || h.Value > a.Count)
            .Select(h => $"{h.Key}: {h.Value}")
            .ToList();

        violations.Should().BeEmpty(
            "в проектном контексте раннер выбирается через ILauncherFactory.ForProject (сторож G8, ADR-016); "
            + "место без проекта — строка в allow-list с причиной и точным числом вызовов");
    }

    [Fact]
    public void AllowList_НеПротух()
    {
        // Разрешение, под которым вызовов стало меньше, — дыра: туда молча встанет новый
        // проектный ForOwner. Заодно доказывает, что скан не вакуумный.
        var stale = Allowed
            .Where(a => Hits.Value.GetValueOrDefault(a.Key) != a.Value.Count)
            .Select(a => $"{a.Key}: ожидалось {a.Value.Count}, найдено {Hits.Value.GetValueOrDefault(a.Key)}")
            .ToList();

        stale.Should().BeEmpty();
    }

    [Theory]
    [InlineData("<StartAsync>d__12", "MoveNext", "StartAsync")]
    [InlineData("<>c__DisplayClass3_0", "<RunAsync>b__0", "RunAsync")]
    [InlineData("<>c", "<Build>g__Local|3_0", "Build")]
    public void LogicalCaller_СводитСгенерированноеКМетоду(string nested, string method, string expected)
    {
        // Имена компилятора проверяем на строках: у реальных типов тот же формат
        GeneratedOwner(method).Should().Be(method.StartsWith('<') ? expected : null);
        if (!method.StartsWith('<')) GeneratedOwner(nested).Should().Be(expected);
    }
}
