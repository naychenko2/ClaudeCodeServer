using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Q1-S3 (1А): чистая функция WikiCandidates — значимые слова и порядок кандидатов.
// Фикстуры — тексты полных страниц (WikiPage.Parse строгий: канонический порядок секций),
// без диска и без модели. Каждая фикстура несёт ровно один сигнал нужного вида и ни одного
// лишнего — иначе тест не различает правила.
public class WikiCandidatesTests
{
    private static WikiGate.Draft Draft(string path, string content) => new(path, content);

    private static string Page(
        string title, string swodka = "", string type = "topic", string[]? aliases = null) =>
        $"""
        ---
        type: {type}
        aliases: [{(aliases is null ? "" : string.Join(", ", aliases))}]
        owns: []
        sources: []
        status: active
        ---

        # {title}

        ## Сводка
        {swodka}

        ## Хронология

        ## Противоречия

        ## История

        """;

    private static string SourceCard(string title) =>
        $"""
        ---
        type: source
        origin: session
        ref: session:s1#m1-9
        hash: 0123456789012345678901234567890123456789012345678901234567890123
        captured: 2026-10-29
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {title}
        """;

    // ── значимые слова ──

    [Fact]
    public void ЗначимыеСлова_КороткиеСловаИСтопСловаНеДаютОснов()
    {
        // Слова короче 4 букв не становятся токенами (c1); служебные слова из стоп-списка
        // не становятся основами — ни в окне, ни в странице.
        WikiCandidates.SignificantWords("он был её сон я тут").Should().BeEmpty();
        WikiCandidates.SignificantWords("этого очень когда можно только").Should().BeEmpty();
    }

    [Fact]
    public void ЗначимыеСлова_ЁРавнаЕ_ИФлексииДаютОднуОснову()
    {
        // «Печёный» и «печеный» — одна основа; «сборки» и «сборка» — одна «сборк» (c1);
        // слова длинее 6 символов обрезаны к одной основе.
        WikiCandidates.SignificantWords("Печёный").Should().Equal(WikiCandidates.SignificantWords("печеный"));
        WikiCandidates.SignificantWords("сборки").Should().Equal(WikiCandidates.SignificantWords("сборка"));
        WikiCandidates.SignificantWords("решения").Should().Equal(WikiCandidates.SignificantWords("решение"));
    }

    [Fact]
    public void ЗначимыеСлова_МорфологияИзПостановки_ПоимённоИWorktree()
    {
        // Реальные пары из постановки Q1-S3 (1А), ранее не зафиксированные: «поимённо» и
        // «поимённый» дают одну основу «поиме» (ё→е, слово длиннее 6 символов → первые 6 без
        // последней буквы), «worktree» и «worktree-а» — одну «workt» (дефис — разделитель,
        // «а» короче значимого токена).
        WikiCandidates.SignificantWords("поимённо").Should().Equal("поиме");
        WikiCandidates.SignificantWords("поимённый").Should().Equal("поиме");
        WikiCandidates.SignificantWords("worktree").Should().Equal("workt");
        WikiCandidates.SignificantWords("worktree-а").Should().Equal("workt");
    }

    [Fact]
    public void ЗначимыеСлова_ДоменныеСловаВики_НеДаютОснов()
    {
        // Инвариант доработки: слово из доменного стоп-списка не даёт очков кандидату.
        // Окно, собранное только из таких слов, не даёт основ вовсе. Мутация «выбросить
        // „страницы" из стоп-списка»: основа «стран» появляется и в этом окне, и в заголовке
        // фикстуры ОкноИзОбщихСловВики_… — краснеют ровно эти два теста (проверено на копии:
        // FAILED 2 из 88), других она не трогает.
        WikiCandidates.SignificantWords("страницы раздела сводки источника заметки").Should().BeEmpty();
    }

    // ── порядок и отбор ──

    [Fact]
    public void ДословныйЗаголовок_Первым_ДажеПротивБолееВысокойОценки()
    {
        // «Перенос сборки» стоит в окне дословно — это первый кандидат, как сейчас,
        // хотя у второй страницы оценка выше (заголовок и сводка дают больше пересечений).
        // Мутация «снять приоритет дословного совпадения» (Exact → обычный счёт): первой
        // идёт Б по оценке — тест красный.
        var window = "Решение про перенос сборки на Linux. Контейнер собран.";
        var literal = Draft("Проекты/p1/А.md", Page("Перенос сборки"));
        var scored = Draft("Проекты/p1/Б.md", Page("Контейнер Linux собран", "контейнер linux"));

        var picked = WikiCandidates.Pick([scored, literal], window, 3);

        picked.Select(d => d.Path).Should().Equal(new[] { "Проекты/p1/А.md", "Проекты/p1/Б.md" },
            "дословное совпадение заголовка — первая очередь, оценка — после него");
    }

    [Fact]
    public void ЗаголовокВеситБольшеСводки_ТотЖеСлово()
    {
        // Одно общее значимое слово: у одной страницы в заголовке, у другой — в сводке.
        // Страница заголовка идёт раньше — несмотря на то, что её pageKey позже (c2).
        // Мутация «TitleWeight = 1»: оценки сравнялись (1 против 1), порядок по pageKey —
        // первой идёт «А» (сводка), именно этот тест красный; других тестов ×1 не трогает
        // (в них порядок держится на дословном совпадении, pageKey или потолке).
        var window = "перенос сборки";
        var inTitle = Draft("Проекты/p1/В.md", Page("Заказ переноса"));
        var inSummary = Draft("Проекты/p1/А.md", Page("Заказ заказа", "перенос"));

        var picked = WikiCandidates.Pick([inTitle, inSummary], window, 6);

        picked.Select(d => d.Path).Should().Equal(new[] { "Проекты/p1/В.md", "Проекты/p1/А.md" },
            "пересечение с заголовком весит ×2 против ×1 у сводки");
    }

    [Fact]
    public void ОценкаНоль_ВВыдачуНеБерётся()
    {
        // «Зимняя вишня» не имеет с окном ни одной общей основы: ни дословного совпадения, ни
        // пересечений — страницы нет в выдаче (c1).
        var window = "перенос сборки";
        var cold = Draft("Проекты/p1/Холодно.md", Page("Зимняя вишня", "ничего общего"));
        var warm = Draft("Проекты/p1/Тепло.md", Page("Сборка"));

        var picked = WikiCandidates.Pick([cold, warm], window, 6);

        picked.Select(d => d.Path).Should().Equal(new[] { "Проекты/p1/Тепло.md" },
            "без пересечения и без дословного совпадения страница не кандидат");
    }

    [Fact]
    public void ОкноИзОбщихСловВики_НеДаётКандидатов_ДажеПротивЗаголовкаСНими()
    {
        // Окно стоит из одних доменных слов вики; у страницы они же — в заголовке и в сводке.
        // Ни пересечения (слова сняты стоп-списком), ни дословного совпадения («оформление
        // страницы» в окне не встречается): страница не кандидат. Без стоп-списка заголовок
        // дал бы ×2 — любая страница с общим словом попадала бы в промпт любой заявки.
        // Мутация «выбросить „страницы" из стоп-списка»: и в окне, и в заголовке остаётся
        // основа «стран» → оценка 2 → краснеют ровно этот тест и ЗначимыеСлова_Доменные…
        // (тот же инвариант на двух уровнях). Форма слова в окне и в заголовке намеренно
        // одна («страницы»): при разных формах выпадение одной из них не дало бы
        // пересечения, и мутация прошла бы незамеченной.
        var window = "страницы раздела сводки источника заметки";
        var page = Draft("Проекты/p1/Оформление-страницы.md",
            Page("Оформление страницы", "сводки источника и заметки"));

        WikiCandidates.Pick([page], window, 6).Should().BeEmpty();
    }

    [Fact]
    public void ПотолокN_ИПриРавнойОценке_ПорядокПоPageKey()
    {
        // Восемь равных по оценке страниц поданы в обратном порядке: выдача — первые
        // TopN=5 по pageKey (потолок N; детерминизм при равной оценке). TopN равен
        // MaxCandidatePages: шестой страницы, всегда отрезаемой в PickCandidatePages,
        // больше нет. Мутация «игнорировать n»: восемь вместо пяти — тест красный.
        WikiCandidates.TopN.Should().Be(5);
        var window = "сборки";
        var drafts = new List<WikiGate.Draft>();
        for (var i = 8; i >= 1; i--)
            drafts.Add(Draft($"Проекты/p1/П{i}.md", Page($"Сборка {i}")));

        var picked = WikiCandidates.Pick(drafts, window, WikiCandidates.TopN);

        picked.Select(d => d.Path).Should().Equal(new[]
        {
            "Проекты/p1/П1.md", "Проекты/p1/П2.md", "Проекты/p1/П3.md",
            "Проекты/p1/П4.md", "Проекты/p1/П5.md",
        }, "при равной оценке порядок детерминирован: по pageKey, не по порядку снимка");
    }

    [Fact]
    public void КарточкаSource_НеКандидат_ДажеСДословнымЗаголовком()
    {
        // Карточка стоит в окне дословно — и всё равно не кандидат: она цель сносок,
        // а не страница знаний (тот же предикат, что у PickCandidatePages).
        var window = "Карточка чата";
        var card = Draft("Проекты/p1/Источники/Карточка-чата.md", SourceCard("Карточка чата"));

        WikiCandidates.Pick([card], window, 6).Should().BeEmpty();
    }

    [Fact]
    public void НеразобраннаяСтраница_НеКандидат()
    {
        var window = "сборки";
        var broken = Draft("Проекты/p1/Битая.md", "произвольный текст без шапки и заголовка про сборки");

        WikiCandidates.Pick([broken], window, 6).Should().BeEmpty();
    }

    [Fact]
    public void АлиасДаётОценку_КогдаЗаголовокБезСовпадения()
    {
        var window = "перенос сборки";
        var aliased = Draft("Проекты/p1/Алиас.md", Page("Другой предмет", aliases: ["Сборка заказа"]));

        WikiCandidates.Pick([aliased], window, 6).Should().ContainSingle();
    }

    [Fact]
    public void ДословныйАлиас_БерётсяДажеБезОценки()
    {
        // «Сны» короче значимого токена — пересечений нет, оценка 0; дословное совпадение
        // алиаса остаётся группой первой очереди, как сейчас (прежний предикат).
        var window = "сны";
        var aliased = Draft("Проекты/p1/Сновидения.md", Page("Другой предмет", aliases: ["Сны"]));

        WikiCandidates.Pick([aliased], window, 6).Should().ContainSingle();
    }
}
