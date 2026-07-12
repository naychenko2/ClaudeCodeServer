using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

// Раздел «Знания»: единый менеджер баз знаний Dify, релевантных пользователю
// (его личные + публичные). Dify — источник истины (отдельного JSON-стора нет):
// список берём из KnowledgeService.ListDatasetsAsync(), классифицируем по имени
// (префиксу пользователя). «Помеченные» ({user}:…) — личные; «без префикса» — публичные/
// глобальные (видны всем); чужие ({otheruser}:…) — скрыты через список пользователей.
// Самостоятельные ({user}:kb:…) и публичные можно создавать и удалять; привязанные
// (заметок/проектов/памяти персон) — только управлять документами. Не путать с
// проектным KnowledgeController'ом (маршрут /api/projects/{id}/knowledge).
[ApiController]
[Authorize]
[Route("api/knowledge")]
public class KnowledgeBasesController(KnowledgeService knowledge, IHubContext<SessionHub> hub, UserStore userStore) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;
    private string Username => User.FindFirstValue(ClaimTypes.Name) ?? UserId;
    private bool IsAdmin => User.IsInRole("admin");

    private Task Broadcast(string action, string? datasetId = null) =>
        hub.Clients.Group("user_" + UserId).SendAsync("message", new KnowledgeChangedMessage(action, datasetId));

    // GET /api/knowledge — список релевантных пользователю баз (личные + публичные),
    // отсортированный по新鲜ести. configured=false — Dify не настроен (фронт показывает empty-state).
    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!knowledge.IsConfigured)
            return Ok(new { configured = false, items = Array.Empty<KnowledgeBaseSummary>() });
        try
        {
            var all = await knowledge.ListDatasetsAsync();
            var others = OtherUsers();
            var items = all.Select(d => Classify(d, others))
                .Where(x => x is not null)
                .Cast<KnowledgeBaseSummary>()
                .OrderByDescending(x => x.CreatedAt ?? DateTime.MinValue)
                .ToList();
            return Ok(new { configured = true, items });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // GET /api/knowledge/{id} — база + её документы. Доступ — только relevant.
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        var c = Classify(d, OtherUsers())!;
        try
        {
            var docs = await knowledge.ListAllDocumentsAsync(id);
            return Ok(new KnowledgeBaseDetail(
                c.Id, c.Title, c.Type, c.Visibility, c.DocumentCount, c.CreatedAt, c.Deletable, c.Description,
                docs.Data.Select(x => new KnowledgeDocumentDto(x.Id, x.Name, x.IndexingStatus)).ToList()));
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // POST /api/knowledge — создать самостоятельную (личную) или публичную базу.
    // Имя по схеме: личная → "{username}:kb:{title}", публичная → "{title}" (без префикса).
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateKnowledgeBaseRequest req)
    {
        if (!knowledge.IsConfigured) return BadRequest(new { error = "Dify не настроен" });
        var title = (req?.Title ?? "").Trim();
        if (title.Length == 0) return BadRequest(new { error = "Не задано название" });
        var public_ = string.Equals(req?.Visibility, "public", StringComparison.OrdinalIgnoreCase);
        var name = public_ ? title : $"{Username}:kb:{title}";
        var permission = public_ ? "all_team_members" : "only_me";
        var description = string.IsNullOrWhiteSpace(req?.Description) ? null : req.Description.Trim();
        try
        {
            var datasetId = await knowledge.CreateDatasetAsync(name, permission, description);
            await Broadcast("created", datasetId);
            return Ok(new { id = datasetId, title, visibility = public_ ? "public" : "personal" });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // DELETE /api/knowledge/{id} — удалить базу. Только deletable (самостоятельная/публичная);
    // привязанные (заметок/проектов/персон) — 403, их удаляют разделы-владельцы.
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        if (!IsDeletable(d, OtherUsers())) return StatusCode(403, new { error = "Удаление этой базы недоступно: она привязана к другому разделу, либо для публичной базы нужны права администратора" });
        try { await knowledge.DeleteDatasetAsync(id); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
        await Broadcast("deleted", id);
        return NoContent();
    }

    // POST /api/knowledge/{id}/documents — добавить документ текстом.
    [HttpPost("{id}/documents")]
    public async Task<IActionResult> AddDocumentText(string id, [FromBody] AddDocumentTextRequest req)
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        var name = (req?.Name ?? "").Trim();
        var text = req?.Text ?? "";
        if (name.Length == 0) return BadRequest(new { error = "Не задано имя документа" });
        if (text.Length == 0) return BadRequest(new { error = "Пустой текст" });
        try
        {
            var doc = await knowledge.IndexFileByTextAsync(id, name, text);
            await Broadcast("doc_changed", id);
            return Ok(new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // POST /api/knowledge/{id}/documents/file — загрузить документ файлом (multipart).
    [HttpPost("{id}/documents/file")]
    public async Task<IActionResult> AddDocumentFile(string id, IFormFile? file, [FromForm] string? name)
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest(new { error = "Файл не передан" });
        var fileName = string.IsNullOrWhiteSpace(name) ? file.FileName : name!;
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        try
        {
            var doc = await knowledge.IndexFileByBytesAsync(id, fileName, ms.ToArray());
            await Broadcast("doc_changed", id);
            return Ok(new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // DELETE /api/knowledge/{id}/documents/{docId} — удалить документ из базы.
    [HttpDelete("{id}/documents/{docId}")]
    public async Task<IActionResult> DeleteDocument(string id, string docId)
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        try { await knowledge.DeleteDocumentAsync(id, docId); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
        await Broadcast("doc_changed", id);
        return NoContent();
    }

    // GET /api/knowledge/{id}/documents/{docId} — содержимое документа (сегменты-чанки).
    [HttpGet("{id}/documents/{docId}")]
    public async Task<IActionResult> GetDocument(string id, string docId)
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        try
        {
            var segments = await knowledge.ListSegmentsAsync(id, docId);
            return Ok(new
            {
                id = docId,
                segments = segments.OrderBy(s => s.Position)
                    .Select(s => new KnowledgeSegmentDto(s.Position, s.Content, s.WordCount)).ToList(),
            });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // GET /api/knowledge/{id}/search — семантический (method=semantic) либо
    // полнотекстовый (method=fulltext) поиск по базе.
    [HttpGet("{id}/search")]
    public async Task<IActionResult> Search(string id, [FromQuery] string q, [FromQuery] int topK = 8, [FromQuery] string method = "semantic")
    {
        var d = await ResolveReadableAsync(id);
        if (d is null) return NotFound();
        if (string.IsNullOrWhiteSpace(q)) return Ok(new { items = Array.Empty<KnowledgeSearchHit>() });
        // semantic → чисто по смыслу; fulltext → точные совпадения. Гибрид здесь не нужен:
        // переключатель на фронте явно выбирает одну из стратегий.
        var searchMethod = string.Equals(method, "fulltext", StringComparison.OrdinalIgnoreCase)
            ? "full_text_search" : "semantic_search";
        try
        {
            var chunks = await knowledge.RetrieveAsync(id, q.Trim(), Math.Clamp(topK, 1, 20), searchMethod: searchMethod);
            return Ok(new { items = chunks.Select(c => new KnowledgeSearchHit(c.Score, c.Content, c.DocumentName)).ToList() });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // --- Классификация датасетов Dify под пользователя ---
    // Модель: «помеченные» (с префиксом {username}:) — личные; «без префикса» —
    // публичные/глобальные (видны всем). Личные делятся по префиксу:
    // {user}:notes / {user}:persona:{handle} / {user}:kb:{Title} / {user}:{project}.
    // Чужие личные ({otheruser}:…) — скрыты (изоляция per-owner). Permission Dify
    // здесь ни при чём: «публичность» определяется отсутствием префикса, а не all_team_members.

    // Имена других пользователей — чтобы отличить «без префикса = глобальная»
    // от «чужая {otheruser}:…» (иначе чужие утекли бы в публичные).
    private HashSet<string> OtherUsers() =>
        userStore.GetAll()
            .Select(u => u.Username)
            .Where(u => u.Length > 0 && !u.Equals(Username, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Владелец датасета по префиксу имени ({username}:), или null — глобальная (без префикса).
    private static string? OwnerOf(string name, string username, HashSet<string> others)
    {
        if (name.StartsWith(username + ":", StringComparison.OrdinalIgnoreCase)) return username;
        foreach (var u in others)
            if (name.StartsWith(u + ":", StringComparison.OrdinalIgnoreCase)) return u;
        return null;
    }

    // Резолв датасета по id с проверкой доступности (relevant): своя или глобальная —
    // доступна; чужая помеченная — нет. Обязательно с общим Dify-ключом: иначе по
    // произвольному id можно читать/менять чужую базу.
    private async Task<DifyDatasetListItem?> ResolveReadableAsync(string id)
    {
        if (!knowledge.IsConfigured || string.IsNullOrEmpty(id)) return null;
        try
        {
            var others = OtherUsers();
            return (await knowledge.ListDatasetsAsync()).FirstOrDefault(d => d.Id == id)
                is { } found && IsRelevant(found, others) ? found : null;
        }
        catch (HttpRequestException) { return null; }
    }

    private bool IsRelevant(DifyDatasetListItem d, HashSet<string> others)
    {
        var owner = OwnerOf(d.Name ?? "", Username, others);
        return owner is null || owner.Equals(Username, StringComparison.OrdinalIgnoreCase);
    }

    // Удалять здесь можно самостоятельные ({user}:kb:…) и публичные (без префикса);
    // привязанные (заметок/проектов/персон) — нельзя.
    private bool IsDeletable(DifyDatasetListItem d, HashSet<string> others)
    {
        var name = d.Name ?? "";
        var owner = OwnerOf(name, Username, others);
        if (owner is null) return IsAdmin;                                              // глобальная — только админ
        if (!owner.Equals(Username, StringComparison.OrdinalIgnoreCase)) return false;  // чужая (не видна)
        var rest = name[(Username.Length + 1)..];                                       // после "{user}:"
        return rest.StartsWith("kb:", StringComparison.Ordinal);                        // самостоятельная
    }

    // Сводка с производными полями или null, если датасет чужой (скрытый).
    private KnowledgeBaseSummary? Classify(DifyDatasetListItem d, HashSet<string> others)
    {
        var name = d.Name ?? "";
        var owner = OwnerOf(name, Username, others);
        if (owner is not null && !owner.Equals(Username, StringComparison.OrdinalIgnoreCase))
            return null; // чужая помеченная — не показываем

        string type; string title; bool deletable; string visibility;
        if (owner is null)
        {
            type = "Публичная"; title = name; deletable = true; visibility = "public";
        }
        else
        {
            var rest = name[(Username.Length + 1)..];
            if (rest == "notes") { type = "Заметки"; title = "Заметки"; deletable = false; }
            else if (rest.StartsWith("persona:", StringComparison.Ordinal)) { type = "Память персоны"; title = rest["persona:".Length..]; deletable = false; }
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

// --- DTO ---

public record KnowledgeBaseSummary(
    string Id, string Title, string Type, string Visibility,
    int DocumentCount, DateTime? CreatedAt, bool Deletable, string? Description);

public record KnowledgeDocumentDto(string Id, string Name, string IndexingStatus);

public record KnowledgeBaseDetail(
    string Id, string Title, string Type, string Visibility,
    int DocumentCount, DateTime? CreatedAt, bool Deletable, string? Description,
    IReadOnlyList<KnowledgeDocumentDto> Documents);

public record CreateKnowledgeBaseRequest(string Title, string? Description, string Visibility);

public record AddDocumentTextRequest(string Name, string Text);

public record KnowledgeSearchHit(double Score, string Content, string DocumentName);

public record KnowledgeSegmentDto(int Position, string Content, int WordCount);
