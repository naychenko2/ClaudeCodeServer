using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Execution;

// Версия claude CLI хоста по `claude --version` — одна точка на процесс. Нужна и User-Agent'у
// опроса usage (SubscriptionOAuthUsageService), и требуемой версии CLI на устройствах
// (DeviceHarnessPolicy: устройства идут за хостом). Опрашивается один раз за жизнь процесса:
// обновление CLI на хосте подхватывается рестартом бэкенда, как и остальное окружение.
public static class ClaudeCliVersion
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly Lazy<Task<string?>> Probe = new(() => ProbeAsync());

    // null — CLI не найден, не ответил за таймаут или ответил без номера версии.
    public static Task<string?> GetAsync() => Probe.Value;

    // «2.1.283 (Claude Code)» → «2.1.283»; без номера — null.
    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = Version.Match(output);
        return m.Success ? m.Value : null;
    }

    private static async Task<string?> ProbeAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(ClaudeCliLocator.FindClaudeExecutable())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = new CancellationTokenSource(ProbeTimeout);
            _ = p.StandardError.ReadToEndAsync(cts.Token);
            var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return Parse(output);
        }
        catch { return null; }
    }

    // Не [GeneratedRegex]: генератор кладёт типы в System.Text.RegularExpressions.Generated,
    // а в Core.dll разрешены только свои неймспейсы (SubsystemBoundaryTests)
    private static readonly Regex Version = new(@"\d+\.\d+\.\d+", RegexOptions.CultureInvariant);
}

// Версия CLI хоста для синхронных потребителей (политика устройств читается на Hello агента).
// Отдельный шов ради тестов: политика без него ведёт себя как раньше — версия только из конфига.
public interface IHostCliVersion
{
    string? Current { get; }
}

public sealed class HostCliVersion : IHostCliVersion
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

    // Опрос стартует при создании синглтона, а не на первом Hello: к приходу агента ответ
    // обычно уже готов, и чтение не ждёт процесс.
    private readonly Task<string?> _probe = ClaudeCliVersion.GetAsync();

    public string? Current
    {
        get
        {
            try { return _probe.Wait(WaitLimit) ? _probe.Result : null; }
            catch { return null; }
        }
    }
}
