using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects/{projectId}/git")]
public class GitController(GitService git, GitServerService gitServer, GitAiService gitAi, ProjectManager projects, UserStore users, IHubContext<SessionHub> hub) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Креды Forgejo владельца проекта — для push/pull/fetch по HTTP (null — без кред,
    // git попробует анонимно/системный helper, публичные remote так тоже работают)
    private GitCredentials? CredsFor(Models.Project p)
    {
        var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
        return owner is { ForgejoUsername: { Length: > 0 } u, ForgejoToken: { Length: > 0 } t }
            ? new GitCredentials(u, t)
            : null;
    }

    // Проект текущего пользователя; чужой/несуществующий → 404 (как в FilesController)
    private Models.Project GetProject(string projectId)
    {
        var p = projects.GetById(projectId);
        if (p is null || p.OwnerId != UserId)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    // Владелец проекта = резолвит среду исполнения git (local/container).
    // OwnerId nullable по модели; ForOwner(null) корректно даёт локальную среду.
    private static string? Owner(Models.Project p) => p.OwnerId;

    // Эффективный промпт AI-генерации сообщения коммита: проектный override → глобальный → null (дефолт)
    private string? EffectiveCommitPrompt(Models.Project p)
    {
        if (!string.IsNullOrWhiteSpace(p.CommitPromptOverride)) return p.CommitPromptOverride;
        var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
        return string.IsNullOrWhiteSpace(owner?.GitCommitPrompt) ? null : owner!.GitCommitPrompt;
    }

    private Task NotifyChanged(string projectId) =>
        hub.Clients.Group("user_" + UserId).SendAsync("message", new GitStatusChangedMessage(projectId));

    [HttpGet("status")]
    public async Task<IActionResult> Status(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.StatusAsync(Owner(p), p.RootPath, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("diff")]
    public async Task<IActionResult> Diff(string projectId, [FromQuery] string path, [FromQuery] bool staged = false, CancellationToken ct = default)
    {
        try
        {
            var p = GetProject(projectId);
            var diff = await git.DiffFileAsync(Owner(p), p.RootPath, path, staged, ct);
            return Ok(new { diff });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("log")]
    public async Task<IActionResult> Log(string projectId, [FromQuery] int limit = 100, [FromQuery] string? branch = null, CancellationToken ct = default)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.LogAsync(Owner(p), p.RootPath, limit, branch, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Незапушенные коммиты (впереди upstream) — стек скоупов в панели «Изменения»
    [HttpGet("unpushed")]
    public async Task<IActionResult> Unpushed(string projectId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.UnpushedLogAsync(Owner(p), p.RootPath, limit, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("commits/{sha}")]
    public async Task<IActionResult> CommitDetail(string projectId, string sha, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var detail = await git.CommitDetailAsync(Owner(p), p.RootPath, sha, ct);
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("commits/{sha}/diff")]
    public async Task<IActionResult> CommitFileDiff(string projectId, string sha, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var diff = await git.CommitFileDiffAsync(Owner(p), p.RootPath, sha, path, ct);
            return Ok(new { diff });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpGet("branches")]
    public async Task<IActionResult> Branches(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.BranchesAsync(Owner(p), p.RootPath, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("stage")]
    public Task<IActionResult> Stage(string projectId, [FromBody] GitPathRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.StageAsync(Owner(p), p.RootPath, body.Path, ct));

    [HttpPost("unstage")]
    public Task<IActionResult> Unstage(string projectId, [FromBody] GitPathRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.UnstageAsync(Owner(p), p.RootPath, body.Path, ct));

    [HttpPost("stage-all")]
    public Task<IActionResult> StageAll(string projectId, CancellationToken ct) =>
        Mutate(projectId, (p) => git.StageAllAsync(Owner(p), p.RootPath, ct));

    // Откат правок файла к HEAD — теряет несохранённые изменения (подтверждение на фронте)
    [HttpPost("discard")]
    public Task<IActionResult> Discard(string projectId, [FromBody] GitPathRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.DiscardAsync(Owner(p), p.RootPath, body.Path, ct));

    // Откат ВСЕХ изменений рабочего дерева (опасно — фронт гейтит подтверждением)
    [HttpPost("discard-all")]
    public Task<IActionResult> DiscardAll(string projectId, CancellationToken ct) =>
        Mutate(projectId, (p) => git.DiscardAllAsync(Owner(p), p.RootPath, ct));

    // Зернистый stage: патч хунка/выбранных строк (синтезирует фронт)
    [HttpPost("stage-hunk")]
    public Task<IActionResult> StageHunk(string projectId, [FromBody] GitPatchRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.StageHunkAsync(Owner(p), p.RootPath, body.Patch, ct));

    [HttpPost("unstage-hunk")]
    public Task<IActionResult> UnstageHunk(string projectId, [FromBody] GitPatchRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.UnstageHunkAsync(Owner(p), p.RootPath, body.Patch, ct));

    // ---------- Stash ----------

    [HttpGet("stash")]
    public async Task<IActionResult> StashList(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.StashListAsync(Owner(p), p.RootPath, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Файлы отложенного для просмотра в верхней зоне панели «Изменения» (как у коммита)
    [HttpGet("stash/{index:int}")]
    public async Task<IActionResult> StashShow(string projectId, int index, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(new { files = await git.StashShowAsync(Owner(p), p.RootPath, index, ct) });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("stash")]
    public Task<IActionResult> StashPush(string projectId, [FromBody] GitStashRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.StashPushAsync(Owner(p), p.RootPath, body.Message, ct));

    [HttpPost("stash/{index:int}/pop")]
    public Task<IActionResult> StashPop(string projectId, int index, CancellationToken ct) =>
        Mutate(projectId, (p) => git.StashPopAsync(Owner(p), p.RootPath, index, ct));

    // Удаление стэша необратимо — фронт спрашивает подтверждение
    [HttpDelete("stash/{index:int}")]
    public Task<IActionResult> StashDrop(string projectId, int index, CancellationToken ct) =>
        Mutate(projectId, (p) => git.StashDropAsync(Owner(p), p.RootPath, index, ct));

    // Безопасная отмена коммита: git revert — новый коммит, история не переписывается
    [HttpPost("commits/{sha}/revert")]
    public Task<IActionResult> RevertCommit(string projectId, string sha, CancellationToken ct) =>
        Mutate(projectId, (p) => git.RevertCommitAsync(Owner(p), p.RootPath, sha, ct));

    // Данные входа в веб-UI Forgejo (владелец видит свои; пароль хранится открыто — решение владельца)
    [HttpGet("forgejo-credentials")]
    public IActionResult ForgejoCredentials(string projectId)
    {
        try
        {
            var p = GetProject(projectId);
            var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
            if (owner?.ForgejoUsername is null)
                return Conflict(new { error = "Аккаунт Forgejo ещё не создан — подключите git-сервер" });
            return Ok(new { login = owner.ForgejoUsername, password = owner.ForgejoPassword });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Сброс пароля Forgejo (утерян — например аккаунт создан до хранения паролей)
    [HttpPost("forgejo-credentials/reset")]
    public async Task<IActionResult> ResetForgejoPassword(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
            if (owner is null) return NotFound();
            var password = await gitServer.ResetPasswordAsync(owner, ct);
            return Ok(new { login = owner.ForgejoUsername, password });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // История одного файла (--follow) — вкладка «История» в просмотре файла
    [HttpGet("file-log")]
    public async Task<IActionResult> FileLog(string projectId, [FromQuery] string path, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.FileLogAsync(Owner(p), p.RootPath, path, limit, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Содержимое файла в конкретной версии — «открыть, как было» во вкладке «История»
    [HttpGet("commits/{sha}/file")]
    public async Task<IActionResult> FileAtCommit(string projectId, string sha, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var content = await git.FileAtCommitAsync(Owner(p), p.RootPath, sha, path, ct);
            return Ok(new { content });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Документный режим: вернуть файл к версии из коммита. При авто-режиме возврат
    // сразу фиксируется отдельным коммитом (человеку не нужен «индекс»)
    [HttpPost("commits/{sha}/restore-file")]
    public async Task<IActionResult> RestoreFile(string projectId, string sha, [FromBody] GitPathRequest body, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            await git.RestoreFileFromCommitAsync(Owner(p), p.RootPath, sha, body.Path, ct);
            if (p.GitAutoCommit)
            {
                await git.StageAsync(Owner(p), p.RootPath, body.Path, ct);
                var fileName = body.Path.Replace('\\', '/').Split('/')[^1];
                var shortSha = sha.Length > 7 ? sha[..7] : sha;
                await git.CommitAsync(Owner(p), p.RootPath, $"Возврат: {fileName} к версии {shortSha}", ct: ct);
            }
            await NotifyChanged(projectId);
            return Ok(await git.StatusAsync(Owner(p), p.RootPath, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Документный режим: «Сохранить сейчас» — всё с авто-сообщением (✨ по диффу;
    // не вышло — честный таймстемп), при GitAutoPush — отправить
    [HttpPost("save-now")]
    public async Task<IActionResult> SaveNow(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var status = await git.StatusAsync(Owner(p), p.RootPath, ct);
            if (status.Staged.Count == 0 && status.Unstaged.Count == 0 && status.Untracked.Count == 0)
                return Ok(new { committed = false });

            await git.StageAllAsync(Owner(p), p.RootPath, ct);
            string message;
            try
            {
                // Авто-коммит на дефолтном стиле (кастомный промпт — только для ручной ✨-генерации)
                var s = await gitAi.SuggestCommitMessageAsync(Owner(p), p.RootPath, null, ct);
                message = s is null ? $"Сохранение: {DateTime.Now:dd.MM.yyyy HH:mm}"
                    : (string.IsNullOrWhiteSpace(s.Description) ? s.Summary : $"{s.Summary}\n\n{s.Description}");
            }
            catch { message = $"Сохранение: {DateTime.Now:dd.MM.yyyy HH:mm}"; }
            var sha = await git.CommitAsync(Owner(p), p.RootPath, message, ct: ct);

            if (p.GitAutoPush && p.GitRemoteUrl is not null)
            {
                try
                {
                    var fresh = await git.StatusAsync(Owner(p), p.RootPath, ct);
                    if (fresh.Upstream is null && fresh.Branch is not null)
                        await git.PushSetUpstreamAsync(Owner(p), p.RootPath, fresh.Branch, CredsFor(p), ct);
                    else
                        await git.PushAsync(Owner(p), p.RootPath, CredsFor(p), ct);
                }
                catch { /* push best-effort — сохранение важнее */ }
            }
            await NotifyChanged(projectId);
            return Ok(new { committed = true, sha });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // LLM-помощь: сообщение коммита по staged-диффу / название стэша по правкам
    [HttpPost("ai/commit-message")]
    public async Task<IActionResult> AiCommitMessage(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var s = await gitAi.SuggestCommitMessageAsync(Owner(p), p.RootPath, EffectiveCommitPrompt(p), ct);
            return s is null ? Conflict(new { error = "Нет проиндексированных изменений" }) : Ok(s);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception) { return Conflict(new { error = "Не удалось сгенерировать описание" }); }
    }

    // Настройка промпта AI-генерации сообщения коммита: глобальный (per-user) + проектный override.
    // effective — что реально применится; default — дефолтные правила (для плейсхолдера).
    [HttpGet("commit-prompt")]
    public IActionResult GetCommitPrompt(string projectId)
    {
        try
        {
            var p = GetProject(projectId);
            var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
            var global = owner?.GitCommitPrompt;
            var projectOverride = p.CommitPromptOverride;
            return Ok(new
            {
                global,
                projectOverride,
                useProject = !string.IsNullOrWhiteSpace(projectOverride),
                effective = EffectiveCommitPrompt(p) ?? GitAiService.DefaultStyleRules,
                @default = GitAiService.DefaultStyleRules,
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Сохранить оба промпта: глобальный (User) пишем всегда; проектный override — только
    // если useProject=true (иначе снимаем, активным становится глобальный).
    [HttpPut("commit-prompt")]
    public IActionResult SetCommitPrompt(string projectId, [FromBody] GitCommitPromptRequest body)
    {
        try
        {
            var p = GetProject(projectId);
            if (p.OwnerId is not null) users.SetGitCommitPrompt(p.OwnerId, body.Global);
            projects.UpdateGitSettings(p.Id, commitPromptOverride: body.UseProject ? (body.Project ?? "") : "");
            return GetCommitPrompt(projectId);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Определить стиль коммитов по истории репы → инструкция для поля настройки (не сохраняет)
    [HttpPost("ai/detect-commit-style")]
    public async Task<IActionResult> DetectCommitStyle(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var prompt = await gitAi.DetectCommitStyleAsync(Owner(p), p.RootPath, ct);
            return prompt is null ? Conflict(new { error = "Недостаточно истории для анализа" }) : Ok(new { prompt });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception) { return Conflict(new { error = "Не удалось определить стиль" }); }
    }

    [HttpPost("ai/stash-name")]
    public async Task<IActionResult> AiStashName(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var name = await gitAi.SuggestStashNameAsync(Owner(p), p.RootPath, ct);
            return name is null ? Conflict(new { error = "Нет изменений" }) : Ok(new { name });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception) { return Conflict(new { error = "Не удалось сгенерировать название" }); }
    }

    [HttpGet("blame")]
    public async Task<IActionResult> Blame(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(await git.BlameAsync(Owner(p), p.RootPath, path, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("commit")]
    public async Task<IActionResult> Commit(string projectId, [FromBody] GitCommitRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Message))
            return BadRequest(new { error = "Пустое сообщение коммита" });
        try
        {
            var p = GetProject(projectId);
            var sha = await git.CommitAsync(Owner(p), p.RootPath, body.Message, body.Amend, ct);
            await NotifyChanged(projectId);
            return Ok(new { sha });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("fetch")]
    public Task<IActionResult> Fetch(string projectId, CancellationToken ct) =>
        Mutate(projectId, (p) => git.FetchAsync(Owner(p), p.RootPath, CredsFor(p), ct));

    [HttpPost("pull")]
    public Task<IActionResult> Pull(string projectId, CancellationToken ct) =>
        Mutate(projectId, (p) => git.PullAsync(Owner(p), p.RootPath, CredsFor(p), ct));

    [HttpPost("push")]
    public async Task<IActionResult> Push(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            var status = await git.StatusAsync(Owner(p), p.RootPath, ct);
            // Ветка без upstream (первый push) — сразу с -u origin <branch>
            if (status.Upstream is null && status.Branch is not null)
                await git.PushSetUpstreamAsync(Owner(p), p.RootPath, status.Branch, CredsFor(p), ct);
            else
                await git.PushAsync(Owner(p), p.RootPath, CredsFor(p), ct);
            await NotifyChanged(projectId);
            return Ok(await git.StatusAsync(Owner(p), p.RootPath, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Инициализация git в папке проекта + (при настроенном Forgejo) создание remote-репозитория
    [HttpPost("init")]
    public async Task<IActionResult> Init(string projectId, CancellationToken ct)
    {
        try
        {
            var p = GetProject(projectId);
            await git.InitAsync(Owner(p), p.RootPath, ct);
            string? htmlUrl = null;
            if (gitServer.Enabled && p.OwnerId is not null && users.GetById(p.OwnerId) is { } owner)
            {
                var repo = await gitServer.CreateRepoAsync(owner, p.Name, p.Id, ct);
                await git.SetRemoteAsync(Owner(p), p.RootPath, repo.CloneUrl, ct);
                projects.UpdateGitSettings(p.Id, remoteUrl: repo.CloneUrl);
                htmlUrl = repo.HtmlUrl;
            }
            await NotifyChanged(projectId);
            return Ok(new { status = await git.StatusAsync(Owner(p), p.RootPath, ct), htmlUrl });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }

    // Данные remote для UI: настроен ли Forgejo, подключён ли origin, deep-link
    [HttpGet("remote")]
    public IActionResult Remote(string projectId)
    {
        try
        {
            var p = GetProject(projectId);
            var owner = p.OwnerId is null ? null : users.GetById(p.OwnerId);
            // clone URL (внутренний, напр. localhost:3005) → публичная веб-ссылка (PublicUrl)
            var htmlUrl = p.GitRemoteUrl is not null && owner?.ForgejoUsername is not null
                ? gitServer.ToPublicHtmlUrl(p.GitRemoteUrl)
                : null;
            return Ok(new { serverEnabled = gitServer.Enabled, remoteUrl = p.GitRemoteUrl, htmlUrl, autoCommit = p.GitAutoCommit, autoPush = p.GitAutoPush });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Режим документов: авто-commit (и опционально push) после каждого хода Claude
    [HttpPut("auto-commit")]
    public IActionResult SetAutoCommit(string projectId, [FromBody] GitAutoCommitRequest body)
    {
        try
        {
            var p = GetProject(projectId);
            var updated = projects.UpdateGitSettings(p.Id, autoCommit: body.Enabled, autoPush: body.Push);
            return Ok(new { autoCommit = updated.GitAutoCommit, autoPush = updated.GitAutoPush });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("checkout")]
    public Task<IActionResult> Checkout(string projectId, [FromBody] GitCheckoutRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.CheckoutAsync(Owner(p), p.RootPath, body.Branch, ct));

    [HttpPost("branches")]
    public Task<IActionResult> CreateBranch(string projectId, [FromBody] GitCreateBranchRequest body, CancellationToken ct) =>
        Mutate(projectId, (p) => git.CreateBranchAsync(Owner(p), p.RootPath, body.Name, body.From, ct));

    // Общая обёртка write-операции: guard проекта → операция → realtime + свежий статус
    private async Task<IActionResult> Mutate(string projectId, Func<Models.Project, Task> op)
    {
        try
        {
            var p = GetProject(projectId);
            await op(p);
            await NotifyChanged(projectId);
            return Ok(await git.StatusAsync(Owner(p), p.RootPath));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Недопустимый путь" }); }
        catch (GitCommandException ex) { return Conflict(new { error = ex.Message }); }
    }
}

public record GitPathRequest(string Path);
public record GitPatchRequest(string Patch);
public record GitStashRequest(string? Message = null);
public record GitAutoCommitRequest(bool Enabled, bool Push = false);
public record GitCommitRequest(string Message, bool Amend = false);
public record GitCheckoutRequest(string Branch);
public record GitCreateBranchRequest(string Name, string? From = null);
// Оба уровня промпта + активный: Global → User (всегда), Project → override при UseProject
public record GitCommitPromptRequest(string? Global, string? Project, bool UseProject);
