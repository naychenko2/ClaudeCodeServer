using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
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
    ImageContextLaunch? context = null,
    IFeatureFlagGate? flags = null,
    CharacterSceneParser? sceneParser = null,
    CharacterPhotoLabeling? labeling = null,
    Faces.FaceDetectionCache? faces = null,
    ILocalImageMedia? localMedia = null)
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
                created.JobId, LaunchPrompt(input, req), input.Initiator, input.BaseVersionId, input.BaseStepId, ct,
                input.FacePasses?.Select(p => p.ThreadFaceTarget).ToList());
            // Быстрый поставщик мог закончить задачу раньше, чем запуск лёг в нить: Finished тогда не
            // нашёл запуска, и карточка висела бы в «Рисуем…». Финал догоняется здесь; событие, пришедшее
            // следом, увидит закрытый запуск и ничего не сделает
            if (jobs.Get(ownerId, scope.Key, created.JobId) is
                { Status: ImageEditJobStatus.Completed or ImageEditJobStatus.Failed or ImageEditJobStatus.Cancelled } done)
                await threads.OnJobFinishedAsync(ownerId, done);
        }
        return started;
    }

    // Промпт запуска для нити (карточка запуска, журнал агента). У запуска на несколько лиц промпт
    // задачи — промпт прохода 1 с одним персонажем, поэтому запуск называет все лица и их персонажей
    // (ADR-028) + дополнение человека, как у проходов. Иначе — промпт задачи как есть
    private static string LaunchPrompt(ImageEditJobInput input, ImageEditLaunchRequest req)
    {
        if (input.FacePasses is not { Count: > 1 } passes) return input.Prompt;
        var faces = string.Join(", ", passes.Select(p =>
            $"{p.Face} — «{(string.IsNullOrWhiteSpace(p.Character.Name) ? p.Character.Slug : p.Character.Name)}»"));
        var extra = string.IsNullOrWhiteSpace(req.Prompt) ? "" : " " + req.Prompt!.Trim();
        return $"Подстановка на лица: {faces}.{extra}";
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
        // Подстановка персонажа на лицо (ADR-028, этап 2): отдельный путь, контекст игнорируется
        if (req.FaceTarget is { } faceTarget)
            return await AssembleFaceTargetAsync(ownerId, scope, req, faceTarget, ct);
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
        // «Дорисовать» (ADR-025): холст любых пропорций «W:H», белый список трёх пропорций — только у старого пути правки.
        // Модели дорисовки формата не объявляют (caps.Format = null), поэтому проверка — читаемость пропорции,
        // тот же разбор, что у ImageFormatPlan
        var outpaint = jobs!.QuoteInfo(ownerId, scope.Key, req.QuoteId!.Trim())?.Op == ImageEditOp.Outpaint;
        if (aspectRatio is not null)
        {
            if (outpaint && !AspectCrop.TryParse(aspectRatio, out _))
                return Invalid($"Пропорции {aspectRatio} не читаются: нужен вид «W:H»");
            if (!outpaint && !AspectRatios.Contains(aspectRatio))
                return Invalid($"Пропорции {aspectRatio} не поддерживаются: только {string.Join(", ", AspectRatios)}");
        }

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
        Func<int, CancellationToken, Task<CharacterPickOutcome>>? picker = null;
        if (req.CharacterSlug is { Length: > 0 } slug)
        {
            var found = CharacterStore.ForRequest(scope.Project!.RootPath, ownerId, slug);
            if (found is null) return Invalid("Персонаж не найден");
            labeling?.Enqueue(ownerId, scope.Project!.RootPath, slug);
            // Явный список — только при включённом флаге; любое нарушение отказывает целиком
            if (req.CharacterPhotos is { } chosen)
            {
                found = flags?.IsEnabled(ownerId, FeatureFlagKeys.CharacterSmartPhotos) == true
                    ? CharacterStore.ForRequest(scope.Project!.RootPath, ownerId, slug, explicitPhotos: chosen)
                    : null;
                if (found is null) return PhotosInvalid();
            }
            character = found.Ref;
            references.InsertRange(0, found.Photos);
            picker = CharacterPicker(ownerId, scope.Project!.RootPath, slug, req, found.Photos.Count);
        }
        else if (req.CharacterPhotos is not null)
            return PhotosInvalid();
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
            BaseVersionId: baseVersionId,
            CharacterPicker: picker));
    }

    // Подбор фото персонажа под сюжет (ADR-027 §7) — только при включённом флаге владельца. Возвращает
    // делегат, который исполнитель зовёт в задаче перед драйвером: разбор сюжета не держит ответ «запустить».
    // Явный список — Pick=manual без подбора; пустой промпт или нет разбора — подбора нет, запись в запуск тоже
    private Func<int, CancellationToken, Task<CharacterPickOutcome>>? CharacterPicker(
        string ownerId, string root, string slug, ImageEditLaunchRequest req, int count)
    {
        if (flags?.IsEnabled(ownerId, FeatureFlagKeys.CharacterSmartPhotos) != true) return null;
        if (req.CharacterPhotos is { } chosen)
            return (_, _) => Task.FromResult(new CharacterPickOutcome(null, new ImageThreadCharacter(slug, chosen, CharacterPhotoPick.Manual, null)));
        if (sceneParser is null || string.IsNullOrWhiteSpace(req.Prompt)) return null;

        var prompt = req.Prompt;
        return async (confidentScore, ct) =>
        {
            var manifest = CharacterStore.Get(root, slug);
            if (manifest is null) throw new InvalidOperationException("Персонаж пропал до подбора");
            var scene = await sceneParser.ParseAsync(prompt, ct);
            var pick = CharacterPhotoPicker.Pick(manifest.Photos, scene, count, confidentScore);
            var names = pick.Photos.Select(p => p.File).ToList();
            var launch = new ImageThreadCharacter(slug, names, pick.Pick, scene);
            if (pick.Pick == CharacterPhotoPick.Fallback) return new CharacterPickOutcome(null, launch);

            // Подобранные фото читаются тем же путём, что явный список; не прочиталось — прежний порядок
            var picked = CharacterStore.ForRequest(root, ownerId, slug, count, names);
            if (picked is null)
                return new CharacterPickOutcome(null, launch with { Pick = CharacterPhotoPick.Fallback });
            return new CharacterPickOutcome(picked.Photos, launch);
        };
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

    // Подстановка персонажа на лицо (ADR-028, этап 3): контекст чата игнорируется целиком,
    // образцы — только персонаж, маска строится сервером по bbox детектора.
    // Порядок: a. флаг → b. личная область/входы → c. версия → d. детекция/facesKey →
    // e. промпт → f. проходы (одна детекция, N масок/кропов, по возрастанию номера лица) →
    // g. входы (N = 1 — старые поля, N > 1 — FacePasses: цепочка проходов в задаче)
    private async Task<ImageEditCallResult<ImageEditJobInput>> AssembleFaceTargetAsync(
        string ownerId, ImageEditScope scope, ImageEditLaunchRequest req, FaceTarget faceTarget, CancellationToken ct)
    {
        // a. Флаг выключен — как в 7a
        if (flags?.IsEnabled(ownerId, FeatureFlagKeys.CharacterFaceSwap) != true)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.FeatureDisabled, "Подстановка персонажа на лицо выключена");

        // b. Личная область, входы
        if (scope.Project is null)
            return Invalid("Подстановка персонажа на лицо — только в проекте");
        // Правила запроса (ADR-028, этап 3): лимит лиц, повторы номеров и персонажей
        var rules = Faces.FaceTargetRules.Validate(faceTarget);
        if (!rules.Ok)
            return Fail<ImageEditJobInput>(rules.ErrorCode!, rules.Error!);
        // Котировка: сверка числа проходов (ADR-028, этап 3) — цена и ETA посчитаны на N лиц;
        // при отказе котировка не расходуется. Почему две сверки (здесь и в StartAsync): здесь —
        // до детекции и сборки проходов, там — защита в глубину для вызовов мимо сборщика
        var quote = jobs!.QuoteInfo(ownerId, scope.Key, req.QuoteId!.Trim());
        if (quote is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.QuoteNotFound, "Котировка устарела");
        if (faceTarget.Faces.Count != (quote.FacePasses ?? 0))
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.QuoteNotFound,
                "Котировка посчитана на другое число лиц — цена пересчитается автоматически");
        if (req.Mask is not null || req.Annotated is not null)
            return Invalid("При подстановке на лицо маску и разметку не указывают");
        // Провайдер — только local
        if (quote.Provider is not "local")
            return Invalid("Подставлять персонажа можно только локальными моделями");
        if (req.ContextRevision is not null)
            return Invalid("Подстановка на лицо несовместима с контекстом чата");
        if (req.ReferencePaths.Count > 0 || req.Uploaded.Count > 0)
            return Invalid("При подстановке на лицо образцы из тела не указывают");

        // c. Версия в нити
        if (threads is null || !threads.OwnThread(ownerId, scope.Key, req.ThreadSessionId, req.ThreadId))
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате");
        var thread = threads.Get(ownerId, req.ThreadSessionId!.Trim()).Threads.FirstOrDefault(t => t.Id == req.ThreadId!.Trim());
        if (thread is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.ThreadNotFound, "Картинка не найдена в этом чате");
        var version = thread.Version(faceTarget.VersionId);
        if (version is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.VersionNotFound, "Версии нет в этой картинке");

        // Байты версии: шаг версии, иначе файл проекта
        var sourceBytes = await VersionBytesAsync(ownerId, scope, thread, version, ct);
        if (sourceBytes is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.VersionNotFound, "У версии ещё нет изображения");

        // Нормализация EXIF: применяем EXIF-поворот и обнуляем тег на стороне бэкенда, чтобы маска
        // (по bbox детекции) и исходник были в одной системе координат (той, что видит пользователь),
        // независимо от того, как загрузчик узла/воркера трактует EXIF. facesKey — по сырым байтам (фронт
        // считает его на исходнике), поэтому сверка facesKey идёт до нормализации.
        var (normalizedSource, sourceType) = NormalizeExif(sourceBytes);

        // d. Детекция: кэш по нормализованным байтам, промах — детектор
        var detector = faces ?? new Faces.FaceDetectionCache();
        Func<Task<LocalFaceDetectResult>> detect = localMedia is null
            ? () => Task.FromResult(new LocalFaceDetectResult(0, 0, [], "Детекция лиц здесь не поддерживается."))
            : () => localMedia.DetectFacesAsync(normalizedSource, CancellationToken.None);
        var result = await detector.GetOrDetectAsync(ownerId, normalizedSource, detect, ct);
        if (result.Busy)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.Busy, "Видеокарта занята долгой задачей. Попробуйте чуть позже");
        if (result.Error is not null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.ProviderUnavailable, result.Error);
        // facesKey не совпал — 409 со свежим списком (та же сборка, что в 7a); сверка по сырым байтам
        if (Faces.FaceDetectionCache.FacesKey(sourceBytes) != faceTarget.FacesKey)
            return ImageEditCallResult<ImageEditJobInput>.Fail(ImageEditErrorCodes.FacesChanged, "Список лиц изменился",
                ClaudeHomeServer.Services.ImageEditor.Controllers.ImageThreadFacesResponse.FromDetection(sourceBytes, result));

        // e. Проходы: детекция одна (по базовой версии), все маски и кропы — до постановки в очередь.
        // Порядок — по возрастанию номера лица (ADR-028, решение 2); отказ маски называет номер лица.
        // Каждый проход собирается заново из СВОЕГО персонажа (CharacterStore.ForRequest), поэтому
        // VisibleTo проверяется на каждом слаге, а образцы от прохода к проходу не переносятся.
        // Промпт — на проход: имя персонажа подставляется (ADR-028, решение 12: «лицо персонажа «…»»),
        // модель видит кроп, поэтому лицо — «в центре первой картинки», а не «в выделенной области»
        var maskBuilder = raster is null ? null : new FaceMaskBuilder(raster);
        if (maskBuilder is null)
            return Fail<ImageEditJobInput>(ImageEditErrorCodes.RasterUnavailable, "Обработка картинок выключена");
        var source = new ImageBytes(normalizedSource, sourceType);
        var extra = string.IsNullOrWhiteSpace(req.Prompt) ? "" : " " + req.Prompt!.Trim();
        var passes = new List<FacePass>();
        foreach (var assignment in faceTarget.Faces.OrderBy(fa => fa.Face))
        {
            var face = result.Faces.FirstOrDefault(f => f.N == assignment.Face);
            if (face is null)
                return FaceFail(ImageEditErrorCodes.InvalidRequest,
                    $"Лицо {assignment.Face} не найдено: на картинке {result.Faces.Count} лиц", assignment.Face);
            if (face.Small)
                return FaceFail(ImageEditErrorCodes.InvalidRequest, $"Лицо {assignment.Face} слишком мелкое для подстановки персонажа", assignment.Face);
            var maskResult = maskBuilder.Build(result.Width, result.Height, result.Faces, assignment.Face, quote.InputCaps);
            if (maskResult.Png is null)
            {
                var code = maskResult.Reason switch
                {
                    FaceMaskReason.TooClose => ImageEditErrorCodes.FacesTooClose,
                    FaceMaskReason.OutOfFrame => ImageEditErrorCodes.FaceOutOfFrame,
                    _ => ImageEditErrorCodes.InvalidRequest,
                };
                return FaceFail(code, $"Лицо {assignment.Face}: {maskResult.Error ?? "Не удалось построить маску лица"}", assignment.Face);
            }
            var character = CharacterStore.ForRequest(scope.Project!.RootPath, ownerId, assignment.CharacterSlug);
            if (character is null)
                return FaceFail(ImageEditErrorCodes.InvalidRequest,
                    $"Персонаж «{assignment.CharacterSlug}» не найден", assignment.Face);
            var mask = new ImageBytes(maskResult.Png, "image/png");
            var prompt = $"Лицо человека в центре первой картинки заменить на лицо персонажа «{character.Ref.Name}», всё остальное оставить как есть.{extra}";
            passes.Add(new FacePass(
                assignment.Face,
                mask,
                maskResult.Crop,
                mask,
                character.Ref,
                character.Photos,
                new ImageThreadFaceTarget(assignment.Face, [face.X1, face.Y1, face.X2, face.Y2], assignment.CharacterSlug),
                prompt));
        }

        // f. Входы: Source = нормализованные байты версии (EXIF применён, тег обнулён), образцы = персонаж
        // первого прохода (общий путь N = 1). Кроп и маска — отдельным каналом: модель видит кроп, а не
        // маску; маска — «вне неё вернуть исходник». Для N = 1 старые поля (FaceCrop/KeepMask/Character/
        // References/FaceTarget) — первый проход через ForPass: та же подстановка, что у проходов
        // цепочки, поэтому N = 1 и проход 1 разойтись не могут
        var baseInput = new ImageEditJobInput(
            req.QuoteId!.Trim(),
            "",
            req.MarksJson,
            source,
            null,
            null,
            [],
            req.SourcePath,
            MatchSourceSize: req.MatchSourceSize,
            ChatSessionId: req.ThreadSessionId,
            Initiator: req.Initiator,
            BaseStepId: null,
            AspectRatio: null,
            ThreadId: req.ThreadId,
            BaseVersionId: faceTarget.VersionId,
            CharacterPicker: null,
            FacePasses: passes);
        return ImageEditCallResult<ImageEditJobInput>.Ok(baseInput.ForPass(passes[0]));
    }

    // Байты версии: текущий шаг (рабочая папка), у исходника — файл проекта. null — картинки нет
    private async Task<byte[]?> VersionBytesAsync(string ownerId, ImageEditScope scope, ImageThread thread,
        ImageThreadVersion version, CancellationToken ct)
    {
        if (thread.ImageStepOf(version) is { Length: > 0 } stepId && steps?.Open(ownerId, scope.Key, stepId) is { } found)
            return found.Image.Bytes;
        if (thread.File is { Length: > 0 } file && scope.Project is { } project)
        {
            var read = await ReadProjectImageAsync(project.RootPath, file, "Версия", ct);
            return read.Value?.Bytes;
        }
        return null;
    }

    // Нормализация EXIF: применяем EXIF-поворот и обнуляем тег, чтобы маска (по bbox детекции)
    // и исходник были в одной системе координат. Orientation 1 (или нет EXIF) — байты не трогаем.
    // raster = null (подсистема images выключена) — исходные байты; маска всё равно не построится.
    // Возвращает байты и метку (mime) реального формата: JPEG без повороста остаётся JPEG-байтами,
    // а не «image/png» (метка по сигнатуре финальных байт, не жёсткая)
    private (byte[] Bytes, string ContentType) NormalizeExif(byte[] image)
    {
        if (raster is null) return (image, ContentTypeOfBytes(image));
        var probe = raster.Probe(image);
        if (probe is null || probe.Orientation <= 1) return (image, ContentTypeOfBytes(image));
        var outcome = raster.Apply(image, [], null);
        return outcome.Image is { } img ? (img.Bytes, ContentTypeOfBytes(img.Bytes)) : (image, ContentTypeOfBytes(image));
    }

    // Метка (mime) по сигнатуре байт: JPEG/PNG/WebP/GIF; нераспознанное — «image/png» (как раньше)
    private static string ContentTypeOfBytes(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        return "image/png";
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

    private static ImageEditCallResult<ImageEditJobInput> PhotosInvalid() =>
        Fail<ImageEditJobInput>(ImageEditErrorCodes.CharacterPhotosInvalid, "Выбранные фото не подходят этому персонажу");

    private static ImageEditCallResult<ImageEditJobInput> Invalid(string error) =>
        Fail<ImageEditJobInput>(ImageEditErrorCodes.InvalidRequest, error);

    // Отказ по конкретному лицу: тело {error, code, face} — фронт подсвечивает виновное лицо (ADR-028, этап 3)
    private static ImageEditCallResult<ImageEditJobInput> FaceFail(string code, string error, int face) =>
        ImageEditCallResult<ImageEditJobInput>.Fail(code, error, new FaceRefusalResponse(error, code, face));

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
    long? ContextRevision = null,
    // Явный список фото персонажа (имена файлов манифеста, порядок значим); null — подбор как обычно
    IReadOnlyList<string>? CharacterPhotos = null,
    // Подстановка персонажа на лицо (ADR-028, этап 2): при наличии контекст чата игнорируется
    // целиком, образцы — только персонаж, маска строится сервером
    FaceTarget? FaceTarget = null);
