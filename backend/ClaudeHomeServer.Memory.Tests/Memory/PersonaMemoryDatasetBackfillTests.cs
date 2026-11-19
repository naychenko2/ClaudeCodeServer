using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Memory;

/// <summary>
/// Догон датасетов памяти после старта: в очередь синхронизации встают только персоны с
/// подтверждённой записью, без датасета и существующие в сторе персон.
/// </summary>
public class PersonaMemoryDatasetBackfillTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "persona-backfill-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IKnowledgeIndex> _knowledge = new();
    private readonly Mock<IPersonaLookup> _lookup = new();
    private readonly Mock<IPersonaResolver> _resolver = new();
    private readonly PersonaMemoryService _memory;

    public PersonaMemoryDatasetBackfillTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        // Без Dify запись памяти не ставит синхронизацию — стор наполняется «как до догона»
        _knowledge.SetupGet(k => k.IsConfigured).Returns(false);
        _memory = new PersonaMemoryService(
            _knowledge.Object, _resolver.Object, _lookup.Object,
            new Mock<IPersonaDirectory>().Object, new Mock<IPersonaEvents>().Object,
            new Mock<IDifyMetrics>().Object, new Mock<IUserStore>().Object, config,
            NullLogger<PersonaMemoryService>.Instance);
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* временный каталог */ }
    }

    private Persona Add(string id, string owner, bool exists, params bool[] pendingEntries)
    {
        var p = new Persona { Id = id, OwnerId = owner, Name = id };
        _resolver.Setup(r => r.Get(id, owner)).Returns(p);
        if (exists) _lookup.Setup(l => l.GetByIdInternal(id)).Returns(p);
        foreach (var pending in pendingEntries)
            _memory.Remember(owner, id, PersonaMemoryType.Semantic, "факт " + Guid.NewGuid(), null, null, pending: pending);
        return p;
    }

    [Fact]
    public void Backfill_БезDify_НичегоНеСтавит()
    {
        Add("p1", "o1", true, false);
        Assert.Equal(0, _memory.BackfillMissingDatasets());
    }

    [Fact]
    public void Backfill_СтавитТолькоПерсонСПодтверждённойЗаписьюБезДатасета()
    {
        Add("with-entry", "o1", true, false);
        Add("mixed", "o1", true, true, false);
        Add("only-pending", "o1", true, true);
        Add("deleted", "o2", false, false);
        _knowledge.SetupGet(k => k.IsConfigured).Returns(true);

        Assert.Equal(2, _memory.BackfillMissingDatasets());
    }

    [Fact]
    public async Task Backfill_УКогоДатасетУже_НеТрогает()
    {
        Add("p1", "o1", true, false);
        _knowledge.SetupGet(k => k.IsConfigured).Returns(true);
        _knowledge.Setup(k => k.CreateDatasetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string?>())).ReturnsAsync("ds-1");
        _knowledge.Setup(k => k.IndexFileByTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<List<string>?>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(new DifyDocumentInfo("doc-1", "n", "completed"));
        await _memory.SyncAsync("o1", "p1");   // датасет появился

        Assert.Equal(0, _memory.BackfillMissingDatasets());
    }
}
