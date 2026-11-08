using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Backgrounds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Фон рабочего пространства проекта (ADR-008 §7): генерация, возврат к стандартному
/// и отдача собранного сервером тайла. Генерация и сброс — только владельцу проекта.
///
/// Подсистема backgrounds отключаемая (ADR-014, S2a-2): при Subsystems:backgrounds:Enabled=false
/// её сервис в контейнер не попадает, поэтому зависимость опциональная (= null), а все три
/// маршрута отвечают честный 503 с причиной вместо 500 активации.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.Platform, ProjectKey = "id")]
[ApiController]
[Authorize]
[Route("api/projects/{id}/background")]
[SubsystemUsage("backgrounds")]
public class ProjectBackgroundsController(
    ProjectManager projects,
    IProjectAccess access,
    // `= null` — обязательность для DI: без значения по умолчанию активация контроллера на
    // выключенной вертикали падала (500), а не отвечала гейтом (та же форма, что PluginsController)
    ProjectBackgroundService? backgrounds = null)
    : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    /// <summary>
    /// Гейт выключенной вертикали (ADR-014, S2a-2): null-зависимость = подсистема выключена
    /// (её сервис регистрируется только при открытом гейте). Тайл (GET tile.svg) отказывается
    /// наравне с генерацией: файл на диске мог остаться от прежней генерации, но показывать
    /// его как живой фон проекта на выключенной вертикали — ложь; 404 сказал бы «файла нет».
    /// </summary>
    private ActionResult BackgroundsDisabled() => StatusCode(503, new
    {
        reason = "backgrounds_disabled",
        error = "Подсистема фонов проекта выключена (Subsystems:backgrounds:Enabled=false)",
    });

    // Доступ к проекту — явный IProjectAccess.Resolve (тот же путь, что разрешил S0-4b для ручек без
    // атрибута: «[ProjectScope] / явный IProjectAccess.Resolve», см. ProjectScopeRestControllersTests и
    // CodeGraphController.cs:38-41). Здесь атрибут ставить нельзя: он фильтр действия и ответил бы
    // 404 на nonexistent id ДО тела действия, а на выключенной вертикали отказ обязан быть 503 с
    // причиной — раньше любого 404 (матрица S2a-2, SubsystemDisableMatrixTests). Права те же, что у
    // master: Write для генерации и сброса (участник Write не получает, см. ProjectAccessService),
    // Read для тайла. Чужой/несуществующий проект — 404, существование чужого не подтверждаем.
    // Осознанное расхождение с соседями на [ProjectScope]: редактору общего проекта (Read есть,
    // Write нет) они отвечают 403 «Нет права на запись», здесь — тоже 404. Единообразия ради
    // различать не стали: ответ остаётся безопасным (не подтверждает существование), а отдельная
    // ветка 403 в явном Resolve ничего не даёт. Закреплено ProjectBackgroundsEditorTests.
    private Project? Scoped(string id, ProjectRight right) =>
        access.Resolve(UserId, id, right) is null ? null : projects.GetById(id);

    private static object Payload(BackgroundResult r) => new
    {
        kind = ProjectBackgroundView.Name(r.Kind),
        tileVersion = r.TileVersion,
        suggestedColorKey = r.SuggestedColorKey,
        colorApplied = r.ColorApplied,
        failReason = r.FailReason,
    };

    [HttpPost("generate")]
    public async Task<ActionResult> Generate(string id, CancellationToken ct)
    {
        if (backgrounds is null) return BackgroundsDisabled();
        if (Scoped(id, ProjectRight.Write) is not { } project) return NotFound();

        return Ok(Payload(await backgrounds.GenerateAsync(project, ct)));
    }

    [HttpPost("reset")]
    public ActionResult Reset(string id)
    {
        if (backgrounds is null) return BackgroundsDisabled();
        if (Scoped(id, ProjectRight.Write) is null) return NotFound();

        return Ok(Payload(backgrounds.Reset(id)));
    }

    // Тайл маски. access_token в query — запрос идёт из CSS (mask-image), заголовок туда
    // браузер не поставит (приём GET {id}/icon). Параметр v в запросе — только cache-buster:
    // имя файла берётся из стора, из запроса путь не строится никогда.
    [HttpGet("tile.svg")]
    public IActionResult Tile(string id)
    {
        if (backgrounds is null) return BackgroundsDisabled();
        if (Scoped(id, ProjectRight.Read) is not { Background: { Kind: ProjectBackgroundKind.Generated, TileFile: { } file } })
            return NotFound();

        var full = Path.Combine(projects.BackgroundsDir, id, file);
        if (!System.IO.File.Exists(full)) return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        // Имя файла меняется при каждой генерации, поэтому кеш безопасно долгий
        Response.Headers.CacheControl = "private, max-age=604800, immutable";
        return PhysicalFile(full, "image/svg+xml");
    }
}
