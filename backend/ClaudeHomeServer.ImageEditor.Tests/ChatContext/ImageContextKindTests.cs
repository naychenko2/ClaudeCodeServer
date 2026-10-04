using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.ImageEditor.Tests.ChatContext;

// Вид «image» контекста чата и двойная запись фокуса (ADR-023, 1б-3): Validate/Describe/засев провайдера,
// зеркалирование смены фокуса в стор, проекция фокуса в DTO нитей, Forget при удалении
public sealed class ImageContextKindTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Project = "p-1";
    private const string Chat = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "image-ctx-" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _threads;
    private readonly ImageContextKind _kind;
    private readonly ChatContextStore _context;
    private readonly ImageThreadService _service;
    private readonly Session _session = new() { Id = Chat, OwnerId = Owner };

    public ImageContextKindTests()
    {
        _threads = new ImageThreadStore(Path.Combine(_root, "image"));
        _kind = new ImageContextKind(_threads);
        _context = new ChatContextStore(Path.Combine(_root, "ctx"), new ContextKindRegistry([_kind]));
        var mirror = new ChatContextFocusMirror(_context, NullLogger<ChatContextFocusMirror>.Instance);
        _service = new ImageThreadService(_threads, NullLogger<ImageThreadService>.Instance, mirror: mirror);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
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
    public void Старый_файл_нитей_с_Focus_без_файла_контекста_засевает_основной_объект()
    {
        // Файл нитей, записанный до контекста: фокус только в собственном поле вертикали, файла контекста нет
        var id = NewThread(focus: true);
        var directory = new Moq.Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(_session);
        var seeder = new ChatContextSeeder([_kind], directory.Object, new Moq.Mock<IProjectManager>().Object);
        var context = new ChatContextStore(Path.Combine(_root, "ctx-seed"), new ContextKindRegistry([_kind]), seeder: seeder);

        var state = context.Get(Owner, Chat);

        state.Revision.Should().Be(0, "файла контекста нет — состояние производное");
        state.Primary.Should().NotBeNull("старый Focus файла нитей читается и засевает основной объект");
        ChatContextFocusMirror.ThreadOf(state.Primary!).Should().Be(id);
        File.Exists(Path.Combine(_root, "ctx-seed", Owner, Chat + ".json")).Should().BeFalse("засев файл не создаёт");

        // Первая запись применяется поверх засеянного состояния
        context.SetPrimary(Owner, Chat, null, null);
        context.Get(Owner, Chat).Primary.Should().BeNull();
    }

    [Fact]
    public async Task Человек_и_агент_зеркалят_смену_фокуса_в_стор()
    {
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
    public async Task Фокус_агента_возвращает_снятый_человеком_объект_основным()
    {
        var a = NewThread();
        await _service.FocusAsync(Owner, Project, Chat, a, _threads.Get(Owner, Chat).Revision);
        // Человек снял объект в контексте (✕); фокус вертикали остался на нити
        _context.SetPrimary(Owner, Chat, null, null);

        await _service.AgentFocusAsync(Owner, Project, Chat, a, CancellationToken.None);

        var primary = _context.Get(Owner, Chat).Primary;
        primary.Should().NotBeNull();
        ChatContextFocusMirror.ThreadOf(primary!).Should().Be(a);
        primary!.By.Should().Be(ContextActor.Agent);
    }

    [Fact]
    public async Task Фокус_агента_не_перезаписывает_выбор_человека()
    {
        var a = NewThread();
        await _service.FocusAsync(Owner, Project, Chat, a, _threads.Get(Owner, Chat).Revision);
        var before = _context.Get(Owner, Chat);
        var chosen = before.Primary!;
        chosen.By.Should().Be(ContextActor.Human);

        // Агент фокусирует ту же нить, что человек уже выбрал основной (ADR-023, Дополнение 3)
        await _service.AgentFocusAsync(Owner, Project, Chat, a, CancellationToken.None);

        var primary = _context.Get(Owner, Chat).Primary!;
        ChatContextFocusMirror.ThreadOf(primary).Should().Be(a);
        primary.By.Should().Be(ContextActor.Human, "выбор человека агент не затирает");
        primary.Id.Should().Be(chosen.Id, "основной объект не пересоздаётся");
        _context.Get(Owner, Chat).Revision.Should().Be(before.Revision, "защищают оба слоя: стор не даёт агенту затереть выбор человека, а зеркало даже не пишет в стор");
    }

    [Fact]
    public async Task Фокус_агента_не_перезаписывает_засеянный_выбор_человека_из_старого_файла()
    {
        // Файл контекста ещё не создан: основной объект — засев из старого Focus нитей (его выбрал человек)
        var a = NewThread(focus: true);
        var directory = new Moq.Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(_session);
        var seeder = new ChatContextSeeder([_kind], directory.Object, new Moq.Mock<IProjectManager>().Object);
        var context = new ChatContextStore(Path.Combine(_root, "ctx-agent"), new ContextKindRegistry([_kind]), seeder: seeder);
        var service = new ImageThreadService(_threads, NullLogger<ImageThreadService>.Instance,
            mirror: new ChatContextFocusMirror(context, NullLogger<ChatContextFocusMirror>.Instance));
        context.Get(Owner, Chat).Primary!.By.Should().Be(ContextActor.Human);

        await service.AgentFocusAsync(Owner, Project, Chat, a, CancellationToken.None);

        var state = context.Get(Owner, Chat);
        ChatContextFocusMirror.ThreadOf(state.Primary!).Should().Be(a);
        state.Primary!.By.Should().Be(ContextActor.Human, "выбор человека агент не перезаписывает");
    }

    [Fact]
    public async Task Усыновление_файла_не_делает_нить_основной_после_снятия_человеком()
    {
        // Файл контекста уже существует (ревизия > 0), основного объекта нет: человек снял его сам
        _context.SetPrimary(Owner, Chat, null, null);

        await _service.AdoptFileAsync(Owner, Project, Chat, "gen/a.png", CancellationToken.None);

        _threads.Get(Owner, Chat).Threads.Should().ContainSingle("нить усыновлена");
        _context.Get(Owner, Chat).Primary.Should().BeNull("усыновитель в контекст чата не пишет");
    }

    // Выбор человека — запись в стор контекста (как делает фронт), а не FocusAsync: сырой Focus нити при этом пуст
    [Fact]
    public async Task Усыновление_файла_не_уводит_контекст_от_выбора_человека()
    {
        var mine = NewThread();
        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("image", mine, ContextActor.Human), null);

        await _service.AdoptFileAsync(Owner, Project, Chat, "gen/a.png", CancellationToken.None);

        var primary = _context.Get(Owner, Chat).Primary!;
        ChatContextFocusMirror.ThreadOf(primary).Should().Be(mine);
        primary.By.Should().Be(ContextActor.Human, "выбор человека усыновление не трогает");
    }

    [Fact]
    public void Фокус_в_DTO_нитей_берётся_из_контекста()
    {
        var id = NewThread(focus: true);

        _service.View(Owner, Chat).Focus.Should().BeNull("в контексте основным объектом картинка не выбрана");
        _threads.Get(Owner, Chat).Focus.Should().Be(id, "собственное поле хранилища не меняется");

        _context.SetPrimary(Owner, Chat, ChatContextFocusMirror.NewItem("image", id, ContextActor.Human), null);
        _service.View(Owner, Chat).Focus.Should().Be(id);
    }

    [Fact]
    public async Task Удаление_нити_убирает_её_отовсюду_в_контексте()
    {
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
