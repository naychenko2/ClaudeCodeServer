using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
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
}
