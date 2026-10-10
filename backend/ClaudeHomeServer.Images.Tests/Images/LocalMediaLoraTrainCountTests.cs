using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Счётчики захвата карты обучением LoRA: считаются по стору, а не отдельной очередью
public class LocalMediaLoraTrainCountTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "lora_count_" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private LocalMediaJobStore _store;

    public LocalMediaLoraTrainCountTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        _store = new LocalMediaJobStore(_config);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private void Add(string owner, string op, string status = LocalMediaStatuses.Running) =>
        _store.Add(new LocalMediaJob
        {
            Id = "lm_" + Guid.NewGuid().ToString("N"), OwnerId = owner, ProjectId = "p", Op = op, Status = status,
        });

    [Fact]
    public void ActiveTraining_BusyForInstanceAndForOwner()
    {
        Add("a", LocalMediaOps.LoraTrain);

        _store.ActiveLoraTrainCount().Should().Be(1);
        _store.ActiveLoraTrainCount("a").Should().Be(1);
        _store.ActiveLoraTrainCount("b").Should().Be(0);
    }

    [Fact]
    public void QueuedTraining_IsActive()
    {
        Add("a", LocalMediaOps.LoraTrain, LocalMediaStatuses.Queued);
        _store.ActiveLoraTrainCount("a").Should().Be(1);
    }

    [Theory]
    [InlineData(LocalMediaStatuses.Completed)]
    [InlineData(LocalMediaStatuses.Failed)]
    public void TerminalTraining_NotCounted(string status)
    {
        Add("a", LocalMediaOps.LoraTrain, status);
        _store.ActiveLoraTrainCount().Should().Be(0);
        _store.ActiveLoraTrainCount("a").Should().Be(0);
    }

    [Fact]
    public void CancelledTraining_NotCounted()
    {
        var id = "lm_" + Guid.NewGuid().ToString("N");
        _store.Add(new LocalMediaJob { Id = id, OwnerId = "a", ProjectId = "p", Op = LocalMediaOps.LoraTrain });
        _store.Update(id, "a", j => j.Status = LocalMediaStatuses.Failed);
        _store.ActiveLoraTrainCount().Should().Be(0);
    }

    [Fact]
    public void OtherOps_NotCounted()
    {
        Add("a", LocalMediaOps.GenerateImage);
        Add("a", LocalMediaOps.LoraDelete);
        _store.ActiveLoraTrainCount().Should().Be(0);
    }

    [Fact]
    public void AfterRestart_ActiveTrainingStillCounted()
    {
        Add("a", LocalMediaOps.LoraTrain);
        _store = new LocalMediaJobStore(_config);

        _store.ActiveLoraTrainCount().Should().Be(1);
        _store.ActiveLoraTrainCount("a").Should().Be(1);
    }

    [Fact]
    public void TwoOwners_CountedSeparately()
    {
        Add("a", LocalMediaOps.LoraTrain);
        Add("b", LocalMediaOps.LoraTrain, LocalMediaStatuses.Queued);

        _store.ActiveLoraTrainCount().Should().Be(2);
        _store.ActiveLoraTrainCount("a").Should().Be(1);
        _store.ActiveLoraTrainCount("b").Should().Be(1);
    }
}
