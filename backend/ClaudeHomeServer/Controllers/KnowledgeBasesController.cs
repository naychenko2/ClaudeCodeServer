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
public class KnowledgeBasesController(KnowledgeService knowledge, IHubContext<SessionHub> hub,
    Services.Llm.ICheapTextRunner cheap, KnowledgeBaseCatalogService catalog) : ControllerBase
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
        try
        {
            var (configured, items) = await catalog.ListForUserAsync(Username);
            return Ok(new { configured, items });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // GET /api/knowledge/{id} — база + её документы. Доступ — только relevant.
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        try
        {
            var detail = await catalog.GetDetailForUserAsync(Username, id);
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
    }

    // POST /api/knowledge/{id}/ai/describe — сгенерировать описание базы по составу документов
    // (локальная модель / claude) и сохранить его в Dify. Возвращает { description }.
    [HttpPost("{id}/ai/describe")]
    public async Task<IActionResult> Describe(string id, CancellationToken ct)
    {
        var d = await catalog.ResolveReadableAsync(Username, id);
        if (d is null) return NotFound();
        try
        {
            var docs = await knowledge.ListAllDocumentsAsync(id);
            var names = docs.Data.Select(x => x.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(50).ToList();
            if (names.Count == 0) return BadRequest(new { error = "В базе нет документов для описания" });
            var c = catalog.Classify(d, Username, catalog.OtherUsers(Username))!;
            var prompt = $"База знаний «{c.Title}» содержит документы:\n" +
                string.Join("\n", names.Select(n => "- " + n)) +
                "\n\nСоставь короткое описание (1-2 предложения, по-русски), о чём эта база знаний и что в ней искать. " +
                "Ответь ТОЛЬКО описанием, без вступлений.";
            var desc = (await cheap.RunAsync(Services.Llm.LocalActionCatalog.KbDescribe, prompt, ownerId: UserId, ct: ct)).Trim();
            if (desc.Length == 0) return StatusCode(502, new { error = "Не удалось сгенерировать описание" });
            if (desc.Length > 400) desc = desc[..400].TrimEnd();
            await knowledge.UpdateDatasetDescriptionAsync(id, desc);
            await Broadcast("updated", id);
            return Ok(new { description = desc });
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
        // Публичная база живёт в общем неймспейсе без префикса: двоеточие в названии позволило
        // бы замаскировать её под чужую личную ({user}:kb:…) — жертва увидела бы её как СВОЮ
        // (запись/удаление документов в подставной базе). Запрещаем.
        if (public_ && title.Contains(':'))
            return BadRequest(new { error = "Двоеточие в названии публичной базы недопустимо" });
        var name = public_ ? title : $"{Username}:kb:{title}";
        var permission = public_ ? "all_team_members" : "only_me";
        var description = string.IsNullOrWhiteSpace(req?.Description) ? null : req.Description.Trim();
        try
        {
            // Явная проверка коллизии: Dify на дубль имени отвечает невнятной ошибкой (→ 502),
            // а совпадение с чужой личной базой вовсе нельзя доводить до создания
            var existing = await knowledge.ListDatasetsAsync();
            if (existing.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                return BadRequest(new { error = "База с таким названием уже существует" });
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
        var d = await catalog.ResolveReadableAsync(Username, id);
        if (d is null) return NotFound();
        if (!catalog.IsDeletable(d, Username, IsAdmin)) return StatusCode(403, new { error = "Удаление этой базы недоступно: она привязана к другому разделу, либо для публичной базы нужны права администратора" });
        try { await knowledge.DeleteDatasetAsync(id); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" }); }
        await Broadcast("deleted", id);
        return NoContent();
    }

    // POST /api/knowledge/{id}/documents — добавить документ текстом.
    [HttpPost("{id}/documents")]
    public async Task<IActionResult> AddDocumentText(string id, [FromBody] AddDocumentTextRequest req)
    {
        var d = await catalog.ResolveReadableAsync(Username, id);
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
        var d = await catalog.ResolveReadableAsync(Username, id);
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
        var d = await catalog.ResolveReadableAsync(Username, id);
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
        var d = await catalog.ResolveReadableAsync(Username, id);
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
        var d = await catalog.ResolveReadableAsync(Username, id);
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

    // Классификация датасетов под пользователя (личные/публичные/чужие) — в
    // KnowledgeBaseCatalogService: та же точка обслуживает WorkspaceToolset (ADR-012, волна 3).
}

// --- DTO ---

public record KnowledgeBaseSummary(
    string Id, string Title, string Type, string Visibility,
    int DocumentCount, DateTime? CreatedAt, bool Deletable, string? Description);

// Error — текст ошибки индексации от Dify (только у документов в статусе error): для тех,
// у кого автолечения нет (ручные документы), это единственный ключ к причине
public record KnowledgeDocumentDto(string Id, string Name, string IndexingStatus, string? Error = null);

public record KnowledgeBaseDetail(
    string Id, string Title, string Type, string Visibility,
    int DocumentCount, DateTime? CreatedAt, bool Deletable, string? Description,
    IReadOnlyList<KnowledgeDocumentDto> Documents);

public record CreateKnowledgeBaseRequest(string Title, string? Description, string Visibility);

public record AddDocumentTextRequest(string Name, string Text);

public record KnowledgeSearchHit(double Score, string Content, string DocumentName);

public record KnowledgeSegmentDto(int Position, string Content, int WordCount);
