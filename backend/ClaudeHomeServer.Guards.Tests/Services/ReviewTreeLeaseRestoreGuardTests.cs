using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Сторож старта: после рестарта учёт аренд пула пуст, и без RestoreLeasesAsync слот открытого
// ревью отдали бы другой задаче и сбросили под ногами ревьюера. Вызов стоит в Program.cs вне
// подсистем, поэтому удаление строки никто бы не заметил — держим его текстовым сторожем.
// Ищем вызов по имени, а не по всей цепочке: перенос строки или локальная переменная не должны краснить сторож.
public class ReviewTreeLeaseRestoreGuardTests
{
    private static readonly Regex Call = new(@"\bRestoreLeasesAsync\s*\(", RegexOptions.Compiled);

    // Строковые литералы разбираем вместе с комментариями (в них бывает «//»), чтобы токены не склеились;
    // комментарии вычищаются полностью, литерал при желании опустошается: имя вызова в тексте — не вызов.
    private static readonly Regex Tokens = new(
        @"@?""(?:\\.|""""|[^""\\])*""|/\*.*?\*/|//[^\r\n]*", RegexOptions.Compiled | RegexOptions.Singleline);

    private static bool IsLiteral(string token) => token[0] == '"' || token.StartsWith("@\"", StringComparison.Ordinal);

    internal static string StripComments(string text) =>
        Tokens.Replace(text, m => IsLiteral(m.Value) ? m.Value : "");

    internal static string StripCommentsAndLiterals(string text) =>
        Tokens.Replace(text, m => IsLiteral(m.Value) ? "\"\"" : "");

    // Пустая строка — вызов на месте; иначе причина отказа.
    internal static string? Verdict(string text)
    {
        if (Call.IsMatch(StripCommentsAndLiterals(text))) return null;
        if (Call.IsMatch(StripComments(text))) return "имя RestoreLeasesAsync есть только в строковом литерале";
        return Call.IsMatch(text)
            ? "вызов RestoreLeasesAsync закомментирован"
            : "вызов RestoreLeasesAsync не найден";
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if ((Directory.Exists(git) || File.Exists(git)) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Корень репозитория (.git + backend/) не найден выше каталога тестов");
    }

    [Fact]
    public void Program_НаСтартеВосстанавливаетАрендыСлотовРевью()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "backend", "ClaudeHomeServer", "Program.cs"));

        var verdict = Verdict(text);

        verdict.Should().BeNull(
            "без восстановления аренд после рестарта слот открытого ревью отдадут другой задаче");
    }

    [Theory]
    [InlineData("await app.Services.GetRequiredService<ReviewTreePool>().RestoreLeasesAsync();")]
    [InlineData("await app.Services\n    .GetRequiredService<ReviewTreePool>()\n    .RestoreLeasesAsync();")]
    [InlineData("var pool = app.Services.GetRequiredService<ReviewTreePool>();\nawait pool.RestoreLeasesAsync();")]
    [InlineData("await Task.Run(() => app.Services.GetRequiredService<ReviewTreePool>().RestoreLeasesAsync());")]
    [InlineData("await Task.Run(async () =>\n{\n    await pool.RestoreLeasesAsync();\n});")]
    public void Вызов_НаходитсяПоИмени_НезависимоОтНаписания(string text) =>
        Verdict(text).Should().BeNull();

    [Theory]
    [InlineData("// await pool.RestoreLeasesAsync();")]
    [InlineData("/* await app.Services.GetRequiredService<ReviewTreePool>().RestoreLeasesAsync(); */")]
    [InlineData("/*\n await pool.RestoreLeasesAsync();\n*/")]
    [InlineData("var x = 1; // await pool.RestoreLeasesAsync();")]
    public void ЗакомментированныйВызов_НеСчитаетсяВызовом(string text) =>
        Verdict(text).Should().Be("вызов RestoreLeasesAsync закомментирован");

    [Fact]
    public void ОтсутствующийВызов_ОтличаетсяОтЗакомментированного() =>
        Verdict("var x = 1;").Should().Be("вызов RestoreLeasesAsync не найден");

    [Fact]
    public void Вызов_ПослеСтроковогоЛитералаСДвойнымСлэшем_НеТеряется() =>
        Verdict("var u = \"http://x\"; await pool.RestoreLeasesAsync();").Should().BeNull();

    [Theory]
    [InlineData("var s = \"await pool.RestoreLeasesAsync();\";")]
    [InlineData("Console.WriteLine(\"RestoreLeasesAsync(\");")]
    [InlineData("var s = @\"pool.RestoreLeasesAsync(\"\"x\"\")\";")]
    public void ИмяВСтроковомЛитерале_НеСчитаетсяВызовом(string text) =>
        Verdict(text).Should().Be("имя RestoreLeasesAsync есть только в строковом литерале");
}
