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
public class UsersController(UserStore users, SessionManager sessions,
    UserKnowledgeCascade knowledgeCascade, DefaultAssistantProvisioner provisioner,
    ProjectMembershipService membership, IPersonalProjects personalProjects,
    ChildTutorProvisioner childTutorProvisioner, ClaudeSubscriptionPool subscriptionPool,
    LlmProviderRegistry llmProviders,
    PersonaManager personas,
    // projects + childSubjectProjects (Б8c): каскад проектов-предметов при удалении ребёнка —
    // единая уборка ProjectDeletionService и per-ключевой замок провижнера (ветка Delete ниже).
    ProjectManager projects, ChildSubjectProjectProvisioner childSubjectProjects,
    ProjectDeletionService deletion,
    // childCuratorSync (Б14a, решение 61): кураторы = опекуны профиля; обход Members
    // проектов-предметов на каждой точке сохранения профиля.
    ChildCuratorSync childCuratorSync,
    // sharedProjects (Б4a, ADR-041 ред. 6 решение 60): готовность опекунов для окна — флаги
    // читаются только через гейт (его правило: инлайновых GetValue этих ключей не заводим).
    SharedProjectsGate sharedProjects,
    ILogger<UsersController> logger) : ControllerBase
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

            // Звено «Каталог материалов ребёнка» снято (Х-W4-19, хвост решения 68): стора в
            // обороте нет, каталога child-subjects на проде и стендах тоже нет — сносить нечего.

            // Проекты-предметы (Б8c, ADR-041 ред. 5, разрез §11.1): удаление ребёнка сносит все его
            // проекты-предметы штатным ProjectManager.Delete — тем же путём стора, что и
            // ProjectsController.Delete, а не своей рекурсией по папке (снимается запись и её
            // служебные файлы: фон, значок). Знания проекта (Dify-датасет, запись БЗ) снимает
            // соседнее звено «Каскада знаний» выше: оно идёт до этого звена и находит проекты
            // по владельцу, пока запись ещё в сторе.
            if (user.ChildMode is { } childProfile)
            {
                // Вызов провижнера по предмету снимка — это замок, а не провижн: per-ключевой
                // замок ChildSubjectProjectProvisioner (childId:subjectId) — единственный замок
                // создания проекта-предмета, и без входа в него остаётся окно: провижнер прочитал
                // ребёнка до users.Delete, а записал проект после нашего перечисления — сирота.
                // Под тем же замком порядок бинарный: либо провижн завершился до перечисления
                // (проект в него попадает), либо провижнер перечитал живой профиль после удаления
                // (ребёнка нет → проекта нет). Ребёнка в сторе здесь уже нет, поэтому провижн
                // ничего не создаёт: EnsureSubjectProjectAsync вернёт существующий проект или null.
                foreach (var subject in childProfile.Subjects)
                {
                    try
                    {
                        var project = await childSubjectProjects.EnsureSubjectProjectAsync(user.Id, subject);
                        if (project is not null) await deletion.DeleteAsync(project);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "Проект-предмет предмета {Subject} при удалении ребёнка {User}", subject.Id, id); }
                }
            }

            // Страховка: проекты-предметы, которых цикл не охватил — «старые» проекты ранее
            // удалённых из профиля предметов (по ADR-041 они живут) и проекты, записанные
            // параллельным провижнером, пока цикл ждал его замка. Сбой одного проекта —
            // только лог, остальные удаляются (тот же best-effort, что у соседних звеньев:
            // ответ Delete остаётся 204, необработанного исключения нет).
            foreach (var orphan in projects.GetByOwner(user.Id).Where(p => p.ChildSubjectId is not null).ToList())
            {
                try { await deletion.DeleteAsync(orphan); }
                catch (Exception ex) { logger.LogWarning(ex, "Проект-предмет {Project} при удалении ребёнка {User}", orphan.Id, id); }
            }
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
    /// Галка «детский режим» в «Управлении пользователями» (ADR-041 ред. 4, решение 30; Б4a):
    /// узкая ручка включения/выключения без тела профиля. Включение заводит профиль в состоянии
    /// «не настроен»: без предметов, маршрутов и тьютора — дальше решает только политика
    /// fail-closed: нечитаемый профиль есть DenyAll, ребёнку не открывается ничего
    /// (ChildModePolicy), окно показывает «администратор ещё не настроил занятия». Галка ничего
    /// не провит: тьютор и проекты-предметы придут первой полной записью PUT child-profile.
    /// Уже включённый режим — идемпотентный 200 без записи: повторный запрос не смеет тронуть
    /// выставленный профиль. Выключенный — возвращается с настройками (DisableChildMode сохранил
    /// их ровно для этого): в запись идёт только Enabled, предметы/тьютор/правила/согласие стор
    /// возьмёт из живого профиля под своим замком. Выключение — как сейчас: та же ветка, что у
    /// PUT child-profile {enabled:false}. Запись — через стор под его замком: два одновременных
    /// PUT сходятся в один профиль; пользователя нет на момент записи — 500, профиля пополам нет.
    /// Проверки «при включении» здесь: not_container (среда ребёнка — песочница, И2/И4; среда
    /// фиксируется после появления чатов — guard метода Update, — поэтому локальный пользователь
    /// с чатами не будет включён никогда). Роль — ровно как у child-profile: [Authorize] по claim
    /// + AdminByStore; [ChildAllowed] нет намеренно: ребёнок свой режим не включает (тот же
    /// приём, что у child-profile). На этой ручке ребёнка отбивает правило роли до контроллера;
    /// на общей непомеченной поверхности его дописывает сторож эндпоинтов (ChildModeEndpointGuardTests).
    /// </summary>
    [HttpPut("{id}/child-mode")]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public IActionResult SetChildMode(string id, [FromBody] ChildModeRequest req)
    {
        var user = users.GetById(id);
        if (user is null)
            return NotFound(new { code = "user_not_found", message = "Пользователь не найден." });

        // Выключение — без проверок: отказы таблицы ADR — проверки «при включении» (тот же
        // порядок, что у ветки !Enabled метода SetChildProfile).
        if (!req.Enabled)
        {
            users.DisableChildMode(id);
            var off = users.GetById(id)!;
            return Ok(new ChildProfileResponse(ToChildProfileDto(off.ChildMode), off.ChildMode?.TutorPersonaId ?? ""));
        }

        if (user.ExecutionEnvironment != ExecutionEnvironments.Container)
            return BadRequest(new { code = "not_container", message = "Детский режим включается только пользователям со средой исполнения container." });

        // Решение «включить/оставить» и запись — под замком стора (EnableChildModeIfAbsentOrDisabled):
        // здесь live читался бы ДО записи, и параллельный PUT child-profile с телом между чтением
        // и записью затёрся бы пустым профилем галки. null — пользователя нет на момент записи
        // (параллельное удаление): профиля пополам не бывает.
        var profile = users.EnableChildModeIfAbsentOrDisabled(id);
        if (profile is null)
            return StatusCode(500, new { code = "profile_not_saved", message = "Профиль ребёнка не сохранился; повторите." });

        return Ok(new ChildProfileResponse(ToChildProfileDto(profile), profile.TutorPersonaId));
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

        // Снимок прежнего профиля — ради отката при отказе провижнера («профиль не сохранён»).
        var previous = user.ChildMode;
        // Целый новый профиль, мутация — под локом стора: RulesAgreedAt/RulesSummary стор
        // возьмёт из живого профиля, а не из нашего снимка (иначе затёрся бы параллельный
        // agree ребёнка), и два одновременных PUT дают один целый профиль, не смесь полей.
        // Согласие на риск маршрутов (ADR-041 ред. 4, решение 26) — ОДНО на профиль: здесь только
        // сигнал «админ отзывает» (acknowledgeRouteRisk=false в теле профиля), решение по живому
        // профилю — под замком стора (UserStore.SetChildProfile). Флаг в теле предмета больше не
        // читается: согласия предмета нет, его прежние поля — история.
        users.SetChildProfile(id, ToChildProfile(req, callerId ?? "", DateTime.UtcNow),
            resetRules: req.ResetRules == true, revokeRouteRisk: req.AcknowledgeRouteRisk == false);

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

        // Провижнер предмета (Б17a, решения 69–70, разрез §13): после тьютора профиля — по каждому
        // предмету сохранённого профиля: проект предмета (Б8b) → проектная персона → руководитель
        // проекта (DefaultPersonaId). Провижер идемпотентен: живой руководитель возвращается без
        // создания; предмет без проекта или с висящим руководителем провится (перенос прежних
        // репетиторов — миграция Б17b). Ключа репетитора у предмета больше нет — Б17c.
        // «Создана ЭТИМ запросом» — собственный ответ провижнера (Created): точное «создана
        // мной» знает только он под своим замком (минор R-Т2b 3a7f4e3c — сравнение с снимком
        // контроллера ошибается на параллельном PUT). Отказанная персона уже удалена самим
        // провижером (RollbackDelete) — в список своего отката она не входит.
        var ownCreatedTutors = new List<string>();
        try
        {
            if (users.GetById(id)?.ChildMode is { } stored)
            {
                foreach (var subject in stored.Subjects)
                {
                    var provision = await childTutorProvisioner.EnsureSubjectTutorAsync(id, subject.Id);
                    // null — провижн невозможен (ребёнок или предмет исчезли на параллельной
                    // правке, запрос отменён, домашняя папка не настроена): ребёнок с предметом
                    // без репетитора не остаётся.
                    if (provision is null)
                    {
                        logger.LogWarning("Детский режим: провижн предмета {Subject} не удался для {User}", subject.Id, id);
                        RollbackSubjectTutors(id, ownCreatedTutors);
                        RollbackChildProfile(id, previous);
                        return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать репетитора предмета; профиль не изменён." });
                    }
                    if (provision.Value.Created) ownCreatedTutors.Add(provision.Value.Id);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Детский режим: провижн предмета не удался для {User}", id);
            RollbackSubjectTutors(id, ownCreatedTutors);
            RollbackChildProfile(id, previous);
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Не удалось создать репетитора предмета; профиль не изменён." });
        }

        var saved = users.GetById(id);
        if (saved?.ChildMode is not { } finalProfile)
            return StatusCode(500, new { code = "tutor_provision_failed", message = "Профиль ребёнка не сохранился; повторите." });

        // Перезакрепление маршрута предмета (Т2e, Т2e2; Б2b): старые чаты предмета продолжают с
        // новой моделью — их закреплённые при создании поля (PersonaId, Provider, Model) приводятся
        // к ЖИВОМУ маршруту предмета, а при смене поставщика транскрипт ещё и копируется в профиль
        // нового (SessionManager.RepinChildSubjectAsync → ProviderMigration). Живой маршрут
        // разворачивает детский гейт (IChildModeGate.LiveRouteFor): та же точка, что у сверки
        // запуска и у догона на старте хода — своей копии развёртки здесь нет (И10: второй точки
        // правды не заводим). Живой профиль читается на каждый чат внутри очереди перезакрепления,
        // а не снимок finalProfile (прочитан до await RemoveOrphanTutorsAsync): параллельный PUT,
        // дописавшийся за время ожидания, не будет затёрт значениями этого запроса.
        // Маршрут не принят ChildRoute (в т. ч. без согласия профиля, решение 26) или репетитора
        // нет — чаты не трогаем: ход и так откажет им (ChildModeGate.LaunchMismatch), а чат с
        // несогласованным маршрутом — намеренное состояние «ждёт согласия». Сбой переноса
        // отдельного чата — лог внутри, ответ остаётся 200.
        foreach (var subject in finalProfile.Subjects)
        {
            if (subject is null || string.IsNullOrWhiteSpace(subject.Id)) continue;
            await sessions.RepinChildSubjectAsync(id, subject.Id);
        }

        // Кураторы = опекуны (Б14a, решение 61): после записи профиля — полный обход
        // Members проектов-предметов (desired читается из ЖИВОГО профиля под замком синка,
        // не из снимка). Сбой отдельного проекта — лог внутри синка, ответ остаётся 200:
        // профиль сохранён и корректен, следующий обход доводит.
        await childCuratorSync.SyncChildCuratorsAsync(id);

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

    private static UserDto ToDto(User u) =>
        new(u.Id, u.Username, u.Role, u.CreatedAt, u.ExecutionEnvironment);

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
        // — очистить. Порядок сохраняется. Предмет — только плитка (Б17c): маршрута, репетитора и
        // знаний в нём нет, маркеры Migrated/KnowledgeMoved стор берёт из живого предмета.
        Subjects = req.Subjects is null
            ? null!
            : [.. req.Subjects.Select(s => new ChildSubject
                {
                    Id = s?.Id ?? "",
                    Label = s?.Label ?? "",
                    Glyph = s?.Glyph ?? "",
                    Color = s?.Color ?? "",
                    Enabled = s?.Enabled ?? true,
                })],
        DailyBudgetUsd = req.DailyBudgetUsd,
        MonthlyBudgetUsd = req.MonthlyBudgetUsd,
        WarnMinutes = req.WarnMinutes ?? 5,
        // Согласие админа на риск маршрутов (ADR-041 ред. 4, решение 26) — ОДНО на профиль: из тела
        // читается только флаг, в модель идёт КАНДИДАТ (момент запроса и админ из claim) ровно при
        // true; при false или отсутствии поля — пусто. Решает слияние стора под замком по ЖИВОМУ
        // профилю: живое согласие остаётся (смена модели или поставщика его не касается), false —
        // отзыв (он передан revokeRouteRisk).
        RouteRiskAcknowledgedAt = req.AcknowledgeRouteRisk == true ? attachedAt : null,
        RouteRiskAcknowledgedBy = req.AcknowledgeRouteRisk == true ? callerId : null,
    };

    // Обратный маппинг — для поля profile ответа (null — профиля нет: выключение пользователя,
    // который им и не был). Правила наружу не отдаются: их поле — дата согласия ребёнка,
    // её показывает status-ручка (№1), не этот ответ. Маппинг больше не static: флаги опекунов
    // и рубильник инстанса читаются через SharedProjectsGate (Б4a).
    private ChildProfileDto? ToChildProfileDto(ChildProfile? p) => p is null ? null :
        new ChildProfileDto(p.Enabled, [.. p.GuardianUserIds], p.ProviderKey, p.Model,
            p.Spaces.ToDictionary(
                kv => kv.Key,
                kv => new ChildSpaceLimitsDto([.. kv.Value.Windows.Select(
                    w => new ChildTimeWindowDto(w.From, w.To, w.Weekdays is { } days ? [.. days] : null))],
                    kv.Value.DailyMinutes)),
            // Предметы — в порядке стора (он же порядок плиток); knowledgeMoved — пометка «у предмета
            // была привязана база знаний: перенесите материалы в папку проекта-предмета» (Б17c).
            [.. p.Subjects.Select(s => new ChildSubjectDto(
                s.Id, s.Label, s.Glyph, s.Color, s.Enabled, s.KnowledgeMoved))],
            p.DailyBudgetUsd, p.MonthlyBudgetUsd, p.WarnMinutes, null,
            // Согласие профиля (решение 26): состояние, момент и автор — поля фронта (галка
            // «согласие стоит», дата и «кто согласился»). Автор в ответе с Б4a: окно настроек
            // показывает его прямо; прежде он оставался только в сторе.
            p.RouteRiskAcknowledgedAt is not null, p.RouteRiskAcknowledgedAt,
            p.RouteRiskAcknowledgedBy,
            // Готовность опекунов к общим проектам (Б4a, ADR-041 ред. 6 решение 60): куратором
            // проекта-предмета опекун становится механизмом общих проектов — ему нужен свой флаг
            // shared-projects и рубильник инстанса. Окно прямо скажет, у чего что; сервер флаг
            // за человека не включает.
            [.. p.GuardianUserIds.Select(g => new ChildGuardianDto(g, sharedProjects.FlagOn(g)))],
            sharedProjects.InstanceEnabled);

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
// Тело узкой ручки галки (Б4a, ADR-041 ред. 4 решение 30): только флаг. Это не ChildProfileDto:
// галка не описывает профиль — «не настроен» есть состояние сервера, а не пустое тело.
public record ChildModeRequest(bool Enabled);
public record ResetPasswordRequest(string NewPassword);
