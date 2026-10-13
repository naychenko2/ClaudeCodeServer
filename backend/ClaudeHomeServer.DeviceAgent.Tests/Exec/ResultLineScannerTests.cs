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
}
