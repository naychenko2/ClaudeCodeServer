using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Notes;

// Тонкий адаптер `INoteAccessor` → `NotesService` (Этап 5, волна 2).
// Notes — вынесенная вертикаль с собственным `NotesService` (полный API заметок:
// CRUD, граф, теги, обратные ссылки). Форвардер в Main режет API до двух методов,
// реально нужных Memory (PersonaMemoryService.MemoryToNote/NoteToMemoryAsync).
public sealed class NotesAccessor(NotesService notes) : INoteAccessor
{
    public NoteDetail Create(string ownerId, CreateNoteRequest request) =>
        notes.Create(ownerId, request);

    public NoteDetail? GetDetail(string ownerId, string noteId) =>
        notes.GetDetail(ownerId, noteId);
}