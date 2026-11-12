using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Notes.Controllers;

// REST настроек вики (S4c, ADR-035 §12г меры 7–8, раздел «Ночной разбор и его настройки»):
// одна ручка GET/PUT /api/wiki/settings для блока «Применение» вкладки «Вики» (макет M2):
// общий выключатель «Применять без ревью», классы с их состоянием и долей откатов, кнопка
// «Снова применять само». С шага NA-2 (Ф2) та же ручка обслуживает и блок «Ночной разбор»: PUT
// принимает его частично (мердж с состоянием на момент записи), GET отдаёт с подставленными
// умолчаниями. Образец — WikiController: владелец только из claim sub, чужая
// вики — 403, выключенный флаг llm-wiki — 409 (тот же код, что у линта).
//
// С LI-2 PUT с включённой ночью заводит вику тем же вызовом WikiKeeper.EnsureInitializedAsync,
// что и остальные человеческие точки: человек поставил тумблер — ночной разбор без заведённой
// вики невозможен, а сама служба её не заводит. GET остаётся читающим и вику не заводит.
//
// GET оценивает здоровье классов (WikiClassHealth) и возвращает настройки с замером: блок
// «Применение» обязан показывать текущую долю, а не память прошлого просмотра. Оценка
// идемпотентна и однопоточна на владельца; выключение класса и уведомление — её часть, а не
// действие человека. PUT ничего не переоценивает: он меняет настройки и возвращает их.
//
// С N7c GET несёт и итог последней ночи (lastNight — из keeper/nightly-last.json, пишет его
// ночная служба): блок «Ночной разбор» показывает результат, а не пустоту. Нет файла или он
// нечитаем — null без 500: «итога не было» и «файл не читается» для человека одно и то же,
// а чинит его сама служба (ReadLast — тот же reading, что у её проверки даты прохода).
// PUT итога не несёт: он меняет настройки, а не читает прошлые проходы (стор фронта
// сохраняет lastNight от последнего GET).
//
// «Только своя вики»: у ручки нет другого способа назвать чужую — явно названный в запросе
// чужой владелец отвергается 403 (тот же контракт, что у чужого ключа раздела в WikiController).
[ApiController]
[Authorize]
[Route("api/wiki/settings")]
public class WikiSettingsController : ControllerBase
{
    private readonly WikiClassHealth _health;
    private readonly IFeatureFlagGate? _flags;
    private readonly WikiKeeper _keeper;
    // WN-5: ручка ручного запуска обращается к самой ночной службе (в приложении она зарегистрирована
    // синглтоном — NotesSubsystem.cs:134). Необязательна: состав без службы (тест/сборка без шва)
    // отвечает на POST 503, GET/PUT работают как раньше.
    private readonly WikiNightlyService? _nightly;
    private readonly string _dataDir;

    public WikiSettingsController(WikiClassHealth health, IFeatureFlagGate? flags, WikiKeeper keeper, IConfiguration config,
        WikiNightlyService? nightly = null)
    {
        _health = health;
        _flags = flags;
        _keeper = keeper;
        _nightly = nightly;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена. [Authorize] — главный гейт; явная проверка — его копия
    // внутри действий: без токена действие отвечает 401 (контракт), а не падает с NRE на claim.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    // Признак сервисного токена (B2b, Ф2): тот же claim-литерал, что в PluginsController/
    // TasksController (MapInboundClaims=false оставляет raw-имена — см. PluginsController:39-44).
    // Настройки вики меняет только человек: сервисный токен (MCP-обвязка хода) на PUT
    // отказывается 403 — до флага и до любой записи; GET остаётся читающим.
    private const string TokenKindClaim = "typ";
    private const string ServiceTokenKind = "svc";
    private bool IsServiceToken => User.FindFirstValue(TokenKindClaim) == ServiceTokenKind;

    private const string CodeForeign = "wiki_foreign";
    private const string CodeLlmWikiOff = "wiki_llm_wiki_off";
    private const string CodeClassKey = "wiki_class_key";
    private const string CodeNothingToChange = "wiki_settings_nothing_to_change";
    private const string CodeNightly = "wiki_nightly";
    private const string CodeWriteFailed = "wiki_settings_write_failed";
    // WN-5: коды отказа ручки ручного запуска — по исходам TryStartManual.
    private const string CodeNightlyDisabled = "wiki_nightly_disabled";
    private const string CodeNightlyRunning = "wiki_nightly_running";
    private const string CodeSettingsInvalid = "wiki_settings_invalid";
    // 503 «службы нет» — без своего кода: общая форма проекта { error = … } (TasksController.cs:760).

    /// <summary>GET /api/wiki/settings — настройки своей вики с замером доли откатов по классам
    /// и источникам за 7 и 30 дней (мера 8). Побочный эффект — оценка здоровья: она же
    /// выключает перешагнувший порог класс (с уведомлением). Невалидный settings.json —
    /// умолчания в ответе, сигнала наружу нет: оценка поставила строку в _health.md.
    /// Ночный блок в ответе целый всегда: absent в файле — умолчания с выключенной ночью.
    /// lastNight — итог последней ночи из keeper/nightly-last.json; нет файла или он нечитаем —
    /// null (для читающего это одно «итога не было»), 500 из-за него нет.</summary>
    [HttpGet]
    public async Task<ActionResult<WikiSettingsDto>> Get()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return LlmWikiOff();

        var root = WikiPaths.Root(_dataDir, userId);
        var outcome = await _health.EvaluateAsync(userId);
        var (settings, _) = WikiSettingsStore.Read(root);
        var stats = outcome.State == WikiClassHealth.RunState.Ran ? outcome.Stats : [];
        // Итог последней ночи — из того же файла, что читает служба (ReadLast): без файла или
        // битый — null, тот же reading, что у её собственной проверки даты прохода.
        return Ok(BuildDto(settings, stats, WikiNightlyService.ReadLast(root), WikiNightlyService.IsRunning(root)));
    }

    /// <summary>PUT /api/wiki/settings — общий выключатель «Применять без ревью», состояние
    /// класса («Выключить» / «Снова применять само», Enable=true ставит ResumedAt — окно отсчёта
    /// класса начинается заново, мера 7) и блок «Ночной разбор» (NA-2): любое подмножество его
    /// полей, null-поле — «не менять». Запись — через стор (per-owner замок + атомарная запись):
    /// одновременные PUT и оценка не рвут файл и не затирают друг друга; мердж ночного блока
    /// делается от состояния на момент записи, а не от чтения вне замка. Кривой merged-блок —
    /// 400 с кодом, файл не тронут. Отказ записи (диск отказал) — {code}, а не исключение наружу.
    /// Замер не возвращается: он читается GET после действия.
    /// LI-2: PUT, включивший ночной разбор, заводит вику — она нужна ночной службе. Заведение
    /// после успешного Mutate и только когда в итоговых настройках nightly.enabled == true:
    /// PUT класса или автоприменения вику не заводит, svc и выключенный флаг до заведения
    /// не доходят (порядок проверок не менялся).</summary>
    [HttpPut]
    public async Task<ActionResult<WikiSettingsDto>> Put([FromBody] WikiSettingsRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        // B2b: настройку ставит только человек — отказ по типу токена раньше состояния
        // (флага и валидации тела): он не зависит от того, включена ли функция.
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: настройки вики меняет только пользовательский токен" });
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return LlmWikiOff();
        if (req is null)
            return BadRequest(new { code = CodeNothingToChange, message = "тело запроса пустое" });

        // Чужая вики: явно названный чужой владелец — 403 (своё имя в запросе — то же, что его нет).
        if (req.OwnerId is not null && req.OwnerId != userId)
            return StatusCode(403, new { code = CodeForeign, message = "настройки относится к своей вики" });

        if (req.Class is not null && req.Enable is null)
            return BadRequest(new { code = CodeClassKey, message = "для класса нужно состояние: Enable" });
        if (req.Enable is not null && req.Class is null)
            return BadRequest(new { code = CodeClassKey, message = "состояние класса без класса не имеет цели" });
        if (req.Class is not null && !WikiSettingsStore.IsValidClassKey(req.Class))
            return BadRequest(new { code = CodeClassKey, message = $"класс «{req.Class}» не пары «источник × операция»" });
        // Пустой ночный блок (все поля null) намерения не имеет — это то же «менять нечего».
        if (req.AutoApplyEnabled is null && req.Class is null && req.Nightly?.HasAnyField() != true)
            return BadRequest(new { code = CodeNothingToChange, message = "менять нечего: ни выключателя, ни класса, ни ночного блока" });

        var now = DateTimeOffset.UtcNow;
        WikiSettingsStore.Settings next;
        try
        {
            next = WikiSettingsStore.Mutate(_dataDir, userId, current =>
            {
                var settings = current;
                if (req.AutoApplyEnabled is { } auto)
                    settings = settings with { AutoApplyEnabled = auto };
                if (req.Class is { } classKey)
                {
                    var classes = new List<WikiSettingsStore.ClassState>(settings.Classes);
                    var prev = classes.Find(c => c.Class == classKey);
                    classes.RemoveAll(c => c.Class == classKey);
                    if (req.Enable == true)
                        // «Снова применять само»: окно начинается заново с этого момента (мера 7).
                        classes.Add(new WikiSettingsStore.ClassState(classKey, false, null, null, now));
                    else
                        // Ручное выключение — тот же состояние, что автовыключение, с своей причиной.
                        // ResumedAt прежней записи сохраняется: окно обнуляет только возврат.
                        classes.Add(prev is not null
                            ? prev with { Disabled = true, Reason = "класс выключен человеком", DisabledAt = now }
                            : new WikiSettingsStore.ClassState(classKey, true, "класс выключен человеком", now, null));
                    settings = settings with { Classes = classes.OrderBy(c => c.Class, StringComparer.Ordinal).ToList() };
                }
                if (req.Nightly is { } nightlyReq)
                {
                    // Мердж от состояния на момент записи (под замком), при отсутствии блока —
                    // от умолчаний. Валидация — по merged-целому: границы окна сравниваются
                    // только в целом; кривой результат — 400 и ни байта в файл.
                    var cur = settings.NightlyOrDefaults();
                    var merged = cur with
                    {
                        Enabled = nightlyReq.Enabled ?? cur.Enabled,
                        WindowStartHour = nightlyReq.WindowStartHour ?? cur.WindowStartHour,
                        WindowEndHour = nightlyReq.WindowEndHour ?? cur.WindowEndHour,
                        MaxPackagesPerNight = nightlyReq.MaxPackagesPerNight ?? cur.MaxPackagesPerNight,
                        MaxPackagesPerSection = nightlyReq.MaxPackagesPerSection ?? cur.MaxPackagesPerSection,
                        MaxChatsPerSection = nightlyReq.MaxChatsPerSection ?? cur.MaxChatsPerSection,
                        InputTokenBudget = nightlyReq.InputTokenBudget ?? cur.InputTokenBudget,
                    };
                    if (!WikiSettingsStore.IsValidNightly(merged))
                        throw new InvalidNightlyException(
                            "ночной блок не целый: часы 0..23 и начало окна не равно его концу; " +
                            $"числа больше 0; пакетов не больше {WikiSettingsStore.MaxAllowedPackagesPerNight}; " +
                            $"токенов не больше {WikiSettingsStore.MaxAllowedInputTokenBudget}");
                    settings = settings with { Nightly = merged };
                }
                return settings;
            });
        }
        catch (InvalidNightlyException e)
        {
            return BadRequest(new { code = CodeNightly, message = e.Message });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Отказ записи (диск отказал — на разных ОС это IOException или UnauthorizedAccessException,
            // тот же приём, что у чтения файла в сторе): temp-файл WriteAtomic уже убрал. Ответ —
            // код, а не 500 без тела: вызывающий различает «файл не записан» и прочие свои ошибки.
            return StatusCode(500, new { code = CodeWriteFailed, message = "файл настроек не записан" });
        }

        // LI-2: включённая ночь — действие человека, после которого вики нужна сразу (ночная
        // служба её не заводит, она только разбирает). Идемпотентный EnsureInitializedAsync под
        // замком владельца: повторный PUT ничего не меняет, две одновременные точки (PUT +
        // analyze) дают ровно один шаблон. Класс и автоприменение сюда не доходят: в итоговых
        // настройках ночь остаётся выключенной.
        if (next.NightlyOrDefaults().Enabled)
            await _keeper.EnsureInitializedAsync(userId);

        // PUT итога ночи не несёт: он меняет настройки, а не читает прошлые проходы (шапка).
        return Ok(BuildDto(next, [], null, WikiNightlyService.IsRunning(WikiPaths.Root(_dataDir, userId))));
    }

    /// <summary>POST /api/wiki/settings/nightly/run — ручной запуск ночного разбора (WN-5,
    /// «Разобрать сейчас»): тот же проход, что у тика, но окно не проверяется и отметка «за дату
    /// уже пройдено» не мешает. 202 {started:true} — проход начат (работа идёт в фоне службы,
    /// её состояние видно по nightlyRunning в GET). 409 — по причине отказа: функция выключена
    /// (wiki_llm_wiki_off), ночь выключена (wiki_nightly_disabled), этот владелец уже проходит
    /// (wiki_nightly_running — тот же замок, что у тика), настройки нечитаемы
    /// (wiki_settings_invalid). 403 — сервисный токен (B2b: проход запускает только пользовательский
    /// токен, отказ раньше любого состояния). 503 — службы нет в этом составе: ответ общей формой
    /// проекта { error = … }, своего кода не заводим. Никакой записи настроек действие не делает.</summary>
    [HttpPost("nightly/run")]
    public ActionResult NightlyRun()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        // B2b: ручной запуск — действие человека, как PUT настроек: отказ по типу токена раньше
        // любого состояния (службы, флага, тумблера) — по ответу нельзя узнать, что из них есть.
        if (IsServiceToken)
            return StatusCode(403, new { error = "Сервисный JWT: ночной разбор запускает только пользовательский токен" });
        if (_nightly is null)
            return StatusCode(503, new { error = "Ночной разбор не зарегистрирован в этом составе" });
        return _nightly.TryStartManual(userId) switch
        {
            WikiNightlyService.ManualStart.Started => Accepted(new { started = true }),
            WikiNightlyService.ManualStart.LlmWikiOff => LlmWikiOff(),
            WikiNightlyService.ManualStart.Disabled => StatusCode(409, new { code = CodeNightlyDisabled, message = "ночной разбор выключен" }),
            WikiNightlyService.ManualStart.AlreadyRunning => StatusCode(409, new { code = CodeNightlyRunning, message = "разбор этой вики уже идёт" }),
            _ => StatusCode(409, new { code = CodeSettingsInvalid, message = "настройки нечитаемы: разбор не начат" }),
        };
    }

    /// <summary>Мerged-ночной блок кривой: сигнал функции мерджа (под замком, по состоянию на
    /// момент записи) в 400 действия. Файл при этом не тронут: исключение до записи.</summary>
    private sealed class InvalidNightlyException : Exception
    {
        public InvalidNightlyException(string message) : base(message) { }
    }

    private ObjectResult LlmWikiOff() =>
        StatusCode(409, new { code = CodeLlmWikiOff, message = "настройки не прочитаны: функция выключена" });

    // Строки класса — объединение состояния из settings и замера оценки: класс без замера
    // (выключен давно, активности нет) остаётся видимым; активный без записи — тоже.
    // lastNight — прочитанный итог ночи (GET) или null (PUT, и GET без читаемого файла).
    // nightlyRunning (WN-5) — идёт ли сейчас проход этой вики (состояние процесса, не файла).
    private static WikiSettingsDto BuildDto(WikiSettingsStore.Settings settings, IReadOnlyList<WikiClassHealth.ClassStat> stats,
        WikiNightlyService.NightResult? lastNight, bool nightlyRunning)
    {
        var night = settings.NightlyOrDefaults();
        var byClass = new Dictionary<string, WikiClassDto>(StringComparer.Ordinal);
        foreach (var c in settings.Classes)
        {
            if (!byClass.TryGetValue(c.Class, out var dto))
                byClass[c.Class] = dto = new WikiClassDto { Class = c.Class };
            dto.Disabled = c.Disabled;
            dto.Reason = c.Reason;
            dto.DisabledAt = c.DisabledAt;
            dto.ResumedAt = c.ResumedAt;
        }
        foreach (var s in stats)
        {
            if (!byClass.TryGetValue(s.Class, out var dto))
                byClass[s.Class] = dto = new WikiClassDto { Class = s.Class };
            dto.Disabled = s.Disabled;
            dto.Applied7 = s.Applied7; dto.Retracted7 = s.Retracted7; dto.Share7 = s.Share7;
            dto.Applied30 = s.Applied30; dto.Retracted30 = s.Retracted30; dto.Share30 = s.Share30;
        }
        return new WikiSettingsDto
        {
            AutoApplyEnabled = settings.AutoApplyEnabled,
            Classes = byClass.Values.OrderBy(c => c.Class, StringComparer.Ordinal).ToList(),
            // Блок ночи — всегда с подставленными умолчаниями: «блока нет» для фронта — то же,
            // что «ночь выключена», двух состояний в контракте быть не должно.
            Nightly = new WikiNightlyDto
            {
                Enabled = night.Enabled,
                WindowStartHour = night.WindowStartHour,
                WindowEndHour = night.WindowEndHour,
                MaxPackagesPerNight = night.MaxPackagesPerNight,
                MaxPackagesPerSection = night.MaxPackagesPerSection,
                MaxChatsPerSection = night.MaxChatsPerSection,
                InputTokenBudget = night.InputTokenBudget,
            },
            // Идёт ли сейчас проход этой вики — состояние процесса (static-замок службы), не файла.
            NightlyRunning = nightlyRunning,
            // Итог ночи — как есть из файла службы; читающий различает только null
            // («итога не было») и строку.
            LastNight = lastNight is null ? null : new WikiLastNightDto
            {
                Date = lastNight.Date,
                Selected = lastNight.Selected,
                Analyzed = lastNight.Analyzed,
                Applied = lastNight.Applied,
                ToInbox = lastNight.ToInbox,
                EstTokens = lastNight.EstTokens,
                Outcome = lastNight.Outcome,
                Rejected = lastNight.Rejected,
                FirstRejectReason = lastNight.FirstRejectReason,
                Manual = lastNight.Manual,
                // Судья (J-J4): те же числа, что в файле и в строке журнала. Поля отброшенных
                // операций (Dropped) и отказов ступени 2 в DTO не идут по прежнему правилу этого
                // блока: их адресат — журнал процесса, не карточка настроек.
                JudgeCalls = lastNight.JudgeCalls,
                JudgeCostUsd = lastNight.JudgeCostUsd,
                JudgeApplied = lastNight.JudgeApplied,
                JudgeRejected = lastNight.JudgeRejected,
                JudgeDeferred = lastNight.JudgeDeferred,
                CodeRejected = lastNight.CodeRejected,
            },
        };
    }
}

/// <summary>Настройки для блока «Применение» (макет M2): общий выключатель и строки классов
/// с долей откатов. Замер полей Applied/Retracted/Share — из GET-оценки; PUT возвращает
/// состояние настроек без замера (нули). Nightly — блок «Ночной разбор» (Ф2), всегда с
/// подставленными умолчаниями. LastNight — итог последней ночи (N7c): его несёт только GET;
/// null — «итога не было» (нет файла или он нечитаем).</summary>
public sealed class WikiSettingsDto
{
    public bool AutoApplyEnabled { get; set; }
    public List<WikiClassDto> Classes { get; set; } = [];
    public WikiNightlyDto Nightly { get; set; } = new();
    // WN-5: идёт ли сейчас проход ночного разбора этой вики (тот же замок, что у тика и ручки
    // ручного запуска): состояние процесса, а не файла; нужен кнопке «Разобрать сейчас».
    public bool NightlyRunning { get; set; }
    public WikiLastNightDto? LastNight { get; set; }
}

/// <summary>Итог последней ночи (N7c) — поле в точности файла keeper/nightly-last.json
/// (план N7b): дата прохода, счётчики и исход. Значения outcome — константы службы
/// (completed/budget/open_limit/window_closed/disabled_midway/error); неизвестное будущее
/// значение проходит как есть (тот же приём, что у фронта).</summary>
public sealed class WikiLastNightDto
{
    public string Date { get; set; } = "";
    public int Selected { get; set; }
    public int Analyzed { get; set; }
    public int Applied { get; set; }
    public int ToInbox { get; set; }
    public long EstTokens { get; set; }
    public string Outcome { get; set; } = "";
    // WN-2: отказанные разборы и причина первого из них — поле в точности нового файла
    // nightly-last.json; старый файл читается нулём/null (аддитивность, как у inputTokens).
    public int Rejected { get; set; }
    public string? FirstRejectReason { get; set; }
    // WN-5: проход был запущен кнопкой вручную (не ночным тиком); старый файл без поля
    // читается false (та же аддитивность).
    public bool Manual { get; set; }
    // J-J4: итог прохода облачного судья, запущенного в конце этого же прохода. Счётчики — по
    // операциям, не по пакетам; JudgeCostUsd — стоимость его вызовов (единственные деньги в
    // итоге). Старый файл без этих полей читается нулями (та же аддитивность).
    public int JudgeCalls { get; set; }
    public decimal JudgeCostUsd { get; set; }
    public int JudgeApplied { get; set; }
    public int JudgeRejected { get; set; }
    public int JudgeDeferred { get; set; }
    public int CodeRejected { get; set; }
}

/// <summary>Блок «Ночной разбор» (макет Ф2): тумблер, окно [WindowStartHour, WindowEndHour),
/// лимиты прохода и бюджет входных токенов — всегда целые, с умолчаниями вместо «блока нет».</summary>
public sealed class WikiNightlyDto
{
    public bool Enabled { get; set; }
    public int WindowStartHour { get; set; }
    public int WindowEndHour { get; set; }
    public int MaxPackagesPerNight { get; set; }
    public int MaxPackagesPerSection { get; set; }
    public int MaxChatsPerSection { get; set; }
    public int InputTokenBudget { get; set; }
}

public sealed class WikiClassDto
{
    public string Class { get; set; } = "";
    public bool Disabled { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? ResumedAt { get; set; }
    public int Applied7 { get; set; }
    public int Retracted7 { get; set; }
    public double Share7 { get; set; }
    public int Applied30 { get; set; }
    public int Retracted30 { get; set; }
    public double Share30 { get; set; }
}

/// <summary>Тело PUT: выключатель, класс и его состояние, блок «Ночной разбор» (частично:
/// null-поле — «не менять»). OwnerId — опциональная явная адресация («только своя вики» как
/// контракт): чужой владелец — 403, своё имя равно его отсутствию.</summary>
public sealed class WikiSettingsRequest
{
    public string? OwnerId { get; set; }
    public bool? AutoApplyEnabled { get; set; }
    public string? Class { get; set; }
    public bool? Enable { get; set; }
    public NightlyRequest? Nightly { get; set; }
}

/// <summary>Частичное обновление блока «Ночной разбор»: каждое поле опционально, целым блок
/// становится мерджем с состоянием на момент записи (или с умолчаниями, если блока ещё нет).</summary>
public sealed class NightlyRequest
{
    public bool? Enabled { get; set; }
    public int? WindowStartHour { get; set; }
    public int? WindowEndHour { get; set; }
    public int? MaxPackagesPerNight { get; set; }
    public int? MaxPackagesPerSection { get; set; }
    public int? MaxChatsPerSection { get; set; }
    public int? InputTokenBudget { get; set; }

    /// <summary>Блок с намерением: хоть одно поле задано. Пустой блок — не «сброс к умолчаниям»,
    /// а «менять нечего».</summary>
    public bool HasAnyField() =>
        Enabled is not null || WindowStartHour is not null || WindowEndHour is not null ||
        MaxPackagesPerNight is not null || MaxPackagesPerSection is not null ||
        MaxChatsPerSection is not null || InputTokenBudget is not null;
}
