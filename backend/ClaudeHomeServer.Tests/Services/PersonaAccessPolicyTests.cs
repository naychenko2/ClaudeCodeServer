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
        result.Should().Contain(["Edit", "Write", "MultiEdit", "NotebookEdit", "Bash", "KillShell"]);
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
}
