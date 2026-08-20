using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Desktop;

/// <summary>Исход обмена кода сопряжения на device-токен.</summary>
public enum DevicePairingStatus
{
    Ok,
    /// <summary>Код не найден, истёк или уже использован — наружу все три неразличимы.</summary>
    BadCode,
    /// <summary>Исчерпаны попытки владельца на этом эндпоинте: код погашен, нужен новый.</summary>
    TooManyAttempts,
    /// <summary>Веб-сессия, выпустившая код, больше не действует (смена пароля, удаление пользователя).</summary>
    SessionGone,
    /// <summary>Отпечаток совпал с хостом бэкенда — руки на машине сервера не выдаём.</summary>
    SameHost,
    /// <summary>Имя, отпечаток — не проходят проверку реестра (текст в Error).</summary>
    Rejected,
}

/// <summary>Заявка на сопряжение, видимая владельцу в вебе.</summary>
public sealed record DevicePairingCode(string Code, DateTime ExpiresAt, int AttemptsLeft);

/// <summary>Результат обмена: токен существует в открытом виде ровно здесь и ровно раз.</summary>
public sealed record DevicePairingResult(
    DevicePairingStatus Status, DesktopDevice? Device = null, string? Token = null, string? Error = null);

/// <summary>
/// Сопряжение устройства одноразовым кодом (ADR-008, «Аутентификация и транспорт»):
/// 8 символов, TTL 5 минут, не более 5 попыток. Заявки живут в памяти — рестарт бэкенда
/// гасит их, и это правильно: код живёт минуты и произносится человеком вслух.
///
/// Счётчик попыток — ПО ВЛАДЕЛЬЦУ И ЭНДПОИНТУ, а не по коду: иначе перебор обходится
/// перевыпуском кода (новый код = новые пять попыток), и 5 попыток превращаются в
/// бесконечность. По той же причине счётчик не обнуляется выпуском новой заявки —
/// только истечением окна.
/// </summary>
public sealed class DevicePairingService(
    DeviceRegistry registry, UserStore users, ILogger<DevicePairingService>? logger = null,
    IConfiguration? config = null, IHostEnvironment? env = null)
{
    /// <summary>
    /// ОТЛАДОЧНАЯ ДЫРА, и другого названия у неё нет: `Desktop:AllowSameHostPairing` снимает
    /// отказ на сопряжение с машиной самого бэкенда. В бою это обход изоляции песочницы —
    /// руки на машине сервера грань не выдаёт вовсе (ADR-008). Держится на двух замках сразу:
    /// ключ выключен по умолчанию и действует ТОЛЬКО в Development, то есть на боевом
    /// инстансе не сработает, даже если ключ туда попадёт.
    ///
    /// Зачем вообще: у разработчика продукт и десктопный клиент живут на одном компьютере, и
    /// без этого люка сквозной путь «сервер ↔ живой клиент» нечем проверить.
    /// </summary>
    private readonly bool _allowSameHost =
        (env?.IsDevelopment() ?? false)
        && (config?.GetValue("Desktop:AllowSameHostPairing", false) ?? false);

    public const int CodeLength = 8;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Эндпоинт обмена — измерение счётчика попыток (второе — владелец).</summary>
    public const string PairEndpoint = "pair";

    /// <summary>
    /// Алфавит кода: заглавные латинские и цифры без I, O, 0, 1 — их путают и на глаз,
    /// и на слух, а код диктуют человеку.
    /// </summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly ConcurrentDictionary<string, Attempts> _attempts = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Выпускает код для владельца. Прежняя заявка владельца умирает — активный код всегда
    /// один. <paramref name="webSessionKey"/> — отпечаток веб-сессии, инициировавшей
    /// сопряжение: только она видит статус заявки и может её отменить.
    /// </summary>
    public DevicePairingCode Start(string ownerId, string webSessionKey, int tokenVersion, DateTime? now = null)
    {
        var moment = now ?? DateTime.UtcNow;
        var pending = new Pending(
            Code: GenerateCode(),
            OwnerId: ownerId,
            WebSessionKey: webSessionKey,
            WebTokenVersion: tokenVersion,
            ExpiresAt: moment.Add(CodeLifetime));

        _pending[ownerId] = pending;
        logger?.LogInformation("Сопряжение устройства: выпущен код для владельца {OwnerId}", ownerId);
        return new DevicePairingCode(pending.Code, pending.ExpiresAt, AttemptsLeft(ownerId, PairEndpoint, moment));
    }

    /// <summary>Активная заявка владельца — только для выпустившей её веб-сессии.</summary>
    public DevicePairingCode? GetPending(string ownerId, string webSessionKey, DateTime? now = null)
    {
        var moment = now ?? DateTime.UtcNow;
        if (!_pending.TryGetValue(ownerId, out var pending)) return null;
        if (pending.ExpiresAt <= moment) return null;
        if (!string.Equals(pending.WebSessionKey, webSessionKey, StringComparison.Ordinal)) return null;
        return new DevicePairingCode(pending.Code, pending.ExpiresAt, AttemptsLeft(ownerId, PairEndpoint, moment));
    }

    /// <summary>Отменяет заявку. false — заявки нет либо она принадлежит другой веб-сессии.</summary>
    public bool Cancel(string ownerId, string webSessionKey)
    {
        if (!_pending.TryGetValue(ownerId, out var pending)) return false;
        if (!string.Equals(pending.WebSessionKey, webSessionKey, StringComparison.Ordinal)) return false;
        return _pending.TryRemove(new KeyValuePair<string, Pending>(ownerId, pending));
    }

    /// <summary>
    /// Обмен кода на device-токен. Вызывается анонимно — единственный «ключ» здесь сам код,
    /// поэтому вся дисциплина попыток живёт тут.
    /// </summary>
    public DevicePairingResult Redeem(
        string endpoint, string? code, string name, string fingerprint,
        string? clientVersion = null, DateTime? now = null)
    {
        var moment = now ?? DateTime.UtcNow;
        var normalizedCode = (code ?? "").Trim().ToUpperInvariant();

        lock (_lock)
        {
            var match = _pending.Values.FirstOrDefault(p =>
                p.ExpiresAt > moment && CodesEqual(p.Code, normalizedCode));

            if (match is null)
            {
                // Промах засчитываем КАЖДОЙ живой заявке: подбирают именно их, а какую
                // именно — по неудачной попытке не видно
                var burned = ChargeMiss(endpoint, moment);
                logger?.LogWarning(
                    "Сопряжение устройства: неверный код, погашено заявок по исчерпанию попыток: {Burned}", burned);
                return new DevicePairingResult(DevicePairingStatus.BadCode,
                    Error: "Код не подходит или истёк. Выпусти новый код в веб-интерфейсе");
            }

            if (AttemptsLeft(match.OwnerId, endpoint, moment) <= 0)
            {
                _pending.TryRemove(match.OwnerId, out _);
                return new DevicePairingResult(DevicePairingStatus.TooManyAttempts,
                    Error: "Слишком много попыток. Подожди пять минут и выпусти новый код");
            }

            // Код привязан к веб-сессии: если та отозвана (смена пароля) — код мёртв
            if (!users.IsTokenVersionCurrent(match.OwnerId, match.WebTokenVersion))
            {
                _pending.TryRemove(match.OwnerId, out _);
                return new DevicePairingResult(DevicePairingStatus.SessionGone,
                    Error: "Сессия, выпустившая код, больше не действует. Войди заново и повтори");
            }

            if (string.Equals(fingerprint, MachineFingerprint.OfHost(), StringComparison.OrdinalIgnoreCase))
            {
                if (!_allowSameHost)
                    return new DevicePairingResult(DevicePairingStatus.SameHost,
                        Error: "Это та же машина, где работает сервер: на ней грань десктопа не нужна");

                // Люк открыт — говорим об этом вслух: молчаливое исключение из правила
                // безопасности в логе не отличить от работающего правила
                logger?.LogWarning(
                    "Сопряжение с машиной самого бэкенда разрешено ключом Desktop:AllowSameHostPairing " +
                    "(отладочный режим, только Development)");
            }

            try
            {
                var (device, token) = registry.Register(match.OwnerId, name, fingerprint, clientVersion);
                _pending.TryRemove(match.OwnerId, out _);
                logger?.LogInformation(
                    "Сопряжено устройство {Device} владельца {OwnerId} (версия токена {Version})",
                    device.Name, device.OwnerId, device.TokenVersion);
                return new DevicePairingResult(DevicePairingStatus.Ok, device, token);
            }
            catch (InvalidOperationException ex)
            {
                // Заявку не гасим: человек поправит имя и повторит с тем же кодом
                return new DevicePairingResult(DevicePairingStatus.Rejected, Error: ex.Message);
            }
        }
    }

    /// <summary>Сколько попыток осталось у владельца на этом эндпоинте.</summary>
    public int AttemptsLeft(string ownerId, string endpoint, DateTime? now = null)
    {
        var moment = now ?? DateTime.UtcNow;
        if (!_attempts.TryGetValue(AttemptKey(ownerId, endpoint), out var attempts)) return MaxAttempts;
        if (attempts.WindowEndsAt <= moment) return MaxAttempts;
        return Math.Max(0, MaxAttempts - attempts.Count);
    }

    // Промах стоит попытки каждому владельцу с живой заявкой; исчерпавшие лимит заявки
    // гасятся немедленно — дальше подбирать нечего
    private int ChargeMiss(string endpoint, DateTime moment)
    {
        var burned = 0;
        foreach (var pending in _pending.Values.Where(p => p.ExpiresAt > moment).ToList())
        {
            var key = AttemptKey(pending.OwnerId, endpoint);
            var attempts = _attempts.AddOrUpdate(key,
                _ => new Attempts(1, moment.Add(CodeLifetime)),
                (_, current) => current.WindowEndsAt <= moment
                    ? new Attempts(1, moment.Add(CodeLifetime))
                    : current with { Count = current.Count + 1 });

            if (attempts.Count < MaxAttempts) continue;
            _pending.TryRemove(pending.OwnerId, out _);
            burned++;
        }
        return burned;
    }

    private static string AttemptKey(string ownerId, string endpoint) => $"{ownerId}|{endpoint}";

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    // Сравнение за постоянное время: код короткий, и разница по времени сравнения —
    // не тот подарок, который стоит делать перебору
    private static bool CodesEqual(string a, string b) =>
        a.Length == b.Length
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

    private sealed record Pending(
        string Code, string OwnerId, string WebSessionKey, int WebTokenVersion, DateTime ExpiresAt);

    private sealed record Attempts(int Count, DateTime WindowEndsAt);
}

/// <summary>
/// Канал, по которому не жалко отдать код сопряжения и device-токен (ADR-008: «по
/// нешифрованному каналу код и токен не выдаются»). Штатный доступ в продукте сегодня —
/// http://&lt;локальный-IP&gt;:5000, и без этого правила подменный сервер в той же сети сам
/// сочиняет текст подтверждения и командует машиной, не нарушив ни одного другого правила.
/// </summary>
public static class DeviceChannelGuard
{
    /// <summary>Заголовки, по которым видно, что перед нами обратный прокси.</summary>
    public static bool ViaProxy(IHeaderDictionary headers) =>
        headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Forwarded-Proto");

    /// <summary>
    /// HTTPS — годится всегда. Незашифрованный канал годится только на петле: там
    /// подслушивать нечего, а сопряжение с машиной самого бэкенда всё равно отказывает по
    /// отпечатку. Но если запрос пришёл через прокси (адрес соединения — петля, а клиент
    /// снаружи), петля больше ничего не доказывает — требуем HTTPS.
    /// </summary>
    public static bool IsSecure(bool isHttps, IPAddress? remoteIp, bool viaProxy)
    {
        if (isHttps) return true;
        if (viaProxy) return false;
        // Адрес неизвестен только у внутрипроцессного хоста (TestServer): у настоящего
        // сетевого клиента он есть всегда
        return remoteIp is null || IPAddress.IsLoopback(remoteIp);
    }

    public static bool IsSecure(HttpRequest request) =>
        IsSecure(request.IsHttps, request.HttpContext.Connection.RemoteIpAddress, ViaProxy(request.Headers));
}
