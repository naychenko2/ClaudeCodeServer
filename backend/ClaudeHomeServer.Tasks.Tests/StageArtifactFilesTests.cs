using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// B06: файлы этапа с версиями (версия = коммит, тронувший файл) и дифф файла между версиями.
public class StageArtifactFilesTests : IDisposable
{
    private const string Owner = "owner";
    private const string Wt = "/tmp/wt-b06";

    private static readonly DateTime T0 = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stageaf_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly FakeReader _git = new();
    private readonly StageScreenService _sut;

    public StageArtifactFilesTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        _sut = new StageScreenService(_tasks, git: _git);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private TaskItem Step()
    {
        var epic = _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));
        var step = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B06"));
        step.WorktreePath = Wt;
        return step;
    }

    private static string Sha(char c) => new(c, 40);

    [Fact]
    public void Файл_тронут_тремя_коммитами_версии_1_3_и_afterReturn_у_третьей()
    {
        var step = Step();
        var review = _tasks.Create(null, Owner, new CreateTaskRequest("ревью", FeatureId: step.FeatureId, Role: TaskRoles.Review, ReviewOf: [step.Id]));
        review.Verification = new TaskVerification { Verdict = TaskVerdicts.Returned, VerifiedAt = T0.AddMinutes(30) };
        _git.Commits = [
            new WorktreeCommit(Sha('a'), "первый", T0, ["A.cs", "B.cs"]),
            new WorktreeCommit(Sha('b'), "второй", T0.AddMinutes(10), ["A.cs"]),
            new WorktreeCommit(Sha('c'), "после возврата", T0.AddMinutes(60), ["A.cs"]),
        ];

        var art = _sut.Build(step.Id, Owner).Value!.Artifacts;

        art.Totals.Should().Be(new StageArtifactTotals(2, 3));
        var a = art.Files.Single(f => f.Path == "A.cs");
        a.Versions.Select(v => v.N).Should().Equal(1, 2, 3);
        a.Versions.Select(v => v.CommitSha).Should().Equal(Sha('a'), Sha('b'), Sha('c'));
        a.Versions.Select(v => v.AfterReturn).Should().Equal(false, false, true);
        art.Files.Single(f => f.Path == "B.cs").Versions.Should().ContainSingle();
        art.Commits.Should().HaveCount(3);
    }

    [Fact]
    public void Дифф_v1_до_v3_берёт_sha_первой_и_третьей_версии_и_режется_по_лимиту()
    {
        var step = Step();
        _git.Commits = [
            new WorktreeCommit(Sha('a'), "1", T0, ["A.cs"]),
            new WorktreeCommit(Sha('b'), "2", T0.AddMinutes(1), ["A.cs"]),
            new WorktreeCommit(Sha('c'), "3", T0.AddMinutes(2), ["A.cs"]),
        ];
        _git.Diff = new WorktreeDiff("@@ diff", true);

        var r = _sut.BuildFileDiff(step.Id, "A.cs", "1", "3", Owner);

        r.Outcome.Should().Be(StageOutcome.Ok);
        r.Value!.Diff.Should().Be("@@ diff");
        r.Value.Truncated.Should().BeTrue();
        r.Value.MaxChars.Should().Be(StageScreenService.MaxDiffChars);
        _git.LastFileDiff.Should().Be((Sha('a'), Sha('c'), "A.cs", StageScreenService.MaxDiffChars));

        // all → от состояния до первой версии до последней
        _sut.BuildFileDiff(step.Id, "A.cs", "all", "all", Owner).Outcome.Should().Be(StageOutcome.Ok);
        _git.LastFileDiff.Should().Be((Sha('a') + "^", Sha('c'), "A.cs", StageScreenService.MaxDiffChars));

        // чужой путь, версия вне диапазона и from > to
        _sut.BuildFileDiff(step.Id, "Z.cs", "1", "3", Owner).Outcome.Should().Be(StageOutcome.NotFound);
        _sut.BuildFileDiff(step.Id, "A.cs", "1", "4", Owner).Outcome.Should().Be(StageOutcome.NotFound);
        _sut.BuildFileDiff(step.Id, "A.cs", "3", "1", Owner).Outcome.Should().Be(StageOutcome.BadRequest);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("a/../../etc/passwd")]
    [InlineData("")]
    [InlineData(".")]
    public void Путь_вне_дерева_или_корень_даёт_400_и_git_не_вызывается(string path)
    {
        var step = Step();
        _git.Commits = [new WorktreeCommit(Sha('a'), "1", T0, ["A.cs"])];

        _sut.BuildFileDiff(step.Id, path, "1", "1", Owner).Outcome.Should().Be(StageOutcome.BadRequest);
        _git.LastFileDiff.Should().Be(default((string, string, string, int)));
    }

    [Fact]
    public void Пустой_этап_без_файлов_и_нулевые_итоги()
    {
        var step = Step();
        _git.Commits = [];

        var art = _sut.Build(step.Id, Owner).Value!.Artifacts;

        art.Files.Should().BeEmpty();
        art.Totals.Should().Be(new StageArtifactTotals(0, 0));
        _sut.BuildFileDiff(step.Id, "A.cs", null, null, Owner).Outcome.Should().Be(StageOutcome.NotFound);
    }

    private sealed class FakeReader : IWorktreeCommitReader
    {
        public IReadOnlyList<WorktreeCommit> Commits { get; set; } = [];
        public WorktreeDiff Diff { get; set; } = new("", false);
        public (string From, string To, string File, int Max) LastFileDiff { get; private set; }

        public IReadOnlyList<WorktreeCommit>? CommitsWithTaskTrailer(string worktreePath, string taskId, int limit) => Commits;
        public WorktreeDiff? CommitDiff(string worktreePath, string fullSha, int maxChars) => null;

        public WorktreeDiff? FileDiff(string worktreePath, string fromRef, string toRef, string file, int maxChars)
        {
            LastFileDiff = (fromRef, toRef, file, maxChars);
            return Diff;
        }
    }
}
