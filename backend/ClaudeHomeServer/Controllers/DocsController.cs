using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Docs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Документация проекта для панели «Доки»: README.md + docs/**. Отдельно от FilesController,
// потому что отдаёт не файлы, а корпус — заголовки, якоря и связи между документами.
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/docs")]
public class DocsController(DocsIndexService docs, ProjectManager projects,
    NotesService notes, ILogger<DocsController> logger) : ControllerBase
{
    // DefaultMapInboundClaims = false → sub читаем напрямую (как в FilesController)
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Проект текущего пользователя; чужой/несуществующий → 404, как у соседних контроллеров
    private Project GetProject(string projectId)
    {
        var p = projects.GetById(projectId);
        if (p is null || p.OwnerId != UserId)
            throw new KeyNotFoundException($"Проект не найден: {projectId}");
        return p;
    }

    // Индекс документов: путь, заголовок, подзаголовки со слагами, дата правки, размер
    [HttpGet]
    public IActionResult Index(string projectId)
    {
        try
        {
            var p = GetProject(projectId);
            var (scope, types) = docs.ResolveScopeAndTypes(p);
            return Ok(docs.GetIndex(p.RootPath, scope, types));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Документ с содержимым и связями в обе стороны.
    // 404 на путь вне области документации — это гейт: без него эндпоинт стал бы вторым
    // универсальным файл-ридером поверх files/content, в обход его правил.
    [HttpGet("doc")]
    public IActionResult Doc(string projectId, [FromQuery] string path)
    {
        try
        {
            var p = GetProject(projectId);
            var (scope, types) = docs.ResolveScopeAndTypes(p);
            var detail = docs.GetDoc(p.RootPath, path, scope, types);
            return detail is null ? NotFound() : Ok(detail);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("search")]
    public IActionResult Search(string projectId, [FromQuery] string q = "")
    {
        try
        {
            var p = GetProject(projectId);
            return Ok(docs.Search(p.RootPath, q, docs.ResolveScope(p)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Настройка области: что выбрано, что можно выбрать (папки, файлы корня, типы файлов)
    // и откуда она взята — файл репозитория или настройка продукта
    [HttpGet("scope")]
    public IActionResult Scope(string projectId)
    {
        try
        {
            return Ok(docs.Describe(GetProject(projectId)));
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // Сохранить область. У каждой оси null — вернуть к дефолту, [] — «ничего отсюда».
    // Нормализация и отсев мусора — в сервисе, поэтому ответ отдаёт уже сохранённое значение,
    // а не присланное: фронт должен показать галки ровно такими, какими они легли в стор.
    //
    // Куда писать, решает наличие .docs: есть файл — правим его, нет — настройку проекта.
    // Разводить это по двум эндпоинтам нельзя: единственный существующий потребитель
    // (кнопка «вернуть README в область» в панели) молча перестал бы работать на проектах
    // с файлом — писал бы в хранилище, которое больше не читается.
    [HttpPut("scope")]
    public IActionResult SetScope(string projectId, [FromBody] SetDocsScopeRequest req)
    {
        try
        {
            var project = GetProject(projectId);   // владение проверяем до записи
            var file = docs.ReadScopeFile(project.RootPath);
            if (file.Scope is null)
            {
                var saved = projects.SetDocsScope(projectId, req.Folders, req.RootFiles, req.Types, req.Home);
                return Ok(docs.Describe(saved));
            }

            // null оси — «вернуть к дефолту», поэтому в файл уезжает дефолтное значение.
            // Home устроен иначе (как color и groupId у проекта): null — «не менять»,
            // пустая строка — сброс к авто. Иначе запрос без home (та самая кнопка про
            // README) стирал бы выбранную домашнюю страницу
            docs.WriteScopeFile(project.RootPath, new DocsScope(
                req.Folders ?? DocsIndexService.DefaultScope.Folders,
                req.RootFiles ?? DocsIndexService.DefaultScope.RootFiles,
                req.Types ?? DocsIndexService.DefaultScope.Types,
                req.Home is null ? file.Scope.Home : DocsIndexService.NormalizeHome(req.Home)));
            return Ok(docs.Describe(project));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать {DocsIndexService.ScopeFileName}: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к {DocsIndexService.ScopeFileName}: {e.Message}" }); }
    }

    // Порядок страниц папки: пишет .order в рабочее дерево. Правка репозитория, поэтому
    // только по явному жесту пользователя (перетаскивание строки), никогда фоном.
    //
    // items — имена без расширения в новом порядке; сервис сам разводит 404 (папки нет в
    // области) и 400 (имя, которого в папке нет). Ответ — свежий индекс: перечитывать его
    // вторым запросом нечестно, порядок обязан приехать вместе с подтверждением
    [HttpPut("order")]
    public IActionResult SetOrder(string projectId, [FromBody] SetDocsOrderRequest req)
    {
        try
        {
            var p = GetProject(projectId);
            var scope = docs.ResolveScope(p);
            var result = docs.WriteOrder(p.RootPath, req.Folder, req.Items ?? [], scope);
            return result.Status switch
            {
                DocsIndexService.OrderWriteStatus.FolderNotInScope => NotFound(new { error = result.Error }),
                DocsIndexService.OrderWriteStatus.BadItems => BadRequest(new { error = result.Error }),
                _ => Ok(docs.GetIndex(p.RootPath, scope)),
            };
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать .order: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к .order: {e.Message}" }); }
    }

    // Создать документ или раздел в папке области. Раздел — сразу парой «страница + папка»:
    // в wiki он существует только так, иначе узел дерева открывается пустой страницей.
    //
    // Ответ — путь созданного документа и свежий индекс: панель откроет его сразу, не ожидая
    // следующего запроса. 400 — непригодное название, 404 — папка вне области, 409 — занято
    [HttpPost("create")]
    public IActionResult Create(string projectId, [FromBody] CreateDocRequest req)
    {
        try
        {
            var p = GetProject(projectId);
            var scope = docs.ResolveScope(p);
            var result = docs.CreateDoc(p.RootPath, req.Folder, req.Name, req.Kind == "section", scope);
            switch (result.Status)
            {
                case DocsIndexService.DocCreateStatus.FolderNotInScope: return NotFound(new { error = result.Error });
                case DocsIndexService.DocCreateStatus.BadName: return BadRequest(new { error = result.Error });
                case DocsIndexService.DocCreateStatus.Conflict: return Conflict(new { error = result.Error });
            }

            // Документ в КОРНЕ репозитория попадает в область только поимённо (папкой корень
            // не выбирают — это был бы обход всего репозитория). Дописываем имя сами: иначе
            // созданный файл не появился бы в панели, и действие выглядело бы несработавшим.
            // Куда писать — туда же, куда пишет настройка области: в файл .docs, если он есть
            if (result.Path is { } created && !created.Contains('/'))
            {
                var rootFiles = new List<string>(scope.RootFiles);
                if (!rootFiles.Contains(created, StringComparer.OrdinalIgnoreCase))
                {
                    rootFiles.Add(created);
                    if (docs.ReadScopeFile(p.RootPath).Scope is not null)
                        docs.WriteScopeFile(p.RootPath, scope with { RootFiles = rootFiles });
                    else
                        projects.SetDocsScope(projectId, scope.Folders, rootFiles, scope.Types, null);
                    scope = docs.ResolveScope(GetProject(projectId));
                }
            }
            return Ok(new { path = result.Path, index = docs.GetIndex(p.RootPath, scope) });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось создать документ: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к папке: {e.Message}" }); }
    }

    // Значение свойства в шапке документа («**Статус:** Принято»). Правка репозитория,
    // поэтому только по явному жесту пользователя.
    //
    // value = null — снять свойство, «» — оставить пустой слот. Ответ — свежий индекс по
    // конвенции панели: метка в списке документов обязана приехать вместе с подтверждением,
    // а не вторым запросом. touched перечисляет ключи, которые изменились фактически, —
    // их больше одного, когда вместе со свойством переписалась «дата смены»
    [HttpPut("property")]
    public IActionResult SetProperty(string projectId, [FromBody] SetDocPropertyRequest req)
    {
        try
        {
            var p = GetProject(projectId);
            var (scope, types) = docs.ResolveScopeAndTypes(p);
            var result = docs.WriteProperty(p.RootPath, req.Path, req.Key, req.Value, scope, types);
            return result.Status switch
            {
                DocsIndexService.PropertyWriteStatus.NotFound => NotFound(new { error = result.Error }),
                DocsIndexService.PropertyWriteStatus.BadKey => BadRequest(new { error = result.Error }),
                DocsIndexService.PropertyWriteStatus.BadValue => BadRequest(new { error = result.Error }),
                DocsIndexService.PropertyWriteStatus.Failed => StatusCode(500, new { error = result.Error }),
                _ => Ok(new
                {
                    properties = result.Properties,
                    touched = result.Touched,
                    index = docs.GetIndex(p.RootPath, scope, types),
                }),
            };
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать документ: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к документу: {e.Message}" }); }
    }

    // Схема типов документов. Живёт ТОЛЬКО в .docs: у соседей по папке владельцы разные, а
    // тип документа — свойство репозитория, а не человека. Поэтому, в отличие от PUT /scope,
    // писать больше некуда — файла нет, значит он создаётся вместе с действующей областью.
    // Это не «молча»: описать тип документа — уже явный жест, такой же, как POST /scope-file.
    [HttpPut("types")]
    public IActionResult SetTypes(string projectId, [FromBody] SetDocTypesRequest req)
    {
        try
        {
            var project = GetProject(projectId);
            var normalized = DocTypeSchema.Normalize(req.Types);
            // Область передаём, только когда файла ещё нет: он создаётся вместе с ней.
            // В существующий файл пишем ОДНУ секцию типов — иначе правка схемы переписала бы
            // область нормализованными значениями и срезала бы то, что написано в файле руками
            var initialScope = docs.ReadScopeFile(project.RootPath).Scope is null
                ? docs.ResolveScope(project)
                : null;
            if (docs.WriteScopeFile(project.RootPath, initialScope, normalized)
                == DocsIndexService.ScopeFileWriteStatus.Broken)
                return Conflict(new { error = $"Файл {DocsIndexService.ScopeFileName} не разобран — исправьте его вручную" });

            var (scope, types) = docs.ResolveScopeAndTypes(project);
            return Ok(new { scope = docs.Describe(project), index = docs.GetIndex(project.RootPath, scope, types) });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать {DocsIndexService.ScopeFileName}: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к {DocsIndexService.ScopeFileName}: {e.Message}" }); }
    }

    // Переименовать документ или раздел. Раздел переезжает парой «страница + папка», и
    // вместе с ним — весь его подкорпус: пути вложенных документов, ссылки на них,
    // строка в .order, привязки комментариев и выбранное «Начало».
    //
    // updateLinks: false — файлы чужих документов не трогаем, но в ответе говорим, сколько
    // ссылок осталось битыми: молчание тут хуже, чем неудобная цифра
    [HttpPost("rename")]
    public IActionResult Rename(string projectId, [FromBody] RenameDocRequest req)
    {
        try
        {
            var p = GetProject(projectId);
            var scope = docs.ResolveScope(p);
            var result = docs.RenameDoc(p.RootPath, req.Path ?? "", req.NewName, req.UpdateLinks, scope);
            switch (result.Status)
            {
                case DocsIndexService.DocRenameStatus.NotFound: return NotFound(new { error = result.Error });
                case DocsIndexService.DocRenameStatus.BadName: return BadRequest(new { error = result.Error });
                case DocsIndexService.DocRenameStatus.Conflict: return Conflict(new { error = result.Error });
                case DocsIndexService.DocRenameStatus.Failed:
                    return StatusCode(500, new { error = result.Error });
            }

            var moved = result.Moved ?? new Dictionary<string, string>();
            scope = ApplyMoveCascade(projectId, p, scope, moved, "переименовании");

            return Ok(new
            {
                path = result.Path,
                updatedDocs = result.UpdatedDocs,
                brokenLinks = result.BrokenLinks,
                moved,
                index = docs.GetIndex(p.RootPath, scope),
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось переименовать: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа: {e.Message}" }); }
    }

    // Перенести документ или раздел в другую папку области. Раздел переезжает со всем
    // поддеревом; ссылки внутри переехавшего чинятся тоже — при смене глубины
    // «../vision.md» указывает уже не туда
    [HttpPost("move")]
    public IActionResult Move(string projectId, [FromBody] MoveDocRequest req)
    {
        try
        {
            var p = GetProject(projectId);
            var scope = docs.ResolveScope(p);
            var result = docs.MoveDoc(p.RootPath, req.Path ?? "", req.TargetFolder, req.UpdateLinks, scope);
            switch (result.Status)
            {
                case DocsIndexService.DocMoveStatus.NotFound: return NotFound(new { error = result.Error });
                case DocsIndexService.DocMoveStatus.BadTarget: return BadRequest(new { error = result.Error });
                case DocsIndexService.DocMoveStatus.Conflict: return Conflict(new { error = result.Error });
                case DocsIndexService.DocMoveStatus.Failed: return StatusCode(500, new { error = result.Error });
            }

            var moved = result.Moved ?? new Dictionary<string, string>();
            scope = ApplyMoveCascade(projectId, p, scope, moved, "переносе");

            return Ok(new
            {
                path = result.Path,
                updatedDocs = result.UpdatedDocs,
                brokenLinks = result.BrokenLinks,
                moved,
                index = docs.GetIndex(p.RootPath, scope),
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось перенести: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа: {e.Message}" }); }
    }

    // Побочные привязки переезда — общие для переименования и переноса: и там, и там
    // документы меняют путь, а помнящее старый путь остаётся снаружи сервиса.
    // Возвращает область (она могла поменяться вместе с «Началом»)
    private DocsScope ApplyMoveCascade(string projectId, Project project, DocsScope scope,
        IReadOnlyDictionary<string, string> moved, string action)
    {
        if (moved.Count == 0) return scope;

        // Комментарии заметок к документу (и ко всему поддереву раздела) следуют за новым
        // путём — привязка не сиротеет. Как в FilesController.Rename
        foreach (var (from, to) in moved)
        {
            try { notes.RewriteAnnotationTargets(UserId!, projectId, from, projectId, to, prefix: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Перепись привязок комментариев при {Action} {Old}", action, from); }
        }

        // «Начало» указывает на конкретный путь: без переезда выбранный документ молча
        // исчез бы из настройки, и панель открылась бы на README
        if (scope.Home is { } home && moved.TryGetValue(home, out var newHome))
        {
            if (docs.ReadScopeFile(project.RootPath).Scope is not null)
                docs.WriteScopeFile(project.RootPath, scope with { Home = newHome });
            else
                projects.SetDocsScope(projectId, scope.Folders, scope.RootFiles, scope.Types, newHome);
            return docs.ResolveScope(GetProject(projectId));
        }
        return scope;
    }

    // Удалить документ или раздел. Раздел уходит парой «страница + папка» со всем
    // содержимым — включая файлы, которых панель не показывала.
    //
    // Ссылки на удалённое починить нечем (цели больше нет), поэтому их число просто
    // возвращается: пользователь должен узнать о них здесь, а не при публикации wiki
    [HttpPost("delete")]
    public IActionResult Delete(string projectId, [FromBody] DeleteDocRequest req)
    {
        try
        {
            var p = GetProject(projectId);
            var scope = docs.ResolveScope(p);
            var result = docs.DeleteDoc(p.RootPath, req.Path ?? "", scope);
            switch (result.Status)
            {
                case DocsIndexService.DocDeleteStatus.NotFound: return NotFound(new { error = result.Error });
                case DocsIndexService.DocDeleteStatus.Failed: return StatusCode(500, new { error = result.Error });
            }

            var removed = result.Removed ?? [];
            // «Начало» указывало на удалённый документ — возвращаем авто-выбор: иначе
            // панель открывалась бы на путь, которого больше нет
            if (scope.Home is { } home && removed.Contains(home, StringComparer.OrdinalIgnoreCase))
            {
                if (docs.ReadScopeFile(p.RootPath).Scope is not null)
                    docs.WriteScopeFile(p.RootPath, scope with { Home = null });
                else
                    projects.SetDocsScope(projectId, scope.Folders, scope.RootFiles, scope.Types, "");
                scope = docs.ResolveScope(GetProject(projectId));
            }

            return Ok(new
            {
                removed,
                brokenLinks = result.BrokenLinks,
                removedFiles = result.RemovedFiles,
                index = docs.GetIndex(p.RootPath, scope),
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось удалить: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа: {e.Message}" }); }
    }

    // Вынести текущую область в файл репозитория: с этого момента она версионируется и
    // одинакова у всех, кто открыл репозиторий. Отдельным действием, а не автоматически —
    // продукт не создаёт файлы в чужом рабочем дереве без спроса.
    [HttpPost("scope-file")]
    public IActionResult SaveScopeFile(string projectId)
    {
        try
        {
            var project = GetProject(projectId);
            // Единственная из пяти точек записи, которая пишет безусловно: остальные сперва
            // убеждаются, что файл разобран. Перезаписать неразобранный — уничтожить чужую
            // ручную правку, о содержимом которой мы ничего не знаем
            if (docs.WriteScopeFile(project.RootPath, docs.ResolveScope(project))
                == DocsIndexService.ScopeFileWriteStatus.Broken)
                return Conflict(new { error = $"Файл {DocsIndexService.ScopeFileName} не разобран — исправьте его вручную" });
            return Ok(docs.Describe(project));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (IOException e) { return StatusCode(500, new { error = $"Не удалось записать {DocsIndexService.ScopeFileName}: {e.Message}" }); }
        catch (UnauthorizedAccessException e) { return StatusCode(500, new { error = $"Нет доступа к {DocsIndexService.ScopeFileName}: {e.Message}" }); }
    }
}

public record SetDocsScopeRequest(List<string>? Folders, List<string>? RootFiles, List<string>? Types, string? Home = null);

// Folder = null или «» — корень репозитория: там тоже бывает свой .order
public record SetDocsOrderRequest(string? Folder, List<string>? Items);

// Name — НАЗВАНИЕ страницы: имя файла из него делает сервис (пробелы → дефисы), а само
// название уходит в первую строку документа заголовком. Kind: «doc» либо «section»
public record CreateDocRequest(string? Folder, string? Name, string? Kind);

// NewName — новое НАЗВАНИЕ (не имя файла): правила те же, что при создании. UpdateLinks —
// чинить ли ссылки в остальных документах; false — оставить как есть и сообщить их число
public record RenameDocRequest(string? Path, string? NewName, bool UpdateLinks = true);

// Раздел удаляется парой со всем содержимым — отдельного флага «с папкой» нет: половина
// пары в wiki это либо пустой узел, либо осиротевшая страница
public record DeleteDocRequest(string? Path);

// TargetFolder — папка области, куда переезжает документ (раздел — вместе с папкой и всем
// поддеревом). UpdateLinks — чинить ли ссылки: при переносе меняется глубина, и ломаются
// не только чужие ссылки на переехавшее, но и его собственные на всё остальное
public record MoveDocRequest(string? Path, string? TargetFolder, bool UpdateLinks = true);

// Key — ключ свойства ровно как он записан в схеме типа. Value: null — снять свойство,
// «» — оставить пустой слот под значение
public record SetDocPropertyRequest(string? Path, string? Key, string? Value);

// Схема целиком: типы перезаписывают секцию docTypes файла .docs. Частичной правки нет —
// редактор всегда присылает весь список, и он же отвечает за сохранность незнакомых полей
public record SetDocTypesRequest(List<DocTypeDef>? Types);
