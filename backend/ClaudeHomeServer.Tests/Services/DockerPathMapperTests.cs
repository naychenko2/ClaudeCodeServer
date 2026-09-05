using System.Reflection;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Маппинг путей хост ↔ sandbox-контейнер: проекты песочницы, профили, temp.
public class DockerPathMapperTests
{
    private static DockerPathMapper Make()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "sbx_map_" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(tmp, "data", "projects.json"),
            ["Sandbox:ProjectsRoot"] = Path.Combine(tmp, "ClaudeSandbox"),
        }).Build();
        var sandbox = new SandboxManager(config, NullLogger<SandboxManager>.Instance);
        return new DockerPathMapper(sandbox);
    }

    [Fact]
    public void ToRuntime_ПроектВКорнеПесочницы_МапитсяВProjects()
    {
        var m = Make();
        var host = Path.Combine(Path.GetTempPath(), "x"); // заглушка, заменим ниже
        _ = host;
        // Берём корень из самого маппера через round-trip известной точки
        var runtime = m.ToRuntime(RootProjectsHost(m));
        runtime.Should().Be("/projects");

        var sub = m.ToRuntime(Path.Combine(RootProjectsHost(m), "alice", "app"));
        sub.Should().Be("/projects/alice/app");
    }

    [Fact]
    public void ToHost_ОбратныйМаппинг_Симметричен()
    {
        var m = Make();
        var host = Path.Combine(RootProjectsHost(m), "bob", "proj");
        var runtime = m.ToRuntime(host);
        m.ToHost(runtime).Should().Be(host);
    }

    [Fact]
    public void ToRuntime_ПутьВнеПесочницы_Бросает()
    {
        var m = Make();
        var act = () => m.ToRuntime(@"C:\Windows\System32\secret.txt");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CanMap_ХостовойПроект_True_Посторонний_False()
    {
        var m = Make();
        m.CanMap(Path.Combine(RootProjectsHost(m), "any")).Should().BeTrue();
        m.CanMap(@"C:\Windows").Should().BeFalse();
    }

    [Fact]
    public void CanMap_ПрефиксСоседнейПапки_False()
    {
        // Правило для «…/ClaudeSandbox» не должно матчить «…/ClaudeSandboxBackup»:
        // StartsWith без границы сегмента пробивал бы инвариант «путь вне монтирований».
        var m = Make();
        var sibling = RootProjectsHost(m) + "Backup";
        m.CanMap(Path.Combine(sibling, "x")).Should().BeFalse();
    }

    [Fact]
    public void ToRuntime_ПрефиксСоседнейПапки_Бросает()
    {
        var m = Make();
        var sibling = RootProjectsHost(m) + "Backup";
        var act = () => m.ToRuntime(Path.Combine(sibling, "secret.txt"));
        act.Should().Throw<InvalidOperationException>();
    }

    // СТОРОЖ правила SystemPrompts: при наличии каталога SystemPrompts рядом с бэкендом
    // (например, bin/SystemPrompts или репозиторный backend/ClaudeHomeServer/SystemPrompts)
    // DockerPathMapper обязан мочь замапить путь в /app/SystemPrompts. Без правила ToRuntime
    // бросает InvalidOperationException, второй catch в ClaudeSession.BuildArgs снимает
    // BareMode тихо — ход провайдера уходит по полной CLAUDE.md. Сторож: мутация удаления
    // правила `SystemPrompts` → этот тест красный.
    [Fact]
    public void ToRuntime_ФайлКартыBareMode_МапитсяВАппSystemPrompts()
    {
        // Имитируем поставку: tmp/SystemPrompts/CLAUDE-local.md. Базовое правило маппера
        // использует AppContext.BaseDirectory — поэтому создаём фиктивный каталог внутри
        // него. Чтобы тест был переносимым, кладём SystemPrompts в AppContext.BaseDirectory
        // и тут же удаляем (структурно — наличие файла для правила не важно, только путь).
        var baseDir = AppContext.BaseDirectory;
        var sysPrompts = Path.Combine(baseDir, "SystemPrompts");
        var existed = Directory.Exists(sysPrompts);
        if (!existed) Directory.CreateDirectory(sysPrompts);
        try
        {
            // Через round-trip достаём правило SystemPrompts из самого маппера.
            var m = Make();
            var runtimeRoot = m.ToHost("/app/SystemPrompts");
            runtimeRoot.TrimEnd(Path.DirectorySeparatorChar)
                .Should().Be(sysPrompts.TrimEnd(Path.DirectorySeparatorChar),
                    "правило SystemPrompts обязано быть в DockerPathMapper; иначе второй catch " +
                    "в ClaudeSession.BuildArgs будет ловить ToRuntime на каждом container-владельце");
        }
        finally
        {
            if (!existed) Directory.Delete(sysPrompts);
        }
    }

    [Fact]
    public void CanMap_ПутьКSystemPrompts_True()
    {
        // Защита от Medium-2 регрессии: правило SystemPrompts существует — CanMap для
        // пути внутри него возвращает true (а не false). Без правила CanMap вернул бы
        // false и optional-пути (--add-dir на SystemPrompts) молча отбрасывались.
        var baseDir = AppContext.BaseDirectory;
        var sysPrompts = Path.Combine(baseDir, "SystemPrompts");
        var existed = Directory.Exists(sysPrompts);
        if (!existed) Directory.CreateDirectory(sysPrompts);
        try
        {
            var m = Make();
            m.CanMap(Path.Combine(sysPrompts, "CLAUDE-local.md")).Should().BeTrue(
                "правило SystemPrompts обязано быть в DockerPathMapper; без него CanMap вернёт false");
        }
        finally
        {
            if (!existed) Directory.Delete(sysPrompts);
        }
    }

    // Достаём хостовый корень /projects через ToHost (внутренние правила приватны)
    private static string RootProjectsHost(DockerPathMapper m) => m.ToHost("/projects");

    // СТОРОЖ отсутствия дубля правила SystemPrompts: на /app/SystemPrompts должно быть
    // РОВНО одно правило в _rules. Два правила на один runtime-путь ломают round-trip
    // стабильность ToHost (первое всегда побеждает, репо-правило становится мёртвым
    // кодом) — и это БЫЛО: до фикса в маппере висело репо-правило на 3 точки, которое
    // ни ToRuntime, ни CanMap не достигали. Мутация «вернуть репо-правило с 5 точекми»
    // → тест красный: в _rules теперь два правила с тем же runtime.
    [Fact]
    public void Правила_НаSystemPrompts_РовноОдно()
    {
        var m = Make();
        var rules = (List<(string Host, string Runtime)>)typeof(DockerPathMapper)
            .GetField("_rules", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(m)!;
        var systemPromptsRules = rules.Where(r => r.Runtime == "/app/SystemPrompts").ToList();
        systemPromptsRules.Should().HaveCount(1,
            "ровно одно правило для /app/SystemPrompts — два делают ToHost не round-trip-стабильным");
        // Хостовый путь должен заканчиваться на bin/.../SystemPrompts — это
        // AppContext.BaseDirectory + "SystemPrompts". На Windows путь с обратными
        // слэшами, на Linux — с прямыми; тест идёт через EndWithEquivalentOf,
        // чтобы быть платформонезависимым.
        systemPromptsRules[0].Host.Replace('\\', '/').Should().EndWith("bin/Debug/net10.0/SystemPrompts");
    }
}
