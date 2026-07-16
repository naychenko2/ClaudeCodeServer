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

    // Контекст генерации: дефолт — web выключен, tasks/notes включены, без привязок
    private static PersonaAgentFileContext Ctx(bool web = false, bool tasks = true,
        bool notes = true, string? bindings = null) => new(web, tasks, notes, bindings);

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
        var text = MakeGenerator().Generate(MakePersona(), Ctx());

        text.Should().StartWith("---");
        text.Should().Contain("name: gefest");
        text.Should().Contain("description: \"Мастер (Гефест) — Кузнец и реализатор");
        text.Should().Contain("tools: Read, Grep, Glob");
        text.Should().Contain("maxTurns: 25");
        text.Should().Contain("color: purple");
    }

    [Fact]
    public void Model_НеПинитсяДажеПриЯвной()
    {
        // Файл виден чатам всех провайдеров: пин чужой модели делает сабагента
        // незапускаемым — сабагент всегда бежит на модели сессии
        MakeGenerator().Generate(MakePersona(), Ctx())
            .Should().NotContain("\nmodel:");
        MakeGenerator().Generate(MakePersona(model: "opus"), Ctx())
            .Should().NotContain("\nmodel:");
    }

    [Fact]
    public void Effort_ТолькоПриЯвном()
    {
        MakeGenerator().Generate(MakePersona(), Ctx())
            .Should().NotContain("\neffort:");
        MakeGenerator().Generate(MakePersona(effort: "high"), Ctx())
            .Should().Contain("effort: high");
    }

    [Fact]
    public void McpServers_ТолькоПриПамяти()
    {
        MakeGenerator().Generate(MakePersona(memory: false), Ctx())
            .Should().NotContain("mcpServers");
        MakeGenerator().Generate(MakePersona(), Ctx())
            .Should().Contain("mcpServers: [pmem_gefest]");
    }

    [Fact]
    public void Цвет_МапитсяВДопустимыеCli()
    {
        // brown нет в палитре CLI — ближний orange
        MakeGenerator().Generate(MakePersona(color: "brown"), Ctx())
            .Should().Contain("color: orange");
        // неизвестный цвет — поле опускается
        MakeGenerator().Generate(MakePersona(color: "magenta"), Ctx())
            .Should().NotContain("\ncolor:");
    }

    [Fact]
    public void Description_ОднострочныйИЭкранированный()
    {
        var persona = MakePersona();
        persona.Description = "Стро\"ка с кавычками\nи переносом";

        var text = MakeGenerator().Generate(persona, Ctx());

        var descLine = text.Split('\n').First(l => l.StartsWith("description:"));
        descLine.Should().Contain("\\\"").And.NotContain("Стро\"ка с кавычками\nи");
    }

    [Fact]
    public void Тело_СодержитХарактерРамкуИПамять()
    {
        var text = MakeGenerator().Generate(MakePersona(), Ctx());
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
        var text = MakeGenerator().Generate(MakePersona(memory: false), Ctx());
        text.Should().NotContain("## Твоя память").And.NotContain("pmem_gefest");
    }

    [Fact]
    public void Память_НачинаетсяСRecall()
    {
        var text = MakeGenerator().Generate(MakePersona(), Ctx());
        text.Should().Contain("mcp__pmem_gefest__memory_recall");
        text.Should().Contain("tools:").And.Match(t => t.Contains("memory_recall"));
    }

    [Fact]
    public void ГейтыTasksNotes_РежутИнструменты()
    {
        var text = MakeGenerator().Generate(MakePersona(), Ctx(tasks: false, notes: false));
        text.Should().NotContain("mcp__tasks__").And.NotContain("mcp__notes__");

        var full = MakeGenerator().Generate(MakePersona(), Ctx());
        full.Should().Contain("mcp__tasks__tasks_list").And.Contain("mcp__notes__notes_read");
    }

    [Fact]
    public void БлокПривязок_ПопадаетВТело()
    {
        var block = "## Привязанные знания и правила\n- [проект] Когда: вопросы по коду → mcp__wsp__files_read";
        var text = MakeGenerator().Generate(MakePersona(), Ctx(bindings: block));
        text.Should().Contain("## Привязанные знания и правила")
            .And.Contain("Когда: вопросы по коду");

        MakeGenerator().Generate(MakePersona(), Ctx())
            .Should().NotContain("## Привязанные знания и правила");
    }

    [Fact]
    public void Дисциплина_НейтральнаяДляСабагента()
    {
        // Дисциплина не зависит от модели персоны: файл общий для всех провайдеров
        var claude = MakeGenerator().Generate(MakePersona(model: "opus"), Ctx());
        var deepseek = MakeGenerator().Generate(MakePersona(model: "deepseek-chat"), Ctx());

        var claudeBody = claude[claude.IndexOf("## Ты — консультант", StringComparison.Ordinal)..];
        var deepseekBody = deepseek[deepseek.IndexOf("## Ты — консультант", StringComparison.Ordinal)..];
        claudeBody.Should().Be(deepseekBody);
        claude.Should().Contain("## Границы");   // NeverRules из универсального набора
        claude.Should().Contain("## Прагматизм"); // LeastChange
    }
}
