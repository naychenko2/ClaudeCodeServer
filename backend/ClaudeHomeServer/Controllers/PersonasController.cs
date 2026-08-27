using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Personas;
using ClaudeHomeServer.Services.TriggerSources;
using ClaudeHomeServer.Services.Tts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

// Персоны, per-owner (изоляция как у задач/заметок — по claim sub).
// Тяжёлая оркестрация (создание/правка/удаление/дефолт, AI-команда, подбор привязок,
// аватар, автоматизации) живёт в PersonasCrudService — общий код с http-тулсетом персон
// (ADR-012, фаза 2 волна 2); экшены ниже — тонкие обёртки, читающие UserId и заголовок.
[ApiController]
[Authorize]
[Route("api/personas")]
public class PersonasController(
    PersonaManager personas,
    ProjectManager projects,
    SessionManager sessions,
    PersonaMemoryService memory,
    PersonaBindingsService bindings,
    NotesService notes,
    SkillsService skills,
    KnowledgeService knowledge,
    Services.Images.ImageGenerationService images,
    Services.Llm.OneShotClaudeRunner oneShot,
    Services.Llm.ICheapTextRunner cheap,
    PersonaPromptBuilder promptBuilder,
    PersonaAskService ask,
    PersonaAutomationService automation,
    PersonaDraftService drafts,
    PersonasCrudService crud,
    IConfiguration config,
    ILogger<PersonasController> log,
    IHubContext<SessionHub> hub) : ControllerBase
{
    private readonly PersonaManager _personas = personas;
    private readonly ProjectManager _projects = projects;
    private readonly SessionManager _sessions = sessions;
    private readonly PersonaMemoryService _memory = memory;
    private readonly PersonaBindingsService _bindings = bindings;
    private readonly NotesService _notes = notes;
    private readonly SkillsService _skills = skills;
    private readonly KnowledgeService _knowledge = knowledge;
    private readonly Services.Images.ImageGenerationService _images = images;
    private readonly Services.Llm.OneShotClaudeRunner _oneShot = oneShot;
    private readonly Services.Llm.ICheapTextRunner _cheap = cheap;
    private readonly PersonaPromptBuilder _promptBuilder = promptBuilder;
    private readonly PersonaAskService _ask = ask;
    private readonly PersonaAutomationService _automation = automation;
    private readonly PersonaDraftService _drafts = drafts;
    private readonly PersonasCrudService _crud = crud;
    private readonly IConfiguration _config = config;
    private readonly ILogger<PersonasController> _log = log;
    private readonly IHubContext<SessionHub> _hub = hub;

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Сессия-вызыватель MCP-вызова (заголовок ставит конфиг хода): по ней crud отличает
    // вызовы из чата (онбординг-предохранители) от рукотворного REST
    private string? CallerSessionId() =>
        Request.Headers[DenyOnDelegatedTurnAttribute.CallerHeader].FirstOrDefault();

    private Task Broadcast(string action, string? personaId = null) =>
        _hub.Clients.Group("user_" + UserId)
            .SendAsync("message", new PersonasChangedMessage(action, personaId));

    // Список персон владельца. scope: "context" — глобальные + этого проекта (+ extraProjectIds/
    // extraPersonaIds — кросс-проектные привязки ProjectPersonas текущей персоны-вызывающего);
    // "project" — только привязанные к projectId; "global" — только глобальные;
    // иначе — все персоны владельца.
    [HttpGet]
    public ActionResult<IReadOnlyList<Persona>> List(
        [FromQuery] string? scope, [FromQuery] string? projectId,
        [FromQuery] string? extraProjectIds = null, [FromQuery] string? extraPersonaIds = null)
    {
        if (string.Equals(scope, "context", StringComparison.OrdinalIgnoreCase))
        {
            var result = _personas.GetForContext(UserId, projectId).ToList();
            var extraProjects = SplitCsv(extraProjectIds);
            var extraPersonas = SplitCsv(extraPersonaIds).ToHashSet(StringComparer.Ordinal);
            if (extraProjects.Count > 0 || extraPersonas.Count > 0)
            {
                var seen = result.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var p in _personas.GetByOwner(UserId))
                {
                    if (seen.Contains(p.Id)) continue;
                    var included = extraPersonas.Contains(p.Id)
                        || (p.Scope == PersonaScope.Project && p.ProjectId is not null
                            && extraProjects.Contains(p.ProjectId));
                    if (!included) continue;
                    result.Add(p);
                    seen.Add(p.Id);
                }
            }
            return Ok(result);
        }
        if (string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase))
            return Ok(_personas.GetByOwner(UserId)
                .Where(p => p.Scope == PersonaScope.Project && p.ProjectId == projectId).ToList());
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
            return Ok(_personas.GetByOwner(UserId)
                .Where(p => p.Scope == PersonaScope.Global).ToList());
        return Ok(_personas.GetByOwner(UserId));
    }

    // Подбор максимально релевантной персоны под задачу (для чат-действий AI-хаба, которые
    // открывают новый чат). Локальная модель выбирает из доступных персон; нет подходящей /
    // нет персон / ошибка → personaId=null (чат создаётся без персоны, как раньше).
    // requiredTool — ключ инструментов, без которого действие не выполнить (напр.
    // notes-annotations у разбора комментариев): персоны без него из выбора выбывают,
    // иначе подобранная персона ответила бы «инструмент недоступен». Пустой остаток —
    // personaId=null, то есть обычный чат, у которого есть всё.
    [HttpPost("match")]
    public async Task<IActionResult> MatchPersona([FromBody] MatchPersonaRequest req)
    {
        var task = (req?.Task ?? "").Trim();
        var personas = _personas.GetForContext(UserId, req?.ProjectId);
        var requiredTool = (req?.RequiredTool ?? "").Trim();
        if (requiredTool.Length > 0)
            personas = personas.Where(p => _bindings.ToolKeyAvailable(UserId, p, requiredTool)).ToList();
        if (task.Length == 0 || personas.Count == 0) return Ok(new { personaId = (string?)null });

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Ниже — задача пользователя и список доступных персон-ассистентов. Выбери ОДНУ персону, " +
            "максимально релевантную задаче по её роли и специализации. Ответь ТОЛЬКО id выбранной персоны " +
            "(одной строкой). Если ни одна явно не подходит — ответь none.");
        sb.AppendLine($"\nЗадача: {task}\n");
        sb.AppendLine("Персоны (id | роль | описание):");
        foreach (var p in personas.Take(40))
        {
            var desc = p.Description ?? p.Contract?.Character ?? "";
            if (desc.Length > 120) desc = desc[..120];
            sb.AppendLine($"{p.Id} | {p.Role ?? p.Name} | {desc.Replace('\n', ' ')}");
        }
        try
        {
            var raw = await _cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaMatch, sb.ToString(),
                _config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku", UserId, ct: HttpContext.RequestAborted);
            var first = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            // Устойчиво к обрамлению: ищем id из списка внутри ответа модели
            var id = personas.FirstOrDefault(p => first.Contains(p.Id, StringComparison.Ordinal))?.Id;
            return Ok(new { personaId = id });
        }
        catch { return Ok(new { personaId = (string?)null }); }
    }

    // Каталог пантеона OmO: карточки-шаблоны + связь с уже подключёнными персонами
    // владельца (connectedPersonaId по TemplateKey). Единый источник — бэкенд-каталог.
    [HttpGet("pantheon")]
    public IActionResult GetPantheon() =>
        Ok(new
        {
            templates = Services.Prompts.OmoPantheonCatalog.All.Select(t => new
            {
                key = t.Key,
                role = t.Role,
                name = t.Name,
                description = t.Description,
                contract = t.Contract,
                greeting = t.Greeting,
                color = t.Color,
                tools = t.Tools,
                access = t.Access,
                model = t.Model,
                // Роль каталога задаёт уровень модели, а не конкретную (см. PantheonTemplate.ModelTier)
                modelTier = t.ModelTier?.ToString().ToLowerInvariant(),
                effort = t.Effort,
                specialty = t.Specialty,
                connectedPersonaId = _personas.GetByTemplateKey(UserId, t.Key)?.Id,
            }),
        });

    // Подключить команду пантеона: идемпотентно создаёт глобальные персоны с готовыми
    // именами для недостающих ключей (пустой keys = все роли каталога).
    [HttpPost("pantheon/connect")]
    public async Task<IActionResult> ConnectPantheon([FromBody] ConnectPantheonRequest? req)
    {
        try
        {
            var personas = _personas.ConnectPantheon(UserId, req?.Keys);
            await Broadcast("created");
            return Ok(personas);
        }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("{id}")]
    public ActionResult<Persona> Get(string id)
    {
        var persona = _personas.Get(id, UserId);
        return persona is null ? NotFound() : Ok(persona);
    }

    [HttpPost]
    public Task<ActionResult<Persona>> Create([FromBody] CreatePersonaRequest req) =>
        _crud.CreateAsync(UserId, req, CallerSessionId());

    [HttpPut("{id}")]
    public Task<ActionResult<Persona>> Update(string id, [FromBody] UpdatePersonaRequest req) =>
        _crud.UpdateAsync(UserId, id, req, CallerSessionId());

    [HttpDelete("{id}")]
    public Task<IActionResult> Delete(string id, [FromQuery] string? successorId = null) =>
        _crud.DeleteAsync(UserId, id, successorId);

    // Назначить персону дефолтной: глобальную — личным дефолтом владельца, проектную —
    // дефолтом её проекта. Вызов из чата разрешён ТОЛЬКО онбординг-сессии — гейт и
    // финализация онбординга внутри crud (по callerSessionId).
    [HttpPost("{id}/make-default")]
    [DenyOnDelegatedTurn("Назначение дефолт-персоны")]
    public Task<IActionResult> MakeDefault(string id) =>
        _crud.MakeDefaultAsync(UserId, id, CallerSessionId());

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
            var chat = await _sessions.CreatePersonaChatAsync(UserId, id, mode, req.ResumeSessionId, req.Name,
                contextProjectId: req.ProjectId);
            return Ok(chat);
        }
        catch (KeyNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // --- Аватар персоны ---

    // Доступна ли AI-генерация аватара и чем именно её сделают (провайдер + модель).
    // Поле generate НЕ переименовывать: на него завязаны формы персоны на фронте.
    [HttpGet("avatar/caps")]
    public ActionResult Caps()
    {
        var provider = _images.ActiveProviderFor(Services.Images.ImagePlaces.PersonaAvatar);
        return Ok(new
        {
            generate = provider is not null,
            provider = provider?.Key,
            providerName = provider?.DisplayName,
            // Эффективная модель настройки; null — дефолт самого драйвера
            model = provider is null ? null : _images.ModelFor(Services.Images.ImagePlaces.PersonaAvatar, provider.Key),
        });
    }

    // Сгенерировать НЕСКОЛЬКО вариантов аватар-фото по описанию (для выбора); провайдера
    // и модель выбирает роутер по настройке инстанса. Кандидаты сохраняются во временную
    // папку, аватар персоны НЕ меняется до выбора.
    // prompt пуст → строим фото-промпт из имени/описания персоны.
    [HttpPost("{id}/avatar/generate")]
    public Task<ActionResult> GenerateAvatar(string id, [FromBody] GenerateAvatarRequest req) =>
        _crud.GenerateAvatarAsync(UserId, id, req);

    // Отдать кандидата аватара (превью в галерее выбора). access_token в query для <img>.
    [HttpGet("{id}/avatar/candidate/{file}")]
    public IActionResult AvatarCandidate(string id, string file)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        var safe = Path.GetFileName(file);   // защита от path-traversal
        var full = Path.Combine(_personas.AssetsDir, id, "candidates", safe);
        if (!System.IO.File.Exists(full)) return NotFound();
        return ImageAssetHelper.PhysicalFileByExt(full);
    }

    // Выбрать кандидата как аватар персоны: делаем основным, чистим остальных кандидатов.
    [HttpPost("{id}/avatar/select")]
    public Task<ActionResult<Persona>> SelectAvatar(string id, [FromBody] SelectAvatarRequest req) =>
        _crud.SelectAvatarAsync(UserId, id, req);

    // Отдать картинку аватара. JWT принимается и в query access_token (браузерный <img>).
    [HttpGet("{id}/avatar")]
    public IActionResult Avatar(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null || persona.Avatar.Kind != PersonaAvatarKind.Image
            || string.IsNullOrEmpty(persona.Avatar.ImageFile))
            return NotFound();

        var full = Path.Combine(_personas.AssetsDir, id, persona.Avatar.ImageFile);
        return System.IO.File.Exists(full) ? ImageAssetHelper.PhysicalFileByExt(full) : NotFound();
    }

    // Оригинал загруженного аватара (для перекропа). access_token в query — как GET avatar.
    [HttpGet("{id}/avatar/original")]
    public IActionResult AvatarOriginal(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null || string.IsNullOrEmpty(persona.Avatar.OriginalFile))
            return NotFound();

        var full = Path.Combine(_personas.AssetsDir, id, persona.Avatar.OriginalFile);
        return System.IO.File.Exists(full) ? ImageAssetHelper.PhysicalFileByExt(full) : NotFound();
    }

    // Загрузка своего аватара: оригинал + кропнутый квадрат + параметры кропа (JSON).
    // Валидация: заявленный ContentType из белого списка И настоящие magic bytes;
    // расширение файла — по фактическому типу, а не по имени от клиента.
    [HttpPost("{id}/avatar/upload")]
    [RequestSizeLimit(15_000_000)]
    public async Task<ActionResult<Persona>> UploadAvatar(string id,
        [FromForm] IFormFile? original, [FromForm] IFormFile? cropped, [FromForm] string? crop)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (original is null || cropped is null)
            return BadRequest(new { error = "Нужны файлы original и cropped" });

        var originalCheck = await ImageAssetHelper.ValidateImageAsync(original);
        if (originalCheck.Error is not null) return BadRequest(new { error = originalCheck.Error });
        var croppedCheck = await ImageAssetHelper.ValidateImageAsync(cropped);
        if (croppedCheck.Error is not null) return BadRequest(new { error = croppedCheck.Error });

        var cropState = ImageAssetHelper.ParseCrop(crop);

        var dir = Path.Combine(_personas.AssetsDir, id);
        Directory.CreateDirectory(dir);
        var originalName = $"original-{Guid.NewGuid():N}{originalCheck.Ext}";
        var imageName = $"avatar-{Guid.NewGuid():N}{croppedCheck.Ext}";
        await ImageAssetHelper.SaveFormFileAsync(original, Path.Combine(dir, originalName));
        await ImageAssetHelper.SaveFormFileAsync(cropped, Path.Combine(dir, imageName));

        var updated = _personas.SetAvatarUploaded(id, UserId, imageName, originalName, cropState);
        await Broadcast("updated", id);
        return Ok(updated);
    }

    // Перекроп сохранённого оригинала: новая кропнутая картинка + параметры.
    [HttpPost("{id}/avatar/recrop")]
    [RequestSizeLimit(5_000_000)]
    public async Task<ActionResult<Persona>> RecropAvatar(string id,
        [FromForm] IFormFile? cropped, [FromForm] string? crop)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (string.IsNullOrEmpty(persona.Avatar.OriginalFile))
            return BadRequest(new { error = "У персоны нет оригинала для перекропа" });
        if (cropped is null) return BadRequest(new { error = "Нужен файл cropped" });

        var croppedCheck = await ImageAssetHelper.ValidateImageAsync(cropped);
        if (croppedCheck.Error is not null) return BadRequest(new { error = croppedCheck.Error });

        var dir = Path.Combine(_personas.AssetsDir, id);
        Directory.CreateDirectory(dir);
        var imageName = $"avatar-{Guid.NewGuid():N}{croppedCheck.Ext}";
        await ImageAssetHelper.SaveFormFileAsync(cropped, Path.Combine(dir, imageName));

        var updated = _personas.SetAvatarRecropped(id, UserId, imageName, ImageAssetHelper.ParseCrop(crop));
        await Broadcast("updated", id);
        return Ok(updated);
    }

    // AI-помощь с характером персоны: сгенерировать с нуля или улучшить/дополнить существующий
    // (one-shot LLM). Возвращает структурированный контракт (P1) для подстановки в форму.
    // Подбор голоса по характеру персоны. БЕЗ id: характер в момент подбора живёт в форме и
    // ещё не сохранён, а у новой персоны id попросту нет.
    //
    // Три разных исхода, а не один: модель никого не выбрала (200 + voice: null), модель
    // ответила мусором (502) и сбой вызова (502). Молчание вместо ответа годится фоновому
    // месту вроде значка проекта — здесь человек нажал кнопку и ждёт реакции.
    [HttpPost("ai/voice")]
    public async Task<ActionResult> AiVoice([FromBody] AiVoiceRequest req)
    {
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
        try
        {
            var raw = await _cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaVoice,
                BuildVoicePrompt(req), model, UserId, ct: HttpContext.RequestAborted);

            var picked = ParseVoiceAnswer(raw);
            if (picked is null && raw.Contains("none", StringComparison.OrdinalIgnoreCase))
                return Ok(new { voice = (string?)null, role = (string?)null });

            if (picked is null)
            {
                _log.LogWarning("ai/voice: голос не распознан; сырой ответ: {Raw}",
                    raw.Length > 600 ? raw[..600] + "…" : raw);
                return StatusCode(502, new { error = "Модель не выбрала голос из списка — попробуйте ещё раз" });
            }
            return Ok(new { voice = picked.Value.Voice, role = picked.Value.Role });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Не удалось подобрать голос: {ex.Message}" });
        }
    }

    // Ответ модели: «голос» либо «голос амплуа». Ищем ключ каталога по границам слова —
    // модель любит добавить объяснение, и подстрочный поиск увёл бы парсер не туда
    internal static (string Voice, string? Role)? ParseVoiceAnswer(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var words = System.Text.RegularExpressions.Regex.Matches(raw, @"[A-Za-z_]+")
            .Select(m => m.Value).ToList();

        foreach (var word in words)
        {
            var voice = Services.Tts.TtsVoiceCatalog.Canonical(word);
            if (voice is null) continue;
            // Амплуа берём только рядом стоящее и только поддержанное этим голосом
            var role = words.FirstOrDefault(w => Services.Tts.TtsVoiceCatalog.SupportsRole(voice, w));
            return (voice, role);
        }
        return null;
    }

    private static string BuildVoicePrompt(AiVoiceRequest req)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Подбери голос синтеза речи, которым будет говорить персона-ассистент.");
        if (!string.IsNullOrWhiteSpace(req.Name)) sb.AppendLine($"Имя: {req.Name.Trim()}");
        if (!string.IsNullOrWhiteSpace(req.Role)) sb.AppendLine($"Роль: {req.Role.Trim()}");
        if (!string.IsNullOrWhiteSpace(req.Description)) sb.AppendLine($"Описание: {req.Description.Trim()}");
        if (!string.IsNullOrWhiteSpace(req.Character)) sb.AppendLine($"Характер: {req.Character.Trim()}");
        if (!string.IsNullOrWhiteSpace(req.Tone)) sb.AppendLine($"Тон: {req.Tone.Trim()}");

        sb.AppendLine();
        sb.AppendLine("Доступные голоса (ключ · описание · пол · доступные амплуа):");
        foreach (var v in Services.Tts.TtsVoiceCatalog.All)
        {
            var roles = v.Roles.Count > 0 ? string.Join(", ", v.Roles) : "нет";
            var gender = v.Gender == Services.Tts.TtsVoiceCatalog.Gender.Female ? "женский" : "мужской";
            sb.AppendLine($"{v.Voice} · {v.Label} · {gender} · амплуа: {roles}");
        }

        sb.AppendLine();
        sb.AppendLine("Ответь ОДНОЙ строкой: ключ голоса, либо ключ голоса и амплуа через пробел. " +
                      "Амплуа указывай только из списка доступных для этого голоса. " +
                      "Если ни один голос не подходит — ответь: none. " +
                      "Никаких пояснений.");
        return sb.ToString();
    }

    [HttpPost("ai/character")]
    public async Task<ActionResult> AiCharacter([FromBody] AiCharacterRequest req)
    {
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
        var prompt = BuildCharacterPrompt(req);
        try
        {
            var raw = await _cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaAiCharacter,
                prompt, model, UserId, jsonFormat: "json", ct: HttpContext.RequestAborted);
            var contract = PersonaManager.NormalizeContract(PersonaDraftService.ParseJsonObject<PersonaContract>(raw));
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
        if (scope == PersonaScope.Project && !_crud.ValidProject(UserId, req.ProjectId))
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
                raw = await _cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaQuickCreate,
                    _drafts.BuildDraftPrompt(req.Prompt), model, UserId, jsonFormat: "json",
                    ct: HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "quick-create: one-shot упал (попытка {Attempt})", attempt);
                if (attempt == 2)
                    return StatusCode(502, new { error = $"Не удалось сгенерировать черновик: {ex.Message}" });
                continue;
            }
            draft = _drafts.ParseDraft(raw);
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
        var color = PersonasCrudService.ValidColor(draft.Color) ? draft.Color : "orange";
        var contract = new PersonaContract
        {
            Character = draft.Character,
            Tone = draft.Tone,
            MustDo = draft.MustDo,
            MustNot = draft.MustNot,
            OutputFormat = draft.OutputFormat,
            SpeechExamples = draft.SpeechExamples,
        };
        if (PersonaManager.ExceedsContractLimit(contract, null, 0, out var tooBig))
            return BadRequest(new { error = tooBig });
        var persona = _personas.Create(UserId, draft.Name!, draft.Role, draft.Description,
            systemPrompt: null, model: null, effort: null, scope, req.ProjectId,
            color, draft.Greeting, memoryEnabled: true, tools: null, contract: contract);

        // 3. Фото-аватар — автоматически (не критично: при сбое остаются инициалы)
        persona = await _crud.TryAutoGenerateAvatarAsync(UserId, persona, draft.AvatarPrompt);

        // 3.5. Проектной персоне — дефолтные привязки к данным проекта (файлы/заметки/знания)
        persona = _bindings.SeedProjectDefaults(UserId, persona);

        // 4. Авто-подбор привязок (по умолчанию включён) —
        // best-effort: сбой не роняет создание, персона остаётся без привязок
        if (req.AutoBindings != false)
            persona = await _crud.TryAutoBindAsync(UserId, persona, HttpContext.RequestAborted);

        await Broadcast("created", persona.Id);
        return Ok(persona);
    }

    // AI-формирование команды: по промпту + контексту проекта (CLAUDE.md) LLM предлагает набор
    // персон (роль/имя/характер/специальность) для создания в команде проекта. Возвращает
    // черновики — фронт показывает их для одобрения, затем создаёт через обычный POST /api/personas.
    [HttpPost("ai/team")]
    public Task<ActionResult> AiTeam([FromBody] AiTeamRequest req) =>
        _crud.AiTeamAsync(UserId, req.ProjectId, req.Prompt ?? "", HttpContext.RequestAborted);

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

        var (binding, parseError) = PersonasCrudService.ParseBinding(req);
        if (binding is null) return BadRequest(new { error = parseError });
        var err = await _bindings.ValidateAsync(UserId, binding, persona.Bindings, persona);
        if (err is not null) return BadRequest(new { error = err });

        var list = new List<PersonaBinding>(persona.Bindings ?? []) { binding };
        _personas.UpdateBindings(id, UserId, list);
        await Broadcast("updated", id);
        return Ok(binding);
    }

    // Полная замена набора привязок (PUT-семантика; дёргается MCP personas_bindings_set)
    [HttpPut("{id}/bindings")]
    public Task<ActionResult<IReadOnlyList<PersonaBinding>>> SetBindings(string id,
        [FromBody] PersonaBindingsSetRequest req) =>
        // Единая точка с http-тулсетом (personas_bindings_set): дословная копия тела здесь
        // уже однажды разошлась бы с сервисом — рассинхрон, ради устранения которого
        // PersonasCrudService и заводили
        _crud.SetBindingsAsync(UserId, id, req.Bindings);

    // Голос персоны в голосовом режиме. Пустое тело (или объект без единого заполненного
    // поля) снимает голос — персона возвращается к голосу инстанса.
    //
    // Здесь валидация СТРОГАЯ (400), в отличие от пути озвучки, где кривое значение молча
    // вырождается в дефолт: там на другом конце ухо человека посреди разговора, а тут —
    // форма, которой ошибку надо показать.
    [HttpPut("{id}/voice")]
    public async Task<ActionResult<Persona>> SetVoice(string id, [FromBody] PersonaVoice? req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();

        if (req is { IsEmpty: false })
        {
            if (!TtsVoiceCatalog.IsKnown(req.Voice))
                return BadRequest(new { error = $"Неизвестный голос синтеза: {req.Voice}" });
            if (!string.IsNullOrWhiteSpace(req.Role) && !TtsVoiceCatalog.SupportsRole(req.Voice, req.Role))
            {
                var roles = TtsVoiceCatalog.RolesFor(req.Voice);
                return BadRequest(new
                {
                    error = roles.Count == 0
                        ? $"Голос {req.Voice} не поддерживает амплуа"
                        : $"Голос {req.Voice} не умеет «{req.Role}»; доступны: {string.Join(", ", roles)}",
                });
            }
            if (req.Speed is { } speed and (< 0.1 or > 3.0))
                return BadRequest(new { error = "Темп речи допустим в диапазоне 0.1–3.0" });
        }

        var persona = _personas.SetVoice(id, UserId, req);
        // Как и все соседние мутации: смена голоса должна долетать до других вкладок и
        // списка персон, иначе там останется прежний голос до перезагрузки
        await Broadcast("updated", id);
        return Ok(persona);
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

        var (parsed, parseError) = PersonasCrudService.ParseBinding(req);
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
        var err = await _bindings.ValidateAsync(UserId, candidate, persona.Bindings, persona);
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

    // --- Автоматизации персоны (событийно-управляемая проактивность): правила «триггер → действие» ---

    [HttpGet("{id}/automation")]
    public ActionResult<IReadOnlyList<PersonaAutomationRule>> Automation(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        return Ok(persona.AutomationRules ?? []);
    }

    // Добавить правило (мгновенное сохранение)
    [HttpPost("{id}/automation")]
    public async Task<ActionResult<PersonaAutomationRule>> AddAutomationRule(string id,
        [FromBody] AutomationRuleRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        // Валидация projectId в triggerArgs
        var error = ValidateTriggerProjectId(persona, req);
        if (error is not null) return BadRequest(new { error });
        var rule = ParseRule(req);
        // Идемпотентность: повторный вызов с теми же параметрами (ретрай MCP-инструмента,
        // автопродолжение цикла, повтор хода после сбоя) не плодит дубль — если правило с той
        // же сигнатурой уже есть, возвращаем существующее вместо создания второго.
        var dup = persona.AutomationRules?.FirstOrDefault(r => AutomationSignature(r) == AutomationSignature(rule));
        if (dup is not null) return Ok(dup);
        var list = new List<PersonaAutomationRule>(persona.AutomationRules ?? []) { rule };
        _personas.UpdateRules(id, UserId, list);
        await Broadcast("updated", id);
        return Ok(rule);
    }

    // Сигнатура правила для дедупа: тип и аргументы триггера, тяжесть и инструкция действия, имя.
    // Полное совпадение = дубль (защита от повторных POST при ретраях/сбоях хода).
    private static string AutomationSignature(PersonaAutomationRule r) =>
        string.Join("",
            r.Trigger.Type,
            JsonSerializer.Serialize(r.Trigger.Args),
            r.Action.Weight,
            r.Action.Instruction?.Trim() ?? "",
            r.Name.Trim());

    // Валидация projectId в triggerArgs: для File/GitCommit-триггера нужна
    // Project/ProjectPath-привязка, для остальных — любая.
    // Возвращает null при успехе или текст ошибки для 400.
    private string? ValidateTriggerProjectId(Persona persona, AutomationRuleRequest req, PersonaAutomationRule? current = null)
    {
        var triggerArgs = req.TriggerArgs ?? current?.Trigger.Args;
        if (triggerArgs is null) return null;
        var projectId = triggerArgs.TryGetValue("projectId", out var el) ? el.GetString() : null;
        if (string.IsNullOrWhiteSpace(projectId)) return null;

        var triggerType = req.TriggerType ?? current?.Trigger.Type;
        var isFileTrigger = triggerType is AutomationTriggerType.File or AutomationTriggerType.GitCommit;

        if (isFileTrigger)
        {
            if (!_bindings.HasFileBindingToProject(persona, projectId))
                return $"Для триггера {triggerType} с projectId нужна привязка Project или ProjectPath к проекту «{_projects.GetById(projectId)?.Name ?? projectId}»";
        }
        else
        {
            if (!_bindings.HasAnyBindingToProject(persona, projectId))
                return $"Для правила с projectId нужна привязка Project, ProjectPath или ProjectTasks к проекту «{_projects.GetById(projectId)?.Name ?? projectId}»";
        }
        return null;
    }

    // Полная замена набора правил (PUT-семантика)
    [HttpPut("{id}/automation")]
    public async Task<ActionResult<IReadOnlyList<PersonaAutomationRule>>> SetAutomationRules(string id,
        [FromBody] AutomationRulesSetRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        var list = (req.Rules ?? []).Select(r => ParseRule(r)).ToList();
        var updated = _personas.UpdateRules(id, UserId, list);
        await Broadcast("updated", id);
        return Ok(updated.AutomationRules ?? []);
    }

    // Изменить одно правило (partial-merge: null-поля наследуются от текущего)
    [HttpPut("{id}/automation/{ruleId}")]
    public async Task<ActionResult<PersonaAutomationRule>> UpdateAutomationRule(string id, string ruleId,
        [FromBody] AutomationRuleRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        var current = persona.AutomationRules?.FirstOrDefault(r => r.Id == ruleId);
        if (current is null) return NotFound(new { error = "Правило не найдено" });

        // Валидация projectId — учитываем как новые, так и текущие параметры
        var error = ValidateTriggerProjectId(persona, req, current);
        if (error is not null) return BadRequest(new { error });

        var merged = ParseRule(req, current);
        var list = (persona.AutomationRules ?? []).Select(r => r.Id == ruleId ? merged : r).ToList();
        _personas.UpdateRules(id, UserId, list);
        await Broadcast("updated", id);
        return Ok(merged);
    }

    [HttpDelete("{id}/automation/{ruleId}")]
    public async Task<IActionResult> DeleteAutomationRule(string id, string ruleId)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        var list = persona.AutomationRules?.Where(r => r.Id != ruleId).ToList();
        if (list is null || list.Count == (persona.AutomationRules?.Count ?? 0))
            return NotFound(new { error = "Правило не найдено" });
        _personas.UpdateRules(id, UserId, list);
        await Broadcast("updated", id);
        return NoContent();
    }

    // Ручной прогон правила (UX «Проверить»): синтетическое событие, байпас троттлинга.
    [HttpPost("{id}/automation/{ruleId}/test")]
    public async Task<IActionResult> TestAutomationRule(string id, string ruleId)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (persona.AutomationRules?.Any(r => r.Id == ruleId) != true) return NotFound();
        _ = _automation.TestAsync(UserId, id, ruleId);   // в фон — ход может быть долгим
        return Accepted();
    }

    // AI-подбор правил автоматизации под роль персоны: возвращает кандидатов, НЕ сохраняет
    [HttpPost("{id}/automation/suggest")]
    public async Task<ActionResult> SuggestAutomation(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();

        try
        {
            var candidates = await SuggestAutomationRulesAsync(persona);
            return Ok(new { candidates });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "suggest automation для персоны {Persona}", id);
            return StatusCode(502, new { error = $"Не удалось подобрать правила: {ex.Message}" });
        }
    }

    // Генерация правил автоматизации под свободный запрос пользователя: тот же конвейер, что и
    // подбор под роль, но главный ориентир — текст пользователя. Возвращает кандидатов, НЕ сохраняет.
    [HttpPost("{id}/automation/generate")]
    public async Task<ActionResult> GenerateAutomation(string id, [FromBody] GenerateAutomationRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Опишите, что должна отслеживать персона" });

        try
        {
            var candidates = await SuggestAutomationRulesAsync(persona, req.Prompt.Trim());
            return Ok(new { candidates });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "generate automation для персоны {Persona}", id);
            return StatusCode(502, new { error = $"Не удалось создать правило: {ex.Message}" });
        }
    }

    // Подбор кандидатов-правил: каталог целей владельца + профиль персоны → one-shot LLM
    // (строгий JSON-массив, ретрай как в suggest bindings), невалидные кандидаты отбрасываются.
    // userPrompt задан — генерация под свободный запрос пользователя, иначе подбор под роль.
    private async Task<List<PersonaAutomationRule>> SuggestAutomationRulesAsync(Persona persona, string? userPrompt = null)
    {
        var prompt = BuildAutomationSuggestPrompt(persona, userPrompt);
        var model = _oneShot.NormalizeModel(_config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");

        List<SuggestRuleRaw>? raws = null;
        for (var attempt = 1; attempt <= 2 && raws is null; attempt++)
        {
            var raw = await _cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaAutomationSuggest,
                prompt, model, UserId, jsonFormat: "json", ct: HttpContext.RequestAborted);
            raws = ParseSuggestRuleArray(raw);
            if (raws is null)
                _log.LogWarning("suggest automation: ответ не распознан (попытка {Attempt}); сырой ответ: {Raw}",
                    attempt, raw.Length > 600 ? raw[..600] + "…" : raw);
        }
        if (raws is null) return [];

        var result = new List<PersonaAutomationRule>();
        foreach (var r in raws.Take(4))
        {
            if (!Enum.TryParse<AutomationTriggerType>(r.TriggerType, true, out var triggerType)) continue;
            if (!ValidateTriggerArgs(triggerType, r.TriggerArgs, out var normalizedArgs)) continue;
            if (!Enum.TryParse<AutomationActionWeight>(r.ActionWeight, true, out var weight))
                weight = AutomationActionWeight.Gate;

            result.Add(new PersonaAutomationRule
            {
                Name = string.IsNullOrWhiteSpace(r.Name) ? "Правило" : r.Name.Trim(),
                Trigger = new AutomationTrigger { Type = triggerType, Args = normalizedArgs },
                Condition = string.IsNullOrWhiteSpace(r.ConditionOnlyIf)
                    ? null
                    : new AutomationCondition { OnlyIf = r.ConditionOnlyIf.Trim() },
                Action = new AutomationAction
                {
                    Weight = weight,
                    Instruction = r.ActionInstruction?.Trim() ?? "",
                    RememberInHistory = r.RememberInHistory ?? false,
                    ExpiresAfterMinutes = 1440,
                },
            });
        }
        return result;
    }

    private string BuildAutomationSuggestPrompt(Persona persona, string? userPrompt = null)
    {
        var hasUserPrompt = !string.IsNullOrWhiteSpace(userPrompt);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(hasUserPrompt
            ? "Составь AI-персоне правило(а) автоматизации («когда X — делай Y») по запросу пользователя. " +
              "Правило — событийный триггер + действие персоны при срабатывании."
            : "Подбери AI-персоне правила автоматизации («когда X — делай Y») под её роль. " +
              "Правило — событийный триггер + действие персоны при срабатывании.");
        sb.AppendLine($"\nПерсона: {persona.Role ?? "без роли"} ({persona.Name}).");
        if (!string.IsNullOrWhiteSpace(persona.Description))
            sb.AppendLine($"Кто это: {persona.Description.Trim()}");
        // Характер: у персон с контрактом (P1) источник правды — Contract.Character,
        // SystemPrompt — legacy-фолбэк
        var personaCharacter = persona.Contract?.Character ?? persona.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(personaCharacter))
        {
            var character = personaCharacter.Trim();
            if (character.Length > 800) character = character[..800] + "…";
            sb.AppendLine($"Характер: {character}");
        }

        if (hasUserPrompt)
            sb.AppendLine($"\nЗапрос пользователя (главный ориентир — построй правило(а) под него): {userPrompt!.Trim()}");

        var existingRules = persona.AutomationRules ?? [];
        if (existingRules.Count > 0)
        {
            sb.AppendLine("\nУже есть правила (не дублируй их по смыслу):");
            foreach (var r in existingRules)
                sb.AppendLine($"- «{r.Name}» — {r.Trigger.Type}{(r.Enabled ? "" : " (выкл)")}");
        }
        else
        {
            sb.AppendLine("\nПравил ещё нет.");
        }

        sb.AppendLine("\nКаталог целей:");
        var projects = _projects.GetByOwner(UserId);
        if (projects.Count > 0)
        {
            sb.AppendLine("Проекты (для триггеров file/gitCommit/taskStatus, projectId = id):");
            foreach (var p in projects.Take(20)) sb.AppendLine($"- {p.Id} — {p.Name}");
        }
        var sources = _notes.GetSources(UserId);
        if (sources.Count > 0)
        {
            sb.AppendLine("Источники заметок (для триггера note, source = key):");
            foreach (var s in sources.Take(20)) sb.AppendLine($"- {s.Key} — {s.Label}");
        }
        sb.AppendLine($"Персона (для триггера mention — детект по @{persona.Handle}, доп. данных не нужно): handle = {persona.Handle}");

        sb.AppendLine("\nСхема triggerArgs по типам триггера (строго соблюдай ключи):");
        sb.AppendLine("- timer: {\"schedule\":{\"type\":\"daily|weekdays|weekly|interval\",\"time\":\"HH:mm\"," +
                      "\"weekdays\":[1..7, ISO пн=1],\"intervalMinutes\":число}}");
        sb.AppendLine("- file: {\"projectId\":\"id проекта\",\"glob\":\"**/*.ts\" (по умолчанию \"**/*\")," +
                      "\"kinds\":[\"created\",\"changed\"]}");
        sb.AppendLine("- note: {\"source\":\"personal|id источника\",\"tags\":[\"#тег\"] (опц.),\"section\":\"папка\" (опц.)}");
        sb.AppendLine("- gitCommit: {\"projectId\":\"id проекта\"}");
        sb.AppendLine("- taskStatus: {\"projectId\":\"id проекта\" (опц.),\"from\":\"Todo|InProgress|Done\" (опц.)," +
                      "\"to\":\"Todo|InProgress|Done\" (опц.)}");
        sb.AppendLine("- mention: {} (пусто)");

        sb.AppendLine("\nВерни ТОЛЬКО JSON-массив (без пояснений и markdown) из НЕ БОЛЕЕ 4 объектов:");
        sb.AppendLine("[{\"name\":\"короткое имя правила по-русски\",\"triggerType\":\"timer|file|note|gitCommit|taskStatus|mention\"," +
                      "\"triggerArgs\":{...по схеме своего типа...},\"conditionOnlyIf\":\"доп. условие реакции (опционально) или null\"," +
                      "\"actionWeight\":\"gate|work\",\"actionInstruction\":\"что делать персоне при срабатывании, 1-3 предложения по-русски\"," +
                      "\"rememberInHistory\":false}]");
        sb.AppendLine(hasUserPrompt
            ? "Построй правило(а) под запрос пользователя, опираясь на доступные типы триггеров и цели; " +
              "если запрос не укладывается ни в один триггер — верни []."
            : "Бери только правила, реально полезные роли и доступным проектам/источникам; если подходящих нет — верни [].");
        return sb.ToString();
    }

    // Парс JSON-массива из ответа модели (устойчиво к преамбуле/markdown-fence) — та же логика,
    // что ParseSuggestArray у bindings, но своя DTO-схема (generic-хелпер не выносим ради простоты)
    private static List<SuggestRuleRaw>? ParseSuggestRuleArray(string raw)
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
                    return System.Text.Json.JsonSerializer.Deserialize<List<SuggestRuleRaw>>(raw[start..(i + 1)],
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (System.Text.Json.JsonException) { return null; }
            }
        }
        return null;
    }

    private sealed record SuggestRuleRaw(string? Name, string? TriggerType, Dictionary<string, JsonElement>? TriggerArgs,
        string? ConditionOnlyIf, string? ActionWeight, string? ActionInstruction, bool? RememberInHistory);

    // Валидация triggerArgs кандидата по типу триггера — та же логика, что должна проходить
    // при ручном создании правила, но невалидный кандидат целиком отбрасывается (continue),
    // а не 400: подбор best-effort, часть предложений может не подойти.
    private bool ValidateTriggerArgs(AutomationTriggerType type, Dictionary<string, JsonElement>? args,
        out Dictionary<string, JsonElement>? normalized)
    {
        normalized = args;
        IReadOnlyDictionary<string, JsonElement> dict = args ?? new Dictionary<string, JsonElement>();
        switch (type)
        {
            case AutomationTriggerType.Timer:
                {
                    var sched = TimerTriggerSource.ParseSchedule(dict);
                    if (sched is null || sched.Type is not ("daily" or "weekdays" or "weekly" or "interval"))
                        return false;
                    return sched.Type == "interval" ? sched.IntervalMinutes is > 0 : sched.Time is not null;
                }
            case AutomationTriggerType.File:
            case AutomationTriggerType.GitCommit:
            case AutomationTriggerType.TaskStatus:
                {
                    // Пустой projectId допустим: File/GitCommit — режим «папка без проекта»
                    // (args.folder, глобальный агент; traversal-guard на рантайме в AutomationRootResolver),
                    // TaskStatus — «любой проект». Заданный projectId обязан принадлежать владельцу.
                    var projectId = dict.GetString("projectId");
                    if (string.IsNullOrWhiteSpace(projectId)) return true;
                    var project = _projects.GetById(projectId);
                    return project is not null && project.OwnerId == UserId;
                }
            case AutomationTriggerType.Note:
                {
                    var source = dict.GetString("source");
                    if (string.IsNullOrWhiteSpace(source)) return false;
                    if (source == "personal") return true;
                    return _notes.GetSources(UserId).Any(s => s.Key == source);
                }
            case AutomationTriggerType.Mention:
                return true;
            default:
                return false;
        }
    }

    // Маппинг DTO → модель правила. existing передаётся при обновлении — Id/CreatedAt и
    // null-поля (Args/Condition/Weight/…) наследуются от текущего правила.
    private static PersonaAutomationRule ParseRule(AutomationRuleRequest req, PersonaAutomationRule? existing = null)
    {
        var cond = new AutomationCondition
        {
            OnlyIf = req.ConditionOnlyIf,
            QuietFrom = req.QuietFrom,
            QuietTo = req.QuietTo,
            MinIntervalMinutes = req.MinIntervalMinutes,
        };
        return new PersonaAutomationRule
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString(),
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
            Enabled = req.Enabled ?? existing?.Enabled ?? true,
            Name = string.IsNullOrWhiteSpace(req.Name) ? (existing?.Name ?? "Правило") : req.Name.Trim(),
            Trigger = new AutomationTrigger
            {
                Type = req.TriggerType ?? existing?.Trigger.Type ?? AutomationTriggerType.Timer,
                Args = req.TriggerArgs ?? existing?.Trigger.Args,
            },
            Condition = cond.IsEmpty ? null : cond,
            Action = new AutomationAction
            {
                Weight = req.ActionWeight ?? existing?.Action.Weight ?? AutomationActionWeight.Gate,
                Instruction = req.ActionInstruction ?? existing?.Action.Instruction ?? "",
                RememberInHistory = req.RememberInHistory ?? existing?.Action.RememberInHistory ?? false,
                // -1 (не передано) — унаследовать текущее значение / дефолт 1440 при создании;
                // null — явный выбор «бессрочно»; N>0 — TTL в минутах
                ExpiresAfterMinutes = req.ActionExpiresAfterMinutes == -1
                    ? existing?.Action.ExpiresAfterMinutes ?? 1440
                    : req.ActionExpiresAfterMinutes,
            },
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // Каталог возможных целей привязки для пикера фронта: type = project | knowledge |
    // notes | tool | skill; для notes с ?source= — папки внутри источника; для узкого пикера
    // ProjectPersonas — personasInProject?source={projectId} (персоны команды проекта).
    [HttpGet("binding-targets")]
    public async Task<ActionResult> BindingTargets([FromQuery] string? type, [FromQuery] string? source,
        [FromQuery] string? personaId)
    {
        switch (type?.Trim().ToLowerInvariant())
        {
            case "project":
                return Ok(_projects.GetByOwner(UserId)
                    .Select(p => new { id = p.Id, label = p.Name, hint = p.RootPath, meta = (string?)null }));

            case "personasinproject" when !string.IsNullOrWhiteSpace(source):
                // Второй уровень пикера ProjectPersonas: команда конкретного (чужого) проекта —
                // сужение привязки до одной персоны вместо всей команды.
                return Ok(_personas.GetByOwner(UserId)
                    .Where(p => p.Scope == PersonaScope.Project && p.ProjectId == source)
                    .Select(p => new { id = p.Id, label = PersonaManager.PersonaLabel(p), hint = p.Description, meta = source }));

            case "knowledge":
                // Все базы знаний Dify, доступные пользователю (его проекты/заметки + датасеты
                // без префикса-владельца или с его префиксом); чужие пользователи скрыты.
                return Ok((await _bindings.KnowledgeTargetsAsync(UserId))
                    .Select(d => new
                    {
                        id = d.Id,
                        label = d.Label,
                        hint = d.ProjectId is null ? "База знаний" : "База знаний проекта",
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
                {
                    Persona? persona = null;
                    if (!string.IsNullOrWhiteSpace(personaId))
                    {
                        persona = _personas.Get(personaId, UserId);
                        if (persona is null) return NotFound();
                    }

                    // Каталог владельца: статические ключи плюс серверы его личного MCP-реестра
                    return Ok(_bindings.ToolCatalogFor(UserId)
                        .Select(kv =>
                        {
                            if (persona is null)
                                return (object)new { id = kv.Key, label = kv.Value.Label, hint = kv.Value.Hint, meta = (string?)null };
                            var (enabled, origin) = _bindings.GetToolDefaultState(UserId, persona, kv.Key);
                            return (object)new
                            {
                                id = kv.Key,
                                label = kv.Value.Label,
                                hint = kv.Value.Hint,
                                meta = (string?)null,
                                defaultEnabled = enabled,
                                defaultOrigin = origin,
                            };
                        }));
                }

            case "skill":
                return Ok(_skills.GetGlobalSkills()
                    .Select(s => new { id = s.Name, label = s.Name, hint = s.Description, meta = (string?)null }));

            default:
                return BadRequest(new { error = "Укажите type: project | knowledge | notes | tool | skill" });
        }
    }

    // Семантический поиск по привязанной базе знаний Dify (по id датасета). Зовётся
    // MCP-инструментом personas-server, когда персона по условию привязки решает
    // подгрузить знания. Датасет должен быть доступен владельцу (правило префикса).
    [HttpPost("knowledge-search")]
    public async Task<ActionResult> KnowledgeSearch([FromBody] KnowledgeSearchRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DatasetId) || string.IsNullOrWhiteSpace(req.Query))
            return BadRequest(new { error = "Нужны datasetId и query" });
        if (!_knowledge.IsConfigured)
            return BadRequest(new { error = "База знаний (Dify) не настроена" });
        // Только датасеты, доступные пользователю (его/общие; чужие скрыты)
        if ((await _bindings.KnowledgeTargetsAsync(UserId)).All(d => d.Id != req.DatasetId))
            return NotFound(new { error = "База знаний не найдена или недоступна" });

        // Доступные поля метаданных базы — для валидации фильтра и подсказки персоне
        IReadOnlyList<KnowledgeMetadataFieldInfo> fields;
        try { fields = await _knowledge.ListMetadataFieldsAsync(req.DatasetId); }
        catch { fields = []; }

        // Валидация фильтров: оператор из разрешённого набора + поле есть в базе.
        // Иначе Dify молча вернул бы 0 (по несуществующему полю) — вместо этого
        // честно говорим персоне, что не так и по каким полям можно фильтровать.
        List<KnowledgeMetadataFilter>? filters = null;
        if (req.Filters is { Count: > 0 })
        {
            filters = [];
            foreach (var f in req.Filters)
            {
                if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Operator))
                    return BadRequest(new { error = "У фильтра нужны name и operator" });
                if (!MetadataFilterOperators.Contains(f.Operator))
                    return BadRequest(new { error = $"Недопустимый оператор «{f.Operator}»", allowedOperators = MetadataFilterOperators });
                if (fields.All(x => !string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase)))
                    return BadRequest(new
                    {
                        error = $"В этой базе знаний нет поля метаданных «{f.Name}» — фильтровать по нему нельзя",
                        availableFields = fields.Select(x => new { x.Name, x.Type }),
                    });
                filters.Add(new KnowledgeMetadataFilter(f.Name, f.Operator, f.Value));
            }
        }

        var topK = req.TopK is > 0 and <= 20 ? req.TopK.Value : 6;
        var chunks = await _knowledge.RetrieveAsync(req.DatasetId, req.Query, topK, filters, req.Logic ?? "and");
        return Ok(new
        {
            // metadataFields — по каким полям можно фильтровать (имя+тип); может быть пусто
            metadataFields = fields.Select(x => new { x.Name, x.Type }),
            // metadata у выдержки — структурные поля документа (дата встречи, id, источник), если есть
            hits = chunks.Select(c => new { document = c.DocumentName, score = c.Score, content = c.Content, metadata = c.Metadata }),
        });
    }

    // Разрешённые операторы фильтра метаданных Dify (строковые поля; диапазоны дат не
    // поддерживаются — meeting_date хранится строкой, только contains/start with и т.п.)
    private static readonly HashSet<string> MetadataFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "contains", "not contains", "start with", "end with", "is", "is not", "empty", "not empty",
    };

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
            // Свободный текст (1-2 предложения) — JSON-режим здесь не нужен
            var text = await _cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaAiCondition,
                sb.ToString(), model, UserId, ct: HttpContext.RequestAborted);
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
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();

        try
        {
            var candidates = await _crud.SuggestBindingsAsync(UserId, persona, ct: HttpContext.RequestAborted);
            return Ok(new { candidates });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "suggest bindings для персоны {Persona}", id);
            return StatusCode(502, new { error = $"Не удалось подобрать привязки: {ex.Message}" });
        }
    }

    // Генерация привязок под свободный запрос пользователя: тот же конвейер, что и подбор
    // под роль, но главный ориентир — текст пользователя. Возвращает кандидатов, НЕ сохраняет.
    [HttpPost("{id}/bindings/generate")]
    public async Task<ActionResult> GenerateBindings(string id, [FromBody] GenerateBindingsRequest req)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return BadRequest(new { error = "Опишите, какая привязка нужна персоне" });

        try
        {
            var candidates = await _crud.SuggestBindingsAsync(UserId, persona, req.Prompt.Trim(),
                ct: HttpContext.RequestAborted);
            return Ok(new { candidates });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "generate bindings для персоны {Persona}", id);
            return StatusCode(502, new { error = $"Не удалось создать привязку: {ex.Message}" });
        }
    }

    // Применить типовые умения специальности к существующей персоне (кнопка «Применить
    // типовые»): материализует профиль роли в личные привязки поверх текущих — вручную
    // настроенное не трогается, дубликаты и недоступные цели пропускаются. Ответ несёт
    // число ДОБАВЛЕННЫХ умений; внутренний сбой (AI-подбор целей, каталог знаний) —
    // это 502, а не applied=0: у кнопки есть ветка «Не удалось применить — попробуйте
    // позже», и без ошибки она недостижима (сбой до записи ничего не меняет).
    [HttpPost("{id}/bindings/apply-defaults")]
    public async Task<ActionResult> ApplyDefaultBindings(string id)
    {
        var persona = _personas.Get(id, UserId);
        if (persona is null) return NotFound();
        if (persona.Specialty == PersonaSpecialty.None)
            return BadRequest(new { error = "У персоны не задана специальность — типовых умений роли нет" });
        try
        {
            var (updated, applied) = await _crud.MaterializeDefaultBindingsAsync(UserId, persona,
                HttpContext.RequestAborted);
            return Ok(new { persona = updated, applied });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "типовые умения: применение для персоны {Persona} не удалось", id);
            return StatusCode(502, new { error = $"Не удалось применить типовые умения: {ex.Message}" });
        }
    }

    // Разбор DTO привязки — общая точка PersonasCrudService.ParseBinding (internal):
    // дословные копии здесь и в сервисе расходились бы молча (приёмка волны 2.1, пункт A)

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

    // Recall-блок памяти (рабочий фокус + скоринг relevance × recency × type × salience +
    // командная память проектной персоны) — тот же BuildRecallAsync, что подмешивается в
    // системный промпт персонной сессии. Дёргается memory_recall из MCP memory-server:
    // файловый сабагент получает память того же качества, что собеседник чата.
    [HttpGet("{id}/memory/recall")]
    public async Task<ActionResult> MemoryRecall(string id, [FromQuery] string q, [FromQuery] int topK = 5)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Пустой запрос");
        var minScore = double.TryParse(_config["Persona:RecallMinScore"],
            System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0.30;
        var recall = await _memory.BuildRecallAsync(UserId, id, q, Math.Clamp(topK, 1, 20), minScore);
        return Ok(new { text = recall?.Text });
    }

    // Запомнить (явный write-path); salience — важность 0..1 (опционально)
    [HttpPost("{id}/memory")]
    public async Task<ActionResult<PersonaMemoryEntry>> Remember(string id, [FromBody] RememberRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Пустой текст");
        if (!Enum.TryParse<PersonaMemoryType>(req.Type, true, out var type)) type = PersonaMemoryType.Semantic;
        // Семантический write-path: близкий факт усилит существующую запись, а не создаст дубль
        var entry = await _memory.RememberAsync(UserId, id, type, req.Text, req.Tags, req.SourceSessionId, req.Salience);
        if (entry is null) return NotFound();
        _memory.EnforceCap(UserId, id);   // потолок и для явного write-path
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

    // Отредактировать текст записи (UI-редактирование)
    [HttpPut("{id}/memory/{entryId}")]
    public async Task<ActionResult<PersonaMemoryEntry>> UpdateMemory(string id, string entryId, [FromBody] UpdateMemoryRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest("Пустой текст");
        var entry = _memory.Update(UserId, id, entryId, req.Text);
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

    // Подтвердить предложенную autolearn запись (③-3.2) — снимает pending, попадает в recall
    [HttpPost("{id}/memory/{entryId}/confirm")]
    public async Task<IActionResult> ConfirmMemory(string id, string entryId)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (!_memory.Confirm(UserId, id, entryId)) return NotFound();
        await Broadcast("memory", id);
        return NoContent();
    }

    // Превратить запись памяти в заметку (③-3.3): инсайт выходит из личного датасета
    // персоны в общий vault — виден/доступен всей команде и вне чата с персоной.
    // Склейка (заголовок/подпись) — в PersonaMemoryService.MemoryToNote, единая точка с
    // MCP-инструментом memory_to_note.
    [HttpPost("{id}/memory/{entryId}/to-note")]
    public IActionResult MemoryToNote(string id, string entryId)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        return _memory.MemoryToNote(UserId, id, entryId) is { } note
            ? Ok(new { noteId = note.NoteId, noteTitle = note.NoteTitle })
            : NotFound("Запись памяти не найдена");
    }

    // Закрепить заметку в памяти персоны (③-3.3): важное подчёркивается, попадает в recall
    // с высоким salience (1.0) как semantic-факт.
    [HttpPost("{id}/memory/from-note")]
    public async Task<IActionResult> NoteToMemory(string id, [FromBody] NoteToMemoryRequest req)
    {
        if (_personas.Get(id, UserId) is null) return NotFound();
        if (!await _memory.NoteToMemoryAsync(UserId, id, req.NoteId)) return NotFound("Заметка не найдена");
        await Broadcast("memory", id);
        return Ok();
    }

    // --- @упоминания: спросить персону (persona_ask из MCP personas-server) ---

    // One-shot ответ персоны от своего лица (PersonaAskService): слой персоны + recall
    // долгой памяти + вопрос; модель — модель персоны. PersonaId — однозначный путь (обходит
    // резолв по handle); без него handle резолвится в контексте + кросс-проектных extra-скоупах
    // (ProjectPersonas) — коллизия (тёзки в разных проектах) → 409 со списком кандидатов.
    [HttpPost("ask")]
    // Анти-рекурсия: с делегированного хода персону не переспрашивают (раньше — снятием
    // persona_ask из состава инструментов, что перезапускало процесс CLI со всеми MCP)
    [DenyOnDelegatedTurn("Вопрос другой персоне")]
    public async Task<ActionResult> Ask([FromBody] PersonaAskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest(new { error = "Пустой вопрос" });

        var projectId = string.IsNullOrWhiteSpace(req.ProjectId) ? null : req.ProjectId;
        Persona? persona;
        if (!string.IsNullOrWhiteSpace(req.PersonaId))
        {
            // Тот же пул достижимости, что и у резолва по handle (AccessiblePool) — personaId
            // не должен быть лазейкой мимо кросс-проектных привязок в любую чужую персону
            persona = _personas.GetReachable(UserId, req.PersonaId, projectId, req.ExtraProjectIds, req.ExtraPersonaIds);
            if (persona is null) return NotFound(new { error = "Персона не найдена или недоступна в этом контексте" });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(req.Handle)) return BadRequest(new { error = "Не указан handle персоны" });
            var handle = req.Handle.Trim().TrimStart('@');
            var candidates = _personas.ResolveHandleCandidates(UserId, handle, projectId,
                req.ExtraProjectIds, req.ExtraPersonaIds);
            if (candidates.Count == 0) return NotFound(new { error = $"Персона @{handle} не найдена" });
            if (candidates.Count > 1)
                return Conflict(new
                {
                    error = $"Персона @{handle} есть в нескольких проектах — уточни personaId",
                    candidates = candidates.Select(p => new
                    {
                        id = p.Id,
                        name = p.Name,
                        role = p.Role,
                        projectId = p.ProjectId,
                        projectName = p.ProjectId is null ? null : _projects.GetById(p.ProjectId)?.Name,
                    }),
                });
            persona = candidates[0];
        }

        try
        {
            var answer = await _ask.AskAsync(UserId, persona, req.Question, req.Context,
                HttpContext.RequestAborted);
            return Ok(new { handle = persona.Handle, name = persona.Name, role = persona.Role, answer });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "persona_ask: one-shot ответа @{Handle} не удался", persona.Handle);
            return StatusCode(502, new { error = $"Не удалось получить ответ персоны: {ex.Message}" });
        }
    }

    // Разбор CSV-параметра запроса (extraProjectIds/extraPersonaIds) в список id
    private static List<string> SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // Парс профиля доступа и валидация ячейки матрицы уровней — общие точки
    // PersonasCrudService.TryParseAccess / IsValidTierCell / ValidProject (internal):
    // бывшие приватные копии оставались мёртвыми дублями (приёмка волны 2.1, пункт A)
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
    List<string>? DisallowedTools = null,
    // Специальность персоны (функциональная роль для оркестрации); null/None — не задана
    PersonaSpecialty? Specialty = null,
    // Явные привязки при создании (валидируются до создания персоны)
    List<PersonaBindingRequest>? Bindings = null,
    // true — после создания подобрать привязки AI (за флагом persona-bindings, best-effort)
    bool? AutoBindings = null,
    // Доступ ко всем проектам владельца (текущим и будущим) — только для Scope.Global
    bool? AllProjectsAccess = null,
    // true — после создания сгенерировать фото-аватар (best-effort, требует Fal:ApiKey).
    // Опт-ин для авто/LLM-путей создания (напр. пакетная команда из ai/team) — обычное
    // создание через форму/мастер не шлёт этот параметр, там инициалы или явный выбор
    bool? AutoAvatar = null,
    // Описание внешности для фотопортрета (англ.); пусто — берём из роли/описания персоны
    string? AvatarPrompt = null,
    // Ручной @handle (latin-slug); пусто — авто-генерация из имени. Занят/невалиден → 400
    string? Handle = null,
    // Уровень модели («strong|medium|weak») вместо конкретной Model; null/"" — не задан
    string? ModelTier = null,
    // Свои модели по уровням (ADR-007 §2): id модели ИЛИ "preset:{id}"; null/"" — не задана
    string? TierStrong = null,
    string? TierMedium = null,
    string? TierWeak = null);

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
    List<string>? DisallowedTools = null,
    // Специальность персоны (функциональная роль); null — не менять, None — сбросить
    PersonaSpecialty? Specialty = null,
    // Доступ ко всем проектам владельца (текущим и будущим); null — не менять.
    // Игнорируется (сбрасывается в false), если персона не Scope.Global
    bool? AllProjectsAccess = null,
    // Ручной @handle (latin-slug); null — не менять, "" — сбросить к авто-генерации.
    // Занят/невалиден → 400
    string? Handle = null,
    // Уровень модели: null — не менять, "" — сбросить, "strong|medium|weak" — задать
    string? ModelTier = null,
    // Свои модели по уровням (ADR-007 §2): null — не менять, "" — сбросить, иначе id/preset:{id}
    string? TierStrong = null,
    string? TierMedium = null,
    string? TierWeak = null);

public record CreatePersonaChatRequest(string Mode = "auto", string? ResumeSessionId = null, string? Name = null,
    string? ProjectId = null);

public record ConnectPantheonRequest(List<string>? Keys = null);
public record MatchPersonaRequest(string? Task = null, string? ProjectId = null, string? RequiredTool = null);

// Правило автоматизации персоны (CRUD /automation). TriggerArgs — гибкий JSON-мешок
// (ключи зависят от TriggerType, см. комментарий к AutomationTrigger).
public record AutomationRuleRequest(
    bool? Enabled,
    string? Name,
    AutomationTriggerType? TriggerType,
    Dictionary<string, JsonElement>? TriggerArgs,
    string? ConditionOnlyIf,
    string? QuietFrom,
    string? QuietTo,
    int? MinIntervalMinutes,
    AutomationActionWeight? ActionWeight,
    string? ActionInstruction,
    bool? RememberInHistory,
    // Время жизни чата правила: -1 (сентинел, не передано) — сохранить текущее при
    // обновлении / дефолт 1440 при создании; null — бессрочно; N>0 — TTL в минутах.
    int? ActionExpiresAfterMinutes = -1);

public record AutomationRulesSetRequest(List<AutomationRuleRequest>? Rules);

// Свободный запрос пользователя для генерации правил автоматизации (POST {id}/automation/generate)
public record GenerateAutomationRequest(string? Prompt);

public record RememberRequest(string Type, string Text, List<string>? Tags = null,
    string? SourceSessionId = null, double? Salience = null);

// Закрепить заметку в памяти персоны (③-3.3)
public record NoteToMemoryRequest(string NoteId);

public record UpdateMemoryRequest(string Text);

public record GenerateAvatarRequest(string? Prompt = null, int? Count = null);

public record AiCharacterRequest(string? Name, string? Role, string? Description, string? Current, string? Instruction);

// Подбор голоса: всё нужное приходит в теле, потому что персона может быть ещё не сохранена
public record AiVoiceRequest(string? Name, string? Role, string? Description, string? Character, string? Tone);

// AutoBindings: null/true — подобрать привязки AI после создания (за флагом persona-bindings),
// false — не подбирать.
public record AiQuickCreateRequest(string Prompt, PersonaScope? Scope = null, string? ProjectId = null,
    bool? AutoBindings = null);

// AI-формирование команды: промпт + проект → LLM предлагает состав (черновики, без создания)
public record AiTeamRequest(string ProjectId, string Prompt);
public record TeamMemberDraft(string? Name, string? Role, string? Description, string? Character,
    string? Tone, string? Specialty, string? Color, string? Greeting, string? AvatarPrompt);

// DTO привязки персоны: type/mode — строками (project|projectPath|knowledge|notes|tool|skill;
// auto|always|off), парсятся без учёта регистра.
public record PersonaBindingRequest(string Type, string Target, string? Path = null,
    string? Condition = null, string? Mode = null);

public record PersonaBindingsSetRequest(List<PersonaBindingRequest>? Bindings);

// Свободный запрос пользователя для генерации привязок (POST {id}/bindings/generate)
public record GenerateBindingsRequest(string? Prompt);

public record AiConditionRequest(string Type, string Target, string? Path = null);

public record KnowledgeSearchRequest(string DatasetId, string Query, int? TopK = null,
    List<KnowledgeSearchFilter>? Filters = null, string? Logic = null);

// Условие фильтра по метаданным от MCP-инструмента (operator — строковый оператор Dify)
public record KnowledgeSearchFilter(string Name, string Operator, string? Value = null);

public record SelectAvatarRequest(string File);

public record PersonaAskRequest(string? Handle, string Question, string? Context = null, string? ProjectId = null,
    // Однозначный путь резолва — обходит поиск по handle (и его возможную коллизию)
    string? PersonaId = null,
    // Кросс-проектные extra-скоупы вызывающей персоны (ProjectPersonas-привязки) — расширяют
    // резолв handle за пределы контекста ProjectId; заполняет MCP personas-server
    List<string>? ExtraProjectIds = null, List<string>? ExtraPersonaIds = null);
