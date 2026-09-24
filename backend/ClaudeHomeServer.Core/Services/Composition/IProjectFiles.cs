using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.Services.Composition;

// Шов файловых операций проекта (ADR-016): асинхронный и с ключом «проект», а не корневой
// папкой. Ключ — проект, потому что у локального проекта файлы живут на устройстве, и путь
// на сервере ничего не значит: реализация сама решает, где взять корень, и для локального
// проекта отказывает (LocalProjectException) ДО обращения к диску. Асинхронность — под
// реализацию, которая ходит к файлам по сети.
//
// Семантика методов 1:1 с FileService (вертикаль Files). Событие OnMutated — то же, что
// у FileService: мутации через файловый API, аргументы — корень, относительный путь, вид,
// новый путь (только для Rename).
public interface IProjectFiles
{
    Task<IReadOnlyList<FileEntry>> ListAsync(Project project, string relativePath = "", bool showHidden = false, CancellationToken ct = default);
    Task<IReadOnlyList<FileEntry>> TreeAsync(Project project, string relativePath = "", bool showHidden = false, CancellationToken ct = default);
    Task<IReadOnlyList<FileEntry>> SearchAsync(Project project, string query, CancellationToken ct = default);

    Task<string> ReadFileAsync(Project project, string relativePath, CancellationToken ct = default);
    Task<byte[]> ReadFileBytesAsync(Project project, string relativePath, CancellationToken ct = default);
    Task WriteFileAsync(Project project, string relativePath, string content, CancellationToken ct = default);
    Task WriteFileBytesAsync(Project project, string relativePath, byte[] content, CancellationToken ct = default);
    Task CreateFileAsync(Project project, string relativePath, string content = "", CancellationToken ct = default);
    Task CreateDirectoryAsync(Project project, string relativePath, CancellationToken ct = default);
    Task DeleteAsync(Project project, string relativePath, CancellationToken ct = default);
    Task RenameAsync(Project project, string oldRelative, string newRelative, CancellationToken ct = default);

    Task<string?> GetDiffAsync(Project project, string relativePath, CancellationToken ct = default);
    Task<bool> RevertFileAsync(Project project, string relativePath, CancellationToken ct = default);

    event Action<string, string, FileMutationKind, string?>? OnMutated;
}
