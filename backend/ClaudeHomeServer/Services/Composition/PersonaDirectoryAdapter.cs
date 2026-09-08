using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Реализация IPersonaDirectory (Core) поверх PersonaManager.
// Тонкая обёртка для UserKnowledgeCascade: только два метода,
// реально вызываемые при каскадном удалении пользователя.
public sealed class PersonaDirectoryAdapter(PersonaManager personas) : IPersonaDirectory
{
    public IReadOnlyCollection<Persona> GetByOwner(string userId) =>
        personas.GetByOwner(userId);

    public void Delete(string id, string userId) =>
        personas.Delete(id, userId);
}
