using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// Гигиена карты проекта (CLAUDE.md). Три ручки по числу фаз:
///  • <c>scan</c> — факты сканера, мгновенно, без модели и без трат (волна 1).
///  • <c>review</c> — формулировки модели поверх тех же фактов, ход до минут (волна 2).
///  • <c>apply</c> — применить отмеченные механические правки к файлу (волна 4).
///
/// Отдельный контроллер, а не ручка в DocsController: гигиена карты — не часть
/// корпуса документации (корневой CLAUDE.md в область панели по умолчанию даже не
/// входит). <c>scan</c> открыт без гейта: ничего не меняет и работает у теста
/// волны 1 (план §11: «не обязательно, решает исполнитель»). <c>review</c> и
/// <c>apply</c> закрыты флагом — пока он выключен, для пользователя этой фичи
/// не существует (NotFound, не Forbid). <c>apply</c> пишет в CLAUDE.md, и открытая
/// ручка записи при выключенной фиче была бы лишним риском.
/// </summary>
[ProjectCapability(ProjectCapabilityArea.ServerContent, ProjectKey = "id")]
[ApiController]
[Authorize]
[Route("api/projects/{id}/map-hygiene")]
public class ProjectMapController(
    ProjectManager projects,
    ProjectMapScanner scanner,
    ProjectMapReviewService review,
    ProjectMapApplyService apply,
    FeatureFlagService flags) : ControllerBase
{
    // BaseSha — отпечаток карты с момента скана: по нему видно, что человек смотрит на
    // тот же файл, который сейчас разбирает сервер
    public record ReviewRequest(string? BaseSha);

    // Ids — предложения, ОТМЕЧЕННЫЕ человеком в том же отчёте: автоприменения нет ни на
    // одном пути, пустой список — штатный случай (применять нечего)
    public record ApplyRequest(string? BaseSha, IReadOnlyList<string>? Ids);

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Гейт фичи — на ручках записи (review, apply). scan не гейтится (план §11: «не
    // обязательно»), и тест волны 1 не предполагает флага — единообразие тут
    // спорит с поведением существующего теста
    private bool FeatureEnabled() =>
        flags.IsEnabled(UserId, FeatureFlagKeys.ProjectMapHygiene);

    // Чужой проект — 404, а не 403: подтверждать его существование незачем (как у соседей).
    // ct — RequestAborted: скан обходит дерево проекта целиком, и ушедший клиент не должен
    // оставлять обход работать
    [HttpGet("scan")]
    public ActionResult Scan(string id, CancellationToken ct)
    {
        var project = projects.GetById(id);
        if (project is null || project.OwnerId != UserId) return NotFound();

        return Ok(scanner.Scan(project.RootPath, ct));
    }

    /// <summary>
    /// Формулировки модели поверх фактов свежего скана.
    ///
    /// Отвечает ПОЛНЫМ отчётом — той же формой, что scan и поле scan в ответе apply, —
    /// а не тройкой «baseSha + modelNote + suggestions»: фронт кладёт ответ в то же
    /// состояние, что и скан, и шапка отчёта не остаётся без чисел.
    ///
    /// BaseSha сверяется ДО обращения к модели и отвечает 409 так же, как применение
    /// правок: ход идёт до минут, и за это время карту в общем дереве вполне могут
    /// дописать — тогда суждения приехали бы по новому составу поверх старых фактов на
    /// экране, то есть с другими id и с чекбоксами, тихо переставшими совпадать с
    /// находками. Отказ здесь дёшев: человек видит «файл изменился · Проверить заново».
    /// </summary>
    [HttpPost("review")]
    public async Task<ActionResult> Review(string id, [FromBody] ReviewRequest? body, CancellationToken ct)
    {
        // Гейт раньше владения: при выключенной фиче ответ одинаков для своего и чужого
        // проекта, и существование ручки наружу не подтверждается ничем
        if (!FeatureEnabled()) return NotFound();

        var project = projects.GetById(id);
        if (project is null || project.OwnerId != UserId) return NotFound();

        var report = scanner.Scan(project.RootPath, ct);
        // Карты нет вовсе (удалили или переименовали, пока отчёт был открыт) — тот же 409
        // и та же плашка: для человека случай неотличим от «карту подменили»
        if (report.BaseSha is null || !string.Equals(report.BaseSha, body?.BaseSha, StringComparison.Ordinal))
            return Conflict(new { error = "staleBaseSha", baseSha = report.BaseSha });

        return Ok(await review.ReviewAsync(report, UserId, ct));
    }

    /// <summary>
    /// Применить отмеченные человеком механические правки к карте (контракт записи Р10а).
    ///
    /// Отвечает честным отчётом, а не мнимой атомарностью: часть правок могла не найти
    /// свой якорь, и об этом человеку говорят прямо. Адреса файла в теле нет и не будет —
    /// предмет правки ровно один, корневой CLAUDE.md проекта (Р4).
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult> Apply(string id, [FromBody] ApplyRequest? body, CancellationToken ct)
    {
        // Гейт раньше владения — как на review: при выключенной фиче ответ одинаков для
        // своего и чужого проекта. Ручка ПИШЕТ в CLAUDE.md, и открытой при выключенной
        // фиче быть не должна ни на одном этапе
        if (!FeatureEnabled()) return NotFound();

        var project = projects.GetById(id);
        if (project is null || project.OwnerId != UserId) return NotFound();

        var result = await apply.ApplyAsync(project.RootPath, body?.BaseSha, body?.Ids ?? [], ct);
        // Отпечаток разошёлся (карту дописали или её не стало) — файл не тронут, человек
        // видит плашку «файл изменился после проверки · Проверить заново»
        if (result.Stale) return Conflict(new { error = "staleBaseSha", baseSha = result.NewSha });

        return Ok(new
        {
            applied = result.Applied,
            failed = result.Failed,
            newSha = result.NewSha,
            scan = result.Scan,
        });
    }
}
