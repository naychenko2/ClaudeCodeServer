using System.Xml.Linq;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож формы динамических модулей (ADR-018 §10.1): ModuleLoader грузит сборку модуля в
// дефолтный контекст, зависимости резолвятся по deps.json Main. Пакета, которого нет в
// замыкании Main, в рантайме просто не будет — модуль упадёт при первом обращении к типу.
// Поэтому каждый PackageReference модуля обязан быть в замыкании пакетов Main: его собственных
// и пакетов проектов, на которые Main ссылается обычным образом (без ReferenceOutputAssembly=false).
// Версию не сверяем: побеждает версия Main, а расхождение ловит сама сборка NuGet.
public class DynamicModulePackagesGuardTests
{
    // Динамические модули — записи DynamicModules с собственной сборкой
    public static TheoryData<string> Modules => new()
    {
        "ClaudeHomeServer.Notes",
        "ClaudeHomeServer.Spend",
        "ClaudeHomeServer.ImageEditor",
    };

    [Theory]
    [MemberData(nameof(Modules))]
    public void Пакеты_модуля_входят_в_замыкание_пакетов_Main(string module)
    {
        var backend = BackendDir();
        var mainClosure = PackageClosure(Path.Combine(backend, "ClaudeHomeServer", "ClaudeHomeServer.csproj"));
        var modulePackages = Packages(Path.Combine(backend, module, module + ".csproj"));

        modulePackages.Except(mainClosure, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            $"{module} грузится в дефолтный контекст по deps.json Main: пакет вне замыкания Main " +
            "в рантайме не найдётся. Нужен пакет — кладите его в Main или в обычную вертикаль, не в модуль");
    }

    [Fact]
    public void Замыкание_Main_видит_пакеты_вертикалей_а_не_только_свои()
    {
        // Без обхода ProjectReference сторож проходил бы вакуумно по пустому множеству
        var closure = PackageClosure(Path.Combine(BackendDir(), "ClaudeHomeServer", "ClaudeHomeServer.csproj"));
        closure.Should().Contain("Microsoft.AspNetCore.Authentication.JwtBearer").And.Contain("SkiaSharp");
    }

    private static HashSet<string> PackageClosure(string csproj)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([Path.GetFullPath(csproj)]);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path) || !File.Exists(path)) continue;
            result.UnionWith(Packages(path));
            foreach (var reference in Items(path, "ProjectReference"))
            {
                // Динамический модуль задаёт только порядок сборки, в deps.json Main его пакеты не едут
                if (string.Equals((string?)reference.Attribute("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase))
                    continue;
                var include = ((string?)reference.Attribute("Include") ?? "").Replace('\\', Path.DirectorySeparatorChar);
                queue.Enqueue(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, include)));
            }
        }
        return result;
    }

    private static HashSet<string> Packages(string csproj) =>
        Items(csproj, "PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<XElement> Items(string csproj, string name) =>
        XDocument.Load(csproj).Descendants().Where(e => e.Name.LocalName == name);

    private static string BackendDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.EnumerateFiles(dir.FullName, "*.slnx").Any())
            dir = dir.Parent;
        return dir!.FullName;
    }
}
