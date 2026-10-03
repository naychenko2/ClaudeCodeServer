using ClaudeHomeServer.Models;
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
public sealed class ImageEditLaunchAssembler(
    IEnumerable<IImageEditor> editors,
    IImageEditJobs? jobs = null,
    IImageRaster? raster = null,
    ImageThreadService? threads = null,
    ImageEditSteps? steps = null)
{
    public static readonly IReadOnlyList<string> AspectRatios = ["1:1", "16:9", "9:16"];

    public async Task<ImageEditCallResult<ImageEditJobCreatedDto>> LaunchAsync(
        string ownerId, ImageEditScope scope, ImageEditLaunchRequest req, CancellationToken ct)
    {
        var assembled = await AssembleAsync(ownerId, scope, req, ct);
        if (assembled.Value is not { } input)
            return Fail<ImageEditJobCreatedDto>(assembled.ErrorCode, assembled.Error);

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
            req.QuoteId.Trim(),
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
