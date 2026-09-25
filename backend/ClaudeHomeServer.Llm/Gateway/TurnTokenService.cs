using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Привязка токена хода: чей ход, в каком чате, какой по счёту (TurnId — идентификатор для
// маршрута шлюза /gw/t/{turnId}/…) и с какого устройства он может звонить. DeviceId = null —
// ход не привязан к устройству (серверный проект), проверку устройства шлюз не делает.
// Route — маршрут шлюза LLM (провайдер, аккаунт, модель), решённый при выдаче токена;
// null — шлюзу LLM этот ход не обслуживать (токен выдан только для MCP).
public sealed record TurnTokenGrant(
    string TurnId,
    string OwnerId,
    string SessionId,
    string? DeviceId,
    DateTimeOffset IssuedAt,
    GatewayRoute? Route = null,
    TurnTokenLifetime Lifetime = TurnTokenLifetime.Turn);

// Чем кончается жизнь токена. Turn — концом хода чата (turn/completed). Process — концом
// процесса CLI, которому токен выдан: ClaudeSession держит один процесс на много ходов, и
// отзыв по turn/completed дал бы второму ходу того же процесса 401. Такой токен отзывает
// выдавший (RevokeTurn) по выходу, kill или сбою запуска процесса; удаление чата и потолок
// жизни действуют на оба вида.
public enum TurnTokenLifetime
{
    Turn,
    Process,
}

// Выданный токен: секрет отдаётся ровно один раз, сервер хранит только его хеш.
public sealed record IssuedTurnToken(string Token, TurnTokenGrant Grant);

// Токен хода для шлюза LLM/MCP (ADR-016, план §2 п. 4, сторож G4).
//
// Живёт ТОЛЬКО в памяти процесса: рестарт бэкенда отзывает всё без отдельного кода.
// TTL нет намеренно — ход может идти часами (долгие сборки, цикл «до готово»), и токен,
// умерший посреди хода, рвёт его на полуслове. Жизнь токена задаёт TurnTokenLifetime:
// Turn — отзыв по turn/completed, Process (ход на устройстве, ADR-016 §2) — по концу
// процесса CLI явным RevokeTurn. Удаление чата и сбой запуска ДО адаптера отзываются явно
// (RevokeSession / RevokeTurn). Потолок жизни — только страховка от потерянного события,
// а не срок годности.
//
// turn/completed публикует FallbackLlmSessionAdapter в finally оркестрации — туда сходятся
// успех, ошибка, «Стоп» (interrupted) и убийство адаптера (cancelled). Событие не несёт
// идентификатора токена, поэтому отзываются все токены чата, выданные к моменту обработки
// события. Контракт выдающего: токен выдаётся на старте оркестрации хода, не раньше, —
// тогда гонка «следующий ход выдал токен до того, как дошло событие прошлого» даёт отказ
// (401 новому ходу), а не утечку.
public sealed class TurnTokenService
{
    public static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromHours(24);

    private sealed record Entry(byte[] TokenHash, TurnTokenGrant Grant);

    private readonly ConcurrentDictionary<string, Entry> _byTurn = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _maxLifetime;
    private readonly ILogger<TurnTokenService>? _log;

    public TurnTokenService(ITurnEventBus events, TimeProvider? time = null,
        TimeSpan? maxLifetime = null, ILogger<TurnTokenService>? log = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _time = time ?? TimeProvider.System;
        _maxLifetime = maxLifetime ?? DefaultMaxLifetime;
        _log = log;
        events.OnNotification<TurnCompleted>(OnTurnCompleted, "TurnTokenService.Revoke");
    }

    public int ActiveCount => _byTurn.Count;

    public IssuedTurnToken Issue(string ownerId, string sessionId, string? deviceId = null, GatewayRoute? route = null,
        TurnTokenLifetime lifetime = TurnTokenLifetime.Turn)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        SweepExpired();
        var turnId = Guid.NewGuid().ToString("N");
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var grant = new TurnTokenGrant(turnId, ownerId, sessionId,
            string.IsNullOrEmpty(deviceId) ? null : deviceId, _time.GetUtcNow(), route, lifetime);
        _byTurn[turnId] = new Entry(Hash(token), grant);
        return new IssuedTurnToken(token, grant);
    }

    // Проверка звонка шлюзу: ход жив, секрет совпадает, устройство то, к которому привязан
    // ход. null — отказ (шлюз отвечает 401); причина наружу не отдаётся.
    public TurnTokenGrant? Validate(string? turnId, string? token, string? deviceId = null)
    {
        if (string.IsNullOrEmpty(turnId) || string.IsNullOrEmpty(token)) return null;
        if (!_byTurn.TryGetValue(turnId, out var entry)) return null;
        if (IsExpired(entry.Grant))
        {
            if (_byTurn.TryRemove(new KeyValuePair<string, Entry>(turnId, entry)))
                _log?.LogWarning("Токен хода {TurnId} чата {SessionId} снят по потолку жизни: конец хода не дошёл",
                    turnId, entry.Grant.SessionId);
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(entry.TokenHash, Hash(token))) return null;
        if (entry.Grant.DeviceId is not null && !string.Equals(entry.Grant.DeviceId, deviceId, StringComparison.Ordinal))
            return null;
        return entry.Grant;
    }

    // Ротация аккаунта посреди хода (исчерпание, auth-dead): шлюз перевешивает маршрут живого
    // токена. Отозванный токен не воскрешается — false.
    public bool Reroute(string turnId, GatewayRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        while (_byTurn.TryGetValue(turnId, out var entry))
        {
            var updated = entry with { Grant = entry.Grant with { Route = route } };
            if (_byTurn.TryUpdate(turnId, updated, entry)) return true;
        }
        return false;
    }

    // Сбой запуска до адаптера: оркестрации не было, turn/completed не придёт.
    public bool RevokeTurn(string turnId) => _byTurn.TryRemove(turnId, out _);

    // Удаление чата и прочие случаи, когда отозвать надо всё, что относится к чату.
    public int RevokeSession(string sessionId) => RevokeWhere(g => g.SessionId == sessionId);

    public int RevokeAll()
    {
        var n = _byTurn.Count;
        _byTurn.Clear();
        return n;
    }

    private Task OnTurnCompleted(TurnCompleted e)
    {
        var cutoff = _time.GetUtcNow();
        RevokeWhere(g => g.Lifetime == TurnTokenLifetime.Turn && g.SessionId == e.Turn.SessionId && g.IssuedAt <= cutoff);
        return Task.CompletedTask;
    }

    private int RevokeWhere(Func<TurnTokenGrant, bool> match)
    {
        var n = 0;
        foreach (var (turnId, entry) in _byTurn)
            if (match(entry.Grant) && _byTurn.TryRemove(new KeyValuePair<string, Entry>(turnId, entry)))
                n++;
        return n;
    }

    private void SweepExpired() => RevokeWhere(IsExpired);

    private bool IsExpired(TurnTokenGrant g) => _time.GetUtcNow() - g.IssuedAt >= _maxLifetime;

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
