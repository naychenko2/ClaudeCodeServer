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
        CreateGlobal(mgr, name);

    private Persona CreateGlobal(PersonaManager mgr, string name, string? role = null, string? handle = null) =>
        mgr.Create(Owner, name, role, description: null, systemPrompt: null,
            model: null, effort: null, PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: true, handle: handle);

    private Persona CreateProject(PersonaManager mgr, string name, string projectId,
        string? role = null, string? handle = null) =>
        mgr.Create(Owner, name, role, description: null, systemPrompt: null,
            model: null, effort: null, PersonaScope.Project, projectId,
            color: null, greeting: null, memoryEnabled: true, handle: handle);

    private static Persona Stored(string name, string handle, PersonaScope scope, string? projectId,
        DateTime createdAt, bool handleCustom = false, string owner = Owner) => new()
    {
        OwnerId = owner, Name = name, Handle = handle, Scope = scope, ProjectId = projectId,
        CreatedAt = createdAt, HandleCustom = handleCustom,
    };

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

    // --- Контекстная уникальность ---

    [Fact]
    public void Create_ПроектныеРазныхПроектов_ОдинаковыйHandle()
    {
        var mgr = NewManager();

        var a = CreateProject(mgr, "Маша", "proj-A");
        var b = CreateProject(mgr, "Маша", "proj-B");

        a.Handle.Should().Be("masha");
        b.Handle.Should().Be("masha", "проектные персоны разных проектов не делят контекст");
    }

    [Fact]
    public void Create_ГлобальнаяИПроектная_ВОдномКонтексте_Суффикс()
    {
        var mgr = NewManager();

        var g = CreateGlobal(mgr, "Маша");
        var p = CreateProject(mgr, "Маша", "proj-A");

        g.Handle.Should().Be("masha");
        p.Handle.Should().Be("masha-2", "глобальная видна в проекте — handle конфликтует");
    }

    [Fact]
    public void Create_ПроектныеОдногоПроекта_Суффикс()
    {
        var mgr = NewManager();

        var a = CreateProject(mgr, "Маша", "proj-A");
        var b = CreateProject(mgr, "Маша", "proj-A");

        a.Handle.Should().Be("masha");
        b.Handle.Should().Be("masha-2");
    }

    [Fact]
    public void Create_FallbackИзРоли_ВместоЦифры()
    {
        var mgr = NewManager();

        var a = CreateGlobal(mgr, "Маша", role: "Аналитик");
        var b = CreateGlobal(mgr, "Маша", role: "Дизайнер");

        a.Handle.Should().Be("masha", "база свободна — роль не нужна");
        b.Handle.Should().Be("masha-dizayner", "коллизия закрывается ролью, а не цифрой");
    }

    // --- Ручной handle ---

    [Fact]
    public void Create_РучнойHandle_Валидный_Принят()
    {
        var mgr = NewManager();

        var p = CreateGlobal(mgr, "Маша", handle: "masha-shop");

        p.Handle.Should().Be("masha-shop");
        p.HandleCustom.Should().BeTrue();
    }

    [Fact]
    public void Create_РучнойHandle_Занятый_Ошибка()
    {
        var mgr = NewManager();
        CreateGlobal(mgr, "Маша"); // masha

        var act = () => CreateGlobal(mgr, "Маша вторая", handle: "masha");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_РучнойHandle_Reserved_Ошибка()
    {
        var mgr = NewManager();

        var act = () => CreateGlobal(mgr, "План", handle: "plan");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_РучнойHandle_СвободенВДругомПроекте()
    {
        var mgr = NewManager();

        var a = CreateProject(mgr, "Маша", "proj-A", handle: "boss");
        var b = CreateProject(mgr, "Маша", "proj-B", handle: "boss");

        a.Handle.Should().Be("boss");
        b.Handle.Should().Be("boss", "тот же handle свободен в другом проекте");
    }

    [Fact]
    public void Update_СменаHandle_ПоднимаетСобытиеСоСтарымHandle()
    {
        var mgr = NewManager();
        var p = CreateGlobal(mgr, "Маша"); // masha
        (Persona Persona, string Old)? fired = null;
        mgr.OnPersonaHandleChanged += (per, oldH) => fired = (per, oldH);

        mgr.Update(p.Id, Owner, name: null, role: null, description: null, systemPrompt: null,
            model: null, effort: null, scope: null, projectId: null, color: null, greeting: null,
            memoryEnabled: null, handle: "boss");

        fired.Should().NotBeNull();
        fired!.Value.Old.Should().Be("masha");
        fired!.Value.Persona.Handle.Should().Be("boss");
        mgr.Get(p.Id, Owner)!.HandleCustom.Should().BeTrue();
    }

    // --- Контекстный резолвинг GetByHandle ---

    [Fact]
    public void GetByHandle_Контекстный_РазныеПроекты()
    {
        var mgr = NewManager();
        var a = CreateProject(mgr, "Маша", "proj-A");
        var b = CreateProject(mgr, "Маша", "proj-B");

        mgr.GetByHandle(Owner, "masha", "proj-A")!.Id.Should().Be(a.Id);
        mgr.GetByHandle(Owner, "masha", "proj-B")!.Id.Should().Be(b.Id);
    }

    [Fact]
    public void GetByHandle_Контекстный_ПроектнаяПриоритетнейГлобальной()
    {
        // Кастомные дубли (Load их не лечит) — в контексте проекта выигрывает проектная
        var g = Stored("Маша", "boss", PersonaScope.Global, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), handleCustom: true);
        var p = Stored("Маша", "boss", PersonaScope.Project, "proj-A",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), handleCustom: true);
        WriteStore([g, p]);
        var mgr = NewManager();

        mgr.GetByHandle(Owner, "boss", "proj-A")!.Id.Should().Be(p.Id);
        mgr.GetByHandle(Owner, "boss", null)!.Id.Should().Be(g.Id, "вне проекта — только глобальная");
    }

    // --- Миграция контекстных handle ---

    [Fact]
    public void Migrate_ДеСуффиксация_CrossProject()
    {
        // Старое per-owner правило дало masha / masha-2 в РАЗНЫХ проектах — суффикс лишний
        var a = Stored("Маша", "masha", PersonaScope.Project, "proj-A",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var b = Stored("Маша", "masha-2", PersonaScope.Project, "proj-B",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteStore([a, b]);
        var mgr = NewManager();

        var renamed = mgr.MigrateContextualHandles();

        mgr.Get(a.Id, Owner)!.Handle.Should().Be("masha");
        mgr.Get(b.Id, Owner)!.Handle.Should().Be("masha", "контексты не пересекаются — суффикс схлопнут");
        renamed.Should().ContainSingle(r => r.Persona.Id == b.Id && r.OldHandle == "masha-2");
    }

    [Fact]
    public void Migrate_РеальныйКонфликт_СуффиксОстаётся()
    {
        var g = Stored("Маша", "masha", PersonaScope.Global, null,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var p = Stored("Маша", "masha-2", PersonaScope.Project, "proj-A",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteStore([g, p]);
        var mgr = NewManager();

        var renamed = mgr.MigrateContextualHandles();

        mgr.Get(p.Id, Owner)!.Handle.Should().Be("masha-2", "глобальная masha видна в проекте — суффикс нужен");
        renamed.Should().BeEmpty();
    }

    [Fact]
    public void Migrate_Идемпотентна()
    {
        var a = Stored("Маша", "masha-2", PersonaScope.Project, "proj-B",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteStore([a]);
        var mgr = NewManager();

        mgr.MigrateContextualHandles();          // masha-2 → masha
        var second = mgr.MigrateContextualHandles();

        second.Should().BeEmpty("повторный прогон ничего не меняет");
        mgr.Get(a.Id, Owner)!.Handle.Should().Be("masha");
    }

    [Fact]
    public void Migrate_РучнойHandle_НеТрогается()
    {
        var a = Stored("Маша", "masha-2", PersonaScope.Project, "proj-B",
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), handleCustom: true);
        WriteStore([a]);
        var mgr = NewManager();

        var renamed = mgr.MigrateContextualHandles();

        renamed.Should().BeEmpty();
        mgr.Get(a.Id, Owner)!.Handle.Should().Be("masha-2", "кастомный handle миграция не трогает");
    }

    private void WriteStore(List<Persona> personas)
    {
        File.WriteAllText(StorePath, JsonSerializer.Serialize(personas, JsonOpts));
    }
}
