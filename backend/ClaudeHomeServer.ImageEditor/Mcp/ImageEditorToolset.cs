using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor.Prefs;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Media;
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
/// ИНВАРИАНТ состава: tools/list зависит от сессии и настройки инстанса
/// ImageEditor:AgentLaunch — не от хода, фокуса и нитей (McpToolsetStabilityTests). Без
/// выбранной картинки инструмент отвечает отказом, а не исчезает.
/// </summary>
public sealed partial class ImageEditorToolset : IMcpParameterizedToolset
{
    public const int MaxLaunchesPerTurn = 2;
    public const int MaxFindFacesPerTurn = 8;
    public const string AgentLaunchKey = "ImageEditor:AgentLaunch";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IMcpSessionAccessor _sessions;
    private readonly IProjectManager _projects;
    private readonly IEnumerable<IImageEditor> _editors;
    private readonly ImageThreadService _threads;
    private readonly ImageEditLaunchAssembler _launcher;
    private readonly IDelegatedTurnGate? _turnGate;
    private readonly IImageEditJobs? _jobs;
    private readonly IImagePlaceSettings? _placeSettings;
    private readonly ImageEditSteps? _steps;
    private readonly ImageProjectPrefsService? _prefs;
    private readonly ChatContext.ImageContextLaunch? _context;
    private readonly IFeatureFlagGate? _flags;
    private readonly Faces.FaceDetectionCache? _faces;
    private readonly ILocalImageMedia? _localMedia;
    private readonly bool _agentLaunch;

    // Запуски агентом в текущем ходу: sessionId → число. Сброс — TurnCompleted этой сессии
    private readonly Dictionary<string, int> _launches = new(StringComparer.Ordinal);
    private readonly Lock _launchGate = new();
    // Вызовы image_find_faces в текущем ходу: sessionId → число. Сброс — TurnCompleted этой сессии
    private readonly Dictionary<string, int> _findFaces = new(StringComparer.Ordinal);
    private readonly Lock _findFacesGate = new();

    public ImageEditorToolset(
        IMcpSessionAccessor sessions,
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
        ImageProjectPrefsService? prefs = null,
        ChatContext.ImageContextLaunch? context = null,
        IFeatureFlagGate? flags = null,
        Faces.FaceDetectionCache? faces = null,
        ILocalImageMedia? localMedia = null)
    {
        _sessions = sessions;
        _projects = projects;
        _editors = editors;
        _threads = threads;
        _launcher = launcher;
        _turnGate = turnGate;
        _jobs = jobs;
        _placeSettings = placeSettings;
        _steps = steps;
        _prefs = prefs;
        _context = context;
        _flags = flags;
        _faces = faces;
        _localMedia = localMedia;
        _agentLaunch = config?.GetValue(AgentLaunchKey, true) ?? true;
        events?.OnNotification<TurnCompleted>(OnTurnCompleted, "ImageEditorToolset.ResetLaunches");
    }

    public string Name => ServerName;
    public string Version => "2.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? ToolsOfInstance : [];

    // Состав инстанса: без запуска агентом остаются чтение, выбор картинки, поиск лиц и предложение промпта —
    // всё, что не тратит денег
    private IReadOnlyList<McpToolSchema> ToolsOfInstance => _agentLaunch
        ? Schemas
        : [.. Schemas.Where(t => t.Name is ToolState or ToolFocus or ToolNew or ToolFindFaces or ToolSuggestPrompt)];

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
            ToolFindFaces => await FindFacesAsync(arguments, context.OwnerId, session, scope, ct),
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
            ImageThreadWriteStatus.Ok => FocusedResult(ownerId, session.Id, scope, withChoice: file is not null),
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
            ? FocusedResult(ownerId, session.Id, scope, withChoice: true)
            : Deny("Человек как раз меняет выбор картинки — повтори позже.");
    }

    // Ответ строится по проекции из контекста: основной объект мог выбрать человек, а не запись нити
    private McpToolCallResult FocusedResult(string ownerId, string sessionId, ImageEditScope scope, bool withChoice)
    {
        var state = _threads.View(ownerId, sessionId);
        return Json(Focused(state, scope, withChoice ? HumanChoice(ownerId, scope, state) : null));
    }

    // humanChoice — настройки, которые картинка унаследовала из выбора человека в строке контекста, и правило
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
        var focused = state.Threads.FirstOrDefault(t => t.Id == state.Focus)?.Settings;
        // Выбор человека — «Чем» контекста хода, персонаж префов проекта не читается
        return new
        {
            text = Chats.ImageEditorStateContributor.ChoiceText(prefs, focused),
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

    // ── image_find_faces ───────────────────────────────────────────────────────

    // Только чтение: гейта хода и квоты MaxLaunchesPerTurn нет (детекция — не запуск: траты,
    // версии и записи в ленту не пишет). Флаг, личная область и локальные модели отказывают
    // на вызове — состав tools/list от них не зависит (инвариант McpToolsetStabilityTests)
    private async Task<McpToolCallResult> FindFacesAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, CancellationToken ct)
    {
        if (_flags?.IsEnabled(ownerId, FeatureFlagKeys.CharacterFaceSwap) != true)
            return Deny("Функция «Подставить персонажа» выключена");
        if (scope.Project is null)
            return Deny("Подстановка персонажа на лицо — только в чате проекта");
        // Локальные модели — опциональная зависимость: нет шва (подсистема images выключена) —
        // понятный отказ, не 500
        if (_localMedia is null)
            return Deny("Локальные модели не настроены: детекция лиц недоступна");

        if (Str(args, "threadId") is not { } threadId)
            return Deny("Не указан threadId: картинка этого чата (id из image_state).");
        if (_threads.Get(ownerId, session.Id).Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
            return Deny($"Картинки {threadId} нет в этом чате. Список — image_state.");
        var versionId = Str(args, "versionId");
        var version = versionId is null ? thread.CurrentVersion : thread.Version(versionId);
        if (version is null)
            return Deny($"У картинки {threadId} нет версии {versionId}. Список версий — image_state.");

        // Байты версии: шаг версии, иначе файл проекта (у исходника). Не прочитано — картинки ещё нет
        byte[]? bytes = null;
        if (thread.ImageStepOf(version) is { Length: > 0 } stepId && _steps?.Open(ownerId, scope.Key, stepId) is { } step)
            bytes = step.Image.Bytes;
        else if (thread.File is { Length: > 0 } file && scope.Project is { } project)
        {
            var read = await ImageEditLaunchAssembler.ReadProjectImageAsync(project.RootPath, file, "Версия", ct);
            if (read.Value is not null) bytes = read.Value.Bytes;
        }
        if (bytes is null)
            return Deny("У версии ещё нет изображения");

        // Лимит поиска лиц за ход: детекция ставится в голову очереди GPU (front:true),
        // агент в цикле может залить очередь; кэш смягчает повторы, но не разные версии
        if (!TryReserveFindFaces(session.Id))
            return Deny($"Лимит поиска лиц за ход исчерпан (не больше {MaxFindFacesPerTurn}). "
                + "Продолжайте с уже найденными номерами или дождитесь следующей реплики человека.");
        var reserved = true;
        try
        {
            // Детекция: кэш по байтам, промах — детектор. CancellationToken.None, как в ручке faces:
            // отмена одного вызывающего не должна отменять детекцию остальных ожидающих (single-flight)
            var result = await (_faces ?? new Faces.FaceDetectionCache())
                .GetOrDetectAsync(ownerId, bytes, () => _localMedia!.DetectFacesAsync(bytes, CancellationToken.None), ct);
            if (result.Busy)
                return Deny("Видеокарта занята долгой задачей. Попробуйте чуть позже");
            if (result.Error is not null)
                return Deny(result.Error);

            reserved = false;
            return Json(Controllers.ImageThreadFacesResponse.FromDetection(bytes, result));
        }
        finally
        {
            if (reserved) ReleaseFindFaces(session.Id);
        }
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

        // Обрезка под пропорции — без ИИ: ни котировки, ни поставщика, ни лимита запусков хода (ADR-025 §4).
        // Сознательно ДО TryReserveLaunch: MaxLaunchesPerTurn считает траты на генерацию, а обрезка бесплатная операция
        // над готовым файлом — её серия не должна выедать лимит запусков
        if (string.Equals(Str(args, "op"), "crop", StringComparison.OrdinalIgnoreCase))
            return await CropAsync(args, ownerId, session, scope, thread, version, ct);

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

    // op = crop: тот же метод сервиса, что у кнопки «Обрезать» человека; результат — новая версия, карточка в ленте та же
    private async Task<McpToolCallResult> CropAsync(JsonObject args, string ownerId, Session session, ImageEditScope scope,
        ImageThread thread, ImageThreadVersion version, CancellationToken ct)
    {
        if (Str(args, "aspect") is not { } aspect)
            return Deny("Для op crop укажи aspect — пропорции вида 1:1 или 16:9, до которых обрезать.");
        var done = await _threads.CropToAspectAsync(ownerId, scope, session.Id, thread.Id, version.Id, aspect,
            Str(args, "anchor"), ImageEditInitiator.Agent, dryRun: false, ct);
        if (!done.Ok) return Deny(done.Error ?? "Обрезка не выполнена");
        return Json(new
        {
            threadId = thread.Id,
            baseVersion = new { versionId = version.Id, label = ImageThread.Label(version) },
            newVersion = done.Version is { } v ? new { versionId = v.Id, label = ImageThread.Label(v) } : null,
            size = new { done.Width, done.Height },
            aspect = done.Aspect,
            note = "Обрезано без ИИ, бесплатно: результат — новая версия картинки, оригинал не тронут.",
        });
    }

    private static readonly string[] EditLikeOps = ["edit", "inpaint", "removebackground"];

    // Формат запуска (ADR-025): явные аргументы агента → желание. Генерация записывает формат проекта (✦); у правки размер
    // задаёт исходник, а другие пропорции — только outpaint или crop; прочее формата не имеет. Persist — записывать ли
    private static (MediaFormat? Wish, string? Refusal, bool Persist) FormatFor(
        JsonObject args, ImageEditOp op, (int Width, int Height)? size, MediaFormat? stored)
    {
        var aspect = Str(args, "aspect");
        var quality = Str(args, "quality");
        var width = Int(args, "width");
        var height = Int(args, "height");
        var given = aspect is not null || quality is not null || width is not null || height is not null;
        FormatTier? tier = quality switch { "standard" => FormatTier.Standard, "high" => FormatTier.High, "max" => FormatTier.Max, _ => null };
        // Неизвестная ступень — явный отказ, а не молчаливое «как есть»
        if (quality is not null && tier is null) return (null, "quality — standard, high или max.", false);

        switch (op)
        {
            case ImageEditOp.Generate:
            {
                if (!given) return (stored, null, false);
                if ((width is null) != (height is null)) return (null, "width и height задаются вместе.", false);
                var storedAspect = stored is { Mode: FormatModes.Aspect } ? stored.Aspect : null;
                var wish = width is { } w && height is { } h
                    ? new MediaFormat(FormatModes.Exact, null, tier, w, h, ClaudeHomeServer.Services.ChatContext.ContextActor.Agent, DateTime.UtcNow)
                    : new MediaFormat(FormatModes.Aspect, aspect ?? storedAspect, tier ?? stored?.Tier,
                        null, null, ClaudeHomeServer.Services.ChatContext.ContextActor.Agent, DateTime.UtcNow);
                if (wish.Mode == FormatModes.Aspect && wish.Aspect is null)
                    return (null, "Укажи aspect вместе с quality: пропорции проекта ещё не заданы.", false);
                return ImageProjectPrefsService.ValidateFormat(wish) is { } bad ? (null, bad, false) : (wish, null, true);
            }
            case ImageEditOp.Outpaint:
                if (quality is not null || width is not null || height is not null)
                    return (null, "У outpaint размер задаёт исходник: передай только aspect — до каких пропорций дорисовать.", false);
                return aspect is null
                    ? (null, null, false)
                    : (new MediaFormat(FormatModes.Aspect, aspect, null, null, null,
                        ClaudeHomeServer.Services.ChatContext.ContextActor.Agent, DateTime.UtcNow), null, false);
            default:
                if (!given) return (null, null, false);
                if (EditLikeOps.Contains(op.ToString().ToLowerInvariant()))
                {
                    if (quality is not null || width is not null || height is not null)
                        return (null, "У правки размер задаёт исходник (quality/width/height не применяются). Дорисовать края до других пропорций — "
                            + "op outpaint с aspect, обрезать — op crop с aspect.", false);
                    if (aspect is not null && size is { } s && AspectCrop.TryParse(aspect, out var target)
                        && Math.Abs((double)s.Width / s.Height / target - 1) > 0.01)
                        return (null, $"Правка не меняет пропорции: исходник {s.Width}×{s.Height}, а запрошено {aspect}. "
                            + "Дорисовать края — op outpaint с aspect, обрезать — op crop с aspect (без ИИ, бесплатно).", false);
                    return (null, null, false);
                }
                return (null, "Формат (aspect/quality/width/height) у этой операции не применяется.", false);
        }
    }

    private async Task<McpToolCallResult> LaunchAsync(JsonObject args, string ownerId, Session session,
        ImageEditScope scope, ImageThread thread, ImageThreadVersion version, CancellationToken ct)
    {
        var prefs = _prefs?.Get(ownerId, scope);
        var mode = Enum<EditMode>(args, "mode") ?? EditMode.Auto;
        var prompt = Str(args, "prompt") ?? "";
        // Образцы и персонаж: явный аргумент (в том числе пустой) заменяет контекст чата, не переданный —
        // берётся из контекста (ADR-023 §Д2.1); описание инструмента от этого не меняется
        var referencesGiven = args["references"] is JsonArray;
        var characterGiven = args.ContainsKey("character");
        var references = ReferencesArg(args);
        // Подстановка персонажа на лицо (ADR-028): входы явные — версия, лицо, персонаж;
        // контекст чата не читается вовсе (ассемблер отвергнет ContextRevision вместе с FaceTarget).
        // Провайдер — только local, выбор человека в строке контекста не подменяем молча (ADR-023)
        // Конфликт: faces и face/character несовместимы — проверка до обеих веток
        if (args["faces"] is JsonArray && (Int(args, "face") is not null || Str(args, "character") is not null))
            return Deny("Укажите либо faces, либо face и character");
        if (Int(args, "face") is { } face)
        {
            if (scope.Project is null)
                return Deny("Подстановка персонажа на лицо — только в чате проекта");
            if (Str(args, "character") is not { } faceCharacter)
                return Deny("Укажи персонажа: персонаж из строки контекста на чужое лицо не подставляется");
            if (Str(args, "provider") is not "local")
                return Deny("Подставлять персонажа можно только локальными моделями: укажи provider local");
            return await FaceTargetAsync(ownerId, session, scope, thread, version,
                [new FaceAssignment(face, faceCharacter)], prompt, ct);
        }
        // Несколько лиц за один запуск (ADR-028, этап 3): массив {face, character};
        // несовместим с одиночным face/character; те же проверки local + проект, один TryReserveLaunch
        if (args["faces"] is JsonArray facesArray)
        {
            if (scope.Project is null)
                return Deny("Подстановка персонажей на лица — только в чате проекта");
            if (Str(args, "provider") is not "local")
                return Deny("Подставлять персонажей можно только локальными моделями: укажи provider local");
            var assignments = new List<FaceAssignment>();
            foreach (var item in facesArray.OfType<JsonObject>())
            {
                if (Int(item, "face") is not { } f) continue;
                if (Str(item, "character") is not { } c) continue;
                assignments.Add(new FaceAssignment(f, c));
            }
            // Правила — та же чистая функция, что котировка и сборщик; FacesKey для проверки не нужен
            var rules = Faces.FaceTargetRules.Validate(new FaceTarget(version.Id, "", assignments));
            if (!rules.Ok) return Deny(rules.Error!);
            return await FaceTargetAsync(ownerId, session, scope, thread, version, assignments, prompt, ct);
        }
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
        var (wish, formatRefusal, persistFormat) = FormatFor(args, op, source is null ? null : ImageDimensions.Read(source.Bytes), prefs?.Format);
        if (formatRefusal is not null) return Deny(formatRefusal);
        if (source is null && op != ImageEditOp.Generate)
            return Deny("Картинки ещё нет: это новая картинка. Сначала нарисуй её — op generate или без op.");
        if (prompt.Length == 0 && op is ImageEditOp.Generate or ImageEditOp.Edit or ImageEditOp.Inpaint)
            return Deny("Пустой промпт: опиши, что нарисовать или поправить.");

        // Что не передано — выбор человека по операции: генерация по тексту — режим «Создать»,
        // правка — настройки нити, иначе «Править»; поставщик по умолчанию — как у каталога.
        // Пока «Создать» не сохраняли (старый фронт), генерация берёт настройки нити, как раньше.
        // Явный provider агента не подменяется. Персонаж — подключённый в полосе проекта (у
        // личной области его нет)
        var settings = op == ImageEditOp.Generate && prefs is { Create: not null }
            ? prefs.CreateSettings()
            : thread.Settings ?? prefs?.EditSettings();
        var provider = Str(args, "provider") ?? settings?.Provider ?? DefaultProvider();
        if (provider is null)
            return Deny("Поставщик рисования не настроен. Обратитесь к администратору.");
        var matchSourceSize = Bool(args, "matchSourceSize") ?? settings?.MatchSourceSize ?? true;
        var size = source is null ? null : ImageDimensions.Read(source.Bytes);

        // Операции со своей моделью — как у человека: из выбора в полосе не наследуются ни
        // модель, ни число вариантов, ни персонаж. Явные аргументы агента остаются как есть
        var own = ImageEditCatalog.OwnModelOps.Contains(op);
        // Модель выбора человека — только вместе с его поставщиком: у явного чужого provider она не годится
        var foreign = settings?.Provider is { } chosen && chosen != provider;
        var model = Str(args, "model") ?? (own || foreign ? null : settings?.Model) ?? ImageEditCatalog.AutoModelId;
        var count = Int(args, "count") ?? (!own && settings is { Count: > 0 } s ? s.Count : 1);
        // Персонаж префов проекта не читается: он ref контекста чата
        var character = Str(args, "character");
        IReadOnlyList<ReferenceImage> contextSamples = [];
        if (_context is not null && (!referencesGiven || !characterGiven))
        {
            var layout = _context.Layout(new ContextScope(ownerId, session, scope.Project),
                _context.Current(ownerId, session.Id), op);
            if (!referencesGiven)
            {
                references = layout.ReferencePaths;
                var loaded = await _context.LoadSamplesAsync(ownerId, scope, session.Id, layout, ct);
                if (loaded.Value is not { } samples) return Deny(loaded.Error ?? "Образцы контекста не прочитаны.");
                contextSamples = samples;
            }
            if (!characterGiven) character = layout.CharacterSlug;
        }

        var quoteRequest = new ImageEditQuoteRequest(provider, model, mode, op, count,
            HasMask: false,
            References: references.Count + contextSamples.Count,
            HasCharacter: character is not null,
            Width: size?.Width,
            Height: size?.Height,
            Removal: EditIntent.IsRemoval(prompt),
            Format: wish);
        var quote = await _jobs!.QuoteAsync(ownerId, scope.Key, quoteRequest, ct);
        if (quote.Value is not { } q)
            return await FailAsync(quote.ErrorCode, quote.Error, ownerId, scope, quoteRequest, ct);

        var request = new ImageEditLaunchRequest(
            q.QuoteId, prompt, MarksJson: null, thread.File, source, Mask: null, Annotated: null,
            Uploaded: contextSamples,
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

        // Явный формат генерации — формат проекта с ✦ (решение 2 Андрея); запись и тихая строка — только после запуска
        if (persistFormat && wish is not null && _prefs is not null && !SameFormat(prefs?.Format, wish))
        {
            var (before, _) = await _prefs.SetFormatAsync(ownerId, scope, wish, ClaudeHomeServer.Services.ChatContext.ContextActor.Agent);
            await _threads.AgentFormatLineAsync(session.Id, before, wish, ct);
        }

        return Json(new
        {
            jobId = created.JobId,
            threadId = thread.Id,
            baseVersion = new { versionId = version.Id, label = ImageThread.Label(version) },
            quote = new { q.Provider, q.Model, q.Estimate, q.ExpectedSeconds, format = q.Format },
            note = "Генерация идёт отдельно от хода: остановка разговора её не отменяет, отмена — image_cancel. "
                + "Каждый вариант станет новой версией картинки внизу ленты; "
                + (scope.IsPersonal ? "скачать её может только человек." : "сохранить в проект может только человек."),
        });
    }

    // Запуск «подставить персонажа на лицо» (ADR-028, этап 2): та же котировка и тот же
    // ImageEditLaunchAssembler, что у обычного image_generate, но входы явные — версия, лицо,
    // персонаж; образцы и персонаж контекста чата не едут, маску строит сервер. Ключ берётся из
    // свежей детекции версии: агенту его передавать не нужно, номера лиц — те же, что в
    // image_find_faces (один кэш, один порядок)
    private async Task<McpToolCallResult> FaceTargetAsync(string ownerId, Session session, ImageEditScope scope,
        ImageThread thread, ImageThreadVersion version, IReadOnlyList<FaceAssignment> faces, string prompt,
        CancellationToken ct)
    {
        // Байты версии: шаг версии, иначе файл проекта (у исходника)
        ImageBytes? source = null;
        string? baseStepId = null;
        if (thread.ImageStepOf(version) is { Length: > 0 } stepId && _steps?.Open(ownerId, scope.Key, stepId) is { } step)
        {
            source = new ImageBytes(step.Image.Bytes, step.Image.ContentType);
            baseStepId = stepId;
        }
        else if (thread.File is { Length: > 0 } file && scope.Project is { } project)
        {
            var read = await ImageEditLaunchAssembler.ReadProjectImageAsync(project.RootPath, file, "Файл картинки", ct);
            if (read.Value is not { } image) return Deny(read.Error!);
            source = image;
        }
        if (source is null)
            return Deny("У версии ещё нет изображения");

        // Одна цель на вызов: одно лицо (этап 2) или несколько (этап 3, ADR-028).
        // Персонаж — только в FaceTarget.Faces: верхний CharacterSlug не едет, иначе
        // StartIn отвергнет запуск как «персонаж задаётся в faceTarget.faces»
        var faceTarget = new FaceTarget(version.Id, Faces.FaceDetectionCache.FacesKey(source.Bytes), faces);
        var quoteRequest = new ImageEditQuoteRequest(LocalImageEditor.ProviderKey, ImageEditCatalog.AutoModelId,
            EditMode.Auto, ImageEditOp.Edit, 1,
            HasMask: true,
            References: 1,
            HasCharacter: true,
            Width: ImageDimensions.Read(source.Bytes)?.Width,
            Height: ImageDimensions.Read(source.Bytes)?.Height,
            FaceTarget: faceTarget);
        var quote = await _jobs!.QuoteAsync(ownerId, scope.Key, quoteRequest, ct);
        if (quote.Value is not { } q)
            return await FailAsync(quote.ErrorCode, quote.Error, ownerId, scope, quoteRequest, ct, face: true);

        var request = new ImageEditLaunchRequest(
            q.QuoteId, prompt, MarksJson: null, thread.File, source, Mask: null, Annotated: null,
            Uploaded: [],
            ReferencePaths: [],
            null,
            MatchSourceSize: true,
            BaseStepId: baseStepId,
            Initiator: ImageEditInitiator.Agent,
            ThreadSessionId: session.Id,
            ThreadId: thread.Id,
            VersionId: version.Id,
            FaceTarget: faceTarget);
        var started = await _launcher.LaunchAsync(ownerId, scope, request, ct);
        if (started.Value is not { } created)
            return await FailAsync(started.ErrorCode, started.Error, ownerId, scope, quoteRequest, ct, face: true);

        return Json(new
        {
            jobId = created.JobId,
            threadId = thread.Id,
            baseVersion = new { versionId = version.Id, label = ImageThread.Label(version) },
            quote = new { q.Provider, q.Model, q.Estimate, q.ExpectedSeconds, format = q.Format },
            note = "Генерация идёт отдельно от хода: остановка разговора её не отменяет, отмена — image_cancel. "
                + "Каждый вариант станет новой версией картинки внизу ленты; сохранить в проект может только человек.",
        });
    }

    private static bool SameFormat(MediaFormat? a, MediaFormat b) =>
        a is not null && a.Mode == b.Mode && a.Aspect == b.Aspect && a.Tier == b.Tier && a.Width == b.Width && a.Height == b.Height;

    // Отказ поставщика: в результате — котировка соседа, чтобы человек повторил одним кликом.
    // Сам тулсет второй раз не запускает — выбор поставщика не подменяется. face — запуск
    // «на лицо»: при faces_changed подсказывает повторить image_find_faces (список лиц изменился)
    private async Task<McpToolCallResult> FailAsync(string? code, string? error, string ownerId, ImageEditScope scope,
        ImageEditQuoteRequest failed, CancellationToken ct, bool face = false)
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
        var text = error ?? "Запуск не выполнен";
        if (face && code == ImageEditErrorCodes.FacesChanged)
            text += " Повторите image_find_faces: номера лиц могут измениться.";
        return new McpToolCallResult(JsonSerializer.Serialize(new
        {
            error = text,
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
        var state = _threads.View(ownerId, session.Id);
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
            formatNote = "Пропорции и ступени каждой модели — providers[].models[].caps.format (aspects, tiers). "
                + "Формат — выбор человека: aspect/quality/width/height передавай только по его просьбе; "
                + "op crop обрезает картинку под пропорции без ИИ, op outpaint дорисовывает края.",
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
                similarity = Chats.ImageEditorStateContributor.SimilarityText(t, v),
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

    // Лимит поиска лиц за ход: все вызовы считаются (включая кэшированные повторы) —
    // проще и безопаснее, чем отслеживать, был ли результат из кэша
    private bool TryReserveFindFaces(string sessionId)
    {
        lock (_findFacesGate)
        {
            var used = _findFaces.GetValueOrDefault(sessionId);
            if (used >= MaxFindFacesPerTurn) return false;
            _findFaces[sessionId] = used + 1;
            return true;
        }
    }

    private void ReleaseFindFaces(string sessionId)
    {
        lock (_findFacesGate)
        {
            if (_findFaces.TryGetValue(sessionId, out var used) && used > 0)
                _findFaces[sessionId] = used - 1;
        }
    }

    private Task OnTurnCompleted(TurnCompleted e)
    {
        lock (_launchGate) _launches.Remove(e.Turn.SessionId);
        lock (_findFacesGate) _findFaces.Remove(e.Turn.SessionId);
        return Task.CompletedTask;
    }

    // ── Маршрут: /mcp/image-editor/{sessionId} ─────────────────────────────────

    // Чат владельца токена: у чата проекта — его проект (свой), у личного —
    // личная область. Сопоставление — только ImageEditScope.Of. Любой отказ одним текстом: чужой
    // чат неотличим от несуществующего, а состав у него пустой
    private bool TryResolve(McpToolCallContext context, out Session session, out ImageEditScope scope, out string error)
    {
        session = null!;
        scope = null!;
        error = "Чат не найден — инструменты редактора картинок недоступны.";
        if (!TryParseRoute(context.RouteTail, out var sessionId)) return false;
        if (_sessions.GetOwned(sessionId, context.OwnerId) is not { } owned) return false;
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
