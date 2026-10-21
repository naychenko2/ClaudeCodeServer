using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Skills;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// Шаг 2.2 (ADR-031 §2.5): привязка персоны типа Skill с target «plugin:{id}/{skill}».
// Плагин установлен и включён → навык в блоке промпта; плагин удалён → привязка ТИХО
// пропадает из промпта, без исключения. Мутация «удаляем плагин, привязка не пропадает»
// (DescribeWay не проверяет наличие пакета) → тест красный.
public class PluginBindingResolverTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string PluginId = "local-a/pkg";
    private const string SkillName = "my-skill";
    private const string Target = "plugin:local-a/pkg/my-skill";

    private readonly string _dir;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _sut;
    private readonly PluginStore _store;

    public PluginBindingResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_bind_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = BuildConfig(_dir);

        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projects, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, users, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var mcp = new ClaudeHomeServer.Services.Mcp.McpRegistry(config,
            new ClaudeHomeServer.Services.Mcp.McpSecretStore(config));

        // SkillsService с тем же DataPath, что и PluginStore: видит пакеты владельца.
        _store = new PluginStore(config);
        _sut = new PersonaBindingsService(_personas, projects, wkStore, knowledge,
            new SkillsService(config), users, config, NullLogger<PersonaBindingsService>.Instance,
            mcp, notes: notesSvc, notesKb: notesKb);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // Персона с одной Skill-привязкой на навык плагина.
    private Persona MakePersona()
    {
        var persona = _personas.Create(Owner, "Тест", null, null, null, null, null,
            PersonaScope.Global, null, null, null, true);
        _personas.UpdateBindings(persona.Id, Owner,
        [
            new PersonaBinding { Type = PersonaBindingType.Skill, Target = Target, Mode = PersonaBindingMode.Auto },
        ]);
        return persona;
    }

    private void InstallPlugin()
    {
        var pkg = _store.PackageDir(Owner, PluginId, "1.0.0");
        var skillDir = Path.Combine(pkg, "skills", SkillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {SkillName}\ndescription: тестовый навык\nuser-invocable: true\n---\n\nТело");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = PluginId, Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);
    }

    // Плагин установлен → привязка в блоке промпта (указывает на файл навыка).
    [Fact]
    public async Task PluginBindingResolver_УстановленныйПлагин_ПривязкаВБлоке()
    {
        var persona = MakePersona();
        InstallPlugin();

        var block = await _sut.BuildTurnBlockAsync(Owner, persona.Id, "вопрос", []);

        block.Should().NotBeNull("плагин установлен и включён — навык в блоке");
        block!.Should().Contain("Привязанные знания и правила")
            .And.Contain("навык")
            .And.Contain("my-skill");
    }

    // M3: в container агенту не дают ХОСТОВЫЙ путь (в песочнице он другой). Промпт —
    // «используй навык {slug}:{skill} через Skill-тул», а не путь к файлу. Мутация
    // «вернуть FilePath в промпт» → хостовый путь в блоке → тест красный.
    [Fact]
    public async Task PersonaBindingsService_ContainerPathПопалВПромпт()
    {
        var persona = MakePersona();
        InstallPlugin();

        var block = await _sut.BuildTurnBlockAsync(Owner, persona.Id, "вопрос", []);
        block.Should().NotBeNull("плагин установлен — привязка в блоке");

        // Хостовый путь к SKILL.md в промпт не попадает (в песочнице путь другой).
        var skillFile = Path.Combine(_store.PackageDir(Owner, PluginId, "1.0.0"),
            "skills", SkillName, "SKILL.md");
        block!.Should().NotContain(skillFile,
            "M3: хостовый путь к файлу навыка в промпт не попадает");
        // Вместо пути — имя навыка через Skill-тул.
        block.Should().Contain("Skill-тул",
            "M3: промпт говорит «используй навык через Skill-тул», а не путь");
    }

    // Плагин удалён → привязка тихо пропадает из промпта (блок пуст), без исключения.
    // Мутация «удаляем плагин, привязка не пропадает» → блок снова содержит навык → тест красный.
    [Fact]
    public async Task PluginBindingResolver_УдалённыйПлагин_ПривязкаПропала()
    {
        var persona = MakePersona();
        InstallPlugin();
        // Sanity: с установленным плагинном блок не пуст.
        (await _sut.BuildTurnBlockAsync(Owner, persona.Id, "вопрос", [])).Should().NotBeNull();

        // Удалить плагин: запись из installed.json исчезает.
        _store.SaveInstalled(Owner, []);

        var block = await _sut.BuildTurnBlockAsync(Owner, persona.Id, "вопрос", []);
        block.Should().BeNull(
            "плагин удалён — единственная привязка тихо пропадает из промпта, без исключения");
    }
}
