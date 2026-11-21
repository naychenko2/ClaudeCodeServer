using System.Collections.Concurrent;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;

namespace ClaudeHomeServer.Services.Notes.Wiki;

/// <summary>
/// Проход облачного судьи спорных правок вики (J-J3a): отдельный идемпотентный проход
/// поверх «Входящих» — спорные операции ИИ-пакетов решает облачная модель, вердикты
/// пишет стор, решение человека не переворачивает (И1). Синтез ночи остаётся локальным
/// (И3): этот проход зовёт модель ТОЛЬКО ключом LocalActionCatalog.Wiki-judge; вызов
/// идёт через тот же шов, что у разбора (ICheapTextRunner), и по обычной цепочке места,
/// выбранной админом: при отказе облака в локальную модель проход не переходит (решение
/// Андрея 12.11) — повтор прохода сам попыткой не карает, судья не дублирует фолбэк.
///
/// Тумблер «Применять без ревью» выключен — прохода нет вовсе: ручной режим, ни одного
/// вызова, файл пакета не тронут (мера 7: «Входящие» решает человек).
///
/// Кандидат — открытый пакет автора ai с хотя бы одной нерешённой спорной операцией
/// (Decision=inbox, ни HumanDecision, ни JudgeDecision). По каждой операции:
/// - код из CodeRejected → отказ выносит код без вызова модели (И4′), вердикт — тем же
///   путём, что решение человека (JudgeOperations), метка JudgeModelCode;
/// - кода нет (старый пакет) или autoapply_off → операция не трогается: судье не о чём
///   решать, а старый пакет без кода к судье не допущен (И4′);
/// - код из JudgeAdmitted → операция идёт в вызов модели.
///
/// Вызов — ОДИН на группу «пакет + целевая страница» (уточнение ревью J-J1b, находки 3–4:
/// BuildInput несёт один pageText, а операции пакета могут быть о разных страницах): в
/// группу входят все допущенные операции этой страницы, в вход — текст ЕЁ страницы.
/// Индексы операций внутри вызова уникальны: их даёт сам проход — позиция операции в
/// Operations пакета (счётчик по пакету), allowedIndexes для разбора — те же значения;
/// дубл проход не передаёт вовсе (группа — множество индексов), и ArgumentException от
/// BuildInput остался бы багом вызывающего, а не «шумом модели».
///
/// Цена вызова (OneShotUsage.CostUsd) делится поровну между операциями вызова: вердикт
/// по группе — один счёт на все её операции. Длины claim, цитат и окрестности ограничены
/// на стороне прохода (хвост ревью J-J1b №6): потолок промпта — размер входных данных,
/// а не доверие к ним.
///
/// Ответ → WikiJudgeVerdict.Parse → JudgeOperations. Операция без вердикта — попытка +1;
/// на 3-й неудаче по этой операции отказ выносит код («судья не вынес решения за 3
/// попытки») — пакет не может висеть в «Входящих» бесконечно, а человек не обязан
/// дежурить у прохода. Отказ вызова (исключение, пустой ответ) — попытка +1 всем
/// допущенным операции группы: пакет остаётся proposed, Deferred считает их, FirstFailure
/// несёт первую причину. Отмена прохода (ct) — не отказ модели: вызов, прерванный отменой,
/// не карает попыток группе, проход останавливается на месте (ревью J-J3a, Н2).
///
/// Пакет, у которого проход решил ВСЕ спорные операции, применяется тем же путём, что
/// решение человека (ApplyDecidedAsync, J-J2b): решение замкнуто применением (S4g).
/// Пакет, который решил человек, проход не применяет повторно: его применил его же
/// запрос решения.
///
/// Конкурентность:
/// - замок прохода — по корню владельца: ключ — путь корня, не экземпляр службы (тот же
///   приём, что у _rootLocks стора триажа): второй одновременный RunAsync того же
///   владельца ждёт первого, а не ведёт второй параллельный проход той же вики;
/// - устаревший ответ — не исключение: пока модель думала, человек мог решить операцию,
///   а пакет мог уйти в stale; JudgeOperations такую операцию пропускает, решение
///   человека остаётся, и в счётчики прохода она не идёт: считается только то, что
///   реально записалось в файл (ревью J-J2a: Ok(pkg) бывает и при записе, и при пропуске);
/// - любое исключение (модель, разбор, стор) ловится внутри прохода и наружу не выходит:
///   итог несёт FirstFailure, «Входящие» и вызывающий (J-J4) не падают там, где решения
///   уже записаны.
///
/// Риск, о котором этот проход знает, но не решает (ревью J-J2a, хвост 03350e5e):
/// IsSupersedable (WikiPackageStore.cs:461) гасит прежний пакет того же окна новым
/// разбором, и оплаченный вердикт судьи может уйти в «stale» вместе с пакетом. Гашение
/// — не этот файл; решение — отдельной задачей.
/// </summary>
public sealed class WikiJudgeSweep
{
    /// <summary>Потолок утверждения входе судьи — та же мера, что у формы пакета
    /// (WikiPackage.MaxClaimLength): дальше судья не читает.</summary>
    public const int MaxClaimChars = WikiPackage.MaxClaimLength;

    /// <summary>Потолок одной цитаты входе — та же мера, что у гейта (WikiPage).</summary>
    public const int MaxQuoteChars = WikiPage.MaxQuoteLength;

    /// <summary>Окрестность цитаты: ±столько символов вокруг неё в тексте страницы.</summary>
    public const int ExcerptRadiusChars = 200;

    /// <summary>Потолок окрестности в целом (цитата + радиусы с обеих сторон).</summary>
    public const int MaxExcerptChars = MaxQuoteChars + 2 * ExcerptRadiusChars;

    /// <summary>Потолок текста целевой страницы входе судьи: судье нужен контекст
    /// страницы, не вся страница.</summary>
    public const int MaxPageChars = 8000;

    /// <summary>После стольких неудач по одной операции вердикт выносит код (шаг 5).</summary>
    public const int MaxAttempts = 3;

    /// <summary>Метка, когда вердикт вынесла модель, но имени модели шов не назвал (usage=null).
    /// Отказ, который вынес код (по коду причины или после исчерпания попыток), — не вердикт
    /// модели: он несёт метку WikiPackage.JudgeModelCode (ревью J-J3a, Н4).</summary>
    public const string ModelUnknown = "unknown";

    /// <summary>Заголовок блока страницы B входе merge-группы: инструкция — без пути B
    /// (путь выбран моделью и остаётся внутри рамки данных, ревью C5, дельта п. 3);
    /// привязка блока к операции — строкой «страница B: …» внутри рамки и merge_into
    /// того же index (вердикт C5, п. 2); пометка «обрезано» — что текст усечён до
    /// потолка и судья дальше не видит (вердикт C5, п. 4).</summary>
    internal static string MergeTargetLabel(bool truncated) =>
        $"ТЕКСТ СТРАНИЦЫ B — ЦЕЛИ СЛИЯНИЯ merge{(truncated ? ", ОБРЕЗАНО" : "")} (данные):";

    /// <summary>Замок прохода: ключ — путь корня вики, не экземпляр (тот же приём, что
    /// у _rootLocks / _createLocks сторов: разные вики друг друга не держат).</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _rootLocks = new(StringComparer.Ordinal);

    private readonly ICheapTextRunner _llm;
    private readonly WikiApplyService _apply;
    private readonly string _dataDir;

    public WikiJudgeSweep(ICheapTextRunner llm, WikiApplyService apply, IConfiguration config)
    {
        _llm = llm;
        _apply = apply;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя):
        // корень прохода выводится из него и владельца, а не принимается из пакета или
        // чужого контекста (J-J2b: ApplyDecidedAsync берёт root от вызывающего — пусть
        // это будет корень этого владельца).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Итог прохода. Calls — сколько вызовов модели состоялось; CostUsd — сумма
    /// их стоимости (то, что взял облако; по операциям делится поровну — это в полях
    /// JudgeCostUsd, здесь — факт вызовов). Applied / Rejected — операции, чей вердикт
    /// РЕАЛЬНО записался в файл прохода (accepted / rejected судьёй); CodeRejected —
    /// отказы, вынесенные кодом: по коду причины без вызова модели (И4′) и «судья не вынес
    /// решения за N попытки» — ни одна модель такого отказа не выносила; отдельный счёт,
    /// в Rejected не входят. Deferred — операции, которым засчитана попытка без вердикта (отказ
    /// вызова или ответ без вердикта по этой операции). FirstFailure — первая причина
    /// любого сбоя прохода (отказ вызова, отказ стора, чужой корень); null — сбоя не было.
    /// Пропущенное молча (человек решил во время вызова, пакет ушёл в stale) в счётчики
    /// не идёт: проход считает только своё запись.</summary>
    public readonly record struct JudgeSweepResult(
        int Calls, decimal CostUsd, int Applied, int Rejected, int Deferred, int CodeRejected,
        string? FirstFailure);

    /// <summary>Пройти вики владельца: вердикты по всем допущенным спорным операциям
    /// открытых ИИ-пакетов. ownerId — владелец; root — корень, который называет
    /// вызывающий: проход работает только по СВОЕМУ корню владельца
    /// (WikiPaths.Root(_dataDir, ownerId)) и чужой root отказывает на входе без единого
    /// вызова. Исключений наружу нет; ct — остановка прохода (остановка службы, закрытие
    /// окна): вызов, прерванный отменой, не карает попытки группе (отмена — не отказ модели),
    /// новых вызовов не начинается.</summary>
    public async Task<JudgeSweepResult> RunAsync(string ownerId, string root,
        IReadOnlySet<string>? onlyPackageIds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return new JudgeSweepResult(0, 0m, 0, 0, 0, 0, "владелец пустой");
        if (string.IsNullOrWhiteSpace(root))
            return new JudgeSweepResult(0, 0m, 0, 0, 0, 0, "корень не назван");

        string ownRoot;
        try
        {
            ownRoot = WikiPaths.Root(_dataDir, ownerId);
            // Нормализация названного корня — тоже здесь: Path.GetFullPath может бросить
            // (недопустимые символы пути), и это — отказ с причиной на входе, а не исключение
            // наружу (ревью J-J3a, Н5).
            if (!string.Equals(Path.GetFullPath(root), Path.GetFullPath(ownRoot), StringComparison.OrdinalIgnoreCase))
                return new JudgeSweepResult(0, 0m, 0, 0, 0, 0,
                    "корень прохода не корень этого владельца: работать можно только по своей вики");
        }
        catch (ArgumentException e)
        {
            return new JudgeSweepResult(0, 0m, 0, 0, 0, 0, e.Message);
        }

        var sem = _rootLocks.GetOrAdd(Path.GetFullPath(ownRoot), _ => new SemaphoreSlim(1));
        var counters = new SweepCounters();
        try
        {
            // Ожидание чужого прохода тоже под обхватом: отмена ct — не исключение
            // наружу, а итог с причиной (вызывающий различит её по тексту).
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await RunOwnedAsync(ownerId, ownRoot, onlyPackageIds, counters, ct).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }
        catch (Exception e)
        {
            // Всё, чем бьётся проход (стор, чтение настрое, сбой сборки входа), — здесь:
            // наружу не выходит ничего. Сделанное до сбоя — в итоге как сделано, а не
            // обнулено: вердикты, записанные до отказа, реальны.
            counters.FirstFailure ??= e.Message;
            return counters.ToResult();
        }
    }

    private async Task<JudgeSweepResult> RunOwnedAsync(string ownerId, string root,
        IReadOnlySet<string>? onlyPackageIds, SweepCounters counters, CancellationToken ct)
    {
        // 1. Ручной режим: выключатель «Применять без ревью» выключен — ни одного вызова,
        // ни одной записи (мера 7: всё как раньше, решает человек).
        if (!WikiSettingsStore.Read(root).Settings.AutoApplyEnabled)
            return new JudgeSweepResult(0, 0m, 0, 0, 0, 0, null);

        foreach (var candidate in WikiPackageStore.List(root).Packages)
        {
            if (ct.IsCancellationRequested)
                break;
            // Добор ночи (M1): решаем только названные пакеты; null — полный проход.
            if (onlyPackageIds is not null && !onlyPackageIds.Contains(candidate.Id))
                continue;
            if (candidate.Status != WikiPackage.StatusProposed || candidate.Author != WikiPackage.AuthorAi)
                continue;
            if (!candidate.Operations.Any(IsUndecidedContested))
                continue;
            await ProcessPackageAsync(ownerId, root, candidate, counters, ct).ConfigureAwait(false);
        }
        return counters.ToResult();
    }

    /// <summary>Один пакет: сначала отказы по коду (без модели, И4′), затем — по одному
    /// вызову на группу «страница», затем — применение, если проход довел все спорные
    /// операции до решения. Состояние пакета держится по последнему ЗАПИСАННОму чтению
    /// стора: после своей записи следующий шаг видит диск, а не своё прежнее чтение.</summary>
    private async Task ProcessPackageAsync(string ownerId, string root, WikiPackage candidate,
        SweepCounters counters, CancellationToken ct)
    {
        var current = candidate;

        // 3а. Отказ по коду: внешний текст и fail-closed ветки — судье там не о чём
        // решать; вердикт выносит код, модель не звалась (И4′).
        var codeOutcomes = current.Operations
            .Select((op, i) => (op, i))
            .Where(t => IsUndecidedContested(t.op) && WikiApplyPolicy.CodeRejected(t.op.ReasonCode))
            .Select(t => new WikiPackageStore.JudgeOutcome(
                t.i, false, $"отклонено кодом: {t.op.Reason ?? "причина не названа"}",
                WikiPackage.JudgeModelCode, null))
            .ToList();
        if (codeOutcomes.Count > 0)
        {
            current = WriteOutcomes(root, current, codeOutcomes, counters) ?? current;
        }

        // 3б/4. Допущенные операции — группами по целевой странице: один вызов на группу.
        var groups = current.Operations
            .Select((op, i) => (op, i))
            .Where(t => IsUndecidedContested(t.op) && WikiApplyPolicy.JudgeAdmitted(t.op.ReasonCode))
            .GroupBy(t => t.op.Page, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (ct.IsCancellationRequested)
                return;
            var members = group.ToList();
            var pageText = ReadPageOrNull(root, group.Key);
            // Merge (операция 13): судье нужны ОБЕ страницы — A (Page, она же страница
            // группы) и B (Target). B читается по своему пути; дубль цели читается один
            // раз; A-цель дублирует текст группы и не перечитывается. Порядок во входе:
            // A раньше B (контракт C5). Инвариант (вердикт ревью C5, п. 1): merge не
            // доходит до модели без обоих текстов — B не читается (нет файла, путь вне
            // pages/, Target не назван вовсе) → операция отклоняется кодом с причиной
            // «страница B недоступна», модель по ней не звалась (тот же путь, что у
            // отказа по коду, И4′). Дедуп — по результату чтения, а не по одному пути:
            // при нечитаемой B кодом отклоняются ВСЕ операции с этой целью — дедуп по
            // пути раньше чтения оставлял вторую merge-операцию идти к модели без
            // текста B (ревью C5, дельта п. 1).
            var targetTexts = new List<(string Path, string Text)>();
            var readTargets = new Dictionary<string, string?>(StringComparer.Ordinal);
            var missingB = new List<(WikiPackage.Operation Op, int Index)>();
            foreach (var t in members)
            {
                if (t.op.Op != WikiPackage.OpMerge)
                    continue;
                var target = t.op.Target;
                if (string.IsNullOrEmpty(target))
                {
                    missingB.Add(t);
                    continue;
                }
                if (string.Equals(target, group.Key, StringComparison.Ordinal))
                    continue; // A-цель: текст B уже во входе как текст группы
                if (!readTargets.TryGetValue(target, out var bText))
                {
                    bText = ReadPageOrNull(root, target);
                    readTargets[target] = bText;
                    if (bText is not null)
                        targetTexts.Add((target, bText));
                }
                if (bText is null)
                    missingB.Add(t);
            }
            if (missingB.Count > 0)
            {
                current = WriteOutcomes(root, current,
                    missingB.Select(t => new WikiPackageStore.JudgeOutcome(
                        t.Index, false, "отклонено кодом: страница B недоступна",
                        WikiPackage.JudgeModelCode, null)).ToList(), counters) ?? current;
                var rejected = missingB.Select(t => t.Index).ToHashSet();
                members = members.Where(t => !rejected.Contains(t.i)).ToList();
                if (members.Count == 0)
                    continue; // вся группа отклонена кодом: вызова нет
            }
            var input = BuildGroupInput(members, pageText, targetTexts);

            string? text;
            OneShotUsage? usage;
            // Вызов считался с момента, когда он начат: отказанное обращение к модели —
            // тоже обращение (его стоимость может и не быть, но попытка группы была).
            counters.Calls++;
            try
            {
                var result = await _llm.RunDetailedAsync(
                    LocalActionCatalog.WikiJudge, input, fallbackModel: null, ownerId,
                    jsonFormat: "json", ct: ct).ConfigureAwait(false);
                text = result.Text;
                usage = result.Usage;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Отмена прохода — не отказ модели: судье не дали ответить, и это не его
                // отказ. Ни одной операции группы попытка не засчитана, пакет ждёт следующего
                // прохода с теми же попытками; проход останавливается здесь (внешний цикл
                // видит ct и новых групп не начнёт). Ревью J-J3a, Н2.
                return;
            }
            catch (Exception e)
            {
                // Отказ вызова (таймаут, 429, занятый слот, прочее) — попытка +1 всем
                // допущенным группы; пакет остаётся proposed. Первая причина — в итог.
                counters.FirstFailure ??= $"судья: вызов модели не удался: {e.Message}";
                current = WriteOutcomes(root, current,
                    members.Select(t => FailOutcome(t.op, t.i)).ToList(), counters) ?? current;
                continue;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                counters.FirstFailure ??= "судья: модель дала пустой ответ";
                current = WriteOutcomes(root, current,
                    members.Select(t => FailOutcome(t.op, t.i)).ToList(), counters) ?? current;
                continue;
            }

            var allowed = members.Select(t => t.i).ToHashSet();
            var (verdicts, _) = WikiJudgeVerdict.Parse(text, allowed);
            var byIndex = new Dictionary<int, WikiJudgeVerdict.Item>();
            foreach (var v in verdicts)
                byIndex[v.Index] = v;

            var model = usage?.Model ?? ModelUnknown;
            decimal callCost = usage?.CostUsd is { } c ? (decimal)c : 0m;
            decimal perOp = members.Count > 0 ? callCost / members.Count : 0m;
            counters.CostUsd += callCost;

            var outcomes = members.Select(t => byIndex.TryGetValue(t.i, out var v)
                ? new WikiPackageStore.JudgeOutcome(t.i, v.Apply, v.Why, model, perOp)
                // Операция без вердикта — попытка +1 (на 3-й — отказ кода).
                : FailOutcome(t.op, t.i)).ToList();
            current = WriteOutcomes(root, current, outcomes, counters) ?? current;
        }

        // 6. Пакет, который довел до решения сам проход, применяется тем же путём, что
        // решение человека (J-J2b). Пакет, который решил только человек, — не здесь:
        // его применил его же запрос решения.
        if (counters.WroteVerdictFor(candidate.Id)
            && current.Status is WikiPackage.StatusAccepted or WikiPackage.StatusPartiallyAccepted)
        {
            var decided = await _apply.ApplyDecidedAsync(ownerId, root, current).ConfigureAwait(false);
            // Отказ применения — сбой прохода: по контракту итога FirstFailure несёт первую
            // причину любого сбоя, и причина применения — в ней (ревью J-J3a, Н3).
            // PackageMissing — не отказ: решение вынесено, файл ушёл в гонке с удалением (W3-1).
            if (decided.Apply is { Applied: false } app)
                counters.FirstFailure ??= $"судья: применение отказало: {app.Reason ?? app.Code}";
            else if (decided.Outcome == WikiApplyService.DecidedOutcome.PackageUnreadable)
                counters.FirstFailure ??= $"судья: {decided.Reason ?? "файл пакета не читается"}";
        }
    }

    /// <summary>Записать вердикты стору и пересчитать счётчики по ЗАПИСАННОМУ состоянию:
    /// в Applied/Rejected/CodeRejected/Deferred идёт только то, что реально легло в файл
    /// (ревью J-J2a: Ok(pkg) возвращает и запись, и молчаливый пропуск). null — стор
    /// отказал (пакета нет, он нечитаем, вне proposed): причина в FirstFailure, состояние
    /// пакета не изменилось.</summary>
    private WikiPackage? WriteOutcomes(string root, WikiPackage before,
        IReadOnlyList<WikiPackageStore.JudgeOutcome> outcomes, SweepCounters counters)
    {
        var res = WikiPackageStore.JudgeOperations(root, before.Id, outcomes);
        if (!res.Ok || res.Package is null)
        {
            counters.FirstFailure ??= $"судья: стор не принял вердикты: {res.Reason ?? res.Code}";
            return null;
        }
        var after = res.Package;
        var wrote = false;
        foreach (var o in outcomes)
        {
            if (o.Index < 0 || o.Index >= after.Operations.Count)
                continue;
            var opAfter = after.Operations[o.Index];
            var opBefore = before.Operations[o.Index];
            if (opAfter.JudgeDecision == WikiPackage.HumanAccepted)
            {
                counters.Applied++;
                wrote = true;
            }
            else if (opAfter.JudgeDecision == WikiPackage.HumanRejected)
            {
                // Источник отказа — на записанном вердикте: метка «code» несёт отказ,
                // вынесенный кодом (по коду причины или после исчерпания попыток); отказ
                // модели идёт в Rejected (ревью J-J3a, Н4).
                if (opAfter.JudgeModel == WikiPackage.JudgeModelCode) counters.CodeRejected++;
                else counters.Rejected++;
                wrote = true;
            }
            else if (opAfter.JudgeAttempts > opBefore.JudgeAttempts)
            {
                // Вердикта нет — засчитана попытка: пакет ждёт следующего прохода.
                counters.Deferred++;
            }
            // Иначе — молчаливый пропуск (человек решил во время вызова, пакет ушёл в
            // stale, индекса больше нет): решение человека сохранено, в счётчики не идёт.
        }
        if (wrote)
            counters.MarkWritten(before.Id);
        return after;
    }

    /// <summary>Исход неудачи по одной операции: до 3-й — «вердикта нет» (стор поднимает
    /// JudgeAttempts), на 3-й — отказ, который выносит код: «судья не вынес решения за
    /// 3 попытки». Ни одна модель такого отказа не выносила — метка та же, что у отказа по
    /// коду: JudgeModelCode (ревью J-J3a, Н4).</summary>
    private static WikiPackageStore.JudgeOutcome FailOutcome(WikiPackage.Operation op, int index) =>
        op.JudgeAttempts + 1 >= MaxAttempts
            ? new WikiPackageStore.JudgeOutcome(index, false,
                $"судья не вынес решения за {MaxAttempts} попытки", WikiPackage.JudgeModelCode, null)
            : new WikiPackageStore.JudgeOutcome(index, null, null, ModelUnknown, null);

    private static bool IsUndecidedContested(WikiPackage.Operation op) =>
        op.Decision == WikiPackage.DecisionInbox && op.HumanDecision is null && op.JudgeDecision is null;

    /// internal static: форма входа группы тестируется напрямую (тот же приём, что у
    /// WikiApplyPolicy.Auto) — склейка страниц merge в неё своя, и тесту нужен результат
    /// метода, а не проход с фейком модели.
    /// <summary>Вход судьи для одной группы: System + блоки операций (с потолка на
    /// стороне прохода), окрестности цитат и текст целевой страницы группы. Окрестности
    /// считаются по тексту страницы: текста источника на диске нет — ни карточка, ни
    /// пакет его не несут (WikiApplyService.cs:724), а цитата, которая есть на странице
    /// (цель supersede/conflict), — ровно тот контекст, по которому судья судит
    /// «противоречит ли claim тексту страницы». Пустая окрестность в вход не идёт.
    /// Длины claim, цитат и окрестности ограничены на стороне прохода (хвост ревью
    /// J-J1b №6). Для merge (операция 13) к входу добавляются тексты страниц B (целей
    /// merge-операций группы): путь B — строкой внутри блока, как недоверенный текст
    /// (ревью C5, дельта п. 3); каждый текст обрезается до MaxPageChars с пометкой
    /// «обрезано» в заголовке, A (текст целевой страницы группы) стоит раньше любой B —
    /// вердикт по merge судья выносит только apply/reject («переписать» нет). B без
    /// текста сюда не доходит: нечитаемую B проход отклоняет кодом до вызова (инвариант
    /// вердикта C5, п. 1).</summary>
    internal static string BuildGroupInput(List<(WikiPackage.Operation Op, int Index)> members,
        string? pageText, IReadOnlyList<(string Path, string Text)> mergeTargetTexts)
    {
        var items = members.Select(t => new WikiJudgePrompts.WikiJudgeItem(
            t.Index,
            t.Op.Op,
            Cut(t.Op.Claim ?? t.Op.Text ?? "", MaxClaimChars),
            (t.Op.Quotes ?? []).Select(q => Cut(q, MaxQuoteChars)).ToList(),
            t.Op.ReasonCode ?? "",
            t.Op.Reason ?? "",
            t.Op.Page,
            t.Op.Target ?? "")).ToList();

        var contexts = new List<WikiJudgePrompts.WikiJudgeQuoteContext>();
        if (pageText is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (op, _) in members)
            {
                foreach (var q in op.Quotes ?? [])
                {
                    if (!seen.Add(q))
                        continue;
                    var excerpt = Cut(WikiJudgePrompts.QuoteExcerpt(pageText, q, ExcerptRadiusChars), MaxExcerptChars);
                    if (excerpt.Length > 0)
                        contexts.Add(new WikiJudgePrompts.WikiJudgeQuoteContext(q, excerpt));
                }
            }
        }

        var input = WikiJudgePrompts.BuildInput(items, pageText, MaxPageChars, contexts);
        foreach (var (path, text) in mergeTargetTexts)
            input += MergePageBlock(path, text);
        return WikiJudgePrompts.System + "\n\n" + input;
    }

    /// <summary>Блок страницы B для merge: та же рамка данных, что у BuildInput (маркеры
    /// публичные в Prompts; экранирование разделителя — здесь, та же формула, что у
    /// Prompts.Escaped). Путь B — текст, выбранный моделью (merge_into): он стоит
    /// строкой «страница B: …» ВНУТРИ рамки данных, как остальной недоверенный текст,
    /// а не в заголовке — в инструктивную часть промпта он не попадает (ревью C5,
    /// дельта п. 3); привязка блока к операции — этой строкой и merge_into того же
    /// index (вердикт C5, п. 2). Усечение до MaxPageChars видно судье пометкой
    /// «обрезано» в заголовке (п. 4). Потолок входа merge остаётся 2 × MaxPageChars.</summary>
    private static string MergePageBlock(string path, string text)
    {
        var truncated = text.Length > MaxPageChars;
        var escaped = ("страница B: " + path + "\n" + Cut(text, MaxPageChars))
            .Replace(WikiJudgePrompts.DataOpen, "＜" + WikiJudgePrompts.DataOpen[1..^1] + "＞")
            .Replace(WikiJudgePrompts.DataClose, "＜" + WikiJudgePrompts.DataClose[1..^1] + "＞");
        return "\n\n" + MergeTargetLabel(truncated) + "\n" + WikiJudgePrompts.DataOpen + "\n" + escaped + "\n"
            + WikiJudgePrompts.DataClose + "\n";
    }

    /// <summary>Текст страницы с диска: null — страницы нет (create ещё не применён) или
    /// файл не читается: судье остаются операции и цитаты — промпт это допускает.</summary>
    private static string? ReadPageOrNull(string root, string pageKey)
    {
        try
        {
            var full = SafePath.Join(Path.Combine(root, "pages"), pageKey);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Счётчики одного прохода. WroteVerdictFor — по каким пакетам этот проход
    /// записал хотя бы один вердикт (только такой пакет он и применяет на шаге 6).</summary>
    private sealed class SweepCounters
    {
        public int Calls;
        public decimal CostUsd;
        public int Applied;
        public int Rejected;
        public int Deferred;
        public int CodeRejected;
        public string? FirstFailure;

        private readonly HashSet<string> _written = new(StringComparer.Ordinal);

        public void MarkWritten(string packageId) => _written.Add(packageId);
        public bool WroteVerdictFor(string packageId) => _written.Contains(packageId);

        public JudgeSweepResult ToResult() =>
            new(Calls, CostUsd, Applied, Rejected, Deferred, CodeRejected, FirstFailure);
    }
}
