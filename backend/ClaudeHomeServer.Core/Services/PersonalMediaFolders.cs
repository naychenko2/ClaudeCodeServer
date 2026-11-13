using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.Composition;

// Подпапки «Личного» под сохранённые результаты медиа-редакторов (ADR-040 §5): Personal/<вид>/.
// Сохранение в личной области идёт только сюда: путь из запроса не может выйти из подпапки своего вида,
// а сам путь проходит ProjectLinkGuard (символическая ссылка наружу — отказ).
public static class PersonalMediaFolders
{
    public const string Images = "images";
    public const string Audio = "audio";
    public const string Video = "video";

    // Запрещённые символы по Windows: папка может переехать туда, и путь должен открыться
    private static readonly char[] WindowsForbidden = ['<', '>', ':', '"', '|', '?', '*'];

    /// <summary>
    /// Папка сохранения: пусто — сама подпапка вида; иначе путь обязан лежать внутри неё. null — путь годится
    /// (<paramref name="folder"/> нормализован), иначе текст отказа.
    /// </summary>
    public static string? Resolve(string kind, string? requested, out string folder)
    {
        folder = kind;
        var value = (requested ?? "").Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0 || value == ".") return null;
        if (value.Split('/').Any(s => s is "" or "." or ".." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                                      || s.IndexOfAny(WindowsForbidden) >= 0))
            return $"Путь вне папки «{kind}» в «Личном»";
        if (value != kind && !value.StartsWith(kind + "/", StringComparison.Ordinal))
            return $"В «Личное» сохраняют только в папку «{kind}»";
        folder = value;
        return null;
    }

    /// <summary>Путь лежит внутри подпапки вида (для входов, где папка уже нормализована).</summary>
    public static bool Allows(string kind, string? relative) => Resolve(kind, relative, out _) is null;

    /// <summary>
    /// Создаёт подпапку (вместе с вложенными) и отдаёт её полный путь. null — путь вне корня или через
    /// символическую ссылку, либо папку создать нельзя.
    /// </summary>
    public static string? EnsureDirectory(string root, string folder)
    {
        if (ProjectLinkGuard.ResolveInside(root, folder) is not { } full) return null;
        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        // Повторная проверка после создания: ссылку могли подставить между проверкой и записью
        return ProjectLinkGuard.HasLink(root, full) ? null : full;
    }
}
