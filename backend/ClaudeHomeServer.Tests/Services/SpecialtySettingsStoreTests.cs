using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Стор настроек специальностей и пресетов-цепочек: слои (глобальный + per-owner),
// матрицы моделей по уровням + DefaultTier, DefaultSpecialty, пресеты-цепочки Steps,
// ExpandChain (preset:{id}), валидация, миграция v1→v2, персистентность, версия формата.
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
        List<ModelRoutePreset>? presets = null,
        SpecialtyTemplateSettings? defaultSpecialty = null) => new()
    {
        Specialties = specialties ?? [],
        Presets = presets ?? [],
        DefaultSpecialty = defaultSpecialty,
    };

    private static SpecialtyTemplateSettings Tmpl(string? strong = null, string? medium = null,
        string? weak = null, ModelTier? defaultTier = null) => new()
    {
        TierStrong = strong,
        TierMedium = medium,
        TierWeak = weak,
        DefaultTier = defaultTier,
    };

    private static ModelRoutePreset Preset(string name, params string[] steps) => new()
    {
        Name = name,
        Steps = steps.ToList(),
    };

    // --- Эффективный шаблон (Access/Tools — без матриц) ---

    [Fact]
    public void EffectiveTemplate_ПустойСтор_ДефолтКода()
    {
        var store = NewStore();
        var template = store.EffectiveTemplate(Owner, PersonaSpecialty.BackendExecutor);
        template.Should().NotBeNull();
        template!.Access.Should().Be(PersonaAccess.Full);
        store.EffectiveTemplate(Owner, PersonaSpecialty.Analyst).Should().BeNull();
    }

    [Fact]
    public void EffectiveTemplate_ЛичнаяНастройка_СильнееГлобальной()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["backendExecutor"] = new SpecialtyTemplateSettings { Access = PersonaAccess.ReadOnly } }));
        store.SetOwner(Owner, Layer(new() { ["backendExecutor"] = new SpecialtyTemplateSettings { Access = PersonaAccess.Custom, DisallowedTools = ["Bash"] } }));
        var own = store.EffectiveTemplate(Owner, PersonaSpecialty.BackendExecutor)!;
        own.Access.Should().Be(PersonaAccess.Custom);
        own.DisallowedTools.Should().BeEquivalentTo("Bash");
    }

    // --- Матрицы специальности (ADR-007 §2) ---

    [Fact]
    public void SpecialtyMatrices_ЛичныйСлойСильнееГлобального()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["backendExecutor"] = Tmpl(strong: "global-opus") }));
        store.SetOwner(Owner, Layer(new() { ["backendExecutor"] = Tmpl(strong: "owner-opus") }));

        // Личная запись (целиком) раньше глобальной
        var matrices = store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor);
        matrices.Should().ContainSingle();
        matrices[0].Strong.Should().Be("owner-opus");
        // Чужой владелец — глобальная
        store.SpecialtyMatrices(Other, PersonaSpecialty.BackendExecutor).Single().Strong.Should().Be("global-opus");
    }

    // --- Слой «пользователь» (B9): приоритет owner → user → global ---

    [Fact]
    public void СлойПользователь_ПриоритетЛичныйПользовательскийГлобальный()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["backendExecutor"] = Tmpl(strong: "global-opus") }));

        // 1) Назначение пользователю бьёт глобальный
        store.SetUser(Owner, Layer(new() { ["backendExecutor"] = Tmpl(strong: "user-opus") }));
        store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor).Single().Strong
            .Should().Be("user-opus", "слой пользователя сильнее глобального");
        // Изоляция: другому владельцу назначение не видно — он на глобальном
        store.SpecialtyMatrices(Other, PersonaSpecialty.BackendExecutor).Single().Strong
            .Should().Be("global-opus", "назначение действует только на своего пользователя");

        // 2) Личный слой бьёт назначение пользователя
        store.SetOwner(Owner, Layer(new() { ["backendExecutor"] = Tmpl(strong: "owner-opus") }));
        store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor).Single().Strong
            .Should().Be("owner-opus", "личный слой сильнее назначения пользователя");

        // 3) Снятие личного слоя (пустой) возвращает назначение пользователя
        store.SetOwner(Owner, Layer());
        store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor).Single().Strong
            .Should().Be("user-opus", "без личного слоя снова видно назначение пользователя");

        // 4) Снятие назначения (пустой слой) возвращает глобальный
        store.SetUser(Owner, Layer());
        store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor).Single().Strong
            .Should().Be("global-opus", "без назначения снова глобальный");
    }

    [Fact]
    public void СлойПользователь_ИзоляцияДанныхМеждуПользователями()
    {
        var store = NewStore();
        store.SetUser(Owner, Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: "user-opus") },
            presets: [Preset("Назначенный", "glm-5.2")],
            defaultSpecialty: Tmpl(weak: "user-haiku")));

        // У своего пользователя назначение видно: запись + DefaultSpecialty + пресеты
        store.SpecialtyMatrices(Owner, PersonaSpecialty.Analyst).Single().Weak.Should().Be("user-haiku");
        store.EffectivePresets(Owner).Single().Name.Should().Be("Назначенный");
        store.TemplateSettings(Owner, PersonaSpecialty.BackendExecutor)!.TierStrong.Should().Be("user-opus");
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().BeNull();

        // У чужого — ничего от назначения
        store.SpecialtyMatrices(Other, PersonaSpecialty.Analyst).Should().BeEmpty();
        store.EffectivePresets(Other).Should().BeEmpty();
        store.TemplateSettings(Other, PersonaSpecialty.BackendExecutor).Should().BeNull();
    }

    [Fact]
    public void СлойПользователь_DefaultTierЦепочкаСлоёв()
    {
        var store = NewStore();
        store.SetGlobal(Layer(defaultSpecialty: Tmpl(defaultTier: ModelTier.Weak)));
        store.SetUser(Owner, Layer(new() { ["backendExecutor"] = Tmpl(defaultTier: ModelTier.Medium) }));

        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Medium,
            "запись пользователя бьёт глобальный DefaultSpecialty");
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.Analyst).Should().Be(ModelTier.Weak,
            "без записи пользователя — глобальный DefaultSpecialty");

        // Личная запись бьёт запись пользователя
        store.SetOwner(Owner, Layer(new() { ["backendExecutor"] = Tmpl(defaultTier: ModelTier.Strong) }));
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Strong);
    }

    [Fact]
    public void СлойПользователь_Пресеты_ЛичныйРаньшеПользовательскогоРаньшеГлобального()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [new ModelRoutePreset { Id = "dup", Name = "Г", Steps = ["global-step"] }]));
        store.SetUser(Owner, Layer(presets: [new ModelRoutePreset { Id = "dup", Name = "П", Steps = ["user-step"] }]));
        store.SetOwner(Owner, Layer(presets: [new ModelRoutePreset { Id = "dup", Name = "Л", Steps = ["owner-step"] }]));

        // Поиск по id: личный раньше назначения, назначение раньше глобального
        store.ExpandChain("preset:dup", Owner).Should().BeEquivalentTo(new[] { "owner-step" });
        store.SetOwner(Owner, Layer());
        store.ExpandChain("preset:dup", Owner).Should().BeEquivalentTo(new[] { "user-step" });
        store.SetUser(Owner, Layer());
        store.ExpandChain("preset:dup", Owner).Should().BeEquivalentTo(new[] { "global-step" });

        // WithScope отдаёт три слоя с признаками в порядке резолва
        store.SetUser(Owner, Layer(presets: [Preset("Назначенный", "local")]));
        store.SetOwner(Owner, Layer(presets: [Preset("Личный", "claude")]));
        var merged = store.EffectivePresetsWithScope(Owner);
        merged.Should().HaveCount(3);
        merged[0].Scope.Should().Be(PresetScope.Owner);
        merged[1].Scope.Should().Be(PresetScope.User);
        merged[2].Scope.Should().Be(PresetScope.Global);
    }

    [Fact]
    public void SpecialtyMatrices_Запись_Затем_DefaultSpecialty()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: "spec-opus") },
            defaultSpecialty: Tmpl(weak: "any-haiku")));

        var matrices = store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor);
        // Запись специальности, затем DefaultSpecialty
        matrices.Should().HaveCount(2);
        matrices[0].Strong.Should().Be("spec-opus");
        matrices[1].Weak.Should().Be("any-haiku");
    }

    [Fact]
    public void SpecialtyDefaultTier_Личный_Затем_Глобальный_Затем_DefaultSpecialty()
    {
        var store = NewStore();
        store.SetGlobal(Layer(defaultSpecialty: Tmpl(defaultTier: ModelTier.Weak)));
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Weak,
            "у специальности нет своей записи — берётся DefaultSpecialty");

        store.SetOwner(Owner, Layer(new() { ["backendExecutor"] = Tmpl(defaultTier: ModelTier.Strong) }));
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Strong,
            "личная запись специальности перебивает DefaultSpecialty");
    }

    // --- ExpandChain (preset:{id}) ---

    [Fact]
    public void ExpandChain_ОбычныйМаршрут_ОдинЭлемент()
    {
        var store = NewStore();
        store.ExpandChain("glm-5.2", Owner).Should().BeEquivalentTo(new[] { "glm-5.2" });
        store.ExpandChain("tier:strong", Owner).Should().BeEquivalentTo(new[] { "tier:strong" });
    }

    [Fact]
    public void ExpandChain_Pресет_ВозвращаетШаги()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets:
        [
            new ModelRoutePreset { Id = "p1", Name = "Рабочая", Steps = ["opus", "glm-5.2", "deepseek"] },
        ]));

        store.ExpandChain("preset:p1", Owner)
            .Should().BeEquivalentTo(new[] { "opus", "glm-5.2", "deepseek" }, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void ExpandChain_БитаяСсылка_ПустойСписок_FailOpen()
    {
        var store = NewStore();
        // Пресета no-such нет — разворот пуст (fail-open вниз), не падает
        store.ExpandChain("preset:no-such", Owner).Should().BeEmpty();
    }

    [Fact]
    public void ExpandChain_ЛичныйПресетРаньшеГлобального()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [new ModelRoutePreset { Id = "dup", Name = "Г", Steps = ["global-step"] }]));
        store.SetOwner(Owner, Layer(presets: [new ModelRoutePreset { Id = "dup", Name = "Л", Steps = ["owner-step"] }]));
        // Личный и глобальный живут рядом; поиск по id — личный раньше
        store.ExpandChain("preset:dup", Owner).Should().BeEquivalentTo(new[] { "owner-step" });
    }

    // --- Пресеты-цепочки ---

    [Fact]
    public void EffectivePresets_ЛичныеРядомСГлобальными_БезПереопределенияПоId()
    {
        var store = NewStore();
        var globalId = "g1";
        store.SetGlobal(Layer(presets:
        [
            new ModelRoutePreset { Id = globalId, Name = "Глобальный", Steps = ["tier:weak"] },
            Preset("Ещё глобальный", "claude"),
        ]));
        store.SetOwner(Owner, Layer(presets:
        [
            new ModelRoutePreset { Id = globalId, Name = "Личная версия", Steps = ["tier:strong"] },
        ]));

        var effective = store.EffectivePresets(Owner);
        effective.Should().HaveCount(3);
        effective[0].Name.Should().Be("Личная версия");
        effective[1].Name.Should().Be("Глобальный");
        effective[2].Name.Should().Be("Ещё глобальный");
    }

    [Fact]
    public void EffectivePresetsWithScope_ОбъединённыйСписокСПризнакомСлоя()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("Общий", "tier:weak")]));
        store.SetOwner(Owner, Layer(presets: [Preset("Мой", "local")]));

        var merged = store.EffectivePresetsWithScope(Owner);
        merged.Should().HaveCount(2);
        merged[0].Scope.Should().Be(PresetScope.Owner);
        merged[0].Preset.Name.Should().Be("Мой");
        merged[1].Scope.Should().Be(PresetScope.Global);
    }

    // --- Валидация ---

    [Fact]
    public void SetGlobal_НеизвестнаяСпециальность_Ошибка()
    {
        var store = NewStore();
        var error = store.SetGlobal(Layer(new() { ["no-such"] = Tmpl() }));
        error.Should().Contain("Неизвестная специальность");
    }

    [Fact]
    public void SetOwner_ПустоеИмяПресета_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets: [Preset("", "claude")])).Should().Contain("пустое имя");
    }

    [Fact]
    public void SetOwner_ПустойШаг_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets: [Preset("П", "opus", "  ")])).Should().Contain("пустой шаг");
    }

    [Fact]
    public void SetOwner_ВложенныйПресет_Запрещён()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(presets: [Preset("П", "opus", "preset:other")]))
            .Should().Contain("не может быть ссылкой на другой пресет");
    }

    [Fact]
    public void SetOwner_TierВЯчейкеМатрицы_Запрещён()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(new() { ["backendExecutor"] = Tmpl(strong: "tier:medium") }))
            .Should().Contain("не может быть tier:*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void SetOwner_ДлинаЦепочкиВнеДиапазона_Ошибка(int count)
    {
        var store = NewStore();
        var steps = Enumerable.Range(0, count).Select(i => $"m{i}").ToArray();
        store.SetOwner(Owner, Layer(presets: [Preset("П", steps)]))
            .Should().Contain("1..5");
    }

    [Fact]
    public void SetOwner_ДубликатIdПресета_Ошибка()
    {
        var store = NewStore();
        var id = "dup-id";
        var error = store.SetOwner(Owner, Layer(presets:
        [
            new ModelRoutePreset { Id = id, Name = "А", Steps = ["claude"] },
            new ModelRoutePreset { Id = id, Name = "Б", Steps = ["local"] },
        ]));
        error.Should().Contain("Дублируется id");
    }

    // --- Миграция v1 → v2 (ADR-007 §6) ---

    // Файл v1: пресеты-сборники правил «специальность → маршрут». После загрузки v2-кодом
    // правила разносятся по матрицам специальностей, пресеты удаляются. Эффективный резолв
    // сохраняется (с оговоркой §6.3 про persona.ModelTier поверх мигрированной строки-модели).
    private static readonly string V1Json = JsonSerializer.Serialize(new
    {
        version = 1,
        global = new
        {
            specialties = new Dictionary<string, object>(),
            presets = new object[]
            {
                new { name = "Глобальный", rules = new object[]
                {
                    new { specialty = "backendExecutor", route = "tier:strong" },
                    new { specialty = "any", route = "glm-5.2" },
                }},
            },
        },
        owners = new Dictionary<string, object>
        {
            ["owner-1"] = new
            {
                specialties = new Dictionary<string, object>(),
                presets = new object[]
                {
                    new { name = "Личный", rules = new object[]
                    {
                        new { specialty = "frontendExecutor", route = "opus" },
                    }},
                },
            },
        },
    });

    [Fact]
    public void Migration_V1Файл_ПравилаРносятсяПоМатрицам()
    {
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), V1Json);
        var store = NewStore();

        // tier:strong-правило backendExecutor → DefaultTier=Strong (матрица специальности пуста → слоты)
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Strong);
        var beMatrices = store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor);
        beMatrices[0].IsEmpty.Should().BeTrue("tier-правило не заполняет матрицу специальности");

        // any → glm-5.2 (модель) → DefaultSpecialty, все три ячейки = glm-5.2
        // (эффективно: «любая специальность без своей записи ходит glm-5.2»)
        var matrices = store.SpecialtyMatrices(Owner, PersonaSpecialty.Analyst);
        matrices.Should().ContainSingle("у Analyst нет своей записи — только DefaultSpecialty")
            .Which.Medium.Should().Be("glm-5.2");

        // Личное правило frontendExecutor → opus → все три ячейки личной записи (первая матрица —
        // запись специальности, за ней DefaultSpecialty как более широкий fallback)
        store.SpecialtyMatrices(Owner, PersonaSpecialty.FrontendExecutor).First().Strong.Should().Be("opus");

        // Пресеты v1 удалены (цепочек из v1-правил не строим)
        store.EffectivePresets(Owner).Should().BeEmpty();

        // Файл сохранён с новой версией
        store.Snapshot.Version.Should().Be(2);
    }

    [Fact]
    public void Migration_V1ПерваяПодходящаяСпециальностьВыигрывает()
    {
        // Два правила для одной специальности в порядке списка — выигрывает первое
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            global = new
            {
                specialties = new Dictionary<string, object>(),
                presets = new object[]
                {
                    new { rules = new object[]
                    {
                        new { specialty = "backendExecutor", route = "first-model" },
                        new { specialty = "backendExecutor", route = "second-model" },
                    }},
                },
            },
            owners = new Dictionary<string, object>(),
        });
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), json);
        var store = NewStore();

        store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor).Single().Strong
            .Should().Be("first-model", "второе правило для той же специальности в v1 было мертво");
    }

    // --- Персистентность и версия формата ---

    [Fact]
    public void Persist_ПишетИЧитаетЧерезНовыйИнстанс()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: "opus", defaultTier: ModelTier.Strong) },
            presets: [Preset("Глобальный", "tier:medium", "glm-5.2")]));
        store.SetUser(Owner, Layer(
            specialties: new() { ["analyst"] = Tmpl(medium: "glm-user") }));
        store.SetOwner(Owner, Layer(
            defaultSpecialty: Tmpl(weak: "haiku")));

        var reloaded = NewStore();
        var global = reloaded.Snapshot.Global.Specialties["backendExecutor"];
        global.TierStrong.Should().Be("opus");
        global.DefaultTier.Should().Be(ModelTier.Strong);
        reloaded.Snapshot.Global.Presets.Single().Steps.Should().BeEquivalentTo(new[] { "tier:medium", "glm-5.2" });
        reloaded.Snapshot.Users[Owner].Specialties["analyst"].TierMedium.Should().Be("glm-user",
            "слой пользователя переживает перезапуск");
        reloaded.Snapshot.Owners[Owner].DefaultSpecialty!.TierWeak.Should().Be("haiku");
    }

    [Fact]
    public void Load_ФайлНовееКода_СтартСДефолтами()
    {
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), JsonSerializer.Serialize(new
        {
            version = SpecialtySettingsStore.FormatVersion + 1,
            global = new { specialties = new Dictionary<string, object>(), presets = new List<object>() },
            owners = new Dictionary<string, object>(),
        }));
        var fresh = NewStore();
        fresh.Snapshot.Global.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Backup_ФайлСтораПопадаетВАрхив()
    {
        ClaudeHomeServer.Services.Backup.BackupPaths.ShouldInclude("specialty-settings.json")
            .Should().BeTrue();
    }
}
