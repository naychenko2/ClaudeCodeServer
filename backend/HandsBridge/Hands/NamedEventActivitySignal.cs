using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Policy;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Сигнал агенту — именованное событие хода, которое создал агент: «ход действует руками»
/// (<see cref="ClaudeHomeServer.Protocol.HandsBridgeArgs.ActivityEvent"/>) или «руки заняты»
/// (<see cref="ClaudeHomeServer.Protocol.HandsBridgeArgs.BusyEvent"/>). Мост его только открывает
/// и поднимает: нет события — агент уже отпустил ход, поднимать некому.
/// </summary>
/// <param name="what">Что значит сигнал — для лога моста.</param>
[SupportedOSPlatform("windows")]
internal sealed class NamedEventActivitySignal(string name, string what = "первое действие рук в ходе") : IHandsActivitySignal
{
    public void Raise()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                HandsLog.Write($"сигнал «{what}»: события «{name}» нет — агент его не ждёт");
                return;
            }
            using (handle)
                handle.Set();
            HandsLog.Write($"сигнал: {what}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            HandsLog.Write($"сигнал «{what}» не поднят: {ex.Message}");
        }
    }
}

/// <summary>
/// Событие агента «ход окончен» (<see cref="ClaudeHomeServer.Protocol.HandsBridgeArgs.TurnEndEvent"/>):
/// каждый его подъём отпускает замок машины. Нет события — замок отпустит только смерть моста.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NamedTurnEndWatch
{
    public static void Start(string name, Action turnEnded)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                HandsLog.Write($"конец хода: события «{name}» нет — замок рук отпустит только смерть моста");
                return;
            }
            // Событие с автосбросом: каждый подъём будит ожидание ровно один раз. Живёт до конца процесса
            ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => turnEnded(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            HandsLog.Write($"конец хода не слушается: {ex.Message}");
        }
    }
}
