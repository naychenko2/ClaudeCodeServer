using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Разметка входа для оси B Батареи II: какие факты во входе есть заведомо.
///
/// Ось B меряет места, которые пересказывают ЧУЖОЙ текст (сводки и выжимки). Контракт у
/// них слабый — свободный текст, — и оракул контракта тут почти ничего не значит: место
/// «прошло», даже если сводка состоит из выдумок. Поэтому оракул здесь другой: во входе
/// заранее размечены проверяемые факты, и считается, сколько из них доехало до выжимки.
///
/// <see cref="Kind"/> — вид факта из спецификации (решение, дата, участник, ОТКАЗ, грабля).
/// Вид <see cref="Refusal"/> обслуживает отдельную ловушку оси: во входе есть «решили НЕ
/// делать X», и сводка, превратившая отказ в решение, — провал независимо от recall.
///
/// <see cref="Anchors"/> — формулировки, по любой из которых факт считается упомянутым.
/// Они пишутся РУКАМИ вместе с кейсом и работают подстрокой по нормализованному тексту:
/// автоматического «понимания» здесь нет и быть не может, а подстрока хотя бы честна и
/// воспроизводима. Отсюда два следствия, записанных заранее:
///  • якорь обязан встречаться в САМОМ входе (<see cref="LocalBenchFactSheet.Grounded"/>) —
///    иначе размечен не факт входа, а фантазия разметчика, и замер мерил бы её;
///  • промах якоря даёт ложный пропуск. Пропущенные факты едут в файл судьи вместе с парой
///    «вход + выжимка», и ложный пропуск там виден глазами.
/// </summary>
public sealed record BenchFact(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("what")] string What,
    [property: JsonPropertyName("anchors")] IReadOnlyList<string> Anchors,
    // Дополнительные маркеры отрицания рядом с якорем — сверх общего списка
    // LocalBenchFactMatcher.NegationMarkers. Нужны там, где отказ во входе выражен
    // словом, которого в общем списке нет («обошлись без», «сняли с повестки»).
    [property: JsonPropertyName("negationMarkers")] IReadOnlyList<string>? NegationMarkers = null)
{
    /// <summary>Вид факта-отказа: «решили НЕ делать X», «отвергли Y, потому что…».</summary>
    public const string Refusal = "refusal";

    public const string Decision = "decision";
    public const string Date = "date";
    public const string Person = "person";
    public const string Pitfall = "pitfall";

    public static readonly IReadOnlyList<string> Kinds =
        [Decision, Date, Person, Refusal, Pitfall];

    public bool IsRefusal => string.Equals(Kind, Refusal, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Попал ли размеченный факт в выжимку.</summary>
public enum FactVerdict
{
    /// <summary>Якорь факта нашёлся в выжимке.</summary>
    Recalled,

    /// <summary>Ни одного якоря — факт до выжимки не доехал.</summary>
    Missed,
}

/// <summary>
/// Что стало с отказом. Трёх состояний, а не двух, намеренно: машина различает «отказ
/// упомянут с отрицанием» и «тема упомянута, отрицания рядом нет», но НЕ различает второе
/// от настоящего переворота смысла — для этого нужен судья.
/// </summary>
public enum NegationVerdict
{
    /// <summary>Тема отказа упомянута, и рядом с ней есть отрицание — отказ уцелел.</summary>
    Preserved,

    /// <summary>
    /// Тема упомянута, отрицания рядом НЕТ. Подозрение на переворот отказа в решение —
    /// кейс уходит судье. Приговором само по себе не является: отрицание бывает выражено
    /// оборотом, которого нет в списке маркеров.
    /// </summary>
    Suspect,

    /// <summary>Темы отказа в выжимке нет вовсе — это пропуск, а не переворот.</summary>
    Missed,
}

/// <summary>Приговор по одному факту: что размечено и что нашлось.</summary>
public sealed record FactOutcome(BenchFact Fact, FactVerdict Verdict, NegationVerdict? Negation)
{
    public bool Recalled => Verdict == FactVerdict.Recalled;
}

/// <summary>
/// Сличитель разметки с выжимкой. Отдельный класс, а не метод теста места: правило
/// совпадения одно на все семь мест оси, и разойдись оно между местами — таблица
/// сравнивала бы места между собой по разным линейкам.
/// </summary>
public static class LocalBenchFactMatcher
{
    /// <summary>
    /// Окно вокруг якоря, в котором ищется отрицание, в символах нормализованного текста.
    /// Одно предложение сводки — примерно столько; шире окно начнёт цеплять отрицание из
    /// соседнего пункта и объявлять уцелевшим отказ, которого в тексте нет.
    /// </summary>
    public const int NegationWindow = 140;

    /// <summary>
    /// Маркеры отрицания — общий список на все места. Пишутся ОСНОВАМИ без окончаний
    /// («отверг» ловит «отвергли», «отвергнут»): морфологии у подстроки нет, а окончания
    /// в русском меняются чаще корня.
    /// </summary>
    public static readonly IReadOnlyList<string> NegationMarkers =
    [
        " не ", "не стали", "нельзя", "отказ", "отверг", "отклон", "вычерк",
        "не будет", "не делать", "решено не", "без ", "вместо", "мимо",
        "запрещ", "исключ", "снят", "убра",
    ];

    /// <summary>
    /// Нормализация текста перед поиском: регистр, ё, пунктуация и переносы строк.
    /// Пунктуация заменяется пробелом, а не вырезается: иначе «CLAUDE.md» склеилось бы в
    /// «claudemd» и перестало совпадать с якорем «claude md» и наоборот.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 2);
        sb.Append(' ');
        var lastSpace = true;
        foreach (var raw in text)
        {
            var ch = char.ToLowerInvariant(raw) switch { 'ё' => 'е', var c => c };
            var keep = char.IsLetterOrDigit(ch) || ch == '_';
            if (keep)
            {
                sb.Append(ch);
                lastSpace = false;
                continue;
            }
            if (lastSpace) continue;
            sb.Append(' ');
            lastSpace = true;
        }
        if (!lastSpace) sb.Append(' ');
        return sb.ToString();
    }

    /// <summary>Есть ли в нормализованном тексте хоть один из якорей.</summary>
    public static bool Contains(string normalizedText, IEnumerable<string> anchors) =>
        FirstHit(normalizedText, anchors) >= 0;

    /// <summary>
    /// Рассудить один факт по выжимке. <paramref name="summary"/> — то, что место отдало
    /// на выходе (а не сырой ответ модели): человек видит именно выход, и продуктовый
    /// разбор — часть места.
    /// </summary>
    public static FactOutcome Judge(BenchFact fact, string? summary)
    {
        var text = Normalize(summary);
        var hit = FirstHit(text, fact.Anchors);
        if (hit < 0)
            return new FactOutcome(fact, FactVerdict.Missed,
                fact.IsRefusal ? NegationVerdict.Missed : null);

        if (!fact.IsRefusal) return new FactOutcome(fact, FactVerdict.Recalled, null);

        // Отказ: тема упомянута — остаётся понять, уцелело ли отрицание рядом с ней.
        var markers = fact.NegationMarkers is { Count: > 0 } extra
            ? NegationMarkers.Concat(extra)
            : NegationMarkers;
        var from = Math.Max(0, hit - NegationWindow);
        var to = Math.Min(text.Length, hit + NegationWindow);
        var window = text[from..to];
        var negated = markers.Any(m => MarkerNeedle(m) is { Length: >= 2 } needle
                                       && window.Contains(needle, StringComparison.Ordinal));
        // Факт «упомянут» в обоих случаях: recall и сохранение отказа — РАЗНЫЕ метрики,
        // и склей их — потерянный отказ было бы не отличить от перевёрнутого.
        return new FactOutcome(fact, FactVerdict.Recalled,
            negated ? NegationVerdict.Preserved : NegationVerdict.Suspect);
    }

    /// <summary>
    /// Искомая строка маркера отрицания. Пробелы по краям маркера ЗНАЧАЩИЕ и сохраняются:
    /// «отказ» без пробелов ищется основой слова (ловит «отказались», «отказ от»), а « не »
    /// с обоими пробелами — целым словом, иначе оно срабатывало бы внутри «нет», «неделя»
    /// и объявляло бы уцелевшим отказ, которого в тексте нет.
    /// </summary>
    internal static string MarkerNeedle(string marker)
    {
        var core = Normalize(marker).Trim(' ');
        if (core.Length == 0) return "";
        var head = marker.StartsWith(' ') ? " " : "";
        var tail = marker.EndsWith(' ') ? " " : "";
        return head + core + tail;
    }

    // Позиция первого сработавшего якоря; -1 — ни один не нашёлся.
    private static int FirstHit(string normalizedText, IEnumerable<string> anchors)
    {
        var best = -1;
        foreach (var anchor in anchors)
        {
            var needle = Normalize(anchor).Trim(' ');
            if (needle.Length < 2) continue;
            var at = normalizedText.IndexOf(needle, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
    }
}

/// <summary>
/// Разметка одного кейса: факты из поля <c>expect.facts</c> банка.
///
/// Лежит в том же <see cref="LocalBenchCase.Expect"/>, что и эталон оси A, — банки обеих
/// осей читает один загрузчик, и заводить второй формат файла значило бы разнести кейсы
/// одного харнесса по двум несовместимым схемам.
/// </summary>
public sealed class LocalBenchFactSheet
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<BenchFact> Facts { get; }

    private LocalBenchFactSheet(IReadOnlyList<BenchFact> facts) => Facts = facts;

    /// <summary>Факты-отказы кейса — ловушка оси на отрицания.</summary>
    public IReadOnlyList<BenchFact> Refusals => Facts.Where(f => f.IsRefusal).ToArray();

    /// <summary>
    /// Прочитать разметку кейса. Её отсутствие — ошибка, а не пустой список: кейс без
    /// разметки прошёл бы замер с recall 0/0, то есть «идеально», и дыра в банке
    /// выглядела бы как успех места.
    /// </summary>
    public static LocalBenchFactSheet Of(LocalBenchCase c)
    {
        if (c.Expect is not { } expect || expect.ValueKind != JsonValueKind.Object
            || !expect.TryGetProperty("facts", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"У кейса «{c.Id}» нет разметки expect.facts — мерить recall нечем");

        var facts = arr.Deserialize<List<BenchFact>>(Json) ?? [];
        if (facts.Count == 0)
            throw new InvalidOperationException($"У кейса «{c.Id}» пустой список фактов");
        return new LocalBenchFactSheet(facts);
    }

    /// <summary>
    /// Факты, НЕ подтверждённые самим входом: ни один якорь в тексте входа не встречается.
    /// Пусто — разметка заземлена. Непусто — размечена фантазия разметчика, и замер по
    /// такому банку мерил бы её, а не модель (это и есть «самая дорогая часть оси»,
    /// на которой велено не экономить).
    /// </summary>
    public IReadOnlyList<BenchFact> Ungrounded(string input)
    {
        var text = LocalBenchFactMatcher.Normalize(input);
        return Facts.Where(f => !LocalBenchFactMatcher.Contains(text, f.Anchors)).ToArray();
    }

    /// <summary>Разметка заземлена входом целиком.</summary>
    public bool Grounded(string input) => Ungrounded(input).Count == 0;

    /// <summary>Рассудить выжимку по всем фактам кейса.</summary>
    public IReadOnlyList<FactOutcome> Judge(string? summary) =>
        Facts.Select(f => LocalBenchFactMatcher.Judge(f, summary)).ToArray();
}
