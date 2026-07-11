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

// Персоны, per-owner (изоляция как у задач/заметок — по claim sub).
[ApiController]
[Authorize]
[Route("api/personas")]
public class PersonasController : ControllerBase
{
    private readonly PersonaManager _personas;
    private readonly ProjectManager _projects;
    private readonly SessionManager _sessions;
    private readonly PersonaMemoryService _memory;
    private readonly PersonaBindingsService _bindings;
    private readonly NotesService _notes;
    private readonly SkillsService _skills;
    private readonly KnowledgeService _knowledge;
    private readonly FalImageService _falImage;
    private readonly Services.Llm.OneShotClaudeRunner _oneShot;
    private readonly FeatureFlagService _flags;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonasController> _log;
    private readonly IHubContext<SessionHub> _hub;

    public PersonasController(PersonaManager personas, ProjectManager projects,
        SessionManager sessions, PersonaMemoryService memory, PersonaBindingsService bindings,
        NotesService notes, SkillsService skills, KnowledgeService knowledge,
        FalImageService falImage,
        Services.Llm.OneShotClaudeRunner oneShot, FeatureFlagService flags, IConfiguration config,
        ILogger<PersonasController> log, IHubContext<SessionHub> hub)
    {
        _personas = personas;
        _projects = projects;
        _sessions = sessions;
        _memory = memory;
        _bindings = bindings;
        _notes = notes;
        _skills = skills;
        _knowledge = knowledge;
        _falImage = falImage;
        _oneShot = oneShot;
        _flags = flags;
        _config = config;
        _log = log;
        _hub = hub;
    }

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task Broadcast(string action, string? personaId = null) =>
        _hub.Clients.Group("user_" + UserId)
            .SendAsync("message", new PersonasChangedMessage(action, personaId));

    // Список персон владельца. scope: "context" — глобальные + этого проекта;
    // "project" — только привязанные к projectId; "global" — только глобальные;
    // иначе — все персоны владельца.
    [HttpGet]
    public ActionResult<IReadOnlyList<Persona>> List(
        [FromQuery] string? scope, [FromQuery] string? projectId)
    {
        if (string.Equals(scope, "context", StringComparison.OrdinalIgnoreCase))
            return Ok(_personas.GetForContext(UserId, projectId));
        if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            return Ok(_personas.GetByOwner(UserId)
                .Where(p => p.Scope == PersonaScope.Project && p.ProjectId == projectId).ToList());
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
            return Ok(_personas.GetByOwner(UserId)
                .Where(p => p.Scope == PersonaScope.Global).ToList());
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

        // Явные привязки валидируем ДО создания персоны — ошибка не оставляет полусозданную
        var bindings = new List<PersonaBinding>();
        if (req.Bindings is { Count: > 0 })
        {
            foreach (var b in req.Bindings)
            {
                var (binding, parseError) = ParseBinding(b);
                if (binding is null) return BadRequest(new { error = parseError });
                var err = await _bindings.ValidateAsync(UserId, binding, bindings);
                if (err is not null) return BadRequest(new { error = err });
                bindings.Add(binding);
            }
        }

        var persona = _personas.Create(UserId, req.Name, req.Role, req.Description, req.SystemPrompt,
            req.Model, req.Effort, scope, req.ProjectId, req.Color, req.Greeting,
            req.MemoryEnabled ?? true, req.Tools);
        if (bindings.Count > 0)
            persona = _personas.UpdateBindings(persona.Id, UserId, bindings);
        // Авто-подбор привязок (autoBindings, за флагом persona-bindings) — best-effort:
        // сбой подбора не роняет создание, персона остаётся без привязок
        if (req.AutoBindings == true && _flags.IsEnabled(UserId, FeatureFlagKeys.PersonaBindings))
            persona = await TryAutoBindAsync(persona);
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
            req.MemoryEnabled, req.Tools);
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

    // AI-помощь с характером персоны: сгенерировать с нуля или улучшить/дополнить существующий
    // (one-shot LLM). Возвращает готовый текст характера для подстановки в форму.
    [HttpPost("ai/character")]
    public async Task<ActionResult> AiCharacter([FromBody] AiCharacterRequest req)
    {
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
        var prompt = BuildCharacterPrompt(req);
        try
        {
            var text = await _oneShot.RunAsync(prompt, model, TimeSpan.FromSeconds(90), HttpContext.RequestAborted);
            var character = text.Trim();
            if (string.IsNullOrWhiteSpace(character))
                return StatusCode(502, new { error = "Пустой ответ модели" });
            return Ok(new { character });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Не удалось сгенерировать характер: {ex.Message}" });
        }
    }

    private static string BuildCharacterPrompt(AiCharacterRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Ты помогаешь описать характер и стиль общения персоны-ассистента. " +
                      "Составь ТЕЛО системного промпта персоны — как она общается и действует.");
        if (!string.IsNullOrWhiteSpace(req.Role)) sb.AppendLine($"Роль персоны: {req.Role.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Name)) sb.AppendLine($"Имя персоны: {req.Name.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Description)) sb.AppendLine($"Кратко: {req.Description.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Current))
        {
            sb.AppendLine($"\nТекущий характер (переработай/улучши его):\n{req.Current.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(req.Instruction))
            sb.AppendLine($"\nПожелание пользователя: {req.Instruction.Trim()}");
        sb.AppendLine("\nТребования к ответу:");
        sb.AppendLine("- обращение на «ты» к персоне («Ты …»), описывай её манеру, ценности, стиль ответов;");
        sb.AppendLine("- по-русски, живо и конкретно, 2–5 предложений;");
        sb.AppendLine("- НЕ упоминай имя модели, не пиши преамбул и пояснений — только сам текст характера.");
        return sb.ToString();
    }

    // Быстрое создание персоны по одному промпту: LLM заполняет роль/имя/описание/характер/
    // приветствие/цвет, персона создаётся, фото-аватар генерируется автоматически (если настроен fal).
    // Возвращает созданную персону — фронт открывает её в редакторе для доводки.
    [HttpPost("ai/quick-create")]
    public async Task<ActionResult<Persona>> AiQuickCreate([FromBody] AiQuickCreateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Опишите, кто это и чем будет заниматься" });

        var scope = req.Scope ?? PersonaScope.Global;
        if (scope == PersonaScope.Project && !ValidProject(req.ProjectId))
            return BadRequest(new { error = "Для проектной персоны нужен корректный projectId" });

        // 1. Черновик всех полей одним one-shot вызовом (строгий JSON-объект).
        // LLM иногда отвечает без валидного JSON — логируем сырой ответ и повторяем один раз.
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
        DraftRaw? draft = null;
        for (var attempt = 1; attempt <= 2 && draft is null; attempt++)
        {
            string raw;
            try
            {
                raw = await _oneShot.RunAsync(BuildDraftPrompt(req.Prompt), model,
                    TimeSpan.FromSeconds(90), HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "quick-create: one-shot упал (попытка {Attempt})", attempt);
                if (attempt == 2)
                    return StatusCode(502, new { error = $"Не удалось сгенерировать черновик: {ex.Message}" });
                continue;
            }
            draft = ParseDraft(raw);
            if (draft is null || string.IsNullOrWhiteSpace(draft.Name))
            {
                _log.LogWarning("quick-create: черновик не распознан (попытка {Attempt}); сырой ответ: {Raw}",
                    attempt, raw.Length > 600 ? raw[..600] + "…" : raw);
                draft = null;
            }
        }
        if (draft is null)
            return StatusCode(502, new { error = "Модель не вернула корректный черновик — попробуйте ещё раз" });

        // 2. Создаём персону с заполненными полями
        var color = ValidColor(draft.Color) ? draft.Color : "orange";
        var persona = _personas.Create(UserId, draft.Name!, draft.Role, draft.Description,
            draft.Character, model: null, effort: null, scope, req.ProjectId,
            color, draft.Greeting, memoryEnabled: true);

        // 3. Фото-аватар — автоматически (не критично: при сбое остаются инициалы)
        if (_falImage.Enabled)
        {
            try
            {
                var avatarPrompt = string.IsNullOrWhiteSpace(draft.AvatarPrompt)
                    ? BuildAvatarPrompt(persona)
                    : $"Photorealistic portrait photo. {draft.AvatarPrompt!.Trim()}";
                var images = await _falImage.GenerateManyAsync(avatarPrompt, 1);
                if (images.Count > 0)
                {
                    var dir = Path.Combine(_personas.AssetsDir, persona.Id);
                    Directory.CreateDirectory(dir);
                    var fileName = $"avatar-{Guid.NewGuid():N}{ExtFor(images[0].ContentType)}";
                    await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, fileName), images[0].Bytes);
                    persona = _personas.SetAvatarImage(persona.Id, UserId, fileName);
                }
            }
            catch { /* аватар не критичен для быстрого создания */ }
        }

        // 4. Авто-подбор привязок (за флагом persona-bindings; по умолчанию включён) —
        // best-effort: сбой не роняет создание, персона остаётся без привязок
        if (req.AutoBindings != false && _flags.IsEnabled(UserId, FeatureFlagKeys.PersonaBindings))
            persona = await TryAutoBindAsync(persona);

        await Broadcast("created", persona.Id);
        return Ok(persona);
    }

    private static string BuildDraftPrompt(string userPrompt)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Пользователь описывает ассистента-персону, которую хочет создать. " +
                      "Придумай и верни ВСЕ поля профиля персоны.");
        sb.AppendLine($"\nОписание пользователя: {userPrompt.Trim()}");
        sb.AppendLine("\nВерни ТОЛЬКО JSON-объект (без пояснений и markdown) с полями:");
        sb.AppendLine("  role — роль/профессия по-русски, 1-3 слова (напр. «Дизайнер», «Личный тренер»);");
        sb.AppendLine("  name — русское имя-человека (одно слово, подходит персоне);");
        sb.AppendLine("  description — краткое «кто это», 3-8 слов, по-русски;");
        sb.AppendLine("  character — характер и стиль общения: обращение на «ты» («Ты …»), живо, 2-5 предложений, по-русски;");
        sb.AppendLine("  greeting — первое приветственное сообщение персоны пользователю, 1-2 предложения, по-русски, в её характере;");
        sb.AppendLine("  color — один из: yellow, orange, blue, green, purple, red, brown, cyan, pink (подходит образу);");
        sb.AppendLine("  avatarPrompt — описание внешности для фотопортрета, по-английски, 5-15 слов (пол, возраст, стиль, настроение, фон).");
        return sb.ToString();
    }

    // Парс JSON-объекта из ответа модели (устойчиво к преамбуле/markdown-fence)
    private static DraftRaw? ParseDraft(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<DraftRaw>(raw[start..(i + 1)],
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (System.Text.Json.JsonException) { return null; }
            }
        }
        return null;
    }

    private static bool ValidColor(string? c) =>
        c is "yellow" or "orange" or "blue" or "green" or "purple" or "red" or "brown" or "cyan" or "pink";

    private sealed record DraftRaw(string? Role, string? Name, string? Description,
        string? Character, string? Greeting, string? Color, string? AvatarPrompt);

    // --- Привязки персоны: источники знаний и правила (фича persona-bindings) ---
    // CRUD работает независимо от флага (данные безвредны и переживают выключение);
    // за флагом — только suggest/autoBindings и сам блок в промпте (PersonaBindingsService).

    [HttpGet("{id}/bindings")]
    public ActionResult<IReadOnlyList<PersonaBinding>> Bindings(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        return Ok(persona.Bindings ?? []);
    }

    // Добавить одну привязку (мгновенное сохранение)
    [HttpPost("{id}/bindings")]
    public async Task<ActionResult<PersonaBinding>> AddBinding(string id, [FromBody] PersonaBindingRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();

        var (binding, parseError) = ParseBinding(req);
        if (binding is null) return BadRequest(new { error = parseError });
        var err = await _bindings.ValidateAsync(UserId, binding, persona.Bindings);
        if (err is not null) return BadRequest(new { error = err });

        var list = new List<PersonaBinding>(persona.Bindings ?? []) { binding };
        _personas.UpdateBindings(id, UserId, list);
        await Broadcast("updated", id);
        return Ok(binding);
    }

    // Полная замена набора привязок (PUT-семантика; дёргается MCP personas_bindings_set)
    [HttpPut("{id}/bindings")]
    public async Task<ActionResult<IReadOnlyList<PersonaBinding>>> SetBindings(string id,
        [FromBody] PersonaBindingsSetRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();

        var list = new List<PersonaBinding>();
        foreach (var b in req.Bindings ?? [])
        {
            var (binding, parseError) = ParseBinding(b);
            if (binding is null) return BadRequest(new { error = parseError });
            var err = await _bindings.ValidateAsync(UserId, binding, list);
            if (err is not null) return BadRequest(new { error = err });
            list.Add(binding);
        }
        var updated = _personas.UpdateBindings(id, UserId, list);
        await Broadcast("updated", id);
        return Ok(updated.Bindings ?? []);
    }

    // Изменить одну привязку
    [HttpPut("{id}/bindings/{bindingId}")]
    public async Task<ActionResult<PersonaBinding>> UpdateBinding(string id, string bindingId,
        [FromBody] PersonaBindingRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        var current = persona.Bindings?.FirstOrDefault(b => b.Id == bindingId);
        if (current is null) return NotFound(new { error = "Привязка не найдена" });

        var (parsed, parseError) = ParseBinding(req);
        if (parsed is null) return BadRequest(new { error = parseError });

        // Валидируем копию с Id исходной привязки (сама себя дубликатом не считается)
        var candidate = new PersonaBinding
        {
            Id = current.Id,
            Type = parsed.Type,
            Target = parsed.Target,
            Path = parsed.Path,
            Condition = parsed.Condition,
            Mode = parsed.Mode,
            CreatedAt = current.CreatedAt,
        };
        var err = await _bindings.ValidateAsync(UserId, candidate, persona.Bindings);
        if (err is not null) return BadRequest(new { error = err });

        current.Type = candidate.Type;
        current.Target = candidate.Target;
        current.Path = candidate.Path;
        current.Condition = candidate.Condition;
        current.Mode = candidate.Mode;
        current.UpdatedAt = DateTime.UtcNow;
        _personas.UpdateBindings(id, UserId, persona.Bindings!.ToList());
        await Broadcast("updated", id);
        return Ok(current);
    }

    [HttpDelete("{id}/bindings/{bindingId}")]
    public async Task<IActionResult> DeleteBinding(string id, string bindingId)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        var list = persona.Bindings?.Where(b => b.Id != bindingId).ToList();
        if (list is null || list.Count == (persona.Bindings?.Count ?? 0))
            return NotFound(new { error = "Привязка не найдена" });
        _personas.UpdateBindings(id, UserId, list);
        await Broadcast("updated", id);
        return NoContent();
    }

    // Каталог возможных целей привязки для пикера фронта: type = project | knowledge |
    // notes | tool | skill; для notes с ?source= — папки внутри источника.
    [HttpGet("binding-targets")]
    public ActionResult BindingTargets([FromQuery] string? type, [FromQuery] string? source)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "project":
                return Ok(_projects.GetByOwner(UserId)
                    .Select(p => new { id = p.Id, label = p.Name, hint = p.RootPath, meta = (string?)null }));

            case "knowledge":
                return Ok(_bindings.KnownDatasets(UserId)
                    .Select(d => new
                    {
                        id = d.Id,
                        label = d.Label,
                        hint = d.ProjectId is null ? "База знаний заметок" : "База знаний проекта",
                        meta = d.ProjectId,
                    }));

            case "notes" when !string.IsNullOrWhiteSpace(source):
            {
                // Папки источника — из путей его заметок (все промежуточные уровни)
                var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in _notes.GetSummaries(UserId, source, null))
                {
                    var dir = System.IO.Path.GetDirectoryName(s.Path)?.Replace('\\', '/');
                    while (!string.IsNullOrEmpty(dir))
                    {
                        folders.Add(dir);
                        dir = System.IO.Path.GetDirectoryName(dir)?.Replace('\\', '/');
                    }
                }
                return Ok(folders.Select(f => new { id = f, label = f, hint = (string?)null, meta = source }));
            }

            case "notes":
                return Ok(_notes.GetSources(UserId)
                    .Select(s => new { id = s.Key, label = s.Label, hint = (string?)null, meta = (string?)null }));

            case "tool":
                return Ok(PersonaBindingsService.ToolCatalog
                    .Select(kv => new { id = kv.Key, label = kv.Value.Label, hint = kv.Value.Hint, meta = (string?)null }));

            case "skill":
                return Ok(_skills.GetGlobalSkills()
                    .Select(s => new { id = s.Name, label = s.Name, hint = s.Description, meta = (string?)null }));

            default:
                return BadRequest(new { error = "Укажите type: project | knowledge | notes | tool | skill" });
        }
    }

    // AI-формулировка условия «когда персоне применять источник» по превью его содержимого
    [HttpPost("bindings/ai-condition")]
    public async Task<ActionResult> AiCondition([FromBody] AiConditionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Type) || string.IsNullOrWhiteSpace(req.Target))
            return BadRequest(new { error = "Нужны type и target" });

        var preview = await BuildSourcePreviewAsync(req.Type.Trim(), req.Target.Trim(), req.Path);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Пользователь привязывает к AI-персоне источник знаний. Сформулируй условие — " +
                      "КОГДА персоне стоит обращаться к этому источнику (в каких вопросах/задачах он полезен).");
        sb.AppendLine($"\nТип источника: {req.Type.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Path)) sb.AppendLine($"Путь внутри источника: {req.Path.Trim()}.");
        if (!string.IsNullOrWhiteSpace(preview)) sb.AppendLine($"\nПревью содержимого:\n{preview}");
        sb.AppendLine("\nТребования к ответу:");
        sb.AppendLine("- 1-2 предложения по-русски, начиная с сути («вопросы по …», «когда …»);");
        sb.AppendLine("- конкретно по содержимому источника, без общих слов;");
        sb.AppendLine("- ТОЛЬКО текст условия, без преамбул, кавычек и markdown.");

        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
        try
        {
            var text = await _oneShot.RunAsync(sb.ToString(), model,
                TimeSpan.FromSeconds(60), HttpContext.RequestAborted);
            var condition = text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(condition))
                return StatusCode(502, new { error = "Пустой ответ модели" });
            return Ok(new { condition });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Не удалось сформулировать условие: {ex.Message}" });
        }
    }

    // AI-подбор привязок под роль персоны: возвращает кандидатов, НЕ сохраняет
    [HttpPost("{id}/bindings/suggest")]
    public async Task<ActionResult> SuggestBindings(string id)
    {
        if (!_flags.IsEnabled(UserId, FeatureFlagKeys.PersonaBindings))
            return BadRequest(new { error = "Фича привязок персон выключена (флаг persona-bindings)" });
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();

        try
        {
            var candidates = await SuggestBindingsAsync(persona);
            return Ok(new { candidates });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "suggest bindings для персоны {Persona}", id);
            return StatusCode(502, new { error = $"Не удалось подобрать привязки: {ex.Message}" });
        }
    }

    // Разбор DTO привязки: строковые type/mode → enum'ы, path нормализуется в валидации
    private static (PersonaBinding? Binding, string? Error) ParseBinding(PersonaBindingRequest req)
    {
        if (!Enum.TryParse<PersonaBindingType>(req.Type?.Trim(), true, out var type))
            return (null, $"Неизвестный тип привязки: {req.Type}");
        var mode = PersonaBindingMode.Auto;
        if (!string.IsNullOrWhiteSpace(req.Mode) && !Enum.TryParse(req.Mode.Trim(), true, out mode))
            return (null, $"Неизвестный режим привязки: {req.Mode}");
        return (new PersonaBinding
        {
            Type = type,
            Target = req.Target?.Trim() ?? "",
            Path = string.IsNullOrWhiteSpace(req.Path) ? null : req.Path.Trim(),
            Condition = req.Condition?.Trim() ?? "",
            Mode = mode,
        }, null);
    }

    // Авто-подбор и сохранение привязок для свежесозданной персоны (best-effort)
    private async Task<Persona> TryAutoBindAsync(Persona persona)
    {
        try
        {
            var candidates = await SuggestBindingsAsync(persona);
            if (candidates.Count > 0)
                return _personas.UpdateBindings(persona.Id, UserId, candidates);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "autoBindings: подбор привязок для {Persona} не удался", persona.Id);
        }
        return persona;
    }

    // Подбор кандидатов-привязок: каталог целей владельца + профиль персоны → one-shot LLM
    // (строгий JSON-массив, ретрай как в quick-create), невалидные кандидаты отбрасываются.
    private async Task<List<PersonaBinding>> SuggestBindingsAsync(Persona persona)
    {
        var prompt = BuildSuggestPrompt(persona);
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");

        List<SuggestRaw>? raws = null;
        for (var attempt = 1; attempt <= 2 && raws is null; attempt++)
        {
            var raw = await _oneShot.RunAsync(prompt, model,
                TimeSpan.FromSeconds(90), HttpContext.RequestAborted);
            raws = ParseSuggestArray(raw);
            if (raws is null)
                _log.LogWarning("suggest bindings: ответ не распознан (попытка {Attempt}); сырой ответ: {Raw}",
                    attempt, raw.Length > 600 ? raw[..600] + "…" : raw);
        }
        if (raws is null) return [];

        var accepted = new List<PersonaBinding>(persona.Bindings ?? []);
        var result = new List<PersonaBinding>();
        foreach (var r in raws.Take(5))
        {
            var (binding, _) = ParseBinding(new PersonaBindingRequest(
                r.Type ?? "", r.Target ?? "", r.Path, r.Condition, r.Mode ?? "auto"));
            if (binding is null) continue;
            var err = await _bindings.ValidateAsync(UserId, binding, accepted);
            if (err is not null) continue;
            accepted.Add(binding);
            result.Add(binding);
        }
        return result;
    }

    private string BuildSuggestPrompt(Persona persona)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Подбери AI-персоне источники знаний и правила («привязки») под её роль. " +
                      "Выбирай ТОЛЬКО из каталога ниже (target — точный id из каталога).");
        sb.AppendLine($"\nПерсона: {persona.Role ?? "без роли"} ({persona.Name}).");
        if (!string.IsNullOrWhiteSpace(persona.Description))
            sb.AppendLine($"Кто это: {persona.Description.Trim()}");
        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt))
        {
            var character = persona.SystemPrompt.Trim();
            if (character.Length > 800) character = character[..800] + "…";
            sb.AppendLine($"Характер: {character}");
        }

        sb.AppendLine("\nКаталог целей:");
        var projects = _projects.GetByOwner(UserId);
        if (projects.Count > 0)
        {
            sb.AppendLine("Проекты (type \"project\", target = id):");
            foreach (var p in projects.Take(20)) sb.AppendLine($"- {p.Id} — {p.Name}");
        }
        var datasets = _bindings.KnownDatasets(UserId);
        if (datasets.Count > 0)
        {
            sb.AppendLine("Базы знаний (type \"knowledge\", target = id):");
            foreach (var d in datasets.Take(20)) sb.AppendLine($"- {d.Id} — {d.Label}");
        }
        var sources = _notes.GetSources(UserId);
        if (sources.Count > 0)
        {
            sb.AppendLine("Источники заметок (type \"notes\", target = key):");
            foreach (var s in sources.Take(20)) sb.AppendLine($"- {s.Key} — {s.Label}");
        }
        var skills = _skills.GetGlobalSkills();
        if (skills.Count > 0)
        {
            sb.AppendLine("Скиллы (type \"skill\", target = имя):");
            foreach (var s in skills.Take(20))
            {
                var desc = s.Description.Length > 120 ? s.Description[..120] + "…" : s.Description;
                sb.AppendLine($"- {s.Name} — {desc}");
            }
        }
        sb.AppendLine("Инструменты (type \"tool\", target = ключ):");
        foreach (var kv in PersonaBindingsService.ToolCatalog)
            sb.AppendLine($"- {kv.Key} — {kv.Value.Label}: {kv.Value.Hint}");

        sb.AppendLine("\nВерни ТОЛЬКО JSON-массив (без пояснений и markdown) из НЕ БОЛЕЕ 5 объектов:");
        sb.AppendLine("[{\"type\":\"project|knowledge|notes|tool|skill\",\"target\":\"id из каталога\"," +
                      "\"path\":\"папка (опционально)\",\"condition\":\"когда применять, 1-2 предложения по-русски\",\"mode\":\"auto\"}]");
        sb.AppendLine("Бери только цели, реально полезные роли персоны; если подходящих нет — верни [].");
        return sb.ToString();
    }

    // Парс JSON-массива из ответа модели (устойчиво к преамбуле/markdown-fence)
    private static List<SuggestRaw>? ParseSuggestArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<List<SuggestRaw>>(raw[start..(i + 1)],
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (System.Text.Json.JsonException) { return null; }
            }
        }
        return null;
    }

    private sealed record SuggestRaw(string? Type, string? Target, string? Path, string? Condition, string? Mode);

    // Превью содержимого источника для ai-condition (2-4 КБ: имена файлов/документов/заметок)
    private async Task<string?> BuildSourcePreviewAsync(string type, string target, string? path)
    {
        const int cap = 4000;
        try
        {
            switch (type.ToLowerInvariant())
            {
                case "project":
                case "projectpath":
                {
                    var project = _projects.GetById(target);
                    if (project is null || project.OwnerId != UserId || !Directory.Exists(project.RootPath))
                        return null;
                    var dir = string.IsNullOrWhiteSpace(path)
                        ? project.RootPath
                        : FileService.SafeJoinPublic(project.RootPath, path);
                    if (!Directory.Exists(dir)) return null;
                    var names = Directory.EnumerateFileSystemEntries(dir)
                        .Select(System.IO.Path.GetFileName)
                        .Where(n => n is not null && !n.StartsWith('.'))
                        .Take(40);
                    var preview = $"Проект «{project.Name}». Содержимое папки: {string.Join(", ", names)}";
                    // README — лучший источник сути проекта
                    var readme = System.IO.Path.Combine(dir, "README.md");
                    if (System.IO.File.Exists(readme))
                    {
                        var head = (await System.IO.File.ReadAllTextAsync(readme)).Trim();
                        if (head.Length > 2000) head = head[..2000] + "…";
                        preview += $"\nREADME.md:\n{head}";
                    }
                    return preview.Length > cap ? preview[..cap] + "…" : preview;
                }
                case "knowledge":
                {
                    var ds = _bindings.KnownDatasets(UserId).FirstOrDefault(d => d.Id == target);
                    if (ds.Id is null || !_knowledge.IsConfigured) return null;
                    var docs = await _knowledge.ListAllDocumentsAsync(target);
                    var names = docs.Data.Select(d => d.Name).Take(40);
                    return $"База знаний «{ds.Label}». Документы: {string.Join(", ", names)}";
                }
                case "notes":
                {
                    var summaries = _notes.GetSummaries(UserId, target, null).AsEnumerable();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        var prefix = path.Trim().Replace('\\', '/').Trim('/') + "/";
                        summaries = summaries.Where(s =>
                            s.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    }
                    var titles = summaries.Select(s => s.Title).Take(40).ToList();
                    return titles.Count == 0 ? null : $"Заголовки заметок: {string.Join(", ", titles)}";
                }
                case "skill":
                {
                    var skill = _skills.GetGlobalSkills()
                        .FirstOrDefault(s => string.Equals(s.Name, target, StringComparison.OrdinalIgnoreCase));
                    return skill is null ? null : $"Скилл «{skill.Name}»: {skill.Description}";
                }
                case "tool":
                    return PersonaBindingsService.ToolCatalog.TryGetValue(target, out var t)
                        ? $"Инструмент «{t.Label}»: {t.Hint}"
                        : null;
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ai-condition: превью источника {Type}:{Target}", type, target);
            return null;
        }
    }

    // --- Долгая память персоны (дёргается MCP memory-server и UI-панелью «что помнит персона») ---

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

    // --- @упоминания: спросить персону (persona_ask из MCP personas-server) ---

    // One-shot ответ персоны от своего лица: слой персоны (роль+характер) + recall её долгой
    // памяти + вопрос. Модель — модель персоны. Анти-рекурсия по построению: one-shot идёт
    // без MCP-серверов, «спросить третью персону» изнутри ответа невозможно.
    [HttpPost("ask")]
    public async Task<ActionResult> Ask([FromBody] PersonaAskRequest req)
    {
        if (!_flags.IsEnabled(UserId, FeatureFlagKeys.Personas) ||
            !_flags.IsEnabled(UserId, FeatureFlagKeys.PersonaMentions))
            return BadRequest(new { error = "Фича @упоминаний персон выключена" });
        if (string.IsNullOrWhiteSpace(req.Handle)) return BadRequest(new { error = "Не указан handle персоны" });
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest(new { error = "Пустой вопрос" });

        var persona = _personas.GetByHandle(UserId, req.Handle.Trim().TrimStart('@'));
        if (persona is null) return NotFound(new { error = $"Персона @{req.Handle} не найдена" });

        // Слой персоны + релевантная память (best-effort: без памяти ответ всё равно валиден)
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(SessionManager.BuildPersonaPrompt(persona));
        if (persona.MemoryEnabled)
        {
            try
            {
                var recall = await _memory.BuildRecallAsync(UserId, persona.Id, req.Question, topK: 5, minScore: 0.02);
                if (!string.IsNullOrWhiteSpace(recall)) sb.AppendLine().AppendLine(recall);
            }
            catch (Exception ex) { _log.LogWarning(ex, "persona_ask: recall памяти {Persona}", persona.Id); }
        }
        sb.AppendLine();
        sb.AppendLine("Тебя спрашивает ассистент пользователя из другого разговора. Этот разговор ты не видишь — " +
                      "отвечай по вопросу и переданному контексту, от своего лица и в своём характере, по существу.");
        if (!string.IsNullOrWhiteSpace(req.Context))
            sb.AppendLine($"\nКонтекст: {req.Context.Trim()}");
        sb.AppendLine($"\nВопрос: {req.Question.Trim()}");

        var timeout = TimeSpan.FromMilliseconds(
            int.TryParse(_config["Persona:AskTimeoutMs"], out var t) ? t : 120_000);
        try
        {
            var answer = await _oneShot.RunAsync(sb.ToString(), _oneShot.NormalizeModel(persona.Model),
                timeout, HttpContext.RequestAborted);
            if (string.IsNullOrWhiteSpace(answer))
                return StatusCode(502, new { error = "Персона не ответила (пустой ответ модели)" });
            return Ok(new { handle = persona.Handle, name = persona.Name, role = persona.Role, answer });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "persona_ask: one-shot ответа @{Handle} не удался", persona.Handle);
            return StatusCode(502, new { error = $"Не удалось получить ответ персоны: {ex.Message}" });
        }
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
    bool? MemoryEnabled,
    List<string>? Tools = null,
    // Явные привязки при создании (валидируются до создания персоны)
    List<PersonaBindingRequest>? Bindings = null,
    // true — после создания подобрать привязки AI (за флагом persona-bindings, best-effort)
    bool? AutoBindings = null);

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
    bool? MemoryEnabled,
    List<string>? Tools = null);

public record CreatePersonaChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null);

public record RememberRequest(string Type, string Text, List<string>? Tags = null, string? SourceSessionId = null);

public record GenerateAvatarRequest(string? Prompt = null, int? Count = null);

public record AiCharacterRequest(string? Name, string? Role, string? Description, string? Current, string? Instruction);

// AutoBindings: null/true — подобрать привязки AI после создания (за флагом persona-bindings),
// false — не подбирать.
public record AiQuickCreateRequest(string Prompt, PersonaScope? Scope = null, string? ProjectId = null,
    bool? AutoBindings = null);

// DTO привязки персоны: type/mode — строками (project|projectPath|knowledge|notes|tool|skill;
// auto|always|off), парсятся без учёта регистра.
public record PersonaBindingRequest(string Type, string Target, string? Path = null,
    string? Condition = null, string? Mode = null);

public record PersonaBindingsSetRequest(List<PersonaBindingRequest>? Bindings);

public record AiConditionRequest(string Type, string Target, string? Path = null);

public record SelectAvatarRequest(string File);

public record PersonaAskRequest(string Handle, string Question, string? Context = null);
