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

    // Операций пять; удаления нет (ADR-035, решение 4): снятие знания — откат по пакету
    // или по источнику (решение 12г, меры 4–5), а не операция пакета.
    public const string OpCreate = "create";
    public const string OpAppend = "append";
    public const string OpSupersede = "supersede";
    public const string OpConflict = "conflict";
    public const string OpLink = "link";

    public static readonly IReadOnlyList<string> OpValues =
        [OpCreate, OpAppend, OpSupersede, OpConflict, OpLink];

    /// <summary>Решение политики применения: `auto` — применяет код после проверок,
    /// `inbox` — ждёт решения человека («Входящие»). Ставит код (S4d), не модель.</summary>
    public const string DecisionAuto = "auto";
    public const string DecisionInbox = "inbox";

    public static readonly IReadOnlyList<string> DecisionValues = [DecisionAuto, DecisionInbox];

    // Статусы пакета (ADR-035, решение 4): checking → proposed | rejected_by_checks →
    // accepted | partially_accepted | rejected → applying → applied | apply_failed | stale.
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
    /// Замкнутые: rejected_by_checks — отказ кодов человек не пересматривает (мера 1:
    ///   утверждение без дословной цитаты не применяется ни при какой политике);
    /// rejected, applied, apply_failed, stale — следующим шагом здесь же быть не может:
    ///   откат и повторный разбор дают НОВЫЙ пакет (меры 4–5, решение 4 про stale).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Transitions =
        new Dictionary<string, string[]>
        {
            [StatusChecking] = [StatusProposed, StatusRejectedByChecks],
            [StatusProposed] = [StatusAccepted, StatusPartiallyAccepted, StatusRejected, StatusApplying, StatusStale],
            [StatusAccepted] = [StatusApplying, StatusStale],
            [StatusPartiallyAccepted] = [StatusApplying, StatusStale],
            [StatusApplying] = [StatusApplied, StatusApplyFailed, StatusStale],
        };

    public static readonly IReadOnlyList<string> StatusValues =
        [StatusChecking, StatusProposed, StatusRejectedByChecks, StatusAccepted, StatusPartiallyAccepted,
         StatusRejected, StatusApplying, StatusApplied, StatusApplyFailed, StatusStale];

    /// <summary>Открытый пакет — тот, из которого возможен следующий переход: он ещё ждёт
    /// работы и занимает лимит 20 (в лимит входят и спорные во «Входящих» = proposed).
    /// Замкнутый — отказ кодов, отказ человека, применённый, неудачно применённый,
    /// устаревший: с самим этим пакетом дальше ничего не делается.</summary>
    public static bool IsOpen(string status) => Transitions.ContainsKey(status);

    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var next) && Array.IndexOf(next, to) >= 0;

    /// <summary>Класс правки — ключ `origin × op` (единица автовыключения класса, решение
    /// 12г, мера 7). Ставится кодом из источника операции и её op.</summary>
    public static string ClassKey(string origin, string op) => origin + "×" + op;

    public int V { get; init; } = ModelVersion;
    public required string Id { get; init; }
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
        /// <summary>Что замещается/оспаривается (supersede, conflict) или цель ссылки (link).
        /// supersede называет заменяемое утверждение: без него нет проверки на stale.</summary>
        public string? Target { get; init; }
        /// <summary>Содержание намерения: текст утверждения (append/supersede), начальное
        /// содержание страницы (create).</summary>
        public string? Text { get; init; }
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
                if (!OpValues.Contains(op.Op))
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
                if (!OpValues.Contains(op.Op))
                    continue;
                if (p.Sources is not null && !p.Sources.Any(s => s is not null && op.Class == ClassKey(s.Origin, op.Op)))
                    errors.Add($"класс «{op.Class}» не из источников пакета и не по op «{op.Op}»");
                switch (op.Op)
                {
                    case OpCreate or OpAppend or OpSupersede when string.IsNullOrWhiteSpace(op.Text):
                        errors.Add($"операция «{op.Op}» без содержания");
                        break;
                    case OpSupersede or OpConflict or OpLink when string.IsNullOrWhiteSpace(op.Target):
                        errors.Add($"операция «{op.Op}» без цели (замещаемого утверждения или ссылки)");
                        break;
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
