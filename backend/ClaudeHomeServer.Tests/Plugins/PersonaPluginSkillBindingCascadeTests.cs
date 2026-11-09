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

// QA-фикс L3, шаг 3: сквозной тест «создание → чтение → удаление плагина».
// Создать Skill-привязку плагина → PersonaSkillBindingLookup находит навык плагина
// (SkillBindingTargets) → удалить плагин (PluginUninstall → каскад IPluginInstallCascade)
// → привязка исчезает (каскад снимает её) и чтение не падает. Это класс ошибок «каждая
// сторона правильна, стык нет»: мутация «убрать каскад» (no-op) → привязка не исчезает → красный.
public class PersonaPluginSkillBindingCascadeTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string PluginId = "local-a/pkg";
    private const string Version = "1.0.0";
    private const string Skill = "my-skill";
    private const string Target = "plugin:local-a/pkg/my-skill";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly PluginSecretsStore _secrets;
    private readonly PersonaManager _personas;
    private readonly PersonaSkillBindingLookup _lookup;
    private readonly PluginUninstall _uninstall;

    public PersonaPluginSkillBindingCascadeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_persona_plugin_cascade_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = BuildConfig(_dir);
        _store = new PluginStore(config);
        _secrets = new PluginSecretsStore(config);

        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projects = new ProjectManager(config, users, appSettings);
        _personas = new PersonaManager(config);
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var bindings = new PersonaBindingsService(_personas, projects, wkStore, knowledge, new SkillsService(),
            users, config, NullLogger<PersonaBindingsService>.Instance);

        // Реальный каскад: PluginInstallCascade → PluginBindingPurgerAdapter → PersonaBindingsService.
        var cascade = new PluginInstallCascade(new PluginBindingPurgerAdapter(bindings));
        _uninstall = new PluginUninstall(_store, _secrets, cascade);
        _lookup = new PersonaSkillBindingLookup(_personas);
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

    // Установить плагин (пакет + запись) + создать персону с Skill-привязкой к его навыку.
    private string Setup()
    {
        var pkg = _store.PackageDir(Owner, PluginId, Version);
        Directory.CreateDirectory(Path.Combine(pkg, "skills", Skill));
        File.WriteAllText(Path.Combine(pkg, "skills", Skill, "SKILL.md"),
            $"---\nname: {Skill}\ndescription: test\n---\n# {Skill}\n");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = PluginId, Version = Version, Sha256 = "abc", Enabled = true, Source = "upload" },
        ]);

        var persona = _personas.Create(Owner, "Тест", null, null, null, null, null,
            PersonaScope.Global, null, null, null, memoryEnabled: false);
        _personas.UpdateBindings(persona.Id, Owner,
        [
            new PersonaBinding { Type = PersonaBindingType.Skill, Target = Target, Condition = "когда нужен", Mode = PersonaBindingMode.Auto },
        ]);
        return persona.Id;
    }

    // Сквозной: создание → чтение (навык плагина виден) → удаление плагина → привязка исчезает,
    // чтение не падает.
    [Fact]
    public void CreateReadUninstall_ПривязкаИсчезаетИЧтениеНеПадает()
    {
        var personaId = Setup();

        // 1) Чтение: PersonaSkillBindingLookup находит навык плагина (SkillBindingTargets).
        var before = _lookup.Get(Owner, personaId);
        before.Should().NotBeNull();
        before!.SkillBindingTargets.Should().Contain(Target,
            "созданная привязка навыка плагина видна при сборке навыков персоны");

        // 2) Удаление плагина: каскад IPluginInstallCascade снимает Skill-привязку.
        _uninstall.Uninstall(Owner, PluginId).Should().BeTrue("плагин удалён");

        // 3) Чтение после удаления: привязка исчезла (каскад), чтение не падает.
        var after = _lookup.Get(Owner, personaId);
        after.Should().NotBeNull("персона жива — чтение не упало");
        after!.SkillBindingTargets.Should().NotContain(Target,
            "каскад удаления плагина снял Skill-привязку");
    }

    // QA-фикс N (major 3): шов IPersonaPluginBindingPurger — опциональная зависимость
    // каскада. Без шва (подсистема самодостаточна, Main не регистрировал адаптер) каскад
    // строится и OnUninstalled — no-op (не бросает, шов не трогает); с шовом — шов вызван.
    [Fact]
    public void КаскадБезШова_NoOp_СШовом_ШовВызван()
    {
        // Без шва: null — DI отдаёт, когда адаптер не зарегистрирован (подсистема самодостаточна).
        var noSeam = new PluginInstallCascade(purger: null);
        noSeam.OnUninstalled(Owner, PluginId); // не бросает — no-op

        // С шовом: вызов снимает привязки (тот же контракт, что в сквозном тесте выше).
        var purger = new Mock<IPersonaPluginBindingPurger>();
        purger.Setup(p => p.PurgePluginSkillBindings(Owner, PluginId)).Returns(3);
        var withSeam = new PluginInstallCascade(purger.Object);
        withSeam.OnUninstalled(Owner, PluginId);
        purger.Verify(p => p.PurgePluginSkillBindings(Owner, PluginId), Times.Once());
    }

    // Мутация «убрать каскад»: no-op-каскад не снимает привязку → она остаётся → тест красный.
    // Проверяем, что именно PluginInstallCascade (а не no-op) делает работу.
    [Fact]
    public void NoOpКаскад_ПривязкаОстаётся()
    {
        var personaId = Setup();
        _lookup.Get(Owner, personaId)!.SkillBindingTargets.Should().Contain(Target);

        // no-op-каскад (MVP): привязку не снимает.
        var noOp = new NoOpPluginInstallCascade();
        var uninstall = new PluginUninstall(_store, _secrets, noOp);
        uninstall.Uninstall(Owner, PluginId).Should().BeTrue();

        _lookup.Get(Owner, personaId)!.SkillBindingTargets.Should().Contain(Target,
            "no-op-каскад привязку не снял — мутация «убрать каскад»");
    }
}
