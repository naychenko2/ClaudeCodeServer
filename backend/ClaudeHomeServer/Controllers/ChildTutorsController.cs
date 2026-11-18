using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Админские ручки репетитора профиля ребёнка (Т2c, ADR-041 редакция 2 §12 и редакция 3 §22,
/// разрез «Репетиторы» §5 / «Знания» §5): повторный провижн репетитора предмета и правка
/// характера репетитора со списками «Обязательно / Никогда», аватар репетитора —
/// generate/upload/select (Т2c2, путь И5 №12) и его чтение — аватар и кандидат
/// (Т2c3, блокер Т4e: GET /api/personas/{id}/avatar* ищут персону по арендатору,
/// персона ребёнка админу там не видна — 404). Все ручки только для админа:
/// [Authorize(Roles="admin")] по claim + AdminByStore по стору (тот же образец, что у
/// PUT child-profile в UsersController.cs:173: сервисный токен хода и снятое админство сюда
/// не проходят), без [ChildAllowed]: ребёнок своего репетитора не правит (И5).
/// Аватарные ручки делегируют PersonasCrudService (тот же код, что у аватаров персон), но
/// владелец расхода — вызывающий админ, не владелец персоны (ребёнок): бюджет ребёнка
/// аватар репетитора не съедает никогда (инвариант И5). Читающие ручки повторяют чтение
/// PersonasController (тот же PersonaAvatarThumbService для миниатюр, та же сетка
/// кэшей), ключ персоны — ребёнок: новой генерации они не делают никогда.
///
/// Модель и маршрут здесь нет: они живут только в записи ChildSubject (редакция 2 §12),
/// Persona.Model репетитора пуст — поле model в теле игнорируется. Прочие слоты контракта
/// (tone, outputFormat, speechExamples, instructions) тоже игнорируются: они стоят в слое
/// выше child-safety-оговорки, которую всё равно перебивает PersonaOverride (разрез §5).
/// Правка идёт через PersonasCrudService — тот же код, что у PUT персоны и её событие
/// «updated»; частичная семантика: null-поле тела не меняет значение.
/// </summary>
[ApiController]
[Authorize(Roles = "admin")]
[Route("api/users/{id}")]
public class ChildTutorsController(
    UserStore users,
    PersonaManager personas,
    ChildTutorProvisioner childTutorProvisioner,
    PersonasCrudService crud,
    ISessionBroadcaster broadcaster,
    PersonaAvatarThumbService avatarThumbs,
    ILogger<ChildTutorsController> logger) : ControllerBase
{
    // Пределы списков «Обязательно / Никогда» (ADR-041 редакция 3 §22): до 12 пунктов по 300 символов.
    private const int MaxRuleCount = 12;
    private const int MaxRuleLength = 300;

    // Замок PUT по ключу персоны (static — не по экземпляру контроллера: на один запрос
    // создаётся новый контроллер, а держать нужно пару запросов): см. PutTutor.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PutTutorGates = new();

    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>
    /// Провижн репетитора предмета (разрез §5, строка POST child-subjects/{subjectId}/tutor):
    /// идемпотентен — живой ключ возвращает тот же id без создания; это доводка редактора для
    /// предмета, провитого не полностью. null от провижнера — ровно «пользователя/профиля/
    /// предмета нет или запрос отменён» → 404 subject_not_found; исключение стора → 500
    /// {code,message}, профиль не тронут (откатом здесь управлять нечем — ничего не записывалось).
    /// </summary>
    [HttpPost("child-subjects/{subjectId}/tutor")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> EnsureSubjectTutor(string id, string subjectId)
    {
        if (users.GetById(id) is null)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });

        string? tutorId;
        try
        {
            tutorId = await childTutorProvisioner.EnsureSubjectTutorAsync(id, subjectId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: провижн репетитора предмета {Subject} не удался для {User}", subjectId, id);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать персону-репетитора предмета." });
        }

        // Ребёнок без профиля — тоже «предмета нет»: ни одного предмета в нём нет.
        if (tutorId is null)
            return NotFound(new { code = "subject_not_found", message = "Предмет не найден в профиле ребёнка." });

        return Ok(new { personaId = tutorId });
    }

    /// <summary>
    /// Правка характера репетитора (ADR-041 редакция 2 §12, редакция 3 §22): name, character,
    /// greeting, voice, gender и списки mustDo/mustNot — через PersonasCrudService, как PUT
    /// персоны. Персона обязана быть репетитором ЭТОГО ребёнка (ключ профиля или любого его
    /// предмета) — иначе 404 tutor_not_found; висящий ключ (персона удалена) — тот же 404,
    /// чужая персона — тоже: существование чужой персоны не раскрываем. Тело проверяется до
    /// первой записи: отказанный вызов оставляет репетитора нетронутым.
    /// </summary>
    [HttpPut("child-tutors/{personaId}")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> PutTutor(string id, string personaId, [FromBody] ChildTutorPatch req)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is null)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });

        // Пустые строки отбрасываются до счёта: пустой пункт — не пункт (разрез §5).
        var mustDo = CleanRules(req.MustDo);
        var mustNot = CleanRules(req.MustNot);
        if (RulesTooLong(mustDo) || RulesTooLong(mustNot))
            return BadRequest(new { code = "rules_too_long",
                message = $"Правил «Обязательно / Никогда» — не больше {MaxRuleCount}, каждое — не длиннее {MaxRuleLength} символов." });
        // Голос проверяется до первой записи той же VoiceError, что у PUT /{id}/voice
        // (PersonasCrudService.cs:1403): отказанный голос не должен оставить половину тела
        // применённой — дальше SetVoiceAsync отказаться уже нечем.
        if (PersonasCrudService.VoiceError(req.Voice) is { } voiceError)
            return BadRequest(new { code = "voice_invalid", message = voiceError });

        // Контракт — частичный патч только по трём слотам: MergeContract (PersonaManager.cs:449)
        // не трогает остальные; null-слоты — «не менять», пустой список — явная очистка.
        var contract = req.Character is null && mustDo is null && mustNot is null
            ? null
            : new PersonaContract { Character = req.Character, MustDo = mustDo, MustNot = mustNot };

        // Замок по ключу персоны (минор ревью Т2c): «обновление + голос» — две мутации одного
        // запроса, две отдельные записи стора; без замка два параллельных PUT одной персоны
        // перемешиваются: имя от одного, голос от другого. Ключ — personaId, не экземпляр
        // контроллера: разные персоны не ждут друг друга (тот же образец ключа, что у
        // ProjectMapApplyService.cs:95-99). Отпущен в finally — отказ любой ветви не оставляет
        // замок взятым.
        var gate = PutTutorGates.GetOrAdd(personaId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            ActionResult<Persona> updated;
            try
            {
                updated = await crud.UpdateAsync(id, personaId,
                    new UpdatePersonaRequest(
                        Name: req.Name, Role: null, Description: null, SystemPrompt: null,
                        // Model: null — и поле model тела игнорируется: маршрут репетитора живёт
                        // в записи предмета, Persona.Model остаётся пустым (редакция 2 §12).
                        Model: null, Effort: null, Scope: null, ProjectId: null, Color: null,
                        Greeting: req.Greeting, MemoryEnabled: null,
                        Contract: contract,
                        Gender: req.Gender),
                    callerSessionId: null);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Детский режим: правка репетитора {Persona} не удалась (ребёнок {Owner})", personaId, id);
                return StatusCode(500, new { code = "tutor_update_failed", message = "Не удалось изменить персону-репетитора." });
            }
            // У успеха UpdateAsync Value пуст: его Ok(persona) (PersonasCrudService.cs:409, хелпер
            // Ok — OkObjectResult, :60) конвертируется в ActionResult<Persona> через Result
            // (ActionResultOfT.cs), Value остаётся null. Проверка по Value считала бы успех
            // отказом, и голос в теле не применялся бы никогда (минор ревью Т2c). Успех —
            // OkObjectResult, persona — его значение; отказ сервиса пробрасывается как есть
            // (та же форма, что у PUT персоны): достижим здесь только потолок размера контракта.
            if (updated.Result is not OkObjectResult ok || ok.Value is not Persona persona)
                return updated.Result ?? new StatusCodeResult(500);

            // Голос — отдельная мутация того же сервиса (как PUT /{id}/voice): после валидации
            // отказа уже не будет; событие «updated» приходит по её итогу — с финальным состоянием.
            if (req.Voice is not null)
            {
                try
                {
                    await crud.SetVoiceAsync(id, personaId, req.Voice);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Детский режим: голос репетитора {Persona} не изменён (ребёнок {Owner})", personaId, id);
                    return StatusCode(500, new { code = "tutor_update_failed", message = "Не удалось изменить голос репетитора." });
                }
            }

            return Ok(persona);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Генерация аватара репетитора (Т2c2, путь И5 №12): тот же сервис, что у аватара персоны
    /// (PersonasCrudService.GenerateAvatarAsync) — кандидаты в папку ассетов репетитора, ключ
    /// чтения и папки — ребёнок (владелец персоны). Отличие — атрибуция расхода: владелец
    /// расхода и актор — вызывающий админ, не ребёнок (инвариант И5: бюджет ребёнка аватар
    /// репетитора не съедает никогда). Фоновый догон для такой заявки не ставится: очередь
    /// оплачивается по OwnerId сущности и другого владельца расхода не называет (PersonasCrudService).
    /// Отказ генерации — 502/503 {code,message} (ADR-014 форма 3: «чем генерировать нет» —
    /// 503, а не 400: клиент не сделал ничего неверного), исключение сервиса наружу не идёт.
    /// </summary>
    [HttpPost("child-tutors/{personaId}/avatar/generate")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    [SubsystemUsage("images")]
    public async Task<IActionResult> GenerateTutorAvatar(string id, string personaId,
        [FromBody] GenerateAvatarRequest? req)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is null)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });

        ActionResult result;
        try
        {
            // Ключ персоны — ребёнок, владелец расхода и актор — админ (И5): трату роутер
            // пишет на названного, догон при названном владельце не ставится.
            result = await crud.GenerateAvatarAsync(id, personaId, req ?? new(),
                SpendInitiators.Human, actorUserId: UserId, spendOwnerId: UserId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: генерация аватара репетитора {Persona} не удалась (ребёнок {Owner})", personaId, id);
            return StatusCode(500, new { code = "avatar_generation_failed", message = "Не удалось сгенерировать аватар репетитора." });
        }

        // Успех сервиса приходит как OkObjectResult (Ok() — PersonasCrudService.cs:794); отказ
        // формы {error,queued} переводится в {code,message} этой ручки по фактическим ветвям
        // GenerateAvatarAsync: 404 «персоны нет» (:734) → tutor_not_found; 503 «вертикаль
        // выключена» (:727) и 400 «нет провайдера для места» (:762) → 503 image_generation_off
        // (отсутствует не запрос, а возможность генерировать — ADR-014 форма 3); 502 «провайдер
        // не отдал картинок» (:778) → 502 avatar_generation_failed.
        if (result is OkObjectResult ok) return Ok(ok.Value);
        if (result is NotFoundResult or NotFoundObjectResult)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });
        return (result as ObjectResult)?.StatusCode == 502
            ? StatusCode(502, new { code = "avatar_generation_failed", message = "Не удалось сгенерировать изображение." })
            : StatusCode(503, new { code = "image_generation_off", message = "Генерация изображений не настроена: ни один провайдер не подключён." });
    }

    /// <summary>
    /// Загрузка аватара репетитора (Т2c2, путь И5 №12): та же валидация (magic bytes + белый
    /// список) и те же файлы, что у загрузки аватара персоны (PersonasController): оригинал +
    /// кропнутый квадрат + параметры кропа. Строки расхода здесь нет: генерации нет, человек
    /// принёс своё. Событие «updated» — владельцу персоны, как у выбора кандидата в сервисе.
    /// </summary>
    [HttpPost("child-tutors/{personaId}/avatar/upload")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    [RequestSizeLimit(15_000_000)]
    public async Task<IActionResult> UploadTutorAvatar(string id, string personaId,
        [FromForm] IFormFile? original, [FromForm] IFormFile? cropped, [FromForm] string? crop)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is null)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });
        if (original is null || cropped is null)
            return BadRequest(new { code = "avatar_files_missing", message = "Нужны файлы original и cropped." });

        var originalCheck = await ImageAssetHelper.ValidateImageAsync(original);
        if (originalCheck.Error is not null) return BadRequest(new { code = "image_invalid", message = originalCheck.Error });
        var croppedCheck = await ImageAssetHelper.ValidateImageAsync(cropped);
        if (croppedCheck.Error is not null) return BadRequest(new { code = "image_invalid", message = croppedCheck.Error });

        var cropState = ImageAssetHelper.ParseCrop(crop);

        var dir = SafePath.Join(personas.AssetsDir, personaId);
        Directory.CreateDirectory(dir);
        var originalName = $"original-{Guid.NewGuid():N}{originalCheck.Ext}";
        var imageName = $"avatar-{Guid.NewGuid():N}{croppedCheck.Ext}";
        await ImageAssetHelper.SaveFormFileAsync(original, SafePath.Join(dir, originalName));
        await ImageAssetHelper.SaveFormFileAsync(cropped, SafePath.Join(dir, imageName));

        var updated = personas.SetAvatarUploaded(personaId, id, imageName, originalName, cropState);
        await broadcaster.ToOwner(id, new PersonasChangedMessage("updated", personaId));
        return Ok(updated);
    }

    /// <summary>
    /// Выбор кандидата как аватар репетитора (Т2c2, путь И5 №12): тот же сервис, что у выбора
    /// аватара персоны (PersonasCrudService.SelectAvatarAsync), ключ — ребёнок. Строки расхода
    /// нет: выбор сохранённого кандидата — не генерация. Два одновременных выбора — последний
    /// выигрывает: стор персон пишет запись атомарно (PersonaManager.Update). Отказ сервиса
    /// переводится в {code,message} этой ручки (400 avatar_file_missing, 404
    /// avatar_candidate_not_found) — та же форма, что у generate/upload, а не сервисный {error}.
    /// </summary>
    [HttpPost("child-tutors/{personaId}/avatar/select")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> SelectTutorAvatar(string id, string personaId, [FromBody] SelectAvatarRequest req)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is null)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });

        ActionResult<Persona> result;
        try
        {
            result = await crud.SelectAvatarAsync(id, personaId, req);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Детский режим: выбор аватара репетитора {Persona} не удался (ребёнок {Owner})", personaId, id);
            return StatusCode(500, new { code = "avatar_select_failed", message = "Не удалось выбрать аватар репетитора." });
        }
        // У успеха Value пуст (Ok() — PersonasCrudService.cs:818, та же конверсия, что у PutTutor):
        // персона — значение исходного OkObjectResult. Отказ сервисной формы {error} в эту
        // ручку фронту не показывается (read rejectText по body.message, ChildTutorCard.tsx:70-74),
        // он переводится в {code,message}, как у generate/upload: пустой файл тела — 400
        // avatar_file_missing (:802), нет файла кандидата на диске — 404 avatar_candidate_not_found (:806).
        if (result.Result is OkObjectResult ok) return Ok(ok.Value);
        return (result.Result as ObjectResult)?.StatusCode switch
        {
            400 => BadRequest(new { code = "avatar_file_missing", message = "Файл кандидата не указан." }),
            404 => NotFound(new { code = "avatar_candidate_not_found", message = "Кандидат не найден." }),
            _ => StatusCode(500, new { code = "avatar_select_failed", message = "Не удалось выбрать аватар репетитора." }),
        };
    }

    /// <summary>
    /// Отдать аватар репетитора (Т2c3, блокер Т4e): персона ребёнка админу в
    /// GET /api/personas/{id}/avatar не видна (та ищет по арендатору) — админка читает
    /// его этой ручкой. Тот же код чтения, что у PersonasController.Avatar (:651):
    /// миниатюра ближайшей ступени по size, оригинал без него; JWT принимается и в
    /// query access_token (браузерный &lt;img&gt;). Миниатюры — тот же PersonaAvatarThumbService,
    /// новой генерации нет. Персона ищется с владельцем = ребёнок; чужая, не репетитор
    /// этого ребёнка или без файла — один 404 (существование персоны не раскрываем).
    /// </summary>
    [HttpGet("child-tutors/{personaId}/avatar")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> GetTutorAvatar(string id, string personaId, [FromQuery] int? size = null)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is not { } persona)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });

        // Нет аватара-картинки — тот же пустой 404, что у персоны (PersonasController.cs:672):
        // «нет файла» здесь не отличается от «нет персоны».
        if (persona.Avatar.Kind != PersonaAvatarKind.Image || string.IsNullOrEmpty(persona.Avatar.ImageFile))
            return NotFound();

        var full = SafePath.Join(personas.AssetsDir, Path.Combine(personaId, persona.Avatar.ImageFile));
        if (!System.IO.File.Exists(full)) return NotFound();

        var step = ImageAssetHelper.AvatarThumbStep(size);
        if (step is null)
        {
            // Оригинал: имя файла в URL меняется при смене аватара — кэш долгий и immutable
            // (тот же приём, что у персоны)
            Response.Headers.CacheControl = "private, max-age=604800, immutable";
            return ImageAssetHelper.PhysicalFileByExt(full);
        }

        var thumb = await avatarThumbs.GetOrGenerateAsync(persona, full, step.Value);
        if (thumb is null)
        {
            // Фолбэк на оригинал БЕЗ долгого кэша (та же причина, что у персоны: миниатюра
            // не получилась — следующий запрос должен её попробовать, а не неделю держать
            // оригинал на URL с size)
            Response.Headers.CacheControl = "no-cache";
            return ImageAssetHelper.PhysicalFileByExt(full);
        }

        // Миниатюра: имя стабильно до смены аватара — кэш долгий и immutable
        Response.Headers.CacheControl = "private, max-age=604800, immutable";
        return ImageAssetHelper.PhysicalFileByExt(thumb);
    }

    /// <summary>
    /// Отдать превью кандидата аватара репетитора (Т2c3): галерея выбора на админке
    /// репетитора. Тот же код, что у PersonasController.AvatarCandidate (:625): имя файла
    /// — Path.GetFileName + SafePath.Join (защита от path-traversal), файлы живут в
    /// {personaId}/candidates/. Чужая/не репетитор/нет файла — тот же 404.
    /// </summary>
    [HttpGet("child-tutors/{personaId}/avatar/candidate/{file}")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public IActionResult GetTutorAvatarCandidate(string id, string personaId, string file)
    {
        if (users.GetById(id) is not { } child)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        if (child.ChildMode is not { } profile
            || !IsChildTutor(profile, personaId)
            || personas.Get(personaId, id) is null)
            return NotFound(new { code = "tutor_not_found", message = "Эта персона — не репетитор этого ребёнка." });

        var safe = Path.GetFileName(file);   // защита от path-traversal
        var full = SafePath.Join(personas.AssetsDir, Path.Combine(personaId, "candidates", safe));
        if (!System.IO.File.Exists(full)) return NotFound();
        return ImageAssetHelper.PhysicalFileByExt(full);
    }

    // Тот же признак «репетитор ребёнка», что у аватар-ручки (PersonasController.cs:709):
    // закреплённая персона профиля или репетитор ЛЮБОГО его предмета, включая выключенный:
    // выключенный предмет скрыт с главной, но его репетитор остаётся репетитором профиля.
    // Сравнение id — Ordinal: это GUID.
    private static bool IsChildTutor(ChildProfile profile, string personaId) =>
        string.Equals(profile.TutorPersonaId, personaId, StringComparison.Ordinal)
        || (profile.Subjects ?? []).Any(s => s is not null
            && string.Equals(s.TutorPersonaId, personaId, StringComparison.Ordinal));

    // Пустые строки — не пункты: они отбрасываются до счёта предела (разрез §5).
    private static List<string>? CleanRules(List<string>? rules) =>
        rules is null ? null : rules.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

    // Предел считается по отчищенному списку: >12 пунктов или пункт длиннее 300 символов.
    private static bool RulesTooLong(List<string>? rules) =>
        rules is { } list && (list.Count > MaxRuleCount || list.Any(r => r.Length > MaxRuleLength));
}

/// <summary>
/// Тело PUT child-tutors (разрез «Знания» §5, ChildTutorPatch): null — поле не трогать;
/// mustDo/mustNot: [] — очистить, пустые строки отбрасываются. Прочие поля тела
/// (tone, model, outputFormat…) игнорируются: их нет в контракте ручки (разрез §5).
/// </summary>
public record ChildTutorPatch(
    string? Name,
    string? Character,
    string? Greeting,
    PersonaVoice? Voice,
    PersonaGender? Gender,
    List<string>? MustDo = null,
    List<string>? MustNot = null);
