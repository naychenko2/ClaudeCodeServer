using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Execution;

// Версия claude CLI хоста по `claude --version` — одна точка на процесс. Нужна и User-Agent'у
// опроса usage (SubscriptionOAuthUsageService), и требуемой версии CLI на устройствах
// (DeviceHarnessPolicy: устройства идут за хостом), и сторожу обновлений CLI
// (ClaudeCliUpdateWatcher). Первый опрос — при первом обращении; дальше версию перечитывает
// RefreshAsync (сторож раз в сутки и раздел «Модели и расход» при открытии), поэтому
// `claude update` на хосте подхватывается без рестарта бэкенда.
public static class ClaudeCliVersion
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly CliVersionCache Cache = new(ProbeAsync);

    // null — CLI не найден, не ответил за таймаут или ответил без номера версии.
    // После первого опроса отдаёт последнюю известную версию без нового процесса.
    public static Task<string?> GetAsync() => Cache.GetAsync();

    // Перечитать версию новым `claude --version`. Пустой ответ известную версию не затирает.
    public static Task<string?> RefreshAsync() => Cache.RefreshAsync();

    // Последняя известная версия без ожидания; null — опроса ещё не было или он не удался.
    public static bool TryGetKnown(out string? version) => Cache.TryGetKnown(out version);

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

// Кэш версии с перечитыванием: экземпляр, а не статика, чтобы тесты создавали свой с
// подменным опросом и не делили состояние (xUnit гоняет классы параллельно).
//   - single-flight: параллельные опросы (сторож + эндпоинт) делят один процесс CLI;
//   - неудачный опрос (null — например, бинарь подменяется посреди `claude update`)
//     последнюю известную версию не затирает.
internal sealed class CliVersionCache(Func<Task<string?>> probe)
{
    private readonly object _gate = new();
    private Task<string?>? _inFlight;
    private Task<string?>? _first;
    private string? _known;
    private bool _probed;

    public Task<string?> GetAsync()
    {
        lock (_gate)
        {
            if (_probed) return Task.FromResult(_known);
            return _first ??= _inFlight ?? StartLocked();
        }
    }

    public Task<string?> RefreshAsync()
    {
        lock (_gate)
            return _inFlight ?? StartLocked();
    }

    public bool TryGetKnown(out string? version)
    {
        lock (_gate)
        {
            version = _known;
            return _probed;
        }
    }

    // Вызывается под _gate: запускает опрос и регистрирует его как текущий
    private Task<string?> StartLocked()
    {
        var task = RunAsync();
        _inFlight = task;
        return task;
    }

    private async Task<string?> RunAsync()
    {
        // Уступаем поток, чтобы StartLocked успел записать _inFlight до завершения опроса
        await Task.Yield();
        string? fresh = null;
        try { fresh = await probe(); }
        catch { /* опрос не должен ронять потребителей — считаем неудачей */ }
        lock (_gate)
        {
            if (fresh is not null) _known = fresh;
            _probed = true;
            _inFlight = null;
            return _known;
        }
    }
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

    // Первый опрос стартует при создании синглтона, а не на первом Hello: к приходу агента
    // ответ обычно уже готов, и чтение не ждёт процесс.
    private readonly Task<string?> _first = ClaudeCliVersion.GetAsync();

    // После первого опроса — последняя известная версия (её обновляет RefreshAsync),
    // до него — ждём первый опрос не дольше WaitLimit.
    public string? Current
    {
        get
        {
            if (ClaudeCliVersion.TryGetKnown(out var known)) return known;
            try { return _first.Wait(WaitLimit) ? _first.Result : null; }
            catch { return null; }
        }
    }
}
