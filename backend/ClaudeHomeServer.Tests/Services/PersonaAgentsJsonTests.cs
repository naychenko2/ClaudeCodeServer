using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Per-run --agents JSON (CLI ≥ 2.1.281): состав определений, детерминированность
// содержимого, omitClaudeMd, миграция старых .md-файлов
public class PersonaAgentsJsonTests : IDisposable
{
    private readonly string _tempDir;
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly PersonaAgentFileSync _sut;
    private readonly string _agentsBase;

    public PersonaAgentsJsonTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pagent_json_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            // Флаг НЕ задан — JSON-канал (дефолт)
        });

        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _users = users;
        _users.Add("owner-1", "password123", "owner");
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _projects = projects;
        var providers = new LlmProviderRegistry(config);
        _personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notes = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notes, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(_personas, projects, wkStore,
            knowledge, new SkillsService(), users, config, NullLogger<PersonaBindingsService>.Instance, notes: notes, notesKb: notesKb);
        var generator = new PersonaAgentFileGenerator(new PersonaPromptBuilder(providers));
        _sut = new PersonaAgentFileSync(config, _personas, projects, providers, bindings, generator,
            users, appSettings, NullLogger<PersonaAgentFileSync>.Instance);
        _agentsBase = Path.Combine(_tempDir, "persona-agents");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private Persona Create(string name, string? model = null,
        PersonaScope scope = PersonaScope.Global, string? projectId = null) =>
        _personas.Create("owner-1", name, "Роль", null, null, model, null, scope, projectId,
            color: null, greeting: null, memoryEnabled: true);

    private string NewProject(string name)
    {
        var root = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(root);
        return _projects.Create(name, root, "owner-1", "owner").Id;
    }

    [Fact]
    public void GetAgentsDefs_СоставПоПерсоне()
    {
        var p = Create("Денис", model: "opus");

        var defs = _sut.GetAgentsDefs([p], omitClaudeMd: false);

        defs.Should().HaveCount(1);
        var def = defs[0];
        def.Name.Should().Be(p.Handle);
        // Description — строка маршрутизации с маркером «файл наш»
        def.Description.Should().Contain(PersonaAgentFileGenerator.AgentDescriptionMarker);
        // Промпт — консультационная рамка + характер персоны
        def.Prompt.Should().Contain("## Ты — консультант");
        // Инструменты: базовый read-набор всегда
        def.Tools.Should().Contain("Read").And.Contain("Grep").And.Contain("Glob");
        // Пин модели — только Claude-алиасы
        def.Model.Should().Be("opus");
        // Память персоны включена — pmem-сервер в mcpServers
        def.McpServers.Should().NotBeNull();
        def.McpServers!.Should().Contain(s => s.StartsWith("pmem_"));
        // Не-исполнитель — потолок 25
        def.MaxTurns.Should().Be(25);
        def.OmitClaudeMd.Should().BeFalse();
    }

    [Fact]
    public void GetAgentsDefs_Исполнитель_Потолок50()
    {
        // Specialty исполнителя + Access=Full (дефолт) → write-набор и потолок 50
        var p = Create("Бэкендер");
        p.Specialty = PersonaSpecialty.BackendExecutor;

        var defs = _sut.GetAgentsDefs([p], omitClaudeMd: false);

        defs[0].MaxTurns.Should().Be(50, "исполнитель: реализация с проверкой длиннее консультации");
        defs[0].Tools.Should().Contain("Bash").And.Contain("Write");
    }

    [Fact]
    public void Serialize_Детерминирован_ПоСессии()
    {
        var a = Create("Альфа");
        var b = Create("Бета");

        var json1 = ClaudeAgentsJson.Serialize(_sut.GetAgentsDefs([a, b], omitClaudeMd: false));
        var json2 = ClaudeAgentsJson.Serialize(_sut.GetAgentsDefs([b, a], omitClaudeMd: false));

        // Порядок персон в списке не важен: ключи JSON сортируются, содержимое стабильно
        json1.Should().Be(json2);
        json1.Should().Contain($"\"{a.Handle}\"").And.Contain($"\"{b.Handle}\"");
    }

    [Fact]
    public void Serialize_СменаПерсоны_МеняетСодержимое()
    {
        var p = Create("Гамма");
        var before = ClaudeAgentsJson.Serialize(_sut.GetAgentsDefs([p], omitClaudeMd: false));

        _personas.Update(p.Id, "owner-1", name: null, role: null, description: "Новое описание",
            systemPrompt: null, model: null, effort: null, scope: null, projectId: null,
            color: null, greeting: null, memoryEnabled: null);

        var after = ClaudeAgentsJson.Serialize(_sut.GetAgentsDefs([p], omitClaudeMd: false));
        after.Should().NotBe(before, "правка персоны → новый хэш → новый путь → перезапуск процесса");
    }

    [Fact]
    public void Serialize_OmitClaudeMd_ТолькоКогдаЗапрошен()
    {
        var p = Create("Дельта");

        var without = ClaudeAgentsJson.Serialize(_sut.GetAgentsDefs([p], omitClaudeMd: false));
        without.Should().NotContain("omitClaudeMd", "без BareMode поле не пишется");

        var with = ClaudeAgentsJson.Serialize(_sut.GetAgentsDefs([p], omitClaudeMd: true));
        with.Should().Contain("\"omitClaudeMd\":true");
    }

    [Fact]
    public void Serialize_ПустойСписок_ПустойОбъект()
    {
        ClaudeAgentsJson.Serialize([]).Should().Be("{}");
    }

    [Fact]
    public void MigrateLegacyAgentFiles_УдаляетТолькоСМаркером()
    {
        // Миграция обходит _users.GetAll() — проект обязан принадлежать реальному владельцу
        var user = _users.Add("migrate-owner", "password123", "user");
        var projRoot = Path.Combine(_tempDir, "migrate-proj");
        Directory.CreateDirectory(Path.Combine(projRoot, ".claude", "agents"));
        var project = _projects.Create("Migrate", projRoot, user.Id, "migrate-owner");

        var ours = Path.Combine(projRoot, ".claude", "agents", "ours.md");
        File.WriteAllText(ours,
            "---\nname: ours\ndescription: \"Backend (X) — Персона-консультант пользователя: вызывай\"\ntools: Read\n---\n\nтело");
        var alien = Path.Combine(projRoot, ".claude", "agents", "alien.md");
        File.WriteAllText(alien,
            "---\nname: alien\ndescription: \"Мой личный агент\"\ntools: Read\n---\n\nтело");

        var removed = _sut.MigrateLegacyAgentFiles();

        removed.Should().Be(1);
        File.Exists(ours).Should().BeFalse("наш файл (с маркером) удалён");
        File.Exists(alien).Should().BeTrue("чужой файл (без маркера) не тронут");
        Directory.Exists(Path.Combine(projRoot, ".claude", "agents")).Should()
            .BeTrue("папку целиком никогда не удаляем");
    }

    [Fact]
    public void MigrateLegacyAgentFiles_ФайловыйРежим_NoOp()
    {
        // Persona:AgentFiles=true — миграция no-op (файлы ещё в деле), маркер не пишется
        var dir = Path.Combine(_tempDir, "filemode");
        Directory.CreateDirectory(dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
                ["Persona:AgentFiles"] = "true",
            })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var providers = new LlmProviderRegistry(config);
        var personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notes = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notes, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(personas, projects, wkStore,
            knowledge, new SkillsService(), users, config, NullLogger<PersonaBindingsService>.Instance, notes: notes, notesKb: notesKb);
        var generator = new PersonaAgentFileGenerator(new PersonaPromptBuilder(providers));
        var sut = new PersonaAgentFileSync(config, personas, projects, providers, bindings, generator,
            users, appSettings, NullLogger<PersonaAgentFileSync>.Instance);

        sut.MigrateLegacyAgentFiles().Should().Be(0);
    }

    [Fact]
    public void GetAddDirs_JsonMode_Пусто()
    {
        Create("Кто-то");
        _sut.GetAddDirs("owner-1", sessionModel: null, projectId: null).Should()
            .BeEmpty("JSON-канал: --add-dir не нужен, определения едут в per-run файле");
    }

    [Fact]
    public void JsonMode_НеПишетМдФайлы()
    {
        var projRoot = Path.Combine(_tempDir, "jsonmode-proj");
        Directory.CreateDirectory(projRoot);
        var project = _projects.Create("JsonMode", projRoot, "owner-1", "owner");
        var p = Create("Персона", scope: PersonaScope.Project, projectId: project.Id);

        // Событие создания персоны в JSON-режиме .md не пишет
        File.Exists(Path.Combine(projRoot, ".claude", "agents", p.Handle + ".md")).Should()
            .BeFalse("JSON-канал: продукт .claude/agents/ не пишет");
    }
}
