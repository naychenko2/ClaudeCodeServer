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
// Правки текста, офисных форматов, git и WebDAV здесь нет. Заметок здесь тоже нет (Х-9,
// дефект 44718bbf): путь «notes» или «notes/…» — 404 на чтении и записи, виртуальная «notes»
// листа корня проекта в личный корень не вставляется — личный vault вне файлов (ADR-040 §5).
// Загрузка файла (upload) и сохранение
// по URL (save-from-url, только в медиальные папки) — Ф-6, требование Андрея 15.11: созданный в
// ходе контент должен быть чем сохранить в «Личное».
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/personal/chats/{sessionId}/files")]
public class PersonalFilesController(ISessionDirectory directory, PersonalFolderResolver personal, FileService files)
    : ControllerBase
{
    private const string SubClaim = "sub";

    // Предел содержимого при создании файла: панель записывает текстовые файлы и диаграммы;
    // двоичное тело идёт через upload или save-from-url, у них свои пределы. Отказ той же
    // формы, что у них: 413 с размером
    private const int MaxContentBytes = 1024 * 1024; // 1 МБ

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

    // В «Личном» нет заметок (Х-9, дефект 44718bbf): их хранилище вне файлов — data/notes/{userId},
    // ADR-040 §5. Путь «notes» или «notes/…» отказывается как несуществующий — 404, а не пустой
    // список: мёртвая папка не должна быть достижима прямой ссылкой и создаваема записью.
    private static bool IsNotesPath(string? rel) =>
        rel is not null && (rel.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
                            rel.StartsWith("notes/", StringComparison.OrdinalIgnoreCase));

    private IActionResult NotesUnavailable() => Fail(StatusCodes.Status404NotFound, "notes_unavailable",
        "В «Личном» нет заметок: их хранилище вне файлов");

    // Путь нормализуем один раз на входе ручки и передаём дальше уже нормализованным: иначе
    // «  a.txt  » проходит проверку обрезанным, а на диск уходит с пробелами — Windows их молча
    // снимает, Linux оставляет в имени, и те же вызовы дают разные файлы. Остальная нормализация
    // (разделители, «..», абсолютный путь) — у Resolve/SyntaxOk ниже; Trim идемпотентен, повтор
    // внутри них безвреден и держит эти помощники самостоятельными.
    private static string Norm(string? rel) => rel?.Trim() ?? "";

    // Абсолютный путь внутри корня или null (абсолютный путь, «..», символическая ссылка, разделитель «\»,
    // недопустимые символы). Пустой путь — сам корень, если он разрешён (belowRoot: false)
    private static string? Resolve(string root, string? rel, bool belowRoot)
    {
        rel = Norm(rel);
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
        path = Norm(path);
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (IsNotesPath(path)) return NotesUnavailable();
        // Личный корень не получает виртуальную «notes» листа корня проекта (Х-9):
        // здесь нет хранилища заметок, мёртвая папка делает пустое состояние недостижимым
        return Browse(session, path, (root, p) => files.List(root, p, false, UserId ?? "", withNotesRoot: false));
    }

    [HttpGet("tree")]
    public IActionResult Tree(string sessionId, [FromQuery] string path = "")
    {
        path = Norm(path);
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (IsNotesPath(path)) return NotesUnavailable();
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
        path = Norm(path);
        if (!TryFile(sessionId, path, out var root, out var denied)) return denied;
        try { return Ok(FileContentReader.Read(files, root, path)); }
        catch (FileNotFoundException) { return NotFoundFile(); }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
    }

    // Поток для <img>/<video>/<audio>: Range включён, SVG и прочее активное содержимое — в песочнице
    [HttpGet("stream")]
    public IActionResult Stream(string sessionId, [FromQuery] string path)
    {
        path = Norm(path);
        if (!TryFile(sessionId, path, out var root, out var denied)) return denied;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "sandbox";
        return PhysicalFile(Resolve(root, path, belowRoot: true)!, FileContentReader.StreamMime(path), enableRangeProcessing: true);
    }

    [HttpGet("download")]
    public IActionResult Download(string sessionId, [FromQuery] string path)
    {
        path = Norm(path);
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
        if (IsNotesPath(path)) { denied = NotesUnavailable(); return false; }
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
        var path = Norm(req.Path);
        if (!SyntaxOk(path)) return InvalidPath();
        // notes/ не создаётся и не раскрывается: мёртвая папка не должна вернуться
        // на диск или в корень (Х-9)
        if (IsNotesPath(path)) return NotesUnavailable();
        var (root, denied) = await RootForWriteAsync(session, ct);
        if (root is null) return denied!;
        if (Resolve(root, path, belowRoot: true) is null) return InvalidPath();
        try
        {
            files.CreateDirectory(root, path);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось создать папку"); }
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateFile(string sessionId, [FromBody] CreateFileRequest req, CancellationToken ct)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        var path = Norm(req.Path);
        if (!SyntaxOk(path)) return InvalidPath();
        if (IsNotesPath(path)) return NotesUnavailable(); // notes/ не создаётся записью (Х-9)
        // Предела добиваемся до любого эффекта: тело читаем в память целиком, и файл с ним
        // не должен появиться, чтобы потом его удалить. Мера — байты UTF-8, не символы:
        // кириллица в файл пишет вдвое больше, чем «считается» по длине строки
        var content = req.Content ?? "";
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
            return Fail(StatusCodes.Status413PayloadTooLarge, "content_too_large",
                $"Содержимое больше {MaxContentBytes / (1024 * 1024)} МБ");
        var (root, denied) = await RootForWriteAsync(session, ct);
        if (root is null) return denied!;
        if (Resolve(root, path, belowRoot: true) is null) return InvalidPath();
        try
        {
            files.CreateFile(root, path, content);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        catch (InvalidOperationException) { return Fail(StatusCodes.Status409Conflict, "exists", "Файл уже существует"); }
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось создать файл"); }
    }

    // Пределы загрузки и выгрузки по URL — те же, что у проектных Upload (100 МБ) и SaveFromUrl
    // (200 МБ, ради видео). RequestSizeLimit поднимает потолок тела над дефолтным для Kestrel 30 МБ,
    // иначе отказом был бы даже разрешённый файл; сам предел файла держит подсчёт в
    // FileService.WriteAtomicFromStreamAsync (Content-Length не доверяем: его может не быть)
    private const long MaxUploadBytes = 100L * 1024 * 1024;
    private const long MaxSaveFromUrlBytes = 200L * 1024 * 1024;

    private IActionResult FileTooLarge(long limitBytes) => Fail(StatusCodes.Status413PayloadTooLarge,
        "file_too_large", $"Файл больше {limitBytes / (1024 * 1024)} МБ");

    // Загрузка файла в «Личное» (Ф-6): по образцу проектной Upload — multipart-поле file, имя через
    // Path.GetFileName (путь-сегменты в имени отсекаются), атомарная запись через
    // FileService.WriteAtomicFromStreamAsync. Журнала правок (git) здесь нет: уведомление
    // подписчиков (синк знаний) делает сам FileService. Перезапись цели разрешена, как в проектной
    // ручке (атомарная подмена, If-Match в личном маршруте нет); цель — файл, не корень.
    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(string sessionId, [FromQuery] string path = "",
        IFormFile? file = null, CancellationToken ct = default)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (file is null || file.Length == 0)
            return Fail(StatusCodes.Status400BadRequest, "no_file", "Файл не выбран или пустой");
        // Предел по объявленной длине части — до любого эффекта (temp-записи и папки):
        // RequestSizeLimit ловит тело на транспорте, здесь тот же отказ формой ручки
        if (file.Length > MaxUploadBytes) return FileTooLarge(MaxUploadBytes);
        var dir = Norm(path);
        if (dir.Length > 0 && !SyntaxOk(dir)) return InvalidPath();
        // Имя отсекаем до любого эффекта: «../evil» в имени файла — не имя
        var name = Path.GetFileName(file.FileName);
        if (name.Length == 0) return InvalidPath();
        var rel = dir.Length == 0 ? name : dir + "/" + name;
        // Путь заметок отказан до любого эффекта: загрузка не создаёт мёртвую папку (Х-9)
        if (IsNotesPath(rel)) return NotesUnavailable();
        var (root, denied) = await RootForWriteAsync(session, ct);
        if (root is null) return denied!;
        if (Resolve(root, rel, belowRoot: true) is null) return InvalidPath();
        try
        {
            await using var stream = file.OpenReadStream();
            if (!await files.WriteAtomicFromStreamAsync(root, rel, stream, MaxUploadBytes, ct))
                return FileTooLarge(MaxUploadBytes);
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        // Имя занято каталогом (или temp-имя вышло за предел имени файла): File.Move/FileStream
        // бросает IOException — тот же 409 «exists», что у create/mkdir/rename, а не 500
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось записать файл"); }
        return Ok(new { path = rel });
    }

    // Сохранение в «Личное» по URL (Ф-6): тот же SSRF-guard и стриминг, что у проектной SaveFromUrl,
    // атомарная запись — через FileService. Путь ограничен медиальными папками: в «Личное»
    // сохраняют только в папку своего вида (ADR-040 §5, PersonalMediaFolders). Путь проверяем
    // до резолва адреса и до сети: отказанной цели не ради чего скачивать тело.
    [HttpPost("save-from-url")]
    [RequestSizeLimit(200 * 1024 * 1024)] // 200 МБ
    public async Task<IActionResult> SaveFromUrl(string sessionId, [FromBody] SaveFromUrlRequest req,
        [FromServices] IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return Fail(StatusCodes.Status400BadRequest, "bad_url", "Некорректный URL");
        var rel = Norm(req.Path);
        var kind = MediaKindOf(rel);
        if (kind is null || PersonalMediaFolders.Resolve(kind, rel, out _) is not null)
            return Fail(StatusCodes.Status400BadRequest, "invalid_path",
                "В «Личное» сохраняют только в папку «images», «audio» или «video»");
        if (!await SsrfGuard.IsPubliclyRoutableAsync(uri, ct))
            return Fail(StatusCodes.Status400BadRequest, "bad_url", "URL указывает на недопустимый адрес");
        var (root, denied) = await RootForWriteAsync(session, ct);
        if (root is null) return denied!;
        if (Resolve(root, rel, belowRoot: true) is null) return InvalidPath();
        try
        {
            var client = httpClientFactory.CreateClient("safe-download");
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            // Content-Length проверяем до записи: папки ради заведомо отказанного тела не заводим
            if (resp.Content.Headers.ContentLength is > MaxSaveFromUrlBytes) return FileTooLarge(MaxSaveFromUrlBytes);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            // IOException ловится только вокруг записи: имя занято каталогом (или temp-имя вышло
            // за предел имени файла) — 409 «exists», как в Upload; сетевые сбои остаются 502
            try
            {
                if (!await files.WriteAtomicFromStreamAsync(root, rel, stream, MaxSaveFromUrlBytes, ct))
                    return FileTooLarge(MaxSaveFromUrlBytes);
            }
            catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось записать файл"); }
        }
        catch (HttpRequestException ex) { return Fail(StatusCodes.Status502BadGateway, "fetch_failed", ex.Message); }
        catch (TaskCanceledException) { return Fail(StatusCodes.Status502BadGateway, "fetch_failed", "Выгрузка не завершена: адрес не ответил или время вышло"); }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        return Ok(new { path = rel });
    }

    // Вид медиальной папки по первому сегменту пути; null — путь не в медиальной папке или
    // это не файл («images» без имени — папка, не цель записи)
    private static string? MediaKindOf(string rel)
    {
        var slash = rel.IndexOf('/');
        if (slash <= 0) return null;
        return rel[..slash] switch
        {
            PersonalMediaFolders.Images => PersonalMediaFolders.Images,
            PersonalMediaFolders.Audio => PersonalMediaFolders.Audio,
            PersonalMediaFolders.Video => PersonalMediaFolders.Video,
            _ => null,
        };
    }

    [HttpPost("rename")]
    public IActionResult Rename(string sessionId, [FromBody] RenameRequest req)
    {
        if (!TryChat(sessionId, out var session)) return ChatNotFound();
        if (RootOf(session) is not { } root) return NotFoundFile();
        var oldPath = Norm(req.OldPath);
        var newPath = Norm(req.NewPath);
        // Оба конца внутри корня и не сам корень: «Личное» не переименовать и не вынести
        if (Resolve(root, oldPath, belowRoot: true) is not { } src
            || Resolve(root, newPath, belowRoot: true) is not { } dst)
            return InvalidPath();
        // notes/ недоступна на обоих концах переноса: нельзя ни внести её обратно,
        // ни вынести её содержимое — наследную физическую папку сносит delete (Х-9)
        if (IsNotesPath(oldPath) || IsNotesPath(newPath)) return NotesUnavailable();
        if (!System.IO.File.Exists(src) && !Directory.Exists(src)) return NotFoundFile();
        if (System.IO.File.Exists(dst) || Directory.Exists(dst))
            return Fail(StatusCodes.Status409Conflict, "exists", "Файл или папка с таким именем уже есть");
        // Родителя цели нет — нет и цели: тот же 404 «нет такого пути», что у отсутствующего
        // источника, а не 409 «имя занято». Без этой проверки FileService бросает IOException
        // из VerifyParent, и он читался бы как коллизия имени. Папку при переносе не создаём:
        // перенос — не создание (create и mkdir свои недостающие предки заводят)
        if (!Directory.Exists(Path.GetDirectoryName(dst)!)) return NotFoundFile();
        // Папку нельзя перенести внутрь себя
        if (Directory.Exists(src) && IsInside(dst, src)) return InvalidPath();
        try
        {
            files.Rename(root, oldPath, newPath);
            return Ok();
        }
        catch (UnauthorizedAccessException) { return InvalidPath(); }
        catch (IOException) { return Fail(StatusCodes.Status409Conflict, "exists", "Не удалось переименовать"); }
    }

    [HttpDelete]
    [DenyOnDelegatedTurn("Удаление файлов")]
    public IActionResult Delete(string sessionId, [FromQuery] string path)
    {
        path = Norm(path);
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
        rel = Norm(rel);
        if (rel.Length == 0 || rel == "." || rel.Contains('\\') || rel.Contains('\0')) return false;
        if (rel.StartsWith('/') || Path.IsPathRooted(rel)) return false;
        return !rel.Split('/').Any(s => s == "..");
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            SafePath.ComparisonFor(OperatingSystem.IsWindows()));
}
