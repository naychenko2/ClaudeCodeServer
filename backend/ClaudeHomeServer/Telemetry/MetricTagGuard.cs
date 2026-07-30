using System.Collections.Concurrent;

namespace ClaudeHomeServer.Telemetry;

/// <summary>
/// Ограничитель кардинальности ЗНАЧЕНИЙ тегов метрик.
///
/// Дисциплина <see cref="ServerMetrics.AllowedTags"/> закрывает только ИМЕНА тегов — она
/// не даёт завести тег <c>user_id</c>, но ничего не говорит про то, сколько разных значений
/// приедет в разрешённый тег. А приезжали они бесконтрольно:
/// <list type="bullet">
/// <item><c>tool_name</c> — когда MCP-сервер не назвался, вместо имени инструмента шёл путь
///   запроса (<c>/api/projects/{guid}/files/...</c>): и взрыв кардинальности, и PII в метрике,
///   которую санитайзер спанов не видит (он сидит только в pipeline трейсов);</item>
/// <item><c>model</c> — свободная строка из <c>PUT /api/projects/{id}/sessions/{sid}</c>:
///   любое значение поля создаёт новый временной ряд.</item>
/// </list>
/// Каждый новый ряд в ClickHouse живёт до конца retention, поэтому одна такая утечка
/// портит стор надолго — чинить приходится мутациями по таблицам.
///
/// Защита двухступенчатая: сначала проверка ФОРМЫ (путь, пробел, кириллица — сразу мимо,
/// чтобы мусор не съедал бюджет), затем лимит на число РАЗНЫХ значений. Всё, что не прошло,
/// схлопывается в <see cref="Overflow"/> — метрика продолжает считать вызовы, теряется лишь
/// детализация. Точные значения остаются в диагностике: <c>GET /api/mcp/calls</c> для
/// инструментов, транскрипт и SpendStore для моделей.
/// </summary>
public static class MetricTagGuard
{
    /// <summary>Значение не задано (заголовок инструмента отсутствует, модель не выбрана).</summary>
    public const string Unnamed = "unnamed";

    /// <summary>Значение не прошло форму или лимит различных значений.</summary>
    public const string Overflow = "other";

    // Реальных MCP-инструментов ≤ 80-90 (docs/observability/audit.md) — запас втрое.
    private static readonly TagValueLimiter Tools = new(256);

    // Моделей у инстанса единицы: три слота тиров, пантеон персон, direct:-маршруты
    // OpenRouter и локальные модели Ollama. 64 — заведомый запас.
    private static readonly TagValueLimiter Models = new(64);

    /// <summary>Значение тега <c>tool_name</c>: имя MCP-инструмента вида <c>tasks_list</c>.</summary>
    public static string Tool(string? raw) => Tools.Limit(raw, IsToolShape);

    /// <summary>
    /// Значение тега <c>model</c>: идентификатор модели вида <c>claude-sonnet-4-5-20250929</c>,
    /// <c>glm-4.6</c>, <c>qwen2.5:7b</c>, <c>direct:openai/gpt-4o-mini</c>.
    /// </summary>
    public static string Model(string? raw) => Models.Limit(raw, IsModelShape);

    // Форма имени инструмента: латиница, цифры и разделители имён MCP-серверов.
    // Путь запроса отсекается слэшем, «(без имени) …» — скобками и пробелом.
    internal static bool IsToolShape(string v) =>
        v.Length <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    // Форма идентификатора модели — та же плюс ':' (тег Ollama) и '/' (вендор в direct:-маршруте).
    // Слэш здесь разрешён, поэтому длина и отсутствие пробелов — единственная защита от пути;
    // за остальное отвечает лимит различных значений.
    internal static bool IsModelShape(string v) =>
        v.Length <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '/');
}

/// <summary>
/// Пропускает не больше <paramref name="limit"/> различных значений; всё сверх лимита
/// и всё, не прошедшее проверку формы, схлопывается в <see cref="MetricTagGuard.Overflow"/>.
///
/// Проверка счётчика и вставка не атомарны — под гонкой лимит может быть превышен на
/// несколько значений. Это осознанно: смысл в порядке величины (не дать рядам расти
/// бесконечно), а не в точной границе, и брать лок на каждый вызов метрики ради этого дорого.
/// </summary>
internal sealed class TagValueLimiter(int limit)
{
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    public string Limit(string? raw, Func<string, bool> shapeOk)
    {
        if (string.IsNullOrEmpty(raw)) return MetricTagGuard.Unnamed;
        if (!shapeOk(raw)) return MetricTagGuard.Overflow;
        if (_seen.ContainsKey(raw)) return raw;
        if (_seen.Count >= limit) return MetricTagGuard.Overflow;
        _seen.TryAdd(raw, 0);
        return raw;
    }

    /// <summary>Число уже пропущенных различных значений — для тестов и диагностики.</summary>
    public int Count => _seen.Count;
}
