using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Plugins;

// Наблюдатель черновиков build-it-yourself (ADR-031, шаг 4.2): когда агент пишет файлы в
// data/plugins/{owner}/_drafts/{slug}/, открытая у человека карточка с предпросмотром панели
// должна обновляться сама. Наблюдение — через Core-примитив RecursiveDirectoryWatcher
// (один inotify-экземпляр на процесс, слежка по перечню каталогов, не FileSystemWatcher с
// IncludeSubdirectories — docs/architecture/file-watching.md).
//
// Число наблюдателей = 1 на процесс, а НЕ по одному на владельца с черновиками: потолок
// inotify-ЭКЗЕМПЛЯРОВ (128) жёстче, чем потолок слежек, и файл-watching.md считает именно
// экземпляры. Корень — data/plugins (НЕ packages/ и НЕ всё дерево): служебные каталоги
// (packages, _uploads) исключены по имени, подписываются только _drafts.
//
// Событие «черновик изменился» (slug + монотонная ревизия, БЕЗ содержимого файлов и хостовых
// путей) уходит ТОЛЬКО владельцу этого черновика (ISessionBroadcaster.ToOwner) и только если
// у него включён флаг plugins-build-yourself. События склеиваются (дебаунс 300 мс): запись
// десяти файлов подряд даёт одно-два события, а не десять.
public sealed class PluginDraftWatcher : BackgroundService
{
    // Дебаунс: 300 мс — каждое событие сбрасывает таймер, поэтому запись десяти файлов подряд
    // (каждое <300 мс) даёт одно-два события, а не десять.
    internal const int DefaultDebounceMs = 300;

    // data/plugins может не существовать (свежая установка, никто плагины не трогал): Start()
    // молчит, не падает. Периодически (каждые 5 с) пробуем поднять наблюдение, пока не встанет;
    // после старта примитив САМ подписывается на новые каталоги (RecursiveDirectoryWatcher.cs:
    // TryAddWatch+AddSubtree при IN_CREATE|IN_ISDIR), поэтому ручной resync на создание не нужен.
    internal const int RootRetryMs = 5000;

    // Служебные каталоги data/plugins, которые НЕ подписываются (по имени, на любой глубине):
    // packages (неизменяемые пакеты), _uploads (загруженные пакеты) и _rejected (отклонённые
    // черновики, шаг 4.3c — живут вне _drafts, наблюдателю не интересны). _drafts — подписывается.
    private static readonly string[] ExcludeDirs = ["packages", "_uploads", "_rejected"];

    private readonly PluginStore _store;
    private readonly ISessionBroadcaster _broadcaster;
    private readonly IFeatureFlagGate? _flags;
    private readonly ILogger<PluginDraftWatcher> _log;

    private readonly RecursiveDirectoryWatcher _watcher;
    private readonly object _lock = new();
    // (owner, slug) → монотонная ревизия (каждая правка файла в черновике её увеличивает).
    private readonly ConcurrentDictionary<(string Owner, string Slug), long> _revision = new();
    // (owner, slug) с ожидающим дебаунсом: в этом множестве — кому ещё не отправлено событие.
    private readonly HashSet<(string Owner, string Slug)> _pending = new();
    private Timer? _timer;
    private volatile bool _stopping;

    // Циклы дебаунса (rework D1, тесты без Task.Delay): каждый OnFsEvent начинает новый цикл
    // (_pendingCycle++), Flush (дебаунс сработал) фиксирует обработанный (_flushedCycle) и будит
    // _cycleTcs. Тест ждёт _flushedCycle >= _pendingCycle (тишина) — без реального сна.
    private long _pendingCycle;
    private long _flushedCycle;
    private TaskCompletionSource<bool>? _cycleTcs;

    // Окно дебаунса; тесты сужают его, чтобы склейка была детерминированной.
    internal int DebounceMs { get; set; } = DefaultDebounceMs;

    public PluginDraftWatcher(PluginStore store, ISessionBroadcaster broadcaster,
        IFeatureFlagGate? flags, ILogger<PluginDraftWatcher> log)
    {
        _store = store;
        _broadcaster = broadcaster;
        _flags = flags;
        _log = log;
        _watcher = new RecursiveDirectoryWatcher(
            _store.PluginsRoot,
            ExcludeDirs,
            OnFsEvent,
            includeDirectories: true,   // создание _drafts/{slug} — событие каталога
            onFailure: OnWatchFailure);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartWatch();
        try
        {
            // Ждём отмены; параллельно, пока наблюдение не встало (корня не было),
            // периодически пробуем его поднять. Встало — цикл просто дремлет.
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(RootRetryMs, stoppingToken); }
                catch (OperationCanceledException) { break; }
                if (!_watcher.IsWatching) StartWatch();
            }
        }
        finally
        {
            StopWatch();
        }
    }

    // internal для тестов: поднимает/гасит наблюдение без полного цикла BackgroundService.
    internal void StartWatch()
    {
        // Start() идемпотентен (повторный вызов снимает прежнее) и молчит, если корня нет.
        // Отказ на корне (лимит ОС, нет прав, неподдерживаемая платформа) — исключение:
        // наблюдение без карточек, но не падает (rework D1: шире фильтр, исключение не в пул).
        try { _watcher.Start(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or PlatformNotSupportedException or NotSupportedException)
        { _log.LogWarning(ex, "Plugins: не удалось поднять наблюдение за черновиками"); }
    }

    internal void StopWatch()
    {
        lock (_lock)
        {
            _stopping = true;
            _timer?.Dispose();
            _timer = null;
            _pending.Clear();
        }
        // Stop() идемпотентен и сериализован; Join потока чтения идёт БЕЗ нашего _lock
        // (OnFsEvent его берёт, но мы к этому моменту отпустили) — взаимоблокировки нет.
        _watcher.Stop();
    }

    // Колбэк события (зовётся с ПОТОКА ЧТЕНИЯ примитива): работа короткая, замки, под которыми
    // гасится наблюдатель, не берём. Разбор пути: {owner}/_drafts/{slug}/... → owner + slug.
    // Примитив (Emit) глотает исключения колбэка без следа: ловим и логируем сами (rework D1).
    private void OnFsEvent(string path, DirectoryWatchKind kind)
    {
        try
        {
            if (!TryParseDraft(path, out var owner, out var slug)) return;

            lock (_lock)
            {
                if (_stopping) return;
                _revision[(owner, slug)] = _revision.TryGetValue((owner, slug), out var rev) ? rev + 1 : 1;
                _pending.Add((owner, slug));
                // Новый цикл дебаунса: сбрасываем TCS тишины (rework D1, тесты без Task.Delay).
                _pendingCycle++;
                _cycleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_timer is null)
                    _timer = new Timer(_ => Flush(), null, DebounceMs, Timeout.Infinite);
                else
                    _timer.Change(DebounceMs, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Plugins: сбой обработки события черновика {Path}", path);
        }
    }

    // Сбой наблюдения (поток чтения): EventsLost — наблюдатель жив, лечится пересинхронизацией;
    // Stopped — наблюдения больше нет, пересоздаём. Оба — из потока чтения, поэтому уходим в
    // пул: Start() берёт замок примитива и Join'ит поток чтения (в нём же мы сейчас).
    private void OnWatchFailure(DirectoryWatchFailure failure) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_stopping) return;
            _log.LogInformation("Plugins: наблюдение за черновиками — {Failure}, пересинхронизация", failure);
            StartWatch();
        });

    // Дебаунс истёк: для каждого (owner, slug) в _pending отправляем ОДНО событие владельцу.
    // Запись десяти файлов подряд в один черновик → одна запись в _pending → одно событие.
    private void Flush()
    {
        List<(string Owner, string Slug, long Revision)> batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = _pending
                .Select(k => (k.Owner, k.Slug, _revision[k]))
                .ToList();
            _pending.Clear();
            // Дебаунс сработал: цикл завершён, будим ожидателя тишины (rework D1).
            _flushedCycle = _pendingCycle;
            _cycleTcs?.TrySetResult(true);
        }

        foreach (var (owner, slug, revision) in batch)
        {
            // Флаг владельца: выключен — событие не уходит (подсистема работает, но человек
            // «Собрать плагин» не включал). null-гейт (тесты/сборка без флагов) — шлём.
            if (_flags is not null && !_flags.IsEnabled(owner, FeatureFlagKeys.PluginsBuildYourself))
                continue;
            _ = _broadcaster.ToOwner(owner, new PluginDraftChangedMessage(slug, revision));
        }
    }

    // internal для тестов: ждать тишины — все начатые циклы дебаунса обработаны (Flush) и новых
    // событий нет. БЕЗ реального сна (rework D1): основной сигнал — TCS, который будит сам Flush
    // (дебаунс fired); если за это время начался новый цикл (TCS заменён) — короткая уступка и
    // переприхватываем текущий. Timeout — страховка от зависания (идиом как в WaitEventAsync).
    internal async Task WaitQuietAsync(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 <= deadline)
        {
            TaskCompletionSource<bool>? tcs;
            lock (_lock)
            {
                if (_flushedCycle >= _pendingCycle) return;   // тишина: всё обработано
                tcs = _cycleTcs;
            }
            if (tcs is null) return;                            // циклов не было вовсе
            await Task.WhenAny(tcs.Task, Task.Delay(20));      // ждём, пока Flush не побудит
        }
    }

    // Путь события → (owner, slug). Форма: {root}/{owner}/_drafts/{slug}/... (root = data/plugins).
    // Возвращает false, если путь не из _drafts (например, служебный каталог, который всё же
    // просочился, или файл вне черновика) — тогда событие не наше.
    private bool TryParseDraft(string path, out string owner, out string slug)
    {
        owner = slug = "";
        try
        {
            var rel = Path.GetRelativePath(_store.PluginsRoot, path).Replace('\\', '/');
            var parts = rel.Split('/');
            // {owner}/_drafts/{slug}/... — минимум 3 сегмента, второй — "_drafts".
            if (parts.Length < 3 || parts[1] != PluginStore.DraftsDirName) return false;
            owner = parts[0];
            slug = parts[2];
            return owner.Length > 0 && slug.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
