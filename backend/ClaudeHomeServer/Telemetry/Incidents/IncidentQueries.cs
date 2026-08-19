using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClaudeHomeServer.Telemetry.Incidents;

/// <summary>
/// Чистая часть разбора инцидента: тела запросов к <c>POST /api/v5/query_range</c>
/// и разбор ответов. Без сети и без состояния — всё под тестом.
///
/// Форма запроса снята с рабочих правил алертов и сохранённых представлений
/// (<c>docker/observability/alerts/*.json</c>, <c>views/*.json</c>) — там та же схема v5:
/// <c>compositeQuery.queries[].spec</c>, фильтр строкой-выражением
/// (<c>filter.expression</c>), разрезы массивом <c>groupBy</c>. Ряды в ответе лежат
/// глубже, чем в легаси-API: <c>data.data.results[]</c> (см. docs/observability/dashboards.md).
///
/// Тела собираются <see cref="Utf8JsonWriter"/>, а не склейкой строк: значение фильтра
/// приходит из конфига (контур) и из меток алерта, и самодельное экранирование уже один
/// раз рождало невалидный JSON — апостроф превращался в <c>\'</c>, которого в JSON нет,
/// SigNoz отвечал 400, а разрез молча оказывался пустым.
/// </summary>
public static class IncidentQueries
{
    /// <summary>Сколько строк тянем в списках трейсов и логов. Больше человеку в карточке не нужно.</summary>
    public const int RowLimit = 10;

    private static readonly JsonWriterOptions WriterOpts = new()
    {
        // Кириллица в теле запроса остаётся читаемой (значения меток бывают русскими),
        // при этом кавычки и слеши экранируются штатно.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Разрез метрики по тегу за окно инцидента: «сколько ошибок какого типа».
    /// Аггрегация та же, что в правилах алертов (increase/sum по Cumulative) — иначе
    /// карточка показывала бы не то, из-за чего алерт загорелся.
    /// </summary>
    public static string Breakdown(
        string metricName, string groupByTag, string? environment,
        DateTimeOffset from, DateTimeOffset to)
        => Build("time_series", from, to, spec =>
        {
            spec.WriteString("name", "A");
            spec.WriteString("signal", "metrics");
            spec.WriteBoolean("disabled", false);
            spec.WriteStartArray("aggregations");
            spec.WriteStartObject();
            spec.WriteString("metricName", metricName);
            spec.WriteString("temporality", "Cumulative");
            spec.WriteString("timeAggregation", "increase");
            spec.WriteString("spaceAggregation", "sum");
            spec.WriteEndObject();
            spec.WriteEndArray();
            spec.WriteStartArray("groupBy");
            spec.WriteStartObject();
            spec.WriteString("name", groupByTag);
            spec.WriteString("fieldDataType", "string");
            spec.WriteEndObject();
            spec.WriteEndArray();
            WriteFilter(spec, Expression(environment));
        });

    /// <summary>
    /// Последние упавшие ходы за окно: спаны <c>chat.turn</c> с <c>outcome = 'error'</c>.
    /// Отсюда берётся связка «инцидент → чат» — тег <c>chat_id</c>.
    /// </summary>
    public static string FailedTurns(string? environment, DateTimeOffset from, DateTimeOffset to, int limit = RowLimit)
        => Build("raw", from, to, spec =>
        {
            spec.WriteString("name", "A");
            spec.WriteString("signal", "traces");
            spec.WriteNumber("limit", limit);
            WriteOrderByTimestamp(spec);
            WriteSelectFields(spec, "traces",
                "chat_id", "session_id", "turn_id", "model", "provider", "error_type", "outcome");
            WriteFilter(spec, Combine("name = 'chat.turn'", "outcome = 'error'", Expression(environment)));
        });

    /// <summary>
    /// Логи уровня Warning/Error за окно инцидента.
    ///
    /// Связь с конкретным ходом здесь только по ВРЕМЕНИ: у логов <c>trace_id</c> пустой
    /// (санитайзер логов работает по своим правилам, а корреляцию логов со спанами мы не
    /// включали). Это известное ограничение, оно записано в docs/observability/incident-queries.md.
    /// </summary>
    public static string Logs(string? environment, DateTimeOffset from, DateTimeOffset to, int limit = RowLimit)
        => Build("raw", from, to, spec =>
        {
            spec.WriteString("name", "A");
            spec.WriteString("signal", "logs");
            spec.WriteNumber("limit", limit);
            WriteOrderByTimestamp(spec);
            WriteSelectFields(spec, "logs", "severity_text", "body");
            WriteFilter(spec, Combine(
                "(severity_text = 'Warning' OR severity_text = 'Error')", Expression(environment)));
        });

    /// <summary>
    /// Фильтр по контуру. Пустой контур — без ограничения: правило без разреза по среде
    /// касается инсталляции целиком, и отфильтровать его значило бы показать пустой разрез.
    /// </summary>
    internal static string? Expression(string? environment)
        => string.IsNullOrWhiteSpace(environment)
            ? null
            : $"deployment.environment = '{EscapeValue(environment)}'";

    /// <summary>
    /// Значение внутри строкового литерала выражения. Экранирование ТОЛЬКО для парсера
    /// выражений (кавычка и слеш) — JSON-экранированием занимается <see cref="Utf8JsonWriter"/>,
    /// и делать это руками второй раз нельзя: именно так и родился невалидный <c>\'</c>.
    /// </summary>
    private static string EscapeValue(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal);

    private static string Combine(params string?[] parts)
        => string.Join(" AND ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Build(string requestType, DateTimeOffset from, DateTimeOffset to,
        Action<Utf8JsonWriter> writeSpec)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("schemaVersion", "v1");
            w.WriteNumber("start", from.ToUnixTimeMilliseconds());
            w.WriteNumber("end", to.ToUnixTimeMilliseconds());
            w.WriteString("requestType", requestType);
            w.WriteStartObject("compositeQuery");
            w.WriteStartArray("queries");
            w.WriteStartObject();
            w.WriteString("type", "builder_query");
            w.WriteStartObject("spec");
            writeSpec(w);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteFilter(Utf8JsonWriter w, string? expression)
    {
        w.WriteStartObject("filter");
        w.WriteString("expression", expression ?? "");
        w.WriteEndObject();
        w.WriteStartObject("having");
        w.WriteString("expression", "");
        w.WriteEndObject();
    }

    private static void WriteOrderByTimestamp(Utf8JsonWriter w)
    {
        w.WriteStartArray("order");
        w.WriteStartObject();
        w.WriteStartObject("key");
        w.WriteString("name", "timestamp");
        w.WriteEndObject();
        w.WriteString("direction", "desc");
        w.WriteEndObject();
        w.WriteEndArray();
    }

    private static void WriteSelectFields(Utf8JsonWriter w, string signal, params string[] names)
    {
        w.WriteStartArray("selectFields");
        foreach (var name in names)
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            w.WriteString("fieldDataType", "string");
            w.WriteString("signal", signal);
            w.WriteString("fieldContext", "attribute");
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    // ==== разбор ответов ====

    /// <summary>
    /// Разрез по тегу из ответа <c>time_series</c>: подписи серий и суммы значений.
    /// Битый/незнакомый ответ — пустой список, а не исключение: карточка инцидента
    /// не должна падать оттого, что SigNoz после обновления ответил иначе.
    /// </summary>
    public static IReadOnlyList<IncidentBreakdownRow> ParseBreakdown(string? json, string groupByTag)
    {
        var rows = new List<IncidentBreakdownRow>();
        foreach (var result in Results(json))
        {
            if (!result.TryGetProperty("aggregations", out var aggs) || aggs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var agg in aggs.EnumerateArray())
            {
                if (!agg.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var serie in series.EnumerateArray())
                {
                    var label = Label(serie, groupByTag);
                    var sum = SumValues(serie);
                    if (sum > 0) rows.Add(new IncidentBreakdownRow(label, sum));
                }
            }
        }
        return rows.OrderByDescending(r => r.Count).ToList();
    }

    /// <summary>Упавшие ходы из ответа <c>raw</c>.</summary>
    public static IReadOnlyList<IncidentTurn> ParseTurns(string? json)
    {
        var turns = new List<IncidentTurn>();
        foreach (var row in Rows(json))
        {
            turns.Add(new IncidentTurn(
                TraceId: Str(row, "trace_id") ?? Str(row, "traceID") ?? "",
                ChatId: Str(row, "chat_id"),
                At: RowTime(row),
                Model: Str(row, "model"),
                Provider: Str(row, "provider"),
                ErrorType: Str(row, "error_type"),
                DurationMs: DurationMs(row)));
        }
        return turns;
    }

    /// <summary>Строки логов из ответа <c>raw</c>.</summary>
    public static IReadOnlyList<IncidentLogLine> ParseLogs(string? json)
    {
        var lines = new List<IncidentLogLine>();
        foreach (var row in Rows(json))
        {
            var message = Str(row, "body") ?? Str(row, "message");
            if (string.IsNullOrWhiteSpace(message)) continue;
            lines.Add(new IncidentLogLine(
                At: RowTime(row),
                Severity: Str(row, "severity_text") ?? "Error",
                Message: message.Length > 400 ? message[..400] + "…" : message));
        }
        return lines;
    }

    /// <summary>
    /// <c>data.data.results[]</c> — общая обёртка ответов v5 (в v3/v4 было <c>data.result[]</c>).
    /// </summary>
    private static IEnumerable<JsonElement> Results(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            var node = doc.RootElement;
            if (!node.TryGetProperty("data", out var outer) || outer.ValueKind != JsonValueKind.Object) yield break;
            // Уровень «data.data» появился в v5; страхуемся и от формы без него.
            var inner = outer.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object
                ? nested : outer;
            if (!inner.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) yield break;
            foreach (var result in results.EnumerateArray())
                yield return result.Clone();
        }
    }

    /// <summary>
    /// Строки raw-ответа. Значения полей лежат во вложенном объекте <c>data</c>; форму без
    /// вложения тоже принимаем — не тот случай, где стоит падать из-за версии SigNoz.
    /// </summary>
    private static IEnumerable<JsonElement> Rows(string? json)
    {
        foreach (var result in Results(json))
        {
            if (!result.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                yield return row;
            }
        }
    }

    private static string? Str(JsonElement row, string name)
    {
        if (row.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && Value(data, name) is { } inner)
            return inner;
        return Value(row, name);

        static string? Value(JsonElement obj, string key)
            => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
               && v.GetString() is { Length: > 0 } s ? s : null;
    }

    private static DateTimeOffset? RowTime(JsonElement row)
    {
        // timestamp приходит и строкой ISO, и числом (наносекунды/миллисекунды)
        if (row.TryGetProperty("timestamp", out var ts))
        {
            if (ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed;
            if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var num))
                return num > 1_000_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(num / 1_000_000)
                    : DateTimeOffset.FromUnixTimeMilliseconds(num);
        }
        return null;
    }

    private static long DurationMs(JsonElement row)
    {
        var source = row.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data : row;
        foreach (var key in new[] { "duration_nano", "durationNano" })
        {
            if (source.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt64(out var nano))
                return nano / 1_000_000;
        }
        return 0;
    }

    /// <summary>Подпись серии: значение разреза из labels; пусто — «без метки».</summary>
    private static string Label(JsonElement serie, string groupByTag)
    {
        if (serie.TryGetProperty("labels", out var labels))
        {
            // labels бывает и объектом {tag: value}, и массивом [{key, value}]
            if (labels.ValueKind == JsonValueKind.Object
                && labels.TryGetProperty(groupByTag, out var direct)
                && direct.ValueKind == JsonValueKind.String)
                return direct.GetString() ?? "—";
            if (labels.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in labels.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                        ? k.GetString() : null;
                    if (!string.Equals(key, groupByTag, StringComparison.Ordinal)) continue;
                    if (item.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String)
                        return val.GetString() ?? "—";
                }
            }
        }
        return "—";
    }

    private static double SumValues(JsonElement serie)
    {
        if (!serie.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            return 0;
        var sum = 0d;
        foreach (var point in values.EnumerateArray())
        {
            if (point.ValueKind == JsonValueKind.Object
                && point.TryGetProperty("value", out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) sum += v.GetDouble();
                else if (v.ValueKind == JsonValueKind.String
                         && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    sum += parsed;
            }
            else if (point.ValueKind == JsonValueKind.Number)
            {
                sum += point.GetDouble();
            }
        }
        return sum;
    }
}
