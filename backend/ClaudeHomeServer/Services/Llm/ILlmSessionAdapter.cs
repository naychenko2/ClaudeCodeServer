using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Состояние делегирования идущего хода сессии — то, что бэкенду нужно знать, чтобы гейтить
// действия MCP-серверов, не трогая состав их инструментов (SessionManager.GetActiveTurnDelegation)
public readonly record struct TurnDelegationState(int AgentDepth, bool ExecutorSuppressed);

// Адаптер LLM-провайдера для одной сессии. Контракт — калька публичного API ClaudeSession:
// SessionManager владеет жизненным циклом и статусами, адаптер — только общением с моделью.
// Все события наружу адаптер шлёт через callback Func<ServerMessage, Task> (LlmSessionContext.OnMessage).
public interface ILlmSessionAdapter : IAsyncDisposable
{
    Session Info { get; }

    // Возможности провайдера — по ним SessionManager и фронт скрывают недоступное
    LlmCapabilities Capabilities { get; }

    // Глубина делегирования идущего хода (0 — обычный ход, >= 1 — ход пришёл через chats_send).
    // Между ходами — 0. По ней бэкенд гейтит действия MCP-серверов (DenyOnDelegatedTurnAttribute):
    // состав инструментов от хода зависеть не должен, иначе процесс CLI перезапускается.
    int CurrentTurnAgentDepth { get; }

    // Идущий ход — реакционный авто-ход постановщика на доклад делегированной задачи:
    // запуск исполнителя на нём запрещён даже при agentDepth = 0 (см. suppressTasksExecute).
    bool CurrentTurnSuppressTasksExecute { get; }

    // Есть ли у адаптера живой прогон CLI. По нему SessionManager отличает честно занятый
    // чат от зависшего в Working/Waiting без хода (см. Interrupt: реанимация мёртвого статуса).
    bool HasLiveTurn { get; }

    Task StartAsync();
    // agentDepth > 0 — ход инициирован агентом из другой сессии (chats_send):
    // адаптер урезает инструменты делегирования на этот ход, чтобы не допустить рекурсию агентов.
    // suppressTasksExecute — реакционный авто-ход постановщика на доклад делегированной задачи:
    // tasks_run_executor недоступен на этот ход даже при agentDepth=0 (см. TaskExecutionService.ReportToDelegatorAsync)
    Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null, int agentDepth = 0,
        bool suppressTasksExecute = false);

    // Сворачивание контекста; при !Capabilities.SupportsCompact — no-op
    Task CompactAsync();

    // behavior: allow | deny | allow_always
    void RespondPermission(string requestId, string behavior);

    // Ответ на AskUserQuestion; при отсутствии поддержки — no-op
    void AnswerQuestion(string toolUseId, string updatedInputJson);

    // Решение по плану (ExitPlanMode); при !Capabilities.SupportsPlanMode — no-op
    void RespondPlan(string requestId, bool approve, string? feedback);

    // Смена режима прав на лету у живого процесса (control-протокол set_permission_mode):
    // новый режим подхватывается уже идущим ходом. true — запрос отправлен живому процессу;
    // false — процесса нет, режим применится со следующего хода (пересоздание с --permission-mode).
    bool TrySetPermissionModeLive(ClaudeMode mode);

    // Смена модели на лету у живого процесса (control-протокол set_model): новая модель
    // применяется к последующим round-trip'ам идущего хода. Только родной Claude (для
    // сторонних провайдеров модель едет в env процесса — там смена лишь со следующего хода).
    // true — запрос отправлен живому процессу; false — процесса нет.
    bool TrySetModelLive(string model);

    void Interrupt();
}
