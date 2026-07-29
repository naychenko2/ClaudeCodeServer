using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Docs;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Индекс документации проекта (панель «Доки»): область, разбор markdown, связи, кеш, поиск.
// Пути строятся от Path.GetTempPath() — набор гоняется и на Linux в CI.
public class DocsIndexTests : IDisposable
{
    private readonly string _root;
    private readonly DocsIndexService _svc = new();

    public DocsIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "docs_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // Создаёт файл вместе с недостающими папками; путь задаётся сегментами, а не литералом
    // с разделителем — на Linux «docs\api.md» было бы именем файла, а не путём
    private void Write(string content, params string[] segments)
    {
        var full = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ─── Область документации ───────────────────────────────────────────────

    [Fact]
    public void Область_ЭтоReadmeИDocs_ОстальныеMdНеПопадают()
    {
        Write("# Проект", "README.md");
        Write("# Архитектура", "docs", "architecture.md");
        Write("# Вложенный", "docs", "adr", "0001.md");
        Write("# Чужой", "backend", "NOTES.md");     // вне области
        Write("# Не документация", "CHANGELOG.md");  // вне области

        var index = _svc.GetIndex(_root);

        index.Select(d => d.Path).Should().BeEquivalentTo(
            ["README.md", "docs/architecture.md", "docs/adr/0001.md"]);
    }

    [Fact]
    public void Порядок_ReadmeПервый_ДальшеПоПути()
    {
        Write("# Б", "docs", "b.md");
        Write("# А", "docs", "a.md");
        Write("# Проект", "README.md");

        var index = _svc.GetIndex(_root);

        index[0].Path.Should().Be("README.md");
        index.Skip(1).Select(d => d.Path).Should().ContainInOrder("docs/a.md", "docs/b.md");
    }

    [Fact]
    public void ПустойПроект_ИндексПустой_БезОшибки()
    {
        _svc.GetIndex(_root).Should().BeEmpty();
    }

    // ─── Заголовки ──────────────────────────────────────────────────────────

    [Fact]
    public void Заголовок_БерётсяИзH1_БезH1ИзИмениФайла()
    {
        Write("# Настоящий заголовок\n\nтекст", "docs", "with-h1.md");
        Write("просто текст без заголовка", "docs", "no-h1.md");

        var index = _svc.GetIndex(_root);

        index.Single(d => d.Path == "docs/with-h1.md").Title.Should().Be("Настоящий заголовок");
        index.Single(d => d.Path == "docs/no-h1.md").Title.Should().Be("no-h1");
    }

    [Fact]
    public void Заголовки_ВнутриБлокаКода_НеСчитаются()
    {
        // Комментарий «# …» в примере shell-кода — не заголовок документа
        Write("""
              # Документ

              ```bash
              # это комментарий, а не заголовок
              echo привет
              ```

              ## Настоящий раздел
              """, "docs", "code.md");

        var doc = _svc.GetIndex(_root).Single();

        doc.Title.Should().Be("Документ");
        doc.Headings.Should().ContainSingle().Which.Text.Should().Be("Настоящий раздел");
    }

    [Fact]
    public void Слаг_СчитаетсяОтТекстаБезРазметки()
    {
        // Фронт получает textContent DOM-узла, где разметки уже нет: слаг обязан
        // совпасть, иначе переход по якорю не найдёт цель
        DocsIndexService.Slugify("Инварианты `SafeJoin` и **границы**")
            .Should().Be("инварианты-safejoin-и-границы");
    }

    // ─── Ссылки ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ссылки_ДелятсяНаDocRepoExternal()
    {
        Write("""
              # Обзор

              Смотри [архитектуру](./docs/architecture.md), [код](backend/Program.cs)
              и [сайт](https://example.com). Картинка ![схема](img/x.png) ссылкой не считается.
              """, "README.md");
        Write("# Архитектура", "docs", "architecture.md");

        var doc = _svc.GetDoc(_root, "README.md")!;

        doc.Links.Should().SatisfyRespectively(
            l => { l.Kind.Should().Be(DocLinkKind.Doc); l.Target.Should().Be("docs/architecture.md"); },
            l => { l.Kind.Should().Be(DocLinkKind.Repo); l.Target.Should().Be("backend/Program.cs"); },
            l => { l.Kind.Should().Be(DocLinkKind.External); l.Target.Should().Be("https://example.com"); });
    }

    [Fact]
    public void Ссылка_СЯкорем_ЯкорьНормализуетсяВСлаг()
    {
        // Якорь написан «как заголовок» — с заглавными: нормализация обязана привести
        // его к тому же слагу, что лежит в заголовке цели
        Write("[туда](./b.md#Первый-Раздел)", "docs", "a.md");
        Write("# Б\n\n## Первый Раздел", "docs", "b.md");

        var link = _svc.GetDoc(_root, "docs/a.md")!.Links.Single();

        link.Target.Should().Be("docs/b.md");
        link.Anchor.Should().Be("первый-раздел");
        // Тот же слаг лежит в заголовке цели — по нему панель и находит раздел
        _svc.GetIndex(_root).Single(d => d.Path == "docs/b.md")
            .Headings.Single().Slug.Should().Be("первый-раздел");
    }

    [Fact]
    public void Ссылка_СЭнкодленнымЯкорем_ДекодируетсяДоСлага()
    {
        // Кириллический якорь в markdown обычно записан процент-энкодингом; без decode
        // слаг получался мусорным, и переход открывал документ с начала вместо раздела
        Write("[туда](./b.md#%D0%9E%D0%B1%D0%B7%D0%BE%D1%80)", "docs", "a.md");
        Write("# Б\n\n## Обзор", "docs", "b.md");

        var link = _svc.GetDoc(_root, "docs/a.md")!.Links.Single();

        link.Anchor.Should().Be("обзор");
        link.Anchor.Should().Be(_svc.GetIndex(_root).Single(d => d.Path == "docs/b.md").Headings.Single().Slug);
    }

    [Fact]
    public void Ссылка_ВышеКорняПроекта_ВКорпусНеПопадает()
    {
        Write("[наружу](../../secret.md)", "docs", "a.md");

        _svc.GetDoc(_root, "docs/a.md")!.Links.Should().BeEmpty();
    }

    [Fact]
    public void ОбратныеСсылки_ЭтоРазворотИсходящих()
    {
        Write("# А\n\n[к бэ](./b.md#раздел)", "docs", "a.md");
        Write("# Бэ", "docs", "b.md");

        var b = _svc.GetDoc(_root, "docs/b.md")!;

        b.Backlinks.Should().ContainSingle();
        b.Backlinks[0].Path.Should().Be("docs/a.md");
        b.Backlinks[0].Title.Should().Be("А");
        b.Backlinks[0].Anchor.Should().Be("раздел");
        // Обратная ссылка не дублирует сама себя в исходящих цели
        b.Links.Should().BeEmpty();
    }

    // ─── Гейт области ───────────────────────────────────────────────────────

    [Fact]
    public void Гейт_ПутьВнеОбласти_НеОтдаётся()
    {
        Write("# Проект", "README.md");
        Write("# Секрет", "backend", "SECRET.md");

        // Файл существует и лежит ВНУТРИ корня проекта, но вне области документации:
        // без этой проверки эндпоинт стал бы вторым универсальным файл-ридером
        _svc.GetDoc(_root, "backend/SECRET.md").Should().BeNull();
    }

    [Fact]
    public void Гейт_ПутьВыходящийЗаКорень_НеОтдаётся()
    {
        Write("# Проект", "README.md");

        _svc.GetDoc(_root, "../secret.md").Should().BeNull();
        _svc.GetDoc(_root, "docs/../../secret.md").Should().BeNull();
    }

    // ─── Кеш ────────────────────────────────────────────────────────────────

    [Fact]
    public void Кеш_ПравкаДокумента_ВидитсяСразу()
    {
        Write("# Старый заголовок", "docs", "a.md");
        _svc.GetIndex(_root).Single().Title.Should().Be("Старый заголовок");

        Write("# Новый заголовок", "docs", "a.md");

        _svc.GetIndex(_root).Single().Title.Should().Be("Новый заголовок");
    }

    [Fact]
    public void Кеш_ЗаменаФайлаСТемЖеВременем_Видится()
    {
        // Ключ «максимальный mtime + количество файлов» такую замену пропустил бы:
        // счётчик тот же, время то же. Отпечаток области видит смену состава.
        var stamp = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Write("# Первый", "docs", "a.md");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "docs", "a.md"), stamp);
        _svc.GetIndex(_root).Single().Path.Should().Be("docs/a.md");

        File.Delete(Path.Combine(_root, "docs", "a.md"));
        Write("# Второй", "docs", "b.md");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "docs", "b.md"), stamp);

        _svc.GetIndex(_root).Single().Path.Should().Be("docs/b.md");
    }

    // ─── Поиск ──────────────────────────────────────────────────────────────

    [Fact]
    public void Поиск_НаходитПоЗаголовкуДокумента()
    {
        Write("# Песочница\n\nтекст про изоляцию", "docs", "sandbox.md");

        var hits = _svc.Search(_root, "песочница");

        hits.Should().ContainSingle().Which.Path.Should().Be("docs/sandbox.md");
    }

    [Fact]
    public void Поиск_НаходитПоПодзаголовку_ИВедётВРаздел()
    {
        Write("# Обзор\n\n## Монтирования контейнера\n\nтекст", "docs", "sandbox.md");

        var hit = _svc.Search(_root, "монтирования").Single();

        hit.Slug.Should().Be("монтирования-контейнера");
    }

    [Fact]
    public void Поиск_ПоТелу_ВозвращаетФрагмент()
    {
        Write("# Обзор\n\n## Раздел\n\nСлово взаперти внутри длинного абзаца текста.", "docs", "a.md");

        var hit = _svc.Search(_root, "взаперти").Single();

        hit.Snippet.Should().Contain("взаперти");
        hit.Slug.Should().Be("раздел");   // якорь ближайшего заголовка выше совпадения
    }

    [Fact]
    public void Поиск_ПустойЗапрос_НичегоНеВозвращает()
    {
        Write("# Проект", "README.md");

        _svc.Search(_root, "   ").Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
        GC.SuppressFinalize(this);
    }
}
