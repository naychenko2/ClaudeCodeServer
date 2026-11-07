using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож инлайн-сравнений владельца с идентификатором вызывающего в точках доступа.
///
/// Зачем: фича «Общие проекты» (план docs/research/shared-projects-plan-2026-10.md, шаг S0-G)
/// переводит точки доступа на `IProjectAccess`/`RequestScope` (шаги S0-4…S0-7, S2a-3). Каждое
/// оставленное инлайн-сравнение — точка, где проверка «по вызывающему» молча переживёт перевод
/// и в общем проекте станет дырой (разрез §6.6, карта S0-3 §0 п. 5). Allow-list
/// <c>OwnerIdInlineAllowList.txt</c> — снимок кода этого дерева по состоянию на шаг S0-G
/// (составлен по таблице 2 карты S0-3, расширенной вердиктом А1): он может только
/// сокращаться по мере перевода. Новый файл или новое сравнение вне снимка — красное:
/// сравнение обязано быть названо и оценено человеком, а не тихой правкой.
///
/// Охват — как в карте S0-3 §2 с правкой А1.2: сегменты путей `Controllers`, `Mcp`
/// (включая `Services/Mcp`) и `Hubs` внутри `backend/`, плюс файлы `*Toolset.cs` независимо
/// от пути (`ArchitectureToolset` лежит вне `/Mcp/`). Тестовые проекты и TestKit исключены.
///
/// Что ищется (состав по А1.2, «просто посчитай по факту, что находит расширенный шаблон»):
/// 1. сравнение `X == Y`/`X != Y` или `string.Equals(X, Y)`, где хотя бы одна сторона —
///    обращение к свойству `.OwnerId`;
/// 2. двухшаговое сравнение: голое `ownerId`/`owner` против `UserId`/`userId`/
///    `context.OwnerId`/`CurrentUserId` (ключ достали из `ResolveOwnerId` или параметра —
///    сравнение уже не видно по `.OwnerId`);
/// 3. сравнение против результата `ResolveOwnerId(...)`.
/// Отсев по форме: комментарии; литералы `null/true/false`; голое слово, кончающееся на
/// `OwnerId` без точки (`ServiceOwnerId`, `expectedOwnerId`) — спец-идентификаторы служб и
/// OAuth-потоков, ровно те исключения, которые карта S0-3 §2 называет «не относящимися к
/// владельцу проекта/задачи».
///
/// Allow-list держится по ФАЙЛУ со счётчиком совпадений (решение А1.2: по номеру строки он
/// рассыпается при каждой правке). Строгий счётчик, а не «файл вообще»: он ловит и новую
/// строку в известном файле, и мёртвую строку снимка.
///
/// Allow-list читается с того же дерева, что сканируется: в CI (main-tree) и в worktree
/// снимок свой, и красное «строки расходятся» между деревьями — честный сигнал, а не шум.
/// </summary>
public class OwnerIdInlineGuardTests : IClassFixture<OwnerIdInlineGuardTests.RepoScan>
{
    private const string AllowListRelativePath =
        "backend/ClaudeHomeServer.Guards.Tests/Services/OwnerIdInlineAllowList.txt";

    // Сегменты путей, которые сторож считает точками доступа (карта S0-3 §2 + правка А1.2).
    private static readonly string[] ScopeSegments = ["Controllers", "Mcp", "Hubs"];

    // 1. Сравнение `X == Y` / `X != Y` между идентификаторами (операнд может быть цепочкой
    // с вызовами и `?.`/`!.`: `projects.GetById(id)?.OwnerId`). Операнд обязан начинаться с
    // буквы — одиночный оператор в тексте не матчится. Дальше фильтр: хотя бы одна сторона —
    // обращение к свойству `.OwnerId` (ловит и `found.OwnerId != userId`, и паттерн-связанные
    // `taskOwner != context.OwnerId` из `is { OwnerId: var taskOwner }`).
    private static readonly Regex OwnerComparisonPattern = new(
        @"\b(?<left>[A-Za-z_][A-Za-z0-9_.!?()]*)\s*(?<op>[!=]=)\s*(?<right>[A-Za-z_][A-Za-z0-9_.!?()]*)",
        RegexOptions.Compiled);

    // 1b. `string.Equals(a, b` — та же проверка в вызове вместо оператора.
    private static readonly Regex EqualsPattern = new(
        @"\bstring\.Equals\s*\(([^)]*)",
        RegexOptions.Compiled);

    // 2. Двухшаговое сравнение (А1.2): ключ достали раньше (`var ownerId = …`, параметр
    // метода), здесь он голый. Идентификаторы вызывающего — те же, что в таблице 2.
    private static readonly Regex TwoStagePattern = new(
        @"\b(?<left>ownerId|owner)\s*(?<op>[!=]=)\s*(?<right>UserId|userId|context\.OwnerId|CurrentUserId)\b",
        RegexOptions.Compiled);

    // 3. Сравнение против результата `ResolveOwnerId(...)` (А1.2): точка, где код уже сам
    // берёт арендатора и сверяет его с вызывающим — после S0-2 это должен быть `RequestScope`.
    private static readonly Regex ResolveOwnerPattern = new(
        @"\bResolveOwnerId\([^)]*\)\s*[!=]=\s*[A-Za-z_][A-Za-z0-9_.!?()]*",
        RegexOptions.Compiled);

    // Аргументы, которые не «идентификатор вызывающего»: голое слово, кончающееся на
    // `OwnerId` без точки (`ServiceOwnerId`, `expectedOwnerId`) — спец-идентификаторы служб
    // и OAuth-потоков; и литеральные ключевые слова (`p.OwnerId == null` — проверка
    // «свой/не свой», не сравнение с вызывающим). Оба отсева — по исключениям карты S0-3 §2.
    private static readonly Regex NotACallerIdentifier = new(
        @"^[A-Za-z0-9_]*OwnerId$",
        RegexOptions.Compiled);

    private static bool IsLiteralKeyword(string operand) =>
        operand is "null" or "true" or "false";

    private readonly RepoScan scan;

    public OwnerIdInlineGuardTests(RepoScan scan)
    {
        this.scan = scan;
    }

    [Fact]
    public void ИнлайнСравненияOwnerId_ВнеAllowList_Отсутствуют()
    {
        scan.Root.Should().NotBeNull("сторож обязан читать настоящее дерево репозитория");
        scan.AllowListError.Should().BeNull($"allow-list должен читаться ({scan.Root}/{AllowListRelativePath})");

        var code = CountByFile(scan.Findings);
        var outside = new List<(string Path, int Allowed, int Actual)>();
        foreach (var (path, actual) in code)
        {
            var allowed = scan.AllowCountByFile.TryGetValue(path, out var n) ? n : 0;
            if (actual > allowed) outside.Add((path, allowed, actual));
        }

        if (outside.Count == 0) return;

        var total = outside.Sum(o => o.Actual - o.Allowed);
        var sb = new StringBuilder();
        sb.AppendLine($"Инлайн-сравнений владельца с идентификатором вызывающего вне allow-list: {total} " +
                      $"(в {outside.Count} файлов).");
        sb.AppendLine("Allow-list — снимок по ФАЙЛУ со счётчиком совпадений (формат «путь|число», решение А1: " +
                      "по номеру строки он рассыпается при каждой правке). По шагам S0-4…S0-7/S2a-3 он может " +
                      "только сокращаться; новое сравнение — только осознанно внесённым в снимок, с вердиктом в дифе.");
        sb.AppendLine("Что делать: сравнение переведено на IProjectAccess/RequestScope — уменьшить или убрать " +
                      "строку снимка; сравнение осталось осознанно (личное/админское) — поднять счётчик с " +
                      "вердиктом; новое случайное — убрать из кода.");
        foreach (var (path, allowed, actual) in outside)
        {
            sb.AppendLine($"  • {path}: в коде {actual}, в снимке {allowed} — строка снимка: «{path}|{actual}»");
            foreach (var f in scan.Findings.Where(f => f.Path == path).Take(MaxExamplesPerFile))
                sb.AppendLine($"      :{f.Line} {f.Expr}");
            if (actual > MaxExamplesPerFile)
                sb.AppendLine($"      … ещё {actual - MaxExamplesPerFile}, показаны не все.");
        }

        Assert.Fail(sb.ToString());
    }

    [Fact]
    public void AllowList_НеСодержитСтрокКоторыхНетВКоде()
    {
        scan.Root.Should().NotBeNull("сторож обязан читать настоящее дерево репозитория");
        scan.AllowListError.Should().BeNull($"allow-list должен читаться ({scan.Root}/{AllowListRelativePath})");

        var code = CountByFile(scan.Findings);

        var stale = new List<(string Path, int Allowed, int Actual)>();
        foreach (var (path, allowed) in scan.AllowList)
        {
            var actual = code.TryGetValue(path, out var n) ? n : 0;
            if (actual < allowed) stale.Add((path, allowed, actual));
        }

        if (stale.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine($"Строк allow-list, которых нет в коде: {stale.Count}.");
        sb.AppendLine("Снимок обязан отражать дерево ровно: лишняя строка — мёртвый пропуск, " +
                      "та точка когда-нибудь вернётся и уже не будет замечена.");
        sb.AppendLine("Что делать: вернуть сравнение в код (если правка была непереводной) или убрать " +
                      "строку из снимка (если сравнение переведено/удалено).");
        foreach (var (path, allowed, actual) in stale)
        {
            var detail = actual == 0
                ? "в коде таких сравнений нет"
                : $"в коде только {actual} из {allowed}";
            sb.AppendLine($"  • {path}: в снимке {allowed}, {detail}");
        }

        Assert.Fail(sb.ToString());
    }

    // Сколько примеров сравнений показать на файл при падении: человеку нужен образец,
    // а не весь список (полный список — строки «путь|число» того же отчёта).
    private const int MaxExamplesPerFile = 5;

    private static Dictionary<string, int> CountByFile(IEnumerable<RepoScan.Finding> findings)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in findings) counts[f.Path] = counts.GetValueOrDefault(f.Path) + 1;
        return counts;
    }

    public sealed class RepoScan
    {
        public string Root { get; }
        public List<Finding> Findings { get; } = [];
        public List<(string Path, int Count)> AllowList { get; } = [];
        public Dictionary<string, int> AllowCountByFile { get; } = new(StringComparer.Ordinal);
        public string? AllowListError { get; private set; }

        public RepoScan()
        {
            Root = FindRepoRoot()
                   // Молчаливо зелёный сторож хуже отсутствующего: не нашли корень — падаем
                   // с диагнозом (по образцу ProjectMapHygieneGuardTests).
                   ?? throw new InvalidOperationException(
                       "Не найден корень репозитория: подъём от " + AppContext.BaseDirectory +
                       " не встретил каталога с .git и backend/. Сторож инлайн-сравнений работает " +
                       "на настоящем дереве и молча пропускать проверку не имеет права.");

            foreach (var file in WalkFiles(Path.Combine(Root, "backend")))
            {
                var rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (!InScope(rel)) continue;
                foreach (var (line, expr) in FindFindings(File.ReadAllLines(file)))
                    Findings.Add(new Finding(rel, line, expr));
            }
            Findings = Findings.OrderBy(f => f.Path, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ToList();

            // Снимок — из того же дерева, что сканируется (в worktree — снимок worktree).
            var allowListPath = Path.Combine(Root, AllowListRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(allowListPath))
            {
                AllowListError = $"нет файла {AllowListRelativePath}";
                return;
            }

            var aggregated = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(allowListPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var sep = line.LastIndexOf('|');
                if (sep <= 0 || !int.TryParse(line[(sep + 1)..], out var count) || count <= 0)
                {
                    AllowListError = $"строка не формата «путь|число»: «{raw}»";
                    break;
                }
                var path = line[..sep];
                aggregated[path] = GetValueOrDefault(aggregated, path) + count;
            }
            if (AllowListError is not null) return;

            AllowList = aggregated
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => (e.Key, e.Value))
                .ToList();
            foreach (var (path, count) in AllowList) AllowCountByFile[path] = count;
        }

        // Одно место сравнения: файл от корня репозитория, строка и само сравнение.
        // Строку показываем человеку как пример; в снимке держится только счётчик файла
        // (решение А1: номера строк после шагов перевода едут).
        public sealed record Finding(string Path, int Line, string Expr);

        // Подъём от каталога сборки тестов до корня репозитория; уровни не считаем — у разных
        // раннеров они разные. .git бывает и файлом: в git worktree это файл со ссылкой на
        // основной репозиторий, а исполнители работают как раз в worktree.
        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if ((Directory.Exists(git) || File.Exists(git)) &&
                    Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        // Плановый обход (не Directory.EnumerateFiles — он в worktree заходит в репозиторий
        // по симлинкам и натыкался на битые пути вне дерева).
        private static IEnumerable<string> WalkFiles(string dir)
        {
            foreach (var f in Directory.GetFiles(dir)) yield return f;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (name is "obj" or "bin" or "node_modules") continue;
                foreach (var f in WalkFiles(d)) yield return f;
            }
        }

        // Охват — сегменты пути (карта S0-3 §2) или имя `*Toolset.cs` (правка А1.2:
        // `ArchitectureToolSet` вне `/Mcp/`); тестовые проекты и TestKit — вне.
        private static bool InScope(string rel)
        {
            var byName = Path.GetFileName(rel).EndsWith("Toolset.cs", StringComparison.Ordinal);
            foreach (var seg in rel.Split('/'))
            {
                if (Array.IndexOf(ScopeSegments, seg) >= 0) return true;
                if (seg == "TestKit" || seg.EndsWith(".Tests") || seg == "Tests") return false;
            }
            return byName;
        }

        private static List<(int Line, string Expr)> FindFindings(string[] lines)
        {
            var result = new List<(int, string)>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Одна строка может быть найдена несколькими шаблонами (например,
                // `owner == context.OwnerId` — и как `.OwnerId`-сравнение, и как двухшаговое):
                // считается один раз.
                var seen = new HashSet<string>(StringComparer.Ordinal);

                void Add(string expr)
                {
                    if (seen.Add(expr)) result.Add((i + 1, expr));
                }

                foreach (Match m in OwnerComparisonPattern.Matches(line))
                {
                    var left = TrimSurroundingParen(m.Groups["left"].Value);
                    var right = TrimSurroundingParen(m.Groups["right"].Value);
                    if (!HasOwnerAccess(left) && !HasOwnerAccess(right)) continue;
                    if (IsExcludedOperand(left) || IsExcludedOperand(right)) continue;
                    if (InComment(line, m.Index)) continue;
                    Add(Normalize(left + m.Groups["op"].Value + right));
                }

                foreach (Match m in EqualsPattern.Matches(line))
                {
                    var args = SplitArgs(m.Groups[1].Value);
                    if (args.Count < 2) continue;
                    var a = args[0];
                    var b = args[1];
                    if (!HasOwnerAccess(a) && !HasOwnerAccess(b)) continue;
                    if (IsExcludedOperand(a) || IsExcludedOperand(b)) continue;
                    if (InComment(line, m.Index)) continue;
                    Add(Normalize($"string.Equals({a},{b})"));
                }

                foreach (Match m in TwoStagePattern.Matches(line))
                {
                    if (InComment(line, m.Index)) continue;
                    Add(Normalize(m.Groups["left"].Value + m.Groups["op"].Value + m.Groups["right"].Value));
                }

                foreach (Match m in ResolveOwnerPattern.Matches(line))
                {
                    if (InComment(line, m.Index)) continue;
                    Add(Normalize(m.Value));
                }
            }
            return result;
        }

        // Комментарий: начало матча стоит после `//` на этой строке.
        private static bool InComment(string line, int index) =>
            line.LastIndexOf("//", index, StringComparison.Ordinal) >= 0;

        // Хвостовая `)` принадлежит окружающим скобкам условия, а не операнду
        // (`… != context.OwnerId)` в `if (…)`): снимаем, пока перед ней символ идентификатора.
        private static string TrimSurroundingParen(string operand)
        {
            while (operand.Length > 1 && operand[^1] == ')' && char.IsLetterOrDigit(operand[^2]))
                operand = operand[..^1];
            return operand;
        }

        // Обращение к свойству владельца: операнд кончается на `.OwnerId` (с `?.`/`!.` внутри
        // цепочки). `expectedOwnerId` без точки — не обращение.
        private static bool HasOwnerAccess(string operand) =>
            operand.EndsWith(".OwnerId", StringComparison.Ordinal);

        private static bool IsExcludedOperand(string operand) =>
            NotACallerIdentifier.IsMatch(operand) || IsLiteralKeyword(operand);

        // Аргументы вызова по запятым вне скобок и строковых литералов.
        private static List<string> SplitArgs(string inside)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            var depth = 0;
            char quote = '\0';
            for (var i = 0; i < inside.Length; i++)
            {
                var c = inside[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
                if (c == '(') depth++;
                if (c == ')') depth--;
                if (c == ',' && depth == 0) { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(c);
            }
            parts.Add(sb.ToString().Trim());
            return parts;
        }

        // Нормализация: пробелы вне строковых литералов. Трейлер `StringComparison…` в
        // матч не входит — форма сравнения в нём не меняется.
        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            char quote = '\0';
            foreach (var c in s)
            {
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static int GetValueOrDefault(Dictionary<string, int> d, string key) =>
            d.TryGetValue(key, out var v) ? v : 0;
    }
}
