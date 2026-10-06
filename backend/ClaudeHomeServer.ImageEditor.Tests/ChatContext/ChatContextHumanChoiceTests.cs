using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;

namespace ClaudeHomeServer.ImageEditor.Tests.ChatContext;

// Регрессия финального ревью ADR-023: выбор человека — запись в стор контекста, как делает фронт (PUT primary).
// Фоновое усыновление результата local_* и image_focus агента на ту же нить его не перебивают
public sealed class ChatContextHumanChoiceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Project = "p-1";
    private const string Chat = "chat-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-repro-" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _threads;
    private readonly ChatContextStore _context;
    private readonly ImageThreadService _service;

    public ChatContextHumanChoiceTests()
    {
        _threads = new ImageThreadStore(Path.Combine(_root, "image"));
        var kind = new ImageContextKind(_threads);
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), new ContextKindRegistry([kind, new FakeAudio()]));
        var mirror = new ChatContextFocusMirror(_context, NullLogger<ChatContextFocusMirror>.Instance);
        _service = new ImageThreadService(_threads, NullLogger<ImageThreadService>.Instance, mirror: mirror);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Усыновление_local_не_перебивает_картинку_выбранную_человеком_через_контекст()
    {
        var mine = _threads.Create(Owner, Chat, null, "", false).Thread.Id;
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("image", mine, ContextActor.Human), null);

        await _service.AdoptFileAsync(Owner, Project, Chat, "gen/a.png", CancellationToken.None);

        var p = _context.Get(Owner, Chat).Primary!;
        ChatContextFocusMirror.ThreadOf(p).Should().Be(mine);
        p.By.Should().Be(ContextActor.Human);
    }

    [Fact]
    public async Task Усыновление_local_картинки_не_перебивает_звук_выбранный_человеком()
    {
        _context.SetPrimary(Owner, Chat, new ContextItem("x", "audio", new JsonObject { ["threadId"] = "a1" }, null, ContextActor.Human, DateTime.UtcNow), null);

        await _service.AdoptFileAsync(Owner, Project, Chat, "gen/a.png", CancellationToken.None);

        var p = _context.Get(Owner, Chat).Primary!;
        p.Kind.Should().Be("audio");
        p.By.Should().Be(ContextActor.Human);
    }

    [Fact]
    public async Task image_focus_агента_на_ту_же_нить_не_стирает_закреплённую_человеком_версию()
    {
        var mine = _threads.Create(Owner, Chat, null, "", false).Thread.Id;
        _context.SetPrimary(Owner, Chat, new ContextItem("h", "image",
            new JsonObject { ["threadId"] = mine, ["versionId"] = "v1" }, null, ContextActor.Human, DateTime.UtcNow), null);

        await _service.AgentFocusAsync(Owner, Project, Chat, mine, CancellationToken.None);

        var p = _context.Get(Owner, Chat).Primary!;
        p.By.Should().Be(ContextActor.Human);
        p.Ref["versionId"]?.GetValue<string>().Should().Be("v1");
    }

    private sealed class FakeAudio : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds => ["audio"];
        public bool CanBePrimary(string kind) => true;
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => new("звук", null, null, false);
        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];
        public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
    }
}
