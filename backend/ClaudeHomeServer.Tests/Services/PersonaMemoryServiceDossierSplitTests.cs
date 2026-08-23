using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Dossiers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Разделение блока досье из recall-memory (план «Секции промптов» этап 3): параметр
// splitDossier у BuildRecallAsync. false (дефолт, persona_ask и выключенный флаг
// specialty-prompt-sections) — досье остаётся ВНУТРИ Text, как до фичи; true (SessionManager
// на ходу персоны при включённом флаге) — досье уходит в отдельное поле DossierText,
// секция dossier-recall клеится своим местом промпта (ClaudeSession).
public class PersonaMemoryServiceDossierSplitTests : IDisposable
{
    private const string OwnerId = "owner-split";
    private const string ProjectId = "project-split";
    private const string AnchorFile = "backend/A.cs";

    private readonly string _tempDir;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _sut;
    private readonly Persona _persona;
    private readonly DossierStore _dossierStore;

    public PersonaMemoryServiceDossierSplitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmem_dossier_split_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        _personas = new PersonaManager(config);
        _dossierStore = new DossierStore(config);
        _sut = new PersonaMemoryService(knowledge, _personas, userStore, config,
            NullLogger<PersonaMemoryService>.Instance, dossierRecall: new FakeRecall(_dossierStore));

        _persona = _personas.Create(OwnerId, "Ада", "Аналитик", null, null,
            null, null, PersonaScope.Global, null, null, null, memoryEnabled: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static ChangeDossier Dossier(string sha = "aaaa1111", string file = AnchorFile) => new()
    {
        OwnerId = OwnerId,
        ProjectId = ProjectId,
        CommitSha = sha,
        CommitSubject = "subj " + sha,
        CommittedAt = DateTimeOffset.UtcNow,
        Why = "зачем " + sha,
        Decisions = ["решили " + sha],
        Files = [file],
    };

    private static DossierRecallRequest Request(string file = AnchorFile, string text = "") =>
        new(ProjectId, "C:/repo", null, [file], text);

    [Fact]
    public async Task SplitDossierTrue_ДосьеОтдельноОтText()
    {
        _dossierStore.Add(Dossier());
        _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Пользователь любит краткие ответы", null, null);

        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "пользователь ответы краткие",
            topK: 5, minScore: 0.1, Request(), splitDossier: true);

        recall.Should().NotBeNull();
        recall!.Text.Should().NotBeNull("личная память подмешана");
        recall.Text.Should().NotContain("dossier_get(", "досье вынесено из recall-memory отдельной секцией");
        recall.DossierText.Should().NotBeNull("якорь файла хода совпал с паспортом");
        recall.DossierText.Should().Contain("dossier_get(");
        recall.DossierHits.Should().ContainSingle(d => d.CommitSha == "aaaa1111");
    }

    [Fact]
    public async Task SplitDossierFalse_ДосьеВнутриText_КакДоФичи()
    {
        _dossierStore.Add(Dossier());
        _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Пользователь любит краткие ответы", null, null);

        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "пользователь ответы краткие",
            topK: 5, minScore: 0.1, Request());

        recall.Should().NotBeNull();
        recall!.Text.Should().Contain("dossier_get(", "дефолт splitDossier=false — досье остаётся внутри recall-memory");
        recall.DossierText.Should().BeNull("не разделяем без явного splitDossier=true");
    }

    [Fact]
    public async Task SplitDossierTrue_ТолькоДосье_TextNullНоDossierTextЕсть()
    {
        _dossierStore.Add(Dossier());

        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "запрос без совпадений по памяти",
            topK: 5, minScore: 0.99, Request(), splitDossier: true);

        recall.Should().NotBeNull();
        recall!.Text.Should().BeNull("ни фокуса, ни хитов памяти, ни команды — recall-memory пуст");
        recall.DossierText.Should().NotBeNull("но досье по якорю нашлось — секция dossier-recall не должна теряться");
    }

    // Наследник без git (ResolveHeadAsync → null): деградация на сохранённых статусах,
    // как в DossierRecallTests.FakeRecall — тому же нужен настоящий git.
    private sealed class FakeRecall(DossierStore store) : DossierRecallService(store)
    {
        protected override Task<string?> ResolveHeadAsync(string ownerId, string root) =>
            Task.FromResult<string?>(null);
    }
}
