using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ClaudeHomeServer.Filters;

/// <summary>
/// Анти-рекурсия делегирования: действие запрещено, если вызвавший его MCP-сервер работает
/// на делегированном ходу (ход пришёл в чат через chats_send из другой сессии, agentDepth >= 1).
/// Иначе агент, которого позвали из чата, мог бы писать в третьи чаты, удалять данные и
/// запускать новых исполнителей — и так по кругу.
///
/// Раньше этот запрет жил в СОСТАВЕ инструментов MCP-сервера (env TASKS_EXECUTE, срезание
/// секций chats/destructive). Состав входит в сигнатуру запуска CLI, поэтому чередование
/// обычного и делегированного хода перезапускало процесс со всеми MCP-серверами: незавершённые
/// вызовы падали «Stream closed», а инструменты то появлялись, то исчезали («No such tool
/// available»). Инвариант теперь такой: **состав инструментов MCP-сервера не зависит от хода**,
/// а ограничения проверяются здесь, по актуальному состоянию сессии-вызывателя.
///
/// Глубину берём не из заголовка (тот запекается в env процесса и протухает при
/// переиспользовании живого прогона), а у самой сессии — по её id.
/// </summary>
/// <param name="action">Что именно запрещено — попадает в текст отказа для модели.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DenyOnDelegatedTurnAttribute(string action) : Attribute, IActionFilter
{
    // Заголовок ставит общий api() каждого MCP-сервера: id сессии, в которой работает модель
    public const string CallerHeader = "X-Caller-Session-Id";

    /// <summary>
    /// Запрещать ещё и на реакционном авто-ходу постановщика (доклад делегированной задачи):
    /// там agentDepth = 0, но запуск исполнителя всё равно нельзя — иначе A сам себе запускает
    /// только что созданную задачу → новый доклад → новая реакция → бесконечный платный цикл A↔B.
    /// </summary>
    public bool AlsoWhenExecutorSuppressed { get; init; }

    /// <summary>
    /// Режим «Командная реализация» (Э4): на ходу координатора запрет заменяется КВОТОЙ —
    /// пока цел бюджет итерации, штаб волен запускать исполнителей сам (иначе автономный
    /// цикл волн невозможен), а исчерпание бюджета возвращает прежний 403.
    /// Квота расходуется на ЛЮБОМ неделегированном ходу штаба, включая обычный человеческий:
    /// иначе координатор спокойно спамит «создать задачу + запустить» мимо бюджета — дыра
    /// ровно в той защите, ради которой запрет и меняли на квоту.
    /// Делегированного хода (agentDepth ≥ 1) исключение не касается: цепочка делегирования
    /// дальше не идёт независимо от режима.
    /// </summary>
    public bool AllowInTeamImplement { get; init; }

    // Ключ HttpContext.Items: квота расходуется здесь, в OnActionExecuting, ДО того как
    // действие реально что-то сделало — храним, за какую (сессия, владелец) она списана,
    // чтобы OnActionExecuted мог вернуть единицу, если действие не состоялось (m3, второй
    // проход Глеба: 404/400 не должны жечь бюджет команды быстрее, чем идёт реальная работа).
    private const string ConsumedRunKey = "TeamImplementRunConsumed";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        // Заголовка нет — запрос не от нашего MCP (фронт, интеграция): ограничение не наше дело
        if (http.Request.Headers[CallerHeader].FirstOrDefault() is not { Length: > 0 } callerSessionId)
            return;
        if (http.RequestServices.GetService<SessionManager>() is not { } sessions) return;
        if (http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is not { Length: > 0 } userId) return;

        var turn = sessions.GetActiveTurnDelegation(callerSessionId, userId);
        var delegated = IsDelegated(turn);

        // Квота вместо запрета — на ЛЮБОМ неделегированном ходу штаба, а не только на
        // реакционном: обычный ход координатора запускает исполнителей той же кнопкой, и
        // мимо бюджета он не должен идти. Разрешение сразу расходует единицу — счёт ведёт
        // бэкенд в точке запуска. Вердикт NotTeamMode = чат не штаб: решает прежний запрет.
        if (!delegated && AllowInTeamImplement)
        {
            var (verdict, reason) = sessions.TryConsumeTeamImplementRun(callerSessionId, userId);
            if (verdict == SessionManager.TeamRunQuota.Allowed)
            {
                http.Items[ConsumedRunKey] = (callerSessionId, userId);
                return;
            }
            if (verdict == SessionManager.TeamRunQuota.Exhausted)
            {
                context.Result = new ObjectResult(new
                {
                    error = $"{action} недоступно: {reason}. Доложи человеку сводку и дождись "
                        + "его решения (подтвердить план, добавить бюджет или завершить итерацию).",
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }
        }

        if (!delegated && !IsSuppressedExecutorTurn(turn, AlsoWhenExecutorSuppressed)) return;

        context.Result = new ObjectResult(new
        {
            error = delegated
                ? $"{action} недоступно на делегированном ходу: этот ход инициирован другим "
                    + "чатом, и цепочка делегирования дальше не идёт. Верни результат тому, кто "
                    + "тебя позвал — решение примет он или пользователь."
                : $"{action} недоступно на этом ходу: ты отвечаешь на доклад исполнителя, и "
                    + "запуск новой сессии отсюда закольцевал бы «доклад → запуск → доклад». "
                    + "Если запустить действительно нужно — попроси пользователя.",
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // Единица списана авансом в OnActionExecuting — вернуть её, если действие не
        // состоялось: контроллер отдал 4xx (задача не найдена, неверное состояние) или упал
        // исключением. Успех (обычно 200 Ok) единицу не трогает — она честно расходована.
        if (context.HttpContext.Items[ConsumedRunKey] is not (string sessionId, string userId)) return;
        var statusCode = context.Result switch
        {
            ObjectResult obj => obj.StatusCode,
            StatusCodeResult sc => sc.StatusCode,
            _ => null,
        };
        if (context.Exception is null && statusCode is null or < 400) return;
        if (context.HttpContext.RequestServices.GetService<SessionManager>() is { } sessions)
            sessions.RefundTeamImplementRun(sessionId, userId);
    }

    // Решение вынесено из фильтра, чтобы проверяться таблицей без HttpContext и DI
    internal static bool IsDelegated(TurnDelegationState turn) => turn.AgentDepth >= 1;

    internal static bool IsSuppressedExecutorTurn(TurnDelegationState turn, bool alsoWhenExecutorSuppressed) =>
        alsoWhenExecutorSuppressed && turn.ExecutorSuppressed;

    // Итоговое решение: запретить действие на этом ходу
    internal static bool ShouldDeny(TurnDelegationState turn, bool alsoWhenExecutorSuppressed) =>
        IsDelegated(turn) || IsSuppressedExecutorTurn(turn, alsoWhenExecutorSuppressed);
}
