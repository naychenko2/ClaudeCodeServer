using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Tasks;

// B4b/B4c (разрез docs/research/ai-fabrika-workflow-cut-2026-11.md §5 «Запуск»): фоновый подбор
// маршрута при создании эпика — общий для всех дверей создания: личной и проектной веток REST
// (TasksController) и агентского tasks_create (TasksToolset).
// Best-effort: создание эпика не ждёт модель и не роняется её отказом. RecommendAsync сам не
// бросает (сбой модели или мусор ответа — «предложения нет» с его же логом); catch здесь — только
// то, что не доехало до него (нет DI, обрыв хоста), и оно не должно стать необработанным
// исключением фона. Ключ владельца — арендатор записи (task.OwnerId), а не вызывающий: эпик общего
// проекта ставит участник, предложение пишется владельцу. Пишется только epic.WorkflowProposal (И7),
// до экрана — штатным task_changed по эпику: другого канала у фона нет.
public static class EpicWorkflowRecommend
{
    public static void Start(WorkflowRecommender? recommender, TaskManager tasks, ISessionBroadcaster broadcaster,
        ISessionDirectory? sessions, IProjectManager? projects, ILogger? log, TaskItem task)
    {
        if (recommender is not { } rec || task.Role != TaskRoles.Feature || task.OwnerId is not { } ownerId) return;
        _ = RunAsync(rec, tasks, broadcaster, sessions, projects, log, task.Id, ownerId, task.ProjectId);
    }

    private static async Task RunAsync(WorkflowRecommender rec, TaskManager tasks, ISessionBroadcaster broadcaster,
        ISessionDirectory? sessions, IProjectManager? projects, ILogger? log, string epicId, string ownerId, string? projectId)
    {
        try
        {
            if (await rec.RecommendAsync(epicId, ownerId, CancellationToken.None) is null) return;
            var epic = tasks.GetById(epicId);
            if (epic is null) return;
            await broadcaster.ToTaskBoard(ownerId, projectId, new TaskChangedMessage("updated", epic), sessions, projects);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "feature-workflow-recommend {Epic}: фоновый подбор маршрута не дошёл до записи", epicId);
        }
    }
}
