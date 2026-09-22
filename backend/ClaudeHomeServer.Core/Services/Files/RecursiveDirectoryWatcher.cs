using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeHomeServer.Services.Files;

/// <summary>Что случилось с путём. Переименование отдаётся парой Deleted+Created.</summary>
public enum DirectoryWatchKind { Created, Changed, Deleted }

/// <summary>
/// Сбой наблюдения. Различать их обязательно: <see cref="EventsLost"/> лечится ТОЛЬКО
/// пересинхронизацией у потребителя (наблюдатель жив, пересоздание тут — рекурсия
/// инцидента 2026-09-19), <see cref="Stopped"/> — пересозданием наблюдателя.
/// </summary>
public enum DirectoryWatchFailure
{
    /// <summary>Часть событий потеряна (переполнение очереди), наблюдение продолжается.</summary>
    EventsLost,
    /// <summary>Наблюдения больше нет: дескриптор непригоден, события не придут вовсе.</summary>
    Stopped,
}

/// <summary>
/// Рекурсивное наблюдение за деревом каталогов БЕЗ подписки на служебные каталоги.
///
/// Зачем свой примитив, а не <see cref="FileSystemWatcher"/> с IncludeSubdirectories:
/// на Linux .NET ставит inotify-слежку на КАЖДЫЙ каталог дерева, а чёрные списки
/// потребителей фильтруют только события. На репозитории ClaudeCodeServer это 16 390
/// слежек за один ход при дефолтном потолке ядра 65 536 (разбор OOM 2026-09-22:
/// бэкенд держал 61 325, после чего user-инстанс systemd переставал заводить слежку
/// на новые scope — «No space left on device»). Каталогов без служебных там 438.
///
/// Разбить дерево на поддеревья и дать каждому свой FileSystemWatcher не выходит:
/// у ~20 .csproj-каталогов есть bin/obj, поэтому минимальное разбиение этого репозитория
/// даёт 178 наблюдателей = 178 inotify-ЭКЗЕМПЛЯРОВ при дефолтном потолке 128. Кривая
/// обмена плохая в обе стороны (потолок 32 наблюдателя → 4 465 слежек), поэтому на Linux
/// подписка ведётся напрямую: ОДИН экземпляр inotify и слежка ровно на те каталоги,
/// которые нужны. На остальных платформах — прежний FileSystemWatcher (там дерево
/// наблюдается одним дескриптором ядра и проблемы нет).
///
/// Инвариант: экземпляр inotify всегда один, сколько бы каталогов ни было в дереве.
/// </summary>
public sealed class RecursiveDirectoryWatcher : IDisposable
{
    // Потолок слежек на наблюдатель — гарантия конструкции, а не тюнинг: он держит
    // бюджет и на чужом дереве, форма которого нам неизвестна. Достигли — новые
    // каталоги не подписываются (события оттуда теряются), наблюдение живёт дальше.
    public const int DefaultMaxWatches = 4000;

    private readonly string _root;
    private readonly HashSet<string> _excludeDirs;
    private readonly Action<string, DirectoryWatchKind> _onEvent;
    private readonly Action<string>? _onWarning;
    private readonly Action<DirectoryWatchFailure>? _onFailure;
    private readonly int _maxWatches;
    private readonly bool _includeDirectories;
    private readonly int _bufferBytes;
    private IWatchBackend? _backend;

    /// <param name="root">Корень дерева.</param>
    /// <param name="excludeDirs">Имена каталогов, которые не обходятся и не подписываются
    /// (на любой глубине). Тот же список, по которому потребитель фильтрует события:
    /// второй список стал бы второй точкой правды и дал бы «подписались, но глушим».</param>
    /// <param name="onEvent">Колбэк события по файлу. Зовётся с потока чтения — работа
    /// в нём должна быть короткой, а замки, под которыми гасится сам наблюдатель, брать
    /// в нём нельзя: <see cref="Dispose"/> ждёт выхода этого потока.</param>
    /// <param name="maxWatches">Потолок слежек на наблюдатель (Linux).</param>
    /// <param name="onWarning">Куда сообщать о частичных отказах (по одному разу на причину).</param>
    /// <param name="includeDirectories">Отдавать ли события самих каталогов. Нужно тем, кто
    /// показывает дерево файлов (FileWatcherService); ватчеру хода каталоги не интересны.</param>
    /// <param name="bufferBytes">Размер буфера событий FileSystemWatcher (не-Linux);
    /// 0 — дефолт ОС. Дефолтные 8 КБ переполняются на git checkout / npm install.</param>
    /// <param name="onFailure">Сбой наблюдения: потеря событий либо смерть наблюдателя.</param>
    public RecursiveDirectoryWatcher(string root, IEnumerable<string> excludeDirs,
        Action<string, DirectoryWatchKind> onEvent, int maxWatches = DefaultMaxWatches,
        Action<string>? onWarning = null, bool includeDirectories = false,
        int bufferBytes = 0, Action<DirectoryWatchFailure>? onFailure = null)
    {
        _root = root;
        _excludeDirs = new HashSet<string>(excludeDirs, StringComparer.OrdinalIgnoreCase);
        _onEvent = onEvent;
        _maxWatches = Math.Max(1, maxWatches);
        _onWarning = onWarning;
        _includeDirectories = includeDirectories;
        _bufferBytes = bufferBytes;
        _onFailure = onFailure;
    }

    /// <summary>Поднять наблюдение. Повторный вызов снимает прежнее. Отказ на КОРНЕ —
    /// исключение (лимит ОС, нет прав): ход идёт без карточек изменений, но не падает.</summary>
    public void Start()
    {
        Stop();
        if (!Directory.Exists(_root)) return;
        _backend = OperatingSystem.IsLinux()
            ? new InotifyBackend(_root, _excludeDirs, Emit, _maxWatches, Warn, _includeDirectories, Fail)
            : new FileSystemWatcherBackend(_root, Emit, _includeDirectories, _bufferBytes, Fail);
    }

    public void Stop()
    {
        var backend = _backend;
        _backend = null;
        backend?.Dispose();
    }

    public void Dispose() => Stop();

    public bool IsWatching => _backend is not null;

    /// <summary>Сколько слежек держит наблюдатель сейчас (для тестов и диагностики);
    /// у FileSystemWatcher-ветки счёт ведёт ядро, поэтому -1.</summary>
    internal int WatchCount => _backend?.WatchCount ?? 0;

    private void Emit(string path, DirectoryWatchKind kind)
    {
        try { _onEvent(path, kind); }
        catch { /* колбэк потребителя не должен ронять поток чтения */ }
    }

    private void Warn(string message)
    {
        try { _onWarning?.Invoke(message); }
        catch { /* диагностика не должна ронять поток чтения */ }
    }

    private void Fail(DirectoryWatchFailure failure)
    {
        try { _onFailure?.Invoke(failure); }
        catch { /* реакция потребителя не должна ронять поток чтения */ }
    }

    private interface IWatchBackend : IDisposable
    {
        int WatchCount { get; }
    }

    // ── Windows/macOS: прежнее поведение. Дерево там наблюдается одним дескриптором ядра ──
    private sealed class FileSystemWatcherBackend : IWatchBackend
    {
        private readonly FileSystemWatcher _watcher;

        public FileSystemWatcherBackend(string root, Action<string, DirectoryWatchKind> emit,
            bool includeDirectories, int bufferBytes, Action<DirectoryWatchFailure> fail)
        {
            var filter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            // Имена каталогов и размер — только тому, кто показывает дерево: ватчеру хода
            // события каталогов не нужны, а Size добавляет ему лишние Changed на записях файлов.
            if (includeDirectories) filter |= NotifyFilters.DirectoryName | NotifyFilters.Size;
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = filter,
            };
            if (bufferBytes > 0) _watcher.InternalBufferSize = bufferBytes;
            _watcher.Changed += (_, e) => emit(e.FullPath, DirectoryWatchKind.Changed);
            _watcher.Created += (_, e) => emit(e.FullPath, DirectoryWatchKind.Created);
            _watcher.Deleted += (_, e) => emit(e.FullPath, DirectoryWatchKind.Deleted);
            // Переименование — пара Deleted+Created, как обещает контракт вида события:
            // без неё старое имя оставалось бы в дереве файлов до ручного обновления.
            _watcher.Renamed += (_, e) =>
            {
                emit(e.OldFullPath, DirectoryWatchKind.Deleted);
                emit(e.FullPath, DirectoryWatchKind.Created);
            };
            // Переполнение внутреннего буфера и отказ слежки на новой папке .NET отдаёт
            // одинаково — как Error; отличить их нечем, поэтому лечим пересозданием.
            _watcher.Error += (_, _) => fail(DirectoryWatchFailure.Stopped);
            // Включение — отдельным шагом: бросок из EnableRaisingEvents (лимит ОС) иначе
            // оставил бы созданный наблюдатель неосвобождённым.
            try { _watcher.EnableRaisingEvents = true; }
            catch { _watcher.Dispose(); throw; }
        }

        public int WatchCount => -1;

        public void Dispose() => _watcher.Dispose();
    }

    // ── Linux: один экземпляр inotify, слежки по явному перечню каталогов ──
    private sealed class InotifyBackend : IWatchBackend
    {
        private const uint IN_MODIFY = 0x00000002;
        private const uint IN_CLOSE_WRITE = 0x00000008;
        private const uint IN_MOVED_FROM = 0x00000040;
        private const uint IN_MOVED_TO = 0x00000080;
        private const uint IN_CREATE = 0x00000100;
        private const uint IN_DELETE = 0x00000200;
        private const uint IN_DELETE_SELF = 0x00000400;
        private const uint IN_MOVE_SELF = 0x00000800;
        private const uint IN_Q_OVERFLOW = 0x00004000;
        private const uint IN_IGNORED = 0x00008000;
        private const uint IN_ONLYDIR = 0x01000000;
        private const uint IN_EXCL_UNLINK = 0x04000000;
        private const uint IN_ISDIR = 0x40000000;

        // Слежка ставится только на каталоги (IN_ONLYDIR) и не тянет события по уже
        // отвязанным файлам (IN_EXCL_UNLINK).
        private const uint WatchMask = IN_MODIFY | IN_CLOSE_WRITE | IN_CREATE | IN_DELETE
                                     | IN_MOVED_FROM | IN_MOVED_TO | IN_DELETE_SELF | IN_MOVE_SELF
                                     | IN_ONLYDIR | IN_EXCL_UNLINK;

        private const int O_NONBLOCK = 0x800;
        private const int O_CLOEXEC = 0x80000;
        private const short POLLIN = 0x001;
        private const int EINTR = 4;
        private const int EAGAIN = 11;
        private const int EventHeaderSize = 16; // wd(4) + mask(4) + cookie(4) + len(4)

        private readonly HashSet<string> _excludeDirs;
        private readonly Action<string, DirectoryWatchKind> _emit;
        private readonly Action<string> _warn;
        private readonly Action<DirectoryWatchFailure> _fail;
        private readonly bool _includeDirectories;
        private readonly int _maxWatches;
        private readonly Dictionary<int, string> _paths = new();
        private readonly Lock _lock = new();
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
        private readonly int _fd;
        private readonly int _wakeFd;
        private readonly Thread _reader;
        private volatile bool _stopping;

        public InotifyBackend(string root, HashSet<string> excludeDirs,
            Action<string, DirectoryWatchKind> emit, int maxWatches, Action<string> warn,
            bool includeDirectories, Action<DirectoryWatchFailure> fail)
        {
            _excludeDirs = excludeDirs;
            _emit = emit;
            _warn = warn;
            _fail = fail;
            _includeDirectories = includeDirectories;
            _maxWatches = maxWatches;

            _fd = inotify_init1(O_NONBLOCK | O_CLOEXEC);
            if (_fd < 0) throw LastError("inotify_init1");
            _wakeFd = eventfd(0, O_NONBLOCK | O_CLOEXEC);
            if (_wakeFd < 0)
            {
                var err = LastError("eventfd");
                close(_fd);
                throw err;
            }

            try
            {
                // Корень обязан встать: без него наблюдения нет вовсе — это отказ старта,
                // а не частичный отказ отдельного каталога.
                if (TryAddWatch(root) != WatchAdd.Added)
                {
                    var err = LastError($"inotify_add_watch({root})");
                    throw err;
                }
                AddSubtree(root, emitExistingFiles: false);
            }
            catch
            {
                close(_fd);
                close(_wakeFd);
                throw;
            }

            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "inotify-watch",
            };
            _reader.Start();
        }

        public int WatchCount { get { lock (_lock) return _paths.Count; } }

        // Дескрипторы закрываются ТОЛЬКО после выхода потока чтения: закрытие fd под
        // ожиданием в poll/read — ровно та форма, что 19.09 оставила 8 076 брошенных
        // экземпляров inotify (поток висит в read, будить его нечем). Поэтому
        // пробуждение идёт через eventfd, а close — после Join.
        public void Dispose()
        {
            _stopping = true;
            Wake();
            if (!_reader.Join(TimeSpan.FromSeconds(5)))
            {
                _warn("поток чтения inotify не вышел за 5 с — дескрипторы остаются до конца процесса");
                return;
            }
            close(_fd);
            close(_wakeFd);
        }

        private void Wake()
        {
            var one = BitConverter.GetBytes(1UL);
            write(_wakeFd, one, one.Length);
        }

        /// <summary>Исход подписки: встала (в том числе повторно на тот же путь),
        /// оказалась вторым именем уже наблюдаемого каталога, не встала вовсе.</summary>
        private enum WatchAdd { Added, Alias, Failed }

        // Подписка на каталог. Не Added — вглубь идти нельзя; причина отказа ядра
        // остаётся в errno для вызывающего.
        private WatchAdd TryAddWatch(string dir)
        {
            lock (_lock)
            {
                if (_paths.Count >= _maxWatches)
                {
                    WarnOnce("max-watches",
                        $"достигнут потолок слежек ({_maxWatches}) — новые каталоги не наблюдаются");
                    return WatchAdd.Failed;
                }
            }
            var wd = inotify_add_watch(_fd, dir, WatchMask);
            if (wd < 0) return WatchAdd.Failed;
            lock (_lock)
            {
                // Тот же inode даёт тот же wd. Если под ним уже записан ДРУГОЙ путь —
                // это второе имя того же каталога внутри дерева (bind-mount): перезапись
                // сделала бы путь живых событий фантомным, поэтому запись не трогаем и
                // вглубь по второму имени не идём.
                if (_paths.TryGetValue(wd, out var known)
                    && !string.Equals(known, dir, StringComparison.Ordinal))
                    return WatchAdd.Alias;
                _paths[wd] = dir;
            }
            return WatchAdd.Added;
        }

        // Ссылка-на-каталог: .NET на Linux отдаёт её в GetDirectories как обычный каталог
        // и идёт внутрь. Петля (`loop -> ..`) даёт бесконечную цепочку путей, которую
        // потолок слежек НЕ останавливает (тот же inode — тот же wd, счёт не растёт), а
        // ссылка наружу тратит бюджет на чужое дерево и отдаёт события по путям вне корня.
        // Ссылки на ФАЙЛЫ тут ни при чём: слежка ставится только на каталоги.
        private static bool IsSymlink(string dir)
        {
            try { return new DirectoryInfo(dir).LinkTarget is not null; }
            catch { return true; } // не смогли определить — безопаснее пропустить
        }

        // Обход поддерева с обрезкой служебных каталогов. emitExistingFiles — для каталогов,
        // появившихся УЖЕ ПОСЛЕ старта: файлы внутри могли создаться до того, как слежка
        // встала, и их события иначе теряются молча.
        private void AddSubtree(string root, bool emitExistingFiles)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; } // каталог исчез или нет прав — не предмет наблюдения
                foreach (var sub in subdirs)
                {
                    if (_excludeDirs.Contains(Path.GetFileName(sub))) continue;
                    if (IsSymlink(sub)) continue; // по ссылкам не ходим: петли и выход за корень
                    switch (TryAddWatch(sub))
                    {
                        case WatchAdd.Added:
                            stack.Push(sub);
                            // Каталог появился уже после старта — потребителю дерева он нужен
                            // так же, как файлы внутри него.
                            if (emitExistingFiles && _includeDirectories)
                                _emit(sub, DirectoryWatchKind.Created);
                            break;
                        case WatchAdd.Alias:
                            WarnOnce("alias-watch",
                                $"{sub} — второе имя уже наблюдаемого каталога, поддерево пропущено");
                            break;
                        default:
                            WarnOnce("add-watch", $"не удалось завести слежку на {sub} — каталог пропущен");
                            break;
                    }
                }
                if (!emitExistingFiles) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir)) _emit(file, DirectoryWatchKind.Created);
                }
                catch { /* каталог исчез между обходом и чтением */ }
            }
        }

        // Снятие слежек поддерева: после переноса каталога ядро оставляет слежку живой,
        // но её путь становится фантомным (IN_IGNORED на move не приходит).
        private void RemoveSubtree(string path)
        {
            var prefix = path + Path.DirectorySeparatorChar;
            List<int> gone;
            lock (_lock)
            {
                gone = _paths.Where(p => p.Value == path || p.Value.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(p => p.Key).ToList();
                foreach (var wd in gone) _paths.Remove(wd);
            }
            foreach (var wd in gone) inotify_rm_watch(_fd, wd);
        }

        private void ReadLoop()
        {
            var buffer = new byte[64 * 1024];
            var fds = new PollFd[2];
            while (!_stopping)
            {
                fds[0] = new PollFd { Fd = _fd, Events = POLLIN };
                fds[1] = new PollFd { Fd = _wakeFd, Events = POLLIN };
                var ready = poll(fds, 2, -1);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EINTR) continue;
                    // Дескриптор непригоден — наблюдения больше нет. Молча выйти нельзя:
                    // потребитель считал бы, что изменений просто не происходит.
                    Die("poll");
                    return;
                }
                if ((fds[1].Revents & POLLIN) != 0) return; // Dispose
                if ((fds[0].Revents & POLLIN) == 0) continue;

                var read = (int)ReadEvents(buffer);
                if (read <= 0)
                {
                    if (read < 0 && Marshal.GetLastPInvokeError() is EAGAIN or EINTR) continue;
                    Die("read");
                    return;
                }
                ParseEvents(buffer, read);
            }
        }

        // Смерть цикла чтения: сообщается ОДИН раз и только если это не наш же Dispose
        // (там поток выходит штатно по eventfd).
        private void Die(string call)
        {
            if (_stopping) return;
            WarnOnce("dead", $"наблюдение за деревом прервано ({call}) — события больше не приходят");
            _fail(DirectoryWatchFailure.Stopped);
        }

        private nint ReadEvents(byte[] buffer)
        {
            try { return read(_fd, buffer, buffer.Length); }
            catch { return -1; }
        }

        private void ParseEvents(byte[] buffer, int length)
        {
            var offset = 0;
            while (offset + EventHeaderSize <= length)
            {
                var wd = BitConverter.ToInt32(buffer, offset);
                var mask = BitConverter.ToUInt32(buffer, offset + 4);
                var nameLength = (int)BitConverter.ToUInt32(buffer, offset + 12);
                string? name = null;
                if (nameLength > 0 && offset + EventHeaderSize + nameLength <= length)
                {
                    var raw = new ReadOnlySpan<byte>(buffer, offset + EventHeaderSize, nameLength);
                    var zero = raw.IndexOf((byte)0); // имя дополнено нулями до выравнивания
                    name = Encoding.UTF8.GetString(zero >= 0 ? raw[..zero] : raw);
                }
                offset += EventHeaderSize + nameLength;
                HandleEvent(wd, mask, name);
            }
        }

        private void HandleEvent(int wd, uint mask, string? name)
        {
            // Переполнение очереди: дескриптор остаётся годным, пересоздавать наблюдателя
            // нельзя (инцидент 19.09 — рекурсия пересозданий). Часть событий потеряна,
            // карточки изменений тут best-effort.
            if ((mask & IN_Q_OVERFLOW) != 0)
            {
                WarnOnce("overflow", "очередь inotify переполнена — часть изменений не показана");
                // Наблюдатель жив: лечится пересинхронизацией у потребителя, а не
                // пересозданием (пересоздание тут — ровно рекурсия инцидента 19.09).
                _fail(DirectoryWatchFailure.EventsLost);
                return;
            }

            string? dir;
            lock (_lock)
                if (!_paths.TryGetValue(wd, out dir)) return;

            // Ядро сняло слежку само (каталог удалён/размонтирован) — чистим карту.
            if ((mask & IN_IGNORED) != 0)
            {
                lock (_lock) _paths.Remove(wd);
                return;
            }
            if ((mask & (IN_DELETE_SELF | IN_MOVE_SELF)) != 0) return; // IN_IGNORED придёт следом

            if (name is null) return;
            if ((mask & IN_ISDIR) != 0)
            {
                if (_excludeDirs.Contains(name)) return;
                var subdir = Path.Combine(dir, name);
                if ((mask & (IN_CREATE | IN_MOVED_TO)) != 0)
                {
                    if (IsSymlink(subdir)) return; // по ссылкам не ходим — как и при обходе
                    // Появление каталога отдаётся ДО подписки и независимо от её исхода:
                    // в дереве файлов он уже есть, даже если слежку на него завести не удалось.
                    if (_includeDirectories) _emit(subdir, DirectoryWatchKind.Created);
                    switch (TryAddWatch(subdir))
                    {
                        case WatchAdd.Added:
                            AddSubtree(subdir, emitExistingFiles: true);
                            break;
                        case WatchAdd.Alias:
                            WarnOnce("alias-watch",
                                $"{subdir} — второе имя уже наблюдаемого каталога, поддерево пропущено");
                            break;
                        default:
                            WarnOnce("add-watch", $"не удалось завести слежку на {subdir} — каталог пропущен");
                            break;
                    }
                }
                else if ((mask & (IN_DELETE | IN_MOVED_FROM)) != 0)
                {
                    RemoveSubtree(subdir);
                    if (_includeDirectories) _emit(subdir, DirectoryWatchKind.Deleted);
                }
                // Дальше — только события файлов: сам каталог уже отдан, если потребителю
                // нужны каталоги, и не нужен вовсе, если нет.
                return;
            }

            var kind =
                (mask & (IN_CREATE | IN_MOVED_TO)) != 0 ? DirectoryWatchKind.Created :
                (mask & (IN_DELETE | IN_MOVED_FROM)) != 0 ? DirectoryWatchKind.Deleted :
                DirectoryWatchKind.Changed;
            _emit(Path.Combine(dir, name), kind);
        }

        private void WarnOnce(string reason, string message)
        {
            lock (_lock)
                if (!_warned.Add(reason)) return;
            _warn(message);
        }

        private const int EMFILE = 24;
        private const int ENFILE = 23;
        private const int ENOSPC = 28;
        private const int EACCES = 13;

        // Текст ошибки повторяет формулировки .NET для тех же errno намеренно: по ним
        // TurnErrorClassifier отличает исчерпание локального ресурса ОС от мёртвого
        // эндпоинта провайдера (инцидент 2026-09-19 — лимит inotify уехал в Unreachable,
        // и фолбэк сжёг три подписки, хотя сеть была ни при чём).
        private static IOException LastError(string call)
        {
            var errno = Marshal.GetLastPInvokeError();
            var reason = errno switch
            {
                EMFILE => "too many open files (EMFILE)",
                ENFILE => "system limit on the number of open files (ENFILE)",
                ENOSPC => "the user limit on the number of inotify instances/watches has been reached (ENOSPC)",
                EACCES => "permission denied (EACCES)",
                _ => $"errno {errno}",
            };
            return new IOException($"{call}: {reason}");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int inotify_init1(int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int inotify_add_watch(int fd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mask);

        [DllImport("libc", SetLastError = true)]
        private static extern int inotify_rm_watch(int fd, int wd);

        [DllImport("libc", SetLastError = true)]
        private static extern int eventfd(uint initval, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern nint read(int fd, byte[] buf, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern nint write(int fd, byte[] buf, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);
    }
}
