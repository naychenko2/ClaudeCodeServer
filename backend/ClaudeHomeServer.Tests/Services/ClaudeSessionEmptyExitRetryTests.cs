using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Гонка same-process хода (TOCTOU): авто-ход (цикл «до готово», авто-уведомления о задачах)
// стартует по result предыдущего хода, пока прогон CLI ещё жив (доживают фоновые агенты).
// TrySubmitTurn успевает записать ход в stdin живого процесса, но тот сразу завершается —
// ход умирает вместе с ним, не выдав ни одного события. Без фикса фолбэк честно классифицировал
// бы это как Unreachable («процесс умер без result») и навсегда уводил чат на другого
// провайдера. Фикс: такая смерть перезапускается новым процессом НА ТОЙ ЖЕ ПАРЕ, не отдавая
// исход наружу. Решение вынесено в чистую функцию ShouldRetryEmptyExit — здесь проверяются
// все её ветки (гонка / обрыв посреди хода / выход между ходами / новый процесс).
public class ClaudeSessionEmptyExitRetryTests
{
    // Основная гонка: same-process ход умер ДО первого события — ретрай той же парой.
    [Fact]
    public void ShouldRetry_SameProcessУмерДоПервогоСобытия_True()
    {
        // activeTurnDied=true (ход был активен), retryOnEmptyExit=true (same-process submit),
        // turnGotEvent=false (ни одного события после submit — процесс уже завершался).
        ClaudeSession.ShouldRetryEmptyExit(
            activeTurnDied: true, retryOnEmptyExit: true, turnGotEvent: false)
            .Should().BeTrue();
    }

    // Обрыв посреди хода: процесс успел выдать события (текст/инструмент), затем умер —
    // это легитимный Unreachable, ретрай НЕ нужен (иначе пересылка дублировала бы частичный вывод).
    [Fact]
    public void ShouldRetry_ОбрывПосредиХода_False()
    {
        ClaudeSession.ShouldRetryEmptyExit(
            activeTurnDied: true, retryOnEmptyExit: true, turnGotEvent: true)
            .Should().BeFalse();
    }

    // Выход между ходами / после result: ход не активен (TurnDone=true) — штатная смерть
    // прогона, ExitedMessage идёт наружу как раньше, никакого ретрая.
    [Fact]
    public void ShouldRetry_ВыходМеждуХодами_False()
    {
        ClaudeSession.ShouldRetryEmptyExit(
            activeTurnDied: false, retryOnEmptyExit: true, turnGotEvent: false)
            .Should().BeFalse();
    }

    // Новый процесс умер пустым: это сбой старта (плохая конфигурация/краш), не гонка —
    // retryOnEmptyExit=false (его ставит только TrySubmitTurn same-process). Ретрая нет,
    // смерть уходит наружу как Unreachable и обрабатывается фолбэком штатно. Естественный
    // предел ретраев: у перезапущенного прогона этот флаг не взводится.
    [Fact]
    public void ShouldRetry_НовыйПроцессУмерПустым_False()
    {
        ClaudeSession.ShouldRetryEmptyExit(
            activeTurnDied: true, retryOnEmptyExit: false, turnGotEvent: false)
            .Should().BeFalse();
    }
}
