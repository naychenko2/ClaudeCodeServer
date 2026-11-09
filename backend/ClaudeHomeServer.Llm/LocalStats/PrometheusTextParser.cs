using System.Globalization;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Один образец метрики Prometheus: имя, словарь меток, число.
/// </summary>
public readonly struct PrometheusSample
{
    public PrometheusSample(string name, IReadOnlyDictionary<string, string> labels, double value)
    {
        Name = name;
        Labels = labels;
        Value = value;
    }

    public string Name { get; }
    public IReadOnlyDictionary<string, string> Labels { get; }
    public double Value { get; }
}

/// <summary>
/// Разобранный текст Prometheus: набор образцов и методы поиска.
/// </summary>
public sealed class PrometheusSeries
{
    private readonly IReadOnlyList<PrometheusSample> _samples;

    public PrometheusSeries(IReadOnlyList<PrometheusSample> samples)
    {
        _samples = samples;
    }

    public IReadOnlyList<PrometheusSample> Samples => _samples;

    // Значение первого образца с данным именем (опционально с фильтром по меткам).
    // null — имя не найдено.
    public double? GetValue(string name, IDictionary<string, string>? labels = null)
    {
        foreach (var s in _samples)
        {
            if (s.Name != name) continue;
            if (labels is not null && !MatchesLabels(s.Labels, labels)) continue;
            return s.Value;
        }
        return null;
    }

    // Сумма всех образцов с данным именем (опционально с фильтром по меткам).
    // null — ни одного образца.
    public double? Sum(string name, IDictionary<string, string>? labels = null)
    {
        double? sum = null;
        foreach (var s in _samples)
        {
            if (s.Name != name) continue;
            if (labels is not null && !MatchesLabels(s.Labels, labels)) continue;
            sum = (sum ?? 0) + s.Value;
        }
        return sum;
    }

    // Максимум по всем образцам с данным именем (опционально с фильтром по меткам).
    // Для gauge-метрик с процентом (kv_cache_usage_perc и др.) при нескольких сериях:
    // берём худшую (максимальную) загрузку, а не первую серию. null — ни одного образца.
    public double? Max(string name, IDictionary<string, string>? labels = null)
    {
        double? max = null;
        foreach (var s in _samples)
        {
            if (s.Name != name) continue;
            if (labels is not null && !MatchesLabels(s.Labels, labels)) continue;
            max = max is null ? s.Value : Math.Max(max.Value, s.Value);
        }
        return max;
    }

    // Сумма и счётчик гистограммы: Prometheus отдаёт <имя>_sum и <имя>_count.
    // Оба null — гистограмма в тексте отсутствует.
    public (double? Sum, double? Count) GetHistogram(string name)
    {
        return (GetValue(name + "_sum"), GetValue(name + "_count"));
    }

    // Фильтр: образец подходит, если все пары из <paramref name="filter"/>
    // присутствуют в его метках с тем же значением.
    private static bool MatchesLabels(IReadOnlyDictionary<string, string> actual,
        IDictionary<string, string> filter)
    {
        foreach (var (key, value) in filter)
        {
            if (!actual.TryGetValue(key, out var actualValue) || actualValue != value)
                return false;
        }
        return true;
    }
}

/// <summary>
/// Разбор текстового формата Prometheus (0.0.4):
/// <code>
/// # HELP name описание
/// # TYPE name gauge|counter|histogram|summary
/// name{label="значение",...} число
/// </code>
/// Числа: обычный double, <c>+Inf</c>/<c>-Inf</c>, <c>NaN</c>, экспоненциальная запись.
/// В значениях меток поддерживаются экранирования <c>\"</c> и <c>\\</c>.
/// Зависимостей кроме BCL нет.
/// </summary>
public static class PrometheusTextParser
{
    public static PrometheusSeries Parse(string? text)
    {
        var samples = new List<PrometheusSample>();
        if (string.IsNullOrEmpty(text))
            return new PrometheusSeries(samples);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (TryParseSample(line, out var sample))
                samples.Add(sample);
        }
        return new PrometheusSeries(samples);
    }

    private static bool TryParseSample(string line, out PrometheusSample sample)
    {
        sample = default;

        // Имя: до '{' или пробела.
        int nameEnd = line.Length;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '{' || c == ' ' || c == '\t')
            {
                nameEnd = i;
                break;
            }
        }
        var name = line[..nameEnd];
        if (name.Length == 0)
            return false;

        // Метки: блок между '{' и '}'.
        var labels = new Dictionary<string, string>();
        int rest = nameEnd;
        if (rest < line.Length && line[rest] == '{')
        {
            int close = FindClosingBrace(line, rest + 1);
            if (close < 0)
                return false;
            if (!TryParseLabels(line[(rest + 1)..close], labels))
                return false;
            rest = close + 1;
        }

        // Число: после меток, пропускаем пробелы.
        while (rest < line.Length && (line[rest] == ' ' || line[rest] == '\t'))
            rest++;
        if (rest >= line.Length)
            return false;

        var valueStr = line[rest..].Trim();
        if (!TryParseValue(valueStr, out var value))
            return false;

        sample = new PrometheusSample(name, labels, value);
        return true;
    }

    // Ищет закрывающую '}' блока меток, пропуская экранированные кавычки в значениях.
    private static int FindClosingBrace(string line, int start)
    {
        bool inQuotes = false;
        for (int i = start; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '\\')
                    i++; // пропускаем экранированный символ
                else if (c == '"')
                    inQuotes = false;
            }
            else
            {
                if (c == '"')
                    inQuotes = true;
                else if (c == '}')
                    return i;
            }
        }
        return -1;
    }

    // Разбирает блок меток: key="value",key2="value2".
    private static bool TryParseLabels(string block, Dictionary<string, string> labels)
    {
        if (block.Length == 0)
            return true;

        int pos = 0;
        while (pos < block.Length)
        {
            // Пропускаем пробелы.
            while (pos < block.Length && (block[pos] == ' ' || block[pos] == '\t'))
                pos++;
            if (pos >= block.Length)
                break;

            // Ключ: до '='.
            int eq = block.IndexOf('=', pos);
            if (eq < 0)
                return false;
            var key = block[pos..eq].Trim();
            pos = eq + 1;

            // Ожидаем открывающую кавычку.
            while (pos < block.Length && (block[pos] == ' ' || block[pos] == '\t'))
                pos++;
            if (pos >= block.Length || block[pos] != '"')
                return false;
            pos++; // пропускаем '"'

            // Значение: до закрывающей кавычки, с учётом экранирований.
            var sb = new System.Text.StringBuilder();
            bool closed = false;
            while (pos < block.Length)
            {
                var c = block[pos];
                if (c == '\\')
                {
                    if (pos + 1 >= block.Length)
                        return false;
                    var next = block[pos + 1];
                    if (next == '"' || next == '\\')
                        sb.Append(next);
                    else
                        return false; // неизвестное экранирование
                    pos += 2;
                }
                else if (c == '"')
                {
                    pos++;
                    closed = true;
                    break;
                }
                else
                {
                    sb.Append(c);
                    pos++;
                }
            }
            if (!closed)
                return false;

            labels[key] = sb.ToString();

            // После значения: ',' (следующая метка) или конец блока.
            while (pos < block.Length && (block[pos] == ' ' || block[pos] == '\t'))
                pos++;
            if (pos < block.Length)
            {
                if (block[pos] == ',')
                    pos++;
                else
                    return false;
            }
        }
        return true;
    }

    private static bool TryParseValue(string s, out double value)
    {
        value = 0;
        if (s == "+Inf" || s == "+inf")
        {
            value = double.PositiveInfinity;
            return true;
        }
        if (s == "-Inf" || s == "-inf")
        {
            value = double.NegativeInfinity;
            return true;
        }
        if (s == "NaN" || s == "nan")
        {
            value = double.NaN;
            return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
