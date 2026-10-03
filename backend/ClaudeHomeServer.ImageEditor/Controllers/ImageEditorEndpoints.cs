using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Общее тело ручек редактора, ключуемых областью (ADR-018 §2: одна реализация проверок на все
// входы). Наследники — проектный ImageEditorController и личный PersonalImageEditorController —
// только проходят свой гейт и зовут методы отсюда. Всё, что читает диск проекта, берёт
// scope.Project и у личной области отказывает до обращения к RootPath.
//
// Гейты по порядку: флаг и область (404, у наследника) → поставщик доступен (409
// provider_unavailable) → исполнитель задач подключён (503). Всё, что приходит из отключаемых
// подсистем, — nullable (ADR-014): без Images нет растра, и transform с jobs отвечают 503
// raster_unavailable, а не 500.
public abstract class ImageEditorEndpoints(
    IEnumerable<IImageEditor> editors,
    ImageEditLaunchAssembler launcher,
    IImagePlaceSettings? placeSettings,
    IImageEditJobs? jobs,
    ImageEditSteps? steps,
    IConfiguration? config,
    IImageRaster? raster) : ControllerBase
{
    // Потолок файла проекта, который ручка transform читает в память; дальше решает растр (100 Мп)
    private const long MaxTransformFileBytes = 100L * 1024 * 1024;
    private const int DefaultHeavyFileMb = 5;

    // Потолок тела запуска: исходник, маска, размеченная копия и до MaxReferences образцов
    protected const long MaxJobBodyBytes = 200L * 1024 * 1024;

    protected string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    protected IImageEditJobs? Jobs => jobs;
    protected ImageEditSteps? Steps => steps;
    protected IImageRaster? Raster => raster;

    protected IActionResult CatalogOf()
    {
        var place = ImagePlaceKeys.ImageEditor;
        var adminProvider = placeSettings?.ProviderFor(place);
        var adminModel = adminProvider is null ? null : placeSettings?.ModelFor(place, adminProvider);
        var heavy = config?.GetValue("ImageEditor:HeavyFileMb", DefaultHeavyFileMb) ?? DefaultHeavyFileMb;
        var limits = ImageEditCatalog.DefaultLimits with { HeavyFileMb = heavy > 0 ? heavy : DefaultHeavyFileMb };
        var catalog = ImageEditCatalog.Build(editors, adminProvider, adminModel, limits);
        // Без растра (подсистема картинок выключена) редактор не работает: ответ остаётся 200,
        // чтобы фронт показал причину, а не общий сбой
        if (jobs is null || raster is null) catalog = catalog with { Reason = ImageEditCatalogReasons.SubsystemDisabled };
        return Ok(catalog);
    }

    protected async Task<IActionResult> QuoteIn(ImageEditScope scope, ImageEditQuoteRequest req, CancellationToken ct)
    {
        var editor = ImageEditCatalog.FindAvailable(editors, req.Provider);
        if (editor is null)
            return Error(StatusCodes.Status409Conflict, ImageEditErrorCodes.ProviderUnavailable,
                ImageEditCatalog.UnavailableError(editors, req.Provider));
        if (!ImageEditCatalog.HasModel(editor, req.Model))
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Модели «{req.Model}» нет у поставщика {editor.Label}");
        var maxCount = ImageEditCatalog.DefaultLimits.MaxCount;
        if (req.Count < 1 || req.Count > maxCount)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Число вариантов — от 1 до {maxCount}");
        if (jobs is null) return JobsUnavailable();

        return Map(await jobs.QuoteAsync(UserId, scope.Key, req, ct), Ok);
    }

    protected async Task<IActionResult> StartIn(ImageEditScope scope, StartJobForm form, CancellationToken ct)
    {
        // Проверки и сборка входа — в общем ImageEditLaunchAssembler (ADR-018 §2): тем же путём
        // пойдёт запуск агентом. Здесь только multipart → байты. Пути и персонаж у личной области
        // сборщик отвергает сам, до диска проекта
        var uploaded = new List<ReferenceImage>();
        var files = form.References ?? [];
        for (var i = 0; i < files.Count; i++)
            uploaded.Add(new ReferenceImage(await ReadAsync(files[i], ct), ContentTypeOf(files[i]),
                RoleAt(form.ReferenceRoles, i), Path.GetFileName(files[i].FileName)));
        var paths = form.ReferencePaths ?? [];
        var referencePaths = paths.Select((p, i) => (p, RoleAt(form.ReferencePathRoles, i))).ToList();

        var request = new ImageEditLaunchRequest(
            form.QuoteId, form.Prompt, form.Marks, form.SourcePath,
            await ToBytesAsync(form.Source, ct),
            await ToBytesAsync(form.Mask, ct),
            await ToBytesAsync(form.Annotated, ct),
            uploaded, referencePaths, form.CharacterSlug,
            MatchSourceSize: form.MatchSourceSize ?? true,
            BaseStepId: form.BaseStepId,
            AspectRatio: form.AspectRatio,
            Initiator: ImageEditInitiator.Human,
            ThreadSessionId: form.SessionId,
            ThreadId: form.ThreadId,
            VersionId: form.VersionId,
            ContextRevision: form.ContextRevision);

        var started = await launcher.LaunchAsync(UserId, scope, request, ct);
        return Map(started, created => StatusCode(StatusCodes.Status202Accepted, created));
    }

    protected IActionResult JobIn(ImageEditScope scope, string jobId)
    {
        var job = jobs?.Get(UserId, scope.Key, jobId);
        return job is null ? JobNotFound() : Ok(job);
    }

    protected async Task<IActionResult> CancelIn(ImageEditScope scope, string jobId, CancellationToken ct)
    {
        if (jobs is null) return JobNotFound();
        var job = await jobs.CancelAsync(UserId, scope.Key, jobId, ct);
        return job is null ? JobNotFound() : Ok(job);
    }

    protected IActionResult VariantIn(ImageEditScope scope, string jobId, int variant)
    {
        var image = jobs?.OpenVariant(UserId, scope.Key, jobId, variant);
        return image is null ? JobNotFound() : File(image.Bytes, image.ContentType);
    }

    // Правка без ИИ от файла проекта или от шага; у личной области — только от шага
    protected async Task<IActionResult> TransformIn(ImageEditScope scope, ImageTransformRequest req, bool dryRun,
        CancellationToken ct)
    {
        if (raster is null) return RasterUnavailable();
        if (steps is null) return JobsUnavailable();

        ProjectImage? file = null;
        if (req.Base?.Path is { Length: > 0 } rel)
        {
            if (scope.Project is not { } project)
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "У чата вне проекта нет файлов проекта: правка — только от шага");
            if (!TryJoinInside(project.RootPath, rel, out var full))
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Путь вне папки проекта или идёт через символическую ссылку");
            var info = new FileInfo(full);
            if (!info.Exists)
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, $"Файл не найден: {rel}");
            if (info.Length > MaxTransformFileBytes)
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    $"Файл больше {MaxTransformFileBytes / 1024 / 1024} МБ");
            file = new ProjectImage(Path.GetRelativePath(project.RootPath, full).Replace('\\', '/'),
                await System.IO.File.ReadAllBytesAsync(full, ct));
        }

        return Map(steps.Transform(UserId, scope.Key, req, file, dryRun), Ok);
    }

    protected IActionResult StepIn(ImageEditScope scope, string stepId)
    {
        if (steps?.Open(UserId, scope.Key, stepId) is not { } found)
            return Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.StepNotFound, "Шаг истории не найден");
        return File(found.Image.Bytes, found.Image.ContentType);
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
        // Подключённый персонаж проекта (characters/<slug>/)
        public string? CharacterSlug { get; set; }
        // Возврат размера оригинала после скачивания; не передано — true (ADR-018 §9)
        public bool? MatchSourceSize { get; set; }
        // Шаг истории, с которого запущена правка: родитель шагов из её вариантов
        public string? BaseStepId { get; set; }
        // Пропорции «Дорисовать за края» (1:1, 16:9, 9:16); не передано — на усмотрение драйвера
        public string? AspectRatio { get; set; }
        // Нить картинки в чате (ADR-019): варианты станут её версиями, внизу ленты —
        // якорь запуска. Не своя нить — 404 thread_not_found до запуска
        public string? SessionId { get; set; }
        public string? ThreadId { get; set; }
        // Версия нити, от которой правка; не передано — текущая. Чужая — 404 version_not_found
        public string? VersionId { get; set; }
        // Ревизия контекста чата (ADR-023 §Д2.1): с ней входы берутся из стора, а Source, References,
        // ReferencePaths, CharacterSlug, ThreadId и VersionId тела игнорируются; не совпала — 409 context_changed
        public long? ContextRevision { get; set; }
    }

    // Строковой проверки SafePath мало: символическая ссылка внутри проекта (refs → /etc)
    // лексически лежит в корне, и бэкенд прочитал бы файл хоста и отдал его поставщику
    protected static bool TryJoinInside(string root, string relativePath, out string full)
    {
        full = ProjectLinkGuard.ResolveInside(root, relativePath) ?? "";
        return full.Length > 0;
    }

    protected ObjectResult Error(int status, string code, string error) =>
        StatusCode(status, new { error, code });

    protected IActionResult RasterUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, ImageEditErrorCodes.RasterUnavailable,
            "Обработка картинок выключена на этом сервере");

    protected IActionResult JobsUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, ImageEditErrorCodes.Unavailable,
            "Редактор картинок недоступен на этом сервере");

    protected IActionResult JobNotFound() =>
        Error(StatusCodes.Status404NotFound, ImageEditErrorCodes.JobNotFound, "Задача не найдена");

    protected IActionResult Map<T>(ImageEditCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.ErrorCode is null && result.Value is not null) return ok(result.Value);
        var code = result.ErrorCode ?? ImageEditErrorCodes.InvalidRequest;
        var status = code switch
        {
            ImageEditErrorCodes.ProviderUnavailable or ImageEditErrorCodes.NameTaken => StatusCodes.Status409Conflict,
            ImageEditErrorCodes.QuoteNotFound or ImageEditErrorCodes.JobNotFound
                or ImageEditErrorCodes.CharacterNotFound or ImageEditErrorCodes.StepNotFound
                or ImageEditErrorCodes.ThreadNotFound or ImageEditErrorCodes.VersionNotFound => StatusCodes.Status404NotFound,
            ImageEditErrorCodes.TooManyJobs => StatusCodes.Status429TooManyRequests,
            ImageEditErrorCodes.Unavailable or ImageEditErrorCodes.RasterUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Error(status, code, result.Error ?? "Запрос не выполнен");
    }

    private static ReferenceRole RoleAt(List<string>? roles, int index) =>
        roles is not null && index < roles.Count
        && Enum.TryParse<ReferenceRole>(roles[index], ignoreCase: true, out var role)
            ? role
            : ReferenceRole.Object;

    protected static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task<ImageBytes?> ToBytesAsync(IFormFile? file, CancellationToken ct) =>
        file is null ? null : new ImageBytes(await ReadAsync(file, ct), ContentTypeOf(file));

    private static string ContentTypeOf(IFormFile file) =>
        string.IsNullOrWhiteSpace(file.ContentType) || file.ContentType == "application/octet-stream"
            ? ImageEditLaunchAssembler.ContentTypeByExtension(file.FileName)
            : file.ContentType;
}
