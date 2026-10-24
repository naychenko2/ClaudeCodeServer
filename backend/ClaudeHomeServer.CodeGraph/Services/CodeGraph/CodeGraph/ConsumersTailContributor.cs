using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.CodeGraph;

// Секция «Контракты файлов из постановки» хвостом первого хода исполнителя задачи — без хука.
// Хук PreToolUse (ClaudeRuntimeSettings) по построению не работает у container-владельцев и на
// Windows-хосте и не видит правок через Bash; здесь сервер сам берёт файлы, названные в тексте
// первого хода, и отдаёт на каждый три подраздела: Зависимости (исходящие Calls|References с
// «путь:строка» объявления, DependenciesByFileAsync), Сторожа по пути (ContractGuardMap) и
// Потребители (входящие, NeighborsByFileAsync). Модель узкого контекста граф сама не спрашивает —
// контракт соседнего слоя должен приходить сам.
//
// Секция идёт по ВСЕМ владельцам, в том числе там, где хук работает (local на Linux): правка через
// Bash (printf >> file) хук Edit|Write не видит. Потолки: ≤ 20 строк на подраздел, ≤ 3 файла, ≤ 120 строк
// на секцию; при превышении «ещё N», режутся сначала зависимости, затем потребители, сторожа — никогда.
// Один раз на первом ходе, только файлы постановки. Агент ничего не спрашивает;
// нет графа, файл вне графа, ответ дольше 3 с, сбой — молчание.
// Секция едет InTurnTail: нестабильный текст не должен попадать в системный блок.
public sealed class ConsumersTailContributor : IPromptSectionContributor
{
    public const string SectionKey = "contracts-tail";
    internal const int LineLimit = 20;
    internal const int SectionLineLimit = 120;
    // Меньше двух строк подраздел не ужимается: заголовок и «ещё N»
    private const int MinSubsectionLines = 2;
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
    private readonly Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>> _dependencies;

    public ConsumersTailContributor(CodeGraphService graphs, CodeGraphQueryService queries,
        IPersonaServerToolGate toolGate, ILogger<ConsumersTailContributor> log, TimeSpan? timeout = null)
        : this(graphs, queries, toolGate, log, timeout, null)
    {
    }

    // Шов для тестов: источник ответа графа подменяется, чтобы проверять потолок без гонок с таймером
    internal ConsumersTailContributor(CodeGraphService graphs, CodeGraphQueryService queries,
        IPersonaServerToolGate toolGate, ILogger<ConsumersTailContributor> log, TimeSpan? timeout,
        Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>>? neighbors,
        Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>>? dependencies = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _graphs = graphs;
        _toolGate = toolGate;
        _log = log;
        _neighbors = neighbors
            ?? ((root, file, ct) => queries.NeighborsByFileAsync(root, file, CodeGraphQueryService.MaxLimit, ct));
        _dependencies = dependencies
            ?? ((root, file, ct) => queries.DependenciesByFileAsync(root, file, CodeGraphQueryService.MaxLimit, ct));
    }

    public string Key => SectionKey;
    public string Title => "Контракты файлов задачи";
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
            _log.LogDebug(ex, "Контракты файлов задачи не получены");
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
            var parts = new List<FileContract>();
            foreach (var file in files)
            {
                var contract = await QueryAsync(file, root, mainRoot, turnText, ct);
                if (contract is not null) parts.Add(contract);
            }
            return parts.Count == 0
                ? null
                : "## Контракты файлов из постановки (по графу кода)\n" + Compose(parts);
        }
        // Покинутая после потолка задача завершается отменой: её никто не ждёт, исключение не должно оставаться ненаблюдаемым
        catch (OperationCanceledException) { return null; }
    }

    // Что граф знает про один файл постановки; null — файла нет в графе (или графа нет): молчание
    private sealed record FileContract(
        string File, string? Note, CodeGraphNeighborsResultDto? Consumers, CodeGraphNeighborsResultDto? Dependencies);

    private async Task<FileContract?> QueryAsync(
        string file, string root, string? mainRoot, string turnText, CancellationToken ct)
    {
        var usedRoot = root;
        string? note = null;
        var outcome = await _neighbors(root, file, ct);
        if (!outcome.HasGraph)
        {
            // Граф дерева ещё не построен: просим постройку и пока отвечаем по основному дереву с пометкой
            _graphs.StartRebuildIfIdle(root);
            if (string.IsNullOrWhiteSpace(mainRoot)
                || PathNormalizer.NormalizePath(mainRoot) == PathNormalizer.NormalizePath(root))
                return null;
            outcome = await _neighbors(mainRoot, file, ct);
            if (!outcome.HasGraph) return null;
            usedRoot = mainRoot;
            note = BuildingNote;
        }
        var dependencies = await _dependencies(usedRoot, file, ct);
        var deps = dependencies.HasGraph ? dependencies.Result : null;
        // Файл вне графа (оба режима без результата) — молчим; сторожа по пути одни, без графа, секцию не заводят
        if (outcome.Result is null && deps is null) return null;
        return new FileContract(file, note, outcome.Result, deps is null ? null : WithMentionedFirst(deps, turnText));
    }

    // Символы, названные в тексте постановки, идут первыми: задача правит или вызывает именно их
    private static CodeGraphNeighborsResultDto WithMentionedFirst(CodeGraphNeighborsResultDto result, string turnText)
    {
        bool Mentioned(CodeGraphNeighborDto n)
        {
            var cut = Math.Max(n.Fqn.LastIndexOf('.'), n.Fqn.LastIndexOf(':'));
            var name = cut >= 0 ? n.Fqn[(cut + 1)..] : n.Fqn;
            return name.Length > 2 && Regex.IsMatch(turnText, $@"(?<![\w]){Regex.Escape(name)}(?![\w])");
        }
        return new CodeGraphNeighborsResultDto
        {
            Node = result.Node,
            Neighbors = result.Neighbors.OrderByDescending(Mentioned).ToList(),
            Total = result.Total,
            TotalIn = result.TotalIn,
            TotalOut = result.TotalOut,
            ByRelation = result.ByRelation,
            IsStale = result.IsStale,
        };
    }

    // Блоки файлов склеиваются пустой строкой. Потолок секции: сперва ужимаются зависимости (их модель
    // может добрать Read-ом), затем потребители; сторожа не ужимаются никогда
    private static string Compose(List<FileContract> parts)
    {
        var depCaps = parts.Select(_ => LineLimit).ToArray();
        var consCaps = parts.Select(_ => LineLimit).ToArray();
        string[] Blocks() => parts.Select((p, i) => RenderFile(p, depCaps[i], consCaps[i])).ToArray();
        // +1 — заголовок секции, плюс по пустой строке между блоками
        int Total(string[] blocks) => 1 + blocks.Sum(b => b.Split('\n').Length) + (blocks.Length - 1);

        var blocks = Blocks();
        foreach (var caps in new[] { depCaps, consCaps })
        {
            for (var i = parts.Count - 1; i >= 0 && Total(blocks) > SectionLineLimit; i--)
            {
                caps[i] = MinSubsectionLines;
                blocks = Blocks();
            }
        }
        return string.Join("\n\n", blocks);
    }

    private static string RenderFile(FileContract p, int depLines, int consLines)
    {
        var lines = new List<string>();
        if (p.Note is not null) lines.Add(p.Note);
        if (p.Dependencies is { Total: > 0 } deps && RenderDependencies(p.File, deps, depLines) is { } d) lines.Add(d);
        if (RenderGuards(p.File) is { } g) lines.Add(g);
        if (p.Consumers is { Total: > 0 } cons && Render(p.File, cons, null, consLines) is { } c) lines.Add(c);
        return string.Join('\n', lines);
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

    // Тот же вид и те же потолки, что у хука: ≤ maxLines строк, тесты вперёд (до 4), дальше «ещё N»
    internal static string? Render(
        string file, CodeGraphNeighborsResultDto result, string? note, int maxLines = LineLimit)
    {
        var total = result.Total > 0 ? result.Total : result.Neighbors.Count;
        if (total <= 0) return null;
        var paths = result.Neighbors.Select(n => (Path: n.Location ?? n.Fqn ?? "?", n.Relation)).ToList();
        var tests = paths.Where(p => IsTest(p.Path)).ToList();
        var ordered = tests.Take(4).Concat(paths.Where(p => !IsTest(p.Path))).Concat(tests.Skip(4)).ToList();

        var lines = new List<string>();
        if (note is not null) lines.Add(note);
        lines.Add($"Потребители {file} (файлов: {total}, тестов среди них: {tests.Count}):");
        var room = Math.Max(maxLines - lines.Count, 0);
        var show = ordered.Take(total <= room ? room : Math.Max(room - 1, 0)).ToList();
        foreach (var (path, relation) in show)
            lines.Add($"- {path}{(IsTest(path) ? " [тест]" : "")} ({relation})");
        if (total - show.Count > 0) lines.Add($"ещё {total - show.Count}");
        return string.Join('\n', lines.Take(Math.Max(maxLines, MinSubsectionLines)));
    }

    // Исходящие связи файла: строка «Fqn — путь:строка объявления (отношение)», названные в постановке — первыми
    internal static string? RenderDependencies(string file, CodeGraphNeighborsResultDto result, int maxLines = LineLimit)
    {
        var total = result.Total > 0 ? result.Total : result.Neighbors.Count;
        if (total <= 0) return null;
        var lines = new List<string> { $"Зависимости {file} (что вызывает и на что ссылается, объявление — путь:строка; символов: {total}):" };
        var room = Math.Max(maxLines - 1, 0);
        var show = result.Neighbors.Take(total <= room ? room : Math.Max(room - 1, 0)).ToList();
        foreach (var n in show) lines.Add($"- {n.Fqn} — {n.Location} ({n.Relation})");
        if (total - show.Count > 0) lines.Add($"ещё {total - show.Count}");
        return string.Join('\n', lines.Take(Math.Max(maxLines, MinSubsectionLines)));
    }

    // Сторожа по пути файла — таблица ContractGuardMap; не режутся никогда
    internal static string? RenderGuards(string file)
    {
        var guards = ContractGuardMap.For(file);
        if (guards.Count == 0) return null;
        return $"Сторожа по пути {file} (краснеют при нарушении контракта слоя):\n"
            + string.Join('\n', guards.Select(g => $"- {g.Prefix} → {g.Guard.Title}"));
    }
}
