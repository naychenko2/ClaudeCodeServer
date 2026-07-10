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

// «Олицетворённые агенты» (персоны), per-owner (изоляция как у задач/заметок — по claim sub).
[ApiController]
[Authorize]
[Route("api/personas")]
public class PersonasController : ControllerBase
{
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly PersonaMemoryService _memory;
    private readonly FalImageService _falImage;
    private readonly IHubContext<SessionHub> _hub;

    public PersonasController(PersonaManager personas, ProjectManager projects,
        SessionManager sessions, PersonaMemoryService memory, FalImageService falImage,
        IHubContext<SessionHub> hub)
    {
        _personas = personas;
        _projects = projects;
        _sessions = sessions;
        _memory = memory;
        _falImage = falImage;
        _hub = hub;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task Broadcast(string action, string? personaId = null) =>
        _hub.Clients.Group("user_" + UserId)
            .SendAsync("message", new PersonasChangedMessage(action, personaId));

    // Список персон владельца. scope/projectId — необязательные фильтры контекста.
    [HttpGet]
    public ActionResult<IReadOnlyList<Persona>> List(
        [FromQuery] string? scope, [FromQuery] string? projectId)
    {
        if (string.Equals(scope, "context", StringComparison.OrdinalIgnoreCase))
            return Ok(_personas.GetForContext(UserId, projectId));
        return Ok(_personas.GetByOwner(UserId));
    }

    [HttpGet("{id}")]
    public ActionResult<Persona> Get(string id)
    {
        var persona = _personas.Get(id, UserId);
        return persona is null ? NotFound() : Ok(persona);
    }

    [HttpPost]
    public async Task<ActionResult<Persona>> Create([FromBody] CreatePersonaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Не задано имя персоны");

        var scope = req.Scope ?? PersonaScope.Global;
        if (scope == PersonaScope.Project && !ValidProject(req.ProjectId))
            return BadRequest("Для проектной персоны нужен корректный projectId");

        var persona = _personas.Create(UserId, req.Name, req.Role, req.Description, req.SystemPrompt,
            req.Model, req.Effort, scope, req.ProjectId, req.Color, req.Greeting,
            req.MemoryEnabled ?? true);
        await Broadcast("created", persona.Id);
        return Ok(persona);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Persona>> Update(string id, [FromBody] UpdatePersonaRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (req.Scope == PersonaScope.Project && !ValidProject(req.ProjectId))
            return BadRequest("Для проектной персоны нужен корректный projectId");
        // Любой непустой projectId (в т.ч. при partial-update без scope) — только свой проект
        if (!string.IsNullOrEmpty(req.ProjectId) && !ValidProject(req.ProjectId))
            return BadRequest("Проект не найден или недоступен");

        var persona = _personas.Update(id, UserId, req.Name, req.Role, req.Description, req.SystemPrompt,
            req.Model, req.Effort, req.Scope, req.ProjectId, req.Color, req.Greeting,
            req.MemoryEnabled);
        await Broadcast("updated", id);
        return Ok(persona);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!_personas.Delete(id, UserId)) return NotFound();
        await Broadcast("deleted", id);
        return NoContent();
    }

    // Чаты, которые ведутся от лица этой персоны
    [HttpGet("{id}/chats")]
    public ActionResult<IReadOnlyList<Session>> Chats(string id)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        return Ok(_sessions.GetPersonaChats(UserId, id));
    }

    // Открыть новый чат с персоной (или продолжить существующий по resumeSessionId)
    [HttpPost("{id}/chats")]
    public async Task<ActionResult<Session>> CreateChat(string id, [FromBody] CreatePersonaChatRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        var mode = Enum.TryParse<ClaudeMode>(req.Mode, true, out var m) ? m : ClaudeMode.Auto;
        try
        {
            var chat = await _sessions.CreatePersonaChatAsync(UserId, id, mode, req.ResumeSessionId, req.Name);
            return Ok(chat);
        }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // --- Аватар персоны ---

    // Доступна ли AI-генерация аватара (настроен ли fal)
    [HttpGet("avatar/caps")]
    public ActionResult Caps() => Ok(new { generate = _falImage.Enabled });

    // Сгенерировать НЕСКОЛЬКО вариантов аватар-фото через fal по описанию (для выбора).
    // Кандидаты сохраняются во временную папку, аватар персоны НЕ меняется до выбора.
    // prompt пуст → строим фото-промпт из имени/описания персоны.
    [HttpPost("{id}/avatar/generate")]
    public async Task<ActionResult> GenerateAvatar(string id, [FromBody] GenerateAvatarRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (!_falImage.Enabled) return BadRequest(new { error = "Генерация изображений не настроена (нет Fal:ApiKey)" });

        var prompt = string.IsNullOrWhiteSpace(req.Prompt)
            ? BuildAvatarPrompt(persona)
            : $"Photorealistic portrait photo. {req.Prompt.Trim()}";
        var count = req.Count is >= 1 and <= 4 ? req.Count.Value : 4;

        var images = await _falImage.GenerateManyAsync(prompt, count);
        if (images.Count == 0) return StatusCode(502, new { error = "Не удалось сгенерировать изображение" });

        // Свежая папка кандидатов (перезатираем прошлую генерацию)
        var candDir = Path.Combine(_personas.AssetsDir, id, "candidates");
        try { if (Directory.Exists(candDir)) Directory.Delete(candDir, recursive: true); } catch { }
        Directory.CreateDirectory(candDir);

        var files = new List<string>();
        foreach (var img in images)
        {
            var ext = ExtFor(img.ContentType);
            var name = $"cand-{Guid.NewGuid():N}{ext}";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(candDir, name), img.Bytes);
            files.Add(name);
        }
        return Ok(new { candidates = files });
    }

    // Отдать кандидата аватара (превью в галерее выбора). access_token в query для <img>.
    [HttpGet("{id}/avatar/candidate/{file}")]
    public IActionResult AvatarCandidate(string id, string file)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        var safe = Path.GetFileName(file);   // защита от path-traversal
        var full = Path.Combine(_personas.AssetsDir, id, "candidates", safe);
        if (!System.IO.File.Exists(full)) return NotFound();
        return PhysicalFileByExt(full);
    }

    // Выбрать кандидата как аватар персоны: делаем основным, чистим остальных кандидатов.
    [HttpPost("{id}/avatar/select")]
    public async Task<ActionResult<Persona>> SelectAvatar(string id, [FromBody] SelectAvatarRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.File)) return BadRequest(new { error = "Не указан файл" });

        var dir = Path.Combine(_personas.AssetsDir, id);
        var candPath = Path.Combine(dir, "candidates", Path.GetFileName(req.File));
        if (!System.IO.File.Exists(candPath)) return NotFound(new { error = "Кандидат не найден" });

        var ext = Path.GetExtension(candPath);
        var fileName = $"avatar-{Guid.NewGuid():N}{ext}";   // cache-busting
        System.IO.File.Copy(candPath, Path.Combine(dir, fileName), overwrite: true);

        // Удаляем прежний аватар и всю папку кандидатов
        if (!string.IsNullOrEmpty(persona.Avatar.ImageFile))
            try { System.IO.File.Delete(Path.Combine(dir, persona.Avatar.ImageFile)); } catch { }
        try { Directory.Delete(Path.Combine(dir, "candidates"), recursive: true); } catch { }

        var updated = _personas.SetAvatarImage(id, UserId, fileName);
        await Broadcast("updated", id);
        return Ok(updated);
    }

    // Отдать картинку аватара. JWT принимается и в query access_token (браузерный <img>).
    [HttpGet("{id}/avatar")]
    public IActionResult Avatar(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null || persona.Avatar.Kind != PersonaAvatarKind.Image
            || string.IsNullOrEmpty(persona.Avatar.ImageFile))
            return NotFound();

        var full = Path.Combine(_personas.AssetsDir, id, persona.Avatar.ImageFile);
        return System.IO.File.Exists(full) ? PhysicalFileByExt(full) : NotFound();
    }

    private static string ExtFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        _ => ".png",
    };

    private IActionResult PhysicalFileByExt(string full)
    {
        var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(full, out var contentType))
            contentType = "application/octet-stream";
        return PhysicalFile(full, contentType);
    }

    // Фото-промпт аватара по умолчанию — из имени и описания персоны
    private static string BuildAvatarPrompt(Persona persona)
    {
        var who = string.IsNullOrWhiteSpace(persona.Description)
            ? persona.Name
            : $"{persona.Name}, {persona.Description}";
        return $"Photorealistic portrait photo of {who}. Head and shoulders, looking at camera, " +
               "clean solid background, soft studio lighting, natural skin, friendly expression, " +
               "high detail, sharp focus, square crop.";
    }

    // --- Долгая память персоны (дёргается MCP memory-server и UI-панелью «что помнит агент») ---

    // Записи памяти (type — необязательный фильтр semantic|episodic|procedural)
    [HttpGet("{id}/memory")]
    public ActionResult<IReadOnlyList<PersonaMemoryEntry>> Memory(string id, [FromQuery] string? type)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        PersonaMemoryType? filter = Enum.TryParse<PersonaMemoryType>(type, true, out var t) ? t : null;
        return Ok(_memory.List(UserId, id, filter));
    }

    // Поиск по памяти (relevance × recency × typeWeight)
    [HttpGet("{id}/memory/search")]
    public async Task<ActionResult> MemorySearch(string id, [FromQuery] string q, [FromQuery] int topK = 8)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Пустой запрос");
        var hits = await _memory.SearchAsync(UserId, id, q, Math.Clamp(topK, 1, 20));
        return Ok(hits);
    }

    // Запомнить (явный write-path)
    [HttpPost("{id}/memory")]
    public async Task<ActionResult<PersonaMemoryEntry>> Remember(string id, [FromBody] RememberRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Пустой текст");
        if (!Enum.TryParse<PersonaMemoryType>(req.Type, true, out var type)) type = PersonaMemoryType.Semantic;
        var entry = _memory.Remember(UserId, id, type, req.Text, req.Tags, req.SourceSessionId);
        if (entry is null) return NotFound();
        await Broadcast("memory", id);
        return Ok(entry);
    }

    // Забыть запись
    [HttpDelete("{id}/memory/{entryId}")]
    public async Task<IActionResult> Forget(string id, string entryId)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (!_memory.Forget(UserId, id, entryId)) return NotFound();
        await Broadcast("memory", id);
        return NoContent();
    }

    // Проект существует и принадлежит владельцу
    private bool ValidProject(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        var project = _projects.GetById(projectId);
        return project is not null && project.OwnerId == UserId;
    }
}

public record CreatePersonaRequest(
    string Name,
    string? Role,
    string? Description,
    string? SystemPrompt,
    string? Model,
    string? Effort,
    PersonaScope? Scope,
    string? ProjectId,
    string? Color,
    string? Greeting,
    bool? MemoryEnabled);

public record UpdatePersonaRequest(
    string? Name,
    string? Role,
    string? Description,
    string? SystemPrompt,
    string? Model,
    string? Effort,
    PersonaScope? Scope,
    string? ProjectId,
    string? Color,
    string? Greeting,
    bool? MemoryEnabled);

public record CreatePersonaChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null);

public record RememberRequest(string Type, string Text, List<string>? Tags = null, string? SourceSessionId = null);

public record GenerateAvatarRequest(string? Prompt = null, int? Count = null);

public record SelectAvatarRequest(string File);
