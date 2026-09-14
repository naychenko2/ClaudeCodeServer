using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов TaskManager для выноса Spend (Этап 5). Spend использует только
// `GetById` — резолв названия задачи для pivot-узла «Задача» и паспорта хода.
// TaskManager живёт в вертикали `Services.Tasks` (волна 4C), и прямой
// `ProjectReference` Spend → Tasks запрещён сторожем границ (`SubsystemBoundaryTests`),
// поэтому заводим шов в Core — это «вертикаль → вертикаль» через Core-интерфейс,
// как у `IKnowledgeSyncParticipant` или `IGitRefSnapshotStore`.
public interface ITaskLookup
{
    // Возвращает задачу по id или null, если такой нет. Удалённые/архивные задачи
    // не возвращаются — Spend резолвит имя как null и фронт показывает «удалено».
    TaskItem? GetById(string id);
}
