namespace ClaudeHomeServer.Services.Notes.Wiki;

// Политика применения (ADR-035, решение 12г): чистая функция (операция, контекст) →
// «само» | «во Входящие» + причина. Ни I/O, ни состояния: всё, что политика знает о мире
// (выключатели, статус целевой страницы, находка дедупа, счётчики ночи), ей даёт вызывающий.
//
// Порядок проверок фиксирован: общий выключатель → класс → риски в порядке строк таблицы
// §12г. Причина называет ПЕРВУЮ сработавшую проверку: человек видит главный риск, а не
// список всех. Причина есть и у «само» — короткое основание, почему без ревью (мера 6:
// журнал показывает политику каждой правки).
//
// Fail-closed: нераспознанная операция, пустой или кривой класс, операция без страницы —
// «inbox», не «auto»: молча применить то, что не разобрал, — ровно тот риск, ради которого
// политика и стоит.
public static class WikiApplyPolicy
{
    // Лимиты меры 3 (§12г). Счётчик считает ТЕКУЩИЙ элемент вместе с уже сделанными за
    // ночь: «пятая страница» = 5 — проходит, «шестая» = 6 — ждёт.
    public const int MaxNewKnowledgePagesPerSection = 5;
    public const int MaxNewKnowledgePagesPerSectionBacklog = 15;
    public const int MaxStatementsPerPackage = 30;
    public const int MaxPackagesPerPagePerNight = 1;

    /// Анти-петля (N7d): страницу, откатанную человеком (revert пакета или retract источника)
    /// менее стольких дней назад, автоприменение не меняет — пакет идёт во «Входящие».
    public const int RevertQuarantineDays = 7;

    // Машинный код причины «Входящих» (решение 12.11): допуск к облачному судье решает код,
    // а не русский текст Reason (он остаётся для человека). Код ставит код в каждой ветке
    // политики; старые пакеты без кода читаются как null — к судье не допускаются (И4′).
    public const string CodeAutoApplyOff = "autoapply_off";
    public const string CodeUnknownOp = "unknown_op";
    public const string CodeNoClass = "no_class";
    public const string CodeBadClass = "bad_class";
    public const string CodeNoPage = "no_page";
    public const string CodeClassDisabled = "class_disabled";
    public const string CodeRevertQuarantine = "revert_quarantine";
    public const string CodeNoHistorySection = "no_history_section";
    public const string CodeOverridesHuman = "overrides_human";
    public const string CodePageDuplicate = "page_duplicate";
    public const string CodeSectionNewPagesLimit = "section_new_pages_limit";
    public const string CodeOriginUrl = "origin_url";
    public const string CodePersonalToCommon = "personal_to_common";
    public const string CodeTargetDisputed = "target_disputed";
    public const string CodeAfterModelRepair = "after_model_repair";
    /// <summary>Код валидатора (число утверждения не подтверждено цитатой), не политики:
    /// числа проверяются в WikiPlanValidator после вердикта.</summary>
    public const string CodeNumberUnconfirmed = "number_unconfirmed";
    /// <summary>Предложение слить две страницы (op merge, ADR-039 решение 13): код ставит
    /// создающий merge-пакет (C6), не Decide — политика merge не порождает.</summary>
    public const string CodeMergeProposal = "merge_proposal";

    /// <summary>Полный перечень кодов причины: форма пакета проверяется по нему.</summary>
    public static readonly IReadOnlyList<string> ReasonCodes =
        [CodeAutoApplyOff, CodeUnknownOp, CodeNoClass, CodeBadClass, CodeNoPage, CodeClassDisabled,
         CodeRevertQuarantine, CodeNoHistorySection, CodeOverridesHuman, CodePageDuplicate,
         CodeSectionNewPagesLimit, CodeOriginUrl, CodePersonalToCommon, CodeTargetDisputed,
         CodeAfterModelRepair, CodeNumberUnconfirmed, CodeMergeProposal];

    /// <summary>Допуск к облачному судье (решение Андрея 12.11): ровно «только для человека»
    /// случаи — выключенный класс, откатанная страница, замена без секции «История», замена
    /// утверждения человека, возможный дубль, сверх лимита новых страниц, личное в «Общем»,
    /// спорная страница, пакет после ремонта, неподтверждённое число, предложение слить
    /// страницы (merge — решает судья: apply/reject, «переписать» нет). autoapply_off (ручной
    /// режим судью не зовёт вовсе) и отсутствие кода (старые пакеты) — не допущены (И4′).</summary>
    public static bool JudgeAdmitted(string? code) => code is CodeClassDisabled or CodeRevertQuarantine
        or CodeNoHistorySection or CodeOverridesHuman or CodePageDuplicate or CodeSectionNewPagesLimit
        or CodePersonalToCommon or CodeTargetDisputed or CodeAfterModelRepair or CodeNumberUnconfirmed
        or CodeMergeProposal;

    /// <summary>Отказ по коду: внешний текст и fail-closed ветки — судье там не о чём решать
    /// (И4′: операция с этими кодами к судье не попадает).</summary>
    public static bool CodeRejected(string? code) => code is CodeUnknownOp or CodeNoClass
        or CodeBadClass or CodeNoPage or CodeOriginUrl;

    /// Вердикт политики: Decision — WikiPackage.DecisionAuto | DecisionInbox; Reason —
    /// текст для человека (для «inbox» обязателен: «Входящие» приходят с объяснением);
    /// ReasonCode — машинный код причины (решение 12.11): допуск к судье решает код, не
    /// текст. У «auto» — null.
    public readonly record struct Verdict(string Decision, string Reason, string? ReasonCode)
    {
        public bool IsAuto => Decision == WikiPackage.DecisionAuto;
    }

    /// Всё, что политика знает о мире вокруг операции. Значения собирает вызывающий
    /// (Хранитель, ночной разбор): политика их не вычисляет и ни о чём не молчит —
    /// отсутствие признака здесь значит «признака нет», а не «не проверено».
    public sealed record ApplyContext
    {
        /// Общий выключатель «Применять без ревью» (мера 7). Умолчание — включён;
        /// выключен — все операции идут во «Входящие», классы не смотрятся.
        public bool AutoApplyEnabled { get; init; } = true;

        /// Классы origin × op, выключенные автоматикой или человеком (мера 7).
        public IReadOnlySet<string> DisabledClasses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// Пакет, которому понадобился ремонт модели: признак низкого качества прохода.
        public bool PackageAfterModelRepair { get; init; }

        /// Дом источника — «личное» (ADR-036, решение 3): в «Общее» такое знание идёт
        /// только решением человека.
        public bool SourceHomeIsPersonal { get; init; }

        /// Статус целевой страницы по текущему состоянию (WikiFrontmatter.Status*).
        /// null — страницы ещё нет (create) или вызывающий её не читал.
        public string? TargetPageStatus { get; init; }

        /// Возможный дубль создаваемой страницы (WikiDedup.FindPageDuplicate).
        /// null — похожей страницы в разделе и вверх по цепочке нет.
        public WikiDedup.Duplicate? PageDuplicate { get; init; }

        /// Создаваемая страница — хроника чата (type: chronicle, папка «Хроники/»).
        /// Дедуп и лимит новых страниц её не касают: §12г ограничивает «страницы знаний»,
        /// а хроника — одна страница на чат, её заголовок несёт имя чата и дату.
        public bool TargetIsChronicle { get; init; }

        /// Счётчики ночи (мера 3), включая текущий элемент.
        public NightCounters Counters { get; init; }

        /// Проход бэклога: лимит новых страниц на раздел — 15 вместо 5 (решение 12в).
        public bool IsBacklogPass { get; init; }

        /// Страницы, которые человек откатил менее RevertQuarantineDays назад: путь →
        /// дата отката. Собирает вызывающий из журнала (op revert|retract, actor user);
        /// пусто — откатов нет. Гейтит только автопуть: применение человеком из «Входящих»
        /// политику не спрашивает.
        public IReadOnlyDictionary<string, DateTimeOffset> RecentlyRevertedPages { get; init; }
            = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        /// <summary>Время написания источника НОВОГО утверждения (поле written его карточки,
        /// ADR-038, решение 4). null — неизвестно: правило замены (B4c) уводит такую замену
        /// во «Входящие». Заполняет вызывающий; в B4a поле только несётся.</summary>
        public DateTimeOffset? NewSourceTime { get; init; }

        /// <summary>Последняя правка человека по странице: путь → время (журнал: actor user,
        /// op edit|restore; WikiJournalReader.ReadLastHumanEdits). Отдельный механизм от отката
        /// (RecentlyRevertedPages, N7d): откат гейтит все операции на 7 дней, правка — только
        /// замены и без срока. Пусто — правок человека нет или журнал нечитаем.</summary>
        public IReadOnlyDictionary<string, DateTimeOffset> LastHumanEditAt { get; init; }
            = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        /// <summary>Самое новое время среди источников утверждения-цели замены: written карточки,
        /// у карточки human — captured. null — неизвестно у хотя бы одного источника или сносок
        /// у цели нет (fail-closed: замена без времени цели не применяется сама). Определяет
        /// код (валидатор) по сноскам страницы СЕЙЧАС, не модель.</summary>
        public DateTimeOffset? TargetSourceTime { get; init; }

        /// <summary>Автор цели supersede/conflict по сноскам утверждения: WikiPackage.AuthorHuman |
        /// AuthorAi; null — операция не замена. Определяет код (валидатор), не модель.</summary>
        public string? TargetAuthor { get; init; }

        /// <summary>У целевой страницы нет секции «История» (хроника): прежняя формулировка при
        /// замене не сохранится. Определяет код (валидатор) по разобранной странице. Для цели
        /// human такая замена не применяется сама (ADR-038): формулировка человека не пропадает молча.</summary>
        public bool TargetHasNoHistory { get; init; }
    }

    /// Счётчики ночи для проверки лимитов (мера 3). Каждый считает текущий элемент вместе
    /// с уже сделанными: 6 новых страниц в разделе = «шестая страница», 31 утверждения =
    /// «тридцать первое утверждение», 2 пакета на страницу = второй пакет этой страницы.
    public readonly record struct NightCounters(
        int NewKnowledgePagesInSection,
        int StatementsInPackage,
        int PackagesForPage);

    /// Решение по лимитам: «ждёт следующей ночи», а не «Входящие» (мера 3: превышение —
    /// остаток ждёт, а не сыплется человеку). Это ОТДЕЛЬНЫЙ порог вызывающего: то, что не
    /// вошло в лимит, пакета не получает и человека не спрашивает. Строка таблицы §12г
    /// «сверх лимита» при этом остаётся: если такая операция всё же оказалась в пакете,
    /// Decide не применяет её молча, а отдаёт на ревью.
    public readonly record struct LimitVerdict(bool Within, string? WaitingReason)
    {
        public bool Waits => !Within;
    }

    /// Решение политики по одной операции. Таблица §12г — источник правды; порядок
    /// проверок выше по файлу и есть порядок причины.
    public static Verdict Decide(WikiPackage.Operation op, ApplyContext context)
    {
        // 1. Общий выключатель (мера 7).
        if (context is null || !context.AutoApplyEnabled)
            return Inbox(CodeAutoApplyOff, "выключатель «Применять без ревью» выключен — каждая правка идёт через «Входящие»");

        // 2. Класс. Нераспознанная операция и кривой класс — не класс: fail-closed.
        if (op is null || op.Op is null || !WikiPackage.OpValues.Contains(op.Op))
            return Inbox(CodeUnknownOp, $"операция «{op?.Op ?? "—"}» вне перечня — политике не о чём выносить решение");
        if (string.IsNullOrWhiteSpace(op.Class))
            return Inbox(CodeNoClass, "операция без класса origin × op — политику не спросить: класс не назван");
        var origin = OriginOf(op.Class, op.Op);
        if (origin is null)
            return Inbox(CodeBadClass, $"класс «{op.Class}» не пары «источник × {op.Op}» — класс правки не опознан");
        if (op.Page is null || op.Page.Length == 0)
            return Inbox(CodeNoPage, "операция без страницы — политику не спросить: цель не названа");
        if (context.DisabledClasses.Contains(op.Class))
            return Inbox(CodeClassDisabled, $"класс правки «{op.Class}» выключен — его правки идут через ревью человека");

        // Анти-петля (N7d): откат человека сильнее любой автоматики — включая create по тому же
        // пути (revert пакета с create удаляет страницу, и тот же источник создал бы её снова).
        if (context.RecentlyRevertedPages.TryGetValue(op.Page, out var revertedAt))
            return Inbox(CodeRevertQuarantine, $"страницу откатили вручную {revertedAt:yyyy-MM-dd}: автоприменение не меняет её {RevertQuarantineDays} дней");

        // 3. Риски — по строкам таблицы §12г.
        // Замена и оспаривание (ADR-038, решение 4): само — только если новый источник новее и
        // заменяемого утверждения, и последней правки человека на странице. Правило одно для
        // целей ai и human: ветки по автору цели нет (автор влияет на класс, не на условия).
        if (op.Op == WikiPackage.OpSupersede
            && context.TargetAuthor == WikiPackage.AuthorHuman && context.TargetHasNoHistory)
            return Inbox(CodeNoHistorySection, "На этой странице нет секции «История», прежняя формулировка не сохранится — решите сами");
        if (op.Op is WikiPackage.OpSupersede or WikiPackage.OpConflict
            && ReplacementBlocker(op, context) is { } blocker)
            return Inbox(CodeOverridesHuman, blocker);
        if (op.Op == WikiPackage.OpCreate && !context.TargetIsChronicle)
        {
            if (context.PageDuplicate is { } dup)
                return Inbox(CodePageDuplicate, $"возможный дубль страницы: {dup.Field} совпадает с {dup.ExistingField} страницы «{dup.Path}»");
            var limit = context.IsBacklogPass ? MaxNewKnowledgePagesPerSectionBacklog : MaxNewKnowledgePagesPerSection;
            if (context.Counters.NewKnowledgePagesInSection > limit)
                return Inbox(CodeSectionNewPagesLimit, $"сверх лимита новых страниц раздела за ночь: {context.Counters.NewKnowledgePagesInSection} из {limit}");
        }
        if (origin == WikiGate.OriginUrl)
            return Inbox(CodeOriginUrl, "внешний текст (origin url): промпт-инъекция извне");
        if (WikiSections.Parse(op.Page) == WikiSections.Common && context.SourceHomeIsPersonal)
            return Inbox(CodePersonalToCommon, "личное в «Общем»: решение о видимости принимает человек (ADR-036, решение 3)");
        if (context.TargetPageStatus is WikiFrontmatter.StatusDisputed or WikiFrontmatter.StatusDrift)
            return Inbox(CodeTargetDisputed, $"целевая страница в статусе «{context.TargetPageStatus}»: там уже нужен человек");
        if (context.PackageAfterModelRepair)
            return Inbox(CodeAfterModelRepair, "пакет после ремонта модели: признак низкого качества прохода");

        // 4. Само — «Что применяется само» (§12г), после проверок кода.
        return Auto(op, context);
    }

    /// Проверка лимитов ночи (мера 3) по счётчикам. Zero-счётчики (ничего не сделано,
    /// текущий элемент единственный) — внутри лимитов. Первый превысивший лимит —
    /// причина ожидания; дальше не смотрим: остаток всё равно ждёт.
    public static LimitVerdict CheckLimits(NightCounters counters, bool isBacklogPass)
    {
        var pageLimit = isBacklogPass ? MaxNewKnowledgePagesPerSectionBacklog : MaxNewKnowledgePagesPerSection;
        if (counters.NewKnowledgePagesInSection > pageLimit)
            return new(false, $"новых страниц знаний в разделе за ночь: {counters.NewKnowledgePagesInSection} из {pageLimit} — остаток ждёт следующей ночи");
        if (counters.StatementsInPackage > MaxStatementsPerPackage)
            return new(false, $"утверждений в пакете: {counters.StatementsInPackage} из {MaxStatementsPerPackage} — остаток ждёт следующей ночи");
        if (counters.PackagesForPage > MaxPackagesPerPagePerNight)
            return new(false, $"пакетов на страницу за ночь: {counters.PackagesForPage} из {MaxPackagesPerPagePerNight} — остаток ждёт следующей ночи");
        return new(true, null);
    }

    // Правило замены, условия 1, 4, 2–3 (условия 5–7 — карантин N7d, класс и общие риски — стоят
    // в Decide до и после). Первое невыполненное — причина. Строго «новее»: равные времена —
    // не новее (half-open: источник того же момента не замещает).
    private static string? ReplacementBlocker(WikiPackage.Operation op, ApplyContext context)
    {
        if (context.NewSourceTime is not { } source)
            return "Неизвестно, когда написан источник: замена без времени источника не применяется сама";
        if (context.LastHumanEditAt.TryGetValue(op.Page, out var editedAt) && source <= editedAt)
            return $"Источник старше вашей правки ({editedAt:yyyy-MM-dd}): замена и оспаривание после неё идут через ревью";
        if (context.TargetSourceTime is not { } target)
            return "Неизвестно, когда написан источник утверждения: без этого не понять, какое из двух новее";
        if (source <= target)
            return "Источник не новее заменяемого утверждения: старый источник не стирает более свежее знание";
        return null;
    }

    // internal: ветка «вне известных» тестируется напрямую — Decide отсекает неизвестные
    // операции раньше, и она работает только для операции, добавленной в OpValues позже
    // (promote/graduate): молча «auto» ей быть нельзя (хвост ревью S4d).
    internal static Verdict Auto(WikiPackage.Operation op, ApplyContext context) => op.Op switch
    {
        WikiPackage.OpCreate when context.TargetIsChronicle =>
            Auto("хроника чата: механическая операция разбора (§12г)"),
        WikiPackage.OpCreate =>
            Auto("новая страница знаний: похожей нет, лимит новых страниц не исчерпан (§12г)"),
        WikiPackage.OpAppend =>
            Auto("утверждение в «Сводку»/«Хронологию»: применяется само (§12г)"),
        WikiPackage.OpLink =>
            Auto("связь между страницами: применяется само (§12г)"),
        WikiPackage.OpSupersede =>
            Auto("замещение: источник новее заменяемого утверждения и вашей правки (ADR-038)"),
        WikiPackage.OpConflict =>
            Auto("оспаривание: источник новее оспариваемого утверждения и вашей правки (ADR-038)"),
        _ =>
            Inbox(CodeUnknownOp, $"операция «{op.Op ?? "—"}» вне известных автоприменению: её риска политика не знает"),
    };

    private static Verdict Auto(string reason) => new(WikiPackage.DecisionAuto, reason, null);

    // Класс правки — ключ «origin × op» (WikiPackage.ClassKey). Origin берётся из него же:
    // второй источник origin у операции нет. Кривой ключ (не две части, origin вне перечня,
    // op не совпадает с операцией) — null: вызывающий решает это как fail-closed. Разбор —
    // WikiPackage.ParseClassKey, он же понимает хвост «@human» (цель — утверждение человека).
    private static string? OriginOf(string classKey, string op) =>
        WikiPackage.ParseClassKey(classKey) is { } parsed && parsed.Op == op ? parsed.Origin : null;

    private static Verdict Inbox(string code, string reason) => new(WikiPackage.DecisionInbox, reason, code);
}
