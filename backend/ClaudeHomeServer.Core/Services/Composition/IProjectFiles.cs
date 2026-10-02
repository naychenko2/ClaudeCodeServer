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

    // Отдача потоком (ADR-016, сторож G7): большой файл не читается в память целиком.
    // Поток открыт на чтение с разделением записи и удаления — правка файла харнесом во
    // время отдачи не падает. Закрывает поток вызывающий.
    // Содержимое для панели файлов: текст, картинка/документ base64 или метаданные бинарника.
    Task<FileContentView> GetContentAsync(Project project, string relativePath, CancellationToken ct = default);

    Task<ProjectFileStream> OpenReadAsync(Project project, string relativePath, CancellationToken ct = default);

    event Action<string, string, FileMutationKind, string?>? OnMutated;

    // Сообщить подписчикам OnMutated о записи мимо этого шва: редактор картинок пишет
    // персонажей и сохранения сам (ADR-018 §10.1). Сбой подписчика операцию не роняет.
    void NotifyMutated(string root, string relativePath, FileMutationKind kind);
}

/// <summary>Открытый на чтение файл проекта: поток и его длина на момент открытия.</summary>
public sealed record ProjectFileStream(Stream Content, long Length) : IAsyncDisposable, IDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
    public void Dispose() => Content.Dispose();
}
