using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
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
    public void Раздел_ПустаяПапкаСПарнойСтраницей_ЭтоТожеРаздел()
    {
        Write("# Журнал", "docs", "decisions.md");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "decisions"));

        // Только что созданный раздел ещё пуст, и по документам его не найти. Без него
        // войти в раздел, чтобы наполнить, было бы нечем — он выглядел бы строкой
        _svc.GetIndex(_root).Single(d => d.Path == "docs/decisions.md")
            .SectionFolder.Should().Be("docs/decisions");
    }

    [Fact]
    public void Раздел_ПоявлениеПапки_ВидитсяСразу()
    {
        Write("# Журнал", "docs", "decisions.md");
        _svc.GetIndex(_root).Single().SectionFolder.Should().BeNull();

        // Ни один файл не менялся: без папки в отпечатке кеш отдал бы прежний корпус
        Directory.CreateDirectory(Path.Combine(_root, "docs", "decisions"));

        _svc.GetIndex(_root).Single().SectionFolder.Should().Be("docs/decisions");
    }

    [Fact]
    public void Раздел_ВложеннаяПапкаБезПары_ПризнакаНиУКого()
    {
        Write("# Запись", "docs", "adr", "0001.md");

        // Папка без файла-напарника — в wiki это пустая страница; продукт её не выдумывает
        _svc.GetIndex(_root).Should().OnlyContain(d => d.SectionFolder == null);
    }

    // ─── Запись порядка (.order) ────────────────────────────────────────────

    // Файл порядка как он лёг на диск — сырым текстом: тесты про концы строк и BOM
    // смотрят именно байты, а не разобранный список
    private string ReadOrderFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([_root, .. segments, ".order"]));

    [Fact]
    public void ЗаписьПорядка_МеняетФайлИИндекс()
    {
        Write("# Яблоко", "docs", "a.md");
        Write("# Абрикос", "docs", "b.md");
        Write("a\nb\n", "docs", ".order");

        var result = _svc.WriteOrder(_root, "docs", ["b", "a"]);

        result.Status.Should().Be(DocsIndexService.OrderWriteStatus.Ok);
        ReadOrderFile("docs").Should().Be("b\na\n");
        _svc.GetIndex(_root)[0].Path.Should().Be("docs/b.md");
    }

    [Fact]
    public void ЗаписьПорядка_ФайлаНеБыло_СоздаётсяСоВсемСоставомПапки()
    {
        Write("# A", "docs", "a.md");
        Write("# B", "docs", "b.md");
        Write("# C", "docs", "c.md");

        // Переставлены две строки, а записаны три: иначе «c» стал бы неперечисленным и
        // уехал в хвост — жест сломал бы порядок вместо того, чтобы его задать
        _svc.WriteOrder(_root, "docs", ["b", "a"]).Status.Should().Be(DocsIndexService.OrderWriteStatus.Ok);

        ReadOrderFile("docs").Should().Be("b\na\nc\n");
    }

    [Fact]
    public void ЗаписьПорядка_ПоявившийсяМимоПанели_ДописанВХвост()
    {
        Write("# A", "docs", "a.md");
        Write("# B", "docs", "b.md");
        Write("# Новый", "docs", "new.md");      // приехал git pull'ом, панель его не видела
        Write("a\nb\n", "docs", ".order");

        _svc.WriteOrder(_root, "docs", ["b", "a"]);

        ReadOrderFile("docs").Should().Be("b\na\nnew\n");
    }

    [Fact]
    public void ЗаписьПорядка_СтрокаБезФайла_ОстаётсяНаСвоёмМесте()
    {
        Write("# A", "docs", "a.md");
        Write("# B", "docs", "b.md");
        // Документ мог быть удалён в другой ветке — молча выбрасывать чужую строку нельзя
        Write("призрак\na\nb\n", "docs", ".order");

        _svc.WriteOrder(_root, "docs", ["b", "a"]);

        ReadOrderFile("docs").Should().Be("призрак\nb\na\n");
    }

    [Fact]
    public void ЗаписьПорядка_РазделМеждуДокументами_НеСдвигается()
    {
        Write("# Vision", "docs", "vision.md");
        Write("# Расширения", "docs", "extensions.md");
        Write("# Плагин", "docs", "extensions", "plugin.md");
        Write("# Бриф", "docs", "business-brief.md");
        Write("vision\nextensions\nbusiness-brief\n", "docs", ".order");

        // Панель показывает раздел отдельной группой и в items его не присылает: строки
        // переставляются по занятым позициям, и «extensions» остаётся между документами
        _svc.WriteOrder(_root, "docs", ["business-brief", "vision"]);

        ReadOrderFile("docs").Should().Be("business-brief\nextensions\nvision\n");
    }

    [Fact]
    public void ЗаписьПорядка_КорневойУровень_ЭтоТожеПапка()
    {
        Write("# Проект", "README.md");
        Write("# Док", "docs", "a.md");

        _svc.WriteOrder(_root, null, ["docs", "README"]).Status.Should().Be(DocsIndexService.OrderWriteStatus.Ok);

        ReadOrderFile().Should().Be("docs\nREADME\n");
    }

    [Fact]
    public void ЗаписьПорядка_КонцыСтрокСохраняются_BomНеПоявляется()
    {
        Write("# A", "docs", "a.md");
        Write("# B", "docs", "b.md");
        Write("a\r\nb\r\n", "docs", ".order");

        _svc.WriteOrder(_root, "docs", ["b", "a"]);

        // Первым байтом сразу имя, а не BOM: лишние байты дали бы в git шум на весь файл
        var bytes = File.ReadAllBytes(Path.Combine(_root, "docs", ".order"));
        bytes.Take(3).Should().NotEqual([(byte)0xEF, (byte)0xBB, (byte)0xBF]);
        ReadOrderFile("docs").Should().Be("b\r\na\r\n");
    }

    [Fact]
    public void ЗаписьПорядка_ПапкаВнеОбласти_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");
        Write("# Чужой", "backend", "NOTES.md");

        _svc.WriteOrder(_root, "backend", ["NOTES"]).Status.Should()
            .Be(DocsIndexService.OrderWriteStatus.FolderNotInScope);
        File.Exists(Path.Combine(_root, "backend", ".order")).Should().BeFalse();
    }

    [Fact]
    public void ЗаписьПорядка_ВыходЗаКорень_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");

        _svc.WriteOrder(_root, "../secret", ["a"]).Status.Should()
            .Be(DocsIndexService.OrderWriteStatus.FolderNotInScope);
    }

    [Fact]
    public void ЗаписьПорядка_ИмениНетВПапке_ЭтоОтказ()
    {
        Write("# A", "docs", "a.md");
        Write("a\n", "docs", ".order");

        // .order — не место для произвольных строк от клиента: чужую строку туда
        // дописывает только сам пользователь в git
        _svc.WriteOrder(_root, "docs", ["a", "выдумка"]).Status.Should()
            .Be(DocsIndexService.OrderWriteStatus.BadItems);
        ReadOrderFile("docs").Should().Be("a\n");
    }

    [Fact]
    public void ЗаписьПорядка_ПовторИмени_ЭтоОтказ()
    {
        Write("# A", "docs", "a.md");
        Write("# B", "docs", "b.md");

        _svc.WriteOrder(_root, "docs", ["a", "a"]).Status.Should()
            .Be(DocsIndexService.OrderWriteStatus.BadItems);
    }

    [Fact]
    public void ЗаписьПорядка_НеMarkdown_ВСоставУровняНеВходит()
    {
        Write("# A", "docs", "a.md");
        Write("%PDF-1.4", "docs", "cover.pdf");

        // «cover» без «cover.md» в wiki-порядке просто мусор — переставлять его нечем
        _svc.WriteOrder(_root, "docs", ["cover"], new DocsScope(["docs"], [], ["markdown", "pdf"]))
            .Status.Should().Be(DocsIndexService.OrderWriteStatus.BadItems);
    }

    // ─── Создание документов и разделов ─────────────────────────────────────

    // Создание пишет в рабочее дерево, поэтому идёт через FileService (SafeJoin + OnMutated).
    // Отдельный экземпляр сервиса: у остальных тестов файлового сервиса нет, он им не нужен
    private DocsIndexService Creating() => new(new FileService());

    [Fact]
    public void Создание_Документа_ФайлСЗаголовкомИПутьВОтвете()
    {
        Write("# Док", "docs", "a.md");

        var result = Creating().CreateDoc(_root, "docs", "Бизнес описание", section: false);

        result.Status.Should().Be(DocsIndexService.DocCreateStatus.Ok);
        // Пробелы в имени файла становятся дефисами, а само название — заголовком страницы
        result.Path.Should().Be("docs/Бизнес-описание.md");
        File.ReadAllText(Path.Combine(_root, "docs", "Бизнес-описание.md")).Should().Be("# Бизнес описание\n");
    }

    [Fact]
    public void Создание_Раздела_ЭтоПараСтраницаИПапка()
    {
        Write("# Док", "docs", "a.md");

        var result = Creating().CreateDoc(_root, "docs", "Журнал решений", section: true);

        result.Path.Should().Be("docs/Журнал-решений.md");
        Directory.Exists(Path.Combine(_root, "docs", "Журнал-решений")).Should().BeTrue();

        // Разделом пара становится с первым документом внутри: индекс строится по документам,
        // и пустая папка ему не видна. Без парного ФАЙЛА раздел открывался бы пустой
        // страницей — ровно тот дефект, ради которого пары и поддерживаются
        Write("# Запись", "docs", "Журнал-решений", "0001.md");
        _svc.GetIndex(_root).Single(d => d.Path == "docs/Журнал-решений.md")
            .SectionFolder.Should().Be("docs/Журнал-решений");
    }

    [Fact]
    public void Создание_ПапкаБезСтраницы_ДостраиваетсяДоПары()
    {
        Write("# Запись", "docs", "adr", "0001.md");

        // Половина пары уже на диске — создаём недостающую, а не отказываем
        Creating().CreateDoc(_root, "docs", "adr", section: true).Status.Should()
            .Be(DocsIndexService.DocCreateStatus.Ok);

        File.Exists(Path.Combine(_root, "docs", "adr.md")).Should().BeTrue();
    }

    [Fact]
    public void Создание_ИмяВписываетсяВСуществующийOrder()
    {
        Write("# A", "docs", "a.md");
        Write("a\n", "docs", ".order");

        Creating().CreateDoc(_root, "docs", "Новый", section: false);

        ReadOrderFile("docs").Should().Be("a\nНовый\n");
    }

    [Fact]
    public void Создание_БезOrder_ФайлНеПоявляется()
    {
        Write("# A", "docs", "a.md");

        Creating().CreateDoc(_root, "docs", "Новый", section: false);

        // Порядок в такой папке задан правилом индекса; рождать файл на весь её состав
        // из-за одного нажатия «Создать» продукт не должен
        File.Exists(Path.Combine(_root, "docs", ".order")).Should().BeFalse();
    }

    [Fact]
    public void Создание_ЗанятоеИмя_ЭтоКонфликт()
    {
        Write("# Док", "docs", "api.md");

        // Регистр не спасает: на Windows «API.md» и «api.md» — один файл
        var result = Creating().CreateDoc(_root, "docs", "API", section: false);

        result.Status.Should().Be(DocsIndexService.DocCreateStatus.Conflict);
        File.ReadAllText(Path.Combine(_root, "docs", "api.md")).Should().Be("# Док");
    }

    [Fact]
    public void Создание_ПапкаВнеОбласти_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");

        Creating().CreateDoc(_root, "backend", "Заметка", section: false).Status.Should()
            .Be(DocsIndexService.DocCreateStatus.FolderNotInScope);
        File.Exists(Path.Combine(_root, "backend", "Заметка.md")).Should().BeFalse();
    }

    [Fact]
    public void Создание_ДокументаВКорне_РазрешеноСоседямФайловКорня()
    {
        Write("# Проект", "README.md");

        // Рядом с README живут и другие файлы корня области (docs.md и соседи). В область
        // такой документ попадёт поимённо — имя в «файлы корня» дописывает контроллер
        var result = Creating().CreateDoc(_root, "", "Карта документации", section: false);

        result.Status.Should().Be(DocsIndexService.DocCreateStatus.Ok);
        result.Path.Should().Be("Карта-документации.md");
        File.Exists(Path.Combine(_root, "Карта-документации.md")).Should().BeTrue();
    }

    [Fact]
    public void Создание_РазделаВКорне_ЭтоОтказ()
    {
        Write("# Проект", "README.md");

        // Раздел в корне — это новая папка документации, то есть правка области: молча
        // расширять её за спиной у остальных владельцев репозитория продукт не должен
        Creating().CreateDoc(_root, "", "Спайки", section: true).Status.Should()
            .Be(DocsIndexService.DocCreateStatus.BadName);
        Directory.Exists(Path.Combine(_root, "Спайки")).Should().BeFalse();
    }

    [Fact]
    public void Создание_ПустаяПапкаОбласти_ЭтоНеПрепятствие()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));

        // Гейт создания судит по НАСТРОЙКЕ, а не по индексу: первый документ в пустой
        // папке области — законное действие
        Creating().CreateDoc(_root, "docs", "Первый", section: false).Status.Should()
            .Be(DocsIndexService.DocCreateStatus.Ok);
    }

    [Theory]
    [InlineData("", "пустое")]
    [InlineData("   ", "одни пробелы")]
    [InlineData(".order", "точка в начале")]
    [InlineData("имя.", "точка в конце")]
    [InlineData("a/b", "разделитель пути")]
    [InlineData("a:b", "двоеточие")]
    [InlineData("раздел#якорь", "решётка ломает markdown-ссылку")]
    [InlineData("CON", "зарезервировано в Windows")]
    [InlineData("lpt9", "зарезервировано в Windows")]
    public void Создание_НепригодноеНазвание_ЭтоОтказ(string title, string _)
    {
        Write("# Док", "docs", "a.md");

        Creating().CreateDoc(_root, "docs", title, section: false).Status.Should()
            .Be(DocsIndexService.DocCreateStatus.BadName);
    }

    [Fact]
    public void Создание_СлишкомДлинныйПуть_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");

        // 235 символов — предел Azure DevOps wiki: узнать о нём при публикации, когда
        // документов уже сотня, дороже, чем отказать сейчас
        var result = Creating().CreateDoc(_root, "docs", new string('я', 240), section: false);

        result.Status.Should().Be(DocsIndexService.DocCreateStatus.BadName);
        result.Error.Should().Contain("235");
    }

    [Fact]
    public void ИмяФайла_ПробелыСтановятсяДефисами()
    {
        DocsIndexService.DocFileName(" Журнал решений ", out var error).Should().Be("Журнал-решений");
        error.Should().BeNull();
    }

    // ─── Переименование ─────────────────────────────────────────────────────

    private string Read(params string[] segments) => File.ReadAllText(Path.Combine([_root, .. segments]));

    [Fact]
    public void Переименование_Документа_ФайлИВходящиеСсылки()
    {
        Write("# Vision\n\nсм. [журнал](decisions.md)", "docs", "vision.md");
        Write("# Журнал", "docs", "decisions.md");

        var result = Creating().RenameDoc(_root, "docs/decisions.md", "Журнал решений", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocRenameStatus.Ok);
        result.Path.Should().Be("docs/Журнал-решений.md");
        File.Exists(Path.Combine(_root, "docs", "Журнал-решений.md")).Should().BeTrue();
        // Подпись ссылки — авторский текст, её не трогаем; меняется только цель
        Read("docs", "vision.md").Should().Contain("[журнал](Журнал-решений.md)");
        result.UpdatedDocs.Should().Be(1);
    }

    [Fact]
    public void Переименование_Раздела_ПараИВсёПоддерево()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое", "docs", "decisions", "0001.md");
        Write("# Второе", "docs", "decisions", "0002.md");

        var result = Creating().RenameDoc(_root, "docs/decisions.md", "Журнал решений", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocRenameStatus.Ok);
        Directory.Exists(Path.Combine(_root, "docs", "Журнал-решений")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "docs", "decisions")).Should().BeFalse();
        // Карта переезда покрывает всё поддерево: по ней контроллер чинит привязки заметок
        result.Moved!.Keys.Should().BeEquivalentTo(
            ["docs/decisions.md", "docs/decisions/0001.md", "docs/decisions/0002.md"]);
        result.Moved["docs/decisions/0002.md"].Should().Be("docs/Журнал-решений/0002.md");
    }

    [Fact]
    public void Переименование_Раздела_СсылкаИзнутриНаСтраницу()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое\n\nназад к [журналу](../decisions.md)", "docs", "decisions", "0001.md");

        Creating().RenameDoc(_root, "docs/decisions.md", "Журнал", updateLinks: true);

        // Документ переехал вместе с папкой, но ссылка смотрит на переименованную
        // страницу уровнем выше — её и пересчитываем
        Read("docs", "Журнал", "0001.md").Should().Contain("(../Журнал.md)");
    }

    [Fact]
    public void Переименование_Раздела_СсылкиВнутриПоддереваНеТрогаем()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое\n\nсм. [второе](0002.md)", "docs", "decisions", "0001.md");
        Write("# Второе", "docs", "decisions", "0002.md");

        Creating().RenameDoc(_root, "docs/decisions.md", "Журнал", updateLinks: true);

        // Оба документа переехали одинаково — относительный путь между ними тот же
        Read("docs", "Журнал", "0001.md").Should().Contain("[второе](0002.md)");
    }

    [Fact]
    public void Переименование_СтрокаOrder_НаПрежнейПозиции()
    {
        Write("# Vision", "docs", "vision.md");
        Write("# Журнал", "docs", "decisions.md");
        Write("# Бриф", "docs", "brief.md");
        Write("vision\ndecisions\nbrief\n", "docs", ".order");

        Creating().RenameDoc(_root, "docs/decisions.md", "Журнал", updateLinks: true);

        // Позиция в порядке чтения к имени отношения не имеет
        ReadOrderFile("docs").Should().Be("vision\nЖурнал\nbrief\n");
    }

    [Fact]
    public void Переименование_БезOrder_ФайлНеСоздаётся()
    {
        Write("# Журнал", "docs", "decisions.md");

        Creating().RenameDoc(_root, "docs/decisions.md", "Журнал", updateLinks: true);

        File.Exists(Path.Combine(_root, "docs", ".order")).Should().BeFalse();
    }

    [Fact]
    public void Переименование_БезПочинкиСсылок_СообщаетЧислоБитых()
    {
        Write("# Vision\n\nсм. [журнал](decisions.md)", "docs", "vision.md");
        Write("# Бриф\n\nтоже [журнал](decisions.md)", "docs", "brief.md");
        Write("# Журнал", "docs", "decisions.md");

        var result = Creating().RenameDoc(_root, "docs/decisions.md", "Журнал", updateLinks: false);

        result.BrokenLinks.Should().Be(2);
        result.UpdatedDocs.Should().Be(0);
        // Чужие файлы не тронуты — молчаливая правка чужого текста хуже битой ссылки
        Read("docs", "vision.md").Should().Contain("(decisions.md)");
    }

    [Fact]
    public void Переименование_ТолькоРегистр_ЭтоНеКоллизия()
    {
        Write("# Api", "docs", "api.md");

        var result = Creating().RenameDoc(_root, "docs/api.md", "API", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocRenameStatus.Ok);
        result.Path.Should().Be("docs/API.md");
        // На регистрозависимой ФС (Linux в CI) файл обязан называться точно так
        Directory.GetFiles(Path.Combine(_root, "docs"), "*.md")
            .Select(Path.GetFileName).Should().BeEquivalentTo(["API.md"]);
    }

    [Fact]
    public void Переименование_ЗанятоеИмя_ЭтоКонфликт()
    {
        Write("# Первый", "docs", "a.md");
        Write("# Второй", "docs", "b.md");

        var result = Creating().RenameDoc(_root, "docs/a.md", "b", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocRenameStatus.Conflict);
        Read("docs", "b.md").Should().Be("# Второй");
    }

    [Fact]
    public void Переименование_ДокументаВнеОбласти_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");
        Write("# Чужой", "backend", "NOTES.md");

        Creating().RenameDoc(_root, "backend/NOTES.md", "Заметки", updateLinks: true)
            .Status.Should().Be(DocsIndexService.DocRenameStatus.NotFound);
    }

    [Fact]
    public void Переименование_НепригодноеИмя_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");

        Creating().RenameDoc(_root, "docs/a.md", "CON", updateLinks: true)
            .Status.Should().Be(DocsIndexService.DocRenameStatus.BadName);
        File.Exists(Path.Combine(_root, "docs", "a.md")).Should().BeTrue();
    }

    [Theory]
    [InlineData("docs/a.md", "docs/b.md", "b.md")]
    [InlineData("docs/a.md", "docs/sub/b.md", "sub/b.md")]
    [InlineData("docs/sub/a.md", "docs/b.md", "../b.md")]
    [InlineData("docs/sub/a.md", "docs/other/b.md", "../other/b.md")]
    [InlineData("README.md", "docs/b.md", "docs/b.md")]
    public void Ссылка_ОтносительныйПуть_КакПишутРуками(string from, string to, string expected)
    {
        DocsIndexService.RelativeLink(from, to).Should().Be(expected);
    }

    // ─── Перенос между папками ──────────────────────────────────────────────

    [Fact]
    public void Перенос_Документа_ФайлИВходящиеСсылки()
    {
        Write("# Vision\n\nсм. [бриф](brief.md)", "docs", "vision.md");
        Write("# Бриф", "docs", "brief.md");
        Write("# Запись", "docs", "adr", "0001.md");

        var result = Creating().MoveDoc(_root, "docs/brief.md", "docs/adr", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocMoveStatus.Ok);
        result.Path.Should().Be("docs/adr/brief.md");
        File.Exists(Path.Combine(_root, "docs", "adr", "brief.md")).Should().BeTrue();
        Read("docs", "vision.md").Should().Contain("[бриф](adr/brief.md)");
    }

    [Fact]
    public void Перенос_ПересчитываетСобственныеСсылкиПереехавшего()
    {
        Write("# Vision", "docs", "vision.md");
        Write("# Бриф\n\nсм. [vision](vision.md)", "docs", "brief.md");
        Write("# Запись", "docs", "adr", "0001.md");

        Creating().MoveDoc(_root, "docs/brief.md", "docs/adr", updateLinks: true);

        // Глубина изменилась: ссылка на неподвижную цель тоже поехала. Именно этим
        // перенос отличается от переименования, где глубина сохраняется
        Read("docs", "adr", "brief.md").Should().Contain("[vision](../vision.md)");
    }

    [Fact]
    public void Перенос_Раздела_ПараИПоддерево()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое", "docs", "decisions", "0001.md");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "архив"));
        Write("# Старое", "docs", "архив", "old.md");

        var result = Creating().MoveDoc(_root, "docs/decisions.md", "docs/архив", updateLinks: true);

        result.Path.Should().Be("docs/архив/decisions.md");
        Directory.Exists(Path.Combine(_root, "docs", "архив", "decisions")).Should().BeTrue();
        Directory.Exists(Path.Combine(_root, "docs", "decisions")).Should().BeFalse();
        result.Moved!.Keys.Should().BeEquivalentTo(["docs/decisions.md", "docs/decisions/0001.md"]);
    }

    [Fact]
    public void Перенос_МеняетOrderОбеихПапок()
    {
        Write("# Vision", "docs", "vision.md");
        Write("# Бриф", "docs", "brief.md");
        Write("# Запись", "docs", "adr", "0001.md");
        Write("vision\nbrief\n", "docs", ".order");
        Write("0001\n", "docs", "adr", ".order");

        Creating().MoveDoc(_root, "docs/brief.md", "docs/adr", updateLinks: true);

        // Из старой папки имя уходит, в новую дописывается в хвост
        ReadOrderFile("docs").Should().Be("vision\n");
        ReadOrderFile("docs", "adr").Should().Be("0001\nbrief\n");
    }

    [Fact]
    public void Перенос_РазделаВСамогоСебя_ЭтоОтказ()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое", "docs", "decisions", "0001.md");

        // Папка не может стать собственным потомком, а ФС отвечает на это невнятной
        // ошибкой уже после того, как страница переименована
        Creating().MoveDoc(_root, "docs/decisions.md", "docs/decisions", updateLinks: true)
            .Status.Should().Be(DocsIndexService.DocMoveStatus.BadTarget);
        File.Exists(Path.Combine(_root, "docs", "decisions.md")).Should().BeTrue();
    }

    [Fact]
    public void Перенос_ЗанятоеИмяВЦелевойПапке_ЭтоКонфликт()
    {
        Write("# Бриф", "docs", "brief.md");
        Write("# Другой бриф", "docs", "adr", "brief.md");

        var result = Creating().MoveDoc(_root, "docs/brief.md", "docs/adr", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocMoveStatus.Conflict);
        Read("docs", "adr", "brief.md").Should().Be("# Другой бриф");
    }

    [Fact]
    public void Перенос_ВПапкуВнеОбласти_ЭтоОтказ()
    {
        Write("# Бриф", "docs", "brief.md");
        Directory.CreateDirectory(Path.Combine(_root, "backend"));

        Creating().MoveDoc(_root, "docs/brief.md", "backend", updateLinks: true)
            .Status.Should().Be(DocsIndexService.DocMoveStatus.BadTarget);
        File.Exists(Path.Combine(_root, "docs", "brief.md")).Should().BeTrue();
    }

    [Fact]
    public void Перенос_ВТуЖеПапку_НичегоНеДелает()
    {
        Write("# Бриф", "docs", "brief.md");

        var result = Creating().MoveDoc(_root, "docs/brief.md", "docs", updateLinks: true);

        result.Status.Should().Be(DocsIndexService.DocMoveStatus.Ok);
        result.Moved.Should().BeEmpty();
    }

    [Fact]
    public void Перенос_БезПочинкиСсылок_СообщаетЧислоБитых()
    {
        Write("# Vision\n\nсм. [бриф](brief.md)", "docs", "vision.md");
        Write("# Бриф", "docs", "brief.md");
        Write("# Запись", "docs", "adr", "0001.md");

        var result = Creating().MoveDoc(_root, "docs/brief.md", "docs/adr", updateLinks: false);

        result.BrokenLinks.Should().Be(1);
        Read("docs", "vision.md").Should().Contain("(brief.md)");
    }

    // ─── Удаление ───────────────────────────────────────────────────────────

    [Fact]
    public void Удаление_Документа_ФайлИСтрокаOrder()
    {
        Write("# A", "docs", "a.md");
        Write("# B", "docs", "b.md");
        Write("a\nb\n", "docs", ".order");

        var result = Creating().DeleteDoc(_root, "docs/a.md");

        result.Status.Should().Be(DocsIndexService.DocDeleteStatus.Ok);
        result.Removed.Should().BeEquivalentTo(["docs/a.md"]);
        File.Exists(Path.Combine(_root, "docs", "a.md")).Should().BeFalse();
        // Имя, которому больше нечего соответствовать, — мусор в версионируемом файле
        ReadOrderFile("docs").Should().Be("b\n");
    }

    [Fact]
    public void Удаление_Раздела_ПараЦеликомСПоддеревом()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое", "docs", "decisions", "0001.md");
        Write("# Второе", "docs", "decisions", "0002.md");

        var result = Creating().DeleteDoc(_root, "docs/decisions.md");

        // Половина пары в wiki — либо пустой узел, либо осиротевшая страница
        Directory.Exists(Path.Combine(_root, "docs", "decisions")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "docs", "decisions.md")).Should().BeFalse();
        result.Removed.Should().BeEquivalentTo(
            ["docs/decisions.md", "docs/decisions/0001.md", "docs/decisions/0002.md"]);
    }

    [Fact]
    public void Удаление_Раздела_СчитаетНевидимыеПанелиФайлы()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое", "docs", "decisions", "0001.md");
        Write("PNG", "docs", "decisions", "схема.png");     // не документ выбранного типа

        // Вместе с папкой уходит и то, чего панель не показывала: об этом диалог
        // предупреждает ДО удаления, а не сообщает после
        Creating().DeleteDoc(_root, "docs/decisions.md").RemovedFiles.Should().Be(1);
    }

    [Fact]
    public void Удаление_СообщаетЧислоБитыхСсылок()
    {
        Write("# Vision\n\nсм. [журнал](decisions.md)", "docs", "vision.md");
        Write("# Бриф\n\nтоже [журнал](decisions.md) и [первое](decisions/0001.md)", "docs", "brief.md");
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое", "docs", "decisions", "0001.md");

        var result = Creating().DeleteDoc(_root, "docs/decisions.md");

        // Чинить их нечем — цели больше нет; знать о них пользователь обязан
        result.BrokenLinks.Should().Be(3);
    }

    [Fact]
    public void Удаление_СсылкиИзУдаляемогоПоддерева_НеСчитаютсяБитыми()
    {
        Write("# Журнал", "docs", "decisions.md");
        Write("# Первое\n\nназад к [журналу](../decisions.md)", "docs", "decisions", "0001.md");

        // Документ-источник исчезает вместе с целью — считать эту ссылку битой не за что
        Creating().DeleteDoc(_root, "docs/decisions.md").BrokenLinks.Should().Be(0);
    }

    [Fact]
    public void Удаление_ДокументаВнеОбласти_ЭтоОтказ()
    {
        Write("# Док", "docs", "a.md");
        Write("# Чужой", "backend", "NOTES.md");

        Creating().DeleteDoc(_root, "backend/NOTES.md").Status.Should()
            .Be(DocsIndexService.DocDeleteStatus.NotFound);
        File.Exists(Path.Combine(_root, "backend", "NOTES.md")).Should().BeTrue();
    }

    [Fact]
    public void Удаление_ПоследнейСтрокиOrder_ОставляетПустойФайл()
    {
        Write("# A", "docs", "a.md");
        Write("a\n", "docs", ".order");

        Creating().DeleteDoc(_root, "docs/a.md");

        // Сам файл не сносим: его завёл автор, и пустой .order — это его решение,
        // а не наша уборка
        ReadOrderFile("docs").Should().BeEmpty();
    }

    // ─── Область из файла .docs ─────────────────────────────────────────────

    // Проект с настройкой области в хранилище продукта — она должна уступать файлу
    private Project ProjectWith(IReadOnlyList<string>? folders = null, string? home = null) => new()
    {
        Id = "p1", Name = "test", RootPath = _root, OwnerId = "u1",
        DocsFolders = folders is null ? null : [.. folders],
        DocsHome = home,
    };

    [Fact]
    public void ФайлОбласти_СильнееНастройкиПроекта()
    {
        Write("# Из файла", "wiki", "a.md");
        Write("# Из проекта", "manual", "b.md");
        Write("""{ "folders": ["wiki"] }""", ".docs");

        var scope = _svc.ResolveScope(ProjectWith(["manual"]));

        scope.Folders.Should().BeEquivalentTo(["wiki"]);
        _svc.GetIndex(_root, scope).Select(d => d.Path).Should().Contain("wiki/a.md");
    }

    [Fact]
    public void ФайлОбласти_ОтсутствующаяОсь_ЭтоДефолт()
    {
        // В файле только папки — остальные оси берут умолчание, а не пустоту
        Write("""{ "folders": ["wiki"] }""", ".docs");

        var scope = _svc.ResolveScope(ProjectWith());

        scope.RootFiles.Should().BeEquivalentTo(DocsIndexService.DefaultScope.RootFiles);
        scope.Types.Should().BeEquivalentTo(DocsIndexService.DefaultScope.Types);
    }

    [Fact]
    public void ФайлОбласти_ПустойМассив_ЭтоНеДефолт()
    {
        // «Снял все галки» — осознанный выбор, и подменять его умолчанием нельзя
        Write("""{ "folders": [], "rootFiles": [] }""", ".docs");

        var scope = _svc.ResolveScope(ProjectWith());

        scope.Folders.Should().BeEmpty();
        scope.RootFiles.Should().BeEmpty();
    }

    [Fact]
    public void ФайлОбласти_РегистрПолейИЛишниеПоля_НеМешают()
    {
        // Файл правят руками и читают разные версии продукта: незнакомое поле не повод падать
        Write("""{ "Folders": ["wiki"], "somethingNew": 42 }""", ".docs");

        _svc.ResolveScope(ProjectWith()).Folders.Should().BeEquivalentTo(["wiki"]);
    }

    [Fact]
    public void ФайлОбласти_БитыйJson_ОткатываетсяКНастройкеПроекта()
    {
        Write("# Из проекта", "manual", "b.md");
        Write("{ это не json", ".docs");

        var project = ProjectWith(["manual"]);
        _svc.ResolveScope(project).Folders.Should().BeEquivalentTo(["manual"]);

        var info = _svc.Describe(project);
        info.ScopeSource.Should().Be("project");
        info.ScopeFileError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ФайлОбласти_Описание_СообщаетИсточник()
    {
        Write("# Док", "wiki", "a.md");
        _svc.Describe(ProjectWith()).ScopeSource.Should().Be("project");

        Write("""{ "folders": ["wiki"] }""", ".docs");

        var info = _svc.Describe(ProjectWith());
        info.ScopeSource.Should().Be("file");
        info.ScopeFileError.Should().BeNull();
    }

    [Fact]
    public void ФайлОбласти_Запись_ЧитаетсяОбратно()
    {
        _svc.WriteScopeFile(_root, new DocsScope(["wiki"], ["INDEX.md"], ["markdown"], "wiki/a.md"));

        var written = File.ReadAllText(Path.Combine(_root, ".docs"));
        written.Should().StartWith("{");
        written.Should().Contain("\"folders\"");     // camelCase, как в API
        written.Should().NotContain("﻿");       // без BOM: файл лежит в репозитории

        var scope = _svc.ReadScopeFile(_root).Scope!;
        scope.Folders.Should().BeEquivalentTo(["wiki"]);
        scope.RootFiles.Should().BeEquivalentTo(["INDEX.md"]);
        scope.Home.Should().Be("wiki/a.md");
    }

    [Fact]
    public void ФайлОбласти_НетФайла_ЭтоНеОшибка()
    {
        var result = _svc.ReadScopeFile(_root);

        result.Scope.Should().BeNull();
        result.Error.Should().BeNull();
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
