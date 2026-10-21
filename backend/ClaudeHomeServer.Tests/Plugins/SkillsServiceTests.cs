using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Skills;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// M2 (rework пачки B): ResolveOwnerPluginSkill учитывает проект — фильтрует по
// ProjectCapabilities.PluginsFor. У локального проекта плагины выключены, поэтому
// навык плагина не виден (привязка не попадает в промпт); серверный проект и ход без
// привязки (project null) — навык на месте.
public class SkillsServiceTests : IDisposable
{
    private const string Owner = "owner-a";
    private const string PluginId = "local-a/pkg";
    private const string SkillName = "my-skill";
    private const string Target = "plugin:local-a/pkg/my-skill";

    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly SkillsService _skills;

    public SkillsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_skills_svc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = BuildConfig(_dir);
        _store = new PluginStore(config);
        _skills = new SkillsService(config);
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

    // Установить пакет владельца: каталог с навыком + запись в installed.json.
    private void InstallPlugin(string id, string version, string skillName)
    {
        var pkg = _store.PackageDir(Owner, id, version);
        var skillDir = Path.Combine(pkg, "skills", skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: тестовый навык\nuser-invocable: true\n---\n\nТело");
        _store.SaveInstalled(Owner,
        [
            new PluginInstall { Id = id, Version = version, Sha256 = "a", Enabled = true, Source = "draft" },
        ]);
    }

    // M2: навык плагина НЕ виден у локального проекта (плагины выключены, ProjectCapabilities).
    // Мутация «не фильтруем по проекту в SkillsService» → навык виден → тест красный.
    [Fact]
    public void SkillsService_ЛокальныйПроектВсеРавноВидит()
    {
        InstallPlugin(PluginId, "1.0.0", SkillName);
        var localProject = new Project { Id = "p1", DeviceId = "device-1" };

        // Локальный проект: плагины выключены → навык не виден (null).
        _skills.ResolveOwnerPluginSkill(Owner, Target, localProject).Should().BeNull(
            "у локального проекта в MVP плагины выключены (ProjectCapabilities.PluginsFor)");

        // Контроль: серверный проект (и ход без привязки, project null) — навык на месте.
        _skills.ResolveOwnerPluginSkill(Owner, Target, null).Should().NotBeNull(
            "серверный проект/ход без привязки — навык плагина виден");
    }
}
