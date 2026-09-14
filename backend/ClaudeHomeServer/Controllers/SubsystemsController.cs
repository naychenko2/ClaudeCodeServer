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
public class SubsystemsController(SubsystemStateStore subsystems, IConfiguration config) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<SubsystemInfo>> List()
    {
        return Ok(subsystems.Snapshot(config));
    }
}