using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services;

// Авто-память персоны (флаг persona-memory-autolearn): по завершении хода в персонной
// сессии one-shot вызовом Claude извлекает из диалога факты о пользователе (semantic) и
// краткий итог (episodic) и складывает их в долгую память персоны — без явной команды.
// Подписывается на SessionManager.OnSessionMessage; тяжёлая работа — вне пайплайна хода.
public sealed class PersonaMemoryAutolearnService : IHostedService
{
    private const int TranscriptBudget = 8_000;
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;
    private readonly PersonaMemoryService _memory;
    private readonly Llm.OneShotClaudeRunner _runner;
    private readonly FeatureFlagService _flags;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaMemoryAutolearnService> _log;

    public PersonaMemoryAutolearnService(SessionManager sessions, PersonaManager personas,
        PersonaMemoryService memory, Llm.OneShotClaudeRunner runner, FeatureFlagService flags,
        IConfiguration config, ILogger<PersonaMemoryAutolearnService> log)
    {
        _sessions = sessions;
        _personas = personas;
        _memory = memory;
        _runner = runner;
        _flags = flags;
        _config = config;
        _log = log;
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
        // Реагируем только на завершение хода в персонной сессии
        if (msg is not ResultMessage || session.PersonaId is null) return Task.CompletedTask;

        var persona = _personas.GetByIdInternal(session.PersonaId);
        if (persona is null || !persona.MemoryEnabled) return Task.CompletedTask;
        if (!_flags.IsEnabled(persona.OwnerId, FeatureFlagKeys.PersonaMemoryAutolearn)) return Task.CompletedTask;

        // Извлечение не должно тормозить пайплайн хода/broadcast — уводим в фон
        _ = Task.Run(() => LearnSafeAsync(session.Id, persona));
        return Task.CompletedTask;
    }

    private async Task LearnSafeAsync(string sessionId, Persona persona)
    {
        try
        {
            var history = await _sessions.GetHistoryAsync(sessionId);
            var transcript = SessionSummaryService.BuildTranscript(history, TranscriptBudget);
            if (string.IsNullOrWhiteSpace(transcript)) return;

            var model = _runner.NormalizeModel(
                _config["Notes:AiModel"] ?? _config["Tasks:AiModel"] ?? "haiku");
            var raw = await _runner.RunAsync(BuildPrompt(persona, transcript), model, LlmTimeout, default);

            var items = ParseItems(raw);
            var saved = 0;
            foreach (var (type, text) in items)
            {
                if (_memory.Remember(persona.OwnerId, persona.Id, type, text, null, sessionId) is not null)
                    saved++;
            }
            if (saved > 0)
                _log.LogInformation("autolearn: персона {Persona}, сессия {Session} — сохранено {Count} записей памяти",
                    persona.Id, sessionId, saved);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "autolearn: извлечение памяти персоны {Persona}", persona.Id);
        }
    }

    private static string BuildPrompt(Persona persona, string transcript)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ты ведёшь долгую память персоны-ассистента по имени {persona.Name}. " +
                      "Ниже транскрипт её разговора с пользователем. Выпиши только то, что стоит запомнить надолго.");
        sb.AppendLine("Типы записей:");
        sb.AppendLine("- type=\"semantic\": устойчивый факт или предпочтение пользователя (имя, вкусы, контекст, " +
                      "привычки, важные детали жизни/работы). До 5 штук.");
        sb.AppendLine("- type=\"episodic\": один короткий итог этого разговора — что обсудили/решили.");
        sb.AppendLine("НЕ включай: мимолётное, гипотетическое, служебные/тестовые реплики, общие рассуждения без сути.");
        sb.AppendLine("Пиши кратко, по-русски, о пользователе в третьем лице. Одна мысль — одна запись.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-массивом объектов {type, text}. Если запоминать нечего — [].");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    private static IReadOnlyList<(PersonaMemoryType Type, string Text)> ParseItems(string raw)
    {
        var json = ExtractJsonArray(raw);
        if (json is null) return [];
        List<ItemRaw>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<ItemRaw>>(json, JsonOpts); }
        catch (JsonException) { return []; }
        if (parsed is null) return [];

        var result = new List<(PersonaMemoryType, string)>();
        foreach (var it in parsed)
        {
            var text = it.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var type = it.Type?.Trim().ToLowerInvariant() switch
            {
                "episodic" => PersonaMemoryType.Episodic,
                "procedural" => PersonaMemoryType.Procedural,
                _ => PersonaMemoryType.Semantic,
            };
            result.Add((type, text));
        }
        return result.Take(6).ToList();
    }

    // Первый сбалансированный JSON-массив из ответа модели (устойчиво к преамбуле/fence)
    private static string? ExtractJsonArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    private sealed record ItemRaw(string? Type, string? Text);
}
