using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Специальности персон и настройки к ним: каталог специальностей с подписями и
// шаблонами прав плюс настройки инстанса (один слой) с именованными пресетами правил
// выбора модели. Специальности — общие для всех пользователей: читать их может любой,
// менять — только админ ([Authorize(Roles = "admin")] на каждой записи). Слоёв
// «пользователь» и «личный» больше нет (v5 стора).
[ApiController]
[Authorize]
[Route("api/specialties")]
public class SpecialtiesController(
    SpecialtySettingsStore settings,
    FallbackSettingsStore fallback,
    PersonaManager personas) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Каталог специальностей: ключ (wire-значение), подпись, описание (для карточек
    // панели «Инструкции для роли»), семейство исполнителя, эффективный шаблон прав
    // вызывающего (настройки поверх дефолтов кода) и дефолтные значок с цветом из
    // каталога (только для показа, не настраиваются). Три исполнительские
    // SpecialtyCatalog отдаёт с подписями: «Исполнитель (универсальный)»,
    // «Исполнитель (бэкенд)», «Исполнитель (фронтенд)».
    [HttpGet]
    public IActionResult List()
    {
        return Ok(SpecialtyCatalog.All.Select(e => new
        {
            key = e.Key,
            label = e.Label,
            description = e.Description,
            executorFamily = e.ExecutorFamily,
            template = settings.EffectiveTemplate(UserId, e.Specialty) is { } t
                ? new { access = t.Access, tools = t.Tools, disallowedTools = t.DisallowedTools }
                : null,
            icon = e.Icon,
            color = e.Color,
        }));
    }

    // Каталог секций промптов: состав секций задаёт система, здесь — их метаданные и
    // дефолты кода по каждой специальности (включённость и типовой текст — пресет
    // «Типовой текст…» и база для клиентского резолва поверх слоёв). Профиль типовых
    // умений роли отдаётся ЭФФЕКТИВНЫМ для вызывающего (owner → user → global → дефолт
    // кода): его фронт не резолвит сам, и именно с ним сверяется счётчик «не хватает
    // типовых умений: N» и кнопка «Применить типовые» (материализация идёт тем же
    // резолвом). Поэтому перечитывание каталога после сохранения роли видит свежий
    // профиль. Секции — дефолты кода: их резолв по слоям фронт делает сам (слои
    // отдаются как есть в settings).
    [HttpGet("prompt-sections")]
    public IActionResult PromptSectionsCatalog()
    {
        return Ok(new
        {
            textLimit = SpecialtyPromptPresets.SectionTextLimit,
            sections = SpecialtyPromptPresets.Sections.Select(s => new
            {
                id = s.Id,
                label = s.Label,
                description = s.Description,
            }),
            specialties = SpecialtyCatalog.All
                .Where(e => e.Specialty != PersonaSpecialty.None)
                .ToDictionary(
                    e => e.Key,
                    e => new
                    {
                        sections = SpecialtyPromptPresets.Sections.Select(s => new
                        {
                            id = s.Id,
                            enabled = SpecialtyPromptPresets.DefaultEnabled(s.Id, e.Specialty),
                            text = SpecialtyPromptPresets.DefaultText(s.Id, e.Specialty),
                        }),
                        defaultBindings = settings.EffectiveDefaultBindings(UserId, e.Specialty)
                            .Select(b => new
                            {
                                type = b.Type,
                                mode = b.Mode,
                                condition = b.Condition,
                                skillName = b.SkillName,
                            }),
                    }),
        });
    }

    // Настройки специальностей и пресетов-цепочек: ОДИН слой (общий для инстанса) плюс
    // список пресетов. Поле scope у пресета сохранено ради контракта фронта, значение
    // теперь всегда global.
    //
    // Контракт v2 (ADR-007): пресет хранит упорядоченную цепочку steps (не rules);
    // у специальности — матрица моделей по уровням (tierStrong/Medium/Weak) + defaultTier;
    // у слоя — defaultSpecialty («любая специальность»). Слой сериализуется как есть
    // (SpecialtySettingsLayer), пресеты разворачиваются в плоский список со steps.
    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        return Ok(new
        {
            version = SpecialtySettingsStore.FormatVersion,
            // Эффективный бюджет подмен цепочки хода (ADR-007 §4): per-owner → global →
            // дефолт, значение клампится в 1..HardMaxSubstitutions (FallbackSettingsStore).
            // Фронт приглушает шаги пресета за этим пределом как «обычно не используется» —
            // без этого числа UI хардкодил дефолт и считал приглушение от неверного потолка.
            maxSubstitutions = fallback.ResolveMaxSubstitutions(UserId),
            global = settings.Snapshot.Global,
            presets = settings.EffectivePresetsWithScope(UserId).Select(e => new
            {
                id = e.Preset.Id,
                name = e.Preset.Name,
                description = e.Preset.Description,
                steps = e.Preset.Steps,
                scope = e.Scope,
            }),
        });
    }

    // Замена глобального слоя — только админ (общие значения инстанса).
    [HttpPut("settings/global")]
    [Authorize(Roles = "admin")]
    public IActionResult SetGlobalSettings([FromBody] SpecialtySettingsLayer layer)
    {
        if (layer is null) return BadRequest(new { error = "Не задан слой настроек" });
        if (settings.SetGlobal(layer) is { } error) return BadRequest(new { error });
        return Ok(new { global = settings.Snapshot.Global });
    }

    // Короткого маршрута PUT /settings больше нет: он был синонимом записи личного слоя,
    // и оставленный «для совместимости с фронтом» стал бы единственной незакрытой ролью
    // точкой записи per-owner (ADR-012). Запись — только settings/global.

    // --- Бюджет подмен цепочки хода (MaxSubstitutions) ---
    //
    // Раньше был только GET (поле maxSubstitutions в settings) — теперь и запись.
    // Значение клампится в 1..HardMaxSubstitutions стором; null = снять настройку.
    // Личный бюджет подмен снят вместе со слоями специальностей: осталось общее
    // значение инстанса (сам FallbackSettingsStore слои поддерживает — они нужны
    // другим местам, здесь мы их просто не выставляем).

    public class FallbackMaxRequest
    {
        public int? MaxSubstitutions { get; set; }
    }

    // Глобальный бюджет подмен — только админ (общее значение инстанса).
    [HttpPut("settings/fallback/global")]
    [Authorize(Roles = "admin")]
    public IActionResult SetGlobalMaxSubstitutions([FromBody] FallbackMaxRequest? body)
    {
        if (fallback.SetGlobal(body?.MaxSubstitutions) is { } error) return BadRequest(new { error });
        return Ok(new { maxSubstitutions = fallback.Snapshot.Global.MaxSubstitutions });
    }

    // --- Сброс настроек моделей к дефолтам кода ---
    //
    // Слой один, поэтому сброс один. Предпросмотр НЕ гейтится ролью осознанно —
    // GetSettings и так отдаёт весь слой любому, секретом счёт по нему не является.
    // key — ключ одной специальности («any» — «Любая специальность»); не задан — весь слой.

    public class SpecialtyResetRequest
    {
        public string? Key { get; set; }
    }

    [HttpGet("settings/reset/global/preview")]
    public IActionResult PreviewResetGlobal([FromQuery] string? key) => Reset(key, apply: false);

    [HttpPost("settings/reset/global")]
    [Authorize(Roles = "admin")]
    public IActionResult ResetGlobal([FromBody] SpecialtyResetRequest? body = null) =>
        Reset(body?.Key, apply: true);

    // --- Сброс уровней у своих персон ---
    //
    // Маршрут reset/owner пережил снятие слоёв, но сузился: специальностей он больше не
    // касается вовсе (общий слой чистит только админ через reset/global), а вторая его
    // половина — «сбросить уровни у МОИХ персон» — к слоям отношения не имела и осталась:
    // персона была и остаётся per-owner сущностью (ADR-012, открытый вопрос — решение
    // архитектора). Ответ вырожден в персон.

    [HttpGet("settings/reset/owner/preview")]
    public IActionResult PreviewResetPersonaTiers() => ResetPersonaTiers(apply: false);

    [HttpPost("settings/reset/owner")]
    public IActionResult ResetOwner() => ResetPersonaTiers(apply: true);

    private IActionResult ResetPersonaTiers(bool apply)
    {
        var touched = personas.ResetTierMatrices(UserId, apply);
        return Ok(new
        {
            personas = touched.Count,
            personaNames = touched.Select(p => p.Name),
        });
    }

    // Общий счёт предпросмотра и сброса: одна и та же ветка, apply решает, писать ли.
    private IActionResult Reset(string? key, bool apply)
    {
        string? resolvedKey = null;
        if (key is not null && !TryNormalizeKey(key, out resolvedKey))
            return BadRequest(new { error = $"Неизвестная специальность: {key}" });

        var result = settings.ResetModelSettings(resolvedKey, apply);
        return Ok(new
        {
            specialties = result.Changed,
            shadowed = result.Shadowed,
        });
    }

    // Ключ точечного сброса в канонический вид каталога; «any» — «Любая специальность».
    // Пустой ключ ключом не считается (ValidateLayer в этот путь не заходит).
    private static bool TryNormalizeKey(string key, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (SpecialtySettingsStore.IsAnyKey(key.Trim()))
        {
            normalized = SpecialtyCatalog.AnySpecialtyKey;
            return true;
        }
        if (!SpecialtyCatalog.TryGetByKey(key, out var entry)) return false;
        normalized = entry.Key;
        return true;
    }
}
