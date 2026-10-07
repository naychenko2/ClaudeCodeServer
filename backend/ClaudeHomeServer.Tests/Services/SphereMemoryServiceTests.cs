using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Полка памяти сферы: запись/поиск, изоляция сфер, перезагрузка стора, удаление вместе со сферой
public class SphereMemoryServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;
    private readonly SphereMemoryService _svc;

    public SphereMemoryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sphere-mem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build();
        _svc = new SphereMemoryService(_config);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* тест-мусор */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Add_Дефолты_ManualFact_БезПроисхождения()
    {
        var e = _svc.Add("u1", "s1", "  общий стек сферы — .NET  ");

        e.Text.Should().Be("общий стек сферы — .NET");
        e.Source.Should().Be(TeamMemorySource.Manual);
        e.Type.Should().Be(TeamMemoryType.Fact);
        e.ProjectId.Should().Be("s1");
        e.PromotedFrom.Should().BeNull();
    }

    [Fact]
    public async Task Search_НаходитЗапись_ИзвестнойСферы()
    {
        _svc.Add("u1", "s1", "деплой идёт через blue-green выкладку");
        _svc.Add("u1", "s1", "названия веток начинаются с feature");

        var found = await _svc.SearchAsync("u1", "s1", "blue-green деплой");

        found.Should().ContainSingle().Which.Text.Should().Contain("blue-green");
    }

    [Fact]
    public async Task Recall_ВыдаётЗаголовокПамятиСферы()
    {
        _svc.Add("u1", "s1", "релизы по четвергам");

        var r = await _svc.BuildRecallBlockAsync("u1", "s1", "релизы четверг");

        r.Text.Should().StartWith("## Память сферы");
        r.Used.Should().ContainSingle();
    }

    [Fact]
    public async Task ДвеСферы_Изолированы_ВПоискеИСписке()
    {
        _svc.Add("u1", "s1", "секрет первой сферы про кроликов");
        _svc.Add("u1", "s2", "секрет второй сферы про черепах");

        (await _svc.SearchAsync("u1", "s1", "черепах")).Should().BeEmpty();
        (await _svc.SearchAsync("u1", "s2", "кроликов")).Should().BeEmpty();
        _svc.List("u1", "s1").Should().ContainSingle().Which.Text.Should().Contain("кроликов");
        _svc.Count("u1", "s2").Should().Be(1);
    }

    [Fact]
    public void ДваВладельца_СОдинаковымИдСферы_Изолированы()
    {
        _svc.Add("u1", "s1", "запись первого владельца");

        _svc.List("u2", "s1").Should().BeEmpty();
        _svc.Count("u2", "s1").Should().Be(0);
    }

    [Fact]
    public async Task DeleteAllForSphere_УдаляетТолькоСвоюСферу_ИВозвращаетЧисло()
    {
        _svc.Add("u1", "s1", "первая запись сферы один");
        _svc.Add("u1", "s1", "вторая запись сферы один");
        _svc.Add("u1", "s2", "запись сферы два");

        var deleted = await _svc.DeleteAllForSphereAsync("u1", "s1");

        deleted.Should().Be(2);
        _svc.Count("u1", "s1").Should().Be(0);
        _svc.List("u1", "s2").Should().ContainSingle();
        _svc.AllScopes().Should().ContainSingle().Which.Should().Be(("u1", "s2"));
    }

    [Fact]
    public async Task DeleteAllForSphere_ПереживаетПерезагрузкуСтора()
    {
        _svc.Add("u1", "s1", "запись на удаление");
        _svc.Add("u1", "s2", "запись остаётся");
        await _svc.DeleteAllForSphereAsync("u1", "s1");

        using var reloaded = new SphereMemoryService(_config);

        reloaded.Count("u1", "s1").Should().Be(0);
        reloaded.Count("u1", "s2").Should().Be(1);
    }

    [Fact]
    public void PromotedFrom_СохраняетсяВСтореИЧитаетсяПослеПерезагрузки()
    {
        var at = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        _svc.Add("u1", "s1", "поднятая из проекта договорённость",
            TeamMemoryType.Convention, promotedFrom: new MemoryPromotion("p1", "e1", at));

        using var reloaded = new SphereMemoryService(_config);

        var e = reloaded.List("u1", "s1").Should().ContainSingle().Subject;
        e.Source.Should().Be(TeamMemorySource.Manual);
        e.PromotedFrom.Should().Be(new MemoryPromotion("p1", "e1", at));
    }

    [Fact]
    public void Файлы_Стора_ЛежатВDataСОжидаемымиИменами()
    {
        _svc.Add("u1", "s1", "запись для проверки файла");

        File.Exists(Path.Combine(_dir, "sphere-memory.json")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "team-memory.json")).Should().BeFalse();
    }

    [Fact]
    public void DatasetName_ИмеетФормуUsernameSphereId()
    {
        SphereMemoryService.DatasetName("alice", "s-42").Should().Be("alice:sphere:s-42");
    }

    // --- Dify: удаление датасета вместе со сферой и подъём записи через асинхронный путь ---

    [Fact]
    public async Task DeleteAllForSphere_УдаляетDifyДатасетСферы()
    {
        var dify = new FakeDify();
        using var svc = new SphereMemoryService(_config, knowledge: dify);
        svc.Add("u1", "s1", "запись, уходящая в Dify");
        await svc.SyncAsync("u1", "s1");
        dify.Datasets.Should().ContainSingle();
        var datasetId = dify.Datasets.Single();

        await svc.DeleteAllForSphereAsync("u1", "s1");

        dify.DeletedDatasets.Should().Equal(datasetId);
    }

    [Fact]
    public async Task Sync_ПослеУдаленияСферы_НеВоскрешаетДатасет()
    {
        var dify = new FakeDify();
        using var svc = new SphereMemoryService(_config, knowledge: dify);
        svc.Add("u1", "s1", "запись до удаления");   // синк отложен дебаунсом — датасета ещё нет
        await svc.DeleteAllForSphereAsync("u1", "s1");

        await svc.SyncAsync("u1", "s1");

        dify.Datasets.Should().BeEmpty("пустая удалённая сфера не должна получить новый датасет");
    }

    [Fact]
    public async Task AdoptAsync_ОтправляетЗаписьВDifyСразу_ИПеремещаетЕё()
    {
        var dify = new FakeDify();
        using var svc = new SphereMemoryService(_config, knowledge: dify);
        using var team = new TeamMemoryService(_config);
        var entry = team.Add("u1", "p1", "договорённость проекта про релизы");

        var adopted = await svc.AdoptAsync("u1", "s1", team, "p1", entry.Id);

        adopted.Should().NotBeNull();
        dify.Indexed.Should().ContainSingle().Which.Should().Contain("договорённость проекта про релизы");
        team.List("u1", "p1").Should().BeEmpty();
        svc.List("u1", "s1").Should().ContainSingle();
    }

    private sealed class FakeDify : IKnowledgeIndex
    {
        public bool IsConfigured => true;
        public List<string> Datasets { get; } = [];
        public List<string> DeletedDatasets { get; } = [];
        public List<string> Indexed { get; } = [];

        public Task<string> CreateDatasetAsync(string name, string permission = "only_me", string? description = null,
            string? indexingTechnique = null)
        {
            var id = "ds-" + (Datasets.Count + DeletedDatasets.Count + 1);
            Datasets.Add(id);
            return Task.FromResult(id);
        }

        public Task DeleteDocumentAsync(string datasetId, string documentId) => Task.CompletedTask;

        public Task DeleteDatasetAsync(string datasetId)
        {
            Datasets.Remove(datasetId);
            DeletedDatasets.Add(datasetId);
            return Task.CompletedTask;
        }

        public Task RenameDatasetAsync(string datasetId, string newName) => Task.CompletedTask;

        public Task<DifyDocumentInfo> IndexFileByTextAsync(string datasetId, string fileName, string content,
            List<string>? tags = null)
        {
            Indexed.Add(content);
            return Task.FromResult(new DifyDocumentInfo("doc-" + Indexed.Count, fileName, "completed"));
        }

        public Task<IReadOnlyList<DifyRetrieveChunk>> RetrieveAsync(string datasetId, string query, int topK = 8,
            IReadOnlyList<KnowledgeMetadataFilter>? filters = null) =>
            Task.FromResult<IReadOnlyList<DifyRetrieveChunk>>([]);
    }
}
