using System.Collections.Concurrent;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.CodeGraph.Roslyn;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Services.CodeGraph;

/// <summary>
/// Сервис построения графа зависимостей кода. Композит над провайдерами языков,
/// с дебаунсом перестроения по изменению файлов и инкрементальным обновлением.
/// IDisposable: гасит pending-таймеры и отменяет бегущие перестроения при остановке.
/// </summary>
public sealed class CodeGraphService : IDisposable
{
    private readonly ILogger<CodeGraphService> _logger;
    private readonly ProjectManager _projects;
    private readonly GraphPersistence _persistence;
    private readonly int _rebuildDebounceMs;

    // Регистры провайдеров: extension → ICodeGraphProvider (например, ".cs" → CSharpGraphProvider)
    private readonly ConcurrentDictionary<string, ICodeGraphProvider> _providers = new();

    // Дебаунс rebuild: normalizedRootPath → (накопленные changedFiles, Timer).
    private readonly ConcurrentDictionary<string, RebuildState> _pendingRebuilds = new();

    // Бегущие фоновые перестроения: normalizedRootPath → CTS. Новый rebuild (явный или
    // по таймеру) отменяет предыдущий — orphan-задачи не копятся. Dispose отменяет все.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRebuilds = new();

    // Фоновые перестроения по запросу GET (build-on-first-GET): normalizedRootPath → CTS.
    // Guard «не плодить»: пока одно построение бежит, повторные GET его не дублируют.
    // Отдельно от _activeRebuilds — тем владеет дебаунс-таймер; Dispose отменяет все.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _onDemandRebuilds = new();

    // Токен остановки сервиса: отменяет все фоновые перестроения разом.
    private readonly CancellationTokenSource _stopping = new();

    public CodeGraphService(
        ILogger<CodeGraphService> logger,
        ProjectManager projects,
        GraphPersistence persistence,
        IConfiguration config)
    {
        _logger = logger;
        _projects = projects;
        _persistence = persistence;
        // Окно дебаунса rebuild: продолгое (15с, как NotesService), чтобы серия правок .cs
        // схлопнулась в одно перестроение; в тестах уменьшается через CodeGraph:RebuildDebounceMs.
        _rebuildDebounceMs = config.GetValue("CodeGraph:RebuildDebounceMs", 15000);
    }

    /// <summary>
    /// Зарегистрировать провайдер для extension (например, ".cs" → CSharpGraphProvider).
    /// </summary>
    public void RegisterProvider(string extension, ICodeGraphProvider provider)
    {
        if (string.IsNullOrWhiteSpace(extension))
            throw new ArgumentException("Extension не может быть пустым", nameof(extension));

        var ext = extension.TrimStart('*').ToLowerInvariant();
        _providers[ext] = provider;
        _logger.LogInformation("Провайдер CodeGraph зарегистрирован для {Extension}: {Provider}",
            ext, provider.GetType().Name);
    }

    /// <summary>
    /// Построить граф для проекта (или взять из кэша).
    /// </summary>
    public async Task<Core.CodeGraph> GetAsync(string rootPath, CancellationToken ct)
    {
        // Пытаемся загрузить из персистентности
        var cached = await _persistence.LoadAsync(rootPath, ct);
        if (cached is not null)
        {
            _logger.LogDebug("Граф загружен из кэша для {Path}", rootPath);
            return cached;
        }

        // Нет в кэше — строим с нуля
        _logger.LogInformation("Построение графа для {Path}", rootPath);
        var graph = await BuildInternalAsync(rootPath, changedByExtension: null, ct);
        await _persistence.SaveAsync(rootPath, graph, ct);
        return graph;
    }

    /// <summary>
    /// Снимок графа для REST: читает сохранённый граф + метаданные, считает isStale.
    /// null — граф ещё не построен для этого rootPath (контроллер вернёт 404).
    /// </summary>
    public async Task<CodeGraphSnapshotDto?> GetSnapshotAsync(string rootPath, CancellationToken ct)
    {
        var snap = await _persistence.LoadSnapshotAsync(rootPath, ct);
        if (snap is null) return null;

        var isStale = IsStale(rootPath, snap.BuiltAt);
        return CodeGraphSnapshotMapper.ToDto(snap.Graph, snap.BuiltAt, snap.FileCount, isStale);
    }

    /// <summary>
    /// Дешёвая сигнатура кэша графа (mtime graph.json; null — граф не построен).
    /// Для slice-провайдера системного промпта: пока сигнатура та же, граф не перестраивался,
    /// и переиспользовать кэш slice можно без загрузки graph.json.
    /// </summary>
    public DateTimeOffset? GetCacheSignature(string rootPath) => _persistence.GetCacheSignature(rootPath);

    /// <summary>
    /// Граф устарел: дешёвый mtime-чек (.cs новее BuiltAt). Экспонирован для slice-провайдера,
    /// чтобы помечать slice устаревшим без полного GetSnapshotAsync каждый ход.
    /// </summary>
    public bool IsStaleFor(string rootPath, DateTimeOffset builtAt) => IsStale(rootPath, builtAt);

    /// <summary>
    /// Граф устарел: хотя бы один .cs-файл проекта изменён позже BuiltAt.
    /// Дёшево по mtime; при отсутствии BuiltAt (старый снимок) считаем устаревшим.
    /// Обход — тот же, что у построения (EnumerateCsFiles минует .claude/bin/obj/node_modules…):
    /// иначе stat шёл бы по мусорным .cs (на ClaudeCodeServer это 7878 файлов и ~40с на GET).
    /// </summary>
    private static bool IsStale(string rootPath, DateTimeOffset builtAt)
    {
        try
        {
            if (!Directory.Exists(rootPath)) return false;
            if (builtAt == DateTimeOffset.MinValue) return true;

            var newest = CompilationBuilder.EnumerateCsFiles(rootPath)
                .Select(f => { try { return new FileInfo(f).LastWriteTimeUtc; } catch { return DateTime.MinValue; } })
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            // Нет .cs — нечего считать устаревшим (граф для пустого проекта не строится).
            if (newest == DateTime.MinValue) return false;
            return newest > builtAt.UtcDateTime;
        }
        catch
        {
            // Ошибка обхода — не считаем граф устаревшим, отдаём как есть.
            return false;
        }
    }

    /// <summary>
    /// Сбросить кэш графа для проекта (перестроить при следующем запросе).
    /// </summary>
    public void Invalidate(string rootPath)
    {
        _persistence.Delete(rootPath);
    }

    /// <summary>
    /// Остановка сервиса: гасим pending-таймеры дебаунса и отменяем бегущие фоновые
    /// перестроения, чтобы после Dispose не осталось orphan-задач, пишущих graph.json.
    /// </summary>
    public void Dispose()
    {
        _stopping.Cancel();

        foreach (var state in _pendingRebuilds.Values)
            state.Dispose();
        _pendingRebuilds.Clear();

        foreach (var cts in _activeRebuilds.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _activeRebuilds.Clear();

        foreach (var cts in _onDemandRebuilds.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _onDemandRebuilds.Clear();

        _stopping.Dispose();
    }

    /// <summary>
    /// Явно перестроить граф (кнопка «Построить граф» в empty-state/stale,
    /// POST /api/projects/{id}/code-graph/build). Строит с нуля (все провайдеры — BuildAsync);
    /// накопленные инкременты сбрасывает: при явном запросе консистентность важнее инкрементальности.
    /// </summary>
    public async Task RebuildAsync(string rootPath, CancellationToken ct)
    {
        var normalized = WorkspaceKnowledgeStore.NormalizePath(rootPath);

        // Снимаем pending rebuild и гасим его таймер: фоновый дебаунс не должен
        // наложиться на явное построение дублирующим перестроением.
        if (_pendingRebuilds.TryRemove(normalized, out var pending))
            pending.Dispose();
        // И отменяем бегущее фоновое перестроение этого проекта — явное его замещает.
        CancelActiveRebuild(normalized);

        _logger.LogInformation("Перестроение графа (явный триггер) для {Path}", rootPath);
        var graph = await BuildInternalAsync(rootPath, changedByExtension: null, ct);
        await _persistence.SaveAsync(rootPath, graph, ct);

        _logger.LogInformation("Граф перестроен для {Path}: {Nodes} узлов, {Edges} рёбер",
            rootPath, graph.Nodes.Count, graph.Edges.Count);
    }

    /// <summary>
    /// Запустить фоновое построение графа, не блокируя вызывающего (build-on-first-GET).
    /// Переиспользует RebuildAsync (BuildInternalAsync+SaveAsync, минуя дебаунс) — НЕ дублирует.
    /// Guard «один фоновый rebuild per project»: пока построение проекта бежит, повторные
    /// запросы (частые GET UI) его не плодят — _onDemandRebuilds. true — построение только что
    /// запущено; false — уже бежит, новый не нужен (UI всё равно получит граф следующим GET).
    /// </summary>
    public bool StartRebuildIfIdle(string rootPath)
    {
        var normalized = WorkspaceKnowledgeStore.NormalizePath(rootPath);

        // Связанный CTS: отменяется при остановке сервиса — orphan-задача не переживёт Dispose.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        if (!_onDemandRebuilds.TryAdd(normalized, cts))
        {
            // Для этого проекта уже бежит фоновое построение — не плодим конкурирующие rebuild.
            cts.Dispose();
            return false;
        }

        _ = RebuildOnDemandAsync(rootPath, normalized, cts);
        return true;
    }

    /// <summary>
    /// Фоновое построение (fire-and-forget) поверх RebuildAsync. По завершении
    /// (успех/ошибка/отмена) снимает guard, чтобы следующий запрос мог запустить новое.
    /// </summary>
    private async Task RebuildOnDemandAsync(string rootPath, string normalized, CancellationTokenSource cts)
    {
        try
        {
            _logger.LogInformation("Фоновое построение графа (on-demand) для {Path}", rootPath);
            await RebuildAsync(rootPath, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Остановка сервиса — штатно, не ошибка.
            _logger.LogDebug("Фоновое построение графа для {Path} отменено", rootPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка фонового построения графа для {Path}", rootPath);
        }
        finally
        {
            // Снимаем из реестра только СЕБЯ: на нашем месте мог оказаться CTS следующего запроса.
            if (_onDemandRebuilds.TryRemove(new KeyValuePair<string, CancellationTokenSource>(normalized, cts)))
                cts.Dispose();
        }
    }

    /// <summary>
    /// Планировать инкрементальное обновление графа по изменённым файлам (дебаунс 15с).
    /// Накопленные файлы передаются провайдеру в UpdateAsync.
    /// </summary>
    public void InvalidateIncremental(string rootPath, IEnumerable<string> changedFiles)
    {
        // Escape-гард (SafeJoin-аналог): файлы вне rootPath отсекаем на границе сервиса —
        // ниже по цепочке провайдеры читают их с диска.
        var files = changedFiles
            .Where(f => IsInsideRoot(rootPath, f))
            .ToList();
        if (files.Count == 0) return;

        var normalized = WorkspaceKnowledgeStore.NormalizePath(rootPath);

        // Дебаунс: накапливаем изменения, сбрасываем таймер на 15с
        if (_pendingRebuilds.TryGetValue(normalized, out var existing))
        {
            lock (existing.Files)
            {
                existing.Files.AddRange(files);
            }
            existing.Reset();
        }
        else
        {
            var state = new RebuildState(normalized, _rebuildDebounceMs, Rebuild);
            state.Files.AddRange(files);
            _pendingRebuilds[normalized] = state;
            state.Start();
        }

        _logger.LogDebug("Запланировано обновление графа для {Path}: +{Count} файлов", rootPath, files.Count);
    }

    /// <summary>
    /// Перестроить граф для проекта (вызывается по таймеру дебаунса).
    /// async void — обработчик Elapsed System.Timers.Timer; все исключения ловим внутри,
    /// отмена — через CTS в _activeRebuilds (новый rebuild) и _stopping (Dispose).
    /// </summary>
    private async void Rebuild(string normalizedPath)
    {
        CancellationTokenSource? active = null;
        try
        {
            // Состояние уже снято явным RebuildAsync/Dispose — перестраивать нечего.
            if (!_pendingRebuilds.TryRemove(normalizedPath, out var state))
                return;
            state.Dispose();

            // Прошлый rebuild этого проекта ещё бежит — отменяем: свежий его замещает.
            CancelActiveRebuild(normalizedPath);
            active = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _activeRebuilds[normalizedPath] = active;
            var ct = active.Token;

            // Находим проект по rootPath (если существует)
            var project = _projects.GetByRootPath(normalizedPath).FirstOrDefault();
            var rootPath = project?.RootPath ?? normalizedPath;

            // Группируем изменённые файлы по extension
            var changedByExtension = GroupByExtension(state.Files);

            _logger.LogInformation("Перестроение графа для {Path} (changed: {Count})",
                rootPath, state.Files.Count);

            var graph = await BuildInternalAsync(rootPath, changedByExtension, ct);
            await _persistence.SaveAsync(rootPath, graph, ct);

            _logger.LogInformation("Граф перестроен для {Path}: {Nodes} узлов, {Edges} рёбер",
                rootPath, graph.Nodes.Count, graph.Edges.Count);
        }
        catch (OperationCanceledException)
        {
            // Отмена при новом rebuild/остановке сервиса — штатно, не ошибка.
            _logger.LogDebug("Перестроение графа для {Path} отменено", normalizedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка перестроения графа для {Path}", normalizedPath);
        }
        finally
        {
            if (active is not null)
            {
                // Снимаем из реестра только СЕБЯ: на нашем месте может уже быть CTS
                // следующего rebuild — его не трогаем.
                _activeRebuilds.TryRemove(
                    new KeyValuePair<string, CancellationTokenSource>(normalizedPath, active));
                active.Dispose();
            }
        }
    }

    /// <summary>Отменить бегущее фоновое перестроение проекта (если есть).</summary>
    private void CancelActiveRebuild(string normalizedPath)
    {
        if (_activeRebuilds.TryRemove(normalizedPath, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Лежит ли file внутри rootPath. Сравнение по полным путям с сегментной границей,
    /// как IsInside в BackupPaths; некорректный путь считается «вне».
    /// </summary>
    private static bool IsInsideRoot(string rootPath, string file)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            var full = Path.GetFullPath(file);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Разбить файлы по extension (нижний регистр с точкой): ".cs" → [файлы...].
    /// </summary>
    private static Dictionary<string, List<string>> GroupByExtension(List<string> files)
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) continue;
            if (!dict.TryGetValue(ext, out var list))
            {
                list = new List<string>();
                dict[ext] = list;
            }
            list.Add(f);
        }
        return dict;
    }

    /// <summary>
    /// Построить граф из всех зарегистрированных провайдеров.
    /// Если changedByExtension задан — провайдер с changed получает UpdateAsync, прочие — BuildAsync.
    /// </summary>
    private async Task<Core.CodeGraph> BuildInternalAsync(
        string rootPath,
        Dictionary<string, List<string>>? changedByExtension,
        CancellationToken ct)
    {
        var allNodes = new Dictionary<string, CodeGraphNode>();
        var allEdges = new List<CodeGraphEdge>();

        // Каждый провайдер: UpdateAsync (если есть changed для его extension) иначе BuildAsync.
        var tasks = _providers.Select(async kvp =>
        {
            var ext = kvp.Key;
            var provider = kvp.Value;
            try
            {
                Core.CodeGraph graph;
                if (changedByExtension is not null
                    && changedByExtension.TryGetValue(ext, out var files)
                    && files.Count > 0)
                {
                    graph = await provider.UpdateAsync(rootPath, files, ct);
                }
                else
                {
                    graph = await provider.BuildAsync(rootPath, ct);
                }
                return (graph.Nodes, graph.Edges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Провайдер {Provider} упал при построении графа для {Path}",
                    provider.GetType().Name, rootPath);
                return (Nodes: new Dictionary<string, CodeGraphNode>(), Edges: new List<CodeGraphEdge>());
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        foreach (var (nodes, edges) in results)
        {
            foreach (var (id, node) in nodes)
            {
                if (!allNodes.ContainsKey(id))
                    allNodes[id] = node;
            }
            allEdges.AddRange(edges);
        }

        return new Core.CodeGraph { Nodes = allNodes, Edges = allEdges };
    }

    /// <summary>
    /// Состояние дебаунса: накопленные changedFiles + самосбрасывающийся таймер.
    /// Dispose останавливает таймер — после явного rebuild/остановки сервиса обработчик
    /// не должен стрелять (иначе дублирующее перестроение).
    /// </summary>
    private sealed class RebuildState : IDisposable
    {
        private readonly string _normalizedPath;
        private readonly int _delayMs;
        private readonly Action<string> _onFire;
        private System.Timers.Timer? _timer;

        public List<string> Files { get; } = new();

        public RebuildState(string normalizedPath, int delayMs, Action<string> onFire)
        {
            _normalizedPath = normalizedPath;
            _delayMs = delayMs;
            _onFire = onFire;
        }

        public void Start()
        {
            _timer = new System.Timers.Timer(_delayMs) { AutoReset = false };
            _timer.Elapsed += (_, _) => _onFire(_normalizedPath);
            _timer.Start();
        }

        /// <summary>Сбросить таймер (новое изменение в окне дебаунса).</summary>
        public void Reset()
        {
            var t = _timer;
            if (t is null) { Start(); return; }
            t.Stop();
            t.Start();
        }

        public void Dispose()
        {
            var t = _timer;
            _timer = null;
            if (t is not null)
            {
                t.Stop();
                t.Dispose();
            }
        }
    }
}
