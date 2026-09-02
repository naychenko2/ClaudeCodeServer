using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Watchdog;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Watchdog;

// Тесты раннера poll-команд против дефекта 01.09: powershell-обёртка в poll-команде
// через cmd /c + ArgumentList давала exit 0 с эхом тела команды = ложный fired трёх
// прод-стороже. Причина: ArgumentList экранирует внутренние " как \", cmd этих правил
// не знает. Фикс — сырая строка «/s /c "команда"» (ProcessSpec.RawArguments): cmd с /s
// снимает только внешние кавычки. Путь — как на проде для local-владельца:
// WatchdogCommandRunner → ILauncherFactory → настоящий LocalProcessRunner (fake —
// только фабрика). Два юнита гоняются на любом CI; живой запуск — Windows-only
// (CI — ubuntu: cmd/powershell нет, ранний return).
public class WatchdogCommandRunnerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(),
        "watchdog_runner_" + Guid.NewGuid().ToString("N"));

    // Фабрика на настоящий local-раннер: ForOwner(local-владелец) на проде отдаёт его же
    private sealed class LocalFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => LocalProcessRunner.Instance;
    }

    public WatchdogCommandRunnerTests() => Directory.CreateDirectory(_workDir);

    [Fact]
    public void WindowsCmdArguments_сыраяСтрокаСключомS()
    {
        // /s: cmd снимает ТОЛЬКО внешние кавычки — внутренние должны дойти до команды
        // как есть. Строка обязана оставаться сырой (Arguments), не ArgumentList
        WatchdogCommandRunner.WindowsCmdArguments(@"powershell -NoProfile -Command ""if (1) { exit 0 }""")
            .Should().Be(@"/s /c ""powershell -NoProfile -Command ""if (1) { exit 0 }""""");
    }

    [Fact]
    public void BuildStartInfo_RawArgumentsИдутВArgumentsНеВArgumentList()
    {
        // Суть инцидента: ArgumentList экранировал бы " как \" — без RawArguments строка
        // с вложенными кавычками до целевой команды не доезжает
        var psi = LocalProcessRunner.BuildStartInfo(new ProcessSpec
        {
            FileName = "cmd.exe",
            RawArguments = WatchdogCommandRunner.WindowsCmdArguments(
                @"powershell -NoProfile -Command ""if (Test-Path 'x') { exit 0 } else { exit 1 }"""),
            Args = [],
            RedirectStdin = false,
            Track = false,
        });
        psi.Arguments.Should().Be(
            @"/s /c ""powershell -NoProfile -Command ""if (Test-Path 'x') { exit 0 } else { exit 1 }""""");
        psi.ArgumentList.Should().BeEmpty("сырая строка не должна дублироваться экранированным списком");
    }

    [Fact]
    public async Task RunAsync_windows_powershellОбёрткаНеДаётЛожногоВозбуждения()
    {
        // Живой запуск (Windows-only; на CI ubuntu — ранний return): точная команда трёх
        // ложных срабатываний прода. ДО фикса: exit 0, вывод = тело команды (эхо строкового
        // литерала). ПОСЛЕ: exit 1, вывод пуст; файл появился — exit 0; Кирын cmd-синтаксис
        // (без вложенных кавычек) не сломан
        if (!OperatingSystem.IsWindows()) return;

        var runner = new WatchdogCommandRunner(new LocalFactory());
        var probe = Path.Combine(_workDir, "ready.txt");
        var wrapper = $@"powershell -NoProfile -Command ""if (Test-Path '{probe}') {{ exit 0 }} else {{ exit 1 }}""";

        var pending = await runner.RunAsync("owner", _workDir, wrapper, 15, CancellationToken.None);
        pending.Kind.Should().Be(PollOutcomeKind.ExitCode);
        pending.ExitCode.Should().Be(1, "файла нет — «ещё нет», никакого эха тела команды");
        pending.Output.Should().BeEmpty();

        await File.WriteAllTextAsync(probe, "ok");
        var done = await runner.RunAsync("owner", _workDir, wrapper, 15, CancellationToken.None);
        done.Kind.Should().Be(PollOutcomeKind.ExitCode);
        done.ExitCode.Should().Be(0, "файл появился — сторож честно срабатывает");

        File.Delete(probe);
        var cmdStyle = $@"if exist ""{probe}"" (exit 0) else (exit 1)";
        var shell = await runner.RunAsync("owner", _workDir, cmdStyle, 15, CancellationToken.None);
        shell.ExitCode.Should().Be(1, "cmd-синтаксис (сценарий smoke Киры) не сломан фиксом");
        shell.Output.Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* временный каталог — мусор не критичен */ }
    }
}
