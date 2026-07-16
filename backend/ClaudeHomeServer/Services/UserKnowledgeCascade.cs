namespace ClaudeHomeServer.Services;

// Каскадная уборка знаний при удалении пользователя: персоны (память: стор + Dify-датасет),
// локальные сторы (notes-knowledge, team-memory*, workspace-knowledge) и ВСЕ Dify-датасеты
// с префиксом «{username}:» (notes, persona:*, team:*, проекты, kb:*). Без каскада датасеты
// сиротеют, а новый пользователь с тем же именем увидел бы чужие базы как свои (классификация
// раздела «Знания» идёт по префиксу имени). Всё best-effort: сбои Dify логируются и не роняют
// удаление учётной записи. Сами проекты/чаты/задачи пользователя — за рамками каскада.
public sealed class UserKnowledgeCascade
{
    private readonly KnowledgeService _knowledge;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _personaMemory;
    private readonly TeamMemoryService _teamMemory;
    private readonly NotesKnowledgeService _notesKb;
    private readonly ILogger<UserKnowledgeCascade> _logger;

    public UserKnowledgeCascade(KnowledgeService knowledge, WorkspaceKnowledgeStore wkStore,
        ProjectManager projects, PersonaManager personas, PersonaMemoryService personaMemory,
        TeamMemoryService teamMemory, NotesKnowledgeService notesKb, ILogger<UserKnowledgeCascade> logger)
    {
        _knowledge = knowledge;
        _wkStore = wkStore;
        _projects = projects;
        _personas = personas;
        _personaMemory = personaMemory;
        _teamMemory = teamMemory;
        _notesKb = notesKb;
        _logger = logger;
    }

    public async Task CleanupAsync(string userId, string username)
    {
        // Персоны пользователя: память (стор + датасет) и сами персоны
        foreach (var persona in _personas.GetByOwner(userId).ToList())
        {
            try { await _personaMemory.DeletePersonaAsync(persona.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "Каскад юзера {User}: память персоны {Persona}", userId, persona.Id); }
            _personas.Delete(persona.Id, userId);
        }

        // Локальные сторы: индекс заметок, память команд, базы знаний проектов
        _notesKb.DeleteUser(userId);
        _teamMemory.DeleteOwnerTeamMemory(userId);
        foreach (var p in _projects.GetByOwner(userId))
        {
            // Папку может делить проект другого владельца — тогда запись знаний не трогаем
            if (_projects.GetByRootPath(p.RootPath).Any(x => x.OwnerId != userId)) continue;
            _wkStore.Delete(p.RootPath);
        }

        // Dify: все датасеты с префиксом «{username}:» — включая осиротевшие со стухшими
        // именами старых проектов. Часть уже удалена выше (персоны) — повторное удаление
        // просто не найдёт датасет.
        if (!_knowledge.IsConfigured) return;
        try
        {
            foreach (var d in await _knowledge.ListDatasetsAsync())
            {
                if (!(d.Name ?? "").StartsWith(username + ":", StringComparison.OrdinalIgnoreCase)) continue;
                try { await _knowledge.DeleteDatasetAsync(d.Id); }
                catch (Exception ex) { _logger.LogWarning(ex, "Каскад юзера {User}: датасет {Name}", userId, d.Name); }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Каскад юзера {User}: список датасетов Dify", userId); }
    }
}
