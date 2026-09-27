using System.Diagnostics;

namespace ClaudeHomeServer.Services.Execution;

// Результат пробы systemd-run: Ok — обёртка разрешена; иначе FailReason —
// причина fail-open (ненулевой код, таймаут, исключение) — для единственного warning.
public sealed record SystemdRunProbeResult(bool Ok, string? FailReason = null);

// Шов, который исполняет пробу systemd-run: по умолчанию реальный запуск команды с
// таймаутом, в тестах — стаб, чтобы тесты не зависели от настоящего systemd.
// systemdRunPath — уже разрешённый путь; wrapperArgs — флаги до `--` (те же, что
// LocalProcessRunner.WrapperArgs); command — пробная команда (в проде `true`).
public delegate SystemdRunProbeResult SystemdRunProbe(
    string systemdRunPath,
    IEnumerable<string> wrapperArgs,
    string command,
    int timeoutMs);
