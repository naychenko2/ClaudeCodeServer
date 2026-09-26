using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Images.LocalMedia;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Локальная генерация картинок и видео нашими моделями на своей GPU через ComfyUI
/// (local_*). Маршрут — <c>POST /mcp/local-media/{sessionId}</c>: хвост несёт
/// СЕССИЮ-ВЫЗЫВАТЕЛЬ, по ней тулсет знает владельца и проект, в папку которого ляжет
/// результат (<c>.cc-attachments/local-media/{дата}/</c>).
///
/// Модель выбирает только операцию и её параметры: граф ComfyUI собирается из фиксированных
/// шаблонов (<see cref="ComfyWorkflows"/>), произвольный граф не принимается никогда.
/// Генерация асинхронная: вызов ставит задачу и сразу отдаёт job_id, результат ждут
/// local_jobs_wait/local_job_status, а фоновый коллектор дописывает его в проект и без них.
///
/// Гейты на КАЖДЫЙ вызов: сессия владельца токена (fail-closed), тумблер LocalMedia:Enabled,
/// чат проекта, проект на сервере (<see cref="ProjectCapabilities"/>), задачи — только свои.
/// ИНВАРИАНТ состава: tools/list зависит только от сессии-вызывателя, не от хода.
/// stdio-ветки отката нет (node-сервера не существовало), как у websearch/higgsfield.
/// </summary>
public sealed class LocalMediaToolset(
    SessionManager sessions,
    ProjectManager projects,
    // Из отключаемой вертикали Images: выключена — инструменты честно отказывают
    LocalMediaService? media = null) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.LocalMediaName;

    public string Name => ServerName;
    public string Version => "2.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolveSession(context, out _, out _) ? Tools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolveSession(context, out var session, out var error)) return Deny(error);
        if (media is null || !media.Enabled)
            return Deny("Локальная генерация выключена на этом сервере — используй облачные генераторы.");
        if (!TryResolveProject(session, context.OwnerId, out var project, out error)) return Deny(error);

        switch (tool)
        {
            case "local_generate_image":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.GenerateImage,
                    Prompt: StringArg(arguments, "prompt"),
                    NegativePrompt: StringArg(arguments, "negative_prompt"),
                    Aspect: StringArg(arguments, "aspect"),
                    Steps: IntArg(arguments, "steps"),
                    Count: IntArg(arguments, "count"),
                    Seed: LongArg(arguments, "seed")), ct);

            case "local_edit_image":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.EditImage,
                    Prompt: StringArg(arguments, "prompt"),
                    Aspect: StringArg(arguments, "aspect"),
                    Seed: LongArg(arguments, "seed"),
                    Images: StringListArg(arguments, "images")), ct);

            case "local_face_detail":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.FaceDetail,
                    Seed: LongArg(arguments, "seed"),
                    Images: [StringArg(arguments, "image") ?? ""]), ct);

            case "local_text_to_video":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.TextToVideo,
                    Prompt: StringArg(arguments, "prompt"),
                    Seed: LongArg(arguments, "seed"),
                    VideoSize: StringArg(arguments, "size"),
                    Orientation: StringArg(arguments, "orientation"),
                    DurationSeconds: IntArg(arguments, "duration_seconds"),
                    Fast: BoolArg(arguments, "fast")), ct);

            case "local_image_to_video":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.ImageToVideo,
                    Prompt: StringArg(arguments, "prompt"),
                    Seed: LongArg(arguments, "seed"),
                    Images: [StringArg(arguments, "image") ?? ""],
                    LastFrame: StringArg(arguments, "last_frame"),
                    VideoSize: StringArg(arguments, "size"),
                    DurationSeconds: IntArg(arguments, "duration_seconds"),
                    Fast: BoolArg(arguments, "fast")), ct);

            case "local_reference_to_video":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.ReferenceToVideo,
                    Prompt: StringArg(arguments, "prompt"),
                    Seed: LongArg(arguments, "seed"),
                    Images: StringListArg(arguments, "ref_images"),
                    RefVideos: StringListArg(arguments, "ref_videos"),
                    RefAudios: StringListArg(arguments, "ref_audios"),
                    VideoSize: StringArg(arguments, "size"),
                    Orientation: StringArg(arguments, "orientation"),
                    DurationSeconds: IntArg(arguments, "duration_seconds"),
                    Identity: StringArg(arguments, "identity")), ct);

            case "local_video_upscale":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.VideoUpscale,
                    Seed: LongArg(arguments, "seed"),
                    SourceJobId: StringArg(arguments, "job_id"),
                    UpscaleTarget: StringArg(arguments, "target")), ct);

            case "local_video_inpaint":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.VideoInpaint,
                    Prompt: StringArg(arguments, "prompt"),
                    Seed: LongArg(arguments, "seed"),
                    Video: StringArg(arguments, "video"),
                    Mask: StringArg(arguments, "mask")), ct);

            case "local_job_status":
            {
                var jobId = StringArg(arguments, "job_id") ?? "";
                // Чужая задача неотличима от несуществующей
                var view = await media.GetAsync(context.OwnerId, jobId, ct);
                return view is null ? Deny($"Задача {jobId} не найдена.") : Json(JobJson(view));
            }

            case "local_jobs_wait":
            {
                var ids = StringListArg(arguments, "job_ids");
                if (ids.Count == 0) return Deny("Передай job_ids — id задач из ответов local_*.");
                var (jobs, missing) = await media.WaitAsync(context.OwnerId, ids,
                    IntArg(arguments, "timeout_seconds") ?? LocalMediaService.MaxWaitSeconds, ct);
                var result = new JsonObject
                {
                    ["all_done"] = jobs.Count > 0 && jobs.All(j => LocalMediaStatuses.IsTerminal(j.Job.Status)),
                    ["jobs"] = new JsonArray([.. jobs.Select(j => (JsonNode)JobJson(j))]),
                };
                if (missing.Count > 0) result["not_found"] = new JsonArray([.. missing.Select(m => (JsonNode)m)]);
                return Json(result);
            }

            case "local_models":
                return Json(await ModelsJson(ct));

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    private async Task<McpToolCallResult> SubmitAsync(LocalMediaRequest request, CancellationToken ct)
    {
        var result = await media!.SubmitAsync(request, ct);
        if (result.View is not { } view) return Deny(result.Error ?? "Задача не поставлена.");
        var json = JobJson(view);
        json["note"] = "Задача поставлена в очередь локальной GPU. Дождись результата через "
            + $"local_jobs_wait(job_ids=[\"{view.Job.Id}\"]) — вызывай повторно, пока all_done=false.";
        return Json(json);
    }

    // Статус задачи: готовые файлы — в стиле fal ({images|videos:[{url,content_type,…}]}).
    // url — same-origin поток файла проекта, path — путь от корня проекта: его можно передать
    // во вход следующей операции и показать в ответе картинкой ![](path)
    private static JsonObject JobJson(LocalMediaJobView view)
    {
        var job = view.Job;
        var json = new JsonObject
        {
            ["job_id"] = job.Id,
            ["operation"] = job.Op,
            ["status"] = job.Status,
            ["seed"] = job.Seed,
        };
        if (job.Heavy) json["heavy"] = true;
        if (view.Position is { } position && !LocalMediaStatuses.IsTerminal(job.Status)) json["position"] = position;
        if (view.EtaSeconds is { } eta) json["eta_seconds"] = eta;
        if (job.Error is { } error) json["error"] = error;
        if (view.Warning is { } warning) json["warning"] = warning;

        if (job.Status == LocalMediaStatuses.Completed)
        {
            var images = new JsonArray();
            var videos = new JsonArray();
            foreach (var output in job.Outputs)
            {
                var item = new JsonObject
                {
                    ["url"] = StreamUrl(job.ProjectId, output.Path),
                    ["content_type"] = output.ContentType,
                    ["width"] = output.Width,
                    ["height"] = output.Height,
                    ["path"] = output.Path,
                };
                (output.ContentType.StartsWith("video/", StringComparison.Ordinal) ? videos : images).Add(item);
            }
            if (images.Count > 0) json["images"] = images;
            if (videos.Count > 0) json["videos"] = videos;
        }
        return json;
    }

    internal static string StreamUrl(string projectId, string path) =>
        $"/api/projects/{Uri.EscapeDataString(projectId)}/files/stream?path={Uri.EscapeDataString(path)}";

    private async Task<JsonObject> ModelsJson(CancellationToken ct)
    {
        var options = media!.Options;
        var queue = await media.QueueLengthAsync(ct);
        return new JsonObject
        {
            ["comfy_available"] = queue is not null,
            ["queue_length"] = queue,
            ["queue_limit"] = options.MaxComfyQueue,
            ["max_active_jobs_per_user"] = options.MaxQueuedPerOwner,
            ["max_heavy_jobs_per_user"] = 1,
            ["video_fast_default"] = options.VideoFastDefault,
            ["upscale_1440_mode"] = options.Upscale1440Tiled ? "tiled" : "single",
            ["eta_note"] = EtaNote,
            ["operations"] = new JsonArray
            {
                Operation("local_generate_image", "Qwen-Image 2.1", "картинка по тексту, 1–4 варианта", "≈45 с на картинку при 25 шагах"),
                Operation("local_edit_image", "Qwen-Image 2.1 (правка)", "правка по 1–16 референсам", "≈60 с"),
                Operation("local_face_detail", "FaceDetailer (YOLOv8 + Qwen-Image)", "доводка лиц на готовой картинке", "≈25 с"),
                Operation("local_text_to_video", "MiniMax H3 fl2va + turbo-LoRA 8 шагов",
                    $"видео со звуком по тексту, до {options.MaxVideoSeconds} с, 24 fps", T2vEtaText,
                    new JsonObject { ["full_5s"] = 310, ["full_10s"] = null, ["half_5s"] = null }),
                Operation("local_image_to_video", "MiniMax H3 fl2va + turbo-LoRA 8 шагов",
                    $"видео со звуком от первого кадра (и необязательно последнего), до {options.MaxVideoSeconds} с, 24 fps",
                    I2vEtaText,
                    new JsonObject
                    {
                        ["full_5s"] = 333, ["full_5s_fast"] = 234, ["full_10s"] = 943, ["full_10s_fast"] = 585,
                        ["half_5s"] = 94, ["half_10s"] = null,
                    }),
                Operation("local_reference_to_video", "MiniMax H3 ref2va + turbo-LoRA 4 шага",
                    "видео по референсам: 1–9 картинок, до 3 видео (2–15 с) и до 3 звуков; identity=max — тяжёлая",
                    "не замерено"),
                Operation("local_video_upscale", "MiniMax H3 Latent Upscaler 3D + донастройка 3 шага",
                    "апскейл видео прошлой задачи до 1440p (2528×1440) или 2K (2688×1536) по её латенту; тяжёлая",
                    "1440p ролика 5 с: по тайлам ≈ 15 мин, одним проходом ≈ 18 мин; 2K — не замерено",
                    new JsonObject { ["1440p_tiled_5s"] = 915, ["1440p_single_5s"] = 1065, ["2k"] = null }),
                Operation("local_video_inpaint", "MiniMax H3 ref2va + Fun ControlNet Union, 4 шага",
                    $"перерисовать область видео по маске, вне маски остаётся исходник и его звук; видео до "
                    + $"{options.MaxVideoSeconds} с; тяжёлая",
                    "864×480, 5 с ≈ 1,5 мин; 1344×768 — не замерено",
                    new JsonObject { ["half_5s"] = 98, ["full_5s"] = null }),
            },
            ["aspects"] = new JsonArray([.. ComfyWorkflows.ImageSizes.Select(s =>
                (JsonNode)$"{s.Key} ({s.Value.Width}×{s.Value.Height})")]),
            ["video_sizes"] = new JsonArray([.. ComfyWorkflows.VideoSizes.Select(s =>
                (JsonNode)$"{s.Key} ({s.Value.Width}×{s.Value.Height}, портретный кадр — стороны меняются местами)")]),
            ["upscale_targets"] = new JsonArray { "1440p (2528×1440)", "2k (2688×1536)" },
        };

        static JsonObject Operation(string tool, string model, string what, string eta, JsonObject? etaSeconds = null)
        {
            var op = new JsonObject
            {
                ["tool"] = tool,
                ["model"] = model,
                ["what"] = what,
                ["eta"] = eta,
            };
            if (etaSeconds is not null) op["eta_seconds"] = etaSeconds;
            return op;
        }
    }

    // --- Маршрут и контекст: /mcp/local-media/{sessionId} ---

    // Один сегмент — id сессии; форма как у тулсетов волны 2 (белый список resumeSessionId)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    private bool TryResolveSession(McpToolCallContext context, out Session session, out string error)
    {
        session = null!;
        error = "";
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера локальной генерации — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ закрыт.";
            return false;
        }
        session = owned;
        return true;
    }

    private bool TryResolveProject(Session session, string ownerId, out Project project, out string error)
    {
        project = null!;
        error = "";
        if (session.ProjectId is null)
        {
            error = "Локальная генерация работает только в чате проекта: результат сохраняется в папку проекта.";
            return false;
        }
        var found = projects.GetById(session.ProjectId);
        if (found is null || !string.Equals(found.OwnerId, ownerId, StringComparison.Ordinal))
        {
            error = "Проект чата не найден.";
            return false;
        }
        if (!ProjectCapabilities.FilesOnServer(found))
        {
            error = ProjectCapabilities.ServerContentOffReason;
            return false;
        }
        project = found;
        return true;
    }

    // --- Ответы и аргументы ---

    // Ответы — как у соседних тулсетов: кириллица без \u
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static McpToolCallResult Json(JsonObject value) => new(value.ToJsonString(JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string? StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? BoolArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? IntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    // Узел числа бывает и long, и int (JsonValue не приводит одно к другому)
    private static long? LongArg(JsonObject arguments, string name) =>
        arguments[name] is not JsonValue v ? null
        : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<int>(out var i) ? i
        : null;

    private static IReadOnlyList<string> StringListArg(JsonObject arguments, string name) =>
        arguments[name] is JsonArray list
            ? list.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList()
            : [];

    private static readonly Lazy<IReadOnlyList<McpToolSchema>> _tools = new(() => [.. Schemas()]);

    internal static IReadOnlyList<McpToolSchema> Tools => _tools.Value;

    private const string ImageRefDescription =
        "Путь файла проекта от его корня (например, .cc-attachments/local-media/2026-09-26/lm_…-1.png) "
        + "или job_id завершённой задачи local_* в этом же проекте";

    // Первое предложение КАЖДОГО генерирующего инструмента: модель, только что звавшая local_* в
    // этом чате, иначе продолжает звать его и на «нарисуй» без «локально» (QA 1e099019, дефект 2)
    internal const string ExplicitOnly =
        "Вызывай ТОЛЬКО если пользователь явно попросил локальную генерацию (локально / нашими моделями / "
        + "на своей видеокарте / бесплатно) в ТЕКУЩЕЙ просьбе; иначе используй glif/fal. ";

    private static IEnumerable<McpToolSchema> Schemas()
    {
        var aspects = new JsonArray([.. ComfyWorkflows.ImageSizes.Keys.Select(k => (JsonNode)k)]);

        yield return Tool("local_generate_image",
            ExplicitOnly + "Сгенерировать картинку по тексту НАШЕЙ моделью Qwen-Image 2.1 на своей GPU (бесплатно, "
            + "асинхронно). Возвращает job_id; результат жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "prompt" },
                ["properties"] = new JsonObject
                {
                    ["prompt"] = Str("Описание картинки; модель понимает русский и английский, умеет текст на картинке"),
                    ["negative_prompt"] = Str("Чего на картинке быть не должно (необязательно)"),
                    ["aspect"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = aspects.DeepClone(),
                        ["description"] = "Соотношение сторон (по умолчанию 1:1 — 1328×1328)",
                    },
                    ["steps"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = ComfyWorkflows.MinSteps,
                        ["maximum"] = ComfyWorkflows.MaxSteps,
                        ["description"] = "Шаги сэмплера (по умолчанию 25)",
                    },
                    ["count"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = ComfyWorkflows.MaxCount,
                        ["description"] = "Сколько вариантов (1–4, по умолчанию 1)",
                    },
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_edit_image",
            ExplicitOnly + "Отредактировать картинку НАШЕЙ моделью Qwen-Image 2.1 на своей GPU по 1–16 референсам: "
            + "первая картинка — основная (холст), остальные — дополнительные референсы (персонаж, предмет, стиль). "
            + "Возвращает job_id; результат жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "prompt", "images" },
                ["properties"] = new JsonObject
                {
                    ["prompt"] = Str("Что изменить или собрать из референсов"),
                    ["images"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = ComfyWorkflows.MaxEditImages,
                        ["items"] = Str(ImageRefDescription),
                        ["description"] = "Входные картинки: первая — основная",
                    },
                    ["aspect"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = aspects.DeepClone(),
                        ["description"] = "Размер результата; не задан — по первой картинке",
                    },
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_face_detail",
            ExplicitOnly + "Довести лица на готовой картинке (FaceDetailer: находит лица и перерисовывает их "
            + "детальнее) НАШЕЙ моделью на своей GPU. Возвращает job_id.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "image" },
                ["properties"] = new JsonObject
                {
                    ["image"] = Str(ImageRefDescription),
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_text_to_video",
            ExplicitOnly + "Сгенерировать видео со звуком по тексту НАШЕЙ моделью MiniMax H3 на своей GPU. prompt "
            + "описывает сцену, движение, камеру, реплики и звук. Долго (" + T2vEtaText + "). Результат можно "
            + "потом увеличить local_video_upscale. Возвращает job_id; жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "prompt" },
                ["properties"] = new JsonObject
                {
                    ["prompt"] = Str("Сцена, движение, камера, реплики (в кавычках), звук"),
                    ["duration_seconds"] = VideoDuration(),
                    ["size"] = VideoSizeSchema(),
                    ["orientation"] = Orientation(),
                    ["fast"] = Fast(),
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_image_to_video",
            ExplicitOnly + "Оживить картинку в видео со звуком НАШЕЙ моделью MiniMax H3 на своей GPU: первый кадр — "
            + "картинка, необязательный last_frame — последний кадр; prompt описывает движение, камеру, речь и звук. "
            + "Долго (" + I2vEtaText + "). Результат можно потом увеличить local_video_upscale. Возвращает job_id; "
            + "жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "image", "prompt" },
                ["properties"] = new JsonObject
                {
                    ["image"] = Str(ImageRefDescription),
                    ["last_frame"] = Str("Последний кадр (необязательно): " + ImageRefDescription),
                    ["prompt"] = Str("Что происходит в кадре: движение, камера, реплики (в кавычках), звук"),
                    ["duration_seconds"] = VideoDuration(),
                    ["size"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "full", "half" },
                        ["description"] = "full — 1344×768 (по умолчанию), half — 864×480 (быстрее); "
                            + "у портретной картинки стороны меняются местами",
                    },
                    ["fast"] = Fast(),
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_reference_to_video",
            ExplicitOnly + "Сгенерировать видео со звуком по референсам НАШЕЙ моделью MiniMax H3 на своей GPU: 1–9 "
            + "картинок (персонажи, предметы, стиль), до 3 видео (2–15 с) и до 3 звуков из проекта. В prompt ссылайся "
            + "на них как <Picture 1>, <Video 1>, <Audio 1>. identity=max — тяжёлая задача (одна за раз). Возвращает "
            + "job_id; жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "prompt", "ref_images" },
                ["properties"] = new JsonObject
                {
                    ["prompt"] = Str("Сцена со ссылками на референсы: <Picture 1>, <Video 1>, <Audio 1>"),
                    ["ref_images"] = RefList(1, ComfyWorkflows.MaxRefImages, ImageRefDescription,
                        "Картинки-референсы, по порядку <Picture 1>…"),
                    ["ref_videos"] = RefList(0, ComfyWorkflows.MaxRefVideos,
                        "Путь mp4 в проекте (2–15 с, 24 fps) или job_id готового видео",
                        "Видео-референсы (необязательно), их звук тоже идёт референсом"),
                    ["ref_audios"] = RefList(0, ComfyWorkflows.MaxRefAudios,
                        "Путь WAV/MP3/FLAC/OGG в проекте", "Звук-референсы (необязательно): голос, музыка"),
                    ["duration_seconds"] = VideoDuration(),
                    ["size"] = VideoSizeSchema(),
                    ["orientation"] = Orientation(),
                    ["identity"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "match", "max" },
                        ["description"] = "match — референсы в размере кадра (по умолчанию); max — 2048 px по короткой "
                            + "стороне: точнее сходство, но в разы медленнее и считается тяжёлой задачей",
                    },
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_video_upscale",
            ExplicitOnly + "Увеличить видео прошлой задачи local_text_to_video или local_image_to_video (размер full) до 1440p или 2K "
            + "НАШИМ латентным апскейлером на своей GPU. Работает только по job_id такой задачи, произвольный mp4 "
            + "не принимает. Тяжёлая задача: одна за раз. Возвращает job_id; жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "job_id" },
                ["properties"] = new JsonObject
                {
                    ["job_id"] = Str("job_id завершённой задачи local_text_to_video или local_image_to_video в этом проекте"),
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "1440p", "2k" },
                        ["description"] = "1440p — 2528×1440 (по умолчанию), 2k — 2688×1536; у портретного видео "
                            + "стороны меняются местами",
                    },
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_video_inpaint",
            ExplicitOnly + "Перерисовать область видео по маске НАШЕЙ моделью MiniMax H3 (Fun ControlNet) на своей GPU: белое на маске "
            + "перегенерируется по prompt, вне маски кадры и звук берутся из исходника. Видео — mp4 1344×768 или 864×480 "
            + "(или портретное), до 10 с; маска — PNG того же размера. Тяжёлая задача: одна за раз. Возвращает job_id.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "video", "mask", "prompt" },
                ["properties"] = new JsonObject
                {
                    ["video"] = Str("Путь mp4 в проекте или job_id готового видео этого проекта"),
                    ["mask"] = Str("PNG-маска того же размера, что видео: белое — перерисовать. " + ImageRefDescription),
                    ["prompt"] = Str("Вся сцена целиком, включая то, что должно появиться в области маски, и звук"),
                    ["seed"] = Seed(),
                },
            });

        yield return Tool("local_job_status",
            "Статус задачи локальной генерации по job_id. Готово — поле images или videos с url и path файла в проекте.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "job_id" },
                ["properties"] = new JsonObject { ["job_id"] = Str("ID задачи из ответа local_*") },
            });

        yield return Tool("local_jobs_wait",
            "Подождать задачи локальной генерации (до 15 с за вызов). all_done=false — вызови ещё раз. Готовые задачи "
            + "несут images/videos: url показывается в чате сам, path — путь файла в проекте.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "job_ids" },
                ["properties"] = new JsonObject
                {
                    ["job_ids"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 1,
                        ["maxItems"] = LocalMediaService.MaxWaitJobs,
                        ["items"] = Str("ID задачи"),
                    },
                    ["timeout_seconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["maximum"] = LocalMediaService.MaxWaitSeconds,
                        ["description"] = "Сколько ждать, с (по умолчанию 15)",
                    },
                },
            });

        yield return Tool("local_models",
            "Что умеют наши локальные модели: операции, размеры, ориентировочное время и длина очереди локальной GPU.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
    }

    // Замер d653be60, зачётные прогоны на одной RTX 3090
    private const string I2vEtaText =
        "1344×768: 5 с ≈ 5,5 мин (fast ≈ 4 мин), 10 с ≈ 15,5 мин (fast ≈ 10 мин); 864×480, 5 с ≈ 1,5 мин";

    private const string T2vEtaText = "1344×768, 5 с ≈ 5 мин; другие длины и размеры не замерены";

    private const string EtaNote = "Время — зачётный прогон на одной RTX 3090 с уже загруженными моделями; первый "
        + "прогон после простоя (загрузка моделей) идёт примерно столько же. Плюс ожидание очереди. "
        + "null — не замерено, время не обещаем.";

    private static JsonObject VideoDuration() => new()
    {
        ["type"] = "integer",
        ["minimum"] = 1,
        ["maximum"] = 10,
        ["description"] = "Длительность, с (по умолчанию 5, максимум 10)",
    };

    private static JsonObject VideoSizeSchema() => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray { "full", "half" },
        ["description"] = "full — 1344×768 (по умолчанию), half — 864×480 (быстрее)",
    };

    private static JsonObject Orientation() => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray { "landscape", "portrait" },
        ["description"] = "landscape — альбомное (по умолчанию), portrait — стороны меняются местами",
    };

    private static JsonObject Fast() => new()
    {
        ["type"] = "boolean",
        ["description"] = "Ускоренный режим (sparse attention, ≈на 30 % быстрее); не задан — настройка сервера",
    };

    private static JsonObject RefList(int min, int max, string item, string description) => new()
    {
        ["type"] = "array",
        ["minItems"] = min,
        ["maxItems"] = max,
        ["items"] = Str(item),
        ["description"] = description,
    };

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Seed() => new()
    {
        ["type"] = "integer",
        ["minimum"] = 0,
        ["description"] = "Seed для повторяемости (необязательно; без него — случайный, вернётся в ответе)",
    };

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);
}
