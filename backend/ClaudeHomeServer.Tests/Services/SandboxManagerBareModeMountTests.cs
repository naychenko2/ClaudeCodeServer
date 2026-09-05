using System.Reflection;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// СТОРОЖ container-фиксов BareMode в SandboxManager: bind-mount каталога SystemPrompts
// (чтобы файл карты был виден в /app/SystemPrompts) и его учёт в ConfigHash (иначе смена
// карты не пересоздаст контейнер). Оба пункта добавлены в ревью 2026-09-05 без тестов;
// здесь — тесты через рефлексию, т.к. BuildRunArgs/ConfigHash приватные.
//
// Мутация удаления блока bind-mount (`if (systemPromptsExists) args.AddRange(["-v", ...])`)
// → тест ToRuntime_SystemPromptsBindMount_AddRangeMount красный.
// Мутация снятия systemPromptsHost из ConfigHash → тест ConfigHash_УчитываетНаличиеSystemPrompts красный.
public class SandboxManagerBareModeMountTests
{
    private static (SandboxManager Manager, string TmpDir) Make()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sbx_baremode_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(tmp, "ClaudeSandbox"),
        }).Build();
        return (new SandboxManager(config, NullLogger<SandboxManager>.Instance), tmp);
    }

    private static string InvokeBuildRunArgs(SandboxManager m, string confHash)
    {
        var method = typeof(SandboxManager).GetMethod(
            "BuildRunArgs", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("BuildRunArgs обязан быть в SandboxManager");
        var result = method!.Invoke(m, new object[] { confHash });
        var args = result.Should().BeAssignableTo<IEnumerable<string>>().Subject;
        return string.Join(' ', args.Select(a =>
            a.Contains(' ') ? "\"" + a + "\"" : a));
    }

    private static string InvokeConfigHash(SandboxManager m, string imageId)
    {
        var method = typeof(SandboxManager).GetMethod(
            "ConfigHash", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull("ConfigHash обязан быть в SandboxManager");
        return (string)method!.Invoke(m, new object[] { imageId })!;
    }

    [Fact]
    public void ToRuntime_SystemPromptsBindMount_AddRangeMount_КогдаКаталогЕсть()
    {
        var (m, tmp) = Make();
        var baseDir = AppContext.BaseDirectory;
        var sysPrompts = Path.Combine(baseDir, "SystemPrompts");
        var existed = Directory.Exists(sysPrompts);
        if (!existed) Directory.CreateDirectory(sysPrompts);
        try
        {
            var argsLine = InvokeBuildRunArgs(m, "test-hash");
            argsLine.Should().Contain(SandboxManager.SystemPromptsMount,
                "при наличии каталога SystemPrompts рядом с бэкендом bind-mount в args обязан быть — " +
                "иначе внутри контейнера файл карты не виден, ToRuntime в DockerPathMapper бросает, " +
                "второй catch в ClaudeSession.BuildArgs снимает BareMode тихо");
        }
        finally
        {
            if (!existed) Directory.Delete(sysPrompts);
        }
    }

    [Fact]
    public void ToRuntime_SystemPromptsBindMount_ЕстьКогдаКаталогЕсть()
    {
        // Контракт: при наличии каталога SystemPrompts bind-mount добавляется в args —
        // иначе внутри контейнера файл карты не виден, ToRuntime в DockerPathMapper
        // бросает, второй catch в ClaudeSession.BuildArgs снимает BareMode тихо.
        var (m, tmp) = Make();
        var baseDir = AppContext.BaseDirectory;
        var sysPrompts = Path.Combine(baseDir, "SystemPrompts");
        var existed = Directory.Exists(sysPrompts);
        if (!existed) Directory.CreateDirectory(sysPrompts);
        try
        {
            var argsLine = InvokeBuildRunArgs(m, "test-hash");
            argsLine.Should().Contain("/projects");
            argsLine.Should().Contain("/sandbox-profiles");
            argsLine.Should().Contain("/turn-tmp");
            argsLine.Should().Contain(SandboxManager.SystemPromptsMount);
        }
        finally
        {
            if (!existed) Directory.Delete(sysPrompts);
        }
    }

    [Fact]
    public void ToRuntime_SystemPromptsBindMount_НетКогдаКаталогаНет()
    {
        // Контракт: пустой каталог в контейнере не плодим (SandboxManager.BuildRunArgs
        // подавляет bind-mount при systemPromptsExists=false). AppContext.BaseDirectory
        // нельзя подменить из теста, поэтому временно ПЕРЕИМЕНОВЫВАЕМ bin/SystemPrompts
        // (он всегда есть после билда — csproj копирует CLAUDE-local.md). Перемещение
        // обратимо через try/finally + явная проверка восстановления в финале.
        // Мутация удаления `if (systemPromptsExists)` → RED по любому каналу.
        var (m, _) = Make();
        var baseDir = AppContext.BaseDirectory;
        var sysPrompts = Path.Combine(baseDir, "SystemPrompts");
        Directory.Exists(sysPrompts).Should().BeTrue(
            "тест ожидает, что bin/SystemPrompts создан при сборке (csproj копирует CLAUDE-local.md)");

        var backup = sysPrompts + ".bak_" + Guid.NewGuid().ToString("N");
        Directory.Move(sysPrompts, backup);
        try
        {
            var argsLine = InvokeBuildRunArgs(m, "test-hash");
            argsLine.Should().Contain("/projects");
            argsLine.Should().Contain("/sandbox-profiles");
            argsLine.Should().Contain("/turn-tmp");
            argsLine.Should().NotContain(SandboxManager.SystemPromptsMount,
                "при отсутствии каталога bind-mount подавляется — иначе контейнер тащит пустой /app/SystemPrompts");
        }
        finally
        {
            if (Directory.Exists(backup))
                Directory.Move(backup, sysPrompts);
            // Если восстановление упало — фейлим тест явно, иначе BareMode молча
            // выключится на дев-стенде (csproj копирует CLAUDE-local.md только если
            // каталог существует, а после `dotnet build` он появится снова).
            Directory.Exists(sysPrompts).Should().BeTrue(
                $"тест обязан восстановить {sysPrompts}; иначе следующая сборка не скопирует CLAUDE-local.md");
        }
    }

    [Fact]
    public void ConfigHash_УчитываетНаличиеSystemPrompts()
    {
        // Тест проверяет, что ConfigHash РАЗЛИЧАЕТ состояния «каталог есть» и
        // «каталога нет» (мутация снятия `systemPromptsExists ? systemPromptsHost : ""`
        // из payload сделает хеш одинаковым с обеими ветками → RED). Хеш учитывает
        // ТОЛЬКО факт наличия каталога, не его содержимое — изменение CLAUDE-local.md
        // без пересоздания bin/SystemPrompts хеш не двигает (см. поведение ниже).
        //
        // Тест работает переименованием: каталог в bin/ уже есть после билда
        // (csproj копирует CLAUDE-local.md), удалять его нельзя — он часть билд-вывода.
        // Переименовываем в уникальное имя → ConfigHash видит отсутствие → переименовываем обратно.
        var (m, _) = Make();
        var baseDir = AppContext.BaseDirectory;
        var sysPrompts = Path.Combine(baseDir, "SystemPrompts");
        Directory.Exists(sysPrompts).Should().BeTrue(
            "тест ожидает, что bin/SystemPrompts создан при сборке (csproj копирует CLAUDE-local.md)");

        var hashWith = InvokeConfigHash(m, "img-1");

        var backup = sysPrompts + ".bak_" + Guid.NewGuid().ToString("N");
        Directory.Move(sysPrompts, backup);
        try
        {
            var hashWithout = InvokeConfigHash(m, "img-1");

            hashWith.Should().NotBe(hashWithout,
                "ConfigHash обязан учитывать наличие SystemPrompts — иначе смена карты не пересоздаст контейнер");
        }
        finally
        {
            if (Directory.Exists(backup))
                Directory.Move(backup, sysPrompts);
            Directory.Exists(sysPrompts).Should().BeTrue(
                $"тест обязан восстановить {sysPrompts}; иначе следующая сборка не скопирует CLAUDE-local.md");
        }
    }
}
