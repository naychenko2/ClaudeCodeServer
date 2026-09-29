using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ImageEditor.Mcp;

/// <summary>
/// MCP-сервер редактора картинок для агента любого чата владельца — проектного и личного вне
/// проекта (ADR-019 §4, «Изменение 29.09»). Тулсет живёт в модуле по прецеденту NotesToolset;
/// ехать ли серверу в ход, решает Main (SessionManager.BuildImageEditorContext). Маршрут —
/// <c>POST /mcp/image-editor/{sessionId}</c>: хвост несёт чат, по нему тулсет находит владельца,
/// область (проект или личную) и нити картинок чата. Схемы одинаковые в обоих видах чатов:
/// проектные аргументы (file, folder, references, character) у личной области отказывают на
/// вызове, до обращения к диску.
///
/// Агент видит нити и сам берёт картинку в работу или заводит черновик (решение Андрея 1) — это
/// всегда видно в ленте тихой строкой «Claude взял в работу: …». Каждый вариант запуска сразу
/// версия нити (изменение 27.09); агент выбирает, от какой версии править (versionId в
/// image_generate и image_focus). Сохранить в проект может только человек: такого инструмента у
/// агента нет. Запуск идёт строго в нить по threadId, а не «по текущему фокусу»: человек может
/// сменить картинку посреди хода.
///
/// Запуск — ровно тем путём, что у человека: котировка исполнителя и вход через
/// ImageEditLaunchAssembler (проверка путей, нити и лимитов одна). Трата ложится на владельца чата
/// (ownerId из сервисного JWT и сессии), инициатор — агент, SessionId — этот чат.
///
/// Сторожа дешёвого запуска — в CallAsync, не в составе: делегированный и реакционный ход —
/// отказ fail-closed (IDelegatedTurnGate), не больше MaxLaunchesPerTurn запусков за ход (счётчик на
/// сессию, сброс по TurnCompleted этой сессии). Задача живёт отдельно от хода: «Стоп» её не
/// отменяет, отмена — только image_cancel или кнопкой.
///
/// ИНВАРИАНТ состава: tools/list зависит от сессии, флага владельца и настройки инстанса
/// ImageEditor:AgentLaunch — не от хода, фокуса и нитей (McpToolsetStabilityTests). Без
/// выбранной картинки инструмент отвечает отказом, а не исчезает.
/// </summary>
public sealed partial class ImageEditorToolset : IMcpParameterizedToolset
{
    public const int MaxLaunchesPerTurn = 2;
    public const string AgentLaunchKey = "ImageEditor:AgentLaunch";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IMcpSessionAccessor _sessions;
    private readonly IFeatureFlagGate _flags;
    private readonly IProjectManager _projects;
    private readonly IEnumerable<IImageEditor> _editors;
    private readonly ImageThreadService _threads;
    private readonly ImageEditLaunchAssembler _launcher;
    private readonly IDelegatedTurnGate? _turnGate;
    private readonly IImageEditJobs? _jobs;
    private readonly IImagePlaceSettings? _placeSettings;
    private readonly ImageEditSteps? _steps;
    private readonly ImageProjectPrefsService? _prefs;
    private readonly bool _agentLaunch;

    // Запуски агентом в текущем ходу: sessionId → число. Сброс — TurnCompleted этой сессии
    private readonly Dictionary<string, int> _launches = new(StringComparer.Ordinal);
    private readonly Lock _launchGate = new();

    public ImageEditorToolset(
        IMcpSessionAccessor sessions,
        IFeatureFlagGate flags,
        IProjectManager projects,
        IEnumerable<IImageEditor> editors,
        ImageThreadService threads,
        ImageEditLaunchAssembler launcher,
        IDelegatedTurnGate? turnGate = null,
        IImageEditJobs? jobs = null,
        IImagePlaceSettings? placeSettings = null,
        ImageEditSteps? steps = null,
        ITurnEventBus? events = null,
        IConfiguration? config = null,
        ImageProjectPrefsService? prefs = null)
    {
        _sessions = sessions;
        _flags = flags;
        _projects = projects;
        _editors = editors;
        _threads = threads;
        _launcher = launcher;
        _turnGate = turnGate;
        _jobs = jobs;
        _placeSettings = placeSettings;
        _steps = steps;
        _prefs = prefs;
        _agentLaunch = config?.GetValue(AgentLaunchKey, true) ?? true;
        events?.OnNotification<TurnCompleted>(OnTurnCompleted, "ImageEditorToolset.ResetLaunches");
    }

    public string Name => ServerName;
    public string Version => "2.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? ToolsOfInstance : [];

    // Состав инстанса: без запуска агентом остаются чтение, выбор картинки и предложение промпта —
    // всё, что не тратит денег
    private IReadOnlyList<McpToolSchema> ToolsOfInstance => _agentLaunch
        ? Schemas
        : [.. Schemas.Where(t => t.Name is ToolState or ToolFocus or ToolNew or ToolSuggestPrompt)];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var scope, out var error))
            return Deny(error);
        if (ToolsOfInstance.All(t => t.Name != tool))
            return Deny($"Инструмент {tool} недоступен на этом сервере.");

        return tool switch
        {
            ToolState => Json(DescribeState(context.OwnerId, session, scope)),
            ToolFocus => await FocusAsync(arguments, context.OwnerId, session, scope, ct),
            ToolNew => await NewAsync(arguments, context.OwnerId, session, scope, ct),
            ToolSuggestPrompt => SuggestPrompt(arguments),
            ToolGenerate => await GenerateAsync(arguments, context.OwnerId, session, scope, ct),
            ToolCancel => await CancelAsync(arguments, context.OwnerId, session, scope, ct),
            _ => Deny($"Неизвестный инструмент: {tool}"),
        };
    }

    // ── image_focus и image_new ────────────────────────────────────────────────

    private async Task<McpToolCallResult> FocusAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, CancellationToken ct)
    {
        var threadId = Str(args, "threadId");
        var file = Str(args, "file");
        var versionId = Str(args, "versionId");
        if (threadId is not null && file is not null)
            return Deny("Укажи что-то одно: threadId картинки этого чата или file — путь картинки проекта.");
        if (versionId is not null && threadId is null)
            return Deny("versionId — версия картинки threadId: укажи и threadId.");
        if (versionId is not null
            && _threads.Get(ownerId, session.Id).Threads.FirstOrDefault(t => t.Id == threadId) is { } target
            && target.Version(versionId) is null)
            return Deny($"У картинки {threadId} нет версии {versionId}. Список версий — image_state.");

        ImageThreadWrite written;
        if (file is not null)
        {
            // У личной области файлов проекта нет — отказ до обращения к диску
            if (scope.Project is not { } project)
                return Deny(NoProjectFiles);
            if (ProjectImagePath(project, file) is not { } path)
                return Deny($"Картинка не найдена в проекте: {file}");
            written = await _threads.AgentOpenAsync(ownerId, scope.Key, session.Id, path, null, ct);
        }
        else
        {
            written = await _threads.AgentFocusAsync(ownerId, scope.Key, session.Id, threadId, ct);
            if (written.Status == ImageThreadWriteStatus.Ok && versionId is not null)
                written = await _threads.AgentContinueAsync(ownerId, scope.Key, session.Id, threadId!, versionId);
        }

        return written.Status switch
        {
            ImageThreadWriteStatus.Ok => Json(Focused(written.State, scope, file is null ? null : HumanChoice(ownerId, scope, written.State))),
            ImageThreadWriteStatus.ThreadNotFound => Deny($"Картинки {threadId} нет в этом чате. Список — image_state."),
            _ => Deny("Человек как раз меняет выбор картинки — повтори позже."),
        };
    }

    private async Task<McpToolCallResult> NewAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, CancellationToken ct)
    {
        var folder = ImageThreadPathTracker.Normalize(Str(args, "folder") ?? "");
        if (folder.Length > 0)
        {
            // Папка — место сохранения в проект; у личной области проекта нет, черновик человек скачает
            if (scope.Project is not { } project)
                return Deny("В чате вне проекта папок нет: вызови image_new без folder — человек скачает картинку сам.");
            if (ProjectLinkGuard.ResolveInside(project.RootPath, folder) is not { } full || !Directory.Exists(full))
                return Deny($"Папка не найдена в проекте: {folder}");
        }

        var written = await _threads.AgentOpenAsync(ownerId, scope.Key, session.Id, null, folder, ct);
        return written.Status == ImageThreadWriteStatus.Ok
            ? Json(Focused(written.State, scope, HumanChoice(ownerId, scope, written.State)))
            : Deny("Человек как раз меняет выбор картинки — повтори позже.");
    }

    // humanChoice — настройки, которые картинка унаследовала из полосы «Картинки», и правило
    private static object Focused(ImageThreadsState state, ImageEditScope scope, object? humanChoice = null) => new
    {
        focus = state.Focus,
        thread = state.Threads.FirstOrDefault(t => t.Id == state.Focus) is { } t ? DescribeThread(t, scope) : null,
        humanChoice,
        note = "Человек видит в ленте, какую картинку ты взял в работу, и может снять выбор.",
    };

    // Выбор человека для картинки в работе (без фокуса — для области): что возьмёт image_generate
    // без provider/model/count/character
    private object? HumanChoice(string ownerId, ImageEditScope scope, ImageThreadsState state)
    {
        if (_prefs is null) return null;
        var prefs = _prefs.Get(ownerId, scope);
        var settings = state.Threads.FirstOrDefault(t => t.Id == state.Focus)?.Settings ?? prefs.ToThreadSettings();
        return new
        {
            text = Chats.ImageEditorStateContributor.ChoiceText(settings, prefs.CharacterSlug),
            rule = Chats.ImageEditorStateContributor.ChoiceRule,
        };
    }

    private const string NoProjectFiles =
        "В чате вне проекта нет файлов проекта: заведи новую картинку (image_new) или возьми картинку этого чата по threadId.";

    // Путь картинки проекта в форме нитей: от корня через «/», строго внутри и без ссылки наружу
    private static string? ProjectImagePath(Project project, string file)
    {
        var rel = ImageThreadPathTracker.Normalize(file);
        if (rel.Length == 0 || ProjectLinkGuard.ResolveInside(project.RootPath, rel) is not { } full || !File.Exists(full))
            return null;
        return ImageEditLaunchAssembler.ContentTypeByExtension(full).StartsWith("image/", StringComparison.Ordinal)
            ? Path.GetRelativePath(project.RootPath, full).Replace('\\', '/')
            : null;
    }

    // ── image_generate ─────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> GenerateAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, CancellationToken ct)
    {
        // threadId обязателен (решение Андрея 1): человек мог сменить картинку посреди хода, и
        // запуск «по текущему фокусу» ушёл бы не туда. Отказ — до гейта и лимита хода
        if (Str(args, "threadId") is not { } threadId)
            return Deny("Не указан threadId: запуск идёт только в конкретную картинку чата. "
                + "Возьми id из image_state (или возьми картинку в работу через image_focus / image_new).");

        // Тратить деньги может только ход, который видит человек
        var denied = _turnGate is null
            ? "Запуск генерации недоступен: сервер не проверил ход — отказ по построению."
            : _turnGate.Deny(ownerId, session.Id, "Запуск генерации");
        if (denied is not null) return Deny(denied);
        if (_jobs is null) return Deny("Редактор картинок недоступен на этом сервере.");

        if (_threads.Get(ownerId, session.Id).Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
            return Deny($"Картинки {threadId} нет в этом чате. Список — image_state.");
        var versionId = Str(args, "versionId");
        var version = versionId is null ? thread.CurrentVersion : thread.Version(versionId);
        if (version is null)
            return Deny($"У картинки {threadId} нет версии {versionId}. Список версий — image_state.");

        if (!TryReserveLaunch(session.Id))
            return Deny($"За один ход можно запустить не больше {MaxLaunchesPerTurn} генераций. "
                + "Покажи человеку, что уже получилось, и дождись его ответа.");
        var launched = false;
        try
        {
            var result = await LaunchAsync(args, ownerId, session, scope, thread, version, ct);
            launched = !result.IsError;
            return result;
        }
        finally
        {
            if (!launched) ReleaseLaunch(session.Id);
        }
    }

    private async Task<McpToolCallResult> LaunchAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, ImageThread thread, ImageThreadVersion version, CancellationToken ct)
    {
        // Что не передано — выбор человека: настройки нити, иначе полосы «Картинки» области, а
        // поставщик по умолчанию — как у каталога. Персонаж — подключённый в полосе проекта (у
        // личной области его нет)
        var prefs = _prefs?.Get(ownerId, scope);
        var settings = thread.Settings ?? prefs?.ToThreadSettings();
        var provider = Str(args, "provider") ?? settings?.Provider ?? DefaultProvider();
        if (provider is null)
            return Deny("Поставщик рисования не настроен. Обратитесь к администратору.");
        var mode = Enum<EditMode>(args, "mode") ?? EditMode.Auto;
        var prompt = Str(args, "prompt") ?? "";
        var matchSourceSize = Bool(args, "matchSourceSize") ?? settings?.MatchSourceSize ?? true;
        var references = ReferencesArg(args);
        // Проектные аргументы в личном чате — отказ до котировки (сборщик отказал бы тоже, но позже)
        if (scope.Project is null && (references.Count > 0 || Str(args, "character") is not null))
            return Deny("В чате вне проекта нет файлов проекта и персонажей: запусти без references и character.");

        // Исходник — картинка версии-основы (её шаг), иначе файл нити с диска проекта. У черновика
        // «Новая картинка» без шага исходника нет вовсе: рисуем новую по тексту
        ImageBytes? source = null;
        string? baseStepId = null;
        if (thread.ImageStepOf(version) is { Length: > 0 } stepId && _steps?.Open(ownerId, scope.Key, stepId) is { } step)
        {
            source = new ImageBytes(step.Image.Bytes, step.Image.ContentType);
            baseStepId = stepId;
        }
        else if (thread.File is { Length: > 0 } file)
        {
            // Нить по файлу бывает только в проекте; у личной области — отказ до диска
            if (scope.Project is not { } project) return Deny(NoProjectFiles);
            // Тот же путь чтения, что у образцов человека: проверки пути и лимита одни
            var read = await ImageEditLaunchAssembler.ReadProjectImageAsync(project.RootPath, file, "Файл картинки", ct);
            if (read.Value is not { } image) return Deny(read.Error!);
            source = image;
        }
        var op = Enum<ImageEditOp>(args, "op") ?? (source is null ? ImageEditOp.Generate : ImageEditOp.Edit);
        if (source is null && op != ImageEditOp.Generate)
            return Deny("Картинки ещё нет: это новая картинка. Сначала нарисуй её — op generate или без op.");
        if (prompt.Length == 0 && op is ImageEditOp.Generate or ImageEditOp.Edit or ImageEditOp.Inpaint)
            return Deny("Пустой промпт: опиши, что нарисовать или поправить.");
        var size = source is null ? null : ImageDimensions.Read(source.Bytes);

        // Операции со своей моделью — как у человека: из выбора в полосе не наследуются ни
        // модель, ни число вариантов, ни персонаж. Явные аргументы агента остаются как есть
        var own = ImageEditCatalog.OwnModelOps.Contains(op);
        var model = Str(args, "model") ?? (own ? null : settings?.Model) ?? ImageEditCatalog.AutoModelId;
        var count = Int(args, "count") ?? (!own && settings is { Count: > 0 } s ? s.Count : 1);
        var character = Str(args, "character") ?? (own ? null : prefs?.CharacterSlug);

        var quoteRequest = new ImageEditQuoteRequest(provider, model, mode, op, count,
            HasMask: false,
            References: references.Count,
            HasCharacter: character is not null,
            Width: size?.Width,
            Height: size?.Height,
            Removal: EditIntent.IsRemoval(prompt));
        var quote = await _jobs!.QuoteAsync(ownerId, scope.Key, quoteRequest, ct);
        if (quote.Value is not { } q)
            return await FailAsync(quote.ErrorCode, quote.Error, ownerId, scope, quoteRequest, ct);

        var request = new ImageEditLaunchRequest(
            q.QuoteId, prompt, MarksJson: null, thread.File, source, Mask: null, Annotated: null,
            Uploaded: [],
            ReferencePaths: references,
            character,
            MatchSourceSize: matchSourceSize,
            BaseStepId: baseStepId,
            Initiator: ImageEditInitiator.Agent,
            ThreadSessionId: session.Id,
            ThreadId: thread.Id,
            VersionId: version.Id);
        var started = await _launcher.LaunchAsync(ownerId, scope, request, ct);
        if (started.Value is not { } created)
            return await FailAsync(started.ErrorCode, started.Error, ownerId, scope, quoteRequest, ct);

        return Json(new
        {
            jobId = created.JobId,
            threadId = thread.Id,
            baseVersion = new { versionId = version.Id, label = ImageThread.Label(version) },
            quote = new { q.Provider, q.Model, q.Estimate, q.ExpectedSeconds },
            note = "Генерация идёт отдельно от хода: остановка разговора её не отменяет, отмена — image_cancel. "
                + "Каждый вариант станет новой версией картинки внизу ленты; "
                + (scope.IsPersonal ? "скачать её может только человек." : "сохранить в проект может только человек."),
        });
    }

    // Отказ поставщика: в результате — котировка соседа, чтобы человек повторил одним кликом.
    // Сам тулсет второй раз не запускает — выбор поставщика не подменяется
    private async Task<McpToolCallResult> FailAsync(string? code, string? error, string ownerId, ImageEditScope scope,
        ImageEditQuoteRequest failed, CancellationToken ct)
    {
        ImageEditQuoteDto? retry = null;
        if (code == ImageEditErrorCodes.ProviderUnavailable)
        {
            foreach (var other in ImageEditCatalog.Available(_editors)
                         .Where(e => !string.Equals(e.Key, failed.Provider, StringComparison.OrdinalIgnoreCase)))
            {
                var alt = await _jobs!.QuoteAsync(ownerId, scope.Key,
                    failed with { Provider = other.Key, Model = ImageEditCatalog.AutoModelId }, ct);
                if (alt.Value is { } value) { retry = value; break; }
            }
        }
        return new McpToolCallResult(JsonSerializer.Serialize(new
        {
            error = error ?? "Запуск не выполнен",
            code = code ?? ImageEditErrorCodes.InvalidRequest,
            retryQuote = retry,
        }, JsonOpts), IsError: true);
    }

    private string? DefaultProvider()
    {
        var place = ImagePlaceKeys.ImageEditor;
        var admin = _placeSettings?.ProviderFor(place);
        var model = admin is null ? null : _placeSettings?.ModelFor(place, admin);
        return ImageEditCatalog.Build(_editors, admin, model).Default.Provider;
    }

    // ── image_cancel ───────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> CancelAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, CancellationToken ct)
    {
        var jobId = Str(args, "jobId") ?? "";
        // Задача другого чата (или чужая) неотличима от несуществующей
        var job = _jobs?.Get(ownerId, scope.Key, jobId);
        if (job is null || job.ChatSessionId != session.Id)
            return Deny("Задача не найдена.");
        var cancelled = await _jobs!.CancelAsync(ownerId, scope.Key, jobId, ct);
        return cancelled is null
            ? Deny("Задача не найдена.")
            : Json(new { cancelled.JobId, cancelled.Status, cancelled.Charged });
    }

    // ── image_suggest_prompt и image_state ─────────────────────────────────────

    private static McpToolCallResult SuggestPrompt(JsonObject args)
    {
        var prompt = Str(args, "prompt");
        if (prompt is null) return Deny("Пустой промпт: предложить нечего.");
        return Json(new
        {
            prompt,
            count = Int(args, "count"),
            model = Str(args, "model"),
            note = "Промпт показан человеку карточкой; ничего не запущено и не изменено.",
        });
    }

    private object DescribeState(string ownerId, Session session, ImageEditScope scope)
    {
        var state = _threads.Get(ownerId, session.Id);
        var place = ImagePlaceKeys.ImageEditor;
        var admin = _placeSettings?.ProviderFor(place);
        var catalog = ImageEditCatalog.Build(_editors, admin, admin is null ? null : _placeSettings?.ModelFor(place, admin));

        return new
        {
            focus = state.Focus,
            threads = state.Threads.Select(t => new
            {
                thread = DescribeThread(t, scope),
                size = t.File is { Length: > 0 } file && scope.Project is { } project ? SizeOf(project, file) : null,
                pendingJob = t.PendingJobId is { } jobId && _jobs?.Get(ownerId, scope.Key, jobId) is { } j
                    ? new { j.JobId, j.Status, j.Provider, j.Model, variants = j.Variants.Count, j.Cost, j.Error, j.Initiator }
                    : null,
                running = t.Launches.Where(l => l.Status == ImageThreadLaunchStatus.Running)
                    .Select(l => _jobs?.Get(ownerId, scope.Key, l.JobId) is { } j
                        ? new { j.JobId, j.Status, j.Provider, j.Model, j.Count, l.Prompt, from = l.BaseVersionId }
                        : new { l.JobId, Status = ImageEditJobStatus.Queued, Provider = "", Model = "", Count = 0, l.Prompt, from = l.BaseVersionId }),
            }),
            defaultProvider = catalog.Default.Provider,
            defaultModel = catalog.Default.Model,
            providers = catalog.Providers,
            agentLaunch = _agentLaunch,
            humanChoice = HumanChoice(ownerId, scope, state),
            note = (state.Threads.Count, scope.IsPersonal) switch
            {
                (0, true) => "В этом чате ещё нет картинок. Заведи новую (image_new): это чат вне проекта, файлов проекта тут нет.",
                (0, false) => "В этом чате ещё нет картинок. Возьми картинку проекта в работу (image_focus с file) или заведи новую (image_new).",
                (_, true) => "Каждый вариант запуска — версия картинки. Править другую версию — versionId в image_generate. "
                    + "Скачать картинку может только человек.",
                _ => "Каждый вариант запуска — версия картинки. Править другую версию — versionId в image_generate. "
                    + "Сохранить в проект может только человек.",
            },
        };
    }

    private static object DescribeThread(ImageThread t, ImageEditScope scope)
    {
        var steps = t.CurrentStack?.Steps ?? [];
        var at = t.CurrentStepId is { } s ? steps.ToList().IndexOf(s) + 1 : 0;
        return new
        {
            threadId = t.Id,
            file = t.File,
            draft = t.File is { Length: > 0 } ? null : new
            {
                folder = t.DraftFolder ?? "",
                note = "Картинки ещё нет: это новая картинка, "
                    + Chats.ImageEditorStateContributor.DraftSaveText(t.DraftFolder, scope.IsPersonal)
                    + ". image_generate без op нарисует её по тексту.",
            },
            currentVersionId = t.CurrentVersionId,
            versions = t.Versions.Select(v => new
            {
                versionId = v.Id,
                label = ImageThread.Label(v),
                current = v.Id == t.CurrentVersionId,
                from = t.Version(v.BaseVersionId) is { } b ? ImageThread.Label(b) : null,
                jobId = v.JobId,
                variant = v.Variant,
                prompt = v.JobId is { } job ? t.Launches.FirstOrDefault(l => l.JobId == job)?.Prompt : null,
                editsWithoutAi = v.IsOrigin ? v.Steps.Count : Math.Max(0, v.Steps.Count - 1),
            }),
            // Стопка — формат нитей до 27.09
            oldStack = steps.Count == 0 ? null : at == 0 ? $"на холсте исходник, шагов {steps.Count}" : $"шаг {at} из {steps.Count}",
            settings = t.Settings,
            pendingJobId = t.PendingJobId,
        };
    }

    private static object? SizeOf(Project project, string file)
    {
        var full = ProjectLinkGuard.ResolveInside(project.RootPath, file);
        if (full is null || !File.Exists(full)) return null;
        using var stream = File.OpenRead(full);
        var head = new byte[Math.Min(64 * 1024, stream.Length)];
        stream.ReadExactly(head);
        return ImageDimensions.Read(head) is { } size ? new { width = size.Width, height = size.Height } : null;
    }

    // ── Счётчик запусков за ход ────────────────────────────────────────────────

    private bool TryReserveLaunch(string sessionId)
    {
        lock (_launchGate)
        {
            var used = _launches.GetValueOrDefault(sessionId);
            if (used >= MaxLaunchesPerTurn) return false;
            _launches[sessionId] = used + 1;
            return true;
        }
    }

    private void ReleaseLaunch(string sessionId)
    {
        lock (_launchGate)
        {
            if (_launches.TryGetValue(sessionId, out var used) && used > 0)
                _launches[sessionId] = used - 1;
        }
    }

    private Task OnTurnCompleted(TurnCompleted e)
    {
        lock (_launchGate) _launches.Remove(e.Turn.SessionId);
        return Task.CompletedTask;
    }

    // ── Маршрут: /mcp/image-editor/{sessionId} ─────────────────────────────────

    // Чат владельца токена при включённом флаге: у чата проекта — его проект (свой), у личного —
    // личная область. Сопоставление — только ImageEditScope.Of. Любой отказ одним текстом: чужой
    // чат неотличим от несуществующего, а состав у него пустой
    private bool TryResolve(McpToolCallContext context, out Session session, out ImageEditScope scope, out string error)
    {
        session = null!;
        scope = null!;
        error = "Чат не найден — инструменты редактора картинок недоступны.";
        if (!TryParseRoute(context.RouteTail, out var sessionId)) return false;
        if (_sessions.GetOwned(sessionId, context.OwnerId) is not { } owned) return false;
        if (!_flags.IsEnabled(context.OwnerId, FeatureFlagKeys.ImageEditor)) return false;
        if (owned.ProjectId is { } projectId)
        {
            if (_projects.GetById(projectId) is not { } found || found.OwnerId != context.OwnerId) return false;
            scope = ImageEditScope.Of(found);
        }
        else
            scope = ImageEditScope.Of(owned);
        session = owned;
        return true;
    }

    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Length is < 1 or > 128
            || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    // ── Аргументы и ответы ─────────────────────────────────────────────────────

    private static McpToolCallResult Json<T>(T value) => new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool? Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static T? Enum<T>(JsonObject args, string name) where T : struct, System.Enum =>
        Str(args, name) is { } s && System.Enum.TryParse<T>(s, ignoreCase: true, out var value) ? value : null;

    // Образцы агента — пути проекта с ролями; проверку пути делает общий сборщик входа
    private static IReadOnlyList<(string Path, ReferenceRole Role)> ReferencesArg(JsonObject args)
    {
        if (args["references"] is not JsonArray array) return [];
        var list = new List<(string, ReferenceRole)>();
        foreach (var item in array.OfType<JsonObject>())
        {
            if (Str(item, "path") is not { } path) continue;
            list.Add((path, Enum<ReferenceRole>(item, "role") ?? ReferenceRole.Object));
        }
        return list;
    }
}
