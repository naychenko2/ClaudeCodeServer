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
    public string Version => "1.0.0";

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

            case "local_image_to_video":
                return await SubmitAsync(new LocalMediaRequest(context.OwnerId, project.Id, session.Id,
                    LocalMediaOps.ImageToVideo,
                    Prompt: StringArg(arguments, "prompt"),
                    Seed: LongArg(arguments, "seed"),
                    Images: [StringArg(arguments, "image") ?? ""],
                    VideoSize: StringArg(arguments, "size"),
                    DurationSeconds: IntArg(arguments, "duration_seconds")), ct);

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
            ["operations"] = new JsonArray
            {
                Operation("local_generate_image", "Qwen-Image 2.1", "картинка по тексту, 1–4 варианта", "≈45 с на картинку при 25 шагах"),
                Operation("local_edit_image", "Qwen-Image 2.1 (правка)", "правка по 1–16 референсам", "≈60 с"),
                Operation("local_face_detail", "FaceDetailer (YOLOv8 + Qwen-Image)", "доводка лиц на готовой картинке", "≈25 с"),
                Operation("local_image_to_video", "MiniMax H3 + turbo-LoRA 8 шагов",
                    $"видео со звуком от первого кадра, до {options.MaxVideoSeconds} с, 24 fps", "5 с ≈ 2 мин, 10 с ≈ 7 мин"),
            },
            ["aspects"] = new JsonArray([.. ComfyWorkflows.ImageSizes.Select(s =>
                (JsonNode)$"{s.Key} ({s.Value.Width}×{s.Value.Height})")]),
            ["video_sizes"] = new JsonArray([.. ComfyWorkflows.VideoSizes.Select(s =>
                (JsonNode)$"{s.Key} ({s.Value.Width}×{s.Value.Height}, портретный кадр — стороны меняются местами)")]),
            ["not_available"] = "видео по тексту без картинки, апскейл, инпейнт по маске",
        };

        static JsonObject Operation(string tool, string model, string what, string eta) => new()
        {
            ["tool"] = tool,
            ["model"] = model,
            ["what"] = what,
            ["eta"] = eta,
        };
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

    private static IEnumerable<McpToolSchema> Schemas()
    {
        var aspects = new JsonArray([.. ComfyWorkflows.ImageSizes.Keys.Select(k => (JsonNode)k)]);

        yield return Tool("local_generate_image",
            "Сгенерировать картинку по тексту НАШЕЙ моделью Qwen-Image 2.1 на своей GPU (бесплатно, асинхронно). "
            + "Вызывай ТОЛЬКО когда пользователь явно просит локально / нашими моделями / бесплатно; "
            + "иначе — облачные генераторы. Возвращает job_id; результат жди через local_jobs_wait.",
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
            "Отредактировать картинку НАШЕЙ моделью Qwen-Image 2.1 на своей GPU по 1–16 референсам: первая картинка — "
            + "основная (холст), остальные — дополнительные референсы (персонаж, предмет, стиль). Только по явной "
            + "просьбе сделать локально. Возвращает job_id; результат жди через local_jobs_wait.",
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
            "Довести лица на готовой картинке (FaceDetailer: находит лица и перерисовывает их детальнее) НАШЕЙ "
            + "моделью на своей GPU. Только по явной просьбе сделать локально. Возвращает job_id.",
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

        yield return Tool("local_image_to_video",
            "Оживить картинку в видео со звуком НАШЕЙ моделью MiniMax H3 на своей GPU: первый кадр — картинка, "
            + "prompt описывает движение, камеру, речь и звук. Видео по тексту без картинки нет. Долго (5 с ≈ 2 мин, "
            + "10 с ≈ 7 мин) — только по явной просьбе сделать локально. Возвращает job_id; жди через local_jobs_wait.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "image", "prompt" },
                ["properties"] = new JsonObject
                {
                    ["image"] = Str(ImageRefDescription),
                    ["prompt"] = Str("Что происходит в кадре: движение, камера, реплики (в кавычках), звук"),
                    ["duration_seconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = 10,
                        ["description"] = "Длительность, с (по умолчанию 5, максимум 10)",
                    },
                    ["size"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "full", "half" },
                        ["description"] = "full — 1344×768 (по умолчанию), half — 864×480 (быстрее); "
                            + "у портретной картинки стороны меняются местами",
                    },
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
