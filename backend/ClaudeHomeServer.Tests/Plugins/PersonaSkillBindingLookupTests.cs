using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Skills;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// M4.2: PersonaSkillBindingLookup отсеивает Skill-привязки с Mode == Off — выключенная
// человеком привязка не должна открывать пакет (--plugin-dir). Персона читается PersonaManager'ом
// из stора (personas.json), поэтому тест кладёт персону с биндингами в файл и строит lookup.
// R-A2 M2: цели берутся из EffectiveBindings; без стора настроек специальностей (этот тест)
// эффективный список = свои привязки — поведение прежнее. Наследованные цели — в тесте
// Миграции/Наследования (PersonaBindingsInheritanceTests, там есть стор настроек).
public class PersonaSkillBindingLookupTests : IDisposable
{
    private const string OwnerId = "owner-a";
    private readonly string _dir;

    public PersonaSkillBindingLookupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_persona_skill_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // Персона с Skill-привязками: одна выключена (Mode == Off), одна Auto.
    private PersonaSkillBindingLookup BuildLookup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();
        File.WriteAllText(Path.Combine(_dir, "personas.json"),
            """
            [
              {
                "id": "persona-1",
                "ownerId": "owner-a",
                "name": "Тест",
                "bindings": [
                  { "type": "skill", "target": "plugin:local-a/pkg/off-skill", "mode": "off" },
                  { "type": "skill", "target": "plugin:local-a/pkg/auto-skill", "mode": "auto" }
                ]
              }
            ]
            """);
        var personas = new PersonaManager(config);
        // Минимальная обвязка PersonaBindingsService (R-A2 M2: lookup читает EffectiveBindings;
        // без specialtySettings он = свои привязки — предмет этого теста)
        var users = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        var projects = new ProjectManager(config, users, new AppSettingsService(config));
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var bindings = new PersonaBindingsService(personas, projects, wkStore, knowledge, new SkillsService(),
            users, config, NullLogger<PersonaBindingsService>.Instance);
        return new PersonaSkillBindingLookup(personas, bindings);
    }

    // M4.2: выключенная (Mode == Off) Skill-привязка не попадает в SkillBindingTargets —
    // пакет не «открывается». Мутация «не отсеивать Off» → off-skill появится → тест красный.
    [Fact]
    public void OffПривязка_НеОткрываетКаталог()
    {
        var lookup = BuildLookup();
        var result = lookup.Get(OwnerId, "persona-1");

        result.Should().NotBeNull();
        result!.SkillBindingTargets.Should().Contain("plugin:local-a/pkg/auto-skill",
            "включённая (Auto) привязка — в списке");
        result.SkillBindingTargets.Should().NotContain("plugin:local-a/pkg/off-skill",
            "выключенная (Off) привязка не открывает пакет");
    }
}
