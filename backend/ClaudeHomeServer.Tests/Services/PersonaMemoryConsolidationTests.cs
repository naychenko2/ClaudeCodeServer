using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Детерминированные гейты консолидации памяти: чужие id, cap 30%, merge разных типов,
// мусорный ответ LLM (no-op), порядок вытеснения по retention-скорингу
public class PersonaMemoryConsolidationTests
{
    private static readonly DateTime Now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private static PersonaMemoryEntry Entry(string id, PersonaMemoryType type = PersonaMemoryType.Semantic,
        double salience = 1.0, double ageDays = 0) => new()
    {
        PersonaId = "p1",
        Type = type,
        Text = $"запись {id}",
        Salience = salience,
        CreatedAt = Now.AddDays(-ageDays),
        LastAccessedAt = Now.AddDays(-ageDays),
    };

    private static List<PersonaMemoryEntry> Entries(int count, PersonaMemoryType type = PersonaMemoryType.Semantic) =>
        Enumerable.Range(1, count).Select(i => Entry($"e{i}", type)).ToList();

    // Записи с реальными Guid-Id; для наглядности берём их из списка по индексу
    private static MemoryConsolidationOp Merge(PersonaMemoryType type, string text, params string[] ids) =>
        new("merge", ids.ToList(), null, type, text, null);

    private static MemoryConsolidationOp Drop(string id) => new("drop", null, id, null, null, null);

    // --- FilterOps: гейты ---

    [Fact]
    public void FilterOps_НеизвестныеId_Игнорируются()
    {
        var entries = Entries(10);
        var ops = new[]
        {
            // Один валидный источник + чужой id → после фильтрации <2 → merge отброшен
            Merge(PersonaMemoryType.Semantic, "сводка", entries[0].Id, "чужой-id"),
            // Два валидных + чужой → merge остаётся с двумя валидными
            Merge(PersonaMemoryType.Semantic, "сводка 2", entries[1].Id, entries[2].Id, "ещё-чужой"),
            Drop("несуществующий"),
        };

        var result = PersonaMemoryConsolidationService.FilterOps(ops, entries);

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

        var result = PersonaMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().HaveCount(3);
        result.Select(o => o.Id).Should().NotContain(entries[3].Id);
    }

    [Fact]
    public void FilterOps_Cap_УчитываетИсточникиMerge()
    {
        var entries = Entries(10);   // cap = 3
        var ops = new[]
        {
            Merge(PersonaMemoryType.Semantic, "сводка", entries[0].Id, entries[1].Id, entries[2].Id),
            Drop(entries[3].Id),   // merge уже занял весь cap
        };

        var result = PersonaMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().ContainSingle().Which.IsMerge.Should().BeTrue();
    }

    [Fact]
    public void FilterOps_MergeРазныхТипов_Отклоняется()
    {
        var semantic = Entry("s1");
        var episodic = Entry("ep1", PersonaMemoryType.Episodic);
        var entries = new List<PersonaMemoryEntry> { semantic, episodic,
            Entry("s2"), Entry("s3"), Entry("s4"), Entry("s5"), Entry("s6") };

        var ops = new[] { Merge(PersonaMemoryType.Semantic, "сводка", semantic.Id, episodic.Id) };

        PersonaMemoryConsolidationService.FilterOps(ops, entries).Should().BeEmpty();
    }

    [Fact]
    public void FilterOps_ЗаявленныйТипНеСовпадаетСИсточниками_Отклоняется()
    {
        var entries = Entries(10, PersonaMemoryType.Episodic);

        var ops = new[] { Merge(PersonaMemoryType.Semantic, "сводка", entries[0].Id, entries[1].Id) };

        PersonaMemoryConsolidationService.FilterOps(ops, entries).Should().BeEmpty();
    }

    [Fact]
    public void FilterOps_ЗаписьВДвухОперациях_ВтораяОтбрасывается()
    {
        var entries = Entries(10);
        var ops = new[]
        {
            Merge(PersonaMemoryType.Semantic, "сводка", entries[0].Id, entries[1].Id),
            Drop(entries[0].Id),   // уже участвует в merge
        };

        var result = PersonaMemoryConsolidationService.FilterOps(ops, entries);

        result.Should().ContainSingle().Which.IsMerge.Should().BeTrue();
    }

    [Fact]
    public void FilterOps_MergeБезТекста_Отклоняется()
    {
        var entries = Entries(10);
        var ops = new[] { new MemoryConsolidationOp("merge", [entries[0].Id, entries[1].Id], null, PersonaMemoryType.Semantic, "  ", null) };

        PersonaMemoryConsolidationService.FilterOps(ops, entries).Should().BeEmpty();
    }

    // --- ParseOps: мусор = no-op ---

    [Theory]
    [InlineData("")]
    [InlineData("не буду ничего объединять")]
    [InlineData("[{broken json")]
    [InlineData("{\"op\":\"merge\"}")]
    public void ParseOps_Мусор_ПустойСписок(string raw)
    {
        PersonaMemoryConsolidationService.ParseOps(raw).Should().BeEmpty();
    }

    [Fact]
    public void ParseOps_ВалидныйОтвет_СПреамбулой()
    {
        var raw = "Готово:\n[{\"op\":\"merge\",\"ids\":[\"a\",\"b\"],\"type\":\"semantic\",\"text\":\"сводка\",\"salience\":0.8},{\"op\":\"drop\",\"id\":\"c\"}]";

        var ops = PersonaMemoryConsolidationService.ParseOps(raw);

        ops.Should().HaveCount(2);
        ops[0].IsMerge.Should().BeTrue();
        ops[0].Type.Should().Be(PersonaMemoryType.Semantic);
        ops[0].Salience.Should().Be(0.8);
        ops[1].IsDrop.Should().BeTrue();
        ops[1].Id.Should().Be("c");
    }

    // --- SelectEvictionIds: порядок вытеснения ---

    [Fact]
    public void SelectEvictionIds_НетПереполнения_Пусто()
    {
        var entries = Entries(5);

        PersonaMemoryConsolidationService
            .SelectEvictionIds(entries, 5, MemoryScoringOptions.Default, Now)
            .Should().BeEmpty();
    }

    [Fact]
    public void SelectEvictionIds_ВытесняетНаименееЦенные()
    {
        var valuable = Entry("v", salience: 1.0, ageDays: 0);
        var stale = Entry("stale", salience: 0.1, ageDays: 120);      // старая мелочь — первая на выход
        var medium = Entry("m", salience: 0.5, ageDays: 30);
        var entries = new List<PersonaMemoryEntry> { valuable, stale, medium };

        var evicted = PersonaMemoryConsolidationService
            .SelectEvictionIds(entries, 2, MemoryScoringOptions.Default, Now);

        evicted.Should().ContainSingle().Which.Should().Be(stale.Id);
    }

    [Fact]
    public void SelectEvictionIds_ВытесняетНужноеКоличество()
    {
        var entries = Enumerable.Range(1, 10)
            .Select(i => Entry($"e{i}", salience: i / 10.0, ageDays: i))
            .ToList();

        var evicted = PersonaMemoryConsolidationService
            .SelectEvictionIds(entries, 7, MemoryScoringOptions.Default, Now);

        evicted.Should().HaveCount(3);
    }
}
