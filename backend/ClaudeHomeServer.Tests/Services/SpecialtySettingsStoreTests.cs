using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Стор настроек специальностей и пресетов правил: слои (глобальный + per-owner),
// эффективные шаблоны, пресеты и маршруты, валидация, персистентность, версия формата.
public class SpecialtySettingsStoreTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Other = "owner-2";

    private readonly string _dir;

    public SpecialtySettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_specialty_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SpecialtySettingsStore NewStore() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            }).Build(),
        NullLogger<SpecialtySettingsStore>.Instance);

    private static SpecialtySettingsLayer Layer(
        Dictionary<string, SpecialtyTemplateSettings>? specialties = null,
        List<ModelRoutePreset>? presets = null) => new()
    {
        Specialties = specialties ?? [],
        Presets = presets ?? [],
    };

    // --- Эффективный шаблон ---

    [Fact]
    public void EffectiveTemplate_ПустойСтор_ДефолтКода()
    {
        var store = NewStore();

        var template = store.EffectiveTemplate(Owner, PersonaSpecialty.BackendExecutor);
        template.Should().NotBeNull();
        template!.Access.Should().Be(PersonaAccess.Full);
        template.Tools.Should().BeNull();

        store.EffectiveTemplate(Owner, PersonaSpecialty.Analyst).Should().BeNull();
    }

    [Fact]
    public void EffectiveTemplate_ГлобальнаяНастройка_СильнееДефолта()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            ["backendExecutor"] = new() { Access = PersonaAccess.ReadOnly, Tools = ["web"] },
        })).Should().BeNull();

        var template = store.EffectiveTemplate(Owner, PersonaSpecialty.BackendExecutor)!;
        template.Access.Should().Be(PersonaAccess.ReadOnly);
        template.Tools.Should().BeEquivalentTo("web");

        // Другие владельцы видят те же глобальные значения
        store.EffectiveTemplate(Other, PersonaSpecialty.BackendExecutor)!.Access
            .Should().Be(PersonaAccess.ReadOnly);
    }

    [Fact]
    public void EffectiveTemplate_ЛичнаяНастройка_СильнееГлобальной()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            ["backendExecutor"] = new() { Access = PersonaAccess.ReadOnly },
        }));
        store.SetOwner(Owner, Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            ["backendExecutor"] = new() { Access = PersonaAccess.Custom, DisallowedTools = ["Bash"] },
        }));

        var own = store.EffectiveTemplate(Owner, PersonaSpecialty.BackendExecutor)!;
        own.Access.Should().Be(PersonaAccess.Custom);
        own.DisallowedTools.Should().BeEquivalentTo("Bash");

        // Чужого владельца личное переопределение не касается
        store.EffectiveTemplate(Other, PersonaSpecialty.BackendExecutor)!.Access
            .Should().Be(PersonaAccess.ReadOnly);
    }

    [Fact]
    public void SetOwner_ПустойСлой_СнимаетПереопределения()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            ["executor"] = new() { Access = PersonaAccess.ReadOnly },
        }));
        store.SetOwner(Owner, Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            ["executor"] = new() { Access = PersonaAccess.Full },
        }));

        store.SetOwner(Owner, Layer()).Should().BeNull();

        store.EffectiveTemplate(Owner, PersonaSpecialty.Executor)!.Access
            .Should().Be(PersonaAccess.ReadOnly, "личный слой снят — остался глобальный");
        store.Snapshot.Owners.Should().NotContainKey(Owner);
    }

    // --- Пресеты правил ---

    private static ModelRoutePreset Preset(string name, params (string Specialty, string Route)[] rules) => new()
    {
        Name = name,
        Rules = rules.Select(r => new ModelRouteRule { Specialty = r.Specialty, Route = r.Route }).ToList(),
    };

    [Fact]
    public void EffectivePresets_ЛичныеРядомСГлобальными_БезПереопределенияПоId()
    {
        var store = NewStore();
        var globalId = Guid.NewGuid().ToString();
        store.SetGlobal(Layer(presets:
        [
            new ModelRoutePreset { Id = globalId, Name = "Глобальный", Rules = [new() { Route = "tier:weak" }] },
            Preset("Ещё глобальный", ("any", "claude")),
        ]));
        store.SetOwner(Owner, Layer(presets:
        [
            new ModelRoutePreset { Id = globalId, Name = "Личная версия", Rules = [new() { Route = "tier:strong" }] },
        ]));

        // Личный пресет с id глобального больше не затирает его — оба набора живут рядом,
        // личный блок идёт первым (порядок резолва)
        var effective = store.EffectivePresets(Owner);
        effective.Should().HaveCount(3);
        effective[0].Name.Should().Be("Личная версия");
        effective[1].Name.Should().Be("Глобальный");
        effective[2].Name.Should().Be("Ещё глобальный");

        // У другого владельца состав прежний
        store.EffectivePresets(Other).Should().HaveCount(2);
        store.EffectivePresets(Other).Single(p => p.Id == globalId).Name.Should().Be("Глобальный");
    }

    [Fact]
    public void EffectivePresets_ТоЖеИмяЧтоУГлобального_ОбаОстаютсяВНаборе()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("Дешёвый фон", ("any", "tier:weak"))]));
        store.SetOwner(Owner, Layer(presets: [Preset("Дешёвый фон", ("any", "local"))]));

        var effective = store.EffectivePresets(Owner);
        effective.Should().HaveCount(2, "совпадение имени не убирает ни один из пресетов");
        effective.Select(p => p.Name).Should().BeEquivalentTo("Дешёвый фон", "Дешёвый фон");
        effective[0].Rules.Single().Route.Should().Be("local", "личный блок идёт первым");
    }

    [Fact]
    public void EffectivePresetsWithScope_ОбъединённыйСписокСПризнакомСлоя()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("Общий", ("any", "tier:weak"))]));
        store.SetOwner(Owner, Layer(presets: [Preset("Мой", ("any", "local"))]));

        var merged = store.EffectivePresetsWithScope(Owner);
        merged.Should().HaveCount(2);
        merged[0].Scope.Should().Be(PresetScope.Owner);
        merged[0].Preset.Name.Should().Be("Мой");
        merged[1].Scope.Should().Be(PresetScope.Global);
        merged[1].Preset.Name.Should().Be("Общий");

        // У владельца без личных пресетов — только глобальные с признаком «общий»
        store.EffectivePresetsWithScope(Other).Should().ContainSingle()
            .Which.Scope.Should().Be(PresetScope.Global);
    }

    [Fact]
    public void ResolveRoute_ПервоеСовпадениеИAnyФолбэк()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets:
        [
            Preset("Правила",
                ("backendExecutor", "tier:strong"),
                ("any", "tier:weak")),
        ]));

        store.ResolveRoute(Owner, PersonaSpecialty.BackendExecutor).Should().Be("tier:strong");
        store.ResolveRoute(Owner, PersonaSpecialty.FrontendExecutor).Should().Be("tier:weak",
            "нет правила для специальности — срабатывает any");
        store.ResolveRoute(Owner, PersonaSpecialty.None).Should().Be("tier:weak");
    }

    [Fact]
    public void ResolveRoute_ЛичныйПресетСильнее()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("Глобальный", ("backendExecutor", "tier:weak"))]));
        store.SetOwner(Owner, Layer(presets: [Preset("Личный", ("backendExecutor", "tier:strong"))]));

        store.ResolveRoute(Owner, PersonaSpecialty.BackendExecutor).Should().Be("tier:strong");
        store.ResolveRoute(Other, PersonaSpecialty.BackendExecutor).Should().Be("tier:weak");
    }

    [Fact]
    public void ResolveRoute_УчитываетОбаНабора_ЛичныйБезСовпаденияНеЗакрываетГлобальный()
    {
        var store = NewStore();
        // Личный пресет накрывает только backendExecutor — для остальных специальностей
        // резолв находит правило в глобальном наборе
        store.SetGlobal(Layer(presets:
        [
            Preset("Глобальный",
                ("frontendExecutor", "tier:medium"),
                ("any", "tier:weak")),
        ]));
        store.SetOwner(Owner, Layer(presets: [Preset("Личный", ("backendExecutor", "tier:strong"))]));

        store.ResolveRoute(Owner, PersonaSpecialty.BackendExecutor).Should().Be("tier:strong",
            "правило из личного набора");
        store.ResolveRoute(Owner, PersonaSpecialty.FrontendExecutor).Should().Be("tier:medium",
            "личное не совпало — сработал глобальный набор");
        store.ResolveRoute(Owner, PersonaSpecialty.Analyst).Should().Be("tier:weak",
            "any-правило глобального набора остаётся в силе");
    }

    // --- Валидация ---

    [Fact]
    public void SetGlobal_НеизвестнаяСпециальность_Ошибка()
    {
        var store = NewStore();
        var error = store.SetGlobal(Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            ["no-such"] = new(),
        }));
        error.Should().Contain("Неизвестная специальность");
    }

    [Fact]
    public void SetOwner_ПустоеИмяПресета_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets: [Preset("", ("any", "claude"))]))
            .Should().Contain("пустое имя");
    }

    [Fact]
    public void SetOwner_ПустойМаршрутПравила_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets: [Preset("П", ("executor", ""))]))
            .Should().Contain("пустой маршрут");
    }

    [Fact]
    public void SetOwner_НеизвестнаяСпециальностьПравила_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets: [Preset("П", ("no-such", "claude"))]))
            .Should().Contain("неизвестная специальность");
    }

    [Fact]
    public void SetOwner_ДубльIdПресета_Ошибка()
    {
        var store = NewStore();
        var id = Guid.NewGuid().ToString();
        var error = store.SetOwner(Owner, Layer(presets:
        [
            new ModelRoutePreset { Id = id, Name = "А", Rules = [new() { Route = "claude" }] },
            new ModelRoutePreset { Id = id, Name = "Б", Rules = [new() { Route = "local" }] },
        ]));
        error.Should().Contain("Дублируется id");
    }

    // --- Персистентность и версия формата ---

    [Fact]
    public void Persist_ПишетИЧитаетЧерезНовыйИнстанс()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            new Dictionary<string, SpecialtyTemplateSettings>
            {
                ["backendExecutor"] = new() { Access = PersonaAccess.ReadOnly, Tools = ["tasks", "web"] },
            },
            [Preset("Глобальный", ("backendExecutor", "tier:medium"))]));
        store.SetOwner(Owner, Layer(
            new Dictionary<string, SpecialtyTemplateSettings>
            {
                ["executor"] = new() { Access = PersonaAccess.Custom, DisallowedTools = ["Bash"] },
            },
            [Preset("Личный", ("any", "local"))]));

        var reloaded = NewStore();

        var global = reloaded.Snapshot.Global.Specialties["backendExecutor"];
        global.Access.Should().Be(PersonaAccess.ReadOnly);
        global.Tools.Should().BeEquivalentTo("tasks", "web");
        reloaded.Snapshot.Global.Presets.Should().ContainSingle().Which.Name.Should().Be("Глобальный");

        var owner = reloaded.Snapshot.Owners[Owner];
        owner.Specialties["executor"].DisallowedTools.Should().BeEquivalentTo("Bash");
        owner.Presets.Should().ContainSingle().Which.Name.Should().Be("Личный");
    }

    [Fact]
    public void Persist_НормализуетКлючиИПоля()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new Dictionary<string, SpecialtyTemplateSettings>
        {
            // Ключ в произвольном регистре, полный набор Tools, запреты у не-Custom
            ["BackendExecutor"] = new()
            {
                Access = PersonaAccess.Full,
                Tools = ["tasks", "notes", "web"],
                DisallowedTools = ["Bash"],
            },
        }));

        var settings = store.Snapshot.Global.Specialties;
        settings.Should().ContainKey("backendExecutor", "ключ приводится к camelCase каталога");
        settings["backendExecutor"].Tools.Should().BeNull("полный набор эквивалентен «все»");
        settings["backendExecutor"].DisallowedTools.Should().BeNull("запреты живут только в Custom");
    }

    [Fact]
    public void Load_ФайлНовееКода_СтартСДефолтами()
    {
        var store = NewStore();
        var path = Path.Combine(_dir, "specialty-settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = SpecialtySettingsStore.FormatVersion + 1,
            global = new { specialties = new Dictionary<string, object>(), presets = new List<object>() },
            owners = new Dictionary<string, object>(),
        }));

        var fresh = NewStore();
        fresh.Snapshot.Global.IsEmpty.Should().BeTrue();
        fresh.Snapshot.Owners.Should().BeEmpty();
    }

    [Fact]
    public void Load_БитыйФайл_НеРоняетСтарт()
    {
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), "{ не json");

        var fresh = NewStore();
        fresh.Snapshot.Global.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Backup_ФайлСтораПопадаетВАрхив()
    {
        // Стор живёт в data/ и не входит в исключения — бэкап подхватывает его автоматически
        ClaudeHomeServer.Services.Backup.BackupPaths.ShouldInclude("specialty-settings.json")
            .Should().BeTrue();
    }
}
