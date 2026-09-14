using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Адаптер IPersonaDirectory (Core) поверх PersonaManager (Main). Тонкая обёртка:
// GetByOwner — прямой вызов; Delete — обёртка вокруг `bool Delete(string id, string userId)`,
// которая убирает персону и возвращает bool-флаг успеха (вне контракта IPersonaDirectory).
//
// Создан ради выноса Memory в отдельный .csproj (Этап 5): PersonaMemoryService.DeleteAllAsync
// (каскад удаления владельца) использует оба метода. Прямая ссылка на PersonaManager
// запрещена в вынесенной вертикали.
public sealed class PersonaDirectoryAdapter(PersonaManager personas) : IPersonaDirectory
{
    public IReadOnlyCollection<Persona> GetByOwner(string userId) =>
        personas.GetByOwner(userId);

    public void Delete(string id, string userId) =>
        personas.Delete(id, userId);
}
