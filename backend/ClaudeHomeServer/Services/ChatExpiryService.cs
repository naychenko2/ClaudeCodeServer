using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Фоновая уборка временных чатов: раз в минуту удаляет чаты, у которых с последней
// активности (UpdatedAt) прошло больше ExpiresAfterMinutes. Чаты с идущим ходом
// (Working/Waiting) не трогаем — удаление посреди работы недопустимо.
// Starting не исключаем: это «создан, ходов не было» — пустой чат висит в нём постоянно.
public class ChatExpiryService(SessionManager sessions, ILogger<ChatExpiryService> log) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try { await TickAsync(DateTime.UtcNow); }
                catch (Exception ex) { log.LogError(ex, "Ошибка тика уборки временных чатов"); }
            }
        }
        catch (OperationCanceledException) { /* остановка приложения */ }
    }

    // Публичный для юнит-тестов: один проход по всем сессиям
    public async Task TickAsync(DateTime nowUtc)
    {
        foreach (var session in sessions.GetAll())
        {
            if (!ShouldExpire(session, nowUtc)) continue;
            try
            {
                await sessions.DeleteAsync(session.Id);
                log.LogInformation("Временный чат {SessionId} «{Name}» удалён: неактивен дольше {Ttl} мин",
                    session.Id, session.Name ?? "Новый чат", session.ExpiresAfterMinutes);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Не удалось удалить временный чат {SessionId}", session.Id);
            }
        }
    }

    // Чистый предикат — извлечён для юнит-тестов
    internal static bool ShouldExpire(Session s, DateTime nowUtc) =>
        s.ExpiresAfterMinutes is int ttl && ttl > 0
        && s.Status is not (SessionStatus.Working or SessionStatus.Waiting)
        && nowUtc - CountFrom(s) >= TimeSpan.FromMinutes(ttl);

    // Точка отсчёта срока: последняя активность, но не раньше момента, когда срок задали
    // (Session.ExpiryAnchor) — иначе короткий срок на давно неактивном чате означал бы
    // удаление на ближайшем тике. Якоря нет (срок выставлен до появления поля) — считаем
    // от активности, как раньше.
    internal static DateTime CountFrom(Session s) =>
        s.ExpiryAnchor is DateTime anchor && anchor > s.UpdatedAt ? anchor : s.UpdatedAt;
}
