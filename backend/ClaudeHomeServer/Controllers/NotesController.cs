using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

// Obsidian-совместимая база заметок, per-owner (изоляция как у задач — по claim sub).
[ApiController]
[Authorize]
[Route("api/notes")]
public class NotesController : ControllerBase
{
    private readonly NotesService _notes;
    private readonly NotesKnowledgeService _kb;
    private readonly IHubContext<SessionHub> _hub;

    public NotesController(NotesService notes, NotesKnowledgeService kb, IHubContext<SessionHub> hub)
    {
        _notes = notes;
        _kb = kb;
        _hub = hub;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task Broadcast(string action, string? noteId = null)
    {
        // Любая мутация — отложенная синхронизация семантического индекса (дифф по хешам)
        _kb.QueueSync(UserId);
        return _hub.Clients.Group("user_" + UserId)
            .SendAsync("message", new NotesChangedMessage(action, noteId));
    }

    // Список заметок владельца (все источники). source — фильтр по источнику, q — поиск.
    [HttpGet]
    public ActionResult<IReadOnlyList<NoteSummary>> List(
        [FromQuery] string? source, [FromQuery] string? q) =>
        Ok(_notes.GetSummaries(UserId, source, q));

    // Источники для выбора «куда создать» (личный vault + проекты владельца).
    [HttpGet("sources")]
    public ActionResult<IReadOnlyList<NoteSourceDto>> Sources() =>
        Ok(_notes.GetSources(UserId));

    // Единый per-owner граф связей (узлы + рёбра, включая «призрачные» заметки).
    [HttpGet("graph")]
    public ActionResult<NoteGraph> Graph() => Ok(_notes.GetGraph(UserId));

    // Резолв заметки по имени вики-ссылки (+ фрагмент по якорю #Заголовок / #^блок) —
    // для hover-preview и embed-вставок ![[…]].
    [HttpGet("resolve")]
    public ActionResult Resolve([FromQuery] string name, [FromQuery] string? anchor)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("Не задано имя");
        var r = _notes.ResolveByName(UserId, name, anchor);
        return r is null ? NotFound() : Ok(new { note = r.Value.Note, fragment = r.Value.Fragment });
    }

    // Вложение из vault (картинка для ![[img.png]]): JWT принимается и в query
    // access_token (браузерный <img> не шлёт Authorization-заголовок).
    [HttpGet("attachment")]
    public IActionResult Attachment([FromQuery] string source, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(path))
            return BadRequest();
        try
        {
            var full = _notes.ResolveAttachmentPath(UserId, source, path);
            if (!System.IO.File.Exists(full)) return NotFound();
            var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            if (!provider.TryGetContentType(full, out var contentType))
                contentType = "application/octet-stream";
            return PhysicalFile(full, contentType);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Возможности раздела (semantic — настроен ли Dify для поиска «по смыслу»)
    [HttpGet("caps")]
    public ActionResult Caps() => Ok(new { semantic = _kb.Available });

    // Семантический поиск по заметкам (Dify retrieve). Пустой список — индекс пуст/выключен.
    [HttpGet("semantic")]
    public async Task<ActionResult> Semantic([FromQuery] string q, [FromQuery] int topK = 8)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Пустой запрос");
        if (!_kb.Available) return Ok(new { available = false, results = Array.Empty<NoteSemanticHit>() });
        try
        {
            var results = await _kb.SearchAsync(UserId, q, Math.Clamp(topK, 1, 20));
            return Ok(new { available = true, results });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = $"Dify недоступен: {ex.Message}" });
        }
    }

    // Полная переиндексация заметок в семантический индекс
    [HttpPost("reindex")]
    public async Task<ActionResult> Reindex()
    {
        if (!_kb.Available) return BadRequest("Dify не настроен");
        var changed = await _kb.SyncAllAsync(UserId);
        return Ok(new { changed });
    }

    // Шаблоны заметок (файлы templates/ личного vault).
    [HttpGet("templates")]
    public ActionResult<IReadOnlyList<NoteTemplateDto>> Templates() =>
        Ok(_notes.GetTemplates(UserId));

    // Дневниковая заметка (get-or-create Journal/{date}.md в личном vault).
    [HttpPost("daily")]
    public async Task<ActionResult<NoteDetail>> Daily([FromBody] DailyNoteRequest req)
    {
        var note = _notes.GetOrCreateDaily(UserId, req.Date);
        await Broadcast("updated", note.Id);
        return Ok(note);
    }

    // «Связать» несвязанное упоминание: первое вхождение заголовка → [[…]].
    [HttpPost("{id}/link-mention")]
    public async Task<ActionResult<NoteDetail>> LinkMention(string id, [FromBody] LinkMentionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.TargetTitle))
            return BadRequest("Не задан заголовок цели");
        try
        {
            var note = _notes.LinkMention(UserId, id, req.TargetTitle);
            if (note is null) return NotFound();
            await Broadcast("updated", note.Id);
            return Ok(note);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
    }

    [HttpGet("{id}")]
    public ActionResult<NoteDetail> Get(string id)
    {
        var note = _notes.GetDetail(UserId, id);
        return note is null ? NotFound() : Ok(note);
    }

    [HttpGet("{id}/backlinks")]
    public ActionResult<IReadOnlyList<NoteBacklinkDto>> Backlinks(string id) =>
        Ok(_notes.GetBacklinks(UserId, id));

    [HttpPost]
    public async Task<ActionResult<NoteDetail>> Create([FromBody] CreateNoteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest("Не задан заголовок заметки");
        try
        {
            var note = _notes.Create(UserId, req);
            await Broadcast("created", note.Id);
            return Ok(note);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<NoteDetail>> Update(string id, [FromBody] UpdateNoteRequest req)
    {
        try
        {
            var note = _notes.Update(UserId, id, req);
            if (note is null) return NotFound();
            await Broadcast("updated", note.Id);
            return Ok(note);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            if (!_notes.Delete(UserId, id)) return NotFound();
            await Broadcast("deleted", id);
            return NoContent();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException) { return BadRequest("Некорректный id заметки"); }
    }
}
