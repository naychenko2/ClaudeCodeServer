using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>Последнее состояние рук устройства — для первой отрисовки бейджа в чате.</summary>
/// <param name="SessionId">Чат хода, о котором донесение.</param>
/// <param name="TurnId">Ход исполнения на устройстве.</param>
/// <param name="State">Одно из <see cref="HandsChatStates.FromDevice"/>.</param>
public sealed record DeviceHandsLastState(string SessionId, string TurnId, string State, string? Reason, DateTimeOffset At);

/// <summary>
/// Ходы с руками, отправленные на устройства (ADR-016 §7), и последнее донесение агента по
/// каждому устройству. Наполняет <c>RemoteProcessRunner</c> (Execution) — только он знает, что
/// в spawn стоит маркер рук и к какому чату привязан ход; читают хаб устройств (Desktop) —
/// принять донесение агента только по СВОЕМУ ходу — и REST бейджа (Main). Реестр, который
/// наполняют и читают разные вертикали, живёт в спине (ADR-014).
///
/// Окно приёма донесений шире окна исполнения, иначе агент проигрывает гонку с обеих сторон:
/// <c>active</c> он шлёт ещё до первого кадра хода, поэтому ход регистрируется ДО отправки
/// spec; итог (<c>allowed</c>/<c>stopped</c>) — уже после кадра Exit, поэтому конец исполнения
/// ход не снимает, а закрывает (<see cref="End"/>): донесения по нему принимаются ещё
/// <see cref="EndGrace"/> либо до итогового донесения, смотря что раньше.
///
/// Память процесса: после перезапуска сервера бейдж начинает с «руки доступны», а агент
/// пришлёт свежее состояние со следующим ходом.
/// </summary>
public static class DeviceHandsTurns
{
    /// <summary>Сколько после конца исполнения ждём итоговое донесение агента.</summary>
    public static readonly TimeSpan EndGrace = TimeSpan.FromMinutes(2);

    private sealed record Turn(string SessionId, DateTimeOffset? EndedAt);

    private static readonly ConcurrentDictionary<string, Turn> Turns = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DeviceHandsLastState> Last = new(StringComparer.Ordinal);

    /// <summary>Ход с маркером рук уходит на устройство — зовётся ДО отправки spec.</summary>
    public static void Register(string ownerId, string deviceId, string turnId, string sessionId, DateTimeOffset? now = null)
    {
        Sweep(now ?? DateTimeOffset.UtcNow);
        Turns[TurnKey(ownerId, deviceId, turnId)] = new Turn(sessionId, null);
    }

    /// <summary>Ход не ушёл (отказ до запуска): донесения по нему не принимаются сразу.</summary>
    public static void Remove(string ownerId, string deviceId, string turnId) =>
        Turns.TryRemove(TurnKey(ownerId, deviceId, turnId), out _);

    /// <summary>
    /// Исполнение хода кончилось: ход больше не «действует руками», но итоговое донесение
    /// агента по нему ещё принимается <see cref="EndGrace"/>.
    /// </summary>
    public static void End(string ownerId, string deviceId, string turnId, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var key = TurnKey(ownerId, deviceId, turnId);
        while (Turns.TryGetValue(key, out var turn) && turn.EndedAt is null
               && !Turns.TryUpdate(key, turn with { EndedAt = at }, turn)) { }
        Sweep(at);
    }

    /// <summary>Чат хода с руками этого устройства; null — ход не наш или окно донесений закрыто.</summary>
    public static string? SessionOf(string ownerId, string deviceId, string turnId, DateTimeOffset? now = null) =>
        Turns.TryGetValue(TurnKey(ownerId, deviceId, turnId), out var turn) && Open(turn, now ?? DateTimeOffset.UtcNow)
            ? turn.SessionId
            : null;

    /// <summary>
    /// Донесение агента принято: итог по закрытому ходу закрывает и окно донесений. Итог,
    /// обогнавший конец исполнения, окно не закрывает — его снимет <see cref="EndGrace"/>.
    /// </summary>
    public static void Accepted(string ownerId, string deviceId, string turnId, string state)
    {
        if (state == HandsChatStates.Active) return;
        var key = TurnKey(ownerId, deviceId, turnId);
        if (Turns.TryGetValue(key, out var turn) && turn.EndedAt is not null)
            Turns.TryRemove(new KeyValuePair<string, Turn>(key, turn));
    }

    public static bool IsLive(string ownerId, string deviceId, string turnId) =>
        Turns.TryGetValue(TurnKey(ownerId, deviceId, turnId), out var turn) && turn.EndedAt is null;

    public static void SetLast(string ownerId, string deviceId, DeviceHandsLastState state) =>
        Last[DeviceKey(ownerId, deviceId)] = state;

    public static DeviceHandsLastState? LastOf(string ownerId, string deviceId) =>
        Last.GetValueOrDefault(DeviceKey(ownerId, deviceId));

    /// <summary>
    /// Состояние бейджа чата по донесениям устройства: «действует руками» — только пока ход жив,
    /// «остановлено» — пока не пришло новое донесение; иначе — «доступны».
    /// </summary>
    public static (string State, string? Reason) ChatStateOf(string ownerId, string deviceId, string sessionId)
    {
        if (LastOf(ownerId, deviceId) is not { } last || last.SessionId != sessionId)
            return (HandsChatStates.Allowed, null);
        return last.State switch
        {
            HandsChatStates.Active when IsLive(ownerId, deviceId, last.TurnId) => (HandsChatStates.Active, null),
            HandsChatStates.Stopped => (HandsChatStates.Stopped, last.Reason),
            _ => (HandsChatStates.Allowed, null),
        };
    }

    private static bool Open(Turn turn, DateTimeOffset now) => turn.EndedAt is not { } ended || now - ended <= EndGrace;

    // Закрытые ходы, по которым итог так и не пришёл (агент упал, связь пропала)
    private static void Sweep(DateTimeOffset now)
    {
        foreach (var (key, turn) in Turns)
            if (!Open(turn, now))
                Turns.TryRemove(new KeyValuePair<string, Turn>(key, turn));
    }

    private static string TurnKey(string ownerId, string deviceId, string turnId) => $"{ownerId}/{deviceId}/{turnId}";

    private static string DeviceKey(string ownerId, string deviceId) => $"{ownerId}/{deviceId}";
}
