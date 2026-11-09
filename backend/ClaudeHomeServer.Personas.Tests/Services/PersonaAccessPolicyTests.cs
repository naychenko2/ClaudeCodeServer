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

        // Продуктовый сервер веб-поиска — второй путь в интернет: выключенный «web»
        // обязан резать и его, иначе тумблер персоны обходится через MCP
        result.Should().BeEquivalentTo(["WebSearch", "WebFetch",
            "mcp__websearch__web_search", "mcp__websearch__web_read"]);
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

        result.Should().BeEquivalentTo(["WebSearch", "WebFetch",
            "mcp__websearch__web_search", "mcp__websearch__web_read", "Bash", "Edit"]);
    }

    [Fact]
    public void Custom_БезСпискаИСВключеннымWeb_Null()
    {
        PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.Custom)).Should().BeNull();
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

    // Пишущие инструменты C4-модели: список запретов в Main пишется строками (вертикаль —
    // динамический модуль, ссылки на неё у Main нет), поэтому соответствие сверяем здесь:
    // новый пишущий arch_* без строки в ReadOnlyDisallowed — дыра профиля «Только чтение»
    [Fact]
    public void ReadOnly_ОтрезаетВсеПишущиеИнструментыАрхитектуры()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.ReadOnly));
        var catalog = ClaudeHomeServer.Services.Architecture.ArchitectureToolset.AllTools.Select(t => t.Name).ToHashSet();
        var writes = ClaudeHomeServer.Services.Architecture.ArchitectureToolset.WriteTools;

        writes.Should().Contain(["arch_create_element", "arch_delete_element"]);
        // Обратная сторона: всё, что не чтение, обязано быть в WriteTools — иначе новый
        // пишущий инструмент прошёл бы мимо обоих гейтов «Только чтение»
        catalog.Except(["arch_context", "arch_search", "arch_get_element"]).Should().BeEquivalentTo(writes);
        foreach (var tool in writes)
        {
            catalog.Should().Contain(tool, "запрет на несуществующее имя — тихо неработающий гейт");
            result.Should().Contain("mcp__architecture__" + tool);
        }
        result.Should().NotContain(["mcp__architecture__arch_context", "mcp__architecture__arch_get_element",
            "mcp__architecture__arch_search"]);
    }

    [Fact]
    public void ReadOnly_ОтрезаетПишущиеИТратящиеИнструментыВидеоРедактора()
    {
        var result = PersonaAccessPolicy.BuildExtraDisallowed(Make(PersonaAccess.ReadOnly));

        result.Should().Contain([
            "mcp__video-editor__video_save_scene", "mcp__video-editor__video_film_edit",
            "mcp__video-editor__video_film_build", "mcp__video-editor__video_shoot",
            "mcp__video-editor__video_cancel"]);
        result.Should().NotContain(["mcp__video-editor__video_state", "mcp__video-editor__video_wait"]);
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
