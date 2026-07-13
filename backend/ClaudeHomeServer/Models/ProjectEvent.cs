namespace ClaudeHomeServer.Models;

// Событие проектного лога — append-only хроника того, что команда (персоны/пользователь/
// система) делает в рамках проекта. Источник для активность-ленты командного центра (①-L1),
// дайджеста (②-2.2) и прозрачности фоновой работы (②-2.4). Хранится в SQLite
// (data/project-events.db) — первая подсистема на SQLite (растущий лог плохо ложится на JSON).
//
// Type — строка из фиксированного набора (см. ProjectEventTypes): chat_turn, task_created,
// task_completed, task_spawned, task_deleted, memory_learned, knowledge_changed, note_changed,
// team_joined, team_left, meeting, pipeline и т.п. — намеренно открытый, чтобы не плодить enum.
// Actor — personaId / persona «Роль (Имя)» / "user" / "system".
// EntityRef — опциональная ссылка на сущность (id сессии/задачи/памяти/датасета) для диплинка.
public class ProjectEvent
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTime Ts { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? EntityRef { get; set; }
}

// Известные типы событий проектного лога (строковые константы Type). Расширяемо.
public static class ProjectEventTypes
{
    public const string ChatTurn = "chat_turn";              // ход чата завершён
    public const string TaskCreated = "task_created";        // задача создана
    public const string TaskCompleted = "task_completed";    // задача завершена (done)
    public const string TaskSpawned = "task_spawned";        // спавнен следующий экземпляр регулярной
    public const string TaskDeleted = "task_deleted";        // задача удалена
    public const string MemoryLearned = "memory_learned";    // персона запомнил факт (autolearn)
    public const string KnowledgeChanged = "knowledge_changed"; // изменение в базе знаний проекта
    public const string NoteChanged = "note_changed";        // заметка проекта создана/изменена
    public const string TeamJoined = "team_joined";          // член команды добавлен (проектная персона)
    public const string TeamLeft = "team_left";              // член команды удалён
    public const string Meeting = "meeting";                 // совещание команды
    public const string Pipeline = "pipeline";               // конвейер ролей
}
