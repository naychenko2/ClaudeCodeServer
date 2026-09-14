using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Knowledge;

// Каскадная уборка знаний при удалении пользователя: память персон (стор + Dify),
// локальные сторы заметок/команд/паспортов/БЗ проектов (через участников синка)
// и ВСЕ Dify-датасеты с префиксом «{username}:» (notes, persona:*, team:*, проекты,
// kb:*). Без каскада датасеты сиротеют, а новый пользователь с тем же именем увидел бы
// чужие базы как свои (классификация раздела «Знания» идёт по префиксу имени).
// Всё best-effort: сбои Dify логируются и не роняют удаление учётной записи. Сами
// проекты/чаты/задачи пользователя — за рамками каскада.
//
// Участники синка подкидываются через DI как `IEnumerable<IKnowledgeSyncParticipant>`
// (форвардеры в Program.cs:661-670); так каскад не держит прямых ссылок на вертикали
// Notes/Memory/Dossiers/Knowledge и остаётся частью спины при их выносе.
public sealed class UserKnowledgeCascade
{
    private readonly KnowledgeService _knowledge;
    private readonly WorkspaceKnowledgeStore _wkStore;
    private readonly IProjectManager _projects;
    private readonly IPersonaDirectory _personas;
    private readonly IReadOnlyList<IKnowledgeSyncParticipant> _participants;
    private readonly ILogger<UserKnowledgeCascade> _logger;

    public UserKnowledgeCascade(KnowledgeService knowledge, WorkspaceKnowledgeStore wkStore,
        IProjectManager projects, IPersonaDirectory personas,
        IEnumerable<IKnowledgeSyncParticipant> participants,
        ILogger<UserKnowledgeCascade> logger)
    {
        _knowledge = knowledge;
        _wkStore = wkStore;
        _projects = projects;
        _personas = personas;
        // Фиксируем порядок раз, чтобы одна и та же ошибка не повторялась дважды в логе.
        // DI по умолчанию даёт регистрации в порядке AddSingleton; перетасовка не нужна.
        _participants = participants.ToList();
        _logger = logger;
    }

    public async Task CleanupAsync(string userId, string username)
    {
        // 1. Участники синка: каждый участник чистит свой локальный стор.
        //    PersonaMemoryService здесь пройдёт по всем персонам владельца и снимет
        //    их память; сами персоны из PersonaManager НЕ удаляет — шаг 4 ниже.
        //    Dify НЕ трогаем на этом шаге (общий проход шага 3).
        foreach (var participant in _participants)
        {
            try { await participant.DeleteAllAsync(userId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Каскад юзера {User}: участник {Participant}",
                    userId, participant.GetType().Name);
            }
        }

        // 2. БЗ проектов: для корня, где владелец единственный, снимаем wkStore.
        //    ProjectKnowledgeSyncService.DeleteAllAsync уже сделал это для своих целей;
        //    здесь оставлено на случай, если устаревшая запись БЗ осталась от удалённого
        //    проекта без участника синка (после волны 5 — резервная страховка).
        foreach (var p in _projects.GetByOwner(userId))
        {
            if (_projects.GetByRootPath(p.RootPath).Any(x => x.OwnerId != userId)) continue;
            _wkStore.Delete(p.RootPath);
        }

        // 3. Dify: все датасеты с префиксом «{username}:» — включая осиротевшие со стухшими
        //    именами старых проектов. Часть уже удалена участниками синка на шаге 1
        //    (удаление Dify-датасета через knowledge.DeleteDatasetAsync), но общий
        //    проход здесь всё равно проходит — повторное удаление не найдёт датасет.
        if (_knowledge.IsConfigured)
        {
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

        // 4. Сами персоны из стора — это НЕ знание, это личный реестр пользователя;
        //    удаляются здесь, после того как память каждой персоны уже снята (шаг 1).
        foreach (var persona in _personas.GetByOwner(userId).ToList())
            _personas.Delete(persona.Id, userId);
    }
}
