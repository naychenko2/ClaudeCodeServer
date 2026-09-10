using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов на NotesService.GetSummaries для TriggerSources (вынос Этап 5):
// NoteTriggerSource читает метаданные заметок для дифф-детекции (hash по title+tags+updatedAt).
// Полный NotesService не нужен — только чтение summary-списка.
public interface INoteSummaryReader
{
    IReadOnlyList<NoteSummary> GetSummaries(string userId, string? source, string? section);
}
