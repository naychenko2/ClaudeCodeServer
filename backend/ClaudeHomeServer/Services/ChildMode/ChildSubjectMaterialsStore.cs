using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Images.Editing.Raster;
using ClaudeHomeServer.Services.Llm.Gateway;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Каталог материалов предмета (разрез «Знания репетитора» §2, К2a):
/// data/child-subjects/{childId}/{subjectId}/ — materials.json (опись),
/// files/{storedName} (оригинал), text/{textName} (извлечённый текст; читает ТОЛЬКО он).
///
/// Почему не в дереве ребёнка: у ребёнка нет проекта; каталог под data/, вне песочницы и
/// вне корня sandbox — недоступен ни ручкам ребёнка, ни процессу CLI. Читает его только
/// бэкенд: ручки админа (К2b) и тулсет репетитора (К3). Опись — не в профиле: PUT профиля
/// пишет его целиком, и загрузка файла стала бы второй точкой записи с гонкой против него (§1).
///
/// Имена — серверные: id материала «m_» + 12 hex, storedName/textName — по определённому
/// типу. Имя от админа живёт в описи только подписью и на диск не идёт никогда: путь из
/// чужого имени не строится (тот же приём, что у ChildAttachmentPolicy.NormalizeExtension —
/// расширение сохраняемого файла не расходится с kind). Все пути — SafePath.Join от каталога
/// предмета; при чтении добавляется SafePath.Canonical: симлинк text/ наружу — отказ,
/// содержимое чужого файла ребёнку не отдаётся (fail-closed, как у PrivateAttachments).
///
/// Тип — по сигнатуре байтов, как у детских вложений (ChildAttachmentPolicy.Detect), плюс
/// docx: zip с записью word/document.xml. PDF и docx превращаются в текст при загрузке
/// тем же MarkitdownService, что ручка №8: без текстового слоя — pdf_no_text / docx_no_text,
/// и оригинал при этом не остаётся: файла без строки описи нет.
///
/// Картинка страницы (КИ1b, разрез §9 «Как идёт загрузка картинки», ADR-041 решение 24):
/// PNG/JPEG/WEBP по сигнатуре; HEIC и прочее — unsupported_type (браузер на iOS отдаёт JPEG
/// сам). Оригинал прогоняется через IImageRaster (AutoOrient, ресайз длинной стороны до
/// OcrMaxSide, кодирование PNG) и распознаётся ILocalPageOcr с меткой = серверный id
/// материала (не имя файла от админа). Успех: оригинал — в files/ со своим расширением
/// формата, текст — в text/, опись kind: "image". Любой отказ или сбой растра/OCR —
/// ocr_unavailable; текст пустой или из одних [?] с пробелами — image_no_text. На любом
/// отказе ни файла, ни строки описи. Нет IImageRaster (вертикаль Images отключаемая) —
/// картинка отказана как ocr_unavailable: без неё не нормализовать страницу (ориентация и
/// размер для потолка OCR не гарантированы), а второй ветки загрузки картинки нет.
///
/// Пределы — ChildMode:Materials:MaxFileBytes (20 МБ) и MaxSubjectBytes (200 МБ), чтение
/// как у ChildLimitsCalculator: неразборное или неположительное значение — дефолт инстанса,
/// не отказ. Сумма байт метра предмета — по описи (оригиналы): производный текст в метре
/// не учитывается.
///
/// Конкурентность: весь read-modify-write описи — под семафором по ключу (childId,
/// subjectId) (словарь семафоров, не один на стор: разные предметы не ждут друг друга);
/// запись описи атомарна (.tmp + File.Move, JsonFileStore.Save). Сбой диска — результат с
/// кодом, исключения наружу нет и полусостояния тоже: положенный на диск файл без строки
/// описи удаляется. Картинка — исключение по длительности: растр и OCR (до минут) идут
/// ВНЕ семафора предмета, под семафором после распознавания — только проверка subject_full
/// и атомарная запись (разрез §9): пока страница распознаётся, другая загрузка в тот же
/// предмет её не ждёт. Ревью КИ1b: перед дорогой работой — дешёвая предпроверка
/// subject_full по описи без семафора (заведомо полный предмет не ждёт трёх минут);
/// повторная проверка под семафором остаётся (предмет мог заполниться за эти минуты).
/// Ревью КИ1b, отмена: AddAsync принимает CancellationToken вызывающего (RequestAborted
/// ручки) и пробрасывает его в OCR — отменённый запрос админа прерывает распознавание,
/// а не ждёт его; исход отмены — ocr_unavailable (сбой шва), ответу клиент уже не рад.
///
/// Живой владелец (ревью КИ1b): первая проверка под семафором, перед любой записью —
/// childExists(childId), есть ли ещё этот id в стору пользователей. Ребёнка могли удалить,
/// пока строился путь первой загрузки или пока шла картинка (минуты OCR вне семафора):
/// DeleteChildAsync берёт семафоры только УЖЕ существующих каталогов предметов, поэтому
/// без этой проверки запись воссоздала бы каталог после сноса. Инвариант: после
/// DeleteChildAsync каталог ребёнка не появляется. Отказ — child_gone, ни файла, ни строки
/// описи. Стор про пользователей ничего не знает сам: шов-делегат ему даёт вызывающий.
///
/// Каталог лежит в data/ и едет в архив сам (служебный .tmp — нет; тот же тест, что у
/// child-usage). Новое хранилище, существующий формат не меняет: BackupSchema.Version не
/// трогается; секретов нет — BackupPaths.SecretFileNames не меняется.
/// </summary>
public sealed class ChildSubjectMaterialsStore(
    string root,
    MarkitdownService markitdown,
    IConfiguration config,
    // Шов-делегат живого владельца (ревью КИ1b): есть ли ещё этот id в стору
    // пользователей. Единственная точка, где стор узнает об удалении ребёнка: сам он про
    // пользователей ничего не знает и сам себе этот вопрос не решает.
    Func<string, bool> childExists,
    ILocalPageOcr ocr,
    IImageRaster? raster = null,
    ILogger<ChildSubjectMaterialsStore>? log = null)
{
    public const string DirName = "child-subjects";

    // Ключи секции "ChildMode:Materials" в appsettings.json (тот же стиль чтения, что у
    // ChildLimitsCalculator: ключи — константы, значение читается TryParse с дефолтом).
    public const string ConfigMaxFileBytesKey = "ChildMode:Materials:MaxFileBytes";
    public const string ConfigMaxSubjectBytesKey = "ChildMode:Materials:MaxSubjectBytes";

    // Разрез §2: 20 МБ на файл, 200 МБ на предмет — пределы держат архив разумным.
    public const long DefaultMaxFileBytes = 20 * 1024 * 1024;
    public const long DefaultMaxSubjectBytes = 200 * 1024 * 1024;

    // Коды отказа AddAsync — контрактный snake_case, их читает ручка К2b (413/415/422).
    public const string CodeTooLarge = "too_large";
    public const string CodeSubjectFull = "subject_full";
    public const string CodeUnsupportedType = "unsupported_type";
    public const string CodePdfNoText = "pdf_no_text";
    public const string CodeDocxNoText = "docx_no_text";
    public const string CodeOcrUnavailable = "ocr_unavailable";
    public const string CodeImageNoText = "image_no_text";
    // Ребёнка больше нет (ревью КИ1b): его удалили, пока шла загрузка. Ручка отвечает тем
    // же 404 child_not_found, что и своя проверка до стора: нового кода клиенту не нужно.
    public const string CodeChildGone = "child_gone";
    // Сбой диска — не код ручки (наружу ему место в 503): стор не бросает наружу ничего,
    // и полусостояния не оставляет.
    public const string CodeStorageError = "storage_error";

    // Виды материала (разрез §2): сигнатура → расширение сохраняемого файла, они не
    // расходятся — downstream (К3, индексация) читает тип снова по расширению.
    public const string KindPdf = "pdf";
    public const string KindText = "text";
    public const string KindDocx = "docx";
    public const string KindImage = "image";

    // Картинка страницы (разрез §9): длинная сторона, до которой она дожимается перед OCR.
    // Арифметика §9: 1800×1350 — даже без сжатия укладывается в 8 МБ потолка LocalOcrService,
    // второй ступени ресайза не нужно.
    public const int OcrMaxSide = 1800;

    // preview результата загрузки (КИ1c отдаёт его только админу): первые 600 символов
    // текста — «как репетитор прочтёт страницу», не новый путь чтения.
    public const int PreviewMaxChars = 600;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ChildSubjectMaterialsStore>? _log = log;

    /// <summary>
    /// Корень каталога — из резолвера путей данных (тот же приём, что у ChildUsageStore):
    /// каталог data — директория DataPath, а не хардкод пути.
    /// </summary>
    public static string RootFromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, DirName);
    }

    /// <summary>
    /// Опись материалов предмета — строки в порядке загрузки. Нет каталога или описи —
    /// пустой список. Содержимого файлов здесь нет никогда.
    /// </summary>
    public Task<IReadOnlyList<ChildSubjectMaterial>> ListAsync(string childId, string subjectId)
    {
        // Лока нет: опись меняется только атомарной заменой файла (.tmp + File.Move) —
        // наполовину записанной описи читателю не встречается (тот же приём, что у
        // ChildUsageStore.MinutesToday).
        return Task.FromResult<IReadOnlyList<ChildSubjectMaterial>>(ReadManifest(SubjectDir(childId, subjectId)) ?? []);
    }

    /// <summary>
    /// Добавить материал: живой владелец → пределы → тип по сигнатуре → оригинал в files/ →
    /// текст в text/ → строка описи в конец списка, всё под семафором предмета. Картинка —
    /// исключение по длительности: растр и OCR вне семафора (AddImageAsync), под семафором —
    /// проверка живого владельца, subject_full и запись. Отказ — результат с кодом,
    /// исключения наружу нет. Аргументы (id ребёнка/предмета, байты, кто загрузил) —
    /// исключение: их обязан выдержать вызывающий, до ручки они не доходят.
    /// ct — токен вызывающего (у ручки — RequestAborted): доходит до OCR картинки
    /// (ревью КИ1b); default — существующие вызовы без токена не меняются.
    /// </summary>
    public async Task<AddMaterialResult> AddAsync(
        string childId, string subjectId, string fileName, byte[] bytes, string uploadedBy,
        CancellationToken ct = default)
    {
        var dir = SubjectDir(childId, subjectId);
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(uploadedBy))
            throw new ArgumentException("Загрузивший — id пользователя", nameof(uploadedBy));

        var kind = DetectKind(bytes);

        // Картинка — отдельным путём: распознавание длится минуты и не должно держать
        // семафор предмета (загрузка другого вида в тот же предмет её не ждёт).
        if (kind == KindImage)
            return await AddImageAsync(dir, childId, subjectId, fileName, bytes, uploadedBy, ct);

        var sem = LockFor(dir);
        await sem.WaitAsync();
        try
        {
            // Первая проверка под семафором — тот же порядок, что у ручки (ребёнок раньше
            // содержимого). Снесённый каталог первой загрузки не должен воскреснуть.
            if (!ChildAlive(childId, subjectId))
                return AddMaterialResult.Refused(CodeChildGone);

            if (bytes.LongLength > MaxFileBytes())
                return AddMaterialResult.Refused(CodeTooLarge);

            if (kind is null)
                return AddMaterialResult.Refused(CodeUnsupportedType);

            // Метр предмета — под тем же семафором: два AddAsync в один предмет не должны
            // просуммировать один и тот же байт и пропустить второй за потолок.
            var manifest = ReadManifest(dir) ?? [];
            var used = 0L;
            foreach (var m in manifest) used += m.Bytes;
            if (used + bytes.LongLength > MaxSubjectBytes())
                return AddMaterialResult.Refused(CodeSubjectFull);

            var id = NewMaterialId();
            var storedName = id + ExtensionOf(kind);
            var textName = id + ".txt";
            var storedPath = SafePath.Join(dir, "files/" + storedName);
            var textPath = SafePath.Join(dir, "text/" + textName);

            var wroteStored = false;
            var wroteText = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
                File.WriteAllBytes(storedPath, bytes);
                wroteStored = true;

                Directory.CreateDirectory(Path.GetDirectoryName(textPath)!);
                string text;
                if (kind == KindText)
                {
                    // Оригинальный текст — он и есть текст: в text/ кладются те же байты,
                    // ReadTextAsync возвращает их дословно.
                    File.WriteAllBytes(textPath, bytes);
                    text = Encoding.UTF8.GetString(bytes);
                }
                else
                {
                    // Тот же путь, что ручка №8: MarkitdownService по абсолютному пути
                    // оригинала. null или «не текст» (скан/битый) — отказа, и оригинал с диска.
                    var md = await markitdown.ConvertAsync(storedPath);
                    if (md is null || !ChildAttachmentPolicy.IsUsableText(Encoding.UTF8.GetBytes(md)))
                    {
                        TryDelete(storedPath);
                        return AddMaterialResult.Refused(kind == KindPdf ? CodePdfNoText : CodeDocxNoText);
                    }
                    File.WriteAllBytes(textPath, Encoding.UTF8.GetBytes(md));
                    text = md;
                }
                wroteText = true;

                var material = new ChildSubjectMaterial(
                    id, LabelOf(fileName), kind, bytes.LongLength, DateTimeOffset.UtcNow,
                    uploadedBy, storedName, textName);
                manifest.Add(material);
                JsonFileStore.Save(ManifestPath(dir), manifest, Json);
                return AddMaterialResult.Added(material, PreviewOf(text));
            }
            catch (Exception e)
            {
                // Отказ диска — результат с кодом; что уже легло на диск без строки описи —
                // удаляется (полусостояния нет).
                _log?.LogWarning(e, "Детский режим: материал предмета не сохранён (child {Child}, subject {Subject})", childId, subjectId);
                if (wroteStored) TryDelete(storedPath);
                if (wroteText) TryDelete(textPath);
                return AddMaterialResult.Refused(CodeStorageError);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Картинка страницы (разрез §9, ADR-041 решение 24): размер → растр (AutoOrient +
    /// ресайз длинной стороны до OcrMaxSide + PNG) → OCR с меткой = серверный id → под
    /// семафором subject_full и атомарная запись (оригинал в files/ со своим расширением
    /// формата, текст в text/). Растр и OCR — вне семафора: минуты распознавания не держат
    /// предмет. Любой отказ или сбой растра/OCR — ocr_unavailable, пустой или «одни [?]»
    /// текст — image_no_text; на любом отказе ни файла, ни строки описи, исключение наружу
    /// не выходит. Метр проверяется после OCR: пока страница распознавалась, предмет мог
    /// заполниться другой загрузкой; перед дорогой работой — дешёвая предпроверка по
    /// описи (ревью КИ1b), заведомо полный предмет не ждёт минут распознавания. Живой
    /// владелец — тоже после OCR, под семафором перед
    /// записью: пока шло распознавание (до минут), ребёнка могли удалить, и его снос не ждал
    /// бы каталог, которого ещё нет (ревью КИ1b) → child_gone.
    /// </summary>
    private async Task<AddMaterialResult> AddImageAsync(
        string dir, string childId, string subjectId, string fileName, byte[] bytes, string uploadedBy,
        CancellationToken ct)
    {
        if (bytes.LongLength > MaxFileBytes())
            return AddMaterialResult.Refused(CodeTooLarge);

        // Дешёвая предпроверка метра до дорогой работы (ревью КИ1b): заведомо полный
        // предмет не должен ждать трёх минут растра и OCR ради заведомого отказа.
        // Читается без семафора — опись атомарна (tmp+Move), полустёртой читателю не
        // встречается; под семафором проверка повторится (ниже) — за минуты OCR предмет
        // мог заполниться другой загрузкой.
        var preflightUsed = 0L;
        foreach (var m in ReadManifest(dir) ?? []) preflightUsed += m.Bytes;
        if (preflightUsed + bytes.LongLength > MaxSubjectBytes())
            return AddMaterialResult.Refused(CodeSubjectFull);

        // Вертикаль Images отключаемая: без растра страницу не нормализовать (ориентация и
        // размер для потолка OCR не гарантированы) — отказ без следа на диске.
        if (raster is null)
            return AddMaterialResult.Refused(CodeOcrUnavailable);

        var id = NewMaterialId();

        byte[] png;
        string storedExt;
        try
        {
            var probe = raster.Probe(bytes);
            if (probe?.Format is null)
            {
                // Сигнатура — картинка, а растр её не прочитал (битый файл): это сбой
                // растра, тот же исход, что отказ OCR.
                _log?.LogWarning("Детский режим: картинку не прочитал растр (child {Child}, subject {Subject}, material {Material})",
                    childId, subjectId, id);
                return AddMaterialResult.Refused(CodeOcrUnavailable);
            }

            // AutoOrient растр делает всегда на входе (AutoOrientOp — явная, но пустая
            // операция); ресайз — по ориентированным сторонам (Display*): повернутое по EXIF
            // фото мерить хранимыми сторонами — значит ресайзить не ту сторону.
            var ops = new List<ImageTransformOp> { new AutoOrientOp() };
            var longSide = Math.Max(probe.DisplayWidth, probe.DisplayHeight);
            if (longSide > OcrMaxSide)
                ops.Add(probe.DisplayWidth >= probe.DisplayHeight
                    ? new ResizeOp(Width: OcrMaxSide)
                    : new ResizeOp(Height: OcrMaxSide));

            var outcome = raster.Apply(bytes, ops, new ImageEncodeSpec(ImageEncodeFormat.Png));
            if (!outcome.Ok)
            {
                _log?.LogWarning("Детский режим: растр отказал картинку (child {Child}, subject {Subject}, material {Material}): {Error}",
                    childId, subjectId, id, outcome.Error);
                return AddMaterialResult.Refused(CodeOcrUnavailable);
            }

            png = outcome.Image!.Bytes;
            // Расширение оригинала — по его формату: kind «image» и расширение не расходятся.
            storedExt = probe.Format.Value switch
            {
                ImageEncodeFormat.Jpeg => ".jpg",
                ImageEncodeFormat.Webp => ".webp",
                _ => ".png",
            };
        }
        catch (Exception e)
        {
            // Контракт растра — отказ ответом, но исключение шва не должно становиться 500.
            _log?.LogWarning(e, "Детский режим: сбой растра картинки (child {Child}, subject {Subject}, material {Material})", childId, subjectId, id);
            return AddMaterialResult.Refused(CodeOcrUnavailable);
        }

        OcrResult ocrRes;
        try
        {
            // Метка — серверный id, не имя файла от админа: имя может нести данные, лог
            // хранит только агрегаты (тот же приём, что у самого LocalOcrService).
            // Токен — вызывающего (ревью КИ1b): отменённый запрос админа прерывает
            // распознавание; отмена доезжает как исключение шва → тот же ocr_unavailable.
            ocrRes = await ocr.RecognizeAsync(png, new OcrRequestInfo(id), ct);
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Детский режим: сбой OCR картинки (child {Child}, subject {Subject}, material {Material})", childId, subjectId, id);
            return AddMaterialResult.Refused(CodeOcrUnavailable);
        }

        if (!ocrRes.Ok)
        {
            // Все причины отказа OCR (Disabled, LocalUnavailable, BusyTimeout, PageTimeout,
            // TooLarge, BadResponse) — для админа один отказ: «распознавание недоступно,
            // повторите»; различие видно в логе OCR по метке.
            _log?.LogWarning("Детский режим: OCR отказал картинку (child {Child}, subject {Subject}, material {Material}): {Reason}",
                childId, subjectId, id, ocrRes.Reason);
            return AddMaterialResult.Refused(CodeOcrUnavailable);
        }

        var text = ocrRes.Text ?? "";
        if (HasNoUsefulText(text))
        {
            // Пустой текст или «одни [?] с пробелами» — на странице нечего читать: парный
            // pdf_no_text (промпт OCR заменяет нечитаемое [?]).
            _log?.LogWarning("Детский режим: на картинке нет читаемого текста (child {Child}, subject {Subject}, material {Material})",
                childId, subjectId, id);
            return AddMaterialResult.Refused(CodeImageNoText);
        }

        var sem = LockFor(dir);
        await sem.WaitAsync();
        try
        {
            // Ревью КИ1b: пока страница распознавалась, ребёнка могли удалить — снос взял
            // бы семафор уже существующего каталога, а его может и не быть (первая
            // загрузка). Без этой проверки файлы и опись создались бы после сноса.
            if (!ChildAlive(childId, subjectId))
                return AddMaterialResult.Refused(CodeChildGone);

            var manifest = ReadManifest(dir) ?? [];
            var used = 0L;
            foreach (var m in manifest) used += m.Bytes;
            if (used + bytes.LongLength > MaxSubjectBytes())
                return AddMaterialResult.Refused(CodeSubjectFull);

            var storedName = id + storedExt;
            var textName = id + ".txt";
            var storedPath = SafePath.Join(dir, "files/" + storedName);
            var textPath = SafePath.Join(dir, "text/" + textName);

            var wroteStored = false;
            var wroteText = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storedPath)!);
                // Оригинал — как прислан (не дожатый PNG): текст уже получен, уменьшенная
                // копия нужна была только OCR и на диске не остаётся.
                File.WriteAllBytes(storedPath, bytes);
                wroteStored = true;

                Directory.CreateDirectory(Path.GetDirectoryName(textPath)!);
                File.WriteAllBytes(textPath, Encoding.UTF8.GetBytes(text));
                wroteText = true;

                var material = new ChildSubjectMaterial(
                    id, LabelOf(fileName), KindImage, bytes.LongLength, DateTimeOffset.UtcNow,
                    uploadedBy, storedName, textName);
                manifest.Add(material);
                JsonFileStore.Save(ManifestPath(dir), manifest, Json);
                _log?.LogInformation("Детский режим: страница сохранена (child {Child}, subject {Subject}, material {Material}, {Chars} символов текста)",
                    childId, subjectId, id, text.Length);
                return AddMaterialResult.Added(material, PreviewOf(text));
            }
            catch (Exception e)
            {
                _log?.LogWarning(e, "Детский режим: картинка не сохранена (child {Child}, subject {Subject}, material {Material})", childId, subjectId, id);
                if (wroteStored) TryDelete(storedPath);
                if (wroteText) TryDelete(textPath);
                return AddMaterialResult.Refused(CodeStorageError);
            }
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Удалить материал: строка уходит из описи (атомарно), затем оригинал и текст.
    /// Нет строки — false, ничего не трогается. Файл, который не удалось снять, — сиротый
    /// мусор вне описи: ни чтению, ни метру предмета он не виден; строка без файла была бы
    /// обещанием, которое стор не выполняет.
    /// </summary>
    public async Task<bool> DeleteAsync(string childId, string subjectId, string materialId)
    {
        var dir = SubjectDir(childId, subjectId);
        var sem = LockFor(dir);
        await sem.WaitAsync();
        try
        {
            var manifest = ReadManifest(dir);
            if (manifest is null) return false;
            var idx = manifest.FindIndex(m => m.Id == materialId);
            if (idx < 0) return false;

            var removed = manifest[idx];
            manifest.RemoveAt(idx);
            JsonFileStore.Save(ManifestPath(dir), manifest, Json);

            TryDelete(SafePath.Join(dir, "files/" + removed.StoredName));
            TryDelete(SafePath.Join(dir, "text/" + removed.TextName));
            return true;
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Детский режим: материал предмета не удалён (child {Child}, subject {Subject})", childId, subjectId);
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Извлечённый текст материала — читает ТОЛЬКО text/{textName}. Нет строки или файла,
    /// путь через симлинк выходит за каталог предмета — null: содержимое чужого файла
    /// ребёнку не отдаётся.
    /// </summary>
    public Task<string?> ReadTextAsync(string childId, string subjectId, string materialId)
    {
        var dir = SubjectDir(childId, subjectId);
        var material = ReadManifest(dir)?.FirstOrDefault(m => m.Id == materialId);
        if (material is null) return Task.FromResult<string?>(null);

        var path = SafePath.Join(dir, "text/" + material.TextName);
        if (!InsideSubject(dir, path))
        {
            _log?.LogWarning(
                "Детский режим: текст материала уходит за каталог предмета (симлинк): child {Child}, subject {Subject}, material {Material}",
                childId, subjectId, materialId);
            return Task.FromResult<string?>(null);
        }

        try
        {
            return Task.FromResult(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Детский режим: текст материала не читается (child {Child}, subject {Subject}, material {Material})",
                childId, subjectId, materialId);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Снести каталог материалов ребёнка (каскад удаления пользователя-ребёнка, разрез §2):
    /// best-effort, лог, исключения наружу нет — как соседние звенья каскада UsersController.
    ///
    /// Семафоры предметов (ревью К2a): идущий AddAsync держит семафор предмета и дописывает
    /// опись/файлы ПОСЛЕ Directory.Delete — каталог воскресал бы после сноса. Снос ждёт
    /// идущие записи: каждый уже созданный каталог предмета берётся под свой семафор
    /// (порядок — Ordinal, два параллельных сноса одного ребёнка не держат друг друга
    /// за разные наборы ключей), затем сносится весь каталог.
    ///
    /// Загрузка, пришедшая после удаления пользователя, к стору ДОХОДИТ (ревью КИ1b):
    /// проверка контроллера К2b (ChildOf → child_not_found) была до минуты распознавания
    /// картинки, а окно между ней и записью — ровно эти минуты. Поэтому сама запись под
    /// своим семафором повторно проверяет живого владельца (ChildAlive → child_gone) и
    /// ничего не пишет. Порядок другой: если запись уже держит семафор предмета, снос ждёт
    /// её и сносит написанное — каталог ребёнка не появляется ни в одном из порядков.
    /// </summary>
    public async Task DeleteChildAsync(string childId)
    {
        RequireChild(childId);
        var dir = SafePath.Join(root, childId);
        try
        {
            var subjectDirs = Directory.Exists(dir)
                ? Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal).ToArray()
                : [];
            var held = new List<SemaphoreSlim>(subjectDirs.Length);
            try
            {
                foreach (var subjectDir in subjectDirs)
                {
                    var sem = LockFor(subjectDir);
                    await sem.WaitAsync();
                    held.Add(sem);
                }
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            finally
            {
                foreach (var sem in held) sem.Release();
            }
        }
        catch (Exception e)
        {
            _log?.LogWarning(e, "Детский режим: каталог материалов ребёнка не снесён (child {Child})", childId);
        }
    }

    // ── тип по сигнатуре ──────────────────────────────────────────────────────────────

    // Вид материала по сигнатуре байтов или null (unsupported_type). Primary — тот же
    // ChildAttachmentPolicy.Detect, что у детских вложений (PNG/JPEG/WEBP — image, КИ1b;
    // HEIC в allow-list не входит), к нему добавлен только docx: zip с записью
    // word/document.xml.
    private static string? DetectKind(byte[] bytes)
    {
        switch (ChildAttachmentPolicy.Detect(bytes))
        {
            case ChildAttachmentPolicy.Kind.Pdf: return KindPdf;
            case ChildAttachmentPolicy.Kind.Text: return KindText;
            case ChildAttachmentPolicy.Kind.Image: return KindImage;
            default: return IsDocx(bytes) ? KindDocx : null;
        }
    }

    private static readonly byte[] ZipLocalHeader = [0x50, 0x4B, 0x03, 0x04]; // «PK\003\004» — начало первой записи zip

    // docx — zip с обязательной записью word/document.xml. «PK…» без валивного central
    // directory — не zip (InvalidDataException), а не отказ формата наружу.
    private static bool IsDocx(byte[] bytes)
    {
        if (bytes.Length < ZipLocalHeader.Length || !bytes[..ZipLocalHeader.Length].SequenceEqual(ZipLocalHeader))
            return false;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            return zip.Entries.Any(e => e.FullName == "word/document.xml");
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string ExtensionOf(string kind) => kind switch
    {
        KindPdf => ".pdf",
        KindDocx => ".docx",
        _ => ".txt",
    };

    // ── текст и preview ──────────────────────────────────────────────────────────────

    // «Текста нет» (разрез §9): пусто или только [?] с пробелами. Промпт OCR заменяет
    // нечитаемое [?]; страница без текста доезжает пустой строкой (defect 1e017cf7).
    // Всё остальное — читаемое содержимое.
    private static bool HasNoUsefulText(string text)
    {
        var s = text.AsSpan();
        for (var i = 0; i < s.Length;)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '[' && i + 2 < s.Length && s[i + 1] == '?' && s[i + 2] == ']') { i += 3; continue; }
            return false;
        }
        return true;
    }

    // preview — первые PreviewMaxChars символов текста: «как репетитор прочтёт страницу»,
    // для админа (КИ1c). Полного текста в результате нет.
    private static string PreviewOf(string text) =>
        text.Length > PreviewMaxChars ? text[..PreviewMaxChars] : text;

    // ── имена и пути ─────────────────────────────────────────────────────────────────

    // «m_» + 12 hex (разрез §2): 48 случайных бит — совпадение внутри предмета практически
    // невозможно; отдельной проверки уникальности по описи нет.
    private static string NewMaterialId() =>
        "m_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    // Имя от админа — только подпись описи: путь из него не строится никогда. Из подписи
    // срезается путь («../../x.txt» → «x.txt») — чужой путь в описи не живёт.
    private static string LabelOf(string fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return "без имени";
        return name.Length > 200 ? name[..200] : name;
    }

    // Живой владелец — под семафором, перед любой записью (ревью КИ1b): снесённый каталог
    // ребёнка не должен воскресать ни после какой загрузки. Отказ — только лог и child_gone:
    // ни файла, ни строки описи, полусостояния нет.
    private bool ChildAlive(string childId, string subjectId)
    {
        if (childExists(childId)) return true;
        _log?.LogWarning("Детский режим: ребёнка больше нет — загрузка отменена (child {Child}, subject {Subject})",
            childId, subjectId);
        return false;
    }

    private string SubjectDir(string childId, string subjectId)
    {
        RequireChild(childId);
        RequireSubject(subjectId);
        return SafePath.Join(root, childId + "/" + subjectId);
    }

    private static string ManifestPath(string subjectDir) => Path.Combine(subjectDir, "materials.json");

    private static void RequireChild(string childId)
    {
        // Идентификатор пользователя (GUID из UserStore): сегмент пути, а не произвольный текст.
        if (string.IsNullOrWhiteSpace(childId) || childId.Length > 64 ||
            childId.IndexOfAny(['/', '\\']) >= 0 || childId.Contains(".."))
            throw new ArgumentException("Недопустимый идентификатор ребёнка", nameof(childId));
    }

    // Та же грамматика id предмета, что у ChildProfileValidator.SubjectIdRegex и политики
    // SubjectsValid: строчные буквы, цифры и «-», 1..32. Стор держит её сам: вызывающий
    // обязан был проверить её по профилю, но путь каталога строится здесь.
    private static readonly System.Text.RegularExpressions.Regex SubjectIdRegex =
        new(@"^[a-z0-9-]{1,32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void RequireSubject(string subjectId)
    {
        if (!SubjectIdRegex.IsMatch(subjectId ?? ""))
            throw new ArgumentException("Недопустимый ключ предмета", nameof(subjectId));
    }

    // Каноническая проверка «внутри каталога предмета»: SafePath.Join держит текст пути,
    // разыменование симлинков — SafePath.Canonical (тот же приём, что у PrivateAttachments).
    // Неразрешимый путь (цикл ссылок) или выход за каталог — отказ (fail-closed).
    private static bool InsideSubject(string subjectDir, string path)
    {
        var dir = SafePath.Canonical(subjectDir)?.TrimEnd('/');
        var full = SafePath.Canonical(path)?.TrimEnd('/');
        if (dir is null || full is null) return false;
        var cmp = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        return full.Equals(dir, cmp) || full.StartsWith(dir + "/", cmp);
    }

    // ── опись ────────────────────────────────────────────────────────────────────────

    private List<ChildSubjectMaterial>? ReadManifest(string subjectDir) =>
        JsonFileStore.Load<List<ChildSubjectMaterial>>(ManifestPath(subjectDir), Json, _log);

    private long MaxFileBytes() =>
        long.TryParse(config[ConfigMaxFileBytesKey], out var bytes) && bytes > 0 ? bytes : DefaultMaxFileBytes;

    private long MaxSubjectBytes() =>
        long.TryParse(config[ConfigMaxSubjectBytesKey], out var bytes) && bytes > 0 ? bytes : DefaultMaxSubjectBytes;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { /* сиротый файл хуже исходного отказа, чем ещё один отказ */ _ = e; }
    }

    // Семафор на каталог предмета, общий для всех экземпляров стора процесса: опись —
    // read-modify-write одного файла, и два AddAsync одного предмета не должны терять друг
    // друга. Разные предметы не ждут друг друга (словарь по ключу, не один на стор).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SubjectLocks =
        new(StringComparer.Ordinal);

    private static SemaphoreSlim LockFor(string subjectDir) =>
        SubjectLocks.GetOrAdd(Path.GetFullPath(subjectDir), _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Строка описи материалов предмета (разрез §2). name — подпись админа (на диск не идёт);
/// storedName/textName — имена, которые дал сервер; kind — "pdf" | "text" | "docx" |
/// "image"; bytes — размер оригинала (он же — метр предмета).
/// </summary>
public sealed record ChildSubjectMaterial(
    string Id,
    string Name,
    string Kind,
    long Bytes,
    DateTimeOffset UploadedAt,
    string UploadedBy,
    string StoredName,
    string TextName);

/// <summary>
/// Результат AddAsync: строка описи (Added) или отказ с кодом (CodeTooLarge, CodeSubjectFull,
/// CodeUnsupportedType, CodePdfNoText, CodeDocxNoText, CodeOcrUnavailable, CodeImageNoText,
/// CodeChildGone, CodeStorageError). Preview — первые 600 символов текста материала для всех видов
/// (разрез §9: «preview» в ответе загрузки, только админу; КИ1c); в описи и на диске его нет.
/// </summary>
public sealed record AddMaterialResult(ChildSubjectMaterial? Material, string? Code, string? Preview = null)
{
    public bool Accepted => Material is not null;

    public static AddMaterialResult Added(ChildSubjectMaterial material, string? preview = null) => new(material, null, preview);
    public static AddMaterialResult Refused(string code) => new(null, code);
}
