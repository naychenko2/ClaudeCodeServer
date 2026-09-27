using ClaudeHomeServer.DeviceAgent.Install;

namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>
/// Linux: переменные графической сессии для CLI хода. Unit агента стартует от
/// <c>default.target</c> — раньше, чем рабочий стол импортирует <c>DISPLAY</c> в менеджер
/// <c>systemd --user</c>, а после перевхода значения меняются. Поэтому они читаются на каждом
/// ходу из <c>systemctl --user show-environment</c>; не ответил — остаются значения самого агента.
/// </summary>
internal static class GraphicalSessionEnvironment
{
    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> agent, ICommandRunner runner)
    {
        var result = new Dictionary<string, string>(agent, StringComparer.Ordinal);
        var (code, output) = runner.Run("systemctl", ["--user", "show-environment"]);
        if (code != 0) return result;

        foreach (var line in output.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq];
            var value = line[(eq + 1)..].TrimEnd('\r');
            // Значение со спецсимволами systemd печатает в $'…' — это не адрес экрана, не берём
            if (CliEnvironment.InheritedFromGraphicalSession.Contains(name) && value.Length > 0 && !value.StartsWith("$'", StringComparison.Ordinal))
                result[name] = value;
        }
        return result;
    }
}
