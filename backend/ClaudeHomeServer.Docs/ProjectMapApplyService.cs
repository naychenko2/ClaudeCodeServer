using System.Collections.Concurrent;
using System.Text;

namespace ClaudeHomeServer.Services.Docs;

/// <summary>
/// Почему правка не применилась. Закрытый список: фронт по нему не ветвится, но человеку
/// на каждый показывается своя строка — «не удалось» без причины он не починит.
/// </summary>
public static class MapApplyReasons
{
    /// <summary>Якоря в карте нет вовсе (вне заборов) — правку уже внесли или текст переписали.</summary>
    public const string AnchorNotFound = "anchorNotFound";
    /// <summary>Якорь встречается несколько раз: куда писать — решение человека, не сервера.</summary>
    public const string AmbiguousAnchor = "ambiguousAnchor";
    /// <summary>Предложения с таким id в свежем скане нет.</summary>
    public const string UnknownId = "unknownId";
    /// <summary>Предложение есть, но механической правки у него нет по определению вида.</summary>
    public const string NotApplicable = "notApplicable";
}

public sealed record MapApplyFailure(string Id, string Reason);

/// <summary>
/// Итог применения. Stale — отпечаток разошёлся, файл не тронут (контроллер отдаёт 409);
/// Scan — свежий отчёт по итоговому тексту, чтобы фронт перерисовал шапку без второго запроса.
/// </summary>
public sealed record MapApplyResult(
    bool Stale,
    IReadOnlyList<string> Applied,
    IReadOnlyList<MapApplyFailure> Failed,
    string? NewSha,
    MapHygieneReport? Scan);

/// <summary>
/// Применение механических правок к карте проекта — единственное место, где продукт ПИШЕТ
/// в CLAUDE.md пользователя. Цена ошибки здесь не косметическая: потерянный текст карты,
/// который грузится в контекст каждой сессии.
///
/// Модель записи — read-verify-write под локом, и <b>файл читается РОВНО ОДИН РАЗ</b>.
/// Это требование, а не оптимизация: сверка хеша, поиск фактов и сама замена идут по
/// ОДНОМУ снимку текста. Читай мы файл заново на каждом шаге — правка постороннего
/// процесса, попавшая между сверкой и записью, была бы сверена в одном тексте, а
/// перезаписана другим: File.Move снёс бы её целиком вместо замены подстроки, а сверка
/// baseSha отработала бы «успешно» и ничего не поймала. С единственным снимком это
/// невозможно по конструкции.
///
/// Между чтением и Move остаётся честное TOCTOU-окно, и оно признано, а не забыто: лок
/// закрывает его только внутри процесса, а карту правят и исполнители задач в общем
/// дереве, и человек в редакторе. Полностью закрыть можно лишь эксклюзивным захватом
/// файла на всё время — он поссорил бы фичу с любым открытым редактором. Цена остатка
/// ограничена: потеряться может одна чужая правка, уложившаяся в миллисекунды между
/// чтением и переносом, дальше её защищает git — поэтому фича и существует только для
/// файла под версионным контролем. Не «чинить» лок.
///
/// Применяются ТОЛЬКО механические правки — починка мёртвой ссылки с единственным
/// кандидатом. Вынос секции в docs/* кнопкой не делается никогда: это содержательная
/// работа, и её цена — потеря знания, оплаченного инцидентами.
/// </summary>
public sealed class ProjectMapApplyService(ProjectMapScanner scanner, ILogger<ProjectMapApplyService> log)
{
    /// <summary>
    /// Разбор байтов карты: текст и признак BOM — или false, если файл не наш.
    ///
    /// Кодировка тут не формальность, а защита от самой дорогой порчи: UTF-8-декодер по
    /// умолчанию молча меняет каждый невалидный байт на U+FFFD, и сканер (File.ReadAllText)
    /// портит карту в CP1251 ровно так же — хеши совпадут, сверка пройдёт, а File.Move
    /// положит на место карты файл из сплошных ромбиков.
    ///
    /// Отдельной проверки на метку UTF-16/UTF-32 здесь намеренно НЕТ: её байты (FF FE,
    /// FE FF) в UTF-8 невалидны сами по себе, и строгий декодер отбивает такой файл первым
    /// же символом. Заведённая ради очевидности, она оказалась кодом, который не способен
    /// покраснеть ни в одном тесте, — а такой код хуже, чем его отсутствие.
    /// </summary>
    internal static bool TryDecode(byte[] bytes, out string text, out bool hasBom)
    {
        text = "";
        hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // Лок на путь карты, а не на файл: два окна продукта у одного человека — реальный
    // сценарий, и параллельный apply по одной карте обязан выстроиться в очередь.
    // Словарь не растёт бесконтрольно: путь один на проект
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    private static SemaphoreSlim LockFor(string path) =>
        Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    /// <param name="root">Корень проекта: предмет правки — его CLAUDE.md и только он (Р4).</param>
    /// <param name="baseSha">Отпечаток с момента скана; разошёлся — не пишем ничего.</param>
    /// <param name="ids">Отмеченные человеком предложения из того же отчёта.</param>
    public async Task<MapApplyResult> ApplyAsync(string root, string? baseSha,
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var mapPath = Path.Combine(root, ProjectMapScanner.MainMapName);
        var gate = LockFor(Path.GetFullPath(mapPath));

        await gate.WaitAsync(ct);
        try
        {
            // ЕДИНСТВЕННОЕ чтение файла за весь вызов. Байты, а не ReadAllText: иначе
            // признак BOM теряется молча (ReadAllText его съедает, WriteAllText по
            // умолчанию не пишет) — файл с BOM после первой же правки остался бы без него
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(mapPath, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Карты нет или не читается (удалили или переименовали, пока отчёт был
                // открыт) — тот же 409 и та же плашка «файл изменился после проверки»:
                // для человека случай неотличим от «карту подменили»
                return new MapApplyResult(Stale: true, [], [], null, null);
            }

            // Карту в чужой кодировке продукт править не умеет и обязан отказаться, а не
            // перекодировать её за человека. Отказ тот же, что у нечитаемой карты: записи
            // не будет, человек увидит «файл изменился после проверки». Диагноз — в лог:
            // молча отказывающая кнопка иначе неотлаживаема
            if (!TryDecode(bytes, out var text, out var hasBom))
            {
                log.LogWarning("Уборка карты: {Path} не читается как UTF-8 — правки не применяются", mapPath);
                return new MapApplyResult(Stale: true, [], [], null, null);
            }

            // Скан от снимка в памяти даёт разом и отпечаток, и факты: формула baseSha
            // (SHA-256 раскрытого состава) живёт в одной точке — в сканере. Вторая её
            // реализация здесь рано или поздно разошлась бы с первой, и тогда apply
            // отвечал бы 409 ВСЕГДА, а человек видел бы штатную плашку и считал фичу рабочей
            var report = scanner.Scan(root, text, ct);
            if (report.BaseSha is null || !string.Equals(report.BaseSha, baseSha, StringComparison.Ordinal))
                return new MapApplyResult(Stale: true, [], [], report.BaseSha, null);

            var (applied, failed, current) = ApplyTo(text, report.Suggestions, ids);

            // Не применилось ничего — файла не касаемся вовсе: лишняя перезапись меняет
            // время файла и будит синки на ровном месте
            if (applied.Count == 0)
                return new MapApplyResult(false, applied, failed, report.BaseSha, report);

            WriteAtomic(mapPath, current, hasBom);

            // Отчёт по ИТОГОВОМУ тексту — тому, что лёг на диск: newSha берётся из него же,
            // поэтому «хеш соответствует записанному файлу» выполняется по конструкции.
            // Токен запроса сюда НЕ передаётся: запись уже состоялась, и отмена по
            // ушедшему клиенту показала бы человеку ошибку при изменённом файле — то есть
            // соврала бы про факт правки его CLAUDE.md
            var after = scanner.Scan(root, current, CancellationToken.None);
            return new MapApplyResult(false, applied, failed, after.BaseSha, after);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Замены поверх снимка текста — вся развилка «применилось / почему нет» в одном месте.
    ///
    /// Отдельный метод, а не тело цикла внутри ApplyAsync: ветки ambiguousAnchor и
    /// anchorNotFound по конструкции недостижимы через публичный путь (при едином снимке
    /// неуникальный якорь обнуляет патч ещё на скане, и предложение приходит без кнопки).
    /// Они существуют ради гонки — правки, пришедшей между сканом и применением, — и
    /// проверяются здесь, иначе остались бы кодом, который никто никогда не выполнял.
    /// </summary>
    internal static (List<string> Applied, List<MapApplyFailure> Failed, string Text) ApplyTo(
        string text, IReadOnlyList<MapSuggestion> suggestions, IReadOnlyList<string> ids)
    {
        var applied = new List<string>();
        var failed = new List<MapApplyFailure>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = text;

        foreach (var id in ids)
        {
            // Повтор того же id в запросе — не вторая правка: без отсева он дал бы
            // ложный anchorNotFound по уже применённой замене
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;

            var suggestion = suggestions.FirstOrDefault(s =>
                string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (suggestion is null)
            {
                failed.Add(new MapApplyFailure(id, MapApplyReasons.UnknownId));
                continue;
            }
            if (suggestion.Apply is not { } patch)
            {
                failed.Add(new MapApplyFailure(id, MapApplyReasons.NotApplicable));
                continue;
            }

            // Уникальность проверяется ВТОРОЙ раз (первый — на скане, где неуникальный
            // якорь рождает предложение без кнопки). Это не дублирование: между сканом
            // и применением карту могли дописать, и вторая проверка закрывает окно,
            // а первая бережёт человека от кнопки-обманки
            var offsets = MapAnchorText.Offsets(current, patch.AnchorText);
            if (offsets.Count == 0)
            {
                failed.Add(new MapApplyFailure(id, MapApplyReasons.AnchorNotFound));
                continue;
            }
            if (offsets.Count > 1)
            {
                failed.Add(new MapApplyFailure(id, MapApplyReasons.AmbiguousAnchor));
                continue;
            }

            // Замена подстроки в исходном тексте, а не пересборка из строк: окончания
            // строк и BOM сохраняются сами собой, и это механизм, а не дисциплина.
            // Построчный разбор со склейкой через '\n' превратил бы CRLF-файл в LF
            // одной правкой — дифф на весь файл вместо одной строки
            current = MapAnchorText.ReplaceAt(current, offsets[0], patch.AnchorText.Length, patch.After);
            applied.Add(suggestion.Id);
        }

        return (applied, failed, current);
    }

    /// <summary>
    /// Запись по образцу <c>JsonFileStore.Save</c>: temp рядом + File.Move с заменой.
    /// Имя temp уникальное (Guid), а не фиксированное «.tmp»: два одновременных Move
    /// поверх одного файла на Windows дают Access denied.
    ///
    /// Известные и осознанно принятые остатки: смерть процесса между созданием temp и
    /// переносом оставит «CLAUDE.md.{guid}.tmp» в рабочем дереве человека (подметать его
    /// автоудалением ПО МАСКЕ рядом с чужими файлами опаснее самого мусора — такой уборке
    /// в этом продукте места нет); File.Move подменяет узел, поэтому симлинк на карту
    /// станет обычным файлом.
    /// </summary>
    private static void WriteAtomic(string path, string text, bool hasBom)
    {
        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        // encoderShouldEmitUTF8Identifier ровно по факту исходного файла: был BOM —
        // остаётся, не было — не появляется
        var encoding = new UTF8Encoding(hasBom);
        try
        {
            // Данные tmp обязаны лечь на диск ДО переименования: иначе при внезапном
            // выключении ФС успевает сохранить rename, но не содержимое — и на месте
            // карты остаётся файл нулевой длины (так дважды терялся sessions.json)
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var preamble = encoding.GetPreamble();
                if (preamble.Length > 0) fs.Write(preamble);
                fs.Write(encoding.GetBytes(text));
                fs.Flush(flushToDisk: true);
            }
            MoveWithRetry(tmpPath, path);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* мусорный tmp не важнее исходной ошибки */ }
            throw;
        }
    }

    // На Windows перенос поверх существующего файла регулярно ловит транзиторный отказ:
    // свежий tmp или целевой файл на доли секунды держит антивирус либо индексатор
    private static void MoveWithRetry(string tmpPath, string path)
    {
        const int attempts = 10;
        for (var i = 1; ; i++)
        {
            try
            {
                File.Move(tmpPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (i < attempts && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(20 * i);
            }
        }
    }
}
