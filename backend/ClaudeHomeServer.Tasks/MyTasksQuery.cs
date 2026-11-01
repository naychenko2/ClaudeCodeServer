using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services.Tasks;

// Выдача GET /api/tasks: собственные задачи пользователя (личные и проектов, где он
// владелец) плюс ВСЕ задачи общих проектов, где у него есть право Read (S2b-3c; назначенные
// ему — частный случай). Состав совпадает с GET /api/projects/{p}/tasks. Посторонний и
// бывший участник получают пустую добавку — доступ проверяется на каждый проект через
// IProjectAccess. Только чтение, без кэша: живой стор под его блокировками.
public sealed class MyTasksQuery(TaskManager tasks, IProjectManager projects, IProjectAccess access)
{
    public IReadOnlyCollection<TaskItem> ForUser(string userId)
    {
        var result = tasks.GetByOwner(userId).ToList();
        var seen = result.Select(t => t.Id).ToHashSet();

        foreach (var project in projects.GetAll())
        {
            if (project.OwnerId == userId) continue; // свои проекты уже в GetByOwner
            if (access.Resolve(userId, project.Id, ProjectRight.Read) is null) continue; // не участник / отозван

            result.AddRange(tasks.GetByProject(project.Id)
                .Where(t => t.OwnerId == project.OwnerId && seen.Add(t.Id)));
        }

        return result
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt)
            .ToList();
    }
}
