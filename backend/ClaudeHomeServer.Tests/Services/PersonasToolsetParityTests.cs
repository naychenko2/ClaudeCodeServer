using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта персон (по образцу MemoryToolsetParityTests, ADR-012 фаза 2
/// волна 2). Схемы и тексты продублированы в PersonasToolset.Schemas.cs (http-ветка) и
/// mcp/personas-server/index.js (stdio-ветка отката) — обе живые, правка обязана ехать парой.
/// Источник контракта — PersonasToolset (index.js заморожен).
///
/// Состав сверяется с ЖИВЫМ stdio-сервером по осям модулей (PERSONAS_MANAGE / _AUTOMATION /
/// _MENTIONS): ядро, +manage, +automation, +mentions. BINDINGS у http-ветки включён всегда
/// (как и в контексте сессии — BindingsEnabled: true), поэтому stdio запускаем с ним же.
/// </summary>
public class PersonasToolsetParityTests
{
    private static string JsPath => RepoFile("mcp", "personas-server", "index.js");

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

    // tools/list живого stdio-сервера с заданными env: состав считается из env, в сеть
    // сервер не ходит. null — node недоступен.
    private static IReadOnlyList<string>? ListStdioTools(params (string Key, string Value)[] env)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "personas-server", "index.js");
            if (File.Exists(candidate)) break;
            dir = dir.Parent;
        }
        Skip.If(dir is null, "mcp/personas-server/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { Path.Combine(dir!.FullName, "mcp", "personas-server", "index.js") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["PERSONAS_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["PERSONAS_API_TOKEN"] = "test";
        // Привязки у http-ветки включены всегда (BindingsEnabled: true в контексте сессии)
        psi.Environment["PERSONAS_BINDINGS"] = "1";
        psi.Environment["PERSONAS_MANAGE"] = "0";
        psi.Environment["PERSONAS_AUTOMATION"] = "0";
        psi.Environment["PERSONAS_MENTIONS"] = "0";
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

    // Ожидаемый состав http-ветки по осям модулей — в порядке ToolsFor
    private static List<string> Expected(bool manage, bool automation, bool mentions,
        bool inProject = true)
    {
        var names = PersonasToolset.CoreTools(inProject).Select(t => t.Name).ToList();
        if (manage) names.AddRange(PersonasToolset.ManageHeadTools(inProject).Select(t => t.Name));
        names.AddRange(PersonasToolset.BindingsReadTools.Select(t => t.Name));
        if (manage) names.AddRange(PersonasToolset.ManageBindingsTools.Select(t => t.Name));
        names.Add(PersonasToolset.KnowledgeSearchTool.Name);
        if (automation) names.AddRange(PersonasToolset.AutomationTools.Select(t => t.Name));
        if (manage) names.AddRange(PersonasToolset.ManageTailTools(inProject).Select(t => t.Name));
        if (mentions) names.AddRange(PersonasToolset.MentionsTools.Select(t => t.Name));
        return names;
    }

    /// <summary>Ядро без модулей: состав и порядок совпадают со stdio-веткой.</summary>
    [SkippableFact]
    public void БезМодулей_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools();
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(Expected(manage: false, automation: false, mentions: false),
            options => options.WithStrictOrdering(),
            "ядро сервера персон обязано совпадать с веткой отката");
    }

    /// <summary>Модуль manage: +create/update, +bindings_set/mcp_grant, +delete/avatar/ai_team.</summary>
    [SkippableFact]
    public void Manage_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("PERSONAS_MANAGE", "1"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(Expected(manage: true, automation: false, mentions: false),
            options => options.WithStrictOrdering());
    }

    /// <summary>Модуль automation: +5 инструментов правил в порядке stdio-ветки.</summary>
    [SkippableFact]
    public void Automation_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("PERSONAS_AUTOMATION", "1"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(Expected(manage: false, automation: true, mentions: false),
            options => options.WithStrictOrdering());
    }

    /// <summary>Упоминания: persona_ask последним, как у stdio-ветки.</summary>
    [SkippableFact]
    public void Mentions_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(("PERSONAS_MENTIONS", "1"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(Expected(manage: false, automation: false, mentions: true),
            options => options.WithStrictOrdering());
    }

    /// <summary>Все модули разом — полный состав сервера.</summary>
    [SkippableFact]
    public void ВсеМодули_СоставСовпадаетСоStdioВеткой()
    {
        var stdio = ListStdioTools(
            ("PERSONAS_MANAGE", "1"), ("PERSONAS_AUTOMATION", "1"), ("PERSONAS_MENTIONS", "1"));
        if (stdio is null) return;
        stdio.Should().BeEquivalentTo(Expected(manage: true, automation: true, mentions: true),
            options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Схемы: required-наборы из JS-литералов обязаны совпадать с C#-схемами по каждому
    /// инструменту — иначе ветки валидируют аргументы по-разному.
    /// </summary>
    [Fact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var all = PersonasToolset.CoreTools(true)
            .Concat(PersonasToolset.ManageHeadTools(true))
            .Concat(PersonasToolset.BindingsReadTools)
            .Concat(PersonasToolset.ManageBindingsTools)
            .Append(PersonasToolset.KnowledgeSearchTool)
            .Concat(PersonasToolset.AutomationTools)
            .Concat(PersonasToolset.ManageTailTools(true))
            .Concat(PersonasToolset.MentionsTools)
            .ToList();

        foreach (var tool in all)
        {
            var blockStart = Js.Value.IndexOf($"name: '{tool.Name}'", StringComparison.Ordinal);
            blockStart.Should().BeGreaterThan(0, $"инструмент {tool.Name} обязан быть в stdio-ветке");
            var next = Js.Value.IndexOf("name: '", blockStart + 10, StringComparison.Ordinal);
            if (next < 0) next = Js.Value.Length;
            var block = Js.Value[blockStart..next];
            // required верхнего уровня схемы: первый в блоке (вложенные — у items фильтров)
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

    /// <summary>Хвост маршрута — та же точка правды, что у tasks/notes/memory.</summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяОднойФормой() =>
        PersonasToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/personas/sess-1");
}
