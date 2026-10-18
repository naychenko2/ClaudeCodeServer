using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.RemoteCommands;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.RemoteCommands;

// Пульт удалённых команд: правила состояния (§5 плана), замок операции и отбраковка
// кривых записей конфига. Настоящие процессы не запускаются — шелл подменён фейком.
public class RemoteCommandsServiceTests
{
    private const string StartCmd = "старт";
    private const string StopCmd = "стоп";
    private const string StatusCmd = "статус";

    private static RemoteCommandAction Oneshot(string key = "demo", string? pattern = null) => new()
    {
        Key = key,
        Title = "Демо",
        Mode = "oneshot",
        Start = StartCmd,
        Stop = StopCmd,
        Status = StatusCmd,
        StatusRunningPattern = pattern,
        TimeoutSeconds = 5,
    };

    private static RemoteCommandsService Build(FakeShellCommandRunner runner, bool enabled = true,
        params RemoteCommandAction[] actions) => Build(runner, NullLogger<RemoteCommandsService>.Instance, enabled, actions);

    private static RemoteCommandsService Build(FakeShellCommandRunner runner, ILogger<RemoteCommandsService> log,
        bool enabled, params RemoteCommandAction[] actions)
    {
        var options = Options.Create(new RemoteCommandsOptions { Enabled = enabled, Actions = [.. actions] });
        return new RemoteCommandsService(options, runner, log);
    }

    // ── Валидация конфигурации ───────────────────────────────────────────────────

    [Fact]
    public void Валидация_ОтбрасываетКривыеЗаписиИЖивётНаОставшихся()
    {
        var good = Oneshot();
        var duplicate = Oneshot();                              // дубль Key
        var badKey = Oneshot("Демо Ключ");                      // slug не тот
        var noStatus = Oneshot("no-status"); noStatus.Status = null;   // oneshot без Status
        var noStart = Oneshot("no-start"); noStart.Start = "";         // нет команды запуска
        var badMode = Oneshot("bad-mode"); badMode.Mode = "oneshoot";  // опечатка в режиме

        var service = Build(new FakeShellCommandRunner(), true, good, duplicate, badKey, noStatus, noStart, badMode);

        service.Enabled.Should().BeTrue();
        service.List().Select(a => a.Key).Should().Equal("demo");
    }

    // Url — ссылка кнопки «Открыть», не команда: обязана доехать до List как есть,
    // а кривая схема — скрыться, не топя запись.
    [Fact]
    public void Url_ЕдетВСписок_КриваяСхемаСкрываетсяНеТопяЗапись()
    {
        var good = Oneshot("with-url"); good.Url = "https://vscode.dev/tunnel/grisha-home";
        var badScheme = Oneshot("bad-url"); badScheme.Url = "javascript:alert(1)";
        var notUrl = Oneshot("not-url"); notUrl.Url = "просто текст";

        var service = Build(new FakeShellCommandRunner(), true, good, badScheme, notUrl);

        var list = service.List();
        list.Select(a => a.Key).Should().Equal("with-url", "bad-url", "not-url");
        list[0].Url.Should().Be("https://vscode.dev/tunnel/grisha-home");
        list[1].Url.Should().BeNull();
        list[2].Url.Should().BeNull();
    }

    [Fact]
    public void ПустойСписокДействий_ФичаСчитаетсяВыключенной()
    {
        var service = Build(new FakeShellCommandRunner());

        service.Enabled.Should().BeFalse();
        service.List().Should().BeEmpty();
    }

    [Fact]
    public async Task РубильникВыключен_ОперацииОтвечаютКакНеизвестныйКлюч()
    {
        var service = Build(new FakeShellCommandRunner(), enabled: false, Oneshot());

        service.Enabled.Should().BeFalse();
        (await service.StartAsync("demo", "tester")).Should().BeNull();
        (await service.StopAsync("demo", "tester")).Should().BeNull();
        (await service.RefreshAsync("demo")).Should().BeNull();
        service.Output("demo").Should().BeNull();
    }

    [Fact]
    public async Task НеизвестныйКлюч_Null()
    {
        var service = Build(new FakeShellCommandRunner(), true, Oneshot());

        (await service.StartAsync("нет-такого", "tester")).Should().BeNull();
        (await service.RefreshAsync("нет-такого")).Should().BeNull();
    }

    // ── Классификация состояния (§5) ─────────────────────────────────────────────

    [Fact]
    public async Task Refresh_Exit0СовпалПаттерн_Running()
    {
        var runner = new FakeShellCommandRunner
        {
            Handler = _ => Task.FromResult(ShellRunResult.Exited(0, "{\"tunnel\":\"Connected\"}")),
        };
        var service = Build(runner, true, Oneshot(pattern: "\"tunnel\":\"Connected\""));

        var result = await service.RefreshAsync("demo");

        result!.State.Should().Be("running");
        service.List()[0].LastExitCode.Should().Be(0);
        service.List()[0].CheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_Exit0НоПаттернНеСовпал_Stopped()
    {
        // Ровно случай `code tunnel status`: exit 0 при отсутствующем туннеле
        var runner = new FakeShellCommandRunner
        {
            Handler = _ => Task.FromResult(ShellRunResult.Exited(0, "{\"tunnel\":null}")),
        };
        var service = Build(runner, true, Oneshot(pattern: "\"tunnel\":\"Connected\""));

        (await service.RefreshAsync("demo"))!.State.Should().Be("stopped");
    }

    [Fact]
    public async Task Refresh_ExitНеНоль_Stopped()
    {
        var runner = new FakeShellCommandRunner { Handler = _ => Task.FromResult(ShellRunResult.Exited(1, "")) };
        var service = Build(runner, true, Oneshot());

        var result = await service.RefreshAsync("demo");

        result!.State.Should().Be("stopped");
        service.List()[0].LastExitCode.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_Таймаут_UnknownСПричиной()
    {
        var runner = new FakeShellCommandRunner { Handler = _ => Task.FromResult(ShellRunResult.TimedOut()) };
        var service = Build(runner, true, Oneshot());

        var result = await service.RefreshAsync("demo");

        result!.State.Should().Be("unknown", "«проверить не удалось» — это не «остановлено»");
        result.Detail.Should().NotBeNullOrWhiteSpace();
        service.List()[0].LastExitCode.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_ЗапускНеСостоялся_UnknownСПричиной()
    {
        var runner = new FakeShellCommandRunner { Handler = _ => Task.FromResult(ShellRunResult.Failed("нет такого шелла")) };
        var service = Build(runner, true, Oneshot());

        var result = await service.RefreshAsync("demo");

        result!.State.Should().Be("unknown");
        result.Detail.Should().Contain("нет такого шелла");
    }

    // ── Операции ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_ЗапускаетКомандуИСамПроверяетСостояние()
    {
        var runner = new FakeShellCommandRunner
        {
            Handler = cmd => Task.FromResult(ShellRunResult.Exited(0, cmd == StatusCmd ? "RUNNING" : "")),
        };
        var service = Build(runner, true, Oneshot(pattern: "RUNNING"));

        var result = await service.StartAsync("demo", "tester");

        result!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
        result.State.Should().Be("running");
        runner.Ran.Should().Equal(StartCmd, StatusCmd);
    }

    [Fact]
    public async Task Start_КомандаУпала_ОтказСХвостомВывода()
    {
        var runner = new FakeShellCommandRunner
        {
            Handler = cmd => Task.FromResult(cmd == StartCmd
                ? ShellRunResult.Exited(5, "Отказано в доступе")
                : ShellRunResult.Exited(1, "")),
        };
        var service = Build(runner, true, Oneshot());

        var result = await service.StartAsync("demo", "tester");

        result!.Status.Should().Be(RemoteCommandOperationStatus.Failed);
        result.State.Should().Be("stopped");
        result.Detail.Should().Contain("Отказано в доступе");
    }

    [Fact]
    public async Task Stop_БезОбъявленнойКомандыОстановки_ЧестныйОтказ()
    {
        var action = Oneshot();
        action.Stop = null;
        var service = Build(new FakeShellCommandRunner(), true, action);

        var result = await service.StopAsync("demo", "tester");

        result!.Status.Should().Be(RemoteCommandOperationStatus.Failed);
        result.Detail.Should().Contain("не объявлена");
    }

    [Fact]
    public async Task ВтораяОперацияПоТомуЖеДействию_Конфликт_АRefreshОтдаётКэшБезИсполнения()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeShellCommandRunner
        {
            Handler = async cmd =>
            {
                if (cmd != StartCmd) return ShellRunResult.Exited(0, "");
                entered.TrySetResult();
                await release.Task; // висим, пока тест не отпустит — без Task.Delay
                return ShellRunResult.Exited(0, "");
            },
        };
        var service = Build(runner, true, Oneshot());

        var first = service.StartAsync("demo", "tester");
        await entered.Task; // операция точно взяла замок

        var second = await service.StartAsync("demo", "другой");
        second!.Status.Should().Be(RemoteCommandOperationStatus.Busy);
        second.Busy.Should().BeTrue();

        var refresh = await service.RefreshAsync("demo");
        refresh!.Busy.Should().BeTrue("refresh замок не берёт и конфликтом не отвечает");
        runner.Ran.Should().Equal([StartCmd], "во время операции refresh не исполняет команд");

        release.TrySetResult();
        (await first)!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
    }

    // ── Режим daemon ─────────────────────────────────────────────────────────────

    private static RemoteCommandAction Daemon(string key = "daemon")
    {
        var action = Oneshot(key);
        action.Mode = "daemon";
        action.Status = null; // у daemon Status опционален
        action.Stop = null;
        return action;
    }

    [Fact]
    public async Task Start_Daemon_ПроцессПережилПаузу_Running()
    {
        var child = new FakeShellProcess();
        var runner = new FakeShellCommandRunner { SpawnHandler = _ => new ShellSpawnResult(child, null) };
        var service = Build(runner, true, Daemon());
        service.DaemonGraceMs = 50;

        var result = await service.StartAsync("daemon", "tester");

        result!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
        result.State.Should().Be("running", "свой живой процесс — честный положительный сигнал");
    }

    [Fact]
    public async Task Start_Daemon_ПроцессУмерСразу_ОтказИUnknown()
    {
        var child = new FakeShellProcess();
        child.Exit(); // умер, не дожив до конца grace-паузы
        var runner = new FakeShellCommandRunner { SpawnHandler = _ => new ShellSpawnResult(child, null) };
        var service = Build(runner, true, Daemon());
        service.DaemonGraceMs = 50;

        var result = await service.StartAsync("daemon", "tester");

        result!.Status.Should().Be(RemoteCommandOperationStatus.Failed);
        result.State.Should().Be("unknown", "своего процесса нет, а команды проверки не объявлено");
        result.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Stop_Daemon_БезКомандыОстановки_УбиваетДеревоСвоегоПроцесса()
    {
        var child = new FakeShellProcess();
        var runner = new FakeShellCommandRunner { SpawnHandler = _ => new ShellSpawnResult(child, null) };
        var service = Build(runner, true, Daemon());
        service.DaemonGraceMs = 50;
        await service.StartAsync("daemon", "tester");

        var result = await service.StopAsync("daemon", "tester");

        child.Killed.Should().BeTrue();
        result!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
    }

    [Fact]
    public async Task ОстановкаСервера_ГаситСвоихДетей()
    {
        var child = new FakeShellProcess();
        var runner = new FakeShellCommandRunner { SpawnHandler = _ => new ShellSpawnResult(child, null) };
        var service = Build(runner, true, Daemon());
        service.DaemonGraceMs = 50;
        await service.StartAsync("daemon", "tester");

        await ((IHostedService)service).StopAsync(CancellationToken.None);

        child.Killed.Should().BeTrue("иначе сирота появлялся бы при каждом штатном рестарте");
    }

    // ── Блокер 1: обрыв соединения не стирает аудит ──────────────────────────────

    [Fact]
    public async Task Операция_ВылетелаИсключением_АудитВсёРавноЗаписан()
    {
        // Команда на машине к этому моменту могла уже отработать: запись «кто нажал, чем
        // кончилось» обязана быть даже тогда, когда операция сорвалась на полпути.
        var log = new CapturingLogger();
        var runner = new FakeShellCommandRunner
        {
            Handler = _ => throw new OperationCanceledException("соединение оборвано"),
        };
        var service = Build(runner, log, true, Oneshot());

        var act = () => service.StartAsync("demo", "tester");

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Lines.Should().ContainSingle(l => l.Contains("Пульт: запуск действия demo пользователем tester")
            && l.Contains("отказ"));
    }

    // ── Блокер 2: повторный старт daemon при живом внешнем процессе ──────────────

    [Fact]
    public async Task Start_Daemon_ПроцессУжеЖивПоStatus_ВторойНеСпавнится()
    {
        // Демон поднят прошлой жизнью CCS или руками: своего ребёнка у сервера нет, но
        // Status честно говорит «запущен» — спавн поднял бы ВТОРОЙ экземпляр.
        var action = Daemon();
        action.Status = StatusCmd;
        action.StatusRunningPattern = "RUNNING";
        var spawned = 0;
        var runner = new FakeShellCommandRunner
        {
            Handler = _ => Task.FromResult(ShellRunResult.Exited(0, "RUNNING")),
            SpawnHandler = _ => { spawned++; return new ShellSpawnResult(new FakeShellProcess(), null); },
        };
        var service = Build(runner, true, action);
        service.DaemonGraceMs = 50;

        var result = await service.StartAsync("daemon", "tester");

        spawned.Should().Be(0, "внешний живой процесс — уже запущено, второго не заводим");
        runner.Ran.Should().NotContain(StartCmd);
        result!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
        result.State.Should().Be("running");
    }

    [Fact]
    public async Task Start_Daemon_StatusГоворитОстановлен_ПроцессВсёЖеСпавнится()
    {
        var action = Daemon();
        action.Status = StatusCmd;
        action.StatusRunningPattern = "RUNNING";
        var child = new FakeShellProcess();
        var runner = new FakeShellCommandRunner
        {
            Handler = _ => Task.FromResult(ShellRunResult.Exited(1, "")),
            SpawnHandler = _ => new ShellSpawnResult(child, null),
        };
        var service = Build(runner, true, action);
        service.DaemonGraceMs = 50;

        var result = await service.StartAsync("daemon", "tester");

        runner.Ran.Should().Contain(StartCmd, "раз процесса нет — запускаем");
        result!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
    }

    // ── Проба состояния сериализована сама с собой ───────────────────────────────

    [Fact]
    public async Task ПараллельныйRefresh_ВторойПолучаетКэшБезВторойПробы()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeShellCommandRunner
        {
            Handler = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return ShellRunResult.Exited(0, "");
            },
        };
        var service = Build(runner, true, Oneshot());

        var first = service.RefreshAsync("demo");
        await entered.Task;

        var second = await service.RefreshAsync("demo");

        second!.Busy.Should().BeTrue("проба уже в полёте — отдаём кэш, а не второй процесс");
        runner.Ran.Should().Equal([StatusCmd], "параллельные «Обновить» не копят процессы проверки");

        release.TrySetResult();
        (await first)!.Busy.Should().BeFalse();
    }

    // ── Контракт ответа и правила разбора вывода ─────────────────────────────────

    [Fact]
    public async Task Refresh_ВОтветЕдутВремяПроверкиИКодВозврата()
    {
        var runner = new FakeShellCommandRunner { Handler = _ => Task.FromResult(ShellRunResult.Exited(3, "")) };
        var service = Build(runner, true, Oneshot());

        var result = await service.RefreshAsync("demo");

        result!.CheckedAt.Should().NotBeNull("иначе карточка пишет «ещё не проверялось» под свежим бейджем");
        result.LastExitCode.Should().Be(3);
    }

    [Fact]
    public async Task Refresh_ПаттернТолькоВStderr_НеСчитаетсяЗапущенным()
    {
        var runner = new FakeShellCommandRunner
        {
            // Полный вывод содержит паттерн, но в stdout его нет — он пришёл из stderr
            Handler = _ => Task.FromResult(ShellRunResult.Exited(0, "warning: RUNNING", stdout: "warning: ")),
        };
        var service = Build(runner, true, Oneshot(pattern: "RUNNING"));

        (await service.RefreshAsync("demo"))!.State.Should().Be("stopped");
    }

    [Fact]
    public async Task Stop_КомандаРугнуласьНоСлужбаОстановлена_Успех()
    {
        // `sc stop` у уже остановленной службы отвечает 1062, `taskkill` — 128: красная
        // плашка поверх честного «Остановлен» была бы враньём.
        var runner = new FakeShellCommandRunner
        {
            Handler = cmd => Task.FromResult(cmd == StopCmd
                ? ShellRunResult.Exited(1062, "Служба не запущена.")
                : ShellRunResult.Exited(1, "")),
        };
        var service = Build(runner, true, Oneshot());

        var result = await service.StopAsync("demo", "tester");

        result!.Status.Should().Be(RemoteCommandOperationStatus.Ok);
        result.State.Should().Be("stopped");
        result.Detail.Should().BeNull();
    }

    // Логгер, запоминающий отформатированные строки: сторож аудита операций
    private sealed class CapturingLogger : ILogger<RemoteCommandsService>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }
}
