using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Llm;

// Адаптер LLM-провайдера для одной сессии. Контракт — калька публичного API ClaudeSession:
// SessionManager владеет жизненным циклом и статусами, адаптер — только общением с моделью.
// Все события наружу адаптер шлёт через callback Func<ServerMessage, Task> (LlmSessionContext.OnMessage).
public interface ILlmSessionAdapter : IAsyncDisposable
{
    Session Info { get; }

    // Возможности провайдера — по ним SessionManager и фронт скрывают недоступное
    LlmCapabilities Capabilities { get; }

    Task StartAsync();
    Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null);

    // Сворачивание контекста; при !Capabilities.SupportsCompact — no-op
    Task CompactAsync();

    // behavior: allow | deny | allow_always
    void RespondPermission(string requestId, string behavior);

    // Ответ на AskUserQuestion; при отсутствии поддержки — no-op
    void AnswerQuestion(string toolUseId, string updatedInputJson);

    // Решение по плану (ExitPlanMode); при !Capabilities.SupportsPlanMode — no-op
    void RespondPlan(string requestId, bool approve, string? feedback);

    void Interrupt();
}
