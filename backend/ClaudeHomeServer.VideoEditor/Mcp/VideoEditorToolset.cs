using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Catalog;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Prefs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Mcp;

/// <summary>
/// MCP-сервер модуля «Видео» для агента любого чата владельца — проектного и личного (ADR-022 §5, по образцу
/// AudioEditorToolset). Маршрут — <c>POST /mcp/video-editor/{sessionId}</c>: хвост несёт чат, по нему тулсет находит
/// владельца, область (проект или личную) и нити сцен чата. Ехать ли серверу в ход, решает Main
/// (SessionManager.BuildVideoEditorContext).
///
/// ГЛАВНЫЙ ИНВАРИАНТ (требование Андрея 2026-10-02): у тулсета нет своего пути исполнения. Каждый инструмент зовёт
/// ТОТ ЖЕ сервисный метод, что REST-ручка человека (VideoSceneService, VideoEditJobService, FilmSceneSaver,
/// FilmService, FilmAssembler), а записи ленты (module_record) пишут эти сервисы — тулсет не пишет их сам никогда.
/// Поэтому действие агента ложится в ленту теми же карточками, что кнопка человека; различает только initiator.
/// Сторожит матричный тест VideoEditorToolsetFeedParityTests.
///
/// Потолка трат за ход и лимита запусков НЕТ (решение Андрея): правило «после каждой сцены спроси „Снимать
/// следующую?“, подряд — только по явному „сними все“» держит блок хвоста хода, а не счётчик. Траты пишет
/// исполнитель с Initiator = Agent. Задача живёт отдельно от хода: «Стоп» её не отменяет, отмена — video_cancel.
///
/// Сторожа — в CallAsync, не в составе: делегированный и реакционный ход получает отказ fail-closed
/// (IDelegatedTurnGate) на shoot, cancel, save_scene, film_edit, film_build.
///
/// ИНВАРИАНТ состава: tools/list зависит от сессии, флага владельца и настройки инстанса VideoEditor:AgentLaunch —
/// не от хода, фокуса, поставщика, вида чата и сцен (McpToolsetStabilityTests). Без нужного — отказ на вызове.
/// </summary>
public sealed partial class VideoEditorToolset : IMcpParameterizedToolset
{
    public const string AgentLaunchKey = "VideoEditor:AgentLaunch";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IMcpSessionAccessor _sessions;
    private readonly IFeatureFlagGate _flags;
    private readonly IProjectManager _projects;
    private readonly IEnumerable<IVideoEngine> _engines;
    private readonly VideoJobThreads _threads;
    private readonly VideoEditJobService _jobs;
    private readonly VideoPrefsService _prefs;
    private readonly VideoSceneService _scenes;
    private readonly FilmService _films;
    private readonly FilmSceneSaver _saver;
    private readonly FilmAssembler _assembler;
    private readonly IDelegatedTurnGate? _turnGate;
    private readonly bool _agentLaunch;

    public VideoEditorToolset(
        IMcpSessionAccessor sessions,
        IFeatureFlagGate flags,
        IProjectManager projects,
        IEnumerable<IVideoEngine> engines,
        VideoJobThreads threads,
        VideoEditJobService jobs,
        VideoPrefsService prefs,
        VideoSceneService scenes,
        FilmService films,
        FilmSceneSaver saver,
        FilmAssembler assembler,
        IDelegatedTurnGate? turnGate = null,
        IConfiguration? config = null)
    {
        _sessions = sessions;
        _flags = flags;
        _projects = projects;
        _engines = engines;
        _threads = threads;
        _jobs = jobs;
        _prefs = prefs;
        _scenes = scenes;
        _films = films;
        _saver = saver;
        _assembler = assembler;
        _turnGate = turnGate;
        _agentLaunch = config?.GetValue(AgentLaunchKey, true) ?? true;
    }

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? ToolsOfInstance : [];

    // Состав инстанса: без запуска агентом остаются чтение, фокус, заготовка сцены и её настройки — всё, что
    // ничего не тратит и не пишет в проект
    private IReadOnlyList<McpToolSchema> ToolsOfInstance => _agentLaunch
        ? Schemas
        : [.. Schemas.Where(t => ReadOnlyTools.Contains(t.Name))];

    private VideoThreadStore Store => _threads.Store;

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var scope, out var error))
            return Deny(error);
        if (ToolsOfInstance.All(t => t.Name != tool))
            return Deny($"Инструмент {tool} недоступен на этом сервере.");

        var owner = context.OwnerId;
        return tool switch
        {
            ToolState => Json(DescribeState(owner, session, scope)),
            ToolFocus => await FocusAsync(arguments, owner, session, scope),
            ToolNew => await NewAsync(arguments, owner, session, scope, ct),
            ToolSceneSet => await SceneSetAsync(arguments, owner, session, scope),
            ToolSuggestPrompt => SuggestPrompt(arguments),
            ToolShoot => await ShootAsync(arguments, owner, session, scope, ct),
            ToolCancel => await CancelAsync(arguments, owner, session, scope, ct),
            ToolSaveScene => await SaveSceneAsync(arguments, owner, session, scope, ct),
            ToolFilmEdit => await FilmEditAsync(arguments, owner, session, scope, ct),
            ToolFilmBuild => FilmBuild(arguments, owner, session, scope),
            _ => Deny($"Неизвестный инструмент: {tool}"),
        };
    }

    // ── video_focus, video_new, video_scene_set ────────────────────────────────

    private async Task<McpToolCallResult> FocusAsync(JsonObject args, string owner, Session session, VideoEditScope scope)
    {
        var current = Store.Get(owner, session.Id);
        var sceneGiven = args.ContainsKey("sceneId");
        var filmGiven = args.ContainsKey("filmPath");
        var versionId = Str(args, "versionId");
        var sceneId = Str(args, "sceneId");
        if (versionId is not null && sceneId is null)
            return Deny("versionId — версия сцены sceneId: укажи и sceneId.");
        if (sceneId is not null && current.Scenes.FirstOrDefault(s => s.SceneId == sceneId) is { } target
            && versionId is not null && target.Versions.All(v => v.VersionId != versionId))
            return Deny($"У сцены {sceneId} нет версии {versionId}. Версии: "
                + string.Join(", ", target.Versions.Select(v => $"{v.VersionId} (версия {v.Number})")) + ".");

        VideoSceneService.Call call;
        if (versionId is not null)
            call = await _scenes.CurrentAsync(owner, scope, session.Id, sceneId!, versionId, null);
        else
        {
            var focus = new VideoFocusDto(
                sceneGiven ? sceneId : current.Focus.SceneId,
                filmGiven ? Str(args, "filmPath") : current.Focus.FilmPath);
            call = await _scenes.FocusAsync(owner, scope, session.Id, focus, null);
        }
        return await ThreadsResultAsync(call, owner, session, scope,
            "Человек видит в панели «Видео», что ты взял в работу; панель сама не двигается и выбор можно снять.");
    }

    private async Task<McpToolCallResult> NewAsync(JsonObject args, string owner, Session session, VideoEditScope scope,
        CancellationToken ct)
    {
        var folder = Str(args, "folder");
        if (folder is not null && scope.Project is null)
            return Deny("В чате вне проекта папок нет: вызови video_new без folder.");
        if (ApplySettings(args, _prefs.ForNewScene(owner, scope), out var settings) is { } bad) return Deny(bad);

        var call = await _scenes.AddAsync(owner, scope, session.Id, folder, settings, Str(args, "name"), null, ct);
        return await ThreadsResultAsync(call, owner, session, scope,
            "Сцена заведена и в работе. Снять её — video_shoot; человек видит карточку сцены в ленте.");
    }

    private async Task<McpToolCallResult> SceneSetAsync(JsonObject args, string owner, Session session, VideoEditScope scope)
    {
        if (Str(args, "sceneId") is not { } sceneId) return Deny("Не указан sceneId: возьми его из video_state.");
        if (Store.Get(owner, session.Id).Scenes.FirstOrDefault(s => s.SceneId == sceneId) is not { } scene)
            return Deny($"Сцены {sceneId} нет в этом чате. Список — video_state.");
        if (ApplySettings(args, scene.Settings, out var settings) is { } bad) return Deny(bad);

        var call = await _scenes.SettingsAsync(owner, scope, session.Id, sceneId, settings, null);
        return await ThreadsResultAsync(call, owner, session, scope, "Настройки сцены сохранены.");
    }

    // Результат операции над нитями: отказ проверки входа — с кодом, запись — состояние сцены в фокусе
    private async Task<McpToolCallResult> ThreadsResultAsync(VideoSceneService.Call call, string owner, Session session,
        VideoEditScope scope, string note)
    {
        if (call.Written is not { } written) return Fail(call.ErrorCode, call.Error);
        return written.Status switch
        {
            VideoThreadWriteStatus.Ok => Json(new
            {
                focus = written.State.Focus,
                scene = (written.Scene ?? written.State.Scenes.FirstOrDefault(s => s.SceneId == written.State.Focus.SceneId)) is { } s
                    ? DescribeScene(owner, scope, VideoStale.Apply(s))
                    : null,
                note,
            }),
            VideoThreadWriteStatus.SceneNotFound => Deny("Такой сцены нет в этом чате. Список — video_state."),
            VideoThreadWriteStatus.VersionNotFound => Deny("У сцены нет такой версии. Список — video_state."),
            VideoThreadWriteStatus.Invalid => Deny("Действие не подходит этой сцене: у неё идёт съёмка или настройки неверны."),
            _ => Deny("Сцены чата как раз меняются — повтори позже."),
        };
    }

    // Настройки сцены из аргументов поверх базовых: указанное заменяет, неуказанное остаётся; null у кадра снимает его.
    // Ошибка — текст отказа, иначе null
    private static string? ApplySettings(JsonObject args, VideoSceneSettingsDto baseSettings, out VideoSceneSettingsDto result)
    {
        result = baseSettings;
        var frameA = baseSettings.FrameA;
        var frameB = baseSettings.FrameB;
        foreach (var (key, slot) in new[] { ("frameA", 0), ("frameB", 1) })
        {
            if (!args.ContainsKey(key)) continue;
            FrameRef? parsed = null;
            if (args[key] is not null)
            {
                if (args[key] is not JsonObject obj || ParseFrame(obj) is not { } frame)
                    return key + ": объект с kind file (и path) или kind image (и threadId, versionId) либо null.";
                parsed = frame;
            }
            if (slot == 0) frameA = parsed; else frameB = parsed;
        }
        if (!TryInt(args, "durationSec", out var duration) || !TryInt(args, "count", out var count))
            return "durationSec и count — целые числа.";
        if (args["sound"] is not null && Bool(args, "sound") is null) return "sound — true или false.";
        result = baseSettings with
        {
            FrameA = frameA,
            FrameB = frameB,
            Text = args.ContainsKey("text") ? Str(args, "text") ?? "" : baseSettings.Text,
            Provider = args.ContainsKey("provider") ? Str(args, "provider") : baseSettings.Provider,
            Model = args.ContainsKey("model") ? Str(args, "model") : baseSettings.Model,
            DurationSec = duration ?? baseSettings.DurationSec,
            Aspect = args.ContainsKey("aspect") ? Str(args, "aspect") : baseSettings.Aspect,
            Sound = Bool(args, "sound") ?? baseSettings.Sound,
            Count = count ?? baseSettings.Count,
        };
        return null;
    }

    private static FrameRef? ParseFrame(JsonObject obj)
    {
        var path = Str(obj, "path");
        var threadId = Str(obj, "threadId");
        var versionId = Str(obj, "versionId");
        return Str(obj, "kind") switch
        {
            FrameRef.KindFile when path is not null => FrameRef.File(path),
            FrameRef.KindImage when threadId is not null && versionId is not null =>
                FrameRef.Image(threadId, versionId, Bool(obj, "follow") ?? true),
            _ => null,
        };
    }

    // ── video_shoot и video_cancel ─────────────────────────────────────────────

    private async Task<McpToolCallResult> ShootAsync(JsonObject args, string owner, Session session, VideoEditScope scope,
        CancellationToken ct)
    {
        // sceneId обязателен: человек мог сменить сцену посреди хода, и съёмка «по текущему фокусу» ушла бы не туда.
        // Отказ — до гейта: он про права, а не про форму вызова
        if (Str(args, "sceneId") is not { } sceneId)
            return Deny("Не указан sceneId: съёмка идёт только в конкретную сцену чата. "
                + "Возьми id из video_state (или заведи сцену через video_new).");
        if (GateTurn(owner, session, "Съёмка видео") is { } denied) return denied;
        if (args["params"] is { } rawParams && rawParams is not JsonObject)
            return Deny("params — объект с частными параметрами модели.");
        if (!TryInt(args, "durationSec", out var duration) || !TryInt(args, "count", out var count))
            return Deny("durationSec и count — целые числа.");
        if (args["sound"] is not null && Bool(args, "sound") is null) return Deny("sound — true или false.");
        if (args["seed"] is not null && Long(args, "seed") is null) return Deny("seed — целое число.");

        if (Store.Get(owner, session.Id).Scenes.All(s => s.SceneId != sceneId))
            return Deny($"Сцены {sceneId} нет в этом чате. Список — video_state.");

        // Тот же путь, что у человека: котировка, затем запуск строго по её quoteId; без provider/model цепочку
        // «настройки сцены → префы области → умолчание каталога» разворачивает исполнитель
        var quote = await _jobs.QuoteAsync(owner, scope,
            new VideoQuoteRequest(session.Id, sceneId, Str(args, "provider"), Str(args, "model"), count, duration,
                Str(args, "aspect"), Bool(args, "sound")), ct);
        if (quote.Value is not { } q) return Fail(quote.ErrorCode, quote.Error, quote.Retry);

        var started = await _jobs.StartAsync(owner, scope,
            new VideoLaunchRequest(q.QuoteId, session.Id, sceneId, VideoInitiators.Agent, args["params"] as JsonObject, Long(args, "seed")), ct);
        if (started.Value is not { } created) return Fail(started.ErrorCode, started.Error, started.Retry);

        return Json(new
        {
            jobId = created.JobId,
            sceneId,
            quote = new { q.Provider, q.Model, q.Count, q.DurationSec, q.Price, q.License },
            note = "Съёмка идёт отдельно от хода: остановка разговора её не отменяет, отмена — video_cancel. "
                + "Результат — версии сцены в video_state (запуск running → done); не выдумывай результат, пока он не готов. "
                + "Каждый вариант станет версией сцены. После сцены спроси человека «Снимать следующую?»; "
                + "подряд снимать можно только по его явному «сними все».",
        });
    }

    private async Task<McpToolCallResult> CancelAsync(JsonObject args, string owner, Session session, VideoEditScope scope,
        CancellationToken ct)
    {
        if (GateTurn(owner, session, "Отмена съёмки видео") is { } denied) return denied;
        var jobId = Str(args, "jobId") ?? "";
        // Задача другого чата (или чужая) неотличима от несуществующей
        var job = _jobs.Get(owner, scope.Key, jobId);
        if (job is null || job.ChatSessionId != session.Id) return Deny("Задача не найдена.");
        var cancelled = await _jobs.CancelAsync(owner, scope.Key, jobId, ct);
        return cancelled is null
            ? Deny("Задача не найдена.")
            : Json(new { cancelled.JobId, cancelled.Status, cancelled.Charged, variants = cancelled.Variants.Count });
    }

    // ── video_save_scene, video_film_edit, video_film_build ────────────────────

    private async Task<McpToolCallResult> SaveSceneAsync(JsonObject args, string owner, Session session, VideoEditScope scope,
        CancellationToken ct)
    {
        if (GateTurn(owner, session, "Сохранение сцены в проект") is { } denied) return denied;
        if (FilmsRefusal(scope) is { } refusal) return refusal;
        if (Str(args, "sceneId") is not { } sceneId) return Deny("Не указан sceneId: возьми его из video_state.");

        // Тот же сервис, что у ручки «Сохранить сцену»; тихую строку и пометку «✦ Claude» пишет он сам
        var saved = await _saver.SaveAsync(owner, scope,
            new SaveSceneRequest(session.Id, sceneId, Str(args, "versionId"), Str(args, "folder"), Str(args, "fileName")),
            VideoInitiators.Agent, ct);
        if (saved.Value is not { } done) return FilmFail(saved.ErrorCode, saved.Error, saved.Conflict);
        return Json(new
        {
            done.Path,
            done.FramePaths,
            done.AddedToFilm,
            note = "Сцена сохранена в проект (перезаписи нет); человек видит тихую строку «✦ Claude» в ленте. "
                + "Собрать фильм — video_film_build.",
        });
    }

    private async Task<McpToolCallResult> FilmEditAsync(JsonObject args, string owner, Session session, VideoEditScope scope,
        CancellationToken ct)
    {
        if (GateTurn(owner, session, "Правка фильма") is { } denied) return denied;
        if (FilmsRefusal(scope) is { } refusal) return refusal;
        if (Str(args, "path") is not { } path) return Deny("Не указан path: путь к .film в video/**.");
        if (Str(args, "revision") is not { } revision)
            return Deny("Не указана revision: фильм правится только под ревизией. Возьми её из video_state (film.revision).");
        if (args["ops"] is not JsonArray array || array.Count == 0)
            return Deny("ops — непустой список операций патча.");
        var ops = new List<FilmPatchOp>();
        foreach (var item in array)
        {
            if (item is not JsonObject obj) return Deny("Операция патча — объект { op, … }.");
            if (ParseOp(obj, out var op) is { } bad) return Deny(bad);
            ops.Add(op!);
        }

        var patched = await _films.PatchAsync(owner, scope, path, new FilmPatch(revision, ops), VideoInitiators.Agent, ct, session.Id);
        if (patched.Value is not { } state) return FilmFail(patched.ErrorCode, patched.Error, patched.Conflict);
        return Json(new
        {
            film = state,
            note = "Фильм изменён; строки, которые ты тронул, помечены «✦ Claude», в ленте — тихая строка. Собрать — video_film_build.",
        });
    }

    private McpToolCallResult FilmBuild(JsonObject args, string owner, Session session, VideoEditScope scope)
    {
        if (GateTurn(owner, session, "Сборка фильма") is { } denied) return denied;
        if (FilmsRefusal(scope) is { } refusal) return refusal;
        var path = Str(args, "path") ?? Store.Get(owner, session.Id).Focus.FilmPath;
        if (path is null) return Deny("Не указан path, и открытого фильма нет: возьми фильм из video_state (films).");

        var started = _assembler.Start(owner, scope, path, VideoInitiators.Agent, session.Id);
        if (started.Value is not { } status) return FilmFail(started.ErrorCode, started.Error, started.Conflict);
        return Json(new
        {
            path,
            build = status,
            note = "Заявка на сборку принята: она идёт в фоне под тяжёлым слотом сервера, ход — в video_state (film.build). "
                + "Когда фильм собран, человек видит карточку в ленте.",
        });
    }

    // Операция патча из аргументов; неизвестный ключ — отказ с именем поля
    private static readonly string[] OpKeys = ["op", "file", "index", "from", "to", "trim", "cutType", "sec", "scene", "music"];
    private static readonly string[] SceneKeys = ["text", "frameA", "frameB", "provider", "model", "durationSec"];

    private static string? ParseOp(JsonObject obj, out FilmPatchOp? op)
    {
        op = null;
        if (obj.Select(p => p.Key).FirstOrDefault(k => !OpKeys.Contains(k)) is { } unknown)
            return $"ops: у операции нет поля «{unknown}». Допустимые: {string.Join(", ", OpKeys)}.";
        if (Str(obj, "op") is not { } name || !FilmOps.Contains(name))
            return $"ops: op — одно из {string.Join(", ", FilmOps)}.";
        if (!TryInt(obj, "index", out var index) || !TryInt(obj, "from", out var from) || !TryInt(obj, "to", out var to))
            return "ops: index, from, to — целые числа.";
        if (obj["sec"] is not null && Num(obj, "sec") is null) return "ops: sec — число секунд.";

        IReadOnlyList<double>? trim = null;
        if (obj["trim"] is not null)
        {
            if (obj["trim"] is not JsonArray t || t.Count != 2 || t.Select(NumberOf).Any(n => n is null))
                return "ops: trim — [начало, конец] в секундах.";
            trim = [.. t.Select(n => NumberOf(n)!.Value)];
        }

        FilmSceneSnapshot? scene = null;
        if (obj["scene"] is { } rawScene)
        {
            if (rawScene is not JsonObject s) return "ops: scene — объект { text, … }.";
            if (s.Select(p => p.Key).FirstOrDefault(k => !SceneKeys.Contains(k)) is { } badKey)
                return $"ops: у scene нет поля «{badKey}». Допустимые: {string.Join(", ", SceneKeys)}.";
            if (!TryInt(s, "durationSec", out var sceneDuration)) return "ops: scene.durationSec — целое число.";
            scene = new FilmSceneSnapshot(Str(s, "text") ?? "", Str(s, "frameA"), Str(s, "frameB"), Str(s, "provider"),
                Str(s, "model"), sceneDuration);
        }

        FilmMusic? music = null;
        if (name == FilmPatchOps.Music && obj["music"] is { } rawMusic)
        {
            if (rawMusic is not JsonObject m || Str(m, "file") is not { } file || !TryInt(m, "volume", out var volume))
                return "ops: music — { file, volume, fadeOut } или null.";
            if (m["fadeOut"] is not null && Num(m, "fadeOut") is null) return "ops: music.fadeOut — секунды.";
            music = new FilmMusic(file, volume ?? 100, Num(m, "fadeOut") ?? 0);
        }

        op = new FilmPatchOp(name, Str(obj, "file"), index, from, to, trim, Str(obj, "cutType"), Num(obj, "sec"), scene, music);
        return null;
    }

    // ── video_suggest_prompt и video_state ─────────────────────────────────────

    private static McpToolCallResult SuggestPrompt(JsonObject args)
    {
        var prompt = Str(args, "prompt");
        if (prompt is null) return Deny("Пустой текст: предложить нечего.");
        return Json(new
        {
            prompt,
            model = Str(args, "model"),
            note = "Предложение показано человеку; ничего не запущено и не изменено.",
        });
    }

    private object DescribeState(string owner, Session session, VideoEditScope scope)
    {
        var state = Store.Get(owner, session.Id).ToDto();
        var filmsOk = scope.Project is { } project && ClaudeHomeServer.Models.ProjectCapabilities.FilesOnServer(project);
        object? films = null;
        object? openFilm = null;
        if (filmsOk)
        {
            if (_films.List(scope).Value is { } list) films = list;
            if (state.Focus.FilmPath is { } filmPath)
            {
                var read = _films.State(owner, scope, filmPath);
                openFilm = read.Value is { } filmState
                    ? filmState
                    : new { path = filmPath, error = read.Error, code = read.ErrorCode };
            }
        }
        return new
        {
            focus = state.Focus,
            scenes = state.Scenes.Select(s => DescribeScene(owner, scope, s)),
            films,
            film = openFilm,
            catalog = VideoCatalogView.Build(_engines, scope, _jobs.PrefersLocal(owner)),
            humanChoice = new
            {
                prefs = _prefs.Get(owner, scope),
                rule = "Это выбор человека: настройки сцены, затем префы области, затем умолчание. Не передавай provider, "
                    + "model, durationSec и count без его просьбы — video_shoot возьмёт выбор сам.",
            },
            agentLaunch = _agentLaunch,
            note = (state.Scenes.Count, scope.IsPersonal) switch
            {
                (0, true) => "В этом чате ещё нет сцен. Заведи новую (video_new): это чат вне проекта, фильмов и файлов проекта тут нет.",
                (0, false) => "В этом чате ещё нет сцен. Заведи новую (video_new), настрой (video_scene_set) и сними (video_shoot).",
                (_, true) => "Каждый вариант съёмки — версия сцены. Фильмы и сохранение в проект — только в чате проекта.",
                _ => "Каждый вариант съёмки — версия сцены. Сохранить версию в проект — video_save_scene, править и собирать фильм — "
                    + "video_film_edit и video_film_build.",
            },
        };
    }

    private object DescribeScene(string owner, VideoEditScope scope, VideoSceneDto s) => new
    {
        sceneId = s.SceneId,
        name = s.Name,
        folder = s.Folder,
        settings = s.Settings,
        currentVersionId = s.CurrentVersionId,
        versions = s.Versions.Select(v => new
        {
            versionId = v.VersionId,
            number = v.Number,
            current = v.VersionId == s.CurrentVersionId,
            v.Provider,
            v.Model,
            v.DurationSec,
            v.HasSound,
            v.Cost,
            v.Initiator,
            prompt = v.Inputs.Text,
        }),
        running = s.Launches.Where(l => l.Status == VideoLaunchStatus.Running).Select(l =>
            _jobs.Get(owner, scope.Key, l.JobId) is { } j
                ? new { j.JobId, j.Status, j.Provider, j.Model, j.Count, j.QueuePosition, j.EtaSeconds, l.Initiator }
                : new { l.JobId, Status = VideoJobStatuses.Queued, l.Provider, l.Model, l.Count, QueuePosition = (int?)null, EtaSeconds = (int?)null, l.Initiator }),
        stale = s.Stale,
        savedFiles = s.SavedFiles,
        filmRef = s.FilmRef,
    };

    // ── Гейты ──────────────────────────────────────────────────────────────────

    // Делегированный и реакционный ход — отказ fail-closed; нет шва — тоже отказ (по построению)
    private McpToolCallResult? GateTurn(string owner, Session session, string action)
    {
        var denied = _turnGate is null
            ? $"{action} недоступно: сервер не проверил ход — отказ по построению."
            : _turnGate.Deny(owner, session.Id, action);
        return denied is null ? null : Deny(denied);
    }

    // Фильмы и сохранение в проект: у личной области диска проекта нет, у локального проекта (ADR-016) файлы на
    // устройстве — отказ до обращения к диску
    private static McpToolCallResult? FilmsRefusal(VideoEditScope scope)
    {
        if (scope.Project is not { } project)
            return Fail(VideoEditorErrors.PersonalScopeNoFilms, "Фильмы — только в чате проекта");
        return ClaudeHomeServer.Models.ProjectCapabilities.FilesOnServer(project)
            ? null
            : Fail(VideoEditorErrors.ProjectLocalUnsupported, "Файлы локального проекта лежат на устройстве: сервер их не трогает");
    }

    // ── Маршрут: /mcp/video-editor/{sessionId} ─────────────────────────────────

    // Чат владельца токена при включённом флаге: у чата проекта — его проект (свой), у личного — личная область.
    // Любой отказ одним текстом: чужой чат неотличим от несуществующего, состав пустой
    private bool TryResolve(McpToolCallContext context, out Session session, out VideoEditScope scope, out string error)
    {
        session = null!;
        scope = null!;
        error = "Чат не найден — инструменты видео недоступны.";
        if (!TryParseRoute(context.RouteTail, out var sessionId)) return false;
        if (_sessions.GetOwned(sessionId, context.OwnerId) is not { } owned) return false;
        if (!_flags.IsEnabled(context.OwnerId, FeatureFlagKeys.VideoEditor)) return false;
        if (owned.ProjectId is { } projectId)
        {
            if (_projects.GetById(projectId) is not { } found || found.OwnerId != context.OwnerId) return false;
            scope = VideoEditScope.Of(found);
        }
        else
            scope = VideoEditScope.Of(owned);
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

    // Отказ сервиса с кодом: агенту нужно различать «поставщик недоступен», «много задач» и «плохой вход».
    // Retry — котировка соседа: запустить её может только человек
    private static McpToolCallResult Fail(string? code, string? error, RetryQuote? retry = null) =>
        new(JsonSerializer.Serialize(new
        {
            error = error ?? "Запрос не выполнен",
            code = code ?? VideoEditorErrors.InvalidRequest,
            retry = retry is null ? null : new { retry.Provider, retry.Model, retry.Reason, retry.Quote.Price },
            note = retry is null ? null : "На другого поставщика сервер сам не переходит: предложи этот вариант человеку.",
        }, JsonOpts), IsError: true);

    private static McpToolCallResult FilmFail(string? code, string? error, FilmStateDto? conflict) =>
        new(JsonSerializer.Serialize(new
        {
            error = error ?? "Запрос не выполнен",
            code = code ?? VideoEditorErrors.InvalidRequest,
            state = conflict,
            note = conflict is null ? null : "Человек успел изменить фильм: state — свежее состояние; перечитай и повтори с новой revision.",
        }, JsonOpts), IsError: true);

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    // Целое необязательное: нет ключа — null и ok; ключ не целое — не ok
    private static bool TryInt(JsonObject args, string name, out int? value)
    {
        value = null;
        if (args[name] is null) return true;
        if (NumberOf(args[name]) is not { } d || d != Math.Floor(d) || Math.Abs(d) > int.MaxValue) return false;
        value = (int)d;
        return true;
    }

    private static long? Long(JsonObject args, string name) =>
        NumberOf(args[name]) is { } d && d == Math.Floor(d) ? (long)d : null;

    private static double? Num(JsonObject args, string name) => NumberOf(args[name]);

    // Число любого происхождения: из разобранного JSON и из кода (JsonValue<int>, <double>)
    private static double? NumberOf(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d
            : null;

    private static bool? Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
