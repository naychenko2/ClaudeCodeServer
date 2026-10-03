using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Films;

// «Видео никогда не читает data/image-threads и data/audio-threads напрямую» (ADR-022 §3, ADR-014): соседей модуль
// видит только через швы и события Core. Типовую границу держит SubsystemBoundaryTests; здесь — то, чего IL-скан по
// типам не видит: строковые пути хранилищ соседей и их типы в тексте кода.
public sealed class VideoModuleIsolationGuardTests
{
    private static readonly string[] Forbidden =
    [
        "image-threads", "audio-threads", "ImageThread", "AudioThread", "Services.ImageEditor", "Services.AudioEditor",
    ];

    [Fact]
    public void Сборка_модуля_не_ссылается_на_редакторы_картинок_и_звука()
    {
        var references = typeof(VideoEditorSubsystem).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().NotContain(n => n.Contains("ImageEditor") || n.Contains("AudioEditor") || n.Contains("Images"),
            "соседей модуль видит только через швы Core");
    }

    [Fact]
    public void В_коде_модуля_нет_путей_и_типов_хранилищ_соседей()
    {
        var root = ModuleRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar) || relative.StartsWith("bin" + Path.DirectorySeparatorChar)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Комментарии объясняют запрет и называют соседей — смотрим только код
                var code = Regex.Replace(lines[i], @"//.*$", "");
                foreach (var token in Forbidden.Where(code.Contains))
                    offenders.Add($"{relative}:{i + 1} — «{token}»");
            }
        }

        offenders.Should().BeEmpty("модуль «Видео» не читает хранилища нитей картинок и звука напрямую");
    }

    private static string ModuleRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "backend", "ClaudeHomeServer.VideoEditor");
            if (Directory.Exists(candidate)) return candidate;
            candidate = Path.Combine(dir.FullName, "ClaudeHomeServer.VideoEditor");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "ClaudeHomeServer.VideoEditor.csproj"))) return candidate;
        }
        throw new DirectoryNotFoundException("Каталог ClaudeHomeServer.VideoEditor не найден выше каталога тестов");
    }
}
