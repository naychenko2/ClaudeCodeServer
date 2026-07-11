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
// и permission. Самостоятельные/публичные БЗ можно создавать и удалять; привязанные
// (заметок/проектов/памяти персон) — только управлять документами. Не путать с
// проектным KnowledgeController'ом (маршрут /api/projects/{id}/knowledge).
[ApiController]
[Authorize]
[Route("api/knowledge")]
public class KnowledgeBasesController(KnowledgeService knowledge, IHubContext<SessionHub> hub) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;
    private string Username => User.FindFirstValue(ClaimTypes.Name) ?? UserId;

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
            var items = all.Select(Classify)
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
        var c = Classify(d)!;
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
        if (!IsDeletable(d)) return StatusCode(403, new { error = "Эту базу нельзя удалить из раздела «Знания» — она принадлежит другому разделу" });
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

    // Резолв датасета по id с проверкой доступности текущему пользователю (relevant).
    // Обязательно: с общим Dify-ключом нельзя оперировать по произвольному id — иначе
    // можно читать/менять чужую only_me базу.
    private async Task<DifyDatasetListItem?> ResolveReadableAsync(string id)
    {
        if (!knowledge.IsConfigured || string.IsNullOrEmpty(id)) return null;
        try
        {
            return (await knowledge.ListDatasetsAsync()).FirstOrDefault(d => d.Id == id)
                is { } found && IsRelevant(found) ? found : null;
        }
        catch (HttpRequestException) { return null; }
    }

    private bool IsPublic(DifyDatasetListItem d) => d.Permission == "all_team_members";
    private bool IsMine(DifyDatasetListItem d) => (d.Name ?? "").StartsWith(Username + ":", StringComparison.Ordinal);
    private bool IsRelevant(DifyDatasetListItem d) => IsMine(d) || IsPublic(d);
    // Удалять здесь можно самостоятельные ({username}:kb:…) и публичные. Привязанные — нет.
    private bool IsDeletable(DifyDatasetListItem d)
    {
        var rest = IsMine(d) ? d.Name![(Username.Length + 1)..] : "";
        return IsPublic(d) || rest.StartsWith("kb:", StringComparison.Ordinal);
    }

    // Сводка с производными полями (заголовок/тип/видимость/deletable) или null, если
    // датасет не релевантен пользователю (чужой only_me) — такие в списке не показываем.
    private KnowledgeBaseSummary? Classify(DifyDatasetListItem d)
    {
        var name = d.Name ?? "";
        var isPublic = IsPublic(d);
        var mine = IsMine(d);
        if (!mine && !isPublic) return null;

        string type; string title; bool deletable;
        var rest = mine ? name[(Username.Length + 1)..] : name;
        if (mine && rest == "notes") { type = "Заметки"; title = "Заметки"; deletable = false; }
        else if (mine && rest.StartsWith("persona:", StringComparison.Ordinal)) { type = "Память персоны"; title = rest["persona:".Length..]; deletable = false; }
        else if (mine && rest.StartsWith("kb:", StringComparison.Ordinal)) { type = "Самостоятельная"; title = rest["kb:".Length..]; deletable = true; }
        else if (mine) { type = "Проект"; title = rest; deletable = false; } // {username}:{projectName}
        else { type = "Публичная"; title = name; deletable = true; }         // all_team_members без префикса

        return new KnowledgeBaseSummary(d.Id, title, type, isPublic ? "public" : "personal",
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
