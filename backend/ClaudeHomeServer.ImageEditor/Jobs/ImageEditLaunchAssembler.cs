using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ImageEditor.ChatContext;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Media;

namespace ClaudeHomeServer.Services.ImageEditor;

// Запуск генерации — одна сборка входа на всех (ADR-018 §2): ручка POST …/jobs и
// MCP-инструмент image_generate (ImageEditorToolset) идут через неё. Две сборки разошлись бы в проверке путей,
// лимитов и владения чатом, а это ровно те места, где дыра стоит денег.
//
// Порядок отказов прежний, как в ImageEditorController.Start до выноса: поставщик → исполнитель
// → растр → котировка → нить → пропорции → размеры → образцы → персонаж → исходник.
//
// Нить картинки (ADR-019). Запуск в нить — ThreadSessionId + ThreadId: нить обязана быть своей
// в своём чате этого проекта, иначе отказ thread_not_found ДО запуска и траты (молча отбросить
// нельзя: варианты ушли бы мимо карточки, которую человек видит). Основа правки — версия VersionId,
// а без неё текущая версия нити (изменение 27.09): чужая версия — version_not_found до запуска.
// Не прислан исходник — он берётся с картинки версии, не прислан BaseStepId — её шаг. После
// старта внизу ленты ложится якорь запуска image_launch_versions, по завершении варианты
// становятся версиями нити, а ход узнаёт о запуске из журнала нитей (блок
// ImageEditorStateContributor). Запуск без нити — без чата: трата и события без SessionId.
//
// Запуск по ревизии контекста (ADR-023 §Д2.1): с ContextRevision входы берутся из стора контекста чата,
// а Source, образцы, пути образцов, персонаж, нить и версия тела игнорируются; ревизия не совпала с
// текущей или с ревизией котировки — context_changed со свежим состоянием. Без поля — всё как прежде.
public sealed class ImageEditLaunchAssembler(
    IEnumerable<IImageEditor> editors,
    IImageEditJobs? jobs = null,
    IImageRaster? raster = null,
    ImageThreadService? threads = null,
    ImageEditSteps? steps = null,
    ImageContextLaunch? context = null)
{
    public static readonly IReadOnlyList<string> AspectRatios = ["1:1", "16:9", "9:16"];

    public async Task<ImageEditCallResult<ImageEditJobCreatedDto>> LaunchAsync(
        string ownerId, ImageEditScope scope, ImageEditLaunchRequest req, CancellationToken ct)
    {
        var assembled = await AssembleAsync(ownerId, scope, req, ct);
        if (assembled.Value is not { } input)
            return assembled.Payload is { } payload
                ? ImageEditCallResult<ImageEditJobCreatedDto>.Fail(
                    assembled.ErrorCode ?? ImageEditErrorCodes.InvalidRequest, assembled.Error ?? "Запрос не выполнен", payload)
                : Fail<ImageEditJobCreatedDto>(assembled.ErrorCode, assembled.Error);

        var started = await jobs!.StartAsync(ownerId, scope.Key, input, ct);
        if (started.Value is { } created && input is { ChatSessionId: { } chatId, ThreadId: { } threadId } && threads is not null)
        {
            await threads.OnLaunchedAsync(ownerId, scope.Key, chatId, threadId, jobs.Get(ownerId, scope.Key, created.JobId),
                created.JobId, input.Prompt, input.Initiator, input.BaseVersionId, input.BaseStepId, ct);
            // Быстрый поставщик мог закончить задачу раньше, чем запуск лёг в нить: Finished тогда не
            // нашёл запуска, и карточка висела бы в «Рисуем…». Финал догоняется здесь; событие, пришедшее
            // следом, увидит закрытый запуск и ничего не сделает
            if (jobs.Get(ownerId, scope.Key, created.JobId) is
                { Status: ImageEditJobStatus.Completed or ImageEditJobStatus.Failed or ImageEditJobStatus.Cancelled } done)
                await threads.OnJobFinishedAsync(ownerId, done);
        }
        return started;
    }

    // Вход задачи без запуска: всё проверено, байты прочитаны, нить своя или её нет. Диск проекта
    // (образцы путями, персонаж, путь исходника) — только у области проекта: у личной отказ до RootPath
    public async Task<ImageEditCallResult<ImageEditJobInput>> AssembleAsync(
        string ownerId, ImageEditScope scope, ImageEditLaunchRequest req, CancellationToken ct)
    {
        // Драйверов может не быть вовсе (не настроены или ещё не подключены) — это понятный
        // отказ, а не 500 и не 202 на задачу, которая никогда не начнётся
        if (ImageEditCatalog.Available(editors).Count == 0)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.ProviderUnavailable,
                "Поставщик рисования не настроен. Обратитесь к администратору");
        if (jobs is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.Unavailable, "Редактор картинок недоступен на этом сервере");
        if (raster is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.RasterUnavailable, "Обработка картинок выключена на этом сервере");
        if (string.IsNullOrWhiteSpace(req.QuoteId))
            return Invalid("Не указана котировка: сначала запросите цену");
        if (req.ContextRevision is { } revision)
        {
            var applied = await ApplyContextAsync(ownerId, scope, req, revision, ct);
            if (applied.Value is not { } byContext)
                return ImageEditCallResult<ImageEditJobInput>.Fail(applied.ErrorCode ?? ImageEditErrorCodes.InvalidRequest,
                    applied.Error ?? "Запрос не выполнен", applied.Payload!);
            req = byContext;
        }
        if (scope.Project is null
            && (req.ReferencePaths.Count > 0 || req.CharacterSlug is { Length: > 0 } || req.SourcePath is { Length: > 0 }))
            return Invalid("Вне проекта нельзя брать образцы, персонажей и исходник из файлов проекта");

        string? threadId = null, chatId = null, baseVersionId = null;
        var source = req.Source;
        var baseStepId = string.IsNullOrWhiteSpace(req.BaseStepId) ? null : req.BaseStepId.Trim();
        if (!string.IsNullOrWhiteSpace(req.ThreadId))
        {
            if (threads is null || !threads.OwnThread(ownerId, scope.Key, req.ThreadSessionId, req.ThreadId))
                return Fail<ImageEditJobInput>(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате");
            threadId = req.ThreadId.Trim();
            chatId = req.ThreadSessionId!.Trim();

            if (threads.Get(ownerId, chatId).Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
                return Fail<ImageEditJobInput>(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате");
            var version = string.IsNullOrWhiteSpace(req.VersionId) ? thread.CurrentVersion : thread.Version(req.VersionId.Trim());
            if (version is null)
                return Fail<ImageEditJobInput>(ImageEditErrorCodes.VersionNotFound, "Версии нет в этой картинке");
            baseVersionId = version.Id;
            var versionStep = thread.ImageStepOf(version);
            baseStepId ??= versionStep;
            if (source is null && versionStep is not null && steps?.Open(ownerId, scope.Key, versionStep) is { } found)
                source = new ImageBytes(found.Image.Bytes, found.Image.ContentType);
        }

        var aspectRatio = string.IsNullOrWhiteSpace(req.AspectRatio) ? null : req.AspectRatio.Trim();
        if (aspectRatio is not null && !AspectRatios.Contains(aspectRatio))
            return Invalid($"Пропорции {aspectRatio} не поддерживаются: только {string.Join(", ", AspectRatios)}");

        var limits = ImageEditCatalog.DefaultLimits;
        var sizes = new[] { source?.Bytes, req.Mask?.Bytes, req.Annotated?.Bytes }
            .Concat(req.Uploaded.Select(r => r.Bytes))
            .Where(b => b is not null);
        if (sizes.Any(b => b!.LongLength > MaxFileBytes))
            return Invalid($"Файл больше {limits.MaxFileMb} МБ");

        var references = new List<ReferenceImage>(req.Uploaded);

        foreach (var (path, role) in req.ReferencePaths)
        {
            var read = await ReadProjectImageAsync(scope.Project!.RootPath, path, "Образец", ct);
            if (read.Value is not { } image) return Fail<ImageEditJobInput>(read.ErrorCode, read.Error);
            references.Add(new ReferenceImage(image.Bytes, image.ContentType, role, Path.GetFileName(path)));
        }
        // Персонаж — фото из его папки образцами с ролью Character, первыми по порядку
        CharacterRef? character = null;
        if (req.CharacterSlug is { Length: > 0 } slug)
        {
            var found = CharacterStore.ForRequest(scope.Project!.RootPath, slug);
            if (found is null) return Invalid("Персонаж не найден");
            character = found.Ref;
            references.InsertRange(0, found.Photos);
        }
        if (references.Count > limits.MaxReferences)
            return Invalid($"Образцов не больше {limits.MaxReferences}");

        if (req.SourcePath is { Length: > 0 } sourcePath && ProjectLinkGuard.ResolveInside(scope.Project!.RootPath, sourcePath) is null)
            return Invalid("Исходник вне папки проекта или идёт через символическую ссылку");

        return ImageEditCallResult<ImageEditJobInput>.Ok(new ImageEditJobInput(
            req.QuoteId!.Trim(),
            req.Prompt ?? "",
            req.MarksJson,
            source,
            req.Mask,
            req.Annotated,
            references,
            req.SourcePath,
            character,
            MatchSourceSize: req.MatchSourceSize,
            ChatSessionId: chatId,
            Initiator: req.Initiator,
            BaseStepId: baseStepId,
            AspectRatio: aspectRatio,
            ThreadId: threadId,
            BaseVersionId: baseVersionId));
    }

    // Входы запуска — из стора контекста по ревизии клиента; поля входов тела заменяются раскладкой
    private async Task<ImageEditCallResult<ImageEditLaunchRequest>> ApplyContextAsync(
        string ownerId, ImageEditScope scope, ImageEditLaunchRequest req, long revision, CancellationToken ct)
    {
        static ImageEditCallResult<ImageEditLaunchRequest> Refuse(string? code, string? error, object? payload) =>
            payload is null
                ? ImageEditCallResult<ImageEditLaunchRequest>.Fail(code ?? ImageEditErrorCodes.InvalidRequest, error ?? "Запрос не выполнен")
                : ImageEditCallResult<ImageEditLaunchRequest>.Fail(code ?? ImageEditErrorCodes.InvalidRequest, error ?? "Запрос не выполнен", payload);

        if (context is null || jobs is null || threads is null)
            return Refuse(ImageEditErrorCodes.Unavailable, "Контекст чата недоступен на этом сервере", null);
        if (jobs.QuoteInfo(ownerId, scope.Key, req.QuoteId!.Trim()) is not { } quote)
            return Refuse(ImageEditErrorCodes.QuoteNotFound, "Котировка устарела — цена пересчитается автоматически", null);

        var resolved = context.Resolve(ownerId, scope, req.ThreadSessionId, revision, quote.Op);
        if (resolved.ErrorCode is not null) return Refuse(resolved.ErrorCode, resolved.Error, resolved.Payload);
        var (inputs, ctx, sessionId) = resolved.Value;
        // Цена посчитана на другой ревизии: образцы и персонаж уже другие, чем в котировке
        if (quote.ContextRevision is { } quoted && quoted != revision)
            return Refuse(ImageEditErrorCodes.ContextChanged, "Контекст чата изменился",
                context.Stale<ImageEditLaunchRequest>(ctx, sessionId).Payload);

        var thread = threads.Get(ownerId, sessionId).Threads.FirstOrDefault(t => t.Id == inputs.ThreadId);
        if (thread is null) return Refuse(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате", null);
        var version = inputs.VersionId is { } v ? thread.Version(v) : thread.CurrentVersion;
        if (version is null) return Refuse(ImageEditErrorCodes.VersionNotFound, "Версии нет в этой картинке", null);

        var source = await context.ReadVersionAsync(ownerId, scope, thread, version, ct);
        if (source.ErrorCode is not null) return Refuse(source.ErrorCode, source.Error, null);
        var samples = await context.LoadSamplesAsync(ownerId, scope, sessionId, inputs, ct);
        if (samples.Value is not { } loaded) return Refuse(samples.ErrorCode, samples.Error, null);

        return ImageEditCallResult<ImageEditLaunchRequest>.Ok(req with
        {
            Source = source.Value,
            SourcePath = req.SourcePath ?? thread.File,
            Uploaded = loaded,
            ReferencePaths = inputs.ReferencePaths,
            CharacterSlug = inputs.CharacterSlug,
            ThreadSessionId = sessionId,
            ThreadId = inputs.ThreadId,
            VersionId = inputs.VersionId,
        });
    }

    private static long MaxFileBytes => ImageEditCatalog.DefaultLimits.MaxFileMb * 1024L * 1024L;

    // Картинка проекта по пути — единственное чтение с диска для запуска (образцы и файл нити
    // у агента): строго внутри корня, не через символическую ссылку, в пределах лимита.
    // what — чем картинка служит, для текста отказа («Образец», «Файл картинки»)
    public static async Task<ImageEditCallResult<ImageBytes>> ReadProjectImageAsync(
        string projectRoot, string path, string what, CancellationToken ct)
    {
        var full = ProjectLinkGuard.ResolveInside(projectRoot, path);
        if (full is null)
            return Fail<ImageBytes>(ImageEditErrorCodes.InvalidRequest,
                $"{what} вне папки проекта или идёт через символическую ссылку");
        var info = new FileInfo(full);
        if (!info.Exists)
            return Fail<ImageBytes>(ImageEditErrorCodes.InvalidRequest, $"{what} не найден: {path}");
        if (info.Length > MaxFileBytes)
            return Fail<ImageBytes>(ImageEditErrorCodes.InvalidRequest,
                $"Файл больше {ImageEditCatalog.DefaultLimits.MaxFileMb} МБ");
        return ImageEditCallResult<ImageBytes>.Ok(
            new ImageBytes(await File.ReadAllBytesAsync(full, ct), ContentTypeByExtension(full)));
    }

    private static ImageEditCallResult<ImageEditJobInput> Invalid(string error) =>
        Fail<ImageEditJobInput>(ImageEditErrorCodes.InvalidRequest, error);

    private static ImageEditCallResult<T> Fail<T>(string? code, string? error) =>
        ImageEditCallResult<T>.Fail(code ?? ImageEditErrorCodes.InvalidRequest, error ?? "Запрос не выполнен");

    public static string ContentTypeByExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
}

// Вход запуска до проверок. Uploaded — образцы, пришедшие байтами (с компьютера), уже с ролями;
// ReferencePaths — образцы из проекта путями. Initiator — кто запустил: человек ручкой или агент
// инструментом; ThreadSessionId + ThreadId — нить картинки в чате проекта (ADR-019), VersionId —
// версия нити, от которой правка (null — текущая)
public sealed record ImageEditLaunchRequest(
    string? QuoteId,
    string? Prompt,
    string? MarksJson,
    string? SourcePath,
    ImageBytes? Source,
    ImageBytes? Mask,
    ImageBytes? Annotated,
    IReadOnlyList<ReferenceImage> Uploaded,
    IReadOnlyList<(string Path, ReferenceRole Role)> ReferencePaths,
    string? CharacterSlug,
    bool MatchSourceSize = true,
    string? BaseStepId = null,
    string? AspectRatio = null,
    ImageEditInitiator Initiator = ImageEditInitiator.Human,
    string? ThreadSessionId = null,
    string? ThreadId = null,
    string? VersionId = null,
    // Ревизия контекста чата, на которой человек видел цену (ADR-023 §Д2.1); null — запуск без контекста
    long? ContextRevision = null);
