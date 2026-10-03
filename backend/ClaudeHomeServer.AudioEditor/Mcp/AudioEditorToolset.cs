using ClaudeHomeServer.Services.ChatContext;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor.Catalog;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.AudioEditor.Mcp;

/// <summary>
/// MCP-сервер модуля «Звук» для агента любого чата владельца — проектного и личного (ADR-021 §5, по
/// образцу ImageEditorToolset). Маршрут — <c>POST /mcp/audio-editor/{sessionId}</c>: хвост несёт чат,
/// по нему тулсет находит владельца, область (проект или личную) и нити звука чата. Ехать ли серверу в
/// ход, решает Main (SessionManager.BuildAudioEditorContext).
///
/// Запуск — тем же путём, что у человека: котировка исполнителя по цепочке «явный аргумент → настройки
/// нити → выбор в полосе режима → умолчание каталога», затем запуск строго по quoteId. Агент без
/// provider/model берёт выбор человека: тулсет их не подставляет, цепочку разворачивает исполнитель.
/// Запуск идёт строго в нить по threadId, а не «по текущему фокусу». Трата — на владельца чата,
/// инициатор — агент. Частные параметры модели (params) проходят только по схеме модели: неизвестный
/// ключ — отказ с именем поля до запуска.
///
/// Сохранить в проект может только человек: такого инструмента у агента нет. Монтаж без ИИ (op trim,
/// gainFade, normalize, mixStems) и склейка (audio_concat) у агента есть — бесплатно, итог новой
/// версией или новой нитью (решение Андрея: «склей эти три реплики» должно работать).
///
/// Сторожа запуска — в CallAsync, не в составе: делегированный и реакционный ход — отказ fail-closed
/// (IDelegatedTurnGate), не больше MaxLaunchesPerTurn платных запусков за ход (счётчик на сессию, сброс
/// по TurnCompleted этой сессии). Монтаж без ИИ и склейка лимит хода не расходуют: денег и GPU они не
/// тратят. Задача живёт отдельно от хода: «Стоп» её не отменяет, отмена — audio_cancel или кнопкой.
///
/// ИНВАРИАНТ состава: tools/list зависит от сессии, флага владельца и настройки инстанса
/// AudioEditor:AgentLaunch — не от хода, фокуса, режима, поставщика и нитей (McpToolsetStabilityTests).
/// Без выбранного звука инструмент отвечает отказом, а не исчезает.
/// </summary>
public sealed partial class AudioEditorToolset : IMcpParameterizedToolset
{
    public const int MaxLaunchesPerTurn = 2;
    public const string AgentLaunchKey = "AudioEditor:AgentLaunch";

    // Потолок исходного звука, который запуск читает в память — как у ручки запуска
    private const long MaxSourceBytes = 200L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<AudioOp> VoiceOps =
        [AudioOp.Speak, AudioOp.DesignVoice, AudioOp.CloneVoice, AudioOp.ConvertVoice, AudioOp.TrainVoice, AudioOp.Dialogue];

    private static readonly HashSet<AudioOp> MusicOps =
        [AudioOp.Song, AudioOp.Cover, AudioOp.Repaint, AudioOp.Outpaint, AudioOp.Extract, AudioOp.Lego, AudioOp.Complete, AudioOp.Sfx];

    // Операции, где текст композера — что озвучить; у остальных он — стиль и описание
    private static readonly HashSet<AudioOp> SpokenText = [AudioOp.Speak, AudioOp.DesignVoice, AudioOp.CloneVoice, AudioOp.Dialogue];

    private readonly IMcpSessionAccessor _sessions;
    private readonly IFeatureFlagGate _flags;
    private readonly IProjectManager _projects;
    private readonly IEnumerable<IAudioEngine> _engines;
    private readonly AudioJobThreads _threads;
    private readonly AudioEditJobService _jobs;
    private readonly AudioPrefsService _prefs;
    private readonly AudioEditWorkspace _workspace;
    private readonly IDelegatedTurnGate? _turnGate;
    private readonly AudioConcatService? _concat;
    private readonly IAudioAgentEdits? _edits;
    private readonly IAudioVoiceLibrary? _library;
    private readonly bool _agentLaunch;

    // Платные запуски агентом в текущем ходу: sessionId → число. Сброс — TurnCompleted этой сессии
    private readonly Dictionary<string, int> _launches = new(StringComparer.Ordinal);
    private readonly Lock _launchGate = new();

    public AudioEditorToolset(
        IMcpSessionAccessor sessions,
        IFeatureFlagGate flags,
        IProjectManager projects,
        IEnumerable<IAudioEngine> engines,
        AudioJobThreads threads,
        AudioEditJobService jobs,
        AudioPrefsService prefs,
        AudioEditWorkspace workspace,
        IDelegatedTurnGate? turnGate = null,
        AudioConcatService? concat = null,
        IAudioAgentEdits? edits = null,
        IAudioVoiceLibrary? library = null,
        ITurnEventBus? events = null,
        IConfiguration? config = null)
    {
        _sessions = sessions;
        _flags = flags;
        _projects = projects;
        _engines = engines;
        _threads = threads;
        _jobs = jobs;
        _prefs = prefs;
        _workspace = workspace;
        _turnGate = turnGate;
        _concat = concat;
        _edits = edits;
        _library = library;
        _agentLaunch = config?.GetValue(AgentLaunchKey, true) ?? true;
        events?.OnNotification<TurnCompleted>(OnTurnCompleted, "AudioEditorToolset.ResetLaunches");
    }

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? ToolsOfInstance : [];

    // Состав инстанса: без запуска агентом остаются чтение, выбор звука, дикторы и предложение текста —
    // всё, что ничего не запускает
    private IReadOnlyList<McpToolSchema> ToolsOfInstance => _agentLaunch
        ? Schemas
        : [.. Schemas.Where(t => t.Name is ToolState or ToolFocus or ToolNew or ToolVoices or ToolSuggestPrompt)];

    private AudioThreadStore Store => _threads.Store;

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
            ToolVoices => await VoicesAsync(arguments, context.OwnerId, scope, ct),
            ToolGenerate => await GenerateAsync(arguments, context.OwnerId, session, scope, ct),
            ToolConcat => await ConcatAsync(arguments, context.OwnerId, session, scope, ct),
            ToolSuggestPrompt => SuggestPrompt(arguments),
            ToolCancel => await CancelAsync(arguments, context.OwnerId, session, scope, ct),
            _ => Deny($"Неизвестный инструмент: {tool}"),
        };
    }

    // ── audio_focus и audio_new ────────────────────────────────────────────────

    private async Task<McpToolCallResult> FocusAsync(JsonObject args, string ownerId, Session session,
        AudioEditScope scope, CancellationToken ct)
    {
        var threadId = Str(args, "threadId");
        var file = Str(args, "file");
        var versionId = Str(args, "versionId");
        if (threadId is not null && file is not null)
            return Deny("Укажи что-то одно: threadId звука этого чата или file — путь звукового файла проекта.");
        if (versionId is not null && threadId is null)
            return Deny("versionId — версия звука threadId: укажи и threadId.");
        // Ранняя проверка, как у картинок: отказ сразу называет версии нити, а не только шлёт в audio_state
        if (versionId is not null
            && Store.Get(ownerId, session.Id).Threads.FirstOrDefault(t => t.Id == threadId) is { } target
            && target.Version(versionId) is null)
            return Deny($"У звука {threadId} нет версии {versionId}. Версии этого звука: "
                + string.Join(", ", target.Versions.Select(v => $"{v.Id} ({AudioThread.Label(v)})")) + ".");

        AudioThreadWrite written;
        if (file is not null)
        {
            // У личной области файлов проекта нет — отказ до обращения к диску
            if (scope.Project is not { } project) return Deny(NoProjectFiles);
            if (ProjectAudioPath(project, file) is not { } path)
                return Deny($"Звуковой файл не найден в проекте: {file}");
            written = _threads.Tracked(ownerId, session.Id, () => Store.Open(ownerId, session.Id, path, null, null), ContextActor.Agent);
            if (written is { Status: AudioThreadWriteStatus.Ok, Existing: false, Thread: { } created })
                await _threads.AnchorAsync(session.Id, created, ct);
        }
        else if (versionId is not null)
            written = _threads.Tracked(ownerId, session.Id,
                () => Store.SetCurrentVersion(ownerId, session.Id, threadId!, versionId, null, focus: true), ContextActor.Agent);
        else
            written = _threads.Tracked(ownerId, session.Id, () => Store.SetFocus(ownerId, session.Id, threadId, null), ContextActor.Agent);

        return written.Status switch
        {
            AudioThreadWriteStatus.Ok => await FocusedAsync(ownerId, scope, session.Id, written.State),
            AudioThreadWriteStatus.ThreadNotFound => Deny($"Звука {threadId} нет в этом чате. Список — audio_state."),
            AudioThreadWriteStatus.VersionNotFound => Deny($"У звука {threadId} нет версии {versionId}. Список версий — audio_state."),
            _ => Deny("Звуки чата как раз меняются — повтори позже."),
        };
    }

    private async Task<McpToolCallResult> NewAsync(JsonObject args, string ownerId, Session session,
        AudioEditScope scope, CancellationToken ct)
    {
        var mode = Str(args, "mode");
        if (mode is not null && !AudioModes.IsValid(mode))
            return Deny($"Неизвестный режим: {mode}. Режимы — voice, music, process.");
        var folder = Normalize(Str(args, "folder") ?? "");
        if (folder.Length > 0)
        {
            // Папка — место сохранения в проект; у личной области проекта нет, черновик человек скачает
            if (scope.Project is not { } project)
                return Deny("В чате вне проекта папок нет: вызови audio_new без folder — человек скачает звук сам.");
            if (ProjectLinkGuard.ResolveInside(project.RootPath, folder) is not { } full || !Directory.Exists(full))
                return Deny($"Папка не найдена в проекте: {folder}");
        }

        var settings = mode is null ? null : _prefs.ForNewThread(ownerId, scope, mode);
        var written = _threads.Tracked(ownerId, session.Id, () => Store.Open(ownerId, session.Id, null, folder, null, settings), ContextActor.Agent);
        if (written is not { Status: AudioThreadWriteStatus.Ok, Thread: { } thread })
            return Deny("Звуки чата как раз меняются — повтори позже.");
        await _threads.AnchorAsync(session.Id, thread, ct);
        return await FocusedAsync(ownerId, scope, session.Id, written.State);
    }

    private async Task<McpToolCallResult> FocusedAsync(string ownerId, AudioEditScope scope, string sessionId,
        AudioThreadsState state)
    {
        await _threads.BroadcastAsync(ownerId, scope.Key, sessionId, state);
        var thread = state.Threads.FirstOrDefault(t => t.Id == state.Focus);
        return Json(new
        {
            focus = state.Focus,
            thread = thread is null ? null : DescribeThread(ownerId, scope, thread),
            humanChoice = HumanChoice(ownerId, scope, thread),
            note = "Человек видит в полосе «Звук», какой звук ты взял в работу, и может снять выбор.",
        });
    }

    private const string NoProjectFiles =
        "В чате вне проекта нет файлов проекта: заведи новый звук (audio_new) или возьми звук этого чата по threadId.";

    // Путь звукового файла проекта от корня через «/»: строго внутри и без ссылки наружу
    private static string? ProjectAudioPath(Project project, string file)
    {
        var rel = Normalize(file);
        if (rel.Length == 0 || ProjectLinkGuard.ResolveInside(project.RootPath, rel) is not { } full || !File.Exists(full))
            return null;
        return AudioVersionFiles.ContentTypeOf(full).StartsWith("audio/", StringComparison.Ordinal)
            ? Path.GetRelativePath(project.RootPath, full).Replace('\\', '/')
            : null;
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').Trim('/');

    // ── audio_voices ───────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> VoicesAsync(JsonObject args, string ownerId, AudioEditScope scope, CancellationToken ct)
    {
        var provider = Str(args, "provider");
        var model = Str(args, "model");
        var language = Str(args, "language");
        var engines = AudioCatalog.Available(_engines).Where(e => e.ScopeRefusal(scope) is null).ToList();
        if (provider is not null)
        {
            engines = [.. engines.Where(e => string.Equals(e.Key, provider, StringComparison.OrdinalIgnoreCase))];
            if (engines.Count == 0)
                return Deny($"Поставщик «{provider}» недоступен в этом чате. Список поставщиков — audio_state.");
        }

        var providers = new List<object>();
        foreach (var engine in engines)
        {
            IReadOnlyList<AudioVoiceInfo>? voices;
            try { voices = await engine.ListVoicesAsync(model, language, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { voices = null; }
            if (voices is { Count: > 0 })
                providers.Add(new { provider = engine.Key, label = engine.Label, voices });
        }
        return Json(new
        {
            providers,
            library = _library?.List(ownerId, scope),
            note = "id диктора передай в voice у audio_generate. Голос, который человек выбрал в полосе «Звук», "
                + "без его просьбы не меняй.",
        });
    }

    // ── audio_generate ─────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> GenerateAsync(JsonObject args, string ownerId, Session session,
        AudioEditScope scope, CancellationToken ct)
    {
        // threadId обязателен: человек мог сменить звук посреди хода, и запуск «по текущему фокусу» ушёл
        // бы не туда. Отказ — до гейта и лимита хода
        if (Str(args, "threadId") is not { } threadId)
            return Deny("Не указан threadId: запуск идёт только в конкретный звук чата. "
                + "Возьми id из audio_state (или возьми звук в работу через audio_focus / audio_new).");

        // Запускать может только ход, который видит человек
        var denied = _turnGate is null
            ? "Запуск недоступен: сервер не проверил ход — отказ по построению."
            : _turnGate.Deny(ownerId, session.Id, "Запуск операции со звуком");
        if (denied is not null) return Deny(denied);

        if (Store.Get(ownerId, session.Id).Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
            return Deny($"Звука {threadId} нет в этом чате. Список — audio_state.");
        var versionId = Str(args, "versionId");
        var version = versionId is null ? thread.CurrentVersion : thread.Version(versionId);
        if (versionId is not null && version is null)
            return Deny($"У звука {threadId} нет версии {versionId}. Список версий — audio_state.");

        AudioOp? op = null;
        if (Str(args, "op") is { } opName)
        {
            if (!TryParseOp(opName, out var parsed))
                return Deny($"Неизвестная операция: {opName}. Операции — {string.Join(", ", Ops)}.");
            if (parsed == AudioOp.Concat)
                return Deny("Склейка — отдельный инструмент audio_concat.");
            op = parsed;
        }
        var mode = Str(args, "mode");
        if (mode is not null && !AudioModes.IsValid(mode))
            return Deny($"Неизвестный режим: {mode}. Режимы — voice, music, process.");
        if (args["params"] is { } rawParams && rawParams is not JsonObject)
            return Deny("params — объект с частными параметрами модели.");
        if (Range(args).Error is { } rangeError) return Deny(rangeError);

        if (op is { } noAi && AudioOps.IsNoAi(noAi))
            return await EditAsync(args, ownerId, session, scope, thread, version, noAi, ct);

        if (!TryReserveLaunch(session.Id))
            return Deny($"За один ход можно запустить не больше {MaxLaunchesPerTurn} операций со звуком. "
                + "Покажи человеку, что уже получилось, и дождись его ответа.");
        var launched = false;
        try
        {
            var result = await LaunchAsync(args, ownerId, session, scope, thread, version, op, mode, ct);
            launched = !result.IsError;
            return result;
        }
        finally
        {
            if (!launched) ReleaseLaunch(session.Id);
        }
    }

    private async Task<McpToolCallResult> LaunchAsync(JsonObject args, string ownerId, Session session,
        AudioEditScope scope, AudioThread thread, AudioThreadVersion? version, AudioOp? op, string? mode, CancellationToken ct)
    {
        // Чего агент не передал — того тулсет не подставляет: provider, model, op и count разворачивает
        // исполнитель по цепочке «настройки нити → выбор в полосе режима → умолчание каталога»
        mode ??= op is { } known ? ModeOf(known) : thread.Settings?.Mode ?? AudioModes.Voice;
        var text = Str(args, "text");
        var lyrics = Str(args, "lyrics");
        var duration = Int(args, "durationSeconds");
        // Текст едет в поле операции: речь — Text, остальное — Prompt. Котировка обязана нести ровно то, что
        // уйдёт в запуск (иначе отказ «Котировка не соответствует запросу»); op без явного — как речь,
        // промах уточняет повторная котировка ниже
        var spokenGuess = op is not { } guessed || SpokenText.Contains(guessed);
        var quoteRequest = new AudioQuoteRequest(mode,
            Operation: op is { } o ? AudioEditJobService.OpName(o) : null,
            Provider: Str(args, "provider"),
            Model: Str(args, "model"),
            Count: Int(args, "count"),
            SessionId: session.Id,
            ThreadId: thread.Id,
            Text: spokenGuess ? text : null,
            DurationSec: duration,
            Prompt: spokenGuess ? null : text,
            Lyrics: lyrics);
        var quote = await _jobs.QuoteAsync(ownerId, scope, quoteRequest, ct);
        if (quote.Value is not { } q) return Fail(quote.ErrorCode, quote.Error);

        // Частные параметры — по схеме модели, которую выбрала цепочка; неизвестный ключ — отказ до запуска
        if (_engines.FirstOrDefault(e => string.Equals(e.Key, q.Provider, StringComparison.OrdinalIgnoreCase)) is not { } engine
            || engine.Models.FirstOrDefault(m => string.Equals(m.Id, q.Model, StringComparison.OrdinalIgnoreCase)) is not { } model)
            return Fail(AudioEditErrorCodes.ProviderUnavailable, $"Поставщик «{q.Provider}» больше недоступен");
        var fields = args["params"] is JsonObject given ? given.DeepClone().AsObject() : new JsonObject();
        var allowed = engine.ParamNames(model, q.Op) ?? new HashSet<string>();
        if (fields.Select(p => p.Key).FirstOrDefault(k => !allowed.Contains(k)) is { } unknown)
            return Deny($"params: модель {q.Model} (поставщик {q.Provider}) не знает параметра «{unknown}». "
                + (allowed.Count == 0 ? "Частных параметров у неё нет." : "Допустимые: " + string.Join(", ", allowed.Order()) + "."));

        string? libraryVoice = null;
        if (Str(args, "voice") is { } wanted
            && _library?.List(ownerId, scope).FirstOrDefault(v => string.Equals(v.Slug, wanted, StringComparison.OrdinalIgnoreCase)) is { } fromLibrary)
        {
            // Голос из библиотеки разворачивает поставщик при запуске (ADR-021 §5); протухший клон MiniMax —
            // отказ, пересоздаёт его только человек кнопкой с ценой
            libraryVoice = AudioVoiceRefs.Prefix + fromLibrary.Slug;
        }
        else if (Str(args, "voice") is { } voice)
        {
            if (engine.VoiceParams(model, q.Op, voice) is not { } voiceFields)
                return Deny($"У модели {q.Model} в этой операции нет готовых дикторов — voice задать нельзя.");
            foreach (var (key, value) in voiceFields)
                if (!fields.ContainsKey(key)) fields[key] = value?.DeepClone();
        }

        // Параметры агента едут в котировку на той же паре: цена и проверка входа — с ними
        var final = q;
        var spoken = SpokenText.Contains(q.Op);
        if (fields.Count > 0 || spoken != spokenGuess)
        {
            var pinned = await _jobs.QuoteAsync(ownerId, scope, quoteRequest with
            {
                Operation = AudioEditJobService.OpName(q.Op),
                Provider = q.Provider,
                Model = q.Model,
                Count = q.Count,
                Fields = fields.Count > 0 ? fields : quoteRequest.Fields,
                Text = spoken ? text : null,
                Prompt = spoken ? null : text,
            }, ct);
            if (pinned.Value is not { } p) return Fail(pinned.ErrorCode, pinned.Error);
            final = p;
        }

        AudioBytes? source = null;
        if (AudioEditorEndpoints.NeedsSource.Contains(final.Op))
        {
            if (await SourceAsync(ownerId, scope, version) is not { } bytes)
                return Deny("Для этой операции нужен звук: возьми в работу файл или версию со звуком.");
            source = bytes;
        }

        var range = Range(args);
        var input = new AudioJobInput(final.QuoteId,
            SessionId: session.Id,
            ThreadId: thread.Id,
            BaseVersionId: version?.Id,
            Initiator: AudioEditInitiator.Agent,
            Text: spoken ? text : null,
            Prompt: spoken ? null : text,
            Lyrics: lyrics,
            Language: Str(args, "language"),
            DurationSec: duration,
            StartSec: range.Start,
            EndSec: range.End,
            Source: source,
            Voice: libraryVoice);
        var started = await _jobs.StartAsync(ownerId, scope, input, ct);
        if (started.Value is not { } created)
            return Fail(started.ErrorCode, started.Recreate is null ? started.Error
                : started.Error + ". Пересоздать клон может только человек — кнопкой «Пересоздать» у голоса в «Голосах».");

        return Json(new
        {
            jobId = created.JobId,
            threadId = thread.Id,
            baseVersion = version is null ? null : new { versionId = version.Id, label = AudioThread.Label(version) },
            quote = new { final.Provider, final.Model, op = AudioEditJobService.OpName(final.Op), final.Count, final.Price, final.License },
            note = "Операция идёт отдельно от хода: остановка разговора её не отменяет, отмена — audio_cancel. "
                + "Каждый вариант станет новой версией звука; "
                + (scope.IsPersonal ? "скачать её может только человек." : "сохранить в проект может только человек."),
        });
    }

    // Монтаж без ИИ: бесплатно и без GPU — лимит хода не расходует; реализация — AudioAgentEdits
    private async Task<McpToolCallResult> EditAsync(JsonObject args, string ownerId, Session session, AudioEditScope scope,
        AudioThread thread, AudioThreadVersion? version, AudioOp op, CancellationToken ct)
    {
        if (_edits is null)
            return Deny("Монтаж без ИИ на этом сервере ещё не подключён — попроси человека сделать это в полосе «Звук».");
        if (version is null) return Deny("У этого звука ещё нет версии со звуком — монтировать нечего.");
        var range = Range(args);
        var edited = await _edits.ApplyAsync(ownerId, scope, session.Id, thread.Id, version.Id, op, range.Start, range.End,
            args["params"] as JsonObject, ct);
        if (edited.Value is not { } value) return Fail(edited.ErrorCode, edited.Error);
        return Json(new
        {
            value.ThreadId,
            value.VersionId,
            note = "Монтаж без ИИ завёл новую версию звука. " + (scope.IsPersonal
                ? "Скачать её может только человек."
                : "Сохранить в проект может только человек."),
        });
    }

    // Главный файл версии-основы: исходник — файл проекта, версия запуска — файл рабочей папки задачи
    private async Task<AudioBytes?> SourceAsync(string ownerId, AudioEditScope scope, AudioThreadVersion? version)
    {
        if (version?.File(AudioFileRoles.Main) is not { } main) return null;
        var path = AudioVersionFiles.Resolve(_workspace, ownerId, scope, version, main);
        if (path is null || !File.Exists(path) || new FileInfo(path).Length > MaxSourceBytes) return null;
        return new AudioBytes(await File.ReadAllBytesAsync(path), AudioVersionFiles.ContentTypeOf(path));
    }

    private static string ModeOf(AudioOp op) =>
        VoiceOps.Contains(op) ? AudioModes.Voice : MusicOps.Contains(op) ? AudioModes.Music : AudioModes.Process;

    private static (double? Start, double? End, string? Error) Range(JsonObject args)
    {
        if (args["range"] is null) return (null, null, null);
        if (args["range"] is not JsonObject range || Num(range, "start") is not { } start || Num(range, "end") is not { } end)
            return (null, null, "range — объект { start, end } в секундах.");
        return start < 0 || end <= start ? (null, null, "range: начало не меньше нуля и раньше конца.") : (start, end, null);
    }

    // ── audio_concat ───────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> ConcatAsync(JsonObject args, string ownerId, Session session,
        AudioEditScope scope, CancellationToken ct)
    {
        var denied = _turnGate is null
            ? "Склейка недоступна: сервер не проверил ход — отказ по построению."
            : _turnGate.Deny(ownerId, session.Id, "Склейка звука");
        if (denied is not null) return Deny(denied);
        if (_concat is null) return Deny(AudioConcatService.DspUnavailableText);
        if (args["pieces"] is not JsonArray array)
            return Deny("pieces — список кусков: у каждого threadId (и versionId) или file.");

        var pieces = new List<AudioConcatPiece>();
        foreach (var item in array)
        {
            if (item is not JsonObject piece) return Deny("Кусок склейки — объект с threadId или file.");
            pieces.Add(new AudioConcatPiece(Str(piece, "threadId"), Str(piece, "versionId"), Str(piece, "file")));
        }

        AudioJoint? joint = null;
        if (args["joint"] is JsonObject j)
        {
            var seconds = Num(j, "seconds") ?? 0;
            joint = Str(j, "kind") switch
            {
                "butt" => AudioJoint.Butt,
                "pause" => AudioJoint.Pause(seconds),
                "crossfade" => AudioJoint.Crossfade(seconds),
                _ => null,
            };
            if (joint is null) return Deny("joint.kind — butt, pause или crossfade.");
        }
        var format = AudioFormat.Wav;
        if (Str(args, "format") is { } f && !Enum.TryParse(f, ignoreCase: true, out format))
            return Deny("format — wav, mp3, flac или ogg.");

        var result = await _concat.ConcatAsync(ownerId, scope, new AudioConcatInput(session.Id, pieces, joint,
            NormalizeLoudness: Bool(args, "normalizeLoudness") ?? true,
            Name: Str(args, "name"),
            Format: format,
            Folder: Str(args, "folder"),
            Initiator: AudioEditInitiator.Agent), ct);
        if (result.Value is not { } done) return Fail(result.ErrorCode, result.Error);
        return Json(new
        {
            done.ThreadId,
            done.VersionId,
            done.Name,
            note = "Склейка — новый звук в работе. " + (scope.IsPersonal
                ? "Скачать его может только человек."
                : "Сохранить в проект может только человек."),
        });
    }

    // ── audio_cancel ───────────────────────────────────────────────────────────

    private async Task<McpToolCallResult> CancelAsync(JsonObject args, string ownerId, Session session,
        AudioEditScope scope, CancellationToken ct)
    {
        var jobId = Str(args, "jobId") ?? "";
        // Задача другого чата (или чужая) неотличима от несуществующей
        var job = _jobs.Get(ownerId, scope.Key, jobId);
        if (job is null || job.ChatSessionId != session.Id)
            return Deny("Задача не найдена.");
        var cancelled = await _jobs.CancelAsync(ownerId, scope.Key, jobId, ct);
        return cancelled is null
            ? Deny("Задача не найдена.")
            : Json(new { cancelled.JobId, cancelled.Status, cancelled.Charged, variants = cancelled.Variants.Count });
    }

    // ── audio_suggest_prompt и audio_state ─────────────────────────────────────

    private static McpToolCallResult SuggestPrompt(JsonObject args)
    {
        var prompt = Str(args, "prompt");
        if (prompt is null) return Deny("Пустой текст: предложить нечего.");
        return Json(new
        {
            prompt,
            mode = Str(args, "mode"),
            model = Str(args, "model"),
            note = "Предложение показано человеку; ничего не запущено и не изменено.",
        });
    }

    private object DescribeState(string ownerId, Session session, AudioEditScope scope)
    {
        var state = Store.Get(ownerId, session.Id);
        return new
        {
            focus = state.Focus,
            threads = state.Threads.Select(t => DescribeThread(ownerId, scope, t)),
            prefs = new
            {
                voice = _prefs.Get(ownerId, scope, AudioModes.Voice),
                music = _prefs.Get(ownerId, scope, AudioModes.Music),
                process = _prefs.Get(ownerId, scope, AudioModes.Process),
            },
            catalog = AudioCatalogView.Build(_engines, scope, _jobs.PrefersLocal(ownerId)),
            agentLaunch = _agentLaunch,
            humanChoice = HumanChoice(ownerId, scope, state.Threads.FirstOrDefault(t => t.Id == state.Focus)),
            note = (state.Threads.Count, scope.IsPersonal) switch
            {
                (0, true) => "В этом чате ещё нет звуков. Заведи новый (audio_new): это чат вне проекта, файлов проекта тут нет.",
                (0, false) => "В этом чате ещё нет звуков. Возьми звуковой файл проекта в работу (audio_focus с file) "
                    + "или заведи новый (audio_new).",
                (_, true) => "Каждый вариант запуска и каждый монтаж — версия звука. Скачать звук может только человек.",
                _ => "Каждый вариант запуска и каждый монтаж — версия звука. Сохранить в проект может только человек.",
            },
        };
    }

    private object DescribeThread(string ownerId, AudioEditScope scope, AudioThread t) => new
    {
        threadId = t.Id,
        file = t.File,
        name = AudioJobThreads.Name(t),
        draft = t.File is { Length: > 0 } ? null : new
        {
            folder = t.DraftFolder ?? "",
            note = scope.IsPersonal
                ? "Звука ещё нет: это новый звук, человек скачает его сам."
                : "Звука ещё нет: это новый звук, человек сохранит его в проект.",
        },
        currentVersionId = t.CurrentVersionId,
        versions = t.Versions.Select(v => new
        {
            versionId = v.Id,
            label = AudioThread.Label(v),
            current = v.Id == t.CurrentVersionId,
            from = t.Version(v.BaseVersionId) is { } b ? AudioThread.Label(b) : null,
            files = v.Files.Select(f => f.Role),
            license = v.License,
            prompt = v.JobId is { } job ? t.Launches.FirstOrDefault(l => l.JobId == job)?.Prompt : null,
        }),
        running = t.Launches.Where(l => l.Status == AudioThreadLaunchStatus.Running).Select(l =>
            _jobs.Get(ownerId, scope.Key, l.JobId) is { } j
                ? new { j.JobId, j.Status, j.Provider, j.Model, op = AudioEditJobService.OpName(j.Op), j.Count, l.Initiator }
                : new { l.JobId, Status = AudioEditJobStatus.Queued, Provider = "", Model = "", op = "", Count = 0, l.Initiator }),
        settings = t.Settings,
    };

    // Выбор человека для звука в работе: что возьмёт audio_generate без provider/model/op/count
    private object HumanChoice(string ownerId, AudioEditScope scope, AudioThread? thread)
    {
        var mode = thread?.Settings?.Mode ?? AudioModes.Voice;
        var chain = _prefs.Resolve(ownerId, scope, mode, thread?.Settings, AudioEditJobService.CatalogDefault(mode));
        return new
        {
            mode,
            chain.Operation,
            provider = chain.Provider ?? AudioCatalog.AutoModelId,
            chain.Model,
            chain.Count,
            rule = "Это выбор человека: настройки звука, затем полоса «Звук», затем умолчание. Не передавай "
                + "provider, model, op и count без его просьбы — audio_generate возьмёт выбор сам.",
        };
    }

    private static bool TryParseOp(string name, out AudioOp op)
    {
        op = default;
        return !int.TryParse(name, out _) && Enum.TryParse(name.Trim(), ignoreCase: true, out op) && Enum.IsDefined(op);
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

    // ── Маршрут: /mcp/audio-editor/{sessionId} ─────────────────────────────────

    // Чат владельца токена при включённом флаге: у чата проекта — его проект (свой), у личного —
    // личная область. Любой отказ одним текстом: чужой чат неотличим от несуществующего, состав пустой
    private bool TryResolve(McpToolCallContext context, out Session session, out AudioEditScope scope, out string error)
    {
        session = null!;
        scope = null!;
        error = "Чат не найден — инструменты звука недоступны.";
        if (!TryParseRoute(context.RouteTail, out var sessionId)) return false;
        if (_sessions.GetOwned(sessionId, context.OwnerId) is not { } owned) return false;
        if (!_flags.IsEnabled(context.OwnerId, FeatureFlagKeys.AudioEditor)) return false;
        if (owned.ProjectId is { } projectId)
        {
            if (_projects.GetById(projectId) is not { } found || found.OwnerId != context.OwnerId) return false;
            scope = AudioEditScope.Of(found);
        }
        else
            scope = AudioEditScope.Of(owned);
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

    // Отказ исполнителя с кодом: агенту нужно различать «поставщик недоступен», «много задач» и «плохой вход»
    private static McpToolCallResult Fail(string? code, string? error) =>
        new(JsonSerializer.Serialize(new
        {
            error = error ?? "Запуск не выполнен",
            code = code ?? AudioEditErrorCodes.InvalidRequest,
        }, JsonOpts), IsError: true);

    private static string? Str(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static int? Int(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    // Число любого происхождения: из разобранного JSON и из кода (JsonValue<int>, <double>)
    internal static double? Num(JsonObject args, string name) =>
        args[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    private static bool? Bool(JsonObject args, string name) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
