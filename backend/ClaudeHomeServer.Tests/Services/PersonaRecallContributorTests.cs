using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Контрибьютор recall-memory всегда просит у шва выделить досье в отдельную секцию:
// гейта по флагу specialty-prompt-sections больше нет.
public class PersonaRecallContributorTests
{
    [Fact]
    public async Task BuildAsync_ПроситВыделитьДосье_ИДобавляетСекциюDossierRecall()
    {
        var recall = new Mock<IPersonaRecallSource>();
        bool? split = null;
        recall.Setup(r => r.BuildRecallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ClaudeHomeServer.Services.Dossiers.DossierRecallRequest?>(),
                It.IsAny<bool>(), It.IsAny<string?>()))
            .Callback<string, string, string, int, double, ClaudeHomeServer.Services.Dossiers.DossierRecallRequest?, bool, string?>(
                (_, _, _, _, _, _, s, _) => split = s)
            .ReturnsAsync(new PersonaRecallBlock("память", "досье", [], [], []));

        var sut = new PersonaRecallContributor(recall.Object, Mock.Of<IFeatureFlagGate>(),
            Mock.Of<IChatHistoryLoader>(), Mock.Of<IProjectManager>(),
            new ConfigurationBuilder().Build(), NullLogger<PersonaRecallContributor>.Instance);

        var ctx = new PromptSessionContext(new Session(), "owner-1", new Persona { Id = "p1" }, null, HasMemoryMcp: true);
        var result = await sut.BuildAsync(ctx, "запрос про память");

        split.Should().BeTrue("досье всегда выделяется отдельной секцией");
        result!.Sections.Select(s => s.Key).Should().Equal("recall-memory", "dossier-recall");
    }
}
