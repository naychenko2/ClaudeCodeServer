using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

public sealed class LoraTrainingFinisherTests : IDisposable
{
    private const string Slug = "andrey";
    private static readonly string Owner = Guid.NewGuid().ToString();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ltf-" + Guid.NewGuid().ToString("N"));
    private readonly CharacterLoraRuns _runs = new(NullLogger<CharacterLoraRuns>.Instance);
    private readonly FakeTrainer _trainer = new();
    private readonly Project _project;
    private readonly LoraTrainingFinisher _sut;
    private readonly Mock<IProjectManager> _projects = new();
    private readonly Mock<ILocalImageMedia> _media = new();

    public LoraTrainingFinisherTests()
    {
        Directory.CreateDirectory(_root);
        _project = new Project { Name = "p", RootPath = _root, OwnerId = Owner };
        var projects = _projects;
        projects.Setup(p => p.GetById(_project.Id)).Returns(_project);
        projects.Setup(p => p.GetAll()).Returns([_project]);
        var media = _media;
        media.Setup(m => m.ListLorasAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[File(1), File(2), "manual.safetensors"]);
        _sut = new LoraTrainingFinisher(projects.Object, _runs, NullLogger<LoraTrainingFinisher>.Instance,
            _trainer, media.Object);
        WriteCharacter(null);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static string RunId(int n) => n.ToString("x12");
    private static string File(int n) => LoraNamespace.RunLoraFile(Owner, Slug, RunId(n));

    private void WriteCharacter(string? lora)
    {
        var manifest = new CharacterManifest { Name = "Андрей", Slug = Slug, CreatedAt = DateTime.UtcNow };
        if (lora is not null)
            manifest.Providers[CharacterManifest.LocalProvider] = new JsonObject { ["lora"] = lora, ["trigger"] = "manualtrig" };
        var dir = Path.Combine(_root, "characters", Slug);
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(Path.Combine(dir, "character.json"), manifest.Serialize());
    }

    private string? ActiveLora() => CharacterStore.Get(_root, Slug)!.ReadLora()?.Name;

    private void Training(int n) => _runs.Create(_root, Slug, new CharacterLoraRun(
        1, RunId(n), $"job{n}", CharacterLoraRuns.Status.Training, null, "standard", "ccsandrey", 30, "sha256:ab",
        DateTimeOffset.UtcNow, null, null, null, null, null));

    private void Job(int n, LoraTrainStatus status, LoraTrainFailure? error = null) => _trainer.States.Add(new LoraTrainState(
        $"job{n}", Owner, _project.Id, Slug, RunId(n), "standard", status, null, null, 2000, null,
        DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, status == LoraTrainStatus.Completed ? File(n) : null,
        error, status == LoraTrainStatus.Completed ? 27 : null,
        status == LoraTrainStatus.Completed ? [new LoraPhotoRejection("a.jpg", "no_face")] : null));

    [Fact]
    public async Task FirstVersion_BecomesReady_AndConnectsItself()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);

        await _sut.RunOnceAsync(default);

        var run = _runs.Get(_root, Slug, RunId(1))!;
        run.Status.Should().Be(CharacterLoraRuns.Status.Ready);
        run.LoraFile.Should().Be(File(1));
        run.PhotosKept.Should().Be(27);
        run.Rejected.Should().ContainSingle(r => r.Name == "a.jpg");
        run.TrainedAt.Should().NotBeNull();
        ActiveLora().Should().Be(File(1));
    }

    [Fact]
    public async Task SecondVersion_BecomesReady_ManifestKeepsActiveLora()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);
        await _sut.RunOnceAsync(default);
        Training(2);
        Job(2, LoraTrainStatus.Completed);

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(2))!.Status.Should().Be(CharacterLoraRuns.Status.Ready);
        ActiveLora().Should().Be(File(1));
    }

    [Fact]
    public async Task ManualLora_StaysConnected_WhenVersionFinishes()
    {
        WriteCharacter("manual.safetensors");
        Training(1);
        Job(1, LoraTrainStatus.Completed);

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Ready);
        ActiveLora().Should().Be("manual.safetensors");
    }

    [Fact]
    public async Task Failed_RecordsError_ManifestUntouched()
    {
        Training(1);
        Job(1, LoraTrainStatus.Failed, new LoraTrainFailure("oom", "не хватило памяти"));

        await _sut.RunOnceAsync(default);

        var run = _runs.Get(_root, Slug, RunId(1))!;
        run.Status.Should().Be(CharacterLoraRuns.Status.Failed);
        run.Error.Should().Be(new LoraRunError("oom", "не хватило памяти"));
        ActiveLora().Should().BeNull();
    }

    [Fact]
    public async Task Cancelled_MarksCancelled_ManifestUntouched()
    {
        Training(1);
        Job(1, LoraTrainStatus.Cancelled);

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Cancelled);
        ActiveLora().Should().BeNull();
    }

    [Fact]
    public async Task RunningJob_LeavesRecordTraining()
    {
        Training(1);
        Job(1, LoraTrainStatus.Running);

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Training);
    }

    [Fact]
    public async Task Deleting_ErasedAfterDeleted_KeptOnPendingAndFailed_EvenWithoutJob()
    {
        // Записи «deleting» без живой задачи — то, что осталось после падения между Update и Erase
        foreach (var n in new[] { 1, 2, 3 })
            _runs.Create(_root, Slug, new CharacterLoraRun(1, RunId(n), null, CharacterLoraRuns.Status.Deleting, null,
                "standard", "ccsandrey", 30, "sha256:ab", DateTimeOffset.UtcNow, null, null, null, null, null));
        _trainer.Delete[RunId(1)] = LoraDeleteState.Deleted;
        _trainer.Delete[RunId(2)] = LoraDeleteState.Pending;
        _trainer.Delete[RunId(3)] = LoraDeleteState.Failed;

        await _sut.RunOnceAsync(default);

        _runs.ListDeleting(_root, Slug).Select(r => r.RunId).Should().BeEquivalentTo([RunId(2), RunId(3)]);

        _trainer.Delete[RunId(2)] = LoraDeleteState.Deleted;
        await _sut.RunOnceAsync(default);
        _runs.ListDeleting(_root, Slug).Select(r => r.RunId).Should().Equal(RunId(3));
    }

    [Fact]
    public async Task ManualConnect_DuringFinish_IsNotOverwritten()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);
        // Человек подключает свою LoRA, пока доводчик ждёт ответ ComfyUI: перепроверка под замком не даёт её затереть
        _media.Setup(m => m.ListLorasAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            WriteCharacter("manual.safetensors");
            return Task.FromResult<IReadOnlyList<string>?>([File(1), "manual.safetensors"]);
        });

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Ready);
        ActiveLora().Should().Be("manual.safetensors");
    }

    [Fact]
    public async Task ComfyUnavailable_KeepsTraining_ThenReadyAndConnectedAfterRecovery()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);
        _media.Setup(m => m.ListLorasAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>?)null);

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Training);
        ActiveLora().Should().BeNull();

        _media.Setup(m => m.ListLorasAsync(It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<string>)[File(1)]);
        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Ready);
        ActiveLora().Should().Be(File(1));
    }

    [Fact]
    public async Task CompletedWithoutFile_BecomesFailedWorkerError()
    {
        Training(1);
        _trainer.States.Add(new LoraTrainState($"job1", Owner, _project.Id, Slug, RunId(1), "standard", LoraTrainStatus.Completed,
            null, null, 2000, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, null, null, null, null));

        await _sut.RunOnceAsync(default);

        var run = _runs.Get(_root, Slug, RunId(1))!;
        run.Status.Should().Be(CharacterLoraRuns.Status.Failed);
        run.Error!.Code.Should().Be("worker_error");
    }

    [Fact]
    public async Task DeviceBoundProject_IsSkipped()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);
        _project.DeviceId = Guid.NewGuid().ToString();

        await _sut.RunOnceAsync(default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Training);
    }

    [Fact]
    public async Task UnknownProject_IsSkippedWithoutError()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);

        await _sut.SettleAsync(Owner, Guid.NewGuid().ToString(), Slug, default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Training);
    }

    [Fact]
    public async Task AfterFirstPass_ProjectsWithoutDeleting_AreNotScanned_AndNewDeletingIsPickedUpViaIndex()
    {
        await _sut.RunOnceAsync(default);
        _projects.Invocations.Clear();

        await _sut.RunOnceAsync(default);
        _projects.Verify(p => p.GetAll(), Times.Never);

        Training(1);
        _runs.Update(_root, Slug, RunId(1), r => r with { Status = CharacterLoraRuns.Status.Deleting });
        _trainer.Delete[RunId(1)] = LoraDeleteState.Deleted;
        await _sut.RunOnceAsync(default);

        _runs.ListDeleting(_root, Slug).Should().BeEmpty();
        _runs.CharactersWithDeleting().Should().BeEmpty();
    }

    [Fact]
    public async Task ForeignProject_IsSkipped()
    {
        Training(1);
        Job(1, LoraTrainStatus.Completed);

        await _sut.SettleAsync(Guid.NewGuid().ToString(), _project.Id, Slug, default);

        _runs.Get(_root, Slug, RunId(1))!.Status.Should().Be(CharacterLoraRuns.Status.Training);
    }

    private sealed class FakeTrainer : ILocalLoraTrainer
    {
        public List<LoraTrainState> States { get; } = [];
        public Dictionary<string, LoraDeleteState> Delete { get; } = [];
        public bool Enabled => true;
        public LoraTrainBusy? Busy(string ownerId) => null;
        public Task<LoraTrainStartResult> StartAsync(LoraTrainStart request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LoraTrainState?> GetAsync(string ownerId, string jobId, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<LoraTrainState> ListFor(string ownerId) => [.. States.Where(s => s.OwnerId == ownerId)];
        public IReadOnlyList<LoraTrainState> ListAll() => States;
        public Task<LoraCancelResult> CancelAsync(string ownerId, string jobId, bool asAdmin, CancellationToken ct) => throw new NotSupportedException();
        public Task<LoraDeleteState> DeleteRunAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct) =>
            Task.FromResult(Delete.GetValueOrDefault(runId, LoraDeleteState.Pending));
    }
}
