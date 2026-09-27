using System.Globalization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Images.Editing.Raster;

namespace ClaudeHomeServer.Services.ImageEditor;

// Запуск генерации — одна сборка входа на всех (ADR-018 §2): ручка POST …/jobs и
// MCP-инструмент image_generate (ImageEditorToolset) идут через неё. Две сборки разошлись бы в проверке путей,
// лимитов и владения чатом, а это ровно те места, где дыра стоит денег.
//
// Порядок отказов прежний, как в ImageEditorController.Start до выноса: поставщик → исполнитель
// → растр → котировка → пропорции → размеры → образцы → персонаж → исходник.
//
// Чат картинки. Чужой, несуществующий, обычный чат и чат другого проекта молча отбрасываются:
// задача запускается без чата, трата и события — без SessionId, а ответ не выдаёт, существует
// ли чужой чат. Свой чат после старта получает строку image_launch в ленту и запись в журнал
// состояния — из журнала о запуске узнаёт ход (блок ImageEditorStateContributor).
//
// Нить картинки (ADR-019). Запуск в нить — ThreadSessionId + ThreadId: нить обязана быть своей
// в своём чате этого проекта, иначе отказ thread_not_found ДО запуска и траты (молча отбросить
// нельзя: варианты ушли бы мимо карточки, которую человек видит). После старта варианты нити
// ждут «Взять», ручной запуск ложится тихой строкой image_launch через IChatFeed.
public sealed class ImageEditLaunchAssembler(
    IEnumerable<IImageEditor> editors,
    ImageChatStateStore states,
    ILogger<ImageEditLaunchAssembler> log,
    IImageEditJobs? jobs = null,
    IImageRaster? raster = null,
    ISessionDirectory? sessionDirectory = null,
    IImageChatSessions? chats = null,
    ISessionBroadcaster? broadcaster = null,
    ImageThreadService? threads = null)
{
    public static readonly IReadOnlyList<string> AspectRatios = ["1:1", "16:9", "9:16"];

    public async Task<ImageEditCallResult<ImageEditJobCreatedDto>> LaunchAsync(
        string ownerId, Project project, ImageEditLaunchRequest req, CancellationToken ct)
    {
        var assembled = await AssembleAsync(ownerId, project, req, ct);
        if (assembled.Value is not { } input)
            return Fail<ImageEditJobCreatedDto>(assembled.ErrorCode, assembled.Error);

        var started = await jobs!.StartAsync(ownerId, project.Id, input, ct);
        if (started.Value is { } created && input.ChatSessionId is { } chatId)
        {
            if (input.ThreadId is { } threadId && threads is not null)
                await threads.OnLaunchedAsync(ownerId, project.Id, chatId, threadId, jobs.Get(ownerId, project.Id, created.JobId),
                    created.JobId, input.Prompt, input.Initiator, ct);
            else
                await RecordLaunchAsync(ownerId, project, chatId, input, created.JobId);
        }
        return started;
    }

    // Вход задачи без запуска: всё проверено, байты прочитаны, чат либо свой, либо отброшен
    public async Task<ImageEditCallResult<ImageEditJobInput>> AssembleAsync(
        string ownerId, Project project, ImageEditLaunchRequest req, CancellationToken ct)
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

        string? threadId = null, chatId;
        if (!string.IsNullOrWhiteSpace(req.ThreadId))
        {
            if (threads is null || !threads.OwnThread(ownerId, project.Id, req.ThreadSessionId, req.ThreadId))
                return Fail<ImageEditJobInput>(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате");
            threadId = req.ThreadId.Trim();
            chatId = req.ThreadSessionId!.Trim();
        }
        else
        {
            chatId = OwnImageChat(project, req.ChatSessionId);
        }

        var aspectRatio = string.IsNullOrWhiteSpace(req.AspectRatio) ? null : req.AspectRatio.Trim();
        if (aspectRatio is not null && !AspectRatios.Contains(aspectRatio))
            return Invalid($"Пропорции {aspectRatio} не поддерживаются: только {string.Join(", ", AspectRatios)}");

        var limits = ImageEditCatalog.DefaultLimits;
        var sizes = new[] { req.Source?.Bytes, req.Mask?.Bytes, req.Annotated?.Bytes }
            .Concat(req.Uploaded.Select(r => r.Bytes))
            .Where(b => b is not null);
        if (sizes.Any(b => b!.LongLength > MaxFileBytes))
            return Invalid($"Файл больше {limits.MaxFileMb} МБ");

        var references = new List<ReferenceImage>(req.Uploaded);

        foreach (var (path, role) in req.ReferencePaths)
        {
            var read = await ReadProjectImageAsync(project.RootPath, path, "Образец", ct);
            if (read.Value is not { } image) return Fail<ImageEditJobInput>(read.ErrorCode, read.Error);
            references.Add(new ReferenceImage(image.Bytes, image.ContentType, role, Path.GetFileName(path)));
        }
        // Персонаж — фото из его папки образцами с ролью Character, первыми по порядку
        CharacterRef? character = null;
        if (req.CharacterSlug is { Length: > 0 } slug)
        {
            var found = CharacterStore.ForRequest(project.RootPath, slug);
            if (found is null) return Invalid("Персонаж не найден");
            character = found.Ref;
            references.InsertRange(0, found.Photos);
        }
        if (references.Count > limits.MaxReferences)
            return Invalid($"Образцов не больше {limits.MaxReferences}");

        if (req.SourcePath is { Length: > 0 } sourcePath && ProjectLinkGuard.ResolveInside(project.RootPath, sourcePath) is null)
            return Invalid("Исходник вне папки проекта или идёт через символическую ссылку");

        return ImageEditCallResult<ImageEditJobInput>.Ok(new ImageEditJobInput(
            req.QuoteId.Trim(),
            req.Prompt ?? "",
            req.MarksJson,
            req.Source,
            req.Mask,
            req.Annotated,
            references,
            req.SourcePath,
            character,
            MatchSourceSize: req.MatchSourceSize,
            ChatSessionId: chatId,
            Initiator: req.Initiator,
            BaseStepId: req.BaseStepId,
            AspectRatio: aspectRatio,
            ThreadId: threadId));
    }

    private static long MaxFileBytes => ImageEditCatalog.DefaultLimits.MaxFileMb * 1024L * 1024L;

    // Картинка проекта по пути — единственное чтение с диска для запуска (образцы и исходник
    // чата у агента): строго внутри корня, не через символическую ссылку, в пределах лимита.
    // what — чем картинка служит, для текста отказа («Образец», «Файл чата»)
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

    // Свой чат картинки этого проекта (проект уже свой — его проверил вызывающий) или null
    private string? OwnImageChat(Project project, string? chatSessionId)
    {
        if (string.IsNullOrWhiteSpace(chatSessionId) || sessionDirectory is null) return null;
        var session = sessionDirectory.GetById(chatSessionId.Trim());
        return session is { ImageChat: not null } && session.ProjectId == project.Id ? session.Id : null;
    }

    // Задача уже идёт: сбой записи в ленту или журнал не должен превращать 202 в ошибку
    private async Task RecordLaunchAsync(string ownerId, Project project, string chatId, ImageEditJobInput input,
        string jobId)
    {
        try
        {
            var job = jobs!.Get(ownerId, project.Id, jobId);
            var by = input.Initiator == ImageEditInitiator.Agent ? SpendInitiators.Agent : SpendInitiators.Human;
            var count = job?.Count ?? 0;
            var estimate = job?.Estimate is { } e ? new StoredImageLaunchEstimate(e.Amount, e.Unit, e.Approx, e.Source) : null;

            if (input.Initiator == ImageEditInitiator.Human && chats is not null)
                await chats.AppendLaunchAsync(chatId, new StoredImageLaunchMessage
                {
                    By = by,
                    Prompt = input.Prompt,
                    Provider = job?.Provider ?? "",
                    Model = job?.Model ?? "",
                    Count = count,
                    Estimate = estimate,
                    JobId = jobId,
                });

            var who = input.Initiator == ImageEditInitiator.Agent ? "Ты запустил" : "Человек запустил вручную";
            var text = $"{who}: «{input.Prompt.Trim()}» · {job?.Model ?? "модель по котировке"}"
                       + (count > 0 ? $" · {count} {ImageEditorStateContributor.Variants(count)}" : "")
                       + EstimateText(job?.Estimate);
            var state = states.AppendEvent(ownerId, chatId,
                new ImageChatEvent(states.Now(), ImageChatEventKinds.Launched, text, jobId));
            await BroadcastStateAsync(ownerId, project.Id, chatId, state, input.Initiator);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Редактор картинок: запуск {JobId} не записан в чат {ChatId}", jobId, chatId);
        }
    }

    public async Task BroadcastStateAsync(string ownerId, string projectId, string chatId, ImageChatState state,
        ImageEditInitiator changedBy, IReadOnlyList<ImageChatStateChange>? changes = null)
    {
        if (broadcaster is null) return;
        try
        {
            await broadcaster.ToOwner(ownerId,
                new ImageChatStateMessage(projectId, state.Revision, state, changedBy, changes ?? []) { SessionId = chatId });
        }
        catch (Exception ex)
        {
            // Потерянное событие редактор догоняет GET …/state
            log.LogDebug(ex, "Редактор картинок: состояние чата {ChatId} не разослано", chatId);
        }
    }

    private static string EstimateText(ImageEditEstimateDto? e) => e?.Amount is not { } amount ? ""
        : e.Unit == ImageEditPriceUnits.Usd
            ? string.Create(CultureInfo.InvariantCulture, $" · ≈ ${amount:0.##}")
            : string.Create(CultureInfo.InvariantCulture, $" · ≈ {amount:0.##} кр.");

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
// ReferencePaths — образцы из проекта путями. ChatSessionId — чат картинки, из которого запуск;
// Initiator — кто запустил: человек ручкой или агент инструментом; ThreadSessionId + ThreadId —
// нить картинки в чате проекта (ADR-019)
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
    string? ChatSessionId = null,
    ImageEditInitiator Initiator = ImageEditInitiator.Human,
    // Нить картинки в чате проекта (ADR-019); ChatSessionId при этом не нужен — это чат v2
    string? ThreadSessionId = null,
    string? ThreadId = null);
