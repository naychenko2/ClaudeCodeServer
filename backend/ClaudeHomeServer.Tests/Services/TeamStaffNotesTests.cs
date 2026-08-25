using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож подписей ⚑ (staffNote) молчаливых ходов координатора: эти строки читает фронт
// (frontend/src/lib/coordinatorTurns.ts) — по ним карточка координатора в ленте штаба
// выбирает строку состояния фазы («Разбирает доклады волны 2», «Ставит задачи…»).
// Правка текста мимо каталога сломала бы подпись фазы МОЛЧА: карточка собралась бы,
// но без состояния. Меняешь строку — меняй и разбор на фронте, тест здесь падает намеренно.
public class TeamStaffNotesTests
{
    [Fact]
    public void WaveClosed_KeepsExactTextWithWaveNumber()
    {
        TeamStaffNotes.WaveClosed(2).Should().Be("Волна 2 закрыта — сводка передана координатору");
        TeamStaffNotes.WaveClosed(11).Should().Be("Волна 11 закрыта — сводка передана координатору");
    }

    [Fact]
    public void EscalationResolved_KeepsExactText()
    {
        TeamStaffNotes.EscalationResolved.Should().Be("Ответ на карточку передан координатору");
    }

    [Fact]
    public void InterviewReturn_KeepsExactText()
    {
        TeamStaffNotes.InterviewReturn.Should().Be("Возврат в интервью — координатор задаст вопросы");
    }

    // Подпись хода-реакции постановщика живёт в TaskExecutionService (это не штаб), но тот же
    // разбор на фронте отличает её от плашек координатора — фиксируем строку здесь же.
    [Fact]
    public void DelegatorReactionStaffNote_KeepsExactText()
    {
        TaskExecutionService.DelegatorReactionStaffNote.Should().Be("Доклад по задаче передан постановщику");
    }
}
