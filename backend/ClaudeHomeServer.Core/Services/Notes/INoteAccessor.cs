using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes;

// Узкий шов записи памяти в заметки (Этап 5, волна 2, разрез Memory↔Notes).
// Memory (PersonaMemoryService) использует ровно две операции: создать заметку
// из записи памяти (MemoryToNote) и проверить существование заметки по id
// (NoteToMemoryAsync). Контракт минимальный по фактическим вызовам — без
// операций редактирования, переноса, удаления (потребитель один, ему больше
// не нужно).
//
// Реализация — `NotesAccessor` (Main, тонкий форвардер на NotesService
// в вынесенной вертикали Notes). Тип `CreateNoteRequest`/`NoteDetail` уже в Core
// (Models/Note.cs), Core-сторона контракта на них опирается без новых DTO.
public interface INoteAccessor
{
    // Создать заметку с минимальным набором полей. Возвращает полную запись
    // (NoteDetail) с проставленными сервером id/timestamps.
    NoteDetail Create(string ownerId, CreateNoteRequest request);

    // Получить заметку по id. null — не найдена (или чужой владелец).
    NoteDetail? GetDetail(string ownerId, string noteId);
}