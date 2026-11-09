using System.Xml.Linq;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторожи сборки (шаг 1.4, фича «тесты по модулям»): держат инварианты, которые шаг 1.3
// довёл вручную, но которые легко сломать молча при следующем переносе.
//
//   1. Ни один .csproj не ссылается на монолит ClaudeHomeServer.Tests.csproj —
//      иначе «модульные» тесты снова тащат монолитную сборку (и её 15 тыс. тестов)
//      при любой правке.
//   2. TestKit (шаг 1.1) не ссылается на Main (ClaudeHomeServer.csproj) — TestKit
//      компилируется только против Core; ссылка на Main вернула бы монолитную
//      зависимость в «чистую» спинку.
//   3. Каждый *.Tests.csproj под backend/ есть в ClaudeHomeServer.slnx — новый
//      тест-проект, добавленный в шаге 3+, не должен пропасть из CI молча (CI гоняет slnx).
//
// Ключевой приём: парсим .csproj/slnx как XML (XDocument) и фильтруем по ИМЕНИ элемента
// (Reference/ProjectReference/Project), а не по тексту файла. Простой File.ReadAllText().Contains
// ложно реагировал на XML-комментарии и на InternalsVisibleTo (`<_Parameter1>ClaudeHomeServer.Tests</_Parameter1>`)
// — строка «ClaudeHomeServer.Tests» сидит в ~50 csproj, но почти нигде как реальная ссылка.
public class MonolithReferenceGuardTests
{
    private const string MonolithName = "ClaudeHomeServer.Tests.csproj";
    private const string MainName = "ClaudeHomeServer.csproj";

    // ─────────────────────────── Guard 1 ───────────────────────────

    [Fact]
    public void Ни_один_csproj_не_ссылается_на_монолит()
    {
        var backend = FindBackendDir();
        var violations = new List<string>();

        foreach (var csproj in Directory.EnumerateFiles(backend, "*.csproj", SearchOption.AllDirectories))
        {
            // Монолит не ссылается на себя — исключаем, чтобы не ловить сам себя.
            if (Path.GetFileName(csproj) == MonolithName)
                continue;

            foreach (var include in GetReferences(csproj))
            {
                if (PointsTo(include, MonolithName))
                    violations.Add($"{Path.GetFileName(csproj)} → {include}");
            }
        }

        violations.Should().BeEmpty(
            "ссылка на монолит ClaudeHomeServer.Tests.csproj вернулась — «модульные» тесты " +
            "снова тащат монолитную сборку; список найденных ссылок: " + string.Join("; ", violations));
    }

    // ─────────────────────────── Guard 2 ───────────────────────────

    [Fact]
    public void TestKit_не_ссылается_на_Main()
    {
        var backend = FindBackendDir();
        var csproj = Path.Combine(backend, "ClaudeHomeServer.TestKit", "ClaudeHomeServer.TestKit.csproj");
        File.Exists(csproj).Should().BeTrue($"сторож обязан видеть настоящий файл, а не проходить вакуумно: {csproj}");

        var mainRefs = GetReferences(csproj)
            .Where(include => PointsTo(include, MainName))
            .ToList();

        mainRefs.Should().BeEmpty(
            "TestKit (шаг 1.1) должен компилироваться только против Core, без Main; " +
            "найдена ссылка на ClaudeHomeServer.csproj: " + string.Join("; ", mainRefs));
    }

    // ─────────────────────────── Guard 3 ───────────────────────────

    [Fact]
    public void Каждый_Tests_csproj_есть_в_slnx()
    {
        var backend = FindBackendDir();
        var slnx = Path.Combine(backend, "ClaudeHomeServer.slnx");
        File.Exists(slnx).Should().BeTrue($"сторож обязан видеть настоящий файл, а не проходить вакуумно: {slnx}");

        var slnxPaths = XDocument.Load(slnx).Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => (string?)e.Attribute("Path"))
            .Where(p => p is not null)
            .Select(p => p!.Replace('\\', '/'))
            .ToHashSet();

        var missing = new List<string>();
        foreach (var csproj in Directory.EnumerateFiles(backend, "*.Tests.csproj", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backend, csproj).Replace('\\', '/');
            if (!slnxPaths.Contains(rel))
                missing.Add(rel);
        }

        missing.Should().BeEmpty(
            "новый *.Tests.csproj отсутствует в ClaudeHomeServer.slnx — CI (гоняет slnx) " +
            "пропустит его молча; отсутствующие: " + string.Join("; ", missing));
    }

    // ─────────────────────────── Хелперы ───────────────────────────

    // Все Include у <Reference> и <ProjectReference> в csproj. XML-комментарии и
    // InternalsVisibleTo (другие элементы) не проходят фильтр по имени.
    private static IEnumerable<string> GetReferences(string csproj)
    {
        foreach (var e in XDocument.Load(csproj).Descendants()
                     .Where(e => e.Name.LocalName is "Reference" or "ProjectReference"))
        {
            var include = (string?)e.Attribute("Include");
            if (include is not null)
                yield return include;
        }
    }

    // Путь (относительный или абсолютный, с \ или /) указывает на файл с данным именем.
    private static bool PointsTo(string include, string fileName)
        => Path.GetFileName(include.Replace('\\', '/')) == fileName;

    // Подъём от каталога сборки тестов: число уровней у разных раннеров разное, а .git в
    // worktree бывает файлом. Тот же приём, что в CorePackageReferenceGuardTests.
    private static string FindBackendDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var backend = Path.Combine(dir.FullName, "backend");
            if (File.Exists(Path.Combine(backend, "ClaudeHomeServer.Core", "ClaudeHomeServer.Core.csproj")))
                return backend;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Не найден backend/ClaudeHomeServer.Core при подъёме от " + AppContext.BaseDirectory +
            ": сторожи ссылок молча пропускать проверку не имеют права.");
    }
}
