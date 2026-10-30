using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;
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
    private readonly WikiKeeper _keeper;
    private readonly WikiApplyService _apply;
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
        // Флаг llm-wiki: состояние раздела читается моделью (BuildFresh) — нужно проверке
        // применения в тесте дефекта d144bb0f (тот же фиксатор, что в WikiApplyCheckTests).
        _notes = new NotesService(_projects, config, NullLogger<NotesService>.Instance, null, new WikiFlagOn());
        _sessions = new FakeSessions();
        // Замок раздела и запись карточки в манифест — у Хранителя; проверка применения —
        // настоящая (тест «первый Prepare после захвата — Ready»).
        _keeper = new WikiKeeper(_notes, config);
        _apply = new WikiApplyService(_notes, _keeper, new NoSpheres(), config);
        _sut = new WikiSourceCapture(_notes, _projects, _sessions, config, _keeper);
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
    public async Task ЗахватЗаметки_карточкаВИсточникахСвоегоРаздела_безТекста_хэшБайтов()
    {
        var project = NewProject("П1");
        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка встречи", "ТЕЛО-КАРТОЧКА-НЕ-КОПИРУЕТСЯ. Мы решили открывать встречу в понедельник.", project.Id));

        var r = await _sut.CaptureNote(User, note.Id);
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
    public async Task ЗахватЗаметки_текстИКлючКарточкиПроходятДословностьГейта()
    {
        var project = NewProject("П2");
        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка встречи", "Мы решили открывать встречу в понедельник.", project.Id));

        var r = await _sut.CaptureNote(User, note.Id);
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
    public async Task ЗахватЗаметки_идемпотентен_карточкаНеПерезаписывается()
    {
        var project = NewProject("П3");
        var note = _notes.Create(User, new CreateNoteRequest("Один раз", "содержание", project.Id));

        var r1 = await _sut.CaptureNote(User, note.Id);
        var first = ReadCard(r1);
        var r2 = await _sut.CaptureNote(User, note.Id);

        r2.Ok.Should().BeTrue(r2.Reason);
        r2.CardRelPath.Should().Be(r1.CardRelPath);
        ReadCard(r2).Should().Be(first);
    }

    // Дом вне проекта: по умолчанию «Личное»; с явным target — «Общее» (решение 3).
    [Fact]
    public async Task ЗахватЛичнойЗаметки_личныйРаздел_аСTargetCommon_общее()
    {
        var a = _notes.Create(User, new CreateNoteRequest("Личная мысль", "текст а"));
        var b = _notes.Create(User, new CreateNoteRequest("Мысль в общее", "текст б"));

        var personal = await _sut.CaptureNote(User, a.Id);
        personal.Ok.Should().BeTrue(personal.Reason);
        personal.CardRelPath.Should().StartWith("Личное/Источники/");

        var common = await _sut.CaptureNote(User, b.Id, "common");
        common.Ok.Should().BeTrue(common.Reason);
        common.CardRelPath.Should().StartWith("Общее/Источники/");
    }

    // Проектный источник с target — отказ на слое захвата, и ничего не записано (c1).
    [Fact]
    public async Task ЗахватПроектнойЗаметки_сTargetCommon_отказИНичегоНеЗаписано()
    {
        var project = NewProject("П4");
        var note = _notes.Create(User, new CreateNoteRequest("Проектная", "текст", project.Id));

        var r = await _sut.CaptureNote(User, note.Id, "common");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("повышени");
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse();
    }

    // —— отказы (c3) ——

    [Fact]
    public async Task ЗаметкаСExpires_неИсточник()
    {
        var note = _notes.Create(User, new CreateNoteRequest("Временная", "текст", "personal", ExpiresAfterMinutes: 60));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("expires");
    }

    [Fact]
    public async Task ЧужойВладелецИЧужойПроект_отказ()
    {
        // Заметка другого владельца в модели этого владельца не существует — не читается
        // «по произвольному пути» и не захватывается.
        var alienNote = _notes.Create("u2", new CreateNoteRequest("Чужая", "тело"));
        (await _sut.CaptureNote(User, alienNote.Id)).Ok.Should().BeFalse();

        var alien = _projects.Create("Чужой", Path.Combine(_roots, "alien"), "u2", "u2", createDirectory: true);
        File.WriteAllText(Path.Combine(alien.RootPath!, "файл.md"), "чужой текст");
        (await _sut.CaptureFile(User, alien.Id, "файл.md")).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task ФайлЗаКорнемПроектаИПодDataWiki_отказ()
    {
        var project = NewProject("П5");
        (await _sut.CaptureFile(User, project.Id, "../вне.md")).Ok.Should().BeFalse();
        (await _sut.CaptureFile(User, project.Id, "docs/../вне.md")).Ok.Should().BeFalse();

        // Корень проекта может быть предком data/ (реальный случай серверного проекта):
        // файл под {data}/wiki — страница вики, а не источник (ADR-035, решение 2).
        var wide = _projects.Create("Шир", _dir, User, User, createDirectory: true);
        var wikiFile = Path.Combine(_dir, "wiki", User, "pages", "Общее", "Страница.md");
        Directory.CreateDirectory(Path.GetDirectoryName(wikiFile)!);
        File.WriteAllText(wikiFile, "страница вики");
        var r = await _sut.CaptureFile(User, wide.Id, $"wiki/{User}/pages/Общее/Страница.md");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("wiki");
    }

    // M1 ревью S6h: SafePath лексический — «docs/link.md → ~/.ssh/id_rsa» лежит в корне строкой
    // и читался бы целиком. Инвариант: файл проекта читается только изнутри канонического корня.
    [Fact]
    public async Task ФайлЧерезСимвольнуюСсылку_отказ_цельНеЧитается()
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

        var r = await _sut.CaptureFile(User, project.Id, "docs/link.md");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("ссылк");
        // Цель не «прочиталась как файл проекта»: на диске вики не появилось ничего.
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse();
    }

    // M3 ревью S6h: закрыт весь {data}, а не только {data}/wiki: под ним живут чужие владельцы
    // (notes/{другойOwner}/) и реестры — источник не выходит за своего.
    [Fact]
    public async Task ФайлПодData_ЧужойВладелецИлиРеестр_отказ()
    {
        // Реальный случай правки: корень проекта — предок {data} (рабочая папка содержит data/).
        var wide = _projects.Create("Шир2", _dir, User, User, createDirectory: true);
        var other = Path.Combine(_dir, "notes", "u2", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "заметка другого владельца");

        var r = await _sut.CaptureFile(User, wide.Id, "notes/u2/x.md");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("{data}");

        // Тот же отказ и для реестра — positional, до обращения к файлу.
        (await _sut.CaptureFile(User, wide.Id, "projects.json")).Ok.Should().BeFalse();
    }

    // —— карточка файла: ref с проектом, хэш байтов, а не строки (c2/c3) ——

    [Fact]
    public async Task ЗахватФайла_карточкаВРазделеПроекта_хэшБайтовАНеСтроки()
    {
        var project = NewProject("П6");
        const string fileRel = "docs/Гайд.md";
        var full = Path.Combine(project.RootPath!, fileRel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // BOM: байты файла ≠ UTF8-байты прочитанной строки — карточка обязана держать хэш файла.
        File.WriteAllText(full, "файл источника: решено вести обзор", new UTF8Encoding(true));

        var r = await _sut.CaptureFile(User, project.Id, fileRel);
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
    public async Task ФайлСвышеПредела_отказДоЧтения()
    {
        var project = NewProject("П9");
        var full = Path.Combine(project.RootPath!, "big.bin");
        using (var fs = new FileStream(full, FileMode.Create)) fs.SetLength(WikiSourceCapture.MaxSourceBytes + 1);

        var r = await _sut.CaptureFile(User, project.Id, "big.bin");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("предела");
    }

    // M4 ревью S6h: бинарник и битый UTF-8 — отказ с причиной, а не U+FFFD в тексте источника
    // (раньше ReadAllText молча заменял битые байты, и дословность QuoteMatcher лгала бы).
    [Fact]
    public async Task БинарникИлиНевалидныйUTF8_отказБезИсключения()
    {
        var project = NewProject("П10");
        var bin = Path.Combine(project.RootPath!, "shot.png");
        File.WriteAllBytes(bin, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE]); // PNG: 0x89 и 0xFF — не UTF-8
        var r = await _sut.CaptureFile(User, project.Id, "shot.png");
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("UTF-8");

        var broken = Path.Combine(project.RootPath!, "broken.md");
        File.WriteAllBytes(broken, [.. Encoding.UTF8.GetBytes("текст "), 0xC3, 0x28]); // битая последовательность
        var r2 = await _sut.CaptureFile(User, project.Id, "broken.md");
        r2.Ok.Should().BeFalse();
        r2.Reason.Should().Contain("UTF-8");
    }

    // —— недоверенный заголовок (c3, урок ревью S2b-3) ——

    [Fact]
    public async Task ЗаголовкиСпецсимволов_неЛомаютКарточкуИНеЗатираютДругДруга()
    {
        var project = NewProject("П7");
        // После нормализации безопасные имена равны ("A B") — карточки различает дискриминатор версии.
        var n1 = _notes.Create(User, new CreateNoteRequest("A | B", "содержание первое", project.Id));
        var n2 = _notes.Create(User, new CreateNoteRequest("A  B", "содержание второе", project.Id));

        var r1 = await _sut.CaptureNote(User, n1.Id);
        var r2 = await _sut.CaptureNote(User, n2.Id);
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
    public async Task ЗахватЗаметкиСНедопустимымНаWindowsЗаголовком_карточкаБезИсключения()
    {
        var project = NewProject("П11");
        var note = _notes.Create(User, new CreateNoteRequest("План: Q4 <сезон*>", "тело заметки", project.Id));

        var r = await _sut.CaptureNote(User, note.Id);
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

    // —— карточка в манифесте раздела (дефект d144bb0f, хвост ревью S6h, п.3) ——
    //
    // Карточка — страница раздела: без её записи в манифесте сверка применения
    // (CheckSectionAgainstDisk) считает любой .md раздела вне манифеста дрейфом, и первое
    // же применение после первого захвата отбито. Запись делает Хранитель под замком
    // раздела; захват не пишет в раздел, состояние которого расходится с манифестом.

    private string ManifestFile(string sectionKey) =>
        Path.Combine(WikiPaths.Root(_dir, User), "keeper/manifest", WikiManifest.SafeFileName(sectionKey) + ".json");

    private static string TopicPage(string title) => $$"""
        ---
        type: topic
        aliases: []
        owns: []
        sources: []
        status: active
        ---

        # {{title}}

        ## Сводка

        ## Хронология

        ## Противоречия

        ## История
        """;

    private static WikiPackage.Operation AppendOp(string page, string text, string baseHash) => new()
    {
        Op = WikiPackage.OpAppend,
        Page = page,
        Text = text,
        Class = WikiPackage.ClassKey(WikiGate.OriginNote, WikiPackage.OpAppend),
        Decision = WikiPackage.DecisionAuto,
        BaseHashes = new Dictionary<string, string> { [page] = baseHash },
    };

    // Пакет доводится до «accepted» штатными переходами хранилища (тот же порядок,
    // что в WikiApplyCheckTests): карточка и базовые хэши читаются с диска.
    private WikiPackage CreateAccepted(string section, params WikiPackage.Operation[] ops)
    {
        var root = WikiPaths.Root(_dir, User);
        var pkg = new WikiPackage
        {
            Id = WikiPackageStore.NewId(),
            Section = section,
            Sources = [new WikiPackage.SourceRef(WikiGate.OriginNote, "n1")],
            Operations = ops,
            Status = WikiPackage.StatusChecking,
        };
        var created = WikiPackageStore.Create(root, pkg);
        created.Ok.Should().BeTrue(created.Reason);
        WikiPackageStore.Transition(root, pkg.Id, WikiPackage.StatusProposed).Ok.Should().BeTrue();
        WikiPackageStore.Transition(root, pkg.Id, WikiPackage.StatusAccepted).Ok.Should().BeTrue();
        return WikiPackageStore.TryRead(root, pkg.Id).Package!;
    }

    // Сам дефект: первый Prepare раздела после захвата — Ready, а не CodeDrift.
    // Раздел настоящий: базовая страница через Хранителя, пакет — штатными переходами,
    // проверка — реальный WikiApplyService.
    [Fact]
    public async Task Захват_первыйPrepareРазделаПослеЗахвата_ReadyАНеCodeDrift()
    {
        var project = NewProject("П13");
        var section = "projects/" + project.Id;
        var root = WikiPaths.Root(_dir, User);
        await _keeper.EnsureInitializedAsync(User);
        var pageRel = $"Проекты/{project.Id}/Ритм встреч.md";
        var w = await _keeper.WritePageAsync(User, section, pageRel, TopicPage("Ритм встреч"));
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);

        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка о ритме", "Мы решили открывать встречу в понедельник.", project.Id));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeTrue(r.Reason);

        var pkg = CreateAccepted(section,
            AppendOp(pageRel, "Мы решили открывать встречу в понедельник",
                WikiManifest.TryRead(root, section).Manifest[pageRel]));
        var prepared = await _apply.PrepareAsync(User, pkg, new WikiApplyService.ApplyContext(
            Path.GetFileNameWithoutExtension(r.CardRelPath!), r.SourceText));
        prepared.Ready.Should().BeTrue($"код {prepared.Code}: {prepared.Reason}");
    }

    // Карточку регистрирует Хранитель: в манифесте — путь карточки и хэш БАЙТОВ её файла
    // (та же форма, что сверяет применение). Мутация «не регистрировать» краснит этот тест
    // и тест выше.
    [Fact]
    public async Task Захват_карточкаЗаписанаВМанифестеРаздела_хэшБайтовФайла()
    {
        var project = NewProject("П12");
        var note = _notes.Create(User, new CreateNoteRequest("Ритм встреч", "Мы решили открывать встречу.", project.Id));

        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeTrue(r.Reason);

        var read = WikiManifest.TryRead(WikiPaths.Root(_dir, User), "projects/" + project.Id);
        read.State.Should().Be(WikiManifestState.Valid);
        var bytes = File.ReadAllBytes(SafePath.Join(WikiPaths.Root(_dir, User), "pages/" + r.CardRelPath!));
        read.Manifest[r.CardRelPath!].Should().Be(Sha256OfBytes(bytes));
    }

    // Идемпотентность записи: повторный захват той же версии не двигает ни файл
    // (existing test), ни манифест — байт в байт.
    [Fact]
    public async Task Захват_повторныйЗахват_манифестНеДополняется_карточкаНеДвижется()
    {
        var project = NewProject("П14");
        var note = _notes.Create(User, new CreateNoteRequest("Один раз", "содержание", project.Id));

        var r1 = await _sut.CaptureNote(User, note.Id);
        r1.Ok.Should().BeTrue(r1.Reason);
        var root = WikiPaths.Root(_dir, User);
        var firstCard = File.ReadAllText(SafePath.Join(root, "pages/" + r1.CardRelPath!));
        var firstManifest = File.ReadAllText(ManifestFile("projects/" + project.Id));

        var r2 = await _sut.CaptureNote(User, note.Id);
        r2.Ok.Should().BeTrue(r2.Reason);
        r2.CardRelPath.Should().Be(r1.CardRelPath);
        File.ReadAllText(SafePath.Join(root, "pages/" + r2.CardRelPath!)).Should().Be(firstCard);
        File.ReadAllText(ManifestFile("projects/" + project.Id)).Should().Be(firstManifest);
    }

    // Карточка на диске без записи (записана прежней версией кода — или сбой посреди
    // записи): следующий захват той же версии регистрирует её без ошибки и без перезаписи
    // файла. Это же — доказательство «повтор безопасен» для сбоя посреди записи.
    [Fact]
    public async Task Захват_карточкаНаДискеБезЗаписи_регистрируетсяБезПерезаписи()
    {
        var project = NewProject("П15");
        var section = "projects/" + project.Id;
        var note = _notes.Create(User, new CreateNoteRequest("Прежняя", "содержание прежней", project.Id));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeTrue(r.Reason);

        var root = WikiPaths.Root(_dir, User);
        var cardFull = SafePath.Join(root, "pages/" + r.CardRelPath!);
        var before = File.ReadAllText(cardFull);
        // Снимаем запись — состояние, в котором карточку оставила прежняя версия кода
        // (RollbackTo с null — тот же API, которым применение откатывает create).
        WikiManifest.RollbackTo(root, section, new Dictionary<string, string?> { [r.CardRelPath!] = null });

        var again = await _sut.CaptureNote(User, note.Id);
        again.Ok.Should().BeTrue(again.Reason);
        again.CardRelPath.Should().Be(r.CardRelPath);
        File.ReadAllText(cardFull).Should().Be(before, "карточка неизменяема: регистрация не переписывает файл");
        WikiManifest.TryRead(root, section).Manifest[r.CardRelPath!]
            .Should().Be(Sha256OfBytes(File.ReadAllBytes(cardFull)));
    }

    // Захват не пишет в раздел, состояние которого расходится с его манифестом: чужой файл
    // вне записи — отказ с причиной, на диске не появилось ничего.
    [Fact]
    public async Task Захват_вРазделеЧужойФайлВнеМанифеста_отказИНичегоНеЗаписано()
    {
        var project = NewProject("П16");
        var foreign = Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Чужое.md");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "файл вне манифеста");

        var note = _notes.Create(User, new CreateNoteRequest("В дрейфующий", "текст", project.Id));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("не в манифесте");
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse();
    }

    // Тот же отказ по второй стороне сверки: запись манифеста не совпадает с диском
    // (страницу правили мимо Хранителя).
    [Fact]
    public async Task Захват_записьМанифестаРасходитсяСДиском_отказ()
    {
        var project = NewProject("П17");
        var section = "projects/" + project.Id;
        await _keeper.EnsureInitializedAsync(User);
        var pageRel = $"Проекты/{project.Id}/Страница.md";
        var w = await _keeper.WritePageAsync(User, section, pageRel, TopicPage("Страница"));
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);
        File.WriteAllText(SafePath.Join(WikiPaths.Root(_dir, User), "pages/" + pageRel),
            TopicPage("Страница") + "\nправка мимо Хранителя\n");

        var note = _notes.Create(User, new CreateNoteRequest("После чужой правки", "текст", project.Id));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("расходится с манифестом");
    }

    // Невалидный манифест раздела — тот же fail-closed, что у записи Хранителя и применения:
    // отказ с причиной, ничего не пишется.
    [Fact]
    public async Task Захват_манифестРазделаНевалиден_отказБезЗаписи()
    {
        var project = NewProject("П18");
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestFile("projects/" + project.Id))!);
        File.WriteAllText(ManifestFile("projects/" + project.Id), "");

        var note = _notes.Create(User, new CreateNoteRequest("В битый манифест", "текст", project.Id));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("невалиден");
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse();
    }

    // Имя карточки — функция (ref, version); файл под ним, не являющийся карточкой,
    // не перезаписывается: захват отказывается с причиной, чужой файл цел.
    [Fact]
    public async Task Захват_подИменемКарточкиЧужойФайл_отказБезПерезаписи()
    {
        var project = NewProject("П19");
        var note = _notes.Create(User, new CreateNoteRequest("Занятое имя", "текст", project.Id));

        // Имя, которое возьмёт захват (та же функция), занято чужим файлом.
        var versionHash = Sha256OfBytes(File.ReadAllBytes(_notes.ResolveAttachmentPath(User, project.Id, note.Path)));
        var name = WikiSourceCapture.CardName("Занятое имя", note.Id, "note:" + note.Id, versionHash);
        var cardDir = Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники");
        Directory.CreateDirectory(cardDir);
        var foreignFile = Path.Combine(cardDir, name + ".md");
        File.WriteAllText(foreignFile, "# Чужой файл без шапки вики\n");

        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("не читается как карточка");
        File.ReadAllText(foreignFile).Should().Be("# Чужой файл без шапки вики\n");
    }

    // Потерянная СВОЯ карточка — не дрейф, а «восстановить из источника» (хвост ревью
    // S6e-2, п.1). Сценарий жизни: restore с неполного бэкапа или ручное удаление файла.
    // Без этой правки сверка отказывала по отсутствующему файлу из манифеста: захват,
    // применение и adopt отказывали разделу навсегда, и починить его можно было только
    // ручной правкой манифеста. Мутация «сверка ловит потерянную свою карточку как дрейф»
    // (вернуть отказ по отсутствующему файлу без исключения ourCardRel) краснит этот тест.
    [Fact]
    public async Task Захват_потеряннаяСвояКарточка_восстановленаИзИсточника_иРазделСноваПрименяется()
    {
        var project = NewProject("П20");
        var section = "projects/" + project.Id;
        var root = WikiPaths.Root(_dir, User);
        await _keeper.EnsureInitializedAsync(User);
        var pageRel = $"Проекты/{project.Id}/Ритм встреч.md";
        var w = await _keeper.WritePageAsync(User, section, pageRel, TopicPage("Ритм встреч"));
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);

        var note = _notes.Create(User, new CreateNoteRequest(
            "Заметка о ритме", "Мы решили открывать встречу в понедельник.", project.Id));
        var first = await _sut.CaptureNote(User, note.Id);
        first.Ok.Should().BeTrue(first.Reason);

        // Файл карточки потерян, запись в манифесте — осталась.
        var cardFull = SafePath.Join(root, "pages/" + first.CardRelPath!);
        var lostHash = Sha256OfBytes(File.ReadAllBytes(cardFull));
        File.Delete(cardFull);
        File.Exists(cardFull).Should().BeFalse();

        var again = await _sut.CaptureNote(User, note.Id);
        again.Ok.Should().BeTrue(again.Reason);
        again.CardRelPath.Should().Be(first.CardRelPath, "имя — функция (ref, version): та же версия того же источника");

        // Карточка по источнику: валидная страница type: source со своим ref и хэшем версии.
        File.Exists(cardFull).Should().BeTrue("карточка восстановлена");
        var model = CardModel(again);
        model.Frontmatter.Type.Should().Be(WikiFrontmatter.TypeSource);
        model.Frontmatter.Ref.Should().Be("note:" + note.Id);
        model.Frontmatter.Hash.Should()
            .Be(Sha256OfBytes(File.ReadAllBytes(_notes.ResolveAttachmentPath(User, project.Id, note.Path))),
            "хэш карточки — байты файла источника, той же версии");

        // Запись манифеста — под восстановленные байты: старая запись той же карточки
        // отличается от них только полем captured (версия источника — в имени, она та же),
        // и оставленная она вернула бы разделу дрейф по хэшу его собственной карточки.
        var manifest = WikiManifest.TryRead(root, section);
        manifest.State.Should().Be(WikiManifestState.Valid);
        manifest.Manifest[again.CardRelPath!].Should().Be(Sha256OfBytes(File.ReadAllBytes(cardFull)));

        // Раздел проекта снова применяется: та же настоящая проверка, что в тесте d144bb0f.
        var pkg = CreateAccepted(section,
            AppendOp(pageRel, "Мы решили открывать встречу в понедельник", manifest.Manifest[pageRel]));
        var prepared = await _apply.PrepareAsync(User, pkg, new WikiApplyService.ApplyContext(
            Path.GetFileNameWithoutExtension(again.CardRelPath!), again.SourceText));
        prepared.Ready.Should().BeTrue($"код {prepared.Code}: {prepared.Reason}");

        // Следующее состояние из вердикта (доработка 2, п.2): повторный захват оборвался
        // между WikiFileWriter.Write и обновлением записи — «файл с новыми байтами + запись
        // с хэшем потерянного файла». Состояние строится напрямую: запись возвращается к
        // хэшу потерянного файла. Мутация «исключение своей карточки только в ветке „нет на
        // диске“» (вернуть отказ по расхождению хэша без ourCardRel) краснит этот шаг:
        // захват отказан; без обновления записи красен Prepare ниже (шаг 2 применения видит
        // протухшую запись).
        WikiManifest.RollbackTo(root, section, new Dictionary<string, string?> { [again.CardRelPath!] = lostHash });

        var third = await _sut.CaptureNote(User, note.Id);
        third.Ok.Should().BeTrue(third.Reason);
        WikiManifest.TryRead(root, section).Manifest[again.CardRelPath!]
            .Should().Be(Sha256OfBytes(File.ReadAllBytes(cardFull)),
                "протухшая запись подставлена под байты диска");

        // Второй Prepare — новым пакетом: первый оставил прежний в «applying» (состояние
        // проверки), а проверяется здесь состояние раздела после обновления записи.
        var pkg2 = CreateAccepted(section,
            AppendOp(pageRel, "Мы решили открывать встречу в понедельник",
                WikiManifest.TryRead(root, section).Manifest[pageRel]));
        var preparedAgain = await _apply.PrepareAsync(User, pkg2, new WikiApplyService.ApplyContext(
            Path.GetFileNameWithoutExtension(third.CardRelPath!), third.SourceText));
        preparedAgain.Ready.Should().BeTrue($"код {preparedAgain.Code}: {preparedAgain.Reason}");
    }

    // Отсутствие файла ДРУГОЙ страницы из манифеста — по-прежнему дрейф: исключение
    // касается только своей карточки этого захвата. На диске не появилось ничего.
    [Fact]
    public async Task Захват_потерянаДРУГАЯСтраницаИзМанифеста_отказИКарточкаНеНаписана()
    {
        var project = NewProject("П21");
        var section = "projects/" + project.Id;
        await _keeper.EnsureInitializedAsync(User);
        var pageRel = $"Проекты/{project.Id}/Страница.md";
        var w = await _keeper.WritePageAsync(User, section, pageRel, TopicPage("Страница"));
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);
        File.Delete(SafePath.Join(WikiPaths.Root(_dir, User), "pages/" + pageRel));

        var note = _notes.Create(User, new CreateNoteRequest("После потери страницы", "текст", project.Id));
        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("отсутствует на диске");
        Directory.Exists(Path.Combine(WikiPaths.Pages(_dir, User), "Проекты", project.Id, "Источники"))
            .Should().BeFalse("отказ — на диске ничего не пишется");
    }

    // Исключение в ветке расхождения хэша — только своей карточки: ДРУГАЯ страница в том же
    // состоянии остаётся дрейфом. Состояние: своя карточка на диске с протухшей записью
    // (файл переписан, запись — прежних байтов) + чужая правка страницы мимо Хранителя →
    // отказ с именем чужой страницы, протухшая запись не подставлена: до записи дело не дошло.
    [Fact]
    public async Task Захват_протухшаяЗаписьСвоейКарточкиИДрейфЧужойСтраницы_отказПоЧужойБезОбновления()
    {
        var project = NewProject("П22");
        var section = "projects/" + project.Id;
        var root = WikiPaths.Root(_dir, User);
        await _keeper.EnsureInitializedAsync(User);
        var pageRel = $"Проекты/{project.Id}/Страница.md";
        var w = await _keeper.WritePageAsync(User, section, pageRel, TopicPage("Страница"));
        w.Status.Should().Be(WikiOpStatus.Applied, w.Reason);

        var note = _notes.Create(User, new CreateNoteRequest("Ритм встреч", "решили открывать в понедельник", project.Id));
        var first = await _sut.CaptureNote(User, note.Id);
        first.Ok.Should().BeTrue(first.Reason);
        var cardFull = SafePath.Join(root, "pages/" + first.CardRelPath!);
        var staleHash = Sha256OfBytes(File.ReadAllBytes(cardFull));

        // Состояние п.2: файл карточки переписан (новые байты), запись — прежних.
        File.Delete(cardFull);
        var restored = await _sut.CaptureNote(User, note.Id);
        restored.Ok.Should().BeTrue(restored.Reason);
        WikiManifest.RollbackTo(root, section, new Dictionary<string, string?> { [first.CardRelPath!] = staleHash });

        // И дрейф чужой страницы.
        File.WriteAllText(SafePath.Join(root, "pages/" + pageRel), TopicPage("Страница") + "\nправка мимо Хранителя\n");

        var r = await _sut.CaptureNote(User, note.Id);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Contain("расходится с манифестом").And.Contain(pageRel,
            "причина — чужая страница: исключение касается только своей карточки");
        WikiManifest.TryRead(root, section).Manifest[first.CardRelPath!].Should().Be(staleHash,
            "отказ — ничего не пишется: протухшая запись не подставлена");
    }

    private sealed class WikiFlagOn : ClaudeHomeServer.Services.Composition.IFeatureFlagGate
    {
        public bool IsEnabled(string userId, string key) => key == FeatureFlagKeys.LlmWiki;
    }

    private sealed class NoSpheres : ISphereDirectory
    {
        public string? SphereOf(string ownerId, string projectId) => null;
        public IReadOnlyList<string> ProjectsOf(string ownerId, string sphereId) => [];
        public bool Enabled(string ownerId) => false;
        public string? SphereName(string ownerId, string sphereId) => null;
        public string? CharterOf(string ownerId, string sphereId) => null;
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
