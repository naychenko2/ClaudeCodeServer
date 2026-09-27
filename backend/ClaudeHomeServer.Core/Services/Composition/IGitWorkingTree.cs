using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Composition;

// Узкий шов GitService (вертикаль Git) для вертикали Files: FileService помечает файлы
// дерева по git-статусу, показывает дифф и откатывает файл. Контракт в Core, реализация —
// сам GitService; выключена подсистема Git — шва нет, и FileService идёт прежним прямым
// запуском git на хосте.
public interface IGitWorkingTree
{
    Task<GitStatusDto> StatusPathsAsync(string? ownerId, string root, CancellationToken ct = default);
    Task<string?> DiffFileVsHeadAsync(string? ownerId, string root, string relPath, CancellationToken ct = default);
    Task DiscardAsync(string? ownerId, string root, string relPath, CancellationToken ct = default);
}
