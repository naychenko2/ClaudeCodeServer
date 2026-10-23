using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.CodeGraph;

// Секция «Потребители файлов из постановки» хвостом первого хода исполнителя задачи — без хука.
// Хук PreToolUse (ClaudeRuntimeSettings) по построению не работает у container-владельцев и на
// Windows-хосте и не видит правок через Bash; здесь сервер сам берёт файлы, названные в тексте
// первого хода, и спрашивает граф тем же режимом «по файлу» (NeighborsByFileAsync).
//
// Секция идёт по ВСЕМ владельцам, в том числе там, где хук работает (local на Linux): правка через
// Bash (printf >> file) хук Edit|Write не видит. Дубль «хук + хвост» принят: ≤ 20 строк на файл (до 3 файлов — ≤ 61 строки
// на секцию: заголовок плюс 3 блока по 20), один раз на первом ходе, только файлы постановки; срабатывают они в разные моменты.
// Агент ничего не спрашивает; нет графа, файл вне графа, ответ дольше 3 с, сбой — молчание.
// Секция едет InTurnTail: нестабильный текст не должен попадать в системный блок.
public sealed class ConsumersTailContributor : IPromptSectionContributor
{
    public const string SectionKey = "consumers-tail";
    internal const int LineLimit = 20;
    internal const int MaxFiles = 3;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public const string BuildingNote =
        "Граф этого дерева ещё строится; ниже — по основному дереву проекта (ветка может отличаться).";

    // Путь (с каталогом или bare-имя) с расширением файла, который понимают провайдеры графа;
    // хвост «:123» — номер строки
    private static readonly Regex FilePattern = new(
        @"(?<![\w/.\\\-])(/?(?:[\w.\-]+/)*[\w.\-]+\.(?:cs|tsx?))(?::\d+)?(?![\w/]|\.[A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Bare-имя ищем обходом не глубже BareSearchDepth, без служебных каталогов; неоднозначное — молчание
    private const int BareSearchDepth = 6;
    private const int BareSearchDirLimit = 5000;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "dist", "packages" };

    private readonly CodeGraphService _graphs;
    private readonly IPersonaServerToolGate _toolGate;
    private readonly ILogger<ConsumersTailContributor> _log;
    private readonly TimeSpan _timeout;
    private readonly Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>> _neighbors;

    public ConsumersTailContributor(CodeGraphService graphs, CodeGraphQueryService queries,
        IPersonaServerToolGate toolGate, ILogger<ConsumersTailContributor> log, TimeSpan? timeout = null)
        : this(graphs, queries, toolGate, log, timeout, null)
    {
    }

    // Шов для тестов: источник ответа графа подменяется, чтобы проверять потолок без гонок с таймером
    internal ConsumersTailContributor(CodeGraphService graphs, CodeGraphQueryService queries,
        IPersonaServerToolGate toolGate, ILogger<ConsumersTailContributor> log, TimeSpan? timeout,
        Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>>? neighbors)
    {
        _timeout = timeout ?? DefaultTimeout;
        _graphs = graphs;
        _toolGate = toolGate;
        _log = log;
        _neighbors = neighbors
            ?? ((root, file, ct) => queries.NeighborsByFileAsync(root, file, CodeGraphQueryService.MaxLimit, ct));
    }

    public string Key => SectionKey;
    public string Title => "Потребители файлов задачи";
    public int Order => 620;
    public string Group => "project";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.Session.TaskExecution
        && sessionContext.Session.MessageCount <= 1
        && sessionContext.ServerContent
        && !string.IsNullOrWhiteSpace(sessionContext.RootPath)
        && _toolGate.IsServerToolEnabled(sessionContext.OwnerId, sessionContext.Persona, "codegraph");

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        var root = sessionContext.RootPath;
        if (string.IsNullOrWhiteSpace(turnText) || string.IsNullOrWhiteSpace(root)) return null;
        // Токен отменяется по выходу: обход дерева и запросы к графу не должны крутиться в фоне после потолка
        using var cts = new CancellationTokenSource();
        try
        {
            var text = await BuildTextAsync(turnText, root, sessionContext.MainRootPath, cts.Token)
                .WaitAsync(_timeout);
            return text is null
                ? null
                : new PromptSectionContribution([new PromptSection(Key, text, Title, InTurnTail: true)]);
        }
        catch (Exception ex)
        {
            // молчание: секция не должна ни ронять, ни тормозить ход
            _log.LogDebug(ex, "Потребители файлов задачи не получены");
            return null;
        }
        finally
        {
            cts.Cancel();
        }
    }

    internal async Task<string?> BuildTextAsync(string turnText, string root, string? mainRoot, CancellationToken ct)
    {
        // Не даём работе завершиться синхронно: потолок WaitAsync обязан действовать и на быстрый граф,
        // и на обход дерева при поиске bare-имён
        await Task.Yield();
        try
        {
            var files = ExtractFiles(turnText, root, ct);
            if (files.Count == 0) return null;
            var blocks = new List<string>();
            foreach (var file in files)
            {
                var (result, note) = await QueryAsync(file, root, mainRoot, ct);
                if (result is { Total: > 0 } && Render(file, result, note) is { } block) blocks.Add(block);
            }
            return blocks.Count == 0
                ? null
                : "## Потребители файлов из постановки (по графу кода)\n" + string.Join("\n\n", blocks);
        }
        // Покинутая после потолка задача завершается отменой: её никто не ждёт, исключение не должно оставаться ненаблюдаемым
        catch (OperationCanceledException) { return null; }
    }

    private async Task<(CodeGraphNeighborsResultDto? Result, string? Note)> QueryAsync(
        string file, string root, string? mainRoot, CancellationToken ct)
    {
        var outcome = await _neighbors(root, file, ct);
        if (outcome.HasGraph) return (outcome.Result, null);

        // Граф дерева ещё не построен: просим постройку и пока отвечаем по основному дереву с пометкой
        _graphs.StartRebuildIfIdle(root);
        if (string.IsNullOrWhiteSpace(mainRoot)
            || PathNormalizer.NormalizePath(mainRoot) == PathNormalizer.NormalizePath(root))
            return (null, null);
        var fallback = await _neighbors(mainRoot, file, ct);
        return fallback.HasGraph ? (fallback.Result, BuildingNote) : (null, null);
    }

    // Файлы постановки, существующие в дереве: путь от корня дерева, без «..», не больше MaxFiles.
    // Файлы берутся из всего текста хода по порядку появления (описание задачи идёт раньше правил,
    // а MaxFiles ограничивает вклад хвоста) — порядок и лимит зафиксированы тестом.
    internal static IReadOnlyList<string> ExtractFiles(
        string turnText, string root, CancellationToken ct = default)
    {
        var rootFull = Path.GetFullPath(root);
        var matches = FilePattern.Matches(turnText).Select(m => m.Groups[1].Value).ToList();
        // Общий бюджет MaxFiles на путь и bare-имя по порядку появления: имя сверх бюджета в ответ не попадёт,
        // и обход дерева ради него не нужен. Путь с каталогом проверяется дёшево, bare-имя занимает место до обхода
        var bare = new HashSet<string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in matches)
        {
            if (taken.Count == MaxFiles) break;
            if (raw.Contains('/') ? ResolvePath(raw, rootFull) is not null : bare.Add(raw)) taken.Add(raw);
        }
        bare.IntersectWith(taken);
        var found = new List<string>();
        Dictionary<string, List<string>>? byName = null;
        foreach (var raw in matches)
        {
            string? rel;
            if (!raw.Contains('/'))
            {
                if (!bare.Contains(raw)) continue;
                byName ??= ScanByName(rootFull, bare, ct);
                rel = byName.TryGetValue(raw, out var hits) && hits.Count == 1 ? hits[0] : null;
            }
            else rel = ResolvePath(raw, rootFull);
            if (rel is null) continue;
            if (!found.Contains(rel, StringComparer.Ordinal)) found.Add(rel);
            if (found.Count == MaxFiles) break;
        }
        return found;
    }

    // Путь с каталогом → путь от корня дерева; null — вне дерева, с «..» или файла нет
    private static string? ResolvePath(string raw, string rootFull)
    {
        var rel = raw.StartsWith('/')
            ? (raw.StartsWith(rootFull.TrimEnd('/') + "/", StringComparison.Ordinal)
                ? raw[(rootFull.TrimEnd('/').Length + 1)..] : null)
            : raw;
        if (rel is null || rel.Split('/').Any(s => s is ".." or "." or "")) return null;
        var full = Path.GetFullPath(Path.Combine(rootFull, rel));
        return full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && File.Exists(full) ? rel : null;
    }

    // Один обход дерева на ход: имя → пути от корня (по всем совпавшим, чтобы видеть неоднозначность)
    private static Dictionary<string, List<string>> ScanByName(
        string rootFull, HashSet<string> names, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var queue = new Queue<(string Dir, string Rel, int Depth)>();
        queue.Enqueue((rootFull, "", 0));
        var dirs = 0;
        while (queue.Count > 0 && dirs++ < BareSearchDirLimit)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, rel, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (!names.Contains(name)) continue;
                    if (!result.TryGetValue(name, out var list)) result[name] = list = [];
                    list.Add(rel.Length == 0 ? name : rel + "/" + name);
                }
                if (depth >= BareSearchDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                    if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    queue.Enqueue((sub, rel.Length == 0 ? name : rel + "/" + name, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static bool IsTest(string path)
    {
        var parts = path.Split('/');
        if (parts[..^1].Any(c => c.Equals("__tests__", StringComparison.OrdinalIgnoreCase)
                || c.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || c.Equals("test", StringComparison.OrdinalIgnoreCase)
                || c.EndsWith(".Tests", StringComparison.Ordinal)
                || c.EndsWith(".Test", StringComparison.Ordinal)))
            return true;
        return Regex.IsMatch(parts[^1], @"(Tests?\.cs|\.(test|spec)\.[jt]sx?)$");
    }

    // Тот же вид и те же потолки, что у хука: ≤ 20 строк, тесты вперёд (до 4), дальше «ещё N»
    internal static string? Render(string file, CodeGraphNeighborsResultDto result, string? note)
    {
        var total = result.Total > 0 ? result.Total : result.Neighbors.Count;
        if (total <= 0) return null;
        var paths = result.Neighbors.Select(n => (Path: n.Location ?? n.Fqn ?? "?", n.Relation)).ToList();
        var tests = paths.Where(p => IsTest(p.Path)).ToList();
        var ordered = tests.Take(4).Concat(paths.Where(p => !IsTest(p.Path))).Concat(tests.Skip(4)).ToList();

        var lines = new List<string>();
        if (note is not null) lines.Add(note);
        lines.Add($"Потребители {file} (файлов: {total}, тестов среди них: {tests.Count}):");
        var room = LineLimit - lines.Count;
        var show = ordered.Take(total <= room ? room : room - 1).ToList();
        foreach (var (path, relation) in show)
            lines.Add($"- {path}{(IsTest(path) ? " [тест]" : "")} ({relation})");
        if (total - show.Count > 0) lines.Add($"ещё {total - show.Count}");
        return string.Join('\n', lines.Take(LineLimit));
    }
}
