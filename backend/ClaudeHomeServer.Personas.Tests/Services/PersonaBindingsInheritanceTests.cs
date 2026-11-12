using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// A2 (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §2 «Типовые умения», «Миграция» п.4):
/// Skill-умения специальности наследуются в рантайме, а не копируются.
/// - эффективный список = Skill-умения специальности (первыми, в порядке профиля, inherited=true)
///   + свои привязки персоны; своя привязка с тем же SkillName гасит унаследованную
///   (Mode=Off — выключает целиком, любой другой Mode — своё решение персоны, без дубля);
/// - MaterializeDefaultBindingsAsync Skill-часть не материализует (AI-часть — копия);
/// - миграция (тот же флаг и маркер, что A1b) удаляет Skill-копии, совпавшие с профилем по
///   SkillName; сухой прогон только перечисляет их поимённо и ничего не пишет.
/// И1: ни материализация, ни запись привязок не пишут specialty-settings.json (хеш).
///
/// Мутации (проверяются на копиях дерева, scripts/mutation-copy.sh):
/// - вернуть Skill-цикл в MaterializeDefaultBindingsAsync — краснеет
///   Материализация_SkillЧастьПрофиляНеМатериализуется (applied=1 вместо 0, копия в сторе);
/// - EffectiveBindings переставляет свои перед унаследованными или не ставит Inherited —
///   краснеет ЭффективныйСписок_SkillУменияСпециальностиПервымиСПризнакомИсточника;
/// - снять гашение по SkillName — краснеет ЭффективныйСписок_СвояOffПривязка… (дубль в списке);
/// - снять шаг Skill в PersonaManager (удаление под локом) — краснеют
///   Миграция_СовпавшиеSkillКопииУдалены… и СухойПрогон_ПеречисляетУдаляемыеSkillПривязки…;
/// - снять lock(_saveLock) в применении плана — краснеет
///   SkillMigration_ДваПрогонаПараллельно_ОдинРезультат (сухой прогон видит полуприменённое
///   состояние: часть персон уже помечена и очищена, часть ещё нет).
/// </summary>
public class PersonaBindingsInheritanceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string SkillName = "code-review";

    private readonly string _tempDir;
    private readonly string _storePath;
    private readonly string _specialtyPath;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly string _userId;
    private readonly ProjectManager _projects;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly KnowledgeService _knowledge;
    private readonly SpecialtySettingsStore _specialty;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _sut;

    // Запись стора персон — как она лежит на диске (тот же сериализатор, что PersonaSpecialtyMigrationTests)
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public PersonaBindingsInheritanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pbind_inherit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _storePath = Path.Combine(_tempDir, "personas.json");
        _specialtyPath = Path.Combine(_tempDir, "specialty-settings.json");

        _config = Config();
        _users = TestSpecialtyStore.Users(_config);
        _userId = _users.GetFirst()!.Id;
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _wkStore = new WorkspaceKnowledgeStore(_config);
        _knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), _wkStore);
        _specialty = TestSpecialtyStore.Create(_config);
        _personas = new PersonaManager(_config, NullLogger<PersonaManager>.Instance, specialty: _specialty);
        _sut = BuildBindingsService(_specialty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private PersonaBindingsService BuildBindingsService(SpecialtySettingsStore specialtyStore) =>
        new(_personas, _projects, _wkStore, _knowledge, new SkillsService(), _users, _config,
            NullLogger<PersonaBindingsService>.Instance,
            specialtySettings: specialtyStore,
            // Плагин-навыки в тесте материализации: шов принимает всё — проверка существования
            // плагина здесь не предмет теста; она нужна, чтобы копия (если бы её материализовали)
            // прошла валидацию и мутация «вернуть цикл» была различима
            pluginSkillResolver: new Mock<IPluginSkillResolver>().Object);

    private IConfiguration Config(bool migrationOn = false) => TestConfig.Build(new Dictionary<string, string?>
    {
        ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        ["PersonasPath"] = _storePath,
        ["Ollama:Model"] = "",
        ["Personas:SpecialtyInheritanceMigration:Enabled"] = migrationOn ? "true" : "false",
    });

    // Типовой профиль Reviewer: Skill-умения с заданными именами. Без имён — профиль пустой
    // (дефолт кода у Reviewer — Project/ProjectPath, без Skill-записей — на них фильтр по типу)
    private void SetSkillProfile(SpecialtySettingsStore store, params string[] skillNames)
    {
        var layer = new SpecialtySettingsLayer();
        layer.Specialties[SpecialtyCatalog.KeyOf(PersonaSpecialty.Reviewer)] = new SpecialtyTemplateSettings
        {
            DefaultBindings = skillNames.Select(n => new SpecialtyDefaultBinding
            {
                Type = PersonaBindingType.Skill,
                SkillName = n,
                Condition = "когда читаю код",
            }).ToList(),
        };
        store.SetGlobal(layer).Should().BeNull();
    }

    private static Persona PersonaWith(PersonaSpecialty specialty, List<PersonaBinding>? bindings) =>
        new() { OwnerId = Owner, Name = "тест", Handle = "тест", Specialty = specialty, Bindings = bindings };

    private static PersonaBinding SkillBinding(string id, string target,
        PersonaBindingMode mode = PersonaBindingMode.Auto) =>
        new() { Id = id, Type = PersonaBindingType.Skill, Target = target, Mode = mode };

    private static PersonaBinding NoteBinding(string id, string target) =>
        new() { Id = id, Type = PersonaBindingType.Notes, Target = target };

    private static string Hash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    // --- Эффективный список ---

    [Fact]
    public void ЭффективныйСписок_SkillУменияСпециальностиПервымиСПризнакомИсточника()
    {
        SetSkillProfile(_specialty, SkillName, "run-tests");
        var persona = PersonaWith(PersonaSpecialty.Reviewer, [NoteBinding("n1", "personal")]);

        var eff = _sut.EffectiveBindings(_userId, persona);

        // Порядок контракта: сначала унаследованные в порядке профиля, затем свои
        eff.Select(e => (e.Binding.Type, e.Binding.Target, e.Inherited)).Should().Equal(
            (PersonaBindingType.Skill, SkillName, true),
            (PersonaBindingType.Skill, "run-tests", true),
            (PersonaBindingType.Notes, "personal", false));
        // Условие и режим — из профиля роли (их формулирует админ, не модель)
        eff[0].Binding.Condition.Should().Be("когда читаю код");
        eff[0].Binding.Mode.Should().Be(PersonaBindingMode.Auto);
        // Наследование — чтение, не запись: привязки персоны не изменились
        persona.Bindings!.Should().HaveCount(1);
    }

    [Fact]
    public void ЭффективныйСписок_СвояOffПривязкаСТемЖеИменемГаситУнаследованную_БезУчетаРегистра()
    {
        SetSkillProfile(_specialty, SkillName);
        // Имя в другом регистре — гашение по SkillName без учёта регистра
        // (то же сравнение, что FindToolBinding/ValidateAsync)
        var off = PersonaWith(PersonaSpecialty.Reviewer, [SkillBinding("s1", "CODE-Review", PersonaBindingMode.Off)]);
        _sut.EffectiveBindings(_userId, off)
            .Select(e => (e.Binding.Id, e.Inherited)).Should().Equal(("s1", false));

        // Своя привязка того же имени с любым другим Mode — тоже своё решение персоны:
        // унаследованная копия рядом с ней не дублируется
        var own = PersonaWith(PersonaSpecialty.Reviewer,
            [SkillBinding("s2", "code-review", PersonaBindingMode.Always)]);
        _sut.EffectiveBindings(_userId, own)
            .Select(e => (e.Binding.Id, e.Inherited)).Should().Equal(("s2", false));

        // Несовпавшая своя Skill-привязка гашения не даёт
        var other = PersonaWith(PersonaSpecialty.Reviewer, [SkillBinding("s3", "other-skill")]);
        _sut.EffectiveBindings(_userId, other).Should().HaveCount(2);
    }

    [Fact]
    public void ЭффективныйСписок_ДобавилиSkillВСпециальность_ИзменилсяБезПравкиПерсоны()
    {
        SetSkillProfile(_specialty); // профиль без Skill-умений
        var persona = PersonaWith(PersonaSpecialty.Reviewer, [NoteBinding("n1", "personal")]);
        _sut.EffectiveBindings(_userId, persona).Should().HaveCount(1);

        // Живая правка специальности — следующий читатель уже видит новое умение,
        // персона и её привязки не тронуты (И2: правка специальности меняет эффективный список)
        SetSkillProfile(_specialty, SkillName);
        var eff = _sut.EffectiveBindings(_userId, persona);
        eff.Should().HaveCount(2);
        eff[0].Inherited.Should().BeTrue();
        eff[0].Binding.Target.Should().Be(SkillName);
        persona.Bindings!.Should().ContainSingle(b => b.Id == "n1");
    }

    [Fact]
    public void ЭффективныйСписок_БитыйSkillNameСпециальностиПропущен()
    {
        // SetGlobal такой профиль не принимает («не указано имя скилла»), поэтому бита — как на
        // диске: правка файла руками. Читатель обязан пропустить её молча, не пустить в список.
        SetSkillProfile(_specialty, SkillName);
        var broken = File.ReadAllText(_specialtyPath).Replace($"\"{SkillName}\"", "\"\"");
        File.WriteAllText(_specialtyPath, broken);
        var brokenStore = TestSpecialtyStore.Create(_config);
        var sut = BuildBindingsService(brokenStore);

        var eff = sut.EffectiveBindings(_userId, PersonaWith(PersonaSpecialty.Reviewer, null));

        eff.Should().BeEmpty("битый SkillName (без имени) пропускается с логом");
    }

    [Fact]
    public void ЭффективныйСписок_БезСпециальностиИлиБезСтораНастроек_ТолькоСвои()
    {
        SetSkillProfile(_specialty, SkillName);

        // Specialty=None — профиля нет ни у кого: список = свои привязки
        var noSpecialty = PersonaWith(PersonaSpecialty.None, [NoteBinding("n1", "personal")]);
        _sut.EffectiveBindings(_userId, noSpecialty)
            .Select(e => e.Inherited).Should().Equal(false);

        // Сервис без стора настроек (юнит-сборка без него) — унаследованных брать не откуда
        var bare = new PersonaBindingsService(_personas, _projects, _wkStore, _knowledge,
            new SkillsService(), _users, _config, NullLogger<PersonaBindingsService>.Instance);
        bare.EffectiveBindings(_userId, PersonaWith(PersonaSpecialty.Reviewer, [NoteBinding("n1", "personal")]))
            .Select(e => e.Inherited).Should().Equal(false);
        bare.EffectiveBindings(_userId, PersonaWith(PersonaSpecialty.Reviewer, null)).Should().BeEmpty();
    }

    [Fact]
    public void ПривязкиХода_УнаследованныеВходятOffИсключаются()
    {
        SetSkillProfile(_specialty, SkillName);
        var persona = PersonaWith(PersonaSpecialty.Reviewer,
            [NoteBinding("n1", "personal"), SkillBinding("s1", "other-skill", PersonaBindingMode.Off)]);

        // Проводка BuildTurnBlock/BuildSubagentIndex/BuildAlwaysExtracts: унаследованное умение
        // доходит до индекса промпта без всякой копии в сторе; Off — нет
        _sut.ActiveBindingsForTurn(_userId, persona)
            .Select(b => b.Target).Should().Equal(SkillName, "personal");
    }

    // --- Материализация: Skill-часть больше не копируется ---

    [Fact]
    public async Task Материализация_SkillЧастьПрофиляНеМатериализуется_ИНеПишетНастройкиСпециальностей()
    {
        SetSkillProfile(_specialty, "plugin:test/slug/skill-a");
        WriteStore(Stored("gen", PersonaSpecialty.Reviewer));
        var manager = NewManager(Config()); // флаг выключен: сторона чтения не тронута
        var persona = manager.Get("gen", Owner)!;

        var settingsHash = Hash(_specialtyPath);
        var crud = BuildCrudService(manager);

        // Плагин-имя выбрано сознательно и в корректном формате plugin:{publisher}/{slug}/{skill}
        // (TryParse требует минимум трёх сегментов): его ValidateAsync пропускает через мок-шов,
        // поэтому мутация «вернуть Skill-цикл» дала бы applied=1 и запись в стор — тест краснеет.
        // Обычное имя глобального скилла мутацию не проверяло бы: его отбила бы валидация
        // («Скилл не найден среди глобальных») и разница была бы неразличима.
        var (updated, applied) = await crud.MaterializeDefaultBindingsAsync(Owner, persona);

        applied.Should().Be(0, "Skill-часть профиля не материализуется: она наследуется в рантайме");
        var raw = manager.GetRaw("gen", Owner)!;
        (raw.Bindings?.Count ?? 0).Should().Be(0, "копии унаследованного умения в сторе нет");
        // И1: «apply-defaults пишет только Persona.Bindings» — настройки специальностей целы
        Hash(_specialtyPath).Should().Be(settingsHash);
        updated.Id.Should().Be("gen");
    }

    private PersonasCrudService BuildCrudService(PersonaManager manager) =>
        new(personas: manager, projects: _projects,
            // Тяжёлые зависимости, которых этот метод не касается (на tested-пути ни одна не
            // разыменована) — только ради сигнатуры конструктора (прецедент null! в тестах решения)
            sessions: null!, users: _users, memory: null!,
            bindings: _sut, skills: new SkillsService(), oneShot: null!,
            cheap: new Mock<ICheapTextRunner>().Object,
            specialtyTemplates: new SpecialtyTemplatesService(_specialty),
            specialtySettings: _specialty, config: _config,
            log: NullLogger<PersonasCrudService>.Instance, broadcaster: null!);

    [Fact]
    public void ЗаписьПривязок_ПишетТолькоPersonas_ХешSpecialtySettingsНеИзменяется()
    {
        SetSkillProfile(_specialty, SkillName);
        WriteStore(Stored("gen", PersonaSpecialty.Reviewer));
        var manager = NewManager(Config());

        var settingsHash = Hash(_specialtyPath);
        // Терминальная запись пути personas_bindings_set (PersonasCrudService.SetBindingsAsync
        // → PersonaManager.UpdateBindings): пишет только Persona.Bindings
        manager.UpdateBindings("gen", Owner, [NoteBinding("n1", "personal")]);

        Hash(_specialtyPath).Should().Be(settingsHash, "И1: ни один путь правки персоны не пишет профиль специальности");
        manager.GetRaw("gen", Owner)!.Bindings!.Should().ContainSingle(b => b.Id == "n1");
    }

    // --- Миграция: удаление совпавших Skill-копий (тот же флаг и маркер, что A1b) ---

    [Fact]
    public void Миграция_СовпавшиеSkillКопииУдалены_НесовпавшиеИAIПривязкиЦелы()
    {
        var config = Config(migrationOn: true);
        var specialty = TestSpecialtyStore.Create(config);
        SetSkillProfile(specialty, SkillName);
        WriteStore(Stored("vera", PersonaSpecialty.Reviewer, [
            SkillBinding("copy", "Code-Review"),      // копия профиля (регистр не мешает совпадению)
            SkillBinding("own-other", "other-skill"), // несовпавшая — остаётся
            NoteBinding("ai-note", "personal"),       // AI-привязка — не трогается
        ]));

        var manager = NewManager(config, specialty); // при загрузке — тот же пуск, что у прав

        var raw = manager.GetRaw("vera", Owner)!;
        raw.Bindings!.Select(b => b.Id).Should().Equal("own-other", "ai-note");
        raw.InheritsAccessMigratedAt.Should().NotBeNull("та же идемпотентность по маркеру, что у прав");
        // Стор после применения: копия удалена, остальное цело
        var stored = File.ReadAllText(_storePath);
        stored.Should().Contain("own-other").And.Contain("personal").And.NotContain("Code-Review");
    }

    [Fact]
    public void СухойПрогон_ПеречисляетУдаляемыеSkillПривязки_НичегоНеПишет()
    {
        var config = Config(); // флаг выключен: сухой прогон доступен всегда
        var specialty = TestSpecialtyStore.Create(config);
        SetSkillProfile(specialty, SkillName);
        WriteStore(Stored("vera", PersonaSpecialty.Reviewer, [
            SkillBinding("copy", SkillName), SkillBinding("own-other", "other-skill")]));

        var manager = NewManager(config, specialty);
        var personasHash = Hash(_storePath);
        var settingsHash = Hash(_specialtyPath);

        var plan = manager.DryRunSpecialtyInheritance();

        // Отчёт поимённо: какая привязка и с каким именем удалится прогоном (Андрей смотрит ДО включения)
        var d = plan.Decisions.Single(x => x.PersonaId == "vera");
        d.RemovedSkills.Should().ContainSingle(r => r.BindingId == "copy" && r.Target == SkillName);
        plan.CountRemovedSkills().Should().Be(1);
        // Ничего не пишет: ни personas.json, ни specialty-settings.json; решения не применены
        Hash(_storePath).Should().Be(personasHash);
        Hash(_specialtyPath).Should().Be(settingsHash);
        manager.GetRaw("vera", Owner)!.Bindings!.Should().HaveCount(2);
        manager.GetRaw("vera", Owner)!.InheritsAccessMigratedAt.Should().BeNull();
    }

    [Fact]
    public void Миграция_ПовторныйПрогон_НольИзменений()
    {
        var config = Config(migrationOn: true);
        var specialty = TestSpecialtyStore.Create(config);
        SetSkillProfile(specialty, SkillName);
        WriteStore(Stored("vera", PersonaSpecialty.Reviewer, [SkillBinding("copy", SkillName)]));

        NewManager(config, specialty); // первый пуск при загрузке: копия удалена, маркер стоит
        var hashAfterFirst = Hash(_storePath);

        var again = NewManager(config, specialty);
        var plan = again.RunSpecialtyInheritanceMigration();

        plan.Pending.Should().BeEmpty("маркер стоит — пересматриваемых нет");
        plan.CountRemovedSkills().Should().Be(0, "решённая маркером персона не порождает строк удаления");
        Hash(_storePath).Should().Be(hashAfterFirst, "повторный прогон не пишет стор");
    }

    [Fact]
    public async Task SkillMigration_ДваПрогонаПараллельно_ОдинРезультат()
    {
        var config = Config(migrationOn: true);
        var specialty = TestSpecialtyStore.Create(config);
        SetSkillProfile(specialty, SkillName);
        // 300 персон: применение под локом — наблюдатель сериализован и видит «0 или все»;
        // без лока цикл применения (сотни мс) перемешивается со снимком сухого прогона —
        // на 60 персон окно было слишком коротким и мутация «снять лок» проходила незамеченной
        var ids = Enumerable.Range(0, 300).Select(i => $"p{i}").ToList();
        WriteStore(ids.Select(id => Stored(id, PersonaSpecialty.Reviewer,
            [SkillBinding(id + ":copy", SkillName), SkillBinding(id + ":own", "other-skill")])).ToArray());

        var manager = NewManager(config, specialty);
        // Вернём состояние «ещё не решено»: первый пуск при загрузке уже прошёл — снимем маркеры
        // и вернём копии в память (тот же приём, что DryRun_ПараллельноСUpdate в A1b-тестах)
        foreach (var id in ids)
        {
            var raw = manager.GetRaw(id, Owner)!;
            raw.InheritsAccessMigratedAt = null;
            raw.Bindings = [SkillBinding(id + ":copy", SkillName), SkillBinding(id + ":own", "other-skill")];
        }

        var apply1 = Task.Run(manager.RunSpecialtyInheritanceMigration);
        var apply2 = Task.Run(manager.RunSpecialtyInheritanceMigration);
        var torn = 0;
        for (var i = 0; i < 300 && !(apply1.IsCompleted && apply2.IsCompleted); i++)
        {
            // Снимок и применение — под одним _saveLock: наблюдатель видит или «ещё не решён никто»,
            // или «решены все». Полуприменённое состояние = прогоны перемешались (без лока — красное)
            var marked = manager.DryRunSpecialtyInheritance().Decisions.Count(d => d.AlreadyMigrated);
            // (not `is > 0 and < ids.Count`: реляционные паттерны требуют констант — ids.Count не константа)
            if (marked > 0 && marked < ids.Count) Interlocked.Increment(ref torn);
        }
        await Task.WhenAll(apply1, apply2);
        torn.Should().Be(0, "два прогона под локом стора не перемешиваются с отчётом и друг с другом");

        // Итоговый файл — как после одного: копии удалены, маркеры стоят, несовпавшие целы
        foreach (var id in ids)
        {
            var raw = manager.GetRaw(id, Owner)!;
            raw.InheritsAccessMigratedAt.Should().NotBeNull();
            raw.Bindings!.Select(b => b.Id).Should().Equal(id + ":own");
        }

        var third = manager.RunSpecialtyInheritanceMigration();
        third.Pending.Should().BeEmpty();
        var hashAfter = Hash(_storePath);
        manager.RunSpecialtyInheritanceMigration();
        Hash(_storePath).Should().Be(hashAfter, "после завершённой миграции писать нечего");
    }

    // --- helpers для тестов миграции (образец PersonaSpecialtyMigrationTests) ---

    private static Persona Stored(string id, PersonaSpecialty specialty, List<PersonaBinding>? bindings = null) =>
        new()
        {
            Id = id,
            OwnerId = Owner,
            Name = id,
            Handle = id,
            Specialty = specialty,
            Bindings = bindings,
            UpdatedAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
        };

    private void WriteStore(params Persona[] personas) =>
        File.WriteAllText(_storePath, JsonSerializer.Serialize(personas.ToList(), JsonOpts));

    private static PersonaManager NewManager(IConfiguration config, SpecialtySettingsStore? specialty = null) =>
        new(config, NullLogger<PersonaManager>.Instance, specialty: specialty);
}
