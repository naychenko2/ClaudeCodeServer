using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож парности контракта рабочего пространства (ADR-012, фаза 2 волна 3). Схемы и тексты
/// продублированы в WorkspaceToolset.Schemas.cs (http-ветка) и mcp/workspace-server/index.js
/// (stdio-ветка отката по рубильнику Mcp:HttpTransport) — обе живые, и правка только в C#
/// прошла бы зелёные тесты, молча разойдясь со второй веткой. Источник контракта —
/// WorkspaceToolset.Schemas.cs (index.js заморожен, см. его шапку).
///
/// Сильнее посимвольного сравнения имён: состав сверяется с ЖИВЫМ stdio-сервером по ОСЯМ
/// СЕКЦИЙ (WORKSPACE_SECTIONS) — базовый набор, надстройки git/git_write/knowledge_bases,
/// секция chats, деструктив и выкатка. Плюс поведенческие проверки, которых у сторожей
/// волны 2 не было (урок приёмки: имена совпадали, а поведение разъезжалось):
/// карта «инструмент → секция» покрывает ВЕСЬ состав, а формулировки-предохранители
/// деструктива и выкатки совпадают дословно.
/// </summary>
public class WorkspaceToolsetParityTests
{
    private static string JsPath => RepoFile("mcp", "workspace-server", "index.js");

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

    // tools/list живого stdio-сервера с заданным набором секций: бэкенд не нужен
    // (состав считается из env, в сеть сервер не ходит). null — node недоступен.
    private static IReadOnlyList<string>? ListStdioTools(string sections)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "mcp", "workspace-server", "index.js");
            if (File.Exists(candidate)) break;
            dir = dir.Parent;
        }
        Skip.If(dir is null, "mcp/workspace-server/index.js не найден");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { Path.Combine(dir!.FullName, "mcp", "workspace-server", "index.js") },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["WORKSPACE_API_URL"] = "http://127.0.0.1:1";
        psi.Environment["WORKSPACE_API_TOKEN"] = "test";
        psi.Environment["WORKSPACE_PROJECT_ID"] = "proj-1";
        psi.Environment["WORKSPACE_SECTIONS"] = sections;
        psi.Environment["WORKSPACE_WRITE"] = "1";

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
    /// ОСИ СЕКЦИЙ: состав http-тулсета на наборе секций совпадает с живым stdio-сервером
    /// на том же наборе — посимвольно, включая порядок. Секции надстроек (git/git_write/
    /// knowledge_bases) и опасные (destructive/deploy) проверяются отдельными осями:
    /// именно на них расходились ветки в прошлых волнах.
    /// </summary>
    [SkippableTheory]
    [InlineData("projects,files,knowledge,search")]
    [InlineData("projects,files,knowledge,search,git")]
    [InlineData("projects,files,knowledge,search,git,git_write")]
    [InlineData("projects,files,knowledge,search,knowledge_bases")]
    [InlineData("projects,files,knowledge,search,chats")]
    [InlineData("projects,files,knowledge,search,chats,destructive")]
    [InlineData("projects,files,knowledge,search,deploy")]
    [InlineData("projects,files,knowledge,search,chats,git,git_write,knowledge_bases,destructive,deploy")]
    public void Состав_СовпадаетСоStdioВеткой_ПоКаждойОсиСекций(string sections)
    {
        var stdio = ListStdioTools(sections);
        if (stdio is null) return;
        var set = sections.Split(",").ToHashSet(StringComparer.Ordinal);
        var expected = WorkspaceToolset.ToolsForSections(set, "контекст")
            .Select(t => t.Name).ToList();
        stdio.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering(),
            $"состав на секциях «{sections}» обязан совпадать со stdio-веткой отката");
    }

    /// <summary>
    /// Карта «инструмент → секция» покрывает ВЕСЬ каталог: инструмент без секции упал бы
    /// в рантайме («Неизвестный инструмент») уже после экспозиции — то есть пропал бы
    /// у модели молча. Обратное тоже проверяем: секций-сирот в карте нет.
    /// </summary>
    [Fact]
    public void КартаСекций_ПокрываетВесьСостав()
    {
        var names = WorkspaceToolset.AllTools.Select(t => t.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
        foreach (var name in names)
            WorkspaceToolset.ToolSection.Should().ContainKey(name,
                "у каждого инструмента обязана быть секция — иначе вызов отобьётся уже после экспозиции");
        WorkspaceToolset.ToolSection.Keys.Should().BeEquivalentTo(names,
            "карта не должна содержать инструментов вне каталога");
    }

    /// <summary>
    /// Схемы: required-наборы из JS-литералов обязаны совпадать с C#-схемами по каждому
    /// инструменту. Промах означал бы, что ветки валидируют аргументы по-разному.
    /// </summary>
    [Fact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        foreach (var tool in WorkspaceToolset.AllTools)
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

    /// <summary>
    /// ПОВЕДЕНЧЕСКАЯ ось: формулировки-предохранители деструктива и выкатки совпадают с
    /// stdio-веткой дословно. Это не косметика — «БЕЗВОЗВРАТНО… ТОЛЬКО по явной просьбе»
    /// и «Зови ТОЛЬКО по явной просьбе… никогда по своей инициативе» и есть защита от
    /// того, чтобы модель удалила или выкатила лишнее сама (требование задачи волны 3:
    /// формулировки менять нельзя).
    /// </summary>
    [Theory]
    [InlineData("files_delete", "БЕЗВОЗВРАТНО удалить файл или папку проекта")]
    [InlineData("files_delete", "ТОЛЬКО по явной просьбе пользователя удалить конкретный путь, никогда по своей инициативе")]
    [InlineData("chats_delete", "БЕЗВОЗВРАТНО удалить чат/сессию вместе со всей историей сообщений")]
    [InlineData("chats_delete", "ТОЛЬКО по явной просьбе пользователя удалить конкретный чат, никогда по своей инициативе")]
    [InlineData("deploy_start", "выкатка ПЕРЕЗАПУСКАЕТ сервер")]
    [InlineData("deploy_start", "никогда по своей инициативе и никогда")]
    [InlineData("deploy_rollback", "ПЕРЕЗАПУСКАЕТ сервер")]
    public void ФормулировкиПредохранители_НеРазошлисьСоStdio(string tool, string fragment)
    {
        var description = WorkspaceToolset.AllTools.Single(t => t.Name == tool).Description;
        description.Should().Contain(fragment,
            $"описание {tool} — часть защиты от самовольного действия модели, менять нельзя");
        // Тот же фрагмент обязан быть и в замороженной stdio-ветке: расхождение веток
        // означало бы разное поведение модели в зависимости от транспорта
        NormalizeJs(Js.Value).Should().Contain(fragment,
            $"stdio-ветка обязана нести ту же формулировку {tool}");
    }

    // Склейка JS-конкатенаций («…» + «…»): в index.js длинные описания разбиты на строки,
    // и поиск фрагмента через границу склейки иначе не находит его
    private static string NormalizeJs(string js) =>
        Regex.Replace(js, @"'\s*\+\s*'", "");

    /// <summary>Хвост маршрута — та же точка правды, что у tasks/notes: форма не разъедется.</summary>
    [Fact]
    public void ХвостМаршрута_СтроитсяОднойФормой()
    {
        WorkspaceToolset.EndpointFor("http://localhost:5000", "sess-1")
            .Should().Be("http://localhost:5000/mcp/wsp/sess-1");
    }

    /// <summary>
    /// Контекстная заметка описания projects_list подставляется живьём (плейсхолдер
    /// не должен доехать до модели) — эквивалент CONTEXT_NOTE stdio-ветки.
    /// </summary>
    [Fact]
    public void КонтекстнаяЗаметка_ПодставляетсяВСостав()
    {
        var tools = WorkspaceToolset.ToolsForSections(
            new HashSet<string>(StringComparer.Ordinal) { "projects" },
            "Текущая сессия идёт в проекте proj-1.");
        var description = tools.Single(t => t.Name == "projects_list").Description;
        description.Should().Contain("Текущая сессия идёт в проекте proj-1.");
        description.Should().NotContain("{CONTEXT_NOTE}");
    }
}
