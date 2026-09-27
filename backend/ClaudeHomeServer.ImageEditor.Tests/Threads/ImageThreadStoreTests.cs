using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Хранилище нитей и фокуса чата (ADR-019 §1, §2): корень вне рабочей папки редактора,
// ревизия и конфликт, чужая нить неотличима от несуществующей, нити уходят с удалённым чатом и
// копируются в ветку с теми же id — по событиям шины session/deleted и session/branched.
public class ImageThreadStoreTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "ccs_threads_" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;

    public ImageThreadStoreTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_data, "projects.json") })
            .Build();
        _store = ImageThreadStore.FromConfig(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Нити_живут_в_data_image_threads_а_не_в_TTL_кеше_редактора()
    {
        _store.Create(Owner, Chat, "images/hero.png", null, focus: true);

        var path = _store.StatePath(Owner, Chat);
        File.Exists(path).Should().BeTrue();
        path.Should().Be(Path.Combine(_data, ImageThreadStore.DirName, Owner, Chat + ".json"));
        path.Should().NotContain(Path.DirectorySeparatorChar + ImageEditorPaths.WorkspaceDirName + Path.DirectorySeparatorChar,
            "рабочую папку чистит TTL на 7 дней и не берёт бэкап, а карточка в ленте живёт бессрочно");
    }

    [Fact]
    public void Пустой_чат_без_файла_нитей()
    {
        var state = _store.Get(Owner, Chat);

        state.Focus.Should().BeNull();
        state.Revision.Should().Be(0);
        state.Threads.Should().BeEmpty();
    }

    [Fact]
    public void Новая_нить_с_первой_стопкой_и_фокусом_переживает_перечитывание()
    {
        var (created, thread) = _store.Create(Owner, Chat, "images/hero.png", null, focus: true);

        created.Revision.Should().Be(1);
        created.Focus.Should().Be(thread.Id);
        thread.Stacks.Should().ContainSingle().Which.StackId.Should().Be(thread.CurrentStackId);

        var reread = new ImageThreadStore(_store.Root).Get(Owner, Chat);
        reread.Focus.Should().Be(thread.Id);
        reread.Threads.Should().ContainSingle().Which.File.Should().Be("images/hero.png");
    }

    [Fact]
    public void Черновик_по_папке_без_файла()
    {
        var (_, draft) = _store.Create(Owner, Chat, null, "art", focus: false);

        draft.File.Should().BeNull();
        draft.DraftFolder.Should().Be("art");
        _store.Get(Owner, Chat).Focus.Should().BeNull("focus: false не берёт черновик в работу");
    }

    [Fact]
    public void Нить_без_файла_и_папки_или_с_обоими_отказ()
    {
        var neither = () => _store.Create(Owner, Chat, null, null, focus: false);
        var both = () => _store.Create(Owner, Chat, "a.png", "art", focus: false);

        neither.Should().Throw<ArgumentException>();
        both.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Смена_фокуса_со_старой_ревизией_конфликт_и_не_затирает_выбор()
    {
        var (_, a) = _store.Create(Owner, Chat, "a.png", null, focus: true);
        var (state, b) = _store.Create(Owner, Chat, "b.png", null, focus: false);

        var ok = _store.SetFocus(Owner, Chat, b.Id, state.Revision);
        ok.Status.Should().Be(ImageThreadWriteStatus.Ok);
        ok.State.Focus.Should().Be(b.Id);
        ok.State.Revision.Should().Be(state.Revision + 1);

        // Вторая вкладка считала состояние до смены и снимает выбор со старой ревизией
        var stale = _store.SetFocus(Owner, Chat, a.Id, state.Revision);

        stale.Status.Should().Be(ImageThreadWriteStatus.Conflict);
        stale.State.Focus.Should().Be(b.Id, "устаревшая запись не затирает принятую");
        _store.Get(Owner, Chat).Focus.Should().Be(b.Id);
    }

    [Fact]
    public void Снять_выбор_null()
    {
        var (state, _) = _store.Create(Owner, Chat, "a.png", null, focus: true);

        var cleared = _store.SetFocus(Owner, Chat, null, state.Revision);

        cleared.Status.Should().Be(ImageThreadWriteStatus.Ok);
        _store.Get(Owner, Chat).Focus.Should().BeNull();
    }

    [Fact]
    public void Нить_другого_чата_не_находится()
    {
        var (_, foreign) = _store.Create(Owner, "chat-2", "a.png", null, focus: false);
        var (mine, _) = _store.Create(Owner, Chat, "b.png", null, focus: false);

        var result = _store.SetFocus(Owner, Chat, foreign.Id, mine.Revision);

        result.Status.Should().Be(ImageThreadWriteStatus.ThreadNotFound);
        _store.Get(Owner, Chat).Revision.Should().Be(mine.Revision);
    }

    [Fact]
    public void Тот_же_фокус_повторно_без_записи()
    {
        var (state, thread) = _store.Create(Owner, Chat, "a.png", null, focus: true);

        var again = _store.SetFocus(Owner, Chat, thread.Id, state.Revision);

        again.Status.Should().Be(ImageThreadWriteStatus.Ok);
        again.State.Revision.Should().Be(state.Revision, "ничего не поменялось — ревизия не растёт");
    }

    [Fact]
    public void Идентификатор_с_обходом_пути_отвергается()
    {
        var act = () => _store.Get(Owner, "../x");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Удаление_чата_на_шине_сносит_нити()
    {
        var bus = await StartedLifecycle();
        _store.Create(Owner, Chat, "a.png", null, focus: true);

        await bus.PublishAsync(new SessionDeleted(new TurnContext(Chat, Owner, 0, 0, "p1")));

        File.Exists(_store.StatePath(Owner, Chat)).Should().BeFalse();
        _store.Get(Owner, Chat).Threads.Should().BeEmpty();
    }

    [Fact]
    public async Task Ветвление_на_шине_копирует_нити_с_теми_же_id()
    {
        var bus = await StartedLifecycle();
        var (source, thread) = _store.Create(Owner, Chat, "a.png", null, focus: true);

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));

        var branch = _store.Get(Owner, "branch-1");
        branch.Should().BeEquivalentTo(source, "якоря в скопированной истории ветки ссылаются на те же threadId и stackId");
        branch.Threads.Single().Id.Should().Be(thread.Id);
        _store.Get(Owner, Chat).Should().BeEquivalentTo(source, "источник ветвление не меняет");
    }

    [Fact]
    public async Task Ветвление_чата_без_нитей_ничего_не_создаёт()
    {
        var bus = await StartedLifecycle();

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));

        File.Exists(_store.StatePath(Owner, "branch-1")).Should().BeFalse();
    }

    private async Task<TurnEventBus> StartedLifecycle()
    {
        var bus = new TurnEventBus();
        var lifecycle = new ImageThreadLifecycle(_store, NullLogger<ImageThreadLifecycle>.Instance, bus);
        await lifecycle.StartAsync(CancellationToken.None);
        // Повторный старт хоста не удваивает подписчиков
        await lifecycle.StartAsync(CancellationToken.None);
        return bus;
    }
}
