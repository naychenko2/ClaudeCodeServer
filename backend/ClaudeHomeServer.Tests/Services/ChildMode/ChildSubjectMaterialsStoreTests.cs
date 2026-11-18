using System.IO.Compression;
using System.Text;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm.Gateway;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace ClaudeHomeServer.Tests.Services.ChildMode;

/// <summary>
/// Каталог материалов предмета (разрез «Знания репетитора» §2, К2a):
/// data/child-subjects/{childId}/{subjectId}/ — materials.json (опись), files/ (оригинал),
/// text/ (извлечённый текст).
///
/// Что фиксируют тесты (критерии К2a + КИ1b, разрез §9):
/// 1. txt: одна строка опися, ReadTextAsync равен исходнику дословно;
/// 2. docx опознаётся по содержимому zip (word/document.xml), текст кладётся в text/ и
///    читается оттуда;
/// 3. 21 МБ — too_large, сумма предмета выше MaxSubjectBytes — subject_full, PDF/docx без
///    текста — pdf_no_text / docx_no_text, HEIC — unsupported_type (картинка с КИ1b — не
///    unsupported_type, её ветка ниже), и ни один отказ не оставляет файла без строки описи;
/// 4. id, storedName, textName — серверные: имя админа — только подпись, «../../x.txt» не
///    создаёт файлов вне каталога предмета;
/// 5. гонка: 20 параллельных AddAsync одного предмета — 20 строк описи и 20 файлов на диске;
/// 6. симлинк text/ наружу — ReadTextAsync отказывает: содержимое чужого файла не отдаётся;
/// 7. DeleteAsync сносит оригинал, текст и строку; DeleteChildAsync — каталог ребёнка;
///    запись под семафором проверяет живого владельца (ревью КИ1b): после сноса каталога
///    нет ни после какой загрузки — в том числе вернувшейся после минутного OCR;
/// 8. картинка (КИ1b): фейковый ILocalPageOcr + настоящий SkiaImageRaster. Успех — оригинал
///    в files/, текст в text/, kind «image», preview = первые 600 символов; любой отказ или
///    сбой OCR/растра и отсутствие растра — ocr_unavailable без следа на диске; пустой или
///    «одни [?]» текст — image_no_text; EXIF-поворот доезжает до OCR повернутым; шумная
///    4000×3000 дожимается до 1800 px и в потолок 8 МБ; OCR вне семафора (медленный не
///    блокирует txt-загрузку того же предмета); в логе стора — ни имени файла, ни текста.
///
/// Конвертер в тестах — не настоящий markitdown: «нет такой команды» (null → отказ no_text,
/// детерминировано на обеих ОС) и «echo маркера» (текстовый результат для docx-фикстуры).
/// Проверяется путь стора, а не конвертер. OCR — свой фейк ILocalPageOcr (КИ1a), растр —
/// настоящий SkiaImageRaster (его поведение — не проверяем здесь, проверяем путь стора).
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

    // Ребёнок по умолчанию жив: остальные тесты — про путь стора, а не про пользователей.
    // Тесты гонки с удалением (ревью КИ1b) передают свой предикат.
    private ChildSubjectMaterialsStore Store(
        IConfiguration? config = null,
        ILocalPageOcr? ocr = null,
        IImageRaster? raster = null,
        ILogger<ChildSubjectMaterialsStore>? log = null,
        Func<string, bool>? childExists = null)
    {
        var cfg = config ?? Config();
        return new ChildSubjectMaterialsStore(
            _root,
            new MarkitdownService(cfg, NullLogger<MarkitdownService>.Instance),
            cfg,
            childExists ?? (_ => true),
            ocr ?? new UnusedOcr(),
            raster,
            log ?? NullLogger<ChildSubjectMaterialsStore>.Instance);
    }

    // ── фейки швов (КИ1b) ────────────────────────────────────────────────────────────

    // OCR в некартинковых тестах не вызывается вовсе: вызовался — значит путь стора
    // сломан, и тест кричит сразу.
    private sealed class UnusedOcr : ILocalPageOcr
    {
        public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct) =>
            throw new InvalidOperationException("OCR в этом тесте не вызывается");
    }

    // Фейк-результат с журналом: какие байты и какие метки доехали до OCR; Block — чтобы
    // держать «распознавание» висителем (тест конкурентности).
    private sealed class RecordingOcr(OcrResult result) : ILocalPageOcr
    {
        public List<byte[]> SeenPng { get; } = [];
        public List<string> SeenLabels { get; } = [];
        public TaskCompletionSource<bool>? Block { get; init; }

        public async Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct)
        {
            SeenPng.Add(png.ToArray());
            SeenLabels.Add(info.Label);
            if (Block is not null) await Block.Task;
            return result;
        }
    }

    private sealed class ThrowingOcr : ILocalPageOcr
    {
        public Task<OcrResult> RecognizeAsync(ReadOnlyMemory<byte> png, OcrRequestInfo info, CancellationToken ct) =>
            throw new InvalidOperationException("сбой OCR (фейк)");
    }

    // Растр, который бросает: контракт IImageRaster — отказ ответом, но исключение шва
    // не должно становиться 500 у вызывающего.
    private sealed class ThrowingRaster : IImageRaster
    {
        public RasterProbe? Probe(byte[] data) => throw new InvalidOperationException("сбой растра (фейк)");
        public RasterOutcome Apply(byte[] data, IReadOnlyList<ImageTransformOp> ops, ImageEncodeSpec? encode = null) =>
            throw new InvalidOperationException("сбой растра (фейк)");
        public RasterOutcome Encode(byte[] data, ImageEncodeSpec encode) => throw new InvalidOperationException("сбой растра (фейк)");
        public RasterOutcome ResizeMask(byte[] mask, int width, int height) => throw new InvalidOperationException("сбой растра (фейк)");
        public RasterOutcome EraseMasked(byte[] image, byte[] mask) => throw new InvalidOperationException("сбой растра (фейк)");
    }

    // Логгер-журнал (тот же приём, что RecordingLogger в LocalOcrServiceTests): проверки
    // «в логе нет имени файла и текста» работают по фактическим записям стора.
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    // ── фикстуры картинок (КИ1b) ──────────────────────────────────────────────────────

    // Настоящий декодируемый PNG (Skia): его прочитает и Probe, и Apply боевого растра.
    private static byte[] PngOf(int w, int h)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(240, 240, 240));
            using var paint = new SKPaint { Color = new SKColor(30, 30, 30) };
            canvas.DrawRect(2, 2, w - 4, h - 4, paint);
            canvas.Flush();
        }
        using var pixmap = bitmap.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6));
        return data!.ToArray();
    }

    // Настоящий JPEG с EXIF Orientation (тот же приём, что WithExif в SkiaImageRasterTests):
    // APP1/EXIF вставляется сразу после SOI. Orientation 6 = «показать повёрнутым на 90°».
    private static byte[] JpegWithExif(int w, int h, int orientation)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Red);
            using var paint = new SKPaint { Color = SKColors.Blue, IsAntialias = false };
            canvas.DrawRect(w / 2f, 0, w / 2f, h, paint);
            canvas.Flush();
        }
        byte[] jpeg;
        using (var pixmap = bitmap.PeekPixels())
        using (var data = pixmap.Encode(new SKJpegEncoderOptions(90, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore)))
            jpeg = data!.ToArray();

        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 8, 0, 0, 0 };
        Le16(tiff, 1);                                    // одна запись IFD0
        Le16(tiff, 0x0112); Le16(tiff, 3); Le32(tiff, 1); Le32(tiff, (uint)orientation); // tag/type/count/Orientation
        Le32(tiff, 0);                                    // следующий IFD — нет
        var payload = System.Text.Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var len = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload);
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();

        static void Le16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        static void Le32(List<byte> b, uint v) { for (var i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i))); }
    }

    // «Шумная» картинка: gray8 со случайными пикселями, PNG собран руками (тот же приём,
    // что HugeBlankPng в SkiaImageRasterTests): для шума deflate почти не сжимает — это
    // худший случай размера, и оригинал 4000×3000 (≈12 МБ) ещё влезает в MaxFileBytes.
    private static byte[] NoisyGrayPng(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + w];
            for (var y = 0; y < h; y++)
            {
                row[0] = 0; // PNG filter None
                for (var x = 0; x < w; x++) row[1 + x] = (byte)rnd.Next(256);
                z.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        Be32(ihdr, 0, w); Be32(ihdr, 4, h);
        ihdr[8] = 8; ihdr[9] = 0; // глубина 8 бит, тип 0 — оттенки серого
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be32(len, 0, data.Length); s.Write(len);
            var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            s.Write(typeAndData);
            var crc = new byte[4]; Be32(crc, 0, (int)Crc32(typeAndData)); s.Write(crc);
        }
        static void Be32(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }
        static uint Crc32(byte[] data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }

    // Заголовок реальной картинки из её байтов — для проверок «что доехало до OCR».
    private static (int W, int H) DecodeSize(byte[] image)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(image));
        if (codec is null) throw new InvalidOperationException("фикстура не декодируется Skia");
        return (codec.Info.Width, codec.Info.Height);
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

    // HEIC — вне allow-list сигнатур (ChildAttachmentPolicy.Detect): не PNG/JPEG/WEBP/PDF
    // и не текст (нулевые байты). КИ1b: картинка из allow-list — больше не unsupported_type,
    // её ветка — ниже; этот код остаётся за чужими форматами.
    [Fact]
    public async Task HeicОтказанUnsupportedType_БезФайловНаДиске()
    {
        var store = Store();
        var heic = new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63, 0x00, 0x00, 0x00, 0x00, 0x68, 0x65, 0x69, 0x63, 0x6D, 0x69, 0x66, 0x31 };

        var res = await store.AddAsync("kid-a", "math", "page.heic", heic, "admin-1");
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

    // ── КИ1b: картинка страницы (разрез §9) ──────────────────────────────────────────

    // Успех: оригинал в files/ как прислан, текст в text/, опись kind «image», preview —
    // первые 600 символов. Метка OCR — серверный id, не имя файла админа.
    [Fact]
    public async Task КартинкаУспех_ОригиналИТекстЗаписаны_KindImage_PreviewПервые600()
    {
        var png = PngOf(40, 30);
        var text = new string('а', 300) + " " + new string('б', 301); // 634 символа
        var ocr = new RecordingOcr(OcrResult.Success(text, "qwen3.8-27b", 120, 10, 20));
        var store = Store(ocr: ocr, raster: new SkiaImageRaster());

        var res = await store.AddAsync("kid-a", "math", "страница.png", png, "admin-1");
        res.Accepted.Should().BeTrue(res.Code);

        var m = (await store.ListAsync("kid-a", "math")).Single();
        m.Kind.Should().Be(ChildSubjectMaterialsStore.KindImage);
        m.Id.Should().MatchRegex("^m_[0-9a-f]{12}$");
        m.StoredName.Should().Be(m.Id + ".png");
        m.TextName.Should().Be(m.Id + ".txt");
        m.Bytes.Should().Be(png.LongLength);

        // По одному файлу под описью: оригинал — дословно то, что прислали (не дожатая
        // уменьшенная копия), текст — то, что распознал OCR.
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "files")).Should().HaveCount(1);
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "text")).Should().HaveCount(1);
        File.ReadAllBytes(Path.Combine(SubjectDir("kid-a", "math"), "files", m.StoredName)).Should().Equal(png);
        (await store.ReadTextAsync("kid-a", "math", m.Id)).Should().Be(text);

        res.Preview.Should().HaveLength(600);
        res.Preview.Should().Be(text[..600]);

        ocr.SeenLabels.Should().Equal(m.Id); // метка — id, не «страница.png»
    }

    // Каждый из шести отказов OCR (Disabled, LocalUnavailable, BusyTimeout, PageTimeout,
    // TooLarge, BadResponse) — для стора один код ocr_unavailable, и на диске ничего нет:
    // распознавание идёт до всякой записи, полусостояния не существует.
    [Fact]
    public async Task КартинкаOcrОтказалПоЛюбойПричине_OcrUnavailable_НаДискеНичего()
    {
        var png = PngOf(40, 30);
        foreach (OcrFailureReason reason in Enum.GetValues<OcrFailureReason>())
        {
            var subject = $"s-{(int)reason}";
            var store = Store(ocr: new RecordingOcr(OcrResult.Fail(reason, "отказ (фейк)")), raster: new SkiaImageRaster());

            var res = await store.AddAsync("kid-a", subject, "page.png", png, "admin-1");

            res.Accepted.Should().BeFalse($"причина {reason}");
            res.Code.Should().Be(ChildSubjectMaterialsStore.CodeOcrUnavailable, $"причина {reason}");
            (await store.ListAsync("kid-a", subject)).Should().BeEmpty($"причина {reason}");
            Directory.Exists(SubjectDir("kid-a", subject)).Should().BeFalse($"причина {reason}");
        }
    }

    // Нет IImageRaster (вертикаль Images отключаемая) — картинка отказана как
    // ocr_unavailable: нечем нормализовать ориентацию и размер; OCR до этого не доходит.
    [Fact]
    public async Task КартинкаБезРастра_OcrUnavailable_ОcrНеВызван_НаДискеНичего()
    {
        var store = Store(); // ocr — UnusedOcr: вызовётся — тест кричит
        var res = await store.AddAsync("kid-a", "math", "page.png", PngOf(40, 30), "admin-1");

        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeOcrUnavailable);
        (await store.ListAsync("kid-a", "math")).Should().BeEmpty();
        Directory.Exists(SubjectDir("kid-a", "math")).Should().BeFalse();
    }

    // Сигнатура — картинка, а растр её не прочитал (битый файл): это сбой растра, тот же
    // исход, что отказ OCR (замена прежнего теста «картинка — unsupported_type»: с КИ1b
    // unsupported_type остаётся за сигнатурами вне allow-list, см. HeicОтказан).
    [Fact]
    public async Task КартинкаБитаяРастрНеПрочитал_OcrUnavailable_НаДискеНичего()
    {
        var store = Store(raster: new SkiaImageRaster());
        var broken = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };

        var res = await store.AddAsync("kid-a", "math", "page.png", broken, "admin-1");

        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeOcrUnavailable);
        Directory.Exists(SubjectDir("kid-a", "math")).Should().BeFalse();
    }

    // Исключение шва (не отказ-результат) наружу не выходит — тот же ocr_unavailable.
    [Fact]
    public async Task КартинкаOcrИлиРастрБросают_OcrUnavailable_ИсключениеНаружуНеВыходит()
    {
        var throwingOcr = Store(ocr: new ThrowingOcr(), raster: new SkiaImageRaster());
        var r1 = await throwingOcr.AddAsync("kid-a", "s-ocr", "page.png", PngOf(40, 30), "admin-1");
        r1.Code.Should().Be(ChildSubjectMaterialsStore.CodeOcrUnavailable);

        var throwingRaster = Store(raster: new ThrowingRaster());
        var r2 = await throwingRaster.AddAsync("kid-a", "s-raster", "page.png", PngOf(40, 30), "admin-1");
        r2.Code.Should().Be(ChildSubjectMaterialsStore.CodeOcrUnavailable);

        Directory.Exists(SubjectDir("kid-a", "s-ocr")).Should().BeFalse();
        Directory.Exists(SubjectDir("kid-a", "s-raster")).Should().BeFalse();
    }

    // «Текста нет» (разрез §9): пустой текст или одни [?] с пробелами — image_no_text,
    // парный pdf_no_text, а не ocr_unavailable. Пустой текст — исход пустой страницы
    // локальной модели (defect 1e017cf7: content=null доезжает пустой строкой).
    [Fact]
    public async Task КартинкаПустойТекстИлиТолькоМаркеры_ImageNoText_НаДискеНичего()
    {
        foreach (var text in new[] { "", "   ", "[?]  [?]\n[?]", "[?]" })
        {
            var subject = "s-" + text.GetHashCode();
            var store = Store(ocr: new RecordingOcr(OcrResult.Success(text, "qwen3.8-27b", 5, 1, 1)), raster: new SkiaImageRaster());

            var res = await store.AddAsync("kid-a", subject, "page.png", PngOf(40, 30), "admin-1");

            res.Code.Should().Be(ChildSubjectMaterialsStore.CodeImageNoText, $"текст «{text}»");
            (await store.ListAsync("kid-a", subject)).Should().BeEmpty();
            Directory.Exists(SubjectDir("kid-a", subject)).Should().BeFalse();
        }
    }

    // Предел оригинала проверяется до растра и OCR: дорогая работа не делается для
    // заведомо отказанного файла.
    [Fact]
    public async Task КартинкаБольшеMaxFileBytes_ОтказTooLarge_ДоРастраИОcr()
    {
        var ocr = new RecordingOcr(OcrResult.Success("x", "m", 1, 1, 1));
        var store = Store(ocr: ocr, raster: new SkiaImageRaster());
        var big = new byte[21 * 1024 * 1024];
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Array.Copy(magic, big, magic.Length); // сигнатура PNG

        var res = await store.AddAsync("kid-a", "math", "big.png", big, "admin-1");

        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeTooLarge);
        ocr.SeenPng.Should().BeEmpty();
        Directory.Exists(SubjectDir("kid-a", "math")).Should().BeFalse();
    }

    // Метр проверяется после OCR: пока страница распознавалась, предмет мог заполниться
    // другой загрузкой — картинка при этом не пишется вовсе.
    [Fact]
    public async Task КартинкаПредметЗаполненПокаИдётOcr_ImageНеЗаписан()
    {
        var store = Store(
            Config(new Dictionary<string, string?> { ["ChildMode:Materials:MaxSubjectBytes"] = "920" }),
            ocr: new RecordingOcr(OcrResult.Success("текст страницы", "qwen3.8-27b", 5, 1, 1)),
            raster: new SkiaImageRaster());

        var txt = await store.AddAsync("kid-a", "math", "урок.txt", Encoding.UTF8.GetBytes(new string('x', 900)), "admin-1");
        txt.Accepted.Should().BeTrue(txt.Code);

        var res = await store.AddAsync("kid-a", "math", "page.png", PngOf(40, 30), "admin-1");
        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeSubjectFull);

        (await store.ListAsync("kid-a", "math")).Should().HaveCount(1);
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "files")).Should().HaveCount(1); // только txt-оригинал
        Directory.GetFiles(Path.Combine(SubjectDir("kid-a", "math"), "text")).Should().HaveCount(1);
    }

    // Конкурентность (критерий c3): OCR (до минут) идёт ВНЕ семафора предмета — пока
    // фейк висит на TaskCompletionSource, txt-загрузка того же предмета завершается.
    // Мутации: перенести RecognizeAsync под sem.WaitAsync в AddImageAsync или писать
    // опись/файлы до OCR — краснеет этот тест (txt ждёт семафор / полусостояние).
    [Fact]
    public async Task Add_TxtNotBlockedBySlowOcr()
    {
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new RecordingOcr(OcrResult.Success("текст страницы", "qwen3.8-27b", 5, 1, 1)) { Block = block };
        var store = Store(ocr: ocr, raster: new SkiaImageRaster());

        var imageTask = Task.Run(() => store.AddAsync("kid-a", "math", "page.png", PngOf(40, 30), "admin-1"));

        // Сигнал: распознавание началось и держится на Block.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (ocr.SeenPng.Count == 0)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("фейк-OCR не вызван: путь картинки не дошёл до распознавания");
            await Task.Delay(25);
        }

        // Пока OCR висит, тот же предмет должен принимать другие загрузки.
        var txtTask = Task.Run(() => store.AddAsync("kid-a", "math", "урок.txt", Encoding.UTF8.GetBytes("урок"), "admin-1"));
        var winner = await Task.WhenAny(txtTask, Task.Delay(5000));
        winner.Should().Be(txtTask, "txt-загрузка ждёт медленный OCR: распознавание под семафором предмета");
        (await txtTask).Accepted.Should().BeTrue();

        block.SetResult(true);
        var img = await imageTask;
        img.Accepted.Should().BeTrue(img.Code);

        // Конечное состояние полного цикла: обе загрузки на месте, порядок — по факту.
        var list = await store.ListAsync("kid-a", "math");
        list.Should().HaveCount(2);
        list.Select(m => m.Kind).Should().Contain(ChildSubjectMaterialsStore.KindText)
            .And.Contain(ChildSubjectMaterialsStore.KindImage);
    }

    // Повернутое по EXIF фото уходит в OCR повернутым: AutoOrient делает растр на входе,
    // метр (когда он нужен) мерится по ориентированным сторонам. 500×300 + Orientation 6
    // → то, что видит человек, 300×500: ширина меньше высоты.
    [Fact]
    public async Task КартинкаПовернутаяПоExif_ДоезжаетДоОcrПовернутой()
    {
        var jpeg = JpegWithExif(500, 300, orientation: 6);
        var ocr = new RecordingOcr(OcrResult.Success("текст", "qwen3.8-27b", 5, 1, 1));
        var store = Store(ocr: ocr, raster: new SkiaImageRaster());

        var res = await store.AddAsync("kid-a", "math", "фото.jpg", jpeg, "admin-1");
        res.Accepted.Should().BeTrue(res.Code);

        var m = res.Material!;
        m.StoredName.Should().Be(m.Id + ".jpg"); // расширение — по формату оригинала
        File.ReadAllBytes(Path.Combine(SubjectDir("kid-a", "math"), "files", m.StoredName)).Should().Equal(jpeg);

        var (w, h) = DecodeSize(ocr.SeenPng.Single());
        w.Should().BeLessThan(h, "EXIF Orientation 6: в OCR доезжает повернутая картинка");
    }

    // Шумная 4000×3000: дожатая копия для OCR — с длинной стороной ≤ OcrMaxSide и в
    // потолке LocalOcrService (8 МБ): арифметика §9. Оригинал при этом не трогается.
    [Fact]
    public async Task КартинкаШумная4000x3000_ДожатаДо1800ИПотолкаOcr()
    {
        var noisy = NoisyGrayPng(4000, 3000, seed: 20261118);
        noisy.LongLength.Should().BeLessThan(ChildSubjectMaterialsStore.DefaultMaxFileBytes, "фикстура — в пределе оригинала");

        var ocr = new RecordingOcr(OcrResult.Success("текст", "qwen3.8-27b", 5, 1, 1));
        var store = Store(ocr: ocr, raster: new SkiaImageRaster());

        var res = await store.AddAsync("kid-a", "math", "huge.png", noisy, "admin-1");
        res.Accepted.Should().BeTrue(res.Code);

        var sent = ocr.SeenPng.Single();
        sent.LongLength.Should().BeLessOrEqualTo(
            (int)ClaudeHomeServer.Services.Llm.Gateway.LocalOcrService.MaxImageBytes,
            $"шумная картинка после ресайза: {sent.LongLength} байт");
        var (w, h) = DecodeSize(sent);
        Math.Max(w, h).Should().BeLessOrEqualTo(ChildSubjectMaterialsStore.OcrMaxSide);

        // Оригинал — тот, что прислали (4000×3000), не дожатая копия.
        File.ReadAllBytes(Path.Combine(SubjectDir("kid-a", "math"), "files", res.Material!.StoredName)).Should().Equal(noisy);
    }

    // Лог — без имени файла и без текста (разрез §9, метка — id): ни подпись админа, ни
    // распознанный текст не должны просачиваться в записи стора ни на одном шаге.
    [Fact]
    public async Task КартинкаЗагружена_ВЛогеСтораНетИмениФайлаНиТекста()
    {
        var log = new RecordingLogger<ChildSubjectMaterialsStore>();
        var marker = "МАРКЕР-ТЕКСТ-СТРАНИЦЫ-9f3a";
        var ocr = new RecordingOcr(OcrResult.Success(marker + " и ещё текст", "qwen3.8-27b", 5, 1, 1));
        var store = Store(ocr: ocr, raster: new SkiaImageRaster(), log: log);

        var res = await store.AddAsync("kid-a", "math", "секрет_имя.png", PngOf(40, 30), "admin-1");
        res.Accepted.Should().BeTrue(res.Code);

        log.Entries.Should().NotBeEmpty("успешная загрузка логируется агрегатами");
        log.Entries.Should().NotContain(e => e.Message.Contains("секрет_имя"),
            string.Join(" | ", log.Entries.Select(e => e.Message)));
        log.Entries.Should().NotContain(e => e.Message.Contains(marker),
            string.Join(" | ", log.Entries.Select(e => e.Message)));
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

    // Ревью К2a: DeleteChildAsync без семафоров предмета проигрывал гонку с идущим
    // AddAsync — снос приходил посреди записи, и AddAsync дописывал опись/файлы ПОСЛЕ
    // Directory.Delete: каталог воскресал после сноса. Здесь AddAsync стоит в конвертере
    // (он вызывается внутри семафора, после того как оригинал лег на диск): тест ждёт
    // сигнала «конвертер запущен», поднимает снос, отпускает конвертер и проверяет
    // конечное состояние — каталога нет.
    //
    // Мутация: убрать семафоры предметов из DeleteChildAsync — краснеет этот тест:
    // снос проходит до окончания AddAsync, опись и файлы воссоздаются после него.
    //
    // Конвертер — тестовый скрипт, ждущий файл-отметку (тот же приём платформы, что
    // EchoMarkerConfig выше): детерминирован на обеих ОС, в отличие от «sleep» с
    // чужим аргументом пути.
    [Fact]
    public async Task УдалениеРебёнкаЖдётИдущийAddAsync_КаталогНеВоскресает()
    {
        var startedFlag = Path.Combine(_tempDir, "ccs-converter-started");
        var releaseFlag = Path.Combine(_tempDir, "ccs-converter-release");
        var script = Path.Combine(_tempDir, OperatingSystem.IsWindows() ? "ccs-wait-converter.bat" : "ccs-wait-converter.sh");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(script,
                $"@echo off\r\necho started> \"{startedFlag}\"\r\n:loop\r\n" +
                $"if exist \"{releaseFlag}\" (echo DOCX-MARKER&exit /b 0)\r\ntimeout /t 1 /nobreak >nul\r\ngoto loop\r\n");
        }
        else
        {
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                $"echo started > '{startedFlag}'\n" +
                $"while [ ! -e '{releaseFlag}' ]; do sleep 0.05; done\n" +
                "echo DOCX-MARKER\n");
        }

        var store = Store(Config(new Dictionary<string, string?>
        {
            ["Markitdown:Command"] = OperatingSystem.IsWindows() ? $"cmd /c {script}" : $"bash {script}",
        }));

        var addTask = Task.Run(() => store.AddAsync("kid-a", "math", "учебник.pdf", PdfBytes, "admin-1"));

        // Сигнал: конвертер запущен — значит AddAsync внутри семафора предмета и оригинал
        // уже на диске (порядок AddAsync: оригинал → ConvertAsync → опись).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(startedFlag))
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("Тестовый конвертер не запустился: фикстура неработает");
            await Task.Delay(50);
        }

        var deleteTask = store.DeleteChildAsync("kid-a");

        // Отпускаем конвертер: AddAsync дописывает опись и освобождает семафор; снос — за ним.
        File.WriteAllText(releaseFlag, "go");

        var add = await addTask;
        await deleteTask;

        add.Accepted.Should().BeTrue(add.Code);
        // Конечное состояние полного цикла: снос был ПОСЛЕ записи — каталога нет.
        // Без семафоров в DeleteChildAsync краснеет: снос шёл посреди AddAsync, опись и
        // файлы воссоздались после него.
        Directory.Exists(Path.Combine(_root, "kid-a")).Should().BeFalse();
    }

    // Ревью КИ1b (major): OCR идёт ВНЕ семафора до минут, а снос берёт семафоры только УЖЕ
    // существующих каталогов предметов. Первая страница предмета: каталога нет, ждать
    // нечего, снос — no-op; вернувшийся OCR писал бы файлы и опись после сноса, и фото
    // страницы с текстом лежали бы на диске без владельца. Запись под семафором проверяет
    // живого владельца → child_gone, на диске ничего.
    //
    // Мутация: убрать ChildAlive из AddImageAsync — краснеет этот тест: опись и файлы
    // дописаны после сноса, каталог ребёнка воскресает.
    [Fact]
    public async Task УдалениеРебёнкаПокаИдётOcr_КаталогНеВоскресает_ChildGone()
    {
        var alive = new HashSet<string>(StringComparer.Ordinal) { "kid-a" };
        var block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new RecordingOcr(OcrResult.Success("текст страницы", "qwen3.8-27b", 5, 1, 1)) { Block = block };
        var store = Store(ocr: ocr, raster: new SkiaImageRaster(), childExists: id => alive.Contains(id));

        var imageTask = Task.Run(() => store.AddAsync("kid-a", "math", "page.png", PngOf(40, 30), "admin-1"));

        // Сигнал: распознавание началось и держится на Block — до записи ещё далеко.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (ocr.SeenPng.Count == 0)
        {
            if (DateTime.UtcNow > deadline) Assert.Fail("фейк-OCR не вызван: путь картинки не дошёл до распознавания");
            await Task.Delay(25);
        }
        Directory.Exists(SubjectDir("kid-a", "math")).Should().BeFalse(
            "каталога первой загрузки ещё нет — снос не возьмёт его семафор");

        // Каскад удаления пользователя: его нет в стору → снос каталога (нет и его — no-op).
        alive.Remove("kid-a");
        await store.DeleteChildAsync("kid-a");

        // OCR вернулся ПОСЛЕ сноса: писать уже некому.
        block.SetResult(true);
        var res = await imageTask;
        res.Accepted.Should().BeFalse();
        res.Code.Should().Be(ChildSubjectMaterialsStore.CodeChildGone);

        // Конечное состояние полного цикла: каталог ребёнка не появился.
        Directory.Exists(Path.Combine(_root, "kid-a")).Should().BeFalse();
    }

    // Тот же шов закрывает ту же гонку обычной загрузки: AddAsync проверяет живого владельца
    // первой же проверкой под семафором — каталог первой загрузки не создаётся после
    // удаления ребёнка. Мутация: убрать ChildAlive из AddAsync — краснеет этот тест.
    [Fact]
    public async Task УдалённыйРебёнок_ЗагрузкаНеСоздаётКаталог_ChildGone()
    {
        var store = Store(childExists: _ => false);

        var txt = await store.AddAsync("kid-a", "math", "урок.txt", Encoding.UTF8.GetBytes("урок"), "admin-1");
        txt.Code.Should().Be(ChildSubjectMaterialsStore.CodeChildGone);

        Directory.Exists(Path.Combine(_root, "kid-a")).Should().BeFalse();
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
