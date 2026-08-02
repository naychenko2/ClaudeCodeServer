using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Раскрытие @-импортов CLAUDE.md для блока «слой CLI». Это наша реконструкция, а не вывод
// самого CLI, поэтому важны границы: цикл не должен вешать сборку снимка, а формы, которые
// мы намеренно не раскрываем (@~/…, абсолютные, выход выше папки), обязаны остаться текстом.
public class ClaudeMdExpanderTests : IDisposable
{
    private readonly string _dir;

    public ClaudeMdExpanderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs-claude-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Импорт_ЗаменяетсяСодержимымФайла()
    {
        Write(Path.Combine("rules", "git.md"), "Коммиты по-русски");
        var root = Write("CLAUDE.md", "Заголовок\n@rules/git.md\nХвост");

        var text = ClaudeMdExpander.Read(root);

        text.Should().Contain("Коммиты по-русски");
        text.Should().Contain("Заголовок").And.Contain("Хвост");
    }

    [Fact]
    public void ВложенныеИмпорты_РаскрываютсяРекурсивно()
    {
        Write(Path.Combine("rules", "inner.md"), "Глубоко");
        Write(Path.Combine("rules", "outer.md"), "@inner.md");
        var root = Write("CLAUDE.md", "@rules/outer.md");

        ClaudeMdExpander.Read(root).Should().Contain("Глубоко");
    }

    [Fact]
    public void Цикл_НеВешаетИПомечается()
    {
        Write(Path.Combine("rules", "b.md"), "Бэ\n@a.md");
        Write(Path.Combine("rules", "a.md"), "А\n@b.md");
        var root = Write("CLAUDE.md", "@rules/a.md");

        var text = ClaudeMdExpander.Read(root);

        text.Should().Contain("А").And.Contain("Бэ");
        text.Should().Contain("циклическая ссылка");
    }

    [Theory]
    // Домашний путь и абсолютный CLI понимает, но мы намеренно не резолвим
    [InlineData("@~/.claude/CLAUDE.md")]
    [InlineData("@/etc/hosts")]
    // Выход выше папки исходного файла — тот же приём, что SafeJoin
    [InlineData("@../secrets.md")]
    public void НераскрываемыеФормы_ОстаютсяТекстом(string importLine)
    {
        var root = Write("CLAUDE.md", importLine);

        var text = ClaudeMdExpander.Read(root);

        text.Should().Contain(importLine.Trim());
    }

    [Fact]
    public void ОтсутствующийФайл_ДаётNull()
    {
        ClaudeMdExpander.Read(Path.Combine(_dir, "нет-такого.md")).Should().BeNull();
    }

    [Fact]
    public void ОгромныйФайл_ОбрезаетсяПоЛимиту()
    {
        var line = new string('я', 1000) + "\n";
        var root = Write("CLAUDE.md", string.Concat(Enumerable.Repeat(line, 400)));

        var text = ClaudeMdExpander.Read(root)!;

        text.Length.Should().BeLessThan(ClaudeMdExpander.MaxTotalChars + 200);
        text.Should().Contain("обрезано");
    }

    [Fact]
    public void СсылкаВнутриТекста_НеСчитаетсяИмпортом()
    {
        // «см. @rules/git.md» — это упоминание в предложении, а не директива импорта
        Write(Path.Combine("rules", "git.md"), "СЕКРЕТНОЕ");
        var root = Write("CLAUDE.md", "Подробности см. @rules/git.md в конце");

        ClaudeMdExpander.Read(root).Should().NotContain("СЕКРЕТНОЕ");
    }
}
