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

        _users = new UserStore(config, NullLogger<UserStore>.Instance);
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
        var flags = new FeatureFlagService(_users);

        _sut = new PersonaBindingsService(_personas, _projects, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), flags, _users, config,
            NullLogger<PersonaBindingsService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private void EnableFlag() => _users.SetFeatureFlag(_userId, FeatureFlagKeys.PersonaBindings, true);

    private Persona MakePersona(List<string>? tools = null, List<PersonaBinding>? bindings = null) =>
        new() { OwnerId = _userId, Name = "Тест", Tools = tools, Bindings = bindings };

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
        EnableFlag();
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
        EnableFlag();
        // Tools запрещает tasks, но Tool-привязка (Auto) включает
        var persona = MakePersona(tools: ["notes"],
            bindings: [ToolBinding("tasks", PersonaBindingMode.Auto)]);
        _sut.EffectiveToolEnabled(_userId, persona, "tasks").Should().BeTrue();
    }

    [Fact]
    public void EffectiveToolEnabled_РежимOff_Выключает()
    {
        EnableFlag();
        // Tools разрешает всё (null), но Off-привязка выключает web
        var persona = MakePersona(bindings: [ToolBinding("web", PersonaBindingMode.Off)]);
        _sut.EffectiveToolEnabled(_userId, persona, "web").Should().BeFalse();
        // остальные ключи не затронуты
        _sut.EffectiveToolEnabled(_userId, persona, "tasks").Should().BeTrue();
    }

    [Fact]
    public void EffectiveToolEnabled_ФлагВыключен_ПривязкиИгнорируются()
    {
        // Флаг persona-bindings НЕ включён — чистый откат к семантике Tools
        var persona = MakePersona(tools: ["notes"],
            bindings: [ToolBinding("tasks", PersonaBindingMode.Auto)]);
        _sut.EffectiveToolEnabled(_userId, persona, "tasks").Should().BeFalse();

        var offPersona = MakePersona(bindings: [ToolBinding("web", PersonaBindingMode.Off)]);
        _sut.EffectiveToolEnabled(_userId, offPersona, "web").Should().BeTrue();
    }

    // --- BuildFileScopes ---

    [Fact]
    public void BuildFileScopes_БезПривязок_Null()
    {
        EnableFlag();
        _sut.BuildFileScopes(_userId, MakePersona()).Should().BeNull();
    }

    [Fact]
    public void BuildFileScopes_ПроектныеПривязки_СписокБезOff()
    {
        EnableFlag();
        var persona = MakePersona(bindings:
        [
            new PersonaBinding { Type = PersonaBindingType.Project, Target = "p1" },
            new PersonaBinding { Type = PersonaBindingType.ProjectPath, Target = "p2", Path = "docs" },
            new PersonaBinding { Type = PersonaBindingType.Project, Target = "p3", Mode = PersonaBindingMode.Off },
            new PersonaBinding { Type = PersonaBindingType.Project, Target = "p1" }, // дубль схлопывается
        ]);
        _sut.BuildFileScopes(_userId, persona).Should().BeEquivalentTo(["p1", "p2"]);
    }

    [Fact]
    public void BuildFileScopes_ФлагВыключен_Null()
    {
        var persona = MakePersona(bindings:
            [new PersonaBinding { Type = PersonaBindingType.Project, Target = "p1" }]);
        _sut.BuildFileScopes(_userId, persona).Should().BeNull();
    }

    // --- BuildIndex ---

    [Fact]
    public void BuildIndex_ЛимитСтрок()
    {
        EnableFlag();
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
        EnableFlag();
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
        EnableFlag();
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
        EnableFlag();
        // Проектная привязка без секции files — способ недоступен, индекс пуст
        var project = MakeProject("Скрытый");
        var bindings = new List<PersonaBinding>
        {
            new() { Type = PersonaBindingType.Project, Target = project.Id, Condition = "всё" },
        };
        _sut.BuildIndex(_userId, bindings, mountedSections: []).Should().BeNull();
    }

    // --- BuildTurnBlockAsync (гейты) ---

    [Fact]
    public async Task BuildTurnBlockAsync_ФлагВыключен_Null()
    {
        var persona = _personas.Create(_userId, "Аналитик", "Аналитик", null, null,
            null, null, PersonaScope.Global, null, null, null, true);
        _personas.UpdateBindings(persona.Id, _userId,
            [ToolBinding("tasks", PersonaBindingMode.Auto)]);

        (await _sut.BuildTurnBlockAsync(_userId, persona.Id, "вопрос", [])).Should().BeNull();
    }

    [Fact]
    public async Task BuildTurnBlockAsync_ПривязокНет_Null()
    {
        EnableFlag();
        var persona = _personas.Create(_userId, "Пустой", null, null, null,
            null, null, PersonaScope.Global, null, null, null, true);

        (await _sut.BuildTurnBlockAsync(_userId, persona.Id, "вопрос", [])).Should().BeNull();
    }

    [Fact]
    public async Task BuildTurnBlockAsync_АктивныеПривязки_БлокСИндексом()
    {
        EnableFlag();
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
}
