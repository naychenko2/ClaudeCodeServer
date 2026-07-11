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
    private readonly FalImageService _falImage;
    private readonly Services.Llm.OneShotClaudeRunner _oneShot;
    private readonly FeatureFlagService _flags;
    private readonly PersonaPromptBuilder _promptBuilder;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonasController> _log;
    private readonly IHubContext<SessionHub> _hub;

    public PersonasController(PersonaManager personas, ProjectManager projects,
        SessionManager sessions, PersonaMemoryService memory, FalImageService falImage,
        Services.Llm.OneShotClaudeRunner oneShot, FeatureFlagService flags,
        PersonaPromptBuilder promptBuilder, IConfiguration config,
        ILogger<PersonasController> log, IHubContext<SessionHub> hub)
    {
        _personas = personas;
        _projects = projects;
        _sessions = sessions;
        _memory = memory;
        _falImage = falImage;
        _oneShot = oneShot;
        _flags = flags;
        _promptBuilder = promptBuilder;
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
        if (!TryParseAccess(req.Access, out var access))
            return BadRequest("Неверный профиль доступа (ожидается full | readOnly | custom)");

        var persona = _personas.Create(UserId, req.Name, req.Role, req.Description, req.SystemPrompt,
            req.Model, req.Effort, scope, req.ProjectId, req.Color, req.Greeting,
            req.MemoryEnabled ?? true, req.Tools, req.Contract,
            access ?? PersonaAccess.Full, req.DisallowedTools);
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
        if (!TryParseAccess(req.Access, out var access))
            return BadRequest("Неверный профиль доступа (ожидается full | readOnly | custom)");

        var persona = _personas.Update(id, UserId, req.Name, req.Role, req.Description, req.SystemPrompt,
            req.Model, req.Effort, req.Scope, req.ProjectId, req.Color, req.Greeting,
            req.MemoryEnabled, req.Tools, req.Contract, access, req.DisallowedTools);
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
    // (one-shot LLM). Возвращает структурированный контракт (P1) для подстановки в форму.
    [HttpPost("ai/character")]
    public async Task<ActionResult> AiCharacter([FromBody] AiCharacterRequest req)
    {
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
        var prompt = BuildCharacterPrompt(req);
        try
        {
            var raw = await _oneShot.RunAsync(prompt, model, TimeSpan.FromSeconds(90), HttpContext.RequestAborted);
            var contract = PersonaManager.NormalizeContract(ParseJsonObject<PersonaContract>(raw));
            if (contract is null)
            {
                _log.LogWarning("ai/character: контракт не распознан; сырой ответ: {Raw}",
                    raw.Length > 600 ? raw[..600] + "…" : raw);
                return StatusCode(502, new { error = "Модель не вернула корректный контракт — попробуйте ещё раз" });
            }
            return Ok(new { contract });
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
                      "Составь структурированный контракт персоны — как она общается и действует.");
        if (!string.IsNullOrWhiteSpace(req.Role)) sb.AppendLine($"Роль персоны: {req.Role.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Name)) sb.AppendLine($"Имя персоны: {req.Name.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Description)) sb.AppendLine($"Кратко: {req.Description.Trim()}.");
        if (!string.IsNullOrWhiteSpace(req.Current))
        {
            // Current — либо legacy-текст характера, либо сериализованный контракт (JSON)
            sb.AppendLine($"\nТекущий характер (текст или JSON-контракт — переработай/улучши его):\n{req.Current.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(req.Instruction))
            sb.AppendLine($"\nПожелание пользователя: {req.Instruction.Trim()}");
        sb.AppendLine("\nВерни ТОЛЬКО JSON-объект (без пояснений и markdown) с полями:");
        sb.AppendLine("  character — характер и манера общения: обращение на «ты» («Ты …»), живо и конкретно, 2-4 предложения;");
        sb.AppendLine("  tone — тон одной короткой фразой (напр. «тепло и на равных», «сухо и по делу»);");
        sb.AppendLine("  mustDo — массив из 2-4 правил «что делать всегда», каждое — короткое предложение;");
        sb.AppendLine("  mustNot — массив из 2-4 правил «чего не делать никогда»;");
        sb.AppendLine("  outputFormat — требования к формату ответов, 1-2 предложения;");
        sb.AppendLine("  speechExamples — массив из 1-2 характерных реплик персоны от её лица.");
        sb.AppendLine("Всё по-русски. НЕ упоминай имя модели.");
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

        // 2. Создаём персону с заполненными полями; характер — сразу контрактом (P1)
        var color = ValidColor(draft.Color) ? draft.Color : "orange";
        var contract = new PersonaContract
        {
            Character = draft.Character,
            Tone = draft.Tone,
            MustDo = draft.MustDo,
            MustNot = draft.MustNot,
            OutputFormat = draft.OutputFormat,
            SpeechExamples = draft.SpeechExamples,
        };
        var persona = _personas.Create(UserId, draft.Name!, draft.Role, draft.Description,
            systemPrompt: null, model: null, effort: null, scope, req.ProjectId,
            color, draft.Greeting, memoryEnabled: true, tools: null, contract: contract);

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
        sb.AppendLine("  tone — тон одной короткой фразой по-русски (напр. «тепло и на равных», «сухо и по делу»);");
        sb.AppendLine("  mustDo — массив из 2-4 правил «что делать всегда», по-русски, короткими предложениями;");
        sb.AppendLine("  mustNot — массив из 2-4 правил «чего не делать никогда», по-русски;");
        sb.AppendLine("  outputFormat — требования к формату ответов, 1-2 предложения, по-русски;");
        sb.AppendLine("  speechExamples — массив из 1-2 характерных реплик персоны от её лица, по-русски;");
        sb.AppendLine("  greeting — первое приветственное сообщение персоны пользователю, 1-2 предложения, по-русски, в её характере;");
        sb.AppendLine("  color — один из: yellow, orange, blue, green, purple, red, brown, cyan, pink (подходит образу);");
        sb.AppendLine("  avatarPrompt — описание внешности для фотопортрета, по-английски, 5-15 слов (пол, возраст, стиль, настроение, фон).");
        return sb.ToString();
    }

    private static DraftRaw? ParseDraft(string raw) => ParseJsonObject<DraftRaw>(raw);

    // Парс первого сбалансированного JSON-объекта из ответа модели
    // (устойчиво к преамбуле/markdown-fence). Общий для quick-create и ai/character.
    private static T? ParseJsonObject<T>(string raw) where T : class
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
                    return System.Text.Json.JsonSerializer.Deserialize<T>(raw[start..(i + 1)],
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
        string? Character, string? Tone, List<string>? MustDo, List<string>? MustNot,
        string? OutputFormat, List<string>? SpeechExamples,
        string? Greeting, string? Color, string? AvatarPrompt);

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

    // Запомнить (явный write-path); salience — важность 0..1 (опционально)
    [HttpPost("{id}/memory")]
    public async Task<ActionResult<PersonaMemoryEntry>> Remember(string id, [FromBody] RememberRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Пустой текст");
        if (!Enum.TryParse<PersonaMemoryType>(req.Type, true, out var type)) type = PersonaMemoryType.Semantic;
        var entry = _memory.Remember(UserId, id, type, req.Text, req.Tags, req.SourceSessionId, req.Salience);
        if (entry is null) return NotFound();
        await Broadcast("memory", id);
        return Ok(entry);
    }

    // --- Рабочий фокус персоны (P3): «что я сейчас делаю» ---

    // Текущий фокус; 204 — фокуса нет
    [HttpGet("{id}/focus")]
    public ActionResult<PersonaWorkingFocus> GetFocus(string id)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        var focus = _memory.GetFocus(UserId, id);
        return focus is null ? NoContent() : Ok(focus);
    }

    // Сбросить фокус (кнопка «Сбросить» в карточке памяти)
    [HttpDelete("{id}/focus")]
    public async Task<IActionResult> ClearFocus(string id)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        _memory.ClearFocus(UserId, id);
        await Broadcast("memory", id);
        return NoContent();
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
        sb.AppendLine(_promptBuilder.Build(persona, persona.Model, greeted: false));
        if (persona.MemoryEnabled)
        {
            try
            {
                // Шкала скоринга — взвешенная сумма (PersonaMemoryScorer), порог ~0.30
                // (старый 0.02 был для шкалы произведения)
                var minScore = double.TryParse(_config["Persona:RecallMinScore"],
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0.30;
                var recall = await _memory.BuildRecallAsync(UserId, persona.Id, req.Question, topK: 5, minScore);
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

    // Парс профиля доступа из запроса: null/пусто → «не менять» (out null),
    // валидная строка → значение, мусор → false (400 у вызывающего)
    private static bool TryParseAccess(string? raw, out PersonaAccess? access)
    {
        access = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!Enum.TryParse<PersonaAccess>(raw, ignoreCase: true, out var parsed)) return false;
        access = parsed;
        return true;
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
    // Структурированный контракт характера (P1); null — не задан
    PersonaContract? Contract = null,
    // Профиль доступа (P6): full | readOnly | custom; null — дефолт (full)
    string? Access = null,
    // Свой список запрещённых инструментов (для custom)
    List<string>? DisallowedTools = null);

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
    List<string>? Tools = null,
    // null — не менять; объект с пустыми слотами — сбросить контракт
    PersonaContract? Contract = null,
    // Профиль доступа (P6): full | readOnly | custom; null — не менять
    string? Access = null,
    // Свой список запрещённых инструментов (для custom); null — не менять
    List<string>? DisallowedTools = null);

public record CreatePersonaChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null);

public record RememberRequest(string Type, string Text, List<string>? Tags = null,
    string? SourceSessionId = null, double? Salience = null);

public record GenerateAvatarRequest(string? Prompt = null, int? Count = null);

public record AiCharacterRequest(string? Name, string? Role, string? Description, string? Current, string? Instruction);

public record AiQuickCreateRequest(string Prompt, PersonaScope? Scope = null, string? ProjectId = null);

public record SelectAvatarRequest(string File);

public record PersonaAskRequest(string Handle, string Question, string? Context = null);
