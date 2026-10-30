using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spheres;

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
// 7. Снятие сносок (мера 5, второй абзац, S4f-2): на страницах пакетов из NeedsUnlink — тех, что не
//    откатаны побайтно, — снимаются сноски этого источника; утверждения, у которых не осталось ни
//    одной сноски, убираются, утверждения с другими источниками остаются (снимается то, что не было
//    подтверждено, — та же мера 1). Страница правится только через модель: WikiPage.Parse даёт
//    структуру сносок и их канонические номера, ссылки переписываются по отображению модели, а не
//    регулярковым разбором текста; результат проверяется тем же гейтом, что применение (страница
//    остаётся валидной: ссылки в тексте и список сносок сходятся) — нечитаемую или не прошедшую
//    гейт страницу правка не трогает, она названа в отчёте с причиной.
//    Различение двух состояний страницы (доработка ревью S4f-2, MAJOR 2): «сносок этого источника
//    здесь нет» — страница обработана, след снят раньше; «сноска есть, но её цель не опознана»
//    (карточка удалена или переименована, вне цепочки, ref другой формы) — это не «следов нет»:
//    гейт назвал бы её «сноской на неизвестную карточку», и атрибутировать её этим источником
//    нельзя. Вторая страница не считается обработанной: она названа в failed, диск её не тронут,
//    пакет остаётся «applied» и в NeedsUnlink — иначе сноска на снятый источник осталась бы
//    навсегда, а S4f-2 её бы не увидел.
//    Запись — как у применения пакета, но НЕ поверх его записей (доработка ревью S4f-2, MAJOR 1):
//    прообраз пакета остаётся байтами ДО применения этого пакета, его запись состояния применения —
//    хэшем того, что записал этот пакет: на них меряет побайтный откат, их подмена лишила бы
//    цепочку состояния «до пакета» и сломала бы сверку других пакетов той же страницы. Состояние
//    «до снятия сносок» пишется ОТДЕЛЬНО (keeper/retract/{pkgId}/…; ключ — пакет, чьи сноски
//    снимаются: он снимается один раз) и держится тем же сроком 90 дней, что прообразы (решение 13).
//    Манифест следует за файлом (он описывает последнее состояние страницы), журнал, проекции —
//    сбой посреди не оставляет раздел в дрейфе (урок S4b-2), а повторная операция не находит
//    снятых сносок и ничего не дописывает. Пакет, чьи страницы обработаны (сняты или следов
//    источника на них уже нет), переходит в «reverted» — единственный переход из «applied» в
//    таблице (мера 4), нового статуса нет; пакет с необработанной страницей остаётся «applied» и
//    назван в отчёте: о битой странице решает человек, не перезапись. Переписанные страницы такого
//    пакета — в журнал и проекции всё равно (п. 8): на диске изменённая страница всегда отражена
//    в журнале. Хроника здесь не обрабатывается: она снимается целиком (п. 5).
// 8. Журнал: строка op=retract на каждый раздел, где диск изменился, — по ФАКТИЧЕСКИ изменённым
//    страницам (доработка ревью S4f-2, MAJOR 3): возвращённые побайтно, удалённые, снятые —
//    независимо от того, доведён ли их пакет до «reverted»; id откатанных и правивших их пакетов. Строки
//    op=revert по пакетам пишет механизм отката (S4b-3) и не заменяются: откат пакета и откат по
//    источнику — разные факты, и «Журнал» (S7a) показывает оба. Проекции раздела перегенерируются
//    после новой строки (тот же порядок, что у применения).
//
// Сбой посреди (второй из N пакетов): каждый пакет откатывается механизмом S4b-3, и он оставляет
// свой раздел согласованным (возвращённое возвращено, манифест следует за файлами). Остальные
// пакеты откатываются дальше — исход частичный и назван по пакетам, а не оборван на середине.
// Повтор операции безопасен и не двоит журнал: откатанные и обработанные пакеты уже «reverted» и из
// выбора выпадают, удалённая хроника не удаляется второй раз, со снятых страниц сносок этого
// источника уже нет и вторая строка не пишется.
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

    // Ссылка сноски в строке страницы — та же форма, что у парсера (WikiPage.FootnoteRefRx):
    // применяется отображение модели (снятие/перенумерование по id из модели), а не разбор
    // текста: множества id приходят из WikiPage.Parse, образец — тот же, что у парсера.
    private static readonly Regex RefRx = new(@"\[\^([^]\r\n]+)\]", RegexOptions.CultureInvariant);

    private readonly NotesService _notes;
    private readonly WikiKeeper _keeper;
    private readonly WikiPackageRevert _revert;
    private readonly ISessionDirectory _sessions;
    // Ссылка страницы резолвится только в своём разделе и вверх по цепочке (ADR-036, решение 4):
    // чтобы гейт на снятой странице не отвергнул законную ссылку вверх, цепочку строит он же,
    // что применение (WikiSections.UpChain).
    private readonly ISphereDirectory _spheres;
    private readonly string _dataDir;

    public WikiRetractService(NotesService notes, WikiKeeper keeper, WikiPackageRevert revert,
        ISessionDirectory sessions, ISphereDirectory spheres, IConfiguration config)
    {
        _notes = notes;
        _keeper = keeper;
        _revert = revert;
        _sessions = sessions;
        _spheres = spheres;
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

    /// <summary>Что сталося со страницами одного пакета при снятии сносок (S4f-2). Pages — только
        /// реально переписанные страницы; пустой список — следов источника на страницах пакета уже
        /// не было (ничего не писали). Complete — обработаны ли ВСЕ страницы пакета: у пакета с
        /// необработанной страницей записи тоже есть, и её Pages — фактически переписанное, а не
        /// «пакет закрыт» (доработка ревью S4f-2, MAJOR 3): переписанное идёт в журнал, а хвост
        /// остаётся в NeedsUnlink. Статус «reverted» доводится по Complete: неудачный переход —
        /// тот же путь доводки, что у S4b-3 (страницы уже чисты, следующий вызов доведёт статус).</summary>
    public readonly record struct UnlinkOutcome(
        string PackageId,
        string Section,
        IReadOnlyList<string> Pages,
        bool Complete);

    /// <summary>Итог операции. Ok — операция дошла до конца (частичный исход по пакетам — в
        /// Outcomes, а не в Ok=false); Ok=false — только глобальный отказ (вход, вики не заведена,
        /// внутренний сбой всей операции). RemovedChronicles — страницы хроники, снятые целиком.
        /// MarkedSkip — чату поставлена пометка «не разбирать». Unlinked — что снято с каждого
        /// пакета, чьи страницы трогали снятие сносок: только фактически переписанные страницы
        /// (доработка ревью S4f-2, MAJOR 3). Failed — названные проблемы частичного исхода:
        /// хроника, которую не удалось снять, и страницы, которые не удалось обработать (с причиной)
        /// — человек видит, что осталось на диске, а не молчание (доработка ревью S4f-1).</summary>
    public readonly record struct RetractResult(
        bool Ok,
        string? Code,
        string? Reason,
        IReadOnlyList<PackageOutcome> Outcomes,
        IReadOnlyList<string> RemovedChronicles,
        bool MarkedSkip,
        IReadOnlyList<UnlinkOutcome> Unlinked,
        IReadOnlyList<string> Failed)
    {
        /// <summary>Исход без снятия сносок (отказ входа, пустой случай, побайтные откаты):
        /// пустые списки S4f-2 по умолчанию — вызывающий S4f-1 не обязан их передавать.</summary>
        public RetractResult(
            bool Ok, string? Code, string? Reason,
            IReadOnlyList<PackageOutcome> Outcomes,
            IReadOnlyList<string> RemovedChronicles,
            bool MarkedSkip)
            : this(Ok, Code, Reason, Outcomes, RemovedChronicles, MarkedSkip, [], [])
        {
        }

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
        /// с известным состоянием, а сигнал неисправности раздела (хвосты ревью S4f-1).
        /// Пакеты, обработанные снятием сносок целиком (перешедшие из «applied» в «reverted»), из
        /// списка выпадают: для них мера 5 уже сделана. В списке остаются те, чью страницу
        /// обработать не удалось, — в том числе те, у которых часть страниц уже переписана, а хвост
        /// нет: необработанная страница — это и есть «требуется человеку» (доработка ревью S4f-2,
        /// MAJOR 2/3).</summary>
        public IEnumerable<PackageOutcome> NeedsUnlink
        {
            get
            {
                // Список читаем в локальную: лямбда внутри record-structa не может захватывать this.
                var unlinked = Unlinked;
                return Outcomes.Where(o => !o.Reverted && PagesRemained(o.Code) && !IsUnlinked(unlinked, o.PackageId));
            }
        }
    }

    /// <summary>Коды отказа отката, при которых страницы пакета остаются на диске. Явный
    /// перечень, а не «все коды»: см. RetractResult.NeedsUnlink.</summary>
    private static bool PagesRemained(string? code) =>
        code == WikiPackageRevert.CodeChanged
        || code == WikiPackageRevert.CodeNoApplied
        || code == WikiPackageRevert.CodeNoPreimages;

    // «Обработан» для списка меры 5 — только целиком: у пакета с необработанной страницей запись
    // есть (её Pages идут в журнал), и из списка он не выпадает (доработка ревью S4f-2, MAJOR 2/3).
    private static bool IsUnlinked(IReadOnlyList<UnlinkOutcome> unlinked, string packageId) =>
        unlinked.Any(u => u.PackageId == packageId && u.Complete);

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
        // когда с диска действительно что-то снято (п.6 шапки): побайтный откат, удалённая
        // хроника или реально переписанная снятием сносок страница.
        var marked = false;
        if (body.Outcomes.Any(o => o.Reverted) || body.RemovedChronicles.Count > 0 ||
            body.Unlinked.Any(u => u.Pages.Count > 0))
            marked = TryMarkSkip(ownerId, sourceRef!);

        return new RetractResult(true, null, null, body.Outcomes, body.RemovedChronicles, marked,
            body.Unlinked, body.Failed);
    }

    private (bool Ok, string? Code, string? Reason, List<PackageOutcome> Outcomes, List<string> RemovedChronicles,
        List<UnlinkOutcome> Unlinked, List<string> Failed)
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
            return (false, CodeInternal, $"внутренний сбой отката по источнику: {ex.Message}", [], [], [], []);
        }
    }

    private (bool Ok, string? Code, string? Reason, List<PackageOutcome> Outcomes, List<string> RemovedChronicles,
        List<UnlinkOutcome> Unlinked, List<string> Failed)
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
        // Failed — доработка ревью S4f-1: неснимаемая страница называется с причиной, пустой
        // catch её не глотает: человек видит, что осталось на диске.
        var removed = new List<string>();
        var failed = new List<string>();
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
                    // Одна неснимаемая страница не останавливает остальных — но она названа:
                    // «отсутствует в RemovedChronicles» без причины было бы молчанием.
                    failed.Add($"хроника «{op.Page}» не снята: {ex.Message}");
                }
            }
        }

        // П.7 шапки (S4f-2): снятие сносок на страницах пакетов из NeedsUnlink. Идёт ПОСЛЕ всех
        // побайтных откатов: встречный пакет цепочки ещё может быть откатан побайтно, и раньше
        // времени его страницу правкой не трогать (п.3 шапки).
        var unlinked = UnlinkPhase(ownerId, root, candidates, outcomes, sourceRef, failed);
        if (unlinked.Any(u => u.Pages.Count > 0))
            // Страницы вики переписаны — модель per-owner не должна остаться о них осведомлённой.
            _notes.Invalidate(ownerId);

        if (removed.Count > 0)
            // Файлы вики изменены — модель per-owner не должна остаться о них осведомлённой
            // (тот же инвариант, что у применения и отката пакета).
            _notes.Invalidate(ownerId);

        // П.8 шапки: строка op=retract на каждый раздел, где диск изменился, — по каноническому
        // порядку разделов (детерминированный журнал, не порядок обхода). Строки op=revert по
        // пакетам уже записаны механизмом отката; эта строка — факт операции по источнику:
        // страницы побайтно возвращённые, удалённые и снятые (переписанные), id откатанных и
        // обработанных пакетов. Пакет без реальных правок страниц строки не требует.
        foreach (var section in outcomes.Where(o => o.Reverted).Select(o => o.Section)
                     .Concat(removed.Select(SafeSection))
                     .Concat(unlinked.Where(u => u.Pages.Count > 0).Select(u => u.Section))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(s => s, StringComparer.Ordinal))
        {
            var pages = outcomes.Where(o => o.Reverted && o.Section == section)
                .SelectMany(o => o.RestoredPages)
                .Concat(removed.Where(p => SafeSection(p) == section))
                .Concat(unlinked.Where(u => u.Section == section).SelectMany(u => u.Pages))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            if (pages.Count == 0)
                continue;
            var packageIds = outcomes.Where(o => o.Reverted && o.Section == section)
                .Select(o => o.PackageId)
                .Concat(unlinked.Where(u => u.Section == section && u.Pages.Count > 0).Select(u => u.PackageId))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            WikiManifest.AppendLog(root, LogOpRetract, section, pages, sourceRef, "user", null, packageIds);
            // _log.md — производная от log.jsonl: после новой строки она обязана быть с ней
            // (тот же порядок, что у применения и отката пакета).
            WikiProjections.RegenerateSection(root, section);
        }

        return (true, null, null, outcomes, removed, unlinked, failed);
    }

    /// <summary>П.7 шапки (мера 5, второй абзац, S4f-2): для каждого пакета из NeedsUnlink —
    /// снять на его страницах сноски этого источника. Пакет, все страницы которого обработаны
    /// (переписаны или следов источника на них уже нет), переходит в «reverted» — единственный
    /// переход из «applied» (мера 4); пакет с необработанной страницей остаётся «applied» и
    /// назван в failed: о битой или не прошедшей гейт странице решает человек. Переписанные
    /// страницы возвращаются и у частичного пакета: они идут в журнал и проекции (п.8), хвост
    /// остаётся на человеке. Состояние раздела (страницы для резолва целей и гейта) читается с
    /// диска один раз на раздел и обновляется после каждой записи — следующий лист строится уже
    /// по новой.</summary>
    private List<UnlinkOutcome> UnlinkPhase(
        string ownerId, string root, List<WikiPackage> candidates, List<PackageOutcome> outcomes,
        string sourceRef, List<string> failed)
    {
        var unlinked = new List<UnlinkOutcome>();
        var states = new Dictionary<string, SectionState>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            var pkg = candidates[i];
            var outcome = outcomes[i];
            if (outcome.Reverted || !PagesRemained(outcome.Code))
                continue;
            var pages = UnlinkPages(root, pkg);
            if (pages.Count == 0)
                // Страниц пакета в вики нет (или все они — хроника, снятая п.5): снимать нечего,
                // статуса этот путь не меняет.
                continue;

            var state = states.TryGetValue(pkg.Section, out var cached) ? cached : BuildSectionState(root, ownerId, pkg.Section);
            states[pkg.Section] = state;

            var changed = new List<string>();
            var pkgFailed = 0;
            foreach (var page in pages)
            {
                var (done, reason) = UnlinkPageUnderLock(root, pkg, page, sourceRef, state);
                if (reason is not null)
                {
                    // Одна необработанная страница не останавливает остальных: пакет остаётся
                    // «applied» и каждая причина названа.
                    failed.Add($"пакет {pkg.Id}, страница «{page}»: {reason}");
                    pkgFailed++;
                    continue;
                }
                if (done)
                    changed.Add(page);
            }
            // Фактически переписанные страницы — в журнал и проекции независимо от исхода пакета
            // (п.8 шапки; доработка ревью S4f-2, MAJOR 3): на диске изменённая страница всегда
            // отражена в журнале. Пустой список строки не требует.
            unlinked.Add(new UnlinkOutcome(pkg.Id, pkg.Section, changed, pkgFailed == 0));
            if (pkgFailed > 0)
                // Необработанный хвост остаётся «applied» и в NeedsUnlink: о своей странице решает
                // человек. Повторный вызов этих же страниц не перепишет (следов уже нет) и вторую
                // строку журнала не допишет.
                continue;

            var transitioned = WikiPackageStore.Transition(root, pkg.Id, WikiPackage.StatusReverted);
            if (!transitioned.Ok)
                // Страницы обработаны, статус не доведён: следующий вызов перечитает пакет
                // («applied»), не найдёт на его страницах сносок источника и доведёт переход,
                // ничего не дописывая (тот же путь доводки, что у S4b-3).
                failed.Add($"пакет {pkg.Id}: страницы обработаны, статус «reverted» не записан: " +
                    $"{transitioned.Reason ?? transitioned.Code}");
        }
        return unlinked;
    }

    /// <summary>Лист страниц пакета для снятия сносок: по прообразам — тот же комплект, чем
    /// меряется побайтный откат (это то, что пакет реально записал); без прообразов — по
    /// операциям. Хроника исключается: она снимается целиком (п.5). Страницы, которой нет на
    /// диске, не обрабатывается.</summary>
    private static List<string> UnlinkPages(string root, WikiPackage pkg)
    {
        var preimagesDir = SafePath.Join(root, WikiApplyService.PreimagesRel(pkg.Id));
        IEnumerable<string> raw;
        if (Directory.Exists(preimagesDir))
            raw = Directory.EnumerateFiles(preimagesDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(preimagesDir, f).Replace('\\', '/'))
                .Where(p => !p.EndsWith(".tmp", StringComparison.Ordinal));
        else
            // Операция без страницы (служебная запись пакета) — не страница для снятия: здесь её
            // нет, иначе обход упал бы NRE уже после побайтных откатов, без журнала (хвост ревью).
            raw = [.. pkg.Operations.Where(op => op.Page is not null).Select(op => op.Page!)];
        return raw
            .Where(p => !IsChroniclePath(p) && File.Exists(SafePath.Join(root, "pages/" + p)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Снятие сносок источника на одной странице. Возврат: (список изменён, причина
    /// отказа); (false, null) — следов этого источника на странице нет (сняты раньше); (false,
    /// «…не опознан…») — сноска есть, но её цель не резолвится в карточку этого источника:
    /// страница не обработана и не тронута (MAJOR 2). Разбор — только моделью (WikiPage.Parse): цель сноски резолвится в карточку
    /// (та же нормализация заголовка, что у резолвера применения и uniqueness-гейта), и
    /// сноска принадлежит источнику, когда ref карточки совпадает с sourceRef («в любой части
    /// диапазонов», п.1 шапки). Ссылки переписываются по отображению модели (старый канонический
    /// id → новый s1..sN), а не разбором текста; утверждение секции-утверждений, потерявшее все
    /// сноски, убирается (мера 1: без подтверждения оно не остаётся), строка, которая сноски не
    /// имела, — не предмет этой операции, она остаётся как есть. Результат проходит тот же
    /// гейт, что применение (sourceTexts=null: новых сносок здесь нет, дословность принятых
    /// проверялась при их добавлении) — не прошедший не пишется.</summary>
    private static (bool Changed, string? Reason) UnlinkPageUnderLock(
        string root, WikiPackage pkg, string page, string sourceRef, SectionState state)
    {
        var full = SafePath.Join(root, "pages/" + page);
        var section = WikiSections.Parse(page);
        if (section is null)
            return (false, "страница не принадлежит ни одному разделу вики");

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (Exception ex)
        {
            return (false, $"страница не читается: {ex.Message}");
        }

        var parsed = WikiPage.Parse(text);
        if (parsed.Model is null)
            return (false, "страница не разбирается как страница вики: " + string.Join("; ", parsed.Errors));

        var kept = new List<(string OldId, WikiPage.WikiFootnote Fn)>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        for (var i = 0; i < parsed.Model.Footnotes.Count; i++)
        {
            var id = $"s{i + 1}";
            var fn = parsed.Model.Footnotes[i];
            switch (TraceOf(fn, sourceRef, state.Drafts))
            {
                case Trace.Source:
                    dropped.Add(id);
                    break;
                case Trace.Unknown:
                    // Снять её нельзя: цель не опознана. Она остаётся как есть и называется.
                    unknown.Add($"s{i + 1} → «{fn.Target.Text}»");
                    kept.Add((id, fn));
                    break;
                default:
                    kept.Add((id, fn));
                    break;
            }
        }
        // «Не опознано» — не «следов нет» (доработка ревью S4f-2, MAJOR 2): сноска, цель которой не
        // резолвится в карточку источника (та же проверка, что делает гейт: «сноска на неизвестную
        // карточку»), — оставленный след, атрибутировать который нельзя. Страница с такой сноской
        // не обработана и не тронута: она названа в отчёте, пакет остаётся «applied» и NeedsUnlink
        // — иначе сноска на снятый источник осталась бы навсегда.
        if (unknown.Count > 0)
            return (false, $"сноски с неопознанной целью: {string.Join(", ", unknown)} — карточка " +
                "этого источника на странице не найдена: след не опознан, страница не обработана");
        if (dropped.Count == 0)
            return (false, null); // на этой странице сносок этого источника уже нет: след снят раньше

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var k = 0; k < kept.Count; k++)
            remap[kept[k].OldId] = $"s{k + 1}";

        var newSections = new List<WikiPage.Section>(parsed.Model.Sections.Count);
        foreach (var sec in parsed.Model.Sections)
        {
            var isAssertion = WikiPage.AssertionSectionNames.Contains(sec.Name);
            var lines = new List<string>(sec.Lines.Count);
            foreach (var line in sec.Lines)
            {
                var rewritten = RefRx.Replace(line, m =>
                {
                    var id = m.Groups[1].Value;
                    if (dropped.Contains(id))
                        return "";
                    return remap.TryGetValue(id, out var newId) ? $"[^{newId}]" : m.Value;
                });
                if (isAssertion && line.Contains("[^") && !rewritten.Contains("[^"))
                    continue; // утверждение потеряло все свои сноски
                lines.Add(rewritten == line ? line : rewritten.TrimEnd());
            }
            newSections.Add(new WikiPage.Section { Name = sec.Name, Lines = lines });
        }

        var rebuilt = new WikiPage
        {
            Title = parsed.Model.Title,
            Frontmatter = parsed.Model.Frontmatter,
            Sections = newSections,
            Footnotes = [.. kept.Select(k => k.Fn)],
        };
        // Самопроверка формы: канонический рендер обязан читаться тем же парсером —
        // «ссылки в тексте и список сносок сходятся» здесь и проверяется.
        var reparsed = WikiPage.Parse(rebuilt.Render());
        if (reparsed.Model is null)
            return (false, "страница после снятия сносок не разбирается: " + string.Join("; ", reparsed.Errors));
        var finalText = reparsed.Model.Render();

        var working = new List<WikiGate.Draft>(state.Drafts);
        working.RemoveAll(d => d.Path == page);
        working.Add(new WikiGate.Draft(page, finalText));
        var violations = WikiGate.Check(working, section, state.UpChain);
        if (violations.Count > 0)
            return (false, "результат после снятия сносок не проходит гейт: " + violations[0]);

        try
        {
            // Запись состояния «до снятия сносок» — ОТДЕЛЬНЫМ файлом (п.7 шапки; доработка ревью
            // S4f-2, MAJOR 1): прообраз пакета остаётся байтами до его применения, его запись
            // состояния применения — хэшем того, что записал этот пакет: на них меряет побайтный
            // откат, и их подмена лишила бы цепочку состояния «до пакета» и сломала бы сверку
            // других пакетов той же страницы. Пакет без своей applied-записи её здесь не получает:
            // fail-closed «отката нет» сохраняется. Держится тот же срок 90 дней, что прообразы
            // (PurgeOldPreimages, решение 13).
            WikiApplyService.WriteBytesAtomic(root, RetractStateRel(pkg.Id, page), File.ReadAllBytes(full));
            WikiFileWriter.Write(root, "pages/" + page, finalText);
            // Манифест — о последнем состоянии страницы: он следует за файлом (сверяется с диском,
            // а не с историей применения).
            var newHash = WikiApplyService.Sha256OfBytes(File.ReadAllBytes(full));
            WikiManifest.RecordApplied(root, section, new Dictionary<string, string> { [page] = newHash });
        }
        catch (Exception ex)
        {
            return (false, $"запись страницы не удалась: {ex.Message}");
        }

        // Состояние раздела обновляется записанной страницей: следующая строит рендер и гейт
        // по новому состоянию, а не по прежнему.
        state.Drafts.RemoveAll(d => d.Path == page);
        state.Drafts.Add(new WikiGate.Draft(page, finalText));
        return (true, null);
    }

    /// <summary>Состояние раздела для резолва и гейта: страницы своего раздела и цепочки «вверх»
    /// с диска (тот же состав, что строит проверка применения), служебные «_*» — не страницы.</summary>
    private sealed class SectionState
    {
        public required IReadOnlyList<string> UpChain;
        public required List<WikiGate.Draft> Drafts;
    }

    private SectionState BuildSectionState(string root, string ownerId, string section)
    {
        var upChain = WikiSections.UpChain(section, ownerId, _spheres);
        var drafts = new List<WikiGate.Draft>();
        foreach (var sec in new[] { section }.Concat(upChain))
        {
            var folder = WikiSections.Folder(sec);
            if (folder is null)
                continue;
            var dir = SafePath.Join(root, "pages/" + folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                var pageKey = folder + Path.GetRelativePath(dir, f).Replace('\\', '/');
                if (WikiPaths.IsProjection(pageKey))
                    continue;
                try
                {
                    drafts.Add(new WikiGate.Draft(pageKey, File.ReadAllText(f)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Нечитаемая чужая страница не участница резолва: свой лист она не получит,
                    // сноска на неё не резолвится, а гейт на целевой странице это увидит
                    // (тот же порядок, что у обхода CheckSectionForCapture).
                }
            }
        }
        return new SectionState { UpChain = upChain, Drafts = drafts };
    }

    /// <summary>К какому источнику относится сноска страницы (доработка ревью S4f-2, MAJOR 2 —
    /// «следов нет» и «не опознано» разные исходы): Source — цель резолвится в карточку этого
    /// источника (та же нормализация заголовка, что у резолвера применения (FindCard) и
    /// uniqueness-гейта; с префиксом раздела — в карточку этого раздела, тот же разбор, что у
    /// CheckLinks; ref сверяется с sourceRef тем же совпадением, что карточки пакета, п.1 шапки)
    /// — снимается. Other — цель резолвится в карточку другого источника: это не след данного,
    /// сноска остаётся как есть. Unknown — цель не резолвится ни в одну карточку источника:
    /// карточка удалена или переименована, вне цепочки, цель написана не ссылкой: след есть, а
    /// атрибутировать его нельзя — страница не обработана.</summary>
    private enum Trace
    {
        Source,
        Other,
        Unknown,
    }

    private static Trace TraceOf(WikiPage.WikiFootnote fn, string sourceRef, List<WikiGate.Draft> state)
    {
        if (!fn.Target.IsLink)
            return Trace.Unknown; // закрытая форма цели: карточки с таким именем нет (тот же исход, что у гейта)
        var title = fn.Target.Text;
        string? sectionFilter = null;
        if (title.Contains('/'))
        {
            sectionFilter = WikiSections.Parse(title);
            if (sectionFilter is null)
                return Trace.Unknown; // префикс не раздел: цель ни в какую карточку не ведёт
            title = title.Split('/')[^1];
        }
        var norm = WikiDedup.NormalizeName(title);
        if (norm.Length == 0)
            return Trace.Unknown;
        foreach (var draft in state)
        {
            if (sectionFilter is not null && WikiSections.Parse(draft.Path) != sectionFilter)
                continue;
            var card = WikiPage.Parse(draft.Content).Model;
            if (card is null || card.Frontmatter.Type != WikiFrontmatter.TypeSource)
                continue;
            if (WikiDedup.NormalizeName(card.Title) != norm)
                continue;
            var cardRef = card.Frontmatter.Ref;
            if (string.IsNullOrEmpty(cardRef))
                continue;
            if (RefMatches(cardRef, sourceRef))
                return Trace.Source;
            var origin = OriginOf(sourceRef);
            if (origin is not null && card.Frontmatter.Origin == origin && RefMatches(cardRef, IdPartOf(sourceRef)))
                return Trace.Source;
            return Trace.Other; // карточка найдена — это другой источник
        }
        return Trace.Unknown; // карточки с такой целью в разделе и его цепочке нет
    }

    /// <summary>Состояние страницы «до снятия сносок этого пакета»: рядом с прообразом применения,
    /// но не вместо него (п.7 шапки). Ключ — id пакета, чьи сноски снимаются: пакет снимается один
    /// раз (после снятия он «reverted»), повторный виток доводки пишет те же байты поверх тех же.
    /// Срок хранения — тот же, что у прообразов (WikiPackageRevert.PurgeOldPreimages).</summary>
    private static string RetractStateRel(string packageId, string pageKey) =>
        $"keeper/retract/{packageId}/{pageKey}";

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

    // internal: форму источника читает REST (WikiPackagesController) — та же разборка, что у
    // поиска пакетов, а не вторая копия правил.
    internal static string? OriginOf(string sourceRef) =>
        WikiGate.OriginValues.FirstOrDefault(o => sourceRef.StartsWith(o + ":", StringComparison.Ordinal));

    /// <summary>Карточ после префикса origin, без диапазона: «session:{id}#m1-9» → «{id}».</summary>
    internal static string IdPartOf(string sourceRef)
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
