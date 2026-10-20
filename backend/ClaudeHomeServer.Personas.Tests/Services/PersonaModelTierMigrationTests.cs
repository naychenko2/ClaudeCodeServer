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

// Миграция «уровень персоны → средняя ячейка» (упрощение модели 15.08.2026, ADR-007 §2):
// при загрузке стора заданный Persona.ModelTier разворачивается по действующей цепочке
// (матрица персоны → специальность → слоты владельца/инстанса) и фиксируется в TierMedium,
// поле очищается. Сторож эквивалентности: эффективная модель персоны после миграции
// совпадает с той, что давал разворот её уровня до неё.
public class PersonaModelTierMigrationTests : IDisposable
{
    private readonly string _tempDir;

    public PersonaModelTierMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "persona_tier_migration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private string StorePath => Path.Combine(_tempDir, "personas.json");

    // Конфиг с общим DataPath: appsettings/users/specialty-settings/personas живут
    // в одной временной папке и переживают пересоздание менеджера (рестарт).
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

    private static Persona Stored(string id, string ownerId, ModelTier? tier,
        PersonaSpecialty specialty = PersonaSpecialty.None, DateTime? updatedAt = null) => new()
    {
        Id = id,
        OwnerId = ownerId,
        Name = id,
        Handle = id,
        ModelTier = tier,
        Specialty = specialty,
        UpdatedAt = updatedAt ?? DateTime.UtcNow,
    };

    [Fact]
    public void Миграция_УровеньФиксируетсяВСреднейЯчейкеСсылкойКакЕсть()
    {
        var config = NewConfig();
        var app = new AppSettingsService(config);
        app.Save(new AppSettings { ModelTierWeak = "instance-haiku" });
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var u1 = users.Add("u1", "password123", "user");
        users.SetModelTiers(u1.Id, strong: "user-opus", null, null);
        var specialty = ClaudeHomeServer.Tests.Helpers.TestSpecialtyStore.Create(config);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { new ModelRoutePreset { Id = "p1", Name = "Каскад", Steps = ["glm-5.2", "deepseek"] } },
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings { TierStrong = "preset:p1" } },
        });

        // Виктор — strong без специальности (разворот = слот владельца); Вера — strong со
        // специальностью (разворот = preset-ссылка из strong-ячейки специальности, переносим
        // КАК ЕСТЬ); Игорь — medium без всего (разворот пуст); Лев — без уровня.
        var stamp = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        WriteStore(
            Stored("viktor", u1.Id, ModelTier.Strong, updatedAt: stamp),
            Stored("vera", u1.Id, ModelTier.Strong, PersonaSpecialty.BackendExecutor, stamp),
            Stored("igor", u1.Id, ModelTier.Medium, updatedAt: stamp),
            Stored("lev", u1.Id, null, updatedAt: stamp));

        var manager = new PersonaManager(config, NullLogger<PersonaManager>.Instance,
            userTiers: new UserModelTierResolver(users, app), specialty: specialty);

        var viktor = manager.Get("viktor", u1.Id)!;
        var vera = manager.Get("vera", u1.Id)!;
        var igor = manager.Get("igor", u1.Id)!;
        var lev = manager.Get("lev", u1.Id)!;

        viktor.TierMedium.Should().Be("user-opus",
            "уровень strong без матриц разворачивается в личный слот владельца");
        vera.TierMedium.Should().Be("preset:p1",
            "ссылка на пресет переносится как есть, а не развёрнутой моделью");
        igor.TierMedium.Should().BeNull("пустой разворот (ни матриц, ни слотов) — ячейку не пишем");
        lev.TierMedium.Should().BeNull();
        foreach (var p in new[] { viktor, vera, igor, lev })
            p.ModelTier.Should().BeNull("поле уровня очищается, в том числе у нетронутых");
        foreach (var p in new[] { viktor, vera, igor, lev })
            p.UpdatedAt.Should().Be(stamp, "миграция не бампает UpdatedAt — список персон не перетасовывается");

        // Идемпотентность: рестарт на уже мигрированном сторе ничего не меняет
        var restarted = new PersonaManager(config, NullLogger<PersonaManager>.Instance,
            userTiers: new UserModelTierResolver(users, app), specialty: specialty);
        restarted.Get("viktor", u1.Id)!.TierMedium.Should().Be("user-opus");
        restarted.Get("viktor", u1.Id)!.ModelTier.Should().BeNull();
    }

    [Fact]
    public void Сторож_ЭффективнаяМодельПослеМиграции_ТаЖеЧтоДоНеё()
    {
        // Персона со strong после загрузки обязана ходить той же моделью, что до миграции.
        // «До» — старая формула (разворот уровня персоны по цепочке матриц), ожидания
        // выписаны литералами: слот владельца → «user-opus», strong-ячейка специальности с
        // пресетом → первый шаг цепочки «glm-5.2». «После» — PersonaModel мигрированной
        // персоны: в месте со средним уровнем (персона после миграции работает средним),
        // а для пруда-паттерна (пустые матрицы) — и в сильном месте чата персоны.
        var config = NewConfig();
        var app = new AppSettingsService(config);
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var u1 = users.Add("u1", "password123", "user");
        var specialty = ClaudeHomeServer.Tests.Helpers.TestSpecialtyStore.Create(config);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { new ModelRoutePreset { Id = "p1", Name = "Каскад", Steps = ["glm-5.2", "deepseek"] } },
        });
        var resolver = new ModelAssignmentResolver(app, store: null,
            new UserModelTierResolver(users, app), specialty);

        // Сценарий 1 — прод-паттерн: матрицы пусты, значение приходит из слота владельца
        users.SetModelTiers(u1.Id, strong: "user-opus", null, null);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings() },
        });
        WriteStore(Stored("viktor", u1.Id, ModelTier.Strong));
        var viktor = new PersonaManager(config, NullLogger<PersonaManager>.Instance,
            userTiers: new UserModelTierResolver(users, app), specialty: specialty)
            .Get("viktor", u1.Id)!;

        resolver.PersonaModel(viktor, u1.Id, ModelTier.Medium).Should().Be("user-opus",
            "в среднем месте — записанная ячейка, тот же разворот сильного уровня");
        resolver.PersonaModel(viktor, u1.Id, ModelTier.Strong).Should().Be("user-opus",
            "в сильном месте (чат персоны) — пустая сильная ячейка падает на тот же слот владельца");

        // Сценарий 2 — значение из strong-ячейки специальности со ссылкой на пресет
        users.SetModelTiers(u1.Id, strong: null, null, null);
        specialty.SetGlobal(new SpecialtySettingsLayer
        {
            Presets = { new ModelRoutePreset { Id = "p1", Name = "Каскад", Steps = ["glm-5.2", "deepseek"] } },
            Specialties = { ["backendExecutor"] = new SpecialtyTemplateSettings { TierStrong = "preset:p1" } },
        });
        WriteStore(Stored("vera", u1.Id, ModelTier.Strong, PersonaSpecialty.BackendExecutor));
        var vera = new PersonaManager(config, NullLogger<PersonaManager>.Instance,
            userTiers: new UserModelTierResolver(users, app), specialty: specialty)
            .Get("vera", u1.Id)!;

        resolver.PersonaModel(vera, u1.Id, ModelTier.Medium).Should().Be("glm-5.2",
            "записанная ссылка preset:p1 разворачивается в первый шаг — тот же, что давал уровень strong");
    }
}
