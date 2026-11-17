using System.IO.Compression;
using System.Text;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Docs;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Каталог материалов предмета (разрез «Знания репетитора» §2, К2a):
/// data/child-subjects/{childId}/{subjectId}/ — materials.json (опись), files/ (оригинал),
/// text/ (извлечённый текст).
///
/// Что фиксируют тесты (критерии К2a):
/// 1. txt: одна строка опися, ReadTextAsync равен исходнику дословно;
/// 2. docx опознаётся по содержимому zip (word/document.xml), текст кладётся в text/ и
///    читается оттуда;
/// 3. картинка в этой пачке — unsupported_type (КИ1a/КИ1b заменят этот тест), 21 МБ —
///    too_large, сумма предмета выше MaxSubjectBytes — subject_full, PDF/docx без текста —
///    pdf_no_text / docx_no_text, и ни один отказ не оставляет файла без строки описи;
/// 4. id, storedName, textName — серверные: имя админа — только подпись, «../../x.txt» не
///    создаёт файлов вне каталога предмета;
/// 5. гонка: 20 параллельных AddAsync одного предмета — 20 строк описи и 20 файлов на диске;
/// 6. симлинк text/ наружу — ReadTextAsync отказывает: содержимое чужого файла не отдаётся;
/// 7. DeleteAsync сносит оригинал, текст и строку; DeleteChildAsync — каталог ребёнка.
///
/// Конвертер в тестах — не настоящий markitdown: «нет такой команды» (null → отказ no_text,
/// детерминировано на обеих ОС) и «echo маркера» (текстовый результат для docx-фикстуры).
/// Проверяется путь стора, а не конвертер.
///
/// Мутация (критерий c2): снять семафор предмета в ChildSubjectMaterialsStore.AddAsync
/// (await sem.WaitAsync() / sem.Release()) — краснеет Add_Parallel_TwentyFiles_AllInManifest:
/// 20 read-modify-write одного materials.json теряют строки (20 → единицы). Остальные
/// тесты от этой мутации не краснеют: у них один вызов на предмет, окна гонки нет.
/// </summary>
public sealed class ChildSubjectMaterialsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _root;

    public ChildSubjectMaterialsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ccs-child-materials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _root = Path.Combine(_tempDir, ChildSubjectMaterialsStore.DirName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* временная папка */ }
        GC.SuppressFinalize(this);
    }

    private string SubjectDir(string child, string subject) => Path.Combine(_root, child, subject);

    // Конвертер по умолчанию — несуществующая команда: Process.Start бросает, сервис
    // отдаёт null, стор видит «текста нет». Детерминировано на Windows и Linux, в отличие
    // от «cat» по бинару (на Windows type может донести байты по-своему).
    private static IConfiguration Config(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?> { ["Markitdown:Command"] = "ccs-no-such-converter-9f3a" };
        if (overrides is not null)
            foreach (var (k, v) in overrides) values[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // Конвертер-заглушка для текстового результата: печатает маркер и путь (тот же приём,
    // что «cmd /c type»/«cat» в FilesController-тестах). Маркер ASCII: вывод cmd — в кодовой
    // странице консоли, кириллица читалась бы mojibake.
    private static IConfiguration EchoMarkerConfig() =>
        Config(new Dictionary<string, string?>
        {
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? "cmd /c echo DOCX-MARKER" : "echo DOCX-MARKER",
        });

    private ChildSubjectMaterialsStore Store(IConfiguration? config = null)
    {
        var cfg = config ?? Config();
        return new ChildSubjectMaterialsStore(
            _root,
            new MarkitdownService(cfg, NullLogger<MarkitdownService>.Instance),
            cfg,
            NullLogger<ChildSubjectMaterialsStore>.Instance);
    }

    // Фикстура docx: zip с обязательной записью word/document.xml — та сигнатура, по которой
    // стор опознаёт docx. Содержимое записи — маркер исходника.
    private static byte[] DocxBytes(string marker)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = entry.Open();
            w.Write(Encoding.UTF8.GetBytes($"<w:document><w:p>{marker}</w:p></w:document>"));
        }
        return ms.ToArray();
    }

    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x00, 0x00]; // «%PDF-1.4.» + бинарный хвост

    // ── c1: txt / docx / картинка / размер / имя / удаление ───────────────────────────

    [Fact]
    public async Task ТекстДобавлен_ОднаСтрокаОписи_ИЧитаетсяДословно()
    {
        var store = Store();
        var source = "Конспект урока: дроби ⅑ и обычные, 12 · 7 = 84";
        var bytes = Encoding.UTF8.GetBytes(source);

        var res = await store.AddAsync("kid-a", "math", "конспект.txt", bytes, "admin-1");
        res.Accepted.Should().BeTrue();
        res.Code.Should().BeNull();

        var list = await store.ListAsync("kid-a", "math");
        list.Should().HaveCount(1);
        var m = list[0];
        m.Id.Should().MatchRegex("^m_[0-9a-f]{12}$");
        m.Name.Should().Be("конспект.txt");                       // подпись админа — как есть
        m.Kind.Should().Be(ChildSubjectMaterialsStore.KindText);
        m.Bytes.Should().Be(bytes.LongLength);
        m.UploadedBy.Should().Be("admin-1");
        m.UploadedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        // Имена серверные: имени админа в пути нет.
        m.StoredName.Should().Be(m.Id + ".txt");
        m.TextName.Should().Be(m.Id + ".txt");

        (await store.ReadTextAsync("kid-a", "math", m.Id)).Should().Be(source);
    }

    [Fact]
    public async Task DocxОпознан_ТекстЛожитсяВText_ИЧитаетсяОттуда()
    {
        var store = Store(EchoMarkerConfig());
        var res = await store.AddAsync("kid-a", "math", "учебник.docx", DocxBytes("абзац-фикстура"), "admin-1");
        res.Accepted.Should().BeTrue(res.Code);

        var m = (await store.ListAsync("kid-a", "math")).Single();
        m.Kind.Should().Be(ChildSubjectMaterialsStore.KindDocx);
        m.StoredName.Should().Be(m.Id + ".docx");

        // Конвертер-заглушка печатает маркер: он дошёл только через text/.
        var text = await store.ReadTextAsync("kid-a", "math", m.Id);
        text.Should().NotBeNull();
        text.Should().Contain("DOCX-MARKER");
    }

    [Fact]
    public async Task КартинкаОтказанаUnsupportedType_БезФайловНаДиске()
    {
        var store = Store();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };

        var res = await store.AddAsync("kid-a", "math", "page.png", png, "admin-1");
        res.Accepted.Should().BeFalse();
        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeUnsupportedType);

        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
        Directory.Exists(SubjectDir("kid-a", "math")).Should().BeFalse(); // отказ — до всякой записи
    }

    [Fact]
    public async Task ФайлБольшеMaxFileBytes_ОтказTooLarge()
    {
        var store = Store();   // дефолт 20 МБ
        var res = await store.AddAsync("kid-a", "math", "big.txt", new byte[21 * 1024 * 1024], "admin-1");

        res.Accepted.Should().BeFalse();
        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeTooLarge);
        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
    }

    [Fact]
    public async Task СуммаПредметаБольшеMaxSubjectBytes_ВторойОтказSubjectFull()
    {
        var store = Store(Config(new Dictionary<string, string?>
        {
            ["ChildMode:Materials:MaxSubjectBytes"] = "1000",
        }));
        var body = Encoding.UTF8.GetBytes(new string('x', 600));

        var first = await store.AddAsync("kid-a", "math", "a.txt", body, "admin-1");
        var second = await store.AddAsync("kid-a", "math", "b.txt", body, "admin-1");

        first.Accepted.Should().BeTrue(first.Code);
        second.Accepted.Should().BeFalse();
        second.Code.Should().Be(ChildSubjectMaterialsStore.CodeSubjectFull);
        (await store.ListAsync("kid-a", "math")).Should().HaveCount(1);

        // Метр предмета — по описи: отказ не оставил файла.
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "files")).Should().HaveCount(1);
    }

    [Fact]
    public async Task ПустойПредмет_ОписьПустая_ЧтениеОтказывает()
    {
        var store = Store();

        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
        (await store.ReadTextAsync("kid-a", "math", "m_00000000000000")).Should().BeNull();
        (await store.DeleteAsync("kid-a", "math", "m_00000000000000")).Should().BeFalse();
    }

    [Fact]
    public async Task ИмяСРодителями_ТолькоПодпись_ВнеКаталогаНичегоНеПоявилось()
    {
        var store = Store();
        var res = await store.AddAsync("kid-a", "math", "../../x.txt", Encoding.UTF8.GetBytes("урок"), "admin-1");
        res.Accepted.Should().BeTrue(res.Code);

        var m = res.Material!;
        m.Name.Should().Be("x.txt");                                     // путь из подписи срезан
        m.StoredName.Should().NotContain("/").And.NotContain("..");
        m.TextName.Should().NotContain("/").And.NotContain("..");

        // Вне каталога предмета ни одного файла: в темповом корне — только каталог стора.
        Directory.GetFileSystemEntries(_tempDir).Should().Equal(_root);
        File.Exists(Path.Combine(_tempDir, "x.txt")).Should().BeFalse();
        // Внутри каталога — ровно опись и два файла.
        Directory.GetFileSystemEntries(SubjectDir("kid-a", "math")).Should().HaveCount(3);
    }

    [Fact]
    public async Task УдалениеСноситОригиналТекстИСтроку()
    {
        var store = Store();
        var m = (await store.AddAsync("kid-a", "math", "урок.txt", Encoding.UTF8.GetBytes("текст урока"), "admin-1")).Material!;

        File.Exists(Path.Combine(SubjectDir("kid-a", "math"), "files", m.StoredName)).Should().BeTrue();
        File.Exists(Path.Combine(SubjectDir("kid-a", "math"), "text", m.TextName)).Should().BeTrue();

        (await store.DeleteAsync("kid-a", "math", m.Id)).Should().BeTrue();

        File.Exists(Path.Combine(SubjectDir("kid-a", "math"), "files", m.StoredName)).Should().BeFalse();
        File.Exists(Path.Combine(SubjectDir("kid-a", "math"), "text", m.TextName)).Should().BeFalse();
        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
        (await store.ReadTextAsync("kid-a", "math", m.Id)).Should().BeNull();
        (await store.DeleteAsync("kid-a", "math", m.Id)).Should().BeFalse();   // повторного удаления нет
    }

    [Fact]
    public async Task PdfБезТекста_ОтказPdfNoText_ОригиналНеОстаётся()
    {
        var store = Store();   // конвертер несуществует → текста нет
        var res = await store.AddAsync("kid-a", "math", "scan.pdf", PdfBytes, "admin-1");

        res.Accepted.Should().BeFalse();
        res.Code.Should().Be(ChildSubjectMaterialsStore.CodePdfNoText);
        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
        // Файла без строки описи нет: под каталогом предмета — ни одного файла.
        Directory.GetFiles(SubjectDir("kid-a", "math"), "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task DocxБезТекста_ОтказDocxNoText_ОригиналНеОстаётся()
    {
        var store = Store();
        var res = await store.AddAsync("kid-a", "math", "скан.docx", DocxBytes("абзац"), "admin-1");

        res.Accepted.Should().BeFalse();
        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeDocxNoText);
        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
        Directory.GetFiles(SubjectDir("kid-a", "math"), "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    // ── c2: гонка ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Add_Parallel_TwentyFiles_AllInManifest()
    {
        var store = Store();
        var body = Encoding.UTF8.GetBytes("строка журнала урока");

        // Task.Run — чтобы вызовы реально сошлись на семафоре: без него первый AddAsync
        // прошёл бы весь на одном потоке до старта второго (окна гонки не было бы).
        var adds = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => store.AddAsync("kid-a", "math", $"ф{i}.txt", body, "admin-1")))
            .ToArray();
        var results = await Task.WhenAll(adds);

        results.Should().OnlyContain(r => r.Accepted, string.Join(", ", results.Where(r => !r.Accepted).Select(r => r.Code)));
        var list = await store.ListAsync("kid-a", "math");
        list.Should().HaveCount(20);
        list.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        list.Should().OnlyContain(m => m.Bytes == body.LongLength);

        // Опись — источник истины: под ней ровно 20 оригиналов и 20 текстов.
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "files")).Should().HaveCount(20);
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "text")).Should().HaveCount(20);
    }

    // ── c3: симлинк ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextЧерезСимлинкНаружу_ЧтениеОтказывает()
    {
        var store = Store();
        var m = (await store.AddAsync("kid-a", "math", "урок.txt", Encoding.UTF8.GetBytes("текст урока"), "admin-1")).Material!;

        var subjectDir = SubjectDir("kid-a", "math");
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);

        // text/ перенесён наружу и заменён симлинком на своё место вне каталога предмета.
        Directory.Move(Path.Combine(subjectDir, "text"), Path.Combine(outside, "text"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(subjectDir, "text"), Path.Combine(outside, "text"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // нет права создавать симлинк (Windows без Developer Mode) — случай не проверяем
        }

        // Файл физически читается — но он чужой: наружу он не отдаётся.
        File.Exists(Path.Combine(outside, "text", m.TextName)).Should().BeTrue();
        (await store.ReadTextAsync("kid-a", "math", m.Id)).Should().BeNull();
    }

    // ── каскад ребёнка ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteChildСнитаетКаталогРебёнка_ЧужойНеТрогает()
    {
        var store = Store();
        await store.AddAsync("kid-a", "math", "урок.txt", Encoding.UTF8.GetBytes("урок"), "admin-1");
        await store.AddAsync("kid-b", "math", "урок.txt", Encoding.UTF8.GetBytes("урок"), "admin-1");

        await store.DeleteChildAsync("kid-a");

        Directory.Exists(Path.Combine(_root, "kid-a")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "kid-b", "math")).Should().BeTrue();
    }

    // ── аргументы ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ЧужойИлиПустойКлючПредмета_ИсключениеАргумента()
    {
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("kid-a", "../escape", "x.txt", new byte[] { 1 }, "admin-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("kid-a", "Математика", "x.txt", new byte[] { 1 }, "admin-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("kid-a", "", "x.txt", new byte[] { 1 }, "admin-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync("", "math", "x.txt", new byte[] { 1 }, "admin-1"));
    }
}
