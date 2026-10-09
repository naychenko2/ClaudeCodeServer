using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.ImageEditor;

// Пространство имён LoRA владельца: веса лежат в `ccs/<ownerId:N>/…` внутри models/loras.
// Список LoRA ComfyUI общий на инстанс, поэтому видимость решает только эта функция —
// проверку префикса больше нигде не дублировать.
public static class LoraNamespace
{
    private const string Root = "ccs/";

    private static bool IsOwnerIdChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';

    private static bool IsSegmentChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';

    // Префикс владельца: `ccs/<id без дефисов, нижний регистр>/`; из мусора не строим
    public static string PrefixFor(string ownerId)
    {
        var id = (ownerId ?? "").ToLowerInvariant();
        if (id.Length == 0 || !id.All(IsOwnerIdChar))
            throw new ArgumentException("Недопустимый идентификатор владельца", nameof(ownerId));
        return Root + id.Replace("-", "") + "/";
    }

    // Видна ли LoRA владельцу: имя строго внутри его префикса, без обхода пути
    public static bool VisibleTo(string ownerId, string? loraName)
    {
        if (string.IsNullOrWhiteSpace(loraName)) return false;
        string prefix;
        try { prefix = PrefixFor(ownerId); }
        catch (ArgumentException) { return false; }

        if (!loraName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var tail = loraName[prefix.Length..];
        if (tail.Length == 0) return false;
        foreach (var segment in tail.Split('/'))
        {
            if (segment is "" or "." or ".." || !segment.All(IsSegmentChar)) return false;
        }
        return true;
    }

    private static readonly Regex RunIdRegex = new("^[0-9a-f]{12}$", RegexOptions.Compiled);
    private static readonly Regex SlugRegex = new("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.Compiled);

    // Ключ владельца/проекта: Guid без дефисов, нижний регистр (32 hex); не Guid — отказ
    public static string OwnerKey(string id) =>
        Guid.TryParse(id, out var guid)
            ? guid.ToString("N")
            : throw new ArgumentException("Идентификатор не Guid", nameof(id));

    public static bool IsValidRunId(string? runId) => runId is not null && RunIdRegex.IsMatch(runId);

    public static bool IsValidSlug(string? slug) => slug is not null && SlugRegex.IsMatch(slug);

    // Каталог прогона в models/loras, со слэшем в конце: `ccs/<ownerKey>/<slug>-<runId>/`
    public static string RunDirectory(string ownerId, string slug, string runId)
    {
        Check(slug, runId);
        return $"ccs/{OwnerKey(ownerId)}/{slug}-{runId}/";
    }

    // Имя весов, как его отдаёт GET /models/loras
    public static string RunLoraFile(string ownerId, string slug, string runId) =>
        $"{RunDirectory(ownerId, slug, runId)}{slug}.safetensors";

    // Подпапка input ComfyUI для фото прогона, без слэша в конце
    public static string RunInputFolder(string ownerId, string runId)
    {
        if (!IsValidRunId(runId)) throw new ArgumentException("Недопустимый runId", nameof(runId));
        return $"ccs/{OwnerKey(ownerId)}/{runId}";
    }

    private static void Check(string slug, string runId)
    {
        if (!IsValidSlug(slug)) throw new ArgumentException("Недопустимый slug", nameof(slug));
        if (!IsValidRunId(runId)) throw new ArgumentException("Недопустимый runId", nameof(runId));
    }
}
