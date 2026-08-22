using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Провайдер графа для TypeScript/React: узлы (component/hook/ui-примитив/util/constant) и рёбра
/// References строит Node-скрипт-экстрактор frontend/scripts/codegraph-extractor.mjs
/// (TS Compiler API). Провайдер запускает его под Node с rootPath и мапит JSON-снапшот
/// из stdout в Core.CodeGraph — по образцу Node-MCP-серверов (подпроцесс node).
///
/// Контракт экстрактора (сверено прогоном): `node codegraph-extractor.mjs <rootPath>` печатает
/// на stdout { "Nodes": [{ Id: "файл::имя", Name, Category, FilePath }],
/// "Edges": [{ From, To, Kind }], "Metadata": {…} }. Маппинг принимает оба набора имён полей
/// (Name/Category/FilePath ↔ Label/Kind/SourceFile, From/To/Kind ↔ Source/Target/Relation,
/// в любом регистре). Category — component/hook/ui-примитив/util/constant; строки Kind
/// состыкованы: «ui-примитив» (кириллицей) и ui-primitive/uiprimitive → UiPrimitive,
/// «константа» → Constant, неизвестное → Util.
/// Конец ребра «файл::*» — модуль целиком: разворачивается во все именованные узлы этого
/// файла (граф живёт именованными узлами); узлов у файла нет — ребро отбрасывается вместе
/// с прочими висящими концами. Metadata не читается (диагностика экстрактора).
///
/// Инкремента нет: UpdateAsync делегирует в BuildAsync — экстрактор строит снапшот целиком,
/// дифф в двух местах стоил бы дороже полного прогона. Если скрипта нет возле проекта
/// (или Node недоступен) — пустой граф с логом: сервер жив, C#-граф строится как ни в чём не бывало.
/// </summary>
public sealed class TypeScriptGraphProvider : ICodeGraphProvider
{
    private readonly ILogger<TypeScriptGraphProvider> _logger;
    private readonly string? _extractorPathOverride;
    private readonly int _timeoutSeconds;

    private const string ExtractorFileName = "codegraph-extractor.mjs";

    /// <summary>
    /// Сколько уровней вверх от rootPath ищем каталог со скриптом: покрывает rootPath =
    /// корень репо (frontend/scripts/…), frontend (scripts/…), frontend/src и backend/…
    /// </summary>
    private const int MaxAncestorLevels = 4;

    public TypeScriptGraphProvider(ILogger<TypeScriptGraphProvider> logger, IConfiguration config)
    {
        _logger = logger;
        _extractorPathOverride = config["CodeGraph:TypeScriptExtractorPath"];
        _timeoutSeconds = config.GetValue("CodeGraph:TypeScriptExtractorTimeoutSeconds", 120);
    }

    /// <summary>
    /// Построить полный граф: найти экстрактор возле rootPath, прогнать его под Node,
    /// разобрать JSON-снапшот. Любой сбой (нет скрипта/Node, таймаут, кривой JSON,
    /// ненулевой exit code) — Warning в лог и пустой граф, без исключения наружу.
    /// </summary>
    public async Task<Core.CodeGraph> BuildAsync(string rootPath, CancellationToken ct)
    {
        if (!Directory.Exists(rootPath)) return Core.CodeGraph.Empty;

        var script = FindExtractorScript(rootPath);
        if (script is null)
        {
            // Штатная ситуация (проект без фронтенда): Debug, а не Warning — иначе
            // каждый такой проект заспамил бы лог на каждом перестроении.
            _logger.LogDebug(
                "TypeScriptGraphProvider: экстрактор {File} не найден возле {Path} — пустой граф",
                ExtractorFileName, rootPath);
            return Core.CodeGraph.Empty;
        }

        var sw = Stopwatch.StartNew();

        var run = await RunExtractorAsync(script, rootPath, ct);
        if (run is null) return Core.CodeGraph.Empty;

        var (exitCode, stdout, stderr) = run.Value;
        if (exitCode != 0)
        {
            _logger.LogWarning(
                "TypeScriptGraphProvider: экстрактор завершился с кодом {Code} для {Path}; stderr: {Stderr}",
                exitCode, rootPath, TrimForLog(stderr));
            return Core.CodeGraph.Empty;
        }

        Core.CodeGraph graph;
        try
        {
            graph = ParseSnapshot(stdout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScriptGraphProvider: не разобран ответ экстрактора для {Path}", rootPath);
            return Core.CodeGraph.Empty;
        }

        _logger.LogInformation(
            "TypeScriptGraphProvider: граф для {Path} — {Nodes} узлов, {Edges} рёбер за {Ms} мс",
            rootPath, graph.Nodes.Count, graph.Edges.Count, sw.ElapsedMilliseconds);
        return graph;
    }

    /// <summary>Инкремент не поддержан: полный прогон экстрактора (см. doc класса).</summary>
    public Task<Core.CodeGraph> UpdateAsync(
        string rootPath,
        IEnumerable<string> changedFiles,
        CancellationToken ct)
        => BuildAsync(rootPath, ct);

    /// <summary>
    /// Найти скрипт экстрактора: явный путь из CodeGraph:TypeScriptExtractorPath (абсолютный
    /// или относительно rootPath), иначе подъём от rootPath с проверкой frontend/scripts/…
    /// и scripts/… на каждом уровне. null — скрипта нет возле проекта.
    /// </summary>
    private string? FindExtractorScript(string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(_extractorPathOverride))
        {
            var configured = Path.IsPathRooted(_extractorPathOverride)
                ? _extractorPathOverride
                : Path.Combine(rootPath, _extractorPathOverride);
            if (File.Exists(configured)) return configured;
        }

        var dir = new DirectoryInfo(rootPath);
        for (var level = 0; dir is not null && level < MaxAncestorLevels; level++)
        {
            var candidates = new[]
            {
                Path.Combine(dir.FullName, "frontend", "scripts", ExtractorFileName),
                Path.Combine(dir.FullName, "scripts", ExtractorFileName),
            };
            foreach (var candidate in candidates)
                if (File.Exists(candidate))
                    return candidate;

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Запустить экстрактор под Node и собрать (exit code, stdout, stderr).
    /// null — не запустился/не уложился в таймаут (уже залогировано). Отмена вызывающего
    /// (остановка сервиса) пробрасывается как OperationCanceledException.
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)?> RunExtractorAsync(
        string script, string rootPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                // node без расширения не резолвится из PATH при UseShellExecute=false на Windows
                FileName = LocalProcessRunner.ResolveExecutable("node"),
                // ArgumentList, а не Arguments с ручным квотированием: путь с кавычкой
                // внутри разорвал бы командную строку.
                ArgumentList = { script, rootPath },
                WorkingDirectory = rootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Оба потока читаем параллельно ожиданию: заполненный пайп заблокировал бы Node.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Внутренний таймаут (внешней отмены не было): гасим дерево процессов,
                // вызывающий получает пустой граф, а не зависшее перестроение.
                TryKill(process);
                _logger.LogWarning(
                    "TypeScriptGraphProvider: экстрактор не уложился в {Seconds} с для {Path}",
                    _timeoutSeconds, rootPath);
                return null;
            }

            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            throw; // отмена вызывающего — не наш сбой, пробрасываем
        }
        catch (Exception ex)
        {
            // Node не установлен, недоступен PATH и т.п. — тихий пустой граф, C#-граф не страдает.
            _logger.LogWarning(ex,
                "TypeScriptGraphProvider: не удалось запустить Node-экстрактор для {Path}", rootPath);
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }            // процесс уже завершился
        catch (Win32Exception) { }
    }

    /// <summary>
    /// Разобрать JSON-снапшот экстрактора в Core.CodeGraph. internal static — задача
    /// стыковки с экстрактором тестирует маппинг без запуска Node. Бросает исключения
    /// разбора (JsonException и т.п.) — BuildAsync переводит их в Warning + пустой граф.
    /// </summary>
    internal static Core.CodeGraph ParseSnapshot(string json)
    {
        // Посторонний текст до JSON (баннер Node и т.п.) — парсим от первой '{'.
        var start = json.IndexOf('{');
        if (start < 0)
            throw new InvalidOperationException("stdout экстрактора не содержит JSON-объект");

        using var doc = JsonDocument.Parse(json.Substring(start));
        var root = doc.RootElement;

        var nodes = new Dictionary<string, CodeGraphNode>(StringComparer.Ordinal);
        foreach (var element in ArrayElements(root, "Nodes"))
        {
            var id = GetString(element, "Id");
            var fqn = GetString(element, "FullyQualifiedName");
            var label = GetString(element, "Label") ?? GetString(element, "Name");
            var nodeId = id ?? fqn ?? label;
            if (string.IsNullOrWhiteSpace(nodeId) || nodes.ContainsKey(nodeId)) continue;

            nodes[nodeId] = new CodeGraphNode
            {
                Id = nodeId,
                Label = string.IsNullOrWhiteSpace(label) ? nodeId : label!,
                FullyQualifiedName = string.IsNullOrWhiteSpace(fqn) ? nodeId : fqn!,
                SourceFile = GetString(element, "SourceFile") ?? GetString(element, "FilePath") ?? "",
                SourceLocation = GetString(element, "SourceLocation") ?? "",
                Kind = ParseKind(GetString(element, "Kind") ?? GetString(element, "Category")),
            };
        }

        // Индекс «файл → узлы» (Id экстрактора — «файл::имя») для разворота модульных концов рёбер.
        var nodesByFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var nodeId in nodes.Keys)
        {
            var file = nodeId.Contains("::") ? nodeId[..nodeId.LastIndexOf("::")] : nodeId;
            if (!nodesByFile.TryGetValue(file, out var list)) nodesByFile[file] = list = [];
            list.Add(nodeId);
        }

        var edges = new List<CodeGraphEdge>();
        foreach (var element in ArrayElements(root, "Edges"))
        {
            var source = GetString(element, "Source") ?? GetString(element, "From");
            var target = GetString(element, "Target") ?? GetString(element, "To");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) continue;

            var relation = ParseRelation(GetString(element, "Relation") ?? GetString(element, "Kind"));
            var confidence = ParseConfidence(GetString(element, "Confidence"));

            foreach (var s in ExpandEndpoint(source!))
            foreach (var t in ExpandEndpoint(target!))
            {
                // Висящие концы (узла нет) не тащим — граф остаётся консистентным, как у C#-провайдера.
                if (!nodes.ContainsKey(s) || !nodes.ContainsKey(t)) continue;
                edges.Add(new CodeGraphEdge { Source = s, Target = t, Relation = relation, Confidence = confidence });
            }
        }

        // Конец ребра «файл::*» — модуль целиком: разворачиваем во все именованные узлы файла
        // (экстрактор вешает импорт на модуль, граф живёт именованными узлами-экспортами).
        List<string> ExpandEndpoint(string endpoint) =>
            endpoint.EndsWith("::*", StringComparison.Ordinal)
                ? nodesByFile.TryGetValue(endpoint[..^3], out var ids) ? ids : []
                : [endpoint];

        // Дедуп как в CSharpGraphProvider.UpdateAsync: экстрактор может дать повторы.
        var dedup = edges.DistinctBy(e => $"{e.Source}\x1F{e.Target}\x1F{e.Relation}").ToList();

        return new Core.CodeGraph { Nodes = nodes, Edges = dedup };
    }

    /// <summary>Элементы массива-свойства root (PascalCase или camelCase); массив не найден — пусто.</summary>
    private static IEnumerable<JsonElement> ArrayElements(JsonElement root, string name)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in prop.Value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    yield return item;
            yield break;
        }
    }

    /// <summary>
    /// Строковое свойство объекта без учёта регистра имени (PascalCase/camelCase).
    /// Число (например, SourceLocation как номер строки) принимаем как сырой текст.
    /// </summary>
    private static string? GetString(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.GetRawText(),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// Kind узла: Category экстрактора (component/hook/ui-примитив/util — «ui-примитив»
    /// кириллицей, отсюда и вариант «uiпримитив») и латинские ui-primitive/uiprimitive,
    /// в любом регистре с -/_, плюс имена NodeKind; null/неизвестное → Util
    /// (у TS нет «класса по умолчанию», утилита — нейтральный catch-all).
    /// </summary>
    private static NodeKind ParseKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return NodeKind.Util;
        var normalized = kind.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        return normalized switch
        {
            "component" => NodeKind.Component,
            "hook" => NodeKind.Hook,
            "uiprimitive" or "uiпримитив" => NodeKind.UiPrimitive,
            "util" or "utility" => NodeKind.Util,
            "constant" or "константа" => NodeKind.Constant,
            "class" => NodeKind.Class,
            "interface" => NodeKind.Interface,
            "struct" => NodeKind.Struct,
            "enum" => NodeKind.Enum,
            _ => NodeKind.Util,
        };
    }

    private static EdgeRelation ParseRelation(string? relation) => relation?.Trim().ToLowerInvariant() switch
    {
        "calls" => EdgeRelation.Calls,
        "implements" => EdgeRelation.Implements,
        _ => EdgeRelation.References,
    };

    private static EdgeConfidence ParseConfidence(string? confidence) =>
        string.Equals(confidence?.Trim(), "Extracted", StringComparison.OrdinalIgnoreCase)
            ? EdgeConfidence.Extracted
            : EdgeConfidence.Inferred;

    private static string TrimForLog(string text) =>
        text.Length <= 1000 ? text : "…" + text[^1000..];
}
