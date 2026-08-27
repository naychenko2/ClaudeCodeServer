using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.ProjectIcons;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Место применения «Значок проекта» в каталоге — текстовое, с выбором модели (ADR-009 §9)
public class ProjectIconCatalogTests
{
    [Fact]
    public void МестоЗначокПроекта_ЕстьВКаталоге()
    {
        var action = LocalActionCatalog.Find("project-icon");

        Assert.NotNull(action);
        Assert.Equal("Значок проекта", action.Title);
        Assert.Equal("Проекты", action.Group);
        Assert.Equal(CheapProfile.Large, action.Profile);
        Assert.False(action.DefaultLocal);
        // Medium: после вырезания генерации path'ов задача — назвать имя из набора
        // (Strong поднимался 2026-08-17 под рисование)
        Assert.Equal(ModelTier.Medium, LocalActionCatalog.EffectiveDefaultTier(action!));
        Assert.True(LocalActionCatalog.IsKnown("project-icon"));
    }

    // Собственный лимит ожидания облака для места (прод 17.08: сильная модель отвечает
    // 52–126 с, профиль Large давал 300 с и держал зависший вызов пять минут) — задаче
    // «таймаут сильной модели» значение 180 с обязано быть закреплено явно. Двухходовая
    // схема (ревизия 20.08) удваивает время подбора, но 180 с покрывают и её
    [Fact]
    public void МестоЗначокПроекта_СобственныйЛимитОблака180с()
    {
        var action = LocalActionCatalog.Find("project-icon");

        Assert.NotNull(action);
        Assert.Equal(180_000, action!.CloudTimeoutMs);
    }
}


// Разбор и валидация ответа модели по контракту ADR-009: имя только из белого списка.
// Рисованные пути вырезаны: ответ с paths вместо имени — негодный кандидат, при нуле
// годных — пустой результат (фолбэк на инициалы), а не значок.
public class ProjectIconGlyphServiceTests
{
    private const string ValidNamesJson =
        """{"glyphs":[{"name":"piggy-bank"},{"name":"chart-line"},{"name":"wallet"},{"name":"rocket"}]}""";

    [Fact]
    public void ГодныйОтвет_ДоЧетырёхИмён()
    {
        var result = ProjectIconGlyphService.Parse(ValidNamesJson);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Equal(["piggy-bank", "chart-line", "wallet", "rocket"],
            result.Candidates.Select(c => c.Name));
        Assert.Null(result.FailReason);
    }

    [Fact]
    public void ОтветВМаркдаунЗаборе_Разбирается()
    {
        var result = ProjectIconGlyphService.Parse("```json\n" + ValidNamesJson + "\n```");

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
    }

    [Fact]
    public void ИмяВнеБелогоСписка_ПустойРезультат()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"name":"super-kitty-icon"},{"name":"chart-line"}]}""");

        // Негодный кандидат отбрасывается, годный остаётся
        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("chart-line", candidate.Name);
    }

    [Fact]
    public void ТолькоИмяВнеБелогоСписка_ОтказПустымРезультатом()
    {
        var result = ProjectIconGlyphService.Parse("""{"glyphs":[{"name":"not-a-lucide-name"}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        // Причина называет класс (имя вне белого списка) и само имя-нарушитель
        Assert.Equal("name-out:not-a-lucide-name", result.FailReason);
    }

    [Fact]
    public void ИменаВнеПрежних89_ПроходятПодбор()
    {
        // Полный набор установленного lucide-react (ADR-009 §5.2): haze не было в рукописных
        // 89, x — однобуквенное имя, отсекавшееся прежней формой {1,39}. Оба обязаны
        // проходить валидатор подбора и повторную валидацию icon/select
        var result = ProjectIconGlyphService.Parse("""{"glyphs":[{"name":"haze"},{"name":"x"}]}""");

        Assert.True(result.Ok);
        Assert.Equal(["haze", "x"], result.Candidates.Select(c => c.Name).ToList());
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("haze"));
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("x"));
    }

    // Модель ещё может слать рисованные пути (ветка вырезана) — они отбрасываются как
    // негодные кандидаты, годные имена из того же ответа остаются
    [Fact]
    public void ПутиВместоИмени_НегодныйКандидатГодныеИменаОстаются()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["M3 21h18"]},{"name":"wallet"},{"name":"nope"}]}""");

        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("wallet", candidate.Name);
    }

    [Fact]
    public void ТолькоПути_ОтказПустымРезультатом()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["M3 21h18","M6 21V9l6-4 6 4v12"]}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        Assert.Equal("glyph-shape:paths", result.FailReason);
    }

    [Fact]
    public void СыраяРазметкаВОтвете_ОтказБезЗначка()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"paths":["<svg onload=alert(1)><path d='M0 0'/></svg>"]}]}""");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""")]   // paths и имя вместе
    [InlineData("""{"glyphs":[{}]}""")]                                       // ни одного поля
    [InlineData("""{"glyphs":[{"paths":[]}]}""")]                             // пустой список путей
    public void НегодныеКандидаты_Отбрасываются(string raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ХвостСверхЧетырёх_Обрезается()
    {
        var result = ProjectIconGlyphService.Parse(
            """{"glyphs":[{"name":"wallet"},{"name":"house"},{"name":"rocket"},{"name":"star"},{"name":"zap"},{"name":"bot"}]}""");

        Assert.True(result.Ok);
        Assert.Equal(4, result.Candidates.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("мусор без json")]
    [InlineData("""{"nope": 1}""")]
    [InlineData("""[{"name":"wallet"}]""")]   // массив вместо объекта
    public void НеJson_ОтказBadJson(string? raw)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Equal("bad-json", result.FailReason);
    }

    // Причины отказа различимы по классам (задача «логи причин отказа»): код называет,
    // что именно не прошло
    [Theory]
    [InlineData("""{"glyphs":[]}""", "no-glyphs")]
    [InlineData("""{"glyphs":[{}]}""", "glyph-shape:none")]
    [InlineData("""{"glyphs":[{"name":"nope"}]}""", "name-out:nope")]
    [InlineData("""{"glyphs":[{"paths":["M0 0h1"]}]}""", "glyph-shape:paths")]
    [InlineData("""{"glyphs":[{"name":"house","paths":["M3 21h18"]}]}""", "glyph-shape:paths")]
    public void ПричинаОтказа_КлассИЗначение(string raw, string expected)
    {
        var result = ProjectIconGlyphService.Parse(raw);

        Assert.False(result.Ok);
        Assert.Equal(expected, result.FailReason);
    }

    [Fact]
    public void ValidateGlyph_ПовторнаяВалидацияТойЖеТочкойВхода()
    {
        // icon/select присылает значок телом — валидация та же, что для модели (ADR-009 §8)
        Assert.NotNull(ProjectIconGlyphService.ValidateGlyph("wallet"));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("нет-такого"));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph(null));
        Assert.Null(ProjectIconGlyphService.ValidateGlyph("  "));
    }

    [Fact]
    public void LucideGlyphs_БелыйСписокЦеликомНижнегоРегистра()
    {
        Assert.All(LucideGlyphs.All, name =>
        {
            // {0,39}: в полном наборе есть однобуквенное имя «x» (ADR-009 §5.5)
            Assert.Matches("^[a-z][a-z0-9-]{0,39}$", name);
        });
        // Прежний стартовый набор ADR-009 §5 на месте и пополнился именами вне старых 89
        Assert.Contains("piggy-bank", LucideGlyphs.All);
        Assert.Contains("chart-line", LucideGlyphs.All);
        Assert.Contains("x", LucideGlyphs.All);
        Assert.Contains("haze", LucideGlyphs.All);
        Assert.NotEmpty(LucideGlyphs.All);
    }
}

// Ход слов двухходовой схемы (мера 1, ревизия 20.08.2026): модель называет слова-понятия,
// реальные имена по ним отбирает сервер
public class ProjectIconWordsParsingTests
{
    [Fact]
    public void КонтрактПервогоХода_СловаНормализуются()
    {
        var (words, reason) = ProjectIconGlyphService.ParseWords(
            """{"words":["Lighthouse","sea","Traffic Light","Traffic-Light","маяк"]}""");

        Assert.Null(reason);
        // Регистр вниз, фраза — и дефисным написанием, и частями; не-латиница отсекается
        Assert.Equal(["lighthouse", "sea", "traffic-light", "traffic", "light"], words);
    }

    [Fact]
    public void ХвостСверхВосьми_Обрезается()
    {
        var raw = "{\"words\":[" + string.Join(",", Enumerable.Range(1, 10).Select(i => $"\"word{i}\"")) + "]}";

        var (words, reason) = ProjectIconGlyphService.ParseWords(raw);

        Assert.Null(reason);
        Assert.Equal(ProjectIconGlyphService.MaxWords, words.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("мусор без json")]
    [InlineData("""{"nope": 1}""")]
    [InlineData("""{"words":"строка вместо массива"}""")]
    public void НеJson_ПричинаBadJson(string? raw)
    {
        var (words, reason) = ProjectIconGlyphService.ParseWords(raw);

        Assert.Empty(words);
        Assert.Equal("bad-json", reason);
    }

    [Theory]
    [InlineData("""{"words":[]}""")]
    [InlineData("""{"words":[42,null]}""")]
    [InlineData("""{"words":["маяк"]}""")]   // кириллица не нормализуется в латиницу
    [InlineData("""[{"words":[]}]""")]
    public void СловаНеИзвлечены_ПричинаNoWords(string raw)
    {
        var (words, reason) = ProjectIconGlyphService.ParseWords(raw);

        Assert.Empty(words);
        Assert.Equal("no-words", reason);
    }
}

// Отбор меню (мера 1): из слов-понятий — реальные имена набора, точным совпадением
// и подстрокой в обе стороны, с добором общеупотребимых
public class ProjectIconMenuTests
{
    [Fact]
    public void СловоЯвляетсяИменем_ТочноеВхождение()
    {
        var menu = ProjectIconGlyphService.SelectMenu(["wallet"]);

        Assert.Contains("wallet", menu);
        // Подстрока в обе стороны: wallet-2, wallet-cards, wallet-minimal тоже в наборе
        Assert.True(menu.Count >= ProjectIconGlyphService.MenuMinimum);
        Assert.All(menu, name => Assert.True(LucideGlyphs.Contains(name)));
    }

    [Fact]
    public void СловоНеИмя_НаходятсяРеальныеИменаПоПодстроке()
    {
        // lighthouse сам по себе не иконка — имя находится ВНУТРИ слова («house»)
        var menu = ProjectIconGlyphService.SelectMenu(["lighthouse"]);

        Assert.Contains("house", menu);
        Assert.All(menu, name => Assert.True(LucideGlyphs.Contains(name)));
    }

    [Fact]
    public void СловаПонятийМаяка_МенюИзРеальныхИмён()
    {
        // Типичный ответ хода слов для проекта про маяк: часть слов — не иконки
        // (lighthouse, tower, sea, coast — таких имён в наборе нет). Меню всё равно
        // собирается из реальных имён: точные (navigation, compass), подстрока
        // (house внутри lighthouse, radio-tower/tower-control для tower)
        var menu = ProjectIconGlyphService.SelectMenu(
            ["lighthouse", "tower", "sea", "navigation", "compass", "coast"]);

        Assert.True(menu.Count >= ProjectIconGlyphService.MenuMinimum);
        Assert.Contains("navigation", menu);
        Assert.Contains("compass", menu);
        Assert.Contains("house", menu);
        Assert.All(menu, name => Assert.True(LucideGlyphs.Contains(name)));
    }

    [Fact]
    public void ПустыеСлова_МенюИзОбщеупотребимыхНеМеньшеЧетырёх()
    {
        var menu = ProjectIconGlyphService.SelectMenu([]);

        Assert.True(menu.Count >= ProjectIconGlyphService.MenuMinimum);
        Assert.All(menu, name => Assert.True(LucideGlyphs.Contains(name)));
    }

    [Fact]
    public void МенюОграниченоИДетерминировано()
    {
        var a = ProjectIconGlyphService.SelectMenu(["star", "light"]);
        var b = ProjectIconGlyphService.SelectMenu(["star", "light"]);

        Assert.True(a.Count <= ProjectIconGlyphService.MenuCap);
        Assert.Equal(a, b);

        // Повтор в одном процессе поймал бы даже случайный порядок HashSet (он стабилен
        // внутри процесса), поэтому проверяем сам ИСТОЧНИК детерминизма: имена одного
        // слова идут по возрастанию, а не как лягут в набор
        var single = ProjectIconGlyphService.SelectMenu(["wallet"]);
        Assert.Equal(single.OrderBy(n => n, StringComparer.Ordinal), single);
    }

    // Обратная подстрока — только по границе слова. Живая приёмка 20.08.2026: «scarf»
    // давал «car» куском из середины, и грид показывал машину для проекта про шарф
    [Theory]
    [InlineData("scarf", "car")]      // кусок середины — не смысл
    [InlineData("hearth", "ear")]
    [InlineData("search", "arch")]
    public void ОбратнаяПодстрокаИзСередины_НеСчитаетсяСовпадением(string word, string junk)
    {
        var menu = ProjectIconGlyphService.SelectMenu([word]);

        Assert.DoesNotContain(junk, menu);
    }

    [Fact]
    public void ОбратнаяПодстрокаПоГранице_ОстаётсяСовпадением()
    {
        // Составное слово: имя стоит концом («lighthouse» → «house») либо началом
        Assert.Contains("house", ProjectIconGlyphService.SelectMenu(["lighthouse"]));
        Assert.Contains("book", ProjectIconGlyphService.SelectMenu(["bookshelf"]));
    }

    // Прямая подстрока — тоже по границе, а не куском середины имени. Ревью 20.08.2026:
    // «hive» (улей) тянул «archive», «rain» — «brain», «over» — «clover». Тот же класс
    // мусора, что «scarf» → «car», только с другой стороны
    [Theory]
    [InlineData("hive", "archive")]
    [InlineData("rain", "brain")]
    [InlineData("over", "clover")]
    public void ПрямаяПодстрокаИзСерединыИмени_НеСчитаетсяСовпадением(string word, string junk)
    {
        var menu = ProjectIconGlyphService.SelectMenu([word]);

        Assert.DoesNotContain(junk, menu);
    }

    [Fact]
    public void ПрямаяПодстрокаПоГраницеСегмента_ОстаётсяСовпадением()
    {
        // Слово начинает имя либо его дефисный сегмент — имя уточняет понятие
        Assert.Contains("lightbulb", ProjectIconGlyphService.SelectMenu(["light"]));
        Assert.Contains("wallet-cards", ProjectIconGlyphService.SelectMenu(["wallet"]));
        Assert.Contains("folder-archive", ProjectIconGlyphService.SelectMenu(["archive"]));
    }

    // Слова понятны, но в наборе нет ничего по смыслу («шарф ручной вязки», «самовар»):
    // добор общеупотребимыми выдавал бы четыре случайных значка за подбор. Пустое меню —
    // правильный ответ, проект честно остаётся на инициалах
    [Fact]
    public void СловаБезСовпадений_МенюПустоеБезДобора()
    {
        var menu = ProjectIconGlyphService.SelectMenu(["samovar", "kettlewarmer"]);

        Assert.Empty(menu);
    }

    // Многодетное слово не должно съедать меню целиком: «book» содержится в 39 именах
    // набора при MenuCap = 24. Если совпадения не раскладывать по кругу, остальные
    // понятия не дойдут до модели, и грид покажет четыре вариации одной иконки
    [Fact]
    public void МногодетноеСлово_НеВытесняетОстальныеПонятия()
    {
        var menu = ProjectIconGlyphService.SelectMenu(["book", "coffee", "music"]);

        Assert.Contains(menu, n => n.Contains("book", StringComparison.Ordinal));
        Assert.Contains(menu, n => n.Contains("coffee", StringComparison.Ordinal));
        Assert.Contains(menu, n => n.Contains("music", StringComparison.Ordinal));
        Assert.All(menu, name => Assert.True(LucideGlyphs.Contains(name)));
    }
}

// Двухходовая схема «меню вместо памяти» (мера 1) и повтор с подсказкой (мера 2):
// ход слов → отбор реальных имён сервером → ход выбора из меню → ровно один повтор
// при нуле годных, без цикла
public class ProjectIconTwoStepFlowTests
{
    // wallet даёт меню [wallet, wallet-2, wallet-cards, wallet-minimal] — точное
    // совпадение плюс подстрока; money и savings в наборе не встречаются
    private const string WordsWallet = """{"words":["wallet","money","savings"]}""";

    [Fact]
    public async Task ДвухХодовыйПодбор_СловаЗатемВыборИзМеню()
    {
        var cheap = new SequencedCheap(WordsWallet,
            """{"glyphs":[{"name":"wallet"},{"name":"wallet-minimal"}]}""");
        var logger = new CaptureLogger();

        var result = await new ProjectIconGlyphService(cheap, logger)
            .SuggestAsync("Копилка", null, "user-1");

        Assert.True(result.Ok);
        Assert.Equal(["wallet", "wallet-minimal"], result.Candidates.Select(c => c.Name));
        Assert.Equal(2, cheap.Prompts.Count);
        // Ход выбора получил меню: оба выбранных имени в него входили
        Assert.Contains("wallet", cheap.Prompts[1]);
        Assert.Contains("wallet-minimal", cheap.Prompts[1]);
        // Ход слов меню не несёт — он спрашивает понятия
        Assert.Contains("\"words\"", cheap.Prompts[0]);
        Assert.DoesNotContain("wallet-minimal", cheap.Prompts[0]);
    }

    [Fact]
    public async Task ПервыйХодСловНеУдался_ПодборПродолжаетсяНаОбщемМеню()
    {
        var cheap = new SequencedCheap("мусор без json",
            """{"glyphs":[{"name":"rocket"},{"name":"star"}]}""");
        var logger = new CaptureLogger();

        var result = await new ProjectIconGlyphService(cheap, logger)
            .SuggestAsync("Проект", null, "user-1");

        // Слова не разобраны — меню собрано из общеупотребимых имён, подбор не умер
        Assert.True(result.Ok);
        Assert.Equal(["rocket", "star"], result.Candidates.Select(c => c.Name));
        Assert.Equal(2, cheap.Prompts.Count);
        Assert.Contains(logger.Entries, e => e.Message.Contains("ход слов отвергнут"));
    }

    [Fact]
    public async Task МодельНеОтветилаНаПервомХоде_ПовторовНет()
    {
        var cheap = new SequencedCheap(new string?[] { null });

        var result = await new ProjectIconGlyphService(cheap, new CaptureLogger())
            .SuggestAsync("Проект", null, "user-1");

        Assert.Equal("no-model", result.FailReason);
        var prompt = Assert.Single(cheap.Prompts);   // второй ход не запускался
        Assert.Contains("\"words\"", prompt);
    }

    [Fact]
    public async Task МодельНеОтветилаНаВторомХоде_ТретийХодНеЗапускается()
    {
        var cheap = new SequencedCheap(WordsWallet, null);

        var result = await new ProjectIconGlyphService(cheap, new CaptureLogger())
            .SuggestAsync("Проект", null, "user-1");

        Assert.Equal("no-model", result.FailReason);
        Assert.Equal(2, cheap.Prompts.Count);
    }

    [Fact]
    public async Task ВыборВнеМеню_ОтбраковываетсяДажеДляРеальногоИмени()
    {
        // stethoscope существует в lucide, но в собранное меню не входит: выбор из памяти
        // разрушает меру 1 и отбраковывается, как чужое имя
        var cheap = new SequencedCheap(WordsWallet,
            """{"glyphs":[{"name":"stethoscope"}]}""",
            """{"glyphs":[{"name":"wallet"}]}""");

        var result = await new ProjectIconGlyphService(cheap, new CaptureLogger())
            .SuggestAsync("Клиника", null, "user-1");

        Assert.True(result.Ok);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("wallet", candidate.Name);
        Assert.Equal(3, cheap.Prompts.Count);
        // В повторе отбракованное имя перечислено
        Assert.Contains("stethoscope", cheap.Prompts[2]);
    }

    [Fact]
    public async Task ПромахНаВыборе_ПовторДаётГодноеИмя()
    {
        var cheap = new SequencedCheap(WordsWallet,
            """{"glyphs":[{"name":"super-kitty"},{"name":"nope"}]}""",
            """{"glyphs":[{"name":"wallet"},{"name":"wallet-2"}]}""");
        var logger = new CaptureLogger();

        var result = await new ProjectIconGlyphService(cheap, logger)
            .SuggestAsync("Копилка", null, "user-1");

        Assert.True(result.Ok);
        Assert.Equal(["wallet", "wallet-2"], result.Candidates.Select(c => c.Name));
        Assert.Equal(3, cheap.Prompts.Count);   // повтор ровно один
        // Подсказка повтора перечисляет отбракованное
        Assert.Contains("super-kitty", cheap.Prompts[2]);
        Assert.Contains("nope", cheap.Prompts[2]);
        Assert.Contains(logger.Entries, e => e.Message.Contains("повтор"));
    }

    // В наборе нет ничего по смыслу проекта: ход выбора не запускается вовсе — выбирать
    // не из чего, а четыре случайных значка хуже честных инициалов
    [Fact]
    public async Task НетИконокПоСмыслу_ХодВыбораНеЗапускается()
    {
        var cheap = new SequencedCheap("""{"words":["samovar","kettlewarmer"]}""");
        var logger = new CaptureLogger();

        var result = await new ProjectIconGlyphService(cheap, logger)
            .SuggestAsync("Самовар", null, "user-1");

        Assert.False(result.Ok);
        Assert.Equal("no-glyphs", result.FailReason);
        Assert.Single(cheap.Prompts);   // только ход слов, второго нет
        Assert.Contains(logger.Entries, e => e.Message.Contains("нет иконок по смыслу"));
    }

    // Подсказка повтора не должна врать: имя вне белого списка и настоящее имя мимо меню —
    // разные причины, и называть их надо разными словами (ревью 20.08.2026)
    [Fact]
    public async Task ПодсказкаПовтора_РазличаетВыдуманноеИмяИИмяМимоМеню()
    {
        // stethoscope существует в lucide, но в меню по словам про кошелёк не входит;
        // super-kitty не существует вовсе
        var cheap = new SequencedCheap(WordsWallet,
            """{"glyphs":[{"name":"stethoscope"},{"name":"super-kitty"}]}""",
            """{"glyphs":[{"name":"wallet"}]}""");

        var result = await new ProjectIconGlyphService(cheap, new CaptureLogger())
            .SuggestAsync("Клиника", null, "user-1");

        Assert.True(result.Ok);
        var retryPrompt = cheap.Prompts[2];
        // Про выдуманное — «в наборе НЕТ», про настоящее вне меню — «есть, но не предлагалось»
        Assert.Contains("super-kitty — таких имён в наборе НЕТ", retryPrompt);
        Assert.Contains("Имена stethoscope в наборе есть", retryPrompt);
        Assert.DoesNotContain("stethoscope — таких имён в наборе НЕТ", retryPrompt);
    }

    // На узком меню модель легко назовёт имя дважды: две одинаковые плитки в гриде
    // выглядят поломкой и занимают место осмысленного варианта
    [Fact]
    public async Task ДубликатыВОтветеМодели_СхлопываютсяВОдногоКандидата()
    {
        var cheap = new SequencedCheap(WordsWallet,
            """{"glyphs":[{"name":"wallet"},{"name":"wallet"},{"name":"wallet-2"}]}""");

        var result = await new ProjectIconGlyphService(cheap, new CaptureLogger())
            .SuggestAsync("Копилка", null, "user-1");

        Assert.Equal(["wallet", "wallet-2"], result.Candidates.Select(c => c.Name));
    }

    [Fact]
    public async Task НольГодныхПослеПовтора_ФолбэкНаИнициалыБезОшибки()
    {
        var cheap = new SequencedCheap(WordsWallet,
            """{"glyphs":[{"name":"super-kitty"}]}""",
            """{"glyphs":[{"name":"another-fake"}]}""");

        // Не исключение, а пустой результат: вызывающий оставляет проект на инициалах
        var result = await new ProjectIconGlyphService(cheap, new CaptureLogger())
            .SuggestAsync("Проект", null, "user-1");

        Assert.False(result.Ok);
        Assert.Empty(result.Candidates);
        Assert.Equal("name-out:another-fake", result.FailReason);
        Assert.Equal(3, cheap.Prompts.Count);   // повтор ровно один, цикла нет
    }
}

// Номера ProjectIconKind закреплены ЯВНО (ADR-009 §6): projects.json хранит enum числом,
// и перенумерация после удаления Image молча превращала бы старые записи в «значковые»
// с пустым значком — исключения не было бы, только тихая порча смысла.
public class ProjectIconKindNumberingTests
{
    [Fact]
    public void НомераЗначений_ЗакрепленыЯвно_ЕдиницаВыведенаИзОбращения()
    {
        Assert.Equal(0, (int)ProjectIconKind.Initials);
        Assert.Equal(2, (int)ProjectIconKind.Glyph);
    }

    [Fact]
    public void СтараяЗаписьСРастровойИконкой_ЧитаетсяБезИсключенияИНеСтановитсяЗначком()
    {
        // Kind=1 — бывший Image; ImageFile/OriginalFile/Crop полей у модели больше нет,
        // лишние поля десериализатор игнорирует. Стор ProjectManager читает ровно с этими
        // опциями (PropertyNameCaseInsensitive, без JsonStringEnumConverter).
        var icon = JsonSerializer.Deserialize<ProjectIcon>(
            """{"Kind":1,"Color":"blue","ImageFile":"icon-abc.png","OriginalFile":"original-abc.png","Crop":{"X":1,"Y":2,"Size":3}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(icon);
        Assert.NotEqual(ProjectIconKind.Glyph, icon!.Kind);   // не «значковый с пустым значком»
        Assert.Null(icon.Glyph);
        Assert.Equal("blue", icon.Color);
    }
}

// Лимит ожидания места «Значок проекта»: пер-местные 180 с применяются ко всем облачным
// шагам цепочки (выбранная модель, финальный claude) и НЕ меняют профильный потолок
// остальных Large-мест. Задача «таймаут сильной модели на подборе значка» (прод 17.08:
// отказы no-model при ответах 52–126 с, зависший вызов жил весь профильный потолок 300 с).
public class ProjectIconTimeoutTests
{
    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Фейковый claude-раннер: отвечает сразу, запоминает применённый таймаут вызова
    private sealed class CaptureTimeoutOneShot : IOneShotRunner
    {
        public readonly List<TimeSpan?> Timeouts = [];

        public string? NormalizeModel(string? model) => model;

        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        {
            Timeouts.Add(timeout);
            return Task.FromResult("""{"glyphs":[{"name":"wallet"}]}""");
        }

        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        {
            Timeouts.Add(timeout);
            return Task.FromResult(new OneShotResult("""{"glyphs":[{"name":"wallet"}]}""", null, 0));
        }
    }

    private static CheapTextRunner Runner(CaptureTimeoutOneShot claude)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = TestConfig.Build(new()
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            ["Ollama:Model"] = "",   // локаль выключена — цепочка сразу идёт на claude
        });
        var ollama = new OllamaClient(new NullHttpFactory(), config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama,
            new LocalActionOverridesStore(config,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalActionOverridesStore>.Instance),
            config, Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalActionRouter>.Instance);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudCheapClient>.Instance);
        return new CheapTextRunner(router, ollama, cloud, claude,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CheapTextRunner>.Instance);
    }

    [Fact]
    public async Task МестоЗначкаПроекта_ЛимитОблака180с_ВместоПотолкаПрофиля()
    {
        var claude = new CaptureTimeoutOneShot();
        var runner = Runner(claude);

        var raw = await runner.RunAsync(LocalActionCatalog.ProjectIcon, "промпт");

        Assert.Contains("wallet", raw);
        var timeout = Assert.Single(claude.Timeouts);
        Assert.Equal(TimeSpan.FromSeconds(180), timeout);
    }

    [Fact]
    public async Task СоседнееКрупноеМесто_ОстаётсяНаПотолкеПрофиля()
    {
        var claude = new CaptureTimeoutOneShot();
        var runner = Runner(claude);

        await runner.RunAsync(LocalActionCatalog.ProjectBackground, "промпт");

        // Профильный потолок не задран и не урезан: пер-местный лимит — только у значка
        var timeout = Assert.Single(claude.Timeouts);
        var profile = LocalActionCatalog.ProfileDefaults[CheapProfile.Large];
        Assert.Equal(TimeSpan.FromMilliseconds(profile.CloudTimeoutMs), timeout);
        Assert.Null(LocalActionCatalog.Find(LocalActionCatalog.ProjectBackground)!.CloudTimeoutMs);
    }

    // Сообщение отказа по времени называет применённый лимит и фактическую длительность
    // (требование задачи) и сохраняет подстроку-контракт ChangelogService.DescribeFailure
    [Theory]
    [InlineData(180_000, 179_500, "лимит 180 с, ждали 179.5 с")]
    [InlineData(null, 121_400, "лимит 120 с, ждали 121.4 с")]   // null = дефолт раннера 120 с
    public void СообщениеТаймаута_ЛимитИФактическаяДлительность(int? timeoutMs, int elapsedMs, string expected)
    {
        var message = OneShotClaudeRunner.TimeoutMessage(
            timeoutMs is null ? null : TimeSpan.FromMilliseconds(timeoutMs.Value),
            TimeSpan.FromMilliseconds(elapsedMs));

        Assert.Contains("не ответил за отведённое время", message);   // контракт DescribeFailure
        Assert.Contains(expected, message);
    }
}

// Причины отказа и диагностика двухходовки обязаны уходить уровнем Warning: файловый лог
// прода режет Information, а диагностика «почему значок не подобрался» должна доходить
// до файла вместе с именем проекта (задача команды)
public class ProjectIconGlyphLoggingTests
{
    private const string Words = """{"words":["wallet"]}""";

    [Fact]
    public async Task ПричиныОтказа_РазличимыВЛогеСИменемПроекта()
    {
        // (1) модель не ответила уже на ходе слов
        var noModel = new CaptureLogger();
        await Service(noModel, new SequencedCheap(new string?[] { null }))
            .SuggestAsync("Проект Альфа", null, "user-1");

        // (2) ответ не разобрался как JSON — и на выборе, и на повторе
        var badJson = new CaptureLogger();
        await Service(badJson, new SequencedCheap(Words, "мусор без json", "мусор без json"))
            .SuggestAsync("Проект Бета", null, "user-1");

        // (3) имя вне меню — и на выборе, и на повторе
        var nameOut = new CaptureLogger();
        await Service(nameOut, new SequencedCheap(Words,
            """{"glyphs":[{"name":"super-kitty"}]}""", """{"glyphs":[{"name":"super-kitty"}]}"""))
            .SuggestAsync("Проект Гамма", null, "user-1");

        // (4) ответ с путями вместо имени — ветка рисования вырезана
        var paths = new CaptureLogger();
        await Service(paths, new SequencedCheap(Words,
            """{"glyphs":[{"paths":["M3 21h18"]}]}""", """{"glyphs":[{"paths":["M3 21h18"]}]}"""))
            .SuggestAsync("Проект Дельта", null, "user-1");

        AssertWarning(noModel, "«Проект Альфа»", "модель не ответила");
        AssertWarning(badJson, "«Проект Бета»", "bad-json");
        AssertWarning(nameOut, "«Проект Гамма»", "name-out:super-kitty");
        AssertWarning(paths, "«Проект Дельта»", "glyph-shape:paths");
    }

    [Fact]
    public async Task ПовторСработал_ВЛогеОтбракованныеИмена()
    {
        var logger = new CaptureLogger();

        var result = await Service(logger, new SequencedCheap(Words,
            """{"glyphs":[{"name":"super-kitty"},{"name":"nope"}]}""",
            """{"glyphs":[{"name":"wallet"}]}"""))
            .SuggestAsync("Проект Гамма", null, "user-1");

        Assert.True(result.Ok);
        // Факт повтора и отбракованные имена — строкой Warning с именем проекта
        var retryLine = logger.Entries.First(
            e => e.Message.Contains("повтор") && e.Message.Contains("«Проект Гамма»"));
        Assert.Contains("super-kitty", retryLine.Message);
        Assert.Contains("nope", retryLine.Message);
    }

    [Fact]
    public async Task ГодныйПодбор_ВЛогеТолькоСводкаСДлительностямиХодов()
    {
        var logger = new CaptureLogger();

        var result = await Service(logger, new SequencedCheap(Words, """{"glyphs":[{"name":"wallet"}]}"""))
            .SuggestAsync("Проект Омега", null, "user-1");

        Assert.True(result.Ok);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // Никаких строк отказа — только сводка с длительностью каждого хода
        Assert.DoesNotContain("отвергнут", entry.Message);
        Assert.DoesNotContain("повтор", entry.Message);
        Assert.Contains("слова", entry.Message);
        Assert.Contains("выбор", entry.Message);
        Assert.Contains("мс", entry.Message);
        // Имена подобранного — в сводке: без них спорный подбор («звезда» для проекта
        // про шарф, живая приёмка 20.08.2026) по логу неотличим от осмысленного
        Assert.Contains("wallet", entry.Message);
    }

    private static void AssertWarning(CaptureLogger logger, string project, string fragment)
    {
        Assert.NotEmpty(logger.Entries);
        Assert.All(logger.Entries, e => Assert.Equal(LogLevel.Warning, e.Level));
        Assert.Contains(logger.Entries, e => e.Message.Contains(project) && e.Message.Contains(fragment));
    }

    private static ProjectIconGlyphService Service(CaptureLogger log, ICheapTextRunner cheap) =>
        new(cheap, log);
}

// Логгер, собирающий записи для утверждений
internal sealed class CaptureLogger : ILogger<ProjectIconGlyphService>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

// Последовательная подставная модель: ответы разбираются по порядку, null — модель
// «недоступна» (исключение). Лишний ход сверх ожидаемых упирается в «ответы исчерпаны» —
// так требование «без цикла» проверяется самим тестом
internal sealed class SequencedCheap(params string?[] answers) : ICheapTextRunner
{
    private readonly Queue<string?> _answers = new(answers);

    public List<string> Prompts { get; } = [];

    public bool UsesLocal(string actionKey) => false;
    public string DescribeRoute(string actionKey, string? fallbackModel) => "test";

    public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
    {
        Prompts.Add(prompt);
        if (_answers.Count == 0)
            throw new InvalidOperationException("ответы исчерпаны — сервис делает лишний ход");
        return Task.FromResult(_answers.Dequeue() ?? throw new InvalidOperationException("модель недоступна"));
    }

    public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default) =>
        throw new NotImplementedException();
    public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
        CancellationToken ct = default) => throw new NotImplementedException();
    public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt, string? fallbackModel = null,
        string? ownerId = null, TimeSpan? timeout = null, int? maxTokens = null,
        object? jsonFormat = null, CancellationToken ct = default) => throw new NotImplementedException();
}
