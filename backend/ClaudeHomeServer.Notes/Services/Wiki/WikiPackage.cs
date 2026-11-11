namespace ClaudeHomeServer.Services.Notes.Wiki;

// Пакет изменений вики (ADR-035, решение 4 и 12г): пакет хранит НАМЕРЕНИЯ, файлы рендерит
// код при применении (S4b). Пакет — в пределах одного раздела: межраздельное изменение —
// повышение, это действие человека, а не пакет модели (ADR-036).
//
// Каждая операция несёт класс правки `origin × op` и решение политики `auto` | `inbox` с
// причиной: неспорные применяет код сам, спорные ждут человека («Входящие»). Класс и решение
// ставит код, не модель: риск — свойство операции, а не её самооценка (политика — S4d).
//
// Форма на диске — v:1 (смена формы — инкремент ModelVersion и пересчёт старых, как с
// BackupSchema.Version). Читается и пишется через WikiPackageStore.
public sealed record WikiPackage
{
    public const int ModelVersion = 1;

    /// <summary>Лимит открытых пакетов на всю вики (ADR-035, решение 4): ночной разбор
    /// упирается в него и останавливает себя, а не копит очередь; спорные во «Входящих»
    /// входят в лимит.</summary>
    public const int MaxOpenPackages = 20;

    /// <summary>Предел длины утверждения операции `claim` (ADR-039, решение 2).</summary>
    public const int MaxClaimLength = 600;

    // Операций пять; удаления нет (ADR-035, решение 4): снятие знания — откат по пакету
    // или по источнику (решение 12г, меры 4–5), а не операция пакета.
    public const string OpCreate = "create";
    public const string OpAppend = "append";
    public const string OpSupersede = "supersede";
    public const string OpConflict = "conflict";
    public const string OpLink = "link";

    public static readonly IReadOnlyList<string> OpValues =
        [OpCreate, OpAppend, OpSupersede, OpConflict, OpLink];

    // Операции, которых нет у модели: правку формулировки, секцию «Заметки» и поля шапки
    // намеревается только человек (ADR-038). Пакет ai с такой операцией — ошибка формы.
    public const string OpEdit = "edit";
    public const string OpNotes = "notes";
    public const string OpHeader = "header";

    public static readonly IReadOnlyList<string> HumanOnlyOpValues = [OpEdit, OpNotes, OpHeader];

    // Что сделал пакет автора human — это же значение op строки журнала (actor: user).
    public const string ActionEdit = "edit";
    public const string ActionRestore = "restore";
    // «Принять как мою правку» (ADR-038, §6): дифф «манифест → диск», собранный в пакет human.
    public const string ActionAdopt = "adopt";

    /// <summary>Решение политики применения: `auto` — применяет код после проверок,
    /// `inbox` — ждёт решения человека («Входящие»). Ставит код (S4d), не модель.</summary>
    public const string DecisionAuto = "auto";
    public const string DecisionInbox = "inbox";

    public static readonly IReadOnlyList<string> DecisionValues = [DecisionAuto, DecisionInbox];

    // Статусы пакета (ADR-035, решение 4): checking → proposed | rejected_by_checks →
    // accepted | partially_accepted | rejected → applying → applied | apply_failed | stale;
    // applied → reverted — «Откатить пакет» человека (решение 12г, мера 4, S4b-3).
    public const string StatusChecking = "checking";
    public const string StatusProposed = "proposed";
    public const string StatusRejectedByChecks = "rejected_by_checks";
    public const string StatusAccepted = "accepted";
    public const string StatusPartiallyAccepted = "partially_accepted";
    public const string StatusRejected = "rejected";
    public const string StatusApplying = "applying";
    public const string StatusApplied = "applied";
    public const string StatusApplyFailed = "apply_failed";
    public const string StatusStale = "stale";
    public const string StatusReverted = "reverted";

    /// <summary>Решение человека по операции `inbox` (REST, S4a): ставится только поверх
    /// решения политики и только в статусе `proposed`.</summary>
    public const string HumanAccepted = "accepted";
    public const string HumanRejected = "rejected";

    /// <summary>Допустимые переходы статуса. poza таблицей переходов нет: «недопустимый
    /// переход — отказ» (S4a).
    /// checking → proposed | rejected_by_checks: вердикт проверок кода.
    /// proposed → accepted | partially_accepted | rejected: решение человека по спорным
    ///   операциям (S4a); → applying: неспорные применяет код сам (ADR-035, решение 5);
    ///   → stale: база ops изменилась, авто-rebase нет (решение 4).
    /// accepted/partially_accepted → applying | stale.
    /// applying → applied | apply_failed (откат по прообразам) | stale.
    /// applied → reverted: «Откатить пакет» — только кнопка человека из журнала (решение 12г,
    ///   мера 4, S4b-3); другой выход из применённого пакета не предусмотрен.
    /// Замкнутые: rejected_by_checks — отказ кодов человек не пересматривает (мера 1:
    ///   утверждение без дословной цитаты не применяется ни при какой политике);
    /// rejected, apply_failed, stale, reverted — следующим шагом здесь же быть не может:
    ///   откат и повторный разбор дают НОВЫЙ пакет (меры 4–5, решение 4 про stale);
    ///   из «reverted» переходов нет: повторный откат того же пакета — отказ по статусу.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions =
        new Dictionary<string, string[]>
        {
            [StatusChecking] = [StatusProposed, StatusRejectedByChecks],
            [StatusProposed] = [StatusAccepted, StatusPartiallyAccepted, StatusRejected, StatusApplying, StatusStale],
            [StatusAccepted] = [StatusApplying, StatusStale],
            [StatusPartiallyAccepted] = [StatusApplying, StatusStale],
            [StatusApplying] = [StatusApplied, StatusApplyFailed, StatusStale],
            [StatusApplied] = [StatusReverted],
        };

    public static readonly IReadOnlyList<string> StatusValues =
        [StatusChecking, StatusProposed, StatusRejectedByChecks, StatusAccepted, StatusPartiallyAccepted,
         StatusRejected, StatusApplying, StatusApplied, StatusApplyFailed, StatusStale, StatusReverted];

    /// <summary>Открытый пакет — тот, который ещё ждёт работы и занимает лимит 20 (в лимит
    /// входят и спорные во «Входящих» = proposed). Список открытых — НЕ ключи таблицы переходов:
    /// «applied» имеет переход (человеческая кнопка отката), но открытым не является — применённый
    /// пакет ночной разбор не ждёт и места в лимите не держит; открытые — ровно те, из которых
    /// работу продолжает код или человек по спорным операциям. Закрытые для кода — отказ кодов,
    /// отказ человека, применённый, неудачно применённый, устаревший, откатанный: ночной разбор
    /// с ними не работает; с применённым возможен только человеческий откат (мера 4).</summary>
    public static bool IsOpen(string status) =>
        status is StatusChecking or StatusProposed or StatusAccepted or StatusPartiallyAccepted or StatusApplying;

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    /// <summary>Класс правки — ключ `origin × op` (единица автовыключения класса, решение
    /// 12г, мера 7). Ставится кодом из источника операции и её op.</summary>
    public static string ClassKey(string origin, string op, bool humanTarget = false) =>
        origin + "×" + op + (humanTarget ? HumanTargetSuffix : "");

    /// <summary>Хвост ключа класса замены, чья цель — утверждение человека (ADR-038, решение 4):
    /// `session×supersede@human`. Замены человека считаются отдельным классом, чтобы их откаты
    /// не размывались в окне замен ИИ. Хвост бывает только у supersede/conflict.</summary>
    public const string HumanTargetSuffix = "@human";

    /// <summary>Разбор ключа класса — ЕДИНСТВЕННАЯ точка (её зовут и WikiSettingsStore.IsValidClassKey,
    /// и политика): два независимых разбора разошлись бы, и автовыключение класса `…@human`
    /// молча не сработало бы — ключ отвергался бы как кривой. null — ключ не класс.</summary>
    public static (string Origin, string Op, bool HumanTarget)? ParseClassKey(string? classKey)
    {
        if (classKey is null)
            return null;
        var parts = classKey.Split('×');
        if (parts.Length != 2)
            return null;
        var op = parts[1];
        var humanTarget = op.EndsWith(HumanTargetSuffix, StringComparison.Ordinal);
        if (humanTarget)
            op = op[..^HumanTargetSuffix.Length];
        if (!WikiGate.OriginValues.Contains(parts[0]) || !OpValues.Contains(op))
            return null;
        if (humanTarget && op is not (OpSupersede or OpConflict))
            return null;
        return (parts[0], op, humanTarget);
    }

    /// <summary>Операция ИИ, применённая САМА (decision=auto) поверх утверждения человека: замена
    /// (`supersede`) или оспаривание (`conflict`) с классом `…@human`. Принятое человеком из
    /// «Входящих» сюда не входит: там решил человек, ИИ ничего не «переопределял». Одно правило для
    /// признака журнала `overrodeHuman` и для строк утреннего уведомления (ADR-038, предохранители 4 и 6).</summary>
    public static bool IsAutoOverrideOfHuman(Operation op) =>
        op.Decision == DecisionAuto && ParseClassKey(op.Class) is { HumanTarget: true };

    /// <summary>Сколько формулировок человека заменил ИИ сам (`supersede@human`) и на каких страницах
    /// он сам оспорил утверждение человека (`conflict@human`): пакет → вклад в сводку ночи. Противоречие
    /// между утверждениями ИИ сюда не входит (ADR-038, предохранители 2 и 6 — про формулировки человека).
    /// Страницы — различные, в порядке операций.</summary>
    public (int Replaced, IReadOnlyList<string> DisputedPages) NightSummary()
    {
        var replaced = Operations.Count(o => o.Op == OpSupersede && IsAutoOverrideOfHuman(o));
        var pages = Operations
            .Where(o => o.Op == OpConflict && IsAutoOverrideOfHuman(o))
            .Select(o => o.Page)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return (replaced, pages);
    }

    // Автор пакета (ADR-038): `ai` — пакет модели (ingest, ночной разбор, MCP), `human` — правка
    // владельца через REST под пользовательским JWT. Карточку origin: human и секцию «Заметки»
    // создаёт/меняет только `human` (WikiGate). Пакет без поля в JSON читается как `ai`.
    public const string AuthorAi = "ai";
    public const string AuthorHuman = "human";

    public static readonly IReadOnlyList<string> AuthorValues = [AuthorAi, AuthorHuman];

    public int V { get; init; } = ModelVersion;
    public required string Id { get; init; }
    /// <summary>Автор пакета; по умолчанию `ai` — обратная совместимость со старыми файлами.</summary>
    public string Author { get; init; } = AuthorAi;
    /// <summary>Что сделал пакет human: `edit` | `restore` («Вернуть мою»); у пакета ai null.
    /// Ставит код (REST), в журнал идёт как op.</summary>
    public string? Action { get; init; }
    public required string Section { get; init; }
    public required IReadOnlyList<SourceRef> Sources { get; init; }
    public required IReadOnlyList<Operation> Operations { get; init; }
    public required string Status { get; init; }
    /// <summary>Причина отказа кодов/отказа применения/устаревания — текст для человека.</summary>
    public string? StatusReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Источник(ы) пакета: origin — из закрытого перечня (WikiGate.OriginValues),
    /// ref — карточка источника («{id}», «{sha256}», путь заметки). Пакет без источника
    /// неоправдан: откат по источнику (мера 5) не за чем делать.</summary>
    public sealed record SourceRef(string Origin, string Ref);

    /// <summary>Намерение изменить страницу. Файл по нему рендерит код при применении.</summary>
    public sealed record Operation
    {
        public required string Op { get; init; }
        /// <summary>Страница раздела, к которой идёт намерение (путь относительно pages/).</summary>
        public required string Page { get; init; }
        /// <summary>Секция страницы, в которой идёт правка человека (append, edit, supersede):
        /// у пакета ai null — модель пишет «Сводку»/«Противоречия» по виду операции.</summary>
        public string? PageSection { get; init; }
        /// <summary>Что замещается/оспаривается (supersede, conflict) или цель ссылки (link).
        /// supersede называет заменяемое утверждение: без него нет проверки на stale.</summary>
        public string? Target { get; init; }
        /// <summary>Содержание намерения: текст утверждения (append/supersede), начальное
        /// содержание страницы (create).</summary>
        public string? Text { get; init; }
        /// <summary>Утверждение операции отдельно от доказательства (ADR-039, решение 2):
        /// что именно утверждает пакет. Обязателен, когда есть `Quotes`; старые пакеты
        /// (без цитат) его не несут.</summary>
        public string? Claim { get; init; }
        /// <summary>Цитаты-доказательства: 1–3 элемента, каждый 8–300 символов после Trim
        /// (границы — WikiPage.MinQuoteLength/MaxQuoteLength). Порядок сохраняется: номера
        /// сносок берутся по нему (S2-7). null — старый пакет, где доказательство в `Text`
        /// (WikiIngestService.VerifiedQuote).</summary>
        public IReadOnlyList<string>? Quotes { get; init; }
        /// <summary>Вид создаваемой страницы: при применении становится полем шапки `type`
        /// (ADR-039 §3), а НЕ полем шапки `kind` (WikiFrontmatter.Kind — другое поле).
        /// Ключ JSON — `kind`: его уже ждёт промпт Synth. Проверка по списку видов — S2-6.</summary>
        public string? Kind { get; init; }
        /// <summary>Дата «на когда» для вида `state` (`ГГГГ-ММ-ДД` или `unknown`):
        /// при применении уходит в шапку `as_of`. Проверка формата — S2-6.</summary>
        public string? AsOf { get; init; }
        /// <summary>Класс `origin × op` — ставит код.</summary>
        public required string Class { get; init; }
        /// <summary>Решение политики — ставит код (S4d).</summary>
        public required string Decision { get; init; }
        /// <summary>Почему операция спорная (обязательна для `inbox`: «Входящие» приходят
        /// к человеку с причиной, а не без объяснения).</summary>
        public string? Reason { get; init; }
        /// <summary>Хэши базового состояния страниц, на которые опирается намерение
        /// (та же форма, что в манифесте — WikiManifest.Sha256Hex). Пусто для create.</summary>
        public IReadOnlyDictionary<string, string> BaseHashes { get; init; } = new Dictionary<string, string>();
        /// <summary>Токены разбора, который дал эту операцию (счёт токенов ночи, решение 12в).</summary>
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }
        /// <summary>Решение человека по спорной операции: accepted | rejected | null
        /// (не решено). Только для `inbox` и только в `proposed`.</summary>
        public string? HumanDecision { get; init; }
        public string? HumanReason { get; init; }
    }

    /// <summary>Безопасный id: GUID. Имя файла пакета строится из id без обхода пути,
    /// поэтому id не может нести разделителей пути.</summary>
    public static bool IsSafeId(string id) => Guid.TryParse(id, out _);

    /// <summary>Проверка формы пакета — всё, что обязан обеспечить код до записи и то, что
    /// делает прочитанный файл пакетом, а не мусором. Пустой список — форма верна.
    /// Отказ по первой же ошибке — у вызывающего.</summary>
    public static IReadOnlyList<string> ShapeErrors(WikiPackage p)
    {
        var errors = new List<string>();
        if (p.V != ModelVersion)
            errors.Add($"версия пакета {p.V}, ожидаемая {ModelVersion}");
        if (!IsSafeId(p.Id))
            errors.Add("id пакета не GUID");
        if (p.Author is null || !AuthorValues.Contains(p.Author))
            errors.Add($"автор пакета «{p.Author ?? "null"}» не из перечня {string.Join(", ", AuthorValues)}");
        var isHuman = p.Author == AuthorHuman;
        if (isHuman && p.Action is not (ActionEdit or ActionRestore or ActionAdopt))
            errors.Add($"у пакета human действие «{p.Action ?? "null"}» не из перечня {ActionEdit}, {ActionRestore}, {ActionAdopt}");
        if (!isHuman && p.Action is not null)
            errors.Add("действие (edit/restore) бывает только у пакета human");
        // Null в поле валидного JSON (восстановленный бэкап, ручная правка, частично
        // уехавший файл) — ошибка формы, а не NRE: проверка до разбора (ревью S4a, MAJOR 2),
        // иначе один такой файл роняет «Входящие» целиком.
        if (p.Section is null)
            errors.Add("раздел пакета null");
        else
        {
            var write = WikiSections.IsWritable(p.Section);
            if (!write.Writable)
                errors.Add($"раздел «{p.Section}»: {write.Reason}");
        }
        if (p.Sources is null)
        {
            errors.Add("источники пакета null");
        }
        else
        {
            if (p.Sources.Count == 0)
                errors.Add("пакет без источника");
            foreach (var s in p.Sources)
            {
                if (s is null)
                {
                    errors.Add("null в списке источников");
                    continue;
                }
                if (!WikiGate.OriginValues.Contains(s.Origin))
                    errors.Add($"origin «{s.Origin}» не из перечня");
                // Ссылка на существующую карточку human допустима и пакету ai (ADR-038, §2);
                // создание такой карточки отбивает гейт сравнением с состоянием раздела.
                if (string.IsNullOrWhiteSpace(s.Ref))
                    errors.Add("источник без карточки");
            }
        }
        // Пустой пакет — не пакет: он ничего не намеревается и занимает место в лимите.
        if (p.Operations is null)
        {
            errors.Add("операции пакета null");
        }
        else
        {
            if (p.Operations.Count == 0)
                errors.Add("пакет без операций");
            foreach (var op in p.Operations)
            {
                if (op is null)
                {
                    errors.Add("null в списке операций");
                    continue;
                }
                var knownOp = OpValues.Contains(op.Op) || (isHuman && HumanOnlyOpValues.Contains(op.Op));
                if (!knownOp)
                    errors.Add($"операция «{op.Op}» не из перечня");
                // Пакет — в пределах одного раздела: страница обязана принадлежать его разделу.
                if (op.Page is null)
                    errors.Add("операция без страницы");
                else if (WikiSections.Parse(op.Page) != p.Section)
                    errors.Add($"страница «{op.Page}» не раздела «{p.Section}»");
                if (!DecisionValues.Contains(op.Decision))
                    errors.Add($"решение политики «{op.Decision}» не из перечня");
                if (op.Decision == DecisionInbox && string.IsNullOrWhiteSpace(op.Reason))
                    errors.Add($"операция «{op.Op}» во «Входящих» без причины");
                // Класс ставится из источника пакета и op: своя строка класса невозможна.
                // Для нераспознанной op про класс молчим — про неё уже сказано выше.
                if (!knownOp)
                    continue;
                if (p.Sources is not null && !p.Sources.Any(s => s is not null
                    && (op.Class == ClassKey(s.Origin, op.Op)
                        || (op.Class == ClassKey(s.Origin, op.Op, humanTarget: true)))))
                    errors.Add($"класс «{op.Class}» не из источников пакета и не по op «{op.Op}»");
                // supersede человека — «снято человеком»: утверждение убирают в «Историю», нового текста нет.
                var textRequired = op.Op is OpCreate or OpAppend or OpEdit || (op.Op == OpSupersede && !isHuman);
                if (textRequired && string.IsNullOrWhiteSpace(op.Text))
                    errors.Add($"операция «{op.Op}» без содержания");
                if (op.Op is OpSupersede or OpConflict or OpLink or OpEdit or OpHeader && string.IsNullOrWhiteSpace(op.Target))
                    errors.Add($"операция «{op.Op}» без цели (замещаемого утверждения, ссылки или поля)");
                // Утверждение отдельно от доказательства (ADR-039, решение 2). Пока цитат нет
                // — старый пакет, форма прежняя; с цитатами claim обязателен и ограничен.
                if (op.Quotes is not null)
                {
                    if (op.Quotes.Count is < 1 or > 3)
                        errors.Add($"цитат операции «{op.Op}»: {op.Quotes.Count}, допустимо 1–3");
                    foreach (var q in op.Quotes)
                    {
                        if (q is null)
                        {
                            errors.Add("null в списке цитат");
                            continue;
                        }
                        var len = q.Trim().Length;
                        if (len is < WikiPage.MinQuoteLength or > WikiPage.MaxQuoteLength)
                            errors.Add($"цитата операции «{op.Op}» длиной {len}, допустимо {WikiPage.MinQuoteLength}–{WikiPage.MaxQuoteLength}");
                    }
                    if (string.IsNullOrWhiteSpace(op.Claim))
                        errors.Add($"операция «{op.Op}» с цитатами без утверждения (claim)");
                    else if (op.Claim.Length > MaxClaimLength)
                        errors.Add($"утверждение операции «{op.Op}» длиной {op.Claim.Length}, допустимо не более {MaxClaimLength}");
                }
                if (op.BaseHashes is null)
                {
                    errors.Add("baseHashes операции null");
                }
                else
                {
                    foreach (var (page, hash) in op.BaseHashes)
                    {
                        if (WikiSections.Parse(page) != p.Section)
                            errors.Add($"база «{page}» не раздела «{p.Section}»");
                        if (hash is null || !IsSha256Hex(hash))
                            errors.Add($"база «{page}»: хэш не sha256 hex");
                    }
                }
                if (op.InputTokens < 0 || op.OutputTokens < 0)
                    errors.Add("отрицательный счёт токенов");
                if (op.HumanDecision is not null && op.HumanDecision is not (HumanAccepted or HumanRejected))
                    errors.Add($"решение человека «{op.HumanDecision}» не из перечня");
                if (op.HumanDecision == HumanRejected && string.IsNullOrWhiteSpace(op.HumanReason))
                    errors.Add("отказ человека без причины");
            }
        }
        if (!StatusValues.Contains(p.Status))
            errors.Add($"статус «{p.Status}» не из перечня");
        return errors;
    }

    /// <summary>Форма хэша та же, что в манифесте: 64 hex в нижнем регистре — иначе сверка
    /// baseHashes с манифестом никогда не сойдётся.</summary>
    public static bool IsSha256Hex(string hash) =>
        hash.Length == 64 && hash.All(Uri.IsHexDigit);
}
