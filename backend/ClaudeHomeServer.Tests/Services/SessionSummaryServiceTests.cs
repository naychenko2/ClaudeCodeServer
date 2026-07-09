using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты чистой логики «Итога сессии»: сборка транскрипта из StoredMessage и заголовок.
// Полный пайплайн (one-shot claude) требует claude.exe и здесь не гоняется.
public class SessionSummaryServiceTests
{
    // ─── BuildTranscript ─────────────────────────────────────────────────────

    [Fact]
    public void BuildTranscript_РепликиИИнструменты_ВПравильномФормате()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("Сделай фичу"),
            new StoredToolUseMessage { Name = "Read" },
            new StoredFileChangedMessage("src/a.ts", 10, 2),
            new StoredTextMessage("Готово, фича сделана"),
            new StoredThinkingMessage("внутренние размышления"),
            new StoredResultMessage("success", 100, 1),
        };

        var t = SessionSummaryService.BuildTranscript(messages, 10_000);

        t.Should().Contain("Пользователь:").And.Contain("Сделай фичу");
        t.Should().Contain("Claude:").And.Contain("Готово, фича сделана");
        t.Should().Contain("[инструмент Read]");
        t.Should().Contain("[изменён файл src/a.ts +10/-2]");
        // thinking и метаданные result в транскрипт не попадают
        t.Should().NotContain("размышления").And.NotContain("success");
    }

    [Fact]
    public void BuildTranscript_ПустаяЛента_ПустаяСтрока()
    {
        SessionSummaryService.BuildTranscript([], 10_000).Should().BeEmpty();
    }

    [Fact]
    public void BuildTranscript_ПереполнениеБюджета_ГоловаПлюсХвост()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("НАЧАЛО " + new string('а', 5000)),
            new StoredTextMessage(new string('б', 5000) + " КОНЕЦ"),
        };

        var t = SessionSummaryService.BuildTranscript(messages, 1000);

        t.Length.Should().BeLessThan(1100); // бюджет + маркер сокращения
        t.Should().StartWith("Пользователь:").And.Contain("НАЧАЛО");
        t.Should().Contain("[…транскрипт сокращён…]");
        t.Should().EndWith("КОНЕЦ");
    }

    [Fact]
    public void BuildTranscript_ПустыеРеплики_Пропускаются()
    {
        var messages = new List<StoredMessage>
        {
            new StoredUserMessage("   "),
            new StoredTextMessage(""),
        };
        SessionSummaryService.BuildTranscript(messages, 1000).Should().BeEmpty();
    }

    // ─── BuildTitle ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildTitle_СИменемСессии()
    {
        var s = new Session { Name = "Рефакторинг заметок" };
        SessionSummaryService.BuildTitle(s).Should()
            .StartWith("Итог: Рефакторинг заметок · ");
    }

    [Fact]
    public void BuildTitle_БезИмени_Чат()
    {
        SessionSummaryService.BuildTitle(new Session()).Should().StartWith("Итог: чат · ");
    }

    [Fact]
    public void BuildTitle_ДлинноеИмя_Обрезается()
    {
        var s = new Session { Name = new string('х', 100) };
        var title = SessionSummaryService.BuildTitle(s);
        title.Should().Contain("…");
        title.Length.Should().BeLessThan(90);
    }
}
