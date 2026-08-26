using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта памяти (по образцу WidgetsToolsetParityTests, ADR-012 фаза 2).
/// Схемы и тексты продублированы в MemoryToolset.cs (http-ветка) и mcp/memory-server/index.js
/// (stdio-ветка отката по рубильнику Mcp:HttpTransport) — обе живые, и правка только в C#
/// прошла бы зелёные тесты, молча разойдясь со второй веткой. Источник контракта —
/// MemoryToolset.cs (index.js заморожен, см. его шапку).
///
/// Сильнее посимвольного сравнения: состав сверяется с ЖИВЫМ stdio-сервером — node с env
/// (MEMORY_PERSONA_ID / MEMORY_PROJECT_ID / MEMORY_DOSSIER_TOOLS) противпадает группам
/// тулсета по тем же осям. Имена инструментов менять нельзя вообще: на них ссылаются
/// файлы агентов персон (mcp__pmem_&lt;handle&gt;__*).
/// </summary>
public class MemoryToolsetParityTests
{
    private static string JsPath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null
                   && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
                   && !File.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent;
            var path = dir is null
                ? null
                : Path.Combine(dir.FullName, "mcp", "memory-server", "index.js");
            if (path is null || !File.Exists(path))
                throw new InvalidOperationException("index.js stdio-ветки не найден — сторож парности не может работать");
            return path;
        }
    }

    private static readonly Lazy<string> Js = new(() => File.ReadAllText(JsPath));

    private static IReadOnlyList<string> GroupNames(IReadOnlyList<McpToolSchema> group) =>
        group.Select(t => t.Name).ToList();

    // tools/list живого stdio-сервера с заданными env (паттерн McpToolsetStabilityTests.
    // ListMemoryTools): бэкенд не нужен, состав считается из env, в сеть сервер не ходит.
    // null — node недоступен.
    private static IReadOnlyList<string>? ListStdioTools(params (string Key, string Value)[] env)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "memory-server", "index.js");
            if (File.Exists(candidate)) break;
            dir = dir.Parent;
        }
        Skip.If(dir is null, "mcp/memory-server/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { Path.Combine(dir!.FullName, "mcp", "memory-server", "index.js") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["MEMORY_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["MEMORY_API_TOKEN"] = "test";
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
    /// Ось personal (задана персона): состав stdio-ветки совпадает с PersonalTools тулсета
    /// — посимвольно, включая порядок (порядок виден модели в tools/list).
    /// </summary>
    [SkippableFact]
    public void PersonalСостав_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("MEMORY_PERSONA_ID", "p1"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(GroupNames(MemoryToolset.PersonalTools),
            options => options.WithStrictOrdering(),
            "личные инструменты обязаны совпадать с stdio-веткой отката");
    }

    /// <summary>Ось team (задан проект): +5 командных инструментов в том же порядке.</summary>
    [SkippableFact]
    public void TeamСостав_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("MEMORY_PERSONA_ID", "p1"), ("MEMORY_PROJECT_ID", "proj1"));
        if (stdio is null) return;
        var expected = GroupNames(MemoryToolset.PersonalTools)
            .Concat(GroupNames(MemoryToolset.TeamTools)).ToList();
        stdio.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Ось dossier (проект + MEMORY_DOSSIER_TOOLS=1): +2 инструмента паспортов.
    /// Это связка «проект чата + флаг владельца change-dossiers-recall» из приёмки.
    /// </summary>
    [SkippableFact]
    public void DossierСостав_СовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(
            ("MEMORY_PERSONA_ID", "p1"), ("MEMORY_PROJECT_ID", "proj1"), ("MEMORY_DOSSIER_TOOLS", "1"));
        if (stdio is null) return;
        var expected = GroupNames(MemoryToolset.PersonalTools)
            .Concat(GroupNames(MemoryToolset.TeamTools))
            .Concat(GroupNames(MemoryToolset.DossierTools)).ToList();
        stdio.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Чат без персоны (MEMORY_PERSONA_ID пуст) — только team_memory_*: приёмка «personal
    /// не регистрируются». Порядок — как в TeamTools.
    /// </summary>
    [SkippableFact]
    public void ЧатБезПерсоны_ТолькоКомандныеИнструменты()
    {
        var stdio = ListStdioTools(("MEMORY_PROJECT_ID", "proj1"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(GroupNames(MemoryToolset.TeamTools),
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы из JS-литералов обязаны совпадать с C#-схемами по каждому
    /// инструменту. Промах означал бы, что ветки валидируют аргументы по-разному — модель
    /// получила бы разные отказы на один и тот же вызов.
    /// </summary>
    [Fact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var all = MemoryToolset.PersonalTools
            .Concat(MemoryToolset.TeamTools).Concat(MemoryToolset.DossierTools).ToList();

        foreach (var tool in all)
        {
            var blockStart = Js.Value.IndexOf($"name: '{tool.Name}'", StringComparison.Ordinal);
            blockStart.Should().BeGreaterThan(0, $"инструмент {tool.Name} обязан быть в stdio-ветке");
            // Блок инструмента: до описания следующего (или конца файла) — required чужого
            // блока в окно попадать не должен
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

    /// <summary>
    /// Имя сервера в URL — та же точка правды, что и для widgets: маршрут контроллера
    /// ({name}) и константа тулсета не могут разъехаться с формой хвоста.
    /// </summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяИРазбираетсяОднойФормой()
    {
        MemoryToolset.EndpointFor("http://localhost:5000", "p-1", "proj-1")
            .Should().Be("http://localhost:5000/mcp/memory/p-1/proj-1");
        // Отсутствующие параметры — дефис: не сталкивается с GUID-идентификаторами
        MemoryToolset.EndpointFor("http://localhost:5000", null, null)
            .Should().Be("http://localhost:5000/mcp/memory/-/-");
    }
}
