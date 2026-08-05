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
    public void Порядок_ReadmeПервый_ДальшеПоЗаголовку()
    {
        // Имя файла и заголовок расходятся намеренно: панель подписывает строки заголовками,
        // и сортировка по пути выглядела бы в ней произвольной
        Write("# Яблоко", "docs", "a-first-by-name.md");
        Write("# Абрикос", "docs", "z-last-by-name.md");
        Write("# Проект", "README.md");

        var index = _svc.GetIndex(_root);

        index[0].Path.Should().Be("README.md");
        index.Skip(1).Select(d => d.Title).Should().ContainInOrder("Абрикос", "Яблоко");
    }

    [Fact]
    public void Порядок_ПапкаСтаршеЗаголовка_ГруппыНеПеремешиваются()
    {
        Write("# Яблоко", "docs", "a.md");
        Write("# Абрикос", "docs", "adr", "b.md");

        _svc.GetIndex(_root).Select(d => d.Path).Should().ContainInOrder("docs/a.md", "docs/adr/b.md");
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

    // ─── Порядок из .order и разделы ────────────────────────────────────────

    [Fact]
    public void Order_ЗадаётПорядок_ПеребиваяАлфавит()
    {
        Write("# Яблоко", "docs", "a.md");
        Write("# Абрикос", "docs", "b.md");
        Write("a\nb\n", "docs", ".order");

        // По заголовку было бы «Абрикос, Яблоко» — файл переставляет их местами
        _svc.GetIndex(_root).Select(d => d.Path).Should().ContainInOrder("docs/a.md", "docs/b.md");
    }

    [Fact]
    public void Order_НеперечисленноеВХвост_ПоПрежнемуПравилу()
    {
        Write("# Яблоко", "docs", "a.md");
        Write("# Банан", "docs", "b.md");
        Write("# Абрикос", "docs", "c.md");
        Write("a\n", "docs", ".order");

        // .order сортирует, а не фильтрует: остальные идут следом по заголовку
        _svc.GetIndex(_root).Select(d => d.Path).Should()
            .ContainInOrder("docs/a.md", "docs/c.md", "docs/b.md");
    }

    [Fact]
    public void Order_ПорядокПапок_ТожеИзФайлаРодителя()
    {
        Write("# Zzz", "docs", "z.md");
        Write("# Запись", "docs", "adr", "0001.md");
        Write("adr\nz\n", "docs", ".order");

        // Без файла документ уровня шёл бы перед вложенной папкой — строка это переопределяет
        _svc.GetIndex(_root).Select(d => d.Path).Should()
            .ContainInOrder("docs/adr/0001.md", "docs/z.md");
    }

    [Fact]
    public void Order_СтраницаРаздела_ИдётПередСвоимиДочерними()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Запись", "docs", "decisions", "0001.md");
        Write("# Vision", "docs", "vision.md");
        Write("vision\ndecisions\n", "docs", ".order");

        // Одна строка «decisions» задаёт место и страницы раздела, и его содержимого
        _svc.GetIndex(_root).Select(d => d.Path).Should()
            .ContainInOrder("docs/vision.md", "docs/decisions.md", "docs/decisions/0001.md");
    }

    [Fact]
    public void Order_РегистрСтроки_НеВажен()
    {
        // На Linux регистрозависимая ФС, и строгое сравнение молча роняло бы строку в хвост
        Write("# Zzz", "docs", "Api.md");
        Write("# Aaa", "docs", "b.md");
        Write("api\n", "docs", ".order");

        _svc.GetIndex(_root)[0].Path.Should().Be("docs/Api.md");
    }

    [Fact]
    public void Order_СтрокаБезФайла_НеЛомаетПорядок()
    {
        Write("# Яблоко", "docs", "a.md");
        Write("# Абрикос", "docs", "b.md");
        // Строка может указывать на документ, удалённый в другой ветке
        Write("удалённый-документ\na\nb\n", "docs", ".order");

        _svc.GetIndex(_root).Select(d => d.Path).Should().ContainInOrder("docs/a.md", "docs/b.md");
    }

    [Fact]
    public void Order_ПравкаФайла_ВидитсяСразу()
    {
        Write("# Яблоко", "docs", "a.md");
        Write("# Абрикос", "docs", "b.md");
        Write("a\nb\n", "docs", ".order");
        _svc.GetIndex(_root)[0].Path.Should().Be("docs/a.md");

        // Ни один документ не менялся: без .order в отпечатке кеш отдал бы прежний порядок
        Write("b\na\n", "docs", ".order");

        _svc.GetIndex(_root)[0].Path.Should().Be("docs/b.md");
    }

    [Fact]
    public void Раздел_ДокументРядомСОдноимённойПапкой_ЭтоЕёСтраница()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Запись", "docs", "decisions", "0001.md");

        var entry = _svc.GetIndex(_root).Single(d => d.Path == "docs/decisions.md");

        entry.SectionFolder.Should().Be("docs/decisions");
    }

    [Fact]
    public void Раздел_ДокументБезОдноимённойПапки_БезПризнака()
    {
        Write("# Обычный", "docs", "a.md");

        _svc.GetIndex(_root).Single().SectionFolder.Should().BeNull();
    }

    [Fact]
    public void Раздел_ВложеннаяПапкаБезПары_ПризнакаНиУКого()
    {
        Write("# Запись", "docs", "adr", "0001.md");

        // Папка без файла-напарника — в wiki это пустая страница; продукт её не выдумывает
        _svc.GetIndex(_root).Should().OnlyContain(d => d.SectionFolder == null);
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

    // ─── Настройка области (Project.Docs*) ──────────────────────────────────

    // Область для теста: папки задаются явно, файлы корня и типы — дефолтные
    private static DocsScope Scope(params string[] folders) =>
        new(folders, DocsIndexService.DefaultScope.RootFiles, DocsIndexService.DefaultScope.Types);

    [Fact]
    public void Настройка_КастомнаяПапка_ЗамещаетDocs()
    {
        Write("# Проект", "README.md");
        Write("# Из docs", "docs", "a.md");
        Write("# Из wiki", "wiki", "b.md");

        var index = _svc.GetIndex(_root, Scope("wiki"));

        // README в дефолтной области — он выбран как файл корня, а не как папка
        index.Select(d => d.Path).Should().BeEquivalentTo(["README.md", "wiki/b.md"]);
    }

    [Fact]
    public void Настройка_ПустойСписокПапок_ОставляетТолькоФайлыКорня()
    {
        Write("# Проект", "README.md");
        Write("# Из docs", "docs", "a.md");

        // Пустой список — осознанное «снял все галки», а не «настройки нет»
        _svc.GetIndex(_root, Scope()).Select(d => d.Path).Should().BeEquivalentTo(["README.md"]);
    }

    [Fact]
    public void Настройка_ВложенныеПапки_ДокументыНеДублируются()
    {
        Write("# Вложенный", "docs", "adr", "0001.md");

        var index = _svc.GetIndex(_root, Scope("docs", "docs/adr"));

        index.Should().ContainSingle().Which.Path.Should().Be("docs/adr/0001.md");
    }

    [Fact]
    public void Настройка_Гейт_ДокументВнеВыбранныхПапок_НеОтдаётся()
    {
        Write("# Из docs", "docs", "a.md");
        Write("# Из wiki", "wiki", "b.md");

        _svc.GetDoc(_root, "docs/a.md", Scope("wiki")).Should().BeNull();
        _svc.GetDoc(_root, "wiki/b.md", Scope("wiki")).Should().NotBeNull();
    }

    [Fact]
    public void Настройка_РазныеОбласти_НеВытесняютКешДругДруга()
    {
        Write("# Проект", "README.md");
        Write("# Из docs", "docs", "a.md");

        // Один корень, разные области — у соседей по папке настройки свои
        _svc.GetIndex(_root, Scope("docs")).Should().HaveCount(2);
        _svc.GetIndex(_root, Scope()).Should().HaveCount(1);
        _svc.GetIndex(_root, Scope("docs")).Should().HaveCount(2);
    }

    // ─── Файлы в корне ──────────────────────────────────────────────────────

    [Fact]
    public void ФайлыКорня_ВыбранныеПопадают_ОстальныеНет()
    {
        Write("# Проект", "README.md");
        Write("# История", "CHANGELOG.md");
        Write("# Вклад", "CONTRIBUTING.md");

        var scope = new DocsScope([], ["README.md", "CHANGELOG.md"], ["markdown"]);

        _svc.GetIndex(_root, scope).Select(d => d.Path)
            .Should().BeEquivalentTo(["README.md", "CHANGELOG.md"]);
    }

    [Fact]
    public void ФайлыКорня_ПустойСписок_УбираетДажеReadme()
    {
        Write("# Проект", "README.md");
        Write("# Док", "docs", "a.md");

        // README не привилегирован: он такой же выбранный файл, как остальные
        _svc.GetIndex(_root, new DocsScope(["docs"], [], ["markdown"]))
            .Select(d => d.Path).Should().BeEquivalentTo(["docs/a.md"]);
    }

    [Fact]
    public void ФайлыКорня_ВыбранныйФайл_ОтдаётсяДажеСЧужимРасширением()
    {
        Write("Просто текст", "NOTES.txt");

        // Явный выбор файла сильнее общего фильтра типов: пользователь назвал его поимённо
        _svc.GetIndex(_root, new DocsScope([], ["NOTES.txt"], ["markdown"]))
            .Should().ContainSingle().Which.Path.Should().Be("NOTES.txt");
    }

    [Fact]
    public void ФайлыКорня_ФайлИзПодпапки_НеПринимается()
    {
        Write("# Док", "docs", "a.md");

        // Подпапки задаются папками; путь здесь был бы второй дорогой мимо той настройки
        _svc.GetIndex(_root, new DocsScope([], ["docs/a.md"], ["markdown"])).Should().BeEmpty();
    }

    [Fact]
    public void Порядок_ReadmeПервыйСредиФайлевКорня()
    {
        Write("# Абрикос", "CHANGELOG.md");
        Write("# Яблоко", "README.md");

        var index = _svc.GetIndex(_root, new DocsScope([], ["README.md", "CHANGELOG.md"], ["markdown"]));

        // README — вход в документацию, по алфавиту заголовков он был бы вторым
        index[0].Path.Should().Be("README.md");
    }

    // ─── Типы файлов (группы) ───────────────────────────────────────────────

    [Fact]
    public void Типы_ВОбластьПопадаетТолькоВыбраннаяГруппа()
    {
        Write("# Маркдаун", "docs", "a.md");
        Write("Текст", "docs", "b.txt");

        _svc.GetIndex(_root, new DocsScope(["docs"], [], ["markdown"]))
            .Select(d => d.Path).Should().BeEquivalentTo(["docs/a.md"]);

        _svc.GetIndex(_root, new DocsScope(["docs"], [], ["markdown", "text"]))
            .Select(d => d.Path).Should().BeEquivalentTo(["docs/a.md", "docs/b.txt"]);
    }

    [Fact]
    public void Типы_ЗаголовокТекстовогоФайла_ИзИмени()
    {
        Write("Просто текст без разметки", "docs", "readme-plain.txt");

        _svc.GetIndex(_root, new DocsScope(["docs"], [], ["text"]))
            .Should().ContainSingle().Which.Title.Should().Be("readme-plain");
    }

    [Fact]
    public void Типы_БинарныйФайл_ВСпискеНоБезРазбора()
    {
        Write("%PDF-1.7 не настоящий, но и не текст", "docs", "spec.pdf");

        var entry = _svc.GetIndex(_root, new DocsScope(["docs"], [], ["pdf"])).Should().ContainSingle().Subject;

        entry.Binary.Should().BeTrue();
        entry.Title.Should().Be("spec.pdf");   // имя целиком: расширение здесь несёт смысл
        entry.Headings.Should().BeEmpty();

        // Содержимое не отдаётся — панель предложит открыть его в центре
        var doc = _svc.GetDoc(_root, "docs/spec.pdf", new DocsScope(["docs"], [], ["pdf"]))!;
        doc.Binary.Should().BeTrue();
        doc.Content.Should().BeEmpty();
    }

    [Fact]
    public void Типы_ГруппаРаскрываетсяВоВсеСвоиРасширения()
    {
        Write("картинка", "docs", "schema.png");
        Write("другая", "docs", "photo.jpeg");

        _svc.GetIndex(_root, new DocsScope(["docs"], [], ["image"]))
            .Select(d => d.Path).Should().BeEquivalentTo(["docs/schema.png", "docs/photo.jpeg"]);
    }

    [Theory]
    [InlineData("markdown")]
    [InlineData("MARKDOWN")]
    public void Типы_РегистрНеВажен(string raw)
    {
        DocsIndexService.NormalizeTypes([raw]).Should().Equal("markdown");
    }

    [Theory]
    [InlineData("")]
    [InlineData("выдумка")]
    [InlineData(".md")]
    public void Типы_НеизвестнаяГруппа_Отбрасывается(string raw)
    {
        // Ключи групп, а не расширения: «.md» здесь не значение, а мимо контракта
        DocsIndexService.NormalizeTypes([raw]).Should().BeEmpty();
    }

    [Fact]
    public void Типы_ПорядокКаталога_НеЗависитОтПорядкаВыбора()
    {
        DocsIndexService.NormalizeTypes(["pdf", "markdown"]).Should().Equal("markdown", "pdf");
    }

    [Theory]
    [InlineData("docs/a.md")]
    [InlineData("../secret.md")]
    [InlineData("C:/Windows/win.md")]
    [InlineData("..")]
    [InlineData("")]
    public void ФайлыКорня_НепригодноеЗначение_Отбрасывается(string raw)
    {
        DocsIndexService.NormalizeRootFiles([raw]).Should().BeEmpty();
    }

    [Fact]
    public void ФайлыКорня_Дубли_Схлопываются()
    {
        DocsIndexService.NormalizeRootFiles(["README.md", "readme.md"]).Should().ContainSingle();
    }

    [Fact]
    public void Настройка_СлужебныеПодпапки_ВОбластьНеПопадают()
    {
        Write("# Наш", "docs", "a.md");
        Write("# Чужой", "docs", "node_modules", "pkg", "README.md");

        _svc.GetIndex(_root, Scope("docs")).Should().ContainSingle().Which.Path.Should().Be("docs/a.md");
    }

    [Theory]
    [InlineData(" docs ", "docs")]
    [InlineData("docs/", "docs")]
    [InlineData("/docs", "docs")]
    [InlineData("docs\\adr", "docs/adr")]
    [InlineData("./docs/./adr", "docs/adr")]
    public void Нормализация_ПриводитПутьККаноничномуВиду(string raw, string expected)
    {
        DocsIndexService.NormalizeFolders([raw]).Should().Equal(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("docs/../..")]
    [InlineData(".")]
    [InlineData("/")]
    public void Нормализация_НепригодноеЗначение_Отбрасывается(string raw)
    {
        // Абсолютный путь с диском — тем же правилом, но литерал «C:\…» на Linux безобиден,
        // поэтому проверяется отдельно ниже, а не в этом наборе
        DocsIndexService.NormalizeFolders([raw]).Should().BeEmpty();
    }

    [Fact]
    public void Нормализация_АбсолютныйПуть_Отбрасывается()
    {
        DocsIndexService.NormalizeFolders(["C:/Windows", "//server/share"]).Should().Equal("server/share");
    }

    [Fact]
    public void Нормализация_Дубли_Схлопываются()
    {
        DocsIndexService.NormalizeFolders(["docs", "docs/", "DOCS"]).Should().ContainSingle();
    }

    [Fact]
    public void Нормализация_Null_ЭтоДефолт()
    {
        DocsIndexService.NormalizeFolders(null).Should().Equal(DocsIndexService.DefaultScope.Folders);
    }

    // ─── Кандидаты в папки документации ─────────────────────────────────────

    [Fact]
    public void Кандидаты_ПапкиСMd_СоСчётчикомПоВсемуПоддереву()
    {
        Write("# Проект", "README.md");           // корень кандидатом не бывает
        Write("# А", "docs", "a.md");
        Write("# Б", "docs", "adr", "b.md");
        Write("# В", "backend", "NOTES.md");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));   // без .md — не кандидат

        var candidates = _svc.SuggestFolders(_root);

        candidates.Select(c => c.Path).Should().BeEquivalentTo(["docs", "docs/adr", "backend"]);
        candidates.Single(c => c.Path == "docs").Count.Should().Be(2);   // свой + вложенный
        candidates.Should().OnlyContain(c => c.Exists);
    }

    [Fact]
    public void Кандидаты_СлужебныеПапки_НеПредлагаются()
    {
        Write("# Чужой", "node_modules", "pkg", "README.md");
        Write("# Скрытый", ".github", "CONTRIBUTING.md");

        // Выбранное пусто намеренно: с дефолтом в списке всегда была бы строка «docs»
        // (выбранные показываются даже несуществующими), и проверка кандидатов размылась бы
        _svc.SuggestFolders(_root, Scope()).Should().BeEmpty();
    }

    [Fact]
    public void Кандидаты_ВыбраннаяНесуществующаяПапка_ОстаётсяВСписке()
    {
        Write("# А", "docs", "a.md");

        var candidates = _svc.SuggestFolders(_root, Scope("docs", "wiki"));

        // Иначе галка на удалённой папке молча исчезла бы, и пустой список документов
        // выглядел бы поломкой панели, а не следствием настройки
        var wiki = candidates.Single(c => c.Path == "wiki");
        wiki.Exists.Should().BeFalse();
        wiki.Count.Should().Be(0);
    }

    // ─── Начальный документ ─────────────────────────────────────────────────

    [Fact]
    public void Начало_БезВыбора_ЭтоReadmeКорня()
    {
        Write("# Проект", "README.md");
        Write("# Док", "docs", "a.md");

        _svc.ResolveHome(_root).Should().Be("README.md");
    }

    [Fact]
    public void Начало_ВыбранныйДокумент_ПеребиваетReadme()
    {
        Write("# Проект", "README.md");
        Write("# Обзор", "docs", "overview.md");

        var scope = new DocsScope(["docs"], ["README.md"], ["markdown"], "docs/overview.md");

        _svc.ResolveHome(_root, scope).Should().Be("docs/overview.md");
    }

    [Fact]
    public void Начало_ВыбранныйВнеОбласти_ОткатываетсяКReadme()
    {
        Write("# Проект", "README.md");
        Write("# Чужой", "backend", "NOTES.md");

        // Гейт области всё равно не отдал бы такой документ — «Начало» не должно
        // упираться в пустой экран из-за настройки, сделанной когда-то раньше
        var scope = new DocsScope(["docs"], ["README.md"], ["markdown"], "backend/NOTES.md");

        _svc.ResolveHome(_root, scope).Should().Be("README.md");
    }

    [Fact]
    public void Начало_БезReadme_ЕгоНет()
    {
        Write("# Док", "docs", "a.md");

        _svc.ResolveHome(_root, new DocsScope(["docs"], [], ["markdown"])).Should().BeNull();
    }

    [Theory]
    [InlineData("docs\\a.md", "docs/a.md")]
    [InlineData("/docs/a.md", "docs/a.md")]
    [InlineData(" docs/a.md ", "docs/a.md")]
    public void Начало_ПутьНормализуется(string raw, string expected)
    {
        DocsIndexService.NormalizeHome(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secret.md")]
    [InlineData("C:/Windows/win.md")]
    public void Начало_НепригодноеЗначение_ЭтоАвтовыбор(string raw)
    {
        DocsIndexService.NormalizeHome(raw).Should().BeNull();
    }

    // ─── Кандидаты в файлы корня ────────────────────────────────────────────

    [Fact]
    public void КандидатыКорня_ВсёПодходящееПоРасширению_ИзКорня()
    {
        Write("# Проект", "README.md");
        Write("# История", "CHANGELOG.md");
        Write("Заметки", "NOTES.txt");
        Write("код", "Program.cs");            // не документация
        Write("# Внутри папки", "docs", "a.md");   // не корень

        var names = _svc.SuggestRootFiles(_root).Select(c => c.Name);

        names.Should().BeEquivalentTo(["README.md", "CHANGELOG.md", "NOTES.txt"]);
    }

    [Fact]
    public void КандидатыКорня_СчитаютсяПоВсемПоддерживаемым_НеПоВыбранным()
    {
        Write("# Проект", "README.md");
        Write("Заметки", "NOTES.txt");

        // Сузив типы до .md, пользователь не должен терять из виду свой же NOTES.txt
        var names = _svc.SuggestRootFiles(_root, new DocsScope([], ["README.md"], ["markdown"]))
            .Select(c => c.Name);

        names.Should().Contain("NOTES.txt");
    }

    [Fact]
    public void КандидатыКорня_ВыбранныйНесуществующий_ОстаётсяВСписке()
    {
        Write("# Проект", "README.md");

        var candidates = _svc.SuggestRootFiles(_root, new DocsScope([], ["README.md", "GONE.md"], ["markdown"]));

        candidates.Single(c => c.Name == "GONE.md").Exists.Should().BeFalse();
        candidates.Single(c => c.Name == "README.md").Exists.Should().BeTrue();
    }

    [Fact]
    public void Описание_ОтдаётВыбранноеКандидатовИДефолты()
    {
        Write("# Проект", "README.md");
        Write("# Док", "docs", "a.md");

        var info = _svc.Describe(_root);

        info.Selected.Should().BeEquivalentTo(DocsIndexService.DefaultScope);
        info.FolderCandidates.Select(c => c.Path).Should().Contain("docs");
        info.RootFileCandidates.Select(c => c.Name).Should().Contain("README.md");
        info.TypeGroups.Select(g => g.Key).Should().Contain(["markdown", "pdf", "visio", "audio"]);
        info.Defaults.Should().BeEquivalentTo(DocsIndexService.DefaultScope);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
        GC.SuppressFinalize(this);
    }
}
