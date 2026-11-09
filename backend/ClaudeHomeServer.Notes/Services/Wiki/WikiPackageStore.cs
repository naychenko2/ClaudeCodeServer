using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Хранилище пакетов (ADR-035, решение 4): keeper/packages/{id}.json — по файлу на пакет,
// id — GUID, имя файла строится из id без обхода пути (SafePath.JoinBelowRoot). Запись —
// через WikiFileWriter: атомарная, уникальный temp в нём уже есть (ревью S2b-2).
//
// Различия с манифестом (образец — WikiManifest): пакет — запись на одного автора и на
// один цикл, его правит не весь файл разом, а переход статуса; поэтому состояние файла
// здесь то же трёхзначное (Missing / Valid / Invalid): битый файл пакета — «невалиден»,
// не исключение наружу и не «пустой пакет» (ревью S2b-1, M1): список «Входящих» и ночной
// разбор не должны падать на одном испорченном файле, а человек должен видеть, что файл
// есть и что он нечитаем.
//
// Порядок перехода статусов держит WikiPackage.Transitions; здесь — только отказ
// недопустимого перехода и лимит 20 открытых на всю вики.
//
// Цикл «чтение → пересчёт → перезапись всего файла» сериализован блокировками (ревью S4a,
// MAJOR 1), в стиле WikiKeeper._locks: без них два параллельных решения теряют одно
// (второе пишет своё прочтение), решение человека затирает статус «applying», а две
// параллельные постановки при 19 открытых пробивают лимит 20 (TOCTOU).
public static class WikiPackageStore
{
    private const string PackagesDir = "keeper/packages";

    // Коды отказов — контракт для контроллера и фронта (S7a), устойчивые строки,
    // как wiki_* у WikiController.
    public const string CodeNotFound = "wiki_pkg_not_found";
    public const string CodeInvalid = "wiki_pkg_invalid";
    public const string CodeShape = "wiki_pkg_shape";
    public const string CodeExists = "wiki_pkg_exists";
    public const string CodeLimit = "wiki_pkg_limit";
    public const string CodeTransition = "wiki_pkg_transition";
    public const string CodeNotDecidable = "wiki_pkg_not_decidable";
    public const string CodeAuto = "wiki_pkg_auto";
    public const string CodeOpIndex = "wiki_pkg_op_index";
    public const string CodeAlreadyDecided = "wiki_pkg_already_decided";
    public const string CodeReasonRequired = "wiki_pkg_reason_required";
    public const string CodeNothingToDecide = "wiki_pkg_nothing_to_decide";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Класс правки («origin × op») и причины по-русски читаются человеком в «Входящих»
        // и в журнале: × и а в файле делают их нечитаемыми.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Блокировки записи пакета (ревью S4a, MAJOR 1), стиль WikiKeeper._locks: ConcurrentDictionary +
    // SemaphoreSlim. Ключ решений и переходов — корень вики + id пакета: разные вики и разные
    // пакеты друг друга не держат. Для Create — отдельная блокировка на корень: лимит считается
    // на всю вики, и CountOpen с записью обязаны быть в одной критической секции, иначе счёт
    // видит состояние до записи первого (TOCTOU). Вложения блоков нет: Create не берёт блок
    // пакета, Decide/Transition/AcceptAll не берут блок корня — взаимной блокировки нет.
    private static readonly ConcurrentDictionary<(string Root, string Id), SemaphoreSlim> _packageLocks = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _createLocks = new();

    private static T WithPackageLock<T>(string root, string id, Func<T> body)
    {
        var sem = _packageLocks.GetOrAdd((root, id), _ => new SemaphoreSlim(1));
        sem.Wait();
        try { return body(); }
        finally { sem.Release(); }
    }

    private static T WithCreateLock<T>(string root, Func<T> body)
    {
        var sem = _createLocks.GetOrAdd(root, _ => new SemaphoreSlim(1));
        sem.Wait();
        try { return body(); }
        finally { sem.Release(); }
    }

    /// <summary>Новый id пакета — безопасный GUID (форма «N»: без дефисов, одна строка).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Создать пакет: он рождается в `checking` (вердикт проверок ещё не вынесен),
    /// с временем сервера. Отказ: форма (CodeShape), уже существующий id (CodeExists),
    /// 21-й открытый (CodeLimit). Намерения и решения (class/decision/reason) принесены
    /// вызывающим — здесь они проверяются, но не вычисляются (политика — S4d).</summary>
    public static WikiPackageOpResult Create(string root, WikiPackage package)
    {
        if (!WikiPackage.IsSafeId(package.Id))
            return Fail(CodeShape, "id пакета не GUID");
        if (package.Status != WikiPackage.StatusChecking)
            return Fail(CodeShape, $"новый пакет рождается в «{WikiPackage.StatusChecking}», а не в «{package.Status}»");
        var errors = WikiPackage.ShapeErrors(package);
        if (errors.Count > 0)
            return Fail(CodeShape, errors[0]);
        // Существование, лимит и запись — под блокировкой корня вики (ревью S4a, MAJOR 1):
        // CountOpen и запись в одной критической секции, иначе две параллельные постановки
        // при 19 открытых видят 19 обе и дают 21. Проверка формы — вне блока: она не трогает
        // диск, и битый по форме пакет не должен держать корень.
        return WithCreateLock<WikiPackageOpResult>(root, () =>
        {
            if (System.IO.File.Exists(PathOf(root, package.Id)))
                return Fail(CodeExists, "пакет с таким id уже есть");
            // Лимит открытых на всю вики: 21-й не создаётся — разбор останавливается, а не копит
            // очередь (ADR-035, решение 4). Считается после проверки формы: битый по форме пакет
            // не должен занимать место в лимите.
            if (CountOpen(root) >= WikiPackage.MaxOpenPackages)
                return Fail(CodeLimit, $"открытых пакетов уже {WikiPackage.MaxOpenPackages}: лимит на всю вики");

            var now = DateTimeOffset.UtcNow;
            var created = package with
            {
                Status = WikiPackage.StatusChecking,
                StatusReason = null,
                // Дату ставит сервер (ревью S4a, минор 6): значение из входа игнорируется —
                // пакет с датой из будущего вечно сортировался бы первым в списке.
                CreatedAt = now,
                UpdatedAt = now,
            };
            Write(root, created);
            return Ok(created);
        });
    }

    /// <summary>Пакет автора human (правка человека, ADR-038): рождается сразу в `accepted` —
    /// человек уже решил, проверять и спрашивать нечего — и вне лимита открытых: «Входящие»,
    /// забитые ночным разбором, не должны не пускать владельца править свою вики. Форма и
    /// уникальность id проверяются как у Create.</summary>
    public static WikiPackageOpResult CreateHuman(string root, WikiPackage package)
    {
        if (package.Author != WikiPackage.AuthorHuman)
            return Fail(CodeShape, "CreateHuman — только для пакета автора human");
        if (!WikiPackage.IsSafeId(package.Id))
            return Fail(CodeShape, "id пакета не GUID");
        if (package.Status != WikiPackage.StatusAccepted)
            return Fail(CodeShape, $"пакет human рождается в «{WikiPackage.StatusAccepted}», а не в «{package.Status}»");
        var errors = WikiPackage.ShapeErrors(package);
        if (errors.Count > 0)
            return Fail(CodeShape, errors[0]);
        if (System.IO.File.Exists(PathOf(root, package.Id)))
            return Fail(CodeExists, "пакет с таким id уже есть");
        var now = DateTimeOffset.UtcNow;
        var created = package with { StatusReason = null, CreatedAt = now, UpdatedAt = now };
        Write(root, created);
        return Ok(created);
    }

    /// <summary>Прочитать пакет. Missing — файла нет (нормально для чужого id);
    /// Valid — карточка; Invalid — файл есть, но не читается как пакет v:1: пустой, битый
    /// JSON или не прошедший проверку формы. Ничего не применяется, список не падает.</summary>
    public static WikiPackageReadResult TryRead(string root, string id)
    {
        if (!WikiPackage.IsSafeId(id))
            return new WikiPackageReadResult(WikiPackageFileState.Missing, null);
        var full = PathOf(root, id);
        if (!System.IO.File.Exists(full))
            return new WikiPackageReadResult(WikiPackageFileState.Missing, null);
        var text = ReadOrNull(full);
        if (text is null || string.IsNullOrWhiteSpace(text))
            return new WikiPackageReadResult(WikiPackageFileState.Invalid, null);
        try
        {
            var pkg = JsonSerializer.Deserialize<WikiPackage>(text, JsonOpts);
            if (pkg is not null)
            {
                var errors = WikiPackage.ShapeErrors(pkg);
                if (errors.Count == 0)
                    return new WikiPackageReadResult(WikiPackageFileState.Valid, pkg);
            }
        }
        catch (JsonException)
        {
            // Битый JSON — состояние «невалиден», не исключение: его ловит и фоновый читатель.
        }
        catch (Exception)
        {
            // Прочее, чем бьётся валидный JSON с испорченными полями (null, чужой тип):
            // TryRead не пропускает исключений наружу (ревью S4a, MAJOR 2) — «Входящие»
            // живы и показывают такой файл числом, а не падают 500 на одном испорченном.
        }
        return new WikiPackageReadResult(WikiPackageFileState.Invalid, null);
    }

    /// <summary>Список пакетов вики: валидные — свежие первыми (дальше по id, чтобы порядок
    /// не зависел от обхода ФС); нечитаемые файлы — отдельным списком имён: они видны
    /// человеку как сломанные, но не притворяются пакетами и не пустым списком.</summary>
    public static WikiPackageListResult List(string root)
    {
        var dir = SafePath.Join(root, PackagesDir);
        var packages = new List<WikiPackage>();
        var invalid = new List<string>();
        if (!System.IO.Directory.Exists(dir))
            return new WikiPackageListResult([], []);
        foreach (var full in SafeEnumerateFiles(dir))
        {
            var name = Path.GetFileName(full);
            var id = Path.GetFileNameWithoutExtension(name);
            // Файл с не-GUID именем — не файл пакета: он не читается и не становится
            // пакетом, но остаётся видимым как лишний в каталоге пакетов.
            if (!WikiPackage.IsSafeId(id)) { invalid.Add(name); continue; }
            var res = TryRead(root, id);
            if (res.State == WikiPackageFileState.Valid && res.Package is { } pkg)
                packages.Add(pkg);
            else if (res.State == WikiPackageFileState.Invalid)
                invalid.Add(Path.GetFileName(full));
        }
        packages.Sort((a, b) =>
        {
            var byTime = b.CreatedAt.CompareTo(a.CreatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Id, b.Id);
        });
        invalid.Sort(StringComparer.Ordinal);
        return new WikiPackageListResult(packages, invalid);
    }

    /// <summary>Число открытых пакетов на всю вики: занимают лимит и нечитаемые файлы —
    /// закрытым их назвать нельзя (содержимое неизвестно), поэтому fail-closed: место
    /// держится, пока человек их не разберёт.</summary>
    public static int CountOpen(string root)
    {
        var list = List(root);
        return list.Packages.Count(p => WikiPackage.IsOpen(p.Status)) + list.InvalidFiles.Count;
    }

    /// <summary>Переход статуса. Недопустимый переход — отказ (таблица в WikiPackage).
    /// reason — текст для человека (причина отказа проверок, отказа применения, устаревания).</summary>
    public static WikiPackageOpResult Transition(string root, string id, string to, string? reason = null)
    {
        // Весь цикл — прочесть, проверить переход, записать — под блоком пакета: параллельное
        // решение не затрёт статус, поставленный здесь (ревью S4a, MAJOR 1, сценарий 2), и
        // само будет принято по уже изменённому статусу, а не по устаревшему чтению.
        return WithPackageLock(root, id, () =>
        {
            var (pkg, fail) = RequireValid(root, id);
            if (fail is not null) return fail.Value;
            if (!WikiPackage.CanTransition(pkg!.Status, to))
                return Fail(CodeTransition, $"переход «{pkg.Status}» → «{to}» недопустим");
            return WriteAndOk(root, pkg with { Status = to, StatusReason = reason, UpdatedAt = DateTimeOffset.UtcNow });
        });
    }

    /// <summary>Решение человека по спорной операции (REST S4a). Только пакет в `proposed`
    /// и только операция с решением `inbox`: неспорное решает политика, а не человек (409).
    /// Отклонить можно только с причиной. Решение по уже решённой операции — отказ
    /// (CodeAlreadyDecided): решение не меняется молча, HumanReason не стирается. Статус
    /// пакета меняется, когда решены ВСЕ спорные операции: все приняты → accepted, все
    /// отклонены → rejected, смешано → partially_accepted; пока хоть одна не решена — пакет
    /// остаётся proposed.</summary>
    public static WikiPackageOpResult DecideOperation(string root, string id, int index, bool accept, string? reason = null)
    {
        // Чтение, проверка и запись — под блоком пакета: два параллельных решения по разным
        // операциям одного пакета не теряют одно из них (ревью S4a, MAJOR 1, сценарий 1).
        return WithPackageLock(root, id, () =>
        {
            var (pkg, fail) = RequireValid(root, id);
            if (fail is not null) return fail.Value;
            if (pkg!.Status != WikiPackage.StatusProposed)
                return Fail(CodeNotDecidable, $"решают по операциям пакета в «{WikiPackage.StatusProposed}», сейчас «{pkg.Status}»");
            if (index < 0 || index >= pkg.Operations.Count)
                return Fail(CodeOpIndex, $"операции {index} в пакете нет");
            var op = pkg.Operations[index];
            if (op.Decision != WikiPackage.DecisionInbox)
                return Fail(CodeAuto, $"операция «{op.Op}» решена политикой как «{op.Decision}» — человек её не решает");
            // Решение по уже решённой операции — отказ (ревью S4a, минор 1): решение не
            // переворачивается молча и HumanReason не стирается; повтор того же решения —
            // тоже отказ, двойного эффекта нет.
            if (op.HumanDecision is not null)
                return Fail(CodeAlreadyDecided, $"операция {index} уже решена как «{op.HumanDecision}»: решение не меняется");
            if (!accept && string.IsNullOrWhiteSpace(reason))
                return Fail(CodeReasonRequired, "отклонить спорную операцию можно только с причиной");

            var decided = op with
            {
                HumanDecision = accept ? WikiPackage.HumanAccepted : WikiPackage.HumanRejected,
                HumanReason = accept ? null : reason,
            };
            var operations = pkg.Operations.ToArray();
            operations[index] = decided;
            return WriteAndOk(root, pkg with { Operations = operations, Status = StatusAfterDecisions(pkg, operations), UpdatedAt = DateTimeOffset.UtcNow });
        });
    }

    /// <summary>«Принять всё спорное пакета» (кнопка «Входящих»): все нерешённые `inbox`
    /// принимаются, пакет становится accepted. Пакета без спорных операций это не касается:
    /// решать нечего (отказ), неспорные применяет политика.</summary>
    public static WikiPackageOpResult AcceptAllContested(string root, string id)
    {
        // Тот же цикл «чтение → пересчёт → перезапись», что у DecideOperation: кнопка
        // «принять всё» гонится с одиночным решением по тому же пакету — блок общий.
        // Уже решённые операции блок сохраняет: accept-all их не переворачивает.
        return WithPackageLock(root, id, () =>
        {
            var (pkg, fail) = RequireValid(root, id);
            if (fail is not null) return fail.Value;
            if (pkg!.Status != WikiPackage.StatusProposed)
                return Fail(CodeNotDecidable, $"решают по операциям пакета в «{WikiPackage.StatusProposed}», сейчас «{pkg.Status}»");
            if (!pkg.Operations.Any(o => o.Decision == WikiPackage.DecisionInbox))
                return Fail(CodeNothingToDecide, "в пакете нет спорных операций: решать нечего");

            var operations = pkg.Operations
                .Select(o => o.Decision == WikiPackage.DecisionInbox && o.HumanDecision is null
                    ? o with { HumanDecision = WikiPackage.HumanAccepted, HumanReason = null }
                    : o)
                .ToArray();
            return WriteAndOk(root, pkg with { Operations = operations, Status = StatusAfterDecisions(pkg, operations), UpdatedAt = DateTimeOffset.UtcNow });
        });
    }

    // Вердикт по всем спорным операциям: пока хоть одна не решена — proposed (решение
    // человека не считается до конца). Решённые все → по их исходам. Неспорные операции
    // в вердикт не входят: их не человек решает.
    private static string StatusAfterDecisions(WikiPackage pkg, WikiPackage.Operation[] operations)
    {
        var contested = operations.Where(o => o.Decision == WikiPackage.DecisionInbox).ToArray();
        if (contested.Any(o => o.HumanDecision is null))
            return WikiPackage.StatusProposed;
        var accepted = contested.Count(o => o.HumanDecision == WikiPackage.HumanAccepted);
        if (accepted == contested.Length) return WikiPackage.StatusAccepted;
        if (accepted == 0) return WikiPackage.StatusRejected;
        return WikiPackage.StatusPartiallyAccepted;
    }

    private static (WikiPackage? Pkg, WikiPackageOpResult? Fail) RequireValid(string root, string id)
    {
        var res = TryRead(root, id);
        return res.State switch
        {
            WikiPackageFileState.Valid => (res.Package, null),
            WikiPackageFileState.Invalid => (null, Fail(CodeInvalid, "файл пакета не читается как пакет v:1")),
            _ => (null, Fail(CodeNotFound, "пакета нет")),
        };
    }

    private static WikiPackageOpResult WriteAndOk(string root, WikiPackage pkg)
    {
        Write(root, pkg);
        return Ok(pkg);
    }

    private static void Write(string root, WikiPackage pkg) =>
        WikiFileWriter.Write(root, $"{PackagesDir}/{pkg.Id}.json", JsonSerializer.Serialize(pkg, JsonOpts));

    // Путь имени из id: id уже проверен как GUID (разделителей пути в нём нет), JoinBelowRoot
    // добавляет вторую защиту — файл не может оказаться самим корнем.
    private static string PathOf(string root, string id) =>
        SafePath.JoinBelowRoot(root, $"{PackagesDir}/{id}.json");

    private static string? ReadOrNull(string full)
    {
        try { return System.IO.File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Каталог недоступен на середине обхода — список с тем, что уже прочитано: список
    // «Входящих» не должен падать из-за одного сбоя ФС.
    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        try { return System.IO.Directory.EnumerateFiles(dir, "*.json").ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static WikiPackageOpResult Ok(WikiPackage pkg) => new(true, "", null, pkg);
    private static WikiPackageOpResult Fail(string code, string reason) => new(false, code, reason, null);
}

/// <summary>Состояние файла пакета на диске — те же три, что у манифеста (ревью S2b-1, M1):
/// «нет», «валиден», «невалиден» нельзя схлопнуть: невалидный файл — это сломанная запись,
/// а не «пакет без операций».</summary>
public enum WikiPackageFileState
{
    Missing,
    Valid,
    /// <summary>Файл есть, но не читается как пакет v:1: пустой, битый JSON или не прошедший
    /// проверку формы. В список не входит как пакет, входит как нечитаемый файл.</summary>
    Invalid,
}

/// <summary>Результат чтения файла пакета.</summary>
public readonly record struct WikiPackageReadResult(WikiPackageFileState State, WikiPackage? Package);

/// <summary>Результат списка: валидные пакеты + имена нечитаемых файлов.</summary>
public readonly record struct WikiPackageListResult(
    IReadOnlyList<WikiPackage> Packages,
    IReadOnlyList<string> InvalidFiles);

/// <summary>Результат операции над пакетом: Ok + карточка после записи, либо код отказа и
/// текст для человека. Карточка возвращается и при Ok со unchanged статусом (решение по
/// одной из нескольких спорных операций ещё не меняет статус).</summary>
public readonly record struct WikiPackageOpResult(bool Ok, string Code, string? Reason, WikiPackage? Package);
