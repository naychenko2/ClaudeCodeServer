using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Notes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using ClaudeHomeServer.Services.Git;
using FileMutationKind = ClaudeHomeServer.Services.Composition.FileMutationKind;

namespace ClaudeHomeServer.Controllers;

[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/files")]
public class FilesController(FileService files, ProjectManager projects, IProjectAccess access, SyncService sync, IConfiguration config, JwtService jwt, ILogger<FilesController> logger, DocumentAiService docAi, ProjectFileSessionsIndex fileSessions, ProjectTreeLocator locator, INoteAccessor? notes = null,
    // W4-5B2 (п.5.2): чтение файла из проверенного дерева эпика (?featureId=). Tasks и Git —
    // отключаемые подсистемы: нет Tasks — маршрут featureId отказывает 404 (fail-closed); нет
    // Git — отказывает заявленное дерево, а отсутствие дерева читает корень (общее правило
    // TreeRootForAsync). Маршрут без параметра работает как раньше.
    ClaudeHomeServer.Services.Tasks.TaskManager? tasks = null,
    GitService? git = null,
    // W4-LINK-B (п.14): резолв дерева задачи, к которой привязан чат (?sessionId=).
    SessionManager sessions = null!,
    // S3-3b (разрез 4bc3d04f §3): журнал правок человека (S3-3) — зовётся после УСПЕШНОЙ записи/
    // удаления/переноса файла. В DI он всегда (Program.cs:438-439); опциональным оставлен как
    // остальные необязательные зависимости контроллера: бесхостовые тесты собирают его напрямую.
    IHumanEditJournal? humanEdits = null) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Ключ хранилищ (задачи, заметки, деревья git) — арендатор из RequestScope ([ProjectScope]), не вызывающий
    private string TenantId => HttpContext.GetRequestScope().TenantOwnerId;

    // Проект запроса (доступ проверил [ProjectScope]); несуществующий → KeyNotFoundException (клиенту 404, как в ProjectsController)
    private ClaudeHomeServer.Models.Project GetProject(string projectId)
    {
        var p = projects.GetById(projectId);
        if (p is null)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    // Для анонимных OnlyOffice-эндпоинтов (office-download/office-callback): JWT нет,
    // доступ защищён подписанным office-токеном, привязанным к владельцу+projectId+path.
    // Пользователь из проверенного токена (ownerId — тот, кто его выдал) обязан иметь право записи
    // в проект: области запроса у анонимного входа нет, поэтому явный Resolve.
    private ClaudeHomeServer.Models.Project GetProjectByOfficeToken(string projectId, string ownerId)
    {
        var p = projects.GetById(projectId);
        if (p is null || access.Resolve(ownerId, projectId, ProjectRight.Write) is null)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    private string GetRoot(string projectId) => GetProject(projectId).RootPath;

    // Путь ведёт в папку, а не в файл. Проверка нужна файловым эндпоинтам: путь папки с
    // завершающим слешем (так `git status` отдаёт новую папку — «.claude/», и панель изменений
    // передаёт его как файл) роняет File.ReadAllText через DirectoryNotFoundException → 500.
    // SafeJoin здесь не глушим: выход за пределы проекта обрабатывают сами эндпоинты (403).
    private static bool IsDirectoryPath(string root, string path) =>
        Directory.Exists(FileService.SafeJoinPublic(root, path));

    // S2d-6b-2 (разрез 4bc3d04f §1 путь 19): чужое приватное вложение (раскладка S2d-6b-1 —
    // .cc-attachments/private/{автор}/…) — 404, а не 403: «нет такого файла» неотличим от чужого
    // приватного — тот же приём, что GetOwnedSession ниже на чужом приватном чате. Автор своего
    // private, общие вложения (.cc-attachments/{guid}/…) и любые прочие пути — как раньше; без JWT
    // (UserId null) любое private чужое — fail-closed, как сам IsForeign. Путь берётся в том виде,
    // в котором его назвал клиент: обходы («./», «..», %2F, слеши, регистр) канонизирует предикат.
    // S2d-6b-5 (дефект e5d55007, асимметрия с MCP/WebDAV): тот же гейт закрыл и пишущие ручки
    // (SaveContent/Create/mkdir/Rename/Delete/Upload/ToMarkdown-цель/Revert/SaveFromUrl) —
    // до любого эффекта: до замка пути, до CreateDirectory, до загрузки.
    // Хвост a6f5cb15: предикат канонический (IsForeignResolved) — корень нужен, чтобы
    // разыменить символьные ссылки: симлинк «L» → .cc-attachments в корне даёт то же чужое
    // private, что и прямой путь, и текстовый гейт о нём молчал.
    // TAIL-ROOTS-GATE (DEC-ROOTS 5c8a4705): закрытое поддерево живёт не только в корне
    // проекта, а в рабочем дереве чата (EffectiveRoot = WorktreePath ?? RootPath), и чужесть
    // судится по сегменту канона, а не по префиксу относительно корня. Гейт по корню проекта
    // остаётся первой линией всех ручек; в multi-root точках (?featureId=/?sessionId=) он
    // повторяется по ФАКТИЧЕСКОМУ корню операции — после резолва дерева, до чтения: ссылка
    // в читаемом дереве разыменовывается там, а не в корне проекта (defect f1c60d82).
    private bool IsForeignPrivate(string root, string? path) => PrivateAttachments.IsForeignResolved(root, path, UserId);
    private IActionResult? ForeignPrivateDenied(string root, string? path) => IsForeignPrivate(root, path) ? NotFound() : null;

    // Контейнеры закрытого поддерева — корень проекта, .cc-attachments, .cc-attachments/private
    // (IsForeign их не видит: нет сегмента-автора), — для РАЗРУШИТЕЛЬНЫХ Rename/Delete — 404,
    // владельцу тоже: операция над контейнером относится ко всему private- поддереву (тот же
    // предикат, что у MCP files_rename/files-delete и WebDAV COPY/MOVE, S2d-6b-3 доработка 2).
    // Свой {id}-каталог автора и общие {guid}-каталоги — не контейнеры: с ними как раньше.
    // Читающим ручкам и листингу контейнер НЕ ставится: List(.cc-attachments) и корень —
    // открыты (закреплено тестом S2d-6b-2 доработки 1). Через ссылку — те же контейнеры
    // (IsContainerResolved, a6f5cb15).
    private IActionResult? PrivateContainerDenied(string root, string? path) =>
        PrivateAttachments.IsContainerResolved(root, path) ? NotFound() : null;

    // Гейт «в вики пишет только Хранитель» (ADR-035, решение 2): Upload и SaveFromUrl пишут
    // FileStream мимо FileService, поэтому их часть проверки стоит здесь, а не в сервисе.
    // data-корень — тот же резолв от DataPath, что у FileService/NotesService. Тип — Core.dll
    // (namespace Services.Notes, не вертикаль Notes), ADR-014 не затронут. Отказ ловится
    // существующим catch (UnauthorizedAccessException) → 403, как у соседних маршрутов.
    private void GuardNotWiki(string path)
    {
        if (WikiPaths.IsWikiPath(path, SubsystemHostingExtensions.ResolveDataDir(config)))
            throw new UnauthorizedAccessException("В вики пишет только Хранитель");
    }

    // Проставляет состояние синхронизации (direct/inherited/null) каждой записи
    private IEnumerable<FileEntry> Annotate(string projectId, IEnumerable<FileEntry> entries) =>
        entries.Select(e => e with { Synced = sync.GetSyncState(projectId, e.Path) });

    [HttpGet]
    [ProjectScope]
    public IActionResult List(string projectId, [FromQuery] string path = "")
    {
        try
        {
            // S2d-6b-2 доработка 1 (ревью f24f7533): явно названный чужой private-путь — 404, как
            // читающие ручки; зритель передаётся в List: каталог чужого автора и чужие файлы ответа
            // не дают — ни имён, ни id автора, ни количества. Без JWT — пустая строка: любое private
            // закрыто (fail-closed, как IsForeign по пустому id). Свой private — как раньше.
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var p = GetProject(projectId);
            return Ok(Annotate(projectId, files.List(p.RootPath, path, p.ShowHiddenFiles, UserId ?? "")));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        // Путь вне корня проекта — отказ, как у соседних маршрутов, а не 500
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("tree")]
    [ProjectScope]
    public IActionResult Tree(string projectId, [FromQuery] string path = "", [FromQuery] bool? showHidden = null)
    {
        try
        {
            // S2d-6b-2 доработка 1 (ревью f24f7533): то же, что для List. TreeExcludes режет ОБХОД,
            // а не названный путь — прямая ссылка внутрь .cc-attachments отдаёт содержимое, поэтому
            // фильтр по записям стоит в FileService.Tree до рекурсии: чужое поддерево не съедает
            // слоты своих по TreeMaxEntries
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var p = GetProject(projectId);
            return Ok(Annotate(projectId, files.Tree(p.RootPath, path, showHidden ?? p.ShowHiddenFiles, UserId ?? "")));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        // Путь вне корня проекта — отказ, как у соседних маршрутов, а не 500
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    [HttpGet("search")]
    [ProjectScope]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        // S2d-6b-2: зритель передаётся в поиск — чужие private-вложения не попадают в выдачу
        // ни именем, ни количеством (фильтр в FileService.Search, до потолка выдачи).
        try { return Ok(Annotate(projectId, files.Search(GetRoot(projectId), q, UserId))); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("content")]
    [ProjectScope]
    public async Task<IActionResult> GetContent(string projectId, [FromQuery] string path, [FromQuery] string? featureId = null, [FromQuery] string? sessionId = null, [FromQuery] string? taskId = null)
    {
        try
        {
            // S2d-6b-2: чужое private-вложение — 404 до резолва дерева и до чтения — первая
            // линия по корню проекта; после резолва дерева гейт повторяется по фактическому
            // корню операции (TAIL-ROOTS-GATE, см. IsForeignPrivate)
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var p = GetProject(projectId);
            // W4-5B2 (п.5.2): ?featureId= остаётся СТРОГИМ: чтение из проверенного дерева
            // эпика по правилу TreeRootForAsync, без поиска по чужим деревьям (тест
            // «featureId не ищет по чужим деревьям»).
            // W4-LINK-B (п.14, смена решения W4-LINK-B — шаг B2a): ?sessionId= — заявленное
            // дерево чата приходит анкером в ProjectTreeLocator: «дерева нет» (пустой
            // WorktreePath) или «дерево заявлено, но его нет в worktree list» (сносили после
            // мержа) — НЕ 404, а поиск по main и остальным worktree; ответ несёт branch и
            // isAnchor, фолбэк не тихий. Без параметров — корень проекта (прежнее поведение).
            // FS-B1a: ?taskId= — третий потребитель локатора: анкер — WorktreePath задачи,
            // поиск и ответ — те же, что у ветки sessionId. Задача — среди видимых владельцу
            // и этого проекта (OwnedTaskForRead), иначе 404; задача без дерева — поиск с main,
            // как в ChatTreeRootForAsync. При нескольких параметрах порядок веток:
            // featureId → sessionId → taskId (каждая называет свой контекст).
            string? root;
            string? branch = null;
            bool isAnchor = false;
            if (!string.IsNullOrEmpty(featureId))
            {
                root = await FeatureTreeRootForAsync(p, featureId);
                if (root is null) return NotFound(new { error = "Дерево не найдено" });
                // TAIL-ROOTS-GATE (DEC-ROOTS): вторая линия — по фактическому корню операции
                // (дерево эпика): ссылка в нём разыменовывается там, а не в корню проекта;
                // проверка по GetRoot выше остаётся первой линией
                if (ForeignPrivateDenied(root, path) is { } treeDenied) return treeDenied;
            }
            else if (!string.IsNullOrEmpty(sessionId))
            {
                var s = GetOwnedSession(projectId, sessionId);
                if (s is null) return NotFound(new { error = "Чат не найден" });
                var hit = await locator.LocateAsync(TenantId, p, path, ChatTreeRootForAsync(p, s));
                if (hit is null) return NotFound(new { error = "Файл не найден ни в одном дереве проекта" });
                root = hit.Root;
                branch = hit.Branch;
                isAnchor = hit.IsAnchor;
                // TAIL-ROOTS-GATE: то же по дереву, из которого реально читаем (EffectiveRoot
                // чата / выбранный локатором worktree), до чтения и до ETag
                if (ForeignPrivateDenied(root, path) is { } treeDenied) return treeDenied;
            }
            else if (!string.IsNullOrEmpty(taskId))
            {
                // FS-B1a: ?taskId= — дерево автора задачи (материал фичи лежит в её worktree,
                // а дерево эпика с ?featureId= есть у единицы эпиков). «Нет задачи» (чужая,
                // другого проекта, несуществующая) — 404; «задача без дерева» — поиск с main,
                // как в ChatTreeRootForAsync. Дальше — та же ветка локатора, что у sessionId:
                // анкер → main → прочие worktree, ответ несёт branch/isAnchor.
                var task = OwnedTaskForRead(p, taskId);
                if (task is null) return NotFound(new { error = "Задача не найдена" });
                var hitTask = await locator.LocateAsync(TenantId, p, path, task.WorktreePath);
                if (hitTask is null)
                {
                    // FS-B2: последний шаг порядка «анкер → main → прочие деревья → git show».
                    // Файла нет ни в одном живом дереве проекта, а у задачи есть WorktreeBranch:
                    // рабочий каталог снесли после мержа/отказа, а файл остался в коммите ветки,
                    // пока жива сама ветка (§7.2 п.3, вариант Б §7.3). Читаем git show по этой
                    // ветке: имя берётся ТОЛЬКО из задачи (параметр branch в запросе игнорируется),
                    // корень — корень проекта (ref живёт в общем репозитории, дерево не нужен).
                    // Только текст: бинарь — 404 reason=binary, ветки нет — 404 reason=branch_deleted
                    // (фронту подпись «удалён вместе с веткой»), прочее — обычный 404. Нет подсистемы
                    // Git — фолбэка нет, как с featureId (fail-closed).
                    if (git is not null && !string.IsNullOrWhiteSpace(task.WorktreeBranch))
                    {
                        try
                        {
                            var atBranch = await git.FileAtBranchAsync(
                                TenantId, p.RootPath, task.WorktreeBranch, path, HttpContext.RequestAborted);
                            if (atBranch.Status == GitBranchFileStatus.Found)
                                return Ok(new { content = atBranch.Content, branch = task.WorktreeBranch, fromCommit = true });
                            if (atBranch.Status == GitBranchFileStatus.BranchDeleted)
                                return NotFound(new { reason = "branch_deleted" });
                            if (atBranch.Status == GitBranchFileStatus.Binary)
                                return NotFound(new { reason = "binary" });
                        }
                        catch (GitCommandException)
                        {
                            // FS-B2 (доработка по ревью R-FS-B2): имя ветки берётся из записи задачи и
                            // не обязано быть ревизией — «a..b», «--upload-pack=x», «../x» отвергает
                            // ValidateRevision, а бросает он ДО своего try в FileAtBranchAsync
                            // (GitService.cs:869). Фолбэка нет — ниже обычный 404: ручка files/content
                            // отвечает только 200/404/403 (500 и Error в лог здесь не нужны).
                        }
                    }
                    return NotFound(new { error = "Файл не найден ни в одном дереве проекта" });
                }
                root = hitTask.Root;
                branch = hitTask.Branch;
                isAnchor = hitTask.IsAnchor;
                // TAIL-ROOTS-GATE: то же по дереву, из которого реально читаем (анкер задачи /
                // выбранный локатором worktree), до чтения и до ETag
                if (ForeignPrivateDenied(root, path) is { } treeDenied) return treeDenied;
            }
            else root = p.RootPath;

            if (IsDirectoryPath(root, path)) return NotFound();

            // S2c-1 (К8, доработка по ревью): ETag — только для текстовых файлов, которые
            // PUT content действительно может записать (не документ, не бинарный, не выше
            // MaxEtagTextBytes). Видео/аудио/документы и крупные тексты раньше не читались
            // вовсе, а полный хеш на каждый GET стоил чтения всего файла. Без тега такая
            // правка остаётся ровно как до S2c-1: PUT без If-Match.
            var absPath = FileService.SafeJoinPublic(root, path);
            if (IsEtaggableText(root, path, absPath))
                HttpContext.Response.Headers.ETag = $"\"{CanonicalSha256OfTextFile(absPath)}\"";

            // Вид файла и способ отдачи — общий с агентом устройства (вертикаль Files):
            // документ base64 для клиентского рендера, картинка, видео/аудио потоком, текст.
            var view = FileContentReader.Read(files, root, path);
            return Ok(view with { Branch = branch, IsAnchor = isAnchor });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        // Путь ведёт в несуществующую папку (или она исчезла между проверкой и чтением)
        catch (DirectoryNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Шаг B2a: ЕДИНАЯ проверка владения сессией для обеих веток (?sessionId=) — content и
    // stream. Нет владельца (нет JWT) — чат не находится: тот же приём, что в
    // GitController.RootFor (?sessionId=), а не NRE в GetOwned. Несуществующий/чужой/чужого
    // проекта — null → 404. S2d-3c: владение плюс видимость (GetOwnedVisible): чужой
    // приватный чат не виден даже владельцу проекта — отказ неотличим от «нет такого чата».
    private ClaudeHomeServer.Models.Session? GetOwnedSession(string projectId, string sessionId)
    {
        if (UserId is null) return null;
        var s = sessions.GetOwnedVisible(sessionId, UserId);
        return s is null || s.ProjectId != projectId ? null : s;
    }

    // Хвост резолва W4-LINK-B3: ЕДИНОЕ правило строгого дерева — его использует ?featureId=
    // (ветка остаётся строгой, смена решения W4-LINK-B на неё НЕ распространяется). «Дерева
    // нет» (пустой WorktreePath — норма большинства задач) или «дерево — корень проекта» —
    // читается корень. «Дерево заявлено, но не проверено» — null → 404, без тихого фолбэка
    // на main: пользователь назвал дерево явно и смотрит именно в него. Без Git сверить
    // нечем — fail-closed.
    private async Task<string?> TreeRootForAsync(ClaudeHomeServer.Models.Project project, string? worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return project.RootPath;
        if (PathNormalizer.NormalizePath(worktreePath) == PathNormalizer.NormalizePath(project.RootPath))
            return project.RootPath;
        if (git is null || UserId is null) return null;
        return await git.VerifiedFeatureTreeAsync(TenantId, project.RootPath, worktreePath);
    }

    // W4-LINK-B (п.14, смена решения W4-LINK-B — шаг B2a): цепочка заявленного дерева для
    // ?sessionId=: своё дерево чата — первично (тот же полем, по которому EffectiveRoot
    // подменяет cwd хода), цепочка по задаче — запасной вариант. Возвращает СЫРОЙ путь без
    // проверки: валидность анкера проверяет ProjectTreeLocator (IsLinkedWorktree +
    // worktree list), и «заявленное, но отсутствующее» дерево само выпадает из корней —
    // поиск продолжается по main и остальным (isAnchor=false). Дерева нет ни у чата, ни у
    // задачи — null: поиск начинается с main.
    private string? ChatTreeRootForAsync(ClaudeHomeServer.Models.Project project, ClaudeHomeServer.Models.Session session)
    {
        if (!string.IsNullOrEmpty(session.WorktreePath)) return session.WorktreePath;
        if (session.TaskId is not null) return TaskTreeRootForAsync(project, session.TaskId);
        return null;
    }

    // W4-5B2 (п.5.2): проверенное дерево эпика для чтения. Эпик — задача с id == featureId,
    // принадлежащая текущему владельцу и этому проекту. Дерево эпика решается строгой
    // TreeRootForAsync: без дерева или на корне — сам корень; заявленное, но нечислящееся —
    // null → 404. По чужим деревьям featureId не ищет. Маршрут только для чтения:
    // SaveContent featureId не принимает — запись идёт в корень проекта.
    private async Task<string?> FeatureTreeRootForAsync(ClaudeHomeServer.Models.Project project, string featureId)
    {
        if (tasks is null || UserId is null) return null;
        var epic = tasks.GetByOwner(TenantId).FirstOrDefault(t => t.Id == featureId);
        if (epic is null || epic.ProjectId != project.Id) return null;
        return await TreeRootForAsync(project, epic.WorktreePath);
    }

    // FS-B1a: ЕДИНАЯ точка поиска задачи для чтения — её зовут ветка ?taskId= и цепочка
    // задачи в ChatTreeRootForAsync. Задача ищется среди видимых владельцу (GetByOwner), её
    // ProjectId обязан совпасть с проектом маршрута: чужая задача или задача другого проекта
    // неотличима от несуществующей — null (клиенту 404, без утечки). Нет подсистемы Tasks —
    // fail-closed, как у featureId.
    private ClaudeHomeServer.Models.TaskItem? OwnedTaskForRead(ClaudeHomeServer.Models.Project project, string taskId)
    {
        if (tasks is null || UserId is null) return null;
        var task = tasks.GetByOwner(TenantId).FirstOrDefault(t => t.Id == taskId);
        if (task is null || task.ProjectId != project.Id) return null;
        return task;
    }

    // W4-LINK-B: дерево, заявленное задачей, когда у самого чата своего дерева нет. Задачи
    // нет (несуществующая/чужая/другой проект) — null. Путь не проверяется здесь: анкер
    // проверяет локатор (см. ChatTreeRootForAsync).
    private string? TaskTreeRootForAsync(ClaudeHomeServer.Models.Project project, string taskId) =>
        OwnedTaskForRead(project, taskId)?.WorktreePath;

    // Абсолютный путь документа с проверкой, что это просматриваемый документ (pdf/docx/xlsx/pptx).
    // null → клиенту 400 (не документ). Иначе — безопасный абсолютный путь для markitdown.
    // Visio исключён: markitdown его не конвертирует.
    private string? DocumentAbsPath(string projectId, string path)
    {
        if (files.GetDocumentInfo(path) is not { } d || d.Kind == "visio") return null;
        return FileService.SafeJoinPublic(GetRoot(projectId), path);
    }

    // Текст файла для ИИ-действий (суть/выжимка/теги): бинарный документ (pdf/docx/xlsx/pptx) →
    // markitdown; текстовый файл (.md/.txt/.csv/код) → читаем как есть; прочие бинарные → null.
    private async Task<string?> GetAiTextAsync(string projectId, string path, CancellationToken ct)
    {
        var root = GetRoot(projectId);
        if (IsDirectoryPath(root, path)) return null;   // папка — не документ (клиенту 400)
        if (files.GetDocumentInfo(path) is { } d)
        {
            if (d.Kind == "visio") return null; // markitdown не конвертирует Visio
            var abs = FileService.SafeJoinPublic(root, path);
            return System.IO.File.Exists(abs) ? await docAi.ConvertAsync(abs, ct) : null;
        }
        return files.IsBinaryFile(root, path) ? null : files.ReadFile(root, path);
    }

    // Конвертация документа в Markdown (markitdown, без модели). Возвращает { markdown }.
    [HttpPost("document/convert")]
    [ProjectScope]
    public async Task<IActionResult> DocumentConvert(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            // S2d-6b-2: конвертация отдаёт содержимое документа — читающая ручка
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            if (DocumentAbsPath(projectId, path) is not { } abs)
                return BadRequest(new { error = "Это не документ (pdf/docx/xlsx/pptx)" });
            var md = await docAi.ConvertAsync(abs, ct);
            return md is null
                ? StatusCode(502, new { error = "Не удалось конвертировать документ (markitdown недоступен?)" })
                : Ok(new { markdown = md });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Краткое содержание документа (локальная модель / claude).
    [HttpPost("document/summary")]
    [ProjectScope]
    public async Task<IActionResult> DocumentSummary(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            // S2d-6b-2: выжимка строится из содержимого файла — читающая ручка
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var text = await GetAiTextAsync(projectId, path, ct);
            if (text is null) return BadRequest(new { error = "Файл не поддерживается (нужен документ или текст)" });
            var summary = await docAi.SummaryAsync(UserId, text, ct);
            return summary is null ? StatusCode(502, new { error = "Не удалось обработать файл" }) : Ok(new { summary });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Структурная выжимка: решения, даты, участники, action items.
    [HttpPost("document/extract")]
    [ProjectScope]
    public async Task<IActionResult> DocumentExtract(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            // S2d-6b-2: структурная выжимка строится из содержимого файла — читающая ручка
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var text = await GetAiTextAsync(projectId, path, ct);
            if (text is null) return BadRequest(new { error = "Файл не поддерживается (нужен документ или текст)" });
            var r = await docAi.ExtractAsync(UserId, text, ct);
            return r is null
                ? StatusCode(502, new { error = "Не удалось обработать файл" })
                : Ok(new { decisions = r.Decisions, dates = r.Dates, people = r.People, actionItems = r.ActionItems });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Трансформация ЛЮБОГО файла в Markdown с СОХРАНЕНИЕМ (markitdown, без модели).
    // targetDir (относительно корня проекта) — куда положить .md; пусто → рядом с исходником.
    // Возвращает { savedPath, markdown }.
    // S2c-8: дверь на WriteContent — редактор общего проекта конвертирует и сохраняет Markdown
    // в проекте арендатора (ключ хранилищ — корень проекта, право — по вызывающему).
    [HttpPost("document/to-markdown")]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    public async Task<IActionResult> ToMarkdown(string projectId, [FromBody] ToMarkdownRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { error = "Нужен путь файла" });
        try
        {
            // S2d-6b-2: по ИСТОЧНИКУ — конвертация отдаёт его содержимым (целевой .md — свой
            // путь и своя дверь, п.1/F-4 ниже них не меняется)
            if (ForeignPrivateDenied(GetRoot(projectId), req.Path) is { } foreign) return foreign;
            var root = GetRoot(projectId);
            var abs = FileService.SafeJoinPublic(root, req.Path);
            if (!System.IO.File.Exists(abs))
                return NotFound(new { error = "Файл не найден" });

            // Имя целевого .md — по исходному имени; каталог — targetDir или рядом с исходником.
            // Расчёт — до конвертации: S2d-6b-5 (дефект e5d55007) отказывает чужую цель
            // (targetDir под чужим private) до любого эффекта, в том числе до работы markitdown
            // и до CreateDirectory целевой папки.
            var baseName = System.IO.Path.GetFileNameWithoutExtension(req.Path) + ".md";
            var dir = string.IsNullOrWhiteSpace(req.TargetDir)
                ? (System.IO.Path.GetDirectoryName(req.Path.Replace('\\', '/')) ?? "")
                : req.TargetDir.Replace('\\', '/').Trim('/');
            var targetRel = string.IsNullOrEmpty(dir) ? baseName : $"{dir}/{baseName}";
            if (ForeignPrivateDenied(root, targetRel) is { } targetDenied) return targetDenied;

            var md = await docAi.ConvertAsync(abs, ct);
            if (md is null)
                return StatusCode(502, new { error = "Не удалось конвертировать файл (markitdown недоступен или формат не поддержан)" });

            // Опционально: восстановить Markdown-разметку локальной моделью (для pdf без структуры)
            if (req.Enhance) md = await docAi.EnhanceMarkdownAsync(UserId, md, ct);

            // Целевая папка может не существовать (пользователь указал новую) — создаём
            if (!string.IsNullOrEmpty(dir)) files.CreateDirectory(root, dir);

            // S2c-10 (хвост ревью R-S2c-8, карточка 1ac0075a п.1): запись целевого .md — под тем же
            // замком пути, что SaveContent/Rename/Delete, и по тому же контракту If-Match: без них
            // ToMarkdown молча затирал параллельную правку того же .md — ни хеша, ни 409. Конвертация
            // — вне замка: сериализовать нужно запись, а не работу конвертера (соседняя правка не
            // обязана ждать markitdown). Без If-Match — прежнее поведение (перезапись), но под замком.
            // F-4 (п.2): тот же отказ, что у Upload — по ЦЕЛЕВОМУ .md: конверт пишет поверх него,
            // не редактируя то, что в нём лежит. Конвертация — вне замка и вне проверки (работать
            // над отказом нет смысла, но и чужую правку она не трогает).
            var absTarget = FileService.SafeJoinPublic(root, targetRel);
            var ifMatch = RequestIfMatch();
            var sem = LockFor(absTarget);
            await sem.WaitAsync();
            try
            {
                if (await UncommittedDenialForEditorAsync(projectId, root, targetRel, absTarget) is { } denied) return denied;
                if (ifMatch is not null)
                {
                    // Тот же канонический хеш, что у GET/SaveContent: назван клиентом версии —
                    // пишем только её; параллельная правка того же .md проигрывает по хешу (409)
                    var current = IsEtaggableText(root, targetRel, absTarget) ? CanonicalSha256OfTextFile(absTarget) : null;
                    if (current != ifMatch) return Conflict(FileConflictBodyFor(root, targetRel, absTarget, current));
                }
                WriteAtomic(root, targetRel, absTarget, md);
            }
            finally { sem.Release(); }
            // S3-3b: записано — в журнал по ЦЕЛЕВОМУ .md (источник не менялся, его не сдаём)
            RecordHumanEdit(projectId, root, targetRel);
            return Ok(new { savedPath = targetRel, markdown = md });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Теги документа по содержимому.
    [HttpPost("document/tags")]
    [ProjectScope]
    public async Task<IActionResult> DocumentTags(string projectId, [FromQuery] string path, CancellationToken ct)
    {
        try
        {
            // S2d-6b-2: теги строятся из содержимого файла — читающая ручка
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var text = await GetAiTextAsync(projectId, path, ct);
            if (text is null) return BadRequest(new { error = "Файл не поддерживается (нужен документ или текст)" });
            var tags = await docAi.TagsAsync(UserId, text, ct);
            return tags is null ? StatusCode(502, new { error = "Не удалось обработать файл" }) : Ok(new { tags });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // S2c-8 (разрез f899602f §6/§7): двери записи содержимого переведены поимённо с Write на
    // WriteContent — редактор общего проекта пишет файлы проекта арендатора. Ключ хранилищ —
    // корень проекта (GetRoot/Annotate/файловые вызовы идут от него, не от id вызывающего),
    // право — по вызывающему через [ProjectScope], автор — только из sub JWT контроллера.
    // На Write остаются: Revert, SaveFromUrl, office-config/discard/force-save, office-callback
    // и удаление папки — всё прочее закрыто default-deny; снимок дверей держит
    // WriteContentDoorsGuardTests.
    [HttpPut("content")]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    public async Task<IActionResult> SaveContent(string projectId, [FromQuery] string path, [FromBody] SaveContentRequest req, [FromQuery] string? taskId = null)
    {
        try
        {
            // FS-B1a (инвариант): материал по задаче только читается — запись через якорь
            // задачи не существует. Параметр в запросе записи — некорректный запрос: 400
            // до любого эффекта (до гейта private, до проверки пути, до замка). featureId в
            // PUT, как раньше, не несёт дерева и не отвергается (запись идёт в корень).
            if (!string.IsNullOrEmpty(taskId))
                return BadRequest(new { error = "Материал по задаче только читается" });
            // S2d-6b-5 (дефект e5d55007): запись в чужое private — 404 до любого эффекта:
            // до проверки пути и до замка. Обе ветки (с If-Match и без) — за гейтом.
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var root = GetRoot(projectId);
            if (IsDirectoryPath(root, path)) return NotFound();

            // S2c-1 (К8): без If-Match — ровно как раньше (обратная совместимость). С If-Match —
            // «сравнить и записать» под замком на полный путь: без замка два сохранения
            // проверяются по одним и тем же старым байтам и проходят оба.
            // Доживка S2c-1 (дефект найден на S2c-F2a): обе ветки — под одним и тем же замком:
            // 200-ответ обязан нести новый ETag (SetContentETagAfterWrite). Доработка по ревью
            // 00f9451e: хеш — по тексту запроса, не по перечитанному файлу: замок сериализует
            // только SaveContent, и писатель мимо замка не должен менять тег нашего ответа.
            var ifMatch = RequestIfMatch();
            var abs = FileService.SafeJoinPublic(root, path);
            var sem = LockFor(abs);
            await sem.WaitAsync();
            try
            {
                if (ifMatch is null)
                {
                    files.WriteFile(root, path, req.Content);
                    SetContentETagAfterWrite(root, path, abs, req.Content);
                    RecordHumanEdit(projectId, root, path);   // S3-3b: запись состоялась
                    return Ok();
                }

                // Тот же канонический хеш, что выдаёт GET (доработка S2c-1 п.4): сверяем то,
                // что клиент действительно видел. Нетекстовому/сверх потолка тега нет ни у
                // клиента, ни здесь — If-Match для него не может совпасть.
                var current = IsEtaggableText(root, path, abs) ? CanonicalSha256OfTextFile(abs) : null;
                if (current != ifMatch)
                    return Conflict(FileConflictBodyFor(root, path, abs, current));
                WriteAtomic(root, path, abs, req.Content);
                SetContentETagAfterWrite(root, path, abs, req.Content);
                RecordHumanEdit(projectId, root, path);   // S3-3b: запись по названной версии состоялась
                return Ok();
            }
            finally { sem.Release(); }
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
        // Файл исчез между сверкой хеша и телом 409 (правка агента мимо замка допустима)
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // If-Match по К8: сильный ETag в кавычках. Нет заголовка или "*" — предусловия нет
    // («как сейчас»); несколько значений — первое (фронт шлёт одно). Слабый W/ отдельно
    // не разбираем: с префиксом он не совпадёт с хешем — отказ, как требует RFC для If-Match.
    private string? RequestIfMatch()
    {
        var raw = Request.Headers["If-Match"].ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "*") return null;
        var value = raw.Split(',')[0].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        return value;
    }

    // Замок на полный путь файла на процесс (S2c-1): один SemaphoreSlim на нормализованный
    // путь. Ключ — PathNormalizer (нижний регистр): на Windows это то же самое файл-имя
    // в разном регистре, на Linux — лишняя сериализация разных файлов, не ошибка.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new();
    // internal — чтобы тест мог читать состояние ИМЕННО этого замка (тот же приём, что у
    // internal MaxEtagTextBytes ниже): «запись держит замок пути» проверяется состоянием замка
    // в момент, когда запись доказанно внутри своей критической секции, а не гаданием по таймерам.
    internal static SemaphoreSlim LockFor(string absPath) => LockByKey(PathNormalizer.NormalizePath(absPath));
    private static SemaphoreSlim LockByKey(string normalizedKey) =>
        _pathLocks.GetOrAdd(normalizedKey, _ => new SemaphoreSlim(1, 1));

    // Два пути одной операции (rename) к замкам в одном порядке: все многозамковые двери берут
    // их сортируя по ключу — цикл между ними невозможен (одна пара путей даёт одну и ту же
    // очередь независимо от направления rename). Равные ключи складываются в один:
    // self-rename не самоблокируется.
    private static (string First, string Second) OrderedLockKeys(string a, string b)
    {
        var x = PathNormalizer.NormalizePath(a);
        var y = PathNormalizer.NormalizePath(b);
        return string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);
    }

    // Потолок размера текста для ETag (доработка S2c-1 п.3): выше потолка полный хеш файла
    // не считаем — тег нужен для правки, а не для архива; PUT таких файлов идёт без If-Match,
    // как до S2c-1. internal — чтобы тест писал тот же потолок, что и код.
    internal const long MaxEtagTextBytes = 10 * 1024 * 1024;

    // «Текстовый для ETag» — ровно то, что отдаёт текстом FileContentReader.Read и что может
    // записать PUT content: не документ (GetDocumentInfo), не бинарный (IsBinaryFile), не выше
    // потолка. Один признак на выдачу тега (GET) и на сверку (PUT): If-Match клиенту доступен
    // только для файлов, которые действительно сохраняются по нему.
    private bool IsEtaggableText(string root, string path, string abs) =>
        System.IO.File.Exists(abs)
        && new System.IO.FileInfo(abs).Length <= MaxEtagTextBytes
        && files.GetDocumentInfo(path) is null
        && !files.IsBinaryFile(root, path);

    // «Канонический хеш текста» — ОДНА функция над СТРОКОЙ: снять BOM-преамбулу (U+FEFF в
    // начале) → UTF-8 без BOM → SHA-256. Её зовут: GET и сверка If-Match (через обёртку ниже,
    // по декодированному содержимому файла) и тег ответа PUT — по тексту запроса (доработка
    // 00f9451e). BOM снимается и у текста запроса: запись пишет U+FEFF как EF BB BF, GET при
    // декоде его срезает — без снятия тег ответа расходился бы с тегом последующего GET и
    // давал ложный 409 на цепочке. Снимается ровно одна преамбула: физический BOM уже срезан
    // декодером, оставшийся в строке U+FEFF — это следующий физический BOM после записи.
    private static string CanonicalSha256OfText(string text)
    {
        // U+FEFF явным кодом точки: экранированный литерал BOM нечитаем в диффе и тонет в редакторах
        if (text.Length > 0 && text[0] == (char)0xFEFF) text = text[1..];
        var canonical = System.Text.Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(canonical)).ToLowerInvariant();
    }

    // Канонический хеш ФАЙЛА — для GET и сверки If-Match: тот же декод, что у FileService.ReadFile
    // (UTF-8, физический BOM срезается при декоде), и тот же хелпер над строкой. Хеш сырых байт
    // диска не годился: файл с BOM дал бы тег, которого нет ни в content из GET, ни в теле,
    // которое вернёт PUT, — unchanged-файл отвечал 409 на собственный же тег.
    private static string CanonicalSha256OfTextFile(string absPath)
    {
        using var reader = new System.IO.StreamReader(
            System.IO.File.OpenRead(absPath), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return CanonicalSha256OfText(reader.ReadToEnd());
    }

    // Доживка S2c-1 (дефект найден на S2c-F2a): 200-ответ сохранения обязан нести новый ETag —
    // по образцу PUT заметок (NotesController.cs:504). Без него следующая сохранка клиента идёт
    // по старому тегу и получает 409 на собственную правку (ложный конфликт). Доработка по
    // ревью 00f9451e: тег — канонический хеш ТЕКСТА ЗАПРОСА (тот же CanonicalSha256OfText,
    // что даёт тег GET после декода файла), а не перечитанного файла: замок на путь
    // сериализует только SaveContent, и писатель мимо замка (revert/upload/офис/агент) может
    // вклиниться между нашей записью и ответом — перечитывание выдало бы тег ЧУЖОЙ версии,
    // и следующий PUT по нему молча затёр бы чужую правку без 409. По тексту запроса тег
    // ответа — всегда хеш нашей версии: если на диске уже чужая запись, следующий PUT по
    // этому тегу даёт настоящий 409. Признак etaggable — по файлу, как у GET: для
    // не-etaggable (бинарные/документы/сверх потолка) заголовка нет.
    private void SetContentETagAfterWrite(string root, string path, string abs, string content)
    {
        if (IsEtaggableText(root, path, abs))
            HttpContext.Response.Headers.ETag = $"\"{CanonicalSha256OfText(content)}\"";
    }

    // Запись через временный файл в том же каталоге и перемещение (File.Move overwrite),
    // а не на месте: читатель не видит недозаписанный файл, а «сравнить и записать»
    // атомарны на пути. Кодировка та же, что у FileService.WriteFile (UTF-8 без BOM).
    // Имя временного файла — TreeExcludes.AtomicTempName: наблюдатель глушит его события
    // тем же признаком (доработка S2c-1 п.2). Уведомление подписчиков — явное, как в
    // Upload/SaveFromUrl (запись мимо FileService.Write*).
    private void WriteAtomic(string root, string path, string abs, string content)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(abs)!, TreeExcludes.AtomicTempName(Path.GetFileName(abs)));
        try
        {
            System.IO.File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
            // После подмены права файла достаются временному: Move сохраняет их от tmp, а не от
            // цели. Без переноса 0600 становилось 0644, бит исполнения пропадал, read-only и
            // hidden на Windows становились обычным файлом (доработка S2c-1 п.1). Если цели
            // нет, ничего не переносим: новый файл получает дефолт ОС.
            CopyFileAttributesToTemp(abs, tmp);
            System.IO.File.Move(tmp, abs, overwrite: true);
        }
        catch { try { System.IO.File.Delete(tmp); } catch { /* временный файл best-effort */ } throw; }
        files.NotifyMutated(root, path, FileMutationKind.Write);
    }

    // Перенос прав и атрибутов существующей цели на временный файл: перед подменой, но после
    // записи содержимого — файл с правами 0400 сам не открылся бы на запись. Unix-права
    // читаются на Unix (на Windows GetUnixFileMode бросает PlatformNotSupportedException),
    // атрибуты NTFS (read-only, hidden) — на Windows. Отказ (цель исчезла между проверкой и
    // chmod) запись не роняет: файл важнее режима, но отказ не молчит — он в логе.
    private void CopyFileAttributesToTemp(string abs, string tmp)
    {
        if (!System.IO.File.Exists(abs)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                System.IO.File.SetAttributes(tmp, System.IO.File.GetAttributes(abs));
            else
                System.IO.File.SetUnixFileMode(tmp, System.IO.File.GetUnixFileMode(abs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Не перенесли права файла при атомарной записи {Path}", abs);
        }
    }

    // Атомарная запись загрузки (S2c-10): тот же порядок, что WriteAtomic (содержимое → перенос
    // прав → подмена), но стримингом, а не строкой: Upload даёт до 100 МБ, буферизовать их в
    // памяти нельзя. Тот же временный файл (TreeExcludes) — наблюдатель глушит его события.
    private async Task WriteAtomicFromFileAsync(string root, string path, string abs, IFormFile file)
    {
        var tmp = Path.Combine(Path.GetDirectoryName(abs)!, TreeExcludes.AtomicTempName(Path.GetFileName(abs)));
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await file.CopyToAsync(fs);
            CopyFileAttributesToTemp(abs, tmp);
            System.IO.File.Move(tmp, abs, overwrite: true);
        }
        catch { try { System.IO.File.Delete(tmp); } catch { /* временный файл best-effort */ } throw; }
        // Запись мимо FileService.Write* — уведомляем подписчиков (синк знаний) явно
        files.NotifyMutated(root, path, FileMutationKind.Write);
    }

    // Тело 409 по К8: актуальное состояние файла на момент отказа — хеш, содержимое
    // (текст — та же граница «текстовый», что у FileContentReader; двоичные и документы — null),
    // время последней правки (mtime — серверное состояние).
    // author — из серверного состояния: его источник (буфер коммиттера «путь → кто, когда»)
    // появляется в S3-3; здесь сервер автора не знает — null (авторство из тела запроса
    // не принимается — по разрез §2). source — форма ChatRef (К5) в коде ещё нет (S2d-4),
    // индекса «последний правивший чат» нет — null.
    // code — причина отказа: "file_changed" (сверка If-Match, К8) или "file_uncommitted"
    // (F-4: чужая незакоммиченная правка, которую редактору затирать нельзя). Тело — одно и то
    // же: актуальное состояние файла, чтобы клиент показал, что именно он мог бы потерять.
    private FileConflictResponse FileConflictBodyFor(string root, string path, string abs, string? currentSha,
        string code = "file_changed")
    {
        var isText = currentSha is not null && files.GetDocumentInfo(path) is null && !files.IsBinaryFile(root, path);
        var mtime = System.IO.File.Exists(abs)
            ? new DateTimeOffset(DateTime.SpecifyKind(System.IO.File.GetLastWriteTimeUtc(abs), DateTimeKind.Utc))
            : (DateTimeOffset?)null;
        return new FileConflictResponse(
            code,
            isText ? files.ReadFile(root, path) : null,
            currentSha,
            Author: null,
            ChangedAt: mtime,
            Source: null);
    }

    // Право владельца (Write) по вызывающему — тот же тест, что был в ветке папки Delete (S2c-8):
    // Write в общем проекте нет ни у редактора, ни у читателя (ProjectRight.Write — «только
    // владелец»), в личном проекте вызывающий — владелец. id вызывающего входит только в
    // запрошенное право, ключом хранилищ не служит (снимок ProjectActionCallerKey).
    private bool CallerIsProjectOwner(string projectId) =>
        access.Resolve(UserId, projectId, ProjectRight.Write) is not null;

    // F-4 (п.2, разрез 4bc3d04f §3): не-владелец (редактор) удаляет или перезаписывает только
    // ЧИСТЫЙ отслеживаемый git файл. Незакоммиченное — правка агента или другого человека,
    // которой ещё не коммитил ни один из них: для редактора она необратима (Revert остаётся на
    // Write, у него её нет), поэтому молча затирать или удалять её нельзя → 409 file_uncommitted.
    // Владелец и личный проект — как раньше (первый же тест).
    // Значение — из того же лёгкого статуса, что рисует панель «Изменения» (StatusPathsAsync:
    // staged + unstaged + untracked, -uall): путь в любом из множеств — незакоммичен. Файла нет
    // — создавать нечего, проверять нечего. Не-репозиторий или нет Git-подсистемы —
    // «незакоммиченного» нет как класса: прежнее поведение (без git судить нечем, дверь не
    // закрывается вслепую). Игнорируемые .gitignore файлы в этом статусе не видны — для
    // проверки они равны чистым отслеживаемым (оценено в отчёте F-4).
    // Проверка — ВНУТРИ замка пути: запись под замком делает файл незакоммиченным, и второй
    // редактор того же пути уже видит чужую правку и получает 409, а не затирает её молча.
    private async Task<IActionResult?> UncommittedDenialForEditorAsync(
        string projectId, string root, string rel, string abs)
    {
        if (CallerIsProjectOwner(projectId)) return null;
        if (!System.IO.File.Exists(abs)) return null;
        if (git is null) return null;
        var status = await git.StatusPathsAsync(TenantId, root);
        if (!status.IsRepo) return null;
        // F-4 доработка 1 (ревью ccca96a7): ключ — канонический путь от корня, а не сырая
        // строка запроса: алиасы «./mod.txt», «sub/../mod.txt» разрешаются SafeJoin в тот же
        // файл (abs уже канонический), но со строкой запроса git-путь не совпадал — проверка
        // молча пропускалась, и редактор терял чужую незакоммиченную правку.
        // GetRelativePath приводит алиасы к тому же виду путей, что отдаёт git status.
        var key = Normalize(System.IO.Path.GetRelativePath(root, abs));
        if (!status.Staged.Concat(status.Unstaged).Concat(status.Untracked).Any(f => Normalize(f.Path) == key))
            return null;
        var current = IsEtaggableText(root, rel, abs) ? CanonicalSha256OfTextFile(abs) : null;
        return Conflict(FileConflictBodyFor(root, rel, abs, current, "file_uncommitted"));
    }

    // S3-3b (разрез 4bc3d04f §3): УСПЕШНАЯ правка файла человеком — в журнал правок (S3-3):
    // путь относительный, корень — корень проекта, автор — sub из JWT вызывающего. Вызов строго
    // после успеха ручки: при 4xx/5xx правки не было, и журнал её видеть не должен. Отбирать
    // путь (файл, а не каталог; свой корень; автор — из участников) и молчать на личном проекте
    // и не-репозитории — правило самого журнала (HumanEditCommitter), здесь не повторяем его.
    private void RecordHumanEdit(string projectId, string root, string relPath)
    {
        var userId = UserId;
        if (humanEdits is null || string.IsNullOrEmpty(userId)) return;
        humanEdits.Record(projectId, root, relPath, userId);
    }

    // Файлы под каталогом (абсолютные пути). Папку журналу не сдают: pathspec каталога
    // индексирует всё поддерево, включая незакоммиченную работу агента (оговорка в
    // HumanEditCommitter), поэтому за удаление и перенос папки в журнал идут ЕЁ ФАЙЛЫ.
    // Снимается ДО операции: после удаления/переноса старых путей уже нет. Обход — по тому же
    // списку исключений, что у дерева (TreeExcludes), и без чужих вложенных репозиториев
    // (HasOwnGit): под них журнал не ходит, их правки — не правка этого проекта.
    private static List<string> FilesUnderDir(string absDir)
    {
        var result = new List<string>();
        void Walk(string dir)
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (TreeExcludes.Contains(Path.GetFileName(d)) || TreeExcludes.HasOwnGit(d)) continue;
                Walk(d);
            }
            foreach (var f in Directory.GetFiles(dir)) result.Add(f);
        }
        Walk(absDir);
        return result;
    }

    private static string RelOf(string root, string abs) => Path.GetRelativePath(root, abs).Replace('\\', '/');

    // Удаление папки для журнала — списком её файлов (снятых до удаления). Ветка доходит только
    // до владельца (гейт в Delete), но правило одно: путь папки вместо файлов не сдаётся.
    private void RecordDeletedDir(string projectId, string root, List<string> underDir)
    {
        foreach (var f in underDir) RecordHumanEdit(projectId, root, RelOf(root, f));
    }

    // Перенос папки — старым и новым путём каждого её файла: новый путь строится из относительного
    // пути внутри папки, поэтому вложенность сохраняется, а путь папки в журнал не входит.
    private void RecordRenamedDir(string projectId, string root, string absOld, string absNew, List<string> underDir)
    {
        foreach (var f in underDir)
        {
            RecordHumanEdit(projectId, root, RelOf(root, f));
            RecordHumanEdit(projectId, root, RelOf(root, Path.Combine(absNew, Path.GetRelativePath(absOld, f))));
        }
    }

    [HttpGet("diff")]
    [ProjectScope]
    public async Task<IActionResult> GetDiff(string projectId, [FromQuery] string path)
    {
        try
        {
            // S2d-6b-2: дифф отдаёт содержимое файла по относительному пути — читающая ручка
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            return Ok(new DiffResponse(await files.GetDiffAsync(GetRoot(projectId), path)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Панель «Изменения»: для присланных путей — какие ЕЩЁ чаты проекта их меняли (бейдж
    // в строке файла + список «Также меняли» в шапке диффа). Ключи ответа — РОВНО присланные
    // строки (в исходном регистре/разделителях) — сопоставление с индексом идёт по lowercase,
    // поэтому caller получает узнаваемые пути git status без перекодировки на фронте.
    [HttpPost("changed-by")]
    [ProjectScope]
    public async Task<IActionResult> ChangedBy(string projectId, [FromBody] ChangedByRequest req)
    {
        try
        {
            GetProject(projectId); // существование проекта (для чужого и несуществующего — 404 решил [ProjectScope])
            var paths = req.Paths ?? [];
            var byNormalized = await fileSessions.GetForProjectAsync(projectId, paths.Select(Normalize).ToList());
            var response = new Dictionary<string, List<SessionRef>>();
            foreach (var raw in paths)
            {
                if (!byNormalized.TryGetValue(Normalize(raw), out var refs)) continue;
                // Доработка S2d-4a (вердикт R-S2d-4a): выдача панели — человеческая, поэтому чата,
                // которого вызывающий не видит, здесь нет ни id, ни названием — та же точка
                // SessionManager.IsVisibleTo, что у GetVisibleByProject. Машинная сторона индекса
                // (GetSessionPathsAsync → CommitAttributionService) работает с чужими приватными
                // чатами законно и здесь не тронута. Путь, который меняли только невидимые чаты,
                // остаётся без ключа: для фронта это то же «никто не менял», что и пустой список.
                var visible = refs.Where(r => sessions.IsVisibleTo(r.SessionId, UserId)).ToList();
                if (visible.Count > 0) response[raw] = visible;
            }
            return Ok(new { files = response });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    [HttpPost("revert")]
    [ProjectScope(Right = ProjectRight.Write)]
    public IActionResult Revert(string projectId, [FromBody] PathRequest req)
    {
        try
        {
            // S2d-6b-5: откат чужого private — 404 до git-операции (как отказ чтения:
            // чужой файл неотличим от несуществующего)
            if (ForeignPrivateDenied(GetRoot(projectId), req.Path) is { } denied) return denied;
            var ok = files.RevertFile(GetRoot(projectId), req.Path);
            return ok ? Ok() : BadRequest(new { error = "Не удалось откатить (не git-репозиторий?)" });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        // Отказ вики (RevertFile бросает до git) — 403, как у соседних маршрутов, а не 500
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // S2c-8: на WriteContent (см. правило у SaveContent).
    [HttpPost("create")]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    public IActionResult CreateFile(string projectId, [FromBody] CreateFileRequest req)
    {
        try
        {
            // S2d-6b-5: создание файла в чужом private — 404 до создания
            if (ForeignPrivateDenied(GetRoot(projectId), req.Path) is { } denied) return denied;
            var root = GetRoot(projectId);
            files.CreateFile(root, req.Path, req.Content ?? "");
            RecordHumanEdit(projectId, root, req.Path);   // S3-3b: коллизия имени — выше, до этой строки
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
        // Коллизия имён: существующий файл не перезаписываем молча
        catch (InvalidOperationException) { return Conflict(new { error = "exists" }); }
    }

    // S2c-8: на WriteContent — редактору открыто создание папок (удаление папки остаётся на Write,
    // см. Delete).
    [HttpPost("mkdir")]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    public IActionResult CreateDir(string projectId, [FromBody] PathRequest req)
    {
        // S2d-6b-5: каталог под чужим автором — 404 до CreateDirectory. Сам контейнер
        // (.cc-attachments, private/) — не «чужой» и не разрушителен: mkdir над ним, как раньше
        try
        {
            if (ForeignPrivateDenied(GetRoot(projectId), req.Path) is { } denied) return denied;
            files.CreateDirectory(GetRoot(projectId), req.Path);
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // S2c-8: на WriteContent (см. правило у SaveContent). Rename берёт тот же замок пути, что
    // SaveContent (S2c-1): операция касается двух путей — оба замка берутся в нормализованном
    // порядке, и две перестановки одних путей получают тот же порядок — цикла нет. Без замка
    // гонка rename↔save молча дала бы два файла (или перенос уже записанной версии) — data loss.
    // If-Match — тот же контракт, что у SaveContent: назван клиентом версии — переименовываем
    // только её; параллельная запись того же пути проигрывает по хешу (409), а не затирает.
    // F-4 (пп.3–4): существующая цель — 409 «exists», а не 500; переименование и перенос папки
    // — только владельцу (403), как её удаление. Незакоммиченное содержимое при rename не
    // затирается: оно переезжает целиком, поэтому п.2 здесь не нужен.
    [HttpPost("rename")]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    public async Task<IActionResult> Rename(string projectId, [FromBody] RenameRequest req)
    {
        try
        {
            // S2d-6b-5: ОБА пути — чужое private и контейнеры (корень, .cc-attachments,
            // .cc-attachments/private) — 404 до замков и до любого эффекта. Контейнерный
            // предикат — тот же, что у MCP files_rename и WebDAV MOVE; владельцу тоже.
            // Свой {id}-каталог автора и общие {guid}-каталоги — не контейнеры, как раньше.
            if (ForeignPrivateDenied(GetRoot(projectId), req.OldPath) is { } dOld) return dOld;
            if (ForeignPrivateDenied(GetRoot(projectId), req.NewPath) is { } dNew) return dNew;
            if (PrivateContainerDenied(GetRoot(projectId), req.OldPath) is { } cOld) return cOld;
            if (PrivateContainerDenied(GetRoot(projectId), req.NewPath) is { } cNew) return cNew;
            var root = GetRoot(projectId);
            var absOld = FileService.SafeJoinPublic(root, req.OldPath);
            var absNew = FileService.SafeJoinPublic(root, req.NewPath);
            var (firstKey, secondKey) = OrderedLockKeys(absOld, absNew);
            var semFirst = LockByKey(firstKey);
            await semFirst.WaitAsync();
            SemaphoreSlim? semSecond = null;
            try
            {
                if (secondKey != firstKey)
                {
                    semSecond = LockByKey(secondKey);
                    await semSecond.WaitAsync();
                }
                if (!System.IO.File.Exists(absOld) && !Directory.Exists(absOld)) return NotFound();
                // F-4 (п.4, решение Андрея 01.11): переименование и перенос ПАПКИ редактору
                // закрыты — тот же тест права, что у удаления папки. Массовая операция без 409:
                // у папки нет версии (тега ей сервер не выдавал), а под её именем — чужие правки
                // всего дерева, которые молча потерять нельзя.
                if (Directory.Exists(absOld) && !CallerIsProjectOwner(projectId))
                    return StatusCode(403, new { error = "Переименование и перенос папки — только владелец проекта", code = ProjectScopeAttribute.ErrorCode });
                // F-4 (п.3): коллизия имени — 409, а не 500. На существующую цель File.Move бросает
                // IOException (.NET Core 3.0+), Directory.Move — то же: раньше это выходило 500.
                // Та же форма отказа, что у CreateFile («exists»): цель существует. Тот же путь
                // (self-rename) — не коллизия: эта ветка ведёт себя как раньше.
                if (firstKey != secondKey && (System.IO.File.Exists(absNew) || Directory.Exists(absNew)))
                    return Conflict(new { error = "exists" });
                var ifMatch = RequestIfMatch();
                if (ifMatch is not null)
                {
                    // Тот же канонический хеш, что у GET/SaveContent; папке тег не выдавался —
                    // If-Match на папку не может совпасть, это 409, а не тихий перенос
                    var current = IsEtaggableText(root, req.OldPath, absOld) ? CanonicalSha256OfTextFile(absOld) : null;
                    if (current != ifMatch) return Conflict(FileConflictBodyFor(root, req.OldPath, absOld, current));
                }
                // S3-3b: журнал берёт только файлы. Файлы папки снимаются ДО переноса (после
                // него старых путей уже нет): в журнал идут старый и новый путь каждого файла,
                // а не путь папки. Файл — свои старый и новый путь.
                var inner = Directory.Exists(absOld) ? FilesUnderDir(absOld) : null;
                files.Rename(root, req.OldPath, req.NewPath);
                if (inner is null)
                {
                    RecordHumanEdit(projectId, root, req.OldPath);
                    RecordHumanEdit(projectId, root, req.NewPath);
                }
                else RecordRenamedDir(projectId, root, absOld, absNew, inner);
                // Комментарии к переименованному/перенесённому документу (или документам
                // внутри папки) следуют за новым путём — привязка не сиротеет
                if (notes is not null)
                {
                    try { notes.RewriteAnnotationTargets(TenantId, projectId, req.OldPath, projectId, req.NewPath, prefix: true); }
                    catch (Exception ex) { logger.LogWarning(ex, "Перепись привязок комментариев при rename {Old}", req.OldPath); }
                }
                return Ok();
            }
            finally
            {
                semSecond?.Release();
                semFirst.Release();
            }
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // S2c-8: дверь открыта редактору только для файла (WriteContent). Папка остаётся на Write:
    // ветка папки — явная проверка права вызывающего (редактору 403, владельцу как раньше).
    // Файловая ветка — под тем же замком пути, что SaveContent (S2c-1), с If-Match там, где
    // клиент назвал версию. F-4 (п.2): незакоммиченный файл редактору отказан (409
    // file_uncommitted): до S3-3 (буфера коммиттера) его удаление необратимо — откат через
    // Revert остаётся на Write, у редактора его нет.
    [HttpDelete]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    // Секция destructive: на делегированном ходу агент не удаляет данные (раньше — срезанием
    // секции из состава инструментов wsp, что перезапускало процесс CLI со всеми MCP)
    [DenyOnDelegatedTurn("Удаление файлов")]
    public async Task<IActionResult> Delete(string projectId, [FromQuery] string path)
    {
        try
        {
            // S2d-6b-5: чужое private (файл и каталог) — 404; контейнеры (корень проекта,
            // .cc-attachments, .cc-attachments/private) — 404 и владельцу: удаление контейнера
            // сносит всё private-поддерево (тот же предикат, что у MCP files_delete и WebDAV).
            // Гейт — до ветки файл/папка, до замка, до files.Delete.
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            if (PrivateContainerDenied(GetRoot(projectId), path) is { } container) return container;
            var root = GetRoot(projectId);
            if (IsDirectoryPath(root, path))
                return CallerIsProjectOwner(projectId)
                    ? await DeleteUnderLockAsync(projectId, root, path, ifMatch: null)
                    : StatusCode(403, new { error = "Удаление папки — только владелец проекта", code = ProjectScopeAttribute.ErrorCode });
            return await DeleteUnderLockAsync(projectId, root, path, RequestIfMatch());
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Файловая ветка Delete под замком пути (S2c-8): If-Match — тот же контракт, что у SaveContent;
    // папка удаляется без сверки версии — тега папке сервер не выдавал.
    // F-4 (п.2): перед сверкой версии — отказ редактору на незакоммиченном файле (он важнее:
    // If-Match здесь о версии, а отказа причина — в праве на чужую правку). Ветка папки доходит
    // сюда только от владельца (гейт в Delete), для неё проверка проходит по построению.
    private async Task<IActionResult> DeleteUnderLockAsync(string projectId, string root, string path, string? ifMatch)
    {
        var abs = FileService.SafeJoinPublic(root, path);
        var sem = LockFor(abs);
        await sem.WaitAsync();
        try
        {
            if (await UncommittedDenialForEditorAsync(projectId, root, path, abs) is { } denied) return denied;
            if (ifMatch is not null)
            {
                if (!System.IO.File.Exists(abs)) return NotFound();
                var current = IsEtaggableText(root, path, abs) ? CanonicalSha256OfTextFile(abs) : null;
                if (current != ifMatch) return Conflict(FileConflictBodyFor(root, path, abs, current));
            }
            // S3-3b: файл — своим путём; папка (до неё доходит только владелец) — списком её
            // файлов, снятых до удаления: путь папки в pathspec затянул бы и чужую работу.
            var inner = Directory.Exists(abs) ? FilesUnderDir(abs) : null;
            files.Delete(root, path);
            if (inner is null) RecordHumanEdit(projectId, root, path);
            else RecordDeletedDir(projectId, root, inner);
            return NoContent();
        }
        finally { sem.Release(); }
    }

    // S2c-8: на WriteContent (см. правило у SaveContent).
    [HttpPost("upload")]
    [ProjectScope(Right = ProjectRight.WriteContent)]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 МБ
    public async Task<IActionResult> Upload(string projectId, [FromQuery] string path = "", IFormFile? file = null)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Файл не выбран или пустой" });

        try
        {
            var root = GetRoot(projectId);
            // Path.GetFileName защищает от path-сегментов в имени файла (../evil)
            var safeName = Path.GetFileName(file.FileName);
            if (string.IsNullOrEmpty(safeName))
                return BadRequest(new { error = "Некорректное имя файла" });

            var relativePath = string.IsNullOrEmpty(path) ? safeName : $"{path}/{safeName}";

            // S2d-6b-5 (дефект e5d55007): каталог назначения под чужим private — 404 до
            // SafeJoin/CreateDirectory: отказанная загрузка не должна создавать каталог назначения
            if (ForeignPrivateDenied(root, relativePath) is { } foreign) return foreign;

            // S2c-10 (хвост ревью R-S2c-8, карточка 1ac0075a п.1): та же дверь, что у SaveContent:
            // замок на путь, If-Match по тому же контракту, запись через временный файл. Без замка
            // FileMode.Create на месте молча затирал параллельную правку того же пути — ни хеша,
            // ни 409. Без If-Match — прежнее поведение (перезапись), но под замком.
            var safePath = FileService.SafeJoinPublic(root, relativePath);
            // До CreateDirectory и до стрима: под data/wiki/ не должна появиться ни папка, ни файл
            GuardNotWiki(safePath);
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            // F-4 (п.2): перед сверкой версии — отказ редактору на незакоммиченном целевом файле:
            // загрузка не редактирует то, что клиент видел, а затирает его целиком.
            var ifMatch = RequestIfMatch();
            var sem = LockFor(safePath);
            await sem.WaitAsync();
            try
            {
                if (await UncommittedDenialForEditorAsync(projectId, root, relativePath, safePath) is { } denied) return denied;
                if (ifMatch is not null)
                {
                    var current = IsEtaggableText(root, relativePath, safePath) ? CanonicalSha256OfTextFile(safePath) : null;
                    if (current != ifMatch) return Conflict(FileConflictBodyFor(root, relativePath, safePath, current));
                }
                // Х-10 (тот же класс, что починен в личном маршруте Х-7, коммит 2a7899b74):
                // имя занято каталогом — File.Move в атомарной записи бросает IOException,
                // раньше это выходило 500. Тот же 409 «exists», что у create/rename. Ловуем
                // ровно вокруг записи: до и после неё — прежнее поведение.
                try { await WriteAtomicFromFileAsync(root, relativePath, safePath, file); }
                catch (IOException) { return Conflict(new { error = "exists" }); }
            }
            finally { sem.Release(); }
            RecordHumanEdit(projectId, root, relativePath);   // S3-3b: загрузка записана
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Шаг B2a: у stream тот же необязательный ?sessionId= и тот же резолв дерева, что у
    // content: заявленное дерево чата — анкер локатора, «его нет» — поиск по main и
    // остальным worktree. Чужой/несуществующий чат — 404 (общая GetOwnedSession). Без
    // параметра — прежнее поведение: корень проекта.
    [HttpGet("stream")]
    [ProjectScope]
    public async Task<IActionResult> Stream(string projectId, [FromQuery] string path, [FromQuery] string? sessionId = null, [FromQuery] string? taskId = null)
    {
        // FS-B1b: sessionId и taskId — два разных анкера; оба сразу — неоднозначно (в отличие
        // от content, где порядок веток задан), поэтому здесь отказ
        if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(taskId))
            return BadRequest(new { error = "Укажите либо sessionId, либо taskId" });
        try
        {
            // S2d-6b-2: поток отдаёт содержимое файла — читающая ручка (тот же 404, что у content)
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            string abs;
            if (!string.IsNullOrEmpty(taskId))
            {
                // FS-B1b: ?taskId= — анкер WorktreePath задачи через общий OwnedTaskForRead,
                // дальше то же, что у sessionId: локатор и вторая линия гейта по корню чтения
                var p = GetProject(projectId);
                var task = OwnedTaskForRead(p, taskId);
                if (task is null) return NotFound(new { error = "Задача не найдена" });
                var hit = await locator.LocateAsync(TenantId, p, path, task.WorktreePath);
                if (hit is null) return NotFound();
                if (ForeignPrivateDenied(hit.Root, path) is { } treeDenied) return treeDenied;
                abs = hit.FullPath;
            }
            else if (!string.IsNullOrEmpty(sessionId))
            {
                var p = GetProject(projectId);
                var s = GetOwnedSession(projectId, sessionId);
                if (s is null) return NotFound(new { error = "Чат не найден" });
                var hit = await locator.LocateAsync(TenantId, p, path, ChatTreeRootForAsync(p, s));
                if (hit is null) return NotFound();
                // TAIL-ROOTS-GATE (DEC-ROOTS): вторая линия — по дереву, где локатор нашёл файл:
                // PhysicalFile отдаёт разыменованный путь, и ссылка в worktree читалась бы
                // мимо гейта по корню проекта
                if (ForeignPrivateDenied(hit.Root, path) is { } treeDenied) return treeDenied;
                abs = hit.FullPath;
            }
            else
            {
                abs = FileService.SafeJoinPublic(GetRoot(projectId), path);
                if (!System.IO.File.Exists(abs)) return NotFound();
            }
            var mime = FileContentReader.StreamMime(path);
            return PhysicalFile(abs, mime, enableRangeProcessing: true);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Шаг B2b (разрез bc27cbee §1 потребитель 3, §4): пакетный резолв относительных путей для
    // «ленивого» индекса фронта. Hit — то же правило, что у content/stream (ProjectTreeLocator),
    // анкер — заявленное дерево чата по sessionId. Ответ только по найденным путям: ненайденный
    // остаётся в отрицательном кэше фронта. Лимит 50 — батчинг фронта (F1b): больше — отказ,
    // не тихая обрезка.
    [HttpPost("locate")]
    [ProjectScope]
    public async Task<IActionResult> Locate(string projectId, [FromBody] LocateRequest req, CancellationToken ct)
    {
        var paths = req.Paths ?? [];
        if (paths.Count > MaxLocatePaths)
            return BadRequest(new { error = $"Не более {MaxLocatePaths} путей за запрос" });
        // FS-B1b: два анкера сразу — неоднозначно (как у stream)
        if (!string.IsNullOrEmpty(req.SessionId) && !string.IsNullOrEmpty(req.TaskId))
            return BadRequest(new { error = "Укажите либо sessionId, либо taskId" });
        try
        {
            var p = GetProject(projectId);
            string? anchor = null;
            if (!string.IsNullOrEmpty(req.TaskId))
            {
                // FS-B1b: анкер — дерево задачи (общий OwnedTaskForRead); задача без дерева —
                // поиск с main, как у content
                var task = OwnedTaskForRead(p, req.TaskId);
                if (task is null) return NotFound(new { error = "Задача не найдена" });
                anchor = task.WorktreePath;
            }
            else if (!string.IsNullOrEmpty(req.SessionId))
            {
                var s = GetOwnedSession(projectId, req.SessionId);
                if (s is null) return NotFound(new { error = "Чат не найден" });
                anchor = ChatTreeRootForAsync(p, s);
            }
            // S2d-6b-2: чужие private-вложения выпадают из ЗАПРОСА до локатора: в ответе нет ни их
            // имён, ни их количества (своё private автора остаётся; потолок п.50 — по присланному
            // пакету, как раньше: отказ зависит от запроса, а не от выдачи)
            var mine = paths.Where(r => !IsForeignPrivate(p.RootPath, r)).ToList();
            return Ok(await locator.LocateManyAsync(TenantId, p, mine, anchor, ct));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Отдаёт байты файла OnlyOffice Document Server (без Authorization — DS не знает API-ключ).
    // Защита: проверка download-токена, который DS получает через office-config.
    [HttpGet("office-download")]
    [AllowAnonymous]
    public IActionResult OfficeDownload(string projectId, [FromQuery] string path, [FromQuery] string token)
    {
        var ownerId = jwt.ValidateOfficeToken(token, projectId, path);
        if (ownerId is null)
            return Unauthorized();

        try
        {
            // S2d-6b-2: читающая ручка (отдаёт байты файла); зрителя нет JWT, а подписанный
            // office-токен выдал конкретный человек — им и меряется: чужое private — 404,
            // своё — как раньше (иначе анонимный вход глушил бы и своё, и office-config не
            // смог бы открыть свой документ). Корень нужен каноническому гейту (a6f5cb15):
            // он разыменяет ссылки, поэтому берётся до решения, а не после.
            var root = GetProjectByOfficeToken(projectId, ownerId).RootPath;
            if (PrivateAttachments.IsForeignResolved(root, path, ownerId)) return NotFound();
            var safePath = FileService.SafeJoinPublic(root, path);
            if (!System.IO.File.Exists(safePath)) return NotFound();
            var mime = files.GetDocumentInfo(path)?.Mime ?? "application/octet-stream";
            // Стриминг с диска вместо буферизации всего файла в памяти
            return PhysicalFile(safePath, mime);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Возвращает конфиг для DocsAPI.DocEditor: URL сервера DS и параметры документа.
    [HttpGet("office-config")]
    [ProjectScope(Right = ProjectRight.Write)]
    public IActionResult OfficeConfig(string projectId, [FromQuery] string path, [FromQuery] string mode = "view", [FromQuery] string? cacheKey = null)
    {
        try
        {
            // S2d-6b-2: конфиг редактора называет имя, тип и время файла и выдаёт download-URL
            // с его содержимым — ручка метаданных чужого private закрыта тем же 404
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, path);
            var modTime = System.IO.File.GetLastWriteTimeUtc(safePath).Ticks;
            var fileName = Path.GetFileName(path);
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            // Visio DS умеет только просматривать — режим редактирования не выдаём
            if (files.GetDocumentInfo(path)?.Kind == "visio")
                mode = "view";

            var serverUrl = config["OnlyOffice:ServerUrl"] ?? "http://localhost:8090";
            var backendUrl = config["OnlyOffice:BackendUrl"] ?? "http://host.docker.internal:5000";
            // Токен привязан к владельцу+projectId+path — office-download/callback сверят владельца
            var token = jwt.IssueOfficeToken(UserId!, projectId, path);

            // Ключ документа: хэш пути + время изменения (DS кеширует по ключу).
            // cacheKey позволяет сбросить кеш OO после сохранения через callback.
            var keyInput = cacheKey != null
                ? $"{projectId}/{path}/{modTime}/{cacheKey}"
                : $"{projectId}/{path}/{modTime}";
            var key = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(keyInput)))
                [..20];

            var docKey = $"{key}-{mode}";
            // Запоминаем edit-ключ текущей сессии для возможного office-discard
            if (mode == "edit")
                _activeEditKeys[$"{projectId}/{path}"] = docKey;

            var downloadUrl = $"{backendUrl}/api/projects/{projectId}/files/office-download" +
                              $"?path={Uri.EscapeDataString(path)}&token={Uri.EscapeDataString(token)}";
            var callbackUrl = mode == "edit"
                ? $"{backendUrl}/api/projects/{projectId}/files/office-callback" +
                  $"?path={Uri.EscapeDataString(path)}&token={Uri.EscapeDataString(token)}"
                : (string?)null;

            return Ok(new
            {
                serverUrl,
                document = new
                {
                    fileType = ext,
                    key = docKey,
                    title = fileName,
                    url = downloadUrl,
                },
                editorConfig = new
                {
                    mode,
                    lang = "ru",
                    callbackUrl,
                    customization = new
                    {
                        uiTheme = "theme-claude-home",
                        anonymous = new { request = false },
                        compactToolbar = true,
                        help = false,
                        chat = false,
                        integrationMode = "embed",
                        toolbarHideFileName = true,
                    },
                },
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (FileNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // SSRF-защита callback: скачиваем документ только с хостов OnlyOffice
    // (хост ServerUrl + явный список OnlyOffice:AllowedCallbackHosts)
    private bool IsAllowedCallbackSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(config["OnlyOffice:ServerUrl"], UriKind.Absolute, out var srv))
            allowed.Add(srv.Host);
        foreach (var h in config.GetSection("OnlyOffice:AllowedCallbackHosts").Get<string[]>() ?? [])
            allowed.Add(h);
        return allowed.Contains(uri.Host);
    }

    // Имя HttpClient для Command API OnlyOffice — под ним в Program.cs зарегистрирован тихий логгер
    public const string OnlyOfficeCommandClient = "onlyoffice-command";

    // Активные edit-ключи: "{projectId}/{path}" → docKey
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _activeEditKeys = new();
    // Ключи для игнорирования в callback (пользователь нажал «Отмена»)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _discardedKeys = new();
    // TCS для ожидания forceSave callback: docKey → TCS
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> _forceSavePending = new();

    // Вызывается OnlyOffice DS после сохранения документа пользователем.
    // status=2: документ готов, url — временная ссылка на изменённый файл.
    // status=6: принудительное сохранение (forceSave).
    [HttpPost("office-callback")]
    [AllowAnonymous]
    public async Task<IActionResult> OfficeCallback(
        string projectId,
        [FromQuery] string path,
        [FromQuery] string token,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        var ownerId = jwt.ValidateOfficeToken(token, projectId, path);
        if (ownerId is null)
            return Unauthorized();

        OOCallbackPayload? payload;
        try
        {
            payload = await System.Text.Json.JsonSerializer.DeserializeAsync<OOCallbackPayload>(
                Request.Body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "office-callback: не удалось разобрать тело запроса для {Path}", path);
            return Ok(new { error = 1, message = "bad json" });
        }

        // Пользователь нажал «Отмена» — пропускаем сохранение
        if (payload?.Key != null && _discardedKeys.TryRemove(payload.Key, out _))
            return Ok(new { error = 0 });

        if (payload?.Status is 2 or 6 && payload.Url != null)
        {
            if (!IsAllowedCallbackSource(payload.Url))
                return Ok(new { error = 1, message = "url host not allowed" });
            try
            {
                var root = GetProjectByOfficeToken(projectId, ownerId).RootPath;
                var client = httpClientFactory.CreateClient("proxy");
                var bytes = await client.GetByteArrayAsync(payload.Url, ct);
                files.WriteFileBytes(root, path, bytes);

                // Разблокируем ожидающий office-force-save запрос
                if (payload.Key != null && _forceSavePending.TryRemove(payload.Key, out var tcs))
                    tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                if (payload.Key != null && _forceSavePending.TryRemove(payload.Key, out var tcs))
                    tcs.TrySetException(new Exception(ex.Message));
                return Ok(new { error = 1, message = ex.Message });
            }
        }

        return Ok(new { error = 0 });
    }

    // Вызывается фронтом при нажатии «Отмена» в режиме редактирования OO.
    // Помечает edit-ключ как «выбросить» и откатывает файл через git.
    [HttpPost("office-discard")]
    [ProjectScope(Right = ProjectRight.Write)]
    public IActionResult OfficeDiscard(string projectId, [FromQuery] string path)
    {
        try
        {
            // S2d-6b-8 (хвост ревью R-S2d-6b-5, п.1): ручка откатывает файл через git по клиентскому
            // пути — та же пишущая операция, что Revert. Раньше инвариант «ни одна пишущая ручка не
            // меняет файл под чужим private» держился здесь только git-исключением .cc-attachments
            // (git не знает такого pathspec → отказ); теперь — тем же гейтом, до снятия edit-ключа
            // и до RevertFile. Своё private автора — открыто, как в Revert.
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var root = GetRoot(projectId);
            if (_activeEditKeys.TryRemove($"{projectId}/{path}", out var editKey))
                _discardedKeys.TryAdd(editKey, 0);
            files.RevertFile(root, path); // false → не git-репо, игнорируем
            return Ok();
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Принудительно сохраняет документ через OO Command API и ждёт callback.
    // Вызывается фронтом при нажатии «Сохранить» — быстрее чем ждать savetimeoutdelay (5 с).
    [HttpPost("office-force-save")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> OfficeForceSave(
        string projectId,
        [FromQuery] string path,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        try { GetProject(projectId); }
        catch (KeyNotFoundException) { return NotFound(); }

        var fileKey = $"{projectId}/{path}";
        if (!_activeEditKeys.TryGetValue(fileKey, out var docKey))
            return Ok(new { ok = false, reason = "no-session" });

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _forceSavePending[docKey] = tcs;

        // Вызов CommandService через внутренний адрес OO DS, а не через публичный URL (YARP+TLS)
        var ooInternalUrl = config["ReverseProxy:Clusters:onlyoffice:Destinations:default:Address"] ?? "http://localhost:8090";
        try
        {
            // Именованный клиент с тихим логгером (Program.cs): OnlyOffice поднят не всегда,
            // отказ здесь штатно ловится ниже и переходит в ожидание таймаута.
            var client = httpClientFactory.CreateClient(OnlyOfficeCommandClient);
            var payload = System.Text.Json.JsonSerializer.Serialize(new { c = "forcesave", key = docKey });
            await client.PostAsync($"{ooInternalUrl}/coauthoring/CommandService.ashx",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);
        }
        catch (Exception ex)
        {
            // Command API недоступен — ждём таймаут
            logger.LogWarning(ex, "office-force-save: Command API недоступен для {Path}", path);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(10));
        linked.Token.Register(() => tcs.TrySetCanceled());

        try
        {
            await tcs.Task;
            return Ok(new { ok = true });
        }
        catch (OperationCanceledException)
        {
            _forceSavePending.TryRemove(docKey, out _);
            return Ok(new { ok = false, reason = "timeout" });
        }
    }

    // Возвращает Unix-время последней записи файла (мс) — для polling после OO-сохранения.
    [HttpGet("office-version")]
    [ProjectScope]
    public IActionResult OfficeVersion(string projectId, [FromQuery] string path)
    {
        try
        {
            // S2d-6b-2: версия файла — его метаданные; чужое private не существует и здесь
            if (ForeignPrivateDenied(GetRoot(projectId), path) is { } denied) return denied;
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, path);
            if (!System.IO.File.Exists(safePath))
                return NotFound();
            var ms = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(safePath)).ToUnixTimeMilliseconds();
            return Ok(new { ms });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("save-from-url")]
    [ProjectScope(Right = ProjectRight.Write)]
    [RequestSizeLimit(200 * 1024 * 1024)] // 200 МБ для видео
    public async Task<IActionResult> SaveFromUrl(
        string projectId,
        [FromBody] SaveFromUrlRequest req,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http"))
            return BadRequest(new { error = "Некорректный URL" });

        // S2d-6b-5: цель под чужим private — 404 до сетевой работы и до CreateDirectory:
        // скачивать ответ ради отказанной цели не за чем
        if (ForeignPrivateDenied(GetRoot(projectId), req.Path) is { } foreign) return foreign;

        // SSRF: не даём серверу скачивать с внутренних адресов (localhost, приватные сети,
        // cloud metadata). Клиент "safe-download" без авто-редиректов — редирект на приватный
        // хост не обойдёт проверку (вернётся 3xx → EnsureSuccessStatusCode → 502).
        if (!await SsrfGuard.IsPubliclyRoutableAsync(uri, ct))
            return BadRequest(new { error = "URL указывает на недопустимый адрес" });

        try
        {
            var root = GetRoot(projectId);
            var safePath = FileService.SafeJoinPublic(root, req.Path);
            // До запроса: скачивать тело ради пути, который всё равно откажем, незачем
            GuardNotWiki(safePath);
            var client = httpClientFactory.CreateClient("safe-download");

            // Стриминг в файл вместо буферизации всего ответа в памяти
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            if (resp.Content.Headers.ContentLength > MaxSaveFromUrlBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = $"Файл больше {MaxSaveFromUrlBytes / (1024 * 1024)} МБ" });

            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            // Х-10: имя занято каталогом — прямой FileStream бросает при этом не IOException,
            // а UnauthorizedAccessException (раньше это выходило 403). Явная проверка цели,
            // как в Rename: коллизия с каталогом — 409 «exists», та же форма, что у create;
            // настоящий отказ права записи остаётся 403.
            if (Directory.Exists(safePath)) return Conflict(new { error = "exists" });
            var exceeded = false;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fs = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                // Content-Length может отсутствовать — копируем с подсчётом и обрываем при превышении
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxSaveFromUrlBytes) { exceeded = true; break; }
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (exceeded)
            {
                try { System.IO.File.Delete(safePath); }
                catch (Exception ex) { logger.LogWarning(ex, "save-from-url: не удалось удалить недокачанный файл {Path}", safePath); }
                return StatusCode(StatusCodes.Status413PayloadTooLarge,
                    new { error = $"Файл больше {MaxSaveFromUrlBytes / (1024 * 1024)} МБ" });
            }

            // Запись мимо FileService.Write* — уведомляем подписчиков (синк знаний) явно
            files.NotifyMutated(root, req.Path, FileMutationKind.Write);
            return Ok(new { path = req.Path });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
        catch (UnauthorizedAccessException) { return StatusCode(403); }
    }

    // Предельный размер скачивания save-from-url (совпадает с RequestSizeLimit)
    private const long MaxSaveFromUrlBytes = 200L * 1024 * 1024;

    // Лимит пакета POST files/locate (шаг B2b): фронт батчит не более 50 «ленивых» путей (F1b)
    private const int MaxLocatePaths = 50;
}

public record ChangedByRequest(List<string>? Paths);

// Контракт тела 409 по К8 (docs/research/shared-projects-plan-v2-2026-10.md, §3).
// author — { userId?, displayName }: заполняется из буфера коммиттера (S3-3), сейчас null.
// source — ChatRef (К5): форма ещё не в коде (S2d-4), сейчас null.
public record FileConflictResponse(
    string Code,
    string? Content,
    string? Sha256,
    FileAuthorRef? Author,
    DateTimeOffset? ChangedAt,
    object? Source);
public record FileAuthorRef(string? UserId, string DisplayName);
public record LocateRequest(List<string>? Paths, string? SessionId, string? TaskId = null);
public record SaveFromUrlRequest(string Url, string Path);
public record ToMarkdownRequest(string Path, string? TargetDir = null, bool Enhance = false);
public record OOCallbackPayload(int Status, string? Url, string? Key);
