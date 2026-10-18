using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using ClaudeHomeServer.Services.Skills;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// Шаг 2.2 (ADR-031 §2.5): навыки и команды УСТАНОВЛЕННЫХ ПАКЕТОВ ВЛАДЕЛЬЦА
// (data/plugins/{owner}/, GetOwnerPluginSkills) — отдельный источник от плагинов профиля CLI.
// Метка источника «plugin:{id}»; user-invocable: true → slash-команда; per-owner изоляция;
// локальный проект (ProjectCapabilities.PluginsFor) — навыки не попадают в листинг.
public class PluginSkillLocalizerTests : IDisposable
{
    private readonly string _dir;
    private readonly PluginStore _store;
    private readonly SkillsService _skills;

    public PluginSkillLocalizerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_skills_" + Guid.NewGuid().ToString("N"));
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

    // Установить пакет владельца: каталог пакета с навыком + запись в installed.json.
    private void InstallPlugin(string owner, string id, string version, string skillName, bool userInvocable)
    {
        var pkg = _store.PackageDir(owner, id, version);
        var skillDir = Path.Combine(pkg, "skills", skillName);
        Directory.CreateDirectory(skillDir);
        var ui = userInvocable ? "user-invocable: true" : "user-invocable: false";
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: тестовый навык\n{ui}\n---\n\nТело навыка");
        _store.SaveInstalled(owner,
        [
            new PluginInstall { Id = id, Version = version, Sha256 = "a", Enabled = true, Source = "draft" },
        ]);
    }

    // Per-owner изоляция: у владельца A есть навык плагина A, у B его нет.
    // Мутация «убрать фильтр владельца» (GetOwnerPluginSkills читает чужой каталог) → тест красный.
    [Fact]
    public void PluginSkillLocalizer_ЧужойВладелец_НеВидитПакетыДругого()
    {
        InstallPlugin("owner-a", "local-a/pkg", "1.0.0", "my-skill", userInvocable: true);
        // owner-b ничего не устанавливал.

        var a = _skills.GetOwnerPluginSkills("owner-a", null);
        a.Should().HaveCount(1);
        a[0].Source.Should().Be("plugin:local-a/pkg");
        a[0].Name.Should().Be("pkg:my-skill");
        a[0].UserInvocable.Should().BeTrue("user-invocable: true → slash-команда");

        _skills.GetOwnerPluginSkills("owner-b", null).Should().BeEmpty(
            "пакеты ключуются владельцем: у B нет установки A");
    }

    // Локальный проект (ADR-016/ADR-031 §5.8, MVP): плагины выключены — навыки в листинг
    // не попадают. Мутация «локальный проект не фильтруется» → тест красный.
    [Fact]
    public void PluginSkillLocalizer_ЛокальныйПроект_НеДаетНавыков()
    {
        InstallPlugin("owner-a", "local-a/pkg", "1.0.0", "my-skill", userInvocable: true);
        var localProject = new Project { Id = "p1", DeviceId = "device-1" };

        _skills.GetOwnerPluginSkills("owner-a", localProject).Should().BeEmpty(
            "у локального проекта в MVP плагины выключены (ProjectCapabilities.PluginsFor)");

        // Серверный проект (и ход без привязки) — навык на месте.
        _skills.GetOwnerPluginSkills("owner-a", new Project { Id = "p1", RootPath = "/srv/p1" })
            .Should().HaveCount(1);
    }

    // user-invocable: false → навык не slash-команда (только через Skill-тул).
    [Fact]
    public void PluginSkillLocalizer_БезUserInvocable_НеSlashКоманда()
    {
        InstallPlugin("owner-a", "local-a/pkg", "1.0.0", "agent-skill", userInvocable: false);

        var s = _skills.GetOwnerPluginSkills("owner-a", null).Single();
        s.UserInvocable.Should().BeFalse("без user-invocable — только через Skill-тул, не в slash_commands");
    }

    // Команда из commands/ — по определению slash-команда (user-invocable).
    [Fact]
    public void PluginSkillLocalizer_КомандаИзCommands_SlashКоманда()
    {
        var pkg = _store.PackageDir("owner-a", "local-a/pkg", "1.0.0");
        var cmdDir = Path.Combine(pkg, "commands");
        Directory.CreateDirectory(cmdDir);
        File.WriteAllText(Path.Combine(cmdDir, "do-thing.md"),
            "---\ndescription: тестовая команда\n---\n\nТело команды");
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var s = _skills.GetOwnerPluginSkills("owner-a", null).Single();
        s.Name.Should().Be("pkg:do-thing");
        s.UserInvocable.Should().BeTrue("файл в commands/ — slash-команда");
    }

    // Контейнер (пункт 4): CLI 2.1.301 видит skills/команды, потому что --plugin-dir
    // (PluginDirSource) указывает на bind-mount data/plugins/{owner}/packages/{id}/{version} —
    // тот же каталог, из которого GetOwnerPluginSkills читает навыки. Сверка: путь из
    // PluginDirSource == корень FilePath навыка из GetOwnerPluginSkills.
    [Fact]
    public void PluginSkillLocalizer_Контейнер_PluginDirСовпадаетСКаталогомНавыков()
    {
        InstallPlugin("owner-a", "local-a/pkg", "1.0.0", "my-skill", userInvocable: true);
        var serverProject = new Project { Id = "p1", RootPath = "/srv/p1" };

        // --plugin-dir: PluginDirSource отдаёт каталог пакета (bind-mount data/plugins).
        var dirSource = new PluginDirSource(_store, Mock.Of<IPersonaSkillBindingLookup>());
        var pluginDir = dirSource.GetPluginDirs("owner-a", null, serverProject).Single();

        // Листинг читает навыки из того же каталога: FilePath навыка лежит внутри pluginDir.
        var skill = _skills.GetOwnerPluginSkills("owner-a", serverProject).Single();
        skill.FilePath.Should().StartWith(pluginDir,
            "навык читается из того же bind-mount, на который указывает --plugin-dir");
    }
}
