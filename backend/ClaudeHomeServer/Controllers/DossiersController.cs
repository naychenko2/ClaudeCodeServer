using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.Dossiers;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Паспорта изменений: просмотр (ADR-004, этап 1), активный канал recall — поиск
// для MCP dossier_lookup и полная запись для dossier_get (этап 2 §5; изоляция —
// владелец проекта, сервисный JWT памяти резолвит того же владельца) и выгрузка
// в ветку ccs/dossiers/v1 (этап 3). Всё, кроме List, — за флагом change-dossiers-recall.
[ApiController]
[Authorize]
[Route("api/projects")]
public class DossiersController(ProjectManager projects, DossierStore store,
    CodeGraphService graphs, DossierRecallService recall, FeatureFlagService flags,
    GitService git, SessionManager sessions, UserStore users, InstanceSecretsProvider secrets,
    DossierDiscussionService discussions, DossierCaptureState captureState) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Экспортёр паспортов не в DI (Program.cs — вне файлов этой волны): собираем сами
    // из инжектируемых синглтонов. Объект тонкий, состояния не держит — ок per-request.
    private DossierGitExporter Exporter => new(sessions, store, git, secrets, discussions);

    // Импортёр — тот же паттерн: тонкий объект над синглтонами, per-request
    private DossierImporter Importer => new(store, git, secrets);

    // Гейт автовыгрузки — тот же паттерн: опрашивается статусом выгрузки (autoExport)
    private DossierAutoExportGate AutoExportGate => new(projects, git, captureState);

    // Чужой проект — 404, а не 403: подтверждать его существование незачему
    private Project? Owned(string id)
    {
        var p = projects.GetById(id);
        return p is null || p.OwnerId != UserId ? null : p;
    }

    // Окно метрики охвата (спринт «История решений», блок В): знаменатель — коммиты
    // за 7 суток. Отдаётся наружу в coverage.periodDays — долю охвата фронт считает сам.
    private const int CoveragePeriodDays = 7;

    [HttpGet("{id}/dossiers")]
    public async Task<IActionResult> List(string id, [FromQuery] string? file,
        [FromQuery] string? symbol, [FromQuery] string? commit, CancellationToken ct)
    {
        var p = Owned(id);
        if (p is null) return NotFound();

        var result = string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(symbol) && string.IsNullOrWhiteSpace(commit)
            ? store.List(UserId, id).OrderByDescending(d => d.CommittedAt).ToList()
            : store.Find(UserId, id, file, symbol, commit);

        // Метрика охвата: коммиты окна против числа паспортов в выдаче. При файловом
        // фильтре знаменатель сужается до истории одного файла (--follow), символ и
        // коммит знаменатель не трогают. Сбой git метрику обнуляет, листинг не роняет.
        var commits = await git.CountRecentCommitsAsync(p.OwnerId, p.RootPath, CoveragePeriodDays, file, ct);

        // Символы в досье якорятся по снимку кодографа (DossierCaptureService.AnchorAsync):
        // пока граф корня не построен, свежие досье остаются на файловом уровне. Сигналим
        // фронту тем же заголовком, что и CodeGraphController на «граф строится» — дешёвым
        // mtime-чеком (GetCacheSignature, без загрузки graph.json). Заголовок аддитивен:
        // фронт, не использующий его, ничего не замечает.
        if (graphs.GetCacheSignature(p.RootPath) is null)
            Response.Headers["X-CodeGraph-Building"] = "true";

        // Контракт ответа: { entries, coverage } (раньше — голый массив записей).
        // Числитель охвата — уникальные коммиты того же окна, что и знаменатель:
        // (1) свой и импортированный паспорта одного sha — пара записей об одном
        // коммите, а не два охваченных; (2) паспорта старше окна не считаются —
        // иначе числитель (вся история стора) перерастал знаменатель (7 дней git).
        var since = DateTimeOffset.UtcNow.AddDays(-CoveragePeriodDays);
        return Ok(new
        {
            entries = result,
            coverage = new
            {
                periodDays = CoveragePeriodDays,
                commits,
                dossiers = result.Where(d => d.CommittedAt >= since)
                    .Select(d => d.CommitSha)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            },
        });
    }

    // Поиск паспортов для MCP dossier_lookup (этап 2, ADR-004 §5): по пути, символу или
    // свободному тексту — в том числе archived (в пассивный канал они не идут, но найти
    // их можно). Один из фильтров обязателен; без фильтров — свежие сверху (лимит).
    [HttpGet("{id}/dossiers/lookup")]
    public IActionResult Lookup(string id, [FromQuery] string? path, [FromQuery] string? symbol,
        [FromQuery] string? query, [FromQuery] int limit = 20)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        var capped = Math.Clamp(limit, 1, 50);
        return Ok(recall.Lookup(UserId, id, path, symbol, query, capped));
    }

    // Полная запись паспорта для MCP dossier_get (этап 2): по id, только свой проект.
    [HttpGet("{id}/dossiers/{dossierId}")]
    public IActionResult GetById(string id, string dossierId)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        return store.GetById(UserId, id, dossierId) is { } dossier ? Ok(dossier) : NotFound();
    }

    // ---------- Выгрузка в ветку ccs/dossiers/v1 (ADR-004 §6) ----------

    // Готовность проекта к выгрузке: isGitRepo гейтит кнопку в UI, sharedFolder —
    // предупреждение «после отправки ветки историю решений увидит и второй владелец
    // папки». Экспорт при общей папке НЕ блокируется: ветка пишется в общий репозиторий
    // осознанно, признак нужен только для честного диалога подтверждения.
    // hasDossierBranch — наличие локальной refs/heads/ccs/dossiers/v1 (один git-вызов):
    // им гейтится кнопка «Загрузить» (импорт), пока ветки нет.
    // autoExport — причина гейта АВТОвыгрузки (DossierAutoExportGate): после сужения
    // фона («ветка заведомо наша») общая подсказка «выгружается само» врала бы при
    // чужом tip / одной origin-ветке / общей папке — панель выбирает текст по причине.
    // null, когда проект не git-репозиторий (подсказка тогда всё равно скрыта).
    [HttpGet("{id}/dossiers/export/status")]
    public async Task<IActionResult> ExportStatus(string id, CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChangeDossiersRecall)) return NotFound();
        var p = Owned(id);
        if (p is null) return NotFound();

        var isGitRepo = GitService.IsGitRepo(p.RootPath);
        return Ok(new
        {
            isGitRepo,
            sharedFolder = projects.GetByRootPath(p.RootPath).Any(x => x.OwnerId != p.OwnerId),
            hasDossierBranch = await git.HasDossiersBranchAsync(p.OwnerId, p.RootPath, ct),
            autoExport = isGitRepo ? await AutoExportGate.ClassifyAsync(UserId, p, ct) : null,
        });
    }

    // Запуск выгрузки. Ветка остаётся локальной: push отсюда недоступен, публикация —
    // отдельный POST /export/push по явной команде пользователя.
    [HttpPost("{id}/dossiers/export")]
    public async Task<IActionResult> Export(string id, CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChangeDossiersRecall)) return NotFound();
        var p = Owned(id);
        if (p is null) return NotFound();

        try
        {
            var result = await Exporter.ExportAsync(UserId, p, ct: ct);
            // Tip ручной выгрузки — тоже «наш»: тик автоимпорта не должен завозить
            // содержимое только что выгруженной ветки обратно как «чужое» (та же
            // петля, что у автовыгрузки — разбор 23.08)
            captureState.MarkOwnTip(UserId, p.Id, result.CommitSha);
            return Ok(new
            {
                status = result.Committed ? "exported" : "nothingToExport",
                count = result.Exported,
            });
        }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Ручная отправка ветки паспортов в origin — единственное место кода экспорта,
    // откуда вызывается push (автопуша нет и не будет, ADR-004 §6). Креды Forgejo
    // владельца — как у обычной публикации в GitController; без них git пробует
    // анонимно/системным helper'ом (публичные remote так тоже работают).
    [HttpPost("{id}/dossiers/export/push")]
    public async Task<IActionResult> Push(string id, CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChangeDossiersRecall)) return NotFound();
        var p = Owned(id);
        if (p is null) return NotFound();

        var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
        var creds = owner is { ForgejoUsername: { Length: > 0 } u, ForgejoToken: { Length: > 0 } t }
            ? new GitCredentials(u, t)
            : null;
        try
        {
            await git.PushDossiersBranchAsync(p.OwnerId, p.RootPath, creds, ct);
            // Отправленная ветка — наша по определению (кнопку жмёт владелец): фиксируем
            // её tip, чтобы тик автоимпорта не завёз содержимое ветки обратно как «чужое».
            // Push tip не двигает, но ветка могла быть создана до включения автоимпорта.
            var tip = await git.GetDossiersTipAsync(p.OwnerId, p.RootPath, ct);
            captureState.MarkOwnTip(UserId, p.Id, tip?.CommitSha);
            return Ok(new { pushed = true });
        }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // ---------- Импорт из ветки ccs/dossiers/v1 (этап 4, ADR-004 §6) ----------

    // Ручной импорт «Историй решений»: читает ветку (локальную либо origin) read-only
    // plumbing-командами и кладёт паспорта в стор с пометкой происхождения (origin=imported,
    // автор и ветка-источник — в полях записи, видны в GET /dossiers). Повторный вызов того
    // же состояния ветки — no-op: существующие записи не меняются, свой паспорт по тому же
    // sha не перезаписывается никогда (импортированный живёт рядом отдельной записью).
    [HttpPost("{id}/dossiers/import")]
    public async Task<IActionResult> Import(string id, CancellationToken ct)
    {
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ChangeDossiersRecall)) return NotFound();
        var p = Owned(id);
        if (p is null) return NotFound();

        try
        {
            var result = await Importer.ImportAsync(UserId, p, ct);
            return Ok(new
            {
                status = !result.BranchFound ? "noBranch"
                    : result.Added > 0 ? "imported" : "nothingToImport",
                added = result.Added,
                skipped = result.Skipped,
            });
        }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }
}
