namespace ClaudeHomeServer.Models;

/// <summary>
/// Что в продукте считается «живой» сессией.
///
/// Определение одно на всех потребителей: сводка главной (<c>HomeController</c>) и гейдж
/// <c>ccs.sessions.active</c>. До этого предикат жил локальной функцией в контроллере,
/// а телеметрия считала совсем другое — размер реестра сессий, то есть все чаты, когда-либо
/// созданные и не удалённые. График «активных сессий» показывал сотни и после рестарта
/// не падал, потому что мерил не то.
/// </summary>
public static class SessionLiveness
{
    /// <summary>
    /// Сессия работает или ждёт человека. Пустой новорождённый чат тоже висит в
    /// <see cref="SessionStatus.Starting"/> (процесс claude стартует лениво, при первом
    /// сообщении) — это не активность, поэтому Starting засчитывается только когда
    /// сообщения уже были.
    /// </summary>
    public static bool IsLive(this Session s) =>
        s.Status is SessionStatus.Working or SessionStatus.Waiting
        || (s.Status is SessionStatus.Starting && s.MessageCount > 0);
}
