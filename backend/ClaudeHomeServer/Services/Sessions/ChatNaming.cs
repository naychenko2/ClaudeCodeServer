using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using Microsoft.Extensions.Logging;

// Тип результата пакетного прогона значков — часть контракта фасада (его читает контроллер
// и тесты), объявлен вложенным в SessionManager; здесь обращение через alias, чтобы тела
// остались дословными (тот же приём, что с QueuedMessage в PendingQueueService).
using IconBatchResult = ClaudeHomeServer.Services.SessionManager.IconBatchResult;

namespace ClaudeHomeServer.Services.Sessions;

// Именование чата (S3-3, docs/research/session-manager-split-2026-11.md §1): авто-заголовок
// из первого сообщения (с темой из обвязки командных механик), его уточнение «дешевым»
// раннером, явное переименование по текущей переписке (AI-хаб) и значки-иконки тем
// (одиночный и пакетный прогон). Вынесено из SessionManager; фасад держит тонкие обёртки
// с прежними сигнатурами (MakeChatTitle зовётся из создания и приёма хода, которые ещё
// живут в фасаде).
// Класс public ТОЛЬКО потому, что он параметр публичного конструктора SessionManager (CS0051);
// обход фасада закрывает сторож формы ядра (правило 4).
// Зависимости (7): store, history, costs, cheap, config, projects, broadcaster. В таблице
// S3-0 их четыре (store, history, cheap, config): Retitle/SetChatIcon строят транскрипт по
// той же склейке чтения ленты, что фасад GetHistoryAsync (история + досчёт цен — без costs
// поведение изменилось бы: досчёт не случался бы при явном переименовании), пакетный прогон
// значков фильтрует чаты по видимости через projects, а рассылка переименования идёт прямо
// в broadcaster (у стора нет равносоставного «rename»-рассыла).
public sealed class ChatNaming
{
    private readonly SessionStore _store;
    private readonly SessionHistory _history;
    private readonly TurnCostLedger _costs;
    private readonly Llm.ICheapTextRunner? _cheap;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly Composition.ISessionBroadcaster _broadcaster;
    private readonly ILogger<ChatNaming> _log;

    public ChatNaming(
        SessionStore store,
        SessionHistory history,
        TurnCostLedger costs,
        Llm.ICheapTextRunner? cheap,
        IConfiguration config,
        ProjectManager projects,
        Composition.ISessionBroadcaster broadcaster,
        ILogger<ChatNaming> log)
    {
        _store = store;
        _history = history;
        _costs = costs;
        _cheap = cheap;
        _config = config;
        _projects = projects;
        _broadcaster = broadcaster;
        _log = log;
    }

    // Заголовок чата из первого сообщения: первая строка, обрезанная до разумной длины.
    // Ход командной механики (/team-implement {...}, /oh-my-claudecode:ralplan "тема"…) — не
    // текст для человека, поэтому сначала пробуем вытащить тему из обвязки (см.
    // ExtractTeamMechanicTopic), а к сырому тексту падаем только если распознать не удалось.
    internal static string MakeChatTitle(string text)
    {
        var topic = ExtractTeamMechanicTopic(text);
        var t = (string.IsNullOrWhiteSpace(topic) ? text : topic).Trim();
        var nl = t.IndexOfAny(['\n', '\r']);
        if (nl >= 0) t = t[..nl].Trim();
        const int max = 48;
        if (t.Length > max) t = string.Concat(t.AsSpan(0, max).TrimEnd(), "…");
        return t;
    }

    // Слаги командных механик с JSON-аргументами — тема лежит в одном из полей ниже
    // (порядок — приоритет: первое непустое). Зеркалит describeTeamTurn во frontend/teamMechanics.ts.
    private static readonly string[] JsonMechanicSlugs =
        ["/panel-of-experts", "/review-consilium", "/red-team", "/team-implement"];
    private static readonly string[] JsonTopicKeys = ["task", "topic", "target", "brief"];

    // Слаги строковых механик — тема в кавычках (см. quoteTopic во frontend/teamMechanics.ts:
    // внутренние " заменены на «», поэтому кавычки темы — первая и единственная пара).
    private static readonly string[] QuotedTopicSlugs =
    [
        "/oh-my-claudecode:ralplan", "/oh-my-claudecode:deep-interview",
        "/oh-my-claudecode:autopilot", "/oh-my-claudecode:trace", "/oh-my-claudecode:sciomc",
    ];
    private const string UltraqaSlug = "/oh-my-claudecode:ultraqa";
    private static readonly Regex QuotedTopicRegex = new("\"([^\"]*)\"", RegexOptions.Compiled);

    // Тема хода командной механики или null, если текст — не вызов известной механики
    // (обычное сообщение, режим «Командная реализация» без обвязки — тема уходит как есть).
    private static string? ExtractTeamMechanicTopic(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '/') return null;

        foreach (var slug in JsonMechanicSlugs)
        {
            if (!trimmed.StartsWith(slug, StringComparison.Ordinal)) continue;
            var braceIdx = trimmed.IndexOf('{');
            if (braceIdx < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(trimmed[braceIdx..]);
                foreach (var key in JsonTopicKeys)
                {
                    if (doc.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch (JsonException) { /* битый JSON — падаем к обрезке сырого текста */ }
            return null;
        }

        foreach (var slug in QuotedTopicSlugs)
        {
            if (!trimmed.StartsWith(slug, StringComparison.Ordinal)) continue;
            var m = QuotedTopicRegex.Match(trimmed);
            return m.Success ? m.Groups[1].Value : null;
        }

        if (trimmed.StartsWith(UltraqaSlug, StringComparison.Ordinal))
        {
            var rest = trimmed[UltraqaSlug.Length..].TrimStart();
            if (rest.StartsWith("--", StringComparison.Ordinal))
            {
                var spaceIdx = rest.IndexOf(' ');
                rest = spaceIdx >= 0 ? rest[(spaceIdx + 1)..] : "";
            }
            return rest.Trim();
        }

        return null;
    }

    // Чтение ленты — та же склейка, что у фасада GetHistoryAsync (история + досчёт цен,
    // S3-0 §1 «цены fal/glif»): транскрипт для переименования строится по полной ленте.
    private async Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
    {
        var list = await _history.GetHistoryAsync(sessionId);
        // Догоняем стоимость старых fal-генераций, у которых её ещё нет (фоном, дедуп внутри)
        _costs.BackfillFalCosts(sessionId, list);
        // Догоняем учёт старых glif-генераций, у которых ещё нет glif_cost
        _costs.BackfillGlifCosts(sessionId, list);
        return list;
    }

    // Уточнение авто-заголовка чата (действие chat-title) по маршруту, назначенному месту в
    // «Поставщиках моделей» (локаль/direct-модель/слот — решает CheapTextRunner.RunAsync).
    // Best-effort: молчим при любой проблеме, не трогаем имя, переименованное вручную.
    internal async Task RefineChatTitleAsync(string sessionId, string firstMessage, string expectedTitle, string? ownerId)
    {
        if (_cheap is null) return;
        try
        {
            var prompt =
                "Придумай короткий заголовок (3-6 слов, по-русски, без кавычек и точки в конце) для чата " +
                "по первому сообщению пользователя. " + Llm.TitleExtraction.JsonHintWithIcon + "\n\n" +
                (firstMessage.Length > 1500 ? firstMessage[..1500] : firstMessage);
            var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
                ownerId: ownerId, jsonFormat: Llm.TitleExtraction.SchemaWithIcon);
            var line = Llm.TitleExtraction.Extract(raw);
            if (line is null || line.Length > 80) return;
            // Значок темы — имя lucide-компонента (PascalCase): имя остаётся чистым текстом,
            // иконку рисует фронт по icons[iconName]
            var iconName = Llm.TitleExtraction.ExtractIconName(raw);

            if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return;
            // Пользователь мог переименовать вручную, пока модель думала — тогда не трогаем
            if (entry.Info.Name != expectedTitle) return;
            entry.Info.Name = line;
            if (iconName is not null) entry.Info.Topic = iconName;
            entry.Info.UpdatedAt = DateTime.UtcNow;
            _store.SaveSessions();
            await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Уточнение заголовка чата {Session}", sessionId); }
    }

    // Обновление названия чата по ТЕКУЩЕЙ переписке — явное действие пользователя (AI-хаб).
    // В отличие от авто-заголовка (только первое сообщение, ставится один раз) — читает весь
    // транскрипт и ВСЕГДА перезаписывает имя. Раннер как у «Итога сессии»: локаль или claude-фолбэк.
    public async Task<Session?> RetitleAsync(string userId, string sessionId, CancellationToken ct)
    {
        // S1: участнику общего проекта отказ (GetOwned → 404): LLM-расход платит арендатор
        var session = _store.GetOwned(sessionId, userId);
        if (session is null) return null;
        if (_cheap is null) throw new InvalidOperationException("ИИ недоступен");

        var history = await GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, 8000);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("В чате ещё нет сообщений");

        var prompt =
            "Ниже — переписка чата. Придумай короткое название (3-6 слов, по-русски, без кавычек и точки в конце), " +
            "отражающее суть текущего разговора. " + Llm.TitleExtraction.JsonHint + "\n\n" + transcript;
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatRetitle, prompt,
            _config["Notes:AiModel"] ?? "haiku", userId, jsonFormat: Llm.TitleExtraction.Schema, ct: ct);
        var line = Llm.TitleExtraction.Extract(raw);
        if (line is null || line.Length > 80)
            throw new InvalidOperationException("Модель вернула пустое название");

        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Name = line;
        entry.Info.NameLocked = true; // явное действие пользователя — авто больше не трогает
        // Архивный чат из архива не выводим: «Обновить название» доступен и в разделе
        // «Архив», а правка названия — не активность разговора (признак архива производный)
        if (!entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        _store.SaveSessions();
        await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        return entry.Info;
    }

    // Подобрать значок-иконку ОДНОМУ чату по переписке. Имя не трогает и NameLocked не ставит —
    // в отличие от RetitleAsync (тот переименовывает). null — значок поставить не вышло
    // (нет переписки, модель не дала имя, чат исчез). Зовётся и пакетным прогоном.
    public async Task<Session?> SetChatIconAsync(string userId, string sessionId, CancellationToken ct)
    {
        // S1: участнику общего проекта отказ (GetOwned → null): LLM-расход платит арендатор
        var session = _store.GetOwned(sessionId, userId);
        if (session is null) return null;
        if (_cheap is null) throw new InvalidOperationException("ИИ недоступен");

        // Транскрипт короткий: для темы разговора сути хватает, лишние токены ни к чему
        var history = await GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, 1500);
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var prompt = Llm.TitleExtraction.IconHint + "\n\n" + transcript;
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
            ownerId: userId, jsonFormat: Llm.TitleExtraction.SchemaIcon, ct: ct);
        var iconName = Llm.TitleExtraction.ExtractIconName(raw);
        if (iconName is null)
        {
            // Диагностика: модель не дала валидное PascalCase-имя. Логируем сырой ответ,
            // чтобы понять — пустой {} (отступила), не-PascalCase (опечатка/пробел) или мусор.
            // Обрезаем: модели иногда шлют длинные рассуждения вслух
            _log.LogInformation("Значок чата {Session} («{Name}»): модель не дала имя. Ответ: {Raw}",
                sessionId, session.Name, raw is null ? "<null>" : (raw.Length > 300 ? raw[..300] + "…" : raw));
            return null;
        }

        if (!_store.Sessions.TryGetValue(sessionId, out var entry)) return null;
        // Повторная проверка: пока модель думала, значок мог поставить авто-заголовок нового
        // чата — не перезаписываем то, что уже стоит
        if (!string.IsNullOrEmpty(entry.Info.Topic)) return entry.Info;
        entry.Info.Topic = iconName;
        // Архивный чат из архива не выводим — зеркало гейта в RetitleAsync и предфильтра
        // пакетного прогона (SetChatIconsAsync): значок — не активность разговора
        if (!entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        _store.SaveSessions();
        // Имя не менялось — шлём его же: событие переносит и значок, отдельного не заводим
        await BroadcastChatRenamedAsync(sessionId, entry.Info, entry.Info.Name ?? "");
        return entry.Info;
    }

    // Подобрать значки чатам без него в рамках проекта (projectId) или, если проект не задан,
    // по всем чатам владельца. Действие AI-палитры «Проставить значки тем» в разделе проекта —
    // разовый проход. Чаты со значком отсеиваются ДО вызова модели (экономия вызовов), поэтому
    // в processed попадают только реально размеченные этим прогоном. Каждый оставшийся чат —
    // отдельный вызов модели: на десятках чатов это десятки секунд.
    public async Task<IconBatchResult> SetChatIconsAsync(string userId, CancellationToken ct, string? projectId = null)
    {
        var all = _store.Sessions.Values
            .Where(e => _store.ResolveOwnerId(e.Info) == userId && (projectId is null || e.Info.ProjectId == projectId))
            .Select(e => e.Info)
            // S2d-3f (дефект 183333fb): чужой приватный чат не входит в пакетный прогон — та же
            // видимость, что у GetOwnedVisible (S2d-3a): его транскрипт не уходит в модель
            // арендатора, карточка не пишется, UpdatedAt не двигается. Свой приватный и общий — как раньше.
            .Where(s => Access.SessionAccess.CanSee(s, s.ProjectId is not null ? _projects.GetById(s.ProjectId) : null, userId))
            .ToList();
        // Предфильтр: у кого значок уже есть — сразу в пропущенные, модель не зовём.
        // Архивные — туда же: у старых чатов Topic как раз пуст (значки появились позже),
        // и один клик «Проставить значки» выводил бы из архива ВСЕ старые чаты (UpdatedAt
        // двигается в SetChatIconAsync) и заказывал сотни вызовов модели
        var pending = all
            .Where(s => string.IsNullOrEmpty(s.Topic) && !s.IsArchived)
            .Select(s => s.Id)
            .ToList();
        var processed = 0;
        var skipped = all.Count - pending.Count;
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var updated = await SetChatIconAsync(userId, id, ct);
                if (updated is not null) processed++; else skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "Определение значка чата {Session}", id); skipped++; }
        }
        return new IconBatchResult(processed, skipped);
    }

    // Уведомить клиентов об авто-переименовании чата (адресация как у BroadcastChatDeletedAsync)
    private async Task BroadcastChatRenamedAsync(string sessionId, Session info, string name)
    {
        var msg = new ChatRenamedMessage(name, info.Topic) with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, msg) };
        tasks.Add(_broadcaster.ToChatAudience(info, msg));
        await Task.WhenAll(tasks);
    }
}
