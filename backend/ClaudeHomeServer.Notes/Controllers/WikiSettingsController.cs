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
// «Снова применять само». Образец — WikiController: владелец только из claim sub, чужая
// вики — 403, выключенный флаг llm-wiki — 409 (тот же код, что у линта).
//
// GET оценивает здоровье классов (WikiClassHealth) и возвращает настройки с замером: блок
// «Применение» обязан показывать текущую долю, а не память прошлого просмотра. Оценка
// идемпотентна и однопоточна на владельца; выключение класса и уведомление — её часть, а не
// действие человека. PUT ничего не переоценивает: он меняет настройки и возвращает их.
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
    private readonly string _dataDir;

    public WikiSettingsController(WikiClassHealth health, IFeatureFlagGate? flags, IConfiguration config)
    {
        _health = health;
        _flags = flags;
        // Каталог данных — как у всех сервисов (DataPath указывает на projects.json в dataDir).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    // Владелец — только из токена. [Authorize] — главный гейт; явная проверка — его копия
    // внутри действий: без токена действие отвечает 401 (контракт), а не падает с NRE на claim.
    private string? OwnerOrNull() =>
        User.Identity?.IsAuthenticated == true ? User.FindFirstValue(JwtRegisteredClaimNames.Sub) : null;

    private const string CodeForeign = "wiki_foreign";
    private const string CodeLlmWikiOff = "wiki_llm_wiki_off";
    private const string CodeClassKey = "wiki_class_key";
    private const string CodeNothingToChange = "wiki_settings_nothing_to_change";

    /// <summary>GET /api/wiki/settings — настройки своей вики с замером доли откатов по классам
    /// и источникам за 7 и 30 дней (мера 8). Побочный эффект — оценка здоровья: она же
    /// выключает перешагнувший порог класс (с уведомлением). Невалидный settings.json —
    /// умолчания в ответе, сигнала наружу нет: оценка поставила строку в _health.md.</summary>
    [HttpGet]
    public async Task<ActionResult<WikiSettingsDto>> Get()
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
        if (_flags?.IsEnabled(userId, FeatureFlagKeys.LlmWiki) != true)
            return LlmWikiOff();

        var outcome = await _health.EvaluateAsync(userId);
        var (settings, _) = WikiSettingsStore.Read(WikiPaths.Root(_dataDir, userId));
        var stats = outcome.State == WikiClassHealth.RunState.Ran ? outcome.Stats : [];
        return Ok(BuildDto(settings, stats));
    }

    /// <summary>PUT /api/wiki/settings — общий выключатель «Применять без ревью» и состояние
    /// класса: «Выключить» / «Снова применять само» (Enable=true ставит ResumedAt — окно
    /// отсчёта класса начинается заново, мера 7). Запись — через стор (per-owner замок +
    /// атомарная запись): одновременные PUT и оценка не рвут файл и не затирают друг друга.
    /// Замер не возвращается: он читается GET после действия.</summary>
    [HttpPut]
    public ActionResult<WikiSettingsDto> Put([FromBody] WikiSettingsRequest? req)
    {
        var userId = OwnerOrNull();
        if (userId is null) return Unauthorized();
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
        if (req.AutoApplyEnabled is null && req.Class is null)
            return BadRequest(new { code = CodeNothingToChange, message = "менять нечего: ни выключателя, ни класса" });

        var now = DateTimeOffset.UtcNow;
        var next = WikiSettingsStore.Mutate(_dataDir, userId, current =>
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
            return settings;
        });

        return Ok(BuildDto(next, []));
    }

    private ObjectResult LlmWikiOff() =>
        StatusCode(409, new { code = CodeLlmWikiOff, message = "настройки не прочитаны: функция выключена" });

    // Строки класса — объединение состояния из settings и замера оценки: класс без замера
    // (выключен давно, активности нет) остаётся видимым; активный без записи — тоже.
    private static WikiSettingsDto BuildDto(WikiSettingsStore.Settings settings, IReadOnlyList<WikiClassHealth.ClassStat> stats)
    {
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
        };
    }
}

/// <summary>Настройки для блока «Применение» (макет M2): общий выключатель и строки классов
/// с долей откатов. Замер полей Applied/Retracted/Share — из GET-оценки; PUT возвращает
/// состояние настроек без замера (нули).</summary>
public sealed class WikiSettingsDto
{
    public bool AutoApplyEnabled { get; set; }
    public List<WikiClassDto> Classes { get; set; } = [];
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

/// <summary>Тело PUT: выключатель, класс и его состояние. OwnerId — опциональная явная
/// адресация («только своя вики» как контракт): чужой владелец — 403, своё имя равно его
/// отсутствию.</summary>
public sealed class WikiSettingsRequest
{
    public string? OwnerId { get; set; }
    public bool? AutoApplyEnabled { get; set; }
    public string? Class { get; set; }
    public bool? Enable { get; set; }
}
