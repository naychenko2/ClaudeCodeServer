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
/// Память процесса: после перезапуска сервера бейдж начинает с «руки доступны», а агент
/// пришлёт свежее состояние со следующим ходом.
/// </summary>
public static class DeviceHandsTurns
{
    private static readonly ConcurrentDictionary<string, string> Sessions = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DeviceHandsLastState> Last = new(StringComparer.Ordinal);

    /// <summary>Ход с маркером рук ушёл на устройство.</summary>
    public static void Register(string ownerId, string deviceId, string turnId, string sessionId) =>
        Sessions[TurnKey(ownerId, deviceId, turnId)] = sessionId;

    /// <summary>Исполнение хода кончилось: донесения по нему больше не принимаются.</summary>
    public static void Remove(string ownerId, string deviceId, string turnId) =>
        Sessions.TryRemove(TurnKey(ownerId, deviceId, turnId), out _);

    /// <summary>Чат хода с руками этого устройства; null — ход не наш или уже кончился.</summary>
    public static string? SessionOf(string ownerId, string deviceId, string turnId) =>
        Sessions.GetValueOrDefault(TurnKey(ownerId, deviceId, turnId));

    public static bool IsLive(string ownerId, string deviceId, string turnId) =>
        Sessions.ContainsKey(TurnKey(ownerId, deviceId, turnId));

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

    private static string TurnKey(string ownerId, string deviceId, string turnId) => $"{ownerId}/{deviceId}/{turnId}";

    private static string DeviceKey(string ownerId, string deviceId) => $"{ownerId}/{deviceId}";
}
