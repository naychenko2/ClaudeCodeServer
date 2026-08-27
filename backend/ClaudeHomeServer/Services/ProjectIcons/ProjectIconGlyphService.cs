using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.ProjectIcons;

/// <summary>
/// Кандидат значка: имя иконки из белого списка <see cref="LucideGlyphs"/> — рисует
/// компонент люцида на фронте. Рисованные моделью пути вырезаны: значок всегда имя.
/// </summary>
public sealed record ProjectIconGlyphCandidate(string Name);

/// <summary>
/// Итог подбора: 0–4 кандидата либо причина отказа. Пустой результат — не ошибка, а
/// фолбэк: вызывающий оставляет проект на инициалах (ADR-009 §7). FailReason — код
/// класса отказа, при полезных деталях — со значением после «:»:
/// no-model (модель не ответила), bad-json (ответ не разобрался как JSON), no-glyphs
/// (пустой набор), glyph-shape:* (элемент не того вида, в том числе paths — ветка
/// рисования вырезана), name-out:{имя} (имя вне белого списка либо вне меню выбора).
/// </summary>
public sealed record ProjectIconGlyphResult(IReadOnlyList<ProjectIconGlyphCandidate> Candidates, string? FailReason)
{
    public bool Ok => Candidates.Count > 0;

    public static readonly ProjectIconGlyphResult NoModel = new([], "no-model");
    public static readonly ProjectIconGlyphResult BadJson = new([], "bad-json");
    public static readonly ProjectIconGlyphResult NoGlyphs = new([], "no-glyphs");
}

/// <summary>
/// Белый список имён lucide (ADR-009 §5.2, ревизия 17.08.2026): всё множество имён
/// установленного lucide-react (~2000). Рукописных списков нет — состав живёт в
/// генерируемой копии <c>lucide-icon-names.g.txt</c> (EmbeddedResource, первая строка —
/// шапка «сгенерировано, руками не править»); равенство копии с установленным пакетом
/// держит <c>LucideGlyphWhitelistGuardTests</c>, меняется состав только вместе с версией
/// пакета. Полный список в модель не уходит (§5.3, ревизия 20.08.2026): ход выбора
/// получает только короткое меню имён, собранное сервером из слов-понятий первого хода.
/// </summary>
public static class LucideGlyphs
{
    private const string ResourceName = "lucide-icon-names.g.txt";

    private static readonly Lazy<IReadOnlySet<string>> Set = new(Load,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlySet<string> All => Set.Value;

    public static bool Contains(string? name) => name is not null && Set.Value.Contains(name);

    private static IReadOnlySet<string> Load()
    {
        using var stream = typeof(LucideGlyphs).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Встроенный ресурс {ResourceName} не найден — генерируемая копия имён lucide " +
                "не подключена к сборке (см. ADR-009 §5.2)");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var name = line.Trim();
            // Шапка-комментарий («сгенерировано, руками не править») и пустые строки — не имена
            if (name.Length == 0 || name[0] == '#') continue;
            set.Add(name);
        }
        return set;
    }
}

/// <summary>
/// Подбор значка проекта (ADR-009, ревизия 20.08.2026): двухходовая схема «меню вместо
/// памяти». Ход 1 — модель называет слова-понятия, а не имена иконок: имена по памяти
/// выдумывались (замер 08.2026: 7 из 27 не существовали в наборе). Сервер отбирает по
/// словам реальные имена (точное совпадение и подстрока), ход 2 — модель выбирает из
/// этого короткого меню. Если годных имён ноль — ровно один повтор с перечислением
/// отбракованных (мера 2), без цикла; не вышло — фолбэк на инициалы. Любой сбой —
/// пустой результат, проект остаётся на инициалах.
/// </summary>
public sealed class ProjectIconGlyphService(
    ICheapTextRunner cheap, ILogger<ProjectIconGlyphService> log)
{
    // Контракт хода выбора (ADR-009 §2.2): грид фиксированный, четыре кандидата
    public const int MaxCandidates = 4;

    // Контракт хода слов (ADR-009 §2.2): просим 5–8 слов-понятий, читаем не больше восьми
    public const int AskWords = 8;
    internal const int MaxWords = 8;

    // Границы меню хода выбора: не меньше MenuMinimum, чтобы выбор всегда был; не больше
    // MenuCap, чтобы промпт оставался коротким (полный набор ~26 КБ в модель не уходит)
    internal const int MenuMinimum = 4;
    internal const int MenuCap = 24;

    // Слова короче не участвуют в подборе подстрокой: «sea» натянуло бы 22 имени набора
    // (search, season, …) — созвучие вместо смысла. Осмысленные короткие слова ловятся
    // точным совпадением (sun, key, cat, dog, egg, pen, bus — всё это имена набора),
    // так что порог режет мусор, а не понятия
    private const int MinSubstringWordLength = 4;

    // Добор меню, когда ключевые слова дали мало имён: общеупотребимые иконки. Список
    // фильтруется по белому списку при старте — апгрейд пакета не сломает добор
    private static readonly string[] CommonMenuNames =
    [
        "folder", "rocket", "star", "zap", "lightbulb", "target",
        "globe", "heart", "book-open", "sparkles", "puzzle", "gem",
    ];

    private static readonly IReadOnlyList<string> CommonMenu =
        CommonMenuNames.Where(LucideGlyphs.Contains).ToList();

    // Набор в стабильном порядке: меню обязано быть детерминированным, а порядок HashSet
    // между процессами не гарантирован. Считается один раз на процесс
    private static readonly Lazy<List<string>> SortedNames = new(
        () => LucideGlyphs.All.OrderBy(static n => n, StringComparer.Ordinal).ToList(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    // В модель уходят только имя проекта и пожелание: место может работать сторонним
    // провайдером, и значок не стоит расширения поверхности утечки (ADR-009 §2.1)
    private const int PromptNameBudget = 120;
    private const int PromptHintBudget = 200;

    // {0,39}, а не {1,39}: в полном наборе есть однобуквенное имя «x», требование двух
    // символов отбрасывало бы его (ADR-009 §5.5). Членство в наборе — главная проверка,
    // форма — только дешёвый предфильтр
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    /// <param name="userHint">Пожелание владельца из поля «Опишите, что изобразить».</param>
    /// <param name="ownerId">Владелец проекта: по нему резолвится слот модели места.</param>
    public async Task<ProjectIconGlyphResult> SuggestAsync(
        string projectName, string? userHint, string ownerId, CancellationToken ct = default)
    {
        // --- Ход 1: слова-понятия (не имена иконок — реальные имена по ним отберёт сервер)
        var wordsTurn = Stopwatch.StartNew();
        string wordsRaw;
        try
        {
            wordsRaw = await cheap.RunAsync(LocalActionCatalog.ProjectIcon,
                BuildWordsPrompt(projectName, userHint), ownerId: ownerId, jsonFormat: "json", ct: ct);
        }
        catch (Exception ex)
        {
            // Длительность нужна и на отказе: по ней видно, упёрся ход в лимит места
            // (180 с) или отвалился сразу — причины разные, лечатся по-разному
            wordsTurn.Stop();
            log.LogWarning(ex, "Значок проекта «{Name}»: модель не ответила (ход слов, {WordsMs} мс)",
                projectName, (long)wordsTurn.Elapsed.TotalMilliseconds);
            return ProjectIconGlyphResult.NoModel;
        }
        wordsTurn.Stop();

        var (words, wordsReject) = ParseWords(wordsRaw);
        if (wordsReject is not null)
            // Не смертельно: меню соберётся из общеупотребимых имён, ход выбора всё равно идёт
            log.LogWarning("Значок проекта «{Name}»: ход слов отвергнут ({Reason}), меню из общеупотребимых имён",
                projectName, wordsReject);

        var menu = SelectMenu(words);
        if (menu.Count == 0)
        {
            // Слова понятны, но в наборе нет ничего про этот проект (SelectMenu §3):
            // ход выбора не запускается — выбирать не из чего, а случайный значок хуже
            // инициалов. Не ошибка, а фолбэк (§7)
            log.LogWarning("Значок проекта «{Name}»: в наборе нет иконок по смыслу ({Words}), " +
                           "остаётся на инициалах; слова {WordsMs} мс",
                projectName, string.Join(", ", words), (long)wordsTurn.Elapsed.TotalMilliseconds);
            return ProjectIconGlyphResult.NoGlyphs;
        }
        var menuSet = new HashSet<string>(menu, StringComparer.Ordinal);

        // --- Ход 2: модель выбирает из короткого меню реально существующих имён
        var pickTurn = Stopwatch.StartNew();
        string pickRaw;
        try
        {
            pickRaw = await cheap.RunAsync(LocalActionCatalog.ProjectIcon,
                BuildPickPrompt(projectName, userHint, menu), ownerId: ownerId, jsonFormat: "json", ct: ct);
        }
        catch (Exception ex)
        {
            pickTurn.Stop();
            log.LogWarning(ex, "Значок проекта «{Name}»: модель не ответила (ход выбора; слова {WordsMs} мс, выбор {PickMs} мс)",
                projectName, (long)wordsTurn.Elapsed.TotalMilliseconds, (long)pickTurn.Elapsed.TotalMilliseconds);
            return ProjectIconGlyphResult.NoModel;
        }
        pickTurn.Stop();

        var (pick, rejected) = ParsePick(pickRaw, menuSet);
        if (pick.Ok)
        {
            LogSummary(projectName, ok: true, wordsTurn.Elapsed, pickTurn.Elapsed, retry: null, pick.Candidates);
            return pick;
        }

        // --- Мера 2: ровно один повтор с перечислением отбракованного. Без цикла:
        // повтор не удался — фолбэк на инициалы
        log.LogWarning("Значок проекта «{Name}»: ход выбора отвергнут ({Reason}){Rejected}, повтор с подсказкой",
            projectName, pick.FailReason, RejectedNote(rejected));

        var retryTurn = Stopwatch.StartNew();
        string retryRaw;
        try
        {
            retryRaw = await cheap.RunAsync(LocalActionCatalog.ProjectIcon,
                BuildPickPrompt(projectName, userHint, menu, RetryNote(pick.FailReason, rejected)),
                ownerId: ownerId, jsonFormat: "json", ct: ct);
        }
        catch (Exception ex)
        {
            retryTurn.Stop();
            log.LogWarning(ex, "Значок проекта «{Name}»: повтор не удался — модель не ответила", projectName);
            LogSummary(projectName, ok: false, wordsTurn.Elapsed, pickTurn.Elapsed, retryTurn.Elapsed, []);
            return pick;
        }
        retryTurn.Stop();

        var (retryResult, _) = ParsePick(retryRaw, menuSet);
        if (!retryResult.Ok)
            log.LogWarning("Значок проекта «{Name}»: повтор отвергнут ({Reason}), проект остаётся на инициалах",
                projectName, retryResult.FailReason);
        LogSummary(projectName, retryResult.Ok, wordsTurn.Elapsed, pickTurn.Elapsed, retryTurn.Elapsed,
            retryResult.Candidates);
        return retryResult;
    }

    /// <summary>
    /// Разбор ответа модели без ограничения по меню — валидация только по белому списку
    /// (ADR-009 §2.2, §5). Точка входа ДЛЯ ТЕСТОВ: продуктовые пути ходят через
    /// <see cref="SuggestAsync"/> (там разбор идёт с меню, мера 1) и
    /// <see cref="ValidateGlyph"/> (icon/select — клиент так же недоверен, как модель).
    /// </summary>
    public static ProjectIconGlyphResult Parse(string? raw)
        => ParsePick(raw, LucideGlyphs.All).Result;

    /// <summary>
    /// Валидация одного значка по данным (не по JSON) — точка входа повторной проверки
    /// icon/select. Годен только кандидат с именем из белого списка; иначе null.
    /// </summary>
    public static ProjectIconGlyphCandidate? ValidateGlyph(string? name)
        => TryValidateName(name?.Trim(), out var named, out _) ? named : null;

    /// <summary>
    /// Отбракованные именa хода выбора, разделённые по причине: <paramref name="Unknown"/> —
    /// не прошли белый список (выдуманы), <paramref name="OffMenu"/> — настоящие имена
    /// lucide, но вне предложенного меню. Подсказка повтора (мера 2) обязана их различать:
    /// сказать про существующее имя «его нет в наборе» — соврать модели.
    /// </summary>
    internal sealed record RejectedNames(IReadOnlyList<string> Unknown, IReadOnlyList<string> OffMenu)
    {
        public static readonly RejectedNames Empty = new([], []);
        public bool Any => Unknown.Count > 0 || OffMenu.Count > 0;
        public IEnumerable<string> All => Unknown.Concat(OffMenu);
    }

    // Разбор хода выбора с ограничением по меню: имя годно, когда проходит белый список
    // И входит в предложенное меню. Выбор из памяти (имя вне меню, даже настоящее)
    // отбраковывается как name-out — в этом смысл меры 1. Отбракованные имена собираются
    // для подсказки повтора (мера 2)
    internal static (ProjectIconGlyphResult Result, RejectedNames Rejected) ParsePick(
        string? raw, IReadOnlySet<string> allowed)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return (ProjectIconGlyphResult.BadJson, RejectedNames.Empty);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("glyphs", out var glyphsEl)
                || glyphsEl.ValueKind != JsonValueKind.Array)
                return (ProjectIconGlyphResult.BadJson, RejectedNames.Empty);

            var candidates = new List<ProjectIconGlyphCandidate>();
            // Отбракованное делится на два вида, и подсказка повтора обязана их различать:
            // «нет в наборе» (выдумано) и «есть в наборе, но не предлагалось» (взято
            // из памяти мимо меню). Сказать модели «stethoscope нет в наборе» — соврать,
            // и знающая lucide модель начнёт спорить вместо выбора
            var unknown = new List<string>();
            var offMenu = new List<string>();
            string? failReason = null;

            void Reject(List<string> bucket, string name)
            {
                failReason ??= $"name-out:{Truncate(name, 60)}";
                if (!bucket.Contains(name)) bucket.Add(name);
            }

            foreach (var el in glyphsEl.EnumerateArray())
            {
                if (candidates.Count >= MaxCandidates) break;   // грид фиксированный
                if (TryReadGlyph(el, out var candidate, out var reason))
                {
                    // Дубль — не отказ, а молчаливый пропуск: на узком меню модель легко
                    // назовёт имя дважды, а две одинаковые плитки в гриде выглядят
                    // поломкой и съедают место у осмысленного варианта
                    if (candidates.Any(c => c.Name == candidate!.Name)) continue;
                    if (allowed.Contains(candidate!.Name)) candidates.Add(candidate);
                    else Reject(offMenu, candidate.Name);   // имя настоящее, но вне меню
                }
                else
                {
                    // Причина пустого итога — отказ первого негодного кандидата: модель
                    // обычно повторяет одну и ту же ошибку, а логу нужна конкретика
                    failReason ??= reason;
                    // Элемент с именем (пусть и отбракованным) — в подсказку повтора;
                    // у paths и пустых элементов перечислять нечего
                    if (el.ValueKind == JsonValueKind.Object
                        && el.TryGetProperty("name", out var nameEl)
                        && nameEl.ValueKind == JsonValueKind.String
                        && nameEl.GetString() is { Length: > 0 } name)
                        Reject(unknown, name);   // имя не прошло белый список — выдумано
                }
            }
            var rejected = new RejectedNames(unknown, offMenu);
            if (candidates.Count > 0) return (new ProjectIconGlyphResult(candidates, null), rejected);
            return (failReason is null ? ProjectIconGlyphResult.NoGlyphs
                : new ProjectIconGlyphResult([], failReason), rejected);
        }
        catch (JsonException)
        {
            return (ProjectIconGlyphResult.BadJson, RejectedNames.Empty);
        }
    }

    // Контракт хода слов: {"words": ["lighthouse", "sea", ...]}. Слова нормализуются
    // в нижний регистр, фразы пробуются и дефисным написанием ("traffic light" →
    // "traffic-light" + части). Причина отказа хода: bad-json | no-words
    internal static (IReadOnlyList<string> Words, string? RejectReason) ParseWords(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return ([], "bad-json");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("words", out var wordsEl)
                || wordsEl.ValueKind != JsonValueKind.Array)
                return ([], "bad-json");

            var words = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in wordsEl.EnumerateArray())
            {
                if (words.Count >= MaxWords) break;
                if (el.ValueKind != JsonValueKind.String) continue;
                foreach (var token in NormalizeWord(el.GetString()))
                {
                    if (words.Count >= MaxWords) break;
                    if (seen.Add(token)) words.Add(token);
                }
            }
            return words.Count > 0 ? (words, null) : ([], "no-words");
        }
        catch (JsonException)
        {
            return ([], "bad-json");
        }
    }

    // Мера 1: реальные имена lucide из слов-понятий. Сначала точное совпадение (слово и
    // есть имя), затем подстрока в обе стороны: имя содержит слово ("light" → "lightbulb")
    // либо слово содержит имя ("lighthouse" → "house"). Имена перебираются отсортированными —
    // меню детерминировано. Меньше MenuMinimum — добор общеупотребимыми: ход выбора
    // всегда должен получить, из чего выбирать
    internal static IReadOnlyList<string> SelectMenu(IReadOnlyCollection<string> words)
    {
        var menu = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name)
        {
            if (menu.Count < MenuCap && seen.Add(name)) menu.Add(name);
        }

        // 1. Точное совпадение: слово само по себе имя набора
        foreach (var word in words)
            if (LucideGlyphs.Contains(word)) Add(word);

        // 2. Подстрока по границе; набор перебирается отсортированным — меню
        // детерминировано независимо от порядка HashSet (и между процессами тоже).
        // 1995 имён × ≤8 слов — доли миллисекунды, подбор значка — редкая операция
        var byWord = new List<List<string>>();
        foreach (var word in words)
        {
            if (word.Length < MinSubstringWordLength) continue;
            byWord.Add(SortedNames.Value.Where(name => Matches(name, word)).ToList());
        }

        // Совпадения раскладываются по кругу — по одному имени с каждого слова за проход.
        // Иначе одно многодетное слово занимает всё меню целиком («book» содержится в 39
        // именах набора при MenuCap = 24), остальные понятия не доходят до модели вовсе,
        // и грид показывает четыре вариации одной иконки вместо четырёх разных смыслов
        for (var round = 0; menu.Count < MenuCap; round++)
        {
            var progressed = false;
            foreach (var matches in byWord)
            {
                if (menu.Count >= MenuCap) break;
                if (round >= matches.Count) continue;
                Add(matches[round]);
                progressed = true;
            }
            if (!progressed) break;   // совпадения всех слов исчерпаны
        }

        // 3. Добор общеупотребимыми именами — только когда есть смысловое ядро либо слов
        // не было вовсе (сбой хода слов, ADR-009 §2.4: он не смертелен).
        //
        // Слова есть, а совпадений ноль — значит в наборе нет ничего про этот проект
        // («шарф ручной вязки», «самовар»: живая приёмка 20.08.2026). Добор в этом случае
        // выдавал четыре случайных значка за подбор — folder, ad, rocket, car для шарфа.
        // Для человека это хуже инициалов: буква «Ш» честна, ракета — нет. Пустое меню
        // тут правильный ответ: подбор не удался, проект остаётся на инициалах (§7)
        if (menu.Count == 0 && words.Count > 0) return menu;

        foreach (var name in CommonMenu)
        {
            if (menu.Count >= MenuMinimum) break;
            Add(name);
        }
        return menu;
    }

    // Совпадение имени набора со словом-понятием — ОБЕ стороны только по границе, никогда
    // куском середины. Живая приёмка 20.08.2026 показала, что середина ловит созвучие,
    // а не смысл, и в обе стороны одинаково: «scarf» давал «car», а «hive» (улей) —
    // «archive», «rain» — «brain», «over» — «clover». Для человека такой значок мусор,
    // а место в гриде он занимает наравне с осмысленным.
    //
    // Граница у имени — дефисный сегмент: «light» → «lightbulb» и «wallet» →
    // «wallet-cards» осмысленны (имя уточняет понятие), «hive» → «archive» нет.
    // Граница у слова — его начало или конец: «lighthouse» → «house», «bookshelf» → «book»
    private static bool Matches(string name, string word)
    {
        // Имя уточняет понятие: слово начинает имя или один из его дефисных сегментов
        if (name.Length >= word.Length)
        {
            foreach (var segment in name.Split('-'))
                if (segment.StartsWith(word, StringComparison.Ordinal)) return true;
            return false;
        }

        // Понятие составное: имя стоит его началом или концом
        return word.StartsWith(name, StringComparison.Ordinal)
            || word.EndsWith(name, StringComparison.Ordinal);
    }

    private static IEnumerable<string> NormalizeWord(string? rawWord)
    {
        var cleaned = Regex.Replace((rawWord ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (cleaned.Length == 0) yield break;
        yield return cleaned;   // «lighthouse» либо «traffic-light»
        foreach (var part in cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries))
            if (!string.Equals(part, cleaned, StringComparison.Ordinal)) yield return part;
    }

    private static bool TryReadGlyph(JsonElement el, out ProjectIconGlyphCandidate? glyph, out string? reason)
    {
        glyph = null;
        reason = null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            reason = "glyph-shape:not-object";
            return false;
        }

        // Рисованные пути вырезаны: элемент с paths негоден и при паре с именем тоже —
        // значок всегда называется именем из набора
        if (el.TryGetProperty("paths", out _))
        {
            reason = "glyph-shape:paths";
            return false;
        }

        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            reason = "glyph-shape:none";
            return false;
        }
        return TryValidateName(nameEl.GetString(), out glyph, out reason);
    }

    private static bool TryValidateName(string? name, out ProjectIconGlyphCandidate? glyph, out string? reason)
    {
        glyph = null;
        reason = null;
        var n = name?.Trim();
        // Форма ключа + членство в белом списке: имя — единственное, что модель может
        // сообщить, и оно обязано существовать в наборе установленного lucide-react
        // (ADR-009 §5). Нарушение формы и промах по списку — один класс: имя негодно,
        // показываем какое
        if (n is null || !NamePattern.IsMatch(n) || !LucideGlyphs.Contains(n))
        {
            reason = $"name-out:{Truncate(n ?? "(пусто)", 60)}";
            return false;
        }
        glyph = new ProjectIconGlyphCandidate(n);
        return true;
    }

    // Ход 1: модель называет слова-понятия, а не имена иконок — имена по памяти
    // выдумывались (замер 08.2026: 7 из 27 не существовали в наборе), реальные имена
    // по словам отбирает сервер (мера 1, ADR-009 §2)
    private static string BuildWordsPrompt(string projectName, string? userHint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты подбираешь значок для проекта — маленькую контурную пиктограмму по смыслу проекта.");
        sb.AppendLine("Пока НЕ называй иконки: только понятия, по которым готовые иконки подберёт сервер.");
        sb.AppendLine();
        sb.AppendLine($"Проект называется «{Truncate(projectName.Trim(), PromptNameBudget)}».");
        AppendHint(sb, userHint);
        sb.AppendLine();
        sb.AppendLine($"Назови 5–{AskWords} РАЗНЫХ английских слов-понятий: предметы, явления, действия, инструменты,");
        sb.AppendLine("ассоциирующиеся с проектом. Существительные, нижний регистр, без интерфейсных терминов.");
        sb.AppendLine("Пример для проекта про маяк: lighthouse, sea, tower, navigation, light, coast.");
        sb.AppendLine();
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом такого вида, без пояснений и без markdown-обвязки:");
        sb.AppendLine("""
            {
              "words": ["lighthouse", "sea", "tower", "navigation", "light"]
            }
            """);
        return sb.ToString();
    }

    // Ход 2: модель выбирает из короткого меню реально существующих имён — не из памяти.
    // retryNote — мера 2: что было отбраковано в прошлый раз и почему выбрать надо иначе
    private static string BuildPickPrompt(string projectName, string? userHint,
        IReadOnlyList<string> menu, string? retryNote = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты подбираешь значок для проекта — маленькую контурную пиктограмму, одну из готовых");
        sb.AppendLine("интерфейсных иконок набора lucide.");
        sb.AppendLine();
        sb.AppendLine($"Проект называется «{Truncate(projectName.Trim(), PromptNameBudget)}».");
        AppendHint(sb, userHint);
        sb.AppendLine();
        if (retryNote is not null)
        {
            sb.AppendLine(retryNote);
            sb.AppendLine();
        }
        sb.AppendLine("Вот имена, которые РЕАЛЬНО существуют в наборе — выбирать можно ТОЛЬКО из них:");
        sb.AppendLine(string.Join(", ", menu));
        sb.AppendLine();
        // «до N», а не «ровно N»: меню могло добраться до четырёх общеупотребимыми именами,
        // и требование ровного числа заставляло модель дописывать в грид folder и rocket
        // рядом с осмысленным значком. Два подходящих кандидата лучше двух подходящих
        // и двух случайных — грид переживёт неполный ряд, доверие к подбору нет
        sb.AppendLine($"Выбери до {MaxCandidates} имён, подходящих проекту по смыслу — только те, что");
        sb.AppendLine("действительно подходят: два хороших лучше четырёх со случайными.");
        sb.AppendLine("Ответь ТОЛЬКО JSON-объектом такого вида, без пояснений и без markdown-обвязки:");
        sb.AppendLine("""
            {
              "glyphs": [
                { "name": "piggy-bank" },
                { "name": "chart-line" }
              ]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Правила:");
        sb.AppendLine("- name — строго одно из имён списка выше, с точностью до символа;");
        sb.AppendLine("- никаких paths и никаких нарисованных путей в ответе — ТОЛЬКО имена;");
        sb.AppendLine("- НИКАКОЙ SVG-разметки: fill, stroke, style, text, transform — цвет и толщину линий");
        sb.AppendLine("  задаёт сам продукт.");
        return sb.ToString();
    }

    private static void AppendHint(StringBuilder sb, string? userHint)
    {
        var hint = userHint?.Trim();
        if (!string.IsNullOrEmpty(hint))
            sb.AppendLine($"Что изобразить (пожелание владельца): {Truncate(hint, PromptHintBudget)}");
    }

    private static string RejectedNote(RejectedNames rejected)
        => rejected.Any ? $", отбраковано: {string.Join(", ", rejected.All)}" : "";

    // Подсказка повтора (мера 2): перечислить отбракованное и потребовать только реальное.
    // Две причины называются РАЗНЫМИ словами: выдуманного имени в наборе нет, а имя вне
    // меню существует — про него нельзя говорить «нет в наборе», иначе знающая lucide
    // модель получит заведомо ложное утверждение и уйдёт спорить вместо выбора
    private static string RetryNote(string? failReason, RejectedNames rejected)
    {
        if (rejected.Any)
        {
            var sb = new StringBuilder("В прошлый раз ");
            if (rejected.Unknown.Count > 0)
                sb.Append($"ты назвал {string.Join(", ", rejected.Unknown)} — таких имён в наборе НЕТ. ");
            if (rejected.OffMenu.Count > 0)
                sb.Append($"Имена {string.Join(", ", rejected.OffMenu)} в наборе есть, но их не было " +
                          "в предложенном списке — выбирать можно только из него. ");
            sb.Append("Назови другие — строго из списка ниже.");
            return sb.ToString();
        }
        if (failReason is not null && failReason.StartsWith("glyph-shape", StringComparison.Ordinal))
            return "В прошлый раз в ответе были нарисованные пути или разметка вместо имён — пути не поддерживаются. " +
                   "Выбери только готовые имена из списка ниже.";
        return "В прошлый раз ответ не удалось использовать. Ответь строго JSON-объектом указанного вида, " +
               "выбрав имена только из списка ниже.";
    }

    // Диагностика двухходовки одним Warning: длительность каждого хода и повтор.
    // Warning, а не Information: файловый лог прода режет Information, а подбор значка —
    // редкая операция (кнопка пользователя да разовая миграция)
    private void LogSummary(string projectName, bool ok, TimeSpan words, TimeSpan pick, TimeSpan? retry,
        IReadOnlyList<ProjectIconGlyphCandidate> candidates)
    {
        // Сами имена — в сводке: без них спорный подбор («звезда» для проекта про шарф)
        // виден только глазами в интерфейсе, а по логу неотличим от осмысленного
        var picked = candidates.Count == 0 ? "—" : string.Join(", ", candidates.Select(c => c.Name));
        if (retry is { } r)
            log.LogWarning("Значок проекта «{Name}»: подбор {Status} [{Picked}]: слова {WordsMs} мс, выбор {PickMs} мс, повтор {RetryMs} мс",
                projectName, ok ? "ок" : "пусто", picked,
                (long)words.TotalMilliseconds, (long)pick.TotalMilliseconds, (long)r.TotalMilliseconds);
        else
            log.LogWarning("Значок проекта «{Name}»: подбор {Status} [{Picked}]: слова {WordsMs} мс, выбор {PickMs} мс",
                projectName, ok ? "ок" : "пусто", picked,
                (long)words.TotalMilliseconds, (long)pick.TotalMilliseconds);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // Ответ модели может приехать в ```-заборе или с болтовнёй вокруг: берём объект от
    // первой { до парной ей } (приём DocumentAiService.ExtractJsonObject)
    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }
}
