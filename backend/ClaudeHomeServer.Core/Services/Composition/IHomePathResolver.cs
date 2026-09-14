using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов на UserHomeResolver для TriggerSources (вынос Этап 5):
// AutomationRootResolver резолвит домашнюю папку пользователя для file/git-триггеров.
// AppSettingsService/SandboxManager не нужны — только конечный резолв пути.
public interface IHomePathResolver
{
    string? Resolve(User? user);
}
