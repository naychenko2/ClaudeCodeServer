using System.Collections.Concurrent;
using System.Globalization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>Откуда пришёл стоп-сигнал (ADR-041, ручка №9).</summary>
public enum StopSignalSource { Tutor, Child }

/// <summary>Итог Raise: сигнал ушёл взрослым или схлопнулся в уже отправленный.</summary>
public enum StopSignalOutcome { Raised, Collapsed }

/// <summary>
/// Стоп-сигнал ребёнка → уведомление взрослым (KM-B6d, ADR-041 решение 1, ручка №9). Два источника,
/// одна точка: маркер тьютора (разбор в конце хода, TurnCompletion) и кнопка «Мне неприятно».
///
/// Инвариант: уведомление НЕ раскрывает переписку — в нём кто, когда, пространство и источник;
/// ни текста сообщений, ни причины из маркера, ни id чата, ни ссылки на чат (Url/SessionId/
/// ProjectId уведомления пусты). В логе — тоже только id владельца и источник.
///
/// Схлопывание: не чаще раза в минуту на пару (ownerId, sessionId); решение принимается под
/// блокировкой ключа ДО первого await, поэтому два одновременных сигнала дают одно уведомление.
/// Сбой NotificationService — только лог: ни ход, ни ручка не падают.
/// Зависимости намеренно без SessionManager: сервис зовёт TurnCompletion, цикла быть не должно.
/// </summary>
public sealed class ChildStopSignalService(
    IUserStore users,
    IChildModePolicy policy,
    ChildLimitsCalculator limits,
    NotificationService notifications,
    ILogger<ChildStopSignalService> logger,
    TimeProvider? time = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class Slot { public DateTimeOffset? Last; }

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<StopSignalOutcome> RaiseAsync(string ownerId, string? sessionId, StopSignalSource source)
    {
        var now = _time.GetUtcNow();
        if (!TryClaim(ownerId, sessionId, now)) return StopSignalOutcome.Collapsed;

        try
        {
            var recipients = Recipients(ownerId);
            var (title, body) = Compose(ownerId, source, now);
            foreach (var userId in recipients)
            {
                try
                {
                    await notifications.SendAsync(userId, new CreateNotificationRequest
                    {
                        Kind = "info",
                        Type = "child_stop_signal",
                        Title = title,
                        Body = body,
                        Source = "Детский режим",
                        Tag = "Детский режим",
                        Sticky = true,
                    }, sendPush: true);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Детский режим: стоп-сигнал не доставлен получателю {Recipient} (ребёнок {Owner}, источник {Source})",
                        userId, ownerId, source);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: стоп-сигнал не разослан (ребёнок {Owner}, источник {Source})", ownerId, source);
        }
        return StopSignalOutcome.Raised;
    }

    // Окно по ключу занимается под блокировкой слота; false — в окне уже есть отправленный сигнал
    private bool TryClaim(string ownerId, string? sessionId, DateTimeOffset now)
    {
        if (_slots.Count > 256)
            foreach (var (key, stale) in _slots)
                if (stale.Last is { } l && now - l >= Window) _slots.TryRemove(key, out _);

        var slot = _slots.GetOrAdd(ownerId + "\n" + (sessionId ?? ""), _ => new Slot());
        lock (slot)
        {
            if (slot.Last is { } last && now - last < Window) return false;
            slot.Last = now;
            return true;
        }
    }

    // Все админы и опекуны ребёнка; сам ребёнок и неизвестные id не получают
    private List<string> Recipients(string ownerId)
    {
        var ids = new List<string>();
        foreach (var u in users.GetAll())
            if (u.Role == "admin") ids.Add(u.Id);
        foreach (var g in policy.For(ownerId)?.Profile?.GuardianUserIds ?? [])
            if (users.GetById(g) is not null) ids.Add(g);
        return ids.Where(id => id != ownerId).Distinct(StringComparer.Ordinal).ToList();
    }

    private (string Title, string Body) Compose(string ownerId, StopSignalSource source, DateTimeOffset now)
    {
        var user = users.GetById(ownerId);
        var who = string.IsNullOrWhiteSpace(user?.DisplayName) ? user?.Username ?? "Ребёнок" : user!.DisplayName!;
        var local = TimeZoneInfo.ConvertTime(now, limits.ResolveTimeZone(ownerId));
        var what = source == StopSignalSource.Child
            ? "нажато «Мне неприятно»"
            : "тьютор заметил тревожное сообщение";
        var body = string.Create(CultureInfo.GetCultureInfo("ru-RU"),
            $"{who}, пространство «Уроки»: {what}. Время: {local:dd.MM HH:mm}. Загляните к ребёнку.");
        return ($"Стоп-сигнал: {who}", body);
    }
}
