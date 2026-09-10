namespace ClaudeHomeServer.Services.Desktop;

/// <summary>
/// Сторож сеансов рук: раз в 30 секунд гасит те, чей повод уже наступил — 15 минут без
/// вызовов, потолок 2 часа, исчезнувший чат (удалён или истёк), выключенная в проекте грань.
///
/// Опрос, а не подписка, — сознательно: чат удаляют и гасят из нескольких мест
/// (SessionManager, уборка временных чатов, каскады проекта), общего события у них нет, а
/// врезаться в чужие горячие файлы ради одной грани дороже, чем раз в полминуты спросить
/// реестр. Разрыв соединения ловится не здесь — на него есть наблюдатель канала
/// (IDeviceConnectionObserver), рестарт бэкенда гасит сеансы сам: реестр живёт в памяти.
/// </summary>
public sealed class DesktopSessionReaper(
    DesktopHandsSessionService hands,
    ILogger<DesktopSessionReaper> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await hands.SweepAsync(ct); }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика сторожа сеансов рук"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }
}
