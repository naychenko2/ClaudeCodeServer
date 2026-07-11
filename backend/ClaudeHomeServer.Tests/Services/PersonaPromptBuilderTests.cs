using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сборка системного промпта персоны: секции контракта (P1) + дисциплина провайдера (P2)
public class PersonaPromptBuilderTests
{
    private static Persona MakePersona(PersonaContract? contract = null, string? systemPrompt = null,
        string? greeting = null) => new()
    {
        Name = "Марк",
        Role = "Ревьюер",
        Description = "строгая проверка кода",
        SystemPrompt = systemPrompt,
        Contract = contract,
        Greeting = greeting,
    };

    private static string Build(Persona p, string providerKey = "claude",
        bool switched = false, bool greeted = false) =>
        PersonaPromptBuilder.BuildCore(p, providerKey, switched, greeted);

    [Fact]
    public void БезКонтракта_LegacySystemPrompt_ЕдинымБлоком()
    {
        var prompt = Build(MakePersona(systemPrompt: "Ты строг, но справедлив."));

        prompt.Should().StartWith("Ты — Ревьюер по имени Марк, строгая проверка кода.");
        prompt.Should().Contain("Ты строг, но справедлив.");
        prompt.Should().NotContain("## Характер");
    }

    [Fact]
    public void Контракт_ПриоритетнееLegacySystemPrompt()
    {
        var persona = MakePersona(
            contract: new PersonaContract { Character = "Ты дотошный и въедливый." },
            systemPrompt: "СТАРЫЙ ТЕКСТ ХАРАКТЕРА");

        var prompt = Build(persona);

        prompt.Should().Contain("## Характер\nТы дотошный и въедливый.");
        prompt.Should().NotContain("СТАРЫЙ ТЕКСТ ХАРАКТЕРА");
    }

    [Fact]
    public void ПустойКонтракт_ЭквивалентенLegacy()
    {
        var persona = MakePersona(
            contract: new PersonaContract { Character = "  ", MustDo = ["", " "] },
            systemPrompt: "Legacy-характер.");

        Build(persona).Should().Contain("Legacy-характер.");
    }

    [Fact]
    public void ПустыеСлоты_СекцииПропускаются()
    {
        var persona = MakePersona(contract: new PersonaContract { Character = "Ты краток." });

        var prompt = Build(persona);

        prompt.Should().Contain("## Характер");
        prompt.Should().NotContain("## Тон");
        prompt.Should().NotContain("## Всегда");
        prompt.Should().NotContain("## Никогда");
        prompt.Should().NotContain("## Формат ответов");
        prompt.Should().NotContain("## Примеры твоих реплик");
    }

    [Fact]
    public void ВсеСлоты_КаждыйВСвоейСекции()
    {
        var persona = MakePersona(contract: new PersonaContract
        {
            Character = "Ты дотошный.",
            Tone = "сухо и по делу",
            MustDo = ["Выноси вердикт первым", "Сортируй замечания по важности"],
            MustNot = ["Не переписывай чужую работу"],
            OutputFormat = "Вердикт, затем до трёх замечаний.",
            SpeechExamples = ["Принято. Одно замечание по краевому случаю."],
        });

        var prompt = Build(persona);

        prompt.Should().Contain("## Тон\nсухо и по делу");
        prompt.Should().Contain("## Всегда\n- Выноси вердикт первым\n- Сортируй замечания по важности");
        prompt.Should().Contain("## Никогда\n- Не переписывай чужую работу");
        prompt.Should().Contain("## Формат ответов\nВердикт, затем до трёх замечаний.");
        prompt.Should().Contain("## Примеры твоих реплик\n> Принято. Одно замечание по краевому случаю.");
        prompt.Should().Contain("образцы стиля, а не готовые ответы");
    }

    [Theory]
    [InlineData(false, "Привет! Что проверить?", false)] // greeted=false — секции нет
    [InlineData(true, null, false)]                      // приветствие пустое — секции нет
    [InlineData(true, "Привет! Что проверить?", true)]   // оба условия — секция есть
    public void GreetedСекция_ТолькоПриGreetedИНепустомПриветствии(bool greeted, string? greeting, bool expected)
    {
        var prompt = Build(MakePersona(greeting: greeting), greeted: greeted);

        prompt.Contains("не здоровайся повторно").Should().Be(expected);
        if (expected) prompt.Should().Contain("«Привет! Что проверить?»");
    }

    [Fact]
    public void Switched_ДобавляетОговоркуПроЧужиеОтветы()
    {
        Build(MakePersona()).Should().NotContain("другой собеседник");
        Build(MakePersona(), switched: true)
            .Should().Contain("мог отвечать другой собеседник");
    }

    [Fact]
    public void Дисциплина_Claude_ТолькоКраткость()
    {
        var prompt = Build(MakePersona(), providerKey: "claude");

        prompt.Should().Contain(PersonaPromptBuilder.Brevity);
        prompt.Should().NotContain(PersonaPromptBuilder.Verification);
        prompt.Should().NotContain(PersonaPromptBuilder.NeverRules);
        prompt.Should().NotContain(PersonaPromptBuilder.AntiSlop);
    }

    [Fact]
    public void Дисциплина_DeepSeek_ПолныйНабор()
    {
        var prompt = Build(MakePersona(), providerKey: "deepseek");

        prompt.Should().Contain(PersonaPromptBuilder.Brevity);
        prompt.Should().Contain(PersonaPromptBuilder.Verification);
        prompt.Should().Contain(PersonaPromptBuilder.NeverRules);
        prompt.Should().Contain(PersonaPromptBuilder.AntiSlop);
    }

    [Fact]
    public void Дисциплина_Glm_БезДостоверности()
    {
        var prompt = Build(MakePersona(), providerKey: "glm");

        prompt.Should().Contain(PersonaPromptBuilder.Brevity);
        prompt.Should().NotContain(PersonaPromptBuilder.Verification);
        prompt.Should().Contain(PersonaPromptBuilder.NeverRules);
        prompt.Should().Contain(PersonaPromptBuilder.AntiSlop);
    }

    [Fact]
    public void Дисциплина_НеизвестныйПровайдер_КраткостьИГраницы()
    {
        var prompt = Build(MakePersona(), providerKey: "someprovider");

        prompt.Should().Contain(PersonaPromptBuilder.Brevity);
        prompt.Should().NotContain(PersonaPromptBuilder.Verification);
        prompt.Should().Contain(PersonaPromptBuilder.NeverRules);
        prompt.Should().NotContain(PersonaPromptBuilder.AntiSlop);
    }

    [Fact]
    public void БезРоли_ПредставлениеТолькоПоИмени()
    {
        var persona = new Persona { Name = "Ада" };

        Build(persona).Should().StartWith("Ты — Ада.");
    }
}
