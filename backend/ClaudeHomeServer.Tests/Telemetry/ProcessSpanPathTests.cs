using System.Diagnostics;
using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Тег <c>command</c> спана <c>process.start</c> не должен нести путь.
///
/// Регрессия, найденная на боевых данных: спан приезжал в SigNoz со значением
/// <c>C:\Users\depec\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe</c> —
/// то есть с именем пользователя ОС внутри. Санитайзер это пропускал по построению: он
/// классифицирует атрибуты по ИМЕНИ тега, а <c>command</c> состоит в allowlist (хэшируются
/// ключи вида *_path). Поэтому режем в источнике.
/// </summary>
public class ProcessSpanPathTests
{
    [Theory]
    // Ровно то, что уехало на бой
    [InlineData(@"C:\Users\depec\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe", "claude.exe")]
    [InlineData(@"C:\Program Files\nodejs\claude.cmd", "claude.cmd")]
    // Песочница: путь приходит с unix-разделителями, Path.GetFileName на Windows их понимает
    [InlineData("/usr/local/bin/claude", "claude")]
    [InlineData("/app/run-turn.sh", "run-turn.sh")]
    // Уже без каталогов — оставляем как есть
    [InlineData("claude", "claude")]
    [InlineData("docker", "docker")]
    public void ExecutableName_KeepsOnlyFileName(string command, string expected)
    {
        TurnTelemetry.ExecutableName(command).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExecutableName_Missing_IsUnknown(string? command)
    {
        TurnTelemetry.ExecutableName(command).Should().Be("unknown");
    }

    [Fact]
    public void ProcessSpan_DoesNotCarryUserProfilePath()
    {
        // Проверяем сам спан, а не только чистую функцию: правило легко обойти,
        // выставив тег мимо хелпера
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServerActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var span = TurnTelemetry.StartProcessSpan(
            kind: "local",
            command: @"C:\Users\depec\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe",
            sessionId: "sess-1",
            mcpConfigHash: "deadbeef");

        span.Should().NotBeNull();
        var command = span!.GetTagItem("command") as string;

        command.Should().Be("claude.exe");
        command.Should().NotContain("depec", "имя пользователя ОС — это PII");
        command.Should().NotContain(@"\", "каталогов в теге быть не должно");
        span.GetTagItem("kind").Should().Be("local", "разрез local/docker обязан сохраниться");
    }
}
