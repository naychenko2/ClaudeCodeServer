using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов на PersonaManager.GetByHandle для TriggerSources (вынос Этап 5):
// MentionTriggerSource резолвит персону по @handle из текста пользователя.
// Полный PersonaManager не нужен — только точечный резолв по handle.
public interface IPersonaHandleResolver
{
    Persona? GetByHandle(string userId, string handle, string? projectId);
}
