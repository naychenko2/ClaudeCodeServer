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
}
