using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Mcp;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.ImageEditor.Threads;

// Версии картинки в ленте (изменение 27.09 к ADR-019): каждый вариант каждого запуска ИИ — версия
// нити, «Взять» нет; правка без ИИ — шаг текущей версии, а не новая версия; «продолжить от
// версии» ничего не удаляет, и следующий запуск растёт от неё. Старая нить со стопками читается с
// исходником, чья картинка — её текущий шаг стопки. Сквозные проверки идут через настоящий
// исполнитель задач: варианты становятся версиями по его событию завершения.
public class ImageThreadVersionsTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-1";
    private const string ProjectId = "p1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs_thread_versions_" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ImageThreadStore _store;
    private readonly RecordingFeed _feed = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly MemorySpendStore _spend = new();
    private readonly List<ImageEditJobService> _services = [];

    public ImageThreadVersionsTests()
    {
        _root = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        File.WriteAllBytes(Path.Combine(_root, "images", "hero.png"), Png(8, 6, SKColors.Red));
        _store = new ImageThreadStore(Path.Combine(_dir, ImageThreadStore.DirName));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private ImageThread Thread(string id) => _store.Get(Owner, Chat).Threads.Single(t => t.Id == id);

    private string Opened(string file = "images/hero.png") =>
        _store.Open(Owner, Chat, file, null, _store.Get(Owner, Chat).Revision).Thread!.Id;

    private long Revision => _store.Get(Owner, Chat).Revision;

    private ImageThreadLaunch Launch(string jobId, string? baseVersion, string? baseStep = null) =>
        new(jobId, baseVersion, baseStep, _store.Now(), ImageThreadLaunchStatus.Running, "human", "синий фон");

    // ── Хранилище ────────────────────────────────────────────────────────────

    [Fact]
    public void Новая_нить_без_стопок_с_исходником_в_работе_переживает_перечитывание()
    {
        var (_, thread) = _store.Create(Owner, Chat, "images/hero.png", null, focus: true);

        thread.Stacks.Should().BeEmpty("новая нить стопок не заводит");
        thread.Versions.Should().ContainSingle().Which.Should().Match<ImageThreadVersion>(v =>
            v.Id == ImageThreadVersion.OriginId && v.Number == 0 && v.IsOrigin);
        thread.CurrentVersionId.Should().Be(ImageThreadVersion.OriginId);

        var reread = new ImageThreadStore(_store.Root).Get(Owner, Chat).Threads.Single();
        reread.Versions.Should().BeEquivalentTo(thread.Versions);
        reread.CurrentVersionId.Should().Be(ImageThreadVersion.OriginId);
    }

    [Fact]
    public void Правка_без_ИИ_ложится_шагом_текущей_версии_и_новой_версии_не_заводит()
    {
        var id = Opened();

        _store.AddVersionStep(Owner, Chat, id, "s1", Revision).Status.Should().Be(ImageThreadWriteStatus.Ok);
        _store.AddVersionStep(Owner, Chat, id, "s2", Revision);

        var origin = Thread(id).Versions.Should().ContainSingle("правка без ИИ — не версия").Subject;
        origin.Steps.Should().Equal("s1", "s2");
        origin.CurrentStepId.Should().Be("s2");
        Thread(id).ImageStepOf(origin).Should().Be("s2");

        // После запуска правка ложится уже в версию от ИИ, которая стала текущей
        _store.AddLaunch(Owner, Chat, id, Launch("job-1", ImageThreadVersion.OriginId, "s2"));
        _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "v1")]);
        _store.AddVersionStep(Owner, Chat, id, "s3", Revision);

        var t = Thread(id);
        t.Versions.Should().HaveCount(2);
        t.CurrentVersion!.Steps.Should().Equal("v1", "s3");
        t.Version(ImageThreadVersion.OriginId)!.Steps.Should().Equal("s1", "s2");
    }

    [Fact]
    public void Завершение_запуска_на_N_вариантов_даёт_N_версий_от_основы_запуска()
    {
        var id = Opened();

        _store.AddLaunch(Owner, Chat, id, Launch("job-1", ImageThreadVersion.OriginId));
        var written = _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done,
            [(1, "a"), (2, "b"), (3, "c")]);

        written.NewVersions.Should().HaveCount(3);
        var t = Thread(id);
        var ai = t.Versions.Where(v => !v.IsOrigin).ToList();
        ai.Select(v => v.Number).Should().Equal(1, 2, 3);
        ai.Select(v => v.Variant).Should().Equal(1, 2, 3);
        ai.Should().OnlyContain(v => v.JobId == "job-1" && v.BaseVersionId == ImageThreadVersion.OriginId);
        ai.Select(v => v.CurrentStepId).Should().Equal("a", "b", "c");
        t.CurrentVersionId.Should().Be(ai[0].Id, "следующая правка — от первого нового варианта");
        t.Launches.Single().Status.Should().Be(ImageThreadLaunchStatus.Done);

        var again = _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "a")]);
        again.NewVersions.Should().BeEmpty("повтор завершения ничего не дописывает");
        Thread(id).Versions.Should().HaveCount(4);
    }

    [Fact]
    public void Продолжить_от_старой_версии_и_запуск_растят_новые_версии_от_неё_а_старые_не_тронуты()
    {
        var id = Opened();
        _store.AddLaunch(Owner, Chat, id, Launch("job-1", ImageThreadVersion.OriginId));
        _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "a"), (2, "b")]);
        var second = Thread(id).Versions.Single(v => v.Number == 2);
        var before = Thread(id).Versions;

        var continued = _store.SetCurrentVersion(Owner, Chat, id, second.Id, null, Revision, focus: true);
        continued.Status.Should().Be(ImageThreadWriteStatus.Ok);
        continued.State.Focus.Should().Be(id);
        _store.AddLaunch(Owner, Chat, id, Launch("job-2", second.Id, "b"));
        _store.FinishLaunch(Owner, Chat, id, "job-2", ImageThreadLaunchStatus.Done, [(1, "c")]);

        var t = Thread(id);
        t.Versions.Take(3).Should().BeEquivalentTo(before, "продолжение ничего не удаляет и не меняет");
        var third = t.Versions.Last();
        third.Number.Should().Be(3);
        third.BaseVersionId.Should().Be(second.Id);
        third.BaseStepId.Should().Be("b");
        t.CurrentVersionId.Should().Be(third.Id);
    }

    [Fact]
    public void Текущая_версия_не_уезжает_если_человек_сменил_её_пока_шла_генерация()
    {
        var id = Opened();
        _store.AddLaunch(Owner, Chat, id, Launch("job-1", ImageThreadVersion.OriginId));
        _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "a")]);
        var first = Thread(id).CurrentVersionId!;
        _store.AddLaunch(Owner, Chat, id, Launch("job-2", first, "a"));

        _store.SetCurrentVersion(Owner, Chat, id, ImageThreadVersion.OriginId, null, Revision);
        _store.FinishLaunch(Owner, Chat, id, "job-2", ImageThreadLaunchStatus.Done, [(1, "b")]);

        Thread(id).CurrentVersionId.Should().Be(ImageThreadVersion.OriginId, "человек выбрал, от чего править дальше");
    }

    [Fact]
    public void Чужая_версия_и_чужой_шаг_версии_отказ_без_записи()
    {
        var id = Opened();
        var revision = Revision;

        _store.SetCurrentVersion(Owner, Chat, id, "нет-такой", null, revision).Status.Should().Be(ImageThreadWriteStatus.VersionNotFound);
        _store.SetCurrentVersion(Owner, Chat, id, ImageThreadVersion.OriginId, "чужой", revision)
            .Status.Should().Be(ImageThreadWriteStatus.StepNotFound);
        _store.SetCurrentVersion(Owner, Chat, id, ImageThreadVersion.OriginId, null, revision - 1)
            .Status.Should().Be(ImageThreadWriteStatus.Conflict);
        Revision.Should().Be(revision);
    }

    [Fact]
    public void Нить_с_версией_или_идущим_запуском_не_убирается()
    {
        var running = Opened("a.png");
        _store.AddLaunch(Owner, Chat, running, Launch("job-1", ImageThreadVersion.OriginId));
        var done = Opened("b.png");
        _store.AddLaunch(Owner, Chat, done, Launch("job-2", ImageThreadVersion.OriginId));
        _store.FinishLaunch(Owner, Chat, done, "job-2", ImageThreadLaunchStatus.Done, [(1, "a")]);

        _store.Remove(Owner, Chat, running, Revision).Status.Should().Be(ImageThreadWriteStatus.Invalid);
        _store.Remove(Owner, Chat, done, Revision).Status.Should().Be(ImageThreadWriteStatus.Invalid);
    }

    [Fact]
    public void Шаги_версий_удерживаются_от_чистки_рабочей_папки()
    {
        var id = Opened();
        _store.AddVersionStep(Owner, Chat, id, "s1", Revision);
        _store.AddLaunch(Owner, Chat, id, Launch("job-1", ImageThreadVersion.OriginId, "s1"));
        _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "v1")]);

        _store.ReferencedSteps(Owner).Should().Contain(["s1", "v1"]);
    }

    [Fact]
    public void Старая_нить_со_стопкой_читается_с_исходником_на_её_текущем_шаге_и_стопка_цела()
    {
        var path = _store.StatePath(Owner, Chat);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Файл нитей до 27.09: версий нет, стопка с откатом на шаг s1 и ожидающая задача
        File.WriteAllText(path, """
            {"focus":"t-old","revision":7,"threads":[{"id":"t-old","file":"images/hero.png","lineage":[],
             "draftFolder":null,"stacks":[{"stackId":"st1","steps":["s1","s2"],"forkedFromStepId":null,"old":false}],
             "currentStackId":"st1","settings":{"provider":"fal","model":"m","count":2,"matchSourceSize":true},
             "pendingJobId":"job-old","createdAt":"2026-09-20T10:00:00Z","currentStepId":"s1"}],
             "events":[]}
            """);

        var t = _store.Get(Owner, Chat).Threads.Single();

        t.Stacks.Single().Steps.Should().Equal("s1", "s2");
        t.PendingJobId.Should().Be("job-old");
        t.Versions.Should().ContainSingle().Which.Id.Should().Be(ImageThreadVersion.OriginId);
        t.CurrentVersionId.Should().Be(ImageThreadVersion.OriginId);
        t.ImageStepOf(t.CurrentVersion!).Should().Be("s1", "правка старой нити идёт от шага, на котором стоит её стопка");

        // Старые ручки со стопкой работают, запись переписывает файл уже с версиями
        _store.AddStep(Owner, Chat, "t-old", "s3", 7, clearPendingJobId: "job-old").Status.Should().Be(ImageThreadWriteStatus.Ok);
        var json = JsonNode.Parse(File.ReadAllText(path))!["threads"]![0]!;
        json["versions"]!.AsArray().Should().ContainSingle();
        json["currentVersionId"]!.GetValue<string>().Should().Be(ImageThreadVersion.OriginId);
        var after = _store.Get(Owner, Chat).Threads.Single();
        after.PendingJobId.Should().BeNull();
        after.ImageStepOf(after.CurrentVersion!).Should().Be("s3");
    }

    [Fact]
    public async Task После_перезапуска_идущий_запуск_без_задачи_помечается_потерянным()
    {
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner, ProjectId = ProjectId });
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, null, _broadcaster);
        var id = Opened();
        _store.AddLaunch(Owner, Chat, id, Launch("job-lost", ImageThreadVersion.OriginId));
        var jobs = NewJobs();

        (await threads.RecoverInterruptedAsync(jobs, default)).Should().Be(1);

        var t = Thread(id);
        t.Launches.Single().Status.Should().Be(ImageThreadLaunchStatus.Interrupted);
        t.HasRunningLaunch.Should().BeFalse();
        _store.Get(Owner, Chat).Events.Should().ContainSingle(e => e.Kind == ImageThreadEventKinds.Interrupted && e.JobId == "job-lost");
        _store.FinishLaunch(Owner, Chat, id, "job-lost", ImageThreadLaunchStatus.Done, [(1, "a")]).NewVersions
            .Should().BeEmpty("потерянный запуск не оживает");
        ImageEditorStateContributor.RenderThreads(_store.Get(Owner, Chat), [], _ => null)
            .Should().Contain("потерян при перезапуске сервера");
    }

    [Fact]
    public void Блок_хода_показывает_версии_с_номерами_текущую_и_откуда_выросла()
    {
        var id = Opened();
        _store.AddLaunch(Owner, Chat, id, Launch("job-1", ImageThreadVersion.OriginId));
        _store.FinishLaunch(Owner, Chat, id, "job-1", ImageThreadLaunchStatus.Done, [(1, "a"), (2, "b")]);
        var second = Thread(id).Versions.Single(v => v.Number == 2);
        _store.SetCurrentVersion(Owner, Chat, id, second.Id, null, Revision);
        _store.AddLaunch(Owner, Chat, id, Launch("job-2", second.Id, "b"));

        var block = ImageEditorStateContributor.RenderThreads(_store.Get(Owner, Chat), [], _ => null);

        block.Should().Contain($"правка пойдёт от: версия 2");
        block.Should().Contain($"версия 2 [{second.Id}] (в работе) — вариант 2 запуска «синий фон», от: исходник");
        block.Should().Contain("версия 1 [").And.Contain("исходник [origin]");
        block.Should().Contain("рисуется: «синий фон», от: версия 2");
    }

    // ── Сквозь исполнитель задач и тулсет агента ─────────────────────────────

    private ImageEditJobService NewJobs(IImageEditor? editor = null)
    {
        var jobs = new ImageEditJobService([editor ?? new VariantsEditor()], new ImageEditWorkspace(Path.Combine(_dir, "image-editor")),
            NullLogger<ImageEditJobService>.Instance, _spend, _broadcaster);
        _services.Add(jobs);
        return jobs;
    }

    private (ImageEditorToolset Toolset, ImageThreadService Threads, ImageEditJobService Jobs, ImageEditSteps Steps) Agent()
    {
        var jobs = NewJobs();
        var raster = new SkiaImageRaster();
        var steps = new ImageEditSteps(raster, new ImageEditWorkspace(Path.Combine(_dir, "image-editor")), jobs);
        var session = new Session { Id = Chat, OwnerId = Owner, ProjectId = ProjectId };
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(session);
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetById(ProjectId)).Returns(new Project { Id = ProjectId, OwnerId = Owner, RootPath = _root });
        var threads = new ImageThreadService(_store, NullLogger<ImageThreadService>.Instance, directory.Object, _feed, _broadcaster, steps);
        threads.Watch(jobs);
        IImageEditor[] editors = [new VariantsEditor()];
        var launcher = new ImageEditLaunchAssembler(editors, jobs, raster, threads, steps);
        var accessor = new Mock<IMcpSessionAccessor>();
        accessor.Setup(a => a.GetOwned(Chat, Owner)).Returns(session);
        var flags = new Mock<IFeatureFlagGate>();
        flags.Setup(f => f.IsEnabled(Owner, FeatureFlagKeys.ImageEditor)).Returns(true);
        var toolset = new ImageEditorToolset(accessor.Object, flags.Object, projects.Object, editors, threads, launcher,
            new Mock<IDelegatedTurnGate>().Object, jobs, steps: steps);
        return (toolset, threads, jobs, steps);
    }

    private static Task<McpToolCallResult> Call(ImageEditorToolset toolset, string tool, JsonObject args) =>
        toolset.CallAsync(tool, args, new McpToolCallContext(Owner, Chat, Chat), default);

    private async Task<string> Generate(ImageEditorToolset toolset, string threadId, int count, string? versionId = null)
    {
        var args = new JsonObject
        {
            ["threadId"] = threadId, ["prompt"] = "синий фон", ["provider"] = VariantsEditor.ProviderKey, ["count"] = count,
        };
        if (versionId is not null) args["versionId"] = versionId;
        var result = await Call(toolset, ImageEditorToolset.ToolGenerate, args);
        result.IsError.Should().BeFalse(result.Text);
        return JsonNode.Parse(result.Text)!["jobId"]!.GetValue<string>();
    }

    // Варианты становятся версиями по событию исполнителя — ждём, пока запуск закроется
    private async Task<ImageThread> Finished(string threadId, string jobId)
    {
        for (var i = 0; i < 500; i++)
        {
            var thread = Thread(threadId);
            if (thread.Launches.Single(l => l.JobId == jobId).Status != ImageThreadLaunchStatus.Running) return thread;
            await Task.Delay(10);
        }
        throw new TimeoutException("запуск не закрылся");
    }

    [Fact]
    public async Task Запуск_агентом_на_три_варианта_даёт_три_версии_и_один_якорь_внизу_ленты()
    {
        var (toolset, _, jobs, steps) = Agent();
        var id = Opened();

        var jobId = await Generate(toolset, id, 3);
        var thread = await Finished(id, jobId);

        var ai = thread.Versions.Where(v => !v.IsOrigin).ToList();
        ai.Should().HaveCount(3);
        ai.Select(v => v.Variant).Should().Equal(1, 2, 3);
        ai.Should().OnlyContain(v => v.JobId == jobId && v.BaseVersionId == ImageThreadVersion.OriginId);
        foreach (var v in ai)
            steps.Open(Owner, ProjectId, v.CurrentStepId!).Should().NotBeNull("вариант лёг шагом и переживёт реестр задач");
        thread.CurrentVersionId.Should().Be(ai[0].Id);

        var records = _feed.Records.Where(r => r.SessionId == Chat).Select(r => r.Record).ToList();
        var anchor = records.Should().ContainSingle(r => r.RecordType == ImageThreadService.RecordTypes.LaunchVersions).Subject;
        records.Last().Should().BeSameAs(anchor, "якорь запуска ложится внизу ленты");
        anchor.Module.Should().Be(ImageThreadService.ModuleKey);
        anchor.Data!.Value.GetProperty("threadId").GetString().Should().Be(id);
        anchor.Data.Value.GetProperty("jobId").GetString().Should().Be(jobId);
        anchor.Fallback.Should().StartWith("Claude запустил: «синий фон»");
        records.Should().NotContain(r => r.RecordType == ImageThreadService.RecordTypes.Launch);

        // Трата при принятии без суммы, затем сумма догоняет её записью без генераций
        _spend.Records.Sum(r => r.Generations).Should().Be(3);
        _spend.Records.Should().OnlyContain(r => r.Initiator == SpendInitiators.Agent && r.SessionId == Chat);
        jobs.Get(Owner, ProjectId, jobId)!.Status.Should().Be(ImageEditJobStatus.Completed);
        _store.Get(Owner, Chat).Events.Should().Contain(e => e.Kind == ImageThreadEventKinds.Versions
            && e.Text.StartsWith("Готово «синий фон» в картинку images/hero.png: версии 1–3"));
        _broadcaster.ToOwnerCalls.Select(c => c.Message).OfType<ImageThreadChangedMessage>()
            .Should().Contain(m => m.State.Threads.Single().Versions.Count == 4);
    }

    [Fact]
    public async Task Агент_с_versionId_правит_указанную_версию_а_прежние_версии_не_тронуты()
    {
        var (toolset, _, jobs, _) = Agent();
        var id = Opened();
        var first = await Finished(id, await Generate(toolset, id, 2));
        var second = first.Versions.Single(v => v.Number == 2);
        first.CurrentVersionId.Should().NotBe(second.Id);

        var jobId = await Generate(toolset, id, 1, versionId: second.Id);
        var after = await Finished(id, jobId);

        jobs.Get(Owner, ProjectId, jobId)!.BaseStepId.Should().Be(second.CurrentStepId, "исходник — картинка второй версии");
        var third = after.Versions.Last();
        third.Number.Should().Be(3);
        third.BaseVersionId.Should().Be(second.Id);
        after.Versions.Take(3).Should().BeEquivalentTo(first.Versions);
        after.CurrentVersionId.Should().Be(third.Id);

        var state = JsonNode.Parse((await Call(toolset, ImageEditorToolset.ToolState, [])).Text)!;
        var versions = state["threads"]![0]!["thread"]!["versions"]!.AsArray();
        versions.Select(v => v!["label"]!.GetValue<string>()).Should().Equal("исходник", "версия 1", "версия 2", "версия 3");
        versions[3]!["from"]!.GetValue<string>().Should().Be("версия 2");
        versions[3]!["current"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Агент_берёт_версию_в_работу_через_image_focus_и_чужая_версия_отказ_без_задачи()
    {
        var (toolset, _, _, _) = Agent();
        var id = Opened();
        var thread = await Finished(id, await Generate(toolset, id, 2));
        var second = thread.Versions.Single(v => v.Number == 2);
        var spent = _spend.Records.Count;

        var focus = await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["threadId"] = id, ["versionId"] = second.Id });
        var unknown = await Call(toolset, ImageEditorToolset.ToolGenerate,
            new JsonObject { ["threadId"] = id, ["prompt"] = "ещё", ["versionId"] = "нет-такой" });
        var noThread = await Call(toolset, ImageEditorToolset.ToolFocus, new JsonObject { ["versionId"] = second.Id });

        focus.IsError.Should().BeFalse(focus.Text);
        Thread(id).CurrentVersionId.Should().Be(second.Id);
        unknown.IsError.Should().BeTrue();
        unknown.Text.Should().Contain("нет версии");
        noThread.IsError.Should().BeTrue();
        _spend.Records.Should().HaveCount(spent, "чужая версия — отказ до котировки и траты");
        Thread(id).Launches.Should().ContainSingle();
    }

    [Fact]
    public async Task Правка_без_ИИ_через_сервис_ложится_в_текущую_версию_без_карточки_в_ленте()
    {
        var (_, threads, _, steps) = Agent();
        var id = Opened();
        var step = steps.Transform(Owner, ProjectId,
            new ImageTransformRequest(new ImageTransformBase(), [new RotateOp(90)]),
            new ProjectImage("images/hero.png", File.ReadAllBytes(Path.Combine(_root, "images", "hero.png"))), dryRun: false);

        var added = await threads.AddStepAsync(Owner, ProjectId, Chat, id, step.Value!.StepId!, Revision);

        added.Write!.Status.Should().Be(ImageThreadWriteStatus.Ok);
        Thread(id).Versions.Should().ContainSingle().Which.Steps.Should().Equal(step.Value.StepId);
        _feed.Records.Should().BeEmpty("правка без ИИ новых карточек не создаёт");
        (await threads.AddStepAsync(Owner, ProjectId, Chat, id, new string('a', 32), Revision)).ErrorCode
            .Should().Be(ImageEditErrorCodes.StepNotFound);
    }

    private sealed class RecordingFeed : IChatFeed
    {
        public List<(string SessionId, StoredModuleRecord Record)> Records { get; } = [];

        public Task<bool> AppendRecordAsync(string sessionId, StoredModuleRecord record, CancellationToken ct = default)
        {
            lock (Records) Records.Add((sessionId, record));
            return Task.FromResult(true);
        }
    }

    // Поставщик, который сразу отдаёт столько настоящих PNG, сколько вариантов просили
    private sealed class VariantsEditor : IImageEditor
    {
        public const string ProviderKey = "vary";

        private static readonly ImageEditModelInfo Model = new("m", "M",
            new ImageEditCaps([ImageEditOp.Edit, ImageEditOp.Generate], MaskSupport.AsReference, 3, 4, false));

        public string Key => ProviderKey;
        public string Label => "Vary";
        public string PriceUnit => ImageEditPriceUnits.Usd;
        public bool Enabled => true;
        public IReadOnlyList<ImageEditModelInfo> Models => [Model];
        public ImageEditModelInfo? PickModel(ImageEditOp op, EditMode mode, EditTraits traits) => Model;

        public Task<ImageEditResult> RunAsync(ImageEditRequest req, IProgress<EditProgress> progress, CancellationToken ct)
        {
            progress.Report(new EditProgress(EditStage.Running));
            SKColor[] colors = [SKColors.Blue, SKColors.Green, SKColors.Yellow, SKColors.Black];
            var images = Enumerable.Range(0, req.Count).Select(i => new EditedImage(Png(8, 6, colors[i]), "image/png")).ToList();
            return Task.FromResult(new ImageEditResult(EditOutcome.Ok, images, new EditCost(0.01 * req.Count, PriceUnit), true, null, null));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
