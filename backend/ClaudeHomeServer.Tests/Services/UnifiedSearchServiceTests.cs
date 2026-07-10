using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Единый поиск (флаг unified-search). Тестируем чистые части: матчинг/ранжирование
// задач и построение сниппета вокруг совпадения.
public class UnifiedSearchServiceTests
{
    private static TaskItem Task(string title, string desc = "", TaskItemStatus status = TaskItemStatus.Todo,
        List<string>? labels = null, DateTime? updated = null) =>
        new()
        {
            Title = title, Description = desc, Status = status,
            Labels = labels ?? [], UpdatedAt = updated ?? DateTime.UtcNow,
        };

    // ─── MatchTasks ──────────────────────────────────────────────────────────

    [Fact]
    public void MatchTasks_НаходитПоНазваниюОписаниюМетке_БезРегистра()
    {
        var tasks = new[]
        {
            Task("Купить ЦВЕТЫ"),
            Task("Другое", desc: "не забыть про цветы в описании"),
            Task("Третье", labels: ["цветы"]),
            Task("Мимо", desc: "ничего общего"),
        };

        var res = UnifiedSearchService.MatchTasks(tasks, "цветы").ToList();
        res.Should().HaveCount(3);
        res.Should().NotContain(t => t.Title == "Мимо");
    }

    [Fact]
    public void MatchTasks_ЗавершённыеНиже()
    {
        var tasks = new[]
        {
            Task("дело done", status: TaskItemStatus.Done, updated: new DateTime(2026, 7, 10)),
            Task("дело todo", status: TaskItemStatus.Todo, updated: new DateTime(2026, 7, 1)),
        };

        var res = UnifiedSearchService.MatchTasks(tasks, "дело").ToList();
        res[0].Title.Should().Be("дело todo");   // незавершённая выше, несмотря на более старый updatedAt
        res[1].Title.Should().Be("дело done");
    }

    [Fact]
    public void MatchTasks_ВРамкахСтатуса_НовыеПервыми()
    {
        var tasks = new[]
        {
            Task("дело старое", updated: new DateTime(2026, 7, 1)),
            Task("дело новое", updated: new DateTime(2026, 7, 10)),
        };

        var res = UnifiedSearchService.MatchTasks(tasks, "дело").ToList();
        res[0].Title.Should().Be("дело новое");
        res[1].Title.Should().Be("дело старое");
    }

    [Fact]
    public void MatchTasks_НетСовпадений_Пусто()
    {
        UnifiedSearchService.MatchTasks(new[] { Task("что-то") }, "ничего").Should().BeEmpty();
    }

    // ─── Snippet ─────────────────────────────────────────────────────────────

    [Fact]
    public void Snippet_ПустойТекст_ПустаяСтрока()
    {
        UnifiedSearchService.Snippet("", "q").Should().BeEmpty();
    }

    [Fact]
    public void Snippet_ЕстьСовпадение_ФрагментВокругНего()
    {
        var text = "начало " + new string('x', 200) + " МЕТКА " + new string('y', 200) + " конец";
        var s = UnifiedSearchService.Snippet(text, "метка");
        s.Should().Contain("МЕТКА");
        s.Should().StartWith("…");   // впереди есть обрезанный текст
        s.Should().EndWith("…");     // и сзади тоже
    }

    [Fact]
    public void Snippet_СовпадениеВНачале_БезВедущегоМноготочия()
    {
        var s = UnifiedSearchService.Snippet("МЕТКА и дальше немного текста", "метка");
        s.Should().StartWith("МЕТКА");
    }

    [Fact]
    public void Snippet_НетСовпадения_КороткийТекстЦеликом()
    {
        UnifiedSearchService.Snippet("короткий текст без совпадения", "zzz")
            .Should().Be("короткий текст без совпадения");
    }

    [Fact]
    public void Snippet_НетСовпадения_ДлинныйТекстОбрезается()
    {
        var text = new string('a', 300);
        var s = UnifiedSearchService.Snippet(text, "zzz");
        s.Should().HaveLength(161);   // 160 символов + «…»
        s.Should().EndWith("…");
    }

    [Fact]
    public void Snippet_ПереносыСтрокСхлопываютсяВПробел()
    {
        UnifiedSearchService.Snippet("строка1\nстрока2", "zzz").Should().Be("строка1 строка2");
    }
}
