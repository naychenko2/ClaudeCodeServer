using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Пустой result CLI (numTurns=0, success, нулевой usage, без api-ошибки и отказов) —
// служебный маркер «модель не вызывалась», а не ответ пользовательскому ходу. Так CLI
// завершает микро-ходы task-notification'ов на --resume и запуск без submit (ре-аттемпт
// фолбэком). Инцидент 16.08.2026: такие result'ы резолвили ход «успехом» — пользователь
// видел завершённый ход без ответа (280 мс, ноль токенов). Решение вынесено в чистую
// функцию IsEmptyNoopResult — здесь все её ветки.
public class ClaudeSessionEmptyNoopResultTests
{
    private static readonly UsageInfo Zero = new(0, 0, 0, 0);
    private static readonly UsageInfo Real = new(2, 772, 52037, 77156);

    // Основной кейс инцидента: «success», ноль ходов, ноль токенов — не ответ ходу.
    [Fact]
    public void Пустой_НольХодовНольТокенов_True()
        => ClaudeSession.IsEmptyNoopResult(numTurns: 0, subtype: "success",
               apiErrorStatus: null, permissionDenials: null, usage: Zero)
            .Should().BeTrue();

    // usage вообще отсутствует (null) — тем более пусто.
    [Fact]
    public void Пустой_БезUsage_True()
        => ClaudeSession.IsEmptyNoopResult(numTurns: 0, subtype: "success",
               apiErrorStatus: null, permissionDenials: null, usage: null)
            .Should().BeTrue();

    // Настоящий ответ модели — хотя бы один ход. Не пустой.
    [Fact]
    public void Пустой_ЕстьХодыМодели_False()
        => ClaudeSession.IsEmptyNoopResult(numTurns: 1, subtype: "success",
               apiErrorStatus: null, permissionDenials: null, usage: Real)
            .Should().BeFalse();

    // Токены есть — модель работала, даже если numTurns посчитан нулём.
    [Fact]
    public void Пустой_ЕстьТокены_False()
        => ClaudeSession.IsEmptyNoopResult(numTurns: 0, subtype: "success",
               apiErrorStatus: null, permissionDenials: null, usage: Real)
            .Should().BeFalse();

    // api-ошибка (например 429 у провайдера) — содержательный отказ, не пустышка.
    [Fact]
    public void Пустой_ApiОшибка_False()
        => ClaudeSession.IsEmptyNoopResult(numTurns: 0, subtype: "success",
               apiErrorStatus: "429", permissionDenials: null, usage: Zero)
            .Should().BeFalse();

    // Отказы в правах — содержательный исход хода.
    [Fact]
    public void Пустой_ОтказыВПравах_False()
        => ClaudeSession.IsEmptyNoopResult(numTurns: 0, subtype: "success",
               apiErrorStatus: null, permissionDenials: ["Write"], usage: Zero)
            .Should().BeFalse();

    // error-подтипы — честные ошибки, не маркеры «модели не было».
    [Theory]
    [InlineData("error")]
    [InlineData("error_during_execution")]
    [InlineData("error_max_turns")]
    public void Пустой_ErrorПодтип_False(string subtype)
        => ClaudeSession.IsEmptyNoopResult(numTurns: 0, subtype: subtype,
               apiErrorStatus: null, permissionDenials: null, usage: Zero)
            .Should().BeFalse();
}
