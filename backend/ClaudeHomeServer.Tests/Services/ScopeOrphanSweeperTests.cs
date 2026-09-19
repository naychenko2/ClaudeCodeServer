using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож scope-сирот: гашение scope висит на событии Exited и умирает вместе с процессом
// бэкенда — упавший бэкенд оставляет в slice узлы MSBuild и VBCSCompiler, которых не видит
// ни --collect, ни ProcessRegistry. Сторож разбирает их при следующем старте.
// Настоящий systemd тестам не нужен: перечисление юнитов, «жив ли владелец» и остановка —
// швы. Коллекция процесс-глобального состояния: тест правит IsolationOptions.Instance и швы.
[Collection(TestCollections.ProcessGlobalState)]
public class ScopeOrphanSweeperTests
{
    private const string Dead = "ccs-run-0000002a-00000000000000000000000000000001.scope";   // pid 42
    private const string Alive = "ccs-run-00000457-00000000000000000000000000000002.scope";  // pid 1111

    [Fact]
    public void Отбор_ГаситТолькоScopeМёртвогоВладельца()
    {
        var orphans = ScopeOrphanSweeper.SelectOrphans([Dead, Alive], pid => pid == 1111);

        orphans.Should().Equal(Dead);
    }

    [Fact]
    public void Отбор_ЧужиеИНепонятныеИменаНеТрогает()
    {
        // run-p12345-… — юнит, который systemd назвал сам (чужой инстанс до этой версии или
        // вовсе посторонний), ccs-run-<guid> — дореализационный формат без отпечатка владельца:
        // владельца в имени нет, значит мёртвости доказать нечем — мимо.
        string[] units =
        [
            "run-p12345-i12345.scope",
            "ccs-run-00000000000000000000000000000003.scope",
            "user@1000.service",
            "",
        ];

        ScopeOrphanSweeper.SelectOrphans(units, _ => false).Should().BeEmpty();
    }

    [Fact]
    public void Отбор_СвойЖивойScopeПереживаетУборку()
    {
        // Имя ровно того вида, что выдаёт боевой запуск этого процесса
        var mine = LocalProcessRunner.NewScopeUnitName();

        ScopeOrphanSweeper.SelectOrphans([mine], pid => pid == Environment.ProcessId)
            .Should().BeEmpty("сторож не гасит scope, заведённый живым бэкендом — своим или соседним");
    }

    [Fact]
    public void Уборка_ГаситНайденныхСиротЧерезОбщийШовОстановки()
    {
        if (OperatingSystem.IsWindows()) return;

        var prevOptions = IsolationOptions.Instance;
        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevAlive = ScopeOrphanSweeper.IsProcessAlive;
        var prevStop = LocalProcessRunner.StopScope;
        var stopped = new List<string>();
        // SystemdRunPath задан явно — поиск по PATH в тесте не нужен
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, SystemdRunPath = "/usr/bin/systemd-run" };
        ScopeOrphanSweeper.ListScopes = _ => [Dead, Alive];
        ScopeOrphanSweeper.IsProcessAlive = pid => pid == 1111;
        LocalProcessRunner.StopScope = (_, unit) => { lock (stopped) stopped.Add(unit); };
        try
        {
            ScopeOrphanSweeper.Sweep(IsolationOptions.Instance);

            lock (stopped) stopped.Should().Equal(Dead);
        }
        finally
        {
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.IsProcessAlive = prevAlive;
            ScopeOrphanSweeper.ListScopes = prevList;
            IsolationOptions.Instance = prevOptions;
        }
    }

    [Fact]
    public void Уборка_ИзоляцияВыключена_НичегоНеПеречисляетИНеГасит()
    {
        var prevOptions = IsolationOptions.Instance;
        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevStop = LocalProcessRunner.StopScope;
        var listed = 0;
        var stops = 0;
        IsolationOptions.Instance = new IsolationOptions { Enabled = false };
        ScopeOrphanSweeper.ListScopes = _ => { Interlocked.Increment(ref listed); return []; };
        LocalProcessRunner.StopScope = (_, _) => Interlocked.Increment(ref stops);
        try
        {
            ScopeOrphanSweeper.Sweep(IsolationOptions.Instance);

            listed.Should().Be(0);
            stops.Should().Be(0);
        }
        finally
        {
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.ListScopes = prevList;
            IsolationOptions.Instance = prevOptions;
        }
    }

    [Fact]
    public void Уборка_ПеречислениеУпало_НеБросаетИНеГасит()
    {
        if (OperatingSystem.IsWindows()) return;

        var prevOptions = IsolationOptions.Instance;
        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevStop = LocalProcessRunner.StopScope;
        var stops = 0;
        IsolationOptions.Instance = new IsolationOptions { Enabled = true, SystemdRunPath = "/usr/bin/systemd-run" };
        ScopeOrphanSweeper.ListScopes = _ => throw new TimeoutException("шина молчит");
        LocalProcessRunner.StopScope = (_, _) => Interlocked.Increment(ref stops);
        try
        {
            var act = () => ScopeOrphanSweeper.Sweep(IsolationOptions.Instance);

            act.Should().NotThrow("уборка сирот fail-open: сбой не должен ронять старт бэкенда");
            stops.Should().Be(0);
        }
        finally
        {
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.ListScopes = prevList;
            IsolationOptions.Instance = prevOptions;
        }
    }
}
