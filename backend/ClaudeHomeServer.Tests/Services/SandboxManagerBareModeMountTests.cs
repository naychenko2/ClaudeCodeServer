using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ container-фиксов BareMode в SandboxManager: bind-mount каталога SystemPrompts
// (чтобы файл карты был виден в /app/SystemPrompts) и его учёт в ConfigHash (иначе смена
// карты не пересоздаст контейнер). Оба пункта добавлены в ревью 2026-09-05 без тестов;
// здесь — тесты через internal pure-функции BuildRunArgsForHost / ConfigHashForHost,
// которые принимают systemPromptsHost параметром.
//
// Раньше тесты гоняли приватные методы через рефлексию и переименовывали общий
// bin/SystemPrompts через Directory.Move (BuildRunArgs/ConfigHash брали путь от
// AppContext.BaseDirectory). Окно между Move и finally при прерывании оставляло
// SystemPrompts.bak_<guid>, и все последующие --no-build прогоны краснели до ручного
// восстановления — блокер CI по H-3 ревью 2026-09-05. Тесты теперь работают в своих
// временных каталогах (CreateDirectory/Delete в finally), build output не трогают.
//
// Мутация удаления блока bind-mount (`if (systemPromptsExists) args.AddRange(["-v", ...])`)
// → тест BuildRunArgs_SystemPromptsСуществует_ДобавляетсяBindMount красный.
// Мутация снятия systemPromptsHost из ConfigHash → тест ConfigHash_РазличаетНаличиеКаталога красный.
public class SandboxManagerBareModeMountTests
{
    // Локальная подделка под bin/SystemPrompts: создаётся/удаляется в try/finally теста.
    // Это РАЗРЕШЕНО потому, что каталог — собственный теста, не общий build output.
    private static (SandboxManager Manager, string SystemPromptsHost) Make(string? createSystemPrompts = "yes")
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sbx_baremode_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(tmp, "ClaudeSandbox"),
        }).Build();
        var mgr = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        // Хост кладём в tmp, а не в AppContext.BaseDirectory — чтобы тесты не трогали
        // build output (см. комментарий выше). Внутри tmp это дочерний каталог, его
        // судьба — за тестом: создал при createSystemPrompts="yes", не создал при "no",
        // удалил весь tmp в finally.
        var host = Path.Combine(tmp, "SystemPrompts");
        if (createSystemPrompts == "yes")
            Directory.CreateDirectory(host);
        return (mgr, host);
    }

    private static string CleanTmp(string tmp)
    {
        try { Directory.Delete(tmp, recursive: true); } catch { /* каталог мог уже не быть */ }
        return tmp;
    }

    [Fact]
    public void BuildRunArgs_SystemPromptsСуществует_ДобавляетсяBindMount()
    {
        var (mgr, host) = Make("yes");
        try
        {
            var argsLine = string.Join(' ', SandboxManager.BuildRunArgsForHost(
                host, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "test-hash")
                .Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

            argsLine.Should().Contain(SandboxManager.SystemPromptsMount,
                "при наличии каталога SystemPrompts рядом с бэкендом bind-mount в args обязан быть — " +
                "иначе внутри контейнера файл карты не виден, ToRuntime в DockerPathMapper бросает, " +
                "второй catch в ClaudeSession.BuildArgs снимает BareMode тихо");
            argsLine.Should().Contain("/projects");
            argsLine.Should().Contain("/sandbox-profiles");
            argsLine.Should().Contain("/turn-tmp");
        }
        finally { CleanTmp(Path.GetDirectoryName(host)!); }
    }

    [Fact]
    public void BuildRunArgs_SystemPromptsОтсутствует_BindMountПодавлен()
    {
        // Каталог не создаём — bind-mount должен подавиться (иначе контейнер тащит
        // пустой /app/SystemPrompts).
        var (mgr, host) = Make("no");
        try
        {
            var argsLine = string.Join(' ', SandboxManager.BuildRunArgsForHost(
                host, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "test-hash")
                .Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));

            argsLine.Should().Contain("/projects");
            argsLine.Should().Contain("/sandbox-profiles");
            argsLine.Should().Contain("/turn-tmp");
            argsLine.Should().NotContain(SandboxManager.SystemPromptsMount,
                "при отсутствии каталога bind-mount подавляется — иначе контейнер тащит пустой /app/SystemPrompts");
        }
        finally { CleanTmp(Path.GetDirectoryName(host)!); }
    }

    [Fact]
    public void ConfigHash_РазличаетНаличиеКаталога()
    {
        // Хеш РАЗЛИЧАЕТ состояния «каталог есть» и «каталога нет» (мутация снятия
        // `systemPromptsExists ? systemPromptsHost : ""` из payload сделает хеш
        // одинаковым с обеими ветками → RED). Хеш учитывает ТОЛЬКО факт наличия каталога,
        // не его содержимое — изменение CLAUDE-local.md без пересоздания каталога
        // хеш не двигает.
        var (mgr, host) = Make("yes");
        try
        {
            var hashWith = SandboxManager.ConfigHashForHost(
                host, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "img-1");

            // Удаляем каталог внутри tmp (НЕ в build output — см. комментарий класса).
            Directory.Delete(host, recursive: true);
            Directory.Exists(host).Should().BeFalse("каталог удалён внутри теста");

            var hashWithout = SandboxManager.ConfigHashForHost(
                host, mgr.Options, mgr.ProfilesHostDir, mgr.TmpHostDir, "img-1");

            hashWith.Should().NotBe(hashWithout,
                "ConfigHash обязан учитывать наличие SystemPrompts — иначе смена карты не пересоздаст контейнер");
        }
        finally { CleanTmp(Path.GetDirectoryName(host)!); }
    }
}