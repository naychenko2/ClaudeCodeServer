using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Парсер чекбоксов markdown ↔ задачи (флаг notes-task-sync). Чистая логика — юнит без окружения.
public class NoteTaskParserTests
{
    // ─── Parse ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("- [ ] Купить хлеб")]
    [InlineData("* [ ] Купить хлеб")]
    [InlineData("+ [ ] Купить хлеб")]
    [InlineData("   - [ ] Купить хлеб")]   // с отступом
    public void Parse_РазныеМаркерыИОтступ_РаспознаётЧекбокс(string line)
    {
        var res = NoteTaskParser.Parse(line);
        res.Should().ContainSingle();
        res[0].Text.Should().Be("Купить хлеб");
        res[0].Done.Should().BeFalse();
    }

    [Theory]
    [InlineData("- [x] Готово", true)]
    [InlineData("- [X] Готово", true)]
    [InlineData("- [ ] Не готово", false)]
    public void Parse_СостояниеГалочки(string line, bool done)
    {
        NoteTaskParser.Parse(line)[0].Done.Should().Be(done);
    }

    [Fact]
    public void Parse_ИгнорируетНеЧекбоксы_ИНумеруетСтроки()
    {
        var content = "# Заголовок\nпростой текст\n- [ ] Первая\nещё текст\n- [x] Вторая";
        var res = NoteTaskParser.Parse(content);

        res.Should().HaveCount(2);
        res[0].Line.Should().Be(2);   // 0-based индекс строки в контенте
        res[0].Text.Should().Be("Первая");
        res[1].Line.Should().Be(4);
        res[1].Done.Should().BeTrue();
    }

    [Fact]
    public void Parse_ВыделяетСрок_ИВырезаетЕгоИзТекста()
    {
        var res = NoteTaskParser.Parse("- [ ] Позвонить врачу 📅 2026-07-15")[0];
        res.Due.Should().Be("2026-07-15");
        res.Text.Should().Be("Позвонить врачу");
    }

    [Fact]
    public void Parse_БезСрока_DueNull()
    {
        NoteTaskParser.Parse("- [ ] Просто дело")[0].Due.Should().BeNull();
    }

    [Theory]
    [InlineData("🔁 every week", TaskRecurrenceType.Weekly, 1)]
    [InlineData("🔁 weekly", TaskRecurrenceType.Weekly, 1)]
    [InlineData("🔁 every 2 days", TaskRecurrenceType.Daily, 2)]
    [InlineData("🔁 every month", TaskRecurrenceType.Monthly, 1)]
    [InlineData("🔁 every 3 years", TaskRecurrenceType.Yearly, 3)]
    public void Parse_РазбираетПравилоПовтора(string marker, TaskRecurrenceType type, int interval)
    {
        var res = NoteTaskParser.Parse($"- [ ] Дело {marker}")[0];
        res.Recurrence.Should().NotBeNull();
        res.Recurrence!.Type.Should().Be(type);
        res.Recurrence.Interval.Should().Be(interval);
    }

    [Fact]
    public void Parse_НераспознанныйПовтор_RecurrenceNull()
    {
        NoteTaskParser.Parse("- [ ] Дело 🔁 когда-нибудь")[0].Recurrence.Should().BeNull();
    }

    [Fact]
    public void Parse_ВырезаетЭмодзиПриоритета_ИзТекста()
    {
        var res = NoteTaskParser.Parse("- [ ] Важное дело ⏫")[0];
        res.Text.Should().Be("Важное дело");
    }

    // Регрессия на баг с классом символов [⏫🔼…]: астральные эмодзи в тексте
    // (🚀🔥🐛) не должны биться при вырезании метаданных.
    [Fact]
    public void Parse_НеЛомаетАстральныеЭмодзиВТексте()
    {
        var res = NoteTaskParser.Parse("- [ ] Задеплоить 🚀 ракету 🔥 📅 2026-07-10")[0];
        res.Text.Should().Be("Задеплоить 🚀 ракету 🔥");
        res.Due.Should().Be("2026-07-10");
    }

    [Fact]
    public void Parse_ТолькоМетаданныеБезТекста_ФолбэкНаТелоСтроки()
    {
        // Если после вырезания метаданных ничего не осталось — берём исходное тело
        var res = NoteTaskParser.Parse("- [ ] ⏫")[0];
        res.Text.Should().NotBeEmpty();
    }

    // ─── SetChecked ──────────────────────────────────────────────────────────

    [Fact]
    public void SetChecked_СтавитГалочку()
    {
        NoteTaskParser.SetChecked("- [ ] Дело", 0, true).Should().Be("- [x] Дело");
    }

    [Fact]
    public void SetChecked_СниматГалочку()
    {
        NoteTaskParser.SetChecked("- [x] Дело", 0, false).Should().Be("- [ ] Дело");
    }

    [Fact]
    public void SetChecked_УжеВНужномСостоянии_ВозвращаетБезИзменений()
    {
        NoteTaskParser.SetChecked("- [x] Дело", 0, true).Should().Be("- [x] Дело");
    }

    [Fact]
    public void SetChecked_НеЧекбокс_Null()
    {
        NoteTaskParser.SetChecked("просто текст", 0, true).Should().BeNull();
    }

    [Fact]
    public void SetChecked_ИндексВнеДиапазона_Null()
    {
        NoteTaskParser.SetChecked("- [ ] Дело", 5, true).Should().BeNull();
    }

    [Fact]
    public void SetChecked_НужнуюСтрокуИзНескольких()
    {
        var content = "- [ ] Первая\n- [ ] Вторая";
        NoteTaskParser.SetChecked(content, 1, true).Should().Be("- [ ] Первая\n- [x] Вторая");
    }

    // ─── SetDue ──────────────────────────────────────────────────────────────

    [Fact]
    public void SetDue_ДобавляетСрок()
    {
        NoteTaskParser.SetDue("- [ ] Дело", 0, "2026-07-20").Should().Be("- [ ] Дело 📅 2026-07-20");
    }

    [Fact]
    public void SetDue_ЗаменяетСуществующийСрок()
    {
        NoteTaskParser.SetDue("- [ ] Дело 📅 2026-07-10", 0, "2026-08-01")
            .Should().Be("- [ ] Дело 📅 2026-08-01");
    }

    [Fact]
    public void SetDue_ПустойСрок_УбираетТокен()
    {
        NoteTaskParser.SetDue("- [ ] Дело 📅 2026-07-10", 0, null).Should().Be("- [ ] Дело");
    }

    [Fact]
    public void SetDue_НеЧекбокс_Null()
    {
        NoteTaskParser.SetDue("текст", 0, "2026-07-20").Should().BeNull();
    }
}
