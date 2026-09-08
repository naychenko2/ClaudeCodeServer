using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Узкий шов ProjectEventLogService для выноса Notes (Этап 5, волна E).
// NotesService пишет ProjectEventTypes.NoteChanged при мутациях заметок
// (`Append(sourceKey, userId, ProjectEventTypes.NoteChanged, "user", …)`);
// остальные виды событий Notes не интересуют. Полный сервис в Main —
// журнал событий проектов для админки/аналитики; Notes использует только
// одну операцию.
public interface IProjectEventLogService
{
    // Записать событие в журнал проекта; null если запись не удалась
    // (нет владельца/проекта) — вызывающий глотает, журнал не критичен.
    ProjectEvent? Append(string projectId, string ownerId, string type, string actor,
        string summary, string? entityRef = null);
}
