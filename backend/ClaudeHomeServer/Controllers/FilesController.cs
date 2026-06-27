using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/files")]
public class FilesController(FileService files, ProjectManager projects, SyncService sync, IConfiguration config) : ControllerBase
{
    private ClaudeHomeServer.Models.Project GetProject(string projectId) =>
        projects.GetById(projectId) ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

    private string GetRoot(string projectId) => GetProject(projectId).RootPath;

    // Проставляет состояние синхронизации (direct/inherited/null) каждой записи
    private IEnumerable<FileEntry> Annotate(string projectId, IEnumerable<FileEntry> entries) =>
        entries.Select(e => e with { Synced = sync.GetSyncState(projectId, e.Path) });

    [HttpGet]
    public IActionResult List(string projectId, [FromQuery] string path = "")
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(Annotate(projectId, files.List(p.RootPath, path, p.ShowHiddenFiles)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpGet("tree")]
    public IActionResult Tree(string projectId, [FromQuery] string path = "")
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(Annotate(projectId, files.Tree(p.RootPath, path, p.ShowHiddenFiles)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpGet("search")]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        try { return Ok(Annotate(projectId, files.Search(GetRoot(projectId), q))); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("content")]
    public IActionResult GetContent(string projectId, [FromQuery] string path)
    {
        try
        {
            var root = GetRoot(projectId);

            // Просматриваемые документы (pdf/docx/xlsx) — отдаём base64 для клиентского рендеринга
            var doc = files.GetDocumentInfo(path);
            if (doc is { } d)
            {
                var size = files.GetFileSize(root, path);
                // Слишком большой документ — только метаданные + скачивание, без base64
                var docBase64 = size > FileService.MaxDocumentBytes ? null : files.GetFileBase64(root, path);
                return Ok(new { content = (string?)null, isBinary = true, isImage = false,
                    isDocument = true, docKind = d.Kind, mimeType = d.Mime, base64 = docBase64, fileSize = size });
            }

            if (files.IsBinaryFile(root, path))
            {
                if (files.IsImageFile(root, path))
                {
                    var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLower();
                    var mime = ext == "svg" ? "image/svg+xml" : $"image/{ext}";
                    return Ok(new { content = (string?)null, isBinary = true, isImage = true,
                        mimeType = mime, base64 = files.GetFileBase64(root, path) });
                }
                if (FileService.IsVideoFile(path))
                {
                    var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLower();
                    var mime = ext switch {
                        "mp4" => "video/mp4", "webm" => "video/webm",
                        "mov" => "video/quicktime", "avi" => "video/x-msvideo",
                        "mkv" => "video/x-matroska", _ => "video/mp4"
                    };
                    var info = new System.IO.FileInfo(System.IO.Path.Combine(root, path));
                    return Ok(new { content = (string?)null, isBinary = true, isImage = false,
                        isVideo = true, mimeType = mime, fileSize = info.Length });
                }
                if (FileService.IsAudioFile(path))
                {
                    var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLower();
                    var mime = ext switch {
                        "mp3" => "audio/mpeg", "wav" => "audio/wav",
                        "ogg" => "audio/ogg", "flac" => "audio/flac",
                        "aac" => "audio/aac", "m4a" => "audio/mp4",
                        "opus" => "audio/opus", "weba" => "audio/webm",
                        _ => "audio/mpeg"
                    };
                    var info = new System.IO.FileInfo(System.IO.Path.Combine(root, path));
                    return Ok(new { content = (string?)null, isBinary = true, isImage = false,
                        isAudio = true, mimeType = mime, fileSize = info.Length });
                }
                var fileInfo = new System.IO.FileInfo(System.IO.Path.Combine(root, path));
                return Ok(new { content = (string?)null, isBinary = true, isImage = false,
                    mimeType = "application/octet-stream", fileSize = fileInfo.Length });
            }
            return Ok(new { content = files.ReadFile(root, path), isBinary = false, isImage = false });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPut("content")]
    public IActionResult SaveContent(string projectId, [FromQuery] string path, [FromBody] SaveContentRequest req)
    {
        try { files.WriteFile(GetRoot(projectId), path, req.Content); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("diff")]
    public IActionResult GetDiff(string projectId, [FromQuery] string path)
    {
        try
        {
            var diff = files.GetDiff(GetRoot(projectId), path);
            return Ok(new { diff });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("revert")]
    public IActionResult Revert(string projectId, [FromBody] PathRequest req)
    {
        try
        {
            var ok = files.RevertFile(GetRoot(projectId), req.Path);
            return ok ? Ok() : BadRequest(new { error = "Не удалось откатить (не git-репозиторий?)" });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("create")]
    public IActionResult CreateFile(string projectId, [FromBody] PathRequest req)
    {
        try { files.CreateFile(GetRoot(projectId), req.Path); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPost("mkdir")]
    public IActionResult CreateDir(string projectId, [FromBody] PathRequest req)
    {
        try { files.CreateDirectory(GetRoot(projectId), req.Path); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPost("rename")]
    public IActionResult Rename(string projectId, [FromBody] RenameRequest req)
    {
        try { files.Rename(GetRoot(projectId), req.OldPath, req.NewPath); return Ok(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpDelete]
    public IActionResult Delete(string projectId, [FromQuery] string path)
    {
        try { files.Delete(GetRoot(projectId), path); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(string projectId, [FromQuery] string path = "", IFormFile? file = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не выбран или пустой" });

        try
        {
            var root = GetRoot(projectId);
            // Path.GetFileName защищает от path-сегментов в имени файла (../evil)
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrEmpty(safeName))
                return BadRequest(new { error = "Некорректное имя файла" });

            var relativePath = string.IsNullOrEmpty(path) ? safeName : $"{path}/{safeName}";

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            files.WriteFileBytes(root, relativePath, ms.ToArray());
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("stream")]
    public IActionResult Stream(string projectId, [FromQuery] string path)
    {
        try
        {
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, path);
            if (!System.IO.File.Exists(safePath)) return NotFound();
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLower();
            var mime = ext switch {
                "mp4" => "video/mp4", "webm" => "video/webm",
                "mov" => "video/quicktime", "avi" => "video/x-msvideo",
                "mkv" => "video/x-matroska", _ => "application/octet-stream"
            };
            return PhysicalFile(safePath, mime, enableRangeProcessing: true);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Отдаёт байты файла OnlyOffice Document Server (без Authorization — DS не знает API-ключ).
    // Защита: проверка download-токена, который DS получает через office-config.
    [HttpGet("office-download")]
    [AllowAnonymous]
    public IActionResult OfficeDownload(string projectId, [FromQuery] string path, [FromQuery] string token)
    {
        var expected = GetDownloadToken();
        if (string.IsNullOrEmpty(token) || token != expected)
            return Unauthorized();

        try
        {
            var root = GetRoot(projectId);
            var bytes = files.ReadFileBytes(root, path);
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var mime = ext switch {
                "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                "pdf"  => "application/pdf",
                _ => "application/octet-stream",
            };
            return File(bytes, mime);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Возвращает конфиг для DocsAPI.DocEditor: URL сервера DS и параметры документа.
    [HttpGet("office-config")]
    public IActionResult OfficeConfig(string projectId, [FromQuery] string path, [FromQuery] string mode = "view", [FromQuery] string? cacheKey = null)
    {
        try
        {
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, path);
            var modTime = System.IO.File.GetLastWriteTimeUtc(safePath).Ticks;
            var fileName = Path.GetFileName(path);
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            var serverUrl = config["OnlyOffice:ServerUrl"] ?? "http://localhost:8090";
            var backendUrl = config["OnlyOffice:BackendUrl"] ?? "http://host.docker.internal:5000";
            var token = GetDownloadToken();

            // Ключ документа: хэш пути + время изменения (DS кеширует по ключу).
            // cacheKey позволяет сбросить кеш OO после сохранения через callback.
            var keyInput = cacheKey != null
                ? $"{projectId}/{path}/{modTime}/{cacheKey}"
                : $"{projectId}/{path}/{modTime}";
            var key = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(keyInput)))
                [..20];

            var docKey = $"{key}-{mode}";
            // Запоминаем edit-ключ текущей сессии для возможного office-discard
            if (mode == "edit")
                _activeEditKeys[$"{projectId}/{path}"] = docKey;

            var downloadUrl = $"{backendUrl}/api/projects/{projectId}/files/office-download" +
                              $"?path={Uri.EscapeDataString(path)}&token={Uri.EscapeDataString(token)}";
            var callbackUrl = mode == "edit"
                ? $"{backendUrl}/api/projects/{projectId}/files/office-callback" +
                  $"?path={Uri.EscapeDataString(path)}&token={Uri.EscapeDataString(token)}"
                : (string?)null;

            return Ok(new {
                serverUrl,
                document = new {
                    fileType = ext,
                    key = docKey,
                    title = fileName,
                    url = downloadUrl,
                },
                editorConfig = new {
                    mode,
                    lang = "ru",
                    callbackUrl,
                    customization = new {
                        uiTheme = "theme-claude-home",
                        anonymous = new { request = false },
                        compactToolbar = true,
                        help = false,
                        chat = false,
                        integrationMode = "embed",
                        toolbarHideFileName = true,
                    },
                },
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    private string GetDownloadToken()
    {
        var token = config["OnlyOffice:DownloadToken"];
        if (!string.IsNullOrEmpty(token)) return token;

        // Авто-генерация и кеш на время жизни приложения
        return _downloadTokenCache ??= Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    }

    private static string? _downloadTokenCache;
    // Активные edit-ключи: "{projectId}/{path}" → docKey
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _activeEditKeys = new();
    // Ключи для игнорирования в callback (пользователь нажал «Отмена»)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _discardedKeys = new();
    // TCS для ожидания forceSave callback: docKey → TCS
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _forceSavePending = new();

    // Вызывается OnlyOffice DS после сохранения документа пользователем.
    // status=2: документ готов, url — временная ссылка на изменённый файл.
    // status=6: принудительное сохранение (forceSave).
    [HttpPost("office-callback")]
    [AllowAnonymous]
    public async Task<IActionResult> OfficeCallback(
        string projectId,
        [FromQuery] string path,
        [FromQuery] string token,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var expected = GetDownloadToken();
        if (string.IsNullOrEmpty(token) || token != expected)
            return Unauthorized();

        OOCallbackPayload? payload;
        try
        {
            payload = await System.Text.Json.JsonSerializer.DeserializeAsync<OOCallbackPayload>(
                Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
        }
        catch { return Ok(new { error = 1, message = "bad json" }); }

        // Пользователь нажал «Отмена» — пропускаем сохранение
        if (payload?.Key != null && _discardedKeys.TryRemove(payload.Key, out _))
            return Ok(new { error = 0 });

        if (payload?.Status is 2 or 6 && payload.Url != null)
        {
            try
            {
                var root = GetRoot(projectId);
                var client = httpClientFactory.CreateClient("proxy");
                var bytes = await client.GetByteArrayAsync(payload.Url, ct);
                files.WriteFileBytes(root, path, bytes);

                // Разблокируем ожидающий office-force-save запрос
                if (payload.Key != null && _forceSavePending.TryRemove(payload.Key, out var tcs))
                    tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                if (payload.Key != null && _forceSavePending.TryRemove(payload.Key, out var tcs))
                    tcs.TrySetException(new Exception(ex.Message));
                return Ok(new { error = 1, message = ex.Message });
            }
        }

        return Ok(new { error = 0 });
    }

    // Вызывается фронтом при нажатии «Отмена» в режиме редактирования OO.
    // Помечает edit-ключ как «выбросить» и откатывает файл через git.
    [HttpPost("office-discard")]
    public IActionResult OfficeDiscard(string projectId, [FromQuery] string path)
    {
        try
        {
            var root = GetRoot(projectId);
            if (_activeEditKeys.TryRemove($"{projectId}/{path}", out var editKey))
                _discardedKeys.TryAdd(editKey, 0);
            files.RevertFile(root, path); // false → не git-репо, игнорируем
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Принудительно сохраняет документ через OO Command API и ждёт callback.
    // Вызывается фронтом при нажатии «Сохранить» — быстрее чем ждать savetimeoutdelay (5 с).
    [HttpPost("office-force-save")]
    public async Task<IActionResult> OfficeForceSave(
        string projectId,
        [FromQuery] string path,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var fileKey = $"{projectId}/{path}";
        if (!_activeEditKeys.TryGetValue(fileKey, out var docKey))
            return Ok(new { ok = false, reason = "no-session" });

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _forceSavePending[docKey] = tcs;

        // Вызов CommandService через внутренний адрес OO DS, а не через публичный URL (YARP+TLS)
        var ooInternalUrl = config["ReverseProxy:Clusters:onlyoffice:Destinations:default:Address"] ?? "http://localhost:8090";
        try
        {
            var client = httpClientFactory.CreateClient();
            var payload = System.Text.Json.JsonSerializer.Serialize(new { c = "forcesave", key = docKey });
            await client.PostAsync($"{ooInternalUrl}/coauthoring/CommandService.ashx",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);
        }
        catch { /* Command API недоступен — ждём таймаут */ }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(10));
        linked.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task;
            return Ok(new { ok = true });
        }
        catch (OperationCanceledException)
        {
            _forceSavePending.TryRemove(docKey, out _);
            return Ok(new { ok = false, reason = "timeout" });
        }
    }

    // Возвращает Unix-время последней записи файла (мс) — для polling после OO-сохранения.
    [HttpGet("office-version")]
    public IActionResult OfficeVersion(string projectId, [FromQuery] string path)
    {
        try
        {
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, path);
            if (!System.IO.File.Exists(safePath))
                return NotFound();
            var ms = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(safePath)).ToUnixTimeMilliseconds();
            return Ok(new { ms });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("save-from-url")]
    [RequestSizeLimit(200 * 1024 * 1024)] // 200 МБ для видео
    public async Task<IActionResult> SaveFromUrl(
        string projectId,
        [FromBody] SaveFromUrlRequest req,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http"))
            return BadRequest(new { error = "Некорректный URL" });

        try
        {
            var root = GetRoot(projectId);
            var client = httpClientFactory.CreateClient("proxy");
            var bytes = await client.GetByteArrayAsync(uri, ct);
            files.WriteFileBytes(root, req.Path, bytes);
            return Ok(new { path = req.Path });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }
}

public record SaveContentRequest(string Content);
public record PathRequest(string Path);
public record RenameRequest(string OldPath, string NewPath);
public record SaveFromUrlRequest(string Url, string Path);
public record OOCallbackPayload(int Status, string? Url, string? Key);
