using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm.Claude;

/// <summary>
/// Склейка системного промпта хода из секций — ЕДИНСТВЕННЫЙ путь сборки.
/// Раньше текст копился в одной переменной тринадцатью повторами
/// «пусто ? кусок : накопленное + \n\n + кусок»; теперь каждый кусок кладётся секцией,
/// а отправляемый текст собирается отсюда. Двух параллельных путей («что показали
/// пользователю» и «что отправили модели») не остаётся — разойтись им негде.
/// </summary>
public static class TurnPromptAssembler
{
    // Разделитель между слоем продукта и слоем персоны: персона — отдельный голос,
    // а не очередная подсказка (порядок и разделители менять нельзя — это ровно то,
    // что уходит в --append-system-prompt)
    public const string PersonaSeparator = "\n\n---\n\n";

    // Жёсткий лимит командной строки Windows — 32 767 символов. Считаем по широким символам
    // (Unicode), и реальный лимит у Process.Start именно в широких символах. .NET собирает
    // командную строку сам и при превышении бросает Win32Exception с NativeErrorCode=206
    // (ERROR_FILENAME_EXCED_RANGE), а не при сериализации каждого аргумента — поэтому порог
    // надо ставить по СУММЕ длин, а не по длине одного --append-system-prompt.
    public const int CmdlineLimit = 32_767;

    // Порог, при котором запускаем срезку. 30 000 из задачи dc641949: между 30 000 и 32 767
    // живёт escape-сериализация .NET (" → \", переводы строк), и небольшой запас на дрейф
    // от длин других аргументов. Все 48 чатов из зоны 30–32к уходят вниз за 30к после срезки
    // пяти нестабильных секций (~5 КБ суммарно). Ниже 30к — обычный ход, никакой срезки.
    public const int BudgetThreshold = 30_000;

    // Приоритеты срезки в указанном порядке. Каждое имя — ключ секции (PromptSectionDto.Key),
    // которую ClaudeSession добавляет через Add("...", ...). Все пятеро помечены stable: false
    // — кэш промпта их не держит, срезка не бьёт по cache_read. Слой персоны и stable-секции
    // (project-builtin, mcp-tasks, voice-mode, mcp-memory, images, mcp-workspace, mcp-personas,
    // persona-bindings, prompt-sections, recall-notes для проектов, dossier-trailer) НЕ
    // трогаем: это контрактная обвязка, которую пользователь ожидает увидеть в каждом ходе.
    // Если после срезания ВСЕХ пяти строка всё равно > 30 000 — ApplyBudget возвращает
    // Overflowed=true, ClaudeSession кидает PromptOverflowException.
    public static readonly IReadOnlyList<string> TruncationOrder = new[]
    {
        "code-graph",
        "dossier-recall",
        "recall-notes",
        "recall-memory",
        "persona-mentions",
    };

    /// <summary>
    /// Текст для --append-system-prompt. Учитываются только секции Kind = "system":
    /// текст хода (Kind = "turn") идёт в stdin отдельным сообщением, а не в промпт.
    /// </summary>
    public static string Combine(IReadOnlyList<PromptSectionDto> sections, string? personaLayer)
    {
        var body = string.Join("\n\n", sections
            .Where(s => s.Kind == "system" && !string.IsNullOrWhiteSpace(s.Text))
            .Select(s => s.Text));

        if (string.IsNullOrWhiteSpace(personaLayer)) return body;
        return string.IsNullOrWhiteSpace(body) ? personaLayer : body + PersonaSeparator + personaLayer;
    }

    /// <summary>
    /// Бюджет промпта на склейке: считает сумму длин cli-команды, WorkingDirectory и всех
    /// аргументов, плюс объединённый промпт. Если итог больше BudgetThreshold — срезает
    /// секции в порядке TruncationOrder и возвращает урезанный список, пока не влезет
    /// в бюджет или пока приоритеты не кончатся. TruncatedSections едут в
    /// PromptSnapshotDraft.TruncatedSections — пометка в шторке "что ушло модели".
    /// Combine секций из TruncatedSections НЕ делает (Kind="truncated"): они нужны
    /// только для UI/диагностики, в модель не уходят.
    /// </summary>
    /// <param name="sections">Список секций, подготовленный ClaudeSession (Kind="system" идут в склейку).</param>
    /// <param name="personaLayer">Слой персоны (PersonaLayerContributor) — отдельный аргумент, в sections не лежит.</param>
    /// <param name="args">Уже собранные аргументы CLI на момент склейки (без --append-system-prompt).</param>
    /// <param name="cliCommand">Имя/путь исполняемого файла (claude или fake-claude в тестах).</param>
    /// <param name="workingDir">WorkingDirectory процесса — путь к корню проекта.</param>
    /// <param name="threshold">Порог в символах; по умолчанию BudgetThreshold.</param>
    public static PromptBudgetResult ApplyBudget(
        IReadOnlyList<PromptSectionDto> sections,
        string? personaLayer,
        IReadOnlyList<string> args,
        string? cliCommand,
        string? workingDir,
        int threshold = BudgetThreshold)
    {
        // Считаем длину всей командной строки, которую соберёт .NET (path + " " + args). Это
        // верхняя оценка: реальная строка после escape-сериализации может быть короче, но
        // здесь нам важна верхняя граница — иначе мы решали бы по ложно-низкой цифре и
        // резали в момент, когда реально процесс бы и так стартовал.
        int Estimate(string combined)
        {
            var n = cliCommand?.Length ?? 0;
            if (!string.IsNullOrEmpty(workingDir)) n += workingDir!.Length + 1;
            foreach (var a in args) n += a.Length + 1; // +1 за пробел-разделитель
            n += combined.Length;
            return n;
        }

        var initialCombined = Combine(sections, personaLayer);
        var initialTotal = Estimate(initialCombined);
        if (initialTotal <= threshold)
            return new PromptBudgetResult(initialCombined, [], initialTotal, false);

        // Превышаем порог — срезаем в порядке TruncationOrder. Копию sections делаем потому,
        // что входной список менять нельзя (вызывающий может иметь свою модель памяти; на
        // текущий момент ClaudeSession уже не использует sections после склейки, но это
        // полезно для тестов и для будущих вызывающих).
        var pool = sections.ToList();
        var truncated = new List<PromptSectionDto>();
        var currentCombined = initialCombined;

        foreach (var key in TruncationOrder)
        {
            // Удаляем все секции с этим ключом из пула. Их может быть больше одной, если
            // контрибьютор в будущем начнёт добавлять несколько — лишних удалений не будет.
            var removed = pool.RemoveAll(s => s.Key == key);
            currentCombined = Combine(pool, personaLayer);
            var totalAfter = Estimate(currentCombined);

            if (totalAfter <= threshold)
            {
                // Срезка УСПЕШНА: ключ из TruncationOrder был в пуле, после удаления
                // влезли в бюджет. В truncated кладём ровно одну запись с пометкой
                // "всё уложилось". Сюда попадаем ТОЛЬКО когда RemoveAll > 0 — иначе мы бы
                // записывали пометку про "срезку" секции, которой не было, а это вводит
                // пользователя в заблуждение (стабильные секции по 10 000 не из
                // TruncationOrder, и ApplyBudget не должен ничего про них писать).
                if (removed > 0)
                    truncated.Add(MakeTruncatedNote(key, totalAfter, threshold, false));
                return new PromptBudgetResult(currentCombined, truncated, totalAfter, false);
            }

            // Ещё не влезло. Секция действительно срезана (RemoveAll > 0) — пишем
            // заметку с пометкой "всё ещё не влезает". Сюда не попадаем, если у пула
            // нет такого ключа: таких секций ApplyBudget не трогает, и пометка о срезании
            // неуместна (тот же гейт, что у успешной ветки выше).
            if (removed > 0)
                truncated.Add(MakeTruncatedNote(key, totalAfter, threshold, true));
        }

        // Приоритеты кончились, и итог всё ещё больше порога. Не влезает — даже срезка
        // не спасла. ClaudeSession бросит PromptOverflowException, адаптер увидит
        // FallbackErrorClass.PromptOverflow и завершит ход без фолбэка.
        var finalTotal = Estimate(currentCombined);
        return new PromptBudgetResult(currentCombined, truncated, finalTotal, true);
    }

    // Заметка в снимке промпта: ровно одна на каждый урезанный ключ. Текст содержит факт
    // (что обрезали, сколько символов сейчас), без сырого содержимого секции — она
    // вырезана целиком. UI решает, как показать.
    private static PromptSectionDto MakeTruncatedNote(string key, int totalAfter, int threshold, bool stillOver)
    {
        var verdict = stillOver
            ? $"После её удаления обвязка всё ещё превышает бюджет ({totalAfter}/{threshold})."
            : $"После её удаления обвязка уложилась в бюджет ({totalAfter}/{threshold}).";
        return new PromptSectionDto(
            Key: $"(truncated:{key})",
            Title: $"Секция «{key}» обрезана",
            Text:
                $"Секция «{key}» удалена из системного промпта: итоговая командная строка " +
                $"превысила порог {threshold} символов (лимит Windows {CmdlineLimit}). " +
                $"{verdict} Секция помечена stable: false, кэш промпта её не держит — " +
                $"срезка не бьёт по cache_read. Содержимое не выводится в этом снимке: " +
                $"секция удалена целиком и не уходила в модель этого хода.",
            Kind: "truncated");
    }
}

/// <summary>
/// Итог сборки промпта под бюджет. Overflowed=true означает, что после срезки ВСЕХ
/// приоритетов строка всё равно длиннее BudgetThreshold — ClaudeSession кидает
/// PromptOverflowException, адаптер получает FallbackErrorClass.PromptOverflow.
/// </summary>
public sealed record PromptBudgetResult(
    string CombinedPrompt,
    IReadOnlyList<PromptSectionDto> TruncatedSections,
    int TotalCmdlineChars,
    bool Overflowed);

