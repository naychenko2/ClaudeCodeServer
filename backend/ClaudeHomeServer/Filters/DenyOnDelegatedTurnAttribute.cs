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

    /// <summary>
    /// Цикл «До готово» (work-loop): как AllowInTeamImplement, но для обычного чата с
    /// включённым тумблером — запрет хода доклада заменяется КВОТОЙ запусков, иначе
    /// «доклад → запуск → доклад» — бесконечный платный цикл. Квота принадлежит самой
    /// сессии цикла (вверх по родителям не поднимаемся); делегированного хода
    /// (agentDepth ≥ 1) исключение не касается — анти-рекурсия не ослабляется режимом.
    /// Guard B4 запрещает оба режима в одном чате, так что двойного списания нет.
    /// </summary>
    public bool AllowInWorkLoop { get; init; }

    // Ключ HttpContext.Items: квота расходуется в OnActionExecuting, ДО того как действие
    // реально что-то сделало — флаг «списана» даёт OnActionExecuted вернуть единицу, если
    // действие не состоялось (m3, второй проход Глеба: 404/400 не должны жечь бюджет
    // команды быстрее, чем идёт реальная работа). Сессия и владелец не меняются между
    // executing/executed — их refund берёт из того же запроса.
    private const string ConsumedRunKey = "TeamImplementRunConsumed";

    // Отдельный ключ квоты цикла: возврат обязан идти ровно в ту квоту, что была списана
    private const string ConsumedWorkLoopRunKey = "WorkLoopRunConsumed";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        // REST-путь без заголовка — запрос не от нашего MCP (фронт, интеграция):
        // ограничение не наше дело, guard фейл-опенит (историческое поведение фильтра)
        var decision = DelegatedTurnGate.Decide(
            http.RequestServices.GetService<SessionManager>(),
            http.User.FindFirstValue(JwtRegisteredClaimNames.Sub),
            http.Request.Headers[CallerHeader].FirstOrDefault(),
            action, AlsoWhenExecutorSuppressed, AllowInTeamImplement, AllowInWorkLoop);
        if (decision.Allowed)
        {
            // Списанную квоту запоминаем для возврата в OnActionExecuted при неудаче
            if (decision.TeamQuotaConsumed) http.Items[ConsumedRunKey] = true;
            if (decision.WorkLoopQuotaConsumed) http.Items[ConsumedWorkLoopRunKey] = true;
            return;
        }

        context.Result = new ObjectResult(new { error = decision.DenyText })
            { StatusCode = StatusCodes.Status403Forbidden };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // Возврат списанной единицы гарантирован при ЛЮБОМ исходе, кроме чистого успеха
        // (2xx — запуск состоялся, платить честно). Раньше условие смотрело лишь на
        // Exception и статус результата — и промахивалось на обрыве/замыкании: если ход
        // замкнулся другим фильтром ДО запуска действия (Canceled=true, без результата-
        // ошибки) или запрос оборвался без результата, единица «зависала» навсегда
        // (ревью Глеба: квота work-loop между TryConsume и Refund).
        var failed = context.Exception is not null
            || context.Canceled
            || context.Result switch
            {
                ObjectResult obj => obj.StatusCode >= 400,
                StatusCodeResult sc => sc.StatusCode >= 400,
                _ => false,
            };
        if (!failed) return;
        var http = context.HttpContext;
        if (http.RequestServices.GetService<SessionManager>() is not { } sessions) return;
        var callerSessionId = http.Request.Headers[CallerHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(callerSessionId)) return;
        if (http.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is not { Length: > 0 } userId) return;

        if (http.Items[ConsumedRunKey] is true)
            sessions.RefundTeamImplementRun(callerSessionId, userId);
        if (http.Items[ConsumedWorkLoopRunKey] is true)
            sessions.RefundWorkLoopRun(callerSessionId, userId);
    }

    // Решение вынесено из фильтра, чтобы проверяться таблицей без HttpContext и DI
    internal static bool IsDelegated(TurnDelegationState turn) => turn.AgentDepth >= 1;

    internal static bool IsSuppressedExecutorTurn(TurnDelegationState turn, bool alsoWhenExecutorSuppressed) =>
        alsoWhenExecutorSuppressed && turn.ExecutorSuppressed;

    // Итоговое решение: запретить действие на этом ходу
    internal static bool ShouldDeny(TurnDelegationState turn, bool alsoWhenExecutorSuppressed) =>
        IsDelegated(turn) || IsSuppressedExecutorTurn(turn, alsoWhenExecutorSuppressed);
}

/// <summary>Решение гейта по одному действию: разрешено (возможно, со списанной квотой) либо отказ с текстом.</summary>
internal sealed record DelegatedTurnGateDecision
{
    public required bool Allowed { get; init; }

    /// <summary>Текст отказа (тело 403 у REST / content-ошибка у http-тулсета); null при Allowed.</summary>
    public required string? DenyText { get; init; }

    /// <summary>Разрешение оплачено квотой «Командной реализации» — вернуть её при неудачном действии.</summary>
    public bool TeamQuotaConsumed { get; init; }

    /// <summary>Разрешение оплачено квотой цикла «до готово» — вернуть её при неудачном действии.</summary>
    public bool WorkLoopQuotaConsumed { get; init; }

    public static DelegatedTurnGateDecision Pass { get; } = new() { Allowed = true, DenyText = null };
}

/// <summary>
/// Единая точка решения анти-рекурсии делегирования для ДВУХ путей вызова: MVC-фильтра
/// <c>[DenyOnDelegatedTurn]</c> (REST: UI и stdio-ветка отката) и http-тулсетов MCP
/// (ADR-012, фаза 2): MVC-атрибут на <c>McpTransportController</c> не применяется вовсе —
/// тулсет зовёт сервисы через DI в обход конвейера фильтров, поэтому проверка обязана жить
/// в самом тулсете, а тексты и порядок — здесь, чтобы пути не разошлись.
///
/// Семантика fail-open у путей разная и это осознанно: REST-запрос без заголовка — чужой
/// клиент (фронт, интеграция), ограничение не его касается; http-вызов без вызывателя —
/// аномалия (конфиг хода кладёт заголовок всегда), и пропуск стоил бы платного цикла
/// «доклад → запуск → доклад» — <paramref name="failOpenWhenUnknown"/>=false даёт отказ.
/// </summary>
internal static class DelegatedTurnGate
{
    public static DelegatedTurnGateDecision Decide(
        SessionManager? sessions, string? ownerId, string? callerSessionId,
        string action, bool alsoWhenExecutorSuppressed,
        bool allowInTeamImplement, bool allowInWorkLoop,
        bool failOpenWhenUnknown = true)
    {
        if (string.IsNullOrEmpty(callerSessionId) || sessions is null || string.IsNullOrEmpty(ownerId))
            return failOpenWhenUnknown
                ? DelegatedTurnGateDecision.Pass
                : new DelegatedTurnGateDecision
                {
                    Allowed = false,
                    DenyText = $"{action} недоступно: вызов пришёл без сессии-вызывателя — "
                        + "отказ по построению.",
                };

        var turn = sessions.GetActiveTurnDelegation(callerSessionId, ownerId);
        var delegated = DenyOnDelegatedTurnAttribute.IsDelegated(turn);

        // Квота вместо запрета — на ЛЮБОМ неделегированном ходу штаба, а не только на
        // реакционном: обычный ход координатора запускает исполнителей той же кнопкой, и
        // мимо бюджета он не должен идти. Разрешение сразу расходует единицу — счёт ведёт
        // бэкенд в точке запуска. Вердикт NotTeamMode = чат не штаб: решает прежний запрет.
        if (!delegated && allowInTeamImplement)
        {
            var (verdict, reason) = sessions.TryConsumeTeamImplementRun(callerSessionId, ownerId);
            if (verdict == SessionManager.TeamRunQuota.Allowed)
                return new DelegatedTurnGateDecision
                    { Allowed = true, DenyText = null, TeamQuotaConsumed = true };
            if (verdict == SessionManager.TeamRunQuota.Exhausted)
                return Deny(QuotaExhaustedText(action, reason,
                    "Доложи человеку сводку и дождись его решения "
                    + "(подтвердить план, добавить бюджет или завершить итерацию)."));
        }

        // Квота вместо запрета в цикле «до готово» — на ЛЮБОМ неделегированном ходу чата
        // с циклом: ход доклада исполнителя — тот самый случай, ради которого снимается
        // запрет, а «чистый» ход не должен обходить счётчик. Вердикт NotInLoop = чат не
        // в цикле: проваливаемся в прежний запрет.
        if (!delegated && allowInWorkLoop)
        {
            var (verdict, reason) = sessions.TryConsumeWorkLoopRun(callerSessionId, ownerId);
            if (verdict == SessionManager.WorkLoopRunQuota.Allowed)
                return new DelegatedTurnGateDecision
                    { Allowed = true, DenyText = null, WorkLoopQuotaConsumed = true };
            if (verdict == SessionManager.WorkLoopRunQuota.Exhausted)
                return Deny(QuotaExhaustedText(action, reason,
                    "Доложи человеку сводку и дождись его решения "
                    + "(остановить цикл или запустить оставшиеся задачи руками)."));
        }

        if (!delegated && !DenyOnDelegatedTurnAttribute.IsSuppressedExecutorTurn(turn, alsoWhenExecutorSuppressed))
            return DelegatedTurnGateDecision.Pass;

        return Deny(delegated
            ? $"{action} недоступно на делегированном ходу: этот ход инициирован другим "
                + "чатом, и цепочка делегирования дальше не идёт. Верни результат тому, кто "
                + "тебя позвал — решение примет он или пользователь."
            : $"{action} недоступно на этом ходу: ты отвечаешь на доклад исполнителя, и "
                + "запуск новой сессии отсюда закольцевал бы «доклад → запуск → доклад». "
                + "Если запустить действительно нужно — попроси пользователя.");
    }

    /// <summary>
    /// Возврат списанных квот при неудачном действии — симметрия OnActionExecuted фильтра:
    /// отказ/исключение после TryConsume не должен жечь бюджет (404/400 запуска не сделали).
    /// </summary>
    public static void Refund(SessionManager sessions, string callerSessionId, string ownerId,
        DelegatedTurnGateDecision decision)
    {
        if (decision.TeamQuotaConsumed) sessions.RefundTeamImplementRun(callerSessionId, ownerId);
        if (decision.WorkLoopQuotaConsumed) sessions.RefundWorkLoopRun(callerSessionId, ownerId);
    }

    private static DelegatedTurnGateDecision Deny(string text) =>
        new() { Allowed = false, DenyText = text };

    private static string QuotaExhaustedText(string action, string? reason, string advice) =>
        $"{action} недоступно: {reason}. {advice}";
}
