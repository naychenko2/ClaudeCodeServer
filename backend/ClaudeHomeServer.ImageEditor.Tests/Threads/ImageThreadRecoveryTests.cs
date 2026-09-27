using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Нить с задачей, оборванной перезапуском сервера: реестр задач живёт в памяти, и после рестарта
// PendingJobId указывает в никуда — карточка висела в «Рисуем 0 вариантов…». Сверка при старте
// снимает такую задачу с нити, помечает её прерванной, пишет журнал и рассылает событие; задачу,
// которую реестр знает, не трогает.
public class ImageThreadRecoveryTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";
    private const string ProjectId = "p1";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "ccs_thread_recovery_" + Guid.NewGuid().ToString("N"));
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly List<ImageEditJobService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // Новый процесс над тем же data: свежее хранилище, пустой реестр задач
    private (ImageThreadStore Store, ImageThreadService Threads, ImageEditJobService Jobs) Instance()
    {
        var store = new ImageThreadStore(Path.Combine(_data, ImageThreadStore.DirName));
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = ProjectId });
        var threads = new ImageThreadService(store, NullLogger<ImageThreadService>.Instance, directory.Object, null, _broadcaster);
        var jobs = new ImageEditJobService([new HangingEditor()], new ImageEditWorkspace(Path.Combine(_data, "image-editor")),
            NullLogger<ImageEditJobService>.Instance);
        _services.Add(jobs);
        return (store, threads, jobs);
    }

    private static async Task<string> StartLiveJob(ImageEditJobService jobs)
    {
        var quote = await jobs.QuoteAsync(Owner, ProjectId,
            new ImageEditQuoteRequest("hang", "auto", EditMode.Fast, ImageEditOp.Edit, 1, false, 0, false, null, null), default);
        var started = await jobs.StartAsync(Owner, ProjectId, new ImageEditJobInput(quote.Value!.QuoteId, "убрать провод", null,
            new ImageBytes(TestImages.Png(4, 4), "image/png"), null, null, [], "images/hero.png"), default);
        return started.Value!.JobId;
    }

    [Fact]
    public async Task После_перезапуска_неизвестная_задача_снимается_с_нити_с_пометкой_и_событием()
    {
        var before = Instance();
        var dead = before.Store.Create(Owner, Chat, "images/hero.png", null, focus: true).Thread;
        before.Store.SetPending(Owner, Chat, dead.Id, "job-before-restart");

        var after = Instance();
        var liveJob = await StartLiveJob(after.Jobs);
        var live = after.Store.Create(Owner, Chat, "images/logo.png", null, focus: false).Thread;
        after.Store.SetPending(Owner, Chat, live.Id, liveJob);

        var recovery = new ImageThreadRecovery(after.Threads, NullLogger<ImageThreadRecovery>.Instance, after.Jobs);
        await recovery.StartAsync(default);

        var state = after.Store.Get(Owner, Chat);
        var t = state.Threads.Single(x => x.Id == dead.Id);
        t.PendingJobId.Should().BeNull("задачи нет в реестре — вариантов не будет, «Рисуем…» висело бы вечно");
        t.InterruptedJobId.Should().Be("job-before-restart", "карточка показывает «прервано» и даёт запустить заново");
        state.Threads.Single(x => x.Id == live.Id).PendingJobId.Should().Be(liveJob, "живую задачу сверка не трогает");
        state.Events.Should().ContainSingle(e => e.Kind == ImageThreadEventKinds.Interrupted)
            .Which.Should().Match<ImageThreadEvent>(e => e.JobId == "job-before-restart" && e.ThreadId == dead.Id
                && e.Text.StartsWith(ImageThreadService.InterruptedText));

        var changed = _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageThreadChangedMessage>().Should().ContainSingle().Subject;
        changed.SessionId.Should().Be(Chat);
        changed.Revision.Should().Be(state.Revision);

        // Следующий запуск снимает пометку, повторная сверка ничего не пишет
        after.Store.SetPending(Owner, Chat, dead.Id, liveJob).State.Threads.Single(x => x.Id == dead.Id)
            .InterruptedJobId.Should().BeNull();
        var revision = after.Store.Get(Owner, Chat).Revision;
        (await after.Threads.RecoverInterruptedAsync(after.Jobs, default)).Should().Be(0);
        after.Store.Get(Owner, Chat).Revision.Should().Be(revision);
    }

    [Fact]
    public void Не_брать_снимает_пометку_прерванной_задачи()
    {
        var (store, threads, _) = Instance();
        var thread = store.Create(Owner, Chat, "images/hero.png", null, focus: true).Thread;
        store.SetPending(Owner, Chat, thread.Id, "gone");
        store.DropDeadPending(Owner, Chat, _ => false, (t, id) => new ImageThreadEvent(store.Now(), ImageThreadEventKinds.Interrupted, "x", t.Id, id));

        var dismissed = store.Dismiss(Owner, Chat, thread.Id, "gone", store.Get(Owner, Chat).Revision);

        dismissed.Thread!.InterruptedJobId.Should().BeNull();
        threads.Get(Owner, Chat).Threads.Single().PendingJobId.Should().BeNull();
    }

    // Поставщик, который держит задачу до отмены: реестр знает её, пока идёт тест
    private sealed class HangingEditor : IImageEditor
    {
        private static readonly ImageEditModelInfo Model = new("m", "M",
            new ImageEditCaps([ImageEditOp.Edit], MaskSupport.AsReference, 0, 1, false));

        public string Key => "hang";
        public string Label => "Hang";
        public string PriceUnit => ImageEditPriceUnits.Usd;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public async Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException(ct);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }
}
