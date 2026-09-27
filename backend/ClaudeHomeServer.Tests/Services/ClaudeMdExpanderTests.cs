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

    // Импорт внутри ```-забора CLI не раскрывает — это пример, а не директива. Наша
    // реконструкция обязана совпадать с ним: иначе пример на существующий файл вклеивает
    // в снимок промпта то, чего в контексте хода нет, а пример на несуществующий даёт
    // ложную находку «правило в контекст не едет»
    [Fact]
    public void ИмпортВнутриЗабора_НеРаскрываетсяИНеСчитаетсяПотерянным()
    {
        Write(Path.Combine("rules", "git.md"), "Коммиты по-русски");
        var root = Write("CLAUDE.md", """
            Заголовок

            ```md
            @rules/git.md
            @rules/опечатка.md
            ```

            Хвост
            """);

        var result = ClaudeMdExpander.Expand(root);

        result.ImportCount.Should().Be(0);
        result.MissingImports.Should().BeEmpty();
        result.Text.Should().NotContain("Коммиты по-русски");
        result.Text.Should().Contain("@rules/git.md");     // строка осталась как есть
    }

    // Забор закрывается по CommonMark: вложенный ``` внутри четырёх бэктиков внешний
    // не закрывает, и импорт после него — всё ещё пример
    [Fact]
    public void ВложенныйЗабор_ВнутреннийНеОткрываетРазборИмпортов()
    {
        Write(Path.Combine("rules", "git.md"), "Коммиты по-русски");
        var root = Write("CLAUDE.md", """
            ````md
            ```
            @rules/git.md
            ```
            ````
            """);

        var result = ClaudeMdExpander.Expand(root);

        result.ImportCount.Should().Be(0);
        result.Text.Should().NotContain("Коммиты по-русски");
    }

    // Бэктик в записи импорта — примета примера, а не директивы: «@rules/git.md`»
    // (code span, открытый строкой выше) давал ложную находку «импорт не найден»
    // с целью «rules/git.md`», которой ни в одной карте нет
    [Fact]
    public void ЗаписьИмпортаСБэктиком_ИмпортомНеСчитается()
    {
        var root = Write("CLAUDE.md", "Правило подключается строкой `\n@rules/git.md`\n");

        var result = ClaudeMdExpander.Expand(root);

        result.ImportCount.Should().Be(0);
        result.MissingImports.Should().BeEmpty();
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

    // Импорта нет на диске — человек уверен, что правило уехало в контекст, а его там нет.
    // Молчание тут дороже всего: раскрытие обязано доложить о потере наружу
    [Fact]
    public void ОтсутствующийИмпорт_ПопадаетВMissingImports()
    {
        Write(Path.Combine("rules", "git.md"), "Коммиты по-русски");
        var root = Write("CLAUDE.md", "@rules/git.md\n@rules/typo.md\n");

        var result = ClaudeMdExpander.Expand(root);

        result.ImportCount.Should().Be(1);           // раскрылся только живой
        result.MissingImports.Should().ContainSingle()
            .Which.Target.Should().Be("rules/typo.md");
        result.Text.Should().Contain("Коммиты по-русски");
        result.Truncated.Should().BeFalse();
        result.DepthExceeded.Should().BeFalse();
    }

    [Fact]
    public void ПределВложенности_ПомечаетсяПризнаком()
    {
        // Цепочка глубже MaxDepth: последний импорт останется нераскрытым
        for (var i = 0; i <= ClaudeMdExpander.MaxDepth + 1; i++)
            Write(Path.Combine("rules", $"r{i}.md"), $"уровень {i}\n@r{i + 1}.md");
        Write(Path.Combine("rules", $"r{ClaudeMdExpander.MaxDepth + 2}.md"), "дно");
        var root = Write("CLAUDE.md", "@rules/r0.md");

        var result = ClaudeMdExpander.Expand(root);

        result.DepthExceeded.Should().BeTrue();
        result.MissingImports.Should().BeEmpty();    // файлы на месте, дело в глубине
    }

    [Fact]
    public void ПотолокРазмера_ПомечаетсяПризнаком()
    {
        Write(Path.Combine("rules", "big.md"), new string('я', ClaudeMdExpander.MaxTotalChars + 10));
        var root = Write("CLAUDE.md", "@rules/big.md\nхвост");

        var result = ClaudeMdExpander.Expand(root);

        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public void ЗдороваяКарта_БезПризнаковПотерь()
    {
        Write(Path.Combine("rules", "git.md"), "Коммиты по-русски");
        var root = Write("CLAUDE.md", "Заголовок\n@rules/git.md\n");

        var result = ClaudeMdExpander.Expand(root);

        result.MissingImports.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
        result.DepthExceeded.Should().BeFalse();
        // Read остаётся тонкой обёрткой над Expand — снимку промпта нужен только текст
        ClaudeMdExpander.Read(root).Should().Be(result.Text);
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

    // Применение правок к карте читает файл ровно один раз и раскрывает состав от уже
    // прочитанного текста, а скан — от пути. Две реализации обязаны совпадать ПОБАЙТОВО:
    // малейшее расхождение дало бы 409 на КАЖДОМ применении, а человек видел бы штатную
    // плашку «файл изменился после проверки», неотличимую от настоящей гонки — фича
    // выглядела бы рабочей и не применяла бы ничего никогда.
    //
    // Случаи не случайные: раскрыватель разбирает текст на строки и склеивает через '\n',
    // то есть файл без финального перевода строки его приобретает, а CRLF переживает
    // разбор по-своему
    [Theory]
    [InlineData("Заголовок\n@rules/git.md\nХвост\n")]
    [InlineData("Заголовок\n@rules/git.md\nХвост без перевода строки в конце")]
    [InlineData("Заголовок\r\n@rules/git.md\r\nХвост\r\n")]
    [InlineData("Карта без импортов вовсе\n")]
    [InlineData("")]
    public void РаскрытиеОтТекстаИОтПути_СовпадаютПобайтово(string map)
    {
        Write(Path.Combine("rules", "git.md"), "Коммиты по-русски\n");
        var root = Write("CLAUDE.md", map);

        var fromPath = ClaudeMdExpander.Expand(root);
        var fromText = ClaudeMdExpander.Expand(File.ReadAllText(root), root);

        fromText.Text.Should().Be(fromPath.Text);
        fromText.ImportCount.Should().Be(fromPath.ImportCount);
        fromText.MissingImports.Should().HaveCount(fromPath.MissingImports.Count);
        fromText.Truncated.Should().Be(fromPath.Truncated);
        fromText.DepthExceeded.Should().Be(fromPath.DepthExceeded);
    }

    // Цикл ловится и на этом пути: карта импортирует сосед, сосед импортирует её саму.
    // Оба файла в одной папке намеренно — выход выше папки не раскрывается вовсе, и на
    // «@../CLAUDE.md» цикла не случилось бы, а тест оказался бы вырожденным.
    // Путь от текста обязан стартовать с УЖЕ занятым корнем: иначе карта прочитается с
    // диска вторым заходом, текст разойдётся с первым путём и apply будет отвечать 409
    [Fact]
    public void РаскрытиеОтТекста_ЦиклНеЗацикливается()
    {
        Write("sosed.md", "Сосед\n@CLAUDE.md\n");
        var root = Write("CLAUDE.md", "Карта\n@sosed.md\n");

        var fromText = ClaudeMdExpander.Expand(File.ReadAllText(root), root);

        fromText.Text.Should().Contain("циклическая ссылка");
        fromText.Text.Should().Be(ClaudeMdExpander.Expand(root).Text);
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
