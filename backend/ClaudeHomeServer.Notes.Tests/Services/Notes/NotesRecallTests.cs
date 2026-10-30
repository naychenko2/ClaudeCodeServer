using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Notes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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

    // S2a-4 (разрез И.3, решение 29.10): в узкой рамке общего проекта личный vault в подборку
    // не идёт — гейт реального контрибьютора IsEnabled при Narrow выключен, при Wide включён
    // (шина зовёт BuildAsync только включённых — TurnEventBus.ApplyAsync).
    [Fact]
    public void IsEnabled_УзкаяРамкаВыключен_ШирокаяВключён()
    {
        var config = new ConfigurationBuilder().Build();
        // Гейт читает только поля контекста хода и не трогает сервисы (их поведение —
        // тесты выше и SearchAsync-тесты вертикали).
        var notes = new NotesKnowledgeService(null!, null!, null!, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var contributor = new NotesRecallContributor(notes, config,
            NullLogger<NotesRecallContributor>.Instance);

        var wide = new ClaudeHomeServer.Services.Turn.PromptSessionContext(
            new Session(), "owner-1", null, null, HasNotesMcp: true);
        var narrow = new ClaudeHomeServer.Services.Turn.PromptSessionContext(
            new Session(), "owner-1", null, null, HasNotesMcp: true,
            Frame: ClaudeHomeServer.Services.Access.TurnFrameKind.Narrow);

        contributor.IsEnabled(wide).Should().BeTrue("в личном чате личный vault включён, как раньше");
        contributor.IsEnabled(narrow).Should().BeFalse(
            "в узкой рамке общего проекта личный vault в подборку хода не попадает");
    }
}
