using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// «Устарел» и «● обновлена» (ADR-022 §2) ВЫЧИСЛЯЮТСЯ, а не хранятся: из builds[].sourceHash и из времени файлов.
// Хеш сборки — отпечаток того, что в неё шло: соотношение сторон, строки (файл, обрезка, размер файла),
// склейки и музыка. Файлы сцен создаются заново с новым именем (CreateNew), поэтому размера достаточно, чтобы
// подмена файла на месте тоже меняла хеш.
public static class FilmStaleness
{
    public static string SourceHash(string projectRoot, FilmDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("aspect=").Append(doc.Aspect).Append('\n');
        foreach (var item in doc.Items)
            sb.Append("item=").Append(item.File).Append('|').Append(Num(item.Trim, 0)).Append('|').Append(Num(item.Trim, 1))
              .Append('|').Append(SizeOf(projectRoot, item.File)).Append('\n');
        foreach (var cut in doc.Cuts) sb.Append("cut=").Append(cut.Type).Append('|').Append(cut.Sec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if (doc.Music is { } m)
            sb.Append("music=").Append(m.File).Append('|').Append(m.Volume).Append('|')
              .Append(m.FadeOut.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
              .Append(SizeOf(projectRoot, m.File)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12].ToLowerInvariant();
    }

    // Фильм устарел: сборки ещё не было или последняя шла из других входов. Пустой фильм собирать нечего — не устарел
    public static bool IsStale(string projectRoot, FilmDocument doc) =>
        doc.Items.Count > 0 && (doc.Builds.Count == 0 || doc.Builds[^1].SourceHash != SourceHash(projectRoot, doc));

    // «● обновлена»: файл сцены новее последней сборки (сборки не было — новее всё)
    public static bool IsUpdated(string projectRoot, FilmDocument doc, FilmItem item)
    {
        if (doc.Builds.Count == 0) return true;
        return ResolveFile(projectRoot, item.File) is { } full && File.Exists(full)
            && File.GetLastWriteTimeUtc(full) > doc.Builds[^1].At.ToUniversalTime();
    }

    private static string? ResolveFile(string root, string relative) => ProjectLinkGuard.ResolveInside(root, relative);

    private static long SizeOf(string root, string relative) =>
        ResolveFile(root, relative) is { } full && File.Exists(full) ? new FileInfo(full).Length : -1;

    private static string Num(IReadOnlyList<double> trim, int i) =>
        trim.Count > i ? trim[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "";
}
