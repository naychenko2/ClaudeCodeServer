using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Prompts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Лимит раундов интервью (Minor, волна 3): раньше InterviewProtocol/ClarifyInterviewTurn
// только ПРОСИЛИ модель остановиться словами, а факт бэкенд не проверял — 3-й раунд так же
// уходил в карточку, и ClarifyInterviewTurn при уже исчерпанном лимите безусловно велел
// «задай вопросы», противореча InterviewProtocol в том же ходе (CoordinatorTurn).
// InterviewRoundsExhausted — общая проверка для текста и для permission-гейта
// (ClaudeSession.HandleControlRequestAsync денаит AskUserQuestion сверх лимита).
public class TeamImplementPromptsTests
{
    private static SessionTeamImplement MkTeam(int rounds) => new() { InterviewRounds = rounds };

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void InterviewRoundsExhausted_СверяетСПотолком(int rounds, bool expected)
    {
        TeamImplementPrompts.InterviewRoundsExhausted(MkTeam(rounds)).Should().Be(expected);
    }

    [Fact]
    public void ClarifyInterviewTurn_ЛимитНеИсчерпан_ВелитЗадатьВопросы()
    {
        var text = TeamImplementPrompts.ClarifyInterviewTurn("неясен формат выгрузки", MkTeam(0));

        text.Should().Contain("AskUserQuestion");
        text.Should().NotContain("не спрашивай");
    }

    [Fact]
    public void ClarifyInterviewTurn_ЛимитИсчерпан_ВелитОформитьДопущением_БезПротиворечияInterviewProtocol()
    {
        // Раньше текст безусловно требовал спросить — а следом в этом же ходу CoordinatorTurn
        // дописывал InterviewProtocol с «лимит исчерпан, больше не спрашивай»: два взаимо-
        // исключающих указания в одном ходу.
        var team = MkTeam(TeamImplementPrompts.MaxInterviewRounds);

        var clarifyTurn = TeamImplementPrompts.ClarifyInterviewTurn("неясен формат выгрузки", team);
        var protocol = TeamImplementPrompts.InterviewProtocol(team);

        clarifyTurn.Should().NotContain("AskUserQuestion",
            "при исчерпанном лимите ход не должен просить задавать вопросы");
        clarifyTurn.Should().Contain("допущением");
        protocol.Should().Contain("больше не спрашивай");
    }

    // Прод 2026-08-03 (находка Веры): просили hello5.txt/«QA smoke test file 5», а координатор
    // отдал в маркере работы smoke5.txt/«smoke run 5 ok» — конкретика из вводной подменена ещё
    // ДО планировщика, самим маркером <team:work> (протокол требовал сжать постановку до
    // «одной-двух фраз», не оговаривая точные значения). Обе точки, где координатор формулирует
    // маркер работы, обязаны требовать дословный перенос.
    [Fact]
    public void WorkClassificationProtocol_ТребуетДословныйПереносЗначений()
    {
        TeamImplementPrompts.WorkClassificationProtocol.Should().Contain("дословно");
    }

    [Fact]
    public void InterviewProtocol_МаркерРаботы_ТребуетДословныйПереносЗначений()
    {
        TeamImplementPrompts.InterviewProtocol(MkTeam(0)).Should().Contain("дословно");
    }
}
