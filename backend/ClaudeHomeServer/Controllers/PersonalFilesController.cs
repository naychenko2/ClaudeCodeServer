using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// «Мои файлы» личного чата (ADR-040 Ф2-9): просмотр и правка папки «Личного» ({home}/Personal).
// Отдельный личный маршрут, а не api/projects/{id}/files: «Личное» в ProjectManager нет, GetById его
// не отдаёт (fail-closed). Владелец — владелец ЧАТА (session.OwnerId), id пользователя из запроса
// сверяется только в SessionAccess; чужой и администратор получают 404, как у остальных личных ручек.
// Корень — PersonalFolderResolver.Find: GET папку не создаёт, без неё список пуст. Создают папку
// только mkdir и create. Сервисы те же, что у проектных файлов (FileService, ProjectLinkGuard,
// FileContentReader); путь обязан быть относительным и не идти через символическую ссылку.
// Загрузки, правки текста, офисных форматов, git и WebDAV здесь нет (вне объёма Ф2-9).
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/personal/chats/{sessionId}/files")]
public class PersonalFilesController(ISessionDirectory directory, PersonalFolderResolver personal, FileService files)
    : ControllerBase
{
    private const string SubClaim = "sub";

    private string? UserId => User.FindFirstValue(SubClaim);

    // Чат вне проекта и виден вызывающему (владелец чата). Иначе — отказ неотличим от «нет чата»
    private bool TryChat(string sessionId, [NotNullWhen(true)] out Session? session)
    {
        session = directory.GetById(sessionId);
        if (session is not null && session.ProjectId is null && SessionAccess.CanSee(session, null, UserId)) return true;
        session = null;
        return false;
    }

    private IActionResult ChatNotFound() => Fail(StatusCodes.Status404NotFound, "chat_not_found", "Чат не найден");

    private ObjectResult Fail(int status, string code, string error) => StatusCode(status, new { error, code });

    private IActionResult InvalidPath() => Fail(StatusCodes.Status400BadRequest, "invalid_path", "Недопустимый путь");

    private IActionResult NotFoundFile() => Fail(StatusCodes.Status404NotFound, "file_not_found", "Файл не найден");

    // Абсолютный путь внутри корня или null (абсолютный путь, «..», символическая ссылка, разделитель «\»,
    // недопустимые символы). Пустой путь — сам корень, если он разрешён (belowRoot: false)
    private static string? Resolve(string root, string? rel, bool belowRoot)
    {
        rel = rel?.Trim() ?? "";
        if (rel.Contains('\\')) return null;
        if (rel.Length == 0 || rel == ".") return belowRoot ? null : root;
        string? full;
        try { full = ProjectLinkGuard.ResolveInside(root, rel); }
        catch (ArgumentException) { return null; }
        if (full is null) return null;
        return belowRoot && IsRoot(root, full) ? null : full;
    }

    private static bool IsRoot(string root, string full) =>
        string.Equals(
            full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            SafePath.ComparisonFor(OperatingSystem.IsWindows()));

    // Корень «Личного» без побочных эффектов; null — папки ещё нет
    private string? RootOf(Session session)
    {
        var root = personal.Find(session.OwnerId)?.RootPath;
        if (root is not null) files.RestrictRoot(root);
        return root;
    }

    [HttpGet]
    public IActionResult List(string sessionId, [FromQuery] string path = "")
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        return Browse(session, path, (root, p) => files.List(root, p, false, UserId ?? ""));
    }

    [HttpGet("tree")]
    public IActionResult Tree(string sessionId, [FromQuery] string path = "")
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        return Browse(session, path, (root, p) => files.Tree(root, p, false, UserId ?? ""));
    }

    [HttpGet("search")]
    public IActionResult Search(string sessionId, [FromQuery] string q = "")
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (RootOf(session) is not { } root) return Ok(Array.Empty<FileEntry>());
        return Ok(files.Search(root, q, UserId ?? "").ToList());
    }

    private IActionResult Browse(Session session, string path, Func<string, string, IEnumerable<FileEntry>> read)
    {
        // Без папки «Личного» файлов нет: пустой корень, любой другой путь — 404; папка не создаётся
        if (RootOf(session) is not { } root)
            return string.IsNullOrWhiteSpace(path) ? Ok(Array.Empty<FileEntry>()) : NotFoundFile();
        if (Resolve(root, path, belowRoot: false) is null) return InvalidPath();
        try { return Ok(read(root, path).ToList()); }
        catch (DirectoryNotFoundException) { return NotFoundFile(); }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
    }

    // Содержимое для предпросмотра: тот же вид, что у проектного files/content
    [HttpGet("content")]
    public IActionResult GetContent(string sessionId, [FromQuery] string path)
    {
        if (!TryFile(sessionId, path, out var root, out var denied)) return denied;
        try { return Ok(FileContentReader.Read(files, root, path)); }
        catch (FileNotFoundException) { return NotFoundFile(); }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
    }

    // Поток для <img>/<video>/<audio>: Range включён, SVG и прочее активное содержимое — в песочнице
    [HttpGet("stream")]
    public IActionResult Stream(string sessionId, [FromQuery] string path)
    {
        if (!TryFile(sessionId, path, out var root, out var denied)) return denied;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox";
        return PhysicalFile(Resolve(root, path, belowRoot: true)!, FileContentReader.StreamMime(path), enableRangeProcessing: true);
    }

    [HttpGet("download")]
    public IActionResult Download(string sessionId, [FromQuery] string path)
    {
        if (!TryFile(sessionId, path, out var root, out var denied)) return denied;
        var full = Resolve(root, path, belowRoot: true)!;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true);
    }

    // Общая проверка читающих ручек по одному файлу: чат, папка, путь внутри корня, файл существует
    private bool TryFile(string sessionId, string? path, [NotNullWhen(true)] out string? root,
        [NotNullWhen(false)] out IActionResult? denied)
    {
        root = null;
        if (!TryChat(sessionId, out var session)) { denied = ChatNotFound(); return false; }
        if (RootOf(session) is not { } folder) { denied = NotFoundFile(); return false; }
        if (Resolve(folder, path, belowRoot: true) is not { } full) { denied = InvalidPath(); return false; }
        if (!System.IO.File.Exists(full)) { denied = NotFoundFile(); return false; }
        root = folder;
        denied = null;
        return true;
    }

    // Первая запись создаёт папку «Личного» лениво; не вышло — отказ без побочных эффектов
    private async Task<(string? Root, IActionResult? Denied)> RootForWriteAsync(Session session, CancellationToken ct)
    {
        if (RootOf(session) is { } root) return (root, null);
        var created = await personal.EnsureAsync(session.OwnerId, ct);
        if (created is not null) files.RestrictRoot(created.RootPath);
        return created is { } project
            ? (project.RootPath, null)
            : (null, Fail(StatusCodes.Status409Conflict, PersonalFolderResolver.UnavailableCode, PersonalFolderResolver.UnavailableText));
    }

    [HttpPost("mkdir")]
    public async Task<IActionResult> CreateDir(string sessionId, [FromBody] PathRequest req, CancellationToken ct)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        // Путь проверяем до создания папки «Личного»: отказ запроса не оставляет следов на диске
        if (!SyntaxOk(req.Path)) return InvalidPath();
        var (root, denied) = await RootForWriteAsync(session, ct);
        if (root is null) return denied!;
        if (Resolve(root, req.Path, belowRoot: true) is null) return InvalidPath();
        try
        {
            files.CreateDirectory(root, req.Path);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось создать папку"); }
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateFile(string sessionId, [FromBody] CreateFileRequest req, CancellationToken ct)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (!SyntaxOk(req.Path)) return InvalidPath();
        var (root, denied) = await RootForWriteAsync(session, ct);
        if (root is null) return denied!;
        if (Resolve(root, req.Path, belowRoot: true) is null) return InvalidPath();
        try
        {
            files.CreateFile(root, req.Path, req.Content ?? "");
            return Ok();
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        catch (InvalidOperationException) { return Fail(StatusCodes.Status409Conflict, "exists", "Файл уже существует"); }
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось создать файл"); }
    }

    [HttpPost("rename")]
    public IActionResult Rename(string sessionId, [FromBody] RenameRequest req)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (RootOf(session) is not { } root) return NotFoundFile();
        // Оба конца внутри корня и не сам корень: «Личное» не переименовать и не вынести
        if (Resolve(root, req.OldPath, belowRoot: true) is not { } src
            || Resolve(root, req.NewPath, belowRoot: true) is not { } dst)
            return InvalidPath();
        if (!System.IO.File.Exists(src) && !Directory.Exists(src)) return NotFoundFile();
        if (System.IO.File.Exists(dst) || Directory.Exists(dst))
            return Fail(StatusCodes.Status409Conflict, "exists", "Файл или папка с таким именем уже есть");
        // Папку нельзя перенести внутрь себя
        if (Directory.Exists(src) && IsInside(dst, src)) return InvalidPath();
        try
        {
            files.Rename(root, req.OldPath, req.NewPath);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось переименовать"); }
    }

    [HttpDelete]
    [DenyOnDelegatedTurn("Удаление файлов")]
    public IActionResult Delete(string sessionId, [FromQuery] string path)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (RootOf(session) is not { } root) return NotFoundFile();
        if (Resolve(root, path, belowRoot: true) is not { } full) return InvalidPath();
        if (!System.IO.File.Exists(full) && !Directory.Exists(full)) return NotFoundFile();
        try
        {
            files.Delete(root, path);
            return NoContent();
        }
        catch (FileNotFoundException) { return NotFoundFile(); }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
    }

    // Лексическая проверка пути до создания папки «Личного»: без диска и без корня
    private static bool SyntaxOk(string? rel)
    {
        rel = rel?.Trim() ?? "";
        if (rel.Length == 0 || rel == "." || rel.Contains('\\') || rel.Contains('\0')) return false;
        if (rel.StartsWith('/') || Path.IsPathRooted(rel)) return false;
        return !rel.Split('/').Any(s => s == "..");
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            SafePath.ComparisonFor(OperatingSystem.IsWindows()));
}
