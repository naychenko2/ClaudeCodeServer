using System.Text.Json;
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
    private static string JsPath => RepoFile("mcp", "memory-server", "index.js");

    // Исходник http-ветки — второй вход сторожа write-path (см. тест ниже): паритет путей
    // записи живёт в коде тулсета, скомпилированный контракт его не различает
    private static string CsPath =>
        RepoFile("backend", "ClaudeHomeServer", "Services", "Mcp", "Http", "MemoryToolset.cs");

    // Файл репозитория по пути от корня: корень ищем от bin-каталога тестов вверх до .git
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

    private record StdioTool(string Name, JsonElement Schema);

    private static IReadOnlyList<string> GroupNames(IReadOnlyList<McpToolSchema> group) =>
        group.Select(t => t.Name).ToList();

    // tools/list живого stdio-сервера с заданными env (паттерн McpToolsetStabilityTests.
    // ListMemoryTools): бэкенд не нужен, состав считается из env, в сеть сервер не ходит.
    // null — node недоступен.
    private static IReadOnlyList<StdioTool>? ListStdioTools(params (string Key, string Value)[] env)
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
                .Select(t => new StdioTool(
                    t.GetProperty("name").GetString()!,
                    t.GetProperty("inputSchema").Clone()))
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
        stdio.Select(t => t.Name).Should().BeEquivalentTo(GroupNames(MemoryToolset.PersonalTools),
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
        stdio.Select(t => t.Name).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
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
        stdio.Select(t => t.Name).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
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
        stdio.Select(t => t.Name).Should().BeEquivalentTo(GroupNames(MemoryToolset.TeamTools),
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы ЖИВОГО stdio-ответа обязаны совпадать с C#-схемами по каждому
    /// инструменту. Промах означал бы, что ветки валидируют аргументы по-разному — модель
    /// получила бы разные отказы на один и тот же вызов. Сверка по данным, без regex-
    /// скрейпинга JS (урок техдолга MCP-over-HTTP §6 о хрупкости по форматированию).
    /// </summary>
    [SkippableFact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var stdio = ListStdioTools(
            ("MEMORY_PERSONA_ID", "p1"), ("MEMORY_PROJECT_ID", "proj1"), ("MEMORY_DOSSIER_TOOLS", "1"));
        if (stdio is null) return;
        var all = MemoryToolset.PersonalTools
            .Concat(MemoryToolset.TeamTools).Concat(MemoryToolset.DossierTools).ToList();
        var byName = all.ToDictionary(t => t.Name);

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

    /// <summary>
    /// Сторож write-path (блокер 2 приёмки волны 1): team_memory_remember обязан писать
    /// точным Add — тем же путём, что REST-эндпоинт, на который POST-ит stdio-ветка
    /// (index.js → POST /api/projects/{id}/team-memory → TeamMemoryService.Add). Семантический
    /// AddAsync менял бы ПОВЕДЕНИЕ, а не транспорт: близкая по смыслу чужая запись
    /// перезаписывалась бы, а модели возвращался её id под видом новой. Рубильник
    /// Mcp:HttpTransport не смеет менять семантику записи — поэтому сверяем исходники веток.
    /// </summary>
    [Fact]
    public void TeamRemember_WriteПуть_ТочныйAddКакУStdioВетки()
    {
        // stdio-ветка пишет POST-ом на REST-эндпоинт (точный Add контроллера) — якорь,
        // что «одинаковый write-path» действительно путь отката, а не выдумка теста
        var rememberJs = CaseBlock(Js.Value, "case 'team_memory_remember':");
        rememberJs.Should().Contain("teamBase").And.Contain("POST",
            "stdio-ветка пишет через REST /team-memory — тот же точный Add");

        var rememberCs = CaseBlock(File.ReadAllText(CsPath), "case \"team_memory_remember\":");
        rememberCs.Should().Contain("teamMemory.Add(",
            "http-ветка пишет тем же точным Add, что и REST/stdio — транспорт, не семантика");
        // Сверяем форму ВЫЗОВА, а не голое слово: «AddAsync» в комментарии блока — не вызов
        rememberCs.Should().NotContain("teamMemory.AddAsync(",
            "семантический дедуп — другое поведение записи, рубильник транспорта его менять не может");
    }

    // Блок case по маркеру («case "x":» для C#, «case 'x':» для JS): до следующего «case ».
    // Окно, в котором живут вызовы одного инструмента — required чужого блока не цепляет
    private static string CaseBlock(string source, string caseMarker)
    {
        var start = source.IndexOf(caseMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"маркер {caseMarker} обязан быть в исходнике");
        var next = source.IndexOf("case ", start + caseMarker.Length, StringComparison.Ordinal);
        return source[start..(next < 0 ? source.Length : next)];
    }
}
