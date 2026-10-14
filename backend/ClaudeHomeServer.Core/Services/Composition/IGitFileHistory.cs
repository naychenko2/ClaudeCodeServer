using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов GitService (вертикаль Git) для вертикали Flows: последний коммит файла — подсказка
// «откуда изменение» на экране повторного подтверждения процесса проекта (ADR-025, F2-5).
// Контракт в Core, реализация — сам GitService; выключена подсистема Git — шва нет, и экран
// просто без подсказки.
public interface IGitFileHistory
{
    // Последний коммит, менявший файл (log --follow -n 1); null — не репозиторий, файла нет в истории
    // или git отказал
    Task<GitLogEntry?> LastCommitAsync(string? ownerId, string root, string relPath, CancellationToken ct = default);
}
