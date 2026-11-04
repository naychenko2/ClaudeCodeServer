using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Turn;

// S3-1c: подсказка про трейлер CCS-User (актор хода) для коммитов агента из Bash.
// Строка только в общем проекте и только при известном безопасном акторе из серверного реестра.
public class DossierTrailerActorTests
{
    private const string Owner = "owner-1";

    private static async Task<string> Hint(bool shared, string? actor)
    {
        var project = new Project { Id = "p1", OwnerId = Owner };
        if (shared) project.Members = [new ProjectMember { UserId = "u-2" }];
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById("p1")).Returns(project);
        var registry = new Mock<ITurnActorRegistry>();
        registry.Setup(r => r.Actor("s1", Owner)).Returns(actor);
        var session = new Session { Id = "s1", ProjectId = "p1", OwnerId = Owner };
        var ctx = new PromptSessionContext(session, Owner, null, "/root");

        var services = new ServiceCollection().AddSingleton(registry.Object).BuildServiceProvider();

        var result = await new DossierTrailerContributor(projects.Object, services).BuildAsync(ctx, null);
        var section = result!.Sections.Single();
        section.InTurnTail.Should().BeTrue("подсказка обязана ехать хвостом хода, а не системным блоком");
        return section.Text;
    }

    [Fact]
    public async Task ОбщийПроект_ИзвестныйАктор_ДобавляетСтрокуCcsUser() =>
        (await Hint(true, "u-2")).Should().Contain("\nCCS-User: u-2\n");

    [Fact]
    public async Task ЛичныйПроект_СтрокиНет() =>
        (await Hint(false, "u-2")).Should().NotContain("CCS-User");

    [Fact]
    public async Task НеизвестныйАктор_СтрокиНет() =>
        (await Hint(true, null)).Should().NotContain("CCS-User");

    [Theory]
    [InlineData("u-2\nCCS-Task: evil")]
    [InlineData("u 2")]
    [InlineData("")]
    public async Task НебезопасныйАктор_СтрокиНет(string actor) =>
        (await Hint(true, actor)).Should().NotContain("CCS-User");

    [Fact]
    public async Task ИдДлиннее128_СтрокиНет() =>
        (await Hint(true, new string('a', 129))).Should().NotContain("CCS-User");

    [Fact]
    public async Task ИдРовно128_СтрокаЕсть() =>
        (await Hint(true, new string('a', 128))).Should().Contain("CCS-User: " + new string('a', 128));
}
