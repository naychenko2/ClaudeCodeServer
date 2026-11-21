using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm.Gateway;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Services.ChildMode;

/// <summary>
/// Фоновая текстовая копия картинок папки предмета (Б13c, ADR-041 ред. 6 решение 67,
/// разрез §12 «Знания и картинки»; форма и видимость копии — решения 90/91).
///
/// У большинства моделей нет зрения (решение 67): картинку модель читает по своей
/// текстовой копии. Офис и PDF получают её при загрузке (Б11b), картинки — этим
/// сервисом, без состояния материала: нет копии — есть только картинка. Никакого
/// нового состояния нигде не заводится — факт ровно тот, что на диске.
///
/// Очередь — у писателя копий (ChildTextCopyService.EnqueueImageOcr): загрузка
/// ставит в неё картинку сама, этот сервис добавляет к ней стартовый обход папок
/// проектов-предметов (маркер Project.ChildSubjectId, перечень — ProjectManager.GetAll
/// через ProjectCapabilities.IsChildSubject) и исполняет её: одна задача на картинку,
/// дедуп по пути держит писатель. Стартовый обход — один раз на процесс: после
/// рестарта он и загрузка наполняют очередь заново (очередь — в памяти, тот же приём,
/// что у CharacterPhotoLabeling, ADR-027 §9).
///
/// Обход папки: только картинки по расширению (png/jpg/jpeg/webp — тот же allow-list
/// формата, что у ChildAttachmentPolicy: downstream читает тип по расширению, и
/// писатель привёл его к сигнатуре). Не идут: любой скрытый сегмент пути (с той же
/// грамматикой, что у ChildPathPolicy.IsProtectedSegment: .ccs-text/ — сами копии,
/// .ccs-trash/ — снесённое, .claude/ и .cc-attachments/ — служебное), уже имеющий
/// копию по TextCopyOf (решение 90) и файл, не проходящий по сигнатуре. «Материалы/»
/// и «Для репетитора/» — идут: копия судится по своему оригиналу (ChildPathPolicy),
/// и видность её ребёнку решает политика путей, а не этот обход.
///
/// Одна задача: файла нет (удалён, пока стоял в очереди) — пропуск без исключения;
/// сбой или отказ OCR (все OcrFailureReason, «недоверенный» ответ), текст пустой или
/// из одних [?] (тот же критерий «текста нет», что у стора материалов, КИ1b) — копии
/// нет, запись в лог, исключение наружу не выходит: по умолчанию .NET гасит хост по
/// BackgroundServiceExceptionBehavior.StopHost. Сбой одной картинки не останавливает
/// очередь: следующая обрабатывается.
///
/// Писатель тот же: результат возвращается в ChildTextCopyService.WriteTextCopyAsync
/// (форма копии — TextCopyOf, запись — под тем же замком корня). Своей формы копии,
/// Path.ChangeExtension и второго писателя .ccs-text/ здесь нет вовсе.
///
/// Потоки: один исполнитель на инстанс (ExecuteAsync), задачи — строго по одной:
/// одна страница — один слот GPU (так держит себя и LocalOcrService). Пробел между
/// картинками — не пауза, а ожидание слота шва.
/// </summary>
public sealed class ChildImageOcrBackfill(
    ProjectManager projects,
    ChildTextCopyService textCopy,
    ILocalPageOcr ocr,
    ILogger<ChildImageOcrBackfill>? log = null) : BackgroundService
{
    // Расширения, по которым обход папки считает файл картинкой: тот же состав, что у
    // allow-list формата ChildAttachmentPolicy (AllowedImageExts). Содержание проверяется
    // по сигнатуре перед OCR (ReadAndCheckAsync) — расширению обход не верит вслепую.
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            ScanSubjectFolders();
            // Внутренний токен — поверх stopping-token: исключение шва гасит только это
            // наблюдение, а не весь хост (тот же приём, что у явного StopHost в
            // ComfyQueueMonitor.cs:149).
            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await foreach (var job in textCopy.OcrJobs.ReadAllAsync(run.Token))
            {
                try { await ProcessJobAsync(job, run.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
        catch (Exception e)
        {
            // Ошибка стартового обхода — не повод гасить хост: загрузка картинок всё
            // равно ставит их в очередь сама, следующая картинка дойдёт до копии.
            log?.LogWarning(e, "Детский режим: фоновый обход картинок прерван");
        }
    }

    /// <summary>
    /// Стартовый обход папок всех проектов-предметов: картинки без копии — в очередь.
    /// internal и для тестов (тот же приём, что у internal-точек CharacterPhotoLabeling);
    /// боевой путь — ExecuteAsync. Сбой обхода одной папки — лог и следующая папка:
    /// половина списка — уже в очереди, исключение наружу не выходит.
    /// </summary>
    internal void ScanSubjectFolders()
    {
        foreach (var project in projects.GetAll())
        {
            if (!ProjectCapabilities.IsChildSubject(project) || string.IsNullOrWhiteSpace(project.RootPath))
                continue;
            try
            {
                ScanFolder(project.RootPath);
            }
            catch (Exception e)
            {
                log?.LogWarning(e, "Детский режим: папка предмета не обойдена при старте OCR-копий ({Root})", project.RootPath);
            }
        }
    }

    private void ScanFolder(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = RelativeUnderRoot(root, path);
            if (rel is null) continue; // симлинк вышел за папку — не её содержимое
            if (HasHiddenSegment(rel)) continue; // .ccs-text/, .ccs-trash/, .claude/ и прочее служебное
            if (!ImageExtensions.Contains(Path.GetExtension(path))) continue;
            if (File.Exists(SafePath.Join(root, ChildPathPolicy.TextCopyOf(rel)))) continue; // копия уже есть
            textCopy.EnqueueImageOcr(root, rel);
        }
    }

    /// <summary>
    /// Тестовая точка: один проход по уже стоящим в очереди задачам — тот же порядок,
    /// что у петли ExecuteAsync (тот же ProcessJobAsync). Боевой путь — ExecuteAsync.
    /// </summary>
    internal async Task RunQueuedAsync(CancellationToken ct = default)
    {
        while (textCopy.OcrJobs.TryRead(out var job))
        {
            try { await ProcessJobAsync(job, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // Одна задача от начала до конца: снятие с дедупа до работы (загрузка той же
    // картинки во время её распознавания поставит новый заход, а не потеряется за
    // этой задачей) и ловля всего, что не выловил ProcessAsync (диск, чтение) — наружу
    // не идёт ни при каком исходе, хост не гасится.
    private async Task ProcessJobAsync(ChildTextCopyService.OcrJob job, CancellationToken ct)
    {
        textCopy.ReleaseOcrJob(job);
        try
        {
            await ProcessAsync(job, ct);
        }
        catch (Exception e)
        {
            log?.LogWarning(e, "Детский режим: фоновая копия картинки упала ({Rel})", job.Rel);
        }
    }

    /// <summary>
    /// Исполнить одну задачу очереди. internal для тестов: сбой шва проверяется на этом
    /// контракте («исключение не выходит»), а не на петле хоста.
    /// </summary>
    internal async Task ProcessAsync(ChildTextCopyService.OcrJob job, CancellationToken ct)
    {
        var originalAbs = SafePath.Join(job.SubjectRoot, job.Rel);
        if (!File.Exists(originalAbs))
            return; // файл снят, пока задача стояла в очереди: пропуск без исключения

        var bytes = File.ReadAllBytes(originalAbs);
        if (ChildAttachmentPolicy.Detect(bytes) is not ChildAttachmentPolicy.Kind.Image)
        {
            // Расширение называет картинку, содержимое — нет (переименованный .exe):
            // в GPU это не идёт, копия не пишется.
            log?.LogWarning("Детский режим: картинка по расширению не прошла по сигнатуре ({Rel})", job.Rel);
            return;
        }

        OcrResult ocrRes;
        try
        {
            // Метка — относительный путь, не содержимое и не данные файла: лог OCR
            // хранит только агрегаты (тот же приём, что у стора материалов, КИ1b).
            ocrRes = await ocr.RecognizeAsync(bytes, new OcrRequestInfo(job.Rel), ct);
        }
        catch (Exception e)
        {
            // Контракт шва — отказ результатом, но исключение шва не должно гасить ни
            // очередь, ни хост: тот же исход, что отказ (тот же приём, что у КИ1b).
            log?.LogWarning(e, "Детский режим: сбой OCR фоновой копии ({Rel})", job.Rel);
            return;
        }

        if (!ocrRes.Ok)
        {
            // Любая OcrFailureReason (Disabled, LocalUnavailable, BusyTimeout, PageTimeout,
            // TooLarge, BadResponse) — для фона одно: копии нет, лог с причиной.
            log?.LogWarning("Детский режим: OCR отказал фоновую копию ({Rel}): {Reason}", job.Rel, ocrRes.Reason);
            return;
        }

        var text = ocrRes.Text ?? "";
        if (HasNoUsefulText(text))
        {
            // Пусто или «одни [?] с пробелами» — на картинке нечего читать: парный
            // image_no_text стора материалов; пустая копия — не копия.
            log?.LogWarning("Детский режим: на картинке нет читаемого текста ({Rel})", job.Rel);
            return;
        }

        var written = await textCopy.WriteTextCopyAsync(job.SubjectRoot, job.Rel, text);
        if (written is not null)
            log?.LogInformation("Детский режим: фоновая копия картинки записана ({Copy}, {Chars} символов текста)", written, text.Length);
    }

    // Относительный путь под корнем в нормализации ChildPathPolicy («/», без ведущего)
    // или null: симлинк вывел файл за папку предмета — он не её содержимое (fail-closed,
    // тот же приём, что у InsideSubject стора материалов).
    private static string? RelativeUnderRoot(string root, string fullPath)
    {
        var rootFull = SafePath.Canonical(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = SafePath.Canonical(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (rootFull is null || full is null) return null;
        var cmp = SafePath.ComparisonFor(OperatingSystem.IsWindows());
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp)) return null;
        return full[(rootFull.Length + 1)..].Replace('\\', '/');
    }

    // Скрытый сегмент на любой глубине — та же грамматика имени, что у
    // ChildPathPolicy.IsProtectedSegment: «.»-префикс (включая .ccs-* и .claude).
    private static bool HasHiddenSegment(string rel)
    {
        foreach (var segment in rel.Split('/'))
            if (segment.Length > 0 && segment[0] == '.') return true;
        return false;
    }

    // «Текста нет» — тот же критерий, что у стора материалов (КИ1b): пусто или только
    // [?] с пробелами (промпт OCR заменяет нечитаемое [?]; пустая страница доезжает
    // пустой строкой, defect 1e017cf7). Всё остальное — читаемое содержимое.
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
}
