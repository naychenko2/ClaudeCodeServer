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
/// фолбэк: вызывающий оставляет проект на инициалах (ADR-009 §7). FailReason — код
/// класса отказа, при полезных деталях — со значением после «:»:
/// no-model (модель не ответила), bad-json (ответ не разобрался как JSON), no-glyphs
/// (пустой набор), glyph-shape:* (элемент не того вида), name-out:{имя} (имя вне
/// белого списка), path-*:{значение}{граница} (путь не прошёл лимит либо проверку —
/// с указанием, какой лимит и какое значение его нарушило, например path-length:300&gt;256).
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
    // 12 по живой выборке сильной модели (17.08): узнаваемые вещи из многих штрихов
    // («самовар» — 7/9/12 путей) гибли целиком на прежних четырёх, обесценивая ветку
    // рисования. Суммарный вес всё равно держит MaxPathsTotalLength.
    public const int MaxPathsPerGlyph = 12;
    public const int MaxPathLength = 256;
    public const int MaxPathsTotalLength = 768;
    private const int MaxCommandsPerPath = 24;
    // Габарит (ADR-009 §3.4): допуск за край холста — под контрольные точки C/Q и радиусы A.
    // Проверяются ФАКТИЧЕСКИЕ точки (для относительных команд — после применения сдвига),
    // а не каждое число строки: модель законно пишет дельты вроде l6-5, чьи точки в холсте
    public const double MinCoord = -4, MaxCoord = 28;

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
    /// icon/select. Заполнено ровно одно из name/paths; иначе null.
    /// </summary>
    public static ProjectIconGlyphCandidate? ValidateGlyph(string? name, IEnumerable<string?>? paths)
    {
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasPaths = paths is not null;
        if (hasName == hasPaths) return null;
        return hasName
            ? TryValidateName(name!.Trim(), out var named, out _) ? named : null
            : TryValidatePaths(paths!, out var drawn, out _) ? drawn : null;
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

        var hasName = el.TryGetProperty("name", out var nameEl)
            && nameEl.ValueKind == JsonValueKind.String;
        var hasPaths = el.TryGetProperty("paths", out var pathsEl)
            && pathsEl.ValueKind == JsonValueKind.Array;
        // Оба поля сразу или ни одного — не тот ответ (ADR-009 §2.2)
        if (hasName == hasPaths)
        {
            reason = hasName ? "glyph-shape:both" : "glyph-shape:none";
            return false;
        }

        return hasName
            ? TryValidateName(nameEl.GetString(), out glyph, out reason)
            : TryValidatePaths(ReadStrings(pathsEl), out glyph, out reason);
    }

    private static bool TryValidateName(string? name, out ProjectIconGlyphCandidate? glyph, out string? reason)
    {
        glyph = null;
        reason = null;
        var n = name?.Trim();
        // Форма ключа + членство в белом списке: имя — единственное, что модель может
        // сообщить для этого вида, и оно обязано существовать в наборе установленного
        // lucide-react (ADR-009 §5). Нарушение формы и промах по списку — один класс:
        // имя негодно, показываем какое
        if (n is null || !NamePattern.IsMatch(n) || !LucideGlyphs.Contains(n))
        {
            reason = $"name-out:{Truncate(n ?? "(пусто)", 60)}";
            return false;
        }
        glyph = new ProjectIconGlyphCandidate(n, null);
        return true;
    }

    private static bool TryValidatePaths(IEnumerable<string?> rawPaths, out ProjectIconGlyphCandidate? glyph, out string? reason)
    {
        glyph = null;
        reason = null;
        var list = rawPaths as IList<string?> ?? rawPaths.ToList();
        // Сверх лимита — не «обрежем хвост», а отбракуем кандидата: это не забытое
        // поле, а не тот ответ (ADR-009 §2.2). Причина называет лимит и значение
        if (list.Count > MaxPathsPerGlyph)
        {
            reason = $"path-count:{list.Count}>{MaxPathsPerGlyph}";
            return false;
        }

        var paths = new List<string>();
        var total = 0;
        foreach (var p in list)
        {
            var d = p?.Trim();
            if (string.IsNullOrEmpty(d)) { reason = "path-empty"; return false; }
            if (d.Length > MaxPathLength)
            {
                reason = $"path-length:{d.Length}>{MaxPathLength}";
                return false;
            }
            if (!TryValidatePath(d, out reason)) return false;
            total += d.Length;
            if (total > MaxPathsTotalLength)
            {
                reason = $"path-total:{total}>{MaxPathsTotalLength}";
                return false;
            }
            paths.Add(d);
        }
        if (paths.Count == 0) { reason = "no-paths"; return false; }
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
    /// не больше <see cref="MaxCommandsPerPath"/> команд) и габарит. Габарит считается
    /// по ФАКТИЧЕСКИМ точкам пути (для относительных команд — после применения сдвига),
    /// включая контрольные точки C/S/Q/T; радиусы дуги A — отдельный диапазон [0, 28].
    /// Экспонента запрещена — единственный дешёвый способ получить координату
    /// в миллион парой символов.
    /// </summary>
    public static bool IsValidPath(string? d) => TryValidatePath(d, out _);

    // Та же проверка с причиной отказа: код path-* называет, что именно не прошло, —
    // по лимитам ещё и значение с границей (path-length:300&gt;256, path-coord:29&gt;28)
    private static bool TryValidatePath(string? d, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(d)) { reason = "path-empty"; return false; }
        if (d.Length > MaxPathLength)
        {
            reason = $"path-length:{d.Length}>{MaxPathLength}";
            return false;
        }

        // (1) Алфавит: буквы команд, цифры, знак, точка, разделители — и ничего больше
        foreach (var c in d)
            if (!IsCommand(c) && !char.IsAsciiDigit(c)
                && c is not ('.' or '-' or '+' or ' ' or ',' or '\t' or '\r' or '\n'))
            {
                reason = $"path-char:{DescribeChar(c)}";
                return false;
            }

        var i = 0;
        while (i < d.Length && IsSeparator(d[i])) i++;
        if (i >= d.Length || (d[i] != 'M' && d[i] != 'm'))
        {
            reason = i >= d.Length ? "path-start" : $"path-start:{d[i]}";
            return false;
        }

        // Трекинг фактических точек: (Cx,Cy) — текущая, (Sx,Sy) — старт субпоя (возврат Z),
        // (Pcx,Pcy) — контрольная точка прошлой C/S/Q/T для отражения в S/T
        var cur = new PathCursor();

        var command = '\0';
        var args = 0;       // числа в буфере текущего шага
        var seen = 0;       // все числа текущей команды (шаги обнуляют args, но не seen)
        var commands = 0;
        var buf = new double[7];
        var firstStep = true;   // повторные пары M по спеке SVG — неявные L
        while (i < d.Length)
        {
            if (IsSeparator(d[i])) { i++; continue; }

            if (IsCommand(d[i]))
            {
                // Неполный шаг предыдущей (args != 0) либо команда с аргументами, не
                // получившая ни одного числа (seen == 0)
                if (command != '\0' && (args != 0 || (seen == 0 && Arity(command) > 0)))
                {
                    reason = $"path-arity:{command}";
                    return false;
                }
                command = d[i];
                args = 0;
                seen = 0;
                firstStep = true;
                if (++commands > MaxCommandsPerPath)
                {
                    reason = $"path-commands:{commands}>{MaxCommandsPerPath}";
                    return false;
                }
                i++;
                continue;
            }

            // Число до команды либо аргумент у Z — та же негодная арность, команды ещё нет
            if (command == '\0' || Arity(command) == 0) { reason = "path-arity"; return false; }
            var start = i;
            if (!TryReadPathNumber(d, ref i, out var value))
            {
                reason = $"path-number:{NumberToken(d, start)}";
                return false;
            }
            buf[args++] = value;
            seen++;
            // Набралась полная арность — шаг; повторные аргументы идут следующим шагом
            if (args == Arity(command))
            {
                if (!ApplyStep(command, buf, firstStep, cur))
                {
                    reason = cur.Reason;
                    return false;
                }
                args = 0;
                firstStep = false;
            }
        }
        // Хвост: неполный шаг или команда без аргументов
        if (command == '\0' || args != 0 || (seen == 0 && Arity(command) > 0))
        {
            reason = command == '\0' ? "path-start" : $"path-arity:{command}";
            return false;
        }
        return true;
    }

    // Один шаг команды: buf — её аргументы, начиная с buf[0]. Смещает (cx,cy) и проверяет
    // габарит всех точек шага. Относительные (строчные) команды берут базой текущую точку.
    // firstStep — первая пара этой команды: повторные пары M по спеке SVG суть неявные L
    // и старт субпоя (адресат Z) не двигают.
    private static bool ApplyStep(char command, double[] buf, bool firstStep, PathCursor cur)
    {
        var rel = char.IsLower(command);
        double X(double v) => rel ? cur.Cx + v : v;
        double Y(double v) => rel ? cur.Cy + v : v;

        switch (char.ToUpperInvariant(command))
        {
            case 'M' or 'L':
            {
                var x = X(buf[0]); var y = Y(buf[1]);
                if (!cur.PointOk(x, y)) return false;
                // Настоящий M открывает субпой (адресат возврата Z); повторные пары M —
                // неявные L и старт не двигают
                if (command is 'M' or 'm' && firstStep) { cur.Sx = x; cur.Sy = y; }
                cur.Cx = x; cur.Cy = y;
                cur.HasCtrl = false;
                return true;
            }
            case 'H':
            {
                var x = X(buf[0]);
                if (!cur.PointOk(x, cur.Cy)) return false;
                cur.Cx = x;
                cur.HasCtrl = false;
                return true;
            }
            case 'V':
            {
                var y = Y(buf[0]);
                if (!cur.PointOk(cur.Cx, y)) return false;
                cur.Cy = y;
                cur.HasCtrl = false;
                return true;
            }
            case 'C':
            {
                // Обе контрольные и опорная: сдвиги даже у контрольных — от текущей точки
                var x1 = X(buf[0]); var y1 = Y(buf[1]);
                var x2 = X(buf[2]); var y2 = Y(buf[3]);
                var x3 = X(buf[4]); var y3 = Y(buf[5]);
                if (!cur.PointOk(x1, y1) || !cur.PointOk(x2, y2) || !cur.PointOk(x3, y3)) return false;
                cur.Pcx = x2; cur.Pcy = y2; cur.Cx = x3; cur.Cy = y3;
                cur.HasCtrl = true;
                return true;
            }
            case 'S':
            {
                // Неявная первая контрольная — отражение прошлой; вычисленная точка тоже
                // проверяем: далеко торчащее плечо кривой так же режет вьюпорт
                var rx = cur.HasCtrl ? 2 * cur.Cx - cur.Pcx : cur.Cx;
                var ry = cur.HasCtrl ? 2 * cur.Cy - cur.Pcy : cur.Cy;
                var x2 = X(buf[0]); var y2 = Y(buf[1]);
                var x3 = X(buf[2]); var y3 = Y(buf[3]);
                if (!cur.PointOk(rx, ry) || !cur.PointOk(x2, y2) || !cur.PointOk(x3, y3)) return false;
                cur.Pcx = x2; cur.Pcy = y2; cur.Cx = x3; cur.Cy = y3;
                cur.HasCtrl = true;
                return true;
            }
            case 'Q':
            {
                var x1 = X(buf[0]); var y1 = Y(buf[1]);
                var x2 = X(buf[2]); var y2 = Y(buf[3]);
                if (!cur.PointOk(x1, y1) || !cur.PointOk(x2, y2)) return false;
                cur.Pcx = x1; cur.Pcy = y1; cur.Cx = x2; cur.Cy = y2;
                cur.HasCtrl = true;
                return true;
            }
            case 'T':
            {
                var rx = cur.HasCtrl ? 2 * cur.Cx - cur.Pcx : cur.Cx;
                var ry = cur.HasCtrl ? 2 * cur.Cy - cur.Pcy : cur.Cy;
                var x = X(buf[0]); var y = Y(buf[1]);
                if (!cur.PointOk(rx, ry) || !cur.PointOk(x, y)) return false;
                cur.Pcx = rx; cur.Pcy = ry; cur.Cx = x; cur.Cy = y;
                cur.HasCtrl = true;
                return true;
            }
            case 'A':
            {
                // Радиусы — неотрицательные и в габарите; флаги large-arc/sweep — строго 0/1
                // (спека SVG: иное значение делает путь невалидным для рендера)
                if (!RadiusOk(buf[0]) || !RadiusOk(buf[1])) return false;
                if (buf[3] is not (0 or 1)) { cur.Reason = $"path-arc-flag:{FmtCoord(buf[3])}"; return false; }
                if (buf[4] is not (0 or 1)) { cur.Reason = $"path-arc-flag:{FmtCoord(buf[4])}"; return false; }
                var x = X(buf[5]); var y = Y(buf[6]);
                if (!cur.PointOk(x, y)) return false;
                cur.Cx = x; cur.Cy = y;
                cur.HasCtrl = false;
                return true;
            }
            default:   // Z/z — арность 0, сюда не попадаем
                return true;
        }

        bool RadiusOk(double r)
        {
            if (r < 0) { cur.Reason = $"path-radius:{FmtCoord(r)}<0"; return false; }
            if (r > MaxCoord) { cur.Reason = $"path-radius:{FmtCoord(r)}>{MaxCoord}"; return false; }
            return true;
        }
    }

    // Позиция прохода по d: текущая точка, старт субпоя (адресат Z), контрольная точка
    // прошлой C/S/Q/T (для отражения в S/T). Reason — причина отказа шага.
    private sealed class PathCursor
    {
        public double Cx, Cy, Sx, Sy, Pcx, Pcy;
        public bool HasCtrl;
        public string? Reason;

        // Габарит фактической точки (ADR-009 §3.4): допуск за края холста — под
        // контрольные точки кривых; всё, что дальше, — ошибка масштаба или раздувание
        public bool PointOk(double x, double y)
        {
            if (x < MinCoord) { Reason = $"path-coord:{FmtCoord(x)}<{MinCoord}"; return false; }
            if (x > MaxCoord) { Reason = $"path-coord:{FmtCoord(x)}>{MaxCoord}"; return false; }
            if (y < MinCoord) { Reason = $"path-coord:{FmtCoord(y)}<{MinCoord}"; return false; }
            if (y > MaxCoord) { Reason = $"path-coord:{FmtCoord(y)}>{MaxCoord}"; return false; }
            return true;
        }
    }

    // Управляющий символ в причине — по коду, прочие — как есть
    private static string DescribeChar(char c) => char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString();

    // Подстрока-нарушитель для path-number: от начала числа до разделителя/команды
    private static string NumberToken(string d, int start)
    {
        var end = start;
        while (end < d.Length && !IsSeparator(d[end]) && !IsCommand(d[end]) && end - start < 12) end++;
        return d[start..end];
    }

    private static string FmtCoord(double value) => value.ToString(CultureInfo.InvariantCulture);

    // Форма числа — [+-]?(\d{1,2}(\.\d{1,2})?|\.\d{1,2}): холст 24 единицы, третий знак
    // до или после точки неразличим на экране, а «хвосты» вида 11.99999 только раздувают
    // строку. Ведущая точка (.5) — законная компактная запись SVG, модели нравится
    private static bool TryReadPathNumber(string d, ref int i, out double value)
    {
        value = 0;
        var start = i;
        if (d[i] is '+' or '-') i++;

        var intDigits = 0;
        while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; intDigits++; }
        if (intDigits > 2) return false;

        if (i < d.Length && d[i] == '.')
        {
            i++;
            var frac = 0;
            while (i < d.Length && char.IsAsciiDigit(d[i])) { i++; frac++; }
            // «5.» без дробной части и голая точка — не числа
            if (frac is 0 or > 2) return false;
        }
        else if (intDigits == 0) return false;

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
        sb.AppendLine($"- paths — от 1 до {MaxPathsPerGlyph} строк атрибута d, каждая не длиннее {MaxPathLength} символов и");
        sb.AppendLine($"  суммарно не больше {MaxPathsTotalLength}; все точки контура и контрольные точки кривых лежат");
        sb.AppendLine($"  в квадрате viewBox 0 0 24 24 с допуском: каждая от {FmtCoord(MinCoord)} до {FmtCoord(MaxCoord)}");
        sb.AppendLine("  (для относительных команд — точка ПОСЛЕ применения сдвига; сами сдвига с любым знаком);");
        sb.AppendLine("  первая команда — M; команды только M m L l H h V v C c S s Q q T t A a Z z; числа вида 12, 12.5");
        sb.AppendLine($"  или .5 (до двух цифр до и после точки, без экспоненты); не больше {MaxCommandsPerPath} команд в строке.");
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
