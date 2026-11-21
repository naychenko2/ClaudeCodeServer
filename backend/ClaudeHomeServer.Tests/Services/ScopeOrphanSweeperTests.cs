using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож scope-сирот: гашение scope висит на событии Exited и умирает вместе с процессом
// бэкенда — упавший бэкенд оставляет в slice узлы MSBuild и VBCSCompiler, которых не видит
// ни --collect, ни ProcessRegistry. Сторож разбирает их при следующем старте.
// Настоящий systemd тестам не нужен: перечисление юнитов, «жив ли владелец» и остановка —
// швы. Коллекция процесс-глобального состояния: тест правит швы ListScopes/IsProcessAlive/StopScope.
// IsolationOptions.Instance тесты не публикуют: Sweep получает опции параметром, а чужие хосты
// (Enabled=false) с их фоновой уборкой из Program.cs выходят сразу, не доходя до заглушек.
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

        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevAlive = ScopeOrphanSweeper.IsProcessAlive;
        var prevStop = LocalProcessRunner.StopScope;
        var stopped = new List<string>();
        // SystemdRunPath задан явно — поиск по PATH в тесте не нужен
        var options = new IsolationOptions { Enabled = true, SystemdRunPath = "/usr/bin/systemd-run" };
        ScopeOrphanSweeper.ListScopes = _ => [Dead, Alive];
        ScopeOrphanSweeper.IsProcessAlive = pid => pid == 1111;
        LocalProcessRunner.StopScope = (_, unit) => { lock (stopped) stopped.Add(unit); };
        try
        {
            ScopeOrphanSweeper.Sweep(options);

            lock (stopped) stopped.Should().Equal(Dead);
        }
        finally
        {
            // Sweep теперь заводит периодический механизм — его состояние (и таймер)
            // тест за собой убирает, иначе чужой прогон ловит фантомный тик
            LocalProcessRunner.ResetDeferredForTests();
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.IsProcessAlive = prevAlive;
            ScopeOrphanSweeper.ListScopes = prevList;
        }
    }

    [Fact]
    public void Уборка_ИзоляцияВыключена_НичегоНеПеречисляетИНеГасит()
    {
        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevStop = LocalProcessRunner.StopScope;
        var listed = 0;
        var stops = 0;
        var options = new IsolationOptions { Enabled = false };
        ScopeOrphanSweeper.ListScopes = _ => { Interlocked.Increment(ref listed); return []; };
        LocalProcessRunner.StopScope = (_, _) => Interlocked.Increment(ref stops);
        try
        {
            ScopeOrphanSweeper.Sweep(options);

            listed.Should().Be(0);
            stops.Should().Be(0);
        }
        finally
        {
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.ListScopes = prevList;
        }
    }

    [Fact]
    public void Уборка_ПеречислениеУпало_НеБросаетИНеГасит()
    {
        if (OperatingSystem.IsWindows()) return;

        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevStop = LocalProcessRunner.StopScope;
        var stops = 0;
        var options = new IsolationOptions { Enabled = true, SystemdRunPath = "/usr/bin/systemd-run" };
        ScopeOrphanSweeper.ListScopes = _ => throw new TimeoutException("шина молчит");
        LocalProcessRunner.StopScope = (_, _) => Interlocked.Increment(ref stops);
        try
        {
            var act = () => ScopeOrphanSweeper.Sweep(options);

            act.Should().NotThrow("уборка сирот fail-open: сбой не должен ронять старт бэкенда");
            stops.Should().Be(0);
        }
        finally
        {
            LocalProcessRunner.ResetDeferredForTests();
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.ListScopes = prevList;
        }
    }

    // Периодический тик (S3): Sweep при старте заводит общий механизм LocalProcessRunner,
    // тик зовёт ту же уборку SelectOrphans/Sweep — с теми же тремя замками отличения своего
    // от чужого: чужое имя формата и живой владелец не остановлены, мёртвый — остановлен.
    // Сбой уборки наружу не бросается.
    [Fact]
    public void ПериодическийТик_ГаситТолькоСиротПоТрёмЗамкам()
    {
        if (OperatingSystem.IsWindows()) return;

        var prevList = ScopeOrphanSweeper.ListScopes;
        var prevAlive = ScopeOrphanSweeper.IsProcessAlive;
        var prevStop = LocalProcessRunner.StopScope;
        var stopped = new List<string>();
        var options = new IsolationOptions { Enabled = true, SystemdRunPath = "/usr/bin/systemd-run" };
        // Три замка на живой выборке: чужой формат — мимо; живой владелец — мимо (в том числе
        // мы сами); мёртвый — гасим. Тик ведёт ту же SelectOrphans, что и старт.
        string[] units =
        [
            "run-p12345-i12345.scope",
            Alive,
            LocalProcessRunner.NewScopeUnitName(),
            Dead,
        ];
        ScopeOrphanSweeper.ListScopes = _ => units;
        ScopeOrphanSweeper.IsProcessAlive = pid => pid != 42;
        LocalProcessRunner.StopScope = (_, unit) => { lock (stopped) stopped.Add(unit); };
        try
        {
            LocalProcessRunner.ResetDeferredForTests();
            // Старт заводит механизм: та же уборка, что была до S3…
            ScopeOrphanSweeper.Sweep(options);
            lock (stopped) stopped.Should().Equal(Dead);
            stopped.Clear();

            // …и тот же тик periodic-механизма: живой сосед и чужое имя снова нетронуты,
            // мёртвый снова гасится; исключения наружу нет. Тик берёт опции из Instance —
            // на время тика публикуем их (IsolationOptionsScope возвращает сравнением).
            using (new IsolationOptionsScope(options))
            {
                var act = () => LocalProcessRunner.RunDeferredTickNow();
                act.Should().NotThrow("сбой периодической уборки наружу не бросается");
            }
            lock (stopped) stopped.Should().Equal(Dead);
        }
        finally
        {
            LocalProcessRunner.ResetDeferredForTests();
            LocalProcessRunner.StopScope = prevStop;
            ScopeOrphanSweeper.IsProcessAlive = prevAlive;
            ScopeOrphanSweeper.ListScopes = prevList;
        }
    }
}
