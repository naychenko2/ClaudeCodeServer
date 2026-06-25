using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;
using ClaudeCodeServer.Services;

namespace ClaudeCodeServer.WebDav;

/// <summary>
/// Хранит информацию об активной блокировке WebDAV LOCK.
/// </summary>
public record DavLock(string Token, string Owner, DateTime Expires);

/// <summary>
/// Обработчик WebDAV — поддерживает классы совместимости 1 и 2.
/// Basic Auth реализован внутри хендлера (не через ASP.NET auth pipeline),
/// чтобы не конфликтовать с JWT-аутентификацией REST/SignalR.
/// </summary>
public static class WebDavHandler
{
    // in-memory хранилище блокировок: ключ = "projectName/relPath"
    private static readonly ConcurrentDictionary<string, DavLock> _locks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task HandleAsync(HttpContext ctx)
    {
        // ── Basic Auth ──────────────────────────────────────────────────────
        var users = ctx.RequestServices.GetRequiredService<UserStore>();
        if (!TryAuthenticateBasic(ctx, users))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"ClaudeCodeServer\", charset=\"UTF-8\"";
            return;
        }

        // ── Разбор маршрута ─────────────────────────────────────────────────
        var projectName = ctx.GetRouteValue("projectName") as string ?? "";
        var rawPath     = ctx.GetRouteValue("path") as string ?? "";

        var projects = ctx.RequestServices.GetRequiredService<ProjectManager>();
        var project  = projects.GetByName(projectName);
        if (project is null)
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("Project not found");
            return;
        }

        var files = ctx.RequestServices.GetRequiredService<FileService>();
        var root  = project.RootPath;
        // relPath — нормализованный относительный путь внутри проекта
        var relPath = rawPath.Trim('/').Replace('\\', '/');

        var method = ctx.Request.Method.ToUpperInvariant();

        try
        {
            switch (method)
            {
                case "OPTIONS":     HandleOptions(ctx); break;
                case "PROPFIND":    await HandlePropfindAsync(ctx, files, root, relPath, projectName); break;
                case "PROPPATCH":   await HandleProppatchAsync(ctx, relPath, projectName); break;
                case "GET":
                case "HEAD":        await HandleGetAsync(ctx, files, root, relPath, method == "HEAD"); break;
                case "PUT":         await HandlePutAsync(ctx, files, root, relPath); break;
                case "DELETE":      HandleDelete(ctx, files, root, relPath); break;
                case "MKCOL":       HandleMkcol(ctx, files, root, relPath); break;
                case "COPY":        await HandleCopyAsync(ctx, files, root, relPath, projectName); break;
                case "MOVE":        HandleMove(ctx, files, root, relPath, projectName); break;
                case "LOCK":        await HandleLockAsync(ctx, files, root, relPath, projectName); break;
                case "UNLOCK":      HandleUnlock(ctx, relPath, projectName); break;
                default:
                    ctx.Response.StatusCode = 405;
                    break;
            }
        }
        catch (UnauthorizedAccessException)
        {
            ctx.Response.StatusCode = 403;
        }
        catch (DirectoryNotFoundException)
        {
            ctx.Response.StatusCode = 404;
        }
        catch (FileNotFoundException)
        {
            ctx.Response.StatusCode = 404;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Basic Auth
    // ────────────────────────────────────────────────────────────────────────

    private static bool TryAuthenticateBasic(HttpContext ctx, UserStore users)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch
        {
            return false;
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0) return false;

        var username = decoded[..colon];
        var password = decoded[(colon + 1)..];

        var user = users.FindByUsername(username);
        return user is not null && users.VerifyPassword(user, password);
    }

    // ────────────────────────────────────────────────────────────────────────
    // OPTIONS
    // ────────────────────────────────────────────────────────────────────────

    private static void HandleOptions(HttpContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["Allow"]         = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
        ctx.Response.Headers["DAV"]           = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
    }

    // ────────────────────────────────────────────────────────────────────────
    // PROPFIND
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandlePropfindAsync(HttpContext ctx, FileService files, string root, string relPath, string projectName)
    {
        var depth = ctx.Request.Headers["Depth"].ToString();
        if (depth == "infinity") depth = "1"; // ограничиваем глубину

        var absPath = string.IsNullOrEmpty(relPath)
            ? root
            : FileService.SafeJoinPublic(root, relPath);

        var isDir  = Directory.Exists(absPath);
        var isFile = !isDir && File.Exists(absPath);

        if (!isDir && !isFile)
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<D:multistatus xmlns:D=\"DAV:\">");

        // сам ресурс
        AppendResponse(sb, ctx, projectName, relPath, absPath, isDir);

        // дочерние элементы (depth=1, только для папок)
        if (isDir && depth == "1")
        {
            foreach (var entry in files.List(root, relPath))
            {
                var childAbs = FileService.SafeJoinPublic(root, entry.Path);
                AppendResponse(sb, ctx, projectName, entry.Path, childAbs, entry.IsDirectory);
            }
        }

        sb.AppendLine("</D:multistatus>");

        ctx.Response.StatusCode  = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(sb.ToString(), Encoding.UTF8);
    }

    private static void AppendResponse(StringBuilder sb, HttpContext ctx, string projectName, string relPath, string absPath, bool isDir)
    {
        var href = BuildHref(ctx, projectName, relPath, isDir);

        string displayName, lastModified, createdDate, etag = "", contentLength = "", contentType = "";

        if (isDir)
        {
            var info = new DirectoryInfo(absPath);
            displayName  = string.IsNullOrEmpty(relPath) ? projectName : XmlEscape(info.Name);
            lastModified = info.LastWriteTimeUtc.ToString("R");
            createdDate  = info.CreationTimeUtc.ToString("O");
        }
        else
        {
            var info = new FileInfo(absPath);
            displayName   = XmlEscape(info.Name);
            lastModified  = info.LastWriteTimeUtc.ToString("R");
            createdDate   = info.CreationTimeUtc.ToString("O");
            contentLength = info.Length.ToString();
            contentType   = XmlEscape(GetMimeType(info.Name));
            etag          = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
        }

        sb.AppendLine("  <D:response>");
        sb.AppendLine($"    <D:href>{href}</D:href>");
        sb.AppendLine("    <D:propstat>");
        sb.AppendLine("      <D:prop>");
        sb.AppendLine($"        <D:displayname>{displayName}</D:displayname>");

        if (isDir)
            sb.AppendLine("        <D:resourcetype><D:collection/></D:resourcetype>");
        else
        {
            sb.AppendLine("        <D:resourcetype/>");
            sb.AppendLine($"        <D:getcontentlength>{contentLength}</D:getcontentlength>");
            sb.AppendLine($"        <D:getcontenttype>{contentType}</D:getcontenttype>");
            sb.AppendLine($"        <D:getetag>{etag}</D:getetag>");
        }

        sb.AppendLine($"        <D:getlastmodified>{lastModified}</D:getlastmodified>");
        sb.AppendLine($"        <D:creationdate>{createdDate}</D:creationdate>");
        sb.AppendLine("      </D:prop>");
        sb.AppendLine("      <D:status>HTTP/1.1 200 OK</D:status>");
        sb.AppendLine("    </D:propstat>");
        sb.AppendLine("  </D:response>");
    }

    // ────────────────────────────────────────────────────────────────────────
    // PROPPATCH  — файловая система read-only со стороны DAV-свойств
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandleProppatchAsync(HttpContext ctx, string relPath, string projectName)
    {
        var href = BuildHref(ctx, projectName, relPath, false);
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>{href}</D:href>
                <D:propstat>
                  <D:prop/>
                  <D:status>HTTP/1.1 403 Forbidden</D:status>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;

        ctx.Response.StatusCode  = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(xml, Encoding.UTF8);
    }

    // ────────────────────────────────────────────────────────────────────────
    // GET / HEAD
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandleGetAsync(HttpContext ctx, FileService files, string root, string relPath, bool headOnly)
    {
        var absPath = string.IsNullOrEmpty(relPath)
            ? root
            : FileService.SafeJoinPublic(root, relPath);

        if (Directory.Exists(absPath))
        {
            // HTML-листинг для браузеров
            var entries = files.List(root, relPath).ToList();
            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset='utf-8'><title>");
            sb.Append(WebUtility.HtmlEncode(relPath.Length > 0 ? relPath : "/"));
            sb.Append("</title></head><body><ul>");
            foreach (var e in entries)
            {
                var name = WebUtility.HtmlEncode(e.Name) + (e.IsDirectory ? "/" : "");
                sb.Append($"<li><a href=\"{Uri.EscapeDataString(e.Name)}\">{name}</a></li>");
            }
            sb.Append("</ul></body></html>");
            var html = sb.ToString();
            ctx.Response.StatusCode  = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
            if (!headOnly) await ctx.Response.WriteAsync(html, Encoding.UTF8);
            return;
        }

        if (!File.Exists(absPath))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var fi       = new FileInfo(absPath);
        var mime     = GetMimeType(fi.Name);
        var etag     = $"\"{fi.LastWriteTimeUtc.Ticks:x}-{fi.Length:x}\"";

        ctx.Response.StatusCode  = 200;
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength = fi.Length;
        ctx.Response.Headers["Last-Modified"] = fi.LastWriteTimeUtc.ToString("R");
        ctx.Response.Headers["ETag"]          = etag;

        if (!headOnly)
            await ctx.Response.SendFileAsync(absPath);
    }

    // ────────────────────────────────────────────────────────────────────────
    // PUT
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandlePutAsync(HttpContext ctx, FileService files, string root, string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            ctx.Response.StatusCode = 409; // нельзя перезаписать корень проекта
            return;
        }

        var absPath = FileService.SafeJoinPublic(root, relPath);
        var existed = File.Exists(absPath);

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        files.WriteFileBytes(root, relPath, ms.ToArray());

        ctx.Response.StatusCode = existed ? 204 : 201;
    }

    // ────────────────────────────────────────────────────────────────────────
    // DELETE
    // ────────────────────────────────────────────────────────────────────────

    private static void HandleDelete(HttpContext ctx, FileService files, string root, string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        var absPath = FileService.SafeJoinPublic(root, relPath);
        if (!File.Exists(absPath) && !Directory.Exists(absPath))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        files.Delete(root, relPath);
        ctx.Response.StatusCode = 204;
    }

    // ────────────────────────────────────────────────────────────────────────
    // MKCOL
    // ────────────────────────────────────────────────────────────────────────

    private static void HandleMkcol(HttpContext ctx, FileService files, string root, string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            ctx.Response.StatusCode = 405;
            return;
        }

        var absPath = FileService.SafeJoinPublic(root, relPath);
        if (Directory.Exists(absPath) || File.Exists(absPath))
        {
            ctx.Response.StatusCode = 405; // уже существует
            return;
        }

        files.CreateDirectory(root, relPath);
        ctx.Response.StatusCode = 201;
    }

    // ────────────────────────────────────────────────────────────────────────
    // COPY
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandleCopyAsync(HttpContext ctx, FileService files, string root, string relPath, string projectName)
    {
        var destRel = ParseDestination(ctx, projectName);
        if (destRel is null)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var overwrite = !string.Equals(ctx.Request.Headers["Overwrite"].ToString(), "F", StringComparison.OrdinalIgnoreCase);
        var srcAbs    = FileService.SafeJoinPublic(root, relPath);
        var dstAbs    = FileService.SafeJoinPublic(root, destRel);

        if (!overwrite && (File.Exists(dstAbs) || Directory.Exists(dstAbs)))
        {
            ctx.Response.StatusCode = 412;
            return;
        }

        var existed = File.Exists(dstAbs) || Directory.Exists(dstAbs);

        if (Directory.Exists(srcAbs))
            CopyDirectory(srcAbs, dstAbs, overwrite);
        else if (File.Exists(srcAbs))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);
            File.Copy(srcAbs, dstAbs, overwrite);
        }
        else
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        ctx.Response.StatusCode = existed ? 204 : 201;
        await Task.CompletedTask;
    }

    private static void CopyDirectory(string src, string dst, bool overwrite)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), overwrite);
    }

    // ────────────────────────────────────────────────────────────────────────
    // MOVE
    // ────────────────────────────────────────────────────────────────────────

    private static void HandleMove(HttpContext ctx, FileService files, string root, string relPath, string projectName)
    {
        var destRel = ParseDestination(ctx, projectName);
        if (destRel is null)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        var dstAbs = FileService.SafeJoinPublic(root, destRel);
        var existed = File.Exists(dstAbs) || Directory.Exists(dstAbs);

        var overwrite = !string.Equals(ctx.Request.Headers["Overwrite"].ToString(), "F", StringComparison.OrdinalIgnoreCase);
        if (!overwrite && existed)
        {
            ctx.Response.StatusCode = 412;
            return;
        }

        if (existed)
        {
            if (Directory.Exists(dstAbs)) Directory.Delete(dstAbs, true);
            else File.Delete(dstAbs);
        }

        files.Rename(root, relPath, destRel);
        ctx.Response.StatusCode = existed ? 204 : 201;
    }

    // ────────────────────────────────────────────────────────────────────────
    // LOCK
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandleLockAsync(HttpContext ctx, FileService files, string root, string relPath, string projectName)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        var absPath = FileService.SafeJoinPublic(root, relPath);
        var existed = File.Exists(absPath);

        // Клиент сначала блокирует несуществующий файл, потом пишет содержимое через PUT
        if (!existed && !Directory.Exists(absPath))
            files.CreateFile(root, relPath);

        // Читаем owner из тела запроса (может быть пустым)
        var owner = "";
        try
        {
            using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await sr.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                // ищем <D:href>...</D:href> или <href>...</href> внутри owner
                var ownerMatch = System.Text.RegularExpressions.Regex.Match(
                    body, @"<[^>]*:?href[^>]*>(.*?)</[^>]*:?href[^>]*>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (ownerMatch.Success)
                    owner = XmlEscape(ownerMatch.Groups[1].Value);
            }
        }
        catch { /* тело может быть пустым */ }

        var token = "urn:uuid:" + Guid.NewGuid();
        var lockKey = $"{projectName}/{relPath}";
        _locks[lockKey] = new DavLock(token, owner, DateTime.UtcNow.AddHours(1));

        var href = BuildHref(ctx, projectName, relPath, false);
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:prop xmlns:D="DAV:">
              <D:lockdiscovery>
                <D:activelock>
                  <D:locktype><D:write/></D:locktype>
                  <D:lockscope><D:exclusive/></D:lockscope>
                  <D:depth>0</D:depth>
                  <D:owner><D:href>{owner}</D:href></D:owner>
                  <D:timeout>Second-3600</D:timeout>
                  <D:locktoken><D:href>{token}</D:href></D:locktoken>
                  <D:lockroot><D:href>{href}</D:href></D:lockroot>
                </D:activelock>
              </D:lockdiscovery>
            </D:prop>
            """;

        ctx.Response.StatusCode = existed ? 200 : 201;
        ctx.Response.Headers["Lock-Token"] = $"<{token}>";
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        await ctx.Response.WriteAsync(xml, Encoding.UTF8);
    }

    // ────────────────────────────────────────────────────────────────────────
    // UNLOCK
    // ────────────────────────────────────────────────────────────────────────

    private static void HandleUnlock(HttpContext ctx, string relPath, string projectName)
    {
        var tokenHeader = ctx.Request.Headers["Lock-Token"].ToString().Trim('<', '>');
        var lockKey = $"{projectName}/{relPath}";

        if (_locks.TryGetValue(lockKey, out var lck) && lck.Token == tokenHeader)
            _locks.TryRemove(lockKey, out _);

        ctx.Response.StatusCode = 204;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Вспомогательные методы
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Строит href для PROPFIND-ответа из текущего Request.PathBase + /webdav/{projectName}/{relPath}.
    /// Сегменты пути URL-кодируются. Не хардкодим схему/хост — берём из контекста.
    /// </summary>
    private static string BuildHref(HttpContext ctx, string projectName, string relPath, bool isDir)
    {
        var pb = ctx.Request.PathBase.ToString().TrimEnd('/');
        var segments = string.IsNullOrEmpty(relPath)
            ? Array.Empty<string>()
            : relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var encoded = string.Join("/", segments.Select(Uri.EscapeDataString));
        var path = $"{pb}/webdav/{Uri.EscapeDataString(projectName)}" +
                   (encoded.Length > 0 ? $"/{encoded}" : "") +
                   (isDir ? "/" : "");
        return path;
    }

    /// <summary>
    /// Извлекает относительный путь назначения из заголовка Destination.
    /// Заголовок содержит полный URL: http://host/webdav/{projectName}/{path}
    /// </summary>
    private static string? ParseDestination(HttpContext ctx, string projectName)
    {
        var dest = ctx.Request.Headers["Destination"].ToString();
        if (string.IsNullOrEmpty(dest)) return null;

        try
        {
            var uri = new Uri(dest);
            // декодируем полный путь URI
            var uriPath = Uri.UnescapeDataString(uri.AbsolutePath);
            // ищем /webdav/{projectName}/ в пути
            var prefix = $"/webdav/{projectName}/";
            var idx = uriPath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return uriPath[(idx + prefix.Length)..].Trim('/');
        }
        catch
        {
            return null;
        }
    }

    private static string XmlEscape(string s) =>
        SecurityElement.Escape(s) ?? s;

    private static string GetMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html",
            ".css"            => "text/css",
            ".js" or ".mjs"  => "text/javascript",
            ".ts"             => "text/typescript",
            ".tsx"            => "text/tsx",
            ".jsx"            => "text/jsx",
            ".json"           => "application/json",
            ".xml"            => "application/xml",
            ".txt"            => "text/plain",
            ".md"             => "text/markdown",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif"            => "image/gif",
            ".svg"            => "image/svg+xml",
            ".webp"           => "image/webp",
            ".ico"            => "image/x-icon",
            ".pdf"            => "application/pdf",
            ".zip"            => "application/zip",
            ".wasm"           => "application/wasm",
            ".cs"             => "text/x-csharp",
            ".go"             => "text/x-go",
            ".py"             => "text/x-python",
            ".rs"             => "text/x-rust",
            ".sh"             => "text/x-sh",
            _                 => "application/octet-stream",
        };
    }
}
