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

    // Локальный проект (ADR-016/ADR-031 §5.8, MVP): плагины выключены — каталог пакетов и
    // секреты на устройство не едут. ProjectCapabilities.PluginsFor — единственная проверка
    // локальности. Мутация «убрать фильтр локального проекта» → этот тест красный.
    [Fact]
    public void GetPluginDirs_ЛокальныйПроект_НеДаетКаталоги()
    {
        var pkg = _store.PackageDir("owner-a", "local-a/x", "1.0.0");
        Directory.CreateDirectory(pkg);
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/x", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var source = new PluginDirSource(_store, Mock.Of<IPersonaSkillBindingLookup>());
        // Локальный проект: DeviceId != null → плагины выключены → [].
        var localProject = new Project { Id = "p1", DeviceId = "device-1" };
        source.GetPluginDirs("owner-a", null, localProject).Should().BeEmpty(
            "у локального проекта в MVP плагинов нет (ProjectCapabilities.PluginsFor)");
    }

    // C1: привязка, созданная в формате пикера «plugin:{id}/{skill}», ОБЯЗАНА дать каталог
    // в GetPluginDirs. До фикса PluginDirSource сравнивал «plugin:{id}» (без навыка) с
    // «plugin:{id}/{skill}» (пикер) — никогда не совпадало, и --plugin-dir не доставлялся.
    [Fact]
    public void PluginDirSource_ПривязкаПерсоныДаетКаталог()
    {
        var pkgA = _store.PackageDir("owner-a", "local-a/pkg", "1.0.0");
        Directory.CreateDirectory(pkgA);
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        // Персона привязана к навыку плагина в формате пикера: plugin:{publisher}/{slug}/{skill}.
        var lookup = new Mock<IPersonaSkillBindingLookup>();
        lookup.Setup(l => l.Get("owner-a", "persona-1"))
            .Returns(new PersonaForSkillSuggestion("Тест", null, null, null, null,
                ["plugin:local-a/pkg/my-skill"]));

        var source = new PluginDirSource(_store, lookup.Object);
        var dirs = source.GetPluginDirs("owner-a", "persona-1", ServerProject());

        dirs.Should().ContainSingle(
            "привязка в формате пикера даёт каталог привязанного плагина (C1)")
            .Which.Should().EndWith("packages/local-a/pkg/1.0.0");
    }

    // C1 (мутация): плагин, НЕ привязанный персоне, в --plugin-dir не попадает.
    // Убираем проверку префикса id (фильтр персоны) → чужой плагин B тоже попадёт → тест красный.
    [Fact]
    public void PluginDirSource_ПривязкаПерсоныНеДаетКаталог()
    {
        var pkgA = _store.PackageDir("owner-a", "local-a/pkg", "1.0.0");
        Directory.CreateDirectory(pkgA);
        var pkgB = _store.PackageDir("owner-a", "local-b/other", "1.0.0");
        Directory.CreateDirectory(pkgB);
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/pkg", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
            new PluginInstall { Id = "local-b/other", Version = "1.0.0", Sha256 = "b", Enabled = true, Source = "draft" },
        ]);

        // Персона привязана ТОЛЬКО к плагину A; B установлен, но не привязан.
        var lookup = new Mock<IPersonaSkillBindingLookup>();
        lookup.Setup(l => l.Get("owner-a", "persona-1"))
            .Returns(new PersonaForSkillSuggestion("Тест", null, null, null, null,
                ["plugin:local-a/pkg/my-skill"]));

        var source = new PluginDirSource(_store, lookup.Object);
        var dirs = source.GetPluginDirs("owner-a", "persona-1", ServerProject());

        dirs.Should().Contain(d => d.EndsWith("packages/local-a/pkg/1.0.0"),
            "привязанный плагин A — в --plugin-dir");
        dirs.Should().NotContain(d => d.EndsWith("packages/local-b/other/1.0.0"),
            "плагин B не привязан персоне — в --plugin-dir не попадает (C1)");
    }

    // СТОРОЖ (инвариант 4, ADR-031 §2.5): набор каталогов --plugin-dir зависит ТОЛЬКО от
    // свойств сессии (владелец, персона, проект), не от хода. Иначе сигнатура запуска CLI
    // «мерцает» между ходами и каждый переход убивает процесс claude вместе со всеми MCP-серверами.
    // M5: сравниваем ДВА РАЗНЫХ хода (отдельные снапшоты проекта), а не один вход дважды:
    // GetPluginDirs перечитывает installed.json на каждый ход, и результат обязан совпасть,
    // даже если снапшот хода — другой объект. Мутация «метод читает ambient-состояние хода»
    // → два хода расходятся → тест красный.
    [Fact]
    public void PluginDirSource_НаборНеЗависитОтХода()
    {
        var pkg = _store.PackageDir("owner-a", "local-a/x", "1.0.0");
        Directory.CreateDirectory(pkg);
        _store.SaveInstalled("owner-a",
        [
            new PluginInstall { Id = "local-a/x", Version = "1.0.0", Sha256 = "a", Enabled = true, Source = "draft" },
        ]);

        var source = new PluginDirSource(_store, Mock.Of<IPersonaSkillBindingLookup>());
        // Два разных хода: отдельные снапшоты проекта (одинаковые данные, разные объекты).
        var turn1 = new Project { Id = "p1", RootPath = "/srv/p1" };
        var turn2 = new Project { Id = "p1", RootPath = "/srv/p1" };
        var first = source.GetPluginDirs("owner-a", null, turn1);
        var second = source.GetPluginDirs("owner-a", null, turn2);
        first.Should().BeEquivalentTo(second,
            "набор --plugin-dir не зависит от хода: два разных хода дают одну сигнатуру запуска");
    }
}
