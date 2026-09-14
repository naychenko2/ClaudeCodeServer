using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Watchdog;

// Исход одного poll-запуска сторожа. Три вида — ровно по семантике плана:
// ExitCode — запуск СОСТОЯЛСЯ и завершился (0 = дождались, != 0 = «ещё нет»);
// LaunchFailed — запуск не состоялся вовсе (процесс не стартовал / каталог исчез /
// песочница недоступна); PollTimeout — запуск состоялся, но не уложился в свой
// таймаут и был убит (считается «ещё нет», НЕ сбоем запуска).
public enum PollOutcomeKind
{
    ExitCode,
    LaunchFailed,
    PollTimeout,
}

public sealed record PollOutcome(PollOutcomeKind Kind, int ExitCode = 0,
    string Output = "", string? Failure = null)
{
    public static readonly PollOutcome ExitedZero = new(PollOutcomeKind.ExitCode, 0);
    public static PollOutcome Exited(int code, string output) =>
        new(PollOutcomeKind.ExitCode, code, output);
    public static PollOutcome LaunchFailed(string reason) =>
        new(PollOutcomeKind.LaunchFailed, Failure: reason);
}

/// <summary>
/// Узкий шов запуска poll-команды над ILauncherFactory: сервис цикла не знает о процессах,
/// а юнит-тесты подменяют реализацию fake-раннером (CI Linux — без реальных процессов).
/// </summary>
public interface IWatchdogCommandRunner
{
    Task<PollOutcome> RunAsync(string ownerId, string workDir, string command,
        int timeoutSeconds, CancellationToken ct);
}

// Реальный раннер: запуск через среду исполнения владельца (IProcessLauncher.ForOwner),
// per-poll таймаут с kill — по образцу GitService.RunAsync (короткоживущая утилита:
// Track = false, свой Kill). Оболочка — по платформе ЦЕЛЕВОЙ среды владельца
// (TargetIsWindows; песочница всегда Linux): cmd.exe /c либо bash -lc (логин-шелл —
// PATH профильного окружения, без него py/nvm в песочнице не видны).
public sealed class WatchdogCommandRunner(ILauncherFactory launchers) : IWatchdogCommandRunner
{
    // Сборка cmd-аргументов — чистый шов под юнит-тест: сырая строка «/s /c "команда"».
    // Строка, а не ArgumentList, — суть фикса ложных fired (см. комментарий в RunAsync)
    internal static string WindowsCmdArguments(string command) => $"/s /c \"{command}\"";

    public async Task<PollOutcome> RunAsync(string ownerId, string workDir, string command,
        int timeoutSeconds, CancellationToken ct)
    {
        var launcher = launchers.ForOwner(ownerId);
        var windows = launcher.TargetIsWindows;
        var spec = new ProcessSpec
        {
            FileName = windows ? "cmd.exe" : "bash",
            // cmd: /s /c + СЫРАЯ строка (RawArguments), не ArgumentList. .NET экранирует
            // внутренние " как \", cmd этих правил не знает: poll-команда с вложенными
            // кавычками (powershell -NoProfile -Command "if …") разваливалась — cmd сносил
            // первую внешнюю кавычку, powershell получал литеральные кавычки, исполнял тело
            // как строковый литерал и ЭХНУЛ его в stdout с exit 0 = ложный fired (прод
            // 01.09, сторожа dd1fac4e/8ea8c9cc/3a091224). /s: cmd снимает только внешние
            // кавычки, внутренние доходят до команды как есть. bash (песочница, Linux) —
            // аргументы идут массивом execve без экранирования, ветка прежняя
            RawArguments = windows ? WindowsCmdArguments(command) : null,
            Args = windows ? [] : ["-lc", command],
            WorkingDirectory = workDir,
            RedirectStdin = false,
            // Вывод команды в UTF-8; без явной кодировки .NET читает в системной (OEM/ANSI)
            // и кириллица превращается в кракозябры (как у git — см. GitService.RunAsync)
            StdioEncoding = new UTF8Encoding(false),
            // Метка убиваемости: в песочнице docker-клиент — лишь пайп, настоящий процесс
            // добивается по TurnId внутри контейнера
            TurnId = Guid.NewGuid().ToString("N"),
            // Короткоживущая команда со своим kill по таймауту — реестр процессов не нужен
            Track = false,
        };

        Process proc;
        try { proc = launcher.Start(spec); }
        catch (Exception ex) { return PollOutcome.LaunchFailed(ex.Message); }

        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                // Kill безусловен — и по собственному таймауту, и по внешней отмене (остановка
                // хоста), по образцу GitService.RunAsync: без kill local-процесс оставался бы
                // сиротой, держа пайпы stdout (п.3 ревью). Внешняя отмена пробрасывается
                // наверх (PollOneAsync её ждёт — остановка цикла), свой таймаут — «ещё нет»
                launcher.Kill(proc, spec.TurnId);
                if (ct.IsCancellationRequested) throw;
                return new PollOutcome(PollOutcomeKind.PollTimeout);
            }
            var output = await stdoutTask;
            var stderr = await stderrTask;
            var text = string.Join("\n", new[] { output.TrimEnd(), stderr.TrimEnd() }
                .Where(s => s.Length > 0));
            return PollOutcome.Exited(proc.ExitCode, text);
        }
        finally { proc.Dispose(); }
    }
}
