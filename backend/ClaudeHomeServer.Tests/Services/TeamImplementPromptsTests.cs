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

    // Единый канал вопросов (запрос владельца 2026-08-04): интервью спрашивает ASK-карточками,
    // и эскалации в живом ходу обязаны тем же каналом — а не карточкой-«простынёй» с полем.
    [Fact]
    public void EscalationProtocol_Вопросы_ТолькоИнструментом_КакВИнтервью()
    {
        var protocol = TeamImplementPrompts.EscalationProtocol;

        protocol.Should().Contain("AskUserQuestion");
        protocol.Should().Contain("не пиши вопросы текстом",
            "формулировка согласована с InterviewProtocol — оба протокола запрещают вопросы текстом");
        protocol.Should().Contain("Другое",
            "свободный ответ текстом остаётся запасным путём — в ASK это вариант «Другое»");
    }

    [Fact]
    public void EscalationProtocol_ПродуктоваяРазвилка_УшлаИзМаркеровВОпрос()
    {
        // Живой вопрос координатора задаётся ASK; маркер <escalate:decision> из протокола ушёл
        // (парсер терпит его только как фолбэк). Останавливающие маркеры остались.
        var protocol = TeamImplementPrompts.EscalationProtocol;

        protocol.Should().NotContain("escalate:decision");
        protocol.Should().Contain("<escalate:deviation>");
        protocol.Should().Contain("<escalate:check>");
        protocol.Should().Contain("<escalate:clarify>");
    }

    [Fact]
    public void EscalationProtocol_ВопросНеОстанавливаетРаботу()
    {
        // Развилка по природе случая: ASK живёт внутри хода и продолжает его после ответа,
        // в отличие от карточки, которая публикуется, когда ход уже завершён.
        TeamImplementPrompts.EscalationProtocol.Should().Contain("продолжай");
    }

    // Правка плана на подтверждении (прод 2026-08-04): текстовая правка обязана стать
    // перепланированием через маркер работы — ответ «план готов, жду согласования» текстом
    // оставляет человека со старой карточкой. Правило сформулировано так же жёстко, как
    // запрет вопросов текстом в интервью.
    [Fact]
    public void PlanEditProtocol_ЗапрещаетОтветТекстом_ТребуетМаркерРаботы()
    {
        var protocol = TeamImplementPrompts.PlanEditProtocol;

        protocol.Should().Contain("<team:work>");
        protocol.Should().Contain("ЗАПРЕЩЕНО", "жёсткость — как у правила про AskUserQuestion");
        protocol.Should().Contain("жду согласования",
            "названа ровно та формулировка, что зависла на проде");
    }

    [Fact]
    public void CoordinatorTurn_ВСтадииПодтверждения_СодержитПротоколПравки()
    {
        var turn = TeamImplementPrompts.CoordinatorTurn(new SessionTeamImplement
        {
            Stage = TeamImplementStage.Confirming,
        });

        turn.Should().Contain("ждёт подтверждения");
        turn.Should().Contain("<team:work>",
            "координатор в Confirming обязан знать путь пересборки плана маркером");
        turn.Should().Contain("ЗАПРЕЩЕНО");
    }
}
