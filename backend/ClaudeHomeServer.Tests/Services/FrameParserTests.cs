using System.Text;
using ConPtyBridge;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты парсера кадров ConPTY-моста: протокол [type:1][len:4 BE][payload],
/// устойчивость к фрагментации на любых границах read (кадры приходят из пайпа
/// произвольными кусками). Парсер — порт стейт-машины из pty-bridge.c.
/// </summary>
public class FrameParserTests
{
    /// <summary>Собрать кадр протокола: [type][len:4 BE][payload].</summary>
    private static byte[] Frame(byte type, byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = type;
        frame[1] = (byte)((payload.Length >> 24) & 0xFF);
        frame[2] = (byte)((payload.Length >> 16) & 0xFF);
        frame[3] = (byte)((payload.Length >> 8) & 0xFF);
        frame[4] = (byte)(payload.Length & 0xFF);
        payload.CopyTo(frame, 5);
        return frame;
    }

    /// <summary>Парсер с аккумуляцией data-кусков и resize-вызовов.</summary>
    private static (FrameParser parser, MemoryStream data, List<(int Cols, int Rows)> resizes) MakeParser()
    {
        var data = new MemoryStream();
        var resizes = new List<(int, int)>();
        var parser = new FrameParser(
            onData: span => data.Write(span),
            onResize: (c, r) => resizes.Add((c, r)));
        return (parser, data, resizes);
    }

    [Fact]
    public void Полный_кадр_данных_отдаёт_payload_как_есть()
    {
        var (parser, data, resizes) = MakeParser();
        var payload = Encoding.UTF8.GetBytes("echo hello\r");

        parser.Feed(Frame(0x00, payload));

        data.ToArray().Should().Equal(payload);
        resizes.Should().BeEmpty();
    }

    [Fact]
    public void Побайтовая_фрагментация_не_ломает_кадр()
    {
        var (parser, data, _) = MakeParser();
        var payload = Encoding.UTF8.GetBytes("ls -la\r");
        var frame = Frame(0x00, payload);

        // Худший случай пайпа: каждый read возвращает один байт
        foreach (var b in frame)
            parser.Feed([b]);

        data.ToArray().Should().Equal(payload);
    }

    [Theory]
    // Разрезы на каждой интересной границе: внутри заголовка (1..4),
    // на границе заголовок/payload (5) и внутри payload (6..11)
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void Разрез_кадра_в_любой_позиции_даёт_тот_же_payload(int splitIndex)
    {
        var (parser, data, _) = MakeParser();
        var payload = Encoding.UTF8.GetBytes("привет");  // 12 байт UTF-8
        var frame = Frame(0x00, payload);

        parser.Feed(frame.AsSpan(0, splitIndex));
        parser.Feed(frame.AsSpan(splitIndex));

        data.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Пустой_кадр_не_дергает_колбэки_и_не_ломает_следующий()
    {
        var (parser, data, resizes) = MakeParser();
        var next = Encoding.UTF8.GetBytes("x");

        parser.Feed(Frame(0x00, []));       // len=0 — ничего не должно случиться
        parser.Feed(Frame(0x00, next));     // следующий кадр разбирается штатно

        data.ToArray().Should().Equal(next);
        resizes.Should().BeEmpty();
    }

    [Fact]
    public void Resize_кадр_разбирает_cols_rows_в_big_endian()
    {
        var (parser, data, resizes) = MakeParser();

        // cols=120 (0x0078), rows=30 (0x001E)
        parser.Feed(Frame(0x01, [0x00, 0x78, 0x00, 0x1E]));

        resizes.Should().Equal((120, 30));
        data.Length.Should().Be(0);
    }

    [Fact]
    public void Смешанный_поток_в_одном_буфере_сохраняет_порядок()
    {
        var (parser, data, resizes) = MakeParser();
        var part1 = Encoding.UTF8.GetBytes("abc");
        var part2 = Encoding.UTF8.GetBytes("def");

        // data + resize + data одним куском — resize не рвёт данные
        var stream = Frame(0x00, part1)
            .Concat(Frame(0x01, [0x00, 0x50, 0x00, 0x18]))  // 80x24
            .Concat(Frame(0x00, part2))
            .ToArray();
        parser.Feed(stream);

        data.ToArray().Should().Equal("abcdef"u8.ToArray());
        resizes.Should().Equal((80, 24));
    }

    [Fact]
    public void Большой_payload_стримится_кусками_без_потерь()
    {
        var (parser, data, _) = MakeParser();
        var payload = new byte[200_000];
        new Random(42).NextBytes(payload);
        var frame = Frame(0x00, payload);

        // Скармливаем произвольными кусками по 4096
        for (var i = 0; i < frame.Length; i += 4096)
            parser.Feed(frame.AsSpan(i, Math.Min(4096, frame.Length - i)));

        data.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Неизвестный_type_молча_пропускается()
    {
        var (parser, data, resizes) = MakeParser();
        var next = Encoding.UTF8.GetBytes("ok");

        parser.Feed(Frame(0x7F, [1, 2, 3, 4, 5]));  // неизвестный тип с payload
        parser.Feed(Frame(0x00, next));             // следующий кадр не пострадал

        data.ToArray().Should().Equal(next);
        resizes.Should().BeEmpty();
    }

    [Fact]
    public void Resize_с_длинным_payload_берёт_первые_4_байта_и_не_падает()
    {
        var (parser, _, resizes) = MakeParser();

        // len=6: мусорный хвост за 4 байтами размера игнорируется (как в C-версии)
        parser.Feed(Frame(0x01, [0x00, 0x64, 0x00, 0x20, 0xAA, 0xBB]));  // 100x32 + мусор

        resizes.Should().Equal((100, 32));
    }
}
