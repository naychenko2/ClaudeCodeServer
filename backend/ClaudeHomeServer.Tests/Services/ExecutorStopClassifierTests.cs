using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Распознавание терминального отказа исполнителя. Главное здесь — 401 приходит и БЕЗ статуса
// (прод-инцидент 04.08.2026: «Failed to authenticate. API Error: 401 invalid access token
// or token expired» при subtype=success), поэтому текст ошибки — равноправный сигнал.
// Обратная сторона — ложные срабатывания: ход, ЦИТИРУЮЩИЙ формулировку в разборе, живой.
public class ExecutorStopClassifierTests
{
    private static ResultMessage Result(string subtype = "success", string? status = null) =>
        new(subtype, DurationMs: 10, NumTurns: 1, Usage: null, TotalCostUsd: null, ApiErrorStatus: status);

    [Fact]
    public void Classify_Статус401_ТерминальныйОтказ()
    {
        ExecutorStopClassifier.Classify(Result(status: "401"), null)
            .Should().Be(ExecutorStopClassifier.AuthFailedReason);
    }

    [Fact]
    public void Classify_ПродовыйТекстБезСтатуса_ТерминальныйОтказ()
    {
        ExecutorStopClassifier.Classify(Result(),
                "Failed to authenticate. API Error: 401 invalid access token or token expired")
            .Should().Be(ExecutorStopClassifier.AuthFailedReason);
    }

    // Живой прогон claude CLI с заведомо неверным ключом провайдера (12.08.2026): result
    // приезжает как subtype=success + is_error, api_error_status приходит ЧИСЛОМ 401 —
    // ClaudeSession читает это поле только как строку, поэтому в ResultMessage статус пуст,
    // и текст остаётся единственным сигналом
    [Fact]
    public void Classify_ЖивойТекстCLIБезСтатуса_ТерминальныйОтказ()
    {
        ExecutorStopClassifier.Classify(Result(),
                "Failed to authenticate. API Error: 401 token expired or incorrect")
            .Should().Be(ExecutorStopClassifier.AuthFailedReason);
    }

    [Theory]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("authentication_error: invalid x-api-key")]
    [InlineData("API Error: 401 Unauthorized")]
    [InlineData("Could not authenticate with the provider")]
    [InlineData("OAuth token has expired — re-login required")]
    public void IsTerminalAuthFailure_КаноническиеФормулировки(string text)
    {
        ExecutorStopClassifier.IsTerminalAuthFailure(null, text).Should().BeTrue();
    }

    [Theory]
    // Слабые слова без признака авторизационного ответа провайдера — ход живой
    [InlineData("Проверил гипотезу: сессия помечена unauthorized в нашей таблице ролей")]
    [InlineData("В разборе инцидента писали, что token expired — но это была другая причина")]
    [InlineData("Prompt is too long")]
    [InlineData("fetch failed")]
    [InlineData("rejected (429)")]
    [InlineData(null)]
    [InlineData("")]
    public void IsTerminalAuthFailure_НеАвторизация(string? text)
    {
        ExecutorStopClassifier.IsTerminalAuthFailure(null, text).Should().BeFalse();
    }

    [Theory]
    [InlineData("429")]
    [InlineData("overloaded_error")]
    [InlineData("500")]
    public void IsTerminalAuthFailure_ЧужиеСтатусы_НеТерминальны(string status)
    {
        ExecutorStopClassifier.IsTerminalAuthFailure(status, "Provider is overloaded").Should().BeFalse();
    }

    [Fact]
    public void Classify_ОбычныйУспешныйХод_НетПричины()
    {
        ExecutorStopClassifier.Classify(Result(), null).Should().BeNull();
    }

    [Fact]
    public void Classify_РабочаяОшибкаХода_НетПричины()
    {
        ExecutorStopClassifier.Classify(Result("error"), "Bash command failed with exit code 1")
            .Should().BeNull();
    }
}
