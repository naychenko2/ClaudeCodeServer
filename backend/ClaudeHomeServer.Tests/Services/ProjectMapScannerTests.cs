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

    // Потолки списков в отчёте — тоже настройка: у карты с сотней дефектов отчёт обязан
    // остаться того же размера
    private static ProjectMapScanner ScannerWithCaps(int? maxLinkFindings = null, int? maxNestedMaps = null)
    {
        var values = new Dictionary<string, string?>();
        if (maxLinkFindings is { } l) values["ProjectMap:MaxDeadLinksInReport"] = l.ToString();
        if (maxNestedMaps is { } n) values["ProjectMap:MaxNestedMaps"] = n.ToString();
        return new ProjectMapScanner(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

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

    // Карта, документирующая формат карт, — целевой класс документов этой фичи: вложенный
    // пример забора там норма. Наивное «переключить флаг на любом маркере» закрывало
    // внешний забор внутренним, и остаток файла разбирался как живой текст
    [Fact]
    public void ВложенныйЗабор_ВнутреннийНеЗакрываетВнешний()
    {
        Write("""
            # Карта

            ````md
            ```
            ## Заголовок из примера
            [ссылка из примера](совсем/нет.md)
            ```
            ````

            ## Настоящая секция
            """, "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.SectionCount.Should().Be(1);
        report.Sections.Should().BeEmpty();      // короткая
        report.DeadLinks.Should().BeEmpty();
        report.UnclosedFence.Should().BeFalse();
    }

    // Незакрытый забор молча выкидывал из разбора весь остаток файла, и отчёт рапортовал
    // «карта здорова». Теперь это отдельный признак, а не тишина
    [Fact]
    public void НезакрытыйЗабор_ОтдельнымПризнакомВОтчёте()
    {
        Write("# Карта\n\n```md\nпример\n\n## Секция ниже разобрана не будет\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.UnclosedFence.Should().BeTrue();
        report.SectionCount.Should().Be(0);
    }

    // @-импорт внутри забора CLI не раскрывает: пример в блоке кода иначе дал бы ложный
    // «мёртвый импорт» (правило якобы не едет в контекст), а пример на живой файл — завысил
    // бы ImportCount и вклеил бы в снимок промпта то, чего там нет
    [Fact]
    public void ИмпортВнутриЗабора_НеРаскрываетсяИНеСчитаетсяМёртвым()
    {
        Write("""
            # Карта

            ```md
            @rules/пример.md
            ```

            @rules/живое.md
            """, "CLAUDE.md");
        Write("правило", "rules", "живое.md");

        var report = _scanner.Scan(_root);

        report.ImportCount.Should().Be(1);
        report.DeadImports.Should().BeEmpty();
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

    // Мёртвый импорт — худший класс дефекта карты: правило в контекст не едет вовсе,
    // а раньше оно молча оставалось строкой текста. Отдельная причина, не «пропущено»
    [Fact]
    public void МёртвыйИмпорт_ОтдельнаяНаходка_СНомеромСтроки()
    {
        Write("# Карта\n\n@rules/git.md\n@rules/typo.md\n", "CLAUDE.md");
        Write("правило", "rules", "git.md");

        var report = _scanner.Scan(_root);

        report.ImportCount.Should().Be(1);
        var finding = report.DeadImports.Should().ContainSingle().Subject;
        finding.Reason.Should().Be("deadImport");
        finding.Target.Should().Be("rules/typo.md");
        finding.Path.Should().Be("CLAUDE.md");
        finding.Line.Should().Be(4);
        // В «пропущенное» такая находка не сваливается: там другие причины и другой смысл
        report.Skipped.Should().BeEmpty();
        report.DeadLinks.Should().BeEmpty();
    }

    [Fact]
    public void УсечениеРаскрытия_ВидноВОтчёте()
    {
        Write("# Карта\n\n@rules/big.md\n", "CLAUDE.md");
        Write(new string('я', 300 * 1024), "rules", "big.md");

        var report = _scanner.Scan(_root);

        report.ExpansionTruncated.Should().BeTrue();
    }

    // ─── Потолки отчёта ─────────────────────────────────────────────────────

    // Карта с сотней дефектов обязана уложиться в то же окно, что и здоровая: на нашем
    // корпусе замер дал 161 мёртвую ссылку, и без потолков отчёт разнесло бы ровно там,
    // где фича нужнее всего
    [Fact]
    public void МножествоМёртвыхСсылок_СписокУрезан_ПолныйСчётчикЧестен()
    {
        var links = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"[нет{i}](docs/нет{i}.md)"));
        Write($"# Карта\n\n{links}\n", "CLAUDE.md");
        var capped = ScannerWithCaps(maxLinkFindings: 5);

        var report = capped.Scan(_root);

        report.DeadLinks.Should().HaveCount(5);
        report.DeadLinkCount.Should().Be(30);       // «и ещё 25» читатель посчитает сам
        report.Truncated.Should().BeTrue();
    }

    [Fact]
    public void МножествоВложенныхКарт_СписокУрезан_ПолныйСчётчикЧестен()
    {
        Write("# Карта\n", "CLAUDE.md");
        foreach (var i in Enumerable.Range(1, 7))
            Write($"# Карта {i}\n", "backend", $"Sub{i}", "CLAUDE.md");
        var capped = ScannerWithCaps(maxNestedMaps: 3);

        var report = capped.Scan(_root);

        report.NestedMaps.Should().HaveCount(3);
        report.NestedMapCount.Should().Be(7);
        report.Truncated.Should().BeTrue();
    }

    [Fact]
    public void ЗдороваяКарта_ПризнакаУсеченияНет()
    {
        Write("# Карта\n\nтекст\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.Truncated.Should().BeFalse();
        report.DeadLinkCount.Should().Be(0);
        report.DeadImportCount.Should().Be(0);
    }

    [Fact]
    public void ОценкаТокенов_ПодписанаКакОценка()
    {
        Write("# Карта\n\nтекст\n", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        // Число зависит от токенизатора модели — отчёт обязан называть его оценкой,
        // иначе первый спор про цифру обесценит весь отчёт
        report.ApproxTokensNote.Should().Contain("оценка");
    }

    [Fact]
    public void ПотолокСпискаНоль_СписокВыключен_СчётчикЧестен()
    {
        Write("# Карта\n\n[нет](docs/нет.md)\n[и ещё](docs/тоже-нет.md)\n", "CLAUDE.md");
        var off = ScannerWithCaps(maxLinkFindings: 0);

        var report = off.Scan(_root);

        // Ноль в настройке — «списка в отчёте не надо», а не приглашение к дефолту
        report.DeadLinks.Should().BeEmpty();
        report.DeadLinkCount.Should().Be(2);
        report.Truncated.Should().BeTrue();
    }

    // ─── Обход дерева ───────────────────────────────────────────────────────

    // Симлинк «self -> .» в репозитории — обычное дело. Обход шёл внутрь (Directory.Exists
    // для ссылки на каталог отдаёт true), предела глубины не было, а потолок считал одни
    // файлы — цикл без файлов не останавливался НИКОГДА и уносил процесс
    // StackOverflowException, который не ловится ни catch, ни middleware
    [Fact]
    public void СимлинкЦикл_СканВозвращается_ПроцессНеПадает()
    {
        Write("# Карта\n", "CLAUDE.md");
        Write("# Вложенная\n", "backend", "Video", "CLAUDE.md");
        var loop = Path.Combine(_root, "петля");
        Directory.CreateDirectory(loop);
        try { Directory.CreateSymbolicLink(Path.Combine(loop, "self"), _root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // Windows без прав на симлинки; на Linux-CI тест настоящий
        }

        var report = _scanner.Scan(_root);

        report.Exists.Should().BeTrue();
        // Внутрь ссылки не пошли — вторых экземпляров карт в отчёте нет
        report.NestedMaps.Select(m => m.Path).Should().BeEquivalentTo(["backend/Video/CLAUDE.md"]);
    }

    // Предел вложенности обхода: дерево глубже предела обрывается, и это видно в отчёте
    [Fact]
    public void ДеревоГлубжеПредела_ОбходУсечён_ВидноВОтчёте()
    {
        Write("# Карта\n", "CLAUDE.md");
        var deep = Enumerable.Repeat("у", 40).ToArray();
        Write("# Глубокая\n", [.. deep, "CLAUDE.md"]);

        var report = _scanner.Scan(_root);

        report.WalkTruncated.Should().BeTrue();
        report.Truncated.Should().BeTrue();
        report.NestedMaps.Should().BeEmpty();   // до дна обход не дошёл
    }

    // Инвариант кандидата — «одноимённый файл в проекте ровно один». На оборванном обходе
    // это уже не факт, а совпадение: второй мог просто не попасть в обход, и механическая
    // правка ушла бы на заведомо неверный файл
    [Fact]
    public void ОбходУсечён_КандидатовНеПредлагает()
    {
        Write("# Карта\n\n[флаги](backend/Models/FeatureFlag.cs)\n", "CLAUDE.md");
        Write("// код", "backend", "Core", "Models", "FeatureFlag.cs");
        Write("// глубоко", [.. Enumerable.Repeat("у", 40), "FeatureFlag.cs"]);

        var report = _scanner.Scan(_root);

        report.WalkTruncated.Should().BeTrue();
        report.DeadLinks.Should().ContainSingle().Which.Candidates.Should().BeEmpty();
    }

    // Обход дерева идёт на каждый запрос — ушёл клиент, ушёл и обход
    [Fact]
    public void ОтменённыйТокен_ОбходПрерывается()
    {
        Write("# Карта\n", "CLAUDE.md");
        Write("# Вложенная\n", "backend", "Video", "CLAUDE.md");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var scan = () => _scanner.Scan(_root, cts.Token);

        scan.Should().Throw<OperationCanceledException>();
    }

    // Указатель «имя файла → путь» нужен только кандидатам, а кандидаты бывают лишь у
    // ссылок разобранных карт: без карты он не собирается вовсе (снаружи это не видно,
    // но обход вложенных карт от этого пострадать не имеет права)
    [Fact]
    public void КартыНет_ВложенныеКартыВсёРавноНайдены()
    {
        Write("# Просто проект", "README.md");
        Write("# Видео\n", "backend", "Video", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.Exists.Should().BeFalse();
        report.NestedMaps.Select(m => m.Path).Should().BeEquivalentTo(["backend/Video/CLAUDE.md"]);
        report.WalkTruncated.Should().BeFalse();
    }

    // Имя кандидата берётся из РЕЗОЛВНУТОГО пути: по сырому тексту ссылки процент-энкодинг
    // дал бы имя «моя%20карта.md», которого нет ни у одного файла
    [Fact]
    public void СсылкаСПроцентЭнкодингом_КандидатНаходится()
    {
        Write("# Карта\n\n[карта](docs/моя%20карта.md)\n", "CLAUDE.md");
        Write("# Карта", "notes", "моя карта.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().ContainSingle()
            .Which.Candidates.Should().BeEquivalentTo(["notes/моя карта.md"]);
    }

    // File.Exists на Linux регистрозависим, а указатель имён — нет: ссылка, промахнувшаяся
    // регистром, получает кандидата с верным написанием вместо глухого «файла нет»
    [Fact]
    public void СсылкаПромахнуласьРегистром_КандидатПодсказываетНаписание()
    {
        Write("# Карта\n\n[адр](docs/ADR-005-Reader.md)\n", "CLAUDE.md");
        Write("# АДР", "docs", "adr", "adr-005-reader.md");

        var report = _scanner.Scan(_root);

        report.DeadLinks.Should().ContainSingle()
            .Which.Candidates.Should().BeEquivalentTo(["docs/adr/adr-005-reader.md"]);
    }

    // ─── Приёмка на фикстурном дереве ───────────────────────────────────────

    // Сводная проверка на дереве с ЗАВЕДОМО известным набором дефектов. Живой репозиторий
    // для этого не годится: обе его мёртвые ссылки чинятся этой же фичей в первый день,
    // а число секций меняется каждой правкой карты — тест на нём протух бы сразу
    [Fact]
    public void ФикстурноеДерево_НаходитВесьЗаведомыйНабор_ИНичегоЛишнего()
    {
        var longSection = "## Раздутая\n\nпервая строка\n" + string.Join('\n', Enumerable.Repeat("тело", 50));
        Write($"""
            # Карта проекта

            @rules/живое.md
            @rules/мёртвое.md

            ## Ссылки

            [живая](docs/жив.md), [мёртвая](backend/Нет.cs), [переехавшая](backend/Старое/Файл.cs)
            [внешняя](https://example.com), [схема](javascript:alert(1))
            [абсолютная](/etc/passwd), [наружу](../../чужое.md)

            ```md
            [пример внутри забора](совсем/нет.md)
            ## Заголовок внутри забора
            ```

            {longSection}
            """, "CLAUDE.md");
        Write("правило", "rules", "живое.md");
        Write("# Жив", "docs", "жив.md");
        Write("// код", "backend", "Новое", "Файл.cs");
        Write("# Вложенная\n\n[от корня](docs/жив.md)\n", "backend", "Новое", "CLAUDE.md");

        var report = _scanner.Scan(_root);

        report.Exists.Should().BeTrue();
        // Секции: «Ссылки» и «Раздутая», длинная одна
        report.SectionCount.Should().Be(2);
        report.LongSectionCount.Should().Be(1);
        report.Sections.Should().ContainSingle().Which.Title.Should().Be("Раздутая");

        // Мёртвых ровно две, и кандидат найден только у той, где одноимённый файл один
        report.DeadLinkCount.Should().Be(2);
        report.DeadLinks.Select(d => d.Target).Should()
            .BeEquivalentTo(["backend/Нет.cs", "backend/Старое/Файл.cs"]);
        report.DeadLinks.Single(d => d.Target == "backend/Старое/Файл.cs")
            .Candidates.Should().BeEquivalentTo(["backend/Новое/Файл.cs"]);
        report.DeadLinks.Single(d => d.Target == "backend/Нет.cs").Candidates.Should().BeEmpty();

        // Мёртвый импорт — своей причиной, ссылка из вложенной карты — третьим исходом
        report.DeadImports.Should().ContainSingle().Which.Target.Should().Be("rules/мёртвое.md");
        report.RootRelativeLinks.Should().ContainSingle()
            .Which.Path.Should().Be("backend/Новое/CLAUDE.md");

        // Абсолютная и уводящая за корень — «не проверяли», внешние и примеры в заборе — тишина
        report.Skipped.Select(s => s.Reason).Should().BeEquivalentTo(["absolute", "outsideProject"]);
        report.Truncated.Should().BeFalse();
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
