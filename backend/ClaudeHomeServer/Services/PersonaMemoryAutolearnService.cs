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
    private readonly PersonaMemoryConsolidationService _consolidation;
    private readonly Llm.OneShotClaudeRunner _runner;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaMemoryAutolearnService> _log;

    public PersonaMemoryAutolearnService(SessionManager sessions, PersonaManager personas,
        PersonaMemoryService memory, PersonaMemoryConsolidationService consolidation,
        Llm.OneShotClaudeRunner runner,
        IConfiguration config, ILogger<PersonaMemoryAutolearnService> log)
    {
        _sessions = sessions;
        _personas = personas;
        _memory = memory;
        _consolidation = consolidation;
        _runner = runner;
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

            var result = Parse(raw);
            var saved = 0;
            foreach (var item in result.Items)
            {
                if (_memory.Remember(persona.OwnerId, persona.Id, item.Type, item.Text,
                        null, sessionId, item.Salience) is not null)
                    saved++;
            }
            // Рабочий фокус: null от модели = разговор не про дело, фокус НЕ трогаем
            if (result.Focus is not null)
                _memory.SetFocus(persona.OwnerId, persona.Id,
                    result.Focus.What, result.Focus.Status, result.Focus.NextStep, sessionId);

            if (saved > 0)
                _log.LogInformation("autolearn: персона {Persona}, сессия {Session} — сохранено {Count} записей памяти",
                    persona.Id, sessionId, saved);

            // Переполнение памяти → отметка «пора консолидировать» (сама уборка — фоном)
            var maxEntries = int.TryParse(_config["Persona:MemoryMaxEntries"], out var max) ? max : 150;
            if (_memory.List(persona.OwnerId, persona.Id, null).Count > maxEntries)
                _consolidation.RequestConsolidation(persona.OwnerId, persona.Id);
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
        sb.AppendLine("- type=\"procedural\": выученный приём или правило работы с пользователем " +
                      "(«как ему удобно», «что всегда делать/не делать»). До 2 штук.");
        sb.AppendLine("У каждой записи укажи salience — важность 0..1 (1 = критично помнить, 0.3 = мелочь).");
        sb.AppendLine("НЕ включай: мимолётное, гипотетическое, служебные/тестовые реплики, общие рассуждения без сути.");
        sb.AppendLine("Пиши кратко, по-русски, о пользователе в третьем лице. Одна мысль — одна запись.");
        sb.AppendLine("Отдельно поле focus — текущее незавершённое дело персоны (над чем работа продолжится): " +
                      "{\"what\":\"…\",\"status\":\"…\",\"nextStep\":\"…\"}. Если разговор не про дело — focus: null.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом вида " +
                      "{\"items\":[{\"type\":\"semantic\",\"text\":\"…\",\"salience\":0.8}],\"focus\":null}. " +
                      "Если запоминать нечего — items: [].");
        sb.AppendLine();
        sb.AppendLine("Транскрипт:");
        sb.AppendLine(transcript);
        return sb.ToString();
    }

    // Результат извлечения: записи памяти (с важностью) + опциональный рабочий фокус
    internal sealed record AutolearnItem(PersonaMemoryType Type, string Text, double Salience);
    internal sealed record AutolearnFocus(string What, string Status, string? NextStep);
    internal sealed record AutolearnResult(IReadOnlyList<AutolearnItem> Items, AutolearnFocus? Focus);

    // Парс ответа модели: новый формат — объект {items, focus}; fallback — legacy-массив
    // [{type, text}] (старые модели/промпты). Мусор → пустой результат.
    internal static AutolearnResult Parse(string raw)
    {
        var empty = new AutolearnResult([], null);
        if (string.IsNullOrWhiteSpace(raw)) return empty;

        // Новый формат: первый сбалансированный JSON-объект с полем items
        var objJson = ExtractBalanced(raw, '{', '}');
        if (objJson is not null)
        {
            ResponseRaw? parsed = null;
            try { parsed = JsonSerializer.Deserialize<ResponseRaw>(objJson, JsonOpts); }
            catch (JsonException) { /* не объект нового формата — пробуем legacy-массив */ }
            if (parsed?.Items is not null)
            {
                var focus = ParseFocus(parsed.Focus);
                return new AutolearnResult(MapItems(parsed.Items), focus);
            }
        }

        // Legacy-fallback: JSON-массив [{type, text}]
        var arrJson = ExtractBalanced(raw, '[', ']');
        if (arrJson is null) return empty;
        List<ItemRaw>? items;
        try { items = JsonSerializer.Deserialize<List<ItemRaw>>(arrJson, JsonOpts); }
        catch (JsonException) { return empty; }
        return items is null ? empty : new AutolearnResult(MapItems(items), null);
    }

    private static IReadOnlyList<AutolearnItem> MapItems(List<ItemRaw> parsed)
    {
        var result = new List<AutolearnItem>();
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
            // Важность: отсутствует → 1.0, иначе кламп в 0.05..1
            var salience = it.Salience is null ? 1.0 : Math.Clamp(it.Salience.Value, 0.05, 1.0);
            result.Add(new AutolearnItem(type, text, salience));
        }
        return result.Take(8).ToList();
    }

    private static AutolearnFocus? ParseFocus(FocusRaw? focus)
    {
        var what = focus?.What?.Trim();
        if (string.IsNullOrWhiteSpace(what)) return null;
        return new AutolearnFocus(what, focus!.Status?.Trim() ?? "", focus.NextStep?.Trim());
    }

    // Первый сбалансированный JSON-фрагмент между open/close (устойчиво к преамбуле/fence)
    private static string? ExtractBalanced(string raw, char open, char close)
    {
        var start = raw.IndexOf(open);
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
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }

    private sealed record ItemRaw(string? Type, string? Text, double? Salience = null);
    private sealed record FocusRaw(string? What, string? Status, string? NextStep);
    private sealed record ResponseRaw(List<ItemRaw>? Items, FocusRaw? Focus);
}
