namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Фоновый прогрев снимка tools/list для Higgsfield: гоняет <see cref="HiggsfieldToolset.RefreshNowAsync"/>
/// каждые <see cref="WarmInterval"/>, чтобы ходы шли из памяти, а не лазили в сеть на
/// каждый <see cref="HiggsfieldToolset.ToolsFor"/>. Гейт: <c>EnsureFresh()==null</c>
/// (интеграция не подключена) — тик молча пропускаем, без WARN на каждый цикл.
///
/// Закрывает замечание Глеба по ревью шага 1: <c>GetAwaiter().GetResult()</c> в горячем
/// пути GetCachedTools при живом прогретом кэше перестаёт быть горячим — блокирующий
/// вызов встречает в кэше свежий снимок и возвращается без сети.
/// </summary>
public sealed class HiggsfieldSnapshotWarmer(
    HiggsfieldToolset toolset,
    ILogger<HiggsfieldSnapshotWarmer> log) : IHostedService, IDisposable
{
    /// <summary>Интервал между прогонами. 20 мин — запас под 30-минутный TTL кэша.</summary>
    private static readonly TimeSpan WarmInterval = TimeSpan.FromMinutes(20);

    /// <summary>Пауза до первого тика после StartAsync, чтобы старт инстанса не блокировался сетью.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    private Timer? _timer;
    private CancellationTokenSource? _cts;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // StartAsync не должен бросать: если старт упадёт, контейнер не поднимется.
        // Ошибки прогона уже проглатываются внутри RefreshNowAsync и через WARN.
        _cts = new CancellationTokenSource();
        _timer = new Timer(OnTick, state: null, InitialDelay, WarmInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnTick(object? state)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            // Прогон синхронный с точки зрения таймера: блокировка ОК, мы в фоне.
            // Токен отмены — на случай остановки инстанса, чтобы не зависнуть в HTTP.
            toolset.RefreshNowAsync(ct).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Не должно случаться — RefreshNowAsync глотает внутренние ошибки.
            // На всякий случай: один WARN на тик лучше, чем тихий вылет процесса.
            log.LogWarning(ex, "Higgsfield warmer: unhandled exception in tick");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
    }
}