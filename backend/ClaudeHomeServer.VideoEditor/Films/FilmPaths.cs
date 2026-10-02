using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services.VideoEditor.Controllers;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Пути фильмов и файлов сцен (ADR-022 §2): все — от корня проекта через «/», писать можно только в video/** и
// music/**. Проверка границы проекта и символических ссылок — ProjectLinkGuard.ResolveInside у вызывающего;
// здесь только форма пути.
internal static class FilmPaths
{
    public const string FilmExtension = ".film";
    public const string VideoRoot = VideoEditorEndpoints.ScenesRoot;
    public const string MusicRoot = "music";

    // «\» → «/», крайние «/» срезаны; пусто — null
    public static string? Normalize(string? raw)
    {
        var value = (raw ?? "").Trim().Replace('\\', '/').Trim('/');
        return value.Length == 0 ? null : value;
    }

    // Внутри video/** или music/**, без «..» и пустых сегментов
    public static bool IsAllowed(string relative) => VideoEditorEndpoints.InsideAllowed(relative);

    // Файл фильма: video/<папка>/<имя>.film, не в самом корне video/
    public static bool IsFilmPath(string relative) =>
        IsAllowed(relative) && relative.StartsWith(VideoRoot + "/", StringComparison.Ordinal)
        && relative.Count(c => c == '/') >= 2
        && relative.EndsWith(FilmExtension, StringComparison.OrdinalIgnoreCase);

    // Файл клипа сцены: video/**, mp4 (сборка кодирует всё, но источник — готовый файл сцены)
    public static bool IsClipPath(string relative) =>
        IsAllowed(relative) && relative.StartsWith(VideoRoot + "/", StringComparison.Ordinal);

    public static bool IsMusicPath(string relative) =>
        IsAllowed(relative) && relative.StartsWith(MusicRoot + "/", StringComparison.Ordinal);

    public static string FolderOf(string relative)
    {
        var i = relative.LastIndexOf('/');
        return i < 0 ? "" : relative[..i];
    }

    public static string NameOf(string relative)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        return name.EndsWith(FilmExtension, StringComparison.OrdinalIgnoreCase) ? name[..^FilmExtension.Length] : name;
    }

    // Ключ состояния фильма вне файла: хеш пути (путь может быть любой длины и с любыми символами)
    public static string KeyOf(string relative) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..16].ToLowerInvariant();

    // Имя файла без пути и без «..»: для явного fileName запроса
    public static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 120 && name is not ("." or "..")
        && name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
