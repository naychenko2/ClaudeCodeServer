using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов на TaskManager.GetByOwner для TriggerSources (вынос Этап 5):
// TaskStatusTriggerSource читает текущие статусы задач для дифф-детекции.
// Полный TaskManager не нужен — только чтение по владельцу.
public interface ITaskStatusReader
{
    IReadOnlyCollection<TaskItem> GetByOwner(string userId);
}
