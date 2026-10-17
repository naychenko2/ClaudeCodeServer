using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ClaudeHomeServer.Tests.Plugins;

// IPluginDirSource (ADR-031 §2.5, M8/minor): каталоги включённых пакетов для --plugin-dir.
// M8: project nullable (ход без привязки — все включённые, локальности нет).
// Minor: каталог пакета обязан существовать, иначе --plugin-dir не указывает на пустоту.
public class PluginDirSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly PluginStore _store;

    public PluginDirSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_plugins_dirsrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new PluginStore(BuildConfig(_dir));
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

    private static Project ServerProject() => new() { Id = "p1", RootPath = "/srv/p1" };

    // Minor: каталог пакета не существует → --plugin-dir не указывает на пустоту.
    [Fact]
    public void GetPluginDirs_КаталогПакетаНеСуществует_НеВключён()
    {
        // Запись включена, но каталог пакета на диске нет (не скопирован/удалён).
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/ghost", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var source = new PluginDirSource(_store, Mock.Of<IPersonaSkillBindingLookup>());
        // Каталог packages/local-a/ghost/1.0.0 не создан → в список не попадает.
        source.GetPluginDirs("owner-a", null, ServerProject()).Should().BeEmpty();
    }

    [Fact]
    public void GetPluginDirs_КаталогСуществует_Включён()
    {
        var pkg = _store.PackageDir("owner-a", "local-a/present", "1.0.0");
        Directory.CreateDirectory(pkg);
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/present", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var source = new PluginDirSource(_store, Mock.Of<IPersonaSkillBindingLookup>());
        var dirs = source.GetPluginDirs("owner-a", null, ServerProject());
        dirs.Should().ContainSingle();
        dirs[0].Should().EndWith("packages/local-a/present/1.0.0");
    }

    // M8: project null (ход без привязки) — все включённые пакеты, локальности нет.
    [Fact]
    public void GetPluginDirs_ProjectNull_ВсеВключённые()
    {
        var pkg = _store.PackageDir("owner-a", "local-a/x", "1.0.0");
        Directory.CreateDirectory(pkg);
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/x", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var source = new PluginDirSource(_store, Mock.Of<IPersonaSkillBindingLookup>());
        source.GetPluginDirs("owner-a", null, project: null).Should().HaveCount(1);
    }
}
