using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Сторож старта: после рестарта учёт аренд пула пуст, и без RestoreLeasesAsync слот открытого
// ревью отдали бы другой задаче и сбросили под ногами ревьюера. Вызов стоит в Program.cs вне
// подсистем, поэтому удаление строки никто бы не заметил — держим его текстовым сторожем.
public class ReviewTreePoolStartupGuardTests
{
    private static string ProgramText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        dir.Should().NotBeNull("не нашли корень репо по CLAUDE.md от AppContext.BaseDirectory");
        return File.ReadAllText(Path.Combine(dir!.FullName, "backend", "ClaudeHomeServer", "Program.cs"));
    }

    [Fact]
    public void Program_НаСтартеВосстанавливаетАрендыСлотовРевью()
    {
        var code = Regex.Replace(ProgramText(), @"^\s*//.*$", "", RegexOptions.Multiline);

        code.Should().MatchRegex(@"await\s+app\.Services\.GetRequiredService<ReviewTreePool>\(\)\s*\.RestoreLeasesAsync\(\)\s*;",
            "без восстановления аренд после рестарта слот открытого ревью отдадут другой задаче");
    }
}
