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
}
