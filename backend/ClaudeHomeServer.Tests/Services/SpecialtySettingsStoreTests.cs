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
        store.Snapshot.Version.Should().Be(SpecialtySettingsStore.FormatVersion);
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

    // --- Переадресация закреплённых моделей (миграция каталога провайдера) ---

    private static readonly Dictionary<string, string> GlmMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glm-5.2[1m]"] = "glm-5.3[1m]",
        ["glm-5.2"] = "glm-5.3",
        ["glm-4.5-air"] = "glm-4.7",
    };

    [Fact]
    public void RemapModels_ПерепиcываетШагиЦепочекИЯчейкиВоВсехСлоях()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: "glm-5.2[1m]", weak: "glm-4.5-air") },
            presets: [Preset("Каскад", "glm-5.2[1m]", "tier:medium", "glm-4.5-air")],
            defaultSpecialty: Tmpl(medium: "glm-5.2")));
        store.SetOwner(Owner, Layer(specialties: new() { ["frontendExecutor"] = Tmpl(medium: "glm-5.2") }));
        store.SetUser("user-1", Layer(presets: [Preset("Пользовательский", "glm-4.5-air")]));

        // 4 ячейки (strong+weak+default medium+owner medium) + 3 шага цепочек
        store.RemapModels(GlmMap).Should().Be(7);

        var global = store.Snapshot.Global;
        global.Specialties["backendExecutor"].TierStrong.Should().Be("glm-5.3[1m]");
        global.Specialties["backendExecutor"].TierWeak.Should().Be("glm-4.7");
        global.DefaultSpecialty!.TierMedium.Should().Be("glm-5.3");
        global.Presets[0].Steps.Should().Equal("glm-5.3[1m]", "tier:medium", "glm-4.7");
        store.Snapshot.Owners[Owner].Specialties["frontendExecutor"].TierMedium.Should().Be("glm-5.3");
        store.Snapshot.Users["user-1"].Presets[0].Steps.Should().Equal("glm-4.7");

        // Значение доехало до диска — иначе миграция не пережила бы рестарт
        NewStore().Snapshot.Global.Presets[0].Steps.Should().Equal("glm-5.3[1m]", "tier:medium", "glm-4.7");
    }

    [Fact]
    public void RemapModels_НеТрогаетСсылкиНаПресетыИНезнакомыеМодели()
    {
        var store = NewStore();
        var presetRef = "preset:" + Guid.NewGuid();
        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: presetRef, medium: "glm-5-turbo") },
            presets: [Preset("Каскад", "tier:strong", "local", "opus", "glm-4.6")]));

        store.RemapModels(GlmMap).Should().Be(0, "адресуемся точным совпадением id");

        var global = store.Snapshot.Global;
        global.Specialties["backendExecutor"].TierStrong.Should().Be(presetRef);
        global.Specialties["backendExecutor"].TierMedium.Should().Be("glm-5-turbo");
        global.Presets[0].Steps.Should().Equal("tier:strong", "local", "opus", "glm-4.6");
    }

    [Fact]
    public void RemapModels_ПовторныйПрогон_НичегоНеМеняет()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("Каскад", "glm-5.2")]));

        store.RemapModels(GlmMap).Should().Be(1);
        store.RemapModels(GlmMap).Should().Be(0, "новых id в карте нет — переписывать нечего");
        store.Snapshot.Global.Presets[0].Steps.Should().Equal("glm-5.3");
    }

    // --- Секции промптов: посекочное наследование (не «замена слоя целиком») ---

    private static SpecialtyPromptSectionSettings Section(string id, bool enabled, string? text = null) => new()
    {
        Id = id,
        Enabled = enabled,
        Text = text,
    };

    private static SpecialtyTemplateSettings TmplWithSections(
        params SpecialtyPromptSectionSettings[] sections) => new() { PromptSections = sections.ToList() };

    [Fact]
    public void EffectivePromptSections_ПустойСтор_ДефолтыКода()
    {
        var store = NewStore();
        var states = store.EffectivePromptSectionStates(Owner, PersonaSpecialty.Analyst);

        states.Select(s => s.Id).Should()
            .Equal(["history", "codeGraph", "processes", "roleRules"], "порядок — порядок каталога");
        states.Should().OnlyContain(s => s.EnabledSource == SpecialtySettingsStore.SectionSource.Code
            && s.TextSource == SpecialtySettingsStore.SectionSource.Code);

        // Таблица дефолтов аналитика: история и правила роли включены, граф и процессы — нет
        var enabled = store.EffectivePromptSections(Owner, PersonaSpecialty.Analyst).Select(s => s.Id).ToList();
        enabled.Should().Equal("history", "roleRules");
        states.Single(s => s.Id == "history").Text
            .Should().Be(SpecialtyPromptPresets.DefaultText("history", PersonaSpecialty.Analyst));
    }

    [Fact]
    public void Секции_None_Пусто()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("history", true)) }));
        store.EffectivePromptSections(Owner, PersonaSpecialty.None).Should().BeEmpty(
            "у «Не задана» секций нет вовсе — поведение как до фичи");
    }

    [Fact]
    public void Секции_ЯвныйOffВладельцаПерекрываетOnАдмина()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("history", true, "админский текст")) }));
        store.SetOwner(Owner, Layer(new() { ["analyst"] = TmplWithSections(Section("history", false)) }));

        store.EffectivePromptSections(Owner, PersonaSpecialty.Analyst).Should().NotContain(s => s.Id == "history",
            "заданное значение владельца (off), а не отсутствие записи");
        // Чужой владелец — на on админа
        store.EffectivePromptSections(Other, PersonaSpecialty.Analyst)
            .Should().Contain(s => s.Id == "history");
    }

    [Fact]
    public void Секции_ТочечноеПерекрытиеНеТрогаетСоседние()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            ["planner"] = TmplWithSections(
                Section("history", true, "админ: история"),
                Section("roleRules", true, "админ: правила")),
        }));
        // Владелец переопределяет ТОЛЬКО roleRules — история админа должна уцелеть
        store.SetOwner(Owner, Layer(new()
        {
            ["planner"] = TmplWithSections(Section("roleRules", true, "владелец: правила")),
        }));

        var effective = store.EffectivePromptSections(Owner, PersonaSpecialty.Planner)
            .ToDictionary(s => s.Id);
        effective["history"].Text.Should().Be("админ: история",
            "переопределение одной секции не сносит соседние");
        effective["roleRules"].Text.Should().Be("владелец: правила");
    }

    [Fact]
    public void Секции_EnabledИТекстНаследуютсяНезависимо()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("history", false, "текст админа")) }));
        // Владелец задаёт только enabled (text = null): текст падает вниз к админу
        store.SetOwner(Owner, Layer(new() { ["analyst"] = TmplWithSections(Section("history", true)) }));

        var state = store.EffectivePromptSectionStates(Owner, PersonaSpecialty.Analyst)
            .Single(s => s.Id == "history");
        state.Enabled.Should().BeTrue();
        state.EnabledSource.Should().Be(SpecialtySettingsStore.SectionSource.Owner);
        state.Text.Should().Be("текст админа");
        state.TextSource.Should().Be(SpecialtySettingsStore.SectionSource.Global);
    }

    [Fact]
    public void Секции_ЦепочкаСлоёвOwnerUserGlobal()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("history", true, "глобальный")) }));
        store.SetUser(Owner, Layer(new() { ["analyst"] = TmplWithSections(Section("history", true, "пользователя")) }));
        store.SetOwner(Owner, Layer(new() { ["analyst"] = TmplWithSections(Section("history", true, "личный")) }));

        var byOwner = store.EffectivePromptSections(Owner, PersonaSpecialty.Analyst).Single(s => s.Id == "history");
        byOwner.Text.Should().Be("личный", "личный слой сильнее назначения пользователя");

        store.SetOwner(Owner, Layer());
        store.EffectivePromptSections(Owner, PersonaSpecialty.Analyst).Single(s => s.Id == "history").Text
            .Should().Be("пользователя", "без личного слоя видно назначение пользователя");

        store.SetUser(Owner, Layer());
        store.EffectivePromptSections(Owner, PersonaSpecialty.Analyst).Single(s => s.Id == "history").Text
            .Should().Be("глобальный", "без назначения — глобальный");
    }

    // --- Типовой профиль умений ---

    [Fact]
    public void DefaultBindings_ВерхнийЗаданныйСлой_ИначеДефолтКода()
    {
        var store = NewStore();
        var custom = new List<SpecialtyDefaultBinding>
        {
            new() { Type = PersonaBindingType.Knowledge, Condition = "когда базы знаний" },
        };
        store.SetGlobal(Layer(new() { ["librarian"] = new SpecialtyTemplateSettings { DefaultBindings = custom } }));

        // У владельца ЕСТЬ запись (модели), но профиля в ней нет — профиль падает к глобальному
        store.SetOwner(Owner, Layer(new() { ["librarian"] = Tmpl(strong: "glm-5.3") }));
        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.Librarian).Should().BeEquivalentTo(custom,
            "профиль наследуется полем, а не «записью целиком» — override моделей его не сносит");

        // И глобального нет — дефолт кода
        store.SetGlobal(Layer());
        var codeProfile = SpecialtyPromptPresets.DefaultBindingsProfile(PersonaSpecialty.Librarian);
        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.Librarian).Select(b => b.Type)
            .Should().Equal(codeProfile.Select(b => b.Type));

        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.None).Should().BeEmpty();
    }

    // --- Валидация секций и профиля ---

    [Fact]
    public void SetOwner_НеизвестнаяСекция_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(new() { ["analyst"] = TmplWithSections(Section("no-such", true)) }))
            .Should().Contain("неизвестная секция");
    }

    [Fact]
    public void SetOwner_ДубльСекции_Ошибка()
    {
        var store = NewStore();
        store.SetOwner(Owner, Layer(new()
        {
            ["analyst"] = TmplWithSections(Section("history", true), Section(" history ", false)),
        })).Should().Contain("дважды");
    }

    [Fact]
    public void SetOwner_ТекстСекцииНадЛимитом_Ошибка()
    {
        var store = NewStore();
        var tooLong = new string('а', SpecialtyPromptPresets.SectionTextLimit + 1);
        store.SetOwner(Owner, Layer(new() { ["analyst"] = TmplWithSections(Section("history", true, tooLong)) }))
            .Should().Contain("длиннее");
    }

    [Fact]
    public void SetOwner_НавыкБезИмени_Ошибка()
    {
        var store = NewStore();
        var profile = new SpecialtyTemplateSettings
        {
            DefaultBindings = [new SpecialtyDefaultBinding { Type = PersonaBindingType.Skill }],
        };
        store.SetOwner(Owner, Layer(new() { ["analyst"] = profile }))
            .Should().Contain("не указано имя скилла");
    }

    [Fact]
    public void SetOwner_ИмяСкиллаНеУНавыка_Ошибка()
    {
        var store = NewStore();
        var profile = new SpecialtyTemplateSettings
        {
            DefaultBindings =
            [
                new SpecialtyDefaultBinding { Type = PersonaBindingType.Notes, SkillName = "чужой скилл" },
            ],
        };
        store.SetOwner(Owner, Layer(new() { ["analyst"] = profile }))
            .Should().Contain("только у типового умения типа «Навык»");
    }

    [Fact]
    public void SetOwner_НавыкСИменем_ПроходитИНормализуется()
    {
        var store = NewStore();
        var profile = new SpecialtyTemplateSettings
        {
            DefaultBindings =
            [
                new SpecialtyDefaultBinding
                {
                    Type = PersonaBindingType.Skill,
                    SkillName = "  my-skill  ",
                    Condition = "  по сценарию  ",
                    Mode = PersonaBindingMode.Always,
                },
            ],
        };
        store.SetOwner(Owner, Layer(new() { ["analyst"] = profile })).Should().BeNull();

        var saved = store.Snapshot.Owners[Owner].Specialties["analyst"].DefaultBindings!.Single();
        saved.SkillName.Should().Be("my-skill");
        saved.Condition.Should().Be("по сценарию");
        saved.Mode.Should().Be(PersonaBindingMode.Always);
    }

    // --- Совместимость формата (v2-файл без новых полей; v3 переживает рестарт) ---

    [Fact]
    public void Совместимость_V2ФайлБезНовыхПолей_ГрузитсяСДефолтамиКода()
    {
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), JsonSerializer.Serialize(new
        {
            version = 2,
            global = new
            {
                specialties = new Dictionary<string, object> { ["analyst"] = new { tierStrong = "opus" } },
                presets = new List<object>(),
            },
            owners = new Dictionary<string, object>(),
            users = new Dictionary<string, object>(),
        }));
        var store = NewStore();
        store.Snapshot.Version.Should().Be(SpecialtySettingsStore.FormatVersion);
        store.Snapshot.Global.Specialties["analyst"].PromptSections.Should().BeNull("полей не было — дефолты из кода");
        store.Snapshot.Global.Specialties["analyst"].DefaultBindings.Should().BeNull();
        store.EffectivePromptSections(Owner, PersonaSpecialty.Analyst)
            .Should().OnlyContain(s => s.TextSource == SpecialtySettingsStore.SectionSource.Code);
    }

    [Fact]
    public void Совместимость_СекцииПереживаютПерезапуск()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            ["reviewer"] = TmplWithSections(Section("history", true, "текст секции")),
        }));
        NewStore().Snapshot.Global.Specialties["reviewer"].PromptSections!.Single().Text
            .Should().Be("текст секции");
    }

    [Fact]
    public void Совместимость_ФайлБудущегоВерсии_СтартСДефолтами()
    {
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), JsonSerializer.Serialize(new
        {
            version = SpecialtySettingsStore.FormatVersion + 1,
            global = new { specialties = new Dictionary<string, object>(), presets = new List<object>() },
            owners = new Dictionary<string, object>(),
        }));
        NewStore().Snapshot.Global.IsEmpty.Should().BeTrue();
    }

    // --- Сброс настроек моделей не сносит секции ---

    [Fact]
    public void ResetModelSettings_ЗаписьТолькоССекциями_НеУдаляется()
    {
        var store = NewStore();
        // Права эквивалентны нижнему слою (совпадают с дефолтом), но запись несёт секции
        store.SetOwner(Owner, Layer(new()
        {
            ["analyst"] = TmplWithSections(Section("history", true, "ценное")),
        }));

        var result = store.ResetModelSettings(Owner, "analyst", apply: true);
        result.Changed.Should().Be(0, "снимать нечего — уровней в записи не было");
        store.Snapshot.Owners[Owner].Specialties["analyst"].PromptSections!.Single().Text
            .Should().Be("ценное", "сброс МОДЕЛЕЙ не трогает секции промптов");

        // Запись с уровнями И секциями: уровни снимаются, секции остаются
        var mixed = new SpecialtyTemplateSettings
        {
            TierStrong = "glm-5.3",
            PromptSections = [Section("history", true, "тоже ценное")],
        };
        store.SetOwner(Owner, Layer(new() { ["analyst"] = mixed }));
        store.ResetModelSettings(Owner, "analyst", apply: true).Changed.Should().Be(1);
        var after = store.Snapshot.Owners[Owner].Specialties["analyst"];
        after.TierStrong.Should().BeNull();
        after.PromptSections!.Single().Text.Should().Be("тоже ценное");
    }

    // --- Бенчмарк резолва (план «Секции промптов» этап 3, риск «цена резолва per-turn») ---

    [Fact]
    public void EffectivePromptSectionStates_Бенчмарк_УкладываетсяВПорог()
    {
        var store = NewStore();
        // Слой с переопределениями по всем 14 специальностям — резолв идёт по полному пути
        // (owner → user → global) на каждый вызов, не по короткому «слоя нет вовсе»
        var specialties = new Dictionary<string, SpecialtyTemplateSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in SpecialtyCatalog.All.Where(e => e.Specialty != PersonaSpecialty.None))
            specialties[entry.Key] = TmplWithSections(
                Section("history", true, "переопределённый текст истории"),
                Section("codeGraph", true, "переопределённый текст графа"));
        store.SetOwner(Owner, Layer(specialties));

        // Прогрев (JIT) — не считаем в замер
        foreach (var e in SpecialtyCatalog.All) store.EffectivePromptSectionStates(Owner, e.Specialty);

        var roles = SpecialtyCatalog.All.Where(e => e.Specialty != PersonaSpecialty.None)
            .Select(e => e.Specialty).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
            store.EffectivePromptSectionStates(Owner, roles[i % roles.Count]);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / 100;
        avgMs.Should().BeLessThan(SpecialtySettingsStore.PromptSectionsResolveBenchmarkThresholdMs,
            "резолвер — чистые словари/списки в памяти без I/O; превышение порога — сигнал " +
            "завести кэш per-specialty (см. комментарий у константы), а не молчаливая деградация хода");
    }
}
