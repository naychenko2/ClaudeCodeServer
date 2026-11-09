using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ bind-mount'а каталога плагинов (ADR-031 §2.5): data/plugins → /plugins (ro) в
// container-сборке. Без mount'а --plugin-dir в контейнере указывал бы на /plugins/…, которого
// там нет (хостовый путь data/plugins не смонтирован) — CLI не видел бы пакеты. Mount добавляется
// одной правкой с правилом DockerPathMapper (тот же хостовый каталог PluginsHostDir) и учитывается
// в ConfigHash (появление каталога пересоздаёт контейнер).
//
// Мутация удаления блока `if (pluginsExists) args.AddRange(["-v", $"{pluginsHostDir}:{PluginsMount}:ro"])`
// → тест BuildRunArgs_КаталогПлагиновСуществует_ДобавляетсяMount красный.
// Мутация снятия pluginsHostDir из ConfigHash → тест ConfigHash_РазличаетНаличиеПлагинов красный.
public class SandboxManagerPluginsMountTests
{
    // Тmp-дерево: data/plugins создаётся/удаляется тестом (не build output).
    private static (SandboxManager Manager, string Tmp) Make(string? createPlugins = "yes")
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sbx_plugins_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(tmp, "ClaudeSandbox"),
        }).Build();
        var mgr = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        if (createPlugins == "yes")
            Directory.CreateDirectory(mgr.PluginsHostDir);
        return (mgr, tmp);
    }

    private static void CleanTmp(string tmp)
    {
        try { Directory.Delete(tmp, recursive: true); } catch { /* каталог мог уже не быть */ }
    }

    // Мутация «убрать bind-mount плагинов из BuildRunArgsForHost» → этот тест красный:
    // в args нет `-v {dataDir}/plugins:/plugins:ro`.
    [Fact]
    public void BuildRunArgs_КаталогПлагиновСуществует_ДобавляетсяMount()
    {
        var (mgr, tmp) = Make("yes");
        try
        {
            var args = SandboxManager.BuildRunArgsForHost(
                mgr.PluginsHostDir, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "test-hash",
                hostCliVersions: null, pluginsHostDir: mgr.PluginsHostDir);
            var argsLine = string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

            argsLine.Should().Contain($":{SandboxManager.PluginsMount}:ro",
                "при наличии каталога плагинов bind-mount /plugins:ro обязан быть в args — " +
                "иначе --plugin-dir в контейнере указывает на несмонтированный путь");
            // Хостовая сторона mount'а — тот же каталог, что мапится DockerPathMapper'ом.
            argsLine.Should().Contain($"{mgr.PluginsHostDir}:{SandboxManager.PluginsMount}:ro");
        }
        finally { CleanTmp(tmp); }
    }

    // Каталог плагинов отсутствует (пакетов ещё нет) — mount подавлен, контейнер не тащит пустой /plugins.
    [Fact]
    public void BuildRunArgs_КаталогПлагиновОтсутствует_MountПодавлен()
    {
        var (mgr, tmp) = Make("no");
        try
        {
            var args = SandboxManager.BuildRunArgsForHost(
                mgr.PluginsHostDir, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "test-hash",
                hostCliVersions: null, pluginsHostDir: mgr.PluginsHostDir);
            var argsLine = string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

            argsLine.Should().NotContain($":{SandboxManager.PluginsMount}:ro",
                "без каталога плагинов mount подавляется — иначе контейнер тащит пустой /plugins");
        }
        finally { CleanTmp(tmp); }
    }

    // ConfigHash различает «каталог плагинов есть» и «нет»: появление первого пакета
    // пересоздаёт контейнер, чтобы новый mount применился.
    [Fact]
    public void ConfigHash_РазличаетНаличиеПлагинов()
    {
        var (mgr, tmp) = Make("yes");
        try
        {
            var hashWith = SandboxManager.ConfigHashForHost(
                mgr.PluginsHostDir, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "img-1",
                hostCliVersions: null, pluginsHostDir: mgr.PluginsHostDir);

            Directory.Delete(mgr.PluginsHostDir, recursive: true);
            Directory.Exists(mgr.PluginsHostDir).Should().BeFalse("каталог плагинов удалён внутри теста");

            var hashWithout = SandboxManager.ConfigHashForHost(
                mgr.PluginsHostDir, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "img-1",
                hostCliVersions: null, pluginsHostDir: mgr.PluginsHostDir);

            hashWith.Should().NotBe(hashWithout,
                "ConfigHash обязан учитывать наличие каталога плагинов — иначе появление пакета не пересоздаст контейнер");
        }
        finally { CleanTmp(tmp); }
    }
}
