using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// QuoteMatcher: дословное сопоставление цитаты с источником (S6a; ADR-035, решение 3,
// §12г мера 1). Юнит — нормализация и наличие циты; ниже — правило дословности WikiGate:
// цитата сноски обязана быть в тексте своей карточки с точностью до пробелов, кавычек
// и разметки, но по границам слов: усечённая цитата («15 тыс.» из «115 тыс.»,
// «планировать» из «перепланировать») — не дословная; два вхождения (в источнике и
// на странице) допустимы.
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
    public void ПробелыСхлопываютсяАНеУдаляются_ложь() =>
        // Схлопывание — не удаление: цитата без пробелов не совпадает с источником,
        // где пробелы есть (иначе «бюджетдо15тыс.₽» сошлась бы с «бюджет до 15 тыс. ₽»).
        QuoteMatcher.ContainsVerbatim("бюджет до 15 тыс. ₽", "бюджетдо15тыс.₽").Should().BeFalse();

    [Theory]
    // Усечение посреди буквенно-цифрового токена — не дословная выдержка (§12г мера 1):
    // модель меняет число или слово в цитате, и её нет в источнике.
    [InlineData("Бюджет — 115 тыс. ₽ в месяц", "15 тыс. ₽ в месяц")]
    [InlineData("перепланировать бюджет", "планировать бюджет")]
    public void УсечённаяЦитатаНеДословная_ложь(string source, string quote) =>
        QuoteMatcher.ContainsVerbatim(source, quote).Should().BeFalse();

    [Fact]
    public void ЦелаяЦитатаПоГраницамСловПроходит_истина() =>
        QuoteMatcher.ContainsVerbatim("решили: бюджет до 15 тыс. ₽ в месяц, не больше", "бюджет до 15 тыс. ₽ в месяц").Should().BeTrue();

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
    public void Гейт_текстИсточникаNull_отбитаБезИсключения()
    {
        // Слой захвата (S6h/S6j) может отдать null для недоступного источника:
        // это нарушение (fail-closed), а не NullReferenceException.
        var texts = new Dictionary<string, string> { [CardPath] = null! };
        WikiGate.Check(State("точная цитата из источника"), P1, [], texts)
            .Should().Contain(e => e.Contains("нет текста источника"));
    }

    [Fact]
    public void Гейт_цитатаУсечённаяОтЧисла_отбита()
    {
        var texts = new Dictionary<string, string> { [CardPath] = "Бюджет — 115 тыс. ₽ в месяц." };
        WikiGate.Check(State("15 тыс. ₽ в месяц"), P1, [], texts)
            .Should().Contain(e => e.Contains("отсутствует в тексте источника"));
    }

    [Fact]
    public void Гейт_цитатаТолькоИзРазметки_отбита()
    {
        // «*******a» — 8 сырых символов (контракт по сырой длине соблюдён), но после
        // нормализации остаётся один значащий символ: минимальная длина меряется
        // по нормализованной строке.
        var texts = new Dictionary<string, string> { [CardPath] = "В документе есть буква a." };
        WikiGate.Check(State("*******a"), P1, [], texts)
            .Should().Contain(e => e.Contains("после нормализации"));
    }

    [Fact]
    public void Гейт_безТекстовИсточникаПравилоМолчит() =>
        WikiGate.Check(State("любая выдуманная цитата"), P1, []).Should().BeEmpty();
}
