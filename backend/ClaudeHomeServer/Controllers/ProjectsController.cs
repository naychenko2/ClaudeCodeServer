using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
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
public class ProjectsController(ProjectManager projects, SessionManager sessions, AppSettingsService appSettings, UserStore users, UserHomeResolver homes, WorkspaceKnowledgeStore wkStore, TaskManager tasks, ProjectEventLogService events, TeamMemoryService teamMemory, ClaudeHomeServer.Services.Dossiers.DossierStore dossiers, KnowledgeService knowledge, NotesKnowledgeService notesKb, PersonaManager personas, PersonaMemoryService personaMemory, ClaudeHomeServer.Services.Git.GitService git, ClaudeHomeServer.Services.Git.GitServerService gitServer, ClaudeHomeServer.Services.ProjectIcons.ProjectIconGlyphService iconGlyphs, ILogger<ProjectsController> logger, IHubContext<SessionHub> hub) : ControllerBase
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
        // Дефолт-персона проекта (фича default-personas-onboarding). Сирота (персона удалена
        // в обход проверки преемника) нормализуется в null — онбординг-гейт фронта сам чинит
        // осиротевший дефолт (как в AuthController.Me для личной)
        var defaultPersonaId = p.DefaultPersonaId is { } dpid && personas.Get(dpid, UserId) is not null
            ? dpid : null;
        return new { p.Id, p.Name, p.RootPath, RelativePath = relativePath, p.CreatedAt, p.UpdatedAt, p.GroupId, p.SystemPrompt, p.ShowHiddenFiles, p.PermissionRules, p.BoardColumns, p.TagRegistry, Icon = ProjectIconDto(p.Icon), p.McpServersOn, Background = Services.Backgrounds.ProjectBackgroundView.Of(p), BuiltInSystemPrompt = ProjectManager.BuiltInSystemPrompt, SessionCount = sessions.CountByProject(p.Id), DefaultPersonaId = defaultPersonaId, p.OnboardingSessionId, p.PresetKey };
    }

    // DTO иконки (ADR-009 §4): значок едет ДАННЫМИ (имя либо пути), разметку фронт не
    // получает — имя рисует компонент lucide, пути уходят значением атрибута d. Поле v —
    // версия содержимого значка для cache-busting у icon.svg (ADR-009 §8).
    private static object ProjectIconDto(ProjectIcon icon) => new
    {
        icon.Kind,
        icon.Color,
        Glyph = icon.Glyph is null ? null : new
        {
            icon.Glyph.Name,
            icon.Glyph.Paths,
            icon.Glyph.SetAt,
            V = GlyphVersion(icon.Glyph),
        },
    };

    // Первые 8 hex от SHA-256 содержимого значка: меняется вместе со значком,
    // стабильна между рестартами (ADR-009 §8, параметр ?v= у icon.svg)
    private static string GlyphVersion(ProjectGlyph glyph)
    {
        var payload = glyph.Name is not null
            ? "n:" + glyph.Name
            : "p:" + string.Join('\n', glyph.Paths ?? []);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..8].ToLowerInvariant();
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

    // Гейт длины записи командной памяти: в recall она всё равно обрежется (RecallTextLimit),
    // а простыня на 2-3 КБ засоряет общий стор и Dify. Ошибка объясняет, что делать дальше.
    // current — длина уже сохранённого текста (0 при создании): запрет срабатывает только на
    // РОСТ сверх лимита, чтобы уже раздутую запись можно было пересохранить без изменений или
    // сократить (как PersonaManager.ExceedsContractLimit для контракта персоны) — иначе её
    // вообще нельзя было бы привести в порядок.
    private static bool TooLongTeamMemory(string text, int current, out object error)
    {
        var length = text.Trim().Length;
        error = new
        {
            error = $"Запись памяти команды длиннее {TeamMemoryService.MaxTextLength} символов "
                + $"(сейчас {length}). Одна запись — одна мысль: разбей на несколько коротких "
                + "или сократи до сути; подробности держи в заметке или документе проекта.",
        };
        return length > TeamMemoryService.MaxTextLength && length > current;
    }

    // Персона-вызыватель MCP-инструмента (mcp/memory-server отдаёт свой MEMORY_PERSONA_ID
    // заголовком на каждый запрос) — пусто у обычного чата проекта без персоны и у фронта
    // (UI «Командного центра» этот заголовок не шлёт вовсе, поэтому ручное управление всегда
    // разрешено). См. DenyOnDelegatedTurnAttribute.CallerHeader — тот же паттерн, свой заголовок:
    // персона — не сессия, а MEMORY_PERSONA_ID у сессии меняется (смена спикера в группе).
    private const string CallerPersonaHeader = "X-Caller-Persona-Id";

    // Гейт записи в память команды проекта (③-3.4, диета памяти команды, часть 3): пишет либо
    // «свой» вызов без персоны (обычный проектный чат, ручное редактирование через UI), либо
    // персона ЭТОГО ЖЕ проекта. Глобальные персоны и консультанты других проектов — read-only
    // (team_memory_list/search остаются доступны, состав tools/list не меняем). Персона, которую
    // не удалось резолвить (удалена/чужой owner) — тоже отказ, а не молчаливое разрешение.
    private bool TeamMemoryWriteAllowed(string projectId, out object? error)
    {
        error = null;
        var callerPersonaId = Request.Headers[CallerPersonaHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(callerPersonaId)) return true;
        var caller = personas.Get(callerPersonaId, UserId);
        if (caller is { Scope: PersonaScope.Project } && caller.ProjectId == projectId) return true;
        error = new
        {
            error = "Запись в память команды доступна только персоне ЭТОГО проекта. Ты — "
                + "глобальная персона или консультант другого проекта: можешь читать общую память "
                + "(team_memory_list/team_memory_search), но не менять её. Попроси персону проекта "
                + "записать это или предложи пользователю.",
        };
        return false;
    }

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
        if (!TeamMemoryWriteAllowed(id, out var denied)) return StatusCode(403, denied);
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        if (TooLongTeamMemory(req.Text, 0, out var tooLong)) return BadRequest(tooLong);
        var entry = teamMemory.Add(UserId, id, req.Text, req.Type ?? TeamMemoryType.Fact);
        await BroadcastTeamMemory("added", id, entry.Id);
        return Ok(entry);
    }

    [HttpPut("{id}/team-memory/{entryId}")]
    public async Task<IActionResult> UpdateTeamMemory(string id, string entryId, [FromBody] TeamMemoryRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        if (!TeamMemoryWriteAllowed(id, out var denied)) return StatusCode(403, denied);
        var existing = teamMemory.List(UserId, id).FirstOrDefault(e => e.Id == entryId);
        if (existing is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new { error = "Пустой текст" });
        if (TooLongTeamMemory(req.Text, existing.Text.Trim().Length, out var tooLong)) return BadRequest(tooLong);
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
        if (!TeamMemoryWriteAllowed(id, out var denied)) return StatusCode(403, denied);
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
            var updated = projects.Update(id, req.Name, req.RootPath, req.SystemPrompt, req.ShowHiddenFiles, req.PermissionRules, req.GroupId, req.Color, req.McpServersOn);

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

    // Реестр общих тегов проекта (имя, порядок, цвет) — перезапись целиком
    [HttpPut("{id}/tags")]
    public IActionResult UpdateTags(string id, [FromBody] List<ProjectTag> registry)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        // Валидация состава: имена непустые, уникальные (без учёта регистра)
        if (registry is not null)
        {
            for (var i = 0; i < registry.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(registry[i].Name))
                    return BadRequest(new { error = $"Тег #{i + 1} имеет пустое имя" });
            }

            var names = registry.Select(t => t.Name.Trim()).ToList();
            var distinct = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (distinct.Count != names.Count)
                return BadRequest(new { error = "Имена тегов должны быть уникальными (без учёта регистра)" });

            // Нормализация Order по позиции массива
            for (var i = 0; i < registry.Count; i++)
                registry[i].Order = i;
        }

        var updated = projects.UpdateTags(id, registry ?? []);
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
        // Паспорта изменений проекта: локальный стор + Dify-датасет (best-effort, тот же образец)
        try { await dossiers.DeleteProjectDossiersAsync(UserId, id); }
        catch { /* удаление проекта не зависит от уборки паспортов */ }

        // Worktree чатов проекта: сессии при удалении проекта НЕ удаляются (осознанно),
        // но их деревья без проекта — мусор на диске и записи в .git/worktrees главной репы.
        // Явный обход обязателен — автокаскада сессий нет. Best-effort + force: судьба
        // незакоммиченных правок решена удалением самого проекта.
        foreach (var s in sessions.GetByProject(id).Where(s => s.WorktreePath is not null).ToList())
        {
            try { await git.WorktreeRemoveAsync(p.OwnerId, p.RootPath, s.WorktreePath!, force: true); }
            catch { /* дерево могло быть удалено руками — запись подчистит prune */ }
        }

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

    // --- Значок проекта (ADR-009 §8) ---
    // Растровый путь (caps/generate/candidate/set-image/upload/recrop/original/GET icon)
    // удалён вместе с ассетами: значок — данные записи, разметку собирает сервер.

    // Подобрать кандидатов значка: до четырёх вперемешку (имена из набора lucide +
    // нарисованные пути). Стор НЕ меняется — принятие отдельным вызовом select. Любой
    // сбой (место не настроено, битый JSON, ни одного годного) = пустой набор с причиной:
    // проект остаётся на инициалах, это фолбэк, а не ошибка (ADR-009 §7).
    [HttpPost("{id}/icon/suggest")]
    public async Task<ActionResult> SuggestIcon(string id, [FromBody] SuggestIconRequest? req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();
        var result = await iconGlyphs.SuggestAsync(p.Name, req?.Hint, p.OwnerId!, HttpContext.RequestAborted);
        return Ok(IconSuggestView(result));
    }

    // Тот же подбор ДО создания проекта — по черновику названия из диалога «Добавить
    // проект» (ADR-009 §8). Проекта (и владельца-владения) ещё нет, гейт — только
    // авторизация: подбор идёт от UserId текущего пользователя, стор не меняется.
    [HttpPost("icon/suggest-preview")]
    public async Task<ActionResult> SuggestIconPreview([FromBody] SuggestIconPreviewRequest? req)
    {
        var name = req?.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "Имя проекта обязательно: подбор идёт по названию" });
        var result = await iconGlyphs.SuggestAsync(name, req!.Hint, UserId, HttpContext.RequestAborted);
        return Ok(IconSuggestView(result));
    }

    // Форма ответа подбора — общая для suggest и suggest-preview: кандидаты
    // (имя либо пути) плюс причина пустого набора.
    private static object IconSuggestView(Services.ProjectIcons.ProjectIconGlyphResult result) => new
    {
        candidates = result.Candidates.Select(c => c.IsNamed
            ? (object)new { name = c.Name }
            : new { paths = c.Paths }),
        result.FailReason,
    };

    // Принять значок: Kind = Glyph, Glyph = {Name|Paths}. Тело валидируется ЦЕЛИКОМ заново
    // тем же валидатором, что и ответ модели, — клиент такой же недоверенный источник
    // (ADR-009 §8, инвариант «валидация на входе в стор» §11.3).
    [HttpPost("{id}/icon/select")]
    public ActionResult SelectIcon(string id, [FromBody] SelectIconRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        var candidate = Services.ProjectIcons.ProjectIconGlyphService.ValidateGlyph(req.Name, req.Paths);
        if (candidate is null)
            return BadRequest(new
            {
                error = "Негодный значок: нужно ровно одно поле — name из набора либо 1–4 корректные строки d",
            });

        return Ok(WithCount(projects.SetIconGlyph(id, new ProjectGlyph
        {
            Name = candidate.Name,
            Paths = candidate.Paths?.ToList(),
            SetAt = DateTime.UtcNow,
        })));
    }

    // Переключить режим иконки: инициалы ↔ значок. Значок при возврате на инициалы НЕ
    // стирается — это путь «назад» и «снова вперёд» без повторного подбора.
    [HttpPost("{id}/icon/mode")]
    public ActionResult SetIconMode(string id, [FromBody] SetIconModeRequest req)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId) return NotFound();

        var kind = (req.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "initials" => ProjectIconKind.Initials,
            "glyph" => ProjectIconKind.Glyph,
            _ => (ProjectIconKind?)null,
        };
        if (kind is null) return BadRequest(new { error = "Режим должен быть 'initials' или 'glyph'" });
        if (kind == ProjectIconKind.Glyph && p.Icon.Glyph is null)
            return BadRequest(new { error = "У проекта нет значка — сначала подберите его" });

        return Ok(WithCount(projects.SetIconKind(id, kind.Value)));
    }

    // Собранный сервером SVG значка — единственная разрешённая форма значка как
    // самостоятельного ресурса (ADR-009 §4). Только Paths-вид: Name-значок рисует фронт
    // компонентом lucide, файла для него не существует. access_token в query — для <img>.
    [HttpGet("{id}/icon.svg")]
    public IActionResult IconSvg(string id)
    {
        var p = projects.GetById(id);
        if (p is null || p.OwnerId != UserId
            || p.Icon.Kind != ProjectIconKind.Glyph
            || p.Icon.Glyph?.Paths is not { Count: > 0 } paths)
            return NotFound();

        Response.Headers.CacheControl = "private, max-age=604800, immutable";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        return Content(Services.ProjectIcons.GlyphSvg.Build(paths), "image/svg+xml");
    }
}

// Контракт — «prompt», как шлёт фронт (api.ts); без атрибута поле молча терялось (QA 2026-08-17)
public record SuggestIconRequest([property: JsonPropertyName("prompt")] string? Hint);
// Preview-подбор до создания проекта: name — черновик названия, prompt — та же подсказка
public record SuggestIconPreviewRequest(string? Name, [property: JsonPropertyName("prompt")] string? Hint);
public record SelectIconRequest(string? Name, List<string>? Paths);
public record SetIconModeRequest(string? Kind);

public record CreateProjectRequest(string Name, string? RootPath, bool CreateDirectory = false, string? GroupId = null,
    bool EnableGit = false, bool GitAutoCommit = false, bool GitAutoPush = false, string? Color = null);
// McpServersOn — ключи включённых серверов личного реестра (allow-модель доступа;
// null = не менять, пустой список = «никто не включён»).
public record UpdateProjectRequest(string? Name, string? RootPath, string? SystemPrompt, bool? ShowHiddenFiles, List<PermissionRule>? PermissionRules = null, string? GroupId = null, string? Color = null, List<string>? McpServersOn = null);
public record UpdateBoardColumnsRequest(List<BoardColumn>? Columns);
public record TeamMemoryRequest(string Text, TeamMemoryType? Type = null);
