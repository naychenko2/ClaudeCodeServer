using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Откат по источнику (ADR-035, решение 12г, мера 5, S4f-1): кнопка «Откатить всё из этого чата» у
// хроники, у карточки источника и в «Журнале». Системная операция (решение 6): ни у агента, ни у
// персоны такого инструмента нет — как у отката пакета (S4b-3).
//
// Порядок операции и почему он такой:
// 1. Поиск применённых пакетов с этим источником. Форма sourceRef — карточка источника
//    («{origin}:…»); в пакете карточка могла быть записана с диапазоном («session:{id}#m{a}-{b}») —
//    совпадение по карточе без диапазона или по её префиксу до «#»: «в любой части диапазонов»
//    (мера 5). Ввод недоверен: длина, «..», управляющие символы, обязательный префикс origin —
//    отказ с причиной, а не поиск по мусору.
// 2. Блокировки всех затронутых разделов разом, в каноническом порядке ключей
//    (WithSectionsLockedAsync): встречный откат на той же паре разделов иначе встаёт в взаимной
//    блокировке. Порядок аргументов здесь — порядок пакетов (по времени применения); каноническим
//    его делает хелпер, а не вызывающий. Состояние пакетов для решения перечитывается ПОД блоком:
//    первое чтение выбирает только набор замков (урок S4b-3, MAJOR 2).
// 3. Пакеты откатываются в обратном порядке применения (мера 5) — через тот же механизм, что
//    «Откатить пакет» (WikiPackageRevert). Для пакета в «applied» его UpdatedAt есть время его
//    перехода в «applied»: стор ставит его на каждый переход, а после «applied» пакет никто не
//    трогает до отката. Обратный порядок и разворачивает цепочку: сначала откатывается поздний
//    пакет, диск страницы возвращается к состоянию, которое оставил ранний, — и сверка раннего
//    проходит. Прямой порядок оставил бы всю цепочку в «требует снятия сносок».
//    Вызов идёт под СВОИМ блоком всех затронутых разделов, поэтому берётся внутренний вариант
//    отката без блока (RevertUnderLock): тот же замок того же ключа повторно — зависание.
// 4. «Не менял» — та же сверка, что у отката пакета (S4b-3): хэш на диске равен записи состояния
//    применения. Отказ wiki_revert_changed — не отказ этой операции: пакет остаётся на диске целым
//    и идёт в список «требует снятия сносок» — что делать с такой страницей решает снятие сносок
//    (S4f-2), а не откат. В тот же список — и прочие отказы, при которых на диске не тронуто
//    ничего: нет записи состояния применения (пакет, заведённый до меры S4b-3) и нет прообразов
//    (сняты 90-дневным хранением решения 13). Инвариант меры (доработка ревью S4f-1): любой
//    пакет, чьи страницы остались в вики после отката, виден в NeedsUnlink — иначе страница
//    навсегда остаётся со сноской на снятый источник, и S4f-2 её не увидит.
// 5. Хроника чата снимается целиком (мера 5): в отличие от страницы знания, всё её содержание —
//    этот чат. При целой цепочке её файл уже возвращён механизмом (create-операция имеет пустой
//    прообраз — файл удаляется). Что осталось после оборванной цепочки, удаляется здесь: файл и
//    его запись манифеста, проекции перегенерируются. Хронику нельзя оставить «со снятием сносок»:
//    сноску снимать не с чего — страница целиком из этого источника.
// 6. Чат получает пометку «не разбирать» (шов S6i, ISessionDirectory.SetWikiMark → WikiMarks.Skip):
//    иначе следующая ночь вернёт откатанное тем же разбором (решение 12а). Только своих чатов
//    (ResolveOwnerId) и только когда с диска действительно что-то снято: пометка чату, из которого
//    ничего не откатано, — не решение этой операции.
// 7. Журнал: строка op=retract на каждый раздел, где диск изменился, — источник, затронутые
//    страницы, id откатанных пакетов. Строки op=revert по пакетам пишет механизм отката (S4b-3) и
//    не заменяются: откат пакета и откат по источнику — разные факты, и «Журнал» (S7a) показывает
//    оба. Проекции раздела перегенерируются после новой строки (тот же порядок, что у применения).
//
// Сбой посреди (второй из N пакетов): каждый пакет откатывается механизмом S4b-3, и он оставляет
// свой раздел согласованным (возвращённое возвращено, манифест следует за файлами). Остальные
// пакеты откатываются дальше — исход частичный и назван по пакетам, а не оборван на середине.
// Повтор операции безопасен и не двоит журнал: откатанные пакеты уже «reverted» и из выбора
// выпадают, удалённая хроника не удаляется второй раз, строк retract не появляется новых.
//
// Это не удаление знания (решение 3): снимается то, что подтверждено не было; прообразы и журнал
// хранят снятое 90 дней (решение 13).
public sealed class WikiRetractService
{
    // Коды отказов — контракт вызывающего (REST «Журнала», S4f-2/S7a), тот же стиль, что wiki_*.
    public const string CodeShape = "wiki_retract_shape";
    public const string CodeOff = "wiki_retract_off";
    public const string CodeInternal = "wiki_retract_internal";

    // Значение поля op строки журнала: по нему «Журнал» отличает откат по источнику от отката
    // пакета (op=revert) и прочих операций.
    private const string LogOpRetract = "retract";

    // Папка хроники чата (ADR-035, решение 12б): «Хроника разговора — страница вики type:
    // chronicle … папка Хроники/». Признак страницы этого чата в S4f-1 — путь под этой папкой у
    // create-операции пакета с этим источником: сама хронику строит разбор (S6), здесь она только
    // снимается целиком.
    private const string ChronicleFolder = "Хроники";

    // Потолок длины карточки источника: карточнее любой реальной ссылки (путь файла с id
    // проекта), но не бесконечный вход из строки запроса.
    private const int MaxSourceLength = 512;

    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiPackageRevert _revert;
    private readonly ISessionDirectory _sessions;
    private readonly string _dataDir;

    public WikiRetractService(NotesService notes, WikiKeeper keeper, WikiPackageRevert revert,
        ISessionDirectory sessions, IConfiguration config)
    {
        _notes = notes;
        _keeper = keeper;
        _revert = revert;
        _sessions = sessions;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Что сталося с одним найденным пакетом. Reverted — откатан механизмом S4b-3;
    /// CodeChanged — «требует снятия сносок» (S4f-2): ChangedPages — те страницы. Code из списка
    /// NeedsUnlink — отказ, при котором страницы пакета остались на диске (см. NeedsUnlink).
    /// Прочие Code — отказ этого пакета (внутренний сбой, невалидный манифест): он назван,
    /// а операция продолжается с остальными.</summary>
    public readonly record struct PackageOutcome(
        string PackageId,
        string Section,
        bool Reverted,
        string? Code,
        string? Reason,
        IReadOnlyList<string> RestoredPages,
        IReadOnlyList<string> ChangedPages);

    /// <summary>Итог операции. Ok — операция дошла до конца (частичный исход по пакетам — в
        /// Outcomes, а не в Ok=false); Ok=false — только глобальный отказ (вход, вики не заведена,
        /// внутренний сбой всей операции). RemovedChronicles — страницы хроники, снятые целиком.
        /// MarkedSkip — чату поставлена пометка «не разбирать».</summary>
    public readonly record struct RetractResult(
        bool Ok,
        string? Code,
        string? Reason,
        IReadOnlyList<PackageOutcome> Outcomes,
        IReadOnlyList<string> RemovedChronicles,
        bool MarkedSkip)
    {
        /// <summary>Пакеты «требует снятия сносок» — список меры 5 для человека и S4f-2.
        /// Критерий — инвариант меры (доработка ревью S4f-1): любой пакет, чьи страницы остались
        /// на диске после отката, виден здесь. Это ровно те отказы механизма S4b-3, при которых
        /// на диске не тронуто ничего и страницы пакета остаются в вики: «wiki_revert_changed»
        /// (страницу меняли после применения), «wiki_revert_no_applied» (нет записи состояния
        /// применения — сверки нет, пакет заведён до этой меры), «wiki_revert_no_preimages»
        /// (прообразов нет — они сняты 90-дневным хранением решения 13). Отказ по статусу не в
        /// списке: «reverted» — страницы уже сняты прежним откатом; прочие статусы в выборку
        /// этой операции не попадают (Candidates берёт только «applied», WikiRetractService).
        /// Внутренние отказы (сбой чтения, невалидный манифест) — не в списке: это не страница
        /// с известным состоянием, а сигнал неисправности раздела (хвосты ревью S4f-1).</summary>
        public IEnumerable<PackageOutcome> NeedsUnlink =>
            Outcomes.Where(o => !o.Reverted && PagesRemained(o.Code));

        /// <summary>Коды отказа отката, при которых страницы пакета остаются на диске. Явный
        /// перечень, а не «все коды»: см. NeedsUnlink.</summary>
        private static bool PagesRemained(string? code) =>
            code == WikiPackageRevert.CodeChanged
            || code == WikiPackageRevert.CodeNoApplied
            || code == WikiPackageRevert.CodeNoPreimages;
    }

    public async Task<RetractResult> RetractAsync(string ownerId, string? sourceRef)
    {
        // Fail-closed входа (уроки S4a/S4b): отказ с причиной, не NRE.
        if (string.IsNullOrWhiteSpace(ownerId))
            return Fail(CodeShape, "владелец пустой");
        var shape = SourceShapeError(sourceRef);
        if (shape is not null)
            return Fail(CodeShape, shape);
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return Fail(CodeOff, "вики этого владельца не заведена");

        var root = WikiPaths.Root(_dataDir, ownerId);
        // Первое чтение — только чтобы узнать, какие разделы придётся держать: исход решается
        // по переччитанному под блоком состоянию (урок S4b-3, MAJOR 2).
        var first = Candidates(root, sourceRef!);
        if (first.Count == 0)
            // Нечего откатывать: не отказ, а пустой исход — на диске не тронуто ничего, пометки
            // нет (из этого чата ничего не снималось).
            return new RetractResult(true, null, null, [], [], false);

        var body = await _keeper.WithSectionsLockedAsync(ownerId,
            // Порядок аргументов — порядок пакетов (обратный порядку применения): встречные
            // откаты на одной паре разделов приходят к хелперу в зеркальных порядках, а замки
            // он берёт канонически (решение 10) — иначе взаимная блокировка.
            [.. first.Select(p => p.Section)],
            () => Task.FromResult(RetractUnderLock(ownerId, root, sourceRef!)));

        if (!body.Ok)
            return new RetractResult(false, body.Code, body.Reason, [], [], false);

        // Пометка — вне блока разделов: это настройка каталога чатов, а не файл раздела. Только
        // когда с диска действительно что-то снято (п.6 шапки).
        var marked = false;
        if (body.Outcomes.Any(o => o.Reverted) || body.RemovedChronicles.Count > 0)
            marked = TryMarkSkip(ownerId, sourceRef!);

        return new RetractResult(true, null, null, body.Outcomes, body.RemovedChronicles, marked);
    }

    private (bool Ok, string? Code, string? Reason, List<PackageOutcome> Outcomes, List<string> RemovedChronicles)
        RetractUnderLock(string ownerId, string root, string sourceRef)
    {
        try
        {
            return RetractBody(ownerId, root, sourceRef);
        }
        catch (Exception ex)
        {
            // Любой неожиданный сбой — результат-отказ с причиной, не исключение вызывающему
            // (тот же контракт, что у применения и отката пакета).
            return (false, CodeInternal, $"внутренний сбой отката по источнику: {ex.Message}", [], []);
        }
    }

    private (bool Ok, string? Code, string? Reason, List<PackageOutcome> Outcomes, List<string> RemovedChronicles)
        RetractBody(string ownerId, string root, string sourceRef)
    {
        // Состояние пакетов — перечитанное под блоком: первым чтением выбран только набор
        // замков; кто «applied» и чей он источник — решается по диску сейчас (урок S4b-3, MAJOR 2).
        var candidates = Candidates(root, sourceRef);

        // П.3 шапки: обратный порядок применения.
        var outcomes = new List<PackageOutcome>(candidates.Count);
        foreach (var pkg in candidates)
        {
            var r = _revert.RevertUnderLock(ownerId, root, pkg.Id);
            outcomes.Add(new PackageOutcome(pkg.Id, pkg.Section, r.Reverted, r.Code, r.Reason,
                r.RestoredPages, r.ChangedPages));
        }

        // П.5 шапки: хроника снимается целиком. Целая цепочка уже сняла её файл механизмом
        // (пустой прообраз create — удаление); здесь подбирается то, что осталось после
        // оборванной цепочки. Файла нет — удалять нечего: повтор операции ничего не двоит.
        var removed = new List<string>();
        foreach (var pkg in candidates)
        {
            foreach (var op in pkg.Operations)
            {
                if (op.Op != WikiPackage.OpCreate || op.Page is null || !IsChroniclePath(op.Page))
                    continue;
                var full = SafePath.Join(root, "pages/" + op.Page);
                if (!File.Exists(full))
                    continue;
                try
                {
                    File.Delete(full);
                    // Манифест следует за файлами: записи удалённой страницы не остаётся.
                    WikiManifest.RollbackTo(root, pkg.Section, new Dictionary<string, string?> { [op.Page] = null });
                    WikiProjections.RegenerateSection(root, pkg.Section);
                    removed.Add(op.Page);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Одна неснимаемая страница не останавливает остальных: её отсутствие
                    // назано в RemovedChronicles — её нет, и человек видит это.
                }
            }
        }
        if (removed.Count > 0)
            // Файлы вики изменены — модель per-owner не должна остаться о них осведомлённой
            // (тот же инвариант, что у применения и отката пакета).
            _notes.Invalidate(ownerId);

        // П.7 шапки: строка op=retract на каждый раздел, где диск изменился, — по каноническому
        // порядку разделов (детерминированный журнал, не порядок обхода). Строки op=revert по
        // пакетам уже записаны механизмом отката; эта строка — факт операции по источнику.
        foreach (var section in outcomes.Where(o => o.Reverted).Select(o => o.Section)
                     .Concat(removed.Select(SafeSection)).Distinct(StringComparer.Ordinal)
                     .OrderBy(s => s, StringComparer.Ordinal))
        {
            var pages = outcomes.Where(o => o.Reverted && o.Section == section)
                .SelectMany(o => o.RestoredPages)
                .Concat(removed.Where(p => SafeSection(p) == section))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (pages.Count == 0)
                continue;
            var packageIds = outcomes.Where(o => o.Reverted && o.Section == section)
                .Select(o => o.PackageId)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            WikiManifest.AppendLog(root, LogOpRetract, section, pages, sourceRef, "user", null, packageIds);
            // _log.md — производная от log.jsonl: после новой строки она обязана быть с ней
            // (тот же порядок, что у применения и отката пакета).
            WikiProjections.RegenerateSection(root, section);
        }

        return (true, null, null, outcomes, removed);
    }

    /// <summary>Применённые пакеты с этим источником, в обратном порядке применения: для пакета в
    /// «applied» его UpdatedAt — время его перехода в «applied» (стор ставит его на каждый
    /// переход; после «applied» пакет не трогается до отката). Прочие статусы не candidates:
    /// «applying» закрывает сторож старта, «apply_failed»/«stale»/«rejected» до диска не дошли
    /// или уже откатаны, «reverted» уже откатан.</summary>
    private static List<WikiPackage> Candidates(string root, string sourceRef) =>
        WikiPackageStore.List(root).Packages
            .Where(p => p.Status == WikiPackage.StatusApplied && SourceMatches(p, sourceRef))
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Совпадение пакета с источником: любая из его карточек совпадает с sourceRef
    /// целиком или как карточ без диапазона («session:{id}» ловит «session:{id}#m{a}-{b}» —
    /// «в любой части диапазонов», мера 5). Принимается и старая форма хранения без префикса
    /// origin («chat-1» при origin «session»): карточ входa разбирается пополам и вторая
    /// половина сверяется с Ref того же origin.</summary>
    private static bool SourceMatches(WikiPackage pkg, string sourceRef)
    {
        foreach (var s in pkg.Sources)
        {
            if (s is null || string.IsNullOrEmpty(s.Ref))
                continue;
            if (RefMatches(s.Ref, sourceRef))
                return true;
            var origin = OriginOf(sourceRef);
            if (origin is not null && s.Origin == origin && RefMatches(s.Ref, IdPartOf(sourceRef)))
                return true;
        }
        return false;
    }

    private static bool RefMatches(string stored, string wanted) =>
        wanted.Length > 0 && (string.Equals(stored, wanted, StringComparison.Ordinal)
            || stored.StartsWith(wanted + "#", StringComparison.Ordinal));

    /// <summary>Проверка формы карточки источника (ввод недоверен): длина, «..», управляющие
    /// символы, обязательный префикс known-origin и непустая карточка после него.</summary>
    private static string? SourceShapeError(string? sourceRef)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
            return "источник пустой";
        if (sourceRef.Length > MaxSourceLength)
            return $"источник длиннее {MaxSourceLength} символов";
        if (sourceRef.Contains("..", StringComparison.Ordinal))
            return "источник содержит «..»";
        if (sourceRef.Any(c => c < ' '))
            return "источник содержит управляющие символы";
        if (OriginOf(sourceRef) is null)
            return "источник не формы «{origin}:…» из закрытого перечня";
        if (IdPartOf(sourceRef).Length == 0)
            return "источник без карточки";
        return null;
    }

    private static string? OriginOf(string sourceRef) =>
        WikiGate.OriginValues.FirstOrDefault(o => sourceRef.StartsWith(o + ":", StringComparison.Ordinal));

    /// <summary>Карточ после префикса origin, без диапазона: «session:{id}#m1-9» → «{id}».</summary>
    private static string IdPartOf(string sourceRef)
    {
        var origin = OriginOf(sourceRef)!;
        var rest = sourceRef[(origin.Length + 1)..];
        var hash = rest.IndexOf('#');
        return hash < 0 ? rest : rest[..hash];
    }

    /// <summary>Путь хроники: сегмент «Хроники» под папкой раздела (решение 12б).</summary>
    private static bool IsChroniclePath(string pageKey) =>
        pageKey.Split('/').Contains(ChronicleFolder, StringComparer.Ordinal);

    /// <summary>Раздел страницы по пути — через единственный разбор (WikiSections.Parse);
    /// неразобранный путь (не должен случиться: страницы из операций валидных пакетов) — пустой
    /// ключ, он не соберёт строку журнала несуществующего раздела.</summary>
    private static string SafeSection(string pageKey) => WikiSections.Parse(pageKey) ?? "";

    /// <summary>Пометка «не разбирать» чату источника (п.6 шапки): только для session:, только
    /// своего чата (ResolveOwnerId — тот же разбор владельца, что у каталога чатов), только если
    /// сессия есть. Чужой или отсутствующий чат — false без записи: пометка чужого каталога —
    /// не право этой операции.</summary>
    private bool TryMarkSkip(string ownerId, string sourceRef)
    {
        if (OriginOf(sourceRef) != "session")
            return false;
        var sessionId = IdPartOf(sourceRef);
        if (sessionId.Length == 0)
            return false;
        var session = _sessions.GetById(sessionId);
        if (session is null)
            return false;
        if (_sessions.ResolveOwnerId(session) != ownerId)
            return false;
        return _sessions.SetWikiMark(sessionId, WikiMarks.Skip);
    }

    private static RetractResult Fail(string code, string reason) =>
        new(false, code, reason, [], [], false);
}
