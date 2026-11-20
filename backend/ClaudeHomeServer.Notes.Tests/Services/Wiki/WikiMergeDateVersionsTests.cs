using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// D1/QA-2 (б): страницы-версии «… 2026-11-05» / «… 2026-11-12» различаются только датой. Слияние
// схлопнуло бы снимки в алиасы, поэтому подбор merge такие пары не берёт; пара без дат — как раньше.
// TitleSubset и WikiDedup не тронуты: на них стоят константы замера T0 и вызов судьи page_duplicate.
public class WikiMergeDateVersionsTests
{
    private static WikiGate.Draft Page(string path, string title, int statements)
    {
        var text = "---\ntype: topic\nsummary: описание " + title + "\n---\n\n# " + title + "\n\n## Сводка\n";
        for (var i = 0; i < statements; i++)
            text += "- Факт " + (i + 1) + ".\n";
        text += "\n## Хронология\n\n## Противоречия\n\n## История\n";
        return new WikiGate.Draft(path, text);
    }

    private static List<(string A, string B)> Pick(params WikiGate.Draft[] pages) =>
        WikiMergeCandidates.Pick([.. pages], new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal), 10, 3);

    [Theory]
    [InlineData("Отчёт по сборке 2026-11-05", "Отчёт по сборке 2026-11-12", true)]
    [InlineData("Отчёт по сборке 05.11.2026", "Отчёт по сборке 12.11.2026", true)]
    [InlineData("Отчёт по сборке", "Отчёт по сборке 2026-11-12", true)]
    [InlineData("Отчёт по сборке 2026-11-05", "Отчёт по сборке 2026-11-05", false)] // тот же заголовок — не версия
    [InlineData("Каталог исходных", "Содержание каталога исходных", false)] // даты ни при чём
    [InlineData("Отчёт по сборке 2026-11-05", "Протокол по сборке 2026-11-12", false)] // кроме даты различаются
    [InlineData("2026-11-05", "2026-11-12", false)] // без остатка предмета
    [InlineData("", "Отчёт 2026-11-12", false)]
    public void DateVersionsOnly_ТолькоДатаОтличается(string a, string b, bool expected) =>
        WikiTopicScore.DateVersionsOnly(a, b).Should().Be(expected);

    [Fact]
    public void Pick_ПараВерсийДат_НеПредлагается()
    {
        // Мутация: убрать `|| DateVersionsOnly(...)` в ветке «дубль» WikiMergeCandidates.Pick — пара появится.
        var pairs = Pick(
            Page("Общее/Отчёт-1105.md", "Отчёт по сборке 2026-11-05", 1),
            Page("Общее/Отчёт-1112.md", "Отчёт по сборке 2026-11-12", 3));

        pairs.Should().BeEmpty("страницы-версии одного предмета не сливаются");
    }

    [Fact]
    public void Pick_ПараБезДат_КакРаньше()
    {
        var pairs = Pick(
            Page("Общее/Каталог-исходных.md", "Каталог исходных", 1),
            Page("Общее/Содержание-каталога-исходных.md", "Содержание каталога исходных", 3));

        pairs.Should().ContainSingle().Which.Should().Be(
            ("Общее/Каталог-исходных.md", "Общее/Содержание-каталога-исходных.md"));
    }
}
