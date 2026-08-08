using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Чистая логика привязок персон: Tool-рубильники (EffectiveToolEnabled), сборка индекса
// для системного промпта (BuildIndex) и валидация привязок (ValidateAsync).
public class PersonaBindingsServiceTests : IDisposable
{
    private const string Username = "test-user";

    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _sut;
    private readonly ClaudeHomeServer.Services.Mcp.McpRegistry _mcp;
    private readonly FeatureFlagService _flags;
    private readonly string _userId;

    public PersonaBindingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pbind_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        _users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userId = _users.GetFirst()!.Id; // дефолтный admin пустого стора
        var appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, _users, appSettings);
        _personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _users, config,
            NullLogger<NotesKnowledgeService>.Instance);

        _mcp = new ClaudeHomeServer.Services.Mcp.McpRegistry(config,
            new ClaudeHomeServer.Services.Mcp.McpSecretStore(config));
        _flags = new FeatureFlagService(_users);
        _sut = new PersonaBindingsService(_personas, _projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _users, config,
            NullLogger<PersonaBindingsService>.Instance, _mcp, _flags);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private Persona MakePersona(List<string>? tools = null, List<PersonaBinding>? bindings = null,
        PersonaSpecialty specialty = PersonaSpecialty.None, PersonaAccess access = PersonaAccess.Full) =>
        new() { OwnerId = _userId, Name = "Тест", Tools = tools, Bindings = bindings, Specialty = specialty, Access = access };

    private static PersonaBinding ToolBinding(string target, PersonaBindingMode mode) =>
        new() { Type = PersonaBindingType.Tool, Target = target, Condition = "по запросу", Mode = mode };

    private Project MakeProject(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        return _projects.Create(name, dir, _userId, Username);
    }

    // --- Видимость Dify-датасета по префиксу владельца ---

    [Theory]
    // Нет префикса (двоеточия) — общий/ничей датасет → показываем
    [InlineData("SharedKnowledge", true)]
    [InlineData("общая-база", true)]
    // Префикс совпадает с текущим пользователем → показываем
    [InlineData("me:notes", true)]
    [InlineData("ME:persona:reviewer", true)]   // регистронезависимо
    [InlineData("me:RR:Узбекистан", true)]      // многоуровневый — важен префикс до первого ':'
    // Любой чужой префикс (даже незарегистрированного/бывшего пользователя) → прячем
    [InlineData("bob:notes", false)]
    [InlineData("andrey:persona:viktoriya", false)]
    public void IsDatasetVisibleToUser_ПравилоПрефикса(string name, bool expected)
    {
        PersonaBindingsService.IsDatasetVisibleToUser(name, "me").Should().Be(expected);
    }

    // --- EffectiveToolEnabled ---

    [Fact]
    public void EffectiveToolEnabled_БезПерсоны_Разрешено()
    {
        _sut.EffectiveToolEnabled(_userId, null, "tasks").Should().BeTrue();
    }

    [Fact]
    public void EffectiveToolEnabled_БезПривязок_СемантикаTools()
    {
        // null-список — без ограничений
        _sut.EffectiveToolEnabled(_userId, MakePersona(), "tasks").Should().BeTrue();
        // ограниченный список — только перечисленные
        var persona = MakePersona(tools: ["notes"]);
        _sut.EffectiveToolEnabled(_userId, persona, "notes").Should().BeTrue();
        _sut.EffectiveToolEnabled(_userId, persona, "tasks").Should().BeFalse();
    }

    [Fact]
    public void EffectiveToolEnabled_ПривязкаПриоритетнееTools()
    {
        // Tools запрещает tasks, но Tool-привязка (Auto) включает
        var persona = MakePersona(tools: ["notes"],
            bindings: [ToolBinding("tasks", PersonaBindingMode.Auto)]);
        _sut.EffectiveToolEnabled(_userId, persona, "tasks").Should().BeTrue();
    }

    [Fact]
    public void EffectiveToolEnabled_РежимOff_Выключает()
    {
        // Tools разрешает всё (null), но Off-привязка выключает web
        var persona = MakePersona(bindings: [ToolBinding("web", PersonaBindingMode.Off)]);
        _sut.EffectiveToolEnabled(_userId, persona, "web").Should().BeFalse();
        // остальные ключи не затронуты
        _sut.EffectiveToolEnabled(_userId, persona, "tasks").Should().BeTrue();
    }

    // --- ServerToolEnabled (рубильники MCP-серверов) ---

    [Fact]
    public void ServerToolEnabled_БезПерсоны_Разрешено()
    {
        foreach (var key in PersonaBindingsService.ServerKeys)
            _sut.ServerToolEnabled(_userId, null, key).Should().BeTrue($"ключ {key}");
    }

    [Fact]
    public void ServerToolEnabled_СуженныйTools_НеВыключаетСерверы()
    {
        // Ключевой инвариант дефолта: Persona.Tools этих ключей никогда не содержал, поэтому
        // фолбэка на него нет — иначе персона со списком ["tasks"] разом лишилась бы серверов,
        // которые сегодня получает безусловно
        var persona = MakePersona(tools: ["tasks"]);
        foreach (var key in PersonaBindingsService.ServerKeys)
            _sut.ServerToolEnabled(_userId, persona, key).Should().BeTrue($"ключ {key}");
        // а старая семантика Tools у своих ключей осталась прежней
        _sut.EffectiveToolEnabled(_userId, persona, "notes").Should().BeFalse();
    }

    [Theory]
    [InlineData("personas")]
    [InlineData("consultants")]
    [InlineData("codegraph")]
    [InlineData("notifications")]
    [InlineData("widgets")]
    public void ServerToolEnabled_OffПривязка_Выключает(string key)
    {
        var persona = MakePersona(bindings: [ToolBinding(key, PersonaBindingMode.Off)]);

        _sut.ServerToolEnabled(_userId, persona, key).Should().BeFalse();
        // соседние ключи не затронуты
        foreach (var other in PersonaBindingsService.ServerKeys.Where(k => k != key))
            _sut.ServerToolEnabled(_userId, persona, other).Should().BeTrue($"ключ {other}");
    }

    [Fact]
    public void ServerToolEnabled_РежимыКромеOff_Включают()
    {
        foreach (var mode in new[] { PersonaBindingMode.Auto, PersonaBindingMode.Always })
        {
            var persona = MakePersona(bindings: [ToolBinding("widgets", mode)]);
            _sut.ServerToolEnabled(_userId, persona, "widgets").Should().BeTrue($"режим {mode}");
        }
    }

    [Fact]
    public void ServerToolEnabled_СтабильноНаВсехХодах()
    {
        // Состав tools/list входит в сигнатуру запуска CLI: решение обязано быть одинаковым
        // на каждом ходу сессии, иначе процесс перезапускается со всеми MCP-серверами
        var persona = MakePersona(tools: ["tasks"],
            bindings: [ToolBinding("codegraph", PersonaBindingMode.Off)]);
        for (var turn = 1; turn <= 10; turn++)
        {
            _sut.ServerToolEnabled(_userId, persona, "codegraph").Should().BeFalse($"ход {turn}");
            _sut.ServerToolEnabled(_userId, persona, "personas").Should().BeTrue($"ход {turn}");
        }
    }

    [Fact]
    public void ServerKeys_ЕстьВКаталогеЦелей()
    {
        // Иначе Off-привязку на такой ключ отклонит валидация ValidateAsync
        foreach (var key in PersonaBindingsService.ServerKeys)
            PersonaBindingsService.ToolCatalog.Should().ContainKey(key);
    }

    // --- Серверы личного реестра как Tool-ключи «mcp:<ключ>» ---

    private McpServerRecord MakeServer(string key, string? label = null) =>
        _mcp.Create(_userId, new McpServerRecord
        {
            Key = key,
            Label = label ?? key,
            Transport = McpTransport.Stdio,
            Command = "node",
        });

    [Fact]
    public void ToolCatalogFor_ДобавляетСерверыРеестра_НеТрогаяСтатическийКаталог()
    {
        MakeServer("context7", "Context7");

        var catalog = _sut.ToolCatalogFor(_userId);
        catalog.Should().ContainKey("mcp:context7");
        catalog["mcp:context7"].Label.Should().Be("Context7");
        // хинт обязан говорить, что условие у такой привязки не работает
        catalog["mcp:context7"].Hint.Should().Contain("не учитывается");
        // статический каталог общий для всех владельцев — его расширять нельзя
        PersonaBindingsService.ToolCatalog.Should().NotContainKey("mcp:context7");
        // чужой владелец записи не видит
        _sut.ToolCatalogFor("другой-владелец").Should().NotContainKey("mcp:context7");
    }

    [Fact]
    public void McpКлюч_ДефолтВключён_ВыключаетТолькоOffПривязка()
    {
        MakeServer("context7");
        var persona = MakePersona();

        _sut.GetToolDefaultState(_userId, persona, "mcp:context7").Should().Be((true, (string?)null));
        _sut.ServerToolEnabled(_userId, persona, "mcp:context7").Should().BeTrue();

        // суженный список возможностей на mcp-ключи не влияет (как и у прочих ServerKeys)
        var narrow = MakePersona(tools: ["tasks"]);
        _sut.ServerToolEnabled(_userId, narrow, "mcp:context7").Should().BeTrue();

        var off = MakePersona(bindings: [ToolBinding("mcp:context7", PersonaBindingMode.Off)]);
        _sut.ServerToolEnabled(_userId, off, "mcp:context7").Should().BeFalse();
        // Auto-привязка ничего не выключает — сервер и так включён
        var auto = MakePersona(bindings: [ToolBinding("mcp:context7", PersonaBindingMode.Auto)]);
        _sut.ServerToolEnabled(_userId, auto, "mcp:context7").Should().BeTrue();
    }

    // --- McpServerGranted (allow-модель, флаг mcp-allowlist) ---

    [Fact]
    public void McpServerGranted_БезПерсоны_НеВыдан()
    {
        // Чат без персоны — сервер не выдан (OR-правило не выполняется по персоне)
        _sut.McpServerGranted(null, "mcp:context7").Should().BeFalse();
    }

    [Fact]
    public void McpServerGranted_БезПривязки_НеВыдан()
    {
        var persona = MakePersona();
        _sut.McpServerGranted(persona, "mcp:context7").Should().BeFalse();
    }

    [Theory]
    [InlineData(PersonaBindingMode.Off, false)]
    [InlineData(PersonaBindingMode.Auto, true)]
    [InlineData(PersonaBindingMode.Always, true)]
    public void McpServerGranted_ЗависитОтРежимаПривязки(PersonaBindingMode mode, bool expected)
    {
        var persona = MakePersona(bindings: [ToolBinding("mcp:context7", mode)]);
        _sut.McpServerGranted(persona, "mcp:context7").Should().Be(expected);
    }

    [Fact]
    public void GetToolDefaultState_McpКлюч_БезФлага_ВключёнПоУмолчанию()
    {
        MakeServer("context7");
        var persona = MakePersona();
        // Флаг mcp-allowlist выключен — прежний дефолт «включён» (deny-модель)
        var (enabled, origin) = _sut.GetToolDefaultState(_userId, persona, "mcp:context7");
        enabled.Should().BeTrue();
        origin.Should().BeNull();
        // Подсказка каталога — deny-текст («ВЫКЛЮЧИТЬ»)
        _sut.ToolCatalogFor(_userId)["mcp:context7"].Hint.Should().Contain("ВЫКЛЮЧИТЬ");
    }

    [Fact]
    public void GetToolDefaultState_McpКлюч_ЗаФлагом_ВыключенПоУмолчанию()
    {
        MakeServer("context7");
        var persona = MakePersona();
        _users.SetFeatureFlag(_userId, FeatureFlagKeys.McpAllowlist, true);

        var (enabled, origin) = _sut.GetToolDefaultState(_userId, persona, "mcp:context7");
        enabled.Should().BeFalse("за флагом mcp-allowlist сервер по умолчанию выключен");
        origin.Should().BeNull();
        // Подсказка каталога перевернулась в allow-текст («ВКЛЮЧАЕТ»)
        _sut.ToolCatalogFor(_userId)["mcp:context7"].Hint.Should().Contain("ВКЛЮЧАЕТ");
    }

    [Fact]
    public async Task ValidateAsync_McpКлюч_СверяетсяСРеестромВладельца()
    {
        MakeServer("context7");

        var ok = await _sut.ValidateAsync(_userId, ToolBinding("mcp:context7", PersonaBindingMode.Off), null, null);
        ok.Should().BeNull();

        var missing = await _sut.ValidateAsync(_userId, ToolBinding("mcp:нет-такого", PersonaBindingMode.Off), null, null);
        missing.Should().NotBeNull();

        // чужому владельцу тот же ключ недоступен
        var alien = await _sut.ValidateAsync("другой-владелец", ToolBinding("mcp:context7", PersonaBindingMode.Off), null, null);
        alien.Should().NotBeNull();
    }

    [Fact]
    public void PurgeMcpBindings_УбираетПривязкиНаУдалённыйСервер()
    {
        MakeServer("context7");
        var persona = _personas.Create(_userId, "Тест", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);
        _personas.UpdateBindings(persona.Id, _userId,
        [
            ToolBinding("mcp:context7", PersonaBindingMode.Off),
            ToolBinding("tasks", PersonaBindingMode.Off),
        ]);

        _sut.PurgeMcpBindings(_userId, "context7").Should().Be(1);

        var updated = _personas.Get(persona.Id, _userId)!;
        updated.Bindings.Should().ContainSingle().Which.Target.Should().Be("tasks");
        // повторный вызов уже никого не трогает
        _sut.PurgeMcpBindings(_userId, "context7").Should().Be(0);
    }

    // --- SectionEnabled (секции-надстройки с дефолтом по specialty) ---

    [Fact]
    public void SectionEnabled_БезПерсоны_Разрешено()
    {
        // Обычный чат (не персонный) пресетов не знает — состав как раньше
        foreach (var key in PersonaBindingsService.PresetKeys)
            _sut.SectionEnabled(_userId, null, key).Should().BeTrue($"ключ {key}");
    }

    [Theory]
    [InlineData(PersonaSpecialty.Executor, "git")]
    [InlineData(PersonaSpecialty.Reviewer, "git")]
    [InlineData(PersonaSpecialty.Tester, "git")]
    [InlineData(PersonaSpecialty.Tester, "browser")]
    [InlineData(PersonaSpecialty.Librarian, "kb")]
    [InlineData(PersonaSpecialty.Coordinator, "personas-manage")]
    [InlineData(PersonaSpecialty.Coordinator, "personas-automation")]
    [InlineData(PersonaSpecialty.Secretary, "personas-manage")]
    [InlineData(PersonaSpecialty.Secretary, "personas-automation")]
    public void SectionEnabled_ПресетПоSpecialty_ДаётСвоиСекции(PersonaSpecialty specialty, string key)
    {
        var persona = MakePersona(specialty: specialty);

        _sut.SectionEnabled(_userId, persona, key).Should().BeTrue();
        // соседние ключи пресет не раздаёт — иначе разрез по ролям не экономит контекст
        foreach (var other in PersonaBindingsService.PresetKeys.Where(k => k != key
                     && !PersonaBindingsService.SpecialtySections(specialty).Contains(k)))
            _sut.SectionEnabled(_userId, persona, other).Should().BeFalse($"ключ {other}");
    }

    [Fact]
    public void SectionEnabled_Браузер_ТолькоТестировщику()
    {
        // Плагин playwright (24 browser_*-инструмента) держим у того, чья работа — щёлкать UI
        foreach (var specialty in Enum.GetValues<PersonaSpecialty>())
            _sut.SectionEnabled(_userId, MakePersona(specialty: specialty), "browser")
                .Should().Be(specialty == PersonaSpecialty.Tester, $"роль {specialty}");
    }

    [Fact]
    public void SectionEnabled_БезSpecialty_ТолькоЯдро()
    {
        var persona = MakePersona();
        foreach (var key in PersonaBindingsService.PresetKeys)
            _sut.SectionEnabled(_userId, persona, key).Should().BeFalse($"ключ {key}");
    }

    // --- ToolKeyAvailable (ключ строкой снаружи: подбор персоны под действие AI-хаба) ---

    [Fact]
    public void ToolKeyAvailable_РешаетТемЖеГейтом_ЧтоИКлючСам()
    {
        // Библиотекарь получает kb пресетом, а notes-annotations — нет: разбор комментариев
        // не должен достаться персоне, у которой этих инструментов не будет.
        var librarian = MakePersona(specialty: PersonaSpecialty.Librarian);
        _sut.ToolKeyAvailable(_userId, librarian, "kb").Should().BeTrue();
        _sut.ToolKeyAvailable(_userId, librarian, "notes-annotations").Should().BeFalse();

        // Явная привязка включает секцию (тот же путь, что у SectionEnabled)
        var withBinding = MakePersona(bindings: [ToolBinding("notes-annotations", PersonaBindingMode.Auto)]);
        _sut.ToolKeyAvailable(_userId, withBinding, "notes-annotations").Should().BeTrue();

        // Рубильник сервера идёт через ServerToolEnabled: суженный Tools его не гасит, Off — гасит
        var narrowTools = MakePersona(tools: ["tasks"]);
        _sut.ToolKeyAvailable(_userId, narrowTools, "widgets").Should().BeTrue();
        _sut.ToolKeyAvailable(_userId, MakePersona(bindings: [ToolBinding("widgets", PersonaBindingMode.Off)]), "widgets")
            .Should().BeFalse();

        // Обычная возможность (не рубильник и не надстройка) — семантика Persona.Tools
        _sut.ToolKeyAvailable(_userId, narrowTools, "tasks").Should().BeTrue();
        _sut.ToolKeyAvailable(_userId, narrowTools, "notes").Should().BeFalse();

        // Не персонный чат получает всё
        foreach (var key in new[] { "notes-annotations", "widgets", "notes" })
            _sut.ToolKeyAvailable(_userId, null, key).Should().BeTrue($"ключ {key}");
    }

    [Fact]
    public void SectionEnabled_ПривязкаПриоритетнееПресета()
    {
        // Явно включили kb аналитику, которому пресет его не давал
        var withBinding = MakePersona(bindings: [ToolBinding("kb", PersonaBindingMode.Auto)]);
        _sut.SectionEnabled(_userId, withBinding, "kb").Should().BeTrue();

        // И наоборот: Off у исполнителя сильнее пресета executor → git
        var offBinding = MakePersona(bindings: [ToolBinding("git", PersonaBindingMode.Off)],
            specialty: PersonaSpecialty.Executor);
        _sut.SectionEnabled(_userId, offBinding, "git").Should().BeFalse();
    }

    [Fact]
    public void SectionEnabled_TollsСКлючамиНадстроек_БелыйСписок()
    {
        // Список возможностей ЗНАЕТ новые ключи — значит он про них и высказался: белый список
        var persona = MakePersona(tools: ["kb"], specialty: PersonaSpecialty.Coordinator);
        _sut.SectionEnabled(_userId, persona, "kb").Should().BeTrue();
        _sut.SectionEnabled(_userId, persona, "personas-manage").Should().BeFalse();
    }

    [Fact]
    public void SectionEnabled_ЛегасиTools_НеУбиваетПресет()
    {
        // Старый суженный список (только tasks/notes/web) о ключах-надстройках не знал —
        // трактовать его как запрет значило бы навсегда выключить пресет такой персоне
        var persona = MakePersona(tools: ["tasks", "notes"], specialty: PersonaSpecialty.Coordinator);
        _sut.SectionEnabled(_userId, persona, "personas-manage").Should().BeTrue();
        _sut.SectionEnabled(_userId, persona, "git").Should().BeFalse("пресет координатора git не даёт");
    }

    [Fact]
    public void SectionEnabled_СтабильноНаВсехХодах()
    {
        // Состав tools/list входит в сигнатуру запуска CLI: решение обязано быть одинаковым
        // на каждом ходу сессии, иначе процесс перезапускается со всеми MCP-серверами
        var persona = MakePersona(specialty: PersonaSpecialty.Executor);
        for (var turn = 1; turn <= 10; turn++)
        {
            _sut.SectionEnabled(_userId, persona, "git").Should().BeTrue($"ход {turn}");
            _sut.SectionEnabled(_userId, persona, "kb").Should().BeFalse($"ход {turn}");
        }
    }

    // --- SectionOrigin (источник решения: пресет по роли vs явное включение) ---

    [Theory]
    [InlineData(PersonaSpecialty.Executor, SectionSource.Preset)]
    [InlineData(PersonaSpecialty.None, SectionSource.Off)]
    public void SectionOrigin_Пресет_ОтличимОтЯвногоВключения(PersonaSpecialty specialty, SectionSource expected)
    {
        // Пресет по роли даёт git на ЧТЕНИЕ, запись истории добавляет только явный ключ:
        // на этом различии стоит секция git_write в BuildWorkspaceContext
        _sut.SectionOrigin(_userId, MakePersona(specialty: specialty), "git").Should().Be(expected);
    }

    [Fact]
    public void SectionOrigin_ЯвноеВключение_Explicit()
    {
        var byBinding = MakePersona(bindings: [ToolBinding("git", PersonaBindingMode.Auto)]);
        _sut.SectionOrigin(_userId, byBinding, "git").Should().Be(SectionSource.Explicit);

        // Список возможностей, знающий ключи-надстройки, — тоже явное высказывание
        var byTools = MakePersona(tools: ["git"]);
        _sut.SectionOrigin(_userId, byTools, "git").Should().Be(SectionSource.Explicit);

        // Off сильнее пресета
        var off = MakePersona(bindings: [ToolBinding("git", PersonaBindingMode.Off)],
            specialty: PersonaSpecialty.Executor);
        _sut.SectionOrigin(_userId, off, "git").Should().Be(SectionSource.Off);

        // Обычный чат пресетов не знает — получает всё, как раньше
        _sut.SectionOrigin(_userId, null, "git").Should().Be(SectionSource.Explicit);
    }

    // --- NotificationsEnabled (дефолт сервера уведомлений по роли) ---

    [Fact]
    public void NotificationsEnabled_БезПерсоны_Разрешено()
    {
        _sut.NotificationsEnabled(_userId, null).Should().BeTrue();
    }

    [Fact]
    public void NotificationsEnabled_ПерсонаБезРоли_Выключено()
    {
        // Дефолт сузили по данным использования: инструменты уведомлений висели у всех,
        // а звали их единицы ходов
        _sut.NotificationsEnabled(_userId, MakePersona()).Should().BeFalse();
    }

    [Theory]
    [InlineData(PersonaSpecialty.Coordinator)]
    [InlineData(PersonaSpecialty.Secretary)]
    public void NotificationsEnabled_МодульАвтоматизации_Включает(PersonaSpecialty specialty)
    {
        // Кому проактивность положена по роли, тому и уведомления
        _sut.NotificationsEnabled(_userId, MakePersona(specialty: specialty)).Should().BeTrue();
    }

    [Fact]
    public void NotificationsEnabled_ЯвнаяПривязка_СильнееДефолта()
    {
        var on = MakePersona(bindings: [ToolBinding("notifications", PersonaBindingMode.Auto)]);
        _sut.NotificationsEnabled(_userId, on).Should().BeTrue();

        var off = MakePersona(bindings: [ToolBinding("notifications", PersonaBindingMode.Off)],
            specialty: PersonaSpecialty.Coordinator);
        _sut.NotificationsEnabled(_userId, off).Should().BeFalse();

        // Список возможностей, называющий уведомления, — тоже явное «да»
        _sut.NotificationsEnabled(_userId, MakePersona(tools: ["notifications"])).Should().BeTrue();
    }

    [Fact]
    public void NotificationsEnabled_СтабильноНаВсехХодах()
    {
        var persona = MakePersona(specialty: PersonaSpecialty.Executor);
        for (var turn = 1; turn <= 10; turn++)
            _sut.NotificationsEnabled(_userId, persona).Should().BeFalse($"ход {turn}");
    }

    [Fact]
    public void PresetKeys_ЕстьВКаталогеЦелей()
    {
        // Иначе привязку на такой ключ отклонит валидация ValidateAsync, и включить
        // выключенную пресетом секцию будет нечем
        foreach (var key in PersonaBindingsService.PresetKeys)
            PersonaBindingsService.ToolCatalog.Should().ContainKey(key);
    }

    // --- GetToolDefaultState (дефолт пикера Tool-привязок без учёта Tool-привязки на ключ) ---

    [Fact]
    public void GetToolDefaultState_БезPersonaId_НеПрименимо()
    {
        // Этот метод вызывается только когда personaId передан; здесь проверяем,
        // что для ядерных ключей с null-Tools дефолт включён с origin=null.
        var persona = MakePersona();
        _sut.GetToolDefaultState(_userId, persona, "tasks").Should().Be((true, (string?)null));
        _sut.GetToolDefaultState(_userId, persona, "notes").Should().Be((true, (string?)null));
    }

    [Fact]
    public void GetToolDefaultState_Tools_БелыйСписок()
    {
        var persona = MakePersona(tools: ["tasks"]);
        _sut.GetToolDefaultState(_userId, persona, "tasks").Should().Be((true, "settings"));
        _sut.GetToolDefaultState(_userId, persona, "notes").Should().Be((false, "settings"));
        _sut.GetToolDefaultState(_userId, persona, "web").Should().Be((false, "settings"));
    }

    [Fact]
    public void GetToolDefaultState_ServerKeys_ВсегдаВключены()
    {
        var persona = MakePersona(tools: ["tasks"]);
        foreach (var key in PersonaBindingsService.ServerKeys.Where(k =>
            !string.Equals(k, "notifications", StringComparison.OrdinalIgnoreCase)))
            _sut.GetToolDefaultState(_userId, persona, key).Should().Be((true, (string?)null), $"ключ {key}");
    }

    [Fact]
    public void GetToolDefaultState_Notifications_ПоРолиИлиTools()
    {
        // Без роли и без Tools — выключено
        _sut.GetToolDefaultState(_userId, MakePersona(), "notifications")
            .Should().Be((false, (string?)null));

        // Явный Tools → settings
        _sut.GetToolDefaultState(_userId, MakePersona(tools: ["notifications"]), "notifications")
            .Should().Be((true, "settings"));

        // Роль с автоматизацией → role
        _sut.GetToolDefaultState(_userId, MakePersona(specialty: PersonaSpecialty.Coordinator), "notifications")
            .Should().Be((true, "role"));
    }

    [Theory]
    [InlineData(PersonaSpecialty.Executor, "git", true, "role")]
    [InlineData(PersonaSpecialty.Executor, "kb", false, null)]
    [InlineData(PersonaSpecialty.Tester, "browser", true, "role")]
    [InlineData(PersonaSpecialty.Coordinator, "personas-manage", true, "role")]
    public void GetToolDefaultState_PresetKeys_ПресетПоSpecialty(PersonaSpecialty specialty, string key, bool enabled, string? origin)
    {
        _sut.GetToolDefaultState(_userId, MakePersona(specialty: specialty), key)
            .Should().Be((enabled, origin));
    }

    [Fact]
    public void GetToolDefaultState_PresetKeys_ToolsЗнаетКлючи_БелыйСписок()
    {
        // Если Tools содержит хотя бы один PresetKey, все PresetKeys решаются по Tools
        var persona = MakePersona(tools: ["git"]);
        _sut.GetToolDefaultState(_userId, persona, "git").Should().Be((true, "settings"));
        _sut.GetToolDefaultState(_userId, persona, "kb").Should().Be((false, "settings"));
        _sut.GetToolDefaultState(_userId, persona, "browser").Should().Be((false, "settings"));
    }

    [Fact]
    public void GetToolDefaultState_PresetKeys_ToolsНеЗнаетКлючи_СохраняетПресет()
    {
        // Старый суженный Tools (tasks/notes) не должен убить пресет по роли
        var persona = MakePersona(tools: ["tasks", "notes"], specialty: PersonaSpecialty.Executor);
        _sut.GetToolDefaultState(_userId, persona, "git").Should().Be((true, "role"));
    }

    [Fact]
    public void GetToolDefaultState_Workspace_ДефолтВключено()
    {
        var persona = MakePersona();
        _sut.GetToolDefaultState(_userId, persona, "projects").Should().Be((true, (string?)null));
        _sut.GetToolDefaultState(_userId, persona, "files").Should().Be((true, (string?)null));
        _sut.GetToolDefaultState(_userId, persona, "knowledge").Should().Be((true, (string?)null));
    }

    [Fact]
    public void GetToolDefaultState_Workspace_СуженныйTools_Отключает()
    {
        var persona = MakePersona(tools: ["notes"]);
        _sut.GetToolDefaultState(_userId, persona, "projects").Should().Be((false, (string?)null));
        _sut.GetToolDefaultState(_userId, persona, "files").Should().Be((false, (string?)null));
    }

    [Fact]
    public void GetToolDefaultState_Chats_ВключаетсяProjectPersonas()
    {
        var narrow = MakePersona(tools: ["notes"]);
        _sut.GetToolDefaultState(_userId, narrow, "chats").Should().Be((false, (string?)null));

        var delegator = MakePersona(tools: ["notes"], bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "p1" },
        ]);
        _sut.GetToolDefaultState(_userId, delegator, "chats").Should().Be((true, (string?)null));
    }

    [Fact]
    public void GetToolDefaultState_Destructive_ReadOnly_Отключено()
    {
        var persona = MakePersona(access: PersonaAccess.ReadOnly);
        _sut.GetToolDefaultState(_userId, persona, "destructive").Should().Be((false, (string?)null));
    }

    // --- BuildFileScopes ---

    [Fact]
    public void BuildFileScopes_БезПривязок_Null()
    {
        _sut.BuildFileScopes(_userId, MakePersona()).Should().BeNull();
    }

    [Fact]
    public void BuildFileScopes_ПроектныеПривязки_СписокБезOff()
    {
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.Project, Target = "p1" },
            new PersonaBinding { Type = PersonaBindingType.ProjectPath, Target = "p2", Path = "docs" },
            new PersonaBinding { Type = PersonaBindingType.Project, Target = "p3", Mode = PersonaBindingMode.Off },
            new PersonaBinding { Type = PersonaBindingType.Project, Target = "p1" }, // дубль схлопывается
        ]);
        _sut.BuildFileScopes(_userId, persona).Should().BeEquivalentTo(["p1", "p2"]);
    }

    // --- BuildChatScopes / ChatsSectionEnabled ---

    [Fact]
    public void BuildChatScopes_ТолькоProjectPersonasБезOff()
    {
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "p1", Mode = PersonaBindingMode.Always },
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = "p2" },   // другой тип — не чаты
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "p3", Mode = PersonaBindingMode.Off },
        ]);
        _sut.BuildChatScopes(_userId, persona).Should().BeEquivalentTo(["p1"]);
    }

    // Регресс на баг прода: у персоны-постановщика (Tools=null + постоянная ProjectPersonas-привязка)
    // chats_send/chats_create «мерцали» между ходами. Решение по секции обязано быть
    // детерминированным — имитируем несколько последовательных ходов (сборок контекста).
    [Fact]
    public void ChatsSectionEnabled_ProjectPersonas_СтабильноOnНаВсехХодах()
    {
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "p1", Mode = PersonaBindingMode.Always },
        ]);

        for (var turn = 1; turn <= 5; turn++)
            _sut.ChatsSectionEnabled(_userId, persona).Should().BeTrue($"ход {turn}");
    }

    [Fact]
    public void ChatsSectionEnabled_ToolsБезChats_ВключаетсяПривязкойКоманды()
    {
        // Ограниченный Tools (без chats) сам по себе секцию не даёт…
        var narrow = MakePersona(tools: ["notes"]);
        _sut.ChatsSectionEnabled(_userId, narrow).Should().BeFalse();

        // …а ProjectPersonas-привязка включает её неявным opt-in
        var delegator = MakePersona(tools: ["notes"], bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "p1" },
        ]);
        _sut.ChatsSectionEnabled(_userId, delegator).Should().BeTrue();
    }

    [Fact]
    public void ChatsSectionEnabled_ToolПривязкаOff_НоПривязкаКомандыВключает()
    {
        var persona = MakePersona(bindings:
        [
            ToolBinding("chats", PersonaBindingMode.Off),
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "p1" },
        ]);
        // Off гасит Tool-ключ, но неявный opt-in по ProjectPersonas — независимое основание:
        // допуск к чужой команде подразумевает и переписку с её чатами (условие «ИЛИ»)
        _sut.EffectiveToolEnabled(_userId, persona, "chats").Should().BeFalse();
        _sut.ChatsSectionEnabled(_userId, persona).Should().BeTrue();
    }

    // --- BuildIndex ---

    [Fact]
    public void BuildIndex_ЛимитСтрок()
    {
        // 20 Tool-привязок (не зависят от секций/целей) → в индексе не больше 12 строк
        var bindings = Enumerable.Range(0, 20)
            .Select(i => new PersonaBinding
            {
                Type = PersonaBindingType.Tool,
                Target = "tasks",
                Condition = $"условие {i}",
            })
            .ToList();
        var index = _sut.BuildIndex(_userId, bindings, []);

        index.Should().NotBeNull();
        index!.Split('\n').Count(l => l.StartsWith("- [")).Should().Be(PersonaBindingsService.IndexLimit);
    }

    [Fact]
    public void BuildIndex_ПустоеУсловие_ВсегдаПодРукой()
    {
        var bindings = new List<PersonaBinding>
        {
            new() { Type = PersonaBindingType.Tool, Target = "notes", Condition = "" },
        };
        var index = _sut.BuildIndex(_userId, bindings, []);

        index.Should().NotBeNull();
        index!.Should().Contain("всегда под рукой");
        index.Should().NotContain("Когда:");
    }

    [Fact]
    public void BuildIndex_ПроектБезСекцииFiles_Опускается()
    {
        var project = MakeProject("Биллинг");
        var bindings = new List<PersonaBinding>
        {
            new() { Type = PersonaBindingType.Project, Target = project.Id, Condition = "вопросы по биллингу" },
            new() { Type = PersonaBindingType.Tool, Target = "tasks", Condition = "работа с задачами" },
        };

        // Секция files НЕ смонтирована → строка проекта опускается, индекс из Tool-строки
        var withoutFiles = _sut.BuildIndex(_userId, bindings, mountedSections: ["projects"]);
        withoutFiles.Should().NotBeNull();
        withoutFiles!.Should().NotContain("Биллинг").And.Contain("работа с задачами");

        // Секция files смонтирована → проект в индексе со способом подгрузки
        var withFiles = _sut.BuildIndex(_userId, bindings, mountedSections: ["projects", "files"]);
        withFiles.Should().NotBeNull();
        withFiles!.Should().Contain("Биллинг").And.Contain("files_tree").And.Contain(project.Id);
    }

    [Fact]
    public void BuildIndex_НиОднойДоступнойПривязки_Null()
    {
        // Проектная привязка без секции files — способ недоступен, индекс пуст
        var project = MakeProject("Скрытый");
        var bindings = new List<PersonaBinding>
        {
            new() { Type = PersonaBindingType.Project, Target = project.Id, Condition = "всё" },
        };
        _sut.BuildIndex(_userId, bindings, mountedSections: []).Should().BeNull();
    }

    // --- BuildTurnBlockAsync ---

    [Fact]
    public async Task BuildTurnBlockAsync_ПривязокНет_Null()
    {
        var persona = _personas.Create(_userId, "Пустой", null, null, null,
            null, null, PersonaScope.Global, null, null, null, true);

        (await _sut.BuildTurnBlockAsync(_userId, persona.Id, "вопрос", [])).Should().BeNull();
    }

    [Fact]
    public async Task BuildTurnBlockAsync_АктивныеПривязки_БлокСИндексом()
    {
        var persona = _personas.Create(_userId, "Секретарь", "Секретарь", null, null,
            null, null, PersonaScope.Global, null, null, null, true);
        _personas.UpdateBindings(persona.Id, _userId,
            [ToolBinding("tasks", PersonaBindingMode.Auto)]);

        var block = await _sut.BuildTurnBlockAsync(_userId, persona.Id, "напомни про встречу", []);

        block.Should().NotBeNull();
        block!.Should().Contain("Привязанные знания и правила").And.Contain("по запросу");
    }

    // --- ValidateAsync ---

    [Fact]
    public async Task ValidateAsync_ПустойTarget_Ошибка()
    {
        var binding = new PersonaBinding { Type = PersonaBindingType.Tool, Target = " " };
        (await _sut.ValidateAsync(_userId, binding, null)).Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_НеизвестныйКлючИнструмента_Ошибка()
    {
        var binding = new PersonaBinding { Type = PersonaBindingType.Tool, Target = "hacking" };
        (await _sut.ValidateAsync(_userId, binding, null)).Should().Contain("Неизвестный ключ");
    }

    [Fact]
    public async Task ValidateAsync_ЧужойПроект_Ошибка()
    {
        var project = MakeProject("Свой");
        var alien = _projects.Create("Чужой",
            Directory.CreateDirectory(Path.Combine(_tempDir, "alien")).FullName, "other-user", "other");

        var ok = new PersonaBinding { Type = PersonaBindingType.Project, Target = project.Id };
        (await _sut.ValidateAsync(_userId, ok, null)).Should().BeNull();

        var bad = new PersonaBinding { Type = PersonaBindingType.Project, Target = alien.Id };
        (await _sut.ValidateAsync(_userId, bad, null)).Should().Contain("не найден");
    }

    [Fact]
    public async Task ValidateAsync_PathTraversal_Ошибка()
    {
        var project = MakeProject("Пф");
        var binding = new PersonaBinding
        {
            Type = PersonaBindingType.ProjectPath,
            Target = project.Id,
            Path = "docs/../../secrets",
        };
        (await _sut.ValidateAsync(_userId, binding, null)).Should().Contain("путь");
    }

    [Fact]
    public async Task ValidateAsync_ProjectPathБезPath_Ошибка()
    {
        var project = MakeProject("Пп");
        var binding = new PersonaBinding { Type = PersonaBindingType.ProjectPath, Target = project.Id };
        (await _sut.ValidateAsync(_userId, binding, null)).Should().Contain("path");
    }

    [Fact]
    public async Task ValidateAsync_Дубликат_Ошибка()
    {
        var existing = new List<PersonaBinding> { ToolBinding("tasks", PersonaBindingMode.Auto) };

        var dup = new PersonaBinding { Type = PersonaBindingType.Tool, Target = "TASKS" };
        (await _sut.ValidateAsync(_userId, dup, existing)).Should().Contain("дубликат");

        // Та же привязка (тот же Id) дубликатом самой себя не считается
        (await _sut.ValidateAsync(_userId, existing[0], existing)).Should().BeNull();

        // Другой target — не дубликат
        var other = new PersonaBinding { Type = PersonaBindingType.Tool, Target = "notes" };
        (await _sut.ValidateAsync(_userId, other, existing)).Should().BeNull();
    }

    [Fact]
    public async Task ValidateAsync_НормализуетPath()
    {
        var project = MakeProject("Норм");
        var binding = new PersonaBinding
        {
            Type = PersonaBindingType.ProjectPath,
            Target = project.Id,
            Path = "docs\\api\\",
        };
        (await _sut.ValidateAsync(_userId, binding, null)).Should().BeNull();
        binding.Path.Should().Be("docs/api");
    }

    [Fact]
    public async Task ValidateAsync_ИсточникЗаметок()
    {
        // "personal" — всегда валидный источник; выдуманный ключ — нет
        var ok = new PersonaBinding { Type = PersonaBindingType.Notes, Target = "personal" };
        (await _sut.ValidateAsync(_userId, ok, null)).Should().BeNull();

        var bad = new PersonaBinding { Type = PersonaBindingType.Notes, Target = "no-such-source" };
        (await _sut.ValidateAsync(_userId, bad, null)).Should().Contain("не найден");
    }

    // --- Кросс-проектные привязки: ProjectPersonas / ProjectTasks ---

    [Fact]
    public async Task ValidateAsync_ProjectPersonas_ЧужойПроектНеНайден()
    {
        var binding = new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "no-such-project" };
        (await _sut.ValidateAsync(_userId, binding, null)).Should().Contain("не найден");
    }

    [Fact]
    public async Task ValidateAsync_ProjectPersonas_ПривязкаКСвоемуПроекту_НоОп()
    {
        var project = MakeProject("Свой");
        var owner = new Persona { OwnerId = _userId, Scope = PersonaScope.Project, ProjectId = project.Id };
        var binding = new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = project.Id };
        (await _sut.ValidateAsync(_userId, binding, null, owner)).Should().Contain("своему же проекту");
    }

    [Fact]
    public async Task ValidateAsync_ProjectPersonas_СужениеДоКонкретнойПерсоны()
    {
        var project = MakeProject("Чужой");
        var teammate = _personas.Create(_userId, "Тимейт", null, null, null, null, null,
            PersonaScope.Project, project.Id, null, null, true);

        var ok = new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = project.Id, Path = teammate.Id };
        (await _sut.ValidateAsync(_userId, ok, null)).Should().BeNull();

        var bad = new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = project.Id, Path = "no-such-persona" };
        (await _sut.ValidateAsync(_userId, bad, null)).Should().Contain("не найдена");
    }

    [Fact]
    public async Task ValidateAsync_ProjectTasks_PathТолькоReadonlyИлиПусто()
    {
        var project = MakeProject("Задачи");
        var full = new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id };
        (await _sut.ValidateAsync(_userId, full, null)).Should().BeNull();

        var readOnly = new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id, Path = "ReadOnly" };
        (await _sut.ValidateAsync(_userId, readOnly, null)).Should().BeNull();

        var bad = new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id, Path = "write" };
        (await _sut.ValidateAsync(_userId, bad, null)).Should().Contain("readonly");
    }

    [Fact]
    public async Task ValidateAsync_ProjectTasks_ПривязкаКСвоемуПроекту_НоОп()
    {
        var project = MakeProject("Свой2");
        var owner = new Persona { OwnerId = _userId, Scope = PersonaScope.Project, ProjectId = project.Id };
        var binding = new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id };
        (await _sut.ValidateAsync(_userId, binding, null, owner)).Should().Contain("своему же проекту");
    }

    [Fact]
    public void BuildExternalPersonaScopes_КомандаЦеликомИТочечнаяПерсона_ВыключенныеПропускаются()
    {
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "projB" },
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "projC", Path = "persX" },
            new PersonaBinding { Type = PersonaBindingType.ProjectPersonas, Target = "projD", Mode = PersonaBindingMode.Off },
        ]);
        var scopes = _sut.BuildExternalPersonaScopes(_userId, persona);
        scopes.Should().BeEquivalentTo(new (string, string?)[] { ("projB", null), ("projC", "persX") });
    }

    [Fact]
    public void BuildExternalTaskScopes_КонфликтПолногоИReadonly_РешаетсяКонсервативно()
    {
        // Один и тот же проект дважды — полный доступ и readonly: побеждает readonly (наименьшее расширение прав)
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = "projB" },
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = "projB", Path = "readonly" },
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = "projC", Mode = PersonaBindingMode.Off },
        ]);
        var scopes = _sut.BuildExternalTaskScopes(_userId, persona);
        scopes.Should().ContainSingle();
        scopes[0].ProjectId.Should().Be("projB");
        scopes[0].ReadOnly.Should().BeTrue();
    }

    // --- HasFileBindingToProject / HasTaskBindingToProject / HasAnyBindingToProject (automation validation) ---

    [Fact]
    public void HasFileBindingToProject_ProjectПривязка_True()
    {
        var project = MakeProject("Файлы");
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.Project, Target = project.Id },
        ]);
        _sut.HasFileBindingToProject(persona, project.Id).Should().BeTrue();
        _sut.HasFileBindingToProject(persona, "other-id").Should().BeFalse();
    }

    [Fact]
    public void HasFileBindingToProject_ProjectPathПривязка_True()
    {
        var project = MakeProject("Папка");
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectPath, Target = project.Id, Path = "docs" },
        ]);
        _sut.HasFileBindingToProject(persona, project.Id).Should().BeTrue();
    }

    [Fact]
    public void HasFileBindingToProject_ProjectTasksПривязка_False()
    {
        var project = MakeProject("Задачи");
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id },
        ]);
        // ProjectTasks не даёт доступа к файлам
        _sut.HasFileBindingToProject(persona, project.Id).Should().BeFalse();
    }

    [Fact]
    public void HasFileBindingToProject_OffПривязка_False()
    {
        var project = MakeProject("Выкл");
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.Project, Target = project.Id, Mode = PersonaBindingMode.Off },
        ]);
        _sut.HasFileBindingToProject(persona, project.Id).Should().BeFalse();
    }

    [Fact]
    public void HasTaskBindingToProject_ProjectTasks_True()
    {
        var project = MakeProject("Только задачи");
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id },
        ]);
        _sut.HasTaskBindingToProject(persona, project.Id).Should().BeTrue();
        _sut.HasTaskBindingToProject(persona, "other").Should().BeFalse();
    }

    [Fact]
    public void HasAnyBindingToProject_ЛюбаяПривязка_True()
    {
        var project = MakeProject("Любой");
        var tasksOnly = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.ProjectTasks, Target = project.Id },
        ]);
        _sut.HasAnyBindingToProject(tasksOnly, project.Id).Should().BeTrue();

        var fileOnly = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.Project, Target = project.Id },
        ]);
        _sut.HasAnyBindingToProject(fileOnly, project.Id).Should().BeTrue();

        var noBindings = MakePersona();
        _sut.HasAnyBindingToProject(noBindings, project.Id).Should().BeFalse();
    }
}
