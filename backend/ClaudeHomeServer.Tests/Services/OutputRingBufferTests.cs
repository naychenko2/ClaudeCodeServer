using ClaudeHomeServer.Services;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты кольцевого буфера вывода — общего для терминалов и дев-серверов.
/// </summary>
public class OutputRingBufferTests
{
    [Fact]
    public void GetAll_ReturnsEverythingWhenUnderLimit()
    {
        var buf = new OutputRingBuffer(100);
        buf.Append("раз\r\n");
        buf.Append("два\r\n");
        buf.GetAll().Should().Be("раз\r\nдва\r\n");
    }

    [Fact]
    public void Append_DropsOldestBeyondLimit()
    {
        var buf = new OutputRingBuffer(10);
        buf.Append(new string('a', 8));
        buf.Append(new string('b', 5));

        var all = buf.GetAll();
        all.Should().HaveLength(10);
        all.Should().Be("aaaaabbbbb");
    }

    [Fact]
    public void Append_KeepsSizeBounded_WithoutTrimmingEveryTime()
    {
        // Обрезка сдвигает весь буфер, поэтому она с запасом: размер гуляет между
        // лимитом и лимитом+25%, но за верхнюю границу не уходит никогда.
        var buf = new OutputRingBuffer(100);
        for (var i = 0; i < 500; i++) buf.Append("0123456789");

        buf.GetAll().Length.Should().BeInRange(100, 125);
    }

    [Fact]
    public void Append_HandlesChunkLargerThanLimit()
    {
        var buf = new OutputRingBuffer(5);
        buf.Append("0123456789");
        buf.GetAll().Should().Be("56789");
    }

    [Fact]
    public void TailLines_ReturnsLastLinesWithoutCarriageReturns()
    {
        var buf = new OutputRingBuffer();
        buf.Append("первая\r\nвторая\r\nтретья\r\n");

        buf.TailLines(2).Should().Be("вторая\nтретья");
    }

    [Fact]
    public void TailLines_SkipsBlankLines()
    {
        var buf = new OutputRingBuffer();
        buf.Append("строка\r\n\r\n\r\n");

        buf.TailLines(40).Should().Be("строка");
    }

    [Fact]
    public void TailLines_OnEmptyBufferReturnsEmpty()
    {
        new OutputRingBuffer().TailLines(40).Should().BeEmpty();
    }
}
