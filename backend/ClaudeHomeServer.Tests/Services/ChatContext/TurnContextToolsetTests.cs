using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Тулсет агента turn-context (ADR-023 §3.2, 2б-5): состав стабилен и стор не читает; context_state — тот же
// текст, что хвост хода; attach/detach проверяют Ref до записи, ставят By = Agent и fail-closed отказывают
// на делегированном ходу; локальный проект и чужой владелец — отказ без записи.
public sealed class TurnContextToolsetTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Stranger = "owner-2";
    private const string Chat = "chat-1";
    private const string ProjectId = "p-1";
    private const string LocalProjectId = "p-local";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tctx-toolset-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly ContextKindRegistry _registry;
    private readonly ChatContextStore _store;
    private readonly Mock<IDelegatedTurnGate> _gate = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private bool _flag = true;

    public TurnContextToolsetTests()
    {
        _project = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(_project, "art"));
        File.WriteAllBytes(Path.Combine(_project, "art", "palette.png"), [1, 2, 3]);
        _registry = new ContextKindRegistry([new FakeImageKind(), new ProjectFileContextKind()]);
        _store = new ChatContextStore(Path.Combine(_root, "ctx"), _registry);
        _sessions[Chat] = new Session { Id = Chat, OwnerId = Owner, ProjectId = ProjectId };
        _sessions["chat-local"] = new Session { Id = "chat-local", OwnerId = Owner, ProjectId = LocalProjectId };
        _sessions["chat-personal"] = new Session { Id = "chat-personal", OwnerId = Owner };
        _gate.Setup(g => g.Deny(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeImageKind : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds { get; } = ["image"];
        public bool CanBePrimary(string kind) => true;
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => new("hero.png", null, null, false);
        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) =>
            [new("style", "Образец стиля", ["project-file"], ["edit"])];
        public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
    }

    private sealed class Flag(Func<bool> on) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => on() && key == FeatureFlagKeys.ComposerContextRow;
    }

    private (TurnContextToolset Toolset, TurnContextContributor Contributor) Build(IChatContextStore? store = null,
        bool withGate = true)
    {
        var flags = new Flag(() => _flag);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _project });
        projects.Setup(p => p.GetById(LocalProjectId)).Returns(new Project
            { Id = LocalProjectId, OwnerId = Owner, RootPath = "/home/dev/proj", DeviceId = "dev-1" });
        var services = new ServiceCollection().AddSingleton<IChatContextStore>(store ?? _store).BuildServiceProvider();
        var contributor = new TurnContextContributor(services, _registry, flags, projects.Object);
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string id, string owner) => _sessions.GetValueOrDefault(id) is { } s && s.OwnerId == owner ? s : null);
        var toolset = new TurnContextToolset(accessor.Object, projects.Object, _registry, store ?? _store, flags, contributor,
            withGate ? _gate.Object : null);
        return (toolset, contributor);
    }

    private static McpToolCallContext Ctx(string owner = Owner, string tail = Chat) => new(owner, tail, tail);

    private static Task<McpToolCallResult> Call(TurnContextToolset toolset, string tool, JsonObject? args = null,
        McpToolCallContext? ctx = null) =>
        toolset.CallAsync(tool, args ?? new JsonObject(), ctx ?? Ctx(), default);

    private static JsonObject Attach(string kind, JsonObject reference, string? role = null)
    {
        var args = new JsonObject { ["kind"] = kind, ["ref"] = reference };
        if (role is not null) args["role"] = role;
        return args;
    }

    private static JsonObject FileRef(string path) => new() { ["path"] = path };

    private void Primary() =>
        _store.SetPrimary(Owner, Chat, new ContextItem("p", "image", new JsonObject { ["threadId"] = "t1" }, null,
            ContextActor.Human, DateTime.UtcNow), null);

    // ── Состав ──

    [Fact]
    public void Состав_фиксирован_и_не_читает_стор()
    {
        var strict = new Mock<IChatContextStore>(MockBehavior.Strict);
        var (toolset, _) = Build(strict.Object);

        var names = toolset.ToolsFor(Ctx()).Select(t => t.Name).ToList();

        names.Should().Equal("context_state", "context_attach", "context_detach");
        strict.VerifyNoOtherCalls();
    }

    [Fact]
    public void Состав_не_зависит_от_содержимого_контекста()
    {
        var (toolset, _) = Build();
        var empty = toolset.ToolsFor(Ctx()).Select(t => (t.Name, t.Description, t.InputSchema.ToJsonString())).ToList();

        Primary();
        _store.AddRef(Owner, Chat, new ContextItem("r", "project-file", FileRef("art/palette.png"), "style",
            ContextActor.Agent, DateTime.UtcNow), null);

        toolset.ToolsFor(Ctx()).Select(t => (t.Name, t.Description, t.InputSchema.ToJsonString()))
            .Should().Equal(empty);
    }

    [Fact]
    public void Чужой_владелец_без_флага_и_битый_хвост_дают_пустой_состав()
    {
        var (toolset, _) = Build();

        toolset.ToolsFor(Ctx(Stranger)).Should().BeEmpty("чужая сессия недоступна");
        toolset.ToolsFor(Ctx(tail: "../x")).Should().BeEmpty("хвост — только id сессии");
        _flag = false;
        toolset.ToolsFor(Ctx()).Should().BeEmpty("без флага владельца сервера нет");
    }

    // ── context_state ──

    [Fact]
    public async Task Context_state_отдаёт_тот_же_текст_что_хвост_хода_с_идентификаторами()
    {
        var (toolset, contributor) = Build();
        Primary();
        await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "style"));

        var state = await Call(toolset, TurnContextToolset.ToolState);

        var session = _sessions[Chat];
        var tail = (await contributor.BuildAsync(new PromptSessionContext(session, Owner, null, _project), "привет"))!
            .Sections.Single().Text;
        state.IsError.Should().BeFalse();
        state.Text.Should().Be(tail);
        state.Text.Should().Contain("palette.png [art/palette.png]").And.Contain("✦ поставил Claude");
    }

    [Fact]
    public async Task Context_state_работает_на_делегированном_ходу()
    {
        var (toolset, _) = Build();
        _gate.Setup(g => g.Deny(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns("делегированный ход");

        (await Call(toolset, TurnContextToolset.ToolState)).IsError.Should().BeFalse();
    }

    // ── context_attach ──

    [Fact]
    public async Task Attach_кладёт_референс_от_агента_с_ролью()
    {
        var (toolset, _) = Build();
        Primary();

        var result = await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "style"));

        result.IsError.Should().BeFalse(result.Text);
        var added = _store.Get(Owner, Chat).Refs.Single();
        added.Kind.Should().Be("project-file");
        added.Role.Should().Be("style");
        added.By.Should().Be(ContextActor.Agent);
        added.Ref["path"]!.GetValue<string>().Should().Be("art/palette.png");
        result.Text.Should().Contain(added.Id);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("/etc/passwd")]
    public async Task Attach_с_мусорным_ref_отказ_и_стор_не_тронут(string path)
    {
        var (toolset, _) = Build();
        Primary();
        var before = _store.Get(Owner, Chat);

        var result = await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef(path), "style"));

        result.IsError.Should().BeTrue();
        _store.Get(Owner, Chat).Revision.Should().Be(before.Revision, "до записи Validate провайдера отказал");
    }

    [Fact]
    public async Task Attach_без_ref_и_с_неизвестным_видом_отказ_без_записи()
    {
        var (toolset, _) = Build();

        (await Call(toolset, TurnContextToolset.ToolAttach, new JsonObject { ["kind"] = "project-file" })).IsError.Should().BeTrue();
        (await Call(toolset, TurnContextToolset.ToolAttach, Attach("video-scene", FileRef("a")))).IsError.Should().BeTrue();

        _store.Get(Owner, Chat).Revision.Should().Be(0);
    }

    [Fact]
    public async Task Attach_с_ролью_которую_основной_объект_не_принимает_отказ_без_записи()
    {
        var (toolset, _) = Build();
        Primary();
        var before = _store.Get(Owner, Chat);

        var wrongRole = await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "face"));
        wrongRole.IsError.Should().BeTrue();
        _store.Get(Owner, Chat).Revision.Should().Be(before.Revision);
    }

    [Fact]
    public async Task Attach_в_локальном_проекте_отказ_до_диска()
    {
        var (toolset, _) = Build();

        var result = await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("a.png")),
            Ctx(tail: "chat-local"));

        result.IsError.Should().BeTrue();
        _store.Get(Owner, "chat-local").Revision.Should().Be(0);
    }

    [Fact]
    public async Task Attach_и_detach_на_делегированном_ходу_отказ_fail_closed()
    {
        var (toolset, _) = Build();
        Primary();
        _gate.Setup(g => g.Deny(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns("делегированный ход");
        var before = _store.Get(Owner, Chat);

        var attach = await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "style"));
        var detach = await Call(toolset, TurnContextToolset.ToolDetach, new JsonObject { ["itemId"] = "ci_x" });

        attach.IsError.Should().BeTrue();
        attach.Text.Should().Contain("делегированный ход");
        detach.IsError.Should().BeTrue();
        _store.Get(Owner, Chat).Revision.Should().Be(before.Revision);
    }

    [Fact]
    public async Task Без_гейта_делегирования_attach_и_detach_отказывают_по_построению()
    {
        var (toolset, _) = Build(withGate: false);
        Primary();

        (await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "style"))).IsError
            .Should().BeTrue();
        (await Call(toolset, TurnContextToolset.ToolDetach, new JsonObject { ["itemId"] = "ci_x" })).IsError.Should().BeTrue();
        (await Call(toolset, TurnContextToolset.ToolState)).IsError.Should().BeFalse("чтение гейта не требует");
    }

    [Fact]
    public async Task Гейт_зовётся_на_каждый_attach_и_detach_но_не_на_state()
    {
        var (toolset, _) = Build();
        Primary();

        await Call(toolset, TurnContextToolset.ToolState);
        _gate.Verify(g => g.Deny(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "style"));
        await Call(toolset, TurnContextToolset.ToolDetach, new JsonObject { ["itemId"] = "ci_x" });
        _gate.Verify(g => g.Deny(Owner, Chat, It.IsAny<string>()), Times.Exactly(2));
    }

    // ── context_detach ──

    [Fact]
    public async Task Detach_убирает_референс_а_основной_объект_не_снимает()
    {
        var (toolset, _) = Build();
        Primary();
        await Call(toolset, TurnContextToolset.ToolAttach, Attach("project-file", FileRef("art/palette.png"), "style"));
        var itemId = _store.Get(Owner, Chat).Refs.Single().Id;

        (await Call(toolset, TurnContextToolset.ToolDetach, new JsonObject { ["itemId"] = "p" })).IsError.Should().BeTrue(
            "основной объект снимается через image_focus / audio_focus");
        _store.Get(Owner, Chat).Primary.Should().NotBeNull();

        var removed = await Call(toolset, TurnContextToolset.ToolDetach, new JsonObject { ["itemId"] = itemId });
        removed.IsError.Should().BeFalse(removed.Text);
        _store.Get(Owner, Chat).Refs.Should().BeEmpty();
    }

}
