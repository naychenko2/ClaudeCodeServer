using ClaudeHomeServer.Controllers;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Каталог баз знаний Dify под пользователя: список релевантных (личные + публичные),
/// классификация по префиксу имени и резолв с проверкой доступности. Общая точка ДВУХ
/// потребителей: KnowledgeBasesController (REST) и WorkspaceToolset (http-ветка MCP,
/// ADR-012 волна 3) — дубль классификации в тулсете гарантировал бы рассинхрон при
/// появлении новых типов датасетов.
///
/// Username — параметр, а не claim: у REST это ClaimTypes.Name, у MCP-вызова — Username
/// из UserStore по владельцу токена (сервисный JWT может не нести Name).
/// </summary>
public sealed class KnowledgeBaseCatalogService(KnowledgeService knowledge, UserStore userStore)
{
    // Имена других пользователей — чтобы отличить «без префикса = глобальная»
    // от «чужая {otheruser}:…» (иначе чужие утекли бы в публичные).
    public HashSet<string> OtherUsers(string username) =>
        userStore.GetAll()
            .Select(u => u.Username)
            .Where(u => u.Length > 0 && !u.Equals(username, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Владелец датасета по префиксу имени ({username}:), или null — глобальная (без префикса).
    private static string? OwnerOf(string name, string username, HashSet<string> others) =>
        KnowledgeAccess.OwnerOf(name, username, others);

    private static bool IsRelevant(DifyDatasetListItem d, string username, HashSet<string> others) =>
        KnowledgeAccess.IsRelevant(d.Name ?? "", username, others);

    /// <summary>Удаляема ли база этим пользователем (самостоятельная/публичная с правами).</summary>
    public bool IsDeletable(DifyDatasetListItem d, string username, bool isAdmin) =>
        KnowledgeAccess.IsDeletable(d.Name ?? "", username, OtherUsers(username), isAdmin);

    /// <summary>
    /// Резолв датасета по id с проверкой доступности (relevant): своя или глобальная —
    /// доступна; чужая помеченная — нет. Обязательно с общим Dify-ключом: иначе по
    /// произвольному id можно читать/менять чужую базу.
    /// </summary>
    public async Task<DifyDatasetListItem?> ResolveReadableAsync(string username, string id)
    {
        if (!knowledge.IsConfigured || string.IsNullOrEmpty(id)) return null;
        try
        {
            var others = OtherUsers(username);
            return (await knowledge.ListDatasetsAsync()).FirstOrDefault(d => d.Id == id)
                is { } found && IsRelevant(found, username, others) ? found : null;
        }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Список релевантных пользователю баз (личные + публичные), свежие первыми.</summary>
    public async Task<(bool Configured, IReadOnlyList<KnowledgeBaseSummary> Items)> ListForUserAsync(
        string username)
    {
        if (!knowledge.IsConfigured)
            return (false, Array.Empty<KnowledgeBaseSummary>());
        var all = await knowledge.ListDatasetsAsync();
        var others = OtherUsers(username);
        var items = all.Select(d => Classify(d, username, others))
            .Where(x => x is not null)
            .Cast<KnowledgeBaseSummary>()
            .OrderByDescending(x => x.CreatedAt ?? DateTime.MinValue)
            .ToList();
        return (true, items);
    }

    /// <summary>Карточка базы с документами (null — недоступна/не найдена).</summary>
    public async Task<KnowledgeBaseDetail?> GetDetailForUserAsync(string username, string id)
    {
        var d = await ResolveReadableAsync(username, id);
        if (d is null) return null;
        var c = Classify(d, username, OtherUsers(username))!;
        var docs = await knowledge.ListAllDocumentsAsync(id);
        return new KnowledgeBaseDetail(
            c.Id, c.Title, c.Type, c.Visibility, c.DocumentCount, c.CreatedAt, c.Deletable, c.Description,
            docs.Data.Select(x => new KnowledgeDocumentDto(x.Id, x.Name, x.IndexingStatus, x.Error)).ToList());
    }

    /// <summary>Сводка с производными полями или null, если датасет чужой (скрытый).</summary>
    public KnowledgeBaseSummary? Classify(DifyDatasetListItem d, string username, HashSet<string> others)
    {
        var name = d.Name ?? "";
        var owner = OwnerOf(name, username, others);
        if (owner is not null && !owner.Equals(username, StringComparison.OrdinalIgnoreCase))
            return null; // чужая помеченная — не показываем

        string type; string title; bool deletable; string visibility;
        if (owner is null)
        {
            type = "Публичная"; title = name; deletable = true; visibility = "public";
        }
        else
        {
            var rest = name[(username.Length + 1)..];
            if (rest == "notes") { type = "Заметки"; title = "Заметки"; deletable = false; }
            else if (rest.StartsWith("persona:", StringComparison.Ordinal)) return null; // память персоны — внутренняя, скрыта
            else if (rest.StartsWith("team:", StringComparison.Ordinal)) return null;     // память команды — внутренняя, скрыта
            else if (rest.StartsWith("kb:", StringComparison.Ordinal)) { type = "Самостоятельная"; title = rest["kb:".Length..]; deletable = true; }
            else { type = "Проект"; title = rest; deletable = false; } // {username}:{projectName}
            visibility = "personal";
        }

        return new KnowledgeBaseSummary(d.Id, title, type, visibility,
            d.DocumentCount, ToDate(d.CreatedAt), deletable, d.Description);
    }

    private static DateTime? ToDate(double? ts) =>
        ts is { } v && v > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)v).UtcDateTime : null;
}
