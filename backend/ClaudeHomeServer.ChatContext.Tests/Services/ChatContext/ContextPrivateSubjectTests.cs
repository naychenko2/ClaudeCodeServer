using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// F2-P2i: субъект приватности в путях рендера контекста. Своё private не гаснет («Missing: false»),
// чужое — гаснет с тем же текстом отказа; неизвестный актор не подменяется арендатором
public sealed class ContextPrivateSubjectTests : IDisposable
{
    private const string Tenant = "tenant";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctx-subj-" + Guid.NewGuid().ToString("N"));
    private readonly ContextKindRegistry _registry = new([new ProjectFileContextKind()]);
    private readonly Project _project;

    public ContextPrivateSubjectTests()
    {
        _project = new Project { Id = "p1", RootPath = _root };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string PrivateFile(string fileOf)
    {
        var dir = Path.Combine(_root, ".cc-attachments", "private", fileOf, "g1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pic.png"), "x");
        return $".cc-attachments/private/{fileOf}/g1/pic.png";
    }

    private static ChatContextState StateWithRef(string path) =>
        new(1, null, [new ContextItem("ci1", ProjectFileContextKind.Kind, new JsonObject { ["path"] = path },
            null, ContextActor.Agent, DateTime.UtcNow)]);

    // Общий чат: арендатор отличается от участника; приватный чат: актор — сам арендатор
    [Theory]
    [InlineData("member-a", "member-a", false)] // общий чат, своё
    [InlineData("member-a", "member-b", true)]  // общий чат, чужое
    [InlineData(Tenant, Tenant, false)]         // приватный чат владельца, своё
    [InlineData(Tenant, "member-a", true)]      // приватный чат владельца, чужое
    [InlineData(null, "member-a", true)]        // субъект неизвестен — fail-closed, арендатором не подменяем
    public void Dto_свой_private_не_гаснет_чужой_гаснет(string? actor, string fileOf, bool missing)
    {
        var path = PrivateFile(fileOf);
        var scope = new ContextScope(Tenant, new Session { Id = Chat }, _project, actor);

        var dto = ChatContextDtoBuilder.Build(_registry, scope, StateWithRef(path));

        dto.Refs.Single().Missing.Should().Be(missing);
    }

    private (TurnContextContributor Contributor, PromptSessionContext Ctx) Contributor(string? actor, string path)
    {
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById("p1")).Returns(_project);
        var store = new Mock<IChatContextStore>();
        store.Setup(s => s.Get(Tenant, Chat)).Returns(StateWithRef(path));
        var actors = new Mock<ITurnActorRegistry>();
        actors.Setup(a => a.Actor(Chat, Tenant)).Returns(actor);
        var services = new ServiceCollection()
            .AddSingleton(store.Object).AddSingleton(actors.Object).BuildServiceProvider();
        var session = new Session { Id = Chat, OwnerId = Tenant, ProjectId = "p1" };
        return (new TurnContextContributor(services, _registry, projects.Object),
            new PromptSessionContext(session, Tenant, null, _root));
    }

    // Агентский путь: актор хода берётся из реестра, а не из арендатора
    [Theory]
    [InlineData("member-a", "member-a", false)]
    [InlineData("member-a", "member-b", true)]
    [InlineData(null, "member-a", true)]
    public async Task Хвост_хода_судит_актора_из_реестра(string? actor, string fileOf, bool missing)
    {
        var path = PrivateFile(fileOf);
        var (contributor, ctx) = Contributor(actor, path);

        var tail = (await contributor.BuildAsync(ctx, "привет"))!.Sections.Single().Text;

        tail.Contains("(недоступен)").Should().Be(missing, tail);
    }

    [Fact]
    public void Compose_с_актором_тулсета_не_гасит_своё_и_гасит_чужое()
    {
        var own = PrivateFile("member-a");
        var foreign = PrivateFile("member-b");
        var (contributor, ctx) = Contributor(null, own);

        contributor.Compose(Tenant, ctx.Session, _project, _root, "member-a").Should().NotContain("(недоступен)");
        contributor.Compose(Tenant, ctx.Session, _project, _root, "member-b").Should().Contain("(недоступен)");
        foreign.Should().NotBeNull();
    }
}
