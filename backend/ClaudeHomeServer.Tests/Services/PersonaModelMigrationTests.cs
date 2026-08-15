using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Миграция «явная модель персоны → ячейка её рабочего уровня» (15.08.2026): поле «Модель»
// убрано из формы персоны, осталась матрица Сильная/Средняя/Слабая. Persona.Model сильнее
// уровней (ModelAssignmentResolver.PersonaModelDetailed, шаг 1), поэтому оставленное значение
// стало бы невидимой настройкой. Переносим в ячейку уровня, которым персона работает в своём
// чате (chat-persona → Strong), только если ячейка пуста; Model очищается всегда.
public class PersonaModelMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public PersonaModelMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "persona_model_migration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string StorePath => Path.Combine(_tempDir, "personas.json");

    private IConfiguration NewConfig() => TestConfig.Build(new Dictionary<string, string?>
    {
        ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        ["Ollama:Model"] = "",
    });

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private void WriteStore(params Persona[] personas) =>
        File.WriteAllText(StorePath, JsonSerializer.Serialize(personas.ToList(), JsonOpts));

    private static Persona Stored(string id, string ownerId, string? model,
        string? tierStrong = null, ModelTier? tier = null,
        PersonaSpecialty specialty = PersonaSpecialty.None, DateTime? updatedAt = null) => new()
    {
        Id = id,
        OwnerId = ownerId,
        Name = id,
        Handle = id,
        Model = model,
        TierStrong = tierStrong,
        ModelTier = tier,
        Specialty = specialty,
        UpdatedAt = updatedAt ?? DateTime.UtcNow,
    };

    private PersonaManager NewManager(IConfiguration config, UserStore users, AppSettingsService app,
        SpecialtySettingsStore specialty) =>
        new(config, NullLogger<PersonaManager>.Instance,
            userTiers: new UserModelTierResolver(users, app), specialty: specialty);

    [Fact]
    public void Миграция_МодельПереноситсяВЯчейкуУровняЧатаПерсоны()
    {
        var config = NewConfig();
        var app = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var u1 = users.Add("u1", "password123", "user");
        var specialty = new SpecialtySettingsStore(config, NullLogger<SpecialtySettingsStore>.Instance);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { new ModelRoutePreset { Id = "p1", Name = "Каскад", Steps = ["glm-5.2", "deepseek"] } },
        });

        // Ася — обычная модель; Пётр — ссылка на пресет (переносится как есть);
        // Клим — сильная ячейка задана руками (не перетираем, но Model снимаем);
        // Ева — без модели (не трогаем).
        var stamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        WriteStore(
            Stored("asya", u1.Id, "glm-5.2", updatedAt: stamp),
            Stored("petr", u1.Id, "preset:p1", updatedAt: stamp),
            Stored("klim", u1.Id, "glm-5.2", tierStrong: "kimi-k3", updatedAt: stamp),
            Stored("eva", u1.Id, null, updatedAt: stamp));

        var manager = NewManager(config, users, app, specialty);

        var asya = manager.Get("asya", u1.Id)!;
        var petr = manager.Get("petr", u1.Id)!;
        var klim = manager.Get("klim", u1.Id)!;
        var eva = manager.Get("eva", u1.Id)!;

        asya.TierStrong.Should().Be("glm-5.2",
            "дефолтный уровень места «Чат с персоной» — сильный, туда и переносим");
        asya.TierMedium.Should().BeNull("остальные ячейки миграция не трогает");
        asya.TierWeak.Should().BeNull();
        petr.TierStrong.Should().Be("preset:p1", "ссылка на пресет переносится как есть");
        klim.TierStrong.Should().Be("kimi-k3", "заданную руками ячейку не перетираем");
        eva.TierStrong.Should().BeNull();

        foreach (var p in new[] { asya, petr, klim })
            p.Model.Should().BeNull("явная модель снимается — иначе она перебивала бы ячейку");
        foreach (var p in new[] { asya, petr, klim, eva })
            p.UpdatedAt.Should().Be(stamp, "миграция не бампает UpdatedAt — список персон не перетасовывается");

        // Идемпотентность: рестарт на уже мигрированном сторе ничего не меняет
        var restarted = NewManager(config, users, app, specialty);
        restarted.Get("asya", u1.Id)!.TierStrong.Should().Be("glm-5.2");
        restarted.Get("asya", u1.Id)!.Model.Should().BeNull();
        restarted.Get("klim", u1.Id)!.TierStrong.Should().Be("kimi-k3");
    }

    [Fact]
    public void Миграция_УровеньСпециальностиОпределяетЯчейку()
    {
        // Персона специальности, у которой задан уровень «слабый», в чате работает слабым —
        // туда и кладём модель, иначе ячейка не сработала бы.
        var config = NewConfig();
        var app = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var u1 = users.Add("u1", "password123", "user");
        var specialty = new SpecialtySettingsStore(config, NullLogger<SpecialtySettingsStore>.Instance);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings { DefaultTier = ModelTier.Weak } },
        });
        WriteStore(Stored("vera", u1.Id, "glm-5.2", specialty: PersonaSpecialty.BackendExecutor));

        var vera = NewManager(config, users, app, specialty).Get("vera", u1.Id)!;

        vera.TierWeak.Should().Be("glm-5.2");
        vera.TierStrong.Should().BeNull();
        vera.Model.Should().BeNull();
    }

    [Fact]
    public void Миграция_БезЗависимостейРазворота_УровеньПерсоныОпределяетЯчейку()
    {
        // Менеджер без userTiers/specialty (юнит-тестовый путь): миграция уровней не идёт,
        // ModelTier доживает до переноса модели и задаёт ячейку-получателя.
        var config = NewConfig();
        WriteStore(Stored("igor", "u1", "glm-5.2", tier: ModelTier.Weak));

        var igor = new PersonaManager(config, NullLogger<PersonaManager>.Instance).Get("igor", "u1")!;

        igor.TierWeak.Should().Be("glm-5.2");
        igor.Model.Should().BeNull();
    }

    [Fact]
    public void Сторож_ЭффективнаяМодельПослеМиграции_ТаЖеЧтоДоНеё()
    {
        // Персона с явной моделью после загрузки обязана ходить той же моделью, что до
        // миграции: «до» — резолв персоны с заполненным Model, «после» — резолв мигрированной
        // персоны в том же месте (чат персоны, дефолтный уровень Strong).
        var config = NewConfig();
        var app = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var u1 = users.Add("u1", "password123", "user");
        users.SetModelTiers(u1.Id, strong: "user-opus", null, null);
        var specialty = new SpecialtySettingsStore(config, NullLogger<SpecialtySettingsStore>.Instance);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { new ModelRoutePreset { Id = "p1", Name = "Каскад", Steps = ["glm-5.2", "deepseek"] } },
        });
        var resolver = new ModelAssignmentResolver(app, store: null,
            new UserModelTierResolver(users, app), specialty);
        var chatTier = LocalActionCatalog.DefaultTierOf(LocalActionCatalog.ChatPersona);

        var before = resolver.PersonaModel(
            new Persona { OwnerId = u1.Id, Model = "kimi-k3" }, u1.Id, chatTier);
        var beforePreset = resolver.PersonaModel(
            new Persona { OwnerId = u1.Id, Model = "preset:p1" }, u1.Id, chatTier);
        before.Should().Be("kimi-k3");
        beforePreset.Should().Be("glm-5.2", "пресет разворачивается в первый шаг цепочки");

        WriteStore(
            Stored("asya", u1.Id, "kimi-k3"),
            Stored("petr", u1.Id, "preset:p1"));
        var manager = NewManager(config, users, app, specialty);

        resolver.PersonaModel(manager.Get("asya", u1.Id), u1.Id, chatTier).Should().Be(before,
            "перенос в сильную ячейку сохранил модель чата персоны, слот владельца её не перебил");
        resolver.PersonaModel(manager.Get("petr", u1.Id), u1.Id, chatTier).Should().Be(beforePreset);
    }
}
