using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Жизнь нити картинки (ADR-019, волна 2): взять картинку в работу без дублей, шаги и откат со
// старой стопкой, «Не брать», удаление пустой нити, переход на сохранённый файл, переименование,
// удержание шагов живых нитей в чистке рабочей папки, ветвление и удаление чата с шагами.
public class ImageThreadFlowTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "ccs_thread_flow_" + Guid.NewGuid().ToString("N"));
    private readonly ImageThreadStore _store;

    public ImageThreadFlowTests()
    {
        _store = new ImageThreadStore(Path.Combine(_data, ImageThreadStore.DirName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ImageThread Thread(string id, string chat = Chat) => _store.Get(Owner, chat).Threads.Single(t => t.Id == id);

    private ImageThreadWrite Add(string threadId, string step, string? pending = null) =>
        _store.AddStep(Owner, Chat, threadId, step, _store.Get(Owner, Chat).Revision, pending);

    private (ImageThread Thread, long Revision) Opened(string file = "images/hero.png")
    {
        var w = _store.Open(Owner, Chat, file, null, _store.Get(Owner, Chat).Revision);
        return (w.Thread!, w.State.Revision);
    }

    [Fact]
    public void Взять_в_работу_тот_же_файл_второй_раз_не_плодит_нить()
    {
        var first = _store.Open(Owner, Chat, "images/hero.png", null, 0);
        _store.SetFocus(Owner, Chat, null, first.State.Revision);

        var again = _store.Open(Owner, Chat, "images/hero.png", null, _store.Get(Owner, Chat).Revision);

        again.Existing.Should().BeTrue();
        again.Thread!.Id.Should().Be(first.Thread!.Id);
        again.State.Threads.Should().ContainSingle();
        again.State.Focus.Should().Be(first.Thread.Id, "повторный выбор той же картинки берёт её в работу");
    }

    [Fact]
    public void Черновики_каждый_раз_новые_и_старая_ревизия_конфликт()
    {
        var a = _store.Open(Owner, Chat, null, "", 0);
        var b = _store.Open(Owner, Chat, null, "", a.State.Revision);
        var stale = _store.Open(Owner, Chat, null, "", a.State.Revision);

        b.Thread!.Id.Should().NotBe(a.Thread!.Id);
        stale.Status.Should().Be(ImageThreadWriteStatus.Conflict);
        _store.Get(Owner, Chat).Threads.Should().HaveCount(2);
    }

    [Fact]
    public void Шаги_дописываются_в_текущую_стопку_и_разбирают_ожидающую_задачу()
    {
        var (thread, _) = Opened();
        _store.SetPending(Owner, Chat, thread.Id, "job-1").State.Threads.Single().PendingJobId.Should().Be("job-1");

        Add(thread.Id, "s1", pending: "job-1");
        var second = Add(thread.Id, "s2");

        second.Forked.Should().BeNull();
        var t = Thread(thread.Id);
        t.Stacks.Should().ContainSingle().Which.Steps.Should().Equal("s1", "s2");
        t.CurrentStepId.Should().Be("s2");
        t.PendingJobId.Should().BeNull("варианты задачи разобраны «Взять»");
    }

    [Fact]
    public void Откат_и_новая_правка_замораживают_старую_стопку_а_новая_начинается_с_общих_шагов()
    {
        var (thread, _) = Opened();
        foreach (var s in new[] { "s1", "s2", "s3", "s4" }) Add(thread.Id, s);
        var oldStack = Thread(thread.Id).CurrentStackId;

        var rolled = _store.Rollback(Owner, Chat, thread.Id, "s2", _store.Get(Owner, Chat).Revision);
        rolled.Status.Should().Be(ImageThreadWriteStatus.Ok);
        rolled.State.Threads.Single().Stacks.Should().ContainSingle("откат сам стопки не трогает");

        var forked = Add(thread.Id, "s5");

        forked.Forked.Should().NotBeNull();
        forked.Forked!.KeptFrom.Should().Be(3);
        forked.Forked.KeptTo.Should().Be(4);
        ImageThreadService.ForkText(forked.Forked).Should().Be("Шаги 3–4 не пропали — они в старой стопке выше");
        var t = Thread(thread.Id);
        t.Stacks.Should().HaveCount(2);
        var frozen = t.Stacks.Single(s => s.StackId == oldStack);
        frozen.Old.Should().BeTrue();
        frozen.Steps.Should().Equal(["s1", "s2", "s3", "s4"], "старая стопка остаётся как была — её якорь на прежнем месте");
        t.CurrentStack!.Steps.Should().Equal("s1", "s2", "s5");
        t.CurrentStack.ForkedFromStepId.Should().Be("s2");
        t.CurrentStack.Old.Should().BeFalse();
        t.CurrentStepId.Should().Be("s5");
    }

    [Fact]
    public void Откат_к_исходнику_и_правка_уводят_все_шаги_в_старую_стопку()
    {
        var (thread, _) = Opened();
        Add(thread.Id, "s1");
        _store.Rollback(Owner, Chat, thread.Id, null, _store.Get(Owner, Chat).Revision);

        var forked = Add(thread.Id, "s2");

        ImageThreadService.ForkText(forked.Forked!).Should().Be("Шаг 1 не пропал — он в старой стопке выше");
        Thread(thread.Id).CurrentStack!.Steps.Should().Equal("s2");
        Thread(thread.Id).CurrentStack!.ForkedFromStepId.Should().BeNull();
    }

    [Fact]
    public void Откат_на_последний_шаг_не_форкает()
    {
        var (thread, _) = Opened();
        Add(thread.Id, "s1");
        Add(thread.Id, "s2");
        _store.Rollback(Owner, Chat, thread.Id, "s2", _store.Get(Owner, Chat).Revision);

        Add(thread.Id, "s3").Forked.Should().BeNull();
        Thread(thread.Id).Stacks.Should().ContainSingle();
    }

    [Fact]
    public void Откат_на_чужой_шаг_отказ()
    {
        var (thread, revision) = Opened();

        var result = _store.Rollback(Owner, Chat, thread.Id, "чужой", revision);

        result.Status.Should().Be(ImageThreadWriteStatus.StepNotFound);
        _store.Get(Owner, Chat).Revision.Should().Be(revision);
    }

    [Fact]
    public void Шаг_со_старой_ревизией_не_ложится()
    {
        var (thread, revision) = Opened();
        _store.SetFocus(Owner, Chat, null, revision);

        var stale = _store.AddStep(Owner, Chat, thread.Id, "s1", revision);

        stale.Status.Should().Be(ImageThreadWriteStatus.Conflict);
        Thread(thread.Id).HasSteps.Should().BeFalse();
    }

    [Fact]
    public void Не_брать_снимает_только_свою_ожидающую_задачу()
    {
        var (thread, _) = Opened();
        _store.SetPending(Owner, Chat, thread.Id, "job-2");

        _store.Dismiss(Owner, Chat, thread.Id, "job-1", _store.Get(Owner, Chat).Revision);
        Thread(thread.Id).PendingJobId.Should().Be("job-2", "задача уже другая");

        _store.Dismiss(Owner, Chat, thread.Id, "job-2", _store.Get(Owner, Chat).Revision);
        Thread(thread.Id).PendingJobId.Should().BeNull();
    }

    [Fact]
    public void Убрать_можно_только_нить_без_шагов_и_без_ожидающих_вариантов()
    {
        var (empty, _) = Opened("a.png");
        var (withSteps, _) = Opened("b.png");
        Add(withSteps.Id, "s1");
        var (pending, _) = Opened("c.png");
        _store.SetPending(Owner, Chat, pending.Id, "job-1");

        _store.Remove(Owner, Chat, withSteps.Id, _store.Get(Owner, Chat).Revision).Status.Should().Be(ImageThreadWriteStatus.Invalid);
        _store.Remove(Owner, Chat, pending.Id, _store.Get(Owner, Chat).Revision).Status.Should().Be(ImageThreadWriteStatus.Invalid);
        _store.SetFocus(Owner, Chat, empty.Id, _store.Get(Owner, Chat).Revision);
        var removed = _store.Remove(Owner, Chat, empty.Id, _store.Get(Owner, Chat).Revision);

        removed.Status.Should().Be(ImageThreadWriteStatus.Ok);
        removed.State.Threads.Select(t => t.Id).Should().NotContain(empty.Id);
        removed.State.Focus.Should().BeNull("фокус уходит вместе с нитью");
    }

    [Fact]
    public void Сохранение_уводит_нить_на_новый_файл_а_прежний_в_lineage()
    {
        var (thread, _) = Opened("images/hero.png");
        var draft = _store.Open(Owner, Chat, null, "art", _store.Get(Owner, Chat).Revision).Thread!;

        _store.MoveToFile(Owner, Chat, thread.Id, "images/hero.v2.png");
        _store.MoveToFile(Owner, Chat, draft.Id, "art/new.png");

        Thread(thread.Id).File.Should().Be("images/hero.v2.png");
        Thread(thread.Id).Lineage.Should().Equal("images/hero.png");
        Thread(draft.Id).File.Should().Be("art/new.png");
        Thread(draft.Id).DraftFolder.Should().BeNull();
        Thread(draft.Id).Lineage.Should().BeEmpty();
    }

    [Fact]
    public void Переименование_папки_переписывает_файл_и_lineage()
    {
        var (thread, _) = Opened("images/hero.png");
        _store.MoveToFile(Owner, Chat, thread.Id, "images/hero.v2.png");

        _store.RewritePaths(Owner, Chat, p => p.StartsWith("images/") ? "pics/" + p["images/".Length..] : p).Should().BeTrue();
        _store.RewritePaths(Owner, Chat, p => p).Should().BeFalse("ничего не поменялось — записи нет");

        Thread(thread.Id).File.Should().Be("pics/hero.v2.png");
        Thread(thread.Id).Lineage.Should().Equal("pics/hero.png");
    }

    [Fact]
    public void Журнал_уходит_в_блок_хода_один_раз()
    {
        var (thread, _) = Opened();
        _store.SetPending(Owner, Chat, thread.Id, "job-1",
            new ImageThreadEvent(_store.Now(), ImageThreadEventKinds.Launched, "Человек запустил вручную", thread.Id, "job-1"));

        var (_, first) = _store.TakeForTurn(Owner, Chat);
        var (_, second) = _store.TakeForTurn(Owner, Chat);

        first.Should().ContainSingle().Which.Text.Should().Be("Человек запустил вручную");
        second.Should().BeEmpty();
    }

    [Fact]
    public void Чистка_рабочей_папки_не_трогает_шаги_живых_нитей()
    {
        var workspace = new ImageEditWorkspace(Path.Combine(_data, "image-editor")) { RetainedSteps = _store.ReferencedSteps };
        var kept = NewStep(workspace, "edit-a");
        var lost = NewStep(workspace, "edit-a");
        var orphanEdit = NewStep(workspace, "edit-b");
        var (thread, _) = Opened();
        Add(thread.Id, kept.StepId);
        var editA = Path.Combine(workspace.Root, Owner, "edit-a");
        var editB = Path.Combine(workspace.Root, Owner, "edit-b");
        var old = DateTime.UtcNow - ImageEditWorkspace.Ttl - TimeSpan.FromDays(1);
        Directory.SetLastWriteTimeUtc(editA, old);
        Directory.SetLastWriteTimeUtc(editB, old);

        workspace.Sweep(DateTime.UtcNow);

        workspace.OpenStep(Owner, kept.StepId).Should().NotBeNull("шаг нити живёт столько же, сколько нить");
        workspace.OpenStep(Owner, lost.StepId).Should().BeNull("шаг без ссылки из нитей уходит по TTL");
        workspace.OpenStep(Owner, orphanEdit.StepId).Should().BeNull();
        Directory.Exists(editB).Should().BeFalse();
    }

    [Fact]
    public async Task Ветвление_копирует_шаги_стопки_и_текущий_шаг_а_удаление_источника_ветку_не_трогает()
    {
        var bus = new TurnEventBus();
        await new ImageThreadLifecycle(_store, NullLogger<ImageThreadLifecycle>.Instance, bus).StartAsync(CancellationToken.None);
        var (thread, _) = Opened();
        Add(thread.Id, "s1");
        Add(thread.Id, "s2");
        _store.Rollback(Owner, Chat, thread.Id, "s1", _store.Get(Owner, Chat).Revision);
        Add(thread.Id, "s3");
        var source = _store.Get(Owner, Chat);

        await bus.PublishAsync(new SessionBranched(new TurnContext("branch-1", Owner, 0, 0, "p1"), Chat));
        await bus.PublishAsync(new SessionDeleted(new TurnContext(Chat, Owner, 0, 0, "p1")));

        var branch = _store.Get(Owner, "branch-1");
        branch.Threads.Single().Stacks.Select(s => s.StackId).Should().Equal(source.Threads.Single().Stacks.Select(s => s.StackId),
            "якоря стопок в скопированной истории ветки ссылаются на те же stackId");
        branch.Threads.Single().CurrentStepId.Should().Be("s3");
        branch.Threads.Single().Stacks.Should().Contain(s => s.Old);
        _store.Get(Owner, Chat).Threads.Should().BeEmpty("нити удалённого чата снесены");
        _store.ReferencedSteps(Owner).Should().Contain(["s1", "s2", "s3"], "шаги ветки удерживаются после удаления источника");
    }

    [Fact]
    public void Нити_другого_владельца_не_удерживают_и_не_видны()
    {
        var (thread, _) = Opened();
        Add(thread.Id, "s1");

        _store.ReferencedSteps("owner-2").Should().BeEmpty();
        _store.Get("owner-2", Chat).Threads.Should().BeEmpty();
    }

    private static ImageEditStep NewStep(ImageEditWorkspace workspace, string editId)
    {
        var step = new ImageEditStep(ImageEditWorkspace.NewStepId(), editId, "p1", null, ImageEditStepKinds.Transform,
            1, 1, 4, DateTime.UtcNow);
        workspace.SaveStep(Owner, step, [0x89, 0x50, 0x4E, 0x47]);
        return step;
    }
}
