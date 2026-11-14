using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaudeHomeServer.Tasks.Tests;

// D1a: сборка экрана этапа — артефакты (коммиты с трейлером CCS-Task, цепочка версий шага и доработок),
// вход и транскрипт, ревью; изоляция по владельцу; дифф только коммита из списка этапа.
// Git — настоящий временный репозиторий (читатель тот же, что в проде), лента чата — заглушка.
public class StageScreenServiceTests : IDisposable
{
    private const string Owner = "owner", Stranger = "stranger";

    private sealed class LocalLaunchers : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => LocalProcessRunner.Instance;
        public IProcessLauncher ForProject(Project project) => LocalProcessRunner.Instance;
    }

    private sealed class FakeTranscripts(params StoredMessage[] messages) : IChatTranscriptReader
    {
        public Task<IReadOnlyList<StoredMessage>> ReadAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(messages);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stage_" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly TaskManager _tasks;

    public StageScreenServiceTests()
    {
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _tasks = new TaskManager(config);
        Git("init", "-b", "master");
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "t");
        Commit("README.md", "основа", "основа");
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "git " + string.Join(' ', args));
        return output.Trim();
    }

    // Коммит файла; taskId задан — с трейлером CCS-Task
    private string Commit(string file, string content, string subject, string? taskId = null)
    {
        File.WriteAllText(Path.Combine(_repo, file), content);
        Git("add", "-A");
        var message = taskId is null ? subject : subject + "\n\nCCS-Task: " + taskId;
        Git("commit", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    private StageScreenService Sut(IChatTranscriptReader? transcripts = null, IProjectAccess? access = null) =>
        new(_tasks, access, new GitWorktreeHeadReader(new LocalLaunchers()), transcripts);

    private TaskItem Epic() => _tasks.Create(null, Owner, new CreateTaskRequest("фича", Role: TaskRoles.Feature));

    private TaskItem Step(TaskItem epic, string role = TaskRoles.Step, string stepRef = "D1a", List<string>? reviewOf = null)
    {
        var t = _tasks.Create(null, Owner, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: role, StepRef: stepRef, ReviewOf: reviewOf));
        t.WorktreePath = _repo;
        return t;
    }

    [Fact]
    public void Три_блока_заполнены_на_фикстуре()
    {
        var epic = Epic();
        var step = Step(epic);
        step.LinkedFiles = ["backend/A.cs"];
        step.ResultMarkdown = "сделано";
        step.AwaitingHumanSince = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
        step.AwaitingHumanReason = "нужен выбор";
        step.LinkedSessionId = "chat-1";
        var sha = Commit("A.cs", "class A {}", "feat: A", step.Id);
        var review = _tasks.Create(null, Owner, new CreateTaskRequest("ревью", FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id]));
        review.Verification = new TaskVerification { Verdict = TaskVerdicts.Returned, ReturnReason = "size", Notes = "много", VerifiedAt = DateTime.UtcNow };
        var transcripts = new FakeTranscripts(
            new StoredUserMessage("Сделай ручку"),
            new StoredToolUseMessage { Id = "1", Name = "Read", Input = JsonSerializer.SerializeToElement(new { file_path = "/x/A.cs" }), StartedAt = 1_700_000_000_000 },
            new StoredToolUseMessage { Id = "2", Name = "Bash", Input = JsonSerializer.SerializeToElement(new { command = "dotnet build" }), Result = "ОГРОМНЫЙ ВЫВОД" });

        var screen = Sut(transcripts).Build(step.Id, Owner).Value!;

        screen.Artifacts.Commits.Should().ContainSingle().Which.Sha.Should().Be(sha);
        screen.Artifacts.Commits[0].Files.Should().Equal("A.cs");
        screen.Artifacts.Commits[0].Version.Should().Be(1);
        screen.Artifacts.CommitsAvailable.Should().BeTrue();
        screen.Artifacts.LinkedFiles.Should().Equal("backend/A.cs");
        screen.Artifacts.ResultMarkdown.Should().Be("сделано");
        screen.Input.Transcript!.Prompt.Should().Be("Сделай ручку");
        screen.Input.Transcript.FilesRead.Should().Equal("/x/A.cs");
        screen.Input.Transcript.ToolCalls.Select(c => c.Name).Should().Equal("Read", "Bash");
        screen.Input.Transcript.ToolCalls[1].Summary.Should().Be("dotnet build");
        screen.Input.Transcript.ToolCalls[0].At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).UtcDateTime);
        JsonSerializer.Serialize(screen).Should().NotContain("ОГРОМНЫЙ ВЫВОД", "полные ответы инструментов не отдаются");
        screen.Review.Reviews.Should().ContainSingle();
        screen.Review.Reviews[0].Verdict.Should().Be("returned");
        screen.Review.Reviews[0].Blocker.Should().BeTrue();
        screen.Review.Reviews[0].Notes.Should().Be("много");
        screen.Review.AwaitingHuman!.Reason.Should().Be("нужен выбор");
        screen.Metrics.Should().NotBeNull("метрики этапа заполняет D1b; журнала в фикстуре нет → approx");
        screen.Metrics!.Mode.Should().Be("approx");
    }

    [Fact]
    public void Версии_артефакта_цепочка_шага_и_доработки_того_же_stepRef()
    {
        var epic = Epic();
        var step = Step(epic);
        var other = Step(epic, stepRef: "D2");
        var rev = _tasks.Create(null, Owner, new CreateTaskRequest("ревью", FeatureId: epic.Id, Role: TaskRoles.Review, ReviewOf: [step.Id]));
        var rework = Step(epic, TaskRoles.Rework, reviewOf: [rev.Id]);
        var s1 = Commit("a.txt", "1", "feat: шаг", step.Id);
        Commit("b.txt", "x", "feat: чужой шаг", other.Id);
        var s2 = Commit("a.txt", "2", "fix: доработка", rework.Id);

        var commits = Sut().Build(step.Id, Owner).Value!.Artifacts.Commits;

        commits.Select(c => c.Sha).Should().Equal(s1, s2);
        commits.Select(c => c.Version).Should().Equal(1, 2);
        commits[1].TaskId.Should().Be(rework.Id);
    }

    [Fact]
    public void Трейлер_чужой_задачи_с_общим_префиксом_id_не_считается()
    {
        var epic = Epic();
        var step = Step(epic);
        Commit("a.txt", "1", "feat: чужой", step.Id + "-x");

        Sut().Build(step.Id, Owner).Value!.Artifacts.Commits.Should().BeEmpty();
    }

    [Fact]
    public void Чужой_пользователь_и_не_фичевая_задача_и_нет_задачи_NotFound()
    {
        var epic = Epic();
        var step = Step(epic);
        var plain = _tasks.Create(null, Owner, new CreateTaskRequest("обычная"));
        var sut = Sut();

        sut.Build(step.Id, Stranger).Outcome.Should().Be(StageOutcome.NotFound);
        sut.Build(plain.Id, Owner).Outcome.Should().Be(StageOutcome.NotFound);
        sut.Build("нет-такой", Owner).Outcome.Should().Be(StageOutcome.NotFound);
        sut.BuildDiff(step.Id, "abcdef1", Stranger).Outcome.Should().Be(StageOutcome.NotFound);
    }

    [Fact]
    public void Дифф_только_коммита_из_списка_этапа()
    {
        var epic = Epic();
        var step = Step(epic);
        var inList = Commit("a.txt", "содержимое-этапа", "feat: A", step.Id);
        var outside = Commit("b.txt", "постороннее", "feat: без трейлера");
        var sut = Sut();

        var ok = sut.BuildDiff(step.Id, inList[..7], Owner);
        ok.Outcome.Should().Be(StageOutcome.Ok);
        ok.Value!.Diff.Should().Contain("содержимое-этапа");
        ok.Value.Sha.Should().Be(inList);
        sut.BuildDiff(step.Id, outside, Owner).Outcome.Should().Be(StageOutcome.NotFound);
        sut.BuildDiff(step.Id, "../x", Owner).Outcome.Should().Be(StageOutcome.BadRequest);
        sut.BuildDiff(step.Id, "ABCDEF1", Owner).Outcome.Should().Be(StageOutcome.BadRequest);
        sut.BuildDiff(step.Id, "abc", Owner).Outcome.Should().Be(StageOutcome.BadRequest);
        sut.BuildDiff(step.Id, new string('a', 41), Owner).Outcome.Should().Be(StageOutcome.BadRequest);
        sut.BuildDiff(step.Id, null, Owner).Outcome.Should().Be(StageOutcome.BadRequest);
    }

    [Fact]
    public void Дифф_длиннее_потолка_режется_и_помечен()
    {
        var epic = Epic();
        var step = Step(epic);
        var sha = Commit("big.txt", string.Join('\n', Enumerable.Repeat("строка строка строка строка", StageScreenService.MaxDiffChars / 10)), "feat: большой", step.Id);

        var diff = Sut().BuildDiff(step.Id, sha, Owner).Value!;

        diff.Truncated.Should().BeTrue();
        diff.Diff.Length.Should().Be(StageScreenService.MaxDiffChars);
    }

    [Fact]
    public void Секреты_в_постановке_и_аргументах_вырезаются()
    {
        var epic = Epic();
        var step = Step(epic);
        step.LinkedSessionId = "chat-1";
        const string secret = "sk-ant-abcdefghijklmnop1234567890";
        var transcripts = new FakeTranscripts(
            new StoredUserMessage("ключ " + secret),
            new StoredToolUseMessage { Id = "1", Name = "Bash", Input = JsonSerializer.SerializeToElement(new { command = "curl -H " + secret }) });

        var t = Sut(transcripts).Build(step.Id, Owner).Value!.Input.Transcript!;

        t.Prompt.Should().NotContain(secret);
        t.ToolCalls[0].Summary.Should().NotContain(secret);
    }

    private sealed class FakeAccess(bool sessionVisible) : IProjectAccess
    {
        public RequestScope? Resolve(string? callerUserId, string? projectId, ProjectRight right = ProjectRight.Read) =>
            new(callerUserId ?? "", Owner, ProjectRole.Reader);

        public RequestScope? ResolveSession(string? callerUserId, string? sessionId, ProjectRight right = ProjectRight.Read) =>
            sessionVisible ? new(callerUserId ?? "", Owner, ProjectRole.Reader) : null;
    }

    private TaskItem SharedStep()
    {
        var step = Step(Epic());
        step.ProjectId = "p1";
        step.LinkedSessionId = "chat-1";
        return step;
    }

    [Fact]
    public void Участник_проекта_без_доступа_к_чату_не_видит_ленту_остальное_отдаётся()
    {
        var step = SharedStep();
        step.ResultMarkdown = "сделано";
        var transcripts = new FakeTranscripts(new StoredUserMessage("приватная постановка"));

        var screen = Sut(transcripts, new FakeAccess(sessionVisible: false)).Build(step.Id, Stranger).Value!;

        screen.Input.TranscriptAvailable.Should().BeFalse();
        screen.Input.Transcript.Should().BeNull();
        screen.Artifacts.ResultMarkdown.Should().Be("сделано", "остальные блоки отдаются");
    }

    [Fact]
    public void Участник_проекта_с_доступом_к_чату_видит_ленту()
    {
        var step = SharedStep();
        var transcripts = new FakeTranscripts(new StoredUserMessage("постановка"));

        var screen = Sut(transcripts, new FakeAccess(sessionVisible: true)).Build(step.Id, Stranger).Value!;

        screen.Input.TranscriptAvailable.Should().BeTrue();
        screen.Input.Transcript!.Prompt.Should().Be("постановка");
    }

    [Fact]
    public void Секрет_на_границе_усечения_не_виден_частично()
    {
        var step = Step(Epic());
        step.LinkedSessionId = "chat-1";
        const string secret = "sk-ant-abcdefghijklmnop1234567890";
        // токен пересекает позицию 160 в аргументе и MaxPromptChars в постановке
        var command = new string('a', StageScreenService.MaxSummaryChars - 10) + " " + secret;
        var prompt = new string('b', StageScreenService.MaxPromptChars - 10) + " " + secret;
        var transcripts = new FakeTranscripts(
            new StoredUserMessage(prompt),
            new StoredToolUseMessage { Id = "1", Name = "Bash", Input = JsonSerializer.SerializeToElement(new { command }) });

        var t = Sut(transcripts).Build(step.Id, Owner).Value!.Input.Transcript!;

        t.ToolCalls[0].Summary.Should().NotContain("sk-ant").And.NotContain("abcdefghij");
        t.Prompt.Should().NotContain("sk-ant").And.NotContain("abcdefghij");
    }

    [Fact]
    public void Нет_дерева_или_git_читатель_null_список_пуст_и_помечен_недоступным()
    {
        var epic = Epic();
        var step = Step(epic);
        step.WorktreePath = Path.Combine(_dir, "нет-такого");

        var screen = Sut().Build(step.Id, Owner).Value!;

        screen.Artifacts.Commits.Should().BeEmpty();
        screen.Artifacts.CommitsAvailable.Should().BeFalse();
        screen.Input.Transcript.Should().BeNull();
    }
}
