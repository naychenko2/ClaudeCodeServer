using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Allow-list персоны-консультанта (файлового сабагента): deny-by-default, только read
// + собственная память. Негативные проверки важнее позитивных — у сабагента нет
// permission-канала, единственная граница — состав этого списка.
public class PersonaConsultantToolsetTests
{
    private static Persona Make(bool memory = true, PersonaScope scope = PersonaScope.Global,
        string? projectId = null, PersonaAccess access = PersonaAccess.Full,
        List<string>? disallowed = null) => new()
    {
        Name = "Тест",
        Handle = "test",
        MemoryEnabled = memory,
        Scope = scope,
        ProjectId = projectId,
        Access = access,
        DisallowedTools = disallowed,
    };

    // Полный негативный список: ничего из этого не должно попасть консультанту НИКОГДА
    private static readonly string[] ForbiddenAlways =
    [
        // Встроенные мутации и исполнение
        "Bash", "KillShell", "Edit", "Write", "MultiEdit", "NotebookEdit",
        // Рекурсия и общение с человеком
        "Task", "Agent", "AskUserQuestion", "ExitPlanMode",
        "mcp__personas__persona_ask",
        // Write-инструменты MCP-серверов
        "mcp__tasks__tasks_create", "mcp__tasks__tasks_update", "mcp__tasks__tasks_complete",
        "mcp__tasks__tasks_delete", "mcp__tasks__tasks_add_subtask", "mcp__tasks__tasks_toggle_subtask",
        "mcp__tasks__tasks_execute",
        "mcp__notes__notes_create", "mcp__notes__notes_update", "mcp__notes__notes_delete",
        "mcp__personas__personas_create", "mcp__personas__personas_update", "mcp__personas__personas_delete",
        "mcp__personas__personas_generate_avatar", "mcp__personas__personas_bindings_set",
        "mcp__personas__personas_automation_create", "mcp__personas__personas_automation_update",
        "mcp__personas__personas_automation_delete", "mcp__personas__personas_automation_test",
        "mcp__wsp__projects_create", "mcp__wsp__projects_update", "mcp__wsp__files_write",
        "mcp__wsp__files_mkdir", "mcp__wsp__files_rename", "mcp__wsp__knowledge_index",
        "mcp__wsp__chats_create", "mcp__wsp__chats_update", "mcp__wsp__chats_send",
        "mcp__wsp__files_delete", "mcp__wsp__chats_delete",
        // Уведомления — не от чужого лица
        "mcp__notifications__notifications_create",
    ];

    [Fact]
    public void НиОдинЗапрещённыйИнструментНеПопадаетВНабор()
    {
        var result = PersonaConsultantToolset.Build(Make(), webAllowed: true);

        result.Should().NotContain(ForbiddenAlways);
        // Память ГЛАВНОЙ сессии (mcp__memory__* — персона A) тоже недоступна
        result.Should().NotContain(t => t.StartsWith("mcp__memory__"));
        result.Should().NotContain(t => t.StartsWith("mcp__notifications__"));
    }

    [Fact]
    public void БазовыйНабор_СодержитReadИнструменты()
    {
        var result = PersonaConsultantToolset.Build(Make(memory: false), webAllowed: false);

        result.Should().Contain(["Read", "Grep", "Glob"]);
        result.Should().Contain("mcp__tasks__tasks_list")
            .And.Contain("mcp__notes__notes_read")
            .And.Contain("mcp__personas__personas_get")
            .And.Contain("mcp__wsp__files_read")
            .And.Contain("mcp__wsp__search_unified");
    }

    [Fact]
    public void Web_ВключаетсяТолькоПоРазрешению()
    {
        PersonaConsultantToolset.Build(Make(), webAllowed: false)
            .Should().NotContain(["WebSearch", "WebFetch"]);
        PersonaConsultantToolset.Build(Make(), webAllowed: true)
            .Should().Contain(["WebSearch", "WebFetch"]);
    }

    [Fact]
    public void Память_ТолькоПриВключеннойПамяти()
    {
        PersonaConsultantToolset.Build(Make(memory: false), webAllowed: false)
            .Should().NotContain(t => t.Contains("pmem"));

        var withMemory = PersonaConsultantToolset.Build(Make(), webAllowed: false);
        withMemory.Should().Contain("mcp__pmem_test__memory_search")
            .And.Contain("mcp__pmem_test__memory_remember")
            .And.Contain("mcp__pmem_test__memory_forget");
    }

    [Fact]
    public void КоманднаяПамять_ТолькоЧтение_ИТолькоУПроектной()
    {
        var global = PersonaConsultantToolset.Build(Make(), webAllowed: false);
        global.Should().NotContain(t => t.Contains("team_memory"));

        var project = PersonaConsultantToolset.Build(
            Make(scope: PersonaScope.Project, projectId: "p1"), webAllowed: false);
        project.Should().Contain("mcp__pmem_test__team_memory_list");
        project.Should().NotContain("mcp__pmem_test__team_memory_remember")
            .And.NotContain("mcp__pmem_test__team_memory_forget");
    }

    [Fact]
    public void CustomЗапреты_ТолькоСужают()
    {
        var result = PersonaConsultantToolset.Build(
            Make(access: PersonaAccess.Custom,
                disallowed: ["Read", "mcp__wsp__files_read", "Bash", "ВыдуманныйИнструмент"]),
            webAllowed: false);

        result.Should().NotContain("Read").And.NotContain("mcp__wsp__files_read");
        // Запрет несуществующего/уже отсутствующего ничего не добавляет
        result.Should().NotContain("Bash").And.NotContain("ВыдуманныйИнструмент");
        result.Should().Contain("Grep"); // остальное на месте
    }

    [Fact]
    public void PmemServerKey_НормализуетHandle()
    {
        PersonaConsultantToolset.PmemServerKey("gefest").Should().Be("pmem_gefest");
        PersonaConsultantToolset.PmemServerKey("Ana-Lyst_2").Should().Be("pmem_ana-lyst_2");
        PersonaConsultantToolset.PmemServerKey("стр@нный").Should().MatchRegex("^pmem_[a-z0-9_-]+$");
    }
}
