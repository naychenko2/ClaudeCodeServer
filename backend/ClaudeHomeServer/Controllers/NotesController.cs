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
    private readonly IHubContext<SessionHub> _hub;

    public NotesController(NotesService notes, IHubContext<SessionHub> hub)
    {
        _notes = notes;
        _hub = hub;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task Broadcast(string action, string? noteId = null) =>
        _hub.Clients.Group("user_" + UserId)
            .SendAsync("message", new NotesChangedMessage(action, noteId));

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
