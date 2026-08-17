using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.ProjectIcons;

/// <summary>
/// Кандидат значка: заполнено РОВНО ОДНО из полей (ADR-009 §2.2). Name — имя иконки из
/// белого списка <see cref="LucideGlyphs"/> (компонент рисует фронт); Paths — нарисованные
/// моделью строки d, разметку из них собирает <see cref="GlyphSvg"/>.
/// </summary>
public sealed record ProjectIconGlyphCandidate(string? Name, IReadOnlyList<string>? Paths)
{
    public bool IsNamed => Name is not null;
}

/// <summary>
/// Итог подбора: 0–4 кандидатов либо причина отказа. Пустой результат — не ошибка, а
/// фолбэк: вызывающий оставляет проект на инициалах (ADR-009 §7).
/// </summary>
public sealed record ProjectIconGlyphResult(IReadOnlyList<ProjectIconGlyphCandidate> Candidates, string? FailReason)
{
    public bool Ok => Candidates.Count > 0;

    public static readonly ProjectIconGlyphResult NoModel = new([], "no-model");
    public static readonly ProjectIconGlyphResult BadJson = new([], "bad-json");
    public static readonly ProjectIconGlyphResult Rejected = new([], "rejected");
}

/// <summary>
/// Белый список имён lucide (ADR-009 §5): серверная копия ключей карты GLYPHS фронта
/// (frontend/src/lib/projectGlyphs.ts). Равенство множеств держит тест-сторож; пополнение —
/// обычный коммит в оба места. Порядок доменный: в таком виде список уходит в промпт,
/// модели проще держать его блоками по смыслу.
/// </summary>
public static class LucideGlyphs
{
    public static readonly IReadOnlyList<(string Label, IReadOnlyList<string> Names)> Domains =
    [
        ("дом и быт", ["house", "sofa", "bed", "key", "wrench", "hammer", "plug", "lightbulb"]),
        ("деньги", ["wallet", "piggy-bank", "banknote", "credit-card", "receipt", "coins"]),
        ("аналитика", ["chart-line", "chart-pie", "chart-column", "table", "gauge", "target"]),
        ("код", ["code", "terminal", "git-branch", "database", "server", "cpu", "bug", "boxes"]),
        ("учёба", ["book", "book-open", "graduation-cap", "pencil", "notebook-pen", "brain"]),
        ("здоровье", ["heart", "activity", "dumbbell", "stethoscope", "pill", "apple", "leaf"]),
        ("еда", ["utensils", "coffee", "chef-hat", "shopping-cart", "cake"]),
        ("дорога", ["plane", "car", "train-front", "bike", "map", "map-pin", "compass", "tent"]),
        ("медиа", ["camera", "image", "film", "music", "mic", "headphones", "palette", "brush"]),
        ("работа", ["briefcase", "building-2", "store", "factory", "calendar", "clock", "users"]),
        ("наука", ["rocket", "atom", "flask-conical", "microscope", "telescope"]),
        ("досуг", ["gamepad-2", "puzzle", "trophy", "dice-5", "flag", "star", "sparkles"]),
        ("прочее", ["folder", "file-text", "layers", "shield", "lock", "globe", "bot", "zap"]),
    ];

    public static readonly IReadOnlyList<string> Names = [.. Domains.SelectMany(g => g.Names)];

    public static readonly HashSet<string> All = new(Names, StringComparer.Ordinal);

    public static bool Contains(string? name) => name is not null && All.Contains(name);
}

/// <summary>
/// Сборка разметки значка из провалидированных путей — единственная точка, где вообще
/// существует SVG-документ значка (ADR-009 §4). Шаблон повторяет ICON_PROPS фронта:
/// только штрих, толщина 2, скругления, currentColor — значок красится цветом проекта
/// на клиенте и перекрашивается при смене цвета/темы без перегенерации.
/// </summary>
public static class GlyphSvg
{
    public const int ViewBox = 24;

    public static string Build(IReadOnlyList<string> paths)
    {
        // Вход обязан пройти валидатор путей; XmlWriter — второй пояс: даже при баге
        // валидатора значение уедет в атрибут экранированным и не породит нового тега
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            Encoding = new UTF8Encoding(false),
        };
        using (var w = XmlWriter.Create(sb, settings))
        {
            w.WriteStartElement("svg", "http://www.w3.org/2000/svg");
            w.WriteAttributeString("width", ViewBox.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("height", ViewBox.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("viewBox", $"0 0 {ViewBox} {ViewBox}");
            w.WriteAttributeString("fill", "none");
            w.WriteAttributeString("stroke", "currentColor");
            w.WriteAttributeString("stroke-width", "2");
            w.WriteAttributeString("stroke-linecap", "round");
            w.WriteAttributeString("stroke-linejoin", "round");
            foreach (var d in paths)
            {
                w.WriteStartElement("path");
                w.WriteAttributeString("d", d);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        return sb.ToString();
    }
}

/// <summary>
/// Подбор значка проекта (ADR-009): дешёвый текстовый ход по названию проекта и
/// необязательному пожеланию владельца → JSON с кандидатами двух видов → валидация →
/// до четырёх кандидатов вперемешку, нарисованные и из набора. Разметки от модели не
/// существует: имя сверяется с белым списком, пути — с алфавитом и лимитами, SVG собирает
/// <see cref="GlyphSvg"/>. Любой сбой — пустой результат, проект остаётся на инициалах.
/// </summary>
public sealed class ProjectIconGlyphService(
    ICheapTextRunner cheap, ILogger<ProjectIconGlyphService> log)
{
    // Лимиты контракта ADR-009 §2.2: просим шесть, показываем четыре — после отсева
    // негодных кандидатов должно остаться из чего выбирать.
    public const int AskGlyphs = 6;
    public const int MaxCandidates = 4;
    public const int MaxPathsPerGlyph = 4;
    public const int MaxPathLength = 256;
    public const int MaxPathsTotalLength = 768;
    private const int MaxCommandsPerPath = 24;
    // Габарит (ADR-009 §3.4): допуск за край холста — под контрольные точки C/Q и радиусы A
    private const double MinCoord = -4, MaxCoord = 28;

    // В модель уходят только имя проекта и пожелание: место может работать сторонним
    // провайдером, и значок не стоит расширения поверхности утечки (ADR-009 §2.1)
    private const int PromptNameBudget = 120;
    private const int PromptHintBudget = 200;

    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{1,39}$", RegexOptions.Compiled);

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
            log.LogInformation("Значок проекта «{Name}»: ответ модели отвергнут ({Reason})",
                projectName, result.FailReason);
        return result;
    }

    /// <summary>
    /// Разбор и валидация ответа модели (ADR-009 §2.2, §3). Публичен для тестов и для
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
            foreach (var el in glyphsEl.EnumerateArray())
            {
                if (candidates.Count >= MaxCandidates) break;   // грид фиксированный
                if (TryReadGlyph(el, out var candidate)) candidates.Add(candidate!);
            }
            return candidates.Count == 0
                ? ProjectIconGlyphResult.Rejected
                : new ProjectIconGlyphResult(candidates, null);
        }
        catch (JsonException)
        {
            return ProjectIconGlyphResult.BadJson;
        }
    }

    /// <summary>
    /// Валидация одного значка по данным (не по JSON) — точка входа повторной проверки
    /// icon/select. Заполнено ровно одно из name/paths; иначе null.
    /// </summary>
    public static ProjectIconGlyphCandidate? ValidateGlyph(string? name, IEnumerable<string?>? paths)
    {
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasPaths = paths is not null;
        if (hasName == hasPaths) return null;
        return hasName
            ? TryValidateName(name!.Trim(), out var named) ? named : null
            : TryValidatePaths(paths!, out var drawn) ? drawn : null;
    }

    private static bool TryReadGlyph(JsonElement el, out ProjectIconGlyphCandidate? glyph)
    {
        glyph = null;
        if (el.ValueKind != JsonValueKind.Object) return false;

        var hasName = el.TryGetProperty("name", out var nameEl)
            && nameEl.ValueKind == JsonValueKind.String;
        var hasPaths = el.TryGetProperty("paths", out var pathsEl)
            && pathsEl.ValueKind == JsonValueKind.Array;
        // Оба поля сразу или ни одного — не тот ответ (ADR-009 §2.2)
        if (hasName == hasPaths) return false;

        return hasName
            ? TryValidateName(nameEl.GetString(), out glyph)
            : TryValidatePaths(ReadStrings(pathsEl), out glyph);
    }

    private static bool TryValidateName(string? name, out ProjectIconGlyphCandidate? glyph)
    {
        glyph = null;
        var n = name?.Trim();
        // Форма ключа + членство в белом списке: имя — единственное, что модель может
        // сообщить для этого вида, и оно обязано существовать в карте фронта (ADR-009 §5)
        if (n is null || !NamePattern.IsMatch(n) || !LucideGlyphs.Contains(n)) return false;
        glyph = new ProjectIconGlyphCandidate(n, null);
        return true;
    }

    private static bool TryValidatePaths(IEnumerable<string?> rawPaths, out ProjectIconGlyphCandidate? glyph)
    {
        glyph = null;
        var paths = new List<string>();
        var total = 0;
        foreach (var p in rawPaths)
        {
            // Сверх лимита — не «обрежем хвост», а отбракуем кандидата: это не забытое
            // поле, а не тот ответ (ADR-009 §2.2)
            if (paths.Count >= MaxPathsPerGlyph) return false;
            var d = p?.Trim();
            if (string.IsNullOrEmpty(d) || d.Length > MaxPathLength) return false;
            if (!IsValidPath(d)) return false;
            total += d.Length;
            if (total > MaxPathsTotalLength) return false;
            paths.Add(d);
        }
        if (paths.Count == 0) return false;
        glyph = new ProjectIconGlyphCandidate(null, paths);
        return true;
    }

    private static List<string?> ReadStrings(JsonElement array)
    {
        var result = new List<string?>();
        foreach (var el in array.EnumerateArray())
            result.Add(el.ValueKind == JsonValueKind.String ? el.GetString() : null);
        return result;
    }

    /// <summary>
    /// Проверка строки d по ADR-009 §3: алфавит команд, форма чисел, синтаксис (арности,
    /// не больше <see cref="MaxCommandsPerPath"/> команд) и габарит (каждое число в
    /// [-4, 28]). Экспонента запрещена — единственный дешёвый способ получить координату
    /// в миллион парой символов.
    /// </summary>
    public static bool IsValidPath(string? d)
    {
        if (string.IsNullOrWhiteSpace(d) || d.Length > MaxPathLength) return false;

        // (1) Алфавит: буквы команд, цифры, знак, точка, разделители — и ничего больше
        foreach (var c in d)
            if (!IsCommand(c) && !char.IsAsciiDigit(c)
                && c is not ('.' or '-' or '+' or ' ' or ',' or '\t' or '\r' or '\n'))
                return false;

        var i = 0;
        while (i < d.Length && IsSeparator(d[i])) i++;
        if (i >= d.Length || (d[i] != 'M' && d[i] != 'm')) return false;

        var command = '\0';
        var args = 0;
        var commands = 0;
        while (i < d.Length)
        {
            if (IsSeparator(d[i])) { i++; continue; }

            if (IsCommand(d[i]))
            {
                if (command != '\0' && !ArityOk(command, args)) return false;
                command = d[i];
                args = 0;
                if (++commands > MaxCommandsPerPath) return false;
                i++;
                continue;
            }

            if (command == '\0' || Arity(command) == 0) return false;   // число до команды либо аргумент у Z
            if (!TryReadPathNumber(d, ref i, out var value)) return false;
            if (value < MinCoord || value > MaxCoord) return false;
            args++;
        }
        return command != '\0' && ArityOk(command, args);
    }

    // Форма числа — [+-]?\d{1,2}(\.\d{1,2})?: холст 24 единицы, третий знак до или после
    // точки неразличим на экране, а «хвосты» вида 11.99999 только раздувают строку
    private static bool TryReadPathNumber(string d, ref int i, out double value)
    {
        value = 0;
        var start = i;
        if (d[i] is '+' or '-') i++;

        var intDigits = 0;
        while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; intDigits++; }
        if (intDigits is 0 or > 2) return false;

        if (i < d.Length && d[i] == '.')
        {
            i++;
            var frac = 0;
            while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; frac++; }
            if (frac is 0 or > 2) return false;
        }

        // Числа — всегда инвариантной культурой: запятая вместо точки в ru-RU молча
        // ломает d (грабля ADR-008 §4.3, на Windows-хосте это не гипотеза)
        return double.TryParse(d.AsSpan(start, i - start), NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);
    }

    private static bool IsSeparator(char c) => c is ' ' or ',' or '\t' or '\r' or '\n';

    private static bool IsCommand(char c) =>
        c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c'
            or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

    private static int Arity(char c) => char.ToUpperInvariant(c) switch
    {
        'M' or 'L' or 'T' => 2,
        'H' or 'V' => 1,
        'C' => 6,
        'S' or 'Q' => 4,
        'A' => 7,
        _ => 0,   // Z/z
    };

    // Число аргументов кратно арности команды (повторы допустимы: «M0 0 5 5» = M + L)
    private static bool ArityOk(char command, int args)
    {
        var arity = Arity(command);
        return arity == 0 ? args == 0 : args > 0 && args % arity == 0;
    }

    // Промпт: просим ДАННЫЕ, а не разметку; запреты перечислены явно, чтобы модель не
    // тратила на них бюджет вывода — схема их всё равно не принимает
    private static string BuildPrompt(string projectName, string? userHint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты подбираешь значок для проекта — маленькую контурную иконку в стиле штриховых");
        sb.AppendLine("иконок интерфейса. У значка два равноправных источника: готовая иконка из набора");
        sb.AppendLine("(назвать имя) или своя (нарисовать списком путей). Узнаваемая вещь почти наверняка");
        sb.AppendLine("есть в наборе — называй имя; специфическое рисуй сам.");
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
                { "name": "chart-line" },
                { "paths": ["M3 21h18", "M6 21V9l6-5 6 5v12", "M10 21v-6h4v6"] }
              ]
            }
            """);
        sb.AppendLine();
        sb.AppendLine("Правила:");
        sb.AppendLine("- у каждого элемента заполнено РОВНО ОДНО поле: name или paths; оба или ни одного — отбраковка.");
        sb.AppendLine("- name — имя иконки из набора, с точностью до символа. Допустимые имена (только они):");
        foreach (var (label, names) in LucideGlyphs.Domains)
            sb.AppendLine($"    {label}: {string.Join(", ", names)}");
        sb.AppendLine($"- paths — от 1 до {MaxPathsPerGlyph} строк атрибута d, каждая не длиннее {MaxPathLength} символов и");
        sb.AppendLine($"  суммарно не больше {MaxPathsTotalLength}; координаты в квадрате viewBox 0 0 24 24, каждая от -4 до 28;");
        sb.AppendLine("  первая команда — M; команды только M m L l H h V v C c S s Q q T t A a Z z; числа вида 12 или");
        sb.AppendLine($"  12.5 (до двух цифр до и после точки, без экспоненты); не больше {MaxCommandsPerPath} команд в строке.");
        sb.AppendLine("- Только контур: НИКАКИХ fill, stroke, opacity, style, text, transform и вообще никакой SVG-разметки");
        sb.AppendLine("  в ответе — цвет и толщину линий задаёт сам продукт.");
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
