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

    /// Вердикт политики: Decision — WikiPackage.DecisionAuto | DecisionInbox; Reason —
    /// текст для человека (для «inbox» обязателен: «Входящие» приходят с объяснением).
    public readonly record struct Verdict(string Decision, string Reason)
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
            return Inbox("выключатель «Применять без ревью» выключен — каждая правка идёт через «Входящие»");

        // 2. Класс. Нераспознанная операция и кривой класс — не класс: fail-closed.
        if (op is null || op.Op is null || !WikiPackage.OpValues.Contains(op.Op))
            return Inbox($"операция «{op?.Op ?? "—"}» вне перечня — политике не о чём выносить решение");
        if (string.IsNullOrWhiteSpace(op.Class))
            return Inbox("операция без класса origin × op — политику не спросить: класс не назван");
        var origin = OriginOf(op.Class, op.Op);
        if (origin is null)
            return Inbox($"класс «{op.Class}» не пары «источник × {op.Op}» — класс правки не опознан");
        if (op.Page is null || op.Page.Length == 0)
            return Inbox("операция без страницы — политику не спросить: цель не названа");
        if (context.DisabledClasses.Contains(op.Class))
            return Inbox($"класс правки «{op.Class}» выключен — его правки идут через ревью человека");

        // 3. Риски — по строкам таблицы §12г.
        if (op.Op == WikiPackage.OpSupersede)
            return Inbox("замещение действующего утверждения: меняет то, что агенты уже считают правдой");
        if (op.Op == WikiPackage.OpConflict)
            return Inbox("пометка противоречия понижает доверие страницы: модель часто путает уточнение с противоречием");
        if (op.Op == WikiPackage.OpCreate && !context.TargetIsChronicle)
        {
            if (context.PageDuplicate is { } dup)
                return Inbox($"возможный дубль страницы: {dup.Field} совпадает с {dup.ExistingField} страницы «{dup.Path}»");
            var limit = context.IsBacklogPass ? MaxNewKnowledgePagesPerSectionBacklog : MaxNewKnowledgePagesPerSection;
            if (context.Counters.NewKnowledgePagesInSection > limit)
                return Inbox($"сверх лимита новых страниц раздела за ночь: {context.Counters.NewKnowledgePagesInSection} из {limit}");
        }
        if (origin == WikiGate.OriginUrl)
            return Inbox("внешний текст (origin url): промпт-инъекция извне");
        if (WikiSections.Parse(op.Page) == WikiSections.Common && context.SourceHomeIsPersonal)
            return Inbox("личное в «Общем»: решение о видимости принимает человек (ADR-036, решение 3)");
        if (context.TargetPageStatus is WikiFrontmatter.StatusDisputed or WikiFrontmatter.StatusDrift)
            return Inbox($"целевая страница в статусе «{context.TargetPageStatus}»: там уже нужен человек");
        if (context.PackageAfterModelRepair)
            return Inbox("пакет после ремонта модели: признак низкого качества прохода");

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

    private static Verdict Auto(WikiPackage.Operation op, ApplyContext context) => new(WikiPackage.DecisionAuto, op.Op switch
    {
        WikiPackage.OpCreate when context.TargetIsChronicle =>
            "хроника чата: механическая операция разбора (§12г)",
        WikiPackage.OpCreate =>
            "новая страница знаний: похожей нет, лимит новых страниц не исчерпан (§12г)",
        WikiPackage.OpAppend =>
            "утверждение в «Сводку»/«Хронологию»: применяется само (§12г)",
        _ =>
            "связь между страницами: применяется само (§12г)",
    });

    // Класс правки — ключ «origin × op» (WikiPackage.ClassKey). Origin берётся из него же:
    // второй источник origin у операции нет. Кривой ключ (не две части, origin вне перечня,
    // op не совпадает с операцией) — null: вызывающий решает это как fail-closed.
    private static string? OriginOf(string classKey, string op)
    {
        var parts = classKey.Split('×');
        if (parts.Length != 2 || parts[1] != op || !WikiGate.OriginValues.Contains(parts[0]))
            return null;
        return parts[0];
    }

    private static Verdict Inbox(string reason) => new(WikiPackage.DecisionInbox, reason);
}
