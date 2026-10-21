using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Skills;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// QA-фикс L3: валидация Skill-привязки плагина (target «plugin:{id}/{skill}») при
// СОЗДАНИИ привязки. PersonaBindingsService проверяет через шов IPluginSkillResolver:
// плагин установлен ВЛАДЕЛЬЦЕМ ПЕРСОНЫ (не чужим), включён, навык объявлен, флаг
// plugins-mvp вкл. Тесты используют РЕАЛЬНЫЙ PluginSkillResolver + PluginStore (не мок),
// чтобы проверка владельца была фактической: мутация «убрать проверку владельца» красит
// ForeignPlugin_Отказ (плагин чужого владельца вдруг проходит).
public class PersonaPluginSkillBindingValidationTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string OwnerB = "owner-b";
    private const string PluginId = "local-a/pkg";
    private const string Version = "1.0.0";
    private const string Skill = "my-skill";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PersonaBindingsService _sut;
    private readonly PersonaBindingsService _sutSubsystemOff;

    public PersonaPluginSkillBindingValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_persona_plugin_skill_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = BuildConfig(_dir);
        _store = new PluginStore(config);

        // Реальный шов: PluginStore + флаг plugins-mvp вкл.
        var resolver = new PluginSkillResolver(_store, new StubFlagGate { PluginsMvp = true });
        _sut = BuildSut(config, resolver);
        // Подсистема выключена → шов null (опциональная зависимость не передана).
        _sutSubsystemOff = BuildSut(config, pluginSkillResolver: null);
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

    private static PersonaBindingsService BuildSut(IConfiguration config, IPluginSkillResolver? pluginSkillResolver)
    {
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        var personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        return new PersonaBindingsService(personas, projects, wkStore, knowledge, new SkillsService(),
            users, config, NullLogger<PersonaBindingsService>.Instance,
            pluginSkillResolver: pluginSkillResolver);
    }

    // Установить плагин владельца: запись installed.json (enabled/disabled) + пакет с навыком.
    private void InstallPlugin(string owner, bool enabled, string skillName = Skill)
    {
        var pkg = _store.PackageDir(owner, PluginId, Version);
        Directory.CreateDirectory(Path.Combine(pkg, "skills", skillName));
        File.WriteAllText(Path.Combine(pkg, "skills", skillName, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: test\n---\n# {skillName}\n");
        _store.SaveInstalled(owner,
        [
            new PluginInstall { Id = PluginId, Version = Version, Sha256 = "abc", Enabled = enabled, Source = "upload" },
        ]);
    }

    private static PersonaBinding SkillBinding(string target) =>
        new() { Type = PersonaBindingType.Skill, Target = target, Condition = "когда нужен", Mode = PersonaBindingMode.Auto };

    // --- Валидная цель: плагин владельца, включён, навык объявлен → null (200) ---

    [Fact]
    public async Task ValidTarget_Валидна()
    {
        InstallPlugin(OwnerA, enabled: true);
        var err = await _sut.ValidateAsync(OwnerA, SkillBinding($"plugin:{PluginId}/{Skill}"), existing: null);
        err.Should().BeNull("плагин владельца, включён, навык объявлен — привязка валидна");
    }

    // --- Чужой плагин: плагин у владельца A, персона владельца B (у B нет плагина) → отказ.
    //     Это и есть проверка владельца: мутация «не проверять владельца» → A проходит → красный. ---

    [Fact]
    public async Task ForeignPlugin_Отказ()
    {
        InstallPlugin(OwnerA, enabled: true); // плагин только у A
        var err = await _sut.ValidateAsync(OwnerB, SkillBinding($"plugin:{PluginId}/{Skill}"), existing: null);
        err.Should().Be("Плагин не найден", "у владельца B плагина нет — чужой плагин");
    }

    // --- Несуществующий навык: плагин есть, но навык другой → отказ ---

    [Fact]
    public async Task NonExistentSkill_Отказ()
    {
        InstallPlugin(OwnerA, enabled: true, skillName: "real-skill");
        var err = await _sut.ValidateAsync(OwnerA, SkillBinding($"plugin:{PluginId}/other-skill"), existing: null);
        err.Should().Be("Навык не объявлен в плагине");
    }

    // --- Выключенный плагин → отказ ---

    [Fact]
    public async Task DisabledPlugin_Отказ()
    {
        InstallPlugin(OwnerA, enabled: false);
        var err = await _sut.ValidateAsync(OwnerA, SkillBinding($"plugin:{PluginId}/{Skill}"), existing: null);
        err.Should().Be("Плагин выключен");
    }

    // --- Подсистема выключена (шов null) → понятный отказ, не 500 ---

    [Fact]
    public async Task SubsystemOff_Отказ()
    {
        InstallPlugin(OwnerA, enabled: true);
        var err = await _sutSubsystemOff.ValidateAsync(OwnerA, SkillBinding($"plugin:{PluginId}/{Skill}"), existing: null);
        err.Should().Be("Подсистема плагинов выключена");
    }

    // --- Флаг plugins-mvp выключен → отказ (реальный шов проверяет флаг) ---

    [Fact]
    public async Task FlagOff_Отказ()
    {
        InstallPlugin(OwnerA, enabled: true);
        var config = BuildConfig(_dir);
        var resolver = new PluginSkillResolver(_store, new StubFlagGate { PluginsMvp = false });
        var sut = BuildSut(config, resolver);
        var err = await sut.ValidateAsync(OwnerA, SkillBinding($"plugin:{PluginId}/{Skill}"), existing: null);
        err.Should().Be("Флаг plugins-mvp выключен");
    }

    // --- Обычная глобальная привязка не сломана: не-plugin цель → прежняя проверка ---

    [Fact]
    public async Task GlobalSkill_НеСломан()
    {
        // Глобальный навык: SkillsService читает ~/.claude/skills — в тесте каталог пуст,
        // поэтому любая не-plugin цель → «Скилл не найден среди глобальных» (прежнее поведение).
        var err = await _sut.ValidateAsync(OwnerA, SkillBinding("some-global-skill"), existing: null);
        err.Should().Be("Скилл не найден среди глобальных", "не-plugin цель идёт прежним путём");
    }

    // IFeatureFlagGate: единственный интересующий флаг — plugins-mvp.
    private sealed class StubFlagGate : IFeatureFlagGate
    {
        public bool PluginsMvp;
        public bool IsEnabled(string userId, string key) =>
            key == FeatureFlagKeys.PluginsMvp && PluginsMvp;
    }
}
