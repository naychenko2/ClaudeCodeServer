using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Профили доступа персон (P6): сборка ExtraDisallowedTools из профиля + возможности «web»
public class PersonaAccessPolicyTests
{
    private static Persona Make(PersonaAccess access = PersonaAccess.Full,
        List<string>? tools = null, List<string>? disallowed = null) => new()
        {
            Name = "Тест",
            Access = access,
            Tools = tools,
            DisallowedTools = disallowed,
        };

    [Fact]
    public void ReadOnly_ЗапрещаетФайловыеМутацииИBash()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.ReadOnly));

        result.Should().NotBeNull();
        result.Should().Contain(["Edit", "Write", "NotebookEdit", "Bash", "KillShell"]);
        // Мутации наших MCP-серверов тоже под запретом
        result.Should().Contain("mcp__tasks__tasks_create")
            .And.Contain("mcp__notes__notes_delete")
            .And.Contain("mcp__personas__personas_update");
    }

    [Fact]
    public void ReadOnly_НеТрогаетПамятьПерсоны()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.ReadOnly));

        // Долгая память — её собственная: memory_remember остаётся доступен
        result.Should().NotContain(t => t.StartsWith("mcp__memory__"));
    }

    [Fact]
    public void ВыключенныйWeb_ДобавляетWebSearchИWebFetch()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(tools: ["tasks", "notes"]));

        result.Should().BeEquivalentTo(["WebSearch", "WebFetch"]);
    }

    [Fact]
    public void Full_СВключеннымWeb_БезЗапретов()
    {
        // Tools == null — без ограничений возможностей
        PersonaAccessPolicy.BuildExtraDisallowed(Make()).Should().BeNull();
        // Явный полный web
        PersonaAccessPolicy.BuildExtraDisallowed(Make(tools: ["tasks", "notes", "web"])).Should().BeNull();
    }

    [Fact]
    public void БезПерсоны_Null()
    {
        PersonaAccessPolicy.BuildExtraDisallowed(null).Should().BeNull();
    }

    [Fact]
    public void Custom_ОбъединяетсяСWebOff_БезДублей()
    {
        var persona = Make(PersonaAccess.Custom,
            tools: ["tasks"],   // web выключен
            disallowed: ["Bash", "WebSearch", " Edit "]);

        var result = PersonaAccessPolicy.BuildExtraDisallowed(persona);

        result.Should().BeEquivalentTo(["WebSearch", "WebFetch", "Bash", "Edit"]);
    }

    [Fact]
    public void Custom_БезСпискаИСВключеннымWeb_Null()
    {
        PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.Custom)).Should().BeNull();
    }

    [Fact]
    public void ReadOnly_ДесктопнаяГрань_МутацииЗапрещены_ЧтениеСвободно()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.ReadOnly));

        // Меняющее чужой рабочий стол — под запретом (ADR-008: desktop_* вносятся в ReadOnly)
        result.Should().Contain([
            "mcp__desktop__desktop_act", "mcp__desktop__desktop_open", "mcp__desktop__desktop_run"]);
        // Читающие инструменты персоне «только чтение» остаются
        result.Should().NotContain("mcp__desktop__desktop_devices")
            .And.NotContain("mcp__desktop__desktop_screen")
            .And.NotContain("mcp__desktop__desktop_ui");
    }

    [Fact]
    public void ДесктопныеЗапреты_ТолькоИменаMcp_НеВстроенные()
    {
        // Класс дефектов MultiEdit (ClaudeSession.BuiltInTaskTools): имя БЕЗ префикса mcp__
        // в deny-списке — это неизвестное встроенное имя, и CLI на него ругается. Deny-имена
        // ReadOnly-персоны уезжают в --disallowedTools КАЖДОЙ сессии — включая ходы, где грань
        // не доставлена (чат не десктопный, грань выключена в проекте). Поэтому каждое
        // desktop-имя обязано быть именем MCP-инструмента mcp__desktop__*. Живой прогон CLI
        // с этим списком — DesktopMcpToolsetStabilityTests.DenyИменаДесктопа_НеРоняютЗапускCli.
        foreach (var name in PersonaAccessPolicy.ReadOnlyDisallowed.Where(t => t.Contains("desktop")))
            name.Should().StartWith("mcp__desktop__",
                "голое имя без префикса mcp__ — это неизвестное встроенное имя, класс MultiEdit");
    }

    // ---------- Рабочее пространство (wsp) в профиле «Только чтение» (волна 3.1) ----------

    // До волны 3.1 шапка WorkspaceToolset и ADR-012 утверждали, что write-инструменты wsp
    // гейтятся ExtraDisallowedTools — а списка не было: персона «Только чтение» спокойно
    // звала files_write/git_commit. Профиль обязан означать то, что написано.
    [Fact]
    public void ReadOnly_ЗапрещаетВсеМутирующиеИнструментыWsp()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.ReadOnly));

        // Весь мутирующий периметр wsp: файлы (включая files_to_markdown — он пишет .md),
        // git-запись, проекты и теги, базы знаний, чаты-мутации, деструктив
        result.Should().Contain([
            "mcp__wsp__files_write", "mcp__wsp__files_mkdir", "mcp__wsp__files_rename",
            "mcp__wsp__files_to_markdown", "mcp__wsp__files_delete",
            "mcp__wsp__git_commit", "mcp__wsp__git_stage",
            "mcp__wsp__projects_create", "mcp__wsp__projects_update", "mcp__wsp__tags_apply",
            "mcp__wsp__tags_remove",
            "mcp__wsp__knowledge_index", "mcp__wsp__kb_add_document",
            "mcp__wsp__chats_create", "mcp__wsp__chats_update", "mcp__wsp__chats_delete"]);

        // Чтение и общение остаются: «только чтение» значит «не меняет», а не «молчит»
        result.Should().NotContain([
            "mcp__wsp__files_read", "mcp__wsp__files_tree", "mcp__wsp__git_status",
            "mcp__wsp__git_log", "mcp__wsp__search_unified", "mcp__wsp__projects_list",
            "mcp__wsp__chats_send", "mcp__wsp__chats_report_up", "mcp__wsp__chats_history"]);
    }

    // Список запретов wsp живёт в PersonaAccessPolicy, каталог инструментов — в
    // WorkspaceToolset: опечатка в имени прошла бы молча (deny неизвестного имени wsp — не
    // падение CLI, а тихо неработающий запрет). Сверяем каждое имя с живым каталогом.
    [Fact]
    public void WspЗапреты_ИменаСуществуютВКаталогеТулсета()
    {
        var catalog = WorkspaceToolset.AllTools.Select(t => t.Name).ToHashSet();
        var wspDeny = PersonaAccessPolicy.ReadOnlyDisallowed
            .Where(t => t.StartsWith("mcp__wsp__", StringComparison.Ordinal))
            .Select(t => t["mcp__wsp__".Length..])
            .ToList();

        wspDeny.Should().NotBeEmpty("список запретов wsp обязан существовать (блокер волны 3.1)");
        foreach (var name in wspDeny)
            catalog.Should().Contain(name,
                $"deny-имя {name} обязано совпадать с инструментом каталога wsp — иначе запрет молча не работает");
    }
}
