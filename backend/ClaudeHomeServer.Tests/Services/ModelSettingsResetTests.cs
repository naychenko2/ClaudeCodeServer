using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Сброс настроек моделей к наследованию: слой специальностей (предикат «запись ничего
// своего не несёт», shadowed как состояние слоя, точечный сброс по ключу, идемпотентность)
// и массовый сброс своих уровней персон владельца.
public class ModelSettingsResetTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Other = "owner-2";

    private readonly string _dir;

    public ModelSettingsResetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_model_reset_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

    private SpecialtySettingsStore NewStore() =>
        new(Config(), NullLogger<SpecialtySettingsStore>.Instance);

    private PersonaManager NewPersonas() => new(Config());

    private string StorePath => Path.Combine(_dir, "specialty-settings.json");

    private static SpecialtySettingsLayer Layer(
        Dictionary<string, SpecialtyTemplateSettings>? specialties = null,
        List<ModelRoutePreset>? presets = null,
        SpecialtyTemplateSettings? defaultSpecialty = null) => new()
    {
        Specialties = specialties ?? [],
        Presets = presets ?? [],
        DefaultSpecialty = defaultSpecialty,
    };

    private static SpecialtyTemplateSettings Tmpl(
        PersonaAccess access = PersonaAccess.Full, List<string>? tools = null,
        List<string>? disallowed = null, string? strong = null, string? medium = null,
        string? weak = null, ModelTier? defaultTier = null) => new()
    {
        Access = access,
        Tools = tools,
        DisallowedTools = disallowed,
        TierStrong = strong,
        TierMedium = medium,
        TierWeak = weak,
        DefaultTier = defaultTier,
    };

    // --- Предикат «запись ничего своего не несёт» ---

    [Fact]
    public void Сброс_ПраваКакУНижнегоСлоя_ЗаписьУдаленаВместеСDefaultTier()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            ["analyst"] = Tmpl(PersonaAccess.ReadOnly, tools: ["web"]),
        }));
        store.SetOwner(Owner, Layer(new()
        {
            // Права те же, что у глобальной записи, — своего запись не несёт
            ["analyst"] = Tmpl(PersonaAccess.ReadOnly, tools: ["web"],
                strong: "gpt-5", defaultTier: ModelTier.Weak),
        }));

        var result = store.ResetModelSettings(Owner, key: null, apply: true);

        result.Changed.Should().Be(1);
        result.Shadowed.Should().BeEmpty();
        store.Snapshot.Owners.Should().NotContainKey(Owner, "пустой слой убирается из файла");
        store.Snapshot.Global.Specialties.Should().ContainKey("analyst", "нижний слой не тронут");
    }

    [Fact]
    public void СбросГлобального_НижнейЗаписиНет_КаталожныйДефолтNull_ЗаписьУдалена()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            // У аналитика каталожного шаблона нет: Full без инструментов = «ничего своего»
            ["analyst"] = Tmpl(medium: "gpt-5"),
            // У исполнителя каталожный дефолт есть и он ровно такой же
            ["backendExecutor"] = Tmpl(strong: "opus", defaultTier: ModelTier.Strong),
        }));

        var result = store.ResetModelSettings(ownerId: null, key: null, apply: true);

        result.Changed.Should().Be(2);
        result.Shadowed.Should().BeEmpty();
        store.Snapshot.Global.Specialties.Should().BeEmpty();
    }

    [Fact]
    public void Сброс_ПраваРасходятся_УровниСнятыПраваЦелыКлючВShadowed()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(
            new()
            {
                ["analyst"] = Tmpl(PersonaAccess.Custom, tools: ["web"], disallowed: ["Bash"],
                    strong: "a", medium: "b", weak: "c", defaultTier: ModelTier.Strong),
            },
            presets: [new ModelRoutePreset { Name = "Дешёвый фон", Steps = ["tier:weak"] }]));

        var result = store.ResetModelSettings(Owner, key: null, apply: true);

        result.Changed.Should().Be(1);
        result.Shadowed.Should().Equal("analyst");

        var owner = store.Snapshot.Owners[Owner];
        var rec = owner.Specialties["analyst"];
        rec.Access.Should().Be(PersonaAccess.Custom, "права сохранены");
        rec.Tools.Should().Equal("web");
        rec.DisallowedTools.Should().Equal("Bash");
        rec.TierStrong.Should().BeNull();
        rec.TierMedium.Should().BeNull();
        rec.TierWeak.Should().BeNull();
        rec.DefaultTier.Should().BeNull("DefaultTier снимается вместе с уровнями");

        owner.Presets.Should().ContainSingle().Which.Name.Should().Be("Дешёвый фон");

        using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
        doc.RootElement.GetProperty("Version").GetInt32()
            .Should().Be(SpecialtySettingsStore.FormatVersion);
    }

    [Fact]
    public void Сброс_ПослеУдаленияЗаписи_УровеньСпециальностиИзНижнегоСлоя()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = Tmpl(defaultTier: ModelTier.Strong) }));
        store.SetOwner(Owner, Layer(new() { ["analyst"] = Tmpl(defaultTier: ModelTier.Weak) }));
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.Analyst).Should().Be(ModelTier.Weak);

        store.ResetModelSettings(Owner, key: null, apply: true).Changed.Should().Be(1);

        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.Analyst)
            .Should().Be(ModelTier.Strong, "личная запись удалена — уровень берётся из нижнего слоя");
    }

    [Fact]
    public void Сброс_ОставшаясяРадиПравЗапись_БольшеНеАдресуетУровень()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = Tmpl(defaultTier: ModelTier.Strong) }));
        store.SetOwner(Owner, Layer(new()
        {
            ["analyst"] = Tmpl(PersonaAccess.ReadOnly, defaultTier: ModelTier.Weak),
        }));

        var result = store.ResetModelSettings(Owner, key: null, apply: true);

        result.Shadowed.Should().Equal("analyst");
        // Запись осталась ради прав и по-прежнему затеняет нижний слой целиком,
        // но модель больше не адресует — прежний собственный уровень не возвращается
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.Analyst).Should().BeNull();
    }

    [Fact]
    public void Сброс_ПраваИзМиграцииV1_НенормализованныеСчитаютсяЭквивалентными()
    {
        // Миграция v1 кладёт шаблоны БЕЗ нормализации: полный набор инструментов остаётся
        // списком, тогда как нижний слой хранит его как null
        File.WriteAllText(StorePath, """
        {
          "Version": 2,
          "Global": { "Specialties": {}, "Presets": [] },
          "Owners": {
            "owner-1": {
              "Specialties": {
                "analyst": {
                  "Access": "full",
                  "Tools": ["web", "notes", "tasks"],
                  "TierStrong": "opus",
                  "DefaultTier": "weak"
                }
              },
              "Presets": []
            }
          }
        }
        """);
        var store = NewStore();
        store.Snapshot.Owners[Owner].Specialties["analyst"].Tools.Should().HaveCount(3);

        var result = store.ResetModelSettings(Owner, key: null, apply: true);

        result.Changed.Should().Be(1);
        result.Shadowed.Should().BeEmpty("полный набор инструментов эквивалентен «все возможности»");
        store.Snapshot.Owners.Should().NotContainKey(Owner);
    }

    [Fact]
    public void Сброс_DefaultSpecialty_ТемЖеПредикатом()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(defaultSpecialty: Tmpl(weak: "haiku", defaultTier: ModelTier.Weak)));

        var deleted = store.ResetModelSettings(Owner, key: null, apply: true);
        deleted.Changed.Should().Be(1);
        deleted.Shadowed.Should().BeEmpty();
        store.Snapshot.Owners.Should().NotContainKey(Owner);

        // Своими правами «любая специальность» держится в слое — как обычная запись
        store.SetOwner(Owner, Layer(defaultSpecialty: Tmpl(PersonaAccess.ReadOnly, weak: "haiku")));
        var kept = store.ResetModelSettings(Owner, key: null, apply: true);
        kept.Changed.Should().Be(1);
        kept.Shadowed.Should().Equal(SpecialtyCatalog.AnySpecialtyKey);
        store.Snapshot.Owners[Owner].DefaultSpecialty!.TierWeak.Should().BeNull();
        store.Snapshot.Owners[Owner].DefaultSpecialty!.Access.Should().Be(PersonaAccess.ReadOnly);
    }

    // --- Точечный сброс по ключу ---

    [Fact]
    public void СбросПоКлючу_ТрогаетОднуСтроку_СоседниеЦелы()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(
            new()
            {
                ["analyst"] = Tmpl(strong: "a"),
                ["planner"] = Tmpl(strong: "b"),
            },
            defaultSpecialty: Tmpl(weak: "c")));

        var result = store.ResetModelSettings(Owner, key: "analyst", apply: true);

        result.Changed.Should().Be(1);
        var owner = store.Snapshot.Owners[Owner];
        owner.Specialties.Should().NotContainKey("analyst");
        owner.Specialties["planner"].TierStrong.Should().Be("b");
        owner.DefaultSpecialty!.TierWeak.Should().Be("c");

        // «Любая специальность» сбрасывается своим ключом
        store.ResetModelSettings(Owner, key: SpecialtyCatalog.AnySpecialtyKey, apply: true)
            .Changed.Should().Be(1);
        store.Snapshot.Owners[Owner].DefaultSpecialty.Should().BeNull();
        store.Snapshot.Owners[Owner].Specialties["planner"].TierStrong.Should().Be("b");
    }

    // --- Предпросмотр и идемпотентность ---

    [Fact]
    public void Предпросмотр_СчитаетТакЖе_НичегоНеМеняет_ПовторныйСбросИдемпотентен()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(new()
        {
            ["analyst"] = Tmpl(strong: "a"),
            ["planner"] = Tmpl(PersonaAccess.ReadOnly, medium: "b"),
        }));
        var before = File.ReadAllText(StorePath);

        var preview = store.ResetModelSettings(Owner, key: null, apply: false);
        preview.Changed.Should().Be(2);
        preview.Shadowed.Should().Equal("planner");
        File.ReadAllText(StorePath).Should().Be(before, "предпросмотр ничего не пишет");
        store.Snapshot.Owners[Owner].Specialties["analyst"].TierStrong.Should().Be("a");

        var applied = store.ResetModelSettings(Owner, key: null, apply: true);
        applied.Changed.Should().Be(preview.Changed);
        applied.Shadowed.Should().Equal(preview.Shadowed);

        var afterFirst = File.ReadAllText(StorePath);
        var again = store.ResetModelSettings(Owner, key: null, apply: true);
        again.Changed.Should().Be(0, "сбрасывать больше нечего");
        // shadowed — состояние слоя, а не дельта вызова
        again.Shadowed.Should().Equal(["planner"]);
        File.ReadAllText(StorePath).Should().Be(afterFirst, "повторный сброс ничего не пишет");
    }

    // --- Персоны ---

    [Fact]
    public void СбросУровнейПерсон_ТолькоСвои_БезModelИБезБампаUpdatedAt()
    {
        var personas = NewPersonas();
        var changedEvents = new List<string>();
        personas.OnPersonaChanged += p => changedEvents.Add(p.Id);

        var mine = Create(personas, Owner, "Денис", strong: "opus", weak: "haiku");
        var plain = Create(personas, Owner, "Кира");
        var alien = Create(personas, Other, "Чужой", strong: "opus");

        var mineUpdatedAt = mine.UpdatedAt;
        var mineModel = mine.Model;

        personas.ResetTierMatrices(Owner, apply: false).Should().ContainSingle()
            .Which.Id.Should().Be(mine.Id);
        mine.TierStrong.Should().Be("opus", "предпросмотр ничего не меняет");

        var touched = personas.ResetTierMatrices(Owner, apply: true);

        touched.Select(p => p.Id).Should().Equal(mine.Id);
        mine.TierStrong.Should().BeNull();
        mine.TierMedium.Should().BeNull();
        mine.TierWeak.Should().BeNull();
        mine.Model.Should().Be(mineModel, "явная модель — другая ось настройки");
        mine.ModelTier.Should().Be(ModelTier.Strong);
        mine.UpdatedAt.Should().Be(mineUpdatedAt, "по UpdatedAt сортируется список персон");
        plain.TierStrong.Should().BeNull();
        alien.TierStrong.Should().Be("opus", "чужие персоны не тронуты");
        // события — только по реально изменённым
        changedEvents.Should().Equal([mine.Id]);

        personas.ResetTierMatrices(Owner, apply: true).Should().BeEmpty("повторный сброс идемпотентен");
    }

    private static Persona Create(PersonaManager mgr, string ownerId, string name,
        string? strong = null, string? weak = null) =>
        mgr.Create(ownerId, name, role: null, description: null, systemPrompt: null,
            model: "claude-sonnet-4-5", effort: null, PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: true, modelTier: "strong",
            tierStrong: strong, tierWeak: weak);
}
