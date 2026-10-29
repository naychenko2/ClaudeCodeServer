using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// Захват источника (S6h): дом источника определяет раздел (WikiSourceHome — ADR-035, решение 9;
// ADR-036, решения 1 и 3); карточка type: source лежит в Источники/ своего раздела, текста
// источника не несёт, hash — sha256 байтов файла на диске (не строки); наружу — путь карточки
// (ключ sourceTexts правила дословности WikiGate — контракт S6a) и текст источника для
// QuoteMatcher. Отказы: заметка с expires, чужой владелец/проект, файл за корнем проекта и
// под {data}/wiki, невалидный дом; заголовок с спецсимволами не ломает карточку и не
// затирает карточку другого источника (урок ревью S2b-3).
public class WikiSourceCaptureTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir; // dataDir
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly WikiSourceCapture _sut;

    public WikiSourceCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_source_capture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "notes", User));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(config, users, new AppSettingsService(config));
        _notes = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
        _sut = new WikiSourceCapture(_notes, _projects, config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Project NewProject(string name) =>
        _projects.Create(name, Path.Combine(_dir, "proj_" + name), User, User, createDirectory: true);

    private string ReadCard(WikiSourceCapture.Result r) =>
        File.ReadAllText(SafePath.Join(WikiPaths.Root(_dir, User), "pages/" + r.CardRelPath!));

    private static string Sha256OfBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private WikiPage CardModel(WikiSourceCapture.Result r)
    {
        var parsed = WikiPage.Parse(ReadCard(r));
        parsed.Errors.Should().BeEmpty("карточка обязана быть валидной страницей вики");
        return parsed.Model!;
    }

    // —— WikiSourceHome: дом источника определяет раздел (c1) ——

    [Theory]
    [InlineData("projects/p1", null, "projects/p1")]      // заметка notes/ проекта и файл проекта
    [InlineData("personal", null, "personal")]            // личный vault — дом по умолчанию
    [InlineData("personal", "common", "common")]          // явный выбор человека (решение 3)
    public void Home_домДаётРаздел(string home, string? target, string expected)
    {
        var d = WikiSourceHome.Decide(home, target);
        d.SectionKey.Should().Be(expected);
        d.Reason.Should().BeNull();
    }

    [Fact]
    public void Home_проектныйИсточникНеИдётВCommon_толькоПовышением()
    {
        var d = WikiSourceHome.Decide("projects/p1", "common");
        d.SectionKey.Should().BeNull();
        d.Reason.Should().Contain("повышени");
    }

    // Fail-closed: отказ с причиной, не исключение и не тихий «common». Невалидный id — через
    // тот же разбор, что и ключ раздела («..», «/», пустой, пробел). Регистр и «Проекты/…» —
    // не дом: папки и ключи канонические (WikiSections).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("common")]          // «Общее» — не дом: в него приходят target-ом или повышением
    [InlineData("spheres/s1")]      // раздел сферы — следующая фича, в Ф0–Ф1 он не дом
    [InlineData("projects/")]       // id нет
    [InlineData("projects/a..b")]
    [InlineData("projects/a/b")]
    [InlineData("projects/a b")]
    [InlineData("projects/p1/")]
    [InlineData("Проекты/p1")]
    [InlineData("PERSONAL")]
    [InlineData("personal/x")]
    [InlineData("unknown")]
    public void Home_неизвестныйИлиНевалидныйДом_отказБезИсключения(string? home)
    {
        var d = WikiSourceHome.Decide(home);
        d.SectionKey.Should().BeNull();
        d.Reason.Should().NotBeNull();
    }

    [Theory]
    [InlineData("personal")]   // target не может указывать раздел прямо
    [InlineData("")]           // пустая строка — не «нет target» и не «common»
    [InlineData("spheres/s1")]
    public void Home_чужойTarget_отказ(string target) =>
        WikiSourceHome.Decide("personal", target).SectionKey.Should().BeNull();

    // —— карточка заметки: место, отсутствие текста, хэш байтов (c2) ——

    [Fact]
    public void ЗахватЗаметки_карточкаВИсточникахСвоегоРаздела_безТекста_хэшБайтов()
    {
        var project = NewProject("П1");
        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка встречи", "ТЕЛО-КАРТОЧКА-НЕ-КОПИРУЕТСЯ. Мы решили открывать встречу в понедельник.", project.Id));

        var r = _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeTrue(r.Reason);
        r.CardRelPath.Should().StartWith($"Проекты/{project.Id}/Источники/");
        r.CardRelPath.Should().EndWith(".md");
        WikiSections.Parse(r.CardRelPath!).Should().Be("projects/" + project.Id);

        var card = CardModel(r);
        card.Frontmatter.Type.Should().Be(WikiFrontmatter.TypeSource);
        card.Frontmatter.Origin.Should().Be(WikiGate.OriginNote);
        card.Frontmatter.Ref.Should().Be("note:" + note.Id);
        ReadCard(r).Should().NotContain("ТЕЛО-КАРТОЧКА-НЕ-КОПИРУЕТСЯ");

        // hash — байтов файла на диске (форма манифеста/дрейфа), не строки из модели.
        var noteBytes = File.ReadAllBytes(_notes.ResolveAttachmentPath(User, project.Id, note.Path));
        card.Frontmatter.Hash.Should().Be(Sha256OfBytes(noteBytes));

        // След записи через WikiFileWriter (temp+Move): рядом с карточкой не остаётся *.tmp.
        var cardDir = Path.GetDirectoryName(SafePath.Join(WikiPaths.Root(_dir, User), "pages/" + r.CardRelPath))!;
        Directory.EnumerateFiles(cardDir).Should().NotContain(f => f.EndsWith(".tmp"));
    }

    // Контракт S6a (хвост ревью S6a, 9fee4bc6): ключ sourceTexts — путь карточки (Draft.Path),
    // значение — текст источника, не null; с ним правило дословности гейта проходит.
    [Fact]
    public void ЗахватЗаметки_текстИКлючКарточкиПроходятДословностьГейта()
    {
        var project = NewProject("П2");
        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка встречи", "Мы решили открывать встречу в понедельник.", project.Id));

        var r = _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeTrue(r.Reason);
        r.SourceText.Should().Be(note.Content);

        var cardTitle = Path.GetFileNameWithoutExtension(r.CardRelPath!);
        var page = new WikiPage
        {
            Title = "Ритм встреч",
            Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeTopic },
            Sections =
            [
                new WikiPage.Section { Name = "Сводка", Lines = ["Встречи начинаются в понедельник.[^s1]"] },
                new WikiPage.Section { Name = "Хронология", Lines = [] },
                new WikiPage.Section { Name = "Противоречия", Lines = [] },
                new WikiPage.Section { Name = "История", Lines = [] },
            ],
            Footnotes =
            [
                new WikiPage.WikiFootnote(
                    new WikiPage.Target(true, cardTitle),
                    "Мы решили открывать встречу в понедельник"),
            ],
        };
        var state = new[]
        {
            new WikiGate.Draft(r.CardRelPath!, ReadCard(r)),
            new WikiGate.Draft($"Проекты/{project.Id}/Ритм встреч.md", page.Render()),
        };
        var sourceTexts = new Dictionary<string, string> { [r.CardRelPath!] = r.SourceText! };

        WikiGate.Check(state, "projects/" + project.Id, [], sourceTexts).Should().BeEmpty();
    }

    // Карточка неизменяема: повторный захват той же версии — та же карточка, файл не тронут
    // (в том числе captured: время первой фиксации, не метка последнего обращения).
    [Fact]
    public void ЗахватЗаметки_идемпотентен_карточкаНеПерезаписывается()
    {
        var project = NewProject("П3");
        var note = _notes.Create(User, new CreateNoteRequest("Один раз", "содержание", project.Id));

        var r1 = _sut.CaptureNote(User, note.Id);
        var first = ReadCard(r1);
        var r2 = _sut.CaptureNote(User, note.Id);

        r2.Ok.Should().BeTrue(r2.Reason);
        r2.CardRelPath.Should().Be(r1.CardRelPath);
        ReadCard(r2).Should().Be(first);
    }

    // Дом вне проекта: по умолчанию «Личное»; с явным target — «Общее» (решение 3).
    [Fact]
    public void ЗахватЛичнойЗаметки_личныйРаздел_аСTargetCommon_общее()
    {
        var a = _notes.Create(User, new CreateNoteRequest("Личная мысль", "текст а"));
        var b = _notes.Create(User, new CreateNoteRequest("Мысль в общее", "текст б"));

        var personal = _sut.CaptureNote(User, a.Id);
        personal.Ok.Should().BeTrue(personal.Reason);
        personal.CardRelPath.Should().StartWith("Личное/Источники/");

        var common = _sut.CaptureNote(User, b.Id, "common");
        common.Ok.Should().BeTrue(common.Reason);
        common.CardRelPath.Should().StartWith("Общее/Источники/");
    }

    // Проектный источник с target — отказ на слое захвата, и ничего не записано (c1).
    [Fact]
    public void ЗахватПроектнойЗаметки_сTargetCommon_отказИНичегоНеЗаписано()
    {
        var project = NewProject("П4");
        var note = _notes.Create(User, new CreateNoteRequest("Проектная", "текст", project.Id));

        var r = _sut.CaptureNote(User, note.Id, "common");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("повышени");
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse();
    }

    // —— отказы (c3) ——

    [Fact]
    public void ЗаметкаСExpires_неИсточник()
    {
        var note = _notes.Create(User, new CreateNoteRequest("Временная", "текст", "personal", ExpiresAfterMinutes: 60));
        var r = _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("expires");
    }

    [Fact]
    public void ЧужойВладелецИЧужойПроект_отказ()
    {
        // Заметка другого владельца в модели этого владельца не существует — не читается
        // «по произвольному пути» и не захватывается.
        var alienNote = _notes.Create("u2", new CreateNoteRequest("Чужая", "тело"));
        _sut.CaptureNote(User, alienNote.Id).Ok.Should().BeFalse();

        var alien = _projects.Create("Чужой", Path.Combine(_dir, "alien"), "u2", "u2", createDirectory: true);
        File.WriteAllText(Path.Combine(alien.RootPath!, "файл.md"), "чужой текст");
        _sut.CaptureFile(User, alien.Id, "файл.md").Ok.Should().BeFalse();
    }

    [Fact]
    public void ФайлЗаКорнемПроектаИПодDataWiki_отказ()
    {
        var project = NewProject("П5");
        _sut.CaptureFile(User, project.Id, "../вне.md").Ok.Should().BeFalse();
        _sut.CaptureFile(User, project.Id, "docs/../вне.md").Ok.Should().BeFalse();

        // Корень проекта может быть предком data/ (реальный случай серверного проекта):
        // файл под {data}/wiki — страница вики, а не источник (ADR-035, решение 2).
        var wide = _projects.Create("Шир", _dir, User, User, createDirectory: true);
        var wikiFile = Path.Combine(_dir, "wiki", User, "pages", "Общее", "Страница.md");
        Directory.CreateDirectory(Path.GetDirectoryName(wikiFile)!);
        File.WriteAllText(wikiFile, "страница вики");
        var r = _sut.CaptureFile(User, wide.Id, $"wiki/{User}/pages/Общее/Страница.md");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("wiki");
    }

    // —— карточка файла: ref с проектом, хэш байтов, а не строки (c2/c3) ——

    [Fact]
    public void ЗахватФайла_карточкаВРазделеПроекта_хэшБайтовАНеСтроки()
    {
        var project = NewProject("П6");
        const string fileRel = "docs/Гайд.md";
        var full = Path.Combine(project.RootPath!, fileRel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // BOM: байты файла ≠ UTF8-байты прочитанной строки — карточка обязана держать хэш файла.
        File.WriteAllText(full, "файл источника: решено вести обзор", new UTF8Encoding(true));

        var r = _sut.CaptureFile(User, project.Id, fileRel);
        r.Ok.Should().BeTrue(r.Reason);
        r.CardRelPath.Should().StartWith($"Проекты/{project.Id}/Источники/");

        var card = CardModel(r);
        card.Frontmatter.Origin.Should().Be(WikiGate.OriginFile);
        // ref файла несёт проект: карточка копируется при повышении, и по одному пути файл не опознать.
        card.Frontmatter.Ref.Should().Be($"file:{project.Id}/{fileRel}");
        var bytes = File.ReadAllBytes(full);
        card.Frontmatter.Hash.Should().Be(Sha256OfBytes(bytes));
        Sha256OfBytes(Encoding.UTF8.GetBytes(r.SourceText!)).Should().NotBe(card.Frontmatter.Hash);
    }

    // —— недоверенный заголовок (c3, урок ревью S2b-3) ——

    [Fact]
    public void ЗаголовкиСпецсимволов_неЛомаютКарточкуИНеЗатираютДругДруга()
    {
        var project = NewProject("П7");
        // После нормализации безопасные имена равны ("A B") — карточки различает дискриминатор версии.
        var n1 = _notes.Create(User, new CreateNoteRequest("A | B", "содержание первое", project.Id));
        var n2 = _notes.Create(User, new CreateNoteRequest("A  B", "содержание второе", project.Id));

        var r1 = _sut.CaptureNote(User, n1.Id);
        var r2 = _sut.CaptureNote(User, n2.Id);
        r1.Ok.Should().BeTrue(r1.Reason);
        r2.Ok.Should().BeTrue(r2.Reason);
        r1.CardRelPath.Should().NotBe(r2.CardRelPath);

        var cardDir = Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники");
        Directory.EnumerateFiles(cardDir, "*.md").Should().HaveCount(2);

        foreach (var (r, n) in new[] { (r1, n1), (r2, n2) })
        {
            var text = ReadCard(r);
            // Разметка целая: ни чужих ссылок ([[…]] граф читает из файла карточки), ни «|».
            text.Should().NotContain("[[");
            text.Should().NotContain("]]");
            text.Should().NotContain("|");
            CardModel(r).Frontmatter.Ref.Should().Be("note:" + n.Id);
        }
    }

    // Форма имени — чистая функция, проверяется напрямую: из заголовка, при пустом — из id;
    // длина ограничена; имя — не путь («..», разделители) и не пустая строка.
    [Fact]
    public void ИмяКарточки_опаснаяФормаИзЗаголовкаИлиИзId()
    {
        WikiSourceCapture.CardName("A\nB\t| [[C]] --- D", "fallback", "note:1", "h1")
            .Should().StartWith("A B C - D-");
        WikiSourceCapture.CardName("|", "cGVyc29uYWxf", "note:2", "h2")
            .Should().StartWith("cGVyc29uYWxf-");
        WikiSourceCapture.CardName("...", "id3", "note:3", "h3").Should().StartWith("id3-");
        WikiSourceCapture.CardName(new string('к', 300), "id", "note:4", "h4")
            .Should().MatchRegex("^к{80}-[0-9a-f]{8}$");
    }
}
