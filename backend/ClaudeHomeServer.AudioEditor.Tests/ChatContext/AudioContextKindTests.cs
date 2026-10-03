using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.ChatContext;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.ChatContext;

// Вид «audio» контекста чата и двойная запись фокуса (ADR-023, 1б-3): Validate/Describe/засев провайдера,
// зеркалирование смены фокуса в стор при флаге владельца, проекция фокуса в DTO нитей, Forget при удалении
public sealed class AudioContextKindTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-ctx-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _threads;
    private readonly AudioContextKind _kind;
    private readonly ChatContextStore _context;
    private readonly Flags _flags = new();
    private readonly AudioJobThreads _jobs;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner };

    public AudioContextKindTests()
    {
        _threads = new AudioThreadStore(Path.Combine(_root, "audio"));
        _kind = new AudioContextKind(_threads);
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), new ContextKindRegistry([_kind]));
        var mirror = new ChatContextFocusMirror(_context, _flags, NullLogger<ChatContextFocusMirror>.Instance);
        _jobs = new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance, null, null, null, mirror: mirror);
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

    [Fact]
    public void Validate_принимает_свою_нить_и_отказывает_чужой_и_неизвестной_версии()
    {
        var id = _threads.Open(Owner, Chat, "a.mp3", null, null).Thread!.Id;
        _threads.Open(Owner, "other-chat", "b.mp3", null, null);

        _kind.Validate(Scope, "audio", Ref(id)).Should().BeNull();
        _kind.Validate(Scope, "audio", Ref(id, AudioThreadVersion.OriginId)).Should().BeNull();
        _kind.Validate(Scope, "audio", Ref(id, "v99")).Should().NotBeNull();
        _kind.Validate(Scope, "audio", Ref("нет-такой")).Should().NotBeNull();
        _kind.Validate(Scope, "audio", new JsonObject()).Should().NotBeNull();
        _kind.Validate(Scope with { OwnerId = "чужой" }, "audio", Ref(id)).Should().NotBeNull("нити хранятся по владельцу");
        _kind.Validate(Scope, "image", Ref(id)).Should().NotBeNull();
    }

    [Fact]
    public void Describe_называет_нить_и_помечает_пропавшую()
    {
        var id = _threads.Open(Owner, Chat, "music/intro.mp3", null, null).Thread!.Id;

        var summary = _kind.Describe(Scope, new ContextItem("i", "audio", Ref(id), null, ContextActor.Human, DateTime.UtcNow));
        summary.Label.Should().Be("music/intro.mp3");
        summary.Missing.Should().BeFalse();

        var gone = _kind.Describe(Scope, new ContextItem("i", "audio", Ref("нет"), null, ContextActor.Human, DateTime.UtcNow));
        gone.Missing.Should().BeTrue();
    }

    [Fact]
    public void Засев_отдаёт_фокус_нити_и_ничего_без_фокуса()
    {
        _kind.SeedPrimary(Scope).Should().BeNull();
        var id = _threads.Open(Owner, Chat, "a.mp3", null, null).Thread!.Id;

        var seed = _kind.SeedPrimary(Scope)!;

        seed.Kind.Should().Be("audio");
        ChatContextFocusMirror.ThreadOf(seed).Should().Be(id);
        _kind.SeedPriority.Should().BeGreaterThan(0, "картинка засевает раньше звука");
    }

    [Fact]
    public void Фокус_агента_возвращает_снятый_человеком_объект_основным()
    {
        _flags.On = true;
        var id = _jobs.Tracked(Owner, Chat, () => _threads.Open(Owner, Chat, "a.mp3", null, null), ContextActor.Human)
            .Thread!.Id;
        _context.SetPrimary(Owner, Chat, null, null);

        _jobs.Tracked(Owner, Chat, () => _threads.SetFocus(Owner, Chat, id, null), ContextActor.Agent);

        var primary = _context.Get(Owner, Chat).Primary;
        primary.Should().NotBeNull();
        ChatContextFocusMirror.ThreadOf(primary!).Should().Be(id);
        primary!.By.Should().Be(ContextActor.Agent);
    }

    [Fact]
    public void При_флаге_смена_фокуса_попадает_в_стор_и_снимается_вместе_с_ним()
    {
        _flags.On = true;
        var id = _jobs.Tracked(Owner, Chat, () => _threads.Open(Owner, Chat, "a.mp3", null, null), ContextActor.Human)
            .Thread!.Id;

        var primary = _context.Get(Owner, Chat).Primary!;
        primary.Kind.Should().Be("audio");
        ChatContextFocusMirror.ThreadOf(primary).Should().Be(id);
        primary.By.Should().Be(ContextActor.Human);

        _jobs.Tracked(Owner, Chat, () => _threads.SetFocus(Owner, Chat, null, null), ContextActor.Agent);
        _context.Get(Owner, Chat).Primary.Should().BeNull();
    }

    [Fact]
    public async Task Усыновление_звука_агентом_без_фокуса_попадает_в_контекст()
    {
        _flags.On = true;
        _context.SetPrimary(Owner, Chat, null, null);
        var adopter = Adopter();

        await adopter.AdoptAsync(new LocalMediaAdoption(Owner, "p-1", Chat, "music_edit", "lm_a",
            [new LocalMediaAdoptedFile("gen/a.mp3", "audio/mpeg")]), CancellationToken.None);

        var thread = _threads.Get(Owner, Chat).Threads.Single();
        var primary = _context.Get(Owner, Chat).Primary;
        primary.Should().NotBeNull("усыновлённая нить стала фокусом вертикали — контекст обязан её показать");
        ChatContextFocusMirror.ThreadOf(primary!).Should().Be(thread.Id);
        primary!.By.Should().Be(ContextActor.Agent);
    }

    [Fact]
    public async Task Усыновление_звука_не_уводит_контекст_от_выбора_человека()
    {
        _flags.On = true;
        var mine = _threads.Open(Owner, Chat, "mine.mp3", null, null).Thread!.Id;
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("audio", mine, ContextActor.Human), null);

        await Adopter().AdoptAsync(new LocalMediaAdoption(Owner, "p-1", Chat, "music_edit", "lm_a",
            [new LocalMediaAdoptedFile("gen/a.mp3", "audio/mpeg")]), CancellationToken.None);

        var primary = _context.Get(Owner, Chat).Primary!;
        ChatContextFocusMirror.ThreadOf(primary).Should().Be(mine);
        primary.By.Should().Be(ContextActor.Human, "выбор человека усыновление не трогает");
    }

    private LocalAudioAdopter Adopter()
    {
        var directory = new Moq.Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = "p-1" });
        directory.Setup(d => d.ResolveOwnerId(Moq.It.IsAny<Session>())).Returns(Owner);
        var projects = new Moq.Mock<IProjectManager>();
        projects.Setup(p => p.GetById("p-1")).Returns(new Project { Id = "p-1", OwnerId = Owner, RootPath = _root });
        var gate = new Moq.Mock<IFeatureFlagGate>();
        gate.Setup(g => g.IsEnabled(Owner, Moq.It.IsAny<string>())).Returns(true);
        return new LocalAudioAdopter(_threads, _jobs, directory.Object, projects.Object, gate.Object);
    }

    [Fact]
    public void Без_флага_стор_контекста_не_трогается()
    {
        _flags.On = false;

        _jobs.Tracked(Owner, Chat, () => _threads.Open(Owner, Chat, "a.mp3", null, null), ContextActor.Human);

        _context.Get(Owner, Chat).Revision.Should().Be(0);
        File.Exists(Path.Combine(_root, "ctx", Owner, Chat + ".json")).Should().BeFalse();
    }

    [Fact]
    public void Фокус_звука_не_снимает_основную_картинку()
    {
        _flags.On = true;
        var image = new ContextItem("i1", "image", Ref("img-1"), null, ContextActor.Human, DateTime.UtcNow);
        var both = new ContextKindRegistry([_kind, new StubImageKind()]);
        var context = new ChatContextStore(Path.Combine(_root, "ctx2"), both);
        var jobs = new AudioJobThreads(_threads, NullLogger<AudioJobThreads>.Instance, null, null, null,
            mirror: new ChatContextFocusMirror(context, _flags, NullLogger<ChatContextFocusMirror>.Instance));
        var id = _threads.Open(Owner, Chat, "a.mp3", null, null).Thread!.Id;
        context.SetPrimary(Owner, Chat, image, null);

        jobs.Tracked(Owner, Chat, () => _threads.SetFocus(Owner, Chat, null, null), ContextActor.Human);

        context.Get(Owner, Chat).Primary.Should().NotBeNull("основной объект был картинкой, а не этой нитью");
        id.Should().NotBeNull();
    }

    [Fact]
    public void При_флаге_фокус_в_DTO_нитей_берётся_из_контекста()
    {
        var id = _threads.Open(Owner, Chat, "a.mp3", null, null).Thread!.Id;
        _flags.On = true;

        _jobs.View(Owner, Chat).Focus.Should().BeNull("в контексте основным объектом звук не выбран");

        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("audio", id, ContextActor.Human), null);
        _jobs.View(Owner, Chat).Focus.Should().Be(id);

        _flags.On = false;
        _context.SetPrimary(Owner, Chat, null, null);
        _jobs.View(Owner, Chat).Focus.Should().Be(id, "без флага DTO отдаёт собственное поле");
    }

    [Fact]
    public void Удаление_нити_убирает_её_отовсюду_в_контексте()
    {
        _flags.On = true;
        var id = _threads.Open(Owner, Chat, "a.mp3", null, null).Thread!.Id;
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("audio", id, ContextActor.Human), null);
        _context.AddRef(Owner, Chat, ChatContextFocusMirror.NewItem("audio", id, ContextActor.Human) with { Role = "reference" }, null);

        _jobs.Forget(Owner, Chat, id);

        var state = _context.Get(Owner, Chat);
        state.Primary.Should().BeNull();
        state.Refs.Should().BeEmpty();
    }

    private sealed class StubImageKind : IContextKindProvider
    {
        public IReadOnlyList<string> Kinds { get; } = ["image"];
        public bool CanBePrimary(string kind) => true;
        public string? Validate(ContextScope scope, string kind, JsonObject reference) => null;
        public ContextItemSummary Describe(ContextScope scope, ContextItem item) => new("img", null, null, false);
        public IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op) => [];
        public string? DescribeExecutor(ContextScope scope, ContextItem primary) => null;
    }
}
