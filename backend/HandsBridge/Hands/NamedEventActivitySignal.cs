using System.Runtime.Versioning;
using ClaudeHomeServer.HandsBridge.Policy;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Сигнал «ход действует руками» — именованное событие хода, которое создал агент
/// (<see cref="ClaudeHomeServer.Protocol.HandsBridgeArgs.ActivityEvent"/>). Мост его только
/// открывает и поднимает: нет события — агент уже отпустил ход, поднимать некому.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NamedEventActivitySignal(string name) : IHandsActivitySignal
{
    public void Raise()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                HandsLog.Write($"сигнал действия: события «{name}» нет — агент его не ждёт");
                return;
            }
            using (handle)
                handle.Set();
            HandsLog.Write("сигнал действия: первое действие рук в ходе");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            HandsLog.Write($"сигнал действия не поднят: {ex.Message}");
        }
    }
}
