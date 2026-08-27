using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта заметок (по образцу MemoryToolsetParityTests, ADR-012 фаза 2
/// волна 2). Схемы и тексты продублированы в NotesToolset.cs (http-ветка) и
/// mcp/notes-server/index.js (stdio-ветка отката) — обе живые, правка обязана ехать парой.
/// Источник контракта — NotesToolset.cs (index.js заморожен).
///
/// Состав сверяется с ЖИВЫМ stdio-сервером по оси NOTES_ANNOTATIONS: ядро (7 инструментов)
/// всегда, модуль комментариев и редких операций (+12) — по привязке персоны.
/// </summary>
public class NotesToolsetParityTests
{
    private static string JsPath => RepoFile("mcp", "notes-server", "index.js");

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var path = dir is null ? null : Path.Combine([dir.FullName, .. parts]);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException(
                $"не найден {Path.Combine(parts)} — сторож парности не может работать");
        return path;
    }

    private static readonly Lazy<string> Js = new(() => File.ReadAllText(JsPath));

    private static IReadOnlyList<string>? ListStdioTools(params (string Key, string Value)[] env)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "notes-server", "index.js");
            if (File.Exists(candidate)) break;
            dir = dir.Parent;
        }
        Skip.If(dir is null, "mcp/notes-server/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { Path.Combine(dir!.FullName, "mcp", "notes-server", "index.js") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["NOTES_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["NOTES_API_TOKEN"] = "test";
        foreach (var (key, value) in env) psi.Environment[key] = value;

        System.Diagnostics.Process? proc;
        try { proc = System.Diagnostics.Process.Start(psi); }
        catch (Exception ex) { Skip.If(true, $"node недоступен: {ex.Message}"); return null; }
        Skip.If(proc is null, "не удалось запустить node");

        using (proc!)
        {
            proc.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
            proc.StandardInput.Flush();

            var line = proc.StandardOutput.ReadLine();
            proc.StandardInput.Close();
            if (!proc.WaitForExit(10_000)) proc.Kill(entireProcessTree: true);

            line.Should().NotBeNullOrWhiteSpace("сервер обязан ответить на tools/list");
            using var doc = JsonDocument.Parse(line!);
            return doc.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .ToList();
        }
    }

    /// <summary>Ядро заметок (модуль выключен): 7 инструментов в порядке stdio-ветки.</summary>
    [SkippableFact]
    public void Ядро_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("NOTES_ANNOTATIONS", "0"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(NotesToolset.CoreTools.Select(t => t.Name).ToList(),
            options => options.WithStrictOrdering(),
            "ядро заметок обязано совпадать с stdio-веткой отката");
    }

    /// <summary>Модуль комментариев включён: ядро + 12 модульных в том же порядке.</summary>
    [SkippableFact]
    public void ПолныйСостав_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("NOTES_ANNOTATIONS", "1"));
        if (stdio is null) return;
        var expected = NotesToolset.CoreTools.Select(t => t.Name)
            .Concat(NotesToolset.AnnotationTools.Select(t => t.Name)).ToList();
        stdio.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы из JS-литералов обязаны совпадать с C#-схемами по каждому
    /// инструменту — ветки валидируют аргументы одинаково.
    /// </summary>
    [Fact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var all = NotesToolset.CoreTools.Concat(NotesToolset.AnnotationTools).ToList();

        foreach (var tool in all)
        {
            var blockStart = Js.Value.IndexOf($"name: '{tool.Name}'", StringComparison.Ordinal);
            blockStart.Should().BeGreaterThan(0, $"инструмент {tool.Name} обязан быть в stdio-ветке");
            var next = Js.Value.IndexOf("name: '", blockStart + 10, StringComparison.Ordinal);
            if (next < 0) next = Js.Value.Length;
            var block = Js.Value[blockStart..next];
            var requiredMatch = Regex.Match(block, @"required:\s*\[([^\]]*)\]");
            var jsRequired = requiredMatch.Success
                ? Regex.Matches(requiredMatch.Groups[1].Value, "'([^']+)'")
                    .Select(m => m.Groups[1].Value).ToList()
                : [];
            var csharpRequired = tool.InputSchema["required"]?.AsArray()
                .Select(n => n!.GetValue<string>()).ToList() ?? [];
            jsRequired.Should().BeEquivalentTo(csharpRequired,
                options => options.WithStrictOrdering(),
                $"required-набор {tool.Name} не должен расходиться между ветками");
        }
    }

    /// <summary>Хвост маршрута — та же точка правды, что у tasks/memory.</summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяОднойФормой()
    {
        NotesToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/notes/sess-1");
    }
}
