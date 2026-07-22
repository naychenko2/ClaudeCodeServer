using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/files")]
public class FilesController(FileService files, ProjectManager projects, SyncService sync, IConfiguration config, JwtService jwt, ILogger<FilesController> logger, NotesService notes, DocumentAiService docAi) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Проект текущего пользователя; чужой/несуществующий → KeyNotFoundException (клиенту 404, как в ProjectsController)
    private ClaudeHomeServer.Models.Project GetProject(string projectId)
    {
        var p = projects.GetById(projectId);
        if (p is null || p.OwnerId != UserId)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    // Для анонимных OnlyOffice-эндпоинтов (office-download/office-callback): JWT нет,
    // доступ защищён подписанным office-токеном, привязанным к владельцу+projectId+path.
    // ownerId извлекается из проверенного токена — сверяем с владельцем проекта.
    private ClaudeHomeServer.Models.Project GetProjectByOfficeToken(string projectId, string ownerId)
    {
        var p = projects.GetById(projectId);
        if (p is null || p.OwnerId != ownerId)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

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

    // Абсолютный путь документа с проверкой, что это просматриваемый документ (pdf/docx/xlsx/pptx).
    // null → клиенту 400 (не документ). Иначе — безопасный абсолютный путь для markitdown.
    // Visio исключён: markitdown его не конвертирует.
    private string? DocumentAbsPath(string projectId, string path)
    {
        if (files.GetDocumentInfo(path) is not { } d || d.Kind == "visio") return null;
        return FileService.SafeJoinPublic(GetRoot(projectId), path);
    }

    // Текст файла для ИИ-действий (суть/выжимка/теги): бинарный документ (pdf/docx/xlsx/pptx) →
    // markitdown; текстовый файл (.md/.txt/.csv/код) → читаем как есть; прочие бинарные → null.
    private async Task<string?> GetAiTextAsync(string projectId, string path, CancellationToken ct)
    {
        var root = GetRoot(projectId);
        if (files.GetDocumentInfo(path) is { } d)
        {
            if (d.Kind == "visio") return null; // markitdown не конвертирует Visio
            var abs = FileService.SafeJoinPublic(root, path);
            return System.IO.File.Exists(abs) ? await docAi.ConvertAsync(abs, ct) : null;
        }
        return files.IsBinaryFile(root, path) ? null : files.ReadFile(root, path);
    }

    // Конвертация документа в Markdown (markitdown, без модели). Возвращает { markdown }.
    [HttpPost("document/convert")]
    public async Task<IActionResult> DocumentConvert(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            if (DocumentAbsPath(projectId, path) is not { } abs)
                return BadRequest(new { error = "Это не документ (pdf/docx/xlsx/pptx)" });
            var md = await docAi.ConvertAsync(abs, ct);
            return md is null
                ? StatusCode(502, new { error = "Не удалось конвертировать документ (markitdown недоступен?)" })
                : Ok(new { markdown = md });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Краткое содержание документа (локальная модель / claude).
    [HttpPost("document/summary")]
    public async Task<IActionResult> DocumentSummary(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var text = await GetAiTextAsync(projectId, path, ct);
            if (text is null) return BadRequest(new { error = "Файл не поддерживается (нужен документ или текст)" });
            var summary = await docAi.SummaryAsync(text, ct);
            return summary is null ? StatusCode(502, new { error = "Не удалось обработать файл" }) : Ok(new { summary });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Структурная выжимка: решения, даты, участники, action items.
    [HttpPost("document/extract")]
    public async Task<IActionResult> DocumentExtract(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var text = await GetAiTextAsync(projectId, path, ct);
            if (text is null) return BadRequest(new { error = "Файл не поддерживается (нужен документ или текст)" });
            var r = await docAi.ExtractAsync(text, ct);
            return r is null
                ? StatusCode(502, new { error = "Не удалось обработать файл" })
                : Ok(new { decisions = r.Decisions, dates = r.Dates, people = r.People, actionItems = r.ActionItems });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Трансформация ЛЮБОГО файла в Markdown с СОХРАНЕНИЕМ (markitdown, без модели).
    // targetDir (относительно корня проекта) — куда положить .md; пусто → рядом с исходником.
    // Возвращает { savedPath, markdown }.
    [HttpPost("document/to-markdown")]
    public async Task<IActionResult> ToMarkdown(string projectId, [FromBody] ToMarkdownRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Нужен путь файла" });
        try
        {
            var root = GetRoot(projectId);
            var abs = FileService.SafeJoinPublic(root, req.Path);
            if (!System.IO.File.Exists(abs))
                return NotFound(new { error = "Файл не найден" });

            var md = await docAi.ConvertAsync(abs, ct);
            if (md is null)
                return StatusCode(502, new { error = "Не удалось конвертировать файл (markitdown недоступен или формат не поддержан)" });

            // Опционально: восстановить Markdown-разметку локальной моделью (для pdf без структуры)
            if (req.Enhance) md = await docAi.EnhanceMarkdownAsync(md, ct);

            // Имя целевого .md — по исходному имени; каталог — targetDir или рядом с исходником
            var baseName = System.IO.Path.GetFileNameWithoutExtension(req.Path) + ".md";
            var dir = string.IsNullOrWhiteSpace(req.TargetDir)
                ? (System.IO.Path.GetDirectoryName(req.Path.Replace('\\', '/')) ?? "")
                : req.TargetDir.Replace('\\', '/').Trim('/');
            var targetRel = string.IsNullOrEmpty(dir) ? baseName : $"{dir}/{baseName}";

            // Целевая папка может не существовать (пользователь указал новую) — создаём
            if (!string.IsNullOrEmpty(dir)) files.CreateDirectory(root, dir);
            files.WriteFile(root, targetRel, md);
            return Ok(new { savedPath = targetRel, markdown = md });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Теги документа по содержимому.
    [HttpPost("document/tags")]
    public async Task<IActionResult> DocumentTags(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var text = await GetAiTextAsync(projectId, path, ct);
            if (text is null) return BadRequest(new { error = "Файл не поддерживается (нужен документ или текст)" });
            var tags = await docAi.TagsAsync(text, ct);
            return tags is null ? StatusCode(502, new { error = "Не удалось обработать файл" }) : Ok(new { tags });
        }
        catch (KeyNotFoundException) { return NotFound(); }
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
        try
        {
            files.Rename(GetRoot(projectId), req.OldPath, req.NewPath);
            // Комментарии к переименованному/перенесённому документу (или документам
            // внутри папки) следуют за новым путём — привязка не сиротеет
            try { notes.RewriteAnnotationTargets(UserId!, projectId, req.OldPath, projectId, req.NewPath, prefix: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Перепись привязок комментариев при rename {Old}", req.OldPath); }
            return Ok();
        }
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

            // Стриминг на диск вместо буферизации всего файла в памяти
            var safePath = FileService.SafeJoinPublic(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            await using (var fs = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await file.CopyToAsync(fs);
            // Запись мимо FileService.Write* — уведомляем подписчиков (синк знаний) явно
            files.NotifyMutated(root, relativePath, FileMutationKind.Write);
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
        var ownerId = jwt.ValidateOfficeToken(token, projectId, path);
        if (ownerId is null)
            return Unauthorized();

        try
        {
            var root = GetProjectByOfficeToken(projectId, ownerId).RootPath;
            var safePath = FileService.SafeJoinPublic(root, path);
            if (!System.IO.File.Exists(safePath)) return NotFound();
            var mime = files.GetDocumentInfo(path)?.Mime ?? "application/octet-stream";
            // Стриминг с диска вместо буферизации всего файла в памяти
            return PhysicalFile(safePath, mime);
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

            // Visio DS умеет только просматривать — режим редактирования не выдаём
            if (files.GetDocumentInfo(path)?.Kind == "visio")
                mode = "view";

            var serverUrl = config["OnlyOffice:ServerUrl"] ?? "http://localhost:8090";
            var backendUrl = config["OnlyOffice:BackendUrl"] ?? "http://host.docker.internal:5000";
            // Токен привязан к владельцу+projectId+path — office-download/callback сверят владельца
            var token = jwt.IssueOfficeToken(UserId!, projectId, path);

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

    // SSRF-защита callback: скачиваем документ только с хостов OnlyOffice
    // (хост ServerUrl + явный список OnlyOffice:AllowedCallbackHosts)
    private bool IsAllowedCallbackSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(config["OnlyOffice:ServerUrl"], UriKind.Absolute, out var srv))
            allowed.Add(srv.Host);
        foreach (var h in config.GetSection("OnlyOffice:AllowedCallbackHosts").Get<string[]>() ?? [])
            allowed.Add(h);
        return allowed.Contains(uri.Host);
    }

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
        var ownerId = jwt.ValidateOfficeToken(token, projectId, path);
        if (ownerId is null)
            return Unauthorized();

        OOCallbackPayload? payload;
        try
        {
            payload = await System.Text.Json.JsonSerializer.DeserializeAsync<OOCallbackPayload>(
                Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "office-callback: не удалось разобрать тело запроса для {Path}", path);
            return Ok(new { error = 1, message = "bad json" });
        }

        // Пользователь нажал «Отмена» — пропускаем сохранение
        if (payload?.Key != null && _discardedKeys.TryRemove(payload.Key, out _))
            return Ok(new { error = 0 });

        if (payload?.Status is 2 or 6 && payload.Url != null)
        {
            if (!IsAllowedCallbackSource(payload.Url))
                return Ok(new { error = 1, message = "url host not allowed" });
            try
            {
                var root = GetProjectByOfficeToken(projectId, ownerId).RootPath;
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
        try { GetProject(projectId); }
        catch (KeyNotFoundException) { return NotFound(); }

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
        catch (Exception ex)
        {
            // Command API недоступен — ждём таймаут
            logger.LogWarning(ex, "office-force-save: Command API недоступен для {Path}", path);
        }

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

        // SSRF: не даём серверу скачивать с внутренних адресов (localhost, приватные сети,
        // cloud metadata). Клиент "safe-download" без авто-редиректов — редирект на приватный
        // хост не обойдёт проверку (вернётся 3xx → EnsureSuccessStatusCode → 502).
        if (!await SsrfGuard.IsPubliclyRoutableAsync(uri, ct))
            return BadRequest(new { error = "URL указывает на недопустимый адрес" });

        try
        {
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, req.Path);
            var client = httpClientFactory.CreateClient("safe-download");

            // Стриминг в файл вместо буферизации всего ответа в памяти
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            if (resp.Content.Headers.ContentLength > MaxSaveFromUrlBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = $"Файл больше {MaxSaveFromUrlBytes / (1024 * 1024)} МБ" });

            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            var exceeded = false;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                // Content-Length может отсутствовать — копируем с подсчётом и обрываем при превышении
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxSaveFromUrlBytes) { exceeded = true; break; }
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (exceeded)
            {
                try { System.IO.File.Delete(safePath); }
                catch (Exception ex) { logger.LogWarning(ex, "save-from-url: не удалось удалить недокачанный файл {Path}", safePath); }
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = $"Файл больше {MaxSaveFromUrlBytes / (1024 * 1024)} МБ" });
            }

            // Запись мимо FileService.Write* — уведомляем подписчиков (синк знаний) явно
            files.NotifyMutated(root, req.Path, FileMutationKind.Write);
            return Ok(new { path = req.Path });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Предельный размер скачивания save-from-url (совпадает с RequestSizeLimit)
    private const long MaxSaveFromUrlBytes = 200L * 1024 * 1024;
}

public record SaveContentRequest(string Content);
public record PathRequest(string Path);
public record RenameRequest(string OldPath, string NewPath);
public record SaveFromUrlRequest(string Url, string Path);
public record ToMarkdownRequest(string Path, string? TargetDir = null, bool Enhance = false);
public record OOCallbackPayload(int Status, string? Url, string? Key);
