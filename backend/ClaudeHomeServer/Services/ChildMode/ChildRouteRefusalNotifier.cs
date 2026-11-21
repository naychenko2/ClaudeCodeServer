using System.Collections.Concurrent;
using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// child/route-refusal — маршрут урока отказан детским гейтом (Б4b, ADR-041 ред. 7, И2″).
/// Издатель — одна точка: ChildModeGate.LogRefusal (точка отказа живого маршрута с причиной);
/// подписчик — <see cref="ChildRouteRefusalNotifier"/>. Текст ребёнка в событии не живёт:
/// его выбирает ChildRoute.RefusedTextFor по опекунам профиля (с опекунами — RefusedText
/// «…взрослым отправлено уведомление», без них — текст без обещания), причина несётся сюда.
/// TurnSeq/AgentDepth = 0: ход не начался — та же конвенция «синтетического хода», что у
/// локального голосового (комментарий у HumanMessageAccepted). SubjectId — предмет чата
/// (для дедупа и для имени предмета в тексте кураторам).
/// </summary>
public sealed record ChildRouteRefusalEvent(TurnContext Turn, string? SubjectId, string Reason) : ITurnNotification
{
    public const string Event = "child/route-refusal";
}

/// <summary>
/// Отказ маршрута урока → уведомление кураторам (Б4b, ADR-041 ред. 7: проверка на запуске,
/// уведомление кураторам). Образец — ChildStopSignalService (KM-B6d): связь с гейтом одна —
/// подписка на событие <see cref="ChildRouteRefusalEvent"/>; сам гейт этого сервиса не знает.
///
/// Получатели — опекуны ребёнка (GuardianUserIds): в отличие от стоп-сигнала админы его НЕ
/// получают — стоп-сигнал о ребёнке (его видят все взрослые), этот отказ о настройках,
/// которые ведут кураторы. Сам ребёнок и несуществующие id не получают.
///
/// Текст несёт причину отказа (человеческий текст гейта) и указание на окно настроек;
/// переписка ребёнка в уведомлении не живёт. Ссылки на чат нет: url на окно настроек у фронта
/// нет (в разборе hash такой поверхности нет, lib/nav.ts) — указание остаётся текстом.
///
/// Схлопывание: не чаще раза в час на (ребёнок, предмет, причина) — ключ эти три части;
/// решение принимается под блокировкой ключа ДО первого await, поэтому пять одновременных
/// отказов одной причины дают одно уведомление. Другая причина с тем же ключом не схлопывается:
/// каждая должна быть видна. Пустой список опекунов — слот не занимается: первым отказам
/// после появления опекунов ничего не мешает.
/// Сбой NotificationService — только лог: отказ запуска уже состоялся и не зависит от рассылки;
/// исключение наружу не выходит (шина погасила бы его сама, но сервис гарантирует это сам).
/// </summary>
public sealed class ChildRouteRefusalNotifier(
    IUserStore users,
    IChildModePolicy policy,
    ChildLimitsCalculator limits,
    NotificationService notifications,
    ITurnEventBus events,
    ILogger<ChildRouteRefusalNotifier> logger,
    TimeProvider? time = null) : IHostedService
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private sealed class Slot { public DateTimeOffset? Last; }

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _subscribed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Отписки у шины нет: повторный старт хоста не должен удваивать подписчика
        // (тот же сторож, что у ChildStopSignalSubscriber).
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            events.OnNotification<ChildRouteRefusalEvent>(OnRefusal, "ChildRouteRefusalNotifier");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task OnRefusal(ChildRouteRefusalEvent e)
    {
        if (e.Turn.OwnerId is not { Length: > 0 } ownerId) return;

        try
        {
            var snapshot = policy.For(ownerId);
            var recipients = Recipients(snapshot, ownerId);
            if (snapshot is null || recipients.Count == 0) return;

            var now = _time.GetUtcNow();
            if (!TryClaim(Key(ownerId, e.SubjectId, e.Reason), now)) return;

            var (title, body) = Compose(snapshot, ownerId, e, now);
            foreach (var userId in recipients)
            {
                try
                {
                    await notifications.SendAsync(userId, new CreateNotificationRequest
                    {
                        Kind = "info",
                        Type = "child_route_refusal",
                        Title = title,
                        Body = body,
                        Source = "Детский режим",
                        Tag = "Детский режим",
                        Sticky = true,
                    }, sendPush: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Детский режим: отказ маршрута не доставлен получателю {Recipient} (ребёнок {Owner}, причина {Reason})",
                        userId, ownerId, e.Reason);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Детский режим: отказ маршрута не разослан (ребёнок {Owner}, причина {Reason})", ownerId, e.Reason);
        }
    }

    // Ключ дедупа: (ребёнок, предмет, причина) — разделитель, которого нет ни в id, ни в тексте причины.
    private static string Key(string ownerId, string? subjectId, string reason) =>
        string.Concat(ownerId, "\u0000", subjectId ?? "", "\u0000", reason);

    // Окно по ключу занимается под блокировкой слота; false — в окне уже есть отправленный отказ
    // (та же схема, что у схлопывания стоп-сигнала).
    private bool TryClaim(string key, DateTimeOffset now)
    {
        if (_slots.Count > 256)
            foreach (var (k, stale) in _slots)
                if (stale.Last is { } l && now - l >= Window) _slots.TryRemove(k, out _);

        var slot = _slots.GetOrAdd(key, _ => new Slot());
        lock (slot)
        {
            if (slot.Last is { } last && now - last < Window) return false;
            slot.Last = now;
            return true;
        }
    }

    // Только опекуны ребёнка (существующие); сам ребёнок — нет; админ-не-опекун — нет (смысл
    // уведомления — настройки, которые ведут кураторы, ADR-041 ред. 7).
    private List<string> Recipients(ChildPolicySnapshot? snapshot, string ownerId)
    {
        var ids = new List<string>();
        foreach (var g in snapshot?.Profile?.GuardianUserIds ?? [])
            if (users.GetById(g) is not null) ids.Add(g);
        return ids.Where(id => id != ownerId).Distinct(StringComparer.Ordinal).ToList();
    }

    private (string Title, string Body) Compose(ChildPolicySnapshot snapshot, string ownerId, ChildRouteRefusalEvent e, DateTimeOffset now)
    {
        var user = users.GetById(ownerId);
        var who = string.IsNullOrWhiteSpace(user?.DisplayName) ? user?.Username ?? "Ребёнок" : user!.DisplayName!;
        var subject = e.SubjectId is { Length: > 0 } id
            ? snapshot.Profile?.Subjects?.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            : null;
        var what = subject is not null ? $"предмет «{subject.Label}»" : "занятие";
        var local = TimeZoneInfo.ConvertTime(now, limits.ResolveTimeZone(ownerId));
        var body = string.Create(CultureInfo.GetCultureInfo("ru-RU"),
            $"{who}, {what}: {e.Reason}. Время: {local:dd.MM HH:mm}. Загляните в настройки ребёнка — раздел «Детский режим».");
        return ($"Занятие недоступно: {who}", body);
    }
}
