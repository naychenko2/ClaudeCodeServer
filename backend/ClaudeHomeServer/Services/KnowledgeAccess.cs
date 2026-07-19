namespace ClaudeHomeServer.Services;

// Чистая логика изоляции per-owner для раздела «Знания» (вынесена из
// KnowledgeBasesController ради тестируемости). Решения принимаются по ИМЕНИ датасета
// Dify (общий ключ — источник истины один на всех), поэтому именно здесь держится
// граница: чужая помеченная база не relevant (→ 403), привязанная — не deletable (→ 403).
public static class KnowledgeAccess
{
    // Владелец датасета по префиксу имени ({username}:), либо null — глобальная (без префикса).
    public static string? OwnerOf(string name, string username, IReadOnlySet<string> others)
    {
        if (name.StartsWith(username + ":", StringComparison.OrdinalIgnoreCase)) return username;
        foreach (var u in others)
            if (name.StartsWith(u + ":", StringComparison.OrdinalIgnoreCase)) return u;
        return null;
    }

    // Доступна ли база пользователю на чтение: своя или глобальная — да; чужая помеченная — нет.
    // Своя память персоны/команды — внутренняя (управляется своим разделом), в «Знаниях» не видна.
    public static bool IsRelevant(string name, string username, IReadOnlySet<string> others)
    {
        var owner = OwnerOf(name, username, others);
        if (owner is null) return true;                                                 // глобальная
        if (!owner.Equals(username, StringComparison.OrdinalIgnoreCase)) return false;  // чужая
        var rest = name[(username.Length + 1)..];
        return !rest.StartsWith("persona:", StringComparison.Ordinal)
            && !rest.StartsWith("team:", StringComparison.Ordinal);
    }

    // Можно ли удалить базу из раздела: самостоятельные ({user}:kb:…) и публичные (без префикса,
    // только админ); привязанные (заметок/проектов/персон/команды) и чужие — нельзя.
    public static bool IsDeletable(string name, string username, IReadOnlySet<string> others, bool isAdmin)
    {
        var owner = OwnerOf(name, username, others);
        if (owner is null) return isAdmin;                                              // глобальная — только админ
        if (!owner.Equals(username, StringComparison.OrdinalIgnoreCase)) return false;  // чужая (не видна)
        var rest = name[(username.Length + 1)..];
        if (rest.StartsWith("team:", StringComparison.Ordinal)) return false;           // память команды — внутренняя
        return rest.StartsWith("kb:", StringComparison.Ordinal);                        // самостоятельная
    }
}
