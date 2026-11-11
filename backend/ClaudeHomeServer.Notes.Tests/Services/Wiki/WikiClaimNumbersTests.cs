using System.Diagnostics;
using ClaudeHomeServer.Services.Notes.Wiki;

namespace ClaudeHomeServer.Tests.Services;

// WikiClaimNumbers: числа утверждения и их подтверждение цитатами (S2-2; ADR-039,
// решение 2). Юнит — разбор числа (знак-мин в токене, разделители тысяч ровно по
// 3 цифры, один десятичный разделитель), нормализация («,» → «.», пробелы внутри
// числа убираются, «−» U+2212 → ASCII) и сравнение целыми токенами: «12» не
// подтверждается цитатой с «123», «−12» — цитатой без знака. Метки сносок [^1] и
// [^sN] вырезаются из claim и из цитат до разбора — их цифры не числа утверждения
// и не подтверждение. Null входов — пустой результат без исключения.
public class WikiClaimNumbersTests
{
    private const char Nbsp = (char)0x00A0;
    private const char Narrow = (char)0x202F;

    // —— Подтверждённые числа: пустой результат ——

    [Fact]
    public void ДесятичнаяЗапятаяСводитсяКТочке()
    {
        // Единственный тест с десятичной запятой: мутация «убрать ,→.» краснит
        // ровно его. «1,18» и «1.18» — одно число после нормализации.
        WikiClaimNumbers.Unconfirmed("темп 1,18 кг", ["1.18 кг/нед"]).Should().BeEmpty();
        // «0,3–0,7»: тире не входит в число — два числа; «0,3» подтверждён цитатой,
        // «0,7» — нет, и в результате он нормализован: запятая свёрнута к точке.
        WikiClaimNumbers.Unconfirmed("0,3–0,7", ["0.3"]).Should().Equal("0.7");
    }

    [Fact]
    public void ПробелыТысячНеМеняютЧисло_подтверждено() =>
        // «176 949» и «176949» — одно число: пробелы-разделители при сравнении убираются.
        WikiClaimNumbers.Unconfirmed("176 949 токенов", ["176949"]).Should().BeEmpty();

    [Theory]
    [InlineData(' ')]
    [InlineData(Nbsp)]
    [InlineData(Narrow)]
    public void РазделительТысяцПодтверждаетсяВЗаписиБезНего(char sep) =>
        // Все три разделителя тысяч (обычный, U+00A0, U+202F) — один и тот же
        // пробел для сравнения: «1<sep>000» подтверждается цитатой «1000».
        WikiClaimNumbers.Unconfirmed($"расход 1{sep}000 Вт", ["1000"]).Should().BeEmpty();

    [Fact]
    public void ЧислоИзЦитатыПодтверждаетЧислоУтверждения() =>
        WikiClaimNumbers.Unconfirmed("урожай 42.5 кг", ["урожай 42.5 кг с сотки"]).Should().BeEmpty();

    // —— Неподтверждённые числа ——

    [Fact]
    public void ДругоеДесятичноеЧисло_неПодтверждено() =>
        WikiClaimNumbers.Unconfirmed("2.28", ["2.27"]).Should().Equal("2.28");

    [Fact]
    public void ЦелоеЧислоНеПодтверждаетсяБолееДлинным() =>
        // Сравнение целыми токенами, не подстрокой: «12» ≠ «123». Мутация
        // «сравнение подстрокой» краснит ровно этот тест.
        WikiClaimNumbers.Unconfirmed("12 шагов", ["123 шага"]).Should().Equal("12");

    [Fact]
    public void ЧислоБезЦитат_неПодтверждено() =>
        WikiClaimNumbers.Unconfirmed("2.28", []).Should().Equal("2.28");

    // —— Знак числа ——

    [Fact]
    public void ОтрицательноеЧислоБезЗнакаВЦитате_неПодтверждено()
    {
        // «−12» (U+2212) и «12» — разные числа: знак входит в число. Мутация
        // «знак не в токене» краснит ровно этот тест (первая проверка).
        WikiClaimNumbers.Unconfirmed("−12 °C", ["12 °C"]).Should().Equal("-12");
        // Оба минуса (ASCII и U+2212) — один и тот же знак после нормализации.
        WikiClaimNumbers.Unconfirmed("−12 °C", ["-12 °C"]).Should().BeEmpty();
        WikiClaimNumbers.Unconfirmed("-12 °C", ["−12 °C"]).Should().BeEmpty();
        // Знак не мешает группам тысяч и десятичной части.
        WikiClaimNumbers.Unconfirmed("расход −1 000 Вт", ["-1000"]).Should().BeEmpty();
        WikiClaimNumbers.Unconfirmed("−2,28", ["-2.27"]).Should().Equal("-2.28");
    }

    [Theory]
    [InlineData("123-456")]
    [InlineData("0.3-0.7")]
    public void ДефисМеждуЧисламиНеЗнак_дваЧисла(string text) =>
        // Знак не стоит после цифры или десятичного разделителя: «123-456» и
        // «0.3-0.7» — два числа, как с тире. Мутация «знак где угодно» краснит
        // ровно этот тест (в результате появился бы «-456» / «-0.7»).
        WikiClaimNumbers.Unconfirmed(text, [text.Split('-')[0]]).Should().Equal(text.Split('-')[1]);

    // —— Разбор: границы и форма результата ——

    [Fact]
    public void МеткиСносокВырезаютсяИхЦифрыНеЧисла() =>
        // [^1] и [^s2] — маркеры сносок: их цифры не должны считаться числами
        // утверждения, иначе «1» и «2» ушли бы в результат как неподтверждённые.
        WikiClaimNumbers.Unconfirmed("решили так [^1] [^s2]", []).Should().BeEmpty();

    [Fact]
    public void МеткиСносокВЦитатахНеПодтверждаютЧислоClaim() =>
        // Цифры «[^1]» в цитате — маркер сноски, а не число «1»: claim «в 1 случае»
        // ими не подтверждён. Мутация «не вырезать метки из цитат» краснит ровно
        // этот тест.
        WikiClaimNumbers.Unconfirmed("в 1 случае", ["решили так [^1]"]).Should().Equal("1");

    [Fact]
    public void NullVMestahPustoyRezultatBezIsklyucheniya()
    {
        // Null входа — нечего проверять: пустой результат, без NRE.
        WikiClaimNumbers.Unconfirmed(null, ["5"]).Should().BeEmpty();
        WikiClaimNumbers.Unconfirmed("5", null).Should().BeEmpty();
        WikiClaimNumbers.Unconfirmed(null, null).Should().BeEmpty();
    }

    [Fact]
    public void ДиапазонСТире_дваЧисла() =>
        // Тире не входит в число: «0.3» подтверждён цитатой, «0.7» — нет.
        // Тот же разбор, что в кейсе с запятой выше, но без запятых: независим от
        // мутации «,→.».
        WikiClaimNumbers.Unconfirmed("0.3–0.7", ["0.3"]).Should().Equal("0.7");

    [Fact]
    public void ДатаКакЧислоПроверяетсяНаТочномСовпадении() =>
        // «17.08» — одно число; цитата с «17.09» его не подтверждает.
        WikiClaimNumbers.Unconfirmed("замер 17.08", ["замер 17.09"]).Should().Equal("17.08");

    [Fact]
    public void ГруппаПослеПробелаРовноИзТрёхЦифр_иначеЭтоДругоеЧисло() =>
        // «12 3456»: группа из 4 цифр после пробела — не разделитель тысяч.
        // Числа — «12» и «3456»; цитата подтверждает только «12».
        WikiClaimNumbers.Unconfirmed("12 3456", ["12"]).Should().Equal("3456");

    [Fact]
    public void ДвеГруппыТысячОднимЧислом() =>
        // «1 000 000» — одно число (группы ровно по 3 цифры), подтверждено цитатой.
        WikiClaimNumbers.Unconfirmed("1 000 000", ["1000000"]).Should().BeEmpty();

    [Fact]
    public void ДубльЧислаВClaimВРезультатеОдинРаз() =>
        // Порядок — по первому вхождению, без дублей.
        WikiClaimNumbers.Unconfirmed("9 и 9", []).Should().Equal("9");

    [Fact]
    public void ПорядокРезультата_поПервомуВхождениюВClaim() =>
        WikiClaimNumbers.Unconfirmed("сначала 9, потом 7", []).Should().Equal("9", "7");

    // —— Бюджет: регулярное выражение без катастрофического отката ——

    [Theory]
    // Смешанные группы разной длины и сплошная цифровая полоса: обе строки 10 000 символов.
    [InlineData("123 45 6", 1250)]
    [InlineData("7", 10000)]
    public void СтрокаИзДесятиТысячСимволовОбрабатываетсяБезКатастрофическогоОтката(string unit, int repeat)
    {
        // Ровно 10 000 символов: группы разной длины («123», «45», «6»), смежные
        // цифровые подряды склеиваются в длинные числа, либо одна сплошная полоса —
        // самый длинный токен. 10 000 заведомо больше лимита claim (600): проверяется
        // устойчивость разбора, а не типичный вход. Порог щадящий (1 с): ловит
        // катастрофический откат (десятки секунд), а не дрожание таймера на CI.
        var s = string.Concat(Enumerable.Repeat(unit, repeat));
        var sw = Stopwatch.StartNew();
        var result = WikiClaimNumbers.Unconfirmed(s, []);
        sw.Stop();
        result.Should().NotBeEmpty();
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }
}
