using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.CodeGraph;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

// Namespace — под корнем вертикали, а не ClaudeHomeServer.Architecture.Controllers:
// запись Architecture в сторожах границ сторожит именно префикс Services.Architecture
namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// Раздел «Архитектура» проекта (Viaduct).
/// GET/PUT /api/projects/{projectId}/architecture/model — хранилище модели
/// (<c>docs/architecture/model.viaduct.json</c>, версия SHA-256, устаревшая → 409);
/// POST /api/projects/{projectId}/architecture/generate — кнопка «Собрать из кода»:
/// стартовая модель из снимка графа кода + проектов-маркеров, слияние с уже лежащим
/// файлом без затирания ручных описаний.
/// Контроллер живёт в вертикали и ходит наружу только через Core-швы: владение
/// проектом (<see cref="IProjectManager"/>), имя автора (<see cref="IUserStore"/>) и
/// снимок графа кода (<see cref="IArchitectureCodeSource"/>, форвардер в Main).
/// Выключенная Архитектура (<c>Subsystems:architecture:Enabled=false</c>) до контроллера
/// не доходит вовсе: ModuleLoader не зовёт Register и не подключает сборку частью MVC,
/// маршрутов нет — поэтому сервисы подсистемы здесь обязательны. Необязателен только
/// шов CodeGraph: он выключен → 503 <c>graph_unavailable</c> на сборке из кода.
/// </summary>
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/architecture")]
public class ArchitectureController(
    IProjectManager projects,
    IUserStore users,
    ILogger<ArchitectureController> logger,
    ArchitectureModelGenerator generator,
    ArchitectureModelStore store,
    IArchitectureCodeSource? graphs = null) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Граф не построился или подсистема CodeGraph выключена (шва нет) — собирать не из чего
    private ObjectResult GraphUnavailable() =>
        StatusCode(503, new { code = "graph_unavailable", message = "Граф кода не построился — модель собрать не из чего" });

    /// <summary>
    /// 200 — <c>{ exists, content, version, updatedAt, updatedBy }</c>, где content —
    /// байты файла как есть (null, если модели ещё нет); 404/403 — нет проекта / чужой.
    /// </summary>
    [HttpGet("model")]
    public async Task<IActionResult> GetModel(string projectId, CancellationToken ct)
    {
        if (OwnedProject(projectId, out var denied) is not { } project) return denied!;

        var snapshot = await store.ReadAsync(project.RootPath, ct);
        if (snapshot.Version is not null) Response.Headers.ETag = $"\"{snapshot.Version}\"";
        return Ok(ToDto(snapshot));
    }

    /// <summary>
    /// Запись модели. <c>baseVersion</c> — версия, от которой шли правки (null — «модели
    /// ещё не было»). 200 — новая версия; 409 <c>version_conflict</c> — файл успели
    /// поменять (другая вкладка, персона, «Собрать из кода»), в теле его текущее состояние;
    /// 400 — тело не JSON-объект; 413 — модель больше потолка.
    /// </summary>
    [HttpPut("model")]
    [RequestSizeLimit(ArchitectureModelStore.MaxContentBytes * 2)]
    public async Task<IActionResult> PutModel(string projectId, [FromBody] ArchitectureModelPutRequest body, CancellationToken ct)
    {
        if (OwnedProject(projectId, out var denied) is not { } project) return denied!;
        if (body.Content is null) return BadRequest(new { code = "model_invalid", message = "Нет содержимого модели" });

        var user = UserId is { } id ? users.GetById(id) : null;
        var author = user?.DisplayName ?? user?.Username ?? UserId ?? "?";
        try
        {
            var outcome = await store.WriteAsync(project.RootPath, body.Content, body.BaseVersion, author, ct);
            if (!outcome.Saved)
                return Conflict(new { code = "version_conflict", current = ToDto(outcome.Current) });
            Response.Headers.ETag = $"\"{outcome.Current.Version}\"";
            return Ok(new
            {
                version = outcome.Current.Version,
                updatedAt = outcome.Current.Author.UpdatedAt,
                updatedBy = outcome.Current.Author.UpdatedBy,
            });
        }
        catch (ArchitectureModelInvalidException ex)
        {
            var tooLarge = System.Text.Encoding.UTF8.GetByteCount(body.Content) > ArchitectureModelStore.MaxContentBytes;
            return StatusCode(tooLarge ? 413 : 400, new { code = "model_invalid", message = ex.Message });
        }
    }

    /// <summary>
    /// 200 — модель собрана и записана (счётчики + время снимка графа); 404 — нет проекта;
    /// 403 — чужой проект; 409 — файл модели повреждён (не перезаписываем);
    /// 503 — граф кода построить не удалось.
    /// </summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate(string projectId, CancellationToken ct)
    {
        if (OwnedProject(projectId, out var denied) is not { } project) return denied!;
        if (graphs is null) return GraphUnavailable();
        var root = project.RootPath;

        try
        {
            var snapshot = await graphs.GetSnapshotAsync(root, ct);
            if (snapshot is null)
            {
                // Графа ещё нет — строим сейчас: кнопка «Собрать из кода» не должна
                // требовать отдельного похода в «Граф».
                await graphs.RebuildAsync(root, ct);
                snapshot = await graphs.GetSnapshotAsync(root, ct);
            }
            if (snapshot is null) return GraphUnavailable();

            var result = await generator.GenerateAsync(root, project.Name, ToInput(snapshot, root), ct);
            return Ok(result);
        }
        catch (ArchitectureModelCorruptException ex)
        {
            logger.LogWarning(ex, "Файл модели архитектуры повреждён в проекте {ProjectId}", projectId);
            return Conflict(new { code = "model_corrupt", message = "Файл модели повреждён — почини или удали его, потом собери заново" });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return StatusCode(499, new { message = "Сборка модели отменена" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка сборки модели архитектуры для проекта {ProjectId}", projectId);
            return StatusCode(500, new { message = "Не удалось собрать модель архитектуры" });
        }
    }

    // Проект есть и принадлежит вызывающему — иначе готовый 404/403 в denied
    private Project? OwnedProject(string projectId, out IActionResult? denied)
    {
        var project = projects.GetById(projectId);
        denied = project is null ? NotFound() : project.OwnerId != UserId ? Forbid() : null;
        return denied is null ? project : null;
    }

    private static object ToDto(ArchitectureModelSnapshot s) => new
    {
        exists = s.Content is not null,
        content = s.Content,
        version = s.Version,
        updatedAt = s.Author.UpdatedAt,
        updatedBy = s.Author.UpdatedBy,
    };

    /// <summary>Снимок графа кода → нейтральный вход генератора (пути — относительно корня).</summary>
    internal static CodeSnapshotInput ToInput(ArchitectureCodeSnapshot snapshot, string root) => new(
        snapshot.Nodes
            .Select(n => new CodeTypeInfo(n.Id, n.Label, RelativeTo(root, n.SourceFile), n.Kind))
            .ToList(),
        snapshot.Edges.Select(e => new CodeEdgeInfo(e.Source, e.Target, e.Relation)).ToList(),
        snapshot.BuiltAt);

    private static string RelativeTo(string root, string file) =>
        string.IsNullOrEmpty(file) || !Path.IsPathRooted(file) ? file : Path.GetRelativePath(root, file);
}

/// <summary>Тело PUT модели: содержимое persist-обёртки Viaduct и версия-основание.</summary>
public record ArchitectureModelPutRequest(string? Content, string? BaseVersion);
