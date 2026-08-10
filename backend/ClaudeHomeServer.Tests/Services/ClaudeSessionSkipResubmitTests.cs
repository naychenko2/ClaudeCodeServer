using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Ре-аттемпт хода фолбэком не должен перепосылать текст в stdin: первый submit новым процессом
// уже durable в .jsonl транскрипта (CLI пишет синхронно с приёмом), и повторный submit создал бы
// второй user-turn того же текста — дубль, видимый модели (инцидент 2026-08-10). Решение вынесено
// в чистую функцию ShouldSkipResubmit — здесь проверяются все её ветки.
public class ClaudeSessionSkipResubmitTests
{
    private const string Text = "Персона-исполнитель завершила делегированную задачу";

    // Основной кейс инцидента: прошлый submit новым процессом (durable), процесс умер без result →
    // ре-аттемпт тем же текстом skip'ает повторный submit (CLI доиграет висящий через --resume).
    [Fact]
    public void Skip_РеаттемптНезавершённогоХода_True()
        => ClaudeSession.ShouldSkipResubmit(Text, Text, lastTurnResolved: false, lastSubmitWasNewProcess: true)
            .Should().BeTrue();

    // Другой текст — новый ход, submit нужен.
    [Fact]
    public void Skip_ДругойТекст_False()
        => ClaudeSession.ShouldSkipResubmit("иная просьба", Text, lastTurnResolved: false, lastSubmitWasNewProcess: true)
            .Should().BeFalse();

    // Предыдущий ход завершён result'ом (success/error) — тот же текст это уже новый ход, а не
    // висящий: submit нужен, иначе зависание (CLI ждёт input после assistant-ответа).
    [Fact]
    public void Skip_ПредыдущийЗавершён_False()
        => ClaudeSession.ShouldSkipResubmit(Text, Text, lastTurnResolved: true, lastSubmitWasNewProcess: true)
            .Should().BeFalse();

    // Same-process submit (TrySubmitTurn) НЕ durable-гарантирован: его DiedEmpty-ретрай новым
    // процессом не должен skip'ать — иначе при отсутствии durable-записи ход зависнет.
    [Fact]
    public void Skip_SameProcessSubmit_False()
        => ClaudeSession.ShouldSkipResubmit(Text, Text, lastTurnResolved: false, lastSubmitWasNewProcess: false)
            .Should().BeFalse();

    // Первый ход сессии — предыдущего submit'а не было (lastText=null, resolved=true по умолчанию).
    [Fact]
    public void Skip_ПервыйХод_False()
        => ClaudeSession.ShouldSkipResubmit(Text, null, lastTurnResolved: true, lastSubmitWasNewProcess: false)
            .Should().BeFalse();
}
