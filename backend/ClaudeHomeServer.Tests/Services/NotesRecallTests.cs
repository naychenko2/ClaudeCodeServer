using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Тесты формирования блока auto-recall для системного промпта (чистая логика).
public class NotesRecallTests
{
    private static NoteSemanticHit Hit(string title, double score) =>
        new(Id: "id-" + title, Title: title, Source: "personal", SourceLabel: "Личный",
            Score: score, Snippet: "фрагмент " + title);

    [Fact]
    public void BuildRecallBlock_ФильтруетПоПорогуИTopK()
    {
        // Dify отдаёт хиты уже отсортированными по score — блок берёт top-K в этом порядке
        var hits = new[]
        {
            Hit("A", 0.9), Hit("D", 0.8), Hit("B", 0.5), Hit("C", 0.2),
        };
        var block = NotesKnowledgeService.BuildRecallBlock(hits, minScore: 0.4, topK: 2);

        block.Should().NotBeNull();
        block!.Should().Contain("[[A]]").And.Contain("[[D]]"); // топ-2 из прошедших порог
        block.Should().NotContain("[[B]]");                    // за пределами topK
        block.Should().NotContain("[[C]]");                    // ниже порога
        block.Should().Contain("notes_read");
    }

    [Fact]
    public void BuildRecallBlock_ВсеНижеПорога_Null()
    {
        var hits = new[] { Hit("A", 0.1), Hit("B", 0.2) };
        NotesKnowledgeService.BuildRecallBlock(hits, minScore: 0.35, topK: 4).Should().BeNull();
    }

    [Fact]
    public void BuildRecallBlock_ПустойСписок_Null()
    {
        NotesKnowledgeService.BuildRecallBlock([], minScore: 0.35, topK: 4).Should().BeNull();
    }
}
