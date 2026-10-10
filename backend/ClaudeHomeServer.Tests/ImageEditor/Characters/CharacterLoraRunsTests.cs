using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

public sealed class CharacterLoraRunsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "clr-" + Guid.NewGuid().ToString("N"));
    private readonly CharacterLoraRuns _sut = new(NullLogger<CharacterLoraRuns>.Instance);
    private const string Slug = "andrey";

    public CharacterLoraRunsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string RunsDir => Path.Combine(_root, "characters", Slug, "lora", "runs");

    private static CharacterLoraRun Run(int n, string status = "ready") => new(
        1, n.ToString("x12"), $"lm_{n}", status, null, "standard", "ccsandrey", 30, "sha256:ab",
        DateTimeOffset.UnixEpoch.AddMinutes(n), null, status == "ready" ? $"ccs/x/andrey-{n:x12}/andrey.safetensors" : null,
        null, null, null);

    [Fact]
    public void Limit_CountsReadyAndTraining_NotFailedOrCancelled()
    {
        foreach (var (n, st) in new[] { (1, "ready"), (2, "training"), (3, "failed"), (4, "cancelled"), (5, "deleting") })
            _sut.Create(_root, Slug, Run(n, st)).Should().BeTrue();

        _sut.CountTowardsLimit(_root, Slug).Should().Be(2);
    }

    [Fact]
    public void TrainingIndex_CreateRegisters_UpdateToFailedUnregisters()
    {
        _sut.Create(_root, Slug, Run(1, "training")).Should().BeTrue();
        _sut.CharactersWithTraining().Should().ContainSingle().Which.Slug.Should().Be(Slug);

        _sut.Update(_root, Slug, 1.ToString("x12"), r => r with { Status = "failed" });

        _sut.CharactersWithTraining().Should().BeEmpty();
    }

    [Fact]
    public void List_HidesDeleting_NewestFirst_ListDeletingSeesIt()
    {
        _sut.Create(_root, Slug, Run(1));
        _sut.Create(_root, Slug, Run(2));
        _sut.Create(_root, Slug, Run(3, "deleting"));

        _sut.List(_root, Slug).Select(r => r.RunId).Should().Equal(2.ToString("x12"), 1.ToString("x12"));
        _sut.ListDeleting(_root, Slug).Select(r => r.RunId).Should().Equal(3.ToString("x12"));
    }

    [Fact]
    public void BrokenAndForeignJson_AreSkipped()
    {
        _sut.Create(_root, Slug, Run(1));
        File.WriteAllText(Path.Combine(RunsDir, "aaaaaaaaaaaa.json"), "{ не json");
        // валидный json, но runId внутри не совпадает с именем файла
        File.WriteAllText(Path.Combine(RunsDir, "bbbbbbbbbbbb.json"),
            File.ReadAllText(Path.Combine(RunsDir, 1.ToString("x12") + ".json")));
        File.WriteAllText(Path.Combine(RunsDir, "note.json"), "{}");

        _sut.List(_root, Slug).Select(r => r.RunId).Should().Equal(1.ToString("x12"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("aaaaaaaaaaaa\n")]
    [InlineData("AAAAAAAAAAAA")]
    public void InvalidRunId_IsRejected(string runId)
    {
        _sut.Create(_root, Slug, Run(1) with { RunId = runId }).Should().BeFalse();
        _sut.Get(_root, Slug, runId).Should().BeNull();
        _sut.Update(_root, Slug, runId, r => r).Should().BeNull();
        _sut.Erase(_root, Slug, runId).Should().BeFalse();
    }

    [Fact]
    public void InvalidSlug_IsRejected()
    {
        _sut.Create(_root, "../evil", Run(1)).Should().BeFalse();
        _sut.List(_root, "../evil").Should().BeEmpty();
    }

    [Fact]
    public void Create_Duplicate_Refused_UpdateAndErase_Work()
    {
        var run = Run(1, "training");
        _sut.Create(_root, Slug, run).Should().BeTrue();
        _sut.Create(_root, Slug, run).Should().BeFalse();

        var updated = _sut.Update(_root, Slug, run.RunId, r => r with { Status = "deleting", RunId = "ffffffffffff" });
        updated!.Status.Should().Be("deleting");
        updated.RunId.Should().Be(run.RunId);
        _sut.Get(_root, Slug, run.RunId)!.Status.Should().Be("deleting");

        _sut.Erase(_root, Slug, run.RunId).Should().BeTrue();
        _sut.Get(_root, Slug, run.RunId).Should().BeNull();
        _sut.Erase(_root, Slug, run.RunId).Should().BeFalse();
    }

    [Fact]
    public void Update_FailingChange_LeavesOldFileIntact_NoTemp()
    {
        var run = Run(1);
        _sut.Create(_root, Slug, run);

        var act = () => _sut.Update(_root, Slug, run.RunId, _ => throw new InvalidOperationException("сбой"));

        act.Should().Throw<InvalidOperationException>();
        _sut.Get(_root, Slug, run.RunId).Should().BeEquivalentTo(run);
        Directory.GetFiles(RunsDir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void IsConnected_ComparesFileName()
    {
        var run = Run(1);
        CharacterLoraRuns.IsConnected(run, run.LoraFile).Should().BeTrue();
        CharacterLoraRuns.IsConnected(run, "other.safetensors").Should().BeFalse();
        CharacterLoraRuns.IsConnected(run, null).Should().BeFalse();
        CharacterLoraRuns.IsConnected(Run(2, "failed"), null).Should().BeFalse();
    }
}
