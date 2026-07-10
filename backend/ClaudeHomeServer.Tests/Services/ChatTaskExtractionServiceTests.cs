using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// «Задачи из чата» (флаг chat-extract-tasks). Тестируем чистый парсинг ответа модели —
// именно он ловил баг (fix 6323a23): устойчивое извлечение JSON-массива из «болтливого» ответа.
public class ChatTaskExtractionServiceTests
{
    // ─── ExtractJsonArray: вырезание сбалансированного массива ────────────────

    [Fact]
    public void ExtractJsonArray_ЧистыйМассив()
    {
        ChatTaskExtractionService.ExtractJsonArray("[{\"title\":\"a\"}]")
            .Should().Be("[{\"title\":\"a\"}]");
    }

    [Fact]
    public void ExtractJsonArray_ВMarkdownFence()
    {
        var raw = "```json\n[{\"title\":\"a\"}]\n```";
        ChatTaskExtractionService.ExtractJsonArray(raw).Should().Be("[{\"title\":\"a\"}]");
    }

    [Fact]
    public void ExtractJsonArray_СПреамбулойИПослесловием()
    {
        var raw = "Вот задачи по итогам разговора:\n[{\"title\":\"a\"}]\nГотово!";
        ChatTaskExtractionService.ExtractJsonArray(raw).Should().Be("[{\"title\":\"a\"}]");
    }

    // Ключевая регрессия: скобки ВНУТРИ строки не должны обрывать массив раньше времени
    // (прежняя жадная пара IndexOf('[')…LastIndexOf(']') на этом ломалась/переедала).
    [Fact]
    public void ExtractJsonArray_СкобкиВнутриСтроки_НеОбрываютМассив()
    {
        var raw = "[{\"title\":\"Убрать мусор [во дворе]\"}]";
        ChatTaskExtractionService.ExtractJsonArray(raw).Should().Be(raw);
    }

    [Fact]
    public void ExtractJsonArray_ВложенныеМассивы_БалансСкобок()
    {
        var raw = "префикс [[1,2],[3]] суффикс";
        ChatTaskExtractionService.ExtractJsonArray(raw).Should().Be("[[1,2],[3]]");
    }

    [Fact]
    public void ExtractJsonArray_ЭкранированнаяКавычкаВСтроке()
    {
        var raw = "[{\"title\":\"строка с \\\" кавычкой ]\"}]";
        ChatTaskExtractionService.ExtractJsonArray(raw).Should().Be(raw);
    }

    [Theory]
    [InlineData("нет никакого массива")]
    [InlineData("")]
    [InlineData("[ незакрытый")]
    public void ExtractJsonArray_НетСбалансированногоМассива_Null(string raw)
    {
        ChatTaskExtractionService.ExtractJsonArray(raw).Should().BeNull();
    }

    // ─── ParseTasks: валидация и нормализация записей ─────────────────────────

    [Fact]
    public void ParseTasks_ВалидныйОтвет_ВозвращаетКандидатов()
    {
        var raw = "[{\"title\":\"Купить цветы\",\"due\":\"2026-07-15\",\"priority\":\"high\"}]";
        var res = ChatTaskExtractionService.ParseTasks(raw);
        res.Should().ContainSingle();
        res[0].Title.Should().Be("Купить цветы");
        res[0].Due.Should().Be("2026-07-15");
        res[0].Priority.Should().Be("high");
    }

    [Fact]
    public void ParseTasks_ОбрезаетTitleИОтбрасываетПустые()
    {
        var raw = "[{\"title\":\"  Дело  \"},{\"title\":\"\"},{\"title\":\"   \"},{\"due\":\"2026-01-01\"}]";
        var res = ChatTaskExtractionService.ParseTasks(raw);
        res.Should().ContainSingle();
        res[0].Title.Should().Be("Дело");
    }

    [Fact]
    public void ParseTasks_НевалиднаяДата_Null()
    {
        var raw = "[{\"title\":\"a\",\"due\":\"завтра\"},{\"title\":\"b\",\"due\":\"2026-13-40\"}]";
        var res = ChatTaskExtractionService.ParseTasks(raw);
        res.Should().HaveCount(2);
        res[0].Due.Should().BeNull();
        res[1].Due.Should().BeNull();
    }

    [Theory]
    [InlineData("HIGH", "high")]
    [InlineData("Urgent", "urgent")]
    [InlineData("bogus", null)]
    [InlineData(null, null)]
    public void ParseTasks_НормализуетПриоритет(string? input, string? expected)
    {
        var due = input is null ? "" : $",\"priority\":\"{input}\"";
        var raw = $"[{{\"title\":\"a\"{due}}}]";
        ChatTaskExtractionService.ParseTasks(raw)[0].Priority.Should().Be(expected);
    }

    [Fact]
    public void ParseTasks_МусорнаяСтрока_ПустойСписок()
    {
        ChatTaskExtractionService.ParseTasks("это не json вовсе").Should().BeEmpty();
    }

    [Fact]
    public void ParseTasks_ОбрезаетДо20Задач()
    {
        var items = string.Join(",", Enumerable.Range(0, 25).Select(i => $"{{\"title\":\"дело {i}\"}}"));
        ChatTaskExtractionService.ParseTasks($"[{items}]").Should().HaveCount(20);
    }

    [Fact]
    public void ParseTasks_ОтветВFence_Парсится()
    {
        var raw = "```json\n[{\"title\":\"Из fenced-ответа\"}]\n```";
        ChatTaskExtractionService.ParseTasks(raw).Should().ContainSingle()
            .Which.Title.Should().Be("Из fenced-ответа");
    }
}
