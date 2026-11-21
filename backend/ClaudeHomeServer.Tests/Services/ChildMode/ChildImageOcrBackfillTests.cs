using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Фоновая текстовая копия картинок папки предмета (ChildImageOcrBackfill, Б13c, ADR-041
/// ред. 6 решение 67; форма и видимость копии — решения 90/91). Без хоста: сервис
/// собирается прямо, как соседние сторы ChildMode; projects.json — в temp-папке.
///
/// Проверяется:
/// 1. стартовый обход: png в папке проекта-предмета → после прохода есть
///    .ccs-text/скан.png.txt с текстом фейка OCR (путь и форма — TextCopyOf, не свои);
/// 2. дедуп по пути: 20 загрузок одной картинки (писатель ставит сам) + стартовый обход
///    того же файла → OCR вызван 1 раз, копия одна;
/// 3. файл удалён до обработки → пропуск без исключения, OCR не вызван;
/// 4. сбой OCR: исключение не выходит из ProcessAsync, копии нет; следующая картинка
///    очереди обработана (петля хоста не гасится);
/// 5. отказ (не-ok OcrResult) и «недоверенный» текст (пустой, одни [?]) → копии нет;
/// 6. ревью Б11b п.1: перезапись без текста снимает старую копию — для pdf (конвертер
///    отказан) и для картинки (фейк OCR вернул «[?]»);
/// 7. обход не трогает: уже имеющий копию, служебное (.ccs-text/, .ccs-trash/, .claude/),
///    не-картинки по расширению, чужие папки (не проекты-предметы).
///
/// Мутации (критерий c3 — в копии scripts/mutation-copy.sh):
/// • снять try/catch вокруг ocr.RecognizeAsync в ChildImageOcrBackfill.ProcessAsync —
///   краснеет СбойOcr_ProcessAsyncНеБросает_КопииНет;
/// • снять TryDelete(copyAbs) перед конвертацией в ChildTextCopyService.SaveAsync —
///   краснеет ПерезаписьPdf_БезТекста_СтараяКопияСнята;
/// • снять TryDelete в EnqueueImageOcr — краснеет ПерезаписьКартинки_OcrБезТекста_СтаройКопииНет.
/// </summary>
public class ChildImageOcrBackfillTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly ChildTextCopyService _textCopy;
    private readonly string _childId;
    private string _subjectRoot = "";

    // Картинка по сигнатуре: PNG-магические байты + содержимое. Detect/SaveAsync судят
    // тип по сигнатуре (ChildAttachmentPolicy), содержимое для них не разбиралось никогда.
    // Маркер в содержимом — способ фейка OCR различить, какой файл ему принесли.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static byte[] PngOf(string marker) => [.. PngSignature, .. System.Text.Encoding.UTF8.GetBytes(marker)];
    private static readonly byte[] PdfBytes = [.. "%PDF-1.4"u8, .. "fake"u8];

    public ChildImageOcrBackfillTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc_child_ocr_backfill_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            // Конвертера под этим именем нет: его сбой — детерминированный «текста нет»
            // для теста ревью Б11b п.1 (MarkitdownService деградирует в null молча).
            ["Markitdown:Command"] = "markitdown-absent-for-tests",
        }).Build();

        _users = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _childId = _users.Add("kid.ocr", "kid-pw-123456", "user").Id;
        _users.GetById(_childId)!.ChildMode = new ChildProfile { Enabled = true };

        var appSettings = new AppSettingsService(config);
        _projects = new ProjectManager(config, _users, appSettings);
        _textCopy = new ChildTextCopyService(
            new MarkitdownService(config, NullLogger<MarkitdownService>.Instance),
            config, NullLogger<ChildTextCopyService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── 1. стартовый обход: картинка без копии получает копию OCR ─────────────────────

    [Fact]
    public async Task СтартовыйОбход_PngВПапкеПредмета_КопящаяСТекстомOcr()
    {
        var root = SubjectRoot();
        File.WriteAllBytes(Path.Combine(root, "скан.png"), PngOf("скан"));

        var ocr = OkOcr("Текст со страницы");
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        ocr.Calls.Should().Be(1);
        // Форма копии — TextCopyOf (решение 90): «.ccs-text/скан.png.txt», не своя и не
        // подмена расширения.
        File.ReadAllText(CopyOf(root, "скан.png")).Should().Be("Текст со страницы");
    }

    // Копия лежит и в подпапке: TextCopyOf сохраняет полный путь ключа (решение 90).
    [Fact]
    public async Task СтартовыйОбход_КартинкаВПодпапке_КопящаяСохраняетПутьКлюча()
    {
        var root = SubjectRoot();
        Directory.CreateDirectory(Path.Combine(root, "Заметки"));
        File.WriteAllBytes(Path.Combine(root, "Заметки", "урок.png"), PngOf("урок"));

        var ocr = OkOcr("Текст урока");
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        File.ReadAllText(CopyOf(root, "Заметки/урок.png")).Should().Be("Текст урока");
    }

    // ── 2. дедуп по пути: 20 загрузок + стартовый обход → один вызов OCR ──────────────

    [Fact]
    public async Task ДедупПоПути_ДвадцатьЗагрузокИСтартовыйОбход_ОдинВызовOcrОднаКопящая()
    {
        var root = SubjectRoot();
        var ocr = OkOcr("Текст");
        var backfill = MakeBackfill(ocr);

        // Загрузка ставит картинку в очередь сама (писатель), 20 повторов одного пути.
        for (var i = 0; i < 20; i++)
        {
            var res = await _textCopy.SaveAsync(
                root, "двойной.png", PngOf("двойной"), _textCopy.MaxAttachmentBytes(), requireText: false);
            res.Accepted.Should().BeTrue($"итерация {i}");
            res.Kind.Should().Be(ChildTextCopyService.KindImage);
            // Копии при загрузке нет — её пишет OCR (решение 67).
            res.TextCopy.Should().BeFalse();
        }

        // Стартовый обход того же файла: копии ещё нет — вторая постановка, её гасит дедуп.
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        ocr.Calls.Should().Be(1, "дедуп по каноническому пути: 20 загрузок + обход — одна задача");
        File.Exists(CopyOf(root, "двойной.png")).Should().BeTrue();
    }

    // ── 3. файл удалён до обработки ────────────────────────────────────────────────────

    [Fact]
    public async Task ФайлУдалёнДоОбработки_ПропускБезИсключенияOcrНеЗван()
    {
        var root = SubjectRoot();
        var ocr = OkOcr("Текст");
        var backfill = MakeBackfill(ocr);

        var res = await _textCopy.SaveAsync(
            root, "снят.png", PngOf("снят"), _textCopy.MaxAttachmentBytes(), requireText: false);
        res.Accepted.Should().BeTrue();
        File.Delete(Path.Combine(root, "снят.png"));

        await backfill.RunQueuedAsync(); // задача уже в очереди, файла нет

        ocr.Calls.Should().Be(0, "удалённый файл не идёт в OCR");
        File.Exists(CopyOf(root, "снят.png")).Should().BeFalse();
    }

    // ── 4. сбой OCR: исключение не выходит, следующая картинка обработана ──────────────

    // Путь очереди целиком (тот же ProcessJobAsync, что у петли хоста): сбой одной
    // картинки не останавливает очередь, следующая обработана.
    [Fact]
    public async Task СбойOcr_ОчередьПродолжается_СледующаяКартинкаОбработана()
    {
        var root = SubjectRoot();
        File.WriteAllBytes(Path.Combine(root, "битый.png"), PngOf("битый"));
        File.WriteAllBytes(Path.Combine(root, "живой.png"), PngOf("живой"));

        var ocr = new FakeOcr(bytes =>
            System.Text.Encoding.UTF8.GetString(bytes).Contains("битый")
                ? throw new InvalidOperationException("сбой шва")
                : OcrResult.Success("Живой текст", "qwen-vl", 1, 1, 1));
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        File.Exists(CopyOf(root, "битый.png")).Should().BeFalse("сбой OCR — копии нет");
        File.ReadAllText(CopyOf(root, "живой.png")).Should().Be("Живой текст",
            "сбой одной картинки не останавливает очередь");
    }

    // Контракт одной задачи: ProcessAsync не выпускает исключение шва наружу — по нему
    // хост не гасится. Мутация «снять try/catch вокруг ocr.RecognizeAsync» краснеет здесь:
    // без него исключение выходит из ProcessAsync и тест падает.
    [Fact]
    public async Task СбойOcr_ProcessAsyncНеБросает_КопииНет()
    {
        var root = SubjectRoot();
        File.WriteAllBytes(Path.Combine(root, "битый.png"), PngOf("битый"));

        var ocr = new FakeOcr(_ => throw new InvalidOperationException("сбой шва"));
        var backfill = MakeBackfill(ocr);

        await backfill.ProcessAsync(new ChildTextCopyService.OcrJob(root, "битый.png"), default);

        ocr.Calls.Should().Be(1);
        File.Exists(CopyOf(root, "битый.png")).Should().BeFalse();
    }

    // ── 5. отказ и «недоверенный» ответ: копии нет ─────────────────────────────────────

    [Fact]
    public async Task ОтказOcr_ЛюбаяПричина_КопииНет()
    {
        var root = SubjectRoot();
        File.WriteAllBytes(Path.Combine(root, "скан.png"), PngOf("скан"));

        var ocr = new FakeOcr(_ => OcrResult.Fail(OcrFailureReason.LocalUnavailable, "эндпоинт лежит"));
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        ocr.Calls.Should().Be(1);
        File.Exists(CopyOf(root, "скан.png")).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[?] [?]  [?]")]
    public async Task НедоверенныйТекст_OcrУспешен_КопииНет(string text)
    {
        var root = SubjectRoot();
        var name = "пусто" + text.Length + ".png";
        File.WriteAllBytes(Path.Combine(root, name), PngOf(name));

        var ocr = OkOcr(text);
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        ocr.Calls.Should().Be(1);
        File.Exists(CopyOf(root, name)).Should().BeFalse("пустой или «одни [?]» текст — не копия (тот же критерий, что у КИ1b)");
    }

    // ── 6. ревью Б11b п.1: перезапись без текста не оставляет старой копии ─────────────

    // Мутация «снять TryDelete(copyAbs) перед конвертацией в SaveAsync» краснеет здесь.
    [Fact]
    public async Task ПерезаписьPdf_БезТекста_СтараяКопияСнята()
    {
        var root = SubjectRoot();
        // Первая версия: копия есть (её записал писатель — здесь руками, тот же путь).
        var copy = CopyOf(root, "лист.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "прежний текст");

        // Перезапись содержимым без текста: конвертер под тестовым именем отказан → null.
        var res = await _textCopy.SaveAsync(
            root, "лист.pdf", PdfBytes, _textCopy.MaxAttachmentBytes(), requireText: false);
        res.Accepted.Should().BeTrue();
        res.TextCopy.Should().BeFalse();

        File.Exists(copy).Should().BeFalse(
            "ревью Б11b п.1: новой версии без текста прежняя копия не остаётся — модель не читает прежний текст");
    }

    // Мутация «снять TryDelete в EnqueueImageOcr» краснеет здесь.
    [Fact]
    public async Task ПерезаписьКартинки_OcrБезТекста_СтаройКопииНет()
    {
        var root = SubjectRoot();
        // Первая версия: копия записана полным циклом (загрузка → обход → OCR).
        var ocrOk = OkOcr("прежний текст");
        var first = MakeBackfill(ocrOk);
        var res = await _textCopy.SaveAsync(
            root, "скан.png", PngOf("скан"), _textCopy.MaxAttachmentBytes(), requireText: false);
        res.Accepted.Should().BeTrue();
        await first.RunQueuedAsync();
        File.ReadAllText(CopyOf(root, "скан.png")).Should().Be("прежний текст");

        // Перезапись новой картинкой: старая копия снята при постановке в очередь;
        // OCR вернул «недоверенный» текст — новой копии нет.
        var ocrEmpty = OkOcr("[?]");
        var second = MakeBackfill(ocrEmpty);
        res = await _textCopy.SaveAsync(
            root, "скан.png", PngOf("скан"), _textCopy.MaxAttachmentBytes(), requireText: false);
        res.Accepted.Should().BeTrue();
        await second.RunQueuedAsync();

        File.Exists(CopyOf(root, "скан.png")).Should().BeFalse();
    }

    // ── 7. обход не трогает: копия есть, служебное, не-картинки, чужие папки ───────────

    [Fact]
    public async Task Обход_УжеИмеющийКопиюИСлужебноеИНеКартинки_ВОчередьНеИдут()
    {
        var root = SubjectRoot();
        // Уже имеет копию — не идёт.
        File.WriteAllBytes(Path.Combine(root, "есть.png"), PngOf("есть"));
        var copy = CopyOf(root, "есть.png");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, "уже есть");
        // Служебное: сама копия, корзина, настройки — не содержимое для OCR.
        Directory.CreateDirectory(Path.Combine(root, ".ccs-text"));
        File.WriteAllBytes(Path.Combine(root, ".ccs-text", "копия.png"), PngOf("копия"));
        Directory.CreateDirectory(Path.Combine(root, ".ccs-trash", "20261119"));
        File.WriteAllBytes(Path.Combine(root, ".ccs-trash", "20261119", "снесён.png"), PngOf("снесён"));
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        File.WriteAllBytes(Path.Combine(root, ".claude", "настройка.png"), PngOf("настройка"));
        // Не картинка по содержимому (pdf по расширению .png): в очередь станет,
        // до OCR не дойдёт — сбой по сигнатуре.
        File.WriteAllBytes(Path.Combine(root, "некартинка.png"), PdfBytes);
        // Обычная картинка — единственный кандидат.
        File.WriteAllBytes(Path.Combine(root, "живая.png"), PngOf("живая"));

        var ocr = OkOcr("Текст живой");
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        ocr.Calls.Should().Be(1, "только та картинка, у которой нет копии и которая прошла по сигнатуре");
        ocr.Labels.Should().Equal("живая.png");
        File.ReadAllText(copy).Should().Be("уже есть", "существующая копия не перезаписывается");
        File.Exists(CopyOf(root, "некартинка.png")).Should().BeFalse();
        // Копии служебного нет: «.ccs-text/копия.png.txt» и «.ccs-text/.ccs-trash/…txt» не заводятся
        // (TextCopyOf пути под «.ccs-text/» — сам путь, поэтому проверяется копия копии).
        File.Exists(CopyOf(root, "копия.png")).Should().BeFalse();
        File.Exists(CopyOf(root, ".ccs-trash/20261119/снесён.png")).Should().BeFalse();
    }

    // Чужая папка (обычный проект, не проект-предмета) — не обходится вовсе.
    [Fact]
    public async Task Обход_ОбычныйПроектНеПроектПредмета_НеОбойден()
    {
        var root = SubjectRoot();
        var other = Path.Combine(_tempDir, "other");
        _projects.Create("Обычный", other, _childId, "kid.ocr", createDirectory: true);
        File.WriteAllBytes(Path.Combine(other, "картинка.png"), PngOf("картинка"));

        var ocr = OkOcr("не должен");
        var backfill = MakeBackfill(ocr);
        backfill.ScanSubjectFolders();
        await backfill.RunQueuedAsync();

        ocr.Calls.Should().Be(0, "обход — только папки проектов-предметов (маркер ChildSubjectId)");
        File.Exists(CopyOf(other, "картинка.png")).Should().BeFalse();
    }

    // ── фикстура ───────────────────────────────────────────────────────────────────────

    // Папка проекта-предмета: маркер ChildSubjectId записан провижером (тот же путь,
    // что в бою); обход судит проект по ProjectCapabilities.IsChildSubject.
    private string SubjectRoot()
    {
        if (_subjectRoot.Length > 0) return _subjectRoot;
        var root = Path.Combine(_tempDir, "subject", "math");
        _projects.Create("Математика", root, _childId, "kid.ocr",
            createDirectory: true, childSubjectId: "math");
        _subjectRoot = root;
        return root;
    }

    private ChildImageOcrBackfill MakeBackfill(ILocalPageOcr ocr) =>
        new(_projects, _textCopy, ocr, NullLogger<ChildImageOcrBackfill>.Instance);

    private static FakeOcr OkOcr(string text) =>
        new(_ => OcrResult.Success(text, "qwen-vl-test", 1, 1, 1));

    // Путь копии — ChildPathPolicy.TextCopyOf (тот же источник формы, что у писателя).
    private static string CopyOf(string root, string rel) =>
        Path.Combine(root, ChildPathPolicy.TextCopyOf(rel).Replace('/', Path.DirectorySeparatorChar));

    private sealed class FakeOcr(Func<byte[], OcrResult> answer) : ILocalPageOcr
    {
        public int Calls;
        public readonly List<string> Labels = [];

        public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct)
        {
            Calls++;
            Labels.Add(info.Label);
            return Task.FromResult(answer(png.ToArray()));
        }
    }
}
