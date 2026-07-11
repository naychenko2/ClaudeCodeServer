using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Уникальность handle персон: генерация с суффиксом, гонка Create,
// миграция Load() и самолечение дублей
public class PersonaHandleTests : IDisposable
{
    private const string Owner = "owner-1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _tempDir;

    public PersonaHandleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "persona_handle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private PersonaManager NewManager()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();
        return new PersonaManager(config);
    }

    private string StorePath => Path.Combine(_tempDir, "personas.json");

    private Persona Create(PersonaManager mgr, string name) =>
        mgr.Create(Owner, name, role: null, description: null, systemPrompt: null,
            model: null, effort: null, PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: true);

    [Fact]
    public void Create_ОдинаковыеИмена_РазныеHandle()
    {
        var mgr = NewManager();

        var p1 = Create(mgr, "Марк");
        var p2 = Create(mgr, "Марк");

        p1.Handle.Should().Be("mark");
        p2.Handle.Should().Be("mark-2");
    }

    [Fact]
    public async Task Create_Параллельно_ВсеHandleУникальны()
    {
        var mgr = NewManager();

        var created = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => Create(mgr, "Анна"))));

        created.Select(p => p.Handle)
            .Should().OnlyHaveUniqueItems("гонка Create не должна давать дубли handle");
    }

    [Fact]
    public void Load_LegacyБезHandleРаньшеЗанятого_ДубляНет()
    {
        // Персона без handle идёт в файле РАНЬШЕ персоны с сохранённым handle "anna":
        // однопроходная миграция выдала бы первой "anna" и создала дубль
        var legacy = new Persona { OwnerId = Owner, Name = "Анна", Handle = "" };
        var existing = new Persona { OwnerId = Owner, Name = "Анна", Handle = "anna" };
        WriteStore([legacy, existing]);

        var mgr = NewManager();

        var handles = mgr.GetByOwner(Owner).Select(p => p.Handle).ToList();
        handles.Should().OnlyHaveUniqueItems();
        handles.Should().Contain("anna");
    }

    [Fact]
    public void Load_ГотовыйДубль_Самолечение_СтараяСохраняетHandle()
    {
        var older = new Persona
        {
            OwnerId = Owner, Name = "Анна", Handle = "anna",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new Persona
        {
            OwnerId = Owner, Name = "Анна", Handle = "anna",
            CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        WriteStore([newer, older]); // порядок в файле не должен влиять

        var mgr = NewManager();

        var byId = mgr.GetByOwner(Owner).ToDictionary(p => p.Id);
        byId[older.Id].Handle.Should().Be("anna", "handle остаётся у самой старой персоны");
        byId[newer.Id].Handle.Should().NotBe("anna");
        byId[newer.Id].Handle.Should().StartWith("anna-");
    }

    [Fact]
    public void Load_ДублиРазныхВладельцев_НеСчитаютсяКоллизией()
    {
        var mine = new Persona { OwnerId = Owner, Name = "Анна", Handle = "anna" };
        var foreign = new Persona { OwnerId = "owner-2", Name = "Анна", Handle = "anna" };
        WriteStore([mine, foreign]);

        var mgr = NewManager();

        mgr.GetByOwner(Owner).Single().Handle.Should().Be("anna");
        mgr.GetByOwner("owner-2").Single().Handle.Should().Be("anna");
    }

    private void WriteStore(List<Persona> personas)
    {
        File.WriteAllText(StorePath, JsonSerializer.Serialize(personas, JsonOpts));
    }
}
