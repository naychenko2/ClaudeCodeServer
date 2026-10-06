using System.Text.Json;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож фронтовой записи динамических модулей: модуль, у которого есть MF-remote в frontend/modules/<имя>/, обязан
// иметь в DynamicModules блок Frontend. Без него /api/subsystem-modules не отдаёт remote, и панели модуля нет даже
// с включённым флагом (волна правок «Видео», B1: у videoeditor блок забыли, у audioeditor он был). Сверка идёт по
// каталогу remote, а не по списку: новый модуль с фронтом не пройдёт мимо сторожа.
public class DynamicModuleFrontendGuardTests
{
    private static readonly string Repo = FindRepoRoot();

    // Модуль → каталог remote: имя каталога берётся из AssemblyPath («modules/video-editor/….dll» → «video-editor»)
    private static IReadOnlyList<(string Key, string Dir, JsonElement Entry)> Modules()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "backend", "ClaudeHomeServer", "appsettings.json")));
        return doc.RootElement.GetProperty("DynamicModules").EnumerateArray()
            .Where(e => e.TryGetProperty("Backend", out _))
            .Select(e =>
            {
                var assembly = e.GetProperty("Backend").GetProperty("AssemblyPath").GetString()!;
                return (e.GetProperty("Key").GetString()!, assembly.Split('/')[1], e.Clone());
            })
            .ToList();
    }

    public static TheoryData<string> WithRemote()
    {
        var data = new TheoryData<string>();
        foreach (var (key, dir, _) in Modules())
            if (HasRemote(dir)) data.Add(key);
        return data;
    }

    private static bool HasRemote(string dir) =>
        File.Exists(Path.Combine(Repo, "frontend", "modules", dir, "subsystem.tsx"));

    [Fact]
    public void Сторож_видит_модули_с_remote_а_не_проходит_вакуумно()
    {
        Modules().Where(m => HasRemote(m.Dir)).Select(m => m.Key).Should().Contain(["videoeditor", "audioeditor", "imageeditor"]);
    }

    [Theory]
    [MemberData(nameof(WithRemote))]
    public void У_динамического_модуля_с_remote_есть_блок_Frontend(string key)
    {
        var (_, dir, entry) = Modules().Single(m => m.Key == key);

        entry.TryGetProperty("Frontend", out var frontend).Should().BeTrue(
            $"у «{key}» есть frontend/modules/{dir}, но в DynamicModules нет блока Frontend: панель не подключится");
        frontend.GetProperty("RemoteUrl").GetString().Should().Be($"/{dir}-remote/remoteEntry.js");
        frontend.GetProperty("ExposedModule").GetString().Should().Be("./subsystem");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "frontend", "modules"))
                && File.Exists(Path.Combine(dir.FullName, "backend", "ClaudeHomeServer", "appsettings.json")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Корень репозитория не найден выше каталога тестов");
    }
}
