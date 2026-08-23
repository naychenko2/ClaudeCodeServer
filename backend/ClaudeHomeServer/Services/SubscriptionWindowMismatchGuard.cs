using System.Globalization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Куда сторож подписок сообщает алерты владельцу. Отдельный шов (а не прямой вызов
// NotificationService) — тот же приём, что IKnowledgeAlertNotifier: сам NotificationService
// тянет стор, хаб и push, а тестировать надо логику дедупа, а не доставку.
public interface ISubscriptionAlertNotifier
{
    Task NotifyAdminsAsync(string title, string body);
}

// Доставка через NotificationService всем администраторам (подписки настраивает админ
// в конфиге — «владелец» это он). Kind "alert" (категория «Алерты») + push: инцидент
// чужого токена жил трое суток и нашёлся только ручным сравнением каналов.
public sealed class SubscriptionAlertNotifier(
    NotificationService notifications,
    UserStore users) : ISubscriptionAlertNotifier
{
    public async Task NotifyAdminsAsync(string title, string body)
    {
        try
        {
            var admins = users.GetAll().Where(u => u.Role == "admin").ToList();
            foreach (var admin in admins)
            {
                try
                {
                    await notifications.SendAsync(admin.Id, new CreateNotificationRequest
                    {
                        Kind = "alert",
                        Type = "subscription_window_mismatch",
                        Title = title,
                        Body = body,
                        // Экран «Расходы», вкладка квот — там видны снимки обоих каналов
                        Url = "#/spend",
                        Tag = "Подписки",
                        Source = "Подписки",
                    }, sendPush: true);
                }
                catch { /* отказ одному админу не должен обрывать рассылку остальным */ }
            }
        }
        catch { /* доставка best-effort — не ронять опрос, из-за которого она вызвана */ }
    }
}

// Сторож «чужого» setup-токена: сравнивает время сброса 5h-окна одного ключа подписки
// между двумя каналами — probe/turn (setup-токен из конфига) и oauth (профильный логин
// sub-{key}). Окно одного аккаунта не может сбрасываться в два разных момента: расхождение
// больше порога означает, что каналы смотрят на РАЗНЫЕ аккаунты — setup-токен был
// сгенерирован под чужим логином (инцидент 20–23.08.2026: два ключа пула жгли один
// Pro-аккаунт, настоящая подписка простаивала; система видела честный rejected и молчала).
//
// Диагностика, а не вердикт о здоровье: из ротации подписку НЕ выводим (ложное срабатывание
// автоматики опаснее ложного молчания — вердикт за человеком). Реакция — лог + алерт
// админам, не чаще раза на аккаунт при СМЕНЕ состояния (как SetStatus oauth-поллера):
// пока расхождение живёт, повторных уведомлений нет; сойди окна — флаг гаснет молча.
// Проверку зовут оба производителя снимков: warmup (probe) и oauth-поллер.
public sealed class SubscriptionWindowMismatchGuard(
    UsageService usage,
    ClaudeSubscriptionPool pool,
    ISubscriptionAlertNotifier notifier)
{
    private const string Window = "five_hour";

    // Окна выравнены по границе часа: легальная разница между каналами — секунды-минуты
    // (время ответа), 30 минут — с запасом за пределами любого шума
    internal static readonly TimeSpan Threshold = TimeSpan.FromMinutes(30);

    // Оба снимка обязаны быть свежими: устаревший сброс (после проката окна) — это уже
    // про другое окно, сравнение бессмысленно
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);

    // Состояние «расхождение живёт» по ключу (rising edge = алерт)
    private readonly object _lock = new();
    private readonly Dictionary<string, bool> _mismatched = [];

    public Task CheckAsync(string key) => CheckAsync(key, DateTime.UtcNow);

    // now вынесен в параметр для тестов свежести снимков
    internal async Task CheckAsync(string key, DateTime now)
    {
        var snapshots = usage.GetAllBySubscription().TryGetValue(key, out var list)
            ? list.Where(s => s.LimitType == Window).ToList()
            : [];
        var pair = LatestFreshPair(snapshots, now);
        // Свежей пары нет — данных для вывода нет, состояние ключа НЕ трогаем: иначе
        // провал одного канала (у аккаунта без профильного логина oauth-снимков нет
        // вовсе) гасил бы флаг, и то же расхождение било бы алертом при его возврате.
        if (pair is null) return;

        var diff = (ParseReset(pair.Value.SetupToken.ResetsAt)!.Value.UtcDateTime
            - ParseReset(pair.Value.Oauth.ResetsAt)!.Value.UtcDateTime).Duration();
        var mismatch = diff > Threshold;

        lock (_lock)
        {
            var prev = _mismatched.TryGetValue(key, out var p) && p;
            if (prev == mismatch) return; // состояние не менялось — молчим
            _mismatched[key] = mismatch;
        }

        if (!mismatch)
        {
            // Сошлись — хорошая новость: в лог, без уведомления и push
            Console.Error.WriteLine($"[SubscriptionGuard] '{key}': окна setup-токена и профильного логина согласованы");
            return;
        }

        Console.Error.WriteLine(
            $"[SubscriptionGuard] '{key}': сброс 5h-окна у setup-токена {pair.Value.SetupToken.ResetsAt} " +
            $"и у профильного логина {pair.Value.Oauth.ResetsAt} расходится на {(int)diff.TotalMinutes} мин — " +
            "каналы смотрят на разные аккаунты (чужой setup-токен?)");
        await notifier.NotifyAdminsAsync(
            $"Подписка «{DisplayNameOf(key)}»: подозрение на чужой setup-токен",
            $"Setup-токен из конфига и вход в профиле подписки ведут на РАЗНЫЕ аккаунты: время сброса " +
            $"5-часового окна расходится на {(int)diff.TotalMinutes} мин. Перегенерируйте токен: залогиньтесь " +
            "в claude.ai под нужным аккаунтом → выполните «claude setup-token» → замените OAuthToken этой " +
            "подписки в секции ClaudeSubscriptions (appsettings.Local.json).");
    }

    // Последняя свежая пара снимков окна по каналам: setup-токен (probe/turn) и oauth.
    // null — пары нет: у канала нет снимков свежее Freshness. Снимки без Source (легаси,
    // записаны до появления поля) канал не определяют и не участвуют.
    internal static (UsageSnapshot SetupToken, UsageSnapshot Oauth)? LatestFreshPair(
        IReadOnlyList<UsageSnapshot> windowSnapshots, DateTime now)
    {
        UsageSnapshot? setupToken = null, oauth = null;
        foreach (var s in windowSnapshots)
        {
            if (now - s.Timestamp > Freshness) continue;
            switch (s.Source)
            {
                case "oauth":
                    if (oauth is null || s.Timestamp > oauth.Timestamp) oauth = s;
                    break;
                case "probe" or "turn":
                    if (setupToken is null || s.Timestamp > setupToken.Timestamp) setupToken = s;
                    break;
            }
        }
        if (setupToken is null || oauth is null || ParseReset(setupToken.ResetsAt) is null
            || ParseReset(oauth.ResetsAt) is null)
            return null;
        return (setupToken, oauth);
    }

    private static DateTimeOffset? ParseReset(string? resetsAt) =>
        DateTimeOffset.TryParse(resetsAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
            ? dto : null;

    // Имя подписки для текста человеку: DisplayName с фолбэком «Аккаунт Claude» — сырой
    // ключ в пользовательские тексты не течёт (инвариант e862a991), только в лог.
    private string DisplayNameOf(string key)
    {
        var sub = pool.All.FirstOrDefault(s => s.Key == key);
        return !string.IsNullOrWhiteSpace(sub?.DisplayName) ? sub!.DisplayName : "Аккаунт Claude";
    }
}
