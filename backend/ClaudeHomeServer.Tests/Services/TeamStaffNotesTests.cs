using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож подписей ⚑ (staffNote) молчаливых ходов координатора: эти строки шлются как
// user_message со staffNote=true и гасятся в ленте набором suppressedByTeamNoise
// (см. frontend/src/components/ChatPanel.tsx). Текст плашки сейчас никто на фронте
// не парсит (отдельная карточка CoordinatorTurnCard упразднена, координатор показывается
// репликой персоны чата), но строки остаются идентификаторами фазы в логах/тестах
// и должны совпадать байт-в-байт. Меняешь строку — меняй и senders на бэке, иначе
// троится рассинхрон без видимой регрессии
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
