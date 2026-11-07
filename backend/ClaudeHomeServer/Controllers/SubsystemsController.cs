using ClaudeHomeServer.Services.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Список включённых/отключённых подсистем для админских дашбордов и диагностики.
// Гейт `Subsystems:{Key}:Enabled` (дефолт true) — одноразовый: читается на старте
// в `SubsystemRegistration.AddSubsystems` и определяет, прошла подсистема в DI
// или была пропущена. Поэтому отдаём не только активные, но и задизейбленные —
// иначе админ не отличит «выключено намеренно» от «забыли подключить».
//
// Эндпоинт админский: лежит под `[Authorize]` (как и весь `/api/*`), плюс роль
// `admin` — список подсистем чувствителен к разведке (ключи = карта вертикалей
// продукта). Доступ читателю/обычному пользователю НЕ открываем, иначе
// `/api/auth/me` (где тот же список, но только ключи активных) был бы избыточен.
[ApiController]
[Route("api/admin/subsystems")]
[Authorize(Roles = "admin")]
public class SubsystemsController(
    SubsystemStateStore subsystems, SubsystemUsageStore usage, IConfiguration config) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<SubsystemInfo>> List()
    {
        return Ok(subsystems.Snapshot(config, usage.Snapshot(DateTimeOffset.UtcNow)));
    }

    // Запись тумблера панели «Компоненты» (S2c-1): пишет ТОЛЬКО Subsystems:{Key}:Enabled в
    // data/subsystems.json. Горячего применения нет: гейт читается один раз при регистрации
    // (ADR-014), поэтому изменение действует после перезапуска — это и отдаёт RestartRequired
    // в ответе. Файл подключён источником конфигурации после Local.json (Program.cs), поэтому
    // записанное панелью перекрывает ручную настройку.
    //
    // Путь записи — SubsystemOverridesFile.PathFor(хост-конфигурации): тот же резолв от того
    // же DataPath, что у источника на старте. Это и есть связка «пишу туда, читает оттуда»;
    // держится она пока Program.cs резолвит путь последним по финальному DataPath —
    // осторожнее с новым источником конфигурации или правкой DataPath после его AddJsonFile.
    [HttpPut("{key}")]
    public IActionResult Put(string key, SubsystemToggleRequest? body)
    {
        // Enabled nullable: отсутствие поля не трактуем как «выключить» — это ошибка клиента,
        // а не решение о подсистеме.
        if (body?.Enabled is null)
            return BadRequest(new { code = "enabled_required", message = "В теле запроса нет поля enabled (true|false)." });

        // Строка ядра (префикс core:) не имеет гейта вовсе: это не IAppSubsystem (RecordCore),
        // она всегда в процессе.
        if (SubsystemStateStore.IsCoreKey(key))
            return BadRequest(new { code = "core_subsystem", message = $"«{key}» — строка ядра: она не отключается." });

        var current = subsystems.Snapshot(config)
            .FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            return NotFound();

        if (!current.CanDisable)
            return BadRequest(new
            {
                code = "core_subsystem",
                message = $"«{key}» — ядро продукта требует эту вертикаль обязательной: отключить нельзя.",
            });

        try
        {
            SubsystemOverridesFile.WriteEnabled(SubsystemOverridesFile.PathFor(config), key, body.Enabled.Value);
        }
        catch (SubsystemOverridesFileException ex)
        {
            // 503, а не 500: причина — состояние файла в data/, и её показывает строка панели.
            // Файл при этом не перетёрт (чтение оборвалось до записи).
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "write_failed", message = ex.Message });
        }

        // Ответ отражает только что записанное значение, а не конфиг старта процесса:
        // reloadOnChange срабатывает асинхронно, и чтение конфига здесь могло бы вернуть
        // старое Enabled. Active — как в процессе (гейт одноразовый), RestartRequired —
        // честная разница между ними.
        return Ok(current with
        {
            Enabled = body.Enabled.Value,
            RestartRequired = body.Enabled.Value != current.Active,
        });
    }
}

// Тело PUT /api/admin/subsystems/{key}: { "enabled": true|false }.
public sealed record SubsystemToggleRequest(bool? Enabled);