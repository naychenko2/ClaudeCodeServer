using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// QuoteMatcher: дословное сопоставление цитаты с источником (S6a; ADR-035, решение 3,
// §12г мера 1). Юнит — нормализация и наличие циты; ниже — правило дословности WikiGate:
// цитата сноски обязана быть в тексте своей карточки с точностью до пробелов, кавычек
// и разметки; два вхождения (в источнике и на странице) допустимы.
public class QuoteMatcherTests
{
    // —— Юнит: ContainsVerbatim ——

    [Fact]
    public void ЦитатыВИсточникеНет_ложь() =>
        QuoteMatcher.ContainsVerbatim("Совсем другой текст ни о чём.", "бюджет до 15 тыс. ₽").Should().BeFalse();

    [Fact]
    public void ПробелыИПереносыСтрокСвёрнуты_истина() =>
        QuoteMatcher.ContainsVerbatim("бюджет   до\n15 тыс. ₽ в месяц", "бюджет до 15 тыс. ₽ в месяц").Should().BeTrue();

    [Fact]
    public void КавычкиСвёрнуты_истина() =>
        QuoteMatcher.ContainsVerbatim("бюджет \"до 15 тыс.\" ₽", "бюджет 'до 15 тыс.' ₽").Should().BeTrue();

    [Fact]
    public void МаркерыРазметкиСброшены_истина() =>
        QuoteMatcher.ContainsVerbatim("**бюджет** до `15 тыс.` ₽", "бюджет до 15 тыс. ₽").Should().BeTrue();

    [Fact]
    public void СсылкаСвёрнутаВВидимыйТекст_истина() =>
        QuoteMatcher.ContainsVerbatim("см. [[Бюджет GPU|бюджет до 15 тыс. ₽]] в месяц", "бюджет до 15 тыс. ₽ в месяц").Should().BeTrue();

    [Fact]
    public void ДваВхожденияВИсточнике_допустимы() =>
        QuoteMatcher.ContainsVerbatim("бюджет до 15 тыс. ₽. И ещё раз: бюджет до 15 тыс. ₽", "бюджет до 15 тыс. ₽").Should().BeTrue();

    [Fact]
    public void РегистрИПрочиеСимволыЗначимы_ложь()
    {
        QuoteMatcher.ContainsVerbatim("Бюджет до 15 тыс. ₽", "бюджет до 15 тыс. ₽").Should().BeFalse();
        QuoteMatcher.ContainsVerbatim("бюджет до 15 тыс. рублей", "бюджет до 15 тыс. ₽").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ПустойЦитатыНигдеНет_ложь(string quote) =>
        QuoteMatcher.ContainsVerbatim("любой текст", quote).Should().BeFalse();

    // —— WikiGate: правило дословности (sourceTexts) ——

    private const string P1 = "projects/p1";
    private const string P1Dir = "Проекты/p1/";
    private const string CardPath = P1Dir + "Источники/Карточка.md";
    private const string Good = "- Бюджет на GPU — до 15 тыс. ₽ в месяц.[^s1]";

    private static string Fm(string type, string extra = "") =>
        "---\ntype: " + type + "\nsummary: тестовая страница\n" + extra + "status: active\n---";

    private static string Card(string title) =>
        Fm("source", "origin: note\nref: note:1\nhash: h1\ncaptured: 2026-10-28\n") + $"\n\n# {title}\n";

    private static string Page(string quote) =>
        Fm("entity")
        + $"\n\n# Тет\n\n## Сводка\n{Good}\n\n## Хронология\n\n## Противоречия\n\n## История\n"
        + $"\n[^s1]: [[Карточка]] — «{quote}»\n";

    private static WikiGate.Draft D(string path, string content) => new(path, content);

    private static List<WikiGate.Draft> State(string quote) =>
        [D(P1Dir + "Тет.md", Page(quote)), D(CardPath, Card("Карточка"))];

    [Fact]
    public void Гейт_цитатыНетВИсточнике_отбита()
    {
        var texts = new Dictionary<string, string> { [CardPath] = "Совсем другой текст ни о чём." };
        WikiGate.Check(State("точная цитата из источника"), P1, [], texts)
            .Should().Contain(e => e.Contains("отсутствует в тексте источника"));
    }

    [Fact]
    public void Гейт_таЖеЦитатаСДругимиПробеламиКавычкамиРазметкой_проходит()
    {
        // Тот же текст в источнике — но markdown: **жирный**, "двойные кавычки",
        // лишние пробелы и перенос строки. Для матчинга это та же цитата.
        var source = "Заметка о бюджете.\n\n**Бюджет на GPU** \"до 15 тыс.\"\n₽ в   месяц, как решили.\n";
        var texts = new Dictionary<string, string> { [CardPath] = source };
        WikiGate.Check(State("Бюджет на GPU 'до 15 тыс.' ₽ в месяц"), P1, [], texts)
            .Should().BeEmpty();
    }

    [Fact]
    public void Гейт_дваВхожденияЦитатыВИсточнике_допустимы()
    {
        var source = "Бюджет на GPU — до 15 тыс. ₽ в месяц. Повтор для ясности: бюджет на GPU — до 15 тыс. ₽ в месяц.";
        var texts = new Dictionary<string, string> { [CardPath] = source };
        WikiGate.Check(State("Бюджет на GPU — до 15 тыс. ₽ в месяц"), P1, [], texts)
            .Should().BeEmpty();
    }

    [Fact]
    public void Гейт_однаЦитатаВДвухСноскахСтраницы_проходит()
    {
        var page = Fm("entity")
            + $"\n\n# Тет\n\n## Сводка\n{Good}\n\n## Хронология\n- То же в хронологии.[^s2]\n\n## Противоречия\n\n## История\n"
            + $"\n[^s1]: [[Карточка]] — «Бюджет на GPU — до 15 тыс. ₽ в месяц»\n[^s2]: [[Карточка]] — «Бюджет на GPU — до 15 тыс. ₽ в месяц»\n";
        var texts = new Dictionary<string, string> { [CardPath] = "Бюджет на GPU — до 15 тыс. ₽ в месяц." };
        WikiGate.Check([D(P1Dir + "Тет.md", page), D(CardPath, Card("Карточка"))], P1, [], texts)
            .Should().BeEmpty();
    }

    [Fact]
    public void Гейт_нетТекстаИсточникаКарточки_отбита()
    {
        WikiGate.Check(State("точная цитата из источника"), P1, [], new Dictionary<string, string>())
            .Should().Contain(e => e.Contains("нет текста источника"));
    }

    [Fact]
    public void Гейт_безТекстовИсточникаПравилоМолчит() =>
        WikiGate.Check(State("любая выдуманная цитата"), P1, []).Should().BeEmpty();
}
