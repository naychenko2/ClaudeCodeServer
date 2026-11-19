using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Q2-A2: чистая функция WikiAutoLink — автолинковка текста по заголовкам страниц раздела
// и цепочки вверх. Фикстуры — полные страницы (WikiPage.Parse строгий) и текст-вход Apply;
// без диска, без модели. Каждая фикстура несёт ровно один проверяемый случай.
public class WikiAutoLinkTests
{
    private const string Self = "Проекты/p1/Своя.md";
    private const string SectionKey = "Проекты/p1";

    private static WikiPage Topic(string title, string[]? aliases = null) =>
        WikiPage.Parse($"""
            ---
            type: topic
            aliases: [{(aliases is null ? "" : string.Join(", ", aliases))}]
            owns: []
            sources: []
            status: active
            ---

            # {title}

            ## Сводка

            ## Хронология

            ## Противоречия

            ## История

            """).Model!;

    private static WikiPage SourceCard(string title) =>
        WikiPage.Parse($"""
            ---
            type: source
            origin: session
            ref: session:s1#m1-9
            hash: 0123456789012345678901234567890123456789012345678901234567890123
            captured: 2026-11-19
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # {title}
            """).Model!;

    private static WikiPage Chronicle(string title) =>
        WikiPage.Parse($"""
            ---
            type: chronicle
            aliases: []
            owns: []
            sources: []
            status: active
            ---

            # {title}

            ## О чём

            ## Что решили

            ## Что выяснили

            ## Открытые вопросы

            """).Model!;

    private static (string Section, WikiPage Page, string Path) Entry(
        string section, string path, WikiPage page) => (section, page, path);

    private static WikiAutoLink.Result Apply(
        string text,
        IReadOnlyList<(string Section, WikiPage Page, string Path)> snapshot,
        IReadOnlyList<string>? chain = null) =>
        WikiAutoLink.Apply(text, Self, SectionKey, chain ?? [], snapshot);

    // ─── линковка точного совпадения ───

    [Fact]
    public void ТочноеСовпадение_Линкуется_КаноническимЗаголовком()
    {
        // «Печёный» в тексте и «Печеный» в заголовке — одно совпадение после нормализации
        // (NFC, нижний регистр, ё→е); ссылка — канонический заголовок страницы.
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Печеный.md", Topic("Печеный")) };
        var res = Apply("Хлеб Печёный свежий.", snapshot);

        res.Text.Should().Be("Хлеб [[Печеный]] свежий.");
        res.Added.Should().Be(1);
        res.MissedWordForms.Should().Be(0);
    }

    [Fact]
    public void Идемпотентность_ПовторныйApply_НеМеняетТекст()
    {
        // f(f(x)) == f(x): поставленные ссылки — защищённый интервал, второй прогон не
        // трогает их и не ставит дублей. В фикстуре — и длиннейшее совпадение, и ё→е.
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/Каталог-Исходники.md", Topic("Каталог Исходники")),
            Entry(SectionKey, "Проекты/p1/Исходники.md", Topic("Исходники")),
            Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")),
            Entry(SectionKey, "Проекты/p1/Печеный.md", Topic("Печеный")),
        };
        var first = Apply("Сборка Модуля и Каталог Исходники.\nПечёный хлеб.", snapshot);
        first.Text.Should().Be("[[Сборка Модуля]] и [[Каталог Исходники]].\n[[Печеный]] хлеб.");
        first.Added.Should().Be(3);

        var second = Apply(first.Text, snapshot);
        second.Text.Should().Be(first.Text);
        second.Added.Should().Be(0);
    }

    [Fact]
    public void ДлинейшееСовпадение_Первым_КороткоеВнутриНегоНеЛинкуется()
    {
        // «Каталог Исходники» раньше «Исходники»: длинное совпадение занимает интервал,
        // короткое внутри него — то же упоминание, второй ссылки не будет.
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/Исходники.md", Topic("Исходники")),
            Entry(SectionKey, "Проекты/p1/Каталог-Исходники.md", Topic("Каталог Исходники")),
        };
        var res = Apply("Каталог Исходники собран.", snapshot);

        res.Text.Should().Be("[[Каталог Исходники]] собран.");
        res.Added.Should().Be(1);
    }

    [Fact]
    public void ОднаЦель_ОднаСсылкаНаТекст_ПервоеВхождение()
    {
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")) };
        var res = Apply("Сборка Модуля тут.\nСборка Модуля там.", snapshot);

        res.Text.Should().Be("[[Сборка Модуля]] тут.\nСборка Модуля там.");
        res.Added.Should().Be(1);
    }

    [Fact]
    public void СтрокаПолучаетНеБольшеТрёхНовыхСсылок()
    {
        // Потолок считается по строке: четвёртая цель в той же строке остаётся нелинкованной.
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/Каталог-Исходники.md", Topic("Каталог Исходники")),
            Entry(SectionKey, "Проекты/p1/Резервуар-Пароля.md", Topic("Резервуар Пароля")),
            Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")),
            Entry(SectionKey, "Проекты/p1/Печеный-Хлеб.md", Topic("Печеный Хлеб")),
        };
        var res = Apply("Каталог Исходники, Резервуар Пароля, Сборка Модуля и Печеный Хлеб.", snapshot);

        res.Added.Should().Be(WikiAutoLink.MaxLinksPerLine);
        res.Text.Should().NotContain("[[Печеный Хлеб]]");
    }

    // ─── инвариант: ни одна поставленная ссылка не уходит вбок ───

    [Fact]
    public void Sideways_NotLinked()
    {
        // «Резервуар Пароля» живёт только в чужом разделе: ResolveLink даёт Sideways —
        // цели нет, ссылки нет. Мутация «убрать проверку Resolved в цель» линкует её —
        // краснеет ровно этот тест (c2 карточки).
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/Местная-Тема.md", Topic("Местная Тема")),
            Entry("Проекты/p2", "Проекты/p2/Резервуар-Пароля.md", Topic("Резервуар Пароля")),
        };
        var res = Apply("Резервуар Пароля и Местная Тема.", snapshot);

        res.Text.Should().NotContain("[[Резервуар");
        res.Text.Should().Contain("[[Местная Тема]]");
        res.Added.Should().Be(1);
    }

    [Fact]
    public void ЦепочкаВверх_Линкуется_СвояЦель()
    {
        // Страница common — в цепочке вверх: ResolveLink Resolved в common — цель допустима.
        var snapshot = new[]
        {
            Entry("common", "common/Общий-Порядок.md", Topic("Общий Порядок")),
        };
        var res = Apply("Общий Порядок описан.", snapshot, chain: ["common"]);

        res.Text.Should().Be("[[Общий Порядок]] описан.");
        res.Added.Should().Be(1);
    }

    // ─── что не линкуется и не считается ───

    [Fact]
    public void ОднословныйЗаголовок_КороткийИСтопСлово_НеЛинкуется()
    {
        // Однословная цель — от 6 букв и не из стоп-списка: «План» короче порога,
        // «Заметка» — доменное стоп-слово.
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/План.md", Topic("План")),
            Entry(SectionKey, "Проекты/p1/Заметка.md", Topic("Заметка")),
        };
        var res = Apply("План и Заметка составлены.", snapshot);

        res.Text.Should().Be("План и Заметка составлены.");
        res.Added.Should().Be(0);
    }

    [Fact]
    public void SourceChronicle_IХорошиеПроекции_IСвояСтраница_НеЦели()
    {
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/Карточка-Источника.md", SourceCard("Карточка Источника")),
            Entry(SectionKey, "Проекты/p1/Ночной-Разбор.md", Chronicle("Ночной Разбор")),
            Entry(SectionKey, "Проекты/p1/_Индекс.md", Topic("_Индекс")),
            Entry(SectionKey, Self, Topic("Собственная Тема")),
        };
        var res = Apply("Карточка Источника, Ночной Разбор, _Индекс и Собственная Тема.", snapshot);

        res.Text.Should().Be("Карточка Источника, Ночной Разбор, _Индекс и Собственная Тема.");
        res.Added.Should().Be(0);
    }

    [Fact]
    public void ФразаЦифрой_НеЛинкуетсяIHеСчитается()
    {
        // Правило чисел claim не ослабляется: «сборка 2026 модуля» — не цель и не замер;
        // чистое вхождение той же фразы линкуется.
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")) };
        var res = Apply("Сборка 2026 Модуля. Сборка Модуля чистая.", snapshot);

        res.Text.Should().Be("Сборка 2026 Модуля. [[Сборка Модуля]] чистая.");
        res.Added.Should().Be(1);
        res.MissedWordForms.Should().Be(0);
    }

    [Fact]
    public void Кодспан_НеЛинкуется()
    {
        // «Сборка Модуля» внутри `…` — пример синтаксиса, не упоминание (NotesService.CodeMask).
        // Мутация «снять маску» линкует внутри кода — краснеет ровно этот тест (c3 карточки).
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")) };
        var res = Apply("Запись `Сборка Модуля` вне кода не линкуется.", snapshot);

        res.Text.Should().Be("Запись `Сборка Модуля` вне кода не линкуется.");
        res.Added.Should().Be(0);
    }

    [Fact]
    public void СуществующаяСсылка_НеТрогается_И_СПустымАлиасом()
    {
        // Хвост 15: [[Сборка|]] — уже стоящая ссылка (пустой алиас после «|»): цель уже
        // связана, текст не трогается, дубль не ставится, словоформы связь не добавляют.
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка.md", Topic("Сборка")) };
        var res = Apply("[[Сборка|]] стоит, рядом сборка.", snapshot);

        res.Text.Should().Be("[[Сборка|]] стоит, рядом сборка.");
        res.Added.Should().Be(0);
        res.MissedWordForms.Should().Be(0);
    }

    [Fact]
    public void СноскаИЕёОпределение_НеТрогаются()
    {
        // Ссылка сноски [^s1] — защищённый интервал; строка определения — вне автолинкования
        // целиком (цель сноски — карточка, а не страница знаний).
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")) };
        var text = "Утверждение [^s1].\n[^s1]: Сборка Модуля — «текст цитаты»";
        var res = Apply(text, snapshot);

        res.Text.Should().Be(text);
        res.Added.Should().Be(0);
        res.MissedWordForms.Should().Be(0);
    }

    [Fact]
    public void СекцииИсторияIZametki_НеЛинкуются()
    {
        // «История» — запись прошлого, «Заметки» — текст человека: ни та ни другая строка
        // не линкуется; «Сводка» — линкуется.
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")) };
        var text = """
            # Тестовая Страница

            ## Сводка
            Сборка Модуля в сводке.

            ## Хронология

            ## Противоречия

            ## История
            Сборка Модуля в истории.

            ## Заметки
            Сборка Модуля в заметках.

            """;
        var res = Apply(text, snapshot);

        res.Text.Should().Be(text.Replace("Сборка Модуля в сводке.", "[[Сборка Модуля]] в сводке."));
        res.Added.Should().Be(1);
    }

    // ─── MissedWordForms: вход замера A0 ───

    [Fact]
    public void Словоформа_НеЛинкуетсяТекстНеИзменён_MissedWordFormsRavno1()
    {
        // «Сборки Модуля» — словоформа заголовка (совпадение по основам, не точное):
        // текст не трогается, цель считается один раз — это потенциальная связь B5.
        var snapshot = new[] { Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля")) };
        var res = Apply("Сборки Модуля составлены.", snapshot);

        res.Text.Should().Be("Сборки Модуля составлены.");
        res.Added.Should().Be(0);
        res.MissedWordForms.Should().Be(1);
    }

    [Fact]
    public void ТочноеВхождение_ПодавляетСловоформу_И_Алиас_НеЛинкуется_НоСчитается()
    {
        // Цель с точным вхождением уже получит ссылку — её словоформа связи не добавляет
        // (0). Цель только со словом алиаса — алиасом в A2 не линкуем, но это одна
        // потенциальная связь (1).
        var snapshot = new[]
        {
            Entry(SectionKey, "Проекты/p1/Сборка-Модуля.md", Topic("Сборка Модуля", ["Реестр Сборок"])),
            Entry(SectionKey, "Проекты/p1/Печеный.md", Topic("Печеный", ["Запись Печеного"])),
        };
        var res = Apply("Сборка Модуля точная, сборки модуля тоже.\nЗапись Печеного ведётся.", snapshot);

        res.Text.Should().Be("[[Сборка Модуля]] точная, сборки модуля тоже.\nЗапись Печеного ведётся.");
        res.Added.Should().Be(1);
        res.MissedWordForms.Should().Be(1);
    }
}
