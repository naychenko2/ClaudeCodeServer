using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Versioning;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Services.ImageEditor.Controllers;

// Общее тело ручек редактора, ключуемых областью (ADR-018 §2: одна реализация проверок на все
// входы). Наследники — проектный ImageEditorController и личный PersonalImageEditorController —
// только проходят свой гейт и зовут методы отсюда. Всё, что читает диск проекта, берёт
// scope.Project и у личной области отказывает до обращения к RootPath.
//
// Гейты по порядку: область (404, у наследника) → поставщик доступен (409
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
    IImageRaster? raster,
    ImageContextLaunch? context = null,
    IFeatureFlagGate? flags = null) : ControllerBase
{
    // Потолок файла проекта, который ручка transform читает в память; дальше решает растр (100 Мп)
    private const long MaxTransformFileBytes = 100L * 1024 * 1024;
    private const int DefaultHeavyFileMb = 5;

    // Потолок тела запуска: исходник, маска, размеченная копия и до MaxReferences образцов
    protected const long MaxJobBodyBytes = 200L * 1024 * 1024;

    // Потолок тела загрузки образца: одна картинка до MaxFileMb плюс поля формы
    protected const long MaxUploadBodyBytes = 30L * 1024 * 1024;

    protected string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    protected IImageEditJobs? Jobs => jobs;
    protected ImageEditSteps? Steps => steps;
    protected IImageRaster? Raster => raster;
    protected IFeatureFlagGate? Flags => flags;

    protected IActionResult CatalogOf()
    {
        var place = ImagePlaceKeys.ImageEditor;
        var adminProvider = placeSettings?.ProviderFor(place);
        var adminModel = adminProvider is null ? null : placeSettings?.ModelFor(place, adminProvider);
        var heavy = config?.GetValue("ImageEditor:HeavyFileMb", DefaultHeavyFileMb) ?? DefaultHeavyFileMb;
        var limits = ImageEditCatalog.DefaultLimits with { HeavyFileMb = heavy > 0 ? heavy : DefaultHeavyFileMb };
        var catalog = ImageEditCatalog.Build(editors, adminProvider, adminModel, limits, preferLocal: true);
        // Без растра (подсистема картинок выключена) редактор не работает: ответ остаётся 200,
        // чтобы фронт показал причину, а не общий сбой
        if (jobs is null || raster is null) catalog = catalog with { Reason = ImageEditCatalogReasons.SubsystemDisabled };
        return Ok(catalog);
    }

    private ImageProjectPrefsService? PrefsService => HttpContext?.RequestServices.GetService<ImageProjectPrefsService>();

    // Формат проекта для генерации без явного формата в теле (ADR-025: умолчание — желание из префов)
    private MediaFormat? StoredFormat(ImageEditScope scope) => PrefsService?.Get(UserId, scope).Format;

    // GET …/format?provider&model&op — состояние чипа «Формат»: всё посчитано на сервере (ADR-025 §3), у
    // каждой ступени своя цена из той же котировки, что на кнопке запуска
    protected async Task<IActionResult> FormatIn(ImageEditScope scope, string? provider, string? model, string? op, CancellationToken ct)
    {
        var editor = ImageEditCatalog.FindAvailable(editors, provider);
        if (editor is null)
            return Error(StatusCodes.Status409Conflict, ImageEditErrorCodes.ProviderUnavailable,
                ImageEditCatalog.UnavailableError(editors, provider));
        var operation = Enum.TryParse<ImageEditOp>(op, ignoreCase: true, out var parsed) ? parsed : ImageEditOp.Generate;
        var info = string.Equals(model?.Trim(), ImageEditCatalog.AutoModelId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(model)
            ? editor.PickModel(operation, EditMode.Auto, new EditTraits(false, 0, false))
            : editor.Models.FirstOrDefault(m => string.Equals(m.Id, model.Trim(), StringComparison.OrdinalIgnoreCase));
        if (info is null)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                $"Модели «{model}» нет у поставщика {editor.Label}");

        // Желание проекта относится к генерации; правка — «Как у исходника» на один запуск
        var wish = operation == ImageEditOp.Generate ? StoredFormat(scope) : null;
        var state = MediaFormatResolver.State(info.Caps.Format, wish);
        if (jobs is null || info.Caps.Format is null || operation != ImageEditOp.Generate) return Ok(state);

        var tiers = new List<FormatTierOption>();
        foreach (var tier in state.Tiers)
        {
            if (tier.DisabledReason is not null) { tiers.Add(tier); continue; }
            var probe = new ImageEditQuoteRequest(editor.Key, info.Id, EditMode.Auto, operation, 1, false,
                Format: new MediaFormat(FormatModes.Aspect, state.Resolved.Aspect, tier.Tier, null, null,
                    ClaudeHomeServer.Services.ChatContext.ContextActor.Human, DateTime.UtcNow));
            var quoted = await jobs.QuoteAsync(UserId, scope.Key, probe, ct);
            FormatPrice? price = quoted.Value?.Estimate is { Amount: { } amount } e ? new FormatPrice(amount, e.Unit, e.Approx) : null;
            tiers.Add(tier with { Price = price });
        }
        return Ok(state with { Tiers = tiers });
    }

    protected async Task<IActionResult> QuoteIn(ImageEditScope scope, ImageEditQuoteRequest req, CancellationToken ct)
    {
        // Resolved считает только сервер; генерация без формата в теле берёт формат проекта
        req = req with { Resolved = null };
        // Желание в теле проходит те же белые списки, что и сохраняемое: «пропорции» без пропорций не маскируется под «как у модели»
        if (req.Format is { } given && ImageProjectPrefsService.ValidateFormat(given) is { } badFormat)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, badFormat);
        // Формат из префов (✦ агента) подставляется в генерацию человека, не дающую явного формата:
        // ступень и цену генерации человек видит в котировке
        if (req.Format is null && req.Op == ImageEditOp.Generate) req = req with { Format = StoredFormat(scope) };
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

        // Подстановка персонажа на лицо (ADR-028, этап 2): проверки a, b; детекция — только на запуске
        if (req.FaceTarget is not null)
        {
            if (flags?.IsEnabled(UserId, FeatureFlagKeys.CharacterFaceSwap) != true)
                return Error(StatusCodes.Status404NotFound, "feature_disabled", "Подстановка персонажа на лицо выключена");
            if (scope.Project is null)
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Подстановка персонажа на лицо — только в проекте");
            // Персонаж задаётся внутри faceTarget.faces: верхний слаг вместе с faceTarget — двусмысленный вход
            if (req.CharacterSlug is { Length: > 0 })
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "При подстановке на лицо персонаж задаётся в faceTarget.faces");
            if (editor.Key != "local")
                return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                    "Подставлять персонажа можно только локальными моделями");
        }

        // По ревизии контекста (ADR-023 §Д2.1): образцы, персонаж и размер — из стора, поля тела игнорируются
        ImageContextLaunch.Inputs? byContext = null;
        if (req.ContextRevision is { } revision)
        {
            if (context is null) return JobsUnavailable();
            var resolved = context.Resolve(UserId, scope, req.SessionId, revision, req.Op);
            if (resolved.ErrorCode is not null) return Map(resolved, _ => Ok());
            var (inputs, _, sessionId) = resolved.Value;
            byContext = inputs;
            var size = await context.BaseSizeAsync(UserId, scope, sessionId, inputs, ct);
            req = req with
            {
                References = inputs.ReferenceCount,
                HasCharacter = inputs.CharacterSlug is not null,
                Width = size?.Width,
                Height = size?.Height,
            };
        }

        var quoted = await jobs.QuoteAsync(UserId, scope.Key, req, ct);
        if (byContext is null) return Map(quoted, Ok);
        // Строки «Чем» по операции запроса: источник один для строки контекста и панели (КТ-3)
        return Map(quoted, q => Ok(q with { Executors = ExecutorRows(req.Op, byContext.BaseHasImage, req.HasMask) }));
    }

    private IReadOnlyList<Protocol.ExecutorRowDto> ExecutorRows(ImageEditOp op, bool hasImage, bool hasMask)
    {
        var place = ImagePlaceKeys.ImageEditor;
        var adminProvider = placeSettings?.ProviderFor(place);
        var adminModel = adminProvider is null ? null : placeSettings?.ModelFor(place, adminProvider);
        var catalog = ImageEditCatalog.Build(editors, adminProvider, adminModel, preferLocal: true);
        // Свою видеокарту «Авто» берёт только под операцию, которую она умеет; иначе — как без неё
        if (!ImageExecutorRows.LocalCan(catalog, op, hasImage, hasMask))
            catalog = ImageEditCatalog.Build(editors, adminProvider, adminModel);
        return ImageExecutorRows.Build(catalog, editors, op, hasImage, hasMask);
    }

    protected async Task<IActionResult> StartIn(ImageEditScope scope, StartJobForm form, CancellationToken ct)
    {
        // faceTarget: прислано, но не разбилось — явный отказ, не «обычная правка»
        var (faceTarget, faceTargetAbsent) = ParseFaceTarget(form.FaceTarget);
        if (!faceTargetAbsent && faceTarget is null)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Не удалось разобрать faceTarget");
        // Персонаж задаётся внутри faceTarget.faces: верхний слаг вместе с faceTarget — двусмысленный вход
        if (faceTarget is not null && form.CharacterSlug is { Length: > 0 })
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest,
                "При подстановке на лицо персонаж задаётся в faceTarget.faces");

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
            ContextRevision: form.ContextRevision,
            CharacterPhotos: form.CharacterPhotos,
            FaceTarget: faceTarget);

        var started = await launcher.LaunchAsync(UserId, scope, request, ct);
        return Map(started, created => StatusCode(StatusCodes.Status202Accepted, created));
    }

    // Образец с диска → рабочая папка модуля → uploadId для ref {upload}. Не картинку и слишком тяжёлый
    // файл отвергаем здесь, до записи на диск
    protected async Task<IActionResult> UploadIn(IFormFile? file, CancellationToken ct)
    {
        if (context is null) return JobsUnavailable();
        if (file is null || file.Length == 0)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Не передан файл образца");
        var limits = ImageEditCatalog.DefaultLimits;
        if (file.Length > limits.MaxFileMb * 1024L * 1024L)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, $"Файл больше {limits.MaxFileMb} МБ");
        var bytes = await ReadAsync(file, ct);
        if (ImageFormatSniffer.DetectExtension(bytes) is null)
            return Error(StatusCodes.Status400BadRequest, ImageEditErrorCodes.InvalidRequest, "Образец должен быть картинкой: png, jpeg, webp или gif");
        return StatusCode(StatusCodes.Status201Created, new { uploadId = context.SaveUpload(UserId, bytes) });
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
        // Явный список фото персонажа (ADR-027 §8): имена файлов манифеста, порядок значим
        public List<string>? CharacterPhotos { get; set; }
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
        // Подстановка персонажа на лицо (ADR-028, этап 3): JSON-строка
        // {versionId, facesKey, faces: [{face, characterSlug}]}. Пустая строка (поле прислано, но
        // пустое) — не «нет поля»: ConvertEmptyStringToNull=false, чтобы её отличить от отсутствия
        // и отвергнуть как «Не удалось разобрать faceTarget», а не проглотить как обычную правку
        [System.ComponentModel.DataAnnotations.DisplayFormat(ConvertEmptyStringToNull = false)]
        public string? FaceTarget { get; set; }
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

    // Единый контракт: provider_unavailable — 409 везде; busy — 503; feature_disabled — 404
    protected IActionResult Map<T>(ImageEditCallResult<T> result, Func<T, IActionResult> ok)
    {
        if (result.ErrorCode is null && result.Value is not null) return ok(result.Value);
        var code = result.ErrorCode ?? ImageEditErrorCodes.InvalidRequest;
        var status = code switch
        {
            ImageEditErrorCodes.NameTaken or ImageEditErrorCodes.ContextChanged
                or ImageEditErrorCodes.FacesChanged or ImageEditErrorCodes.ProviderUnavailable
                => StatusCodes.Status409Conflict,
            ImageEditErrorCodes.QuoteNotFound or ImageEditErrorCodes.JobNotFound
                or ImageEditErrorCodes.CharacterNotFound or ImageEditErrorCodes.StepNotFound
                or ImageEditErrorCodes.ThreadNotFound or ImageEditErrorCodes.VersionNotFound
                or ImageEditErrorCodes.FeatureDisabled => StatusCodes.Status404NotFound,
            ImageEditErrorCodes.TooManyJobs => StatusCodes.Status429TooManyRequests,
            ImageEditErrorCodes.Unavailable or ImageEditErrorCodes.RasterUnavailable
                or ImageEditErrorCodes.Busy => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        // context_changed несёт свежий контекст: тело то же, что у ручек контекста чата
        if (result.Payload is { } payload)
        {
            // faces_changed: свежий список лиц плюс явные code и error (ADR-028, этап 3).
            // Текст — константа, не result.Error: ассемблер (чужой файл) может его сменить, а
            // контракт HTTP обязан быть стабильным
            if (code == ImageEditErrorCodes.FacesChanged && payload is ImageThreadFacesResponse faces)
                return StatusCode(status, FacesChangedResponse.From(faces, "Картинка изменилась — лица найдены заново"));
            return StatusCode(status, payload);
        }
        return Error(status, code, result.Error ?? "Запрос не выполнен");
    }

    private static ReferenceRole RoleAt(List<string>? roles, int index) =>
        roles is not null && index < roles.Count
        && Enum.TryParse<ReferenceRole>(roles[index], ignoreCase: true, out var role)
            ? role
            : ReferenceRole.Object;

    // JSON-строка {versionId, facesKey, faces: [{face, characterSlug}]} → FaceTarget.
    // Возвращает (цель, absent): absent=true — поля нет (обычная правка); absent=false и цель null —
    // прислано, но не разбилось (не-JSON, "{}", "null", неверные типы) → явный отказ, не «обычная правка».
    // Разбор безопасный: любой неверный тип — отказ, а не 500 (GetInt32/GetString на чужом типе бросают).
    private static (FaceTarget? Target, bool Absent) ParseFaceTarget(string? json)
    {
        if (json is null) return (null, true);
        if (string.IsNullOrWhiteSpace(json)) return (null, false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, false);
            if (!root.TryGetProperty("versionId", out var vid) ||
                !root.TryGetProperty("facesKey", out var key) ||
                !root.TryGetProperty("faces", out var facesArr)) return (null, false);
            if (vid.ValueKind != System.Text.Json.JsonValueKind.String ||
                key.ValueKind != System.Text.Json.JsonValueKind.String) return (null, false);
            if (facesArr.ValueKind != System.Text.Json.JsonValueKind.Array) return (null, false);
            var faces = new List<FaceAssignment>();
            foreach (var el in facesArr.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, false);
                if (!el.TryGetProperty("face", out var face) ||
                    !el.TryGetProperty("characterSlug", out var slug)) return (null, false);
                if (face.ValueKind != System.Text.Json.JsonValueKind.Number ||
                    !face.TryGetInt32(out var faceNum)) return (null, false);
                // characterSlug: строка; null — пустой (обязательность проверит FaceTargetRules)
                string slugStr;
                if (slug.ValueKind == System.Text.Json.JsonValueKind.String) slugStr = slug.GetString()!;
                else if (slug.ValueKind == System.Text.Json.JsonValueKind.Null) slugStr = "";
                else return (null, false);
                faces.Add(new FaceAssignment(faceNum, slugStr));
            }
            return (new FaceTarget(vid.GetString()!, key.GetString()!, faces), false);
        }
        catch (System.Text.Json.JsonException) { return (null, false); }
    }

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
