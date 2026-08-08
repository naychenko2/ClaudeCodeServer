using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Ватчдог прогона: допустимая тишина stdout по состоянию (ResolveWatchdog). Решающий кейс —
// ход-продолжение CLI (ответ на task_notification фоновой задачи): до фикса его долгие инструменты
// (npx tsc, dotnet build) гибли через короткий ResultExitGrace (15с молчания), т.к. состояние
// TurnDone && !HasPendingBg не различало «между ходами, CLI вот-вот выйдет» и «идёт полноценный
// агентный ход». Плюс после завершения последней bg-задачи stdin НЕ закрывается (раньше
// CloseStdinIfIdle в bg-путях ронял permission-канал продолжения «Stream closed») — окно старта
// продолжения получает отдельный грейс. Маппинг вынесен в чистую функцию — ветки покрыты напрямую.
public class ClaudeSessionWatchdogMappingTests
{
    private static readonly TimeSpan Bg = TimeSpan.FromMinutes(30);

    private static TimeSpan W(bool turnDone, bool continuationActive, bool hasPendingBg,
        bool stdinClosed, bool promptSuggestions = false) =>
        ClaudeSession.ResolveWatchdog(turnDone, continuationActive, hasPendingBg, stdinClosed,
            promptSuggestions, Bg);

    // Ход-продолжение CLI — это полноценный агентный ход: долгие инструменты не должны гибнуть по
    // короткому грейсу. До фикса продолжение при TurnDone шло по ResultExitGrace и убивало dotnet
    // build после 15с тишины stdout.
    [Fact]
    public void Продолжение_РавноActiveTurn_ЩедрыйПотолок()
    {
        var active = W(turnDone: false, continuationActive: false, hasPendingBg: false, stdinClosed: false);
        var continuation = W(turnDone: true, continuationActive: true, hasPendingBg: false, stdinClosed: false);

        continuation.Should().Be(active);
        // Не короткий грейс — иначе долгий инструмент продолжения гибнет посреди выполнения
        continuation.Should().BeGreaterThan(TimeSpan.FromMinutes(10));
    }

    // continuationActive перебивает даже гипотетический bg (на практике pending уже пуст —
    // продолжение стартует именно по завершении последней фоновой задачи)
    [Fact]
    public void Продолжение_ПеребиваетBg()
        => W(turnDone: true, continuationActive: true, hasPendingBg: true, stdinClosed: false)
            .Should().Be(W(turnDone: false, continuationActive: false, hasPendingBg: false, stdinClosed: false));

    // Доживание фоновых агентов — ровно потолок bgLinger (процесс держат работающие внутри агенты)
    [Fact]
    public void ДоживаниеФоновых_BgLinger()
        => W(turnDone: true, continuationActive: false, hasPendingBg: true, stdinClosed: false).Should().Be(Bg);

    // Окно старта продолжения: последняя bg завершена (pending пуст), stdin ЕЩЁ ОТКРЫТ —
    // task_notification запускает ход-продолжение. Отдельный грейс, БОЛЬШИЙ ResultExitGrace:
    // иначе разгон продолжения под нагрузкой/ретраях провайдера рубился бы за 15с. Это состояние
    // и значит, что после bg-завершения stdin закрыт быть не должен (иначе permission-канал продолжения
    // падает «Stream closed» — исходный баг чата f8458abe).
    [Fact]
    public void ПослеПоследнейBg_StdinОткрыт_ГрейсБольшеВыхода()
    {
        var startWindow = W(turnDone: true, continuationActive: false, hasPendingBg: false, stdinClosed: false);
        var exitGrace = W(turnDone: true, continuationActive: false, hasPendingBg: false, stdinClosed: true);

        startWindow.Should().NotBe(exitGrace);
        startWindow.Should().BeGreaterThan(exitGrace);
    }

    // result без фоновых задач и закрытым stdin — CLI выйдет сам, короткий грейс выхода
    [Fact]
    public void ResultБезBg_StdinЗакрыт_КороткийГрейс()
        => W(turnDone: true, continuationActive: false, hasPendingBg: false, stdinClosed: true)
            .Should().BeLessThan(TimeSpan.FromMinutes(1));

    // --prompt-suggestions: подсказка генерится ПОСЛЕ result (stdin уже закрыт) — грейс шире обычного
    [Fact]
    public void PromptSuggestions_ГрейсШиреОбычного()
    {
        var prompt = W(turnDone: true, continuationActive: false, hasPendingBg: false,
            stdinClosed: true, promptSuggestions: true);
        var plain = W(turnDone: true, continuationActive: false, hasPendingBg: false,
            stdinClosed: true, promptSuggestions: false);

        prompt.Should().BeGreaterThan(plain);
    }
}
