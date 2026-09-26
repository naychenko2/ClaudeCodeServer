using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Чаты картинки (ADR-018 §1): создание по первому сообщению, поиск чата редактором по пути и
// перепривязка к файлу. Сессии создаёт и правит ядро через шов IImageChatSessions, поиск —
// линейный проход по ISessionDirectory.GetAll с фильтром по проекту.
//
// Гейты те же, что у остальных ручек редактора: флаг и чужой проект — одинаково 404. Чужой,
// несуществующий и не-картиночный чат тоже неотличимы — 404 с одним и тем же телом.
[ProjectCapability(ProjectCapabilityArea.FileBound)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor/chats")]
public class ImageChatsController(
    IFeatureFlagGate flags,
    IProjectManager projects,
    ISessionDirectory directory,
    ImageChatStateStore states,
    ImageEditLaunchAssembler launcher,
    IImageChatSessions? chats = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpPost]
    public async Task<IActionResult> Create(string projectId, [FromBody] ImageChatCreateRequest req, CancellationToken ct)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        if (ExistingFile(project, req.SourcePath) is not { } sourcePath) return PathRejected();
        if (chats is null) return Unavailable();

        var created = await chats.CreateAsync(UserId, project, sourcePath, req.PersonaId, ct);
        if (created.Session is { } session) return StatusCode(StatusCodes.Status201Created, session);
        var status = created.ErrorCode == ImageChatCreateOutcome.Unavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status400BadRequest;
        return Error(status, created.ErrorCode ?? ImageEditErrorCodes.InvalidRequest, created.Error ?? "Чат не создан");
    }

    // current — чат, связанный с файлом сейчас: свежий неархивный, иначе свежий архивный
    // (первое же сообщение вернёт его из архива само). continued — чаты, ушедшие с этого файла
    // на новую версию: сами на них не перескакиваем, редактор предлагает открыть.
    [HttpGet]
    public IActionResult Find(string projectId, [FromQuery] string? path)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        // Файла может уже не быть (удалён мимо редактора) — чат по старому пути всё равно ищем
        if (InsideProject(project, path) is not { } rel) return PathRejected();

        var imageChats = directory.GetAll()
            .Where(s => s.ProjectId == project.Id && s.ImageChat is not null)
            .ToList();
        var current = imageChats
            .Where(s => s.ImageChat!.CurrentPath == rel)
            .OrderBy(s => s.IsArchived)
            .ThenByDescending(s => s.UpdatedAt)
            .FirstOrDefault();
        var continued = imageChats
            .Where(s => s.ImageChat!.CurrentPath != rel && s.ImageChat.Lineage.Contains(rel))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
        return Ok(new ImageChatLookupDto(current, continued));
    }

    // «Привязать чат к этому файлу»: файл переименовали мимо FileService, и current не нашёлся
    [HttpPut("{sessionId}/path")]
    public IActionResult SetPath(string projectId, string sessionId, [FromBody] ImageChatPathRequest req)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        if (OwnImageChat(project, sessionId) is null) return ChatNotFound();
        if (ExistingFile(project, req.Path) is not { } path) return PathRejected();
        if (chats is null) return Unavailable();

        var updated = chats.SetPath(sessionId, path);
        return updated is null ? ChatNotFound() : Ok(updated);
    }

    // ── Состояние редактора (ADR-018 §2) ───────────────────────────────────────────

    // Потолок тела записи состояния: JSON и маска холста
    private const long MaxStateBodyBytes = 30L * 1024 * 1024;

    [HttpGet("{sessionId}/state")]
    public IActionResult GetState(string projectId, string sessionId)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        if (OwnImageChat(project, sessionId) is not { } session) return ChatNotFound();
        return Ok(states.Get(UserId, session.Id));
    }

    // Запись с дебаунсом от редактора. Тело — JSON состояния либо multipart: поле state (JSON) и
    // файл mask — маска едет только при смене canvasRevision. revision — та, от которой редактор
    // считал; устарела — 409 с актуальным состоянием. Сессию не трогает: UpdatedAt не двигается.
    [HttpPut("{sessionId}/state")]
    [RequestSizeLimit(MaxStateBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxStateBodyBytes)]
    public async Task<IActionResult> PutState(string projectId, string sessionId, CancellationToken ct)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        if (OwnImageChat(project, sessionId) is not { } session) return ChatNotFound();

        ImageChatState? incoming;
        byte[]? mask = null;
        try
        {
            if (Request.HasFormContentType)
            {
                var form = await Request.ReadFormAsync(ct);
                incoming = form.TryGetValue("state", out var json) && !string.IsNullOrWhiteSpace(json)
                    ? System.Text.Json.JsonSerializer.Deserialize<ImageChatState>(json.ToString(), ImageChatStateStore.Json)
                    : null;
                if (form.Files.GetFile("mask") is { Length: > 0 } file)
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, ct);
                    mask = ms.ToArray();
                }
            }
            else
            {
                incoming = await System.Text.Json.JsonSerializer.DeserializeAsync<ImageChatState>(
                    Request.Body, ImageChatStateStore.Json, ct);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            incoming = null;
        }
        if (incoming is null)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Состояние не прочитано");
        if (incoming.Count < 1 || incoming.Count > ImageEditCatalog.DefaultLimits.MaxCount)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Число вариантов — от 1 до {ImageEditCatalog.DefaultLimits.MaxCount}");

        var written = states.Write(UserId, session.Id, incoming, mask);
        if (!written.Ok)
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                error = "Состояние уже поменялось — перечитайте его",
                code = ImageEditErrorCodes.RevisionConflict,
                state = written.State,
            });
        if (written.Changes.Count > 0)
            await launcher.BroadcastStateAsync(UserId, project.Id, session.Id, written.State,
                ImageEditInitiator.Human, written.Changes);
        return Ok(written.State);
    }

    // Чат этого проекта (а проект уже свой — Gate) и именно чат картинки; иначе null
    private Session? OwnImageChat(Project project, string sessionId)
    {
        var session = directory.GetById(sessionId);
        return session is { ImageChat: not null } && session.ProjectId == project.Id ? session : null;
    }

    // Путь от корня проекта через «/» или null: вне проекта, абсолютный, через ссылку
    private static string? InsideProject(Project project, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = ProjectLinkGuard.ResolveInside(project.RootPath, path.Trim());
        if (full is null) return null;
        var rel = Path.GetRelativePath(project.RootPath, full).Replace('\\', '/');
        return rel == "." ? null : rel;
    }

    private static string? ExistingFile(Project project, string? path) =>
        InsideProject(project, path) is { } rel && System.IO.File.Exists(Path.Combine(project.RootPath, rel)) ? rel : null;

    private IActionResult? Gate(string projectId, out Project project)
    {
        project = null!;
        if (!flags.IsEnabled(UserId, FeatureFlagKeys.ImageEditor))
            return NotFound(new { error = "Редактор картинок выключен" });
        var found = projects.GetById(projectId);
        if (found is null || found.OwnerId != UserId)
            return NotFound(new { error = "Проект не найден" });
        project = found;
        return null;
    }

    private ObjectResult Error(int status, string code, string error) => StatusCode(status, new { error, code });

    private IActionResult ChatNotFound() =>
        Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.ChatNotFound, "Чат картинки не найден");

    private IActionResult PathRejected() =>
        Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
            "Файл не найден, вне папки проекта или идёт через символическую ссылку");

    private IActionResult Unavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, ImageEditErrorCodes.Unavailable,
            "Чат картинки недоступен на этом сервере");
}

// PersonaId — собеседник, выбранный в композере; не передан — руководитель проекта или ассистент
public sealed record ImageChatCreateRequest(string? SourcePath, string? PersonaId = null);

public sealed record ImageChatPathRequest(string? Path);

public sealed record ImageChatLookupDto(Session? Current, IReadOnlyList<Session> Continued);
