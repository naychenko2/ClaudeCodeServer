using System.Collections;
using System.Reflection;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Tests.Helpers;

// Доступ тестов к внутренностям SessionManager, которые наружу не торчат: накопитель хода
// живёт в приватной записи сессии, а тестам нужно разыграть на нём tool_use до вызова
internal static class SessionManagerTestAccess
{
    public static TurnAccumulator AccumulatorOf(this SessionManager sessions, string sessionId)
    {
        var entries = (IDictionary)typeof(SessionManager)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sessions)!;
        var entry = entries[sessionId]!;
        return (TurnAccumulator)entry.GetType().GetField("Accumulator")!.GetValue(entry)!;
    }
}
