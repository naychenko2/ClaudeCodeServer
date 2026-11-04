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
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string, string, int, double, ClaudeHomeServer.Services.Dossiers.DossierRecallRequest?, bool, string?, bool>(
                (_, _, _, _, _, _, s, _, _) => split = s)
            .ReturnsAsync(new PersonaRecallBlock("память", "досье", [], [], []));

        var sut = new PersonaRecallContributor(recall.Object,
            Mock.Of<IChatHistoryLoader>(), Mock.Of<IProjectManager>(),
            new ConfigurationBuilder().Build(), NullLogger<PersonaRecallContributor>.Instance);

        var ctx = new PromptSessionContext(new Session(), "owner-1", new Persona { Id = "p1" }, null, HasMemoryMcp: true);
        var result = await sut.BuildAsync(ctx, "запрос про память");

        split.Should().BeTrue("досье всегда выделяется отдельной секцией");
        result!.Sections.Select(s => s.Key).Should().Equal("recall-memory", "dossier-recall");
    }

    // S2a-4 (разрез И.3, решение 29.10): узкая рамка общего проекта — личная часть блока памяти
    // не строится вовсе (шов зовётся с personalMemory=false), из контрибьютора остаётся одна
    // dossier-секция: досье проекта — проектные данные, они остаются.
    [Fact]
    public async Task BuildAsync_NarrowFrameOnlyDossier_ShimZovetsBezPersonalnoyPamyati()
    {
        var recall = new Mock<IPersonaRecallSource>();
        bool? personal = null;
        recall.Setup(r => r.BuildRecallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ClaudeHomeServer.Services.Dossiers.DossierRecallRequest?>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Callback<string, string, string, int, double, ClaudeHomeServer.Services.Dossiers.DossierRecallRequest?, bool, string?, bool>(
                (_, _, _, _, _, _, _, _, p) => personal = p)
            // Фикстура c1 (S2a-4): маркеры-строки личной памяти и досье. Даже если шов по ошибке
            // вернул бы личную часть, в собранный текст узкого хода она не обязана попасть.
            .ReturnsAsync(new PersonaRecallBlock(
                "МАРКЕР_ЛИЧНОЙ_ПАМЯТИ запись владельца", "МАРКЕР_ДОСЬЕ_ПРОЕКТА паспорт", [], [], []));

        var sut = new PersonaRecallContributor(recall.Object,
            Mock.Of<IChatHistoryLoader>(), Mock.Of<IProjectManager>(),
            new ConfigurationBuilder().Build(), NullLogger<PersonaRecallContributor>.Instance);

        var ctx = new PromptSessionContext(new Session(), "owner-1", new Persona { Id = "p1" }, null,
            HasMemoryMcp: true, Frame: ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow);
        var result = await sut.BuildAsync(ctx, "запрос про память");

        personal.Should().BeFalse("в узкой рамке личная часть recall-блока не запрашивается");
        result!.Sections.Select(s => s.Key).Should().Equal("dossier-recall");
        var composed = string.Join("\n\n", result.Sections.Select(s => s.Text));
        composed.Should().NotContain("МАРКЕР_ЛИЧНОЙ_ПАМЯТИ",
            "в собранный текст хода общего проекта личная память владельца не попадает")
            .And.Contain("МАРКЕР_ДОСЬЕ_ПРОЕКТА", "досье проекта — проектные данные, они остаются");
    }

}
