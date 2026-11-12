using System.Text;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа облачного судьи вики (J-J1a): чистая функция без состояния и без хранения —
// только текст модели и допустимые индексы пакета на входе, список вердиктов на выходе.
// Критерии карточки c1: валидный ответ; текст до и после JSON; index вне допустимых; повтор
// index; decision=rewrite; пустой why; мусор/пусто/null; why из 500 символов. Плюс то, чем
// модель чаще всего пытается тронуть лишнее: свои поля, строка вместо числа, лишняя проза.
public class WikiJudgeVerdictTests
{
    // Индексы операций этого пакета.
    private static readonly IReadOnlySet<int> Allowed = new HashSet<int> { 0, 1, 2 };

    // ─── 1. Валидный ответ ─────────────────────────────────────────────────

    [Fact]
    public void ВалидныйОтвет_ДаётВердикты_ПоОднойНаIndex_БезПропусков()
    {
        const string text = """
            {"verdicts":[
              {"index":0,"decision":"apply","why":"цитаты подтверждают claim"},
              {"index":1,"decision":"reject","why":"такого утверждения в цитатах нет"}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(0);
        verdicts.Should().HaveCount(2);
        verdicts[0].Should().Be(new WikiJudgeVerdict.Item(0, true, "цитаты подтверждают claim"));
        verdicts[1].Should().Be(new WikiJudgeVerdict.Item(1, false, "такого утверждения в цитатах нет"));
    }

    // ─── 2. Проза до и после JSON ──────────────────────────────────────────

    [Fact]
    public void ПрозаДоИПослеJSON_БерётсяПервыйСбалансированныйОбъект_ЛишняяСкобкаВПрозеНеЛомает()
    {
        // Хвост прозы содержит {1}: «первая { и последняя }» взяла бы хвост в объект и
        // дала бы нечитаемый JSON. Здесь берётся объект, закрытый по своему же { — проза
        // остаётся прозой.
        const string text = """
            Судья решил так: {"verdicts":[{"index":2,"decision":"reject","why":"противоречит тексту страницы"}]}
            Пояснение: пункт {1} пакета я не трогал.
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(0);
        Assert.Single(verdicts).Should().Be(new WikiJudgeVerdict.Item(2, false, "противоречит тексту страницы"));
    }

    [Fact]
    public void СкобкиВСамомWhyНеЛомаютРазбор_ЭкранированныеКавычкиТоже()
    {
        const string text = """
            {"verdicts":[{"index":0,"decision":"apply","why":"в цитате «{нет}» и \"кавычки\" — всё есть"}]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(0);
        verdicts.Should().HaveCount(1);
        verdicts[0].Why.Should().Be("в цитате «{нет}» и \"кавычки\" — всё есть");
    }

    // ─── 3. Index вне допустимых ───────────────────────────────────────────

    [Fact]
    public void IndexВнеПакета_ПропущенИПосчитан_СоседнийВердиктЖив()
    {
        const string text = """
            {"verdicts":[
              {"index":7,"decision":"apply","why":"этой операции мне не давали"},
              {"index":0,"decision":"apply","why":"по этой — всё сходится"}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(1);
        Assert.Single(verdicts).Should().Be(new WikiJudgeVerdict.Item(0, true, "по этой — всё сходится"));
    }

    // ─── 4. Повтор index: берётся первый ───────────────────────────────────

    [Fact]
    public void ПовторIndex_БерётсяПервый_ВторойПропущен()
    {
        const string text = """
            {"verdicts":[
              {"index":1,"decision":"apply","why":"первый вердикт"},
              {"index":1,"decision":"reject","why":"второй вердикт на тот же индекс"}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(1);
        Assert.Single(verdicts).Should().Be(new WikiJudgeVerdict.Item(1, true, "первый вердикт"));
    }

    [Fact]
    public void ПовторIndexНеПолучаетВердиктаДажеЕслиПервыйБылсяБракованным()
    {
        // Первое вхождение индекса занято им одним: второй попыткой модель не получает
        // вердикт вместо того, который не смогла сформулировать.
        const string text = """
            {"verdicts":[
              {"index":1,"decision":"apply","why":""},
              {"index":1,"decision":"apply","why":"вторая попытка"}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(2);
        verdicts.Should().BeEmpty();
    }

    // ─── 5. Decision не apply/reject ───────────────────────────────────────

    [Theory]
    [InlineData("rewrite")]
    [InlineData("maybe")]
    [InlineData("apply_with_edit")]
    [InlineData("")]
    public void DecisionНеApplyReject_Пропущен(string decision)
    {
        var text = $$"""{"verdicts":[{"index":0,"decision":"{{decision}}","why":"есть объяснение"}]}""";

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(1);
        verdicts.Should().BeEmpty();
    }

    [Fact]
    public void DecisionБезПоляИНеСтрокой_Пропущен()
    {
        const string text = """
            {"verdicts":[
              {"index":0,"why":"нет поля decision"},
              {"index":1,"decision":true,"why":"не строка"}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(2);
        verdicts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("apply", true)]
    [InlineData("reject", false)]
    [InlineData("Apply", true)]
    [InlineData(" REJECT ", false)]
    public void DecisionЧитаетсяБезРегистраПробелов(string decision, bool expectedApply)
    {
        var text = $$"""{"verdicts":[{"index":0,"decision":"{{decision}}","why":"объяснение есть"}]}""";

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(0);
        verdicts.Should().HaveCount(1);
        verdicts[0].Apply.Should().Be(expectedApply);
    }

    // ─── 6. Пустой why ─────────────────────────────────────────────────────

    // \n\t здесь — экранированные последовательности JSON: в значении строки это настоящие
    // перевод строки и таб, а не control-символ, который сделал бы JSON нечитаемым.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\\n\\t")]
    public void ПустойИлиПробельныйWhy_Пропущен(string why)
    {
        var text = $$"""{"verdicts":[{"index":0,"decision":"apply","why":"{{why}}"}]}""";

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(1);
        verdicts.Should().BeEmpty();
    }

    [Fact]
    public void WhyБезПоляИНеСтрокой_Пропущен()
    {
        const string text = """
            {"verdicts":[
              {"index":0,"decision":"reject"},
              {"index":1,"decision":"reject","why":null}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(2);
        verdicts.Should().BeEmpty();
    }

    // ─── 7. Мусор, пусто, null ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Я не могу судить эти операции.")]
    [InlineData("{")]
    [InlineData("{\"verdicts\":[")]
    [InlineData("{\"verdicts\":{}}")]
    [InlineData("{\"other\":[1,2]}")]
    public void МусорПустоNull_НольВердиктовБезИсключения(string? text)
    {
        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        verdicts.Should().BeEmpty();
        // Разобранного списка item-ов нет — считать пропуск не по чему.
        skipped.Should().Be(0);
    }

    [Theory]
    // Отличается от входов выше: здесь FirstJsonObject доходит до закрытия по своей же {
    // (глубина обнуляется) и отдаёт вынутый объект в JsonDocument.Parse — а JSON внутри
    // нечитаемый: незакрытый массив или лишняя } внутри него. Держит такой ответ только
    // catch (JsonException) в Parse: станет rethrow — нечитаемый JSON долетает до
    // вызывающего исключением.
    [InlineData("{\"verdicts\":[}")]
    [InlineData("{\"verdicts\":[{\"index\":0,\"decision\":\"apply\",\"why\":\"x\"}}]")]
    public void СбалансированныйНоНечитаемыйJSON_НольВердиктовБезИсключения(string text)
    {
        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        verdicts.Should().BeEmpty();
        // Разобранного списка item-ов нет — считать пропуск не по чему.
        skipped.Should().Be(0);
    }

    [Fact]
    public void IndexНеЧислом_Пропущен_ItemНеОбъектПропущен()
    {
        const string text = """
            {"verdicts":[
              {"index":"0","decision":"apply","why":"строка вместо числа"},
              {"index":1,"decision":"apply","why":"есть"},
              "index: 2, decision: apply"
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(2);
        Assert.Single(verdicts).Should().Be(new WikiJudgeVerdict.Item(1, true, "есть"));
    }

    // ─── 8. Why длиннее потолка ────────────────────────────────────────────

    [Fact]
    public void WhyИз500Символов_ОбрезанРовноДо300()
    {
        var longWhy = new string('к', 500);
        var text = $$"""{"verdicts":[{"index":0,"decision":"apply","why":"{{longWhy}}"}]}""";

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(0);
        verdicts.Should().HaveCount(1);
        verdicts[0].Why.Should().HaveLength(WikiJudgeVerdict.MaxWhyChars);
        verdicts[0].Why.Should().Be(longWhy[..WikiJudgeVerdict.MaxWhyChars]);
    }

    [Fact]
    public void WhyРовно300Символов_НеТрогается()
    {
        var exact = new string('р', WikiJudgeVerdict.MaxWhyChars);
        var text = $$"""{"verdicts":[{"index":0,"decision":"reject","why":"{{exact}}"}]}""";

        var (verdicts, _) = WikiJudgeVerdict.Parse(text, Allowed);

        verdicts[0].Why.Should().Be(exact);
    }

    // ─── Лишние поля: модель не трогает ничего сверх вердикта ─────────────

    [Fact]
    public void ЛишниеПоляИгнорируются_ВердикТТолькоIndexDecisionWhy()
    {
        const string text = """
            {"verdicts":[
              {"index":0,"decision":"apply","why":"всё сходится",
               "newText":"переписанный судьёй текст",
               "claim":"своя формулировка",
               "extra":{"page":"wiki/новая-страница.md","confidence":0.9}}
            ]}
            """;

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, Allowed);

        skipped.Should().Be(0);
        Assert.Single(verdicts).Should().Be(new WikiJudgeVerdict.Item(0, true, "всё сходится"));
    }

    // ─── Пустой allowedIndexes: ни один вердикт не принимается ─────────────

    [Fact]
    public void ПустойСписокИндексов_НольВердиктов()
    {
        const string text = """{"verdicts":[{"index":0,"decision":"apply","why":"есть"}]}""";

        var (verdicts, skipped) = WikiJudgeVerdict.Parse(text, new HashSet<int>());

        skipped.Should().Be(1);
        verdicts.Should().BeEmpty();
    }

    [Fact]
    public void ПустойМассивVerdicts_НольВердиктовБезПропусков()
    {
        var (verdicts, skipped) = WikiJudgeVerdict.Parse("""{"verdicts":[]}""", Allowed);

        verdicts.Should().BeEmpty();
        skipped.Should().Be(0);
    }
}
