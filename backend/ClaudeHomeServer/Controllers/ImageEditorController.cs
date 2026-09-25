using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Редактор картинок в проекте (ADR-016, раздел 1). Контроллер в Main, потому что ему
// нужна спина: владение проектом, корень проекта, SafePath. В вертикаль уходят уже байты
// и абсолютный корень.
//
// Гейты по порядку: флаг image-editor на СЕРВЕРЕ (404) → проект свой (404) → поставщик
// доступен (409 provider_unavailable) → исполнитель задач подключён (503). Всё, что
// приходит из Images, — nullable: подсистема отключаемая (ADR-014), её выключение
// обязано давать честный ответ, а не 500.
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/image-editor")]
public class ImageEditorController(
    FeatureFlagService flags,
    ProjectManager projects,
    IEnumerable<IImageEditor> editors,
    ImageGenerationSettingsStore? placeSettings = null,
    IImageEditJobs? jobs = null,
    IImageEditSaver? saver = null) : ControllerBase
{
    // Потолок тела запуска: исходник, маска, размеченная копия и до MaxReferences образцов
    private const long MaxJobBodyBytes = 200L * 1024 * 1024;

    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    [HttpGet("catalog")]
    public IActionResult Catalog(string projectId)
    {
        if (Gate(projectId, out _) is { } denied) return denied;

        var place = ImagePlaces.ImageEditor;
        var adminProvider = placeSettings?.ProviderFor(place);
        var adminModel = adminProvider is null ? null : placeSettings?.ModelFor(place, adminProvider);
        return Ok(ImageEditCatalog.Build(editors, adminProvider, adminModel));
    }

    [HttpPost("quote")]
    public async Task<IActionResult> Quote(string projectId, [FromBody] ImageEditQuoteRequest req, CancellationToken ct)
    {
        if (Gate(projectId, out _) is { } denied) return denied;

        var editor = ImageEditCatalog.FindAvailable(editors, req.Provider);
        if (editor is null)
            return Error(StatusCodes.Status409Conflict, ImageEditErrorCodes.ProviderUnavailable,
                $"Поставщик «{req.Provider}» не настроен или отключён администратором");
        if (!ImageEditCatalog.HasModel(editor, req.Model))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Модели «{req.Model}» нет у поставщика {editor.Label}");
        var maxCount = ImageEditCatalog.DefaultLimits.MaxCount;
        if (req.Count < 1 || req.Count > maxCount)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Число вариантов — от 1 до {maxCount}");
        if (jobs is null) return JobsUnavailable();

        return Map(await jobs.QuoteAsync(UserId, projectId, req, ct), Ok);
    }

    [HttpPost("jobs")]
    [RequestSizeLimit(MaxJobBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxJobBodyBytes)]
    public async Task<IActionResult> Start(string projectId, [FromForm] StartJobForm form, CancellationToken ct)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;

        // Драйверов может не быть вовсе (не настроены или ещё не подключены) — это понятный
        // отказ, а не 500 и не 202 на задачу, которая никогда не начнётся
        if (ImageEditCatalog.Available(editors).Count == 0)
            return Error(StatusCodes.Status409Conflict, ImageEditErrorCodes.ProviderUnavailable,
                "Поставщик рисования не настроен. Обратитесь к администратору");
        if (jobs is null) return JobsUnavailable();
        if (string.IsNullOrWhiteSpace(form.QuoteId))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Не указана котировка: сначала запросите цену");

        var limits = ImageEditCatalog.DefaultLimits;
        var maxFileBytes = limits.MaxFileMb * 1024L * 1024L;
        var files = new[] { form.Source, form.Mask, form.Annotated }
            .Concat(form.References ?? [])
            .Where(f => f is not null);
        if (files.Any(f => f!.Length > maxFileBytes))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Файл больше {limits.MaxFileMb} МБ");

        var references = new List<ReferenceImage>();
        var uploaded = form.References ?? [];
        for (var i = 0; i < uploaded.Count; i++)
        {
            var file = uploaded[i];
            references.Add(new ReferenceImage(await ReadAsync(file, ct), ContentTypeOf(file),
                RoleAt(form.ReferenceRoles, i), Path.GetFileName(file.FileName)));
        }

        // Образцы из проекта — по пути, строго внутри корня
        var paths = form.ReferencePaths ?? [];
        for (var i = 0; i < paths.Count; i++)
        {
            if (!TryJoinInside(project.RootPath, paths[i], out var full))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Образец вне папки проекта");
            var info = new FileInfo(full);
            if (!info.Exists)
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    $"Образец не найден: {paths[i]}");
            if (info.Length > maxFileBytes)
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    $"Файл больше {limits.MaxFileMb} МБ");
            references.Add(new ReferenceImage(await System.IO.File.ReadAllBytesAsync(full, ct),
                ContentTypeByExtension(full), RoleAt(form.ReferencePathRoles, i), info.Name));
        }
        if (references.Count > limits.MaxReferences)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Образцов не больше {limits.MaxReferences}");

        if (form.SourcePath is { Length: > 0 } sourcePath && !TryJoinInside(project.RootPath, sourcePath, out _))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "Исходник вне папки проекта");

        var input = new ImageEditJobInput(
            form.QuoteId.Trim(),
            form.Prompt ?? "",
            form.Marks,
            await ToBytesAsync(form.Source, ct),
            await ToBytesAsync(form.Mask, ct),
            await ToBytesAsync(form.Annotated, ct),
            references,
            form.SourcePath);

        var started = await jobs.StartAsync(UserId, projectId, input, ct);
        return Map(started, created => StatusCode(StatusCodes.Status202Accepted, created));
    }

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string projectId, string jobId)
    {
        if (Gate(projectId, out _) is { } denied) return denied;
        var job = jobs?.Get(UserId, projectId, jobId);
        return job is null ? JobNotFound() : Ok(job);
    }

    [HttpDelete("jobs/{jobId}")]
    public async Task<IActionResult> Cancel(string projectId, string jobId, CancellationToken ct)
    {
        if (Gate(projectId, out _) is { } denied) return denied;
        if (jobs is null) return JobNotFound();
        var job = await jobs.CancelAsync(UserId, projectId, jobId, ct);
        return job is null ? JobNotFound() : Ok(job);
    }

    [HttpGet("jobs/{jobId}/variants/{variant:int}")]
    public IActionResult Variant(string projectId, string jobId, int variant)
    {
        if (Gate(projectId, out _) is { } denied) return denied;
        var image = jobs?.OpenVariant(UserId, projectId, jobId, variant);
        return image is null ? JobNotFound() : File(image.Bytes, image.ContentType);
    }

    [HttpPost("save")]
    public IActionResult Save(string projectId, [FromBody] ImageEditSaveRequest req, CancellationToken ct)
    {
        if (Gate(projectId, out var project) is { } denied) return denied;
        if (jobs is null || saver is null) return JobsUnavailable();

        foreach (var rel in new[] { req.SourcePath, req.Folder })
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            if (!TryJoinInside(project.RootPath, rel, out _))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Путь вне папки проекта");
        }

        var image = jobs.OpenVariant(UserId, projectId, req.JobId, req.Variant);
        if (image is null) return JobNotFound();
        return Map(saver.Save(project.RootPath, req, image), Ok);
    }

    // Поля multipart запуска. Роли образцов — параллельные списки к файлам и путям
    // (character | style | object), незнакомая роль читается как object.
    public sealed class StartJobForm
    {
        public string? QuoteId { get; set; }
        public string? Prompt { get; set; }
        // marks.json: тип пометки, координаты в долях 0…1, текст подписи
        public string? Marks { get; set; }
        // Путь исходника в проекте (для имени v2 и истории); сами байты — в Source
        public string? SourcePath { get; set; }
        public IFormFile? Source { get; set; }
        public IFormFile? Mask { get; set; }
        public IFormFile? Annotated { get; set; }
        public List<IFormFile>? References { get; set; }
        public List<string>? ReferenceRoles { get; set; }
        public List<string>? ReferencePaths { get; set; }
        public List<string>? ReferencePathRoles { get; set; }
    }

    // null — можно работать; иначе готовый отказ. Выключенный флаг и чужой проект
    // одинаково 404: ручки редактора не должны выдавать ни фичу, ни чужие id.
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

    // Путь из запроса — только относительный: SafePath срезает ведущий «/» и молча приклеил
    // бы «/etc/passwd» к корню проекта вместо отказа
    private static bool TryJoinInside(string root, string relativePath, out string full)
    {
        full = "";
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
            return false;
        try
        {
            full = SafePath.Join(root, relativePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ObjectResult Error(int status, string code, string error) =>
        StatusCode(status, new { error, code });

    private IActionResult JobsUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, ImageEditErrorCodes.Unavailable,
            "Редактор картинок недоступен на этом сервере");

    private IActionResult JobNotFound() =>
        Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.JobNotFound, "Задача не найдена");

    private IActionResult Map<T>(ImageEditCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.ErrorCode is null && result.Value is not null) return ok(result.Value);
        var code = result.ErrorCode ?? ImageEditErrorCodes.InvalidRequest;
        var status = code switch
        {
            ImageEditErrorCodes.ProviderUnavailable => StatusCodes.Status409Conflict,
            ImageEditErrorCodes.QuoteNotFound or ImageEditErrorCodes.JobNotFound => StatusCodes.Status404NotFound,
            ImageEditErrorCodes.TooManyJobs => StatusCodes.Status429TooManyRequests,
            ImageEditErrorCodes.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Error(status, code, result.Error ?? "Запрос не выполнен");
    }

    private static ReferenceRole RoleAt(List<string>? roles, int index) =>
        roles is not null && index < roles.Count
        && Enum.TryParse<ReferenceRole>(roles[index], ignoreCase: true, out var role)
            ? role
            : ReferenceRole.Object;

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task<ImageBytes?> ToBytesAsync(IFormFile? file, CancellationToken ct) =>
        file is null ? null : new ImageBytes(await ReadAsync(file, ct), ContentTypeOf(file));

    private static string ContentTypeOf(IFormFile file) =>
        string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType == "application/octet-stream"
            ? ContentTypeByExtension(file.FileName)
            : file.ContentType;

    private static string ContentTypeByExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
}
