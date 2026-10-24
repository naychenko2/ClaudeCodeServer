using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож: при старте хоста обязан вызываться ReviewTreePool.RestoreLeasesAsync. Без него учёт
/// аренд слотов ревью после рестарта пуст, и слот открытого ревью отдали бы другой задаче.
/// </summary>
public class ReviewTreeLeaseRestoreGuardTests
{
    [Fact]
    public void ProgramCs_ПриСтартеВосстанавливаетАрендыСлотовРевью()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "backend", "ClaudeHomeServer", "Program.cs")))
            dir = dir.Parent;
        dir.Should().NotBeNull("корень репозитория находится подъёмом от " + AppContext.BaseDirectory);

        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, "backend", "ClaudeHomeServer", "Program.cs"));

        lines.Any(l => !l.TrimStart().StartsWith("//")
                && l.Contains("GetRequiredService<ReviewTreePool>()") && l.Contains("RestoreLeasesAsync()"))
            .Should().BeTrue("Program.cs обязан звать ReviewTreePool.RestoreLeasesAsync при старте");
    }
}
