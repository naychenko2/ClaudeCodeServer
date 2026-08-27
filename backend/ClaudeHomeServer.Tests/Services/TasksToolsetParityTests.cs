using System.Text.Json;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта задач (по образцу MemoryToolsetParityTests, ADR-012 фаза 2
/// волна 2). Схемы и тексты продублированы в TasksToolset.cs (http-ветка) и
/// mcp/tasks-server/index.js (stdio-ветка отката по рубильнику Mcp:HttpTransport) — обе живые,
/// и правка только в C# прошла бы зелёные тесты, молча разойдясь со второй веткой.
/// Источник контракта — TasksToolset.cs (index.js заморожен, см. его шапку).
///
/// Сильнее посимвольного сравнения: состав и required-наборы сверяются с ЖИВЫМ stdio-сервером —
/// node с env TASKS_EXECUTE противпадает группам тулсета. Осей две: execute (12 без run_executor /
/// 13 с) и контекст чата (проект/личные — состав один, различаются описания; состав
/// сверяем по обеим группам тулсета). Сверка по данным ответа, без regex-скрейпинга JS
/// (техдолг MCP-over-HTTP §6).
/// </summary>
public class TasksToolsetParityTests
{
    private record StdioTool(string Name, JsonElement Schema);

    // tools/list живого stdio-сервера с заданным env: бэкенд не нужен (состав считается из
    // env, в сеть сервер не ходит). null — node недоступен.
    private static IReadOnlyList<StdioTool>? ListStdioTools(params (string Key, string Value)[] env)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "tasks-server", "index.js");
            if (File.Exists(candidate)) break;
            dir = dir.Parent;
        }
        Skip.If(dir is null, "mcp/tasks-server/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { Path.Combine(dir!.FullName, "mcp", "tasks-server", "index.js") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["TASKS_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["TASKS_API_TOKEN"] = "test";
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

    /// <summary>
    /// Основная ось (продуктовый режим, TASKS_EXECUTE=1): 13 инструментов, включая
    /// tasks_run_executor — совпадает с обеими группами тулсета посимвольно, включая порядок.
    /// </summary>
    [SkippableFact]
    public void Состав_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("TASKS_PROJECT_ID", "proj-1"), ("TASKS_EXECUTE", "1"));
        if (stdio is null) return;
        var expected = TasksToolset.ProjectChatTools.Select(t => t.Name).ToList();
        stdio.Select(t => t.Name).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering(),
            "состав обязан совпадать с stdio-веткой отката");
        // Контекст (проект/личные) состав не меняет — только описания
        TasksToolset.PersonalTools.Select(t => t.Name).Should().BeEquivalentTo(expected,
            options => options.WithStrictOrdering(),
            "контекст чата меняет описания, но не состав");
    }

    /// <summary>
    /// Аварийная ось (TASKS_EXECUTE=0, прямые запуски сервера): run_executor исчезает
    /// из stdio-состава. У http-ветки этой оси нет и это осознанно: в продукте execute
    /// всегда включён, а анти-рекурсию решает гейт на вызове (ADR-012, фиксация флагов).
    /// </summary>
    [SkippableFact]
    public void АварийнаяОсь_БезRunExecutor_СоставСовпадаетМинусОдин()
    {
        var stdio = ListStdioTools(("TASKS_PROJECT_ID", "proj-1"), ("TASKS_EXECUTE", "0"));
        if (stdio is null) return;
        var expected = TasksToolset.ProjectChatTools.Select(t => t.Name)
            .Where(n => n != "tasks_run_executor").ToList();
        stdio.Select(t => t.Name).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы ЖИВОГО stdio-ответа обязаны совпадать с C#-схемами по каждому
    /// инструменту. Промах означал бы, что ветки валидируют аргументы по-разному.
    /// </summary>
    [SkippableFact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var stdio = ListStdioTools(("TASKS_PROJECT_ID", "proj-1"), ("TASKS_EXECUTE", "1"));
        if (stdio is null) return;
        var byName = TasksToolset.ProjectChatTools.ToDictionary(t => t.Name);

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

    /// <summary>Хвост маршрута — та же точка правды, что у memory: форма не может разъехаться.</summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяОднойФормой()
    {
        TasksToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/tasks/sess-1");
    }
}
