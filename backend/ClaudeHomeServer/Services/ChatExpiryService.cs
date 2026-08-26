using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Фоновая уборка временных чатов: раз в минуту удаляет чаты, у которых с последней
// активности (UpdatedAt) прошло больше ExpiresAfterMinutes. Чаты с идущим ходом
// (Working/Waiting) не трогаем — удаление посреди работы недопустимо.
// Starting не исключаем: это «создан, ходов не было» — пустой чат висит в нём постоянно.
//
// Тем же проходом чистится архив — но ТОЛЬКО если ВЛАДЕЛЕЦ чата включил ретенцию (кнопка
// «Хранить» в строке действий архивного списка → User.ArchiveRetentionDays). По умолчанию
// архив вечен: человек убирает чат в архив именно чтобы сохранить его, и молчаливое удаление
// сделало бы кнопку «В архив» отложенным «Удалить». Ключ Session:ArchiveRetentionDays остаётся
// дефолтом ИНСТАНСА для тех, кто настройку не трогал.
// Один сервис на оба правила, а не второй BackgroundService: проход по сессиям и так один,
// а таймер и логика удаления совпадают до строчки.
public class ChatExpiryService(SessionManager sessions, UserStore users, IConfiguration config,
    ILogger<ChatExpiryService> log) : BackgroundService
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
        var instanceDefault = InstanceArchiveRetentionDays;
        // Кеш «владелец → срок» на один проход: у чатов одного человека владелец общий, а
        // резолв тянет проект и пользователя из сторов
        var perOwner = new Dictionary<string, int>();
        foreach (var session in sessions.GetAll())
        {
            var expired = ShouldExpire(session, nowUtc);
            var archiveDays = expired ? 0 : ArchiveDaysFor(session, instanceDefault, perOwner);
            var archiveRotted = !expired && ShouldPurgeArchived(session, nowUtc, archiveDays);
            if (!expired && !archiveRotted) continue;
            try
            {
                await sessions.DeleteAsync(session.Id);
                if (expired)
                    log.LogInformation("Временный чат {SessionId} «{Name}» удалён: неактивен дольше {Ttl} мин",
                        session.Id, session.Name ?? "Новый чат", session.ExpiresAfterMinutes);
                else
                    log.LogInformation("Архивный чат {SessionId} «{Name}» удалён: лежит в архиве дольше {Days} дн",
                        session.Id, session.Name ?? "Новый чат", archiveDays);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Не удалось удалить чат {SessionId} по сроку хранения", session.Id);
            }
        }
    }

    // Срок хранения архива для конкретного чата: настройка ЕГО владельца, а при отсутствии —
    // дефолт инстанса. Владелец не резолвится (осиротевший чат) — считаем, что уборка
    // выключена: удалять то, чей хозяин неизвестен, опаснее, чем не удалить.
    private int ArchiveDaysFor(Session s, int instanceDefault, Dictionary<string, int> cache)
    {
        if (s.ArchivedAt is null) return 0;
        if (sessions.ResolveOwnerId(s) is not { } ownerId) return 0;
        if (cache.TryGetValue(ownerId, out var cached)) return cached;
        var own = users.GetById(ownerId)?.ArchiveRetentionDays;
        var days = own is > 0 ? own.Value : instanceDefault;
        cache[ownerId] = days;
        return days;
    }

    // Дефолт инстанса: 0 (и любое некорректное/отрицательное значение) — уборка архива
    // выключена. Читаем на каждом тике, а не в конструкторе: значение меняется правкой
    // appsettings.Local.json, и перезапуск ради него требовать незачем.
    private int InstanceArchiveRetentionDays =>
        int.TryParse(config["Session:ArchiveRetentionDays"], out var days) && days > 0 ? days : 0;

    // Чистый предикат — извлечён для юнит-тестов
    internal static bool ShouldExpire(Session s, DateTime nowUtc) =>
        s.ExpiresAfterMinutes is int ttl && ttl > 0
        && s.Status is not (SessionStatus.Working or SessionStatus.Waiting)
        && nowUtc - CountFrom(s) >= TimeSpan.FromMinutes(ttl);

    // Чистый предикат уборки архива — извлечён для юнит-тестов.
    // Отсчёт идёт от МОМЕНТА АРХИВАЦИИ (ArchivedAt), а не от последней активности: чат мог
    // пролежать без движения полгода до того, как его убрали, и считать от UpdatedAt значило бы
    // снести его на ближайшем тике сразу после архивации.
    // Идущий ход защищает чат так же, как временный: удалять посреди работы нельзя (архивный
    // чат может работать — например, в нём доигрывает исполнитель задачи).
    internal static bool ShouldPurgeArchived(Session s, DateTime nowUtc, int retentionDays) =>
        retentionDays > 0
        && s.ArchivedAt is DateTime archivedAt
        && s.Status is not (SessionStatus.Working or SessionStatus.Waiting)
        && nowUtc - archivedAt >= TimeSpan.FromDays(retentionDays);

    // Точка отсчёта срока: последняя активность, но не раньше момента, когда срок задали
    // (Session.ExpiryAnchor) — иначе короткий срок на давно неактивном чате означал бы
    // удаление на ближайшем тике. Якоря нет (срок выставлен до появления поля) — считаем
    // от активности, как раньше.
    internal static DateTime CountFrom(Session s) =>
        s.ExpiryAnchor is DateTime anchor && anchor > s.UpdatedAt ? anchor : s.UpdatedAt;
}
