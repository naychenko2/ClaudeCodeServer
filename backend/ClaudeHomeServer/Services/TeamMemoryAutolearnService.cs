using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Memory;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Авто-память КОМАНДЫ проекта (флаг team-memory-autolearn): по завершении хода в ЛЮБОЙ проектной
// сессии — обычной, групповом чате или совещании — one-shot вызовом Claude вычленяет из диалога
// ОБЪЕКТИВНОЕ проектное знание (решения/договорённости/факты/термины, полезные всей команде) и
// складывает в общую память команды (TeamMemoryService). Разграничение с личной памятью персоны:
// сюда идёт только «про проект», НЕ «про меня/пользователя». Подписка на SessionManager.OnSessionMessage,
// тяжёлая работа — вне пайплайна хода. Эталон — PersonaMemoryAutolearnService.
public sealed class TeamMemoryAutolearnService : IHostedService
{
    private const int TranscriptBudget = 8_000;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SessionManager _sessions;
    private readonly ProjectManager _projects;
    private readonly TeamMemoryService _memory;
    private readonly TeamMemoryConsolidationService _consolidation;
    private readonly Llm.ICheapTextRunner _cheap;
    private readonly FeatureFlagService _flags;
    private readonly IConfiguration _config;
    private readonly ILogger<TeamMemoryAutolearnService> _log;
    private readonly IHubContext<SessionHub> _hub;
    private readonly ProjectEventLogService? _events;

    // Длина транскрипта на момент последнего извлечения по сессии — гасит повторную работу на
    // каждый ResultMessage (в групповом чате/совещании каждый ход спикера = отдельный Result).
    private readonly ConcurrentDictionary<string, int> _lastLen = new();

    public TeamMemoryAutolearnService(SessionManager sessions, ProjectManager projects,
        TeamMemoryService memory, TeamMemoryConsolidationService consolidation,
        Llm.ICheapTextRunner cheap, FeatureFlagService flags,
        IConfiguration config, ILogger<TeamMemoryAutolearnService> log, IHubContext<SessionHub> hub,
        ProjectEventLogService? events = null)
    {
        _sessions = sessions;
        _projects = projects;
        _memory = memory;
        _consolidation = consolidation;
        _cheap = cheap;
        _flags = flags;
        _config = config;
        _log = log;
        _hub = hub;
        _events = events;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage += OnSessionMessageAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _sessions.OnSessionMessage -= OnSessionMessageAsync;
        return Task.CompletedTask;
    }

    private Task OnSessionMessageAsync(Session session, ServerMessage msg)
    {
        // Только завершение хода в ПРОЕКТНОЙ сессии (обычной/групповой/совещании — все с ProjectId)
        if (msg is not ResultMessage || string.IsNullOrEmpty(session.ProjectId)) return Task.CompletedTask;

        // Владелец проектной сессии резолвится через проект (Session.OwnerId у проектных сессий = null)
        var ownerId = _projects.GetById(session.ProjectId)?.OwnerId;
        if (string.IsNullOrEmpty(ownerId)) return Task.CompletedTask;

        // Гейт флага — внутри хука (переключается без рестарта, как recall-провайдеры)
        if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.TeamMemoryAutolearn)) return Task.CompletedTask;

        // Источник: несколько участников → групповой чат/совещание; иначе — одиночный ход
        var source = session.Participants is { Count: > 0 }
            ? TeamMemorySource.AutoMeeting : TeamMemorySource.AutoTurn;

        // Извлечение не должно тормозить пайплайн хода/broadcast — уводим в фон
        _ = Task.Run(() => LearnSafeAsync(session.Id, ownerId, session.ProjectId!, source));
        return Task.CompletedTask;
    }

    private async Task LearnSafeAsync(string sessionId, string ownerId, string projectId, TeamMemorySource source)
    {
        try
        {
            var history = await _sessions.GetHistoryAsync(sessionId);
            var transcript = SessionSummaryService.BuildTranscript(history, TranscriptBudget);
            if (string.IsNullOrWhiteSpace(transcript)) return;

            // Анти-спам: извлекаем только если транскрипт заметно вырос с прошлого раза
            var minGrowth = int.TryParse(_config["TeamMemory:MinTranscriptGrowth"], out var g) && g > 0 ? g : 600;
            var prevLen = _lastLen.GetValueOrDefault(sessionId, 0);
            if (transcript.Length - prevLen < minGrowth) return;
            _lastLen[sessionId] = transcript.Length;

            var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.TeamMemoryAutolearn,
                BuildTeamPrompt(transcript),
                _config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");

            var items = Parse(raw);
            if (items.Count == 0) return;

            string? lastId = null;
            var saved = 0;
            foreach (var item in items)
            {
                // Авто-путь с разрешением противоречий (Memory #2): дубль усилит существующую запись,
                // конфликт → LLM-резолвер (UPDATE/DELETE), иначе ADD; NOOP (дубль/незначимо) → null.
                var entry = await _memory.AddWithResolutionAsync(ownerId, projectId, item.Text, item.Type, source, sessionId, item.Salience);
                if (entry is not null) { lastId = entry.Id; saved++; }
            }
            if (saved == 0) return;   // всё отброшено резолвером — уборку/уведомления не гоняем

            _log.LogInformation(
                "team-autolearn: проект {Project}, сессия {Session} — {Count} записей командной памяти",
                projectId, sessionId, saved);

            // Потолок памяти команды: механическое вытеснение хвоста сверх TeamMemory:MaxEntries —
            // сразу, чтобы стор не рос неограниченно при одном лишь autolearn
            _memory.EnforceCap(ownerId, projectId);

            // LLM-merge (умная уборка дублей) — заявка «пора», если записей больше софт-порога
            var softLimit = int.TryParse(_config["TeamMemory:SoftLimit"], out var soft) && soft > 0 ? soft : 150;
            if (_memory.List(ownerId, projectId).Count > softLimit)
                _consolidation.RequestConsolidation(ownerId, projectId);

            // Realtime: командный центр слушает team_memory_changed
            await _hub.Clients.Group("user_" + ownerId)
                .SendAsync("message", new TeamMemoryChangedMessage("added", projectId, lastId));

            // Активность-лента проекта
            _events?.Append(projectId, ownerId, ProjectEventTypes.MemoryLearned, "team",
                $"Команда: запомнила {saved} факт(ов) проекта", sessionId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "team-autolearn: извлечение памяти команды проекта {Project}", projectId);
        }
    }

    // КОНСЕРВАТИВНЫЙ экстрактор командного знания. Разграничение с личной памятью — в промпте.
    private static string BuildTeamPrompt(string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты ведёшь ОБЩУЮ память команды проекта — её видят ВСЕ персоны-ассистенты этого проекта. " +
                      "Ниже транскрипт рабочего хода. Выпиши только ОБЪЕКТИВНОЕ знание о ПРОЕКТЕ, полезное всей команде.");
        sb.AppendLine("Типы записей:");
        sb.AppendLine("- type=\"decision\": принятое решение или выбор (архитектурный, технический, продуктовый).");
        sb.AppendLine("- type=\"convention\": договорённость/правило проекта (как коммитим, именуем, оформляем, деплоим).");
        sb.AppendLine("- type=\"fact\": устойчивый факт о проекте (адрес прода, стек, структура, внешние сервисы, ключевые пути).");
        sb.AppendLine("- type=\"glossary\": термин проекта и его значение.");
        sb.AppendLine("У каждой записи укажи salience — важность 0..1 (1 = критично для всех, 0.3 = мелочь).");
        sb.AppendLine("СТРОГО НЕ включай: личные факты/предпочтения о пользователе или отдельной персоне " +
                      "(это ЛИЧНАЯ память, не командная); мимолётное, гипотезы, ход рассуждений, детали кода/диффы, " +
                      "тестовые и служебные реплики.");
        sb.AppendLine("Консервативно: при сомнении — НЕ извлекай. Лучше пусто, чем шум в общей памяти команды.");
        sb.AppendLine("Пиши кратко, по-русски, самодостаточно (понятно без контекста разговора). Одна мысль — одна запись.");
        sb.AppendLine("Ответь ТОЛЬКО JSON вида " +
                      "{\"items\":[{\"type\":\"decision\",\"text\":\"…\",\"salience\":0.8}]}. Нечего запоминать — items: [].");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    internal sealed record TeamItem(TeamMemoryType Type, string Text, double Salience);

    // Парс ответа модели: объект {items:[...]}; fallback — legacy-массив [{type,text}]. Мусор → пусто.
    // Извлечение JSON и маппинг записей — общая MemoryLlmParsing; специфичен только маппинг типа.
    internal static IReadOnlyList<TeamItem> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var objJson = MemoryLlmParsing.ExtractBalanced(raw, '{', '}');
        if (objJson is not null)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ResponseRaw>(objJson, JsonOpts);
                if (parsed?.Items is not null) return MapItems(parsed.Items);
            }
            catch (JsonException) { /* не объект нового формата — пробуем legacy-массив */ }
        }

        var arrJson = MemoryLlmParsing.ExtractBalanced(raw, '[', ']');
        if (arrJson is null) return [];
        try
        {
            var items = JsonSerializer.Deserialize<List<MemoryLlmParsing.ItemRaw>>(arrJson, JsonOpts);
            return items is null ? [] : MapItems(items);
        }
        catch (JsonException) { return []; }
    }

    // Маппинг сырых записей в TeamItem — через общее ядро; специфичен только маппинг типа
    private static IReadOnlyList<TeamItem> MapItems(List<MemoryLlmParsing.ItemRaw> parsed) =>
        MemoryLlmParsing.MapItems(parsed, ParseType,
            (type, text, salience) => new TeamItem(type, text, salience));

    // Маппинг строки типа из ответа LLM в TeamMemoryType (неизвестное → fact)
    private static TeamMemoryType ParseType(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "decision" => TeamMemoryType.Decision,
        "convention" => TeamMemoryType.Convention,
        "glossary" => TeamMemoryType.Glossary,
        _ => TeamMemoryType.Fact,
    };

    private sealed record ResponseRaw(List<MemoryLlmParsing.ItemRaw>? Items);
}
