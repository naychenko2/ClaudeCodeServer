using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Tests.Helpers;

namespace ClaudeHomeServer.Tests.Services;

// Захват источника (S6h): дом источника определяет раздел (WikiSourceHome — ADR-035, решение 9;
// ADR-036, решения 1 и 3); карточка type: source лежит в Источники/ своего раздела, текста
// источника не несёт, hash — sha256 байтов файла на диске (не строки); наружу — путь карточки
// (ключ sourceTexts правила дословности WikiGate — контракт S6a) и текст источника для
// QuoteMatcher. Отказы: заметка с expires, чужой владелец/проект, файл за корнем проекта или
// через символьную ссылку в нём, под {data} целиком (не только {data}/wiki), файл свыше
// предела или невалидный UTF-8, невалидный дом; заголовок с спецсимволами (в т.ч. недопустимыми
// на Windows) не ломает карточку и не затирает карточку другого источника (урок ревью S2b-3,
// доработка ревью S6h).
public class WikiSourceCaptureTests : IDisposable
{
    private const string User = "u1";
    private readonly string _dir; // dataDir
    private readonly string _roots; // рабочие каталоги проектов — вне {data}
    private readonly ProjectManager _projects;
    private readonly NotesService _notes;
    private readonly FakeSessions _sessions;
    private readonly WikiSourceCapture _sut;

    public WikiSourceCaptureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wiki_source_capture_" + Guid.NewGuid().ToString("N"));
        // Корни проектов — вне {data}: в реальности они лежат в рабочих папках владельца, а
        // {data} — служебное пространство сервера (notes/ всех владельцев, wiki/, реестры).
        // Корень под dataDir дал бы «файл проекта», который новый отказ (M3 ревью S6h) закрывает;
        // случай «корень проекта = предок {data}» проверяется отдельно, проектом «Шир».
        _roots = Path.Combine(Path.GetTempPath(), "wiki_source_capture_roots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "notes", User));
        Directory.CreateDirectory(_roots);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        var users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(config, users, new AppSettingsService(config));
        _notes = new NotesService(_projects, config, NullLogger<NotesService>.Instance);
        _sessions = new FakeSessions();
        _sut = new WikiSourceCapture(_notes, _projects, _sessions, config);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _dir, _roots })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private Project NewProject(string name) =>
        _projects.Create(name, Path.Combine(_roots, "proj_" + name), User, User, createDirectory: true);

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

        var alien = _projects.Create("Чужой", Path.Combine(_roots, "alien"), "u2", "u2", createDirectory: true);
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

    // M1 ревью S6h: SafePath лексический — «docs/link.md → ~/.ssh/id_rsa» лежит в корне строкой
    // и читался бы целиком. Инвариант: файл проекта читается только изнутри канонического корня.
    [Fact]
    public void ФайлЧерезСимвольнуюСсылку_отказ_цельНеЧитается()
    {
        var project = NewProject("П8");
        var outside = Path.Combine(_roots, "вне_" + project.Id, "id_rsa");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "приватный ключ вне корня проекта");
        var link = Path.Combine(project.RootPath!, "docs/link.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // создание ссылки на Windows требует прав (Developer Mode) — случай POSIX
        }

        var r = _sut.CaptureFile(User, project.Id, "docs/link.md");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("ссылк");
        // Цель не «прочиталась как файл проекта»: на диске вики не появилось ничего.
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse();
    }

    // M3 ревью S6h: закрыт весь {data}, а не только {data}/wiki: под ним живут чужие владельцы
    // (notes/{другойOwner}/) и реестры — источник не выходит за своего.
    [Fact]
    public void ФайлПодData_ЧужойВладелецИлиРеестр_отказ()
    {
        // Реальный случай правки: корень проекта — предок {data} (рабочая папка содержит data/).
        var wide = _projects.Create("Шир2", _dir, User, User, createDirectory: true);
        var other = Path.Combine(_dir, "notes", "u2", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "заметка другого владельца");

        var r = _sut.CaptureFile(User, wide.Id, "notes/u2/x.md");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("{data}");

        // Тот же отказ и для реестра — positional, до обращения к файлу.
        _sut.CaptureFile(User, wide.Id, "projects.json").Ok.Should().BeFalse();
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

    // M4 ревью S6h: предел — до чтения. Файл разрежённый: на диске он почти ничего не занимает,
    // но прочитанный целиком стал бы источником (без проверки размера этот тест краснеет).
    [Fact]
    public void ФайлСвышеПредела_отказДоЧтения()
    {
        var project = NewProject("П9");
        var full = Path.Combine(project.RootPath!, "big.bin");
        using (var fs = new FileStream(full, FileMode.Create)) fs.SetLength(WikiSourceCapture.MaxSourceBytes + 1);

        var r = _sut.CaptureFile(User, project.Id, "big.bin");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("предела");
    }

    // M4 ревью S6h: бинарник и битый UTF-8 — отказ с причиной, а не U+FFFD в тексте источника
    // (раньше ReadAllText молча заменял битые байты, и дословность QuoteMatcher лгала бы).
    [Fact]
    public void БинарникИлиНевалидныйUTF8_отказБезИсключения()
    {
        var project = NewProject("П10");
        var bin = Path.Combine(project.RootPath!, "shot.png");
        File.WriteAllBytes(bin, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE]); // PNG: 0x89 и 0xFF — не UTF-8
        var r = _sut.CaptureFile(User, project.Id, "shot.png");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("UTF-8");

        var broken = Path.Combine(project.RootPath!, "broken.md");
        File.WriteAllBytes(broken, [.. Encoding.UTF8.GetBytes("текст "), 0xC3, 0x28]); // битая последовательность
        var r2 = _sut.CaptureFile(User, project.Id, "broken.md");
        r2.Ok.Should().BeFalse();
        r2.Reason.Should().Contain("UTF-8");
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

    // M2 ревью S6h: «План: Q4» на Windows не записался бы вовсе — исключение из записи карточки
    // шло бы наружу захвата. Форма имени одна на обеих платформах: недопустимые на Windows
    // символы («:*?"<>») заменяются «_» (Path.GetInvalidFileNameChars здесь не образец: на Linux
    // он их не отдаёт, а карточка пишется и там, и там).
    [Fact]
    public void ИмяКарточки_НедопустимыеНаWindowsСимволы_ЗаменяютсяПодчёркиванием()
    {
        WikiSourceCapture.CardName("План: Q4", "id", "note:5", "h5").Should().StartWith("План_ Q4-");
        foreach (var title in new[] { "a * b", "c ? d", "e \"f\"", "g < h", "i > j" })
        {
            var name = WikiSourceCapture.CardName(title, "id", "note:6", "h6");
            foreach (var bad in new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' })
                name.Should().NotContain(bad.ToString());
        }
    }

    // Тот же случай через захват: карточка пишется, исключения наружу нет, имя реально на диске.
    [Fact]
    public void ЗахватЗаметкиСНедопустимымНаWindowsЗаголовком_карточкаБезИсключения()
    {
        var project = NewProject("П11");
        var note = _notes.Create(User, new CreateNoteRequest("План: Q4 <сезон*>", "тело заметки", project.Id));

        var r = _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeTrue(r.Reason);
        var name = Path.GetFileNameWithoutExtension(r.CardRelPath!);
        foreach (var bad in new[] { ':', '*', '?', '"', '<', '>' })
            name.Should().NotContain(bad.ToString());
        ReadCard(r).Should().Contain("# " + name);
    }

    // —— CaptureSession (S6j, origin: session): выдержка чата и её границы ——

    private Session AddSession(string id, string? projectId, bool taskExecution = false, string? name = "Чат разбора")
    {
        var s = new Session { Id = id, OwnerId = User, ProjectId = projectId, Name = name, TaskExecution = taskExecution };
        _sessions.All.Add(s);
        _sessions.History[id] =
        [
            new StoredUserMessage("Решили: нарезку делает код, выдержка не больше 8 КБ."),
            new StoredTextMessage("Принято, режу по диапазону m{a}-m{b}."),
        ];
        return s;
    }

    [Fact]
    public async Task ЗахватСессии_тотЖеВладелецИПроект_карточкаСВыдержкой()
    {
        AddSession("s1", "p1");

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 2);

        r.Ok.Should().BeTrue(r.Reason);
        r.SourceText.Should().Contain("Решили: нарезку делает код");
        r.SourceText.Should().Contain("Принято, режу по диапазону");
        var card = CardModel(r);
        card.Frontmatter.Origin.Should().Be(WikiGate.OriginSession);
        // ref — та же форма, которую читает TryParseSessionRef (и UI «Открыть в чате» по m{a})
        card.Frontmatter.Ref.Should().Be("session:s1#m1-2");
        WikiPage.TryParseSessionRef(card.Frontmatter.Ref!, out var sid, out var a, out var b).Should().BeTrue();
        (sid, a, b).Should().Be(("s1", 1, 2));
    }

    // Три отказа по владению (c3): чат чужого владельца, чат другого проекта, чат исполнения
    // задачи. Каждый — с причиной, ни один — исключением.
    [Fact]
    public async Task ЗахватСессии_чужойВладелец_отказ()
    {
        var s = AddSession("s1", "p1");
        s.OwnerId = "u2";

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 2);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("владел");
    }

    [Fact]
    public async Task ЗахватСессии_чужойПроект_отказ()
    {
        AddSession("s1", "p2");

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 2);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("дом");
    }

    [Fact]
    public async Task ЗахватСессии_чатИсполненияЗадачи_отказ()
    {
        AddSession("s1", "p1", taskExecution: true);

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 2);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("исполнения задач");
    }

    // Чат вне проекта — дом «Личное» (ADR-036, решение 3): в свой раздел пишется,
    // в проектный — отказ.
    [Fact]
    public async Task ЗахватСессии_чатВнеПроекта_пишетсяВЛичное()
    {
        AddSession("s1", null);

        var r = await _sut.CaptureSession(User, "personal", "s1", 1, 2);
        r.Ok.Should().BeTrue(r.Reason);
        r.CardRelPath!.Should().StartWith("Личное/Источники/");

        var r2 = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 2);
        r2.Ok.Should().BeFalse();
        r2.Reason.Should().Contain("personal", "дом чата вне проекта — «Личное», в раздел проекта он не идёт");
    }

    // Диапазон за точку ветвления: префикс ветки — ответственность родителя (§12б этап 3),
    // карточка ветки его не цитирует — отказ с номером, с которого начинается диапазон.
    [Fact]
    public async Task ЗахватСессии_диапазонЗахватываетПрефиксВетки_отказ()
    {
        AddSession("s1", "p1");
        _sessions.History["s1"] =
        [
            new StoredUserMessage("реплика префикса"),
            new StoredTextMessage("ответ префикса"),
            new StoredBranchedFromMessage { SourceSessionId = "p0", SourceName = "Родитель" },
            new StoredUserMessage("реплика после ветвления"),
        ];

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 4);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("m4");

        var ok = await _sut.CaptureSession(User, "projects/p1", "s1", 4, 4);
        ok.Ok.Should().BeTrue(ok.Reason);
        ok.SourceText.Should().Contain("реплика после ветвления").And.NotContain("реплика префикса");
    }

    // Границы диапазона: m{N} 1-based (TryParseSessionRef: a ≥ 1); from>to — отказ;
    // from за концом истории — отказ; пустая история — отказ. Ничего не пишется на диске.
    [Theory]
    [InlineData(0, 2)]   // m{0} не существует
    [InlineData(2, 1)]   // пустой перевёрнутый диапазон
    [InlineData(3, 4)]   // за концом истории (2 записи)
    public async Task ЗахватСессии_неверныйДиапазон_отказ(int from, int to)
    {
        AddSession("s1", "p1");

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", from, to);
        r.Ok.Should().BeFalse();
        r.Reason.Should().NotBeNull();
    }

    [Fact]
    public async Task ЗахватСессии_пустаяИсторияИлиТолькоСлужебное_отказ()
    {
        AddSession("s1", "p1");
        _sessions.History["s1"] = [];
        var empty = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 1);
        empty.Ok.Should().BeFalse();
        empty.Reason.Should().Contain("истории");

        _sessions.History["s1"] = [new StoredResultMessage("success", 10, 1)];
        var onlyService = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 1);
        onlyService.Ok.Should().BeFalse();
        onlyService.Reason.Should().Contain("нечего цитировать");
    }

    // Выдержка карточки — ≤8 КБ (решение 3): в карточку входят целые реплики, целиком
    // помещающиеся в предел; обрезанной реплики нет (дословность QuoteMatcher — по целым).
    [Fact]
    public async Task ЗахватСессии_выдержкаНеБольше8КБ_целымиРепликами()
    {
        AddSession("s1", "p1");
        var longText = new string('к', 3000); // 6000 байт UTF-8 на реплику
        _sessions.History["s1"] =
        [
            new StoredUserMessage(longText),
            new StoredTextMessage(longText),
            new StoredUserMessage(longText),
        ];

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 3);
        r.Ok.Should().BeTrue(r.Reason);
        Encoding.UTF8.GetByteCount(r.SourceText!).Should().BeLessThanOrEqualTo(WikiChatSlicer.MaxExcerptBytes);
        // 3 реплики по ~6 КБ в 8 КБ не помещаются: вошла одна, остальные — не обрезанные куски
        (r.SourceText!.Split("Пользователь:").Length - 1).Should().Be(1);
    }

    // Точные секреты вызывающего доходят до выдержки (параметр exactSecrets):
    // значение длиннее порога SecretRedactor не остаётся в тексте карточки.
    [Fact]
    public async Task ЗахватСессии_точныйСекретВызывающего_неПопадаетВВыдержку()
    {
        AddSession("s1", "p1");
        _sessions.History["s1"] = [new StoredUserMessage("ключ провайдера: zzz-тайный-секрет-инстанса-12345")];

        var r = await _sut.CaptureSession(User, "projects/p1", "s1", 1, 1,
            new[] { "zzz-тайный-секрет-инстанса-12345" });
        r.Ok.Should().BeTrue(r.Reason);
        r.SourceText.Should().NotContain("zzz-тайный-секрет-инстанса-12345");
        r.SourceText.Should().Contain("[REDACTED:instance-secret]");
    }

    // Stub каталога сессий: набор чатов и их история — какие построил тест.
    // ResolveOwnerId возвращает поле OwnerId: правила делегирования — механика
    // SessionManager, не этот тест.
    private sealed class FakeSessions : ISessionDirectory
    {
        public readonly List<Session> All = [];
        public readonly Dictionary<string, List<StoredMessage>> History = [];

        public Session? GetById(string id) => All.FirstOrDefault(s => s.Id == id);
        public IReadOnlyCollection<Session> GetAll() => All;
        public string? ResolveOwnerId(Session s) => s.OwnerId;
        public Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId) =>
            Task.FromResult<IReadOnlyList<StoredMessage>>(
                History.TryGetValue(sessionId, out var h) ? h : new List<StoredMessage>());
        public bool SetWikiMark(string sessionId, string? mark) => true;
    }
}
