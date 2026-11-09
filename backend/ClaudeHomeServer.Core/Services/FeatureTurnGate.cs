using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Гард «закрытие и возобновление эпика фичи — только человеком» (дефект df85c799): один
// предикат для обеих дверей вызова — REST (TasksController.Update) и MCP http-тулсет
// (TasksToolset.DenyFeatureClose). Вызывающий считается исполнителем, если он сервисный
// токен без валидной сессии (fail-closed) или сессия, привязанная к любой задаче
// (Session.TaskId задан); чат человека без задачи на глубине 0 проходит.
//
// Сведена в один хелпер по пункту 1 вердикта финального ревью фичи «Статусная модель»
// (3d2d793c): предикат был записан дважды — строкой в контроллере и хелпером в тулсете,
// связь держали только комментарии. Решение о делегированном ходу остаётся за
// DelegatedTurnGate (он живёт в Main и смотрит в SessionManager) — сюда передётся делегат:
// в спинке общая только композиция двух веток, идентичная для обоих путей. Сам отказ
// бросает TaskManager (FeatureClose/FeatureReopenDenied); хелпер лишь решает, пускать вызов.
public static class FeatureTurnGate
{
    public static bool DenyFeature(
        bool isServiceToken,
        Session? callerSession,
        string? callerSessionHeader,
        Func<bool> delegatedTurnDenied)
    {
        return (isServiceToken && (callerSession is null || callerSession.TaskId is not null))
            || (callerSessionHeader is not null && delegatedTurnDenied());
    }
}
