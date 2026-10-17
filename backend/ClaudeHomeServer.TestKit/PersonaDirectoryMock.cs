using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Tests.Helpers;

// Простой mock IPersonaDirectory для тестов, которым нужен был PersonaManager
// (UserKnowledgeCascade после выноса Knowledge). Делегирует в настоящий
// PersonaManager, если он передан, иначе возвращает пустоту.
public sealed class PersonaDirectoryMock : IPersonaDirectory
{
    private readonly List<Persona> _personas = [];

    public IReadOnlyCollection<Persona> GetByOwner(string userId) =>
        _personas.Where(p => p.OwnerId == userId).ToList();

    public void Delete(string id, string userId)
    {
        _personas.RemoveAll(p => p.Id == id && p.OwnerId == userId);
    }

    public void Seed(Persona persona) => _personas.Add(persona);
}