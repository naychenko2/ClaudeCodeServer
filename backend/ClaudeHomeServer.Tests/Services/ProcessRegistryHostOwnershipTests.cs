using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Реестр процессов ПРОЦЕСС-ГЛОБАЛЕН (статика), а тестовых хостов в одном процессе ОС
// десятки, и они поднимаются-гасятся параллельно с другими классами тестов. Пока
// ApplicationStopping звал KillAll без оговорки, каждый Dispose тестового хоста вычищал
// реестр и убивал ВСЕ учтённые процессы — включая чужие, запущенные соседним тестом.
// Так плавающе падал LocalProcessRunnerIsolationTests.Start_Linux_РеестрУдерживаетОбёрнутыйПроцесс
// (только в связке: в одиночку хостов рядом нет), а под ударом был и сам процесс тестов —
// ProcessRegistryTests ставит на учёт Process.GetCurrentProcess().
//
// Инвариант: хост, который реестром не владеет (Execution:ProcessRegistry:Enabled=false из
// appsettings.Testing.json), при остановке его не трогает.
public class ProcessRegistryHostOwnershipTests
{
    [Fact]
    public async Task ОстановкаТестовогоХоста_НеТрогаетЧужиеЗаписиРеестра()
    {
        // Долгоживущий подопытный процесс — через sh: на Windows пропуск, там этот путь
        // и не гоняется (CI — Linux). Свой процесс в роли подопытного не годится: до
        // починки KillAll убил бы сам прогон вместо красного теста.
        if (OperatingSystem.IsWindows()) return;

        using var alien = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh")
        {
            ArgumentList = { "-c", "exec sleep 30" },
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        try
        {
            ProcessRegistry.TrackForTests(
                new ProcessRegistry.TrackedProcess(alien.Id, alien.ProcessName, alien.StartTime));
            ProcessRegistry.IsTracked(alien.Id).Should().BeTrue("запись только что поставлена");

            using (var factory = new TestWebApplicationFactory())
            {
                _ = factory.CreateClient();
            }
            // KillAll уходил бы синхронно в ApplicationStopping, но убийство дерева
            // наблюдается не мгновенно — даём фону шанс проявиться
            await Task.Delay(300);

            ProcessRegistry.IsTracked(alien.Id).Should().BeTrue(
                "тестовый хост реестром не владеет и не имеет права снимать чужие записи");
            alien.HasExited.Should().BeFalse(
                "тестовый хост не имеет права убивать процессы, запущенные параллельным тестом");
        }
        finally
        {
            try { if (!alien.HasExited) alien.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessRegistry.Unregister(alien);
        }
    }
}
