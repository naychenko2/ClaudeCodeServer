using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Секция специальности включена всегда (флага specialty-prompt-sections больше нет):
// решают только наличие персоны со специальностью и провайдера секций.
public class PromptSectionsContributorTests
{
    private static IPromptSectionProvider Provider(string text)
    {
        var mock = new Mock<IPromptSectionProvider>();
        mock.Setup(p => p.EffectivePromptSections(It.IsAny<string>(), It.IsAny<PersonaSpecialty>()))
            .Returns([new EffectivePromptSection("history", true, text, default, default)]);
        return mock.Object;
    }

    private static PromptSessionContext Ctx(Persona? persona) =>
        new(new Session(), "owner-1", persona, null, HasMemoryMcp: true);

    [Fact]
    public async Task ЕстьПерсонаСоСпециальностьюИПровайдер_СекцияПрисутствует()
    {
        var sut = new PromptSectionsContributor(Provider("как работать с историей"));
        var ctx = Ctx(new Persona { Id = "p1", Specialty = PersonaSpecialty.Planner });

        sut.IsEnabled(ctx).Should().BeTrue();
        var result = await sut.BuildAsync(ctx, null);

        result!.Sections.Should().ContainSingle(s => s.Key == "prompt-sections")
            .Which.Text.Should().Be("как работать с историей");
    }

    [Fact]
    public void НетПерсоны_СекцииНет() =>
        new PromptSectionsContributor(Provider("текст")).IsEnabled(Ctx(null)).Should().BeFalse();

    [Fact]
    public void ПерсонаБезСпециальности_СекцииНет() =>
        new PromptSectionsContributor(Provider("текст"))
            .IsEnabled(Ctx(new Persona { Id = "p1", Specialty = PersonaSpecialty.None })).Should().BeFalse();

    [Fact]
    public void НетПровайдераСекций_СекцииНет() =>
        new PromptSectionsContributor(null)
            .IsEnabled(Ctx(new Persona { Id = "p1", Specialty = PersonaSpecialty.Planner })).Should().BeFalse();

    // --- P1c: слой секций персоны (Г6) ---

    // Провайдер с несколькими секциями специальности: roleRules + history + processes.
    private static IPromptSectionProvider MultiProvider()
    {
        var mock = new Mock<IPromptSectionProvider>();
        mock.Setup(p => p.EffectivePromptSections(It.IsAny<string>(), It.IsAny<PersonaSpecialty>()))
            .Returns([
                new EffectivePromptSection("roleRules", true, "текст роли по умолчанию", default, default),
                new EffectivePromptSection("history", true, "как работать с историей", default, default),
                new EffectivePromptSection("processes", true, "правила процессов", default, default),
            ]);
        return mock.Object;
    }

    [Fact]
    public async Task ПерсонаСоСвоимТекстомRoleRules_ВыводСодержитТекстПерсоны_ОстальныеСекцииКакУСпециальности()
    {
        var sut = new PromptSectionsContributor(MultiProvider());
        var persona = new Persona
        {
            Id = "p1",
            Specialty = PersonaSpecialty.Planner,
            PromptSections = [new PromptSectionOverride("roleRules", null, "свой текст роли")],
        };
        var ctx = Ctx(persona);

        var result = await sut.BuildAsync(ctx, null);

        var text = result!.Sections.Single(s => s.Key == "prompt-sections").Text;
        text.Should().Contain("свой текст роли");
        text.Should().Contain("как работать с историей");
        text.Should().Contain("правила процессов");
    }

    [Fact]
    public async Task ПерсонаВыключилаСекциюProcesses_СекцииНетВВыводе()
    {
        var sut = new PromptSectionsContributor(MultiProvider());
        var persona = new Persona
        {
            Id = "p1",
            Specialty = PersonaSpecialty.Planner,
            PromptSections = [new PromptSectionOverride("processes", false, null)],
        };
        var ctx = Ctx(persona);

        var result = await sut.BuildAsync(ctx, null);

        var text = result!.Sections.Single(s => s.Key == "prompt-sections").Text;
        text.Should().NotContain("правила процессов");
        text.Should().Contain("текст роли по умолчанию");
    }

    [Fact]
    public async Task ДваBuildAsyncСРазнымиPromptSections_РазныйВывод()
    {
        var sut = new PromptSectionsContributor(MultiProvider());
        var persona1 = new Persona
        {
            Id = "p1",
            Specialty = PersonaSpecialty.Planner,
            PromptSections = [new PromptSectionOverride("roleRules", null, "первый текст")],
        };
        var persona2 = new Persona
        {
            Id = "p1",
            Specialty = PersonaSpecialty.Planner,
            PromptSections = [new PromptSectionOverride("roleRules", null, "второй текст")],
        };

        var r1 = await sut.BuildAsync(Ctx(persona1), null);
        var r2 = await sut.BuildAsync(Ctx(persona2), null);

        var t1 = r1!.Sections.Single(s => s.Key == "prompt-sections").Text;
        var t2 = r2!.Sections.Single(s => s.Key == "prompt-sections").Text;
        t1.Should().Contain("первый текст").And.NotContain("второй текст");
        t2.Should().Contain("второй текст").And.NotContain("первый текст");
    }
}
