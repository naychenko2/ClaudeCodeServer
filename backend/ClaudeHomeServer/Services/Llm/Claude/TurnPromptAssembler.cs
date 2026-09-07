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
    // нестабильных секций (~5 КБ суммарно). Ниже 30к — обычный ход, никакой срезки.
    //
    // ВАЖНО, чем этот порог НЕ является: это триггер срезки, а не отказ. Ход, у которого
    // после срезки осталось 30–32к, стартует штатно — Overflowed ставится по CmdlineLimit.
    // Прежняя версия отказывала уже на 30 000 и роняла PromptOverflowException у чатов,
    // которые до фикса ходили нормально (регрессия, ревью dc641949: M5).
    public const int BudgetThreshold = 30_000;

    // Пятый и последний шаг срезки. Вынесен именем, потому что он — исключение из правила
    // «режем только stable: false»: секция persona-mentions заводится в ClaudeSession
    // стабильной (дефолт stable: true), и срезка по ней инвалидирует кэшируемый префикс.
    //
    // Флаг НЕ меняем осознанно: stable: false удешевил бы аварийный случай ценой
    // инвалидации кэша у КАЖДОГО хода ВСЕХ чатов — размен неверный. Правда в том, что
    // пятый шаг действительно бьёт по кэшируемому префиксу, и оправдан он ровно одним:
    // альтернатива — несостоявшийся ход.
    public const string StableLastResortKey = "persona-mentions";

    // Имя флага, под которым собранный промпт уезжает в CLI. Держим здесь, потому что
    // Estimate обязан учесть и сам флаг: ClaudeSession добавляет пару
    // ["--append-system-prompt", combined] уже ПОСЛЕ вызова ApplyBudget, и в args её нет.
    private const string AppendSystemPromptFlag = "--append-system-prompt";

    // Приоритеты срезки в указанном порядке. Каждое имя — ключ секции (PromptSectionDto.Key),
    // которую ClaudeSession добавляет через Add("...", ...).
    //
    // ПЕРВЫЕ ЧЕТЫРЕ помечены stable: false — кэш промпта их не держит, срезка не бьёт
    // по cache_read: code-graph, dossier-recall, recall-notes (ClaudeSession.cs, Add(...,
    // stable: false) — она НЕ из числа стабильных, вопреки прежней редакции этого
    // комментария), recall-memory.
    // ПЯТЫЙ — StableLastResortKey, см. выше: стабильная секция как последний шанс.
    //
    // Слой персоны и остальные stable-секции (project-builtin, mcp-tasks, voice-mode,
    // mcp-memory, images, mcp-workspace, mcp-personas, persona-bindings, prompt-sections,
    // dossier-trailer) НЕ трогаем: это контрактная обвязка, которую пользователь ожидает
    // видеть в каждом ходе. Если после срезания ВСЕХ пяти оценка всё равно > CmdlineLimit
    // — ApplyBudget возвращает Overflowed=true, ClaudeSession кидает PromptOverflowException.
    public static readonly IReadOnlyList<string> TruncationOrder = new[]
    {
        "code-graph",
        "dossier-recall",
        "recall-notes",
        "recall-memory",
        StableLastResortKey,
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
    /// Бюджет промпта на склейке: считает ВЕРХНЮЮ оценку длины командной строки,
    /// которую соберёт .NET из ProcessStartInfo.ArgumentList (cli + каждый аргумент
    /// в экранированном виде + пара --append-system-prompt со склеенным промптом).
    ///
    /// Две разные величины, их нельзя путать:
    /// BudgetThreshold (30 000) — ТРИГГЕР срезки: превысили — режем секции в порядке
    /// TruncationOrder, пока не уложимся или пока приоритеты не кончатся;
    /// CmdlineLimit (32 767) — ОТКАЗ: Overflowed ставится только по нему. Ход, который
    /// после срезки остался в зоне 30–32к, стартует штатно.
    ///
    /// TruncatedSections едут в PromptSnapshotDraft.TruncatedSections — пометка в шторке
    /// "что ушло модели"; Combine их НЕ берёт (Kind="truncated"): они для UI/диагностики,
    /// в модель не уходят. Sections — итоговый пул после срезки, его и надо публиковать
    /// в снимок, иначе снимок разойдётся с отправленным промптом.
    /// </summary>
    /// <param name="sections">Список секций, подготовленный ClaudeSession (Kind="system" идут в склейку).</param>
    /// <param name="personaLayer">Слой персоны (PersonaLayerContributor) — отдельный аргумент, в sections не лежит.</param>
    /// <param name="args">Уже собранные аргументы CLI на момент склейки (без --append-system-prompt).</param>
    /// <param name="cliCommand">Имя/путь исполняемого файла (claude или fake-claude в тестах).</param>
    /// <param name="workingDir">WorkingDirectory процесса — путь к корню проекта. Не входит в
    /// командную строку (это отдельный параметр ProcessStartInfo.WorkingDirectory).</param>
    /// <param name="threshold">Порог срезки; по умолчанию BudgetThreshold (30 000).</param>
    public static PromptBudgetResult ApplyBudget(
        IReadOnlyList<PromptSectionDto> sections,
        string? personaLayer,
        IReadOnlyList<string> args,
        string? cliCommand,
        string? workingDir,
        int threshold = BudgetThreshold)
    {
        // ВЕРХНЯЯ оценка длины командной строки, которую соберёт .NET из ArgumentList:
        // cliCommand + для каждого аргумента (пробел + экранированный аргумент).
        //
        // Экранирование .NET (ProcessStartInfo → PasteArguments): аргумент с пробелом или
        // табом обрамляется кавычками (+2), а каждая внутренняя " экранируется обратным
        // слэшем (+1 на кавычку); плюс слэши перед кавычкой удваиваются. Считать точно
        // незачем — нам нужна ВЕРХНЯЯ граница, поэтому берём худший случай для каждого
        // аргумента: длина + 2 (кавычки) + 2 * число кавычек внутри + 1 (пробел-разделитель).
        // Занижать нельзя: по заниженной цифре мы бы пропустили ход, который Process.Start
        // отвергнет с Win32 206.
        //
        // workingDir в оценку НЕ входит: это ProcessStartInfo.WorkingDirectory — отдельный
        // параметр CreateProcess (lpCurrentDirectory), в lpCommandLine он не попадает
        // (см. LocalProcessRunner.BuildStartInfo: psi.WorkingDirectory ставится отдельно
        // от psi.ArgumentList). Прежняя версия считала его — завышала оценку на длину
        // корня проекта и резала ходы, которые стартовали бы штатно (ревью dc641949: L7).
        static int ArgCost(string a)
        {
            var quotes = 0;
            foreach (var c in a) if (c == '"') quotes++;
            return a.Length + 2 + 2 * quotes + 1;
        }

        int Estimate(string combined)
        {
            var n = cliCommand?.Length ?? 0;
            foreach (var a in args) n += ArgCost(a);
            // combined на момент вызова ещё НЕ в args: ClaudeSession добавляет
            // --append-system-prompt уже после возврата ApplyBudget. Поэтому оба слагаемых
            // (имя флага и его значение) считаем здесь руками, иначе самый длинный аргумент
            // хода остался бы неучтённым.
            if (!string.IsNullOrWhiteSpace(combined))
                n += ArgCost(AppendSystemPromptFlag) + ArgCost(combined);
            return n;
        }

        var initialCombined = Combine(sections, personaLayer);
        var initialTotal = Estimate(initialCombined);

        // Ниже порога срезки — обычный ход, ничего не трогаем.
        if (initialTotal <= threshold)
            return new PromptBudgetResult(initialCombined, [], initialTotal, false, sections.ToList());

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
                return new PromptBudgetResult(currentCombined, truncated, totalAfter, false, pool);
            }

            // Ещё не влезло. Секция действительно срезана (RemoveAll > 0) — пишем
            // заметку с пометкой "всё ещё не влезает". Сюда не попадаем, если у пула
            // нет такого ключа: таких секций ApplyBudget не трогает, и пометка о срезании
            // неуместна (тот же гейт, что у успешной ветки выше).
            if (removed > 0)
                truncated.Add(MakeTruncatedNote(key, totalAfter, threshold, true));
        }

        // Приоритеты кончились, а порог срезки так и не взят. ОТКАЗ ставим не по нему:
        // порог — триггер срезки, а настоящая граница одна — лимит командной строки
        // Windows. Ход, оставшийся в зоне 30 000–32 767, стартует штатно: наша оценка
        // ВЕРХНЯЯ, реальная строка после экранирования не длиннее (ревью dc641949: M5,
        // регрессия у чатов со стабильной обвязкой 30–32к и пустыми нестабильными секциями).
        //
        // Если же оценка перевалила CmdlineLimit — отказываем сразу и честно: доказать
        // безопасность старта мы не можем, а Process.Start отдал бы Win32 206, который
        // классификатор увёл бы в бесполезный перебор мёртвых шагов цепочки.
        var finalTotal = Estimate(currentCombined);
        return new PromptBudgetResult(
            currentCombined, truncated, finalTotal, finalTotal > CmdlineLimit, pool);
    }

    // Заметка в снимке промпта: ровно одна на каждый урезанный ключ. Текст содержит факт
    // (что обрезали, сколько символов сейчас), без сырого содержимого секции — она
    // вырезана целиком. UI решает, как показать.
    private static PromptSectionDto MakeTruncatedNote(string key, int totalAfter, int threshold, bool stillOver)
    {
        var verdict = stillOver
            ? $"После её удаления обвязка всё ещё превышает бюджет ({totalAfter}/{threshold})."
            : $"После её удаления обвязка уложилась в бюджет ({totalAfter}/{threshold}).";

        // Про кэш промпта говорим по факту флага секции, а не одной фразой на всех:
        // persona-mentions стабильна (stable: true) и стоит последней в очереди — срезка
        // по ней РЕАЛЬНО инвалидирует кэшируемый префикс. Прежний текст утверждал
        // «stable: false, кэш её не держит» для всех пяти ключей и для пятого врал.
        var cacheNote = key == StableLastResortKey
            ? "Секция стабильная: её срезка бьёт по кэшируемому префиксу. Это последний шаг "
              + "перед отказом — потерять кэш лучше, чем не отправить ход."
            : "Секция помечена stable: false, кэш промпта её не держит — "
              + "срезка не бьёт по cache_read.";

        return new PromptSectionDto(
            Key: $"(truncated:{key})",
            Title: $"Секция «{key}» обрезана",
            Text:
                $"Секция «{key}» удалена из системного промпта: итоговая командная строка " +
                $"превысила порог {threshold} символов (лимит Windows {CmdlineLimit}). " +
                $"{verdict} {cacheNote} Содержимое не выводится в этом снимке: " +
                $"секция удалена целиком и не уходила в модель этого хода.",
            Kind: "truncated");
    }
}

/// <summary>
/// Итог сборки промпта под бюджет. Overflowed=true означает, что после срезки ВСЕХ
/// приоритетов строка всё равно длиннее CmdlineLimit — ClaudeSession кидает
/// PromptOverflowException, адаптер получает FallbackErrorClass.PromptOverflow.
/// Sections содержат итоговый пул секций после срезки (для согласованного снимка промпта).
/// </summary>
public sealed record PromptBudgetResult(
    string CombinedPrompt,
    IReadOnlyList<PromptSectionDto> TruncatedSections,
    int TotalCmdlineChars,
    bool Overflowed,
    IReadOnlyList<PromptSectionDto> Sections);

