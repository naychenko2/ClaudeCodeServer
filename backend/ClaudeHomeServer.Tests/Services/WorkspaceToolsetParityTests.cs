using System.Text.Json;
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
/// Сильнее посимвольного сравнения имён: состав, required-наборы и описания сверяются
/// с ЖИВЫМ stdio-сервером по ОСЯМ СЕКЦИЙ (WORKSPACE_SECTIONS) — базовый набор, надстройки
/// git/git_write/knowledge_bases, секция chats, деструктив и выкатка. Плюс поведенческие
/// проверки, которых у сторожей волны 2 не было (урок приёмки: имена совпадали, а поведение
/// разъезжалось): карта «инструмент → секция» покрывает ВЕСЬ состав, а формулировки-
/// предохранители деструктива и выкатки совпадают с stdio-веткой дословно. Сверка по данным
/// ответа, без regex-скрейпинга JS (техдолг MCP-over-HTTP §6).
/// </summary>
public class WorkspaceToolsetParityTests
{
    private record StdioTool(string Name, string Description, JsonElement Schema);

    // Полный набор секций — режим, в котором живой stdio-сервер отдаёт весь каталог
    private const string AllSections = "projects,files,knowledge,search,chats,git,git_write,knowledge_bases,destructive,deploy";

    // tools/list живого stdio-сервера с заданным набором секций: бэкенд не нужен
    // (состав считается из env, в сеть сервер не ходит). null — node недоступен.
    private static IReadOnlyList<StdioTool>? ListStdioTools(string sections)
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
            // node пишет stdout в UTF-8, а .NET по умолчанию читает в консольной кодировке
            // ОС — кириллические описания превращались в кракозябры и ломали посимвольную
            // сверку (на Linux тесты проходят и без этого, CI там)
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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
                .Select(t => new StdioTool(
                    t.GetProperty("name").GetString()!,
                    t.GetProperty("description").GetString()!,
                    t.GetProperty("inputSchema").Clone()))
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
        stdio.Select(t => t.Name).Should().BeEquivalentTo(expected, options => options.WithStrictOrdering(),
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
    /// Схемы: required-наборы ЖИВОГО stdio-ответа обязаны совпадать с C#-схемами по каждому
    /// инструменту. Промах означал бы, что ветки валидируют аргументы по-разному.
    /// </summary>
    [SkippableFact]
    public void RequiredНаборы_СовпадаютПосимвольно()
    {
        var stdio = ListStdioTools(AllSections);
        if (stdio is null) return;
        var byName = WorkspaceToolset.AllTools.ToDictionary(t => t.Name);

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
    /// ПОВЕДЕНЧЕСКАЯ ось: описания инструментов деструктива и выкатки совпадают с ЖИВЫМ
    /// stdio-ответом ПОСИМВОЛЬНО — включая формулировки-предохранители («БЕЗВОЗВРАТНО…,
    /// ТОЛЬКО по явной просьбе…, никогда по своей инициативе»). Это не косметика: они и есть
    /// защита от того, чтобы модель удалила или выкатила лишнее сама (требование задачи
    /// волны 3: формулировки менять нельзя). Посимвольная сверка покрывает все фрагменты
    /// разом и не зависит от разбивки описаний на строки в index.js.
    /// </summary>
    [SkippableFact]
    public void ОписанияПредохранителей_СовпадаютСоСtdioПосимвольно()
    {
        var stdio = ListStdioTools(AllSections);
        if (stdio is null) return;
        var byName = WorkspaceToolset.AllTools.ToDictionary(t => t.Name);

        foreach (var name in new[] { "files_delete", "chats_delete", "deploy_start", "deploy_rollback" })
        {
            var stdioDescription = stdio.SingleOrDefault(t => t.Name == name)?.Description;
            stdioDescription.Should().NotBeNull($"инструмент {name} обязан быть в stdio-ветке");
            var csharp = byName.GetValueOrDefault(name);
            csharp.Should().NotBeNull($"инструмент {name} обязан быть в http-ветке");
            stdioDescription.Should().Be(csharp!.Description,
                $"описание {name} обязано совпадать между ветками посимвольно — это защита от самовольного действия модели");
        }
    }

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
