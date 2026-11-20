using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// C1 (перепостановка 20.11, консультация a2b00801): дубль по предмету — вложение основ
// заголовков (WikiTopicScore.TitleSubset, без порога) только по страницам своего раздела.
// Мера «По теме» такую пару не ловит: в своём разделе слова-предметы стоят почти в каждом
// заголовке, их IDF мал. Точное имя проверяется первым, порядок не меняется; точность
// даёт судья («Входящие», page_duplicate).
public class WikiPageDuplicateSubjectTests
{
    private static WikiPage Page(string title) => new()
    {
        Title = title,
        Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeTopic },
        Sections = [],
    };

    private static WikiDedup.ExistingPage Existing(string path, WikiPage page) => new(path, page);

    [Fact]
    public void FindPageDuplicate_ОсновыНовогоЗаголовкаВходятВОсновыСтраницыСвоегоРаздела_ВозможныйДубльПоПредмету()
    {
        // Та самая пара из перепостановки: «Каталог Исходники» / «Содержимое каталога Исходники».
        // Точного имени нет; основы меньшего заголовка («катал…», «исход…») целиком входят
        // в больший и их не меньше MinCommon — дубль по предмету, Field = subject.
        var inSection = Existing("Проекты/p1/Каталог Исходники.md", Page("Каталог Исходники"));

        var dup = WikiDedup.FindPageDuplicate(
            "Содержимое каталога Исходники", null, null, [inSection], [inSection]);

        dup.HasValue.Should().BeTrue();
        var d = dup.GetValueOrDefault();
        d.Path.Should().Be("Проекты/p1/Каталог Исходники.md");
        d.Field.Should().Be("subject");
        d.ExistingField.Should().Be("заголовок");
    }

    [Fact]
    public void FindPageDuplicate_ОсновыНовогоЗаголовкаНеПодмножество_ДубляНет()
    {
        // «фронт» в основы «Конвейер сборки» не входит: не подмножество — не дубль.
        var inSection = Existing("Проекты/p1/Конвейер сборки.md", Page("Конвейер сборки"));

        var dup = WikiDedup.FindPageDuplicate(
            "Сборка фронта", null, null, [inSection], [inSection]);

        dup.HasValue.Should().BeFalse();
    }

    [Fact]
    public void FindPageDuplicate_ПараПоПредметуТолькоВЦепочкеВверх_ДубляНет()
    {
        // Та же пара c1, но существующая страница — из цепочки вверх (common), не своего
        // раздела: дубль по предмету её не касается, точного имени нет — дубля нет.
        var inChain = Existing("Общее/Каталог Исходники.md", Page("Каталог Исходники"));

        var dup = WikiDedup.FindPageDuplicate(
            "Содержимое каталога Исходники", null, null, [inChain], []);

        dup.HasValue.Should().BeFalse();
    }

    [Fact]
    public void FindPageDuplicate_ОдноСловоНовогоЗаголовкаКорочеMinCommon_ДубляНет()
    {
        // Одно слово («адапт») — не предмет: меньший набор короче MinCommon, даже когда
        // он вошёл бы в больший.
        var inSection = Existing("Проекты/p1/Адаптер шины.md", Page("Адаптер шины"));

        var dup = WikiDedup.FindPageDuplicate("Адаптер", null, null, [inSection], [inSection]);

        dup.HasValue.Should().BeFalse();
    }

    [Fact]
    public void FindPageDuplicate_ТочноеИмяРаньшеПредмета_ВозвращаетсяТочноеИмя()
    {
        // Порядок не меняется: точное имя (раздел и цепочка вверх) проверяется первым;
        // дубль по предмету — только когда точного совпадения нет.
        var subjectMatch = Existing("Проекты/p1/Каталог Исходники.md", Page("Каталог Исходники"));
        var exactMatch = Existing("Общее/Содержимое каталога Исходники.md", Page("Содержимое каталога Исходники"));

        var dup = WikiDedup.FindPageDuplicate(
            "Содержимое каталога Исходники", null, null, [subjectMatch, exactMatch], [subjectMatch]);

        dup.HasValue.Should().BeTrue();
        var d = dup.GetValueOrDefault();
        d.Path.Should().Be("Общее/Содержимое каталога Исходники.md");
        d.Field.Should().Be("заголовок");
    }
}
