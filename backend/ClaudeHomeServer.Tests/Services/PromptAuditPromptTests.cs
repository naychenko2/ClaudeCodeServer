using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Промпт разбора «что в промпте лишнее». Главный тест здесь — сторож приватности:
// место prompt-audit может быть назначено на OpenRouter или локальную Ollama, а в секциях
// лежат auto-recall личных заметок и долгая память персоны. Без явной галочки человека
// ни один символ их текста наружу уходить не должен.
public class PromptAuditPromptTests
{
    private const string Secret = "ПАРОЛЬ-ОТ-СЕЙФА-42";

    private static PromptSnapshotDto Snapshot() => new(
        Id: "1700000000000-abcd", CreatedAt: 1700000000000, Applied: true, InheritedFromId: null,
        Sections:
        [
            new PromptSectionDto("recall-memory", "Auto-recall памяти персоны", Secret),
            new PromptSectionDto("turn-text", "Текст хода", "тоже " + Secret, "turn"),
        ],
        CliArgs: ["--print"], McpServers: ["tasks"], Model: "opus", Mode: "acceptEdits");

    [Fact]
    public void БезГалочки_ТекстСекцийНеПокидаетМашину()
    {
        var prompt = PromptAuditService.BuildPrompt(Snapshot(), includeText: false);

        prompt.Should().NotContain(Secret);
        // Метаданные при этом на месте — разбору есть с чем работать
        prompt.Should().Contain("recall-memory");
        prompt.Should().Contain("Auto-recall памяти персоны");
    }

    [Fact]
    public void СГалочкой_ФрагментыПрикладываются()
    {
        var prompt = PromptAuditService.BuildPrompt(Snapshot(), includeText: true);

        prompt.Should().Contain(Secret);
    }

    [Fact]
    public void СГалочкой_ФрагментыОбрезаныПоЛимиту()
    {
        var huge = new string('я', 50_000);
        var snapshot = Snapshot() with
        {
            Sections = [new PromptSectionDto("big", "Большая секция", huge)],
        };

        var prompt = PromptAuditService.BuildPrompt(snapshot, includeText: true);

        // Лимит нужен не только ради денег: у Large-профиля локали NumCtx 16k,
        // и Ollama режет хвост промпта молча
        prompt.Length.Should().BeLessThan(PromptAuditService.MaxTotalChars + 4096);
    }

    [Fact]
    public void ТекстХода_ВРазборНеИдёт()
    {
        // Разбираем системный промпт, а не сообщение человека: доли и рекомендации
        // считаются только по секциям Kind = system
        var prompt = PromptAuditService.BuildPrompt(Snapshot(), includeText: false);

        prompt.Should().NotContain("turn-text");
    }
}
