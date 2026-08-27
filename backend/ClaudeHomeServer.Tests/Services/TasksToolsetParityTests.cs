using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Сильнее посимвольного сравнения: состав сверяется с ЖИВЫМ stdio-сервером — node с env
/// TASKS_EXECUTE противпадает группам тулсета. Осей две: execute (12 без run_executor /
/// 13 с) и контекст чата (проект/личные — состав один, различаются описания; состав
/// сверяем по обеим группам тулсета).
/// </summary>
public class TasksToolsetParityTests
{
    private static string JsPath => RepoFile("mcp", "tasks-server", "index.js");

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

    // tools/list живого stdio-сервера с заданным env: бэкенд не нужен (состав считается из
    // env, в сеть сервер не ходит). null — node недоступен.
    private static IReadOnlyList<string>? ListStdioTools(params (string Key, string Value)[] env)
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
                .Select(t => t.GetProperty("name").GetString()!)
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
        stdio.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering(),
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
        stdio.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы из JS-литералов обязаны совпадать с C#-схемами по каждому
    /// инструменту. Промах означал бы, что ветки валидируют аргументы по-разному.
    /// </summary>
    [Fact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var all = TasksToolset.ProjectChatTools;

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

    /// <summary>Хвост маршрута — та же точка правды, что у memory: форма не может разъехаться.</summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяОднойФормой()
    {
        TasksToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/tasks/sess-1");
    }
}
