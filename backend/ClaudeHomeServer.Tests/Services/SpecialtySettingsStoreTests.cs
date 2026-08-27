using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Стор настроек специальностей и пресетов-цепочек: ОДИН слой (общий для инстанса, ADR-012),
// матрицы моделей по уровням + DefaultTier, DefaultSpecialty, пресеты-цепочки Steps,
// ExpandChain (preset:{id}), валидация, миграции (v1→v2 и v4→v5 — снятие слоёв),
// персистентность, версия формата.
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

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();

    private SpecialtySettingsStore NewStore(UserStore? users = null) =>
        new(Config(), users ?? TestSpecialtyStore.Users(Config()),
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
    public void EffectiveTemplate_ГлобальнаяНастройка_ПоверхДефолтаКода()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            ["backendExecutor"] = new SpecialtyTemplateSettings
            {
                Access = PersonaAccess.Custom,
                DisallowedTools = ["Bash"],
            },
        }));
        var template = store.EffectiveTemplate(Owner, PersonaSpecialty.BackendExecutor)!;
        template.Access.Should().Be(PersonaAccess.Custom);
        template.DisallowedTools.Should().BeEquivalentTo("Bash");
    }

    // --- Один слой: настройки одинаковы для всех владельцев (ADR-012) ---

    [Fact]
    public void Настройки_ОбщиеДляВсехВладельцев()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: "global-opus") },
            presets: [Preset("Общий", "glm-5.2")],
            defaultSpecialty: Tmpl(weak: "global-haiku")));

        foreach (var who in new[] { Owner, Other })
        {
            store.SpecialtyMatrices(who, PersonaSpecialty.BackendExecutor)[0].Strong
                .Should().Be("global-opus", "специальность — контракт инстанса, а не личная настройка");
            store.TemplateSettings(who, PersonaSpecialty.BackendExecutor)!.TierStrong.Should().Be("global-opus");
            store.SpecialtyMatrices(who, PersonaSpecialty.Analyst).Single().Weak.Should().Be("global-haiku");
            store.EffectivePresets(who).Single().Name.Should().Be("Общий");
        }
    }

    [Fact]
    public void Пресеты_ВсеИзГлобальногоСлоя_ScopeGlobal()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets:
        [
            new ModelRoutePreset { Id = "p1", Name = "Первый", Steps = ["global-step"] },
            Preset("Второй", "local"),
        ]));

        store.ExpandChain("preset:p1", Owner).Should().BeEquivalentTo(new[] { "global-step" });
        var merged = store.EffectivePresetsWithScope(Owner);
        merged.Should().HaveCount(2);
        merged.Should().OnlyContain(e => e.Scope == PresetScope.Global);
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
    public void SpecialtyDefaultTier_ЗаписьСпециальности_Затем_DefaultSpecialty()
    {
        var store = NewStore();
        store.SetGlobal(Layer(defaultSpecialty: Tmpl(defaultTier: ModelTier.Weak)));
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Weak,
            "у специальности нет своей записи — берётся DefaultSpecialty");

        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(defaultTier: ModelTier.Strong) },
            defaultSpecialty: Tmpl(defaultTier: ModelTier.Weak)));
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Strong,
            "запись специальности перебивает DefaultSpecialty");
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
        store.SetGlobal(Layer(presets:
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

    // --- Пресеты-цепочки ---

    [Fact]
    public void EffectivePresets_ПорядокСпискаСохраняется()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets:
        [
            new ModelRoutePreset { Id = "g1", Name = "Первый", Steps = ["tier:weak"] },
            Preset("Второй", "claude"),
        ]));

        store.EffectivePresets(Owner).Select(p => p.Name).Should().Equal("Первый", "Второй");
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
    public void SetGlobal_ПустоеИмяПресета_Ошибка()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("", "claude")])).Should().Contain("пустое имя");
    }

    [Fact]
    public void SetGlobal_ПустойШаг_Ошибка()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("П", "opus", "  ")])).Should().Contain("пустой шаг");
    }

    [Fact]
    public void SetGlobal_ВложенныйПресет_Запрещён()
    {
        var store = NewStore();
        store.SetGlobal(Layer(presets: [Preset("П", "opus", "preset:other")]))
            .Should().Contain("не может быть ссылкой на другой пресет");
    }

    [Fact]
    public void SetGlobal_TierВЯчейкеМатрицы_Запрещён()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["backendExecutor"] = Tmpl(strong: "tier:medium") }))
            .Should().Contain("не может быть tier:*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void SetGlobal_ДлинаЦепочкиВнеДиапазона_Ошибка(int count)
    {
        var store = NewStore();
        var steps = Enumerable.Range(0, count).Select(i => $"m{i}").ToArray();
        store.SetGlobal(Layer(presets: [Preset("П", steps)]))
            .Should().Contain("1..5");
    }

    [Fact]
    public void SetGlobal_ДубликатIdПресета_Ошибка()
    {
        var store = NewStore();
        var id = "dup-id";
        var error = store.SetGlobal(Layer(presets:
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
        // owner-1 — админ: иначе его слой не переживёт миграцию v5 (слои сняты)
        var store = NewStore(TestSpecialtyStore.UsersFile(Config(), (Owner, "admin")));

        // tier:strong-правило backendExecutor → DefaultTier=Strong (матрица специальности пуста → слоты)
        store.SpecialtyDefaultTier(Owner, PersonaSpecialty.BackendExecutor).Should().Be(ModelTier.Strong);
        var beMatrices = store.SpecialtyMatrices(Owner, PersonaSpecialty.BackendExecutor);
        beMatrices[0].IsEmpty.Should().BeTrue("tier-правило не заполняет матрицу специальности");

        // any → glm-5.2 (модель) → DefaultSpecialty, все три ячейки = glm-5.2
        // (эффективно: «любая специальность без своей записи ходит glm-5.2»)
        var matrices = store.SpecialtyMatrices(Owner, PersonaSpecialty.Analyst);
        matrices.Should().ContainSingle("у Analyst нет своей записи — только DefaultSpecialty")
            .Which.Medium.Should().Be("glm-5.2");

        // Правило админа frontendExecutor → opus → все три ячейки записи, влитой миграцией v5
        // в общий слой (первая матрица — запись специальности, за ней DefaultSpecialty)
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

    // --- Миграция v4 → v5: снятие слоёв (ADR-012) ---

    // Файл v4 со всеми тремя слоями. Владельцы: Owner (роль назначается тестом), Other.
    private void WriteV4File(object global, object owners, object? users = null)
    {
        File.WriteAllText(Path.Combine(_dir, "specialty-settings.json"), JsonSerializer.Serialize(new
        {
            version = 4,
            global,
            owners,
            users = users ?? new Dictionary<string, object>(),
        }));
    }

    [Fact]
    public void MigrationV5_СлойАдмина_ВлитВГлобальный_ПриКонфликтеВыигрываетOwner()
    {
        WriteV4File(
            global: new
            {
                specialties = new Dictionary<string, object>
                {
                    ["backendExecutor"] = new { tierStrong = "global-opus" },
                    ["analyst"] = new { tierMedium = "global-glm" },
                },
                presets = new object[] { new { id = "g1", name = "Общий", steps = new[] { "claude" } } },
                defaultSpecialty = new { tierWeak = "global-haiku" },
            },
            owners: new Dictionary<string, object>
            {
                [Owner] = new
                {
                    specialties = new Dictionary<string, object>
                    {
                        ["backendExecutor"] = new { tierStrong = "owner-opus" },
                    },
                    presets = new object[] { new { id = "o1", name = "Личный", steps = new[] { "local" } } },
                    defaultSpecialty = new { tierWeak = "owner-haiku" },
                },
            });

        var store = NewStore(TestSpecialtyStore.UsersFile(Config(), (Owner, "admin")));

        var global = store.Snapshot.Global;
        global.Specialties["backendExecutor"].TierStrong.Should().Be("owner-opus",
            "при конфликте ключа выигрывает значение из слоя админа — оно и работало у него");
        global.Specialties["analyst"].TierMedium.Should().Be("global-glm",
            "запись, которой у админа не было, остаётся из глобального слоя");
        global.DefaultSpecialty!.TierWeak.Should().Be("owner-haiku");
        // Пресеты живут рядом: личные впереди общих, дублей по id нет
        global.Presets.Select(p => p.Id).Should().Equal("o1", "g1");
    }

    [Fact]
    public void MigrationV5_СекцииПромптов_ВливаютсяПосекционно()
    {
        WriteV4File(
            global: new
            {
                specialties = new Dictionary<string, object>
                {
                    ["planner"] = new
                    {
                        promptSections = new object[]
                        {
                            new { id = "history", enabled = true, text = "глобальная история" },
                            new { id = "roleRules", enabled = true, text = "глобальные правила" },
                        },
                    },
                },
                presets = Array.Empty<object>(),
            },
            owners: new Dictionary<string, object>
            {
                [Owner] = new
                {
                    specialties = new Dictionary<string, object>
                    {
                        // Владелец задал ТОЛЬКО roleRules — и только enabled у codeGraph
                        ["planner"] = new
                        {
                            promptSections = new object[]
                            {
                                new { id = "roleRules", enabled = true, text = "личные правила" },
                                new { id = "codeGraph", enabled = true, text = (string?)null },
                            },
                        },
                    },
                    presets = Array.Empty<object>(),
                },
            });

        var store = NewStore(TestSpecialtyStore.UsersFile(Config(), (Owner, "admin")));

        var states = store.EffectivePromptSectionStates(Owner, PersonaSpecialty.Planner)
            .ToDictionary(s => s.Id);
        states["roleRules"].Text.Should().Be("личные правила", "секция владельца сильнее");
        states["history"].Text.Should().Be("глобальная история",
            "замена записи целиком снесла бы секции, заданные глобально");
        states["codeGraph"].Enabled.Should().BeTrue("владелец задал только enabled");
        states["codeGraph"].TextSource.Should().Be(SpecialtySettingsStore.SectionSource.Code,
            "текста владелец не задавал — остаётся дефолт кода");
    }

    [Fact]
    public void MigrationV5_НесколькоАдминов_РаннийВыигрывает_ЧужиеСлоиОтброшены()
    {
        const string SecondAdmin = "owner-3";
        WriteV4File(
            global: new { specialties = new Dictionary<string, object>(), presets = Array.Empty<object>() },
            owners: new Dictionary<string, object>
            {
                [Owner] = new
                {
                    specialties = new Dictionary<string, object>
                    {
                        ["analyst"] = new { tierStrong = "первый-админ" },
                    },
                    presets = Array.Empty<object>(),
                },
                [SecondAdmin] = new
                {
                    specialties = new Dictionary<string, object>
                    {
                        ["analyst"] = new { tierStrong = "второй-админ" },
                        ["planner"] = new { tierStrong = "только-у-второго" },
                    },
                    presets = Array.Empty<object>(),
                },
                // Не админ — его слой не должен доехать до общего
                [Other] = new
                {
                    specialties = new Dictionary<string, object>
                    {
                        ["reviewer"] = new { tierStrong = "чужой" },
                    },
                    presets = Array.Empty<object>(),
                },
            },
            users: new Dictionary<string, object>
            {
                [Other] = new
                {
                    specialties = new Dictionary<string, object>
                    {
                        ["tester"] = new { tierStrong = "назначенный" },
                    },
                    presets = Array.Empty<object>(),
                },
            });

        // Порядок users.json: Owner раньше SecondAdmin
        var store = NewStore(TestSpecialtyStore.UsersFile(Config(),
            (Owner, "admin"), (SecondAdmin, "admin"), (Other, "user")));

        var global = store.Snapshot.Global;
        global.Specialties["analyst"].TierStrong.Should().Be("первый-админ",
            "при конфликте ключа выигрывает более ранний админ в users.json");
        global.Specialties["planner"].TierStrong.Should().Be("только-у-второго",
            "непересекающиеся записи второго админа доезжают");
        global.Specialties.Should().NotContainKey("reviewer", "слой не-админа отброшен");
        global.Specialties.Should().NotContainKey("tester", "слой «пользователь» (B9) отброшен");
    }

    [Fact]
    public void MigrationV5_СловариСлоёвПусты_ВерсияПять_ФайлПереписан()
    {
        WriteV4File(
            global: new { specialties = new Dictionary<string, object>(), presets = Array.Empty<object>() },
            owners: new Dictionary<string, object>
            {
                [Owner] = new
                {
                    specialties = new Dictionary<string, object> { ["analyst"] = new { tierStrong = "opus" } },
                    presets = Array.Empty<object>(),
                },
            });

        var store = NewStore(TestSpecialtyStore.UsersFile(Config(), (Owner, "admin")));

        store.Snapshot.Owners.Should().BeEmpty();
        store.Snapshot.Users.Should().BeEmpty();
        store.Snapshot.Version.Should().Be(5);

        // Файл на диске переписан: следующий старт видит уже v5 и мигрировать нечего
        var onDisk = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "specialty-settings.json")));
        onDisk.RootElement.GetProperty("Version").GetInt32().Should().Be(5);
        onDisk.RootElement.GetProperty("Owners").EnumerateObject().Should().BeEmpty();
        onDisk.RootElement.GetProperty("Users").EnumerateObject().Should().BeEmpty();

        // Значение админа доехало и переживает перезапуск
        NewStore().Snapshot.Global.Specialties["analyst"].TierStrong.Should().Be("opus");
    }

    [Fact]
    public void MigrationV5_КопияИсходногоФайла_ЛожитсяРядом()
    {
        WriteV4File(
            global: new { specialties = new Dictionary<string, object>(), presets = Array.Empty<object>() },
            owners: new Dictionary<string, object>
            {
                [Owner] = new
                {
                    specialties = new Dictionary<string, object> { ["analyst"] = new { tierStrong = "opus" } },
                    presets = Array.Empty<object>(),
                },
            });

        NewStore(TestSpecialtyStore.UsersFile(Config(), (Owner, "admin")));

        // Owners/Users в файле не остаются — без копии разбирать «куда делась настройка» нечем
        var backup = Path.Combine(_dir, "specialty-settings.v4.bak");
        File.Exists(backup).Should().BeTrue();
        File.ReadAllText(backup).Should().Contain("owner-1").And.Contain("opus");
    }

    // --- Персистентность и версия формата ---

    [Fact]
    public void Persist_ПишетИЧитаетЧерезНовыйИнстанс()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            specialties: new() { ["backendExecutor"] = Tmpl(strong: "opus", defaultTier: ModelTier.Strong) },
            presets: [Preset("Глобальный", "tier:medium", "glm-5.2")],
            defaultSpecialty: Tmpl(weak: "haiku")));

        var reloaded = NewStore();
        var global = reloaded.Snapshot.Global.Specialties["backendExecutor"];
        global.TierStrong.Should().Be("opus");
        global.DefaultTier.Should().Be(ModelTier.Strong);
        reloaded.Snapshot.Global.Presets.Single().Steps.Should().BeEquivalentTo(new[] { "tier:medium", "glm-5.2" });
        reloaded.Snapshot.Global.DefaultSpecialty!.TierWeak.Should().Be("haiku");
        reloaded.Snapshot.Owners.Should().BeEmpty("слоёв больше нет — файл их не хранит");
        reloaded.Snapshot.Users.Should().BeEmpty();
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
    public void RemapModels_ПерепиcываетШагиЦепочекИЯчейкиСлоя()
    {
        var store = NewStore();
        store.SetGlobal(Layer(
            specialties: new()
            {
                ["backendExecutor"] = Tmpl(strong: "glm-5.2[1m]", weak: "glm-4.5-air"),
                ["frontendExecutor"] = Tmpl(medium: "glm-5.2"),
            },
            presets: [Preset("Каскад", "glm-5.2[1m]", "tier:medium", "glm-4.5-air")],
            defaultSpecialty: Tmpl(medium: "glm-5.2")));

        // 4 ячейки (backend strong+weak, frontend medium, default medium) + 2 шага цепочки
        store.RemapModels(GlmMap).Should().Be(6);

        var global = store.Snapshot.Global;
        global.Specialties["backendExecutor"].TierStrong.Should().Be("glm-5.3[1m]");
        global.Specialties["backendExecutor"].TierWeak.Should().Be("glm-4.7");
        global.Specialties["frontendExecutor"].TierMedium.Should().Be("glm-5.3");
        global.DefaultSpecialty!.TierMedium.Should().Be("glm-5.3");
        global.Presets[0].Steps.Should().Equal("glm-5.3[1m]", "tier:medium", "glm-4.7");

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
    public void Секции_ЯвныйOff_ПерекрываетДефолтКода()
    {
        var store = NewStore();
        // history у аналитика включён дефолтом кода — явный off настройки его снимает
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("history", false)) }));

        foreach (var who in new[] { Owner, Other })
            store.EffectivePromptSections(who, PersonaSpecialty.Analyst)
                .Should().NotContain(s => s.Id == "history",
                    "заданное значение (off) сильнее дефолта кода, и оно одно на инстанс");
    }

    [Fact]
    public void Секции_ТочечнаяНастройкаНеТрогаетСоседние()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            ["planner"] = TmplWithSections(
                Section("history", true, "свой: история"),
                Section("roleRules", true, "свой: правила")),
        }));

        var effective = store.EffectivePromptSections(Owner, PersonaSpecialty.Planner)
            .ToDictionary(s => s.Id);
        effective["history"].Text.Should().Be("свой: история");
        effective["roleRules"].Text.Should().Be("свой: правила");
        // Секция, которую настройка не трогает, осталась на дефолте кода
        var states = store.EffectivePromptSectionStates(Owner, PersonaSpecialty.Planner);
        states.Single(s => s.Id == "codeGraph").TextSource
            .Should().Be(SpecialtySettingsStore.SectionSource.Code);
    }

    [Fact]
    public void Секции_EnabledИТекстБерутсяКаждыйСвоимПараметром()
    {
        var store = NewStore();
        // Настройка задаёт только enabled (text = null) — текст остаётся дефолтом кода
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("codeGraph", true)) }));

        var state = store.EffectivePromptSectionStates(Owner, PersonaSpecialty.Analyst)
            .Single(s => s.Id == "codeGraph");
        state.Enabled.Should().BeTrue("дефолт кода у аналитика — off, настройка его включила");
        state.EnabledSource.Should().Be(SpecialtySettingsStore.SectionSource.Global);
        state.Text.Should().Be(SpecialtyPromptPresets.DefaultText("codeGraph", PersonaSpecialty.Analyst));
        state.TextSource.Should().Be(SpecialtySettingsStore.SectionSource.Code);
    }

    // --- Типовой профиль умений ---

    [Fact]
    public void DefaultBindings_ЗаданныйПрофиль_ИначеДефолтКода()
    {
        var store = NewStore();
        var custom = new List<SpecialtyDefaultBinding>
        {
            new() { Type = PersonaBindingType.Knowledge, Condition = "когда базы знаний" },
        };
        store.SetGlobal(Layer(new() { ["librarian"] = new SpecialtyTemplateSettings { DefaultBindings = custom } }));
        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.Librarian).Should().BeEquivalentTo(custom);

        // В записи есть модели, но профиля нет — профиль падает к дефолту кода, а не пустеет
        store.SetGlobal(Layer(new() { ["librarian"] = Tmpl(strong: "glm-5.3") }));
        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.Librarian)
            .Should().NotBeEmpty("профиль наследуется полем, а не «записью целиком»");

        // Настройки нет вовсе — дефолт кода
        store.SetGlobal(Layer());
        var codeProfile = SpecialtyPromptPresets.DefaultBindingsProfile(PersonaSpecialty.Librarian);
        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.Librarian).Select(b => b.Type)
            .Should().Equal(codeProfile.Select(b => b.Type));

        store.EffectiveDefaultBindings(Owner, PersonaSpecialty.None).Should().BeEmpty();
    }

    // --- Валидация секций и профиля ---

    [Fact]
    public void SetGlobal_НеизвестнаяСекция_Ошибка()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("no-such", true)) }))
            .Should().Contain("неизвестная секция");
    }

    [Fact]
    public void SetGlobal_ДубльСекции_Ошибка()
    {
        var store = NewStore();
        store.SetGlobal(Layer(new()
        {
            ["analyst"] = TmplWithSections(Section("history", true), Section(" history ", false)),
        })).Should().Contain("дважды");
    }

    [Fact]
    public void SetGlobal_ТекстСекцииНадЛимитом_Ошибка()
    {
        var store = NewStore();
        var tooLong = new string('а', SpecialtyPromptPresets.SectionTextLimit + 1);
        store.SetGlobal(Layer(new() { ["analyst"] = TmplWithSections(Section("history", true, tooLong)) }))
            .Should().Contain("длиннее");
    }

    [Fact]
    public void SetGlobal_НавыкБезИмени_Ошибка()
    {
        var store = NewStore();
        var profile = new SpecialtyTemplateSettings
        {
            DefaultBindings = [new SpecialtyDefaultBinding { Type = PersonaBindingType.Skill }],
        };
        store.SetGlobal(Layer(new() { ["analyst"] = profile }))
            .Should().Contain("не указано имя скилла");
    }

    [Fact]
    public void SetGlobal_ИмяСкиллаНеУНавыка_Ошибка()
    {
        var store = NewStore();
        var profile = new SpecialtyTemplateSettings
        {
            DefaultBindings =
            [
                new SpecialtyDefaultBinding { Type = PersonaBindingType.Notes, SkillName = "чужой скилл" },
            ],
        };
        store.SetGlobal(Layer(new() { ["analyst"] = profile }))
            .Should().Contain("только у типового умения типа «Навык»");
    }

    [Fact]
    public void SetGlobal_НавыкСИменем_ПроходитИНормализуется()
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
        store.SetGlobal(Layer(new() { ["analyst"] = profile })).Should().BeNull();

        var saved = store.Snapshot.Global.Specialties["analyst"].DefaultBindings!.Single();
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
        // Права эквивалентны дефолту кода, но запись несёт секции
        store.SetGlobal(Layer(new()
        {
            ["analyst"] = TmplWithSections(Section("history", true, "ценное")),
        }));

        var result = store.ResetModelSettings("analyst", apply: true);
        result.Changed.Should().Be(0, "снимать нечего — уровней в записи не было");
        store.Snapshot.Global.Specialties["analyst"].PromptSections!.Single().Text
            .Should().Be("ценное", "сброс МОДЕЛЕЙ не трогает секции промптов");

        // Запись с уровнями И секциями: уровни снимаются, секции остаются
        var mixed = new SpecialtyTemplateSettings
        {
            TierStrong = "glm-5.3",
            PromptSections = [Section("history", true, "тоже ценное")],
        };
        store.SetGlobal(Layer(new() { ["analyst"] = mixed }));
        store.ResetModelSettings("analyst", apply: true).Changed.Should().Be(1);
        var after = store.Snapshot.Global.Specialties["analyst"];
        after.TierStrong.Should().BeNull();
        after.PromptSections!.Single().Text.Should().Be("тоже ценное");
    }

    // --- Бенчмарк резолва (план «Секции промптов» этап 3, риск «цена резолва per-turn») ---

    [Fact]
    public void EffectivePromptSectionStates_Бенчмарк_УкладываетсяВПорог()
    {
        var store = NewStore();
        // Слой с настройками по всем 14 специальностям — резолв идёт по записи на каждый
        // вызов, а не по короткому «настроек нет вовсе»
        var specialties = new Dictionary<string, SpecialtyTemplateSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in SpecialtyCatalog.All.Where(e => e.Specialty != PersonaSpecialty.None))
            specialties[entry.Key] = TmplWithSections(
                Section("history", true, "переопределённый текст истории"),
                Section("codeGraph", true, "переопределённый текст графа"));
        store.SetGlobal(Layer(specialties));

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
