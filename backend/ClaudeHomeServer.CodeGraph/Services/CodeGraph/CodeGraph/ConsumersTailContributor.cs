using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.CodeGraph;

// Секция «Потребители файлов из постановки» хвостом первого хода исполнителя задачи — без хука.
// Хук PreToolUse (ClaudeRuntimeSettings) по построению не работает у container-владельцев и на
// Windows-хосте и не видит правок через Bash; здесь сервер сам берёт файлы, названные в тексте
// первого хода, и спрашивает граф тем же режимом «по файлу» (NeighborsByFileAsync).
//
// Только там, где хука нет (PromptSessionContext.ConsumersHookActive = false): у local-владельца
// на Linux хук держит правку сам, а дубль потребовал бы общего состояния между хуком и сервером.
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

    // Путь с каталогом и расширением файла, который понимают провайдеры графа; хвост «:123» — номер строки
    private static readonly Regex FilePattern = new(
        @"(?<![\w/.\-])(/?(?:[\w.\-]+/)+[\w.\-]+\.(?:cs|tsx?))(?::\d+)?(?![\w/])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly CodeGraphService _graphs;
    private readonly CodeGraphQueryService _queries;
    private readonly IPersonaServerToolGate _toolGate;
    private readonly ILogger<ConsumersTailContributor> _log;
    private readonly TimeSpan _timeout;

    public ConsumersTailContributor(CodeGraphService graphs, CodeGraphQueryService queries,
        IPersonaServerToolGate toolGate, ILogger<ConsumersTailContributor> log, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _graphs = graphs;
        _queries = queries;
        _toolGate = toolGate;
        _log = log;
    }

    public string Key => SectionKey;
    public string Title => "Потребители файлов задачи";
    public int Order => 620;
    public string Group => "project";

    public bool IsEnabled(PromptSessionContext sessionContext) =>
        sessionContext.Session.TaskExecution
        && sessionContext.Session.MessageCount <= 1
        && !sessionContext.ConsumersHookActive
        && sessionContext.ServerContent
        && !string.IsNullOrWhiteSpace(sessionContext.RootPath)
        && _toolGate.IsServerToolEnabled(sessionContext.OwnerId, sessionContext.Persona, "codegraph");

    public async Task<PromptSectionContribution?> BuildAsync(
        PromptSessionContext sessionContext, string? turnText)
    {
        var root = sessionContext.RootPath;
        if (string.IsNullOrWhiteSpace(turnText) || string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var files = ExtractFiles(turnText, root);
            if (files.Count == 0) return null;
            var text = await BuildTextAsync(files, root, sessionContext.MainRootPath)
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
    }

    private async Task<string?> BuildTextAsync(IReadOnlyList<string> files, string root, string? mainRoot)
    {
        // Не даём работе завершиться синхронно: потолок WaitAsync обязан действовать и на быстрый граф
        await Task.Yield();
        var blocks = new List<string>();
        foreach (var file in files)
        {
            var (result, note) = await QueryAsync(file, root, mainRoot);
            if (result is { Total: > 0 } && Render(file, result, note) is { } block) blocks.Add(block);
        }
        return blocks.Count == 0
            ? null
            : "## Потребители файлов из постановки (по графу кода)\n" + string.Join("\n\n", blocks);
    }

    private async Task<(CodeGraphNeighborsResultDto? Result, string? Note)> QueryAsync(
        string file, string root, string? mainRoot)
    {
        var outcome = await _queries.NeighborsByFileAsync(root, file, CodeGraphQueryService.MaxLimit, CancellationToken.None);
        if (outcome.HasGraph) return (outcome.Result, null);

        // Граф дерева ещё не построен: просим постройку и пока отвечаем по основному дереву с пометкой
        _graphs.StartRebuildIfIdle(root);
        if (string.IsNullOrWhiteSpace(mainRoot)
            || PathNormalizer.NormalizePath(mainRoot) == PathNormalizer.NormalizePath(root))
            return (null, null);
        var fallback = await _queries.NeighborsByFileAsync(mainRoot, file, CodeGraphQueryService.MaxLimit, CancellationToken.None);
        return fallback.HasGraph ? (fallback.Result, BuildingNote) : (null, null);
    }

    // Файлы постановки, существующие в дереве: путь от корня дерева, без «..», не больше MaxFiles
    internal static IReadOnlyList<string> ExtractFiles(string turnText, string root)
    {
        var rootFull = Path.GetFullPath(root);
        var found = new List<string>();
        foreach (Match m in FilePattern.Matches(turnText))
        {
            var raw = m.Groups[1].Value;
            var rel = raw.StartsWith('/')
                ? (raw.StartsWith(rootFull.TrimEnd('/') + "/", StringComparison.Ordinal)
                    ? raw[(rootFull.TrimEnd('/').Length + 1)..] : null)
                : raw;
            if (rel is null || rel.Split('/').Any(s => s is ".." or "." or "")) continue;
            var full = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!full.StartsWith(rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) || !File.Exists(full)) continue;
            if (!found.Contains(rel, StringComparer.Ordinal)) found.Add(rel);
            if (found.Count == MaxFiles) break;
        }
        return found;
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
