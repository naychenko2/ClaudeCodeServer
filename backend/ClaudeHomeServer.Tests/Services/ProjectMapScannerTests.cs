using ClaudeHomeServer.Services.Docs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Сканер гигиены карты проекта (CLAUDE.md): секции, ссылки, вложенные карты, @-импорты.
// Каждый кейс закрывает грабли, найденные замером на живом репозитории, а не гипотезу.
// Пути строятся от Path.GetTempPath() + Path.Combine — набор гоняется и на Linux в CI.
public class ProjectMapScannerTests : IDisposable
{
    private readonly string _root;
    private readonly ProjectMapScanner _scanner = new();

    public ProjectMapScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccs-map-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // Порог секции — настройка, а не константа: у разных проектов карта живёт по-разному
    private static ProjectMapScanner ScannerWithThreshold(int lines) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
                { ["ProjectMap:SectionLineThreshold"] = lines.ToString() })
            .Build());

    private void Write(string content, params string[] segments)
    {
        var full = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ─── Ссылки ─────────────────────────────────────────────────────────────

    [Fact]
    public void СсылкаНаПереехавшийФайл_Мёртвая_СЕдинственнымКандидатом()
    {
        Write("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n", "CLAUDE.md");
        Write("// код", "backend", "Core", "Models", "FeatureFlag.cs");

        var report = _scanner.Scan(_root);

        var dead = report.DeadLinks.Should().ContainSingle().Subject;
        dead.Target.Should().Be("backend/Models/FeatureFlag.cs");
        dead.Reason.Should().Be("notFound");
        dead.Line.Should().Be(3);
        // Механическая починка: одноимённый файл в проекте ровно один — правку формирует
        // сканер, модель для неё не нужна вовсе
        dead.Candidates.Should().BeEquivalentTo(["backend/Core/Models/FeatureFlag.cs"]);
    }

    [Fact]
    public void МёртваяСсылка_ДваОдноимённыхФайла_КандидатовНеПредлагает()
    {
        Write("# Карта\n\n[карта](backend/Deploy/CLAUDE.md)\n", "CLAUDE.md");
        Write("# Вложенная", "backend", "Video", "CLAUDE.md");
        Write("# Вложенная", "backend", "Desktop", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        // Кандидатов двое (плюс корневая карта) — выбор за человеком, кнопки быть не должно
        report.DeadLinks.Should().ContainSingle().Which.Candidates.Should().BeEmpty();
    }

    // Кейс 1 плана: ссылка от корня репозитория из вложенной карты — третий исход резолва
    [Fact]
    public void СсылкаОтКорняИзВложеннойКарты_ТретийИсход_НеМёртваяИНеЖивая()
    {
        Write("# Карта\n", "CLAUDE.md");
        Write("# Видео\n\n[фича](docs/features/video.md)\n", "backend", "Video", "CLAUDE.md");
        Write("# Видео-панель", "docs", "features", "video.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().BeEmpty();
        var finding = report.RootRelativeLinks.Should().ContainSingle().Subject;
        finding.Reason.Should().Be("rootRelativeOnly");
        finding.Path.Should().Be("backend/Video/CLAUDE.md");
        finding.Target.Should().Be("docs/features/video.md");
    }

    // Кейс 1а плана: javascript:/data: — внешние схемы, а не отсутствующие файлы
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vscode://file/x")]
    public void СсылкаСоСхемой_ВнешняяИВОтчётНеПопадает(string target)
    {
        Write($"# Карта\n\n[пример]({target})\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().BeEmpty();
        report.Skipped.Should().BeEmpty();
    }

    // Кейс 2 плана: абсолютный путь в SafePath.Join не уходит (на Linux он приклеился бы
    // к корню, и проверка «ссылка наружу» исчезла бы молча)
    [Fact]
    public void АбсолютныйПуть_ПомеченSkipped_ИНеПроверяется()
    {
        Write("# Карта\n\n[локальный](/etc/passwd)\n[диск](C:\\Users\\a\\notes.md)\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().BeEmpty();
        report.Skipped.Should().HaveCount(2);
        report.Skipped.Should().OnlyContain(s => s.Reason == "absolute");
    }

    // Кейс 3 плана: выход за корень проекта — skipped, сканер не падает
    [Fact]
    public void ВыходЗаКореньПроекта_Skipped_СканерНеПадает()
    {
        Write("# Карта\n\n[наружу](../../secrets.txt)\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().BeEmpty();
        report.Skipped.Should().ContainSingle().Which.Reason.Should().Be("outsideProject");
    }

    // ─── Заборы кода ────────────────────────────────────────────────────────

    // Кейс 4 плана: «##» внутри ```-забора — не заголовок секции
    [Fact]
    public void ЗаголовокВнутриЗабора_СекциейНеСчитается()
    {
        Write("# Карта\n\n## Настоящая\n\n```md\n## Пример из документации\n```\n\nхвост\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.SectionCount.Should().Be(1);
    }

    // Кейс 5 плана: ссылка внутри забора не проверяется вовсе. Найдено самопроверкой —
    // прототип отчитался о двух «мёртвых ссылках», и обе были примерами внутри забора
    [Fact]
    public void СсылкиВнутриЗабора_НеРазбираютсяВовсе()
    {
        Write("""
            # Карта

            ```markdown
            [текст](путь/которого/нет.md)
            [ещё пример](backend/Models/FeatureFlag.cs)
            ```

            ~~~jsonc
            { "target": "backend/устарело/File.cs" }
            [и такое](docs/нет.md)
            ~~~
            """, "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().BeEmpty();
        report.RootRelativeLinks.Should().BeEmpty();
        report.Skipped.Should().BeEmpty();
    }

    // ─── Размер и секции ────────────────────────────────────────────────────

    [Fact]
    public void ДлинныеСекции_ПопадаютВОтчёт_КороткиеНет()
    {
        var longSection = "## Длинная\n\nпервая строка секции\n" + string.Join('\n', Enumerable.Repeat("текст", 50));
        Write($"# Карта\n\n## Короткая\n\nмало\n\n{longSection}\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.SectionCount.Should().Be(2);
        report.LongSectionCount.Should().Be(1);
        var section = report.Sections.Should().ContainSingle().Subject;
        section.Title.Should().Be("Длинная");
        section.Lines.Should().BeGreaterThan(40);
        section.FirstLine.Should().Be("первая строка секции");
    }

    [Fact]
    public void ПорогСекции_БерётсяИзКонфигурации()
    {
        Write("# Карта\n\n## Секция\n\nа\nб\nв\nг\n", "CLAUDE.md");
        var strict = ScannerWithThreshold(3);

        _scanner.Scan(_root).Sections.Should().BeEmpty();
        strict.Scan(_root).Sections.Should().ContainSingle().Which.Title.Should().Be("Секция");
    }

    [Fact]
    public void БюджетСтрок_СчитаетсяПоОриентируВКонфигурации()
    {
        Write("# Карта\n" + string.Join('\n', Enumerable.Repeat("строка", 250)), "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.Lines.Should().Be(251);
        report.Budget.RecommendedLines.Should().Be(200);
        report.Budget.OverBudget.Should().BeTrue();
        // Оценка токенов — та же, что у снимка промпта: байты/3
        report.ApproxTokens.Should().Be((int)(report.Bytes / 3));
    }

    // Кейс 6 плана: @-импорт увеличивает раскрытый размер — платится именно он
    [Fact]
    public void ИмпортРаскрывается_ExpandedLinesБольшеИсходных()
    {
        Write("# Карта\n\n@rules/git.md\n", "CLAUDE.md");
        Write(string.Join('\n', Enumerable.Repeat("правило", 20)), "rules", "git.md");

        var report = _scanner.Scan(_root);

        report.ImportCount.Should().Be(1);
        report.Lines.Should().Be(3);
        report.ExpandedLines.Should().BeGreaterThan(20);
    }

    [Fact]
    public void ИмпортовНет_РаскрытыйРазмерРавенИсходному()
    {
        Write("# Карта\n\nтекст\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.ImportCount.Should().Be(0);
        report.ExpandedLines.Should().Be(report.Lines);
    }

    // ─── Прочие карты проекта ───────────────────────────────────────────────

    // Кейс 7 плана: карты нет — штатный ответ, а не исключение
    [Fact]
    public void КартыНет_ШтатныйОтвет_БезИсключения()
    {
        Write("# Просто проект", "README.md");

        var report = _scanner.Scan(_root);

        report.Exists.Should().BeFalse();
        report.Lines.Should().Be(0);
        report.BaseSha.Should().BeNull();
        report.Budget.OverBudget.Should().BeFalse();
        report.DeadLinks.Should().BeEmpty();
    }

    // Кейс 8 плана: рабочие деревья и node_modules — копии репозитория, в отчёте это мусор
    [Fact]
    public void ВложенныеКарты_БезWorktreesИNodeModules()
    {
        Write("# Карта\n", "CLAUDE.md");
        Write("# Видео\n", "backend", "Video", "CLAUDE.md");
        Write("# Копия\n", ".claude", "worktrees", "task-1", "CLAUDE.md");
        Write("# Пакет\n", "frontend", "node_modules", "pkg", "CLAUDE.md");
        Write("# Сборка\n", "backend", "bin", "Debug", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.NestedMaps.Select(m => m.Path).Should().BeEquivalentTo(["backend/Video/CLAUDE.md"]);
    }

    [Fact]
    public void ВтораяКартаПроекта_ИдётОтдельнымБлоком_СПроверкойЕёСсылок()
    {
        Write("# Карта\n", "CLAUDE.md");
        Write("# Вторая\n\n[нет такого](docs/нет.md)\n", ".claude", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.SecondMap.Should().NotBeNull();
        report.SecondMap!.Path.Should().Be(".claude/CLAUDE.md");
        report.NestedMaps.Should().BeEmpty();   // не «вложенная»: это вторая карта проекта
        report.DeadLinks.Should().ContainSingle().Which.Path.Should().Be(".claude/CLAUDE.md");
    }

    [Fact]
    public void КомпактнаяКартаДляBareMode_ИдётСправочнойСтрокой()
    {
        Write("# Карта\n", "CLAUDE.md");
        Write("# Компактная\nвторая строка\n", "docs", "CLAUDE-local.md");

        var report = _scanner.Scan(_root);

        report.LocalMap.Should().NotBeNull();
        report.LocalMap!.Path.Should().Be("docs/CLAUDE-local.md");
        report.LocalMap.Lines.Should().Be(2);
    }

    [Fact]
    public void СсылкиНаDocs_СчитаютсяПоСекциям()
    {
        Write("""
            # Карта

            ## Раздел

            Смотри [архитектуру](docs/a.md) и [ещё](docs/b.md), а также [код](backend/x.cs).
            """, "CLAUDE.md");
        Write("# А", "docs", "a.md");
        Write("# Б", "docs", "b.md");
        Write("// код", "backend", "x.cs");
        var strict = ScannerWithThreshold(1);

        var report = strict.Scan(_root);

        // Живые ссылки на docs/* — признак пересказа готового документа; ссылка на код им не является
        report.Sections.Should().ContainSingle().Which.DocsRefs.Should().Be(2);
    }
}
