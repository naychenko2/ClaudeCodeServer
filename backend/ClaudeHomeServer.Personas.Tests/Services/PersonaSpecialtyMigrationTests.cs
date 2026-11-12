using System.Net;
using System.Security.Cryptography;
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

/// <summary>
/// A1b: разовая миграция прав персон к наследованию от специальности (разрез §2 «Миграция»)
/// и соглашение «нет эффективного шаблона ≠ Full» (M3 из R-A1a) — в Hydrate и в миграции.
/// </summary>
public class PersonaSpecialtyMigrationTests : IDisposable
{
    private const string Owner = "owner-1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _tempDir;
    private readonly string _storePath;
    private readonly string _specialtyPath;

    public PersonaSpecialtyMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pmgr_specmig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "personas.json");
        _specialtyPath = Path.Combine(_tempDir, "specialty-settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private IConfiguration Config(bool migrationOn = false) => TestConfig.Build(new Dictionary<string, string?>
    {
        ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        ["PersonasPath"] = _storePath,
        ["Ollama:Model"] = "",
        ["Personas:SpecialtyInheritanceMigration:Enabled"] = migrationOn ? "true" : "false",
    });

    // Запись как она лежит на диске: права материализованы, флаг наследования — отдельное поле
    private static Persona Stored(string id, PersonaSpecialty specialty,
        PersonaAccess access = PersonaAccess.Full, List<string>? tools = null, List<string>? disallowed = null,
        bool inherits = false, string? tierMedium = null, DateTime? updatedAt = null) => new()
    {
        Id = id,
        OwnerId = Owner,
        Name = id,
        Handle = id,
        Specialty = specialty,
        Access = access,
        Tools = tools,
        DisallowedTools = disallowed,
        InheritsAccess = inherits,
        TierMedium = tierMedium,
        UpdatedAt = updatedAt ?? new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
    };

    private void WriteStore(params Persona[] personas) =>
        File.WriteAllText(_storePath, JsonSerializer.Serialize(personas.ToList(), JsonOpts));

    private static PersonaManager NewManager(IConfiguration config, SpecialtySettingsStore specialty) =>
        new(config, NullLogger<PersonaManager>.Instance, specialty: specialty);

    // Глобальный слой:Reviewer с заданными правами и средней ячейкой. Analyst слоя не получает
    // (и дефолта кода у неё нет) — это «нет эффективного шаблона»
    private static SpecialtySettingsStore SpecialtyStore(IConfiguration config,
        PersonaAccess? reviewerAccess = null, List<string>? reviewerTools = null, string? reviewerTierMedium = null)
    {
        var store = TestSpecialtyStore.Create(config);
        var layer = new SpecialtySettingsLayer();
        if (reviewerAccess is not null)
            layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
            {
                Access = reviewerAccess.Value,
                Tools = reviewerTools,
                TierMedium = reviewerTierMedium,
            };
        store.SetGlobal(layer);
        return store;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // Чистая функция: план по готовым персонам, без стора
    private static PersonaSpecialtyMigration.Plan PlanOf(
        IEnumerable<Persona> personas,
        Func<string, PersonaSpecialty, SpecialtyTemplate?> templateOf,
        Func<string, PersonaSpecialty, PersonaSpecialtyMigration.SpecialtyCells?>? cellsOf = null) =>
        PersonaSpecialtyMigration.Build(personas, templateOf, cellsOf);

    private static PersonaSpecialtyMigration.Decision DecisionOf(
        PersonaSpecialtyMigration.Plan plan, string id) =>
        plan.Decisions.Single(d => d.PersonaId == id);

    // Позиционных аргументов Update (name..memoryEnabled) — 11, их пропускаать нельзя
    private static void UpdateName(PersonaManager manager, string id, string name) =>
        manager.Update(id, Owner, name, null, null, null, null, null, null, null, null, null, null);

    private static readonly Func<string, PersonaSpecialty, SpecialtyTemplate?> ReviewerTasksOnly =
        (_, specialty) => specialty == PersonaSpecialty.Reviewer
            ? new SpecialtyTemplate(PersonaAccess.ReadOnly, ["tasks"], null)
            : null;

    [Fact]
    public void План_ГруппаСовпалаСШаблоном_НаследованиеTrue_Отличающаяся_False()
    {
        var plan = PlanOf(
            [
                Stored("match", PersonaSpecialty.Reviewer, PersonaAccess.ReadOnly, ["tasks"]),
                Stored("override", PersonaSpecialty.Reviewer, PersonaAccess.Full),
                Stored("none-full", PersonaSpecialty.None),
                Stored("none-override", PersonaSpecialty.None, PersonaAccess.ReadOnly, ["tasks"]),
                Stored("no-template", PersonaSpecialty.Analyst, PersonaAccess.ReadOnly, ["tasks"]),
            ],
            ReviewerTasksOnly);

        DecisionOf(plan, "match").Group.Should().Be(PersonaSpecialtyMigration.Group.Inherits);
        DecisionOf(plan, "none-full").Group.Should().Be(PersonaSpecialtyMigration.Group.Inherits,
            "None — осознанный шаблон кода (Full без ограничений): у персоны ровно он же");
        DecisionOf(plan, "override").Group.Should().Be(PersonaSpecialtyMigration.Group.Override);
        DecisionOf(plan, "none-override").Group.Should().Be(PersonaSpecialtyMigration.Group.Override,
            "None даёт наследование только при Full без ограничений");
        DecisionOf(plan, "no-template").Group.Should().Be(PersonaSpecialtyMigration.Group.NoTemplate,
            "специальность ≠ None и шаблона нет: наследовать нечего, свои права остаются");

        foreach (var id in new[] { "match", "none-full" })
            DecisionOf(plan, id).InheritsAccess.Should().BeTrue();
        foreach (var id in new[] { "override", "none-override", "no-template" })
            DecisionOf(plan, id).InheritsAccess.Should().BeFalse();

        plan.CountOf(PersonaSpecialtyMigration.Group.Inherits).Should().Be(2);
        plan.CountOf(PersonaSpecialtyMigration.Group.Override).Should().Be(2);
        plan.CountOf(PersonaSpecialtyMigration.Group.NoTemplate).Should().Be(1);
        // До миграции у всех записей InheritsAccess=false → правка значений только у совпавших
        plan.CountChanges().Should().Be(2);
    }

    [Fact]
    public void План_ГруппаСравниваетсяНормализованно_ПолныйНаборРавенNull_ЗапретВнеCustomНеСчитается()
    {
        var plan = PlanOf(
            [
                // Полный набор инструментов = «без ограничений» = null стора: та же группа
                Stored("all-tools", PersonaSpecialty.Reviewer, PersonaAccess.Full, ["tasks", "notes", "web"]),
                // Свой запрет при Full не живёт (PersonaAccessPolicy) — группа равна шаблону
                Stored("disallowed-ignored", PersonaSpecialty.Reviewer, PersonaAccess.Full, null, ["Edit"]),
                // При Custom тот же запрет, что у шаблона, — равна; другой — своя группа
                Stored("custom-same", PersonaSpecialty.Tester, PersonaAccess.Custom, null, ["Bash"]),
                Stored("custom-differs", PersonaSpecialty.Tester, PersonaAccess.Custom, null, ["Write"]),
            ],
            (_, specialty) => specialty switch
            {
                PersonaSpecialty.Reviewer => new SpecialtyTemplate(PersonaAccess.Full, ["tasks", "notes", "web"], ["Bash"]),
                PersonaSpecialty.Tester => new SpecialtyTemplate(PersonaAccess.Custom, null, ["Bash"]),
                _ => null,
            });

        DecisionOf(plan, "all-tools").Group.Should().Be(PersonaSpecialtyMigration.Group.Inherits);
        DecisionOf(plan, "disallowed-ignored").Group.Should().Be(PersonaSpecialtyMigration.Group.Inherits);
        DecisionOf(plan, "custom-same").Group.Should().Be(PersonaSpecialtyMigration.Group.Inherits);
        DecisionOf(plan, "custom-differs").Group.Should().Be(PersonaSpecialtyMigration.Group.Override);
    }

    [Fact]
    public void План_ОтчётДетерминирован_ПоИмениЗатемId()
    {
        var plan = PlanOf(
            [Stored("b", PersonaSpecialty.None), Stored("a", PersonaSpecialty.None), Stored("c", PersonaSpecialty.None)],
            (_, _) => null);

        plan.Decisions.Select(d => d.PersonaId).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void План_ЯчейкиРавныеСпециальности_ПоимённоЧужиеНет()
    {
        var plan = PlanOf(
            [
                Stored("match", PersonaSpecialty.Reviewer, tierMedium: "glm-5.2"),
                Stored("other", PersonaSpecialty.Reviewer, tierMedium: "deepseek"),
                Stored("empty", PersonaSpecialty.Reviewer),
                // Без специальности сравнивать не с чем
                Stored("no-specialty", PersonaSpecialty.None, tierMedium: "glm-5.2"),
            ],
            (_, _) => null,
            (_, _) => new PersonaSpecialtyMigration.SpecialtyCells(null, "glm-5.2", null));

        plan.ModelCells.Should().ContainSingle();
        plan.ModelCells.Single().PersonaId.Should().Be("match");
        plan.ModelCells.Single().Cell.Should().Be(nameof(Persona.TierMedium));
        plan.ModelCells.Single().Value.Should().Be("glm-5.2");
    }

    [Fact]
    public void МиграцияПриЗагрузке_РешаетКаждуюПерсонуИСтавитМаркер_UpdatedAtНеТронут()
    {
        var config = Config(migrationOn: true);
        var specialty = SpecialtyStore(config, PersonaAccess.ReadOnly, ["tasks"]);
        var stamp = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        WriteStore(
            Stored("match", PersonaSpecialty.Reviewer, PersonaAccess.ReadOnly, ["tasks"], updatedAt: stamp),
            Stored("override", PersonaSpecialty.Reviewer, PersonaAccess.Full, updatedAt: stamp),
            Stored("no-template", PersonaSpecialty.Analyst, PersonaAccess.ReadOnly, ["tasks"], updatedAt: stamp));

        var manager = NewManager(config, specialty);

        manager.GetRaw("match", Owner)!.InheritsAccess.Should().BeTrue();
        manager.GetRaw("override", Owner)!.InheritsAccess.Should().BeFalse("своя группа сохраняется");
        manager.GetRaw("no-template", Owner)!.InheritsAccess.Should().BeFalse("наследовать нечего");
        foreach (var id in new[] { "match", "override", "no-template" })
        {
            manager.GetRaw(id, Owner)!.InheritsAccessMigratedAt.Should().NotBeNull(
                "маркер ставится и подтверждённым: иначе следующий пуск пересчитал бы их заново");
            manager.GetRaw(id, Owner)!.UpdatedAt.Should().Be(stamp, "миграция не перетасовывает список персон");
        }
        File.ReadAllText(_storePath).Should().Contain(nameof(Persona.InheritsAccessMigratedAt));
    }

    [Fact]
    public void Миграция_ПовторныйПрогонНичегоНеПишет_0Изменений()
    {
        var config = Config(migrationOn: true);
        var specialty = SpecialtyStore(config, PersonaAccess.ReadOnly, ["tasks"]);
        WriteStore(
            Stored("match", PersonaSpecialty.Reviewer, PersonaAccess.ReadOnly, ["tasks"]),
            Stored("override", PersonaSpecialty.Reviewer, PersonaAccess.Full));

        var first = NewManager(config, specialty);
        var hashAfterFirst = Hash(_storePath);

        var again = NewManager(config, specialty);
        var plan = again.RunSpecialtyInheritanceMigration();

        plan.Pending.Should().BeEmpty("маркер стоит у всех — повторная миграция никого не пересматривает");
        plan.CountChanges().Should().Be(0);
        Hash(_storePath).Should().Be(hashAfterFirst, "повторный прогон не пишет стор");
        again.GetRaw("match", Owner)!.InheritsAccess.Should().BeTrue();
    }

    [Fact]
    public void СухойПрогон_НичегоНеПишет_НиPersonasНиSpecialtySettings()
    {
        var config = Config(); // флаг выключен: сухой прогон доступен всегда
        var specialty = SpecialtyStore(config, PersonaAccess.ReadOnly, ["tasks"]);
        WriteStore(
            Stored("match", PersonaSpecialty.Reviewer, PersonaAccess.ReadOnly, ["tasks"]),
            Stored("override", PersonaSpecialty.Reviewer, PersonaAccess.Full),
            Stored("no-template", PersonaSpecialty.Analyst));

        var manager = NewManager(config, specialty);
        var personasHash = Hash(_storePath);
        var settingsHash = Hash(_specialtyPath);

        var plan = manager.DryRunSpecialtyInheritance();

        plan.Decisions.Should().HaveCount(3);
        plan.CountOf(PersonaSpecialtyMigration.Group.NoTemplate).Should().Be(1);
        Hash(_storePath).Should().Be(personasHash);
        Hash(_specialtyPath).Should().Be(settingsHash);
        manager.GetRaw("match", Owner)!.InheritsAccess.Should().BeFalse("решения не применяются");
        manager.GetRaw("match", Owner)!.InheritsAccessMigratedAt.Should().BeNull();
    }

    [Fact]
    public void ЗагрузкаПриВыключенномФлаге_СторНеТронут()
    {
        var config = Config();
        var specialty = SpecialtyStore(config, PersonaAccess.ReadOnly, ["tasks"]);
        WriteStore(
            Stored("match", PersonaSpecialty.Reviewer, PersonaAccess.ReadOnly, ["tasks"]),
            Stored("no-template", PersonaSpecialty.Analyst));
        var personasHash = Hash(_storePath);
        var settingsHash = Hash(_specialtyPath);

        var manager = NewManager(config, specialty);

        Hash(_storePath).Should().Be(personasHash);
        Hash(_specialtyPath).Should().Be(settingsHash);
        manager.GetRaw("match", Owner)!.InheritsAccessMigratedAt.Should().BeNull();
    }

    [Fact]
    public void ЯчейкиМоделейПоказаныВОтчёте_ВСтореНеЧищены()
    {
        var config = Config(migrationOn: true);
        var specialty = SpecialtyStore(config, PersonaAccess.Full, null, reviewerTierMedium: "glm-5.2");
        WriteStore(
            Stored("vera", PersonaSpecialty.Reviewer, tierMedium: "glm-5.2"),
            Stored("kuzma", PersonaSpecialty.Reviewer, tierMedium: "deepseek"));

        var manager = NewManager(config, specialty);
        var plan = manager.DryRunSpecialtyInheritance();

        plan.ModelCells.Should().ContainSingle(c => c.PersonaId == "vera" && c.Cell == "TierMedium");
        // Шаг A1b ячейки не чистит (вопрос 2 Андрею): обе остаются в сторе
        File.ReadAllText(_storePath).Should().Contain("glm-5.2").And.Contain("deepseek");
    }

    // Соглашение M3, путь чтения №1 — Hydrate: «нет шаблона» не превращает персону в Full
    [Fact]
    public void Hydrate_НетЭффективногоШаблона_ОтдаётМатериализованныеПраваНеFull()
    {
        var config = Config();
        var specialty = SpecialtyStore(config); // глобальный слой пуст, у Analyst дефолта кода нет
        WriteStore(Stored("analyst", PersonaSpecialty.Analyst, PersonaAccess.ReadOnly, ["tasks"], inherits: true));

        var manager = NewManager(config, specialty);
        var got = manager.Get("analyst", Owner)!;

        got.Access.Should().Be(PersonaAccess.ReadOnly, "нет шаблона — не Full");
        got.Tools.Should().Equal("tasks");
        manager.GetRaw("analyst", Owner)!.Access.Should().Be(PersonaAccess.ReadOnly);
    }

    // Путь чтения №2 — миграция: такая персона уходит в группу «нет шаблона», права её собственные
    [Fact]
    public void Миграция_НетЭффективногоШаблона_НаследованиеFalse_ГруппаОтдельная()
    {
        var config = Config(migrationOn: true);
        var specialty = SpecialtyStore(config);
        WriteStore(Stored("analyst", PersonaSpecialty.Analyst, PersonaAccess.ReadOnly, ["tasks"], inherits: true));

        var manager = NewManager(config, specialty);

        var raw = manager.GetRaw("analyst", Owner)!;
        raw.InheritsAccess.Should().BeFalse();
        raw.InheritsAccessMigratedAt.Should().NotBeNull();
        manager.Get("analyst", Owner)!.Access.Should().Be(PersonaAccess.ReadOnly);
        manager.Get("analyst", Owner)!.Tools.Should().Equal("tasks");

        DecisionOf(manager.DryRunSpecialtyInheritance(), "analyst")
            .Group.Should().Be(PersonaSpecialtyMigration.Group.NoTemplate);
    }

    // Админ снял глобальный шаблон: существующая персона остаётся при своих правах
    [Fact]
    public void Hydrate_ШаблонСнят_ПраваПерсоныЦелы()
    {
        var config = Config();
        var specialty = SpecialtyStore(config, PersonaAccess.ReadOnly, ["tasks"]);
        WriteStore(Stored("reviewer", PersonaSpecialty.Reviewer, PersonaAccess.Full, inherits: true));
        var manager = NewManager(config, specialty);
        manager.Get("reviewer", Owner)!.Access.Should().Be(PersonaAccess.ReadOnly);

        specialty.SetGlobal(new SpecialtySettingsLayer());

        var got = manager.Get("reviewer", Owner)!;
        got.Access.Should().Be(PersonaAccess.Full, "материализованное значение персоны");
        got.Tools.Should().BeNull();
    }

    /// <summary>
    /// Сухой прогон параллельно с живой правкой: ничего не пишет, правку не теряет и не отдаёт
    /// смешанный снимок. Смешение различимо по построению: половина персон совпадает с шаблоном A,
    /// половина — с шаблоном B. Снимок, целиком взятый при A, обязан дать ровно {A-совпадающие:
    /// Inherits, B-совпадающие: Override} (и зеркально при B). Обе группы Inherits — значит
    /// шаблон сменился СРЕДИ чтения: без лока стора отчёт собирался бы из двух моментов.
    /// </summary>
    [Fact]
    public async Task DryRun_ПараллельноСUpdate_НеПишетИНеТеряет()
    {
        // Флаг включён: без него миграция ничего не делает и вторая часть теста была бы пустой
        var config = Config(migrationOn: true);
        var specialty = SpecialtyStore(config, PersonaAccess.Custom, null);
        // A-совпадающие: Custom + запрет Edit (именно их просит шаблон A);
        // B-совпадающие: Full без ограничений (именно их просит шаблон B)
        var store = new List<Persona>();
        for (var i = 0; i < 60; i++)
        {
            store.Add(Stored($"a{i}", PersonaSpecialty.Reviewer, PersonaAccess.Custom, null, ["Edit"]));
            store.Add(Stored($"b{i}", PersonaSpecialty.Reviewer, PersonaAccess.Full));
        }
        WriteStore(store.ToArray());
        var manager = NewManager(config, specialty);

        var mixed = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
                SetTemplate(specialty, i % 2 == 0 ? TemplateA() : TemplateB());
        });

        Parallel.For(0, 40, _ =>
        {
            var plan = manager.DryRunSpecialtyInheritance();
            var aInherits = plan.Decisions.Count(d => d.PersonaId.StartsWith("a")
                && d.Group == PersonaSpecialtyMigration.Group.Inherits);
            // Снимок при B: a-персоны не совпадают ни с каким состоянием B-шаблона
            var whole = aInherits == 60 || aInherits == 0;
            if (!whole) Interlocked.Increment(ref mixed);
        });
        stop.Cancel();
        await writer;

        mixed.Should().Be(0, "план по специальности строится по ОДНОМУ снимку шаблона: резолв на каждую персону смешал бы два момента");

        // Миграция при загрузке — под локом стора. Снимем маркеры и пустим миграцию рядом с
        // сухим прогоном: прогон обязан видеть цельный снимок — либо ещё не решён никто, либо
        // решены все. Полупрогон (часть персон уже помечена) значит, что миграция шла рядом
        var pendingIds = store.Select(p => p.Id);
        foreach (var id in pendingIds) manager.GetRaw(id, Owner)!.InheritsAccessMigratedAt = null;

        var torn = 0;
        var apply = Task.Run(() => manager.RunSpecialtyInheritanceMigration());
        for (var i = 0; i < 200; i++)
        {
            var marked = manager.DryRunSpecialtyInheritance().Decisions.Count(d => d.AlreadyMigrated);
            if (marked is > 0 and < 120) Interlocked.Increment(ref torn);
        }
        await apply;
        torn.Should().Be(0, "миграция и отчёт не перемешиваются: оба под локом стора");
        manager.DryRunSpecialtyInheritance().Decisions.Should().OnlyContain(d => d.AlreadyMigrated);

        var hashBefore = Hash(_storePath);
        UpdateName(manager, "a0", "последняя правка");
        var hashAfter = Hash(_storePath);
        hashAfter.Should().NotBe(hashBefore, "правка пишет стор — только она");
        // В JSON кириллица экранирована — сверяем разобранное значение, а не подстроку файла
        JsonSerializer.Deserialize<List<Persona>>(File.ReadAllText(_storePath), JsonOpts)!
            .First(p => p.Id == "a0").Name.Should().Be("последняя правка");

        manager.DryRunSpecialtyInheritance();
        Hash(_storePath).Should().Be(hashAfter, "сухой прогон после правки ничего не пишет и правку не теряет");
    }

    private static SpecialtyTemplate TemplateA() =>
        new(PersonaAccess.Custom, null, ["Edit"]);

    private static SpecialtyTemplate TemplateB() => new(PersonaAccess.Full, null, null);

    private static void SetTemplate(SpecialtySettingsStore store, SpecialtyTemplate template)
    {
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
        {
            Access = template.Access,
            Tools = template.Tools?.ToList(),
            DisallowedTools = template.DisallowedTools?.ToList(),
        };
        store.SetGlobal(layer);
    }
}

/// <summary>
/// Ручка сухого прогона: только админ (отчёт поимённый по персон всех владельцев),
/// dryRun=true ничего не пишет, без dryRun и при выключенном флаге — 400 с указанием флага.
/// </summary>
[Trait("Category", "Host")]
public class PersonaSpecialtyInheritanceEndpointTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Ручка_БезРолиAdmin403_СAdmin200()
    {
        var user = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername,
            TestWebApplicationFactory.SecondPassword);
        var denied = await user.PostAsync("/api/personas/migrations/specialty-inheritance?dryRun=true", null);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = _factory.CreateAuthenticatedClient();
        var ok = await admin.PostAsync("/api/personas/migrations/specialty-inheritance?dryRun=true", null);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await ok.Content.ReadAsStringAsync());
        body.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        body.GetProperty("counts").GetProperty("noTemplate").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task РучкаБезDryRun_ПриВыключенномФлаге400()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var resp = await admin.PostAsync("/api/personas/migrations/specialty-inheritance", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
