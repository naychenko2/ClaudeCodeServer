using System.Security.Cryptography;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spheres;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Применение пакета (ADR-035, решение 4, решение 10): S4b-1 — проверки до записи (шаги 1–3),
// S4b-2 — сама запись (шаги 4–6) и откат при сбое. Публичная дверь одна: ApplyAsync проходит
// оба цикла. PrepareAsync (шаги 1–3) и WriteAsync (шаги 4–6) — internal: тот же разделитель,
// которым их тестируют: между проверкой и записью тест ставит диск с состоянием «между ними»
// (чужую правку, сбой ФС) — определённо, без рандомной гонки.
// Заявка и статусы — только через WikiPackageStore.Transition под его блокировкой (единственная
// дверь в статусы): применить пакет дважды нельзя — первый вызов ведёт его в «applying»,
// второй отказан по состоянию на диске, а не по переданной карточке.
//
// Порядок:
// 1. Чтение пакета с диска под его id (TryRead) и заявка: accepted | partially_accepted →
//    applying через Transition (блок пакета): две параллельные заявки одного пакета дают
//    ровно один переход. Гейт и рендер судят по ДИСКОВОЙ копии (Status, Operations,
//    HumanDecision) — аргумент метода несёт только идентификатор (ревью S4b-1, MAJOR 1):
//    устаревшая карточка вызывающего не может обойти ни статус на диске («proposed» на
//    диске → отказ), ни решение человека по спорной операции.
// 2. Блокировка раздела через Хранителя (тот же замок, что у записи страницы; своего второго
//    блока того же ключа в потоке нет — иначе зависание) и под ней: модель мимо кэша
//    (BuildFresh, шаг 1 решения 4), сверка манифеста раздела с диском по sha256 БАЙТОВ
//    (дрейф — стоп) и baseHashes пакета с текущим состоянием.
// 3. Рендер операций в памяти общим рендером (WikiPlanValidator.RenderApply) и весь
//    WikiGate на итоговом состоянии; цепочка «вверх» — WikiSections.UpChain, не «видимые»
//    разделы (видимость шире: она для recall и UI, право на ссылку даёт только цепочка).
//    Итог — снимок базового состояния каждой коснутой страницы (PrepareResult.BaseSnapshot:
//    хэш манифеста на момент проверки; null — страницы в манифеста нет, это create).
// 4. Прообразы каждой коснутой страницы — keeper/preimages/{pkgId}/ по тому же относительному
//    пути, что у страницы под pages/, байтами и с fsync: между бэкапами откат держат они
//    (решение 13). Пустой прообраз = страницы не было (create): возврат по нему — удалить файл.
// 5. Каждая страница — сверка базового состояния с тем, на котором строился рендер (байты
//    диска и запись манифеста против снимка шага 3), затем temp + File.Move (WikiFileWriter,
//    тот же атомарный писатель, что у Хранителя).
// 6. Манифест (хэши байтов, прочитанных с диска) → строка log.jsonl (op=apply, политика
//    auto|inbox, источник, список изменённых страниц) → проекции раздела → статус «applied»
//    через Transition → инвалидация кэша графа → событие проекта (WikiChanged, только для
//    раздела проекта: у «Общего» и «Личного» нет адресата — тот же порядок, что у Хранителя).
//
// Запись состояния применения (ревью S4b-3, MAJOR 1) — перед манифестом: keeper/applied/{pkgId}.json,
// карта страница → sha256 записанных байтов. Её держит откат: манифест описывает последнее состояние
// страницы, а не состояние на момент этого применения, и откат по манифесту вернул бы прообраз этого
// пакета поверх записи более позднего. Рядом с прообразами, но вне их каталога: тот каталог обходят
// откат и сторож старта как список страниц — служебный файл внутри него стал бы страницей
// «applied.json». Пакет без этой записи (до этой меры) откат не принимает: см. WikiPackageRevert.
//
// Порядок шага 6 — не дословный список ADR: журнал перед проекциями, иначе _log.md раздела
// не донесёт строку этого применения (проекции — производные от log.jsonl). Диффа в строке
// журнала нет: он восстанавливается из прообразов и текущего файла (тот же источник, чем
// держится откат) — «Журнал» на фронте (S7a) собирает его из них.
//
// Проверка и запись — два блока раздела, а не один: между ними состояние могло измениться
// (чужой писатель вне блокировки, другой применённый пакет). Опасность снимает не длина
// блока, а шаг 5: он не пишет поверх того, чего не видел, — расхождение с базовым снимком
// остановка, а не перезапись. Плюс заявка: «applying» держит повторное применение того же
// пакета до конца цикла (решение 10).
//
// Сбой на 4–6 — откат: возвращаются только страницы, записанные этим вызовом (по их
// прообразам), и — там, где этот вызов успел их изменить, — их записи в манифесте
// (прежний хэш из снимка проверки; для create — удаление) с проекциями раздела;
// остальное — чужая правка, файл, на котором споткнулась проверка, — не тронуто. Манифест
// без этой правки остался бы с хэшем применённого состояния при возвращённых файлах — и
// следующий PrepareAsync любого пакета раздела отказал бы на «дрейфе» (ревью S4b-2, M1).
// Статус «apply_failed» с причиной; пакет не остаётся в «applying» никогда: незавершённый
// после падения процесса доводит сторож старта (WikiApplyStartupGuard).
// Любой неожиданный вход — результат-отказ с причиной, не исключение наружу.
//
// baseHashes: несущий здесь смысл — не «хэш совпал», а «на что опиралось намерение»:
// страница-база исчезла или замещаемое утверждение ушло из неё → stale (авто-rebase нет,
// пакет требует нового разбора); страница менялась так, что намерение цело (чужой append) —
// проверка проходит. Дословная сверка хэшей ничего не решает одна: базовый текст в пакете
// не хранится, решает наличие цели, его проверяет рендер.
//
// Цитата в пакете не хранится (хвост ревью S4d): при применении Хранитель выводит её из
// текста источника заново — через QuoteMatcher, не из записи: утверждение может быть своей
// цитатой только если оно дословно в источнике (мера 1: без дословной цитаты не применяется
// ни при какой политике). Текст источника и заголовок карточки приносит вызывающий —
// тот, кто пакет собрал (захват источника — S6h).
public sealed class WikiApplyService
{
    // Коды отказов — контракт вызывающего (REST «Входящих», S7a), тот же стиль, что wiki_pkg_*.
    public const string CodeShape = "wiki_apply_shape";
    public const string CodeStatus = "wiki_apply_status";
    public const string CodeClaim = "wiki_apply_claim";
    public const string CodeOff = "wiki_apply_off";
    public const string CodeSource = "wiki_apply_source";
    // Путь решения человека (S4g): карточки источника пакета в его разделе на диске нет —
    // применять нечем. Тот же код и на неоднозначности (ревью S4g, MAJOR): источник опознан
    // несколькими карточками раздела — прежняя и новая версия, точная не названа. Отказ после
    // заявки: пакет «apply_failed» с причиной, решения человека сохранены (они записаны до
    // применения).
    public const string CodeSourceUnavailable = "wiki_apply_source_unavailable";
    public const string CodeManifest = "wiki_apply_manifest";
    public const string CodeDrift = "wiki_apply_drift";
    public const string CodeStale = "wiki_apply_stale";
    public const string CodeGate = "wiki_apply_gate";
    // Внутренний сбой проверки (ФС, чтение файла, неожиданный вход): пакет ведётся в
    // «apply_failed» с текстом сбоя — исключение наружу не выходит (ревью S4b-1, MAJOR 2).
    public const string CodeInternal = "wiki_apply_internal";

    // Значение поля op строки журнала для применения пакета (шаг 6, мера 6): та же форма, что
    // у op=adopt у Хранителя; по нему «Журнал» отличает применение от системных операций.
    private const string LogOpApply = "apply";

    // Форма записи состояния применения: ключи-пути по-русски читаются человеком в файле, который
    // он открывает при отказе отката (тот же приём, что у манифеста и журнала).
    private static readonly JsonSerializerOptions AppliedRecordJsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly ISphereDirectory _spheres;
    private readonly IProjectEventLogService? _events;
    private readonly string _dataDir;

    public WikiApplyService(NotesService notes, WikiKeeper keeper, ISphereDirectory spheres, IConfiguration config,
        IProjectEventLogService? events = null)
    {
        _notes = notes;
        _keeper = keeper;
        _spheres = spheres;
        _events = events;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Контекст применения: что вызывающий знает об источнике пакета — заголовок
    /// карточки (цель сносок рендера, она обязана быть в разделе или вверх по цепочке) и
    /// текст источника (по нему заново выводится цитата).</summary>
    public sealed record ApplyContext(string? SourceCard, string? SourceText);

    /// <summary>Итог проверок (шаги 1–3). Ready — «готово к записи»: Rendered — изменённые
    /// рендером страницы раздела, в порядке их первой правки (он и есть порядок записи).
    /// Отказ — код и причина; для провала гейта — весь перечень нарушений. На диске изменён
    /// только статус пакета.</summary>
    public readonly record struct PrepareResult(
        bool Ready,
        string? Code,
        string? Reason,
        IReadOnlyList<string> Violations,
        IReadOnlyList<WikiGate.Draft> Rendered)
    {
        /// <summary>Раздел пакета: под его блокировке идёт запись (шаги 4–6).</summary>
        public string? Section { get; init; }

        /// <summary>Снимок базового состояния каждой коснутой страницы: хэш её записи в
        /// манифесте на момент проверки; null — страницы в манифеста нет (create). Шаг 5
        /// сверяет с этим снимком байты диска и запись манифеста: рендер построен на том,
        /// что на диске сейчас, — иначе запись остановлена, а не наложена поверх чужого.</summary>
        public IReadOnlyDictionary<string, string?>? BaseSnapshot { get; init; }
    }

    /// <summary>Итог применения целиком (шаги 1–6). Applied — страницы записаны, манифест и
    /// журнал обновлены, проекции перегенерированы, пакет в «applied». Отказ — код и причина
    /// (те же коды, что у проверок): записанное до сбоя откатано по прообразам, чужие файлы
    /// не тронуты, на диске изменён только статус пакета.</summary>
    public readonly record struct ApplyResult(bool Applied, string? Code, string? Reason, IReadOnlyList<string> Violations);

    /// <summary>Применить пакет по его идентификатору целиком: состояние (Status, Operations,
    /// HumanDecision) читается с диска под id, карточка аргумента несёт только id
    /// (ревью S4b-1, MAJOR 1). Сначала проверки и рендер в памяти (PrepareAsync, шаги 1–3),
    /// затем прообразы, запись, журнал и статус (WriteAsync, шаги 4–6). Отказ любой фазы —
    /// результат-отказ с причиной: исключения наружу нет.
    ///
    /// Два вызывающих — два источника карточки и текста (S4g, ADR-035 §5): контекст принесён
    /// (авто-поток инджеста, у него свежий захват) или контекста нет (решение человека через
    /// REST) — тогда карточка ищется на диске в разделе самого пакета, а дословность не
    /// перепроверяется: она подтверждена при разборе, текст источника нигде не хранится
    /// (в карточку он не пишется, в пакете — только намерения). См. PrepareChecked.</summary>
    public async Task<ApplyResult> ApplyAsync(string ownerId, WikiPackage? package, ApplyContext? context = null)
    {
        var prepared = await PrepareAsync(ownerId, package, context).ConfigureAwait(false);
        if (!prepared.Ready)
            return new ApplyResult(false, prepared.Code, prepared.Reason, prepared.Violations);
        return await WriteAsync(ownerId, package!.Id, prepared, context!).ConfigureAwait(false);
    }

    internal async Task<PrepareResult> PrepareAsync(string ownerId, WikiPackage? package, ApplyContext? context)
    {
        // Fail-closed входа: null и пустой владелец — отказ с причиной, не NRE и не исключение
        // (уроки S4a/S6b-2). Форма пакета проверяется не по карточке аргумента: дальше
        // работает только дисковая копия, её форма — у TryRead (ревью S4b-1, MAJOR 1).
        if (package is null)
            return Fail(CodeShape, "пакет null");
        if (string.IsNullOrWhiteSpace(ownerId))
            return Fail(CodeShape, "владелец пустой");
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Fail(CodeOff, "вики этого владельца не заведена");

        var root = WikiPaths.Root(_dataDir, ownerId);
        var read = WikiPackageStore.TryRead(root, package.Id);
        if (read.State == WikiPackageFileState.Missing)
            return Fail(CodeClaim, "пакета нет на диске: применять нечего");
        if (read.State == WikiPackageFileState.Invalid)
            return Fail(CodeShape, "файл пакета не читается как пакет v:1");
        var disk = read.Package!;

        // Статусный гейт — по дисковому состоянию, не по карточке вызывающего: «proposed»
        // на диске — отказ, решение человека по спорным операциям ещё не вынесено.
        var status = disk.Status;
        if (!WikiPackage.IsOpen(status))
            return Fail(CodeStatus, $"пакет в конечном статусе «{status}»: применить его дважды нельзя");
        if (status is not (WikiPackage.StatusAccepted or WikiPackage.StatusPartiallyAccepted))
            return Fail(CodeStatus, status == WikiPackage.StatusApplying
                ? "пакет уже в «applying»: его применяет другой вызов"
                : $"пакет в «{status}» не принят к применению");
        // Принесённый контекст проверяется так же строго и так же рано: полупустой контекст —
        // отказ до заявки, «applying» на входе с битым контекстом не открывается. Контекста нет —
        // путь решения человека через REST (S4g): источник берётся с диска под блокировкой
        // раздела (PrepareChecked), и его отсутствие — отказ уже там, с «apply_failed».
        if (context is not null)
        {
            if (context.SourceCard is not { Length: > 0 })
                return Fail(CodeSource, "карточка источника не задана: сноскам рендера нечем назвать цель");
            if (context.SourceText is not { Length: > 0 })
                return Fail(CodeSource, "текст источника не задан: цитату вывести не из чего");
        }

        // Заявка пакета — единственная дверь в «applying»: Transition перечитывает состояние
        // под блоком id. Два параллельных вызова одного пакета: первый пишет «applying»,
        // второй под тем же блоком видит его и отказан — двойного перехода нет.
        var claim = WikiPackageStore.Transition(root, disk.Id, WikiPackage.StatusApplying);
        if (!claim.Ok)
            return Fail(CodeClaim, claim.Reason ?? "переход в «applying» не удался");

        // Блокировка раздела — та же, что у записи страницы (решение 10): применение не берёт
        // второй блокировки того же ключа и не пишет под чужой правкой посреди проверки.
        // Работаем с дисковой копией: её Operations и HumanDecision — те, что на диске.
        return await _keeper.WithSectionsLockedAsync(ownerId, [disk.Section],
            () => Task.FromResult(PrepareUnderLock(ownerId, root, disk, context!)));
    }

    /// <summary>Запись проверенного пакета (шаги 4–6). internal — та же граница фазы, что у
    /// PrepareAsync: тест ставит между ними диск с нужным состоянием (чужую правку, файл,
    /// который нельзя прочитать) и видит откат, а не гонку.</summary>
    internal Task<ApplyResult> WriteAsync(string ownerId, string packageId, PrepareResult prepared, ApplyContext context)
    {
        if (prepared.Section is not { Length: > 0 } || prepared.BaseSnapshot is null)
            return Task.FromResult(new ApplyResult(false, CodeShape,
                "запись невозможна: у результата проверок нет раздела и снимка базового состояния",
                ["запись невозможна: нет снимка проверки"]));
        var root = WikiPaths.Root(_dataDir, ownerId);
        // Блок раздела — тот же замок, что у проверок и у записи страницы Хранителя.
        // WritePageAsync отсюда не зовём: он взял бы второй блок той же пары (зависание),
        // а весь шаг 5 обязан идти под тем же блоком, что и сверка.
        return _keeper.WithSectionsLockedAsync(ownerId, [prepared.Section],
            () => Task.FromResult(WriteUnderLock(ownerId, root, packageId, prepared, context)));
    }

    private ApplyResult WriteUnderLock(string ownerId, string root, string packageId, PrepareResult prepared, ApplyContext context)
    {
        var written = new List<string>();
        try
        {
            return WriteBody(ownerId, root, packageId, prepared, context, written);
        }
        catch (Exception ex)
        {
            // Любой неожиданный сбой записи (ФС, нечитаемый манифест, пустая страница рендера) —
            // тот же контракт, что у проверки: откат записанного по прообразам и «apply_failed»
            // с причиной, а не исключение вызывающему и не «applying» навсегда (ревью S4b-1, MAJOR 2).
            return FailAfterWrite(root, packageId, prepared.Section!, prepared.BaseSnapshot!, written, CodeInternal,
                $"внутренний сбой записи применения: {ex.Message}");
        }
    }

    private ApplyResult WriteBody(string ownerId, string root, string packageId, PrepareResult prepared, ApplyContext context, List<string> written)
    {
        // Судим по дисковому пакету (урок S4b-1): здесь нужен он целиком — источник и решения
        // операций идут в журнал, их PrepareResult не несёт.
        var read = WikiPackageStore.TryRead(root, packageId);
        if (read.State != WikiPackageFileState.Valid)
            return new ApplyResult(false, CodeClaim, "файл пакета не читается как пакет v:1: запись не начата",
                ["файл пакета не читается: запись не начата"]);
        var package = read.Package!;
        if (package.Status != WikiPackage.StatusApplying)
            return new ApplyResult(false, CodeStatus,
                $"пакет в «{package.Status}», а не в «{WikiPackage.StatusApplying}»: его записывает не этот вызов",
                ["статус пакета не «applying»"]);
        var section = package.Section;
        if (section != prepared.Section)
            return new ApplyResult(false, CodeInternal,
                $"раздел пакета «{section}» не тот, что проверялся («{prepared.Section}»): запись остановлена",
                ["раздел пакета изменился между проверкой и записью"]);

        var snapshot = prepared.BaseSnapshot!;
        var manifest = WikiManifest.TryRead(root, section);
        if (manifest.State == WikiManifestState.Invalid)
            throw new InvalidOperationException($"манифест раздела «{section}» невалиден — запись невозможна");

        foreach (var draft in prepared.Rendered)
        {
            if (draft.Content.Length == 0)
                throw new InvalidOperationException($"рендер дал пустую страницу «{draft.Path}»: пустой страницы вики не бывает");

            var rel = "pages/" + draft.Path;
            var full = SafePath.JoinBelowRoot(root, rel);
            // Шаг 5, сверка базы: байты диска и запись манифеста обязаны быть тем, что было при
            // проверке. Расхождение — правка мимо Хранителя или чужое применение: стоп. Чужое
            // остаётся на диске: молчаливой перезаписи нет (тот же закон, что у WritePageAsync).
            var diskNow = File.Exists(full) ? File.ReadAllBytes(full) : null;
            var baseHash = snapshot.TryGetValue(draft.Path, out var b) ? b : null;
            var diskHash = diskNow is null ? null : Sha256OfBytes(diskNow);
            var recorded = manifest.Manifest.TryGetValue(draft.Path, out var cur) ? cur : null;
            if (diskHash != baseHash || recorded != baseHash)
                return FailAfterWrite(root, packageId, section, snapshot, written, CodeDrift,
                    $"базовое состояние «{draft.Path}» изменилось после проверки (правка мимо Хранителя или чужое применение): запись остановлена, чужая правка не тронута");

            // Шаг 4: прообраз — байты страницы до записи, по тому же относительному пути.
            // Пустой прообраз = страницы не было (create): возврат по нему — удалить файл.
            // fsync: прообразы — то, чем откатывают после падения процесса (решение 13).
            WriteBytesAtomic(root, PreimageRel(packageId, draft.Path), diskNow ?? []);

            // Шаг 5: temp + File.Move — тот же атомарный писатель, что у Хранителя: цель либо
            // прежняя, либо новая целиком, temp-мусора нет ни в одном исходе.
            WikiFileWriter.Write(root, rel, draft.Content);
            written.Add(draft.Path);
        }

        // Шаг 6, манифест: хэши байтов, прочитанных с диска, а не из строки рендера (та же
        // форма, что у Хранителя): следующая сверка сравнивает то, что реально лежит в файле.
        var applied = new Dictionary<string, string>(written.Count, StringComparer.Ordinal);
        foreach (var p in written)
        {
            var bytes = File.ReadAllBytes(SafePath.JoinBelowRoot(root, "pages/" + p));
            // Основа версии — для «Принять как мою правку», если поверх неё правят мимо веб-морды.
            applied[p] = WikiBaselines.Save(root, bytes);
        }

        // Запись состояния применения (ревью S4b-3, MAJOR 1) — до манифеста: она описывает ровно то,
        // что записал этот пакет, и не зависит от того, что станет со страницей позже. Её сбой — тот
        // же путь, что у любого сбоя шага 6: откат записанного по прообразам и «apply_failed».
        WriteBytesAtomic(root, AppliedRecordRel(packageId), AppliedRecordBytes(applied));

        WikiManifest.RecordApplied(root, section, applied);

        // Строка журнала (мера 6): политика пакета, его источник, изменённые страницы.
        // Политика — «inbox», если хоть одна применённая операция пришла из «Входящих» (её
        // решил человек), иначе «auto»: журнал обязан показывать, что человека спросили, а не
        // только то, что он принял. Диффа в строке нет: он собирается из прообразов и
        // текущего файла (тот же источник, чем держится откат) — на фронте (S7a).
        var policy = package.Operations.Any(o => Applies(o) && o.Decision == WikiPackage.DecisionInbox)
            ? WikiPackage.DecisionInbox
            : WikiPackage.DecisionAuto;
        // Правка человека — своя строка журнала: op edit|restore и actor «user» (ADR-038, решение 4):
        // на этом стоит «Журнал» с фильтром «ИИ поменял моё» и условие «источник новее правки».
        if (package.Author == WikiPackage.AuthorHuman)
            WikiManifest.AppendLog(root, package.Action!, section, written, SourceOf(package), "user");
        else
            WikiManifest.AppendLog(root, LogOpApply, section, written, SourceOf(package), "keeper", policy,
                overrodeHuman: package.Operations.Any(WikiPackage.IsAutoOverrideOfHuman));

        // Проекции — после журнала: _log.md — производная от log.jsonl и без этой строки
        // остался бы на полшага позади применения.
        WikiProjections.RegenerateSection(root, section);

        // Статус — до побочных эффектов: «applied» значит «файлы и записи согласованы», и с
        // него пакет замкнут (повторить применение нельзя).
        var done = WikiPackageStore.Transition(root, packageId, WikiPackage.StatusApplied);
        if (!done.Ok)
            // Файлы применены и согласованы с манифестом, но пакет не закрыт: переход, который
            // его закрыл бы, уже не в таблице. Откатывать здесь — снять то, что уже принято
            // человеком и лежит на диске; состояние доведёт сторож старта (он откатит по
            // прообразам и назовёт причину). Вызывающий видит исход, а не молчание (минор 4).
            return new ApplyResult(false, CodeClaim,
                $"файлы применены, статус «applied» не записан: {done.Reason ?? done.Code}",
                ["файлы применены, статус «applied» не записан"]);

        // Граф обязан видеть запись сразу (шаг 6 транзакции). Событие — только раздела
        // проекта: у «Общего» и «Личного» нет адресата (тот же порядок, что у Хранителя).
        _notes.Invalidate(ownerId);
        foreach (var p in written)
            AppendEvent(ownerId, section, p, package.Author == WikiPackage.AuthorHuman);
        return new ApplyResult(true, null, null, []);
    }

    /// <summary>Берётся ли операция в рендер и на диск: «auto» — всегда, «inbox» — только с
    /// принятым решением человека (решение читается из HumanDecision дисковой копии, не из
    /// тела пакета). Одно правило на шаг 3 (рендер) и на шаг 6 (политика журнала): «что
    /// применилось» в них не может разойтись.</summary>
    private static bool Applies(WikiPackage.Operation op) =>
        op.Decision == WikiPackage.DecisionAuto ||
        (op.Decision == WikiPackage.DecisionInbox && op.HumanDecision == WikiPackage.HumanAccepted);

    /// <summary>Карточка источника строки журнала — карточки всех источников пакета: по ним
    /// же человек откатывает «всё из этого источника» (мера 5).</summary>
    private static string SourceOf(WikiPackage package) =>
        string.Join(", ", package.Sources.Select(s => s.Ref));

    private void AppendEvent(string ownerId, string sectionKey, string pageKey, bool byHuman = false)
    {
        if (WikiSections.TryProjectId(sectionKey, out var projectId))
            _events?.Append(projectId, ownerId, ProjectEventTypes.WikiChanged, byHuman ? "user" : "keeper",
                byHuman ? $"Человек правит страницу вики «{pageKey}»" : $"Хранитель применил страницу вики «{pageKey}»");
    }

    // Отказ после начала записи: откат — только того, что записал этот вызов. Всё остальное
    // (чужая правка, файл, на котором споткнулась сверка) остаётся как есть: молча отменить
    // то, чего не трогал, права нет — тот же закон, что у дрейфа у Хранителя.
    private static ApplyResult FailAfterWrite(string root, string packageId, string section,
        IReadOnlyDictionary<string, string?> baseSnapshot, List<string> written, string code, string reason)
    {
        var (restored, failed) = RestoreFromPreimages(root, packageId, written);
        var text = reason;
        if (restored.Count > 0)
            text += $" (откатано по прообразам: {string.Join(", ", restored)})";
        if (failed.Count > 0)
            text += $" (откат не удался: {string.Join("; ", failed)} — файл мог остаться применённым при закрытом пакете)";
        // Манифест следует за файлами: что возвращено к прежнему состоянию, то и записано
        // обратно (для create — прежней записи нет вовсе, она убирается). Без этого сбой ПОСЛЕ
        // записи манифеста оставлял в нём хэш применённого состояния при возвращённых файлах,
        // и следующий PrepareAsync любого пакета раздела отказывал на «дрейфе»: раздел был
        // заблокирован до ручного adopt (ревью S4b-2, M1).
        // Правится ровно то, что отличается от базового состояния: при сбое ДО шага 6
        // (сверка базы, чтение файла) манифест не менялся и уже описывает возвращённые файлы:
        // править его и перегенерировывать проекции — значит трогать то, что не сломано.
        // Не откатываются страницы, чей возврат не удался: их
        // файл мог остаться применённым, и его хэш в манифесте — хотя бы согласован с диском.
        if (restored.Count > 0)
        {
            var manifest = WikiManifest.TryRead(root, section);
            var rollback = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (manifest.State != WikiManifestState.Invalid)
                foreach (var p in restored)
                {
                    var current = manifest.Manifest.TryGetValue(p, out var c) ? c : null;
                    var baseline = baseSnapshot.TryGetValue(p, out var b) ? b : null;
                    if (current != baseline)
                        rollback[p] = baseline;
                }
            if (rollback.Count > 0)
            {
                try
                {
                    WikiManifest.RollbackTo(root, section, rollback);
                    // Проекции — за файлами и манифестом: они производные от них, как и в шаге 6.
                    WikiProjections.RegenerateSection(root, section);
                }
                catch (Exception ex)
                {
                    // Откат записей — часть того же отказа: его сбой называется в причине,
                    // а не превращает результат-отказ в исключение вызывающему.
                    text += $" (откат записей манифеста не удался: {ex.Message} — хэш применённого состояния мог остаться в манифесте)";
                }
            }
        }
        var fail = FailApply(root, packageId, code, text);
        return new ApplyResult(false, fail.Code, fail.Reason, fail.Violations);
    }

    /// <summary>Вернуть страницы к состоянию до пакета по их прообразам. Пустой прообраз =
    /// страницы не было (create) → файл удалить. Файла прообразов нет → этой записи не было:
    /// откатывать нечего. Сбой на одном файле не останавливает остальные (окно чистки, а не
    /// половина отката) и называется в причине.</summary>
    private static (List<string> Restored, List<string> Failed) RestoreFromPreimages(string root, string packageId, IReadOnlyList<string> pageKeys)
    {
        var restored = new List<string>();
        var failed = new List<string>();
        foreach (var page in pageKeys)
        {
            try
            {
                var pre = SafePath.Join(root, PreimageRel(packageId, page));
                if (!File.Exists(pre))
                    continue;
                var bytes = File.ReadAllBytes(pre);
                var full = SafePath.Join(root, "pages/" + page);
                if (bytes.Length == 0)
                    File.Delete(full);
                else
                    WriteBytesAtomic(root, "pages/" + page, bytes);
                restored.Add(page);
            }
            catch (Exception ex)
            {
                // Любая причина, а не только файловая: сбой этого файла обязан попасть в
                // текст отказа, а не выйти из отката исключением (ревью S4b-2, п. 4).
                failed.Add($"{page}: {ex.Message}");
            }
        }
        return (restored, failed);
    }

    // Тот же относительный путь, что у страницы под pages/: прообраз читается рядом с тем,
    // что он описывает, и не требует обратного разбора имени. internal: та же форма нужна
    // откату пакета (WikiPackageRevert): местоположение прообразов определено в одном месте.
    internal static string PreimagesRel(string packageId) =>
        $"keeper/preimages/{packageId}";

    internal static string PreimageRel(string packageId, string pageKey) =>
        $"{PreimagesRel(packageId)}/{pageKey}";

    /// <summary>Запись состояния применения пакета: страница → sha256 тех байтов, которые записал
    /// этот пакет. internal: её читает откат (WikiPackageRevert) — местоположение и форма
    /// определены здесь, в одном месте, как прообразы.</summary>
    internal static string AppliedRecordRel(string packageId) =>
        $"keeper/applied/{packageId}.json";

    // Порядок ключей фиксированный (как у манифеста): файл — данные, его diff между применениями
    // должен читаться, а не зависеть от обхода словаря. internal: ту же форму пишет и снятие
    // сносок (WikiRetractService, S4f-2): форма записи состояния определена здесь, в одном месте.
    internal static byte[] AppliedRecordBytes(IReadOnlyDictionary<string, string> applied)
    {
        var sorted = new Dictionary<string, string>(applied.Count, StringComparer.Ordinal);
        foreach (var kv in applied.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sorted[kv.Key] = kv.Value;
        return JsonSerializer.SerializeToUtf8Bytes(sorted, AppliedRecordJsonOpts);
    }

    /// <summary>Записать байты атомарно и с fsync: temp в той же папке → Flush(true) → Move.
    /// Для прообразов и их возврата: это то, что держит откат после падения процесса, и его
    /// нельзя отдавать половине записи. Не WikiFileWriter: тот пишет СТРОКУ (свою кодировку),
    /// а прообраз и возврат меряются байтами — «тот же файл» здесь значит именно байты.</summary>
    // internal: та же операция нужна сторожу старта (WikiApplyStartupGuard) — вторая половина
    // этого же отката.
    internal static void WriteBytesAtomic(string root, string relPath, byte[] bytes)
    {
        var full = SafePath.Join(root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                fs.Flush(true); // fsync: данные на диске до того, как файл станет прообразом
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            // Исходная ошибка идёт дальше; ошибка уборки её не подменяет (тот же порядок, что у WikiFileWriter).
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // Тело проверки под блоком раздела обёрнуто: файловые чтения (манифест, байты страниц,
    // обход каталога) и сборка модели могут бросить — наружу исключение не уходит: пакет
    // ведётся в «apply_failed» с текстом сбоя, а не остаётся в «applying» навсегда
    // (ревью S4b-1, MAJOR 2).
    private PrepareResult PrepareUnderLock(string ownerId, string root, WikiPackage package, ApplyContext context,
        (string Page, string? Content)? baseOverride = null)
    {
        try
        {
            return PrepareChecked(ownerId, root, package, context, baseOverride);
        }
        catch (Exception ex)
        {
            return FailApply(root, package.Id, CodeInternal,
                $"внутренний сбой проверки применения: {ex.Message}");
        }
    }

    private PrepareResult PrepareChecked(string ownerId, string root, WikiPackage package, ApplyContext context,
        (string Page, string? Content)? baseOverride)
    {
        var section = package.Section;

        // Шаг 1 решения 4: модель строится мимо кэша графа — фактическое состояние диска,
        // а не кэш с TTL 2 с. Состояние проверки: страницы раздела и цепочки «вверх»
        // (UpChain — право на ссылку даёт только цепочка, не «видимые» разделы).
        var (state, upChain) = BuildState(ownerId, section);
        if (baseOverride is { } over)
            OverrideBase(state, over.Page, over.Content);

        // Шаг 2: манифест раздела против диска, по sha256 байтов. Любое расхождение — дрейф:
        // стоп, ничего не применять (молчаливой перезаписи чужой правки нет, как у WritePage).
        var mres = WikiManifest.TryRead(root, section);
        if (mres.State == WikiManifestState.Invalid)
            return FailApply(root, package.Id, CodeManifest, $"манифест раздела «{section}» невалиден — применение невозможно");
        var manifest = mres.Manifest;

        var drift = CheckSectionAgainstDisk(root, section, manifest);
        if (drift is not null)
            return FailApply(root, package.Id, CodeDrift, drift);

        // baseHashes против текущего состояния: отсутствие страницы-базы — база намерения
        // исчезла (stale, авто-rebase нет). Изменённая, но целая база — не отказ: что именно
        // сломалось, решает рендер (ниже), а не сам факт смены хэша.
        foreach (var op in package.Operations)
            foreach (var (page, baseHash) in op.BaseHashes)
            {
                if (!manifest.TryGetValue(page, out var current))
                    return StaleResult(root, package.Id,
                        $"страница «{page}» — база операции «{op.Op}» — исчезла из раздела (база {baseHash} больше не несущая): пакет требует нового разбора");
                // Совпала — база цела; изменилась — продолжение проверки в рендере: намерение
                // живо, пока цела его цель (чужой append той же страницы — проверка проходит).
            }

        // Пакет human (ADR-038): правку человека рендерит WikiHumanRender — карточку правки он
        // строит сам, источника «с диска или от вызывающего» у него нет.
        if (package.Author == WikiPackage.AuthorHuman)
            return PrepareHuman(root, package, section, upChain, state, manifest);

        // Шаг 3: рендер операций в памяти и весь гейт на итоговом состоянии. Карточка
        // источника и её текст — по двум путям (S4g):
        // — контекст принесён (авто-поток инджеста): карточка ищется по заголовку (раздел и
        //   цепочка вверх), дословность проверяется по принесённому тексту источника (мера 1);
        // — контекста нет (после решения человека через REST): карточка берётся с диска — она
        //   уже в прочитанном состоянии раздела, её записал захват при разборе (WikiSourceCapture).
        //   Только раздел самого пакета: чужих разделов этот путь не читает. Ни в карточке,
        //   ни в пакете текста источника нет (в карточку он не пишется, пакет несёт намерения),
        //   здесь он не перепроверяется: дословность подтверждена при разборе об текст источника
        //   (WikiPlanValidator.cs:199), а в рендер идёт ровно тот Text, что лежит в Operations
        //   пакета. Пустой sourceTexts для карточки — то же «этой карточки здесь нет», что
        //   accepted-раньше сноска в GivenOnly (WikiGate.cs:214-218): она не отвергает новую запись.
        // Нет карточки в разделе — отказ после заявки: «apply_failed» с причиной, решения
        // человека на диске сохранены.
        WikiGate.Draft? card;
        string cardTitle;
        Dictionary<string, string> sourceTexts;
        if (context is null)
        {
            var matches = FindSourceCards(state, section, package.Sources);
            if (matches.Count == 0)
                return FailApply(root, package.Id, CodeSourceUnavailable,
                    "карточка источника пакета не найдена в его разделе «" + section + "»: применять нечего — "
                    + string.Join(", ", package.Sources.Select(s => s.Origin + ":" + s.Ref)));
            // Неоднозначность — не выбор первой (ревью S4g, MAJOR): один источник опознан
            // несколькими карточками раздела (его прежняя и новая версия). Сноска ушла бы на
            // ту из них, что раньше легла в обход, — а не на ту, которую назвал человек.
            // Отказ с причиной; решения человека на диске сохранены, как при любом отказе.
            var dup = matches.GroupBy(m => m.SourceIndex).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
            {
                var src = package.Sources[dup.Key];
                return FailApply(root, package.Id, CodeSourceUnavailable,
                    $"несколько версий источника, нужна точная карточка: «{src.Origin}:{src.Ref}» опознан "
                    + $"в разделе «{section}» {dup.Count()} карточками ({string.Join(", ", dup.Select(m => m.Draft.Path))}): "
                    + "применять нечем");
            }
            var found = matches[0];
            card = found.Draft;
            cardTitle = found.Title;
            sourceTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        else
        {
            var byTitle = FindCard(state, context.SourceCard!);
            if (byTitle is null)
                return FailApply(root, package.Id, CodeSource,
                    $"карточка источника «{context.SourceCard}» не найдена в разделе или вверх по цепочке: сноскам рендера нечем подтвердить утверждения");
            card = byTitle;
            cardTitle = context.SourceCard!;
            sourceTexts = new Dictionary<string, string>(StringComparer.Ordinal) { [byTitle.Value.Path] = context.SourceText! };
        }
        var working = new List<WikiGate.Draft>(state);
        var renderedOrder = new List<string>(); // страницы раздела в порядке первой правки
        var unconfirmed = new List<string>();

        foreach (var op in package.Operations)
        {
            // «inbox» без решения человека или отклонённое человеком из рендера не берётся:
            // решение читается только из HumanDecision, не из тела пакета.
            if (!Applies(op))
                continue;

            var existing = working.FirstOrDefault(d => d.Path == op.Page);
            if (op.Op != WikiPackage.OpCreate && existing.Path != op.Page)
                return StaleResult(root, package.Id,
                    $"страница «{op.Page}» не в состоянии раздела: база операции «{op.Op}» исчезла, пакет требует нового разбора");

            // Цитата заново выводится из текста источника: утверждение может быть своей
            // цитатой только если оно дословно в источнике (мера 1). Не выводится — операция
            // не применяется ни при какой политике; пакет отказан, на диске ничего.
            // У пути решения человека (S4g) текста источника нет и здесь: ни в карточке, ни в
            // пакете его нет, а дословность подтверждена при разборе (WikiPlanValidator.cs:199) —
            // рендерит тот же Text из Operations пакета, второй проверки тем же нечем.
            if (op.Text is not { Length: > 0 } ||
                (context is not null && !QuoteMatcher.ContainsVerbatim(context.SourceText!, op.Text)))
            {
                unconfirmed.Add($"операция «{op.Op}» на «{op.Page}»: цитата не выводится из текста источника дословно");
                continue;
            }

            WikiGate.Draft? draft;
            try
            {
                // Общий рендер плана (S6b-2): вторая копия рендера в проекте не заводится.
                // cardTitle — из ветки выбора карточки выше: принесённый или с самой карточки.
                draft = WikiPlanValidator.RenderApply(working, op, op.Text, cardTitle);
            }
            catch (Exception)
            {
                // Внутренняя ошибка рендера — отказ с причиной, не исключение вызывающему.
                return FailApply(root, package.Id, CodeGate,
                    $"внутренняя ошибка рендера операции «{op.Op}» на «{op.Page}»");
            }
            if (draft is null)
                // supersede: замещаемого утверждения больше нет в состоянии страницы —
                // его уже заместило другое применение; авто-rebase нет (решение 4).
                return StaleResult(root, package.Id,
                    $"замещаемое утверждение «{op.Target}» больше не в странице «{op.Page}»: пакет требует нового разбора");

            working.RemoveAll(d => d.Path == op.Page);
            working.Add(draft.Value);
            if (!renderedOrder.Contains(op.Page))
                renderedOrder.Add(op.Page);
        }

        if (unconfirmed.Count > 0)
            return FailApply(root, package.Id, CodeSource, unconfirmed[0], unconfirmed);

        // Дословность — только для карточек этого пакета (GivenOnly, тот же смысл, что в
        // проверке плана S6b-2): принятые раньше чужие сноски не могут отвергнуть новую запись.
        // sourceTexts — из ветки выбора карточки выше: у пути решения человека он пустой.
        var violations = WikiGate.Check(working, section, upChain, sourceTexts, WikiGate.VerbatimScope.GivenOnly,
            package.Author, state);
        if (violations.Count > 0)
            return FailApply(root, package.Id, CodeGate, $"итоговое состояние не проходит гейт: {violations[0]}", violations);

        var rendered = new List<WikiGate.Draft>(renderedOrder.Count);
        // Снимок базы каждой коснутой страницы: её хэш в манифесте на момент проверки
        // (null — страницы в манифеста нет: create). Шаг 5 сверяет с ним байты диска и
        // запись манифеста — «база цела» означает «то же состояние, на котором строился
        // рендер», а не «когда-то было похоже».
        var snapshot = new Dictionary<string, string?>(renderedOrder.Count, StringComparer.Ordinal);
        foreach (var path in renderedOrder)
        {
            rendered.Add(working.First(d => d.Path == path));
            snapshot[path] = manifest.TryGetValue(path, out var h) ? h : null;
        }
        // Готово к записи: пакет остаётся в «applying» — его заявка держит повторное
        // применение до конца цикла (до status «applied» или «apply_failed»).
        return new PrepareResult(true, null, null, [], rendered) { Section = section, BaseSnapshot = snapshot };
    }

    // ─── Правка человека (ADR-038, B3b) ─────────────────────────────────────────

    /// <summary>Исход правки человека. Applied — пакет human применён; Unchanged — текст не
    /// отличается от страницы, пакета нет; Changed — страница изменилась после открытия
    /// редактора (baseHash не сошёлся): в результате её свежая версия; Drift — файл расходится
    /// с манифестом; Invalid — правка не прошла разбор или гейт (причина для редактора);
    /// Refused — вики не заведена, манифест невалиден, сбой.</summary>
    public enum HumanEditOutcome { Applied, Unchanged, Changed, Drift, Invalid, Refused }

    /// <summary>Hash — sha256 байтов страницы (та же форма, что в манифесте): у Applied и
    /// Unchanged — после правки, у Changed — свежий; Content — свежий текст у Changed (null —
    /// страницы больше нет).</summary>
    public readonly record struct HumanEditResult(
        HumanEditOutcome Outcome, string? Reason, IReadOnlyList<string> Violations,
        string? PackageId = null, string? Hash = null, string? Content = null);

    private readonly record struct HumanBuild(WikiPackage? Package, HumanEditResult? Early);

    private readonly record struct HumanChecked(
        IReadOnlyList<WikiGate.Draft> Rendered, string? Error, bool Stale, IReadOnlyList<string> Violations);

    /// <summary>Сохранить правку страницы человеком: content — полный новый текст, baseHash —
    /// sha256 байтов страницы, какой её видел редактор (null — страницы не было, создание).
    /// Сверка baseHash, разбор правки в пакет human, проверки и запись — ОДНА критическая секция
    /// блокировки раздела: два сохранения по одному baseHash дают одно применение и один Changed.
    /// Правка не принимается молча поверх чужой: после применения пакета ИИ редактор получает
    /// Changed со свежей версией. Исключений наружу нет.</summary>
    public Task<HumanEditResult> EditPageAsync(string ownerId, string section, string page, string? baseHash, string? content) =>
        RunHumanAsync(ownerId, section, page, baseHash, (was, currentHash, _) =>
        {
            if (string.IsNullOrWhiteSpace(content))
                return new(null, Invalid("нет нового текста страницы"));
            var plan = WikiHumanEditPlanner.Build(page, was, content);
            if (!plan.Ok)
                return new(null, Invalid(plan.Reason ?? "правка не разобрана"));
            if (plan.Operations.Count == 0)
                return new(null, new HumanEditResult(HumanEditOutcome.Unchanged, null, [], Hash: currentHash));

            var id = WikiPackageStore.NewId();
            var type = WikiPage.Parse(content).Model?.Frontmatter.Type;
            var ops = plan.Operations.Select(o => new WikiPackage.Operation
            {
                Op = HumanOpName(o.Kind),
                Page = o.Page,
                PageSection = o.Section,
                // create несёт тип страницы в цели: планировщик его не называет, он в новом тексте.
                Target = o.Kind == WikiHumanEditPlanner.KindCreate ? type : o.Target,
                Text = o.Text,
                Reason = o.Reason,
                Class = WikiPackage.ClassKey(WikiGate.OriginHuman, HumanOpName(o.Kind)),
                Decision = WikiPackage.DecisionAuto,
                BaseHashes = currentHash is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [page] = currentHash },
            }).ToList();
            return new(NewHumanPackage(id, section, WikiPackage.ActionEdit, WikiGate.OriginHuman + ":" + id, ops), null);
        });

    /// <summary>«Вернуть мою»: current — действующее утверждение (его заместил ИИ), restore —
    /// формулировка человека в «Истории». Пакет human: обратный supersede со сноской на ИСХОДНУЮ
    /// карточку человека; новой карточки не создаёт. Конкурентность — как у EditPageAsync.</summary>
    public Task<HumanEditResult> RestorePageAsync(
        string ownerId, string section, string page, string? baseHash, string? current, string? restore) =>
        RunHumanAsync(ownerId, section, page, baseHash, (was, currentHash, state) =>
        {
            if (was is null || currentHash is null)
                return new(null, Invalid("страницы нет: возвращать нечего"));
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(restore))
                return new(null, Invalid("нужны действующее утверждение и формулировка из «Истории»"));
            var model = WikiPage.Parse(was).Model;
            if (model is null)
                return new(null, Invalid("страница не разбирается — править неразобранную нельзя"));
            var currentText = WikiHumanEditPlanner.Normalize(current);
            var restoreText = WikiHumanEditPlanner.Normalize(restore);
            var (cardRef, error) = WikiHumanRender.ResolveRestoreCard(state, model, restoreText);
            if (error is not null)
                return new(null, Invalid(error));
            var ops = new List<WikiPackage.Operation>
            {
                new()
                {
                    Op = WikiPackage.OpSupersede,
                    Page = page,
                    Target = currentText,
                    Text = restoreText,
                    Class = WikiPackage.ClassKey(WikiGate.OriginHuman, WikiPackage.OpSupersede),
                    Decision = WikiPackage.DecisionAuto,
                    Reason = "возвращена формулировка человека",
                    BaseHashes = new Dictionary<string, string> { [page] = currentHash },
                },
            };
            return new(NewHumanPackage(WikiPackageStore.NewId(), section, WikiPackage.ActionRestore, cardRef!, ops), null);
        });

    /// <summary>«Принять как мою правку» (ADR-038, §6): страница расходится с манифестом, человек
    /// объявляет расхождение своей правкой. Дифф «манифест → диск» (основа — версия из манифеста,
    /// WikiBaselines; диск — текущий файл) разбирает тот же планировщик, что и правку в редакторе;
    /// пакет human применяется ПОВЕРХ основы и пишет страницу со сносками human, а байты человека
    /// остаются прообразом (откат возвращает его файл). baseHash — sha256 файла, каким его видел
    /// человек: не сошёлся с диском (файл правили ещё раз) — Changed, ничего не записано.
    /// Конкурентность — под замком раздела, как у EditPageAsync. Отказы: основы нет (версия из
    /// манифеста нигде не сохранена), правка не разобрана (поля шапки, заголовок), дрейф других
    /// страниц раздела. Правок по существу нет (только форма) — страница принимается как есть,
    /// пакета нет. Журнал: op=adopt, actor=user, источник «human:{пакет}».</summary>
    public async Task<HumanEditResult> AdoptMineAsync(string ownerId, string section, string page, string? baseHash)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return Refused("владелец пустой");
        if (!WikiSections.IsWritable(section).Writable)
            return Invalid($"раздел «{section}» не записываемый");
        if (string.IsNullOrWhiteSpace(page) || WikiPaths.IsProjection(page) || WikiSections.Parse(page) != section
            || !page.EndsWith(".md", StringComparison.Ordinal))
            return Invalid("страница должна принадлежать разделу");
        if (page.Contains("/" + WikiSourceCapture.SourceDirName + "/", StringComparison.Ordinal))
            return Invalid("карточки источников неизменяемы: их не правят");
        if (string.IsNullOrWhiteSpace(baseHash))
            return Invalid("нужен хэш файла, каким его видел человек");
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Refused("вики этого владельца не заведена");

        try
        {
            return await _keeper.WithSectionsLockedAsync(ownerId, [section],
                () => Task.FromResult(AdoptMineUnderLock(ownerId, section, page, baseHash.ToLowerInvariant())))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Refused($"внутренний сбой принятия правки: {ex.Message}");
        }
    }

    private HumanEditResult AdoptMineUnderLock(string ownerId, string section, string page, string baseHash)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var mres = WikiManifest.TryRead(root, section);
        if (mres.State == WikiManifestState.Invalid)
            return Refused($"манифест раздела «{section}» невалиден — применение невозможно");
        var manifest = mres.Manifest;

        var full = SafePath.JoinBelowRoot(root, "pages/" + page);
        if (!File.Exists(full))
            return Refused($"файла «{page}» на диске нет — принимать нечего");
        var diskBytes = File.ReadAllBytes(full);
        var diskHash = Sha256OfBytes(diskBytes);
        // Сверка базы — под замком, до любых записей: файл правили после того, как человек его видел.
        if (baseHash != diskHash)
            return Fresh(root, page, "файл изменился после открытия сравнения");

        var recorded = manifest.TryGetValue(page, out var known) ? known : null;
        if (recorded == diskHash)
            return new(HumanEditOutcome.Unchanged, null, [], Hash: diskHash);

        // Дрейф других страниц раздела блокирует применение пакета целиком — выясняем ДО записи
        // в манифест, чтобы отказ не оставил там половину решения.
        var withThis = new Dictionary<string, string>(manifest, StringComparer.Ordinal) { [page] = diskHash };
        if (CheckSectionAgainstDisk(root, section, withThis) is { } other)
            return new(HumanEditOutcome.Drift, other, [other]);

        // Основа: версия, которую знал манифест. Страницы вне манифеста основы не имеют (создание).
        string? baseText = null;
        if (recorded is not null)
        {
            var baseBytes = WikiBaselines.TryLoad(root, page, recorded);
            if (baseBytes is null)
                return Invalid("версия страницы из манифеста не сохранена: сравнить нечем. " +
                               "Примите расхождение как есть или верните файл из прообразов.");
            baseText = System.Text.Encoding.UTF8.GetString(baseBytes);
        }
        var diskText = System.Text.Encoding.UTF8.GetString(diskBytes);

        var plan = WikiHumanEditPlanner.Build(page, baseText, diskText);
        if (!plan.Ok)
            return Invalid(plan.Reason ?? "правка не разобрана");

        if (plan.Operations.Count == 0)
        {
            // По существу ничего не изменилось (форма, порядок строк): сносок ставить не на что.
            WikiManifest.RecordApplied(root, section, new Dictionary<string, string> { [page] = WikiBaselines.Save(root, diskBytes) });
            WikiManifest.AppendLog(root, WikiPackage.ActionAdopt, section, [page], null, "user", null);
            WikiProjections.RegenerateSection(root, section);
            return new(HumanEditOutcome.Applied, null, [], null, diskHash, diskText);
        }

        var id = WikiPackageStore.NewId();
        var type = WikiPage.Parse(diskText).Model?.Frontmatter.Type;
        var ops = plan.Operations.Select(o => new WikiPackage.Operation
        {
            Op = HumanOpName(o.Kind),
            Page = o.Page,
            PageSection = o.Section,
            Target = o.Kind == WikiHumanEditPlanner.KindCreate ? type : o.Target,
            Text = o.Text,
            Reason = o.Reason,
            Class = WikiPackage.ClassKey(WikiGate.OriginHuman, HumanOpName(o.Kind)),
            Decision = WikiPackage.DecisionAuto,
            // База операции — файл человека: он войдёт в манифест ниже, и запись сверит именно его.
            BaseHashes = new Dictionary<string, string> { [page] = diskHash },
        }).ToList();
        var package = NewHumanPackage(id, section, WikiPackage.ActionAdopt, WikiGate.OriginHuman + ":" + id, ops);

        // Сухой прогон на основе до записи: отказ не должен оставить ни пакета, ни записи в манифесте.
        var (state, upChain) = BuildState(ownerId, section);
        OverrideBase(state, page, baseText);
        var dry = RenderHumanChecked(state, package, section, upChain);
        if (dry.Error is not null)
            return dry.Stale ? Fresh(root, page, dry.Error) : Invalid(dry.Error);
        if (dry.Violations.Count > 0)
            return Invalid(dry.Violations[0], dry.Violations);

        // Файл человека принимается в манифест (как «Принять как есть»), и сразу поверх него идёт
        // пакет. Любой отказ дальше возвращает манифест к прежней записи: дрейф остаётся дрейфом.
        WikiManifest.RecordApplied(root, section, new Dictionary<string, string> { [page] = WikiBaselines.Save(root, diskBytes) });
        HumanEditResult Rollback(HumanEditResult failure)
        {
            WikiManifest.RollbackTo(root, section, new Dictionary<string, string?> { [page] = recorded });
            return failure;
        }

        var created = WikiPackageStore.CreateHuman(root, package);
        if (!created.Ok)
            return Rollback(Refused(created.Reason ?? created.Code));
        var saved = created.Package!;
        var claim = WikiPackageStore.Transition(root, saved.Id, WikiPackage.StatusApplying);
        if (!claim.Ok)
            return Rollback(Refused(claim.Reason ?? claim.Code));

        var prepared = PrepareUnderLock(ownerId, root, saved, null!, (page, baseText));
        if (!prepared.Ready)
            return Rollback(prepared.Code switch
            {
                CodeStale => Fresh(root, page, prepared.Reason ?? "правка устарела"),
                CodeDrift => new(HumanEditOutcome.Drift, prepared.Reason, [prepared.Reason ?? ""]),
                CodeGate => Invalid(prepared.Reason ?? "правка не проходит проверки", prepared.Violations),
                _ => Refused(prepared.Reason ?? "применение не удалось"),
            });
        var written = WriteUnderLock(ownerId, root, saved.Id, prepared, null!);
        if (!written.Applied)
            return Rollback(written.Code == CodeDrift
                ? Fresh(root, page, written.Reason ?? "страница изменилась")
                : Refused(written.Reason ?? "запись не удалась"));

        return new(HumanEditOutcome.Applied, null, [], saved.Id,
            Sha256OfBytes(File.ReadAllBytes(full)), File.ReadAllText(full, System.Text.Encoding.UTF8));
    }

    // Состояние проверки «как до правки человека»: страница заменена основой (null — её не было).
    private static void OverrideBase(List<WikiGate.Draft> state, string page, string? content)
    {
        state.RemoveAll(d => d.Path == page);
        if (content is not null)
            state.Add(new WikiGate.Draft(page, content));
    }

    // Вид операции планировщика → операция пакета.
    private static string HumanOpName(string kind) => kind switch
    {
        WikiHumanEditPlanner.KindCreate => WikiPackage.OpCreate,
        WikiHumanEditPlanner.KindAppend => WikiPackage.OpAppend,
        WikiHumanEditPlanner.KindEdit => WikiPackage.OpEdit,
        WikiHumanEditPlanner.KindSupersede => WikiPackage.OpSupersede,
        WikiHumanEditPlanner.KindNotes => WikiPackage.OpNotes,
        _ => WikiPackage.OpHeader,
    };

    private static WikiPackage NewHumanPackage(
        string id, string section, string action, string sourceRef, IReadOnlyList<WikiPackage.Operation> ops) => new()
    {
        Id = id,
        Author = WikiPackage.AuthorHuman,
        Action = action,
        Section = section,
        Sources = [new WikiPackage.SourceRef(WikiGate.OriginHuman, sourceRef)],
        Operations = ops,
        Status = WikiPackage.StatusAccepted,
    };

    private static HumanEditResult Invalid(string reason, IReadOnlyList<string>? violations = null) =>
        new(HumanEditOutcome.Invalid, reason, violations ?? [reason]);

    private static HumanEditResult Refused(string reason) =>
        new(HumanEditOutcome.Refused, reason, [reason]);

    private async Task<HumanEditResult> RunHumanAsync(
        string ownerId, string section, string page, string? baseHash,
        Func<string?, string?, List<WikiGate.Draft>, HumanBuild> build)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return Refused("владелец пустой");
        if (!WikiSections.IsWritable(section).Writable)
            return Invalid($"раздел «{section}» не записываемый");
        if (string.IsNullOrWhiteSpace(page) || WikiPaths.IsProjection(page) || WikiSections.Parse(page) != section
            || !page.EndsWith(".md", StringComparison.Ordinal))
            return Invalid("страница должна принадлежать разделу");
        if (page.Contains("/" + WikiSourceCapture.SourceDirName + "/", StringComparison.Ordinal))
            return Invalid("карточки источников неизменяемы: их не правят");
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Refused("вики этого владельца не заведена");

        try
        {
            return await _keeper.WithSectionsLockedAsync(ownerId, [section],
                () => Task.FromResult(HumanUnderLock(ownerId, section, page, baseHash?.ToLowerInvariant(), build)))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Refused($"внутренний сбой применения правки: {ex.Message}");
        }
    }

    private HumanEditResult HumanUnderLock(
        string ownerId, string section, string page, string? baseHash,
        Func<string?, string?, List<WikiGate.Draft>, HumanBuild> build)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var manifest = WikiManifest.TryRead(root, section);
        if (manifest.State == WikiManifestState.Invalid)
            return Refused($"манифест раздела «{section}» невалиден — применение невозможно");
        var drift = CheckSectionAgainstDisk(root, section, manifest.Manifest);
        if (drift is not null)
            return new(HumanEditOutcome.Drift, drift, [drift]);

        var full = SafePath.JoinBelowRoot(root, "pages/" + page);
        var currentHash = File.Exists(full) ? Sha256OfBytes(File.ReadAllBytes(full)) : null;
        // Сверка базы — под замком раздела, в той же секции, что и запись: ни пакет ИИ, ни второе
        // сохранение не успеют между сверкой и записью.
        if (baseHash != currentHash)
            return Fresh(root, page, "страница изменилась после открытия редактора");

        var was = currentHash is null ? null : File.ReadAllText(full, System.Text.Encoding.UTF8);
        var (state, upChain) = BuildState(ownerId, section);
        var built = build(was, currentHash, state);
        if (built.Early is { } early)
            return early;
        var package = built.Package!;

        // Сухой прогон до записи пакета: опечатка в ссылке не оставляет в «Входящих» отказанные пакеты.
        var dry = RenderHumanChecked(state, package, section, upChain);
        if (dry.Error is not null)
            return dry.Stale ? Fresh(root, page, dry.Error) : Invalid(dry.Error);
        if (dry.Violations.Count > 0)
            return Invalid(dry.Violations[0], dry.Violations);

        var created = WikiPackageStore.CreateHuman(root, package);
        if (!created.Ok)
            return Refused(created.Reason ?? created.Code);
        var saved = created.Package!;
        var claim = WikiPackageStore.Transition(root, saved.Id, WikiPackage.StatusApplying);
        if (!claim.Ok)
            return Refused(claim.Reason ?? claim.Code);

        var prepared = PrepareUnderLock(ownerId, root, saved, null!);
        if (!prepared.Ready)
            return prepared.Code switch
            {
                CodeStale => Fresh(root, page, prepared.Reason ?? "правка устарела"),
                CodeDrift => new(HumanEditOutcome.Drift, prepared.Reason, [prepared.Reason ?? ""]),
                CodeGate => Invalid(prepared.Reason ?? "правка не проходит проверки", prepared.Violations),
                _ => Refused(prepared.Reason ?? "применение не удалось"),
            };
        var written = WriteUnderLock(ownerId, root, saved.Id, prepared, null!);
        if (!written.Applied)
            return written.Code == CodeDrift
                ? Fresh(root, page, written.Reason ?? "страница изменилась")
                : Refused(written.Reason ?? "запись не удалась");

        return new(HumanEditOutcome.Applied, null, [], saved.Id,
            Sha256OfBytes(File.ReadAllBytes(full)), File.ReadAllText(full, System.Text.Encoding.UTF8));
    }

    // Свежая версия страницы для редактора: сравнение «Моё / ИИ / Было» строится по ней.
    private static HumanEditResult Fresh(string root, string page, string reason)
    {
        var full = SafePath.JoinBelowRoot(root, "pages/" + page);
        if (!File.Exists(full))
            return new(HumanEditOutcome.Changed, reason, [reason]);
        var bytes = File.ReadAllBytes(full);
        return new(HumanEditOutcome.Changed, reason, [reason], Hash: Sha256OfBytes(bytes),
            Content: System.Text.Encoding.UTF8.GetString(bytes));
    }

    private PrepareResult PrepareHuman(
        string root, WikiPackage package, string section, IReadOnlyList<string> upChain,
        List<WikiGate.Draft> state, IReadOnlyDictionary<string, string> manifest)
    {
        var res = RenderHumanChecked(state, package, section, upChain);
        if (res.Error is not null)
            return res.Stale
                ? StaleResult(root, package.Id, res.Error)
                : FailApply(root, package.Id, CodeGate, res.Error);
        if (res.Violations.Count > 0)
            return FailApply(root, package.Id, CodeGate,
                $"итоговое состояние не проходит гейт: {res.Violations[0]}", res.Violations);

        var snapshot = new Dictionary<string, string?>(res.Rendered.Count, StringComparer.Ordinal);
        foreach (var draft in res.Rendered)
            snapshot[draft.Path] = manifest.TryGetValue(draft.Path, out var h) ? h : null;
        return new PrepareResult(true, null, null, [], res.Rendered) { Section = section, BaseSnapshot = snapshot };
    }

    // Рендер правки и весь гейт с правами автора human. Нарушения чужих страниц раздела (то, что
    // лежало до правки и ей не принадлежит) человека не останавливают: его правка отвечает за свои
    // страницы — иначе одна старая ошибка линта закрыла бы редактирование всего раздела.
    private static HumanChecked RenderHumanChecked(
        List<WikiGate.Draft> state, WikiPackage package, string section, IReadOnlyList<string> upChain)
    {
        var render = WikiHumanRender.Render(state, package);
        if (render.Error is not null)
            return new([], render.Error, render.Stale, []);

        var working = new List<WikiGate.Draft>(state);
        foreach (var draft in render.Drafts)
        {
            working.RemoveAll(d => d.Path == draft.Path);
            working.Add(draft);
        }
        var sourceTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var snapshot = WikiHumanRender.Snapshot(package);
        if (snapshot.Length > 0)
            sourceTexts[WikiHumanRender.CardPath(package)] = snapshot;

        var touched = render.Drafts.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var violations = WikiGate.Check(working, section, upChain, sourceTexts, WikiGate.VerbatimScope.GivenOnly,
                WikiPackage.AuthorHuman, state)
            .Where(v =>
            {
                var path = v.Split(':', 2)[0];
                return touched.Contains(path) || !path.EndsWith(".md", StringComparison.Ordinal);
            })
            .ToList();
        return new(render.Drafts, null, false, violations);
    }

    // Состояние проверки: страницы раздела и цепочки «вверх» мимо кэша графа (шаг 1 решения 4).
    private (List<WikiGate.Draft> State, IReadOnlyList<string> UpChain) BuildState(string ownerId, string section)
    {
        var model = _notes.BuildFresh(ownerId);
        var upChain = WikiSections.UpChain(section, ownerId, _spheres);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { section };
        allowed.UnionWith(upChain);

        var state = new List<WikiGate.Draft>();
        foreach (var n in model.Notes)
        {
            if (n.SourceKey != NotesService.WikiKey)
                continue;
            var sec = WikiSections.Parse(n.RelPath);
            if (sec is null || !allowed.Contains(sec))
                continue;
            state.Add(new WikiGate.Draft(n.RelPath, n.Content));
        }
        return (state, upChain);
    }

    // Манифест против диска, по байтам: каждая запись манифеста — файл с этим хэшем; каждый
    // файл-страница раздела на диске — запись манифеста. Служебные «_*» — не страницы (тот же
    // контракт, что у Хранителя); нечитаемый файл — тоже дрейф: его байты не подтвердить.
    private static string? CheckSectionAgainstDisk(string root, string section, IReadOnlyDictionary<string, string> manifest)
    {
        foreach (var page in manifest.Keys)
        {
            if (!IsPageDrifted(root, page, manifest)) continue;
            // Причина — за ней уже решившая её мера (IsPageDrifted): здесь только форма строки.
            var full = SafePath.JoinBelowRoot(root, "pages/" + page);
            return File.Exists(full)
                ? $"файл «{page}» расходится с манифестом раздела (правка мимо Хранителя)"
                : $"файл «{page}» из манифеста раздела отсутствует на диске";
        }

        var folder = WikiSections.Folder(section);
        if (folder is null)
            return $"«{section}» не ключ раздела вики";
        var dir = SafePath.JoinBelowRoot(root, "pages/" + folder);
        if (!Directory.Exists(dir))
            return null; // раздела на диске нет: норма до первой применённой операции (пустой манифест проверен выше).

        var known = new HashSet<string>(manifest.Keys, StringComparer.Ordinal);
        foreach (var full in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
        {
            var pageKey = folder + Path.GetRelativePath(dir, full).Replace('\\', '/');
            if (WikiPaths.IsProjection(pageKey))
                continue;
            if (!known.Contains(pageKey))
                return $"файл «{pageKey}» на диске, но не в манифесте раздела (записан не Хранителем)";
        }
        return null;
    }

    /// <summary>Равенство ОДНОЙ страницы её записи в манифесте — та же мера, что вся сверка
    /// выше: хэш байтов файла ≠ записи, файла нет, а запись есть, либо файл есть, а записи
    /// нет. Ни файла, ни записи — страницы нет: не дрейф. Служебные «_*» сюда не приходят
    /// (не страницы). internal — ту же меру читает контроллер вики: признак дрейфа страницы
    /// для UI (плашка «Расходится с журналом Хранителя» и «Принять как есть» — до adopt,
    /// а не после 409); второй копии меры в проекте нет.</summary>
    internal static bool IsPageDrifted(string root, string pageKey, IReadOnlyDictionary<string, string> manifest)
    {
        var full = SafePath.JoinBelowRoot(root, "pages/" + pageKey);
        var hasRecord = manifest.TryGetValue(pageKey, out var recorded);
        if (!File.Exists(full)) return hasRecord; // записи нет и файла нет — не страница
        return !hasRecord || Sha256OfBytes(File.ReadAllBytes(full)) != recorded;
    }

    // Карточка источника — страница type: source с этим заголовком (та же нормализация,
    // чем мерит uniqueness гейт): цель сносок рендера обязана резолвиться в разделе или вверх.
    private static WikiGate.Draft? FindCard(List<WikiGate.Draft> state, string title)
    {
        var norm = WikiDedup.NormalizeName(title);
        if (norm.Length == 0)
            return null;
        foreach (var d in state)
        {
            var page = WikiPage.Parse(d.Content).Model;
            if (page is null || page.Frontmatter.Type != WikiFrontmatter.TypeSource)
                continue;
            if (WikiDedup.NormalizeName(page.Title) == norm)
                return d;
        }
        return null;
    }

    /// <summary>Карточки источника пакета — страницы type: source в разделе САМОГО пакета
    /// (чужих разделов путь решения человека не читает: ADR-036, решение 1), чьи origin и ref
    /// совпадают с одним из источников пакета. Её записал захват при разборе (S6h), и она уже
    /// прочитана в состоянии раздела — отдельного чтения файла нет. Терпимость сверки — та же,
    /// чем мерит откат по источнику (WikiRetractService.SourceMatches): целое совпадение, карточ
    /// с диапазоном против ссылки без него и старая форма Ref без префикса origin: две формулы
    /// одного источника разошлись бы когда-нибудь.
    ///
    /// Возвращает ВСЕ совпадения, а не первое: в разделе могут лежать прежняя и новая версия
    /// одного источника — одна и та же origin+ref в двух карточках. Выбрать первую значило бы
    /// отнести сноску к версии, которую человек не выбирал (ревью S4g, MAJOR): пустой список —
    /// «карточки нет», несколько совпадений на один источник — «несколько версий»; решает
    /// вызывающий (PrepareChecked), ему нужен и текст отказа, и путь каждой карточки.
    /// Одна карточка на один источник — тот же выбор, что был.</summary>
    private static List<(WikiGate.Draft Draft, string Title, int SourceIndex)> FindSourceCards(
        List<WikiGate.Draft> state, string section, IReadOnlyList<WikiPackage.SourceRef> sources)
    {
        var found = new List<(WikiGate.Draft Draft, string Title, int SourceIndex)>();
        foreach (var d in state)
        {
            if (WikiSections.Parse(d.Path) != section)
                continue;
            var page = WikiPage.Parse(d.Content).Model;
            if (page is null || page.Frontmatter.Type != WikiFrontmatter.TypeSource)
                continue;
            var cardOrigin = page.Frontmatter.Origin;
            var cardRef = page.Frontmatter.Ref;
            // Пустой заголовок — не цель сноски: сноска резолвится по заголовку (WikiGate).
            var title = page.Title;
            if (string.IsNullOrEmpty(cardRef) || string.IsNullOrWhiteSpace(title))
                continue;
            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                if (s is null || string.IsNullOrEmpty(s.Ref) || cardOrigin != s.Origin)
                    continue;
                var cardId = cardRef.StartsWith(s.Origin + ":", StringComparison.Ordinal)
                    ? cardRef[(s.Origin.Length + 1)..]
                    : cardRef;
                if (RefMatches(cardRef, s.Ref) || RefMatches(cardId, s.Ref))
                {
                    // Одна карточка относится к одному источнику: второй источник той же
                    // карточки — не второе совпадение, а та же карточка.
                    found.Add((d, title, i));
                    break;
                }
            }
        }
        return found;
    }

    private static bool RefMatches(string stored, string wanted) =>
        wanted.Length > 0 && (string.Equals(stored, wanted, StringComparison.Ordinal)
            || stored.StartsWith(wanted + "#", StringComparison.Ordinal));

    // Отказ применения: статус «apply_failed» — только через Transition (тот же порядок,
    // что у всех переходов); причина — текст для человека («Входящие», журнал).
    private static PrepareResult FailApply(string root, string id, string code, string reason, IReadOnlyList<string>? violations = null)
    {
        var transition = WikiPackageStore.Transition(root, id, WikiPackage.StatusApplyFailed, reason);
        // Минор 4 (ревью S4b-1): исход перехода — часть отказа. Если статус не сдвинулся,
        // вызывающий обязан видеть это в причине, а не читать молча «apply_failed».
        var resultReason = transition.Ok
            ? reason
            : $"{reason} (статус «apply_failed» не записан: {transition.Reason ?? transition.Code})";
        return new PrepareResult(false, code, resultReason, violations ?? [reason], []);
    }

    private static PrepareResult StaleResult(string root, string id, string reason)
    {
        WikiPackageStore.Transition(root, id, WikiPackage.StatusStale, reason);
        return new PrepareResult(false, CodeStale, reason, [reason], []);
    }

    private static PrepareResult Fail(string code, string reason) => new(false, code, reason, [reason], []);

    // sha256 БАЙТОВ — та же форма, что у манифеста и сверки Хранителя: сравнивать должны одно и то же.
    // internal — та же форма нужна сторожу старта при возврате страницы (хэш в манифест).
    internal static string Sha256OfBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
