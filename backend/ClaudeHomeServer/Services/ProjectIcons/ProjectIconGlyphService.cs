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
/// рисования вырезана), name-out:{имя} (имя вне белого списка).
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
/// пакета. В промпт список не уходит (§5.3): имя модель называет по своему знанию lucide,
/// членство проверяет сервер.
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
/// Подбор значка проекта (ADR-009): дешёвый текстовый ход по названию проекта и
/// необязательному пожеланию владельца → JSON с кандидатами → валидация имён по белому
/// списку → до четырёх кандидатов. Рисованные пути вырезаны: значок — только имя иконки
/// из набора lucide, разметки от модели не существует. Любой сбой — пустой результат,
/// проект остаётся на инициалах.
/// </summary>
public sealed class ProjectIconGlyphService(
    ICheapTextRunner cheap, ILogger<ProjectIconGlyphService> log)
{
    // Лимиты контракта ADR-009 §2.2: просим шесть, показываем четыре — после отсева
    // негодных кандидатов должно остаться из чего выбирать.
    public const int AskGlyphs = 6;
    public const int MaxCandidates = 4;

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
        string raw;
        try
        {
            raw = await cheap.RunAsync(LocalActionCatalog.ProjectIcon,
                BuildPrompt(projectName, userHint), ownerId: ownerId, jsonFormat: "json", ct: ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Значок проекта «{Name}»: модель не ответила", projectName);
            return ProjectIconGlyphResult.NoModel;
        }

        var result = Parse(raw);
        if (!result.Ok)
            // Warning, а не Information: файловый лог прода режет Information, а причина
            // отказа — единственная зацепка при «значок не подобрался», она обязана
            // доходить до лога вместе с именем проекта
            log.LogWarning("Значок проекта «{Name}»: ответ модели отвергнут ({Reason})",
                projectName, result.FailReason);
        return result;
    }

    /// <summary>
    /// Разбор и валидация ответа модели (ADR-009 §2.2, §5). Публичен для тестов и для
    /// повторной валидации в icon/select: клиент — такой же недоверенный источник, как
    /// модель, и валидация стоит на входе в стор, а не на выходе модели.
    /// </summary>
    public static ProjectIconGlyphResult Parse(string? raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is null) return ProjectIconGlyphResult.BadJson;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("glyphs", out var glyphsEl)
                || glyphsEl.ValueKind != JsonValueKind.Array)
                return ProjectIconGlyphResult.BadJson;

            var candidates = new List<ProjectIconGlyphCandidate>();
            string? failReason = null;
            foreach (var el in glyphsEl.EnumerateArray())
            {
                if (candidates.Count >= MaxCandidates) break;   // грид фиксированный
                if (TryReadGlyph(el, out var candidate, out var reason)) candidates.Add(candidate!);
                // Причина пустого итога — отказ первого негодного кандидата: модель
                // обычно повторяет одну и ту же ошибку, а логу нужна конкретика
                else failReason ??= reason;
            }
            if (candidates.Count > 0) return new ProjectIconGlyphResult(candidates, null);
            return failReason is null ? ProjectIconGlyphResult.NoGlyphs
                : new ProjectIconGlyphResult([], failReason);
        }
        catch (JsonException)
        {
            return ProjectIconGlyphResult.BadJson;
        }
    }

    /// <summary>
    /// Валидация одного значка по данным (не по JSON) — точка входа повторной проверки
    /// icon/select. Годен только кандидат с именем из белого списка; иначе null.
    /// </summary>
    public static ProjectIconGlyphCandidate? ValidateGlyph(string? name)
        => TryValidateName(name?.Trim(), out var named, out _) ? named : null;

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

    // Промпт: просим имена готовых иконок, а не разметку; запрет на paths перечислен явно,
    // чтобы модель не тратила бюджет вывода на вырезанный вид ответа
    private static string BuildPrompt(string projectName, string? userHint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты подбираешь значок для проекта — маленькую контурную иконку в стиле штриховых");
        sb.AppendLine("иконок интерфейса. Значок — ТОЛЬКО имя готовой иконки из набора lucide: рисование");
        sb.AppendLine("собственных путей не поддерживается, любое узнаваемое изображение уже есть в наборе.");
        sb.AppendLine();
        sb.AppendLine($"Проект называется «{Truncate(projectName.Trim(), PromptNameBudget)}».");
        var hint = userHint?.Trim();
        if (!string.IsNullOrEmpty(hint))
            sb.AppendLine($"Что изобразить (пожелание владельца): {Truncate(hint, PromptHintBudget)}");
        sb.AppendLine();
        sb.AppendLine($"Предложи {AskGlyphs} РАЗНЫХ значков по смыслу. Ответь ТОЛЬКО JSON-объектом такого вида,");
        sb.AppendLine("без пояснений и без markdown-обвязки:");
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
        // Полный список (~2000 имён, ≈26 КБ) в промпт не уходит (ADR-009 §5.3): список такого
        // размера модель не читает, а ищет в нём похожее, теряя смысл названия. Имя модель
        // называет по своему знанию lucide, членство проверяет сервер; образцы дают формат
        // и типовую лексику, предупреждение — стимул не угадывать редкое
        sb.AppendLine("- name — имя готовой иконки из набора lucide (контурные иконки интерфейсов, ~2000 имён),");
        sb.AppendLine("  с точностью до символа, в kebab-case. Примеры имён: piggy-bank, wallet, chart-line, house,");
        sb.AppendLine("  rocket, dumbbell, shopping-cart, plane, graduation-cap, stethoscope, gamepad-2, sparkles.");
        sb.AppendLine("  Полного списка здесь нет — называй иконки, в существовании которых уверен; учти переименования");
        sb.AppendLine("  новой версии набора (house, а не home; chart-line, а не line-chart). Имя не из набора");
        sb.AppendLine("  отбрасывается — лучше заведомо существующая иконка, чем редкая наугад.");
        sb.AppendLine("- никаких paths и никаких нарисованных путей в ответе — ТОЛЬКО имена из набора lucide;");
        sb.AppendLine("  НИКАКОЙ SVG-разметки, fill, stroke, style, text и transform — цвет и толщину линий");
        sb.AppendLine("  задаёт сам продукт.");
        return sb.ToString();
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
