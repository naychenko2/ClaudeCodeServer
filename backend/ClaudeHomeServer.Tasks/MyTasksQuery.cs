using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Access;

namespace ClaudeHomeServer.Services.Tasks;

// Выдача «мои задачи» и календаря (S2b-3a): собственные задачи пользователя (личные и
// проектов, где он владелец) плюс задачи ОБЩИХ проектов, исполнитель которых — он
// (TaskManager.ResolveMeUserId). Чужие задачи участников и владельца не попадают;
// посторонний (не участник) и бывший участник получают пустую добавку — доступ
// проверяется на каждый проект через IProjectAccess (Read: читателю назначить нельзя,
// но если назначено — видит своё). Без кэша: читает живой стор под его блокировками.
public sealed class MyTasksQuery(TaskManager tasks, IProjectManager projects, IProjectAccess access)
{
    public IReadOnlyCollection<TaskItem> ForUser(string userId)
    {
        var result = tasks.GetByOwner(userId).ToList();

        foreach (var project in projects.GetAll())
        {
            if (project.OwnerId == userId) continue; // свои проекты уже в GetByOwner
            if (project.Members?.Any(m => m.UserId == userId) != true) continue;
            if (access.Resolve(userId, project.Id, ProjectRight.Read) is null) continue;

            result.AddRange(tasks.GetByProject(project.Id)
                .Where(t => t.OwnerId == project.OwnerId
                            && TaskManager.ResolveMeUserId(t) == userId));
        }

        return result
            .OrderBy(t => t.DueDate ?? "9999").ThenBy(t => t.CreatedAt)
            .ToList();
    }
}
