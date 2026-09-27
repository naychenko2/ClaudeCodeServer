using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.DynamicModules;

// Статика MF-remote (/{имя}-remote/*): где она лежит на диске, чем сбрасывать кэш
// remoteEntry.js и какие заголовки кэша ей отдавать.
//
// Версия сброса кэша — хеш СОДЕРЖИМОГО remoteEntry.js, а не версия манифеста: у модулей
// Version годами «1.0.0», URL не менялся от сборки к сборке, и браузер после выкатки
// держал старый remoteEntry.js со ссылками на уже удалённые хешированные чанки.
public sealed class RemoteStaticFiles
{
    // Корень статики фронта. Пусто — боевой wwwroot рядом с exe, а для dev-стенда ещё и
    // frontend/dist (там remote лежат внутри dist и раздаются общей статикой).
    public const string RootKey = "Frontend:StaticRoot";

    private readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, string Hash)> _hashes = new();

    public IReadOnlyList<string> Roots { get; }

    public RemoteStaticFiles(IConfiguration config)
    {
        var configured = config[RootKey];
        Roots = !string.IsNullOrWhiteSpace(configured)
            ? [configured]
            : [PrimaryRoot(config), Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "frontend", "dist"))];
    }

    /// <summary>Каталог, из которого Program.cs раздаёт папки MF-remote.</summary>
    public static string PrimaryRoot(IConfiguration config) =>
        config[RootKey] is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "wwwroot");

    /// <summary>
    /// Короткий SHA-256 файла по относительному URL («/notes-remote/remoteEntry.js»);
    /// null — URL не относительный или файла нет ни в одном корне. Пересчитывается,
    /// только когда у файла сменились время записи или размер.
    /// </summary>
    public string? ContentVersion(string url)
    {
        if (!url.StartsWith('/') || url.StartsWith("//")) return null;
        var relative = url.Split('?', '#')[0];
        foreach (var root in Roots)
        {
            string path;
            try { path = SafePath.Join(root, relative); }
            catch (UnauthorizedAccessException) { return null; }
            var info = new FileInfo(path);
            if (!info.Exists) continue;
            if (_hashes.TryGetValue(path, out var cached)
                && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
                return cached.Hash;
            string hash;
            try
            {
                using var stream = info.OpenRead();
                hash = Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
            }
            catch (IOException) { return null; }
            _hashes[path] = (info.LastWriteTimeUtc, info.Length, hash);
            return hash;
        }
        return null;
    }

    public static string WithVersion(string url, string version) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}v={Uri.EscapeDataString(version)}";

    /// <summary>
    /// Заголовки кэша статики фронта: точки входа (index.html, SW, remoteEntry.js любых
    /// remote) — всегда ревалидировать; хешированные чанки хоста и remote — «вечно».
    /// </summary>
    public static void ApplyCacheHeaders(PathString requestPath, string fileName, IHeaderDictionary headers)
    {
        if (fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("sw.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("registerSW.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("remoteEntry.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
            headers.Expires = "0";
        }
        else if (requestPath.StartsWithSegments("/assets") || IsRemoteAsset(requestPath))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    }

    // /{имя}-remote/assets/** — чанки remote с хешами в именах
    private static bool IsRemoteAsset(PathString path)
    {
        var segments = (path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3
            && segments[0].EndsWith("-remote", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("assets", StringComparison.OrdinalIgnoreCase);
    }
}
