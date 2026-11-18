using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Route("api/users")]
[Authorize(Roles = "admin")]
// knowledge — необязательная зависимость от вертикали «Знаний» (К1b): детский профиль
// живёт и без неё, а новый datasetId без сервиса или без Dify — отказ knowledge_unavailable,
// не исключение и не обязательный параметр конструктора (тот же приём, что у
// FilesController/OnboardingController с опциональными сервисами).
public class UsersController(UserStore users, SessionManager sessions,
    UserKnowledgeCascade knowledgeCascade, DefaultAssistantProvisioner provisioner,
    ProjectMembershipService membership, IPersonalProjects personalProjects,
    ChildTutorProvisioner childTutorProvisioner, ClaudeSubscriptionPool subscriptionPool,
    LlmProviderRegistry llmProviders,
    PersonaManager personas, ChildSubjectMaterialsStore childMaterials,
    ILogger<UsersController> logger,
    // `= null` — обязательность для DI (S2b-1, ADR-014): на выключенной вертикали «Знаний»
    // активация контроллера не должна падать; без неё новый datasetId — отказ ниже.
    KnowledgeService? knowledge = null) : ControllerBase
{
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    [HttpGet]
    public IActionResult GetAll()
    {
        var dtos = users.GetAll().Select(ToDto);
        return Ok(dtos);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        var validationError = ValidateUsername(req.Username)
                           ?? ValidatePassword(req.Password)
                           ?? ValidateRole(req.Role)
                           ?? ValidateExecutionEnvironment(req.ExecutionEnvironment);
        if (validationError is not null) return BadRequest(new { error = validationError });

        try
        {
            var user = users.Add(req.Username, req.Password, req.Role,
                req.ExecutionEnvironment ?? ExecutionEnvironments.Local);
            // Провижн ассистента новому пользователю (план 2.3): новичок сразу получает
            // ассистента при заведении, без отдельного шага. Идемпотентно — повторный
            // вызов на первом входе вернёт уже созданную заготовку.
            await provisioner.EnsureAsync(user.Id);
            return CreatedAtAction(nameof(GetAll), ToDto(user));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateUserRequest req)
    {
        if (req.Username is not null)
        {
            var err = ValidateUsername(req.Username);
            if (err is not null) return BadRequest(new { error = err });
        }

        if (req.Role is not null)
        {
            var err = ValidateRole(req.Role);
            if (err is not null) return BadRequest(new { error = err });
        }

        var envChange = req.ExecutionEnvironment;
        if (envChange is not null)
        {
            var err = ValidateExecutionEnvironment(envChange);
            if (err is not null) return BadRequest(new { error = err });

            // Среда фиксируется после появления чатов: корни проектов и профили сред различаются,
            // resume-транскрипты привязаны к путям старой среды (аналог guard'а смены провайдера)
            var existing = users.GetById(id);
            if (existing is not null && existing.ExecutionEnvironment != envChange
                && sessions.HasSessionsOwnedBy(id))
                return Conflict(new { error = "Нельзя сменить среду исполнения: у пользователя уже есть чаты. Удалите их и повторите." });
        }

        try
        {
            if (!users.Update(id, req.Username, req.Role, envChange))
                return NotFound();

            var user = users.GetById(id)!;
            return Ok(ToDto(user));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        // Нельзя удалить самого себя
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (id == currentUserId)
            return BadRequest(new { error = "Нельзя удалить собственную учётную запись" });

        // Владелец общих проектов с живыми участниками: пока они есть, учётку не удаляем
        if (membership.OwnerHasMembers(id))
            return Conflict(new
            {
                error = "У пользователя есть общие проекты с участниками: сначала уберите участников.",
                code = "owner_has_members",
            });

        var user = users.GetById(id);
        try
        {
            if (!users.Delete(id))
                return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        // Каскад знаний: датасеты «{username}:…», локальные сторы, персоны. Без него всё это
        // сиротеет, а новый пользователь с тем же именем увидел бы чужие базы как свои.
        if (user is not null)
        {
            try { await knowledgeCascade.CleanupAsync(user.Id, user.Username); }
            catch (Exception ex) { logger.LogWarning(ex, "Каскад знаний при удалении пользователя {User}", id); }

            // Каталог материалов предмета (разрез «Знания репетитора» §2, К2c): без этого
            // звена data/child-subjects/{id} сиротеет навсегда — ребёнок — id из UserStore,
            // повторного с таким id пользователя не будет. Сбой звена — только лог: ответ
            // Delete не меняется (тот же best-effort, что у соседних звеньев).
            try { await childMaterials.DeleteChildAsync(user.Id); }
            catch (Exception ex) { logger.LogWarning(ex, "Каталог материалов ребёнка при удалении {User}", id); }
        }

        // «Личное»: запись стора удаляется, папка на диске остаётся (ADR-040 §3)
        try { personalProjects.DeleteOf(id); }
        catch (Exception ex) { logger.LogWarning(ex, "Удаление записи «Личного» пользователя {User}", id); }

        return NoContent();
    }

    [HttpPut("{id}/password")]
    public IActionResult ResetPassword(string id, [FromBody] ResetPasswordRequest req)
    {
        var err = ValidatePassword(req.NewPassword);
        if (err is not null) return BadRequest(new { error = err });

        if (!users.ResetPassword(id, req.NewPassword))
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Живой детский профиль пользователя для админского редактора (ADR-041 «Контракт», ручка №10;
    /// Д1): та же форма ответа, что у PUT — { profile, tutorPersonaId }. Только снимок на момент
    /// чтения: ни записей, ни провижна, ни валидатора тела — поэтому и отказов таблицы «при
    /// включении» (not_container, invalid_window, guardian_not_found) здесь нет: локальному
    /// пользователю, которого тот же PUT выключает телом { enabled: false } с 200 (ветка
    /// `!req.Enabled` метода `SetChildProfile` ниже), здесь просто нет профиля — 200 с
    /// profile: null (тот же случай, который документирует ChildProfileResponse). Роль — ровно
    /// как у PUT: [Authorize] по claim и AdminByStore по стору; ребёнок свой профиль не читает.
    /// </summary>
    [HttpGet("{id}/child-profile")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public IActionResult GetChildProfile(string id)
    {
        var user = users.GetById(id);
        if (user is null)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });

        // Снимок, а не живой объект: ToChildProfileDto копирует поля и списки в новые записи,
        // поэтому параллельная правка стора в ответ уже не попадает. Тьютор — живой ключ из
        // профиля; у пользователя без профиля — пустая строка (тот же ответ ветки выключения PUT).
        return Ok(new ChildProfileResponse(ToChildProfileDto(user.ChildMode), user.ChildMode?.TutorPersonaId ?? ""));
    }

    /// <summary>
    /// Детский профиль пользователя (ADR-041 «Контракт», ручка №10; KM-B1e): включает и правит
    /// режим только админ. Роль — и по claim ([Authorize] класса), и по стору (AdminByStore):
    /// сервисный токен хода — всегда роль "user", без политики он к админ-ручке не допускается,
    /// а снятое админство не ждало бы истечения токена. [ChildAllowed] здесь нет намеренно:
    /// ребёнок свой профиль не правит (тот же приём, что у AdminUserModelTiersController).
    /// Тело — ChildProfileDto или { enabled: false } для выключения. Ответ 200 { profile,
    /// tutorPersonaId }; отказы — { code, message } с кодами ADR (фронт переводит code сам).
    /// Правила (RulesAgreedAt/RulesSummary) из тела не принимаются: их пишет только детский
    /// rules/agree (KM-B6b); resetRules=true — сбросить оба.
    /// </summary>
    [HttpPut("{id}/child-profile")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> SetChildProfile(string id, [FromBody] ChildProfileDto req)
    {
        var user = users.GetById(id);
        if (user is null)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });
        // Кто привязывает базу знаний (К1b): адмен ручки — он же автор привязки в сторе.
        var callerId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);

        // Выключение — без проверок тела: отказы таблицы ADR — проверки «при включении».
        // Профиль при этом сохраняется (Enabled=false): тело {enabled:false} просит выключить
        // режим, а не стереть выставленные админом лимиты; политика «профиль выключен» =
        // обычный пользователь (ChildModePolicy).
        if (!req.Enabled)
        {
            users.DisableChildMode(id);
            var off = users.GetById(id)!;
            return Ok(new ChildProfileResponse(ToChildProfileDto(off.ChildMode), off.ChildMode?.TutorPersonaId ?? ""));
        }

        if (ChildProfileValidator.Validate(req, user, users, subscriptionPool, llmProviders) is { } reject)
            return BadRequest(new { code = reject.Code, message = reject.Message });

        // Привязка базы знаний (К1b, разрез §1/§3) — после того, как тело само читаемо: id
        // предметов здесь ключи сличения с сохранённой привязкой. Новый datasetId проверяется
        // по свежему списку Dify (он один на запрос и только если тело просит привязку).
        // Без вертикали «Знаний», без настроенного Dify или без ответа Dify — отказ до записи
        // профиля: не исключение и не тихий пропуск проверки.
        if (RequestsKnowledgeBinding(req))
        {
            if (knowledge is null || !knowledge.IsConfigured)
                return BadRequest(new { code = "knowledge_unavailable", message = "База знаний недоступна: вертикаль «Знаний» выключена или Dify не настроен." });

            IReadOnlyDictionary<string, string> datasets;
            try
            {
                // id → логическое имя (префикс неймспейса снимает сам KnowledgeService): дальше
                // по этому имени идёт предикат допустимой привязки (К1a).
                datasets = (await knowledge.ListDatasetsAsync()).ToDictionary(d => d.Id, d => d.Name ?? "");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Детский режим: список датасетов Dify не получен для {User}", id);
                return BadRequest(new { code = "knowledge_unavailable", message = "Не удалось проверить базу знаний: Dify не ответил." });
            }

            // Привязывает адмен ручки: «своя» база бывает у него, а не у ребёнка. Вызывающий
            // проверяется после списка датасетов — непосредственно перед валидатором, который
            // его читает: состояние «адмена нет в сторе» достижимо только внутри запроса —
            // политика ручки (AdminByStore) читает тот же стор до действия, и на момент
            // авторизации вызывающий в нём есть. Знание о вызывающем до списка описывало бы
            // более ранний момент и сливалось бы с отказом вертикали; knowledge_unavailable
            // остаётся текстом только про вертикаль/Dify (минор R-К1b 24b98007).
            var caller = string.IsNullOrEmpty(callerId) ? null : users.GetById(callerId);
            if (caller is null)
                return StatusCode(403, new { code = "caller_not_found", message = "Администратор, вызвавший изменение, не найден." });

            if (ChildProfileValidator.ValidateSubjectKnowledge(req, caller, users, LiveKnowledge(user), datasets) is { } kReject)
                return BadRequest(new { code = kReject.Code, message = kReject.Message });
        }

        // Снимок прежнего профиля — ради отката при отказе провижнера («профиль не сохранён»).
        var previous = user.ChildMode;
        // Целый новый профиль, мутация — под локом стора: RulesAgreedAt/RulesSummary стор
        // возьмёт из живого профиля, а не из нашего снимка (иначе затёрся бы параллельный
        // agree ребёнка), и два одновременных PUT дают один целый профиль, не смесь полей.
        // Привязка знания — тот же приём: в профиле только кандидат (id вызывающего админа и
        // момент запроса), живая пара остаётся, если id датасета не изменился (К1b).
        // Отзыв согласия (Х1): acknowledgeRouteRisk=false — снять согласие даже при том же providerKey;
        // здесь только список id, решение — под замком стора по живому предмету (UserStore.SetChildProfile).
        HashSet<string>? revokedAcks = null;
        if (req.Subjects is { } bodySubjects)
        {
            revokedAcks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in bodySubjects)
                if (s?.AcknowledgeRouteRisk == false && !string.IsNullOrWhiteSpace(s.Id))
                    revokedAcks.Add(s.Id!);
        }

        users.SetChildProfile(id, ToChildProfile(req, callerId ?? "", DateTime.UtcNow),
            resetRules: req.ResetRules == true, revokeAcknowledgedSubjectIds: revokedAcks);

        // Тьютор провится после записи: EnsureAsync закрепляет id в существующем профиле
        // (SetChildTutorPersona) и идемпотентен — повторное включение возвращает живого тьютора.
        string? tutorId;
        try
        {
            tutorId = await childTutorProvisioner.EnsureAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Детский режим: провижн тьютора не удался для {User}", id);
            RollbackChildProfile(id, previous);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать персону-тьютора; профиль не изменён." });
        }
        // null — провижн невозможен (пользователь или профиль исчезли на параллельной правке,
        // запрос отменён): ребёнок без тьютора не остаётся — тот же откат и 500.
        if (tutorId is null)
        {
            RollbackChildProfile(id, previous);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось закрепить персону-тьютора; профиль не изменён." });
        }

        // Репетиторы предметов (Т2b, разрез §2): после тьютора профиля — по каждому предмету
        // сохранённого профиля. Провижер идемпотентен: живой ключ возвращает тот же id без
        // создания (один репетитор может вести несколько предметов, засев миграции даёт
        // предмету тьютора профиля), пустой или висящий — провится заново.
        // «Создана ЭТИМ запросом» собирается в цикле: ключ результата, отличный от ключа
        // предмета на момент вызова (живое чтение прямо перед ним). Сравнение с снимком до
        // цикла — неверный критерий при параллельном PUT: соседний запрос закрепил своего
        // репетитора после снимка, а этот провиж вернул его как живой — откат по снимку удалил
        // бы чужую персону, оставив висящий ключ (минор R-Т2b 3a7f4e3c). Точность здесь —
        // окно одного вызова: чужая запись между живым чтением и провижем того же предмета
        // всё ещё возможна; точное «создана мной» знает только провижер под своим замком.
        var ownCreatedTutors = new List<string>();
        try
        {
            if (users.GetById(id)?.ChildMode is { } stored)
            {
                foreach (var subject in stored.Subjects)
                {
                    var beforeKey = subject.TutorPersonaId;
                    string? subjectTutorId;
                    try
                    {
                        subjectTutorId = await childTutorProvisioner.EnsureSubjectTutorAsync(id, subject.Id);
                    }
                    catch
                    {
                        // Исключение изнутри провижна: ключ уже закреплён (отказ вещания после
                        // закрепления) — созданная персона наша, добавляем её живым ключом;
                        // закрепления не было — провижер удалил свою сам, и живой ключ равен
                        // прежнему, добавлять нечего.
                        if (!string.IsNullOrWhiteSpace(subject.TutorPersonaId) && subject.TutorPersonaId != beforeKey)
                            ownCreatedTutors.Add(subject.TutorPersonaId);
                        throw;
                    }
                    // null — провижн невозможен (ребёнок или предмет исчезли на параллельной
                    // правке, запрос отменён): ребёнок с предметом без репетитора не остаётся.
                    if (subjectTutorId is null)
                    {
                        logger.LogWarning("Детский режим: провижн репетитора предмета {Subject} не удался для {User}", subject.Id, id);
                        RollbackSubjectTutors(id, ownCreatedTutors);
                        RollbackChildProfile(id, previous);
                        return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось закрепить персону-репетитора предмета; профиль не изменён." });
                    }
                    if (subjectTutorId != beforeKey) ownCreatedTutors.Add(subjectTutorId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Детский режим: провижн репетитора предмета не удался для {User}", id);
            RollbackSubjectTutors(id, ownCreatedTutors);
            RollbackChildProfile(id, previous);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать персону-репетитора предмета; профиль не изменён." });
        }

        var saved = users.GetById(id);
        if (saved?.ChildMode is not { } finalProfile)
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Профиль ребёнка не сохранился; повторите." });

        // Репетиторы-сироты удалённых предметов (Т2d, разрез §1): ключ каждого предмета,
        // исчезнувшего из списка (его Id нет в итоговом профиле), — кандидат на удаление.
        // Снимок previous — объект до записи: при живом слиянии «тела без subjects» его
        // список — копия [.. live.Subjects] (UserStore.cs:671), предметы общие с живым
        // профилем, но в этой ветке живой список сохраняется как есть — ни один Id не
        // исчезает и список кандидатов пуст. При «теле с subjects» новые объекты строит
        // ToChildProfile, а ключи исчезнувших предметов доживают только в previous —
        // сравнение по Id, не по объекту: общий объект дал бы пустой список там, где
        // предмет реально убран. Провижер решает «сирота ли» по ЖИВОМУ профилю под своим
        // замком — устаревшая запись (предмет вернулся параллельной правкой) не удаляется.
        // Чаты предмета не трогаются (только чтение — Т1c/Т1d). Сбой удаления — лог внутри
        // провижера, ответ остаётся 200: профиль уже сохранён и корректен.
        var orphanTutors = RemovedSubjectTutors(previous, finalProfile);
        if (orphanTutors.Count > 0)
            await childTutorProvisioner.RemoveOrphanTutorsAsync(id, orphanTutors);

        // Перезакрепление маршрута предмета (Т2e, Т2e2; решение Андрея 18.11): старые чаты предмета
        // продолжают с новой моделью — их закреплённые при создании поля (PersonaId, Provider, Model)
        // приводятся к маршруту предмета, а при смене поставщика транскрипт ещё и копируется в
        // профиль нового (SessionManager.RepinChildSubjectAsync → ProviderMigration). Условие одно:
        // маршрут принят ChildRoute (не-пул только с согласием админа). Без согласия чаты не
        // трогаем: ход и так откажет им (ChildModeGate.LaunchMismatch), а чат с несогласованным
        // маршрутом — намеренное состояние «ждёт согласия».
        // Поля берёт делегат liveTarget: он читает ЖИВОЙ профиль на каждый чат внутри очереди
        // перезакрепления, а не снимок finalProfile (прочитан до await RemoveOrphanTutorsAsync):
        // параллельный PUT, дописавшийся за время ожидания, не будет затёрт значениями этого запроса.
        // Сбой переноса отдельного чата — лог внутри, ответ остаётся 200.
        foreach (var subject in finalProfile.Subjects)
        {
            if (subject is null || string.IsNullOrWhiteSpace(subject.Id)) continue;
            var subjectId = subject.Id;
            await sessions.RepinChildSubjectAsync(id, subjectId, () =>
            {
                var live = users.GetById(id)?.ChildMode?.Subjects?
                    .FirstOrDefault(x => x is not null && string.Equals(x.Id, subjectId, StringComparison.OrdinalIgnoreCase));
                if (live is null) return null;
                if (!ChildRoute.Evaluate(live.ProviderKey, live.Model,
                        live.RouteAcknowledgedAt is not null, subscriptionPool, llmProviders).Accepted)
                    return null;
                return new ChildRepinTarget(live.TutorPersonaId, live.ProviderKey, live.Model);
            });
        }

        return Ok(new ChildProfileResponse(ToChildProfileDto(finalProfile), tutorId));
    }

    // --- вспомогательные методы ---

    // Откат отказанного включения: тьютор, созданный ЭТИМ вызовом (его id в живом профиле
    // отличается от прежнего), удаляется — иначе сирота в personas.json; профиль
    // возвращается целиком, с прежними правилами и прежним тьютором.
    private void RollbackChildProfile(string id, ChildProfile? previous)
    {
        var liveTutor = users.GetById(id)?.ChildMode?.TutorPersonaId;
        if (!string.IsNullOrWhiteSpace(liveTutor) && liveTutor != previous?.TutorPersonaId)
            personas.Delete(liveTutor, id);
        users.RestoreChildProfile(id, previous);
    }

    // Откат репетиторов предметов, созданных ЭТИМ запросом: ключи, собранные в цикле провижна
    // (результат вызова, отличный от живого ключа предмета на момент вызова). Чужие ключи —
    // те, что закрепил параллельный запрос после живого чтения этого, — не трогаются: они
    // живут в профиле или будут убраны своим запросом (минор R-Т2b 3a7f4e3c). Свой откат
    // провижера (отказ закрепления) в этот разбор не попадает: там персона уже удалена им
    // самим. Сбой самого удаления — только лог: причина 500 уже известна, сирота видна в
    // логе (тот же приём, что у RollbackDelete в провижере).
    private void RollbackSubjectTutors(string id, List<string> ownCreatedTutors)
    {
        foreach (var key in ownCreatedTutors)
        {
            try { personas.Delete(key, id); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Детский режим: откат персоны-репетитора предмета не прошёл (ребёнок {User}, персона {Persona})", id, key);
            }
        }
    }

    // Ключи репетиторов исчезнувших предметов (Т2d): непустой TutorPersonaId каждого
    // предмета previous, чьего Id нет в итоговом профиле. Сравнение по Id — снимок и итог
    // при «теле без subjects» делят одни и те же объекты предметов (UserStore.cs:671),
    // по объекту разница не читалась бы. «Оставшийся» — любой предмет итогового профиля,
    // выключенный (Enabled=false) тоже: его репетитора нет в списке удаления.
    private static List<string> RemovedSubjectTutors(ChildProfile? previous, ChildProfile finalProfile)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in finalProfile.Subjects)
            if (subject is not null)
                kept.Add(subject.Id);

        var removed = new List<string>();
        foreach (var subject in previous?.Subjects ?? [])
        {
            if (subject is null || string.IsNullOrWhiteSpace(subject.TutorPersonaId)) continue;
            if (kept.Contains(subject.Id)) continue;
            removed.Add(subject.TutorPersonaId);
        }
        return removed;
    }

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.Role, u.CreatedAt, u.ExecutionEnvironment);

    // Есть ли в теле хоть одна привязка (К1b): нужен ли ради проверки свежий список Dify.
    // Пустой/отсутствующий datasetId — снятие, а не привязка: проверка ему не нужна.
    private static bool RequestsKnowledgeBinding(ChildProfileDto req) =>
        req.Subjects?.Any(s => !string.IsNullOrWhiteSpace(s?.Knowledge?.DatasetId)) == true;

    // Привязки ЖИВОГО профиля: id предмета → DatasetId (предметов без привязки в словаре нет).
    // Снимок ради вопроса «изменился ли id» — решение о сохранении пары стор делает под своим
    // локом по живому значению (UserStore.SetChildProfile), здесь только сравнение валидатора.
    private static Dictionary<string, string> LiveKnowledge(User user)
    {
        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subject in user.ChildMode?.Subjects ?? [])
            if (subject?.Knowledge is { } k)
                live[subject.Id] = k.DatasetId;
        return live;
    }

    // Wire-контракт ADR-041 (ChildProfileDto) → модель стора. Правил (RulesAgreedAt/
    // RulesSummary) в wire-типе нет: они живут только в модели и из тела не принимаются
    // никогда. Порог предупреждения: нет поля — дефолт 5 (ADR-041).
    private static ChildProfile ToChildProfile(ChildProfileDto req, string callerId, DateTime attachedAt) => new()
    {
        Enabled = true,
        GuardianUserIds = [.. req.GuardianUserIds ?? []],
        ProviderKey = req.ProviderKey ?? "",
        Model = req.Model ?? "",
        Spaces = (req.Spaces ?? []).ToDictionary(
            kv => kv.Key,
            kv => new SpaceLimits
            {
                Windows = [.. (kv.Value?.Windows ?? []).Select(w => new TimeWindow
                {
                    From = w?.From ?? "",
                    To = w?.To ?? "",
                    Weekdays = w?.Weekdays is { } days ? [.. days] : null,
                })],
                DailyMinutes = kv.Value?.DailyMinutes,
            }),
        // Предметы (Т2a): тела нет — null как сигнал стору «предметы не трогать» (живое
        // слияние в SetChildProfile; без живого профиля он нормализует его в []). Явный []
        // — очистить. Порядок сохраняется. Тьютора предмета из тела НЕ читаем: слияние
        // берёт его по Id из живого профиля, новый предмет остаётся пустым — персону
        // пишет SetChildSubjectTutor (Т2b).
        Subjects = req.Subjects is null
            ? null!
            : [.. req.Subjects.Select(s => new ChildSubject
                {
                    Id = s?.Id ?? "",
                    Label = s?.Label ?? "",
                    Glyph = s?.Glyph ?? "",
                    Color = s?.Color ?? "",
                    ProviderKey = s?.ProviderKey ?? "",
                    Model = s?.Model ?? "",
                    Enabled = s?.Enabled ?? true,
                    // Согласие на не-Anthropic маршрут (Т5a): из тела — только флаг; в модель идёт КАНДИДАТ
                    // (момент запроса и админ из claim) ровно при true; при false или отсутствии поля — пусто.
                    // Решает слияние стора под замком по ЖИВОМУ предмету: то же providerKey и уже стоящее
                    // согласие — остаётся прежнее; смена providerKey без нового согласия в теле — сброс;
                    // false — отзыв (id передан в revokeAcknowledgedSubjectIds), он снимает и живое согласие.
                    RouteAcknowledgedAt = s?.AcknowledgeRouteRisk == true ? attachedAt : null,
                    RouteAcknowledgedBy = s?.AcknowledgeRouteRisk == true ? callerId : null,
                    // Привязка знания (К1b): из тела — только id датасета; автор и момент —
                    // вызывающий админ и момент запроса. Это КАНДИДАТ: живое слияние стора
                    // сохранит прежнюю пару, если id не изменился, и возьмёт этот — если
                    // изменился. null (поля нет или datasetId пуст) — привязка снимается.
                    Knowledge = string.IsNullOrWhiteSpace(s?.Knowledge?.DatasetId)
                        ? null
                        : new ChildSubjectKnowledge(s!.Knowledge!.DatasetId!, callerId, attachedAt),
                })],
        DailyBudgetUsd = req.DailyBudgetUsd,
        MonthlyBudgetUsd = req.MonthlyBudgetUsd,
        WarnMinutes = req.WarnMinutes ?? 5,
    };

    // Обратный маппинг — для поля profile ответа (null — профиля нет: выключение пользователя,
    // который им и не был). Правила наружу не отдаются: их поле — дата согласия ребёнка,
    // её показывает status-ручка (№1), не этот ответ.
    private static ChildProfileDto? ToChildProfileDto(ChildProfile? p) => p is null ? null :
        new ChildProfileDto(p.Enabled, [.. p.GuardianUserIds], p.ProviderKey, p.Model,
            p.Spaces.ToDictionary(
                kv => kv.Key,
                kv => new ChildSpaceLimitsDto([.. kv.Value.Windows.Select(
                    w => new ChildTimeWindowDto(w.From, w.To, w.Weekdays is { } days ? [.. days] : null))],
                    kv.Value.DailyMinutes)),
            // Предметы — в порядке стора (он же порядок плиток); тьютор — живой (Т2a:
            // поле ответа, из тела оно не читалось). Привязка знания — id и момент (К1b):
            // автор привязки в ответ не идёт, он остаётся в сторе.
            [.. p.Subjects.Select(s => new ChildSubjectDto(
                s.Id, s.Label, s.Glyph, s.Color, s.ProviderKey, s.Model, s.Enabled, s.TutorPersonaId,
                s.Knowledge is { } k ? new ChildSubjectKnowledgeDto(k.DatasetId, k.AttachedAt) : null,
                s.RouteAcknowledgedAt is not null, s.RouteAcknowledgedAt))],
            p.DailyBudgetUsd, p.MonthlyBudgetUsd, p.WarnMinutes, null);

    private static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Имя пользователя не может быть пустым";
        if (username.Length < 3 || username.Length > 32)
            return "Имя пользователя должно содержать от 3 до 32 символов";
        if (!UsernameRegex.IsMatch(username))
            return "Имя пользователя может содержать только буквы, цифры, _ и -";
        return null;
    }

    private static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
            return "Пароль должен содержать не менее 8 символов";
        return null;
    }

    private static string? ValidateRole(string? role)
    {
        if (role is not "admin" and not "user")
            return "Роль должна быть 'admin' или 'user'";
        return null;
    }

    private static string? ValidateExecutionEnvironment(string? env)
    {
        if (env is not null && !ExecutionEnvironments.IsValid(env))
            return "Среда исполнения должна быть 'local' или 'container'";
        return null;
    }
}

public record UserDto(string Id, string Username, string Role, DateTime CreatedAt, string ExecutionEnvironment);
public record CreateUserRequest(string Username, string Password, string Role, string? ExecutionEnvironment = null);
public record UpdateUserRequest(string? Username, string? Role, string? ExecutionEnvironment = null);
public record ResetPasswordRequest(string NewPassword);
