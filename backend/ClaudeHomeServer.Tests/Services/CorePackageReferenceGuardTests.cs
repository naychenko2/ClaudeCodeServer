using System.Xml.Linq;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож «Core без пакетов» (ADR-014, ADR-018 §9): общая спина ClaudeHomeServer.Core не
// тянет ни одного NuGet-пакета — иначе пакет вертикали (SkiaSharp у Images) незаметно
// растекается по всем сборкам через Core. Раньше правило держало только ревью.
// Источник правды — сам .csproj; Directory.Build.props пакетов в Core тоже не добавляет.
public class CorePackageReferenceGuardTests
{
    [Fact]
    public void В_ClaudeHomeServer_Core_csproj_нет_PackageReference()
    {
        var csproj = Path.Combine(FindBackendDir(), "ClaudeHomeServer.Core", "ClaudeHomeServer.Core.csproj");
        File.Exists(csproj).Should().BeTrue($"сторож обязан видеть настоящий файл, а не проходить вакуумно: {csproj}");

        var packages = XDocument.Load(csproj).Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion")
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "?")
            .ToList();

        packages.Should().BeEmpty(
            "ClaudeHomeServer.Core — спина без пакетов; пакет вертикали кладётся в её собственный .csproj");
    }

    // Подъём от каталога сборки тестов: число уровней у разных раннеров разное, а .git в
    // worktree бывает файлом
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
            ": сторож «Core без пакетов» молча пропускать проверку не имеет права.");
    }
}
