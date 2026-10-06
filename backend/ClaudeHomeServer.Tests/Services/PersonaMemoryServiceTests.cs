using ClaudeHomeServer.Core.Telemetry;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Долгая память персоны: salience в Remember, рабочий фокус, recall с фокусом,
// reinforcement попавших в блок записей, дедуп. Dify не настроен → полнотекст-fallback.
public class PersonaMemoryServiceTests : IDisposable
{
    private const string OwnerId = "owner-1";

    private readonly string _tempDir;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _sut;
    private readonly Persona _persona;

    public PersonaMemoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmem_tests_" + Guid.NewGuid().ToString("N"));
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
        _sut = new PersonaMemoryService(knowledge, _personas, _personas,
            new PersonaDirectoryAdapter(_personas), new NoopPersonaEvents(),
            new NoopDifyMetrics(), userStore, config,
            NullLogger<PersonaMemoryService>.Instance);

        _persona = _personas.Create(OwnerId, "Ада", "Аналитик", null, null,
            null, null, PersonaScope.Global, null, null, null, memoryEnabled: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // --- Remember с salience ---

    [Fact]
    public void Remember_БезSalience_Единица()
    {
        var entry = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Факт", null, null);

        entry.Should().NotBeNull();
        entry!.Salience.Should().Be(1.0);
    }

    [Fact]
    public void Remember_SalienceКлампитсяВДиапазон()
    {
        var high = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Важное", null, null, salience: 5.0);
        var low = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Мелочь", null, null, salience: 0.001);
        var mid = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Среднее", null, null, salience: 0.7);

        high!.Salience.Should().Be(1.0);
        low!.Salience.Should().Be(0.05);
        mid!.Salience.Should().Be(0.7);
    }

    [Fact]
    public void Remember_Дедуп_ОдинаковыйТекст_Null()
    {
        _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Пользователь любит кофе", null, null)
            .Should().NotBeNull();

        // Тот же тип + тот же текст (без учёта регистра) — не плодим дубликат
        _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "пользователь любит КОФЕ", null, null)
            .Should().BeNull();
    }

    // --- Рабочий фокус (P3) ---

    [Fact]
    public void Focus_SetGetClear()
    {
        _sut.GetFocus(OwnerId, _persona.Id).Should().BeNull();

        var focus = _sut.SetFocus(OwnerId, _persona.Id, "Ревью PR #42", "в процессе", "проверить тесты", "sess-1");
        focus.Should().NotBeNull();

        var got = _sut.GetFocus(OwnerId, _persona.Id);
        got.Should().NotBeNull();
        got!.What.Should().Be("Ревью PR #42");
        got.Status.Should().Be("в процессе");
        got.NextStep.Should().Be("проверить тесты");
        got.SourceSessionId.Should().Be("sess-1");

        _sut.ClearFocus(OwnerId, _persona.Id).Should().BeTrue();
        _sut.GetFocus(OwnerId, _persona.Id).Should().BeNull();
        _sut.ClearFocus(OwnerId, _persona.Id).Should().BeFalse();
    }

    [Fact]
    public void Focus_ПустоеWhat_НеСтавится()
    {
        _sut.SetFocus(OwnerId, _persona.Id, "  ", "статус", null, null).Should().BeNull();
        _sut.GetFocus(OwnerId, _persona.Id).Should().BeNull();
    }

    [Fact]
    public void Focus_ЧужойВладелец_Недоступен()
    {
        _sut.SetFocus(OwnerId, _persona.Id, "Дело", "в работе", null, null);

        _sut.GetFocus("другой-владелец", _persona.Id).Should().BeNull();
    }

    // --- Recall: фокус всегда первым блоком ---

    [Fact]
    public async Task Recall_СФокусомБезХитов_НеNull()
    {
        _sut.SetFocus(OwnerId, _persona.Id, "Подготовка отчёта", "в процессе", "собрать цифры", null);

        // Память пуста — хитов нет, но фокус делает recall непустым
        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "совсем другой запрос", topK: 5, minScore: 0.30);

        recall.Should().NotBeNull();
        recall!.Text.Should().Contain("Твоё текущее дело (рабочая память): Подготовка отчёта.");
        recall.Text.Should().Contain("Статус: в процессе.");
        recall.Text.Should().Contain("Следующий шаг: собрать цифры.");
    }

    [Fact]
    public async Task Recall_БезФокусаИБезХитов_ПустойText()
    {
        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "запрос", topK: 5, minScore: 0.30);

        recall.Should().NotBeNull();
        recall!.Text.Should().BeNull();
        recall.Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task Recall_ФокусПередЗаписямиПамяти()
    {
        _sut.SetFocus(OwnerId, _persona.Id, "Ревью", "идёт", null, null);
        _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Пользователь любит краткие ответы", null, null);

        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "пользователь ответы краткие", topK: 5, minScore: 0.1);

        recall.Should().NotBeNull();
        recall!.Text.Should().NotBeNull();
        var focusIdx = recall.Text!.IndexOf("Твоё текущее дело", StringComparison.Ordinal);
        var memIdx = recall.Text.IndexOf("долгой памяти", StringComparison.Ordinal);
        focusIdx.Should().BeGreaterThanOrEqualTo(0);
        memIdx.Should().BeGreaterThan(focusIdx);
    }

    // --- Reinforcement: только записи, попавшие в блок ---

    [Fact]
    public async Task Recall_Reinforcement_ТолькоУПопавшихВБлок()
    {
        var hitEntry = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic,
            "Кошка пользователя любит играть", null, null)!;
        var missEntry = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic,
            "Собака соседа гуляет в парке", null, null)!;

        var missBefore = missEntry.LastAccessedAt;
        await Task.Delay(30);   // чтобы Touch дал заметно более позднюю метку

        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "кошка любит играть", topK: 5, minScore: 0.1);
        recall!.Text.Should().Contain("Кошка");
        recall.Text.Should().NotContain("Собака");
        recall.Hits.Should().ContainSingle(h => h.Id == hitEntry.Id);

        var entries = _sut.List(OwnerId, _persona.Id, null);
        entries.Single(e => e.Id == hitEntry.Id).LastAccessedAt
            .Should().BeAfter(hitEntry.CreatedAt);
        entries.Single(e => e.Id == missEntry.Id).LastAccessedAt
            .Should().Be(missBefore);
    }

    [Fact]
    public async Task Recall_ПамятьВыключена_Null()
    {
        _personas.Update(_persona.Id, OwnerId, null, null, null, null, null, null, null, null,
            null, null, memoryEnabled: false);
        _sut.SetFocus(OwnerId, _persona.Id, "Дело", "идёт", null, null);

        var recall = await _sut.BuildRecallAsync(OwnerId, _persona.Id, "запрос", topK: 5, minScore: 0.1);

        recall.Should().BeNull();
    }

    // --- ApplyConsolidation ---

    [Fact]
    public void ApplyConsolidation_MergeИDrop()
    {
        var a = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Любит кофе", null, null)!;
        var b = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Semantic, "Пьёт кофе по утрам", null, null)!;
        var c = _sut.Remember(OwnerId, _persona.Id, PersonaMemoryType.Episodic, "Обсуждали погоду", null, null)!;

        var ops = new List<MemoryConsolidationOp>
        {
            new("merge", [a.Id, b.Id], null, PersonaMemoryType.Semantic, "Любит кофе, пьёт по утрам", 0.9),
            new("drop", null, c.Id, null, null, null),
        };

        var affected = _sut.ApplyConsolidation(OwnerId, _persona.Id, ops);

        affected.Should().Be(3);
        var entries = _sut.List(OwnerId, _persona.Id, null);
        entries.Should().HaveCount(1);
        entries[0].Text.Should().Be("Любит кофе, пьёт по утрам");
        entries[0].Type.Should().Be(PersonaMemoryType.Semantic);
        entries[0].Salience.Should().Be(0.9);
    }
}

// Recall полки сферы: ключ — только проект ЧАТА (не persona.SphereId), глобальные персоны тоже
// видят полку сферы проекта, вне проекта сферы полки нет.
public class PersonaMemoryServiceSphereRecallTests : IDisposable
{
    private const string OwnerId = "owner-1";

    private sealed class FakeDir : ClaudeHomeServer.Services.Spheres.ISphereDirectory
    {
        public Dictionary<string, List<string>> Spheres = new();
        public bool Enabled(string ownerId) => true;
        public string? SphereOf(string ownerId, string projectId) =>
            Spheres.FirstOrDefault(s => s.Value.Contains(projectId)).Key;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) =>
            Spheres.TryGetValue(sphereId, out var l) ? l.ToList() : [];
        public string? SphereName(string ownerId, string sphereId) => "Сфера " + sphereId;
        public string? CharterOf(string ownerId, string sphereId) => null;
    }

    private readonly string _tempDir;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _sut;
    private readonly SphereMemoryService _sphereMemory;
    private readonly FakeDir _dir = new();

    public PersonaMemoryServiceSphereRecallTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmem_sphere_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), new WorkspaceKnowledgeStore(config));
        _personas = new PersonaManager(config);
        _sphereMemory = new SphereMemoryService(config);
        _sut = new PersonaMemoryService(knowledge, _personas, _personas,
            new PersonaDirectoryAdapter(_personas), new NoopPersonaEvents(),
            new NoopDifyMetrics(), userStore, config, NullLogger<PersonaMemoryService>.Instance,
            sphereMemory: _sphereMemory, spheres: _dir);

        _dir.Spheres["S1"] = ["p1"];
        _dir.Spheres["S2"] = ["p2"];
        _sphereMemory.Add(OwnerId, "S1", "релизы сферы первой идут по четвергам");
        _sphereMemory.Add(OwnerId, "S2", "релизы сферы второй идут по пятницам");
    }

    public void Dispose()
    {
        _sphereMemory.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private Persona Make(PersonaScope scope, string? sphereId = null)
    {
        var p = _personas.Create(OwnerId, "Ада" + Guid.NewGuid().ToString("N")[..4], "Аналитик", null, null,
            null, null, PersonaScope.Global, null, null, null, memoryEnabled: true);
        p.Scope = scope;
        p.SphereId = sphereId;
        return p;
    }

    [Fact]
    public async Task Recall_ПерсонаСферыS1ВПроектеСферыS2_ВспоминаетТолькоS2()
    {
        var persona = Make(PersonaScope.Sphere, "S1");

        var recall = await _sut.BuildRecallAsync(OwnerId, persona.Id, "релизы идут", 5, 0.1,
            sessionProjectId: "p2");

        recall!.Text.Should().Contain("«Сфера S2»").And.Contain("по пятницам");
        recall.Text.Should().NotContain("по четвергам", "полка S1 не принадлежит проекту чата");
        recall.SphereHits.Should().ContainSingle();
    }

    [Fact]
    public async Task Recall_ГлобальнаяПерсонаВПроектеСферы_ВидитПолкуСферы()
    {
        var persona = Make(PersonaScope.Global);

        var recall = await _sut.BuildRecallAsync(OwnerId, persona.Id, "релизы идут", 5, 0.1,
            sessionProjectId: "p1");

        recall!.Text.Should().Contain("## Память сферы «Сфера S1»").And.Contain("по четвергам");
    }

    [Fact]
    public async Task Recall_ПроектВнеСферыИЧатВнеПроекта_ПолкиСферыНет()
    {
        var persona = Make(PersonaScope.Global);

        var outside = await _sut.BuildRecallAsync(OwnerId, persona.Id, "релизы идут", 5, 0.1,
            sessionProjectId: "p-вне-сфер");
        var noProject = await _sut.BuildRecallAsync(OwnerId, persona.Id, "релизы идут", 5, 0.1);

        outside!.Text.Should().BeNull();
        noProject!.Text.Should().BeNull();
    }
}
