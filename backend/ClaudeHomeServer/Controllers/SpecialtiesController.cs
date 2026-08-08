using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Специальности персон и настройки к ним: каталог специальностей с подписями
// и эффективными шаблонами прав, стор настроек (глобальные значения + per-owner
// переопределение) и именованные пресеты правил выбора модели.
[ApiController]
[Authorize]
[Route("api/specialties")]
public class SpecialtiesController(
    SpecialtySettingsStore settings,
    FallbackSettingsStore fallback,
    PersonaManager personas) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Каталог специальностей: ключ (wire-значение), подпись, семейство исполнителя
    // и эффективный шаблон прав вызывающего (настройки поверх дефолтов кода).
    // Три исполнительские SpecialtyCatalog отдаёт с подписями:
    // «Исполнитель (универсальный)», «Исполнитель (бэкенд)», «Исполнитель (фронтенд)».
    [HttpGet]
    public IActionResult List()
    {
        return Ok(SpecialtyCatalog.All.Select(e => new
        {
            key = e.Key,
            label = e.Label,
            executorFamily = e.ExecutorFamily,
            template = settings.EffectiveTemplate(UserId, e.Specialty) is { } t
                ? new { access = t.Access, tools = t.Tools, disallowedTools = t.DisallowedTools }
                : null,
        }));
    }

    // Настройки специальностей и пресетов-цепочек: глобальный слой и личный слой
    // вызывающего (фронт рендерит оба уровня; эффективное значение — см. List/шаблоны)
    // плюс объединённый список пресетов с признаком слоя: набор один, различие между
    // «общий» и «мой» — в поле scope (личные впереди, в порядке резолва).
    //
    // Контракт v2 (ADR-007): пресет хранит упорядоченную цепочку steps (не rules);
    // у специальности — матрица моделей по уровням (tierStrong/Medium/Weak) + defaultTier;
    // у слоя — defaultSpecialty («любая специальность»). Слои сериализуются как есть
    // (SpecialtySettingsLayer), пресеты разворачиваются в плоский список со steps.
    [HttpGet("settings")]
    public IActionResult GetSettings()
    {
        var file = settings.Snapshot;
        return Ok(new
        {
            version = SpecialtySettingsStore.FormatVersion,
            // Эффективный бюджет подмен цепочки хода (ADR-007 §4): per-owner → global →
            // дефолт, значение клампится в 1..HardMaxSubstitutions (FallbackSettingsStore).
            // Фронт приглушает шаги пресета за этим пределом как «обычно не используется» —
            // без этого числа UI хардкодил дефолт и считал приглушение от неверного потолка.
            maxSubstitutions = fallback.ResolveMaxSubstitutions(UserId),
            global = file.Global,
            owner = file.Owners.GetValueOrDefault(UserId) ?? new SpecialtySettingsLayer(),
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

    // Замена личного слоя вызывающего. Пустой слой снимает переопределения
    // (владелец возвращается к глобальным значениям). Маршрут без суффикса — для
    // обратной совместимости с фронтом, который уже пишет сюда: см. api.ts saveOwnerLayer.
    [HttpPut("settings")]
    public IActionResult SetOwnerSettings([FromBody] SpecialtySettingsLayer layer) => SetOwnerLayer(layer);

    // Явный маршрут per-owner слоя (settings/owner), симметричный settings/global:
    // QA при попытке «прибрать owner-пресет backendExecutor → kimi-k3» стучался сюда
    // и получал 404 — теперь маршрут есть. Per-owner изоляция: UserId берётся из JWT,
    // подменить чужой слой нельзя. Семантика идентична короткому маршруту.
    [HttpPut("settings/owner")]
    public IActionResult SetOwnerLayer([FromBody] SpecialtySettingsLayer layer)
    {
        if (layer is null) return BadRequest(new { error = "Не задан слой настроек" });
        if (settings.SetOwner(UserId, layer) is { } error) return BadRequest(new { error });
        return Ok(new { owner = settings.Snapshot.Owners.GetValueOrDefault(UserId) ?? new SpecialtySettingsLayer() });
    }

    // --- Сброс настроек моделей к наследованию (scope — в ПУТИ) ---
    //
    // Scope нельзя брать из тела: атрибут роли на значение тела не повесить, а глобальный
    // слой правит только админ. Предпросмотр глобального scope НЕ гейтится ролью осознанно —
    // GetSettings и так отдаёт весь global любому, секретом счёт по нему не является.
    // key — ключ одной специальности («any» — «Любая специальность»); не задан — весь слой.

    public class SpecialtyResetRequest
    {
        public string? Key { get; set; }
    }

    [HttpGet("settings/reset/owner/preview")]
    public IActionResult PreviewResetOwner([FromQuery] string? key) => Reset(UserId, key, apply: false);

    [HttpPost("settings/reset/owner")]
    public IActionResult ResetOwner([FromBody] SpecialtyResetRequest? body = null) =>
        Reset(UserId, body?.Key, apply: true);

    [HttpGet("settings/reset/global/preview")]
    public IActionResult PreviewResetGlobal([FromQuery] string? key) => Reset(null, key, apply: false);

    [HttpPost("settings/reset/global")]
    [Authorize(Roles = "admin")]
    public IActionResult ResetGlobal([FromBody] SpecialtyResetRequest? body = null) =>
        Reset(null, body?.Key, apply: true);

    // Общий счёт предпросмотра и сброса: одна и та же ветка, apply решает, писать ли.
    // Персоны — сущность per-owner, «общих» персон нет: их уровни чистятся только у scope=owner.
    private IActionResult Reset(string? ownerId, string? key, bool apply)
    {
        string? resolvedKey = null;
        if (key is not null && !TryNormalizeKey(key, out resolvedKey))
            return BadRequest(new { error = $"Неизвестная специальность: {key}" });

        var result = settings.ResetModelSettings(ownerId, resolvedKey, apply);
        // Точечный сброс одной специальности персон не касается — он про строку матрицы
        IReadOnlyList<Persona> touched = ownerId is not null && key is null
            ? personas.ResetTierMatrices(ownerId, apply)
            : [];
        return Ok(new
        {
            specialties = result.Changed,
            shadowed = result.Shadowed,
            personas = touched.Count,
            personaNames = touched.Select(p => p.Name),
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
