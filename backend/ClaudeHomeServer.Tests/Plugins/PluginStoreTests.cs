using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Backup;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// Юниты вертикали Plugins (ADR-031, шаг 1.2): файловое хранилище PluginStore
// (per-owner изоляция, все пути через SafePath.Join) и реализация шва IPluginDirSource
// (фильтр включённых пакетов, гейт персоны, MVP-гейт локального проекта).
//
// Тесты живут в ClaudeHomeServer.Tests (не в отдельной ClaudeHomeServer.Plugins.Tests):
// стенду не нужен Program из Main, а вертикаль — чистая библиотека под Web SDK.
public class PluginStoreTests
{
    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), "ccs-plugins-" + Guid.NewGuid().ToString("N"));

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    private static PluginStore NewStore(out string dataDir)
    {
        dataDir = CreateTempDir();
        Directory.CreateDirectory(dataDir);
        return new PluginStore(BuildConfig(dataDir));
    }

    // Серверный проект (DeviceId == null) — плагины доступны; локальный — нет.
    private static Project ServerProject() => new() { Id = "p1", RootPath = "/srv/p1" };
    private static Project LocalProject() => new() { Id = "p2", RootPath = "/home/u/p2", DeviceId = "dev-1" };

    // --- PluginStore: per-owner изоляция ---

    [Fact]
    public void LoadInstalled_ВладелецНеВидитЗаписиДругогоВладельца()
    {
        var store = NewStore(out _);

        store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        store.LoadInstalled("owner-a").Should().HaveCount(1);
        store.LoadInstalled("owner-b").Should().BeEmpty();
    }

    [Fact]
    public void SaveLoadInstalled_ЦиклСохраняетЗаписи()
    {
        var store = NewStore(out _);

        var records = new List<PluginInstall>
        {
            new() { Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
            new() { Id = "local-a/other", Version = "2.0.0", Sha256 = "b", Enabled = false, Source = "upload" },
        };
        store.SaveInstalled("owner-a", records);

        var loaded = store.LoadInstalled("owner-a");
        loaded.Should().HaveCount(2);
        loaded[0].Id.Should().Be("local-a/pkg");
        loaded[0].Enabled.Should().BeTrue();
        loaded[1].Id.Should().Be("local-a/other");
        loaded[1].Enabled.Should().BeFalse();
        loaded[1].Source.Should().Be("upload");
    }

    // --- PluginStore: все пути через SafePath.Join ---

    [Fact]
    public void OwnerRoot_ПутьСДвухТочкамиВыходящийЗаДанные_Отклоняется()
    {
        var store = NewStore(out _);

        // ownerId «../..» выносит корень владельца (data/plugins/{owner}) за пределы data/ —
        // SafePath.Join обязан отклонить исключением, а не нормализовать «..» в сосед.
        Action act = () => store.OwnerRoot("../..");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void PackageDir_ПутьСДвухТочкамиВыходящийЗаВладельца_Отклоняется()
    {
        var store = NewStore(out _);

        // M3: id «../..» — не по формату {publisher}/{slug} ([a-z0-9-]+), поэтому отклоняется
        // формат-валидацией (ArgumentException) ДО SafePath.Join, а не как UnauthorizedAccessException.
        Action act = () => store.PackageDir("owner-a", "../..", "1.0.0");
        act.Should().Throw<ArgumentException>();
    }

    // --- IPluginDirSource: фильтры ---

    [Fact]
    public void GetPluginDirs_ВозвращаетТолькоВключённыеПакеты()
    {
        var store = NewStore(out _);
        store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/on", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
            new PluginInstall { Id = "local-a/off", Version = "1.0.0", Sha256 = "b", Enabled = false, Source = "draft" },
        ]);
        // Minor: каталог включённого пакета обязан существовать, иначе --plugin-dir не отдаётся.
        Directory.CreateDirectory(store.PackageDir("owner-a", "local-a/on", "1.0.0"));

        var source = new PluginDirSource(store, Mock.Of<IPersonaSkillBindingLookup>());
        var dirs = source.GetPluginDirs("owner-a", null, ServerProject());

        dirs.Should().HaveCount(1);
        dirs[0].Should().EndWith($"packages/local-a/on/1.0.0");
    }

    [Fact]
    public void GetPluginDirs_ЛокальныйПроект_БезПлагинов()
    {
        var store = NewStore(out _);
        store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/on", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var source = new PluginDirSource(store, Mock.Of<IPersonaSkillBindingLookup>());
        // MVP: у локального проекта (DeviceId != null) плагинов нет (ProjectCapabilities).
        source.GetPluginDirs("owner-a", null, LocalProject()).Should().BeEmpty();
    }

    [Fact]
    public void GetPluginDirs_Персона_ОткрываетТолькоСвоиБиндинги()
    {
        var store = NewStore(out _);
        store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/open", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
            new PluginInstall { Id = "local-a/closed", Version = "1.0.0", Sha256 = "b", Enabled = true, Source = "draft" },
        ]);

        // Персона имеет Skill-биндинг только на local-a/open (target plugin:local-a/open).
        var bindings = new Mock<IPersonaSkillBindingLookup>();
        bindings
            .Setup(b => b.Get("owner-a", "persona-1"))
            .Returns(new PersonaForSkillSuggestion(
                Name: "Персона", Role: null, Description: null, Character: null,
                MustDo: null, SkillBindingTargets: ["plugin:local-a/open"]));

        // Minor: каталог открытого пакета обязан существовать, иначе --plugin-dir не отдаётся.
        Directory.CreateDirectory(store.PackageDir("owner-a", "local-a/open", "1.0.0"));

        var source = new PluginDirSource(store, bindings.Object);
        var dirs = source.GetPluginDirs("owner-a", "persona-1", ServerProject());

        dirs.Should().HaveCount(1);
        dirs[0].Should().EndWith("packages/local-a/open/1.0.0");
    }

    [Fact]
    public void GetPluginDirs_ПерсонаНеНайдена_БезПлагинов()
    {
        var store = NewStore(out _);
        store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/open", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var bindings = new Mock<IPersonaSkillBindingLookup>();
        bindings.Setup(b => b.Get("owner-a", "ghost")).Returns((PersonaForSkillSuggestion?)null);

        var source = new PluginDirSource(store, bindings.Object);
        // Персона задана, но не найдена — биндингов нет, значит пакетов нет (гейт — биндинг).
        source.GetPluginDirs("owner-a", "ghost", ServerProject()).Should().BeEmpty();
    }

    // --- Бэкап: data/plugins попадает в основной архив (ADR-031 §2.3) ---

    [Fact]
    public void BackupPaths_КаталогПлагинов_ПопадаетВАрхив()
    {
        // data/plugins/{owner}/... — данные владельца, в основной (облачный) архив едут;
        // секреты (McpSecretStore) — отдельным локальным архивом (после 3.3).
        BackupPaths.ShouldInclude("plugins/owner-a/installed.json").Should().BeTrue();
        BackupPaths.ShouldInclude("plugins/owner-a/packages/local-a/pkg/1.0.0/plugin.json").Should().BeTrue();
        BackupPaths.ShouldInclude("plugins/owner-a/_drafts/slug/SKILL.md").Should().BeTrue();
    }
}
