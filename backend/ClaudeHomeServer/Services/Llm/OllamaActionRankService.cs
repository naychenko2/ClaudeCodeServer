using System.Text.Json;

namespace ClaudeHomeServer.Services.Llm;

// Ранжирование действий AI-хаба локальной моделью. По содержанию открытой сущности
// выбирает из списка ДОСТУПНЫХ действий уместные СЕЙЧАС и оценивает уровень пользы
// (strong/medium/minor). Результат питает проактивные подсказки и палитру. При любой
// проблеме возвращает пустой список — фронт откатывается на rule-based механизм.

// Действие-кандидат (пришло с фронта: id + человеческие подписи для модели).
public sealed record RankCandidate(string Id, string Title, string Hint);

// Ранжированное действие с уровнем пользы.
public sealed record RankedAction(string Id, string Level);

public sealed class OllamaActionRankService
{
    private readonly OllamaClient _ollama;
    private readonly LocalActionRouter _router;

    // Ранжир доступен, если Ollama настроена И действие action-rank заведено на локаль (конфиг)
    public bool Enabled => _router.UsesLocal(LocalActionCatalog.ActionRank);

    public OllamaActionRankService(OllamaClient ollama, LocalActionRouter router)
    {
        _ollama = ollama;
        _router = router;
    }

    private static readonly HashSet<string> ValidLevels = new(StringComparer.OrdinalIgnoreCase)
        { "strong", "medium", "minor" };

    // JSON-schema ответа: {"actions":[{"id","level"}]}. Ollama форсит по ней вывод.
    private static readonly object FormatSchema = new
    {
        type = "object",
        properties = new
        {
            actions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string" },
                        level = new { type = "string", @enum = new[] { "strong", "medium", "minor" } },
                    },
                    required = new[] { "id", "level" },
                },
            },
        },
        required = new[] { "actions" },
    };

    private const string SystemPrompt =
        "Ты помощник в рабочей среде (заметки, задачи, чаты, файлы, персоны, знания). " +
        "По содержанию открытой сущности выбери из списка ДОСТУПНЫХ действий те, что реально уместны СЕЙЧАС. " +
        "Будь СТРОГ и консервативен: по умолчанию НИЧЕГО не уместно — верни пустой массив. Предлагай только при " +
        "явном сигнале в контексте, что действие полезно прямо сейчас. Уровни: strong — действие явно и " +
        "безотлагательно нужно ПРЯМО СЕЙЧАС (конкретный повод в контексте), ставь strong РЕДКО; medium — уместно, " +
        "стоит предложить; minor — можно, но не важно. Сомневаешься между strong и medium — выбирай medium. " +
        "Верни JSON {\"actions\":[{\"id\":\"…\",\"level\":\"strong|medium|minor\"}]} — только id из списка, " +
        "максимум K по убыванию уровня. Лучше не предложить, чем предложить лишнее. Не выдумывай id.";

    // Отранжировать действия. contextText — компактное описание открытой сущности (усечено фронтом).
    // Пустой результат = сигнал фолбэка (Ollama выключен/ошибка/ничего не уместно).
    public async Task<IReadOnlyList<RankedAction>> RankAsync(
        string contextType, string contextText, IReadOnlyList<RankCandidate> actions, int maxK,
        CancellationToken ct = default)
    {
        if (!Enabled || actions.Count == 0) return [];

        var menu = actions.Select(a => new { id = a.Id, desc = a.Title + " — " + a.Hint });
        var userPrompt =
            $"КОНТЕКСТ ({contextType}):\n{contextText}\n\n" +
            $"МАКСИМУМ: {maxK}\n\n" +
            $"ДОСТУПНЫЕ ДЕЙСТВИЯ:\n{JsonSerializer.Serialize(menu)}";

        // Профиль каталога применяем явно: раньше вызов шёл с дефолтами ChatJsonAsync, и
        // num_ctx/num_predict/timeout из CheapProfile.Small молча не действовали — меню
        // действий с длинным контекстом обрезалось на входе.
        var spec = _router.ProfileFor(LocalActionCatalog.ActionRank);
        var raw = await _ollama.ChatJsonAsync(SystemPrompt, userPrompt, FormatSchema, ct,
            timeoutMs: spec.TimeoutMs, numPredict: spec.NumPredict, numCtx: spec.NumCtx);
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var allowed = actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(raw);
            if (!parsed.TryGetProperty("actions", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

            var result = new List<RankedAction>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in arr.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                var level = item.TryGetProperty("level", out var l) ? l.GetString() : null;
                // Страховка от галлюцинаций: только id из переданного списка и валидный уровень
                if (string.IsNullOrEmpty(id) || !allowed.Contains(id) || !seen.Add(id)) continue;
                if (string.IsNullOrEmpty(level) || !ValidLevels.Contains(level)) level = "minor";
                result.Add(new RankedAction(id, level.ToLowerInvariant()));
                if (result.Count >= maxK) break;
            }
            return result;
        }
        catch
        {
            return [];
        }
    }
}
