using System.Text;
using ClaudeHomeServer.DeviceAgent.Exec;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

/// <summary>Конец хода в выводе CLI: строка result, где бы её ни разрезали куски чтения.</summary>
public class ResultLineScannerTests
{
    private static int Feed(ResultLineScanner scanner, string text) => scanner.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Находит_строку_result_и_только_её()
    {
        var scanner = new ResultLineScanner();

        Feed(scanner, "{\"type\":\"system\",\"subtype\":\"init\"}\n").Should().Be(0);
        Feed(scanner, "{\"type\":\"assistant\",\"text\":\"{\\\"type\\\":\\\"result\\\"\"}\n").Should().Be(0, "result внутри текста — не конец хода");
        Feed(scanner, "echo:{\"type\":\"result\"}\n").Should().Be(0, "префикс — только с начала строки");
        Feed(scanner, "{\"type\":\"result\",\"subtype\":\"success\"}\n{\"type\":\"result\"}\n").Should().Be(2);
    }

    [Fact]
    public void Строка_разрезанная_на_куски_находится_один_раз()
    {
        var scanner = new ResultLineScanner();
        var line = "{\"type\":\"user\"}\n{\"type\":\"result\",\"is_error\":false}\n";

        var found = line.Select(c => Feed(scanner, c.ToString())).Sum();

        found.Should().Be(1);
    }

    [Fact]
    public void Несколько_result_подряд_засчитываются_каждый()
    {
        var scanner = new ResultLineScanner();

        Feed(scanner, "{\"type\":\"result\",\"subtype\":\"success\"}\n{\"type\":\"result\"}\n{\"type\":\"result\",\"is_error\":true}\n")
            .Should().Be(3);
    }

    [Fact]
    public void Result_внутри_строки_другого_типа_не_конец_хода()
    {
        var scanner = new ResultLineScanner();

        Feed(scanner, "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"{\\\"type\\\":\\\"result\\\"}\"}]}}\n")
            .Should().Be(0, "tool_result — строка user");
        Feed(scanner, "{\"type\":\"assistant\",\"parent_tool_use_id\":\"toolu_1\",\"message\":{\"type\":\"result\"}}\n")
            .Should().Be(0, "сообщение субагента — строка assistant");
        Feed(scanner, "{\"type\":\"result_extra\"}\n{\"type\":\"results\"}\n{\"type\":\"result\"x}\n")
            .Should().Be(0, "значение type должно совпасть целиком");
    }

    [Fact]
    public void Result_засчитывается_на_переводе_строки_даже_через_границу_куска()
    {
        var scanner = new ResultLineScanner();

        Feed(scanner, "{\"type\":\"user\"}\n{\"type\":\"resu").Should().Be(0);
        Feed(scanner, "lt\",\"result\":\"готово\"").Should().Be(0, "строка ещё не дописана");
        Feed(scanner, "}\n{\"type\":\"res").Should().Be(1);
        Feed(scanner, "ult\"").Should().Be(0, "разделитель ещё не пришёл");
        Feed(scanner, "}").Should().Be(0);
        Feed(scanner, "\n").Should().Be(1);
    }
}
