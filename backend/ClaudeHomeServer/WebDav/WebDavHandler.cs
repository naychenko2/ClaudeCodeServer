using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authentication;

namespace ClaudeHomeServer.WebDav;

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
        var authHdr = ctx.Request.Headers.Authorization.ToString();

        // OPTIONS:
        // • Анонимный (без Auth) — Windows WebClient зондирует анонимно, пускаем без аутентификации.
        // • С Auth-заголовком — пускаем через TryAuthenticateAsync:
        //   - Bearer невалидный → 401 + Negotiate, Word переключает соединение на NTLM до LOCK.
        //   - NTLM T1 → 401 + T2 (нужно для завершения рукопожатия на соединении).
        //   - NTLM T3 / Basic / Bearer валидный → 200.
        if (ctx.Request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(authHdr))
            {
                HandleOptions(ctx);
                return;
            }
            var users2 = ctx.RequestServices.GetRequiredService<UserStore>();
            if (!await TryAuthenticateAsync(ctx, users2))
            {
                ctx.Response.StatusCode    = 401;
                ctx.Response.ContentLength = 0;
                if (!ctx.Response.Headers.ContainsKey("WWW-Authenticate"))
                    ctx.Response.Headers["WWW-Authenticate"] = "Negotiate, Basic realm=\"ClaudeHomeServer\"";
                return;
            }
            HandleOptions(ctx);
            return;
        }

        // ── Аутентификация: NTLM (для Office) или Basic ────────────────────
        var users = ctx.RequestServices.GetRequiredService<UserStore>();
        if (!await TryAuthenticateAsync(ctx, users))
        {
            ctx.Response.StatusCode    = 401;
            ctx.Response.ContentLength = 0;
            if (!ctx.Response.Headers.ContainsKey("WWW-Authenticate"))
                ctx.Response.Headers["WWW-Authenticate"] = "Negotiate, Basic realm=\"ClaudeHomeServer\"";
            return;
        }

        // ── Разбор маршрута из пути запроса ────────────────────────────────
        // Путь: /projects/{projectName}/{**relPath}
        var segments = (ctx.Request.Path.Value ?? "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segments[0] = "projects", [1] = projectName, [2..] = relPath
        var projectName = segments.Length >= 2
            ? Uri.UnescapeDataString(segments[1])
            : "";
        var rawPath = segments.Length >= 3
            ? string.Join("/", segments.Skip(2).Select(Uri.UnescapeDataString))
            : "";

        var projects = ctx.RequestServices.GetRequiredService<ProjectManager>();

        // PROPFIND на корне /projects/ — виртуальная коллекция со списком проектов
        // Windows WebClient зондирует этот путь перед монтированием подпапки
        if (string.IsNullOrEmpty(projectName))
        {
            if (ctx.Request.Method.Equals("PROPFIND", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePropfindRootAsync(ctx, projects);
            }
            else
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.ContentLength = 0;
            }
            return;
        }

        var project  = projects.GetByName(projectName);
        if (project is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.ContentLength = 0;
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
    // Аутентификация: NTLM (Windows Negotiate) + Basic
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Проверяет аутентификацию запроса.
    /// Приоритет: Basic (Mini-Redirector) > JWT Bearer (Office на повторных соединениях) > NTLM Negotiate.
    /// При NTLM Type 1/3 делегирует ASP.NET Core Negotiate-хендлеру (SSPI).
    /// </summary>
    private static async Task<bool> TryAuthenticateAsync(HttpContext ctx, UserStore users)
    {
        // Basic — проверяем первым, Mini-Redirector всегда шлёт Basic напрямую
        if (TryAuthenticateBasic(ctx, users)) return true;

        var authHeader = ctx.Request.Headers.Authorization.ToString();

        // JWT Bearer — Microsoft Office иногда повторно использует соединение из REST API
        // и шлёт Bearer вместо NTLM. UseAuthentication() уже проверил токен и выставил ctx.User.
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.User.Identity?.IsAuthenticated == true) return true;
            // Токен недействителен — выдаём Negotiate-вызов, чтобы Word переключился на NTLM/Basic
            await ctx.ChallengeAsync("Negotiate");
            ctx.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"ClaudeHomeServer\"");
            return false;
        }

        if (!authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
            return false;

        // Negotiate (NTLM) — ASP.NET Core Negotiate handler обрабатывает Type1/Type3 через SSPI
        // и сохраняет состояние в IConnectionItems между запросами одного соединения.
        var result = await ctx.AuthenticateAsync("Negotiate");
        if (result.Succeeded) return true;

        // Type 1: хендлер записал Type2 в connection items → ChallengeAsync добавит его в ответ.
        // Type 3 failure: хендлер вернул Fail.
        await ctx.ChallengeAsync("Negotiate");
        // Добавляем Basic как запасной вариант (Office может попробовать Basic если NTLM не удался)
        ctx.Response.Headers.Append("WWW-Authenticate", "Basic realm=\"ClaudeHomeServer\"");
        return false;
    }

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
        ctx.Response.StatusCode    = 200;
        ctx.Response.ContentLength = 0;
        ctx.Response.Headers["Allow"]         = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, COPY, MOVE, LOCK, UNLOCK";
        ctx.Response.Headers["DAV"]           = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
    }

    // ────────────────────────────────────────────────────────────────────────
    // PROPFIND /projects/ — корневая коллекция
    // ────────────────────────────────────────────────────────────────────────

    private static async Task HandlePropfindRootAsync(HttpContext ctx, ProjectManager projects)
    {
        var pb   = ctx.Request.PathBase.ToString().TrimEnd('/');
        // href — абсолютный URI с trailing slash для коллекции (IIS-поведение)
        var href = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.Path.Value?.TrimEnd('/')}/";

        var now = DateTime.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        // xmlns:Z обязателен — Windows WebDAV Mini-Redirector требует этот namespace для Win32-свойств
        sb.AppendLine("<D:multistatus xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\">");
        sb.AppendLine("  <D:response>");
        sb.AppendLine($"    <D:href>{href}</D:href>");
        sb.AppendLine("    <D:propstat>");
        sb.AppendLine("      <D:prop>");
        sb.AppendLine("        <D:displayname>projects</D:displayname>");
        sb.AppendLine("        <D:resourcetype><D:collection/></D:resourcetype>");
        sb.AppendLine($"        <D:getlastmodified>{now:R}</D:getlastmodified>");
        sb.AppendLine($"        <D:creationdate>{now:yyyy-MM-ddTHH:mm:ssZ}</D:creationdate>");
        sb.AppendLine("        <Z:Win32FileAttributes>00000010</Z:Win32FileAttributes>");
        sb.AppendLine($"        <Z:Win32CreationTime>{now:R}</Z:Win32CreationTime>");
        sb.AppendLine($"        <Z:Win32LastAccessTime>{now:R}</Z:Win32LastAccessTime>");
        sb.AppendLine($"        <Z:Win32LastModifiedTime>{now:R}</Z:Win32LastModifiedTime>");
        sb.AppendLine("      </D:prop>");
        sb.AppendLine("      <D:status>HTTP/1.1 200 OK</D:status>");
        sb.AppendLine("    </D:propstat>");
        sb.AppendLine("  </D:response>");

        var depth = ctx.Request.Headers["Depth"].ToString();
        if (depth == "1")
        {
            foreach (var p in projects.GetAll())
            {
                var pHref = $"{ctx.Request.Scheme}://{ctx.Request.Host}{pb}/projects/{Uri.EscapeDataString(p.Name)}/";
                DateTime pModified = now, pCreated = now;
                if (Directory.Exists(p.RootPath))
                {
                    var di = new DirectoryInfo(p.RootPath);
                    pModified = di.LastWriteTimeUtc;
                    pCreated  = di.CreationTimeUtc;
                }
                sb.AppendLine("  <D:response>");
                sb.AppendLine($"    <D:href>{pHref}</D:href>");
                sb.AppendLine("    <D:propstat>");
                sb.AppendLine("      <D:prop>");
                sb.AppendLine($"        <D:displayname>{XmlEscape(p.Name)}</D:displayname>");
                sb.AppendLine("        <D:resourcetype><D:collection/></D:resourcetype>");
                sb.AppendLine($"        <D:getlastmodified>{pModified:R}</D:getlastmodified>");
                sb.AppendLine($"        <D:creationdate>{pCreated:yyyy-MM-ddTHH:mm:ssZ}</D:creationdate>");
                sb.AppendLine("        <Z:Win32FileAttributes>00000010</Z:Win32FileAttributes>");
                sb.AppendLine($"        <Z:Win32CreationTime>{pCreated:R}</Z:Win32CreationTime>");
                sb.AppendLine($"        <Z:Win32LastAccessTime>{pModified:R}</Z:Win32LastAccessTime>");
                sb.AppendLine($"        <Z:Win32LastModifiedTime>{pModified:R}</Z:Win32LastModifiedTime>");
                sb.AppendLine("      </D:prop>");
                sb.AppendLine("      <D:status>HTTP/1.1 200 OK</D:status>");
                sb.AppendLine("    </D:propstat>");
                sb.AppendLine("  </D:response>");
            }
        }

        sb.AppendLine("</D:multistatus>");

        ctx.Response.StatusCode = 207;
        await WriteXmlAsync(ctx, sb.ToString());
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
        // xmlns:Z обязателен — Windows WebDAV Mini-Redirector требует этот namespace для Win32-свойств
        sb.AppendLine("<D:multistatus xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\">");

        // для коллекций href всегда с trailing slash (IIS-поведение; Windows WebDAV ожидает именно это)
        var reqPath = ctx.Request.Path.Value ?? "";
        var requestHref = isDir
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}{reqPath.TrimEnd('/')}/"
            : $"{ctx.Request.Scheme}://{ctx.Request.Host}{reqPath}";

        // Передаём активную блокировку в PROPFIND-ответ (lockdiscovery)
        static DavLock? GetActiveLock(string key) =>
            _locks.TryGetValue(key, out var lck) && lck.Expires > DateTime.UtcNow ? lck : null;

        var rootLockKey = string.IsNullOrEmpty(relPath) ? projectName : $"{projectName}/{relPath}";
        AppendResponse(sb, requestHref, absPath, isDir, GetActiveLock(rootLockKey));

        // дочерние элементы (depth=1, только для папок)
        if (isDir && depth == "1")
        {
            foreach (var entry in files.List(root, relPath))
            {
                var childAbs = FileService.SafeJoinPublic(root, entry.Path);
                var childHref = BuildHref(ctx, projectName, entry.Path, entry.IsDirectory);
                AppendResponse(sb, childHref, childAbs, entry.IsDirectory, GetActiveLock($"{projectName}/{entry.Path}"));
            }
        }

        sb.AppendLine("</D:multistatus>");

        ctx.Response.StatusCode = 207;
        await WriteXmlAsync(ctx, sb.ToString());
    }

    private static void AppendResponse(StringBuilder sb, string href, string absPath, bool isDir, DavLock? activeLock = null)
    {
        // displayName берём из последнего сегмента href (декодированного)
        var displayName = XmlEscape(Uri.UnescapeDataString(href.TrimEnd('/').Split('/').Last()));

        string lastModified, createdDate, createdDateRfc, win32Attrs, etag = "", contentLength = "", contentType = "";

        if (isDir)
        {
            var info = new DirectoryInfo(absPath);
            lastModified   = info.LastWriteTimeUtc.ToString("R");
            createdDate    = info.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
            createdDateRfc = info.CreationTimeUtc.ToString("R");
            win32Attrs     = "00000010"; // FILE_ATTRIBUTE_DIRECTORY — фиксированное значение для коллекций
        }
        else
        {
            var info = new FileInfo(absPath);
            lastModified   = info.LastWriteTimeUtc.ToString("R");
            createdDate    = info.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
            createdDateRfc = info.CreationTimeUtc.ToString("R");
            win32Attrs     = ((uint)info.Attributes).ToString("X8");
            contentLength  = info.Length.ToString();
            contentType    = XmlEscape(GetMimeType(info.Name));
            etag           = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
        }

        sb.AppendLine("  <D:response>");
        sb.AppendLine($"    <D:href>{href}</D:href>");
        sb.AppendLine("    <D:propstat>");
        sb.AppendLine("      <D:prop>");
        sb.AppendLine($"        <D:displayname>{displayName}</D:displayname>");

        // supportedlock и lockdiscovery для файлов и папок
        var lockBlock = new System.Text.StringBuilder();
        lockBlock.AppendLine("        <D:supportedlock>");
        lockBlock.AppendLine("          <D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>");
        lockBlock.AppendLine("          <D:lockentry><D:lockscope><D:shared/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>");
        lockBlock.AppendLine("        </D:supportedlock>");
        if (activeLock != null && activeLock.Expires > DateTime.UtcNow)
        {
            lockBlock.AppendLine("        <D:lockdiscovery>");
            lockBlock.AppendLine("          <D:activelock>");
            lockBlock.AppendLine("            <D:locktype><D:write/></D:locktype>");
            lockBlock.AppendLine("            <D:lockscope><D:exclusive/></D:lockscope>");
            lockBlock.AppendLine("            <D:depth>0</D:depth>");
            lockBlock.AppendLine($"            <D:owner><D:href>{activeLock.Owner}</D:href></D:owner>");
            lockBlock.AppendLine("            <D:timeout>Second-3600</D:timeout>");
            lockBlock.AppendLine($"            <D:locktoken><D:href>{activeLock.Token}</D:href></D:locktoken>");
            lockBlock.AppendLine($"            <D:lockroot><D:href>{href}</D:href></D:lockroot>");
            lockBlock.AppendLine("          </D:activelock>");
            lockBlock.AppendLine("        </D:lockdiscovery>");
        }
        else
        {
            lockBlock.AppendLine("        <D:lockdiscovery/>");
        }

        if (isDir)
        {
            sb.AppendLine("        <D:resourcetype><D:collection/></D:resourcetype>");
            sb.Append(lockBlock);
        }
        else
        {
            sb.AppendLine("        <D:resourcetype/>");
            sb.AppendLine($"        <D:getcontentlength>{contentLength}</D:getcontentlength>");
            sb.AppendLine($"        <D:getcontenttype>{contentType}</D:getcontenttype>");
            sb.AppendLine($"        <D:getetag>{etag}</D:getetag>");
            sb.Append(lockBlock);
        }

        sb.AppendLine($"        <D:getlastmodified>{lastModified}</D:getlastmodified>");
        sb.AppendLine($"        <D:creationdate>{createdDate}</D:creationdate>");
        // Win32-свойства — Windows WebDAV Mini-Redirector требует их в namespace Z для корректного монтирования
        sb.AppendLine($"        <Z:Win32FileAttributes>{win32Attrs}</Z:Win32FileAttributes>");
        sb.AppendLine($"        <Z:Win32CreationTime>{createdDateRfc}</Z:Win32CreationTime>");
        sb.AppendLine($"        <Z:Win32LastAccessTime>{lastModified}</Z:Win32LastAccessTime>");
        sb.AppendLine($"        <Z:Win32LastModifiedTime>{lastModified}</Z:Win32LastModifiedTime>");
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

        ctx.Response.StatusCode = 207;
        await WriteXmlAsync(ctx, xml);
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

        // Читаем owner из тела запроса (пустое тело = refresh существующей блокировки)
        var owner = "";
        var hasBody = false;
        try
        {
            using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await sr.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                hasBody = true;
                // ищем <D:href>...</D:href> или <href>...</href> внутри owner
                var ownerMatch = System.Text.RegularExpressions.Regex.Match(
                    body, @"<[^>]*:?href[^>]*>(.*?)</[^>]*:?href[^>]*>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (ownerMatch.Success)
                    owner = XmlEscape(ownerMatch.Groups[1].Value);
            }
        }
        catch { /* тело может быть пустым */ }

        var lockKey = $"{projectName}/{relPath}";

        // Проверяем существующую блокировку
        if (_locks.TryGetValue(lockKey, out var existing) && existing.Expires > DateTime.UtcNow)
        {
            // Refresh: пустое тело + If-заголовок с нашим токеном
            var ifHeader = ctx.Request.Headers["If"].ToString();
            var ifToken = System.Text.RegularExpressions.Regex.Match(ifHeader, @"<(urn:[^>]+)>").Groups[1].Value;
            if (!hasBody && ifToken == existing.Token)
            {
                // Продлеваем блокировку, возвращаем тот же токен
                _locks[lockKey] = existing with { Expires = DateTime.UtcNow.AddHours(1) };
                var refreshHref = BuildHref(ctx, projectName, relPath, false);
                var refreshXml = $"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <D:prop xmlns:D="DAV:">
                      <D:lockdiscovery>
                        <D:activelock>
                          <D:locktype><D:write/></D:locktype>
                          <D:lockscope><D:exclusive/></D:lockscope>
                          <D:depth>0</D:depth>
                          <D:owner><D:href>{existing.Owner}</D:href></D:owner>
                          <D:timeout>Second-3600</D:timeout>
                          <D:locktoken><D:href>{existing.Token}</D:href></D:locktoken>
                          <D:lockroot><D:href>{refreshHref}</D:href></D:lockroot>
                        </D:activelock>
                      </D:lockdiscovery>
                    </D:prop>
                    """;
                ctx.Response.StatusCode = 200;
                ctx.Response.Headers["Lock-Token"] = $"<{existing.Token}>";
                await WriteXmlAsync(ctx, refreshXml);
                return;
            }

            // Файл занят другим клиентом — 423 Locked
            ctx.Response.StatusCode = 423;
            ctx.Response.ContentLength = 0;
            return;
        }

        // Новая блокировка
        // Клиент сначала блокирует несуществующий файл, потом пишет содержимое через PUT
        if (!existed && !Directory.Exists(absPath))
            files.CreateFile(root, relPath);

        var token = "urn:uuid:" + Guid.NewGuid();
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
        await WriteXmlAsync(ctx, xml);
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
    /// Строит абсолютный URI для href в PROPFIND-ответе.
    /// Windows WebDAV Mini-Redirector требует абсолютные URI — относительные пути не принимает.
    /// </summary>
    private static string BuildHref(HttpContext ctx, string projectName, string relPath, bool isDir)
    {
        var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase.ToString().TrimEnd('/')}";
        var segments = string.IsNullOrEmpty(relPath)
            ? Array.Empty<string>()
            : relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var encoded = string.Join("/", segments.Select(Uri.EscapeDataString));
        var path = $"/projects/{Uri.EscapeDataString(projectName)}" +
                   (encoded.Length > 0 ? $"/{encoded}" : "") +
                   (isDir ? "/" : "");
        return origin + path;
    }

    /// <summary>
    /// Извлекает относительный путь назначения из заголовка Destination.
    /// Заголовок содержит полный URL: http://host/projects/{projectName}/{path}
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
            // ищем /projects/{projectName}/ в пути
            var prefix = $"/projects/{projectName}/";
            var idx = uriPath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            return uriPath[(idx + prefix.Length)..].Trim('/');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Записывает XML-ответ с явным Content-Length, чтобы Windows WebClient
    /// не получал chunked transfer encoding (вызывает ошибку 59).
    /// </summary>
    private static async Task WriteXmlAsync(HttpContext ctx, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.ContentType   = "text/xml; charset=\"utf-8\"";
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.Headers["DAV"]           = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        await ctx.Response.Body.WriteAsync(bytes);
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
