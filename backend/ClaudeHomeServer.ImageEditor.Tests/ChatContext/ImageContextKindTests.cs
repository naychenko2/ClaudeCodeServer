using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.ImageEditor.Tests.ChatContext;

// Вид «image» контекста чата и двойная запись фокуса (ADR-023, 1б-3): Validate/Describe/засев провайдера,
// зеркалирование смены фокуса в стор при флаге владельца, проекция фокуса в DTO нитей, Forget при удалении
public sealed class ImageContextKindTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Project = "p-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "image-ctx-" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _threads;
    private readonly ImageContextKind _kind;
    private readonly ChatContextStore _context;
    private readonly Flags _flags = new();
    private readonly ImageThreadService _service;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner };

    public ImageContextKindTests()
    {
        _threads = new ImageThreadStore(Path.Combine(_root, "image"));
        _kind = new ImageContextKind(_threads);
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), new ContextKindRegistry([_kind]));
        var mirror = new ChatContextFocusMirror(_context, _flags, NullLogger<ChatContextFocusMirror>.Instance);
        _service = new ImageThreadService(_threads, NullLogger<ImageThreadService>.Instance, mirror: mirror);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Flags : IFeatureFlagGate
    {
        public bool On { get; set; }
        public bool IsEnabled(string userId, string key) => On && key == FeatureFlagKeys.ComposerContextRow;
    }

    private ContextScope Scope => new(Owner, _session, null);

    private static JsonObject Ref(string threadId, string? versionId = null) =>
        versionId is null ? new JsonObject { ["threadId"] = threadId } : new JsonObject { ["threadId"] = threadId, ["versionId"] = versionId };

    private string NewThread(bool focus = false) => _threads.Create(Owner, Chat, null, "", focus).Thread.Id;

    [Fact]
    public void Validate_принимает_свою_нить_и_отказывает_чужой_и_неизвестной_версии()
    {
        var id = NewThread();
        _threads.Create(Owner, "other-chat", null, "", focus: false);

        _kind.Validate(Scope, "image", Ref(id)).Should().BeNull();
        _kind.Validate(Scope, "image", Ref(id, ImageThreadVersion.OriginId)).Should().BeNull();
        _kind.Validate(Scope, "image", Ref(id, "v99")).Should().NotBeNull();
        _kind.Validate(Scope, "image", Ref("нет-такой")).Should().NotBeNull();
        _kind.Validate(Scope, "image", new JsonObject()).Should().NotBeNull();
        _kind.Validate(Scope with { OwnerId = "чужой" }, "image", Ref(id)).Should().NotBeNull("нити хранятся по владельцу");
        _kind.Validate(Scope, "audio", Ref(id)).Should().NotBeNull();
    }

    [Fact]
    public void Describe_называет_нить_и_помечает_пропавшую()
    {
        var id = _threads.Create(Owner, Chat, "images/hero.png", null, focus: false).Thread.Id;

        var summary = _kind.Describe(Scope, new ContextItem("i", "image", Ref(id), null, ContextActor.Human, DateTime.UtcNow));
        summary.Label.Should().Be("images/hero.png");
        summary.Missing.Should().BeFalse();

        _kind.Describe(Scope, new ContextItem("i", "image", Ref("нет"), null, ContextActor.Human, DateTime.UtcNow))
            .Missing.Should().BeTrue();
    }

    [Fact]
    public void Засев_отдаёт_фокус_нити_и_ничего_без_фокуса()
    {
        NewThread(focus: false);
        _kind.SeedPrimary(Scope).Should().BeNull();
        var id = NewThread(focus: true);

        var seed = _kind.SeedPrimary(Scope)!;

        seed.Kind.Should().Be("image");
        ChatContextFocusMirror.ThreadOf(seed).Should().Be(id);
        _kind.SeedPriority.Should().Be(0);
    }

    [Fact]
    public async Task При_флаге_человек_и_агент_зеркалят_смену_фокуса_в_стор()
    {
        _flags.On = true;
        var a = NewThread();
        var b = NewThread();

        await _service.FocusAsync(Owner, Project, Chat, a, _threads.Get(Owner, Chat).Revision);
        var human = _context.Get(Owner, Chat).Primary!;
        ChatContextFocusMirror.ThreadOf(human).Should().Be(a);
        human.By.Should().Be(ContextActor.Human);

        await _service.AgentFocusAsync(Owner, Project, Chat, b, CancellationToken.None);
        var agent = _context.Get(Owner, Chat).Primary!;
        ChatContextFocusMirror.ThreadOf(agent).Should().Be(b);
        agent.By.Should().Be(ContextActor.Agent);

        await _service.FocusAsync(Owner, Project, Chat, null, _threads.Get(Owner, Chat).Revision);
        _context.Get(Owner, Chat).Primary.Should().BeNull();
    }

    [Fact]
    public async Task Без_флага_стор_контекста_не_трогается()
    {
        _flags.On = false;
        var a = NewThread();

        await _service.FocusAsync(Owner, Project, Chat, a, _threads.Get(Owner, Chat).Revision);

        File.Exists(Path.Combine(_root, "ctx", Owner, Chat + ".json")).Should().BeFalse();
        _service.View(Owner, Chat).Focus.Should().Be(a);
    }

    [Fact]
    public void При_флаге_фокус_в_DTO_нитей_берётся_из_контекста()
    {
        var id = NewThread(focus: true);
        _flags.On = true;

        _service.View(Owner, Chat).Focus.Should().BeNull("в контексте основным объектом картинка не выбрана");
        _threads.Get(Owner, Chat).Focus.Should().Be(id, "собственное поле хранилища не меняется");

        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("image", id, ContextActor.Human), null);
        _service.View(Owner, Chat).Focus.Should().Be(id);

        _flags.On = false;
        _context.SetPrimary(Owner, Chat, null, null);
        _service.View(Owner, Chat).Focus.Should().Be(id, "без флага DTO отдаёт собственное поле");
    }

    [Fact]
    public async Task Удаление_нити_убирает_её_отовсюду_в_контексте()
    {
        _flags.On = true;
        var id = NewThread();
        await _service.FocusAsync(Owner, Project, Chat, id, _threads.Get(Owner, Chat).Revision);
        _context.AddRef(Owner, Chat, ChatContextFocusMirror.NewItem("image", id, ContextActor.Human) with { Role = "style" }, null);

        var removed = await _service.RemoveAsync(Owner, Project, Chat, id, _threads.Get(Owner, Chat).Revision);

        removed.Status.Should().Be(ImageThreadWriteStatus.Ok);
        var state = _context.Get(Owner, Chat);
        state.Primary.Should().BeNull();
        state.Refs.Should().BeEmpty();
    }
}
