using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов PersonaManager для выноса Knowledge. UserKnowledgeCascade
// использует GetByOwner (для каскада при удалении пользователя) и
// Delete(id, userId) (последний шаг каскада — снять сами персоны).
public interface IPersonaDirectory
{
    IReadOnlyCollection<Persona> GetByOwner(string userId);
    void Delete(string id, string userId);
}
