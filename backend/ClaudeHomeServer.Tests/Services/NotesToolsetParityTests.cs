using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта заметок (по образцу MemoryToolsetParityTests, ADR-012 фаза 2
/// волна 2). Схемы и тексты продублированы в NotesToolset.cs (http-ветка) и
/// mcp/notes-server/index.js (stdio-ветка отката) — обе живые, правка обязана ехать парой.
/// Источник контракта — NotesToolset.cs (index.js заморожен).
///
/// Состав и required-наборы сверяются с ЖИВЫМ stdio-сервером по оси NOTES_ANNOTATIONS:
/// ядро (7 инструментов) всегда, модуль комментариев и редких операций (+12) — по привязке
/// персоны. Сверка по данным ответа, без regex-скрейпинга JS (техдолг MCP-over-HTTP §6).
/// </summary>
public class NotesToolsetParityTests
{
    private record StdioTool(string Name, JsonElement Schema);

    private static IReadOnlyList<StdioTool>? ListStdioTools(params (string Key, string Value)[] env)
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
                .Select(t => new StdioTool(
                    t.GetProperty("name").GetString()!,
                    t.GetProperty("inputSchema").Clone()))
                .ToList();
        }
    }

    /// <summary>Ядро заметок (модуль выключен): 7 инструментов в порядке stdio-ветки.</summary>
    [SkippableFact]
    public void Ядро_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("NOTES_ANNOTATIONS", "0"));
        if (stdio is null) return;
        stdio.Select(t => t.Name).Should().BeEquivalentTo(NotesToolset.CoreTools.Select(t => t.Name).ToList(),
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
        stdio.Select(t => t.Name).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы ЖИВОГО stdio-ответа обязаны совпадать с C#-схемами по каждому
    /// инструменту — ветки валидируют аргументы одинаково.
    /// </summary>
    [SkippableFact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var stdio = ListStdioTools(("NOTES_ANNOTATIONS", "1"));
        if (stdio is null) return;
        var byName = NotesToolset.CoreTools.Concat(NotesToolset.AnnotationTools)
            .ToDictionary(t => t.Name);

        foreach (var tool in stdio)
        {
            var csharp = byName.GetValueOrDefault(tool.Name);
            csharp.Should().NotBeNull($"инструмент {tool.Name} обязан быть в http-ветке");
            var stdioRequired = tool.Schema.TryGetProperty("required", out var required)
                ? required.EnumerateArray().Select(n => n.GetString()!).ToList()
                : [];
            var csharpRequired = csharp!.InputSchema["required"]?.AsArray()
                .Select(n => n!.GetValue<string>()).ToList() ?? [];
            stdioRequired.Should().BeEquivalentTo(csharpRequired,
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
