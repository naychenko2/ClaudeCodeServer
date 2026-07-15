using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Генерация .md-файла сабагента из персоны: frontmatter по фактической схеме CLI + тело
public class PersonaAgentFileGeneratorTests
{
    private static PersonaAgentFileGenerator MakeGenerator()
    {
        var config = new ConfigurationBuilder().Build();
        return new PersonaAgentFileGenerator(new PersonaPromptBuilder(new LlmProviderRegistry(config)));
    }

    private static Persona MakePersona(string? model = null, bool memory = true,
        string? color = "purple", string? effort = null) => new()
    {
        Name = "Гефест",
        Role = "Мастер",
        Handle = "gefest",
        Description = "Кузнец и реализатор",
        Contract = new PersonaContract { Character = "Ты — мастер-кузнец.", Tone = "сухо и по делу" },
        Model = model,
        Effort = effort,
        MemoryEnabled = memory,
        Avatar = new PersonaAvatar { Color = color },
    };

    [Fact]
    public void Frontmatter_СодержитОбязательныеПоля()
    {
        var text = MakeGenerator().Generate(MakePersona(), webAllowed: false);

        text.Should().StartWith("---");
        text.Should().Contain("name: gefest");
        text.Should().Contain("description: \"Мастер (Гефест) — Кузнец и реализатор");
        text.Should().Contain("tools: Read, Grep, Glob");
        text.Should().Contain("maxTurns: 25");
        text.Should().Contain("color: purple");
    }

    [Fact]
    public void Model_ТолькоПриЯвной()
    {
        MakeGenerator().Generate(MakePersona(), webAllowed: false)
            .Should().NotContain("\nmodel:");
        MakeGenerator().Generate(MakePersona(model: "opus"), webAllowed: false)
            .Should().Contain("model: opus");
    }

    [Fact]
    public void Effort_ТолькоПриЯвном()
    {
        MakeGenerator().Generate(MakePersona(), webAllowed: false)
            .Should().NotContain("\neffort:");
        MakeGenerator().Generate(MakePersona(effort: "high"), webAllowed: false)
            .Should().Contain("effort: high");
    }

    [Fact]
    public void McpServers_ТолькоПриПамяти()
    {
        MakeGenerator().Generate(MakePersona(memory: false), webAllowed: false)
            .Should().NotContain("mcpServers");
        MakeGenerator().Generate(MakePersona(), webAllowed: false)
            .Should().Contain("mcpServers: [pmem_gefest]");
    }

    [Fact]
    public void Цвет_МапитсяВДопустимыеCli()
    {
        // brown нет в палитре CLI — ближний orange
        MakeGenerator().Generate(MakePersona(color: "brown"), webAllowed: false)
            .Should().Contain("color: orange");
        // неизвестный цвет — поле опускается
        MakeGenerator().Generate(MakePersona(color: "magenta"), webAllowed: false)
            .Should().NotContain("\ncolor:");
    }

    [Fact]
    public void Description_ОднострочныйИЭкранированный()
    {
        var persona = MakePersona();
        persona.Description = "Стро\"ка с кавычками\nи переносом";

        var text = MakeGenerator().Generate(persona, webAllowed: false);

        var descLine = text.Split('\n').First(l => l.StartsWith("description:"));
        descLine.Should().Contain("\\\"").And.NotContain("Стро\"ка с кавычками\nи");
    }

    [Fact]
    public void Тело_СодержитХарактерРамкуИПамять()
    {
        var text = MakeGenerator().Generate(MakePersona(), webAllowed: false);
        var body = text[(text.IndexOf("---", 3, StringComparison.Ordinal) + 3)..];

        body.Should().Contain("Ты — Мастер по имени Гефест");        // идентичность из PersonaPromptBuilder
        body.Should().Contain("мастер-кузнец");                       // контракт
        body.Should().Contain("## Ты — консультант");                 // рамка
        body.Should().Contain("сам разговор ты не видишь");
        body.Should().Contain("mcp__pmem_gefest__memory_search");     // память
        body.Should().Contain("No such tool available");              // retry-hint
        body.Should().Contain("## Границы консультанта");
    }

    [Fact]
    public void БезПамяти_НетСекцииПамяти()
    {
        var text = MakeGenerator().Generate(MakePersona(memory: false), webAllowed: false);
        text.Should().NotContain("## Твоя память").And.NotContain("pmem_gefest");
    }
}
