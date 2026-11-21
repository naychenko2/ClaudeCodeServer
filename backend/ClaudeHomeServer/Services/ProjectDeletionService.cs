using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Единая уборка проекта: её зовут и ручка DELETE /api/projects/{id}, и каскад удаления ребёнка.
// Владелец везде — p.OwnerId, а не вызывающий: в каскаде вызывающий — админ или опекун.
// Всё после стора — best-effort: сбой звена пишется в лог и наружу не выходит.
// Сессии проекта сервис НЕ удаляет (осознанно), сносит только их собственные worktree.
public class ProjectDeletionService(
    ProjectManager projects, SessionManager sessions, TaskManager tasks, TaskExecutionService executor,
    TeamMemoryService teamMemory, IDossierProjectCleaner dossiers, KnowledgeService knowledge,
    WorkspaceKnowledgeStore wkStore, PersonaManager personas, PersonaMemoryService personaMemory,
    GitService git, IHubContext<SessionHub> hub, ILogger<ProjectDeletionService> logger,
    INoteSemanticIndex? notesKb = null,
    IProjectTrainingPhotoCleaner? trainingPhotos = null,
    ReviewTreePool? reviewPool = null)
{
    public async Task<bool> DeleteAsync(Project p)
    {
        var id = p.Id;
        var ownerId = p.OwnerId!;
        // W4-HOLD-N (хвост R-N п.3): страховка до зачистки задач: удалённый вместе с проектом
        // эпик на паузе больше ни «снялся», ни «встал» — его шаги остались бы с замороженным
        // сроком навсегда. Чистим все двери удаления до DeleteByProject.
        foreach (var t in tasks.GetByOwner(ownerId).Where(t => t.ProjectId == id))
            executor.RestoreFrozenChatsBeforeDelete(t);
        var deleted = projects.Delete(id);
        tasks.DeleteByProject(id);
        // Память команды проекта: локальные сторы + Dify-датасет (best-effort — уборка не должна ронять удаление)
        try { await teamMemory.DeleteProjectTeamMemoryAsync(ownerId, id); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось убрать память команды проекта {ProjectId}", id); }
        // Паспорта изменений проекта: локальный стор + Dify-датасет (best-effort, тот же образец)
        try { await dossiers.DeleteProjectDossiersAsync(ownerId, id); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось убрать паспорта проекта {ProjectId}", id); }
        // Обучающие фото персонажей для LoRA (биометрия): шов Core, нет подсистемы ImageEditor — пропуск
        try { trainingPhotos?.PurgeProject(ownerId, id); }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось убрать обучающие фото проекта {ProjectId}", id); }

        // Worktree чатов проекта: сессии при удалении проекта НЕ удаляются (осознанно),
        // но их деревья без проекта — мусор на диске и записи в .git/worktrees главной репы.
        // Сносим только СВОИ деревья (WorktreeOwned): чужие/общие (привязанные) и старые (без
        // признака) не трогаем, если на тот же нормализованный путь не ссылается другой живой чат.
        // Деревья локального проекта живут на устройстве — серверному git до них не дотянуться
        try
        {
            foreach (var s in sessions.GetByProject(id).Where(s => s.WorktreePath is not null
                         && ProjectCapabilityGuard.Allows(p, ProjectCapabilityArea.FileBound)).ToList())
            {
                var wt = s.WorktreePath!;
                var removeTree = s.WorktreeOwned
                    && !sessions.GetAll().Any(e => e.Id != s.Id && e.WorktreePath is string other
                        && WorkspaceKnowledgeStore.NormalizePath(other) == WorkspaceKnowledgeStore.NormalizePath(wt));
                if (removeTree)
                {
                    try { await git.WorktreeRemoveAsync(p.OwnerId, p.RootPath, wt, force: true); }
                    catch { /* дерево могло быть удалено руками — запись подчистит prune */ }
                }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Worktree чатов проекта {ProjectId} не сняты", id); }

        // Слоты ревью волн проекта (detached-деревья рядом с деревьями чатов): сносим все разом —
        // они покрывают и слоты снесённых выше деревьев волн, и осиротевшие. Локальному проекту
        // слоты не выдаются, а серверный git до его дерева не дотянется
        if (ProjectCapabilities.FilesOnServer(p) && reviewPool is not null)
        {
            try { await reviewPool.RemoveSlotsOfProjectAsync(p.OwnerId, p); }
            catch (Exception ex) { logger.LogWarning(ex, "Слоты ревью проекта {ProjectId} не снесены", id); }
        }

        // База знаний проекта: Dify-датасет + запись WorkspaceKnowledge. Датасет общий для
        // проектов в одной папке — чистим, только если RootPath больше никем не используется
        if (ProjectCapabilities.KnowledgeRoot(p) is { } deletedKnowledgeRoot)
        {
            try { await DropKnowledgeIfOrphanAsync(deletedKnowledgeRoot, ownerId); }
            catch (Exception ex) { logger.LogWarning(ex, "Не удалена база знаний проекта {ProjectId}", id); }
        }

        // Заметки notes/ проекта выпали из alive-set — вычистить их из «{user}:notes» сразу,
        // не дожидаясь следующей несвязанной правки заметок
        try { notesKb?.QueueSync(ownerId); }
        catch (Exception ex) { logger.LogWarning(ex, "Не поставлена пересинхронизация заметок владельца проекта {ProjectId}", id); }

        // Проектные персоны осиротели вместе с проектом — каскад: память (стор + Dify-датасет),
        // сама персона (файлы сабагента снимет OnPersonaDeleted), событие фронту
        try
        {
            foreach (var persona in personas.GetByOwner(ownerId)
                         .Where(x => PersonaZone.IsProjectTeam(x, id)).ToList())
            {
                try { await personaMemory.DeletePersonaAsync(persona.Id); }
                catch { /* память персоны — best-effort */ }
                personas.Delete(persona.Id, ownerId);
                await hub.Clients.Group("user_" + ownerId)
                    .SendAsync("message", new PersonasChangedMessage("deleted", persona.Id));
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Каскад проектных персон проекта {ProjectId} не завершён", id); }

        return deleted;
    }

    // База знаний серверной папки: Dify-датасет + запись WorkspaceKnowledge. Датасет общий для
    // проектов в одной папке — чистим, только если папка больше никем не используется.
    // notifyOwnerId — кому уходит событие «датасет удалён».
    public async Task DropKnowledgeIfOrphanAsync(string knowledgeRoot, string notifyOwnerId)
    {
        if (projects.GetByRootPath(knowledgeRoot).Count > 0) return;
        var wk = wkStore.GetByPath(knowledgeRoot);
        if (wk is null) return;
        if (!string.IsNullOrEmpty(wk.DifyDatasetId))
        {
            try { await knowledge.DeleteDatasetAsync(wk.DifyDatasetId); }
            catch { /* датасет мог быть удалён в Dify — снимаем только запись */ }
            await hub.Clients.Group("user_" + notifyOwnerId)
                .SendAsync("message", new KnowledgeChangedMessage("deleted", wk.DifyDatasetId));
        }
        wkStore.Delete(knowledgeRoot);
    }
}
