using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Финал Б1 (хвост 1a6a5cda): человек пишет формулировку с переносом строки — та же болезнь,
// что у утверждения плана, только в ручной записи (WikiHumanRender.WithFootnote). Формат
// страницы строчный: расползшаяся строка — верхняя половина без сноски (нарушение гейта), а
// определение сноски FootnoteDefRx не читается: разбор даёт «ссылка на несуществующую сноску».
// Складывает пишущий в страницу, а не вызывающий: пакет — граница контракта, его операции
// приходят не только из редактора (восстановленный бэкап, пакет, записанный до этой правки).
public class WikiHumanMultiLineTests
{
    private const string PagePath = "Общее/Сущность.md";
    private const string Folded = "первая строка формулировки вторая строка той же формулировки";

    private const string PageBase = """
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # Сущность

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    [Fact]
    public void Человек_AppendМногострочный_ПишетОднуСтрокуИОднуСноску()
    {
        var package = new WikiPackage
        {
            Id = "multi-line-01",
            Author = WikiPackage.AuthorHuman,
            Action = WikiPackage.ActionEdit,
            Section = WikiSections.Common,
            Sources = [new WikiPackage.SourceRef("human", "multi-line")],
            Operations = [new WikiPackage.Operation
            {
                Op = WikiPackage.OpAppend, Page = PagePath, PageSection = "Сводка",
                Text = "первая строка формулировки\nвторая строка той же формулировки",
                Class = WikiPackage.ClassKey("human", WikiPackage.OpAppend),
                Decision = WikiPackage.DecisionAuto,
            }],
            Status = WikiPackage.StatusAccepted,
        };
        List<WikiGate.Draft> state = [new(PagePath, PageBase)];

        var res = WikiHumanRender.Render(state, package);

        res.Error.Should().BeNull(res.Error);
        var content = res.Drafts.Single(d => d.Path == PagePath).Content;
        content.Should().Contain(Folded + " [^s1]");
        // Мутация «убрать складывание в WithFootnote»: краснеет здесь же — строка «Сводки»
        // разорвана (верхняя половина без сноски), определение сноски не читается, и разбор
        // отдаёт «ссылка на несуществующую сноску».
        var parsed = WikiPage.Parse(content);
        parsed.Errors.Should().BeEmpty(string.Join("; ", parsed.Errors));
        parsed.Model!.Sections.Single(s => s.Name == "Сводка").Lines.Should().ContainSingle()
            .Which.Should().Be(Folded + " [^s1]");
        parsed.Model.Footnotes.Should().ContainSingle();
        parsed.Model.Footnotes[0].Quote.Should().NotContain("\n").And.NotContain("\r");
    }
}
