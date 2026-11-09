using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// B6 (находка В1, В4): вклад пакета в сводку ночи — WikiPackage.NightSummary. Считаются только
// операции ИИ, применённые САМИ (decision=auto) поверх утверждения человека (класс «…@human»):
// supersede@human — «заменил формулировку», conflict@human — «отметил противоречие». Замена и
// противоречие между утверждениями ИИ, а также то, что решил человек во «Входящих», не считаются
// (ADR-038, предохранители 2 и 6 — про формулировки человека).
public class WikiPackageNightSummaryTests
{
    private static WikiPackage.Operation Op(string op, string page, string decision, bool humanTarget) => new()
    {
        Op = op,
        Page = page,
        Text = "утверждение из источника",
        Target = "прежнее утверждение",
        Class = WikiPackage.ClassKey("session", op, humanTarget),
        Decision = decision,
        BaseHashes = new Dictionary<string, string>(),
    };

    private static WikiPackage Pkg(params WikiPackage.Operation[] ops) => new()
    {
        Id = "p",
        Section = "common",
        Sources = [new WikiPackage.SourceRef("session", "chat-1")],
        Operations = ops,
        Status = WikiPackage.StatusApplied,
    };

    [Fact]
    public void NightSummary_ЧетыреКласса_СчитаетТолькоAutoПоУтверждениямЧеловека()
    {
        // Мутации по одной: «conflict@human как замена» (Op == OpConflict в счётчике замен) —
        // replaced = 2; «снять фильтр supersede@human» — replaced = 2; «conflict без фильтра цели» —
        // страницы включают А.md (ИИ против ИИ).
        var pkg = Pkg(
            Op(WikiPackage.OpSupersede, "Общее/Замена-человека.md", WikiPackage.DecisionAuto, humanTarget: true),
            Op(WikiPackage.OpSupersede, "Общее/Замена-ИИ.md", WikiPackage.DecisionAuto, humanTarget: false),
            Op(WikiPackage.OpConflict, "Общее/Спор-с-человеком.md", WikiPackage.DecisionAuto, humanTarget: true),
            Op(WikiPackage.OpConflict, "Общее/Спор-ИИ-с-ИИ.md", WikiPackage.DecisionAuto, humanTarget: false));

        var (replaced, pages) = pkg.NightSummary();

        replaced.Should().Be(1);
        pages.Should().Equal(["Общее/Спор-с-человеком.md"]);
    }

    [Fact]
    public void NightSummary_РешилЧеловекИзВходящих_НеСчитается()
    {
        var pkg = Pkg(
            Op(WikiPackage.OpSupersede, "Общее/А.md", WikiPackage.DecisionInbox, humanTarget: true),
            Op(WikiPackage.OpConflict, "Общее/Б.md", WikiPackage.DecisionInbox, humanTarget: true));

        var (replaced, pages) = pkg.NightSummary();

        replaced.Should().Be(0, "ИИ ничего не «переопределял»: решил человек");
        pages.Should().BeEmpty();
    }

    [Fact]
    public void NightSummary_СпорыНаОднойСтранице_СтраницаОдна_ВПорядкеОпераций()
    {
        var pkg = Pkg(
            Op(WikiPackage.OpConflict, "Общее/Б.md", WikiPackage.DecisionAuto, humanTarget: true),
            Op(WikiPackage.OpConflict, "Общее/А.md", WikiPackage.DecisionAuto, humanTarget: true),
            Op(WikiPackage.OpConflict, "Общее/Б.md", WikiPackage.DecisionAuto, humanTarget: true));

        pkg.NightSummary().DisputedPages.Should().Equal(["Общее/Б.md", "Общее/А.md"]);
    }

    [Fact]
    public void NightSummary_ПустойПакет_Нули()
    {
        var (replaced, pages) = Pkg().NightSummary();

        replaced.Should().Be(0);
        pages.Should().BeEmpty();
    }
}
