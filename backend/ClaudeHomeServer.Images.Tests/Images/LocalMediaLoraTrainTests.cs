using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Обучение LoRA персонажа (ADR-026): порядок отказов, загрузка фото на GPU, постановка графа,
// отмена, переживание рестарта, сбор результата без записи в проект, удаление прогона
public class LocalMediaLoraTrainTests : IDisposable
{
    private static readonly string Owner = Guid.NewGuid().ToString();
    private static readonly string Other = Guid.NewGuid().ToString();
    private const string RunId = "3f9a1c2b7d10";
    private const string Slug = "andrey";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "lora_train_" + Guid.NewGuid().ToString("N"));
    private readonly FakeComfy _comfy = new();

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private IConfiguration Config(bool loraEnabled = true) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:LoraTrainEnabled"] = loraEnabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();

    private (LocalMediaService Service, LocalMediaJobStore Store) Build(bool loraEnabled = true, TimeProvider? time = null)
    {
        var config = Config(loraEnabled);
        var store = new LocalMediaJobStore(config);
        var service = new LocalMediaService(new ComfyClient(new FakeComfyFactory(_comfy), config), store,
            new FakeProjectAccess(), config, NullLogger<LocalMediaService>.Instance, null, time)
        {
            // Сокет ComfyUI в тестах не открываем
            LoraProgressListener = (_, _, ct) => Task.Delay(Timeout.Infinite, ct),
        };
        return (service, store);
    }

    private LoraTrainStart Start(string? owner = null, int photos = 20, string preset = "standard", string trigger = "ccsandrey",
        string runId = RunId)
    {
        Directory.CreateDirectory(_tempDir);
        var list = Enumerable.Range(0, photos).Select(i =>
        {
            var name = $"{i:x16}.jpg";
            var path = Path.Combine(_tempDir, name);
            File.WriteAllBytes(path, [1, 2, 3, (byte)i]);
            return new LoraTrainPhoto(name, path);
        }).ToList();
        return new LoraTrainStart(owner ?? Owner, "proj-1", Slug, runId, preset, trigger, list);
    }

    private static void AddActive(LocalMediaJobStore store, string owner, string status = LocalMediaStatuses.Running) =>
        store.Add(new LocalMediaJob
        {
            Id = "lm_" + Guid.NewGuid().ToString("N"), OwnerId = owner, ProjectId = "p", Op = LocalMediaOps.LoraTrain,
            Status = status, EtaSeconds = 5000, StartedAt = DateTime.UtcNow, LoraSlug = Slug, LoraRunId = RunId,
        });

    [Fact]
    public async Task Disabled_WhenToggleOff()
    {
        var (service, _) = Build(loraEnabled: false);
        var result = await service.StartLoraTrainAsync(Start(), default);
        result.Error.Should().Be(LoraTrainErrorCode.Disabled);
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task BadPresetAndTrigger_Refused()
    {
        var (service, _) = Build();
        (await service.StartLoraTrainAsync(Start(preset: "turbo"), default)).Error.Should().Be(LoraTrainErrorCode.BadPreset);
        (await service.StartLoraTrainAsync(Start(trigger: "Bad Trigger"), default)).Error.Should().Be(LoraTrainErrorCode.BadTrigger);
    }

    [Fact]
    public async Task TooFewPhotos_Refused()
    {
        var (service, _) = Build();
        (await service.StartLoraTrainAsync(Start(photos: 19), default)).Error.Should().Be(LoraTrainErrorCode.TooFewPhotos);
    }

    [Fact]
    public async Task OwnerBusy_RefusedWithMineBusy()
    {
        var (service, store) = Build();
        AddActive(store, Owner);
        var result = await service.StartLoraTrainAsync(Start(), default);
        result.Error.Should().Be(LoraTrainErrorCode.OwnerBusy);
        result.Busy!.Mine.Should().BeTrue();
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task GpuBusy_ByAnotherOwner_ReturnsBusyUntil()
    {
        var (service, store) = Build();
        AddActive(store, Other);
        var result = await service.StartLoraTrainAsync(Start(), default);
        result.Error.Should().Be(LoraTrainErrorCode.GpuBusy);
        result.Busy!.Mine.Should().BeFalse();
        result.Busy.BusyUntil.Should().NotBeNull();
        service.LoraBusy(Owner)!.JobId.Should().Be(result.Busy.JobId);
    }

    [Fact]
    public async Task ComfyDown_Unavailable()
    {
        var (service, store) = Build();
        _comfy.Down = true;
        (await service.StartLoraTrainAsync(Start(), default)).Error.Should().Be(LoraTrainErrorCode.Unavailable);
        store.ActiveLoraTrainCount().Should().Be(0);
    }

    [Fact]
    public async Task Start_UploadsPhotosToRunFolderAndQueuesGraph()
    {
        var (service, store) = Build();
        var result = await service.StartLoraTrainAsync(Start(), default);

        result.Error.Should().BeNull();
        result.EtaSeconds.Should().Be(4850);
        var folder = LoraNamespace.RunInputFolder(Owner, RunId) + "/";
        _comfy.UploadPaths.Should().HaveCount(20).And.OnlyContain(p => p.StartsWith(folder));
        var worker = _comfy.Prompts.Single()["prompt"]!["worker"]!["inputs"]!.AsObject();
        worker["op"]!.GetValue<string>().Should().Be("lora_train");
        JsonNode.Parse(worker["params_json"]!.GetValue<string>())!["run_id"]!.GetValue<string>().Should().Be(RunId);

        var job = store.Get(result.JobId!, Owner)!;
        job.Heavy.Should().BeTrue();
        job.Op.Should().Be(LocalMediaOps.LoraTrain);
        job.LoraPreset.Should().Be("standard");
        job.ComfyInputs.Should().BeEmpty("фото чистит воркер, а не чистка LocalMedia");
        store.ActiveLoraTrainCount(Owner).Should().Be(1);
    }

    [Fact]
    public async Task Cancel_Queued_RemovesFromQueue_AndFreesGpu()
    {
        var (service, store) = Build();
        _comfy.Running.Add("foreign-prompt");
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;

        (await service.CancelLoraTrainAsync(Other, id, asAdmin: false, default)).Should().Be(LoraCancelResult.NotFound);
        (await service.CancelLoraTrainAsync(Owner, id, asAdmin: false, default)).Should().Be(LoraCancelResult.Cancelled);

        _comfy.Pending.Should().BeEmpty();
        store.ActiveLoraTrainCount().Should().Be(0);
        (await service.GetLoraTrainAsync(Owner, id, default))!.Status.Should().Be(LoraTrainStatus.Cancelled);
        (await service.CancelLoraTrainAsync(Owner, id, asAdmin: false, default)).Should().Be(LoraCancelResult.AlreadyFinished);
    }

    [Fact]
    public async Task Cancel_Running_InterruptsByPromptId_AdminMayCancelForeign()
    {
        var (service, _) = Build();
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;
        var prompt = _comfy.Pending.Single();
        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);

        (await service.CancelLoraTrainAsync(Other, id, asAdmin: true, default)).Should().Be(LoraCancelResult.Cancelled);
        _comfy.Interrupted.Should().Equal(prompt);
    }

    // Состояние очереди меняется ровно между опросом отмены и снятием: ждущая стала идущей
    [Fact]
    public async Task Cancel_QueuedBecomesRunningDuringCancel_StillInterrupts()
    {
        var (service, store) = Build();
        _comfy.Running.Add("foreign-prompt");
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;
        var prompt = _comfy.Pending.Single();
        _comfy.AfterQueueRead = () =>
        {
            _comfy.AfterQueueRead = null;
            _comfy.Pending.Remove(prompt);
            _comfy.Running.Add(prompt);
        };

        (await service.CancelLoraTrainAsync(Owner, id, asAdmin: false, default)).Should().Be(LoraCancelResult.Cancelled);

        _comfy.Interrupted.Should().Contain(prompt);
        store.ActiveLoraTrainCount().Should().Be(0);
    }

    [Fact]
    public async Task Cancel_ComfyUnreachable_KeepsJobActiveUntilSweepInterrupts()
    {
        var (service, store) = Build();
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;
        var prompt = _comfy.Pending.Single();
        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);
        _comfy.Down = true;

        (await service.CancelLoraTrainAsync(Owner, id, asAdmin: false, default)).Should().Be(LoraCancelResult.Cancelled);

        // Прогон на карте мог продолжиться — карту не освобождаем
        store.ActiveLoraTrainCount().Should().Be(1);
        var job = store.ListByOp(LocalMediaOps.LoraTrain, Owner).Single();
        job.CancelRequested.Should().BeTrue();
        job.Cancelled.Should().BeFalse();
        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(0);
        store.ActiveLoraTrainCount().Should().Be(1);

        _comfy.Down = false;
        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(1);

        _comfy.Interrupted.Should().Contain(prompt);
        store.ActiveLoraTrainCount().Should().Be(0);
        (await service.GetLoraTrainAsync(Owner, id, default))!.Status.Should().Be(LoraTrainStatus.Cancelled);
    }

    // Отмена запрошена при лежащем ComfyUI; возвращает сервис, часы и id задачи
    private async Task<(LocalMediaService Service, LocalMediaJobStore Store, FakeClock Clock, string Id, string Prompt)> CancelRequestedWhileDownAsync()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var (service, store) = Build(time: clock);
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;
        var prompt = _comfy.Pending.Single();
        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);
        _comfy.Down = true;
        await service.CancelLoraTrainAsync(Owner, id, asAdmin: false, default);
        _comfy.Down = false;
        return (service, store, clock, id, prompt);
    }

    [Fact]
    public async Task Sweep_CancelRequestedBeyondCeiling_ComfyConfirmsNotRunning_FailsWithCancelTimeout()
    {
        var (service, store, clock, id, prompt) = await CancelRequestedWhileDownAsync();
        clock.Now = clock.Now.AddMinutes(31);

        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(1);

        store.ActiveLoraTrainCount().Should().Be(0);
        var state = (await service.GetLoraTrainAsync(Owner, id, default))!;
        state.Status.Should().Be(LoraTrainStatus.Failed);
        state.Error!.Code.Should().Be("cancel_timeout");
    }

    [Fact]
    public async Task Sweep_CancelRequestedBeyondCeiling_ComfyDown_KeepsCardBusy()
    {
        var (service, store, clock, _, _) = await CancelRequestedWhileDownAsync();
        _comfy.Down = true;
        clock.Now = clock.Now.AddMinutes(600);

        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(0);

        store.ActiveLoraTrainCount().Should().Be(1);
    }

    [Fact]
    public async Task Sweep_QueuedBehindForeignRuns_IsNotTimedOutFromCreatedAt()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var (service, store) = Build(time: clock);
        _comfy.Running.Add("foreign-prompt");
        await service.StartLoraTrainAsync(Start(), default);

        clock.Now = clock.Now.AddMinutes(600);
        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(0);

        store.ActiveLoraTrainCount().Should().Be(1);
        _comfy.Interrupted.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_PartialUploadFailure_QueuesCleanupOfRunFolder()
    {
        var (service, store) = Build();
        _comfy.FailUploadsAfter = 5;

        var result = await service.StartLoraTrainAsync(Start(), default);

        result.Error.Should().Be(LoraTrainErrorCode.Unavailable);
        _comfy.Uploads.Should().HaveCount(5);
        store.ActiveLoraTrainCount().Should().Be(0);
        var cleanup = store.ListByOp(LocalMediaOps.LoraDelete, Owner).Should().ContainSingle().Subject;
        cleanup.LoraRunId.Should().Be(RunId);
        _comfy.Prompts.Single()["prompt"]!["worker"]!["inputs"]!["op"]!.GetValue<string>().Should().Be("lora_delete");
    }

    [Fact]
    public async Task Start_FailureBeforeAnyUpload_QueuesNoCleanup()
    {
        var (service, store) = Build();
        _comfy.FailUploadsAfter = 0;

        (await service.StartLoraTrainAsync(Start(), default)).Error.Should().Be(LoraTrainErrorCode.Unavailable);
        store.ListByOp(LocalMediaOps.LoraDelete, Owner).Should().BeEmpty();
    }

    private sealed class FakeClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Sweep_HungTrainBeyondCeiling_InterruptsAndFailsWithTimeout()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        var (service, store) = Build(time: clock);
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;
        var prompt = _comfy.Pending.Single();
        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);
        await service.GetLoraTrainAsync(Owner, id, default); // фиксирует StartedAt

        clock.Now = clock.Now.AddMinutes(269);
        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(0);
        store.ActiveLoraTrainCount().Should().Be(1);

        clock.Now = clock.Now.AddMinutes(2);
        (await service.SweepLoraTimeoutsAsync(default)).Should().Be(1);

        _comfy.Interrupted.Should().Equal(prompt);
        store.ActiveLoraTrainCount().Should().Be(0);
        var state = (await service.GetLoraTrainAsync(Owner, id, default))!;
        state.Status.Should().Be(LoraTrainStatus.Failed);
        state.Error!.Code.Should().Be("timeout");
    }

    [Fact]
    public async Task DeleteRun_AfterFailure_QueuesNewTask()
    {
        var (service, _) = Build();
        await service.DeleteLoraRunAsync(Owner, "proj-1", Slug, RunId, default);
        _comfy.Fail(_comfy.Pending.Single(), "boom");

        (await service.DeleteLoraRunAsync(Owner, "proj-1", Slug, RunId, default)).Should().Be(LoraDeleteState.Pending);
        _comfy.Prompts.Should().HaveCount(2);

        _comfy.Complete(_comfy.Pending.Single());
        (await service.DeleteLoraRunAsync(Owner, "proj-1", Slug, RunId, default)).Should().Be(LoraDeleteState.Deleted);
        _comfy.Prompts.Should().HaveCount(2);
    }

    [Fact]
    public async Task Job_SurvivesRestart()
    {
        var (service, _) = Build();
        _comfy.Running.Add("foreign-prompt");
        var id = (await service.StartLoraTrainAsync(Start(), default)).JobId!;

        var (restarted, store2) = Build();
        store2.ActiveLoraTrainCount(Owner).Should().Be(1);
        var state = await restarted.GetLoraTrainAsync(Owner, id, default);
        state!.Status.Should().Be(LoraTrainStatus.Queued);
        state.Slug.Should().Be(Slug);
        restarted.ListLoraTrain(Owner).Should().ContainSingle();
        restarted.ListLoraTrain(Other).Should().BeEmpty();
    }

    private async Task<(LocalMediaService Service, string JobId)> Started()
    {
        var (service, _) = Build();
        return (service, (await service.StartLoraTrainAsync(Start(), default)).JobId!);
    }

    private void Finish(string name, JsonObject? stats = null)
    {
        var prompt = _comfy.Pending.Single();
        _comfy.Complete(prompt);
        _comfy.History[prompt]["outputs"]!.AsObject()["worker"] = new JsonObject
        {
            ["ccs_lora"] = new JsonArray(new JsonObject { ["name"] = name }),
            ["ccs_stats"] = new JsonArray(stats ?? new JsonObject
            {
                ["photos_kept"] = 18,
                ["rejected"] = new JsonArray(new JsonObject { ["name"] = "0a1b2c3d4e5f6a7b.jpg", ["reason"] = "no_face" }),
            }),
        };
    }

    [Fact]
    public async Task Collect_Success_RecordsLoraFileWithoutProject()
    {
        var (service, id) = await Started();
        Finish(LoraNamespace.RunLoraFile(Owner, Slug, RunId));

        var state = (await service.GetLoraTrainAsync(Owner, id, default))!;
        state.Status.Should().Be(LoraTrainStatus.Completed);
        state.LoraFile.Should().Be(LoraNamespace.RunLoraFile(Owner, Slug, RunId));
        state.PhotosKept.Should().Be(18);
        state.Rejected.Should().ContainSingle(r => r.Reason == "no_face");
    }

    [Fact]
    public async Task Collect_ForeignLoraName_FailsWithBadResult()
    {
        var (service, id) = await Started();
        Finish(LoraNamespace.RunLoraFile(Other, Slug, RunId));

        var state = (await service.GetLoraTrainAsync(Owner, id, default))!;
        state.Status.Should().Be(LoraTrainStatus.Failed);
        state.Error!.Code.Should().Be("bad_result");
        state.LoraFile.Should().BeNull();
    }

    [Theory]
    [InlineData("CcsAudioWorker: lora failed ccs_error=too_few_faces", "too_few_faces")]
    [InlineData("CUDA out of memory", "oom")]
    [InlineData("что-то странное", "worker_error")]
    public async Task Collect_WorkerFailure_MapsErrorCode(string message, string code)
    {
        var (service, id) = await Started();
        _comfy.Fail(_comfy.Pending.Single(), message);

        var state = (await service.GetLoraTrainAsync(Owner, id, default))!;
        state.Status.Should().Be(LoraTrainStatus.Failed);
        state.Error!.Code.Should().Be(code);
    }

    [Fact]
    public void ParseHistory_LongMessage_KeepsCcsErrorMarkerInTrimmedTail()
    {
        var text = new string('x', 600) + " ccs_error=too_few_faces";
        var failed = new System.Text.Json.Nodes.JsonObject
        {
            ["status"] = new System.Text.Json.Nodes.JsonObject
            {
                ["status_str"] = "error",
                ["completed"] = false,
                ["messages"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonArray("execution_error",
                    new System.Text.Json.Nodes.JsonObject { ["node_type"] = "CcsAudioWorker", ["exception_message"] = text })),
            },
            ["outputs"] = new System.Text.Json.Nodes.JsonObject(),
        };

        var error = ComfyClient.ParseHistory(failed).Error!;

        error.Length.Should().BeLessOrEqualTo(400);
        error.Should().EndWith("ccs_error=too_few_faces");
    }

    [Fact]
    public async Task Collect_VanishedFromQueue_IsComfyRestart()
    {
        var (service, id) = await Started();
        _comfy.Pending.Clear();

        (await service.GetLoraTrainAsync(Owner, id, default))!.Error!.Code.Should().Be("comfy_restart");
    }

    [Fact]
    public async Task Running_ReportsStepFromProgressListener()
    {
        var (service, id) = await Started();
        var prompt = _comfy.Pending.Single();
        _comfy.Pending.Clear();
        _comfy.Running.Add(prompt);
        Action<double>? report = null;
        var listening = new TaskCompletionSource();
        service.LoraProgressListener = (_, on, ct) =>
        {
            report = on;
            listening.SetResult();
            return Task.Delay(Timeout.Infinite, ct);
        };

        await service.GetLoraTrainAsync(Owner, id, default);
        await listening.Task;
        report!(0.5);

        var state = (await service.GetLoraTrainAsync(Owner, id, default))!;
        state.Status.Should().Be(LoraTrainStatus.Running);
        state.Step.Should().Be(1000);
        state.EtaSeconds.Should().Be(LoraTrainPresets.Remaining(1000, 2000));
    }

    [Fact]
    public async Task DeleteRun_IsIdempotent_AndReportsOutcome()
    {
        var (service, _) = Build();

        (await service.DeleteLoraRunAsync(Owner, "proj-1", Slug, RunId, default)).Should().Be(LoraDeleteState.Pending);
        (await service.DeleteLoraRunAsync(Owner, "proj-1", Slug, RunId, default)).Should().Be(LoraDeleteState.Pending);
        _comfy.Prompts.Should().ContainSingle();
        _comfy.Prompts[0]["prompt"]!["worker"]!["inputs"]!["op"]!.GetValue<string>().Should().Be("lora_delete");

        _comfy.Complete(_comfy.Pending.Single());
        (await service.DeleteLoraRunAsync(Owner, "proj-1", Slug, RunId, default)).Should().Be(LoraDeleteState.Deleted);
        _comfy.Prompts.Should().ContainSingle();
    }

    [Fact]
    public void Heavy_AndRegistration()
    {
        LocalMediaService.IsHeavy(new LocalMediaRequest(Owner, "p", null, LocalMediaOps.LoraTrain)).Should().BeTrue();

        var config = Config();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.AddSingleton<ILocalMediaProjectAccess>(new FakeProjectAccess());
        services.AddLocalMedia(config);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILocalLoraTrainer>().Should().BeOfType<LocalLoraTrainerAdapter>();
    }
}
