using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Install;

/// <summary>Внешние команды (systemctl, loginctl) — отдельно, чтобы тесты шли без systemd.</summary>
internal interface ICommandRunner
{
    /// <summary>Код выхода и объединённый вывод; команды нет — код -1.</summary>
    (int Code, string Output) Run(string file, IReadOnlyList<string> args);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public (int Code, string Output) Run(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (-1, $"{file} не ответил за 30 с");
            }
            return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
        catch (Win32Exception e)
        {
            return (-1, e.Message);
        }
    }
}

/// <summary>
/// Linux: unit <c>systemd --user</c> с <c>Restart=on-failure</c> на симлинк <c>current</c> (Р6):
/// при переключении версии меняется симлинк, а не unit. <c>--always-on</c> включает
/// <c>loginctl enable-linger</c> — агент поднимается при загрузке без входа. Нет
/// <c>systemd --user</c> (WSL1, контейнер) — автозапуск не прописывается, человек получает
/// команду запуска руками; это не ошибка.
/// </summary>
internal sealed class SystemdUserAutostart(
    AgentLayout layout, ICommandRunner runner, string unitDirectory, IReadOnlyDictionary<string, string> environment) : IAutostart
{
    public const string UnitName = "ai-home-agent.service";

    /// <summary>Переменные, которые определяют каталоги агента: у менеджера systemd они бывают другими, чем в терминале.</summary>
    private static readonly string[] InheritedVariables = ["XDG_CONFIG_HOME", "XDG_DATA_HOME"];

    private bool? _available;

    public string UnitFile => Path.Combine(unitDirectory, UnitName);

    public string Describe => $"unit systemd --user {UnitFile}";

    public static string DefaultUnitDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xc
            ? xc
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "systemd", "user");
    }

    public static IReadOnlyDictionary<string, string> InheritedEnvironment() =>
        InheritedVariables
            .Select(name => (name, value: Environment.GetEnvironmentVariable(name)))
            .Where(x => !string.IsNullOrEmpty(x.value))
            .ToDictionary(x => x.name, x => x.value!);

    public string ExecutablePath => Path.Combine(layout.CurrentLink, SupervisorContract.ExecutableName);

    /// <summary>Текст unit: путь — через симлинк current, переменные каталогов — из окружения установки.</summary>
    public static string RenderUnit(string executable, IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder()
            .AppendLine("[Unit]")
            .AppendLine("Description=AI Home: агент устройства")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=simple")
            .AppendLine($"ExecStart={ExecQuote(executable)} {Autostarts.SupervisorCommand}")
            .AppendLine("Restart=on-failure")
            .AppendLine("RestartSec=5");
        foreach (var (name, value) in environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            text.AppendLine($"Environment={EnvQuote(name + "=" + value)}");
        return text
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=default.target")
            .ToString()
            .Replace("\r\n", "\n");
    }

    public AutostartResult Register(string version, bool alwaysOn)
    {
        if (!IsAvailable()) return new AutostartResult(false, [ManualInstruction()]);

        var unit = RenderUnit(ExecutablePath, environment);
        Directory.CreateDirectory(unitDirectory);
        if (!File.Exists(UnitFile) || File.ReadAllText(UnitFile) != unit) AgentLayout.WriteAtomic(UnitFile, unit);

        var notes = new List<string>();
        Require(Systemctl("daemon-reload"), "daemon-reload");
        Require(Systemctl("enable", UnitName), "enable");
        if (alwaysOn)
        {
            var (code, output) = runner.Run("loginctl", ["enable-linger"]);
            notes.Add(code == 0
                ? "linger включён: агент поднимется при загрузке машины без входа"
                : $"linger не включился ({output}); выполни с правами администратора: sudo loginctl enable-linger {Environment.UserName}");
        }
        return new AutostartResult(true, notes);
    }

    // Unit смотрит на симлинк current, его переставляет AgentLayout
    public void Repoint(string version) { }

    public SupervisorStart StartNow(string version)
    {
        if (!IsAvailable()) return new SupervisorStart(SupervisorStartStatus.Manual, ManualInstruction());
        var (code, output) = Systemctl("restart", UnitName);
        return code == 0
            ? new SupervisorStart(SupervisorStartStatus.Started, $"запущен {UnitName}")
            : new SupervisorStart(SupervisorStartStatus.Deferred,
                $"systemctl --user restart {UnitName} не отработал ({output}); агент запустится при следующем входе");
    }

    public void Unregister()
    {
        if (IsAvailable()) Systemctl("disable", "--now", UnitName);
        if (File.Exists(UnitFile))
        {
            File.Delete(UnitFile);
            if (IsAvailable()) Systemctl("daemon-reload");
        }
    }

    private bool IsAvailable() => _available ??= runner.Run("systemctl", ["--user", "show-environment"]).Code == 0;

    private (int Code, string Output) Systemctl(params string[] args) => runner.Run("systemctl", ["--user", .. args]);

    private static void Require((int Code, string Output) result, string step)
    {
        if (result.Code != 0) throw new InvalidOperationException($"systemctl --user {step} не отработал: {result.Output}");
    }

    private string ManualInstruction() =>
        "systemd --user недоступен (WSL1, контейнер?) — автозапуск не прописан. Запускай агента руками: " +
        $"nohup '{ExecutablePath}' {Autostarts.SupervisorCommand} >/dev/null 2>&1 &";

    // systemd: в ExecStart раскрываются $ и %, внутри кавычек экранируются \ и "
    private static string ExecQuote(string value) =>
        "\"" + NoNewlines(value).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\"";

    private static string EnvQuote(string value) =>
        "\"" + NoNewlines(value).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%") + "\"";

    private static string NoNewlines(string value) =>
        value.IndexOfAny(['\n', '\r']) < 0 ? value : throw new ArgumentException("перевод строки в значении unit");
}
