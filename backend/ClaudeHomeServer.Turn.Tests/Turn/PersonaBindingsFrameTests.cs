using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.Turn;

// Доработка S2a-4 по ревью R-S2a-4 (Major 1): Always-привязки типа Notes/Knowledge кладут
// в блок «Привязанные знания и правила» ВЫЖИМКУ содержимого личных заметок/баз знаний
// владельца (PersonaBindingsService.ExtractNotesAsync), а секция нестабильная — при Narrow
// она ехала хвостом ТЕКСТА ХОДА, который участник общего проекта видит целиком. Гейт
// PersonaBindingsContributor.IsEnabled по рамке: при Narrow блок не строется вовсе
// (fail-closed, тот же приём, что у NotesRecallContributor).
//
// Фикстура не вакуумная: реальный PersonaBindingsService с реальным привязанным Notes-
// источником и маркером, который реальный пайплайн BuildTurnBlockAsync кладёт в блок
// (заглушен только внешний семантический индекс INoteSemanticIndex — его работа тестирована
// вертикалью Notes).
public class PersonaBindingsFrameTests : IDisposable
{
    private const string Marker = "МАРКЕР_ЛИЧНОЙ_ЗАМЕТКИ";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(),
        "pb_frame_tests_" + Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly string _userId;
    private readonly PersonaManager _personas = null!;
    private readonly PersonaBindingsService _bindings = null!;

    public PersonaBindingsFrameTests()
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userId = _users.GetFirst()!.Id; // дефолтный admin пустого стора
        var projects = new ProjectManager(config, _users, new AppSettingsService(config));
        _personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        _bindings = new PersonaBindingsService(_personas, projects, wkStore,
            knowledge, new SkillsService(), _users, config,
            NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: new MarkerNoteIndex());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // Полная цепочка через шину (шина зовёт BuildAsync только включённых IsEnabled —
    // TurnEventBus.ApplyAsync): в Wide маркер доезжает до секций промпта (поведение не
    // изменилось), в Narrow — ни одна секция его не содержит (0 вхождений).
    [Fact]
    public async Task NarrowFrame_БлокПривязокНеСтроется_WideСтроется()
    {
        var persona = MakePersonaWithAlwaysNotesBinding();
        var contributor = new PersonaBindingsContributor(
            new PersonaBindingsSourceAdapter(_bindings),
            NullLogger<PersonaBindingsContributor>.Instance);

        var session = new Session { OwnerId = _userId, PersonaId = persona.Id };
        var baseCtx = new PromptSessionContext(session, _userId, persona, RootPath: null);

        var wide = await RunBusAsync(contributor, baseCtx);
        wide.Should().Contain("МАРКЕР", "в широкой рамке (личный чат) Always-выжимка личной " +
            "заметки доезжает до промпта, как раньше");
        wide.Should().Contain(Marker, "маркер реально прошёл через BuildTurnBlockAsync — " +
            "фикстура не вакуумная");

        var narrow = await RunBusAsync(contributor,
            baseCtx with { Frame = ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow });
        narrow.Should().NotContain("МАРКЕР",
            "в узкой рамке общего проекта блок привязок не строется вовсе: Always-выжимки " +
            "несут личное содержимое владельца, а текст хода Narrow видит его целиком");
    }

    // Гейт IsEnabled — прямая проверка (тот же образец, что IsEnabled-тест
    // NotesRecallContributor в Notes.Tests): мутация «снять гейт» краснит и этот тест.
    [Fact]
    public void IsEnabled_УзкаяРамкаВыключен_ШирокаяВключён()
    {
        var persona = MakePersonaWithAlwaysNotesBinding();
        var contributor = new PersonaBindingsContributor(
            new PersonaBindingsSourceAdapter(_bindings),
            NullLogger<PersonaBindingsContributor>.Instance);

        var session = new Session { OwnerId = _userId, PersonaId = persona.Id };
        var wide = new PromptSessionContext(session, _userId, persona, RootPath: null);
        var narrow = wide with
        {
            Frame = ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow,
        };

        contributor.IsEnabled(wide).Should().BeTrue("в личном чате привязки включены, как раньше");
        contributor.IsEnabled(narrow).Should().BeFalse(
            "в узкой рамке общего проекта привязки персоны в подборку хода не попадают");
    }

    // Прогон реальной шины с одним контрибьютором: возвращает тексты получившихся секций.
    private static async Task<string> RunBusAsync(
        IPromptSectionContributor contributor, PromptSessionContext ctx)
    {
        var bus = new TurnEventBus(NullLogger<TurnEventBus>.Instance);
        PromptSectionContributorsRegistration.RegisterAll(bus, [contributor]);
        // turnText обязателен: шина отдаёт его контрибьюторам (PromptSectionContributorsRegistration.cs:43),
        // а BuildAsync без текста хода — null по контракту.
        var prompt = new PromptAssembling(new TurnContext("s-1", "o-1", 1, 0), ctx, "напомни про встречу");
        await bus.ApplyAsync(prompt);
        return string.Join("\n", prompt.Sections.Select(s => s.Text));
    }

    // Персона с единственной Always-привязкой личных заметок (Target «personal» —
    // личный vault, всегда валидный источник).
    private Persona MakePersonaWithAlwaysNotesBinding()
    {
        var persona = _personas.Create(_userId, "Хранитель", null, null, null,
            null, null, PersonaScope.Global, null, null, null, true);
        _personas.UpdateBindings(persona.Id, _userId,
            [new PersonaBinding
            {
                Type = PersonaBindingType.Notes,
                Target = "personal",
                Mode = PersonaBindingMode.Always,
            }]);
        return _personas.Get(persona.Id, _userId)!;
    }

    // Заглушка семантического индекса заметок: всегда отдаёт один хит личного источника,
    // чей snippet содержит маркер. Это — граница внешнего сервиса (Dify), не проверяемая
    // здесь логика; весь остальной путь (ExtractNotesAsync → BuildExtractsAsync →
    // BuildTurnBlockAsync → контрибьютор) — реальный.
    private sealed class MarkerNoteIndex : INoteSemanticIndex
    {
        public bool Available => true;
        public string? GetDatasetId(string userId) => "ds-notes";
        public void QueueSync(string userId) { }

        public Task<IReadOnlyList<NoteSemanticHit>> SearchAsync(string userId, string query, int topK = 8) =>
            Task.FromResult<IReadOnlyList<NoteSemanticHit>>([
                new NoteSemanticHit(Id: "note-1", Title: "Личная встреча", Source: "personal",
                    SourceLabel: "Личный", Score: 0.9, Snippet: "фрагмент: " + Marker),
            ]);
    }
}
