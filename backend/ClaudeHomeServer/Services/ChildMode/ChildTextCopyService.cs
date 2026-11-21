using System.Text;
using System.Threading.Channels;
using ClaudeHomeServer.Services.Docs;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Общий писатель загрузки в папку предмета (ADR-041 ред. 6, решения 56/89/90; разрез §11.4
/// «Загрузка»). Единственный, кто кладёт в папку предмета оригинал и его текстовую копию: его
/// зовёт `upload` рельсы (Б11b), ручка №8 урока (Б11b2) и OCR картинки (Б13c). Второго писателя
/// той же папки ни у одной ручки нет: тип, размер, метр и форма копии решаются здесь один раз.
///
/// Тип — по сигнатуре байтов (ChildAttachmentPolicy.DetectForUpload: картинки, PDF,
/// docx/xlsx/pptx без макросов, текст); расширение сохраняемого имени приводится к нему
/// (NormalizeExtension — тот же возврат ревью R-KM-B6e: downstream читает тип по расширению).
/// Форма копии — только ChildPathPolicy.TextCopyOf (решение 90): второй формы копии здесь
/// нет вовсе — ни своей, ни подстановкой расширения. Копию получают PDF и офисные файлы; текстовый
/// оригинал сам есть текст, картинки — нет (их копию пишет OCR, Б13c).
///
/// Пределы: файл — maxBytes вызывающего (для рельсы — maxAttachmentBytes, решение 56); папка —
/// ChildMode:Projects:MaxSubjectBytes (200 МБ). В счёт папки входит всё её содержимое, включая
/// .ccs-trash/ (снимки занимают то же место) и .ccs-text/ (копии занимают то же место).
/// Неразборное или неположительное значение конфига — дефолт инстанса, не отказ.
/// Перезапись — не рост объёма: при подмене файла по тому же пути размер прежнего файла и его
/// прежней копии (оба входят в счёт) вычитается из текущего объёма до проверки потолка —
/// перезаливка того же файла у потолка даёт 200, а не ложный 413 (ревью 49941fdc, п.2);
/// дельта сверх потолка по-прежнему отказывается.
///
/// Конкурентность: метр папки и запись — под одной блокировкой по каноническому пути
/// subjectRoot (словарь семафоров по ключу, не по экземпляру писателя или контроллера: две
/// загрузки одного предмета не должны разойтись мимо потолка). Конвертация — под тем же
/// замком: сбой внешнего конвертера — часть той же операции, а не окно, в котором метр
/// протухает. Пустая коллекция и ровно-на-границе здесь решены: used + bytes &gt; max — отказ,
/// ровно равное — пройдено; пустая папка даёт used = 0.
///
/// Копия снимается до дорогой работы, а не после неё (ревью Б11b п.1): и для PDF/офиса —
/// перед записью новой версии, и для картинки — при её постановке в очередь. Перезапись файла
/// содержимым без текста (или сбой конвертера/OCR) не должна оставлять прежнюю копию:
/// модель читает прежний текст нового файла. «копии нет» — честное состояние.
///
/// Запись — staging-файлом рядом с оригиналом и подменой атомарным File.Move только после
/// успеха проверки (доработка Б11b2, ревью ac7abe2d п.2): при requireText неудачная
/// перезапись не трогает то, что у ребёнка уже было — прежний файл и его копия остаются,
/// временный файл удалён. Стагинг не проходит метр папки: он ровно байты принятого файла,
/// а метр уже зарезервирован под тем же замком. Имя staging не разбирается типом:
/// NormalizeExtension — инъекция по типу (существующий файл даёт свой тип), .ccs-staging
/// не коллидирует ни с одним именем результата.
///
/// Картинка, записанная здесь, ставится в очередь её текстовой копии (Б13c): очередь —
/// работа писателя, она живёт здесь; единственный исполнитель очереди —
/// ChildImageOcrBackfill (тот же асембл), он зовёт OCR и возвращает результат в
/// WriteTextCopyAsync. Второго писателя .ccs-text/ нет и для картинок.
///
/// Отказ — результат с кодом, полусостояния нет: файл без копии возможен (textCopy=false —
/// он прочитается как есть), копии без файла — никогда. При requireText оригинал без текста
/// снимается этим же вызовом (полуфайла в папке не остаётся, решение 89); без него файл
/// остаётся, ответа об отказе нет. Наружу не выходит ни отказа диском, ни сбоя конвертера;
/// ArgumentException на пустых аргументах и выход пути за корень — ошибка вызывающего
/// (путь обязан был пройти ChildPathPolicy), а не отказ загрузки.
/// </summary>
// Не sealed: тесту ручки нужна подмена писателя, который бросает на SaveAsync (ветка
// внешнего catch контроллера) — тот же приём, что подмена конфига в том же тесте.
public class ChildTextCopyService(
    MarkitdownService markitdown,
    IConfiguration config,
    ILogger<ChildTextCopyService>? log = null)
{
    // Ключ метра папки предмета: ключ — константа, значение — TryParse с дефолтом инстанса.
    public const string ConfigMaxSubjectBytesKey = "ChildMode:Projects:MaxSubjectBytes";
    public const long DefaultMaxSubjectBytes = 200 * 1024 * 1024;

    // Коды отказа — их читает вызывающая ручка (413/415/422).
    public const string CodeTooLarge = "too_large";
    public const string CodeFolderLimit = "folder_limit";
    public const string CodeUnsupportedType = "unsupported_type";
    // «Текста нет» при requireText (ручка №8): тип отказа общий для PDF и офиса — ручка
    // переводит его своим 422, различие вида видено в Kind результата.
    public const string CodeNoText = "no_text";
    // Имя занято каталогом (тот же 409 «exists», что у create/rename/upload взрослого):
    // файл в каталог не пишется, ничего не создаётся и не сносится.
    public const string CodeExists = "exists";
    // Сбой диска или неразрешимый корень — не код ручки (наружу ему место в 503).
    public const string CodeStorageError = "storage_error";

    // Виды результата (те же строки, что в описи материалов и в ответе ручки №8): строка
    // результата и расширение файла не расходятся — downstream читает тип по ним обоим.
    public const string KindImage = "image";
    public const string KindPdf = "pdf";
    public const string KindOffice = "office";
    public const string KindText = "text";

    private readonly ILogger<ChildTextCopyService>? _log = log;

    /// <summary>
    /// Сохранить файл в папку предмета: тип по сигнатуре → размер файла → метр папки →
    /// запись → текстовая копия (PDF и офис — по TextCopyOf).
    /// rel — относительный путь, уже прошедший ChildPathPolicy вызывающего: из его вердикта
    /// писатель не исходит («Материалы/», «Для репетитора/» и защищённые имена решает
    /// граница, не писатель). maxBytes — лимит файла, который называет вызывающий (для
    /// рельсы — maxAttachmentBytes). requireText — режим ручки №8: файла без текста не будет.
    /// ct — токен вызывающего: доходит до конвертера.
    /// </summary>
    public virtual async Task<ChildSaveResult> SaveAsync(
        string subjectRoot, string rel, byte[] bytes, long maxBytes, bool requireText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(subjectRoot))
            throw new ArgumentException("Папка предмета не задана", nameof(subjectRoot));
        if (string.IsNullOrWhiteSpace(rel))
            throw new ArgumentException("Путь файла не задан", nameof(rel));

        var kind = ChildAttachmentPolicy.DetectForUpload(bytes);
        if (kind is ChildAttachmentPolicy.Kind.Unsupported)
            return ChildSaveResult.Refused(CodeUnsupportedType);

        if (bytes.LongLength > maxBytes)
            return ChildSaveResult.Refused(CodeTooLarge);

        var root = SafePath.Canonical(subjectRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(root))
        {
            // Корень не разобрать — тот же исход, что сбой диска: ничего не пишется.
            _log?.LogWarning("Детский режим: папка предмета не читается при загрузке ({Root})", subjectRoot);
            return ChildSaveResult.Refused(CodeStorageError);
        }

        // Расширение — по определённому типу: kind ответа и файл на диске не расходятся
        // (NormalizeExtension). Имя остаётся именем клиента, меняется только расширение.
        // Путь считается до замка: NormalizeExtension и TextCopyOf — чистые вычисления по
        // байтам и имени, диска не касаются; путь нужен метру — при перезаписи размер
        // прежнего файла по тому же пути вычитается из текущего объёма.
        var saveRel = ChildAttachmentPolicy.NormalizeExtension(rel, kind, bytes);
        var originalAbs = SafePath.Join(root, saveRel);
        var copyAbs = kind is ChildAttachmentPolicy.Kind.Pdf or ChildAttachmentPolicy.Kind.Office
            ? SafePath.Join(root, ChildPathPolicy.TextCopyOf(saveRel))
            : null;

        var sem = LockFor(root);
        await sem.WaitAsync(ct);
        try
        {
            var used = SumBytes(root);
            if (used < 0) return ChildSaveResult.Refused(CodeStorageError);

            // Перезапись — не рост объёма: прежний файл по тому же пути и его прежняя копия
            // (оба входят в метр) будут заменены новым содержимым, их размер вычитается
            // до проверки потолка — иначе перезаливка того же файла у потолка даёт ложный
            // 413 (ревью 49941fdc, п.2). Вычитание — под тем же замком: чтение прежнего
            // размера и подмена в одной критической секции, две параллельные загрузки
            // одного предмета не могут пройти проверку, не видя записи друг друга.
            used -= FileSizeOrZero(originalAbs);
            if (copyAbs is not null) used -= FileSizeOrZero(copyAbs);

            if (used + bytes.LongLength > MaxSubjectBytes())
                return ChildSaveResult.Refused(CodeFolderLimit);

            // Имя занято каталогом — отказ до любого эффекта (тот же 409 «exists», что у
            // записи взрослого): файл в каталог не пишется, подпапку копии не создаём.
            // Проверяются и имя, которое назвал клиент, и имя после нормализации расширения:
            // «папка» без расширения для текстового типа — тот же занятый каталог.
            if (Directory.Exists(SafePath.Join(root, rel)) || Directory.Exists(originalAbs))
                return ChildSaveResult.Refused(CodeExists);

            // Доработка Б11b2 (ревью ac7abe2d п.2): файл пишется staging-файлом рядом с
            // оригиналом и подменяет его атомарным Move только после успеха проверки.
            // При отказе оригинал и его копия не тронуты — неудачная загрузка не стирает то,
            // что у ребёнка уже было. Стагинг — под тем же замком корня, своего не заводим.
            var stagedAbs = originalAbs + ".ccs-staging";
            var staged = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(originalAbs)!);
                File.WriteAllBytes(stagedAbs, bytes);
                staged = true;
            }
            catch (Exception e)
            {
                _log?.LogWarning(e, "Детский режим: файл не записан в папку предмета ({Rel})", saveRel);
                if (staged) TryDelete(stagedAbs);
                return ChildSaveResult.Refused(CodeStorageError);
            }

            if (copyAbs is null)
            {
                // Текстовый оригинал и картинка: проверки нет — стейнинг сразу становится
                // оригиналом. Картинка: своей копии при загрузке нет — её пишет OCR (Б13c);
                // записанная здесь картинка ставится в очередь копиюписателя сразу после
                // подмены: старая копия прежнего содержимого снимается там же (ревью Б11b п.1),
                // и с этого момента модель не читает прежний текст.
                if (!TryReplace(stagedAbs, originalAbs))
                {
                    _log?.LogWarning("Детский режим: файл не подменён в папке предмета ({Rel})", saveRel);
                    TryDelete(stagedAbs);
                    return ChildSaveResult.Refused(CodeStorageError);
                }
                if (kind is ChildAttachmentPolicy.Kind.Image)
                    EnqueueImageOcr(root, saveRel);
                return ChildSaveResult.Saved(saveRel, KindOf(kind), textCopy: false);
            }

            // Тот же путь, что у ручки №8 и каталога материалов: MarkitdownService по
            // абсолютному пути файла — здесь стейнинга: оригинал при отказе не тронут.
            // null или «не текст» (скан/битый) — текста нет.
            string? text;
            try
            {
                text = await markitdown.ConvertAsync(stagedAbs, ct);
            }
            catch (Exception e)
            {
                // Контракт конвертера — null на любой ошибке; исключение шва не становится
                // 500: тот же исход, что null.
                _log?.LogWarning(e, "Детский режим: сбой конвертера при загрузке в папку предмета ({Rel})", saveRel);
                text = null;
            }

            if (text is null || !ChildAttachmentPolicy.IsUsableText(Encoding.UTF8.GetBytes(text)))
            {
                if (requireText)
                {
                    // Ручка №8 (решение 89 + Б11b2): полуприложения нет — стейнинг снят, а
                    // прежний файл и его копия, если они были, остаются как были: отказа
                    // для ребёнка не было.
                    TryDelete(stagedAbs);
                    return ChildSaveResult.Refused(CodeNoText);
                }
                // Рельса: файл прочитается как есть, копии нет — это не отказ. Ревью Б11b п.1:
                // старая копия под тем же замком снимается при подмене новой версии —
                // «копии нет» — честное состояние, а не старая копия нового файла.
                if (!TryReplace(stagedAbs, originalAbs))
                {
                    _log?.LogWarning("Детский режим: файл не подменён в папке предмета ({Rel})", saveRel);
                    TryDelete(stagedAbs);
                    return ChildSaveResult.Refused(CodeStorageError);
                }
                TryDelete(copyAbs);
                return ChildSaveResult.Saved(saveRel, KindOf(kind), textCopy: false);
            }

            // Проверка пройдена: стейнинг становится оригиналом (атомарная подмена),
            // новая копия пишется после неё — «копия не вышла» не отменяет файл, как и было.
            if (!TryReplace(stagedAbs, originalAbs))
            {
                _log?.LogWarning("Детский режим: файл не подменён в папке предмета ({Rel})", saveRel);
                TryDelete(stagedAbs);
                return ChildSaveResult.Refused(CodeStorageError);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copyAbs)!);
                File.WriteAllBytes(copyAbs, Encoding.UTF8.GetBytes(text));
            }
            catch (Exception e)
            {
                // Копия не записана — оригинал остаётся: «копия не вышла» не отменяет файл
                // (тот же исход, что отказ конвертера). Полусостояния нет: копии нет вовсе.
                _log?.LogWarning(e, "Детский режим: текстовая копия не записана ({Rel})", saveRel);
                TryDelete(copyAbs);
                return ChildSaveResult.Saved(saveRel, KindOf(kind), textCopy: false);
            }

            return ChildSaveResult.Saved(saveRel, KindOf(kind), textCopy: true);
        }
        finally
        {
            sem.Release();
        }
    }

    // ── очередь текстовых копий картинок (Б13c) ────────────────────────────────────────
    //
    // Работа писателя живёт у писателя: картинка, записанная этим классом, нуждается в
    // копии, и список того, что ждёт OCR, — здесь же. Единственный исполнитель очереди —
    // ChildImageOcrBackfill (тот же асембл): он берёт задачу, зовёт OCR и возвращает
    // результат в WriteTextCopyAsync — второго писателя .ccs-text/ нет и для картинок.
    // Очередь — в памяти процесса: после рестарта её заново наполняют загрузка и стартовый
    // проход исполнителя (тот же приём, что у CharacterPhotoLabeling, ADR-027 §9).
    // Дедуп — по каноническому абсолютному пути оригинала: та же картинка, поставленная
    // дважды (загрузка + стартовый проход), даёт одну задачу и один вызов OCR. Снятие с
    // дедупа — до работы: перезапись файла во время его распознавания поставит новый заход
    // (тот же порядок, что у _pending у CharacterPhotoLabeling).

    internal sealed record OcrJob(string SubjectRoot, string Rel);

    private readonly Channel<OcrJob> _ocrJobs = Channel.CreateUnbounded<OcrJob>();
    private readonly HashSet<string> _ocrPending = new(StringComparer.Ordinal);

    /// <summary>
    /// Поставить картинку папки предмета в очередь её текстовой копии (Б13c): дедуп по
    /// каноническому пути оригинала; старая копия под тем же замком снимается здесь же —
    /// до OCR, не после (ревью Б11b п.1). true — новая задача в очереди; false — уже стоит
    /// или корень не разбирается (тот же storage-исход, что у SaveAsync: только лог).
    /// Зовётся SaveAsync сразу после записи картинки и стартовым проходом исполнителя.
    /// </summary>
    public bool EnqueueImageOcr(string subjectRoot, string rel)
    {
        if (string.IsNullOrWhiteSpace(subjectRoot))
            throw new ArgumentException("Папка предмета не задана", nameof(subjectRoot));
        if (string.IsNullOrWhiteSpace(rel))
            throw new ArgumentException("Путь файла не задан", nameof(rel));

        var root = SafePath.Canonical(subjectRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(root))
        {
            _log?.LogWarning("Детский режим: папка предмета не читается при постановке картинки в очередь OCR ({Root})", subjectRoot);
            return false;
        }

        // Старая копия прежнего содержимого снимается ровно там, где картинка попала в
        // очередь: с этого момента и до новой копии модель не читает прежний текст.
        TryDelete(SafePath.Join(root, ChildPathPolicy.TextCopyOf(rel)));

        lock (_ocrPending)
        {
            if (!_ocrPending.Add(SafePath.Join(root, rel))) return false;
        }
        _ocrJobs.Writer.TryWrite(new OcrJob(root, rel));
        return true;
    }

    // Единственный читатель очереди — ChildImageOcrBackfill.
    internal ChannelReader<OcrJob> OcrJobs => _ocrJobs.Reader;

    // Снять путь с дедупа — до работы исполнителя: загрузка того же файла во время его
    // распознавания не потерялась бы за уже взятой задачей, а поставила бы новый заход.
    internal void ReleaseOcrJob(OcrJob job)
    {
        lock (_ocrPending) _ocrPending.Remove(SafePath.Join(job.SubjectRoot, job.Rel));
    }

    /// <summary>
    /// Записать текстовую копию картинки по её оригиналу (путь результата OCR, Б13c):
    /// форма копии — TextCopyOf (решение 90), запись — та же, что у PDF и офиса в
    /// SaveAsync, и под тем же замком корня (копия занимает место в папке — метр).
    /// Зовётся только исполнителем OCR (тот же асембл): внешний вызывающий копию не пишет.
    /// null — копия не записана (сбой диска, лог); полусостояния нет: незаписанное до
    /// конца — удалено.
    /// </summary>
    internal async Task<string?> WriteTextCopyAsync(string subjectRoot, string rel, string text)
    {
        var root = SafePath.Canonical(subjectRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(root))
        {
            _log?.LogWarning("Детский режим: папка предмета не читается при записи копии OCR ({Root})", subjectRoot);
            return null;
        }

        var copyRel = ChildPathPolicy.TextCopyOf(rel);
        var sem = LockFor(root);
        await sem.WaitAsync();
        try
        {
            var copyAbs = SafePath.Join(root, copyRel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copyAbs)!);
                File.WriteAllBytes(copyAbs, Encoding.UTF8.GetBytes(text));
                return copyRel;
            }
            catch (Exception e)
            {
                _log?.LogWarning(e, "Детский режим: текстовая копия OCR не записана ({Rel})", copyRel);
                TryDelete(copyAbs);
                return null;
            }
        }
        finally
        {
            sem.Release();
        }
    }

    private static string KindOf(ChildAttachmentPolicy.Kind kind) => kind switch
    {
        ChildAttachmentPolicy.Kind.Image => KindImage,
        ChildAttachmentPolicy.Kind.Pdf => KindPdf,
        ChildAttachmentPolicy.Kind.Office => KindOffice,
        _ => KindText,
    };

    // Сумма байт всего содержимого папки (метрируется до записи). Ошибка обхода — −1:
    // метра нет, и за потолок не пропускаем (fail-closed, тот же приём, что у всей границы).
    // Пустая папка — 0: любое содержимое проходит свой потолок ровно на границе.
    private static long SumBytes(string root)
    {
        try
        {
            long sum = 0;
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                sum += new FileInfo(f).Length;
            return sum;
        }
        catch
        {
            return -1;
        }
    }

    // Размер файла, которого может не быть (прежняя версия перед перезаписью): файла нет — 0,
    // сбой чтения — 0: не вычитаем то, чего не прочитали, метр остаётся консервативным.
    private static long FileSizeOrZero(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private long MaxSubjectBytes() =>
        long.TryParse(config[ConfigMaxSubjectBytesKey], out var bytes) && bytes > 0 ? bytes : DefaultMaxSubjectBytes;

    /// <summary>
    /// Лимит одного файла загрузки — ChildMode:MaxAttachmentBytes (10 МБ): та же величина,
    /// которую ChildLimitsCalculator отдаёт статусу ребёнка и загрузке вложения чата
    /// (решение 56: «размер — maxAttachmentBytes»). Значение держит он; это — то же чтение
    /// ключа для ручки, которой нужен только этот лимит, а не второй источник значения.
    /// </summary>
    public long MaxAttachmentBytes() =>
        long.TryParse(config[ChildLimitsCalculator.ConfigMaxAttachmentBytesKey], out var bytes) && bytes > 0
            ? bytes
            : ChildLimitsCalculator.DefaultMaxAttachmentBytes;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* сиротый файл хуже исходного отказа, чем ещё один отказ */ }
    }

    // Атомарная подмена оригинала стейнингом (File.Move с overwrite=true, .NET 7+): на любой
    // ошибке оригинал остаётся каким был, стейнинг удаляет вызывающий.
    private static bool TryReplace(string stagedAbs, string originalAbs)
    {
        try { File.Move(stagedAbs, originalAbs, overwrite: true); return true; }
        catch { return false; }
    }

    // Замок по каноническому корню предмета, общий для всех экземпляров процесса: метр и
    // запись одного предмета сериализованы. Разные папки друг друга не ждут (словарь по
    // ключу, не один на писателя — тот же приём, что у SubjectLocks стора материалов).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> RootLocks =
        new(StringComparer.Ordinal);

    private static SemaphoreSlim LockFor(string canonicalRoot) =>
        RootLocks.GetOrAdd(canonicalRoot, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Результат SaveAsync: Accepted — файл на диске (Path — относительный путь оригинала под
/// корнем предмета, Kind — image/pdf/office/text, TextCopy — есть ли его копия по
/// TextCopyOf); иначе отказ с кодом (CodeTooLarge, CodeFolderLimit, CodeUnsupportedType,
/// CodeNoText, CodeExists, CodeStorageError).
/// </summary>
public sealed record ChildSaveResult(string? Path, string? Kind, bool TextCopy, string? Code)
{
    public bool Accepted => Code is null && Path is not null;

    public static ChildSaveResult Saved(string path, string kind, bool textCopy) =>
        new(path, kind, textCopy, null);

    public static ChildSaveResult Refused(string code) => new(null, null, false, code);
}
