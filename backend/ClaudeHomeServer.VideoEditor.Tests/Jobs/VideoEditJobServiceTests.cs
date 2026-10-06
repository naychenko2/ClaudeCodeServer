using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Jobs;

public sealed class VideoEditJobServiceTests : IDisposable
{
    private const string Owner = "u1";
    private const string Session = "s1";
    private static readonly VideoEditScope Scope = new("proj1", null);
    private static readonly VideoEditScope Personal = new(VideoEditScope.Personal, null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vjob-" + Guid.NewGuid().ToString("N"));
    private readonly VideoThreadStore _store;
    private readonly Spend _spend = new();
    private readonly Events _events = new();

    public VideoEditJobServiceTests() => _store = new VideoThreadStore(Path.Combine(_root, "threads"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Spend : ISpendCollector
    {
        public List<SpendRecord> Records { get; } = [];
        public void Record(SpendRecord record) => Records.Add(record);
    }

    private sealed class Events : ISessionBroadcaster
    {
        public List<ServerMessage> Sent { get; } = [];

        public Task ToOwner(string ownerId, ServerMessage message)
        {
            lock (Sent) Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    private sealed class Flags(bool preferLocal) : IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => preferLocal && key == FeatureFlagKeys.LocalMediaDefault;
    }

    private sealed class Fake(string key, string unit, Func<VideoRequest, IProgress<VideoProgress>, CancellationToken, Task<VideoResult>> run,
        double perSec = 0.1, string? refusal = null, bool enabled = true) : IVideoEngine
    {
        public string Key => key;
        public string Label => key;
        public string PriceUnit => unit;
        public bool Enabled => enabled;
        public int Runs;
        public IReadOnlyList<VideoModelInfo> Models { get; } =
        [
            new VideoModelInfo(key + "-model", key + " модель",
                new VideoCaps([5, 8], ["16:9"], false, false, VideoLicense.Commercial, unit, FirstFrame: false),
                new VideoPriceHint(perSec, unit, "sec")),
        ];

        public string? ScopeRefusal(VideoEditScope scope) => refusal;

        public Task<VideoResult> RunAsync(VideoRequest req, IProgress<VideoProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return run(req, progress, ct);
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(true);
    }

    private static Task<VideoResult> Ok(VideoRequest r, IProgress<VideoProgress> p, CancellationToken ct)
    {
        p.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r1", Accepted: true));
        return Task.FromResult(new VideoResult(VideoOutcome.Ok, new VideoFile([1, 2, 3], "video/mp4", ".mp4", r.DurationSec, true),
            null, true, "r1", null));
    }

    private VideoEditJobService Service(IEnumerable<IVideoEngine> engines, bool preferLocal = false)
    {
        var workspace = new VideoEditWorkspace(Path.Combine(_root, "ws"));
        var threads = new VideoJobThreads(_store, NullLogger<VideoJobThreads>.Instance, broadcaster: _events);
        return new VideoEditJobService(engines, workspace, threads, NullLogger<VideoEditJobService>.Instance,
            spend: _spend, broadcaster: _events, flags: new Flags(preferLocal));
    }

    private string AddScene(string text = "закат над морем")
    {
        var settings = new VideoSceneSettingsDto(null, null, text, null, null, null, null, null, null);
        return _store.AddScene(Owner, Session, "", settings, null).Scene!.SceneId;
    }

    private static VideoQuoteRequest QuoteOf(string scene, string? provider = null, int? count = null, int? duration = null) =>
        new(Session, scene, provider, null, count, duration, null, null);

    private async Task<(VideoEditJobService Svc, string JobId)> RunToEnd(VideoEditJobService svc, VideoEditScope scope,
        string scene, string provider, int count = 1, string? initiator = null)
    {
        var quote = (await svc.QuoteAsync(Owner, scope, QuoteOf(scene, provider, count, 5), default)).Value!;
        var started = await svc.StartAsync(Owner, scope, new VideoLaunchRequest(quote.QuoteId, Session, scene, initiator), default);
        started.Error.Should().BeNull();
        await svc.WhenDone(started.Value!.JobId);
        return (svc, started.Value.JobId);
    }

    [Fact]
    public async Task Котировка_считается_в_валюте_поставщика_за_все_варианты()
    {
        var svc = Service([new Fake("fal", "usd", Ok, perSec: 0.1)]);
        var scene = AddScene();

        var quote = (await svc.QuoteAsync(Owner, Scope, QuoteOf(scene, "fal", count: 2, duration: 8), default)).Value!;

        quote.Price.Unit.Should().Be("usd");
        quote.Price.Amount.Should().BeApproximately(0.1 * 8 * 2, 1e-9);
        quote.Count.Should().Be(2);
        quote.License.Should().Be(VideoLicense.Commercial.Label);
    }

    [Fact]
    public async Task Принятая_задача_пишет_трату_на_владельца_с_инициатором_и_даёт_версию()
    {
        var svc = Service([new Fake("fal", "usd", Ok)]);
        var scene = AddScene();

        var (_, jobId) = await RunToEnd(svc, Scope, scene, "fal", count: 2, initiator: VideoInitiators.Agent);

        _spend.Records.Should().ContainSingle();
        var spend = _spend.Records[0];
        spend.OwnerId.Should().Be(Owner);
        spend.ProjectId.Should().Be("proj1");
        spend.Initiator.Should().Be(SpendInitiators.Agent);
        spend.CostUsd.Should().BeApproximately(0.1 * 5 * 2, 1e-9);
        spend.CostCredits.Should().BeNull();
        spend.Generations.Should().Be(2);
        spend.Provider.Should().Be("fal");
        var state = _store.Get(Owner, Session).Scenes.Single();
        state.Versions.Should().HaveCount(2);
        state.Versions[0].Cost!.Currency.Should().Be("usd");
        state.Versions[0].Initiator.Should().Be(VideoInitiators.Agent);
        state.Launches.Single().Status.Should().Be(VideoLaunchStatus.Done);
        svc.Get(Owner, Scope.Key, jobId)!.Status.Should().Be("completed");
        _events.Sent.Should().Contain(m => m is VideoEditCompletedMessage);
    }

    [Fact]
    public async Task Процент_съёмки_доходит_до_сообщения_прогресса_а_без_данных_пуст()
    {
        var svc = Service([new Fake("fal", "usd", (r, p, ct) =>
        {
            p.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r1", Accepted: true));
            p.Report(new VideoProgress(VideoStage.Running, RemoteId: "r1", Accepted: true, Percent: 0.42));
            return Ok(r, p, ct);
        })]);

        await RunToEnd(svc, Scope, AddScene(), "fal");

        var progress = _events.Sent.OfType<VideoEditProgressMessage>().ToList();
        progress.Single(m => m.Stage == "running").Percent.Should().Be(0.42);
        progress.Where(m => m.Stage == "queued").Should().OnlyContain(m => m.Percent == null);
    }

    [Fact]
    public async Task Кредиты_Higgsfield_идут_в_свою_валюту()
    {
        var svc = Service([new Fake("higgsfield", "credits", Ok, perSec: 2)]);

        await RunToEnd(svc, Scope, AddScene(), "higgsfield");

        _spend.Records.Should().ContainSingle();
        _spend.Records[0].CostCredits.Should().Be(10);
        _spend.Records[0].CostUsd.Should().BeNull();
        _store.Get(Owner, Session).Scenes.Single().Versions[0].Cost!.Currency.Should().Be("credits");
    }

    [Fact]
    public async Task Local_пишет_трату_с_нулём_и_валюту_local()
    {
        var svc = Service([new Fake("local", "free", Ok, perSec: 0)]);

        await RunToEnd(svc, Scope, AddScene(), "local");

        _spend.Records.Should().ContainSingle();
        _spend.Records[0].CostUsd.Should().Be(0);
        _store.Get(Owner, Session).Scenes.Single().Versions[0].Cost!.Currency.Should().Be("local");
    }

    [Fact]
    public async Task Отказ_до_принятия_не_списывает_и_предлагает_соседа_без_запуска()
    {
        var fal = new Fake("fal", "usd", (_, _, _) => Task.FromResult(VideoResult.Fail(VideoOutcome.Unavailable, "лежит")));
        var neighbor = new Fake("higgsfield", "credits", Ok, perSec: 2);
        var svc = Service([fal, neighbor]);
        var scene = AddScene();

        await RunToEnd(svc, Scope, scene, "fal");

        _spend.Records.Should().BeEmpty("поставщик не принял задачу — деньги не списываются");
        neighbor.Runs.Should().Be(0, "на соседа сервер не переходит никогда");
        var failed = _events.Sent.OfType<VideoEditFailedMessage>().Single();
        failed.Charged.Should().BeFalse();
        failed.RetryQuote.Should().NotBeNull();
        failed.RetryQuote!.Provider.Should().Be("higgsfield");
        failed.RetryQuote.Quote.Price.Unit.Should().Be("credits");
        _store.Get(Owner, Session).Scenes.Single().Launches.Single().Status.Should().Be(VideoLaunchStatus.Failed);
    }

    [Fact]
    public async Task Сбой_после_принятия_с_честным_не_списано_даёт_компенсацию_минусом()
    {
        var fal = new Fake("fal", "usd", (_, p, _) =>
        {
            p.Report(new VideoProgress(VideoStage.Queued, RemoteId: "r1", Accepted: true));
            return Task.FromResult(VideoResult.Fail(VideoOutcome.Failed, "модель упала", charged: false));
        });
        var svc = Service([fal]);

        await RunToEnd(svc, Scope, AddScene(), "fal");

        _spend.Records.Should().HaveCount(2);
        _spend.Records[0].CostUsd.Should().BeGreaterThan(0);
        _spend.Records[1].CostUsd.Should().Be(-_spend.Records[0].CostUsd);
        _spend.Records[1].Generations.Should().Be(-1);
    }

    [Fact]
    public async Task Отмена_до_принятия_ничего_не_списывает_и_ничего_не_предлагает()
    {
        var started = new TaskCompletionSource();
        var fal = new Fake("fal", "usd", async (_, p, ct) =>
        {
            // Очередь локальной карты: отчёт есть, принятия поставщиком ещё нет
            p.Report(new VideoProgress(VideoStage.Queued, QueuePosition: 2));
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });
        var svc = Service([fal]);
        var scene = AddScene();
        var quote = (await svc.QuoteAsync(Owner, Scope, QuoteOf(scene, "fal", 1, 5), default)).Value!;
        var job = (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default)).Value!;
        await started.Task;

        var cancelled = await svc.CancelAsync(Owner, Scope.Key, job.JobId, default);

        cancelled!.Status.Should().Be("cancelled");
        _spend.Records.Should().BeEmpty();
        _store.Get(Owner, Session).Scenes.Single().Launches.Single().Status.Should().Be(VideoLaunchStatus.Cancelled);
    }

    [Fact]
    public async Task Явный_поставщик_не_подменяется_а_недоступный_отказ()
    {
        var svc = Service([new Fake("fal", "usd", Ok, enabled: false), new Fake("higgsfield", "credits", Ok)]);

        var result = await svc.QuoteAsync(Owner, Scope, QuoteOf(AddScene(), "fal"), default);

        result.ErrorCode.Should().Be(VideoEditorErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task Авто_ставит_local_первым_только_по_флагу_владельца()
    {
        Fake[] Engines() => [new Fake("fal", "usd", Ok), new Fake("local", "free", Ok, perSec: 0)];
        var scene = AddScene();

        var plain = (await Service(Engines()).QuoteAsync(Owner, Scope, QuoteOf(scene), default)).Value!;
        var preferring = (await Service(Engines(), preferLocal: true).QuoteAsync(Owner, Scope, QuoteOf(scene), default)).Value!;

        plain.Provider.Should().Be("fal");
        preferring.Provider.Should().Be("local");
    }

    [Fact]
    public async Task Local_в_личном_чате_закрыт_кодом_local_unavailable_personal()
    {
        var svc = Service([new Fake("local", "free", Ok, refusal: "Локальные модели — только в чате проекта")]);
        var scene = AddScene();

        var result = await svc.QuoteAsync(Owner, Personal, QuoteOf(scene, "local"), default);

        result.ErrorCode.Should().Be(VideoEditorErrors.LocalUnavailablePersonal);
    }

    [Fact]
    public async Task Запуск_только_по_котировке_своей_сцены_и_один_раз()
    {
        var svc = Service([new Fake("fal", "usd", Ok)]);
        var scene = AddScene();
        var other = AddScene("вторая");
        var quote = (await svc.QuoteAsync(Owner, Scope, QuoteOf(scene, "fal", 1, 5), default)).Value!;

        (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest("нет-такой", Session, scene), default))
            .ErrorCode.Should().Be(VideoEditorErrors.QuoteNotFound);
        (await svc.StartAsync("чужой", Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default))
            .ErrorCode.Should().Be(VideoEditorErrors.QuoteNotFound);
        (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, other), default))
            .ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest, "котировка выписана на другую сцену");

        var first = await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default);
        first.Value.Should().NotBeNull();
        await svc.WhenDone(first.Value!.JobId);
        (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(quote.QuoteId, Session, scene), default))
            .ErrorCode.Should().Be(VideoEditorErrors.QuoteNotFound, "котировка одноразовая");
    }

    [Fact]
    public async Task Неизвестный_параметр_отказ_с_именем_поля_до_запуска()
    {
        var fal = new Fake("fal", "usd", Ok);
        var svc = Service([fal]);
        var scene = AddScene();
        var quote = (await svc.QuoteAsync(Owner, Scope, QuoteOf(scene, "fal", 1, 5), default)).Value!;

        var result = await svc.StartAsync(Owner, Scope,
            new VideoLaunchRequest(quote.QuoteId, Session, scene, Params: new() { ["cfg"] = 1 }), default);

        result.ErrorCode.Should().Be(VideoEditorErrors.InvalidRequest);
        result.Error.Should().Contain("cfg");
        fal.Runs.Should().Be(0);
    }

    [Fact]
    public async Task Третья_съёмка_владельца_отказ_по_потолку()
    {
        var gate = new TaskCompletionSource();
        var fal = new Fake("fal", "usd", async (_, _, _) =>
        {
            await gate.Task;
            return VideoResult.Fail(VideoOutcome.Failed, "x");
        });
        var svc = Service([fal]);
        var scene = AddScene();

        var started = new List<string>();
        for (var i = 0; i < VideoEditJobService.MaxJobsPerOwner; i++)
        {
            var q = (await svc.QuoteAsync(Owner, Scope, QuoteOf(scene, "fal", 1, 5), default)).Value!;
            var job = (await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(q.QuoteId, Session, scene), default)).Value;
            job.Should().NotBeNull();
            started.Add(job!.JobId);
        }
        var third = (await svc.QuoteAsync(Owner, Scope, QuoteOf(scene, "fal", 1, 5), default)).Value!;

        var refused = await svc.StartAsync(Owner, Scope, new VideoLaunchRequest(third.QuoteId, Session, scene), default);
        gate.SetResult();
        foreach (var id in started) await svc.WhenDone(id);

        refused.ErrorCode.Should().Be(VideoEditorErrors.TooManyJobs);
    }
}
