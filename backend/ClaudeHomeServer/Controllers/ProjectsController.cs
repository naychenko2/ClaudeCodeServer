using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public class ProjectsController(ProjectManager projects, SessionManager sessions, AppSettingsService appSettings, UserStore users, UserHomeResolver homes, WorkspaceKnowledgeStore wkStore, TaskManager tasks, ProjectEventLogService events, TeamMemoryService teamMemory, KnowledgeService knowledge, NotesKnowledgeService notesKb, PersonaManager personas, PersonaMemoryService personaMemory, ClaudeHomeServer.Services.Git.GitService git, ClaudeHomeServer.Services.Git.GitServerService gitServer, FalImageService falImage, ILogger<ProjectsController> logger, IHubContext<SessionHub> hub) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Task BroadcastTeamMemory(string action, string projectId, string? entryId = null) =>
        hub.Clients.Group("user_" + UserId).SendAsync("message", new TeamMemoryChangedMessage(action, projectId, entryId));

    private object WithCount(Project p)
    {
        // Путь показываем относительно домашней папки владельца — с учётом override она может
        // не совпадать с DefaultProjectsPath (иначе получилось бы «..\..\GIT\myproj»)
        var basePath = homes.Resolve(users.GetById(UserId)) ?? appSettings.Get().DefaultProjectsPath;
        var relativePath = string.IsNullOrEmpty(basePath) ? p.RootPath : Path.GetRelativePath(basePath, p.RootPath);
        return new { p.Id, p.Name, p.RootPath, RelativePath = relativePath, p.CreatedAt, p.UpdatedAt, p.GroupId, p.SystemPrompt, p.ShowHiddenFiles, p.ToolsEnabled, p.PermissionRules, p.BoardColumns, p.Icon, BuiltInSystemPrompt = ProjectManager.BuiltInSystemPrompt, SessionCount = sessions.CountByProject(p.Id) };
    }

    [HttpGet("builtin-prompt")]
    public IActionResult GetBuiltinPrompt() => Ok(new { content = ProjectManager.BuiltInSystemPrompt });

    // Эффективный системный промпт проекта — ровно те части, что уходят в --append-system-prompt
    // (без промпта агента: он добавляется per-session для агент-чатов)
    [HttpGet("{id}/effective-prompt")]
    public IActionResult GetEffectivePrompt(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        var wk = wkStore.GetByPath(p.RootPath);
        var parts = ProjectManager.GetSystemPromptParts(
            p.SystemPrompt, wk?.DifyDatasetId != null, wk?.DocumentTags);
        return Ok(new { parts });
    }

    [HttpGet]
    public IActionResult GetAll() => Ok(projects.GetByOwner(UserId).Select(WithCount));

    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(WithCount(p));
    }

    // Лента событий проекта (активность команды): ходы, задачи, память, база, заметки, состав.
    // Фильтры опциональны (since/type/actor/limit). Источник для командного центра (①-L1).
    [HttpGet("{id}/events")]
    public IActionResult GetEvents(string id,
        [FromQuery] DateTime? since, [FromQuery] string? type,
        [FromQuery] string? actor, [FromQuery] int? limit)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(events.Query(id, UserId, since, type, actor, limit ?? 100));
    }

    // === Память команды проекта (③-3.4) === — общие факты/договорённости, которые recall'ят
    // все персоны команды проекта наравне с личной памятью.

    [HttpGet("{id}/team-memory")]
    public IActionResult TeamMemory(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        return Ok(teamMemory.List(UserId, id));
    }

    [HttpPost("{id}/team-memory")]
    public async Task<IActionResult> AddTeamMemory(string id, [FromBody] TeamMemoryRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        var entry = teamMemory.Add(UserId, id, req.Text, req.Type ?? TeamMemoryType.Fact);
        await BroadcastTeamMemory("added", id, entry.Id);
        return Ok(entry);
    }

    [HttpPut("{id}/team-memory/{entryId}")]
    public async Task<IActionResult> UpdateTeamMemory(string id, string entryId, [FromBody] TeamMemoryRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        var entry = teamMemory.Update(UserId, id, entryId, req.Text);
        if (entry is null) return NotFound();
        await BroadcastTeamMemory("updated", id, entryId);
        return Ok(entry);
    }

    [HttpDelete("{id}/team-memory/{entryId}")]
    public async Task<IActionResult> RemoveTeamMemory(string id, string entryId)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (!teamMemory.Remove(UserId, id, entryId)) return NotFound();
        await BroadcastTeamMemory("removed", id, entryId);
        return NoContent();
    }

    // Поиск по памяти команды: семантический (при Dify) либо полнотекстовый. Дёргается MCP team_memory_search.
    [HttpGet("{id}/team-memory/search")]
    public async Task<IActionResult> SearchTeamMemory(string id, [FromQuery] string q, [FromQuery] int topK = 8)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(q)) return Ok(Array.Empty<TeamMemoryEntry>());
        return Ok(await teamMemory.SearchAsync(UserId, id, q.Trim(), Math.Clamp(topK, 1, 20)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest req)
    {
        try
        {
            var username = User.FindFirstValue(ClaimTypes.Name) ?? UserId;
            var p = projects.Create(req.Name, req.RootPath, UserId, username, req.CreateDirectory, req.GroupId, req.Color);

            // Git-режим из диалога создания: init (+ Forgejo-репо при настроенном сервере).
            // Best-effort: сбой git/Forgejo не отменяет создание проекта — подключить можно позже
            if (req.EnableGit)
            {
                try
                {
                    await git.InitAsync(p.OwnerId, p.RootPath);
                    if (gitServer.Enabled && p.OwnerId is not null && users.GetById(p.OwnerId) is { } owner)
                    {
                        var repo = await gitServer.CreateRepoAsync(owner, p.Name, p.Id);
                        await git.SetRemoteAsync(p.OwnerId, p.RootPath, repo.CloneUrl);
                        projects.UpdateGitSettings(p.Id, remoteUrl: repo.CloneUrl,
                            autoCommit: req.GitAutoCommit, autoPush: req.GitAutoPush);
                    }
                    else if (req.GitAutoCommit)
                        projects.UpdateGitSettings(p.Id, autoCommit: true, autoPush: false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Git при создании проекта {Name} не подключился (проект создан)", p.Name);
                }
            }
            return CreatedAtAction(nameof(GetById), new { id = p.Id }, WithCount(p));
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateProjectRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        // Update мутирует объект проекта на месте — старые значения снимаем до вызова
        var oldName = p.Name;
        var oldRoot = p.RootPath;
        try
        {
            var updated = projects.Update(id, req.Name, req.RootPath, req.SystemPrompt, req.ShowHiddenFiles, req.PermissionRules, req.GroupId, req.ToolsEnabled, req.Color);

            // Смена папки проекта: перенести запись знаний под новый ключ — иначе запись сиротеет,
            // для нового пути создаётся дубль-датасет, а mcp dify молча теряет dataset_id
            if (WorkspaceKnowledgeStore.NormalizePath(oldRoot) != WorkspaceKnowledgeStore.NormalizePath(updated.RootPath))
                wkStore.Move(oldRoot, updated.RootPath);

            // Переименование проекта: best-effort освежить имена Dify-датасетов
            // ({user}:{project} и {user}:team:{project}); сбой не ломает работу по id
            if (!string.Equals(oldName, updated.Name, StringComparison.Ordinal))
            {
                var username = User.FindFirstValue(ClaimTypes.Name) ?? UserId;
                var datasetId = wkStore.GetByPath(updated.RootPath)?.DifyDatasetId;
                if (!string.IsNullOrEmpty(datasetId))
                    try { await knowledge.RenameDatasetAsync(datasetId, $"{username}:{updated.Name}"); }
                    catch { /* стухшее имя не критично */ }
                try { await teamMemory.RenameProjectDatasetAsync(UserId, id, username, updated.Name); }
                catch { /* стухшее имя не критично */ }
            }

            return Ok(WithCount(updated));
        }
        catch (DirectoryNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
        // папка вне песочницы либо уже занята другим проектом владельца — это ошибка ввода, не 500
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Кастомные колонки Kanban-доски проекта (пустой список → дефолтные 3)
    [HttpPut("{id}/board-columns")]
    public IActionResult UpdateBoardColumns(string id, [FromBody] UpdateBoardColumnsRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        var updated = projects.UpdateBoardColumns(id, req.Columns);
        return Ok(WithCount(updated));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        projects.Delete(id);
        tasks.DeleteByProject(id);
        // Память команды проекта: локальные сторы + Dify-датасет (best-effort — уборка не должна ронять удаление)
        try { await teamMemory.DeleteProjectTeamMemoryAsync(UserId, id); }
        catch { /* удаление проекта не зависит от уборки памяти команды */ }

        // База знаний проекта: Dify-датасет + запись WorkspaceKnowledge. Датасет общий для
        // проектов в одной папке — чистим, только если RootPath больше никем не используется
        if (projects.GetByRootPath(p.RootPath).Count == 0)
        {
            var wk = wkStore.GetByPath(p.RootPath);
            if (wk is not null)
            {
                if (!string.IsNullOrEmpty(wk.DifyDatasetId))
                {
                    try { await knowledge.DeleteDatasetAsync(wk.DifyDatasetId); }
                    catch { /* датасет мог быть удалён в Dify — снимаем только запись */ }
                    await hub.Clients.Group("user_" + UserId)
                        .SendAsync("message", new KnowledgeChangedMessage("deleted", wk.DifyDatasetId));
                }
                wkStore.Delete(p.RootPath);
            }
        }

        // Заметки notes/ проекта выпали из alive-set — вычистить их из «{user}:notes» сразу,
        // не дожидаясь следующей несвязанной правки заметок
        notesKb.QueueSync(UserId);

        // Проектные персоны осиротели вместе с проектом — каскад: память (стор + Dify-датасет),
        // сама персона (файлы сабагента снимет OnPersonaDeleted), событие фронту
        foreach (var persona in personas.GetByOwner(UserId)
                     .Where(x => x.Scope == PersonaScope.Project && x.ProjectId == id).ToList())
        {
            try { await personaMemory.DeletePersonaAsync(persona.Id); }
            catch { /* память персоны — best-effort */ }
            personas.Delete(persona.Id, UserId);
            await hub.Clients.Group("user_" + UserId)
                .SendAsync("message", new PersonasChangedMessage("deleted", persona.Id));
        }

        return NoContent();
    }

    // --- Иконка проекта (по образцу аватара персоны) ---

    // Доступна ли AI-генерация иконки (настроен ли fal)
    [HttpGet("icon/caps")]
    public ActionResult IconCaps() => Ok(new { generate = falImage.Enabled });

    // Сгенерировать НЕСКОЛЬКО вариантов иконки через fal по описанию (для выбора).
    // Кандидаты сохраняются во временную папку, иконка проекта НЕ меняется до выбора.
    [HttpPost("{id}/icon/generate")]
    public async Task<ActionResult> GenerateIcon(string id, [FromBody] GenerateIconRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (!falImage.Enabled) return BadRequest(new { error = "Генерация изображений не настроена (нет Fal:ApiKey)" });

        var prompt = string.IsNullOrWhiteSpace(req.Prompt)
            ? BuildIconPrompt(p)
            : $"Flat minimalist 2D vector emblem, full-bleed filling the entire square canvas edge to edge, "
              + "solid flat background color, bold simple shapes, no rounded-rectangle app-icon frame, "
              + $"no border, no drop shadow, no 3D, no gloss, no padding, no text. {req.Prompt.Trim()}";
        var count = req.Count is >= 1 and <= 4 ? req.Count.Value : 4;

        var images = await falImage.GenerateManyAsync(prompt, count);
        if (images.Count == 0) return StatusCode(502, new { error = "Не удалось сгенерировать изображение" });

        // Свежая папка кандидатов (перезатираем прошлую генерацию)
        var candDir = Path.Combine(projects.IconsDir, id, "candidates");
        try { if (Directory.Exists(candDir)) Directory.Delete(candDir, recursive: true); } catch { }
        Directory.CreateDirectory(candDir);

        var files = new List<string>();
        foreach (var img in images)
        {
            var ext = ImageAssetHelper.ExtFor(img.ContentType);
            var name = $"cand-{Guid.NewGuid():N}{ext}";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(candDir, name), img.Bytes);
            files.Add(name);
        }
        return Ok(new { candidates = files });
    }

    // Генерация кандидатов иконки ДО создания проекта (в диалоге «Добавить проект»): проекта
    // ещё нет, поэтому байты возвращаем инлайн как data-url и на диск НИЧЕГО не пишем.
    // Литерал «icon» первым сегментом не конфликтует с «{id}/icon/...» (как и «icon/caps»).
    [HttpPost("icon/generate-preview")]
    public async Task<ActionResult> GenerateIconPreview([FromBody] GenerateIconPreviewRequest req)
    {
        if (!falImage.Enabled) return BadRequest(new { error = "Генерация изображений не настроена (нет Fal:ApiKey)" });

        var prompt = string.IsNullOrWhiteSpace(req.Prompt)
            ? BuildIconPrompt(req.Name ?? "")
            : $"Flat minimalist 2D vector emblem, full-bleed filling the entire square canvas edge to edge, "
              + "solid flat background color, bold simple shapes, no rounded-rectangle app-icon frame, "
              + $"no border, no drop shadow, no 3D, no gloss, no padding, no text. {req.Prompt.Trim()}";
        var count = req.Count is >= 1 and <= 4 ? req.Count.Value : 4;

        var images = await falImage.GenerateManyAsync(prompt, count);
        if (images.Count == 0) return StatusCode(502, new { error = "Не удалось сгенерировать изображение" });

        var candidates = images
            .Select(i => new { dataUrl = $"data:{i.ContentType};base64,{Convert.ToBase64String(i.Bytes)}" })
            .ToList();
        return Ok(new { candidates });
    }

    // Отдать кандидата иконки (превью в галерее выбора). access_token в query для <img>.
    [HttpGet("{id}/icon/candidate/{file}")]
    public IActionResult IconCandidate(string id, string file)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        var safe = Path.GetFileName(file);   // защита от path-traversal
        var full = Path.Combine(projects.IconsDir, id, "candidates", safe);
        if (!System.IO.File.Exists(full)) return NotFound();
        return ImageAssetHelper.PhysicalFileByExt(full);
    }

    // Выбрать кандидата как иконку проекта: делаем основным, чистим остальных кандидатов.
    [HttpPost("{id}/icon/select")]
    public ActionResult SelectIcon(string id, [FromBody] SelectIconRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrWhiteSpace(req.File)) return BadRequest(new { error = "Не указан файл" });

        var dir = Path.Combine(projects.IconsDir, id);
        var candPath = Path.Combine(dir, "candidates", Path.GetFileName(req.File));
        if (!System.IO.File.Exists(candPath)) return NotFound(new { error = "Кандидат не найден" });

        var ext = Path.GetExtension(candPath);
        var fileName = $"icon-{Guid.NewGuid():N}{ext}";   // cache-busting
        System.IO.File.Copy(candPath, Path.Combine(dir, fileName), overwrite: true);

        // Удаляем прежнюю иконку и всю папку кандидатов
        if (!string.IsNullOrEmpty(p.Icon.ImageFile))
            try { System.IO.File.Delete(Path.Combine(dir, p.Icon.ImageFile)); } catch { }
        try { Directory.Delete(Path.Combine(dir, "candidates"), recursive: true); } catch { }

        return Ok(WithCount(projects.SetIconImage(id, fileName)));
    }

    // Прикрепить готовую картинку-иконку к УЖЕ созданному проекту (паритет с SetIconImage:
    // без оригинала/кропа). Нужен, чтобы досылать СГЕНЕРИРОВАННУЮ в диалоге создания иконку
    // после create() — генерация там была stateless, файла на сервере ещё нет.
    [HttpPost("{id}/icon/set-image")]
    [RequestSizeLimit(15_000_000)]
    public async Task<ActionResult> SetIconImageFile(string id, [FromForm] IFormFile? image)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (image is null) return BadRequest(new { error = "Нужен файл image" });

        var check = await ImageAssetHelper.ValidateImageAsync(image);
        if (check.Error is not null) return BadRequest(new { error = check.Error });

        var dir = Path.Combine(projects.IconsDir, id);
        Directory.CreateDirectory(dir);
        var name = $"icon-{Guid.NewGuid():N}{check.Ext}";
        await ImageAssetHelper.SaveFormFileAsync(image, Path.Combine(dir, name));

        return Ok(WithCount(projects.SetIconImage(id, name)));
    }

    // Переключить режим иконки: буквы (initials) ↔ картинка (image). Файлы картинки НЕ стираются —
    // это возврат к инициалам без потери загруженной/сгенерированной картинки (и обратно к ней).
    [HttpPost("{id}/icon/mode")]
    public ActionResult SetIconMode(string id, [FromBody] SetIconModeRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        var kind = (req.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "initials" => ProjectIconKind.Initials,
            "image" => ProjectIconKind.Image,
            _ => (ProjectIconKind?)null,
        };
        if (kind is null) return BadRequest(new { error = "Режим должен быть 'initials' или 'image'" });
        if (kind == ProjectIconKind.Image && string.IsNullOrEmpty(p.Icon.ImageFile))
            return BadRequest(new { error = "У проекта нет картинки — сначала сгенерируйте или загрузите" });

        return Ok(WithCount(projects.SetIconKind(id, kind.Value)));
    }

    // Отдать картинку иконки. JWT принимается и в query access_token (браузерный <img>).
    [HttpGet("{id}/icon")]
    public IActionResult Icon(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId || p.Icon.Kind != ProjectIconKind.Image
            || string.IsNullOrEmpty(p.Icon.ImageFile))
            return NotFound();

        var full = Path.Combine(projects.IconsDir, id, p.Icon.ImageFile);
        return System.IO.File.Exists(full) ? ImageAssetHelper.PhysicalFileByExt(full) : NotFound();
    }

    // Оригинал загруженной иконки (для перекропа). access_token в query — как GET icon.
    [HttpGet("{id}/icon/original")]
    public IActionResult IconOriginal(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId || string.IsNullOrEmpty(p.Icon.OriginalFile))
            return NotFound();

        var full = Path.Combine(projects.IconsDir, id, p.Icon.OriginalFile);
        return System.IO.File.Exists(full) ? ImageAssetHelper.PhysicalFileByExt(full) : NotFound();
    }

    // Загрузка своей иконки: оригинал + кропнутый квадрат + параметры кропа (JSON).
    // Валидация: заявленный ContentType из белого списка И настоящие magic bytes.
    [HttpPost("{id}/icon/upload")]
    [RequestSizeLimit(15_000_000)]
    public async Task<ActionResult> UploadIcon(string id,
        [FromForm] IFormFile? original, [FromForm] IFormFile? cropped, [FromForm] string? crop)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (original is null || cropped is null)
            return BadRequest(new { error = "Нужны файлы original и cropped" });

        var originalCheck = await ImageAssetHelper.ValidateImageAsync(original);
        if (originalCheck.Error is not null) return BadRequest(new { error = originalCheck.Error });
        var croppedCheck = await ImageAssetHelper.ValidateImageAsync(cropped);
        if (croppedCheck.Error is not null) return BadRequest(new { error = croppedCheck.Error });

        var cropState = ImageAssetHelper.ParseCrop(crop);

        var dir = Path.Combine(projects.IconsDir, id);
        Directory.CreateDirectory(dir);
        var originalName = $"original-{Guid.NewGuid():N}{originalCheck.Ext}";
        var imageName = $"icon-{Guid.NewGuid():N}{croppedCheck.Ext}";
        await ImageAssetHelper.SaveFormFileAsync(original, Path.Combine(dir, originalName));
        await ImageAssetHelper.SaveFormFileAsync(cropped, Path.Combine(dir, imageName));

        return Ok(WithCount(projects.SetIconUploaded(id, imageName, originalName, cropState)));
    }

    // Перекроп сохранённого оригинала: новая кропнутая картинка + параметры.
    [HttpPost("{id}/icon/recrop")]
    [RequestSizeLimit(5_000_000)]
    public async Task<ActionResult> RecropIcon(string id,
        [FromForm] IFormFile? cropped, [FromForm] string? crop)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (string.IsNullOrEmpty(p.Icon.OriginalFile))
            return BadRequest(new { error = "У проекта нет оригинала для перекропа" });
        if (cropped is null) return BadRequest(new { error = "Нужен файл cropped" });

        var croppedCheck = await ImageAssetHelper.ValidateImageAsync(cropped);
        if (croppedCheck.Error is not null) return BadRequest(new { error = croppedCheck.Error });

        var dir = Path.Combine(projects.IconsDir, id);
        Directory.CreateDirectory(dir);
        var imageName = $"icon-{Guid.NewGuid():N}{croppedCheck.Ext}";
        await ImageAssetHelper.SaveFormFileAsync(cropped, Path.Combine(dir, imageName));

        return Ok(WithCount(projects.SetIconRecropped(id, imageName, ImageAssetHelper.ParseCrop(crop))));
    }

    // Промпт иконки по умолчанию — из имени проекта. Просим ПЛОСКИЙ символ во всю площадь
    // без рамки/фона-плитки/тени/полей: иначе модель рисует «иконку приложения» с рамкой и
    // мелким символом в центре, а наша плитка добавляет вторую рамку.
    private static string BuildIconPrompt(Project project) => BuildIconPrompt(project.Name);

    // Перегрузка по имени — для генерации ДО создания проекта (проекта ещё нет). Пустое имя →
    // безымянный фолбэк (абстрактная эмблема).
    private static string BuildIconPrompt(string name)
    {
        var subject = string.IsNullOrWhiteSpace(name)
            ? "an abstract project emblem"
            : $"a project named '{name.Trim()}'";
        return $"Flat minimalist 2D vector emblem representing {subject}. " +
            "A single bold symbol that fills the entire square canvas edge to edge (full-bleed), " +
            "simple flat shapes, solid flat background color, high contrast, centered composition. " +
            "No rounded-rectangle app-icon frame, no border, no drop shadow, no 3D, no gloss, " +
            "no small padding around the symbol, no text, no letters.";
    }
}

public record GenerateIconRequest(string? Prompt, int? Count);
public record GenerateIconPreviewRequest(string? Name, string? Prompt, int? Count);
public record SelectIconRequest(string File);
public record SetIconModeRequest(string? Kind);

public record CreateProjectRequest(string Name, string? RootPath, bool CreateDirectory = false, string? GroupId = null,
    bool EnableGit = false, bool GitAutoCommit = false, bool GitAutoPush = false, string? Color = null);
public record UpdateProjectRequest(string? Name, string? RootPath, string? SystemPrompt, bool? ShowHiddenFiles, bool? ToolsEnabled = null, List<PermissionRule>? PermissionRules = null, string? GroupId = null, string? Color = null);
public record UpdateBoardColumnsRequest(List<BoardColumn>? Columns);
public record TeamMemoryRequest(string Text, TeamMemoryType? Type = null);
