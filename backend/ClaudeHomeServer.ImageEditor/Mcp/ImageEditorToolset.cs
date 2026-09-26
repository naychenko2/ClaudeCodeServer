using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Chats;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.ImageEditor.Mcp;

/// <summary>
/// MCP-сервер редактора картинок для агента чата картинки (ADR-018 §2, §10.2). Тулсет живёт в
/// модуле по прецеденту NotesToolset; ехать ли серверу в ход, решает Main
/// (SessionManager.BuildImageEditorContext). Маршрут — <c>POST /mcp/image-editor/{sessionId}</c>:
/// хвост несёт чат картинки, по нему тулсет находит владельца, проект и файл.
///
/// Запуск — ровно тем путём, что у человека: котировка исполнителя и вход через
/// ImageEditLaunchAssembler (проверка путей и лимитов одна). Трата ложится на владельца чата
/// (ownerId из сервисного JWT и сессии), инициатор — агент, SessionId — этот чат.
///
/// Сторожа дешёвого запуска — в CallAsync, не в составе: делегированный и реакционный ход —
/// отказ fail-closed (IDelegatedTurnGate), не больше MaxLaunchesPerTurn запусков за ход (счётчик на
/// сессию, сброс по TurnCompleted этой сессии). Задача живёт отдельно от хода: «Стоп» её не
/// отменяет, отмена — только image_cancel или кнопкой. Сохранять в проект агент не может.
///
/// ИНВАРИАНТ состава: tools/list зависит от сессии, флага владельца и настройки инстанса
/// ImageEditor:AgentLaunch — не от хода (McpToolsetStabilityTests).
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
    private readonly ImageChatStateStore _states;
    private readonly ImageEditLaunchAssembler _launcher;
    private readonly IDelegatedTurnGate? _turnGate;
    private readonly IImageEditJobs? _jobs;
    private readonly IImagePlaceSettings? _placeSettings;
    private readonly ImageEditSteps? _steps;
    private readonly bool _agentLaunch;

    // Запуски агентом в текущем ходу: sessionId → число. Сброс — TurnCompleted этой сессии
    private readonly Dictionary<string, int> _launches = new(StringComparer.Ordinal);
    private readonly Lock _launchGate = new();

    public ImageEditorToolset(
        IMcpSessionAccessor sessions,
        IFeatureFlagGate flags,
        IProjectManager projects,
        IEnumerable<IImageEditor> editors,
        ImageChatStateStore states,
        ImageEditLaunchAssembler launcher,
        IDelegatedTurnGate? turnGate = null,
        IImageEditJobs? jobs = null,
        IImagePlaceSettings? placeSettings = null,
        ImageEditSteps? steps = null,
        ITurnEventBus? events = null,
        IConfiguration? config = null)
    {
        _sessions = sessions;
        _flags = flags;
        _projects = projects;
        _editors = editors;
        _states = states;
        _launcher = launcher;
        _turnGate = turnGate;
        _jobs = jobs;
        _placeSettings = placeSettings;
        _steps = steps;
        _agentLaunch = config?.GetValue(AgentLaunchKey, true) ?? true;
        events?.OnNotification<TurnCompleted>(OnTurnCompleted, "ImageEditorToolset.ResetLaunches");
    }

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? ToolsOfInstance : [];

    // Состав инстанса: без запуска агентом остаются только чтение состояния и предложение промпта
    private IReadOnlyList<McpToolSchema> ToolsOfInstance => _agentLaunch
        ? Schemas
        : [.. Schemas.Where(t => t.Name is ToolState or ToolSuggestPrompt)];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var project, out var error))
            return Deny(error);
        if (ToolsOfInstance.All(t => t.Name != tool))
            return Deny($"Инструмент {tool} недоступен на этом сервере.");

        return tool switch
        {
            ToolState => Json(DescribeState(context.OwnerId, session, project)),
            ToolSuggestPrompt => SuggestPrompt(arguments),
            ToolGenerate => await GenerateAsync(arguments, context.OwnerId, session, project, ct),
            ToolCancel => await CancelAsync(arguments, context.OwnerId, session, project, ct),
            _ => Deny($"Неизвестный инструмент: {tool}"),
        };
    }

    // ── image_generate ─────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> GenerateAsync(JsonObject args, string ownerId, Session session,
        Project project, CancellationToken ct)
    {
        // Тратить деньги может только ход, который видит человек
        var denied = _turnGate is null
            ? "Запуск генерации недоступен: сервер не проверил ход — отказ по построению."
            : _turnGate.Deny(ownerId, session.Id, "Запуск генерации");
        if (denied is not null) return Deny(denied);
        if (_jobs is null) return Deny("Редактор картинок недоступен на этом сервере.");

        if (!TryReserveLaunch(session.Id))
            return Deny($"За один ход можно запустить не больше {MaxLaunchesPerTurn} генераций. "
                + "Покажи человеку, что уже получилось, и дождись его ответа.");
        var launched = false;
        try
        {
            var result = await LaunchAsync(args, ownerId, session, project, ct);
            launched = !result.IsError;
            return result;
        }
        finally
        {
            if (!launched) ReleaseLaunch(session.Id);
        }
    }

    private async Task<McpToolCallResult> LaunchAsync(JsonObject args, string ownerId, Session session,
        Project project, CancellationToken ct)
    {
        var current = _states.Get(ownerId, session.Id);
        var chatPath = session.ImageChat!.CurrentPath;

        // Что не передано — из состояния редактора, а поставщик по умолчанию — как у каталога
        var provider = Str(args, "provider") ?? current.Provider ?? DefaultProvider();
        if (provider is null)
            return Deny("Поставщик рисования не настроен. Обратитесь к администратору.");
        var model = Str(args, "model") ?? current.Model ?? ImageEditCatalog.AutoModelId;
        var mode = Enum<EditMode>(args, "mode") ?? current.Mode;
        var count = Int(args, "count") ?? current.Count;
        var prompt = Str(args, "prompt") ?? current.Prompt;
        var matchSourceSize = Bool(args, "matchSourceSize") ?? current.MatchSourceSize;
        var references = ReferencesArg(args) ?? current.References;
        var marksJson = current.Marks?.GetRawText();

        // Исходник — текущий шаг истории редактора, иначе файл чата с диска проекта
        ImageBytes? source = null;
        string? baseStepId = null;
        if (current.CurrentStepId is { Length: > 0 } stepId && _steps?.Open(ownerId, project.Id, stepId) is { } step)
        {
            source = new ImageBytes(step.Image.Bytes, step.Image.ContentType);
            baseStepId = stepId;
        }
        else
        {
            var full = ProjectLinkGuard.ResolveInside(project.RootPath, chatPath);
            var info = full is null ? null : new FileInfo(full);
            if (info is not { Exists: true })
                return Deny($"Файл чата {chatPath} не найден или идёт через символическую ссылку.");
            if (info.Length > ImageEditCatalog.DefaultLimits.MaxFileMb * 1024L * 1024L)
                return Deny($"Файл больше {ImageEditCatalog.DefaultLimits.MaxFileMb} МБ.");
            source = new ImageBytes(await File.ReadAllBytesAsync(full!, ct),
                ImageEditLaunchAssembler.ContentTypeByExtension(full!));
        }
        var mask = _states.ReadMask(ownerId, session.Id) is { Length: > 0 } maskBytes
            ? new ImageBytes(maskBytes, "image/png")
            : null;
        var op = Enum<ImageEditOp>(args, "op") ?? ImageEditOp.Edit;
        var size = ImageDimensions.Read(source.Bytes);

        var quoteRequest = new ImageEditQuoteRequest(provider, model, mode, op, count,
            HasMask: mask is not null,
            References: references.Count,
            HasCharacter: !string.IsNullOrWhiteSpace(current.CharacterSlug),
            Width: size?.Width,
            Height: size?.Height,
            HasAnnotations: !string.IsNullOrWhiteSpace(EditMarksPrompt.Describe(marksJson, MarksScope.WithoutBrush)),
            Removal: EditIntent.IsRemoval(prompt));
        var quote = await _jobs!.QuoteAsync(ownerId, project.Id, quoteRequest, ct);
        if (quote.Value is not { } q)
            return await FailAsync(quote.ErrorCode, quote.Error, ownerId, project, quoteRequest, ct);

        var request = new ImageEditLaunchRequest(
            q.QuoteId, prompt, marksJson, chatPath, source, mask, Annotated: null,
            Uploaded: [],
            ReferencePaths: [.. references.Select(r => (r.Path, r.Role))],
            current.CharacterSlug,
            MatchSourceSize: matchSourceSize,
            BaseStepId: baseStepId,
            ChatSessionId: session.Id,
            Initiator: ImageEditInitiator.Agent);
        var started = await _launcher.LaunchAsync(ownerId, project, request, ct);
        if (started.Value is not { } created)
            return await FailAsync(started.ErrorCode, started.Error, ownerId, project, quoteRequest, ct);

        // Состояние меняется только после старта: отвергнутый запуск не оставляет в редакторе
        // чужой модели или битого пути образца
        var changes = await ApplyStateAsync(ownerId, session.Id, project.Id, latest => latest with
        {
            Prompt = prompt,
            PromptAuthor = Str(args, "prompt") is not null ? ImageEditInitiator.Agent : latest.PromptAuthor,
            Provider = q.Provider,
            Model = model,
            Mode = mode,
            Count = count,
            References = references,
            MatchSourceSize = matchSourceSize,
        });

        return Json(new
        {
            jobId = created.JobId,
            quote = new { q.Provider, q.Model, q.Estimate, q.ExpectedSeconds },
            changes = changes.Select(c => new { c.Field, c.From, c.To }),
            note = "Генерация идёт отдельно от хода: остановка разговора её не отменяет, отмена — image_cancel. "
                + "Сохранить результат в проект может только человек.",
        });
    }

    // Отказ поставщика: в результате — котировка соседа, чтобы человек повторил одним кликом.
    // Сам тулсет второй раз не запускает — выбор поставщика не подменяется
    private async Task<McpToolCallResult> FailAsync(string? code, string? error, string ownerId, Project project,
        ImageEditQuoteRequest failed, CancellationToken ct)
    {
        ImageEditQuoteDto? retry = null;
        if (code == ImageEditErrorCodes.ProviderUnavailable)
        {
            foreach (var other in ImageEditCatalog.Available(_editors)
                         .Where(e => !string.Equals(e.Key, failed.Provider, StringComparison.OrdinalIgnoreCase)))
            {
                var alt = await _jobs!.QuoteAsync(ownerId, project.Id,
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

    // Запись изменений агента в состояние и событие редактору. Человек мог записать своё между
    // чтением и записью — перечитываем и накладываем правку заново
    private async Task<IReadOnlyList<ImageChatStateChange>> ApplyStateAsync(string ownerId, string sessionId,
        string projectId, Func<ImageChatState, ImageChatState> change)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = _states.Get(ownerId, sessionId);
            var written = _states.Write(ownerId, sessionId, change(current), mask: null);
            if (!written.Ok) continue;
            if (written.Changes.Count > 0)
                await _launcher.BroadcastStateAsync(ownerId, projectId, sessionId, written.State,
                    ImageEditInitiator.Agent, written.Changes);
            return written.Changes;
        }
        return [];
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
        Project project, CancellationToken ct)
    {
        var jobId = Str(args, "jobId") ?? "";
        // Задача другого чата (или чужая) неотличима от несуществующей
        var job = _jobs?.Get(ownerId, project.Id, jobId);
        if (job is null || job.ChatSessionId != session.Id)
            return Deny("Задача не найдена.");
        var cancelled = await _jobs!.CancelAsync(ownerId, project.Id, jobId, ct);
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

    private object DescribeState(string ownerId, Session session, Project project)
    {
        var state = _states.Get(ownerId, session.Id);
        var chatPath = session.ImageChat!.CurrentPath;
        var full = ProjectLinkGuard.ResolveInside(project.RootPath, chatPath);
        (int Width, int Height)? size = null;
        if (full is not null && File.Exists(full))
        {
            using var stream = File.OpenRead(full);
            var head = new byte[Math.Min(64 * 1024, stream.Length)];
            stream.ReadExactly(head);
            size = ImageDimensions.Read(head);
        }

        var place = ImagePlaceKeys.ImageEditor;
        var admin = _placeSettings?.ProviderFor(place);
        var catalog = ImageEditCatalog.Build(_editors, admin, admin is null ? null : _placeSettings?.ModelFor(place, admin));
        var jobIds = state.Events.Select(e => e.JobId).OfType<string>().Distinct().TakeLast(5);

        return new
        {
            file = new { path = chatPath, width = size?.Width, height = size?.Height },
            state.Prompt,
            state.PromptAuthor,
            provider = state.Provider ?? catalog.Default.Provider,
            model = state.Model ?? catalog.Default.Model,
            state.Mode,
            state.Count,
            state.References,
            character = state.CharacterSlug,
            marks = state.Marks is { } m ? EditMarksPrompt.Describe(m.GetRawText()) : "",
            state.MatchSourceSize,
            recentJobs = jobIds
                .Select(id => _jobs?.Get(ownerId, project.Id, id))
                .OfType<ImageEditJobDto>()
                .Select(j => new { j.JobId, j.Status, j.Provider, j.Model, variants = j.Variants.Count, j.Cost, j.Error, j.Initiator }),
            providers = catalog.Providers,
            agentLaunch = _agentLaunch,
        };
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

    // Чат картинки владельца токена в его проекте и при включённом флаге. Любой отказ одним
    // текстом: чужой чат неотличим от несуществующего, а состав у него пустой
    private bool TryResolve(McpToolCallContext context, out Session session, out Project project, out string error)
    {
        session = null!;
        project = null!;
        error = "Чат картинки не найден — инструменты редактора недоступны.";
        if (!TryParseRoute(context.RouteTail, out var sessionId)) return false;
        if (_sessions.GetOwned(sessionId, context.OwnerId) is not { ImageChat: not null, ProjectId: { } projectId } owned)
            return false;
        if (!_flags.IsEnabled(context.OwnerId, FeatureFlagKeys.ImageEditor)) return false;
        if (_projects.GetById(projectId) is not { } found || found.OwnerId != context.OwnerId) return false;
        session = owned;
        project = found;
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
    private static IReadOnlyList<ImageChatReference>? ReferencesArg(JsonObject args)
    {
        if (args["references"] is not JsonArray array) return null;
        var list = new List<ImageChatReference>();
        foreach (var item in array.OfType<JsonObject>())
        {
            if (Str(item, "path") is not { } path) continue;
            list.Add(new ImageChatReference(path, Enum<ReferenceRole>(item, "role") ?? ReferenceRole.Object));
        }
        return list;
    }
}
