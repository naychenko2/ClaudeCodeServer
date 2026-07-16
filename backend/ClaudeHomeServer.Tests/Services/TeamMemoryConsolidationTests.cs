using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Детерминированные гейты консолидации памяти команды: чужие id, cap 30%, merge разных типов,
// мусорный ответ LLM (no-op), парс операций
public class TeamMemoryConsolidationTests
{
    private static TeamMemoryEntry Entry(string tag, TeamMemoryType type = TeamMemoryType.Fact) => new()
    {
        OwnerId = "o1",
        ProjectId = "p1",
        Type = type,
        Text = $"запись {tag}",
    };

    private static List<TeamMemoryEntry> Entries(int count, TeamMemoryType type = TeamMemoryType.Fact) =>
        Enumerable.Range(1, count).Select(i => Entry($"e{i}", type)).ToList();

    private static TeamMemoryConsolidationOp Merge(TeamMemoryType type, string text, params string[] ids) =>
        new("merge", ids.ToList(), null, type, text, null);

    private static TeamMemoryConsolidationOp Drop(string id) => new("drop", null, id, null, null, null);

    // --- FilterOps: гейты ---

    [Fact]
    public void FilterOps_НеизвестныеId_Игнорируются()
    {
        var entries = Entries(10);
        var ops = new[]
        {
            // Один валидный источник + чужой id → после фильтрации <2 → merge отброшен
            Merge(TeamMemoryType.Fact, "сводка", entries[0].Id, "чужой-id"),
            // Два валидных + чужой → merge остаётся с двумя валидными
            Merge(TeamMemoryType.Fact, "сводка 2", entries[1].Id, entries[2].Id, "ещё-чужой"),
            Drop("несуществующий"),
        };

        var result = TeamMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().ContainSingle();
        result[0].Ids.Should().BeEquivalentTo([entries[1].Id, entries[2].Id]);
    }

    [Fact]
    public void FilterOps_Cap30Процентов_ЛишниеОперацииОтбрасываются()
    {
        var entries = Entries(10);   // cap = floor(10 · 0.3) = 3
        var ops = new[]
        {
            Drop(entries[0].Id),
            Drop(entries[1].Id),
            Drop(entries[2].Id),
            Drop(entries[3].Id),   // четвёртая — сверх cap
        };

        var result = TeamMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().HaveCount(3);
        result.Select(o => o.Id).Should().NotContain(entries[3].Id);
    }

    [Fact]
    public void FilterOps_Cap_УчитываетИсточникиMerge()
    {
        var entries = Entries(10);   // cap = 3
        var ops = new[]
        {
            Merge(TeamMemoryType.Fact, "сводка", entries[0].Id, entries[1].Id, entries[2].Id),
            Drop(entries[3].Id),   // merge уже занял весь cap
        };

        var result = TeamMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().ContainSingle().Which.IsMerge.Should().BeTrue();
    }

    [Fact]
    public void FilterOps_MergeРазныхТипов_Отклоняется()
    {
        var fact = Entry("f1", TeamMemoryType.Fact);
        var decision = Entry("d1", TeamMemoryType.Decision);
        var entries = new List<TeamMemoryEntry> { fact, decision,
            Entry("f2"), Entry("f3"), Entry("f4"), Entry("f5"), Entry("f6") };

        var ops = new[] { Merge(TeamMemoryType.Fact, "сводка", fact.Id, decision.Id) };

        TeamMemoryConsolidationService.FilterOps(ops, entries).Should().BeEmpty();
    }

    [Fact]
    public void FilterOps_ЗаявленныйТипНеСовпадаетСИсточниками_Отклоняется()
    {
        var entries = Entries(10, TeamMemoryType.Convention);

        var ops = new[] { Merge(TeamMemoryType.Decision, "сводка", entries[0].Id, entries[1].Id) };

        TeamMemoryConsolidationService.FilterOps(ops, entries).Should().BeEmpty();
    }

    [Fact]
    public void FilterOps_ЗаписьВДвухОперациях_ВтораяОтбрасывается()
    {
        var entries = Entries(10);
        var ops = new[]
        {
            Merge(TeamMemoryType.Fact, "сводка", entries[0].Id, entries[1].Id),
            Drop(entries[0].Id),   // уже участвует в merge
        };

        var result = TeamMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().ContainSingle().Which.IsMerge.Should().BeTrue();
    }

    [Fact]
    public void FilterOps_MergeБезТекста_Отклоняется()
    {
        var entries = Entries(10);
        var ops = new[] { new TeamMemoryConsolidationOp("merge", [entries[0].Id, entries[1].Id], null, TeamMemoryType.Fact, "  ", null) };

        TeamMemoryConsolidationService.FilterOps(ops, entries).Should().BeEmpty();
    }

    // --- ParseOps: мусор = no-op, валидный ответ ---

    [Theory]
    [InlineData("")]
    [InlineData("не буду ничего объединять")]
    [InlineData("[{broken json")]
    [InlineData("{\"op\":\"merge\"}")]
    public void ParseOps_Мусор_ПустойСписок(string raw)
    {
        TeamMemoryConsolidationService.ParseOps(raw).Should().BeEmpty();
    }

    [Fact]
    public void ParseOps_ВалидныйОтвет_СПреамбулой()
    {
        var raw = "Готово:\n[{\"op\":\"merge\",\"ids\":[\"a\",\"b\"],\"type\":\"decision\",\"text\":\"сводка\",\"salience\":0.8},{\"op\":\"drop\",\"id\":\"c\"}]";

        var ops = TeamMemoryConsolidationService.ParseOps(raw);

        ops.Should().HaveCount(2);
        ops[0].IsMerge.Should().BeTrue();
        ops[0].Type.Should().Be(TeamMemoryType.Decision);
        ops[0].Salience.Should().Be(0.8);
        ops[1].IsDrop.Should().BeTrue();
        ops[1].Id.Should().Be("c");
    }
}
