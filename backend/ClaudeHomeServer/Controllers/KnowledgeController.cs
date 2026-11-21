using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Knowledge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.Controllers;

[ProjectCapability(ProjectCapabilityArea.ServerContent)]
[ProjectScope]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/knowledge")]
[SubsystemUsage("knowledge")]
public class KnowledgeController(
    ProjectManager projects,
    KnowledgeService knowledge,
    FileService files,
    WorkspaceKnowledgeStore workspaceStore,
    ProjectKnowledgeSyncService sync,
    IHubContext<SessionHub> hub) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;
    private string Username => User.FindFirstValue(System.Security.Claims.ClaimTypes.Name) ?? UserId;

    // Состав базы знаний проекта изменился — сообщаем всем вкладкам владельца.
    // Раньше этого не было, и панель «Знания» доносила свежий список наверх пропом,
    // из-за чего пометки в дереве файлов зависели от того, открыта ли панель рядом.
    // Тот же канал, что у KnowledgeBasesController: событие knowledge_changed.
    private Task Broadcast(string? datasetId) =>
        hub.Clients.Group("user_" + UserId).SendAsync("message", new KnowledgeChangedMessage("doc_changed", datasetId));

    // Проект запроса; доступ проверил [ProjectScope] (чужой — 404)
    private Project? GetScopedProject(string projectId) => projects.GetById(projectId);

    // ── детская граница чтения («Знаний», Б20a, ADR-041 ред. 8 решение 81) ──────────
    // Ребёнку открыты ровно эти две ручки (список и поиск) и ровно своего проекта-предмета.
    // Сама граница — в одной точке, ChildSubjectAccess (Х-W4-47): «ребёнок ли» по стору,
    // «свой ли это предмет» и отказ not_child_subject. Прочие ручки класса (индексация, теги,
    // удаление) [ChildAllowed] не имеют намеренно: ребёнок базу не наполняет и не правит —
    // её наполняет синк папки (решение 66), и гейт И1 отсекает их до тела ручки.

    // GET /api/projects/{id}/knowledge — статус БЗ + список документов
    // Ребёнку: [ChildAllowed] + «свой проект-предмет» (решение 81); взрослому — без изменений.
    [ChildAllowed]
    [HttpGet]
    public async Task<IActionResult> GetStatus(string projectId)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();
        if (ChildSubjectAccess.IsChild(HttpContext, UserId) && !ChildSubjectAccess.IsOwnSubjectProject(p, UserId))
            return ChildSubjectAccess.NotOwnSubject("Знания");

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId))
            return Ok(new { datasetId = (string?)null, documents = Array.Empty<object>(), total = 0 });

        // Сверка при открытии панели БЗ: подхватить правки, сделанные пока сервер/ватчеры не смотрели
        sync.QueueSync(p.RootPath);

        try
        {
            var docs = await knowledge.ListAllDocumentsAsync(wk.DifyDatasetId);
            var tags = wk.DocumentTags ?? new Dictionary<string, List<string>>();
            var docsDto = docs.Data.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                indexingStatus = d.IndexingStatus,
                // Текст ошибки индексации — причина, по которой документ выпал из поиска
                error = d.Error,
                tags = tags.TryGetValue(d.Name, out var t) ? t : [],
            });
            return Ok(new { datasetId = wk.DifyDatasetId, documents = docsDto, total = docs.Total });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    // GET /api/projects/{id}/knowledge/search — семантический поиск по базе знаний проекта
    // Ребёнку: [ChildAllowed] + «свой проект-предмет» (решение 81); взрослому — без изменений.
    [ChildAllowed]
    [HttpGet("search")]
    public async Task<IActionResult> Search(string projectId, [FromQuery] string q = "", [FromQuery] int topK = 8)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();
        // Детская проверка — до пустой ветки q: чужой проект остаётся отказан и без запроса
        if (ChildSubjectAccess.IsChild(HttpContext, UserId) && !ChildSubjectAccess.IsOwnSubjectProject(p, UserId))
            return ChildSubjectAccess.NotOwnSubject("Знания");

        if (string.IsNullOrWhiteSpace(q))
            return Ok(new { items = Array.Empty<object>() });

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId))
            return Ok(new { items = Array.Empty<object>(), hint = "знания не проиндексированы" });

        try
        {
            var chunks = await knowledge.RetrieveAsync(wk.DifyDatasetId, q.Trim(), Math.Clamp(topK, 1, 20));
            return Ok(new
            {
                items = chunks.Select(c => new { content = c.Content, score = c.Score, documentName = c.DocumentName }),
            });
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // POST /api/projects/{id}/knowledge/index — индексировать файл (lazy-создаёт датасет)
    [HttpPost("index")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> IndexFile(string projectId, [FromBody] IndexFileRequest req)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();

        // S2d-6b-6 (хвост S2d-6b-5, доработка по ревью R-S2d-6b-5): явно названное чужое private-
        // вложение (S2d-6b-1) — 404 до индексации, неотличимо от несуществующего файла — тот же
        // приём, что у читающих и пишущих ручек FilesController (S2d-6b-2/6b-5). Своё private,
        // общие вложения (.cc-attachments/{guid}/…) и обычные файлы — как раньше.
        // Гейт канонический (хвост a6f5cb15): путь через симлинк в корне проекта — тот же чужой
        // private на диске, и индексироваться он не должен.
        if (PrivateAttachments.IsForeignResolved(p.RootPath, req.RelativePath, UserId)) return NotFound();

        if (!KnowledgeService.IsKnowledgeIndexable(req.RelativePath))
            return BadRequest(new { error = $"Формат файла не поддерживается для индексирования: {Path.GetExtension(req.RelativePath)}" });

        try
        {
            // Идемпотентная индексация через синк-сервис: имя документа = относительный путь,
            // повторный вызов обновляет документ (не плодит дубль), файл попадает под отслеживание
            var (datasetId, doc) = await sync.IndexPathAsync(p, Username, req.RelativePath);
            var wk = workspaceStore.GetByPath(p.RootPath);
            var docName = req.RelativePath.Replace('\\', '/');
            var docTags = wk?.DocumentTags?.TryGetValue(docName, out var dt) == true ? dt : new List<string>();
            await Broadcast(datasetId);
            return Ok(new { datasetId, document = new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus, tags = docTags } });
        }
        catch (FileNotFoundException) { return NotFound(new { error = "Файл не найден" }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // GET /api/projects/{id}/knowledge/tags — теги всех документов
    [HttpGet("tags")]
    public IActionResult GetTags(string projectId)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();
        var wk = workspaceStore.GetByPath(p.RootPath);
        return Ok(wk?.DocumentTags ?? new Dictionary<string, List<string>>());
    }

    // PUT /api/projects/{id}/knowledge/tags — задать теги для документа
    [HttpPut("tags")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> SetTags(string projectId, [FromBody] SetTagsRequest req)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();

        var tags = req.Tags ?? [];

        // Сохраняем в Dify если датасет настроен и передан documentId
        var wk = workspaceStore.GetByPath(p.RootPath);
        if (!string.IsNullOrEmpty(wk?.DifyDatasetId) && !string.IsNullOrEmpty(req.DocumentId))
        {
            try
            {
                await knowledge.UpdateDocumentTagsAsync(wk.DifyDatasetId, req.DocumentId, tags);
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { error = ex.Message });
            }
        }

        // Локальный кэш в WorkspaceKnowledge — для быстрого отображения без запросов к Dify
        var workspace = workspaceStore.GetOrCreate(p.RootPath);
        workspace.DocumentTags ??= new Dictionary<string, List<string>>();
        if (tags.Count == 0)
            workspace.DocumentTags.Remove(req.DocumentName);
        else
            workspace.DocumentTags[req.DocumentName] = tags;
        workspaceStore.Save(workspace);

        await Broadcast(wk?.DifyDatasetId);
        return NoContent();
    }

    // POST /api/projects/{id}/knowledge/index-folder — рекурсивно индексировать папку
    [HttpPost("index-folder")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> IndexFolder(string projectId, [FromBody] IndexFileRequest req)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();

        // Папка заметок индексируется отдельной базой знаний (NotesKnowledgeService,
        // per-owner dataset) — файловая индексация notes/ дала бы двойной индекс.
        var rel = (req.RelativePath ?? "").Replace('\\', '/').Trim('/');
        if (rel.Equals("notes", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("notes/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Заметки индексируются отдельной базой знаний" });

        // S2d-6b-6 (хвост S2d-6b-5): индексация контейнера относится ко всему closed- поддереву —
        // тот же разбор, что у Rename/Delete (S2d-6b-3 доработка 2), поэтому сам .cc-attachments
        // и .cc-attachments/private — 404. Корень проекта здесь НЕ контейнер: обход TreeExcludes
        // в .cc-attachments не спускается, и индексация всего проекта — штатная цель метода,
        // поэтому IsAttachmentsContainer без корня (IsAttachmentsContainerResolved), а не
        // IsContainerResolved. Канонический вариант (хвост a6f5cb15): «L» → .cc-attachments и
        // «L/private» в корне проекта — те же контейнеры на диске, и индексировать их нельзя.
        if (PrivateAttachments.IsAttachmentsContainerResolved(p.RootPath, rel)) return NotFound();

        // Явно названный чужой private — 404 до обхода: неотличим от несуществующей папки/файла,
        // тот же приём, что у ручек FilesController (S2d-6b-2/6b-5); через симлинк — та же запись.
        if (PrivateAttachments.IsForeignResolved(p.RootPath, rel, UserId)) return NotFound();

        IEnumerable<FileEntry> allFiles;
        try
        {
            // S2d-6b-6: зритель передаётся в обход — чужое private-поддерево выпадает из результата
            // тем же фильтром FileService.Tree, что у читающих ручек (S2d-6b-2 доработка 1): защита
            // обхода за пределами названного пути. Своё private и обычные файлы — как раньше.
            allFiles = files.Tree(p.RootPath, req.RelativePath ?? "", p.ShowHiddenFiles, UserId);
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound(new { error = "Папка не найдена" });
        }

        // Исключаем notes/ при индексации папки-родителя (напр. корня проекта)
        var indexable = allFiles
            .Where(f => !f.IsDirectory && KnowledgeService.IsKnowledgeIndexable(f.Path)
                        && !IsInNotesVault(f.Path))
            .ToList();

        if (indexable.Count == 0)
            return Ok(new { indexed = 0, skipped = 0, documents = Array.Empty<object>() });

        var indexed = new List<object>();
        int skipped = 0;

        foreach (var file in indexable)
        {
            try
            {
                // Идемпотентная индексация: повторный прогон папки обновляет документы, не плодит дубли
                var (_, doc) = await sync.IndexPathAsync(p, Username, file.Path);
                var wk = workspaceStore.GetByPath(p.RootPath);
                var docTags = wk?.DocumentTags?.TryGetValue(file.Path, out var dt) == true ? dt : new List<string>();
                indexed.Add(new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus, tags = docTags });
            }
            catch
            {
                skipped++;
            }
        }

        if (indexed.Count > 0) await Broadcast(workspaceStore.GetByPath(p.RootPath)?.DifyDatasetId);
        return Ok(new { indexed = indexed.Count, skipped, documents = indexed });
    }

    // DELETE /api/projects/{id}/knowledge/documents/{documentId}
    [HttpDelete("documents/{documentId}")]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> DeleteDocument(string projectId, string documentId)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId)) return NotFound();

        try
        {
            await knowledge.DeleteDocumentAsync(wk.DifyDatasetId, documentId);
            // Снять файл с отслеживания — иначе синк пересоздаст документ по живому файлу
            sync.ForgetDocument(p.RootPath, documentId);
            await Broadcast(wk.DifyDatasetId);
            return NoContent();
        }
        catch (HttpRequestException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // DELETE /api/projects/{id}/knowledge — удалить датасет
    [HttpDelete]
    [ProjectScope(Right = ProjectRight.Write)]
    public async Task<IActionResult> DeleteDataset(string projectId)
    {
        var p = GetScopedProject(projectId);
        if (p is null) return NotFound();

        var wk = workspaceStore.GetByPath(p.RootPath);
        if (string.IsNullOrEmpty(wk?.DifyDatasetId)) return NoContent();

        try { await knowledge.DeleteDatasetAsync(wk.DifyDatasetId); }
        catch (HttpRequestException) { /* датасет мог быть удалён в Dify — сбрасываем ссылку */ }

        workspaceStore.Delete(p.RootPath);
        await Broadcast(null);
        return NoContent();
    }

    // Путь внутри vault заметок (notes/…) — такие файлы индексируются отдельно
    private static bool IsInNotesVault(string path)
    {
        var n = path.Replace('\\', '/').TrimStart('/');
        return n.StartsWith("notes/", StringComparison.OrdinalIgnoreCase);
    }
}

public record IndexFileRequest(string RelativePath);
public record SetTagsRequest(string DocumentName, string? DocumentId, List<string>? Tags);
