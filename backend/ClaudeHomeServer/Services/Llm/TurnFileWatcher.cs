using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Llm;

// Настройки шумоподавления ватчера (из секции конфига FileWatcher). Дефолты покрывают
// служебные каталоги инструментов (.omc, .claude), вложения чата (FileService.AttachmentsDir),
// артефакты сборки и временные файлы, чтобы командные ходы (OmO, workflow) не спамили ленту
// чата чужими изменениями, а загрузка вложения не выглядела правкой файла проекта.
public sealed record FileWatcherOptions(
    IReadOnlyList<string> IgnoreDirs,
    IReadOnlyList<string> IgnoreFilePatterns,
    bool RespectGitignore)
{
    public static readonly FileWatcherOptions Default = new(
        IgnoreDirs: [".git", ".omc", ".claude", ".cc-attachments", "node_modules", "obj", "bin", "dist", ".vs", ".idea", ".playwright"],
        IgnoreFilePatterns: ["*~", "*.tmp", "*.tmp.*"],
        RespectGitignore: true);
}

// Тайминги обработки FS-события. В проде — Default; тесты укорачивают паузы и получают
// точку синхронизации BeforeExternalRetry, чтобы гонку «заявка пришла внутри retry-окна»
// воспроизводить событием, а не сном (на слабом CI-раннере сон уезжает и тест мигает).
public sealed record FileWatcherTiming(
    TimeSpan Debounce,
    TimeSpan ExternalRetry,
    // Вызывается ПЕРЕД паузой retry-атрибуции, аргумент — полный путь файла. В проде null.
    Func<string, Task>? BeforeExternalRetry = null)
{
    // Debounce 400мс — склейка серии событий одной записи файла. ExternalRetry 1.5с — пауза
    // перед вердиктом «правка вне чата»: заявка автора подтверждается только по успешному
    // tool_result (ClaudeSession), который при медленном стриме приходит ПОЗЖЕ самого файла —
    // debounce-проверки на это не хватает, и правка чужого параллельного хода уезжала в ленту
    // этого чата как «Изменение вне чата». Один повтор после паузы закрывает окно гонки.
    public static readonly FileWatcherTiming Default = new(
        Debounce: TimeSpan.FromMilliseconds(400),
        ExternalRetry: TimeSpan.FromSeconds(1.5));
}

// Следит за изменениями файлов в рабочей папке на время хода и шлёт FileChangedMessage.
// Один экземпляр на сессию: кэш содержимого живёт между ходами, чтобы diff считался
// от последнего известного состояния. Общий для всех адаптеров.
public sealed class TurnFileWatcher : IDisposable
{
    private readonly string _rootPath;
    private readonly Func<ServerMessage, Task> _onMessage;
    private readonly FileWatcherOptions _options;
    private readonly HashSet<string> _ignoreDirs;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string?> _fileCache = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounce = new();
    // Кэш вердикта git check-ignore по полному пути (живёт на сессию — файлы те же
    // ход за ходом, а запуск git-процесса на каждое событие дорог)
    private readonly ConcurrentDictionary<string, bool> _gitIgnoreCache = new();
    private readonly bool _isGitRepo;
    // Атрибуция file_changed чату-источнику (см. FileChangeAttributor): null — фильтрация
    // выключена (тесты, сессия без владельца), как и раньше.
    private readonly FileChangeAttributor? _attributor;
    private readonly string? _ownerSessionId;
    private readonly FileWatcherTiming _timing;

    public TurnFileWatcher(string rootPath, Func<ServerMessage, Task> onMessage, FileWatcherOptions? options = null,
        FileChangeAttributor? attributor = null, string? ownerSessionId = null, FileWatcherTiming? timing = null)
    {
        _rootPath = rootPath;
        _onMessage = onMessage;
        _options = options ?? FileWatcherOptions.Default;
        _timing = timing ?? FileWatcherTiming.Default;
        _attributor = attributor;
        _ownerSessionId = ownerSessionId;
        _ignoreDirs = new HashSet<string>(_options.IgnoreDirs, StringComparer.OrdinalIgnoreCase);
        // Дешёвая проверка «это git-репо»: .git — каталог (обычный клон) или файл
        // (worktree/submodule). Без неё git check-ignore в не-git папке впустую
        // плодит процессы с кодом 128 на каждый новый путь.
        var gitDir = Path.Combine(rootPath, ".git");
        _isGitRepo = Directory.Exists(gitDir) || File.Exists(gitDir);
    }

    public void Start()
    {
        if (!Directory.Exists(_rootPath)) return;
        // Повторный Start без Stop (новый ход при опоздавшей финализации старого прогона)
        // не должен утекать прежним FileSystemWatcher
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        // Отменяем токены ДО Clear: карточка с retry-паузой атрибуции не должна
        // выстрелить после Stop (конец хода) — токен живёт до конца обработки
        foreach (var cts in _debounce.Values) cts.Cancel();
        _debounce.Clear();
    }

    public void Dispose() => Stop();

    private void OnFileSystemEvent(object _, FileSystemEventArgs e)
    {
        var fullPath = e.FullPath;
        // Дешёвый чёрный список (каталоги/маски имён) — до debounce и запуска git
        if (ShouldIgnore(fullPath)) return;

        if (_debounce.TryRemove(fullPath, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _debounce[fullPath] = cts;
        _ = ProcessFileEventAsync(fullPath, cts);
    }

    // Обработка одного FS-события: debounce → чёрный список/gitignore → diff → атрибуция
    // → карточка. Запись в _debounce снимается только в finally: токен живёт ДО КОНЦА
    // обработки, и новое событие того же файла отменяет не только debounce, но и паузу
    // retry-атрибуции — иначе параллельный цикл дал бы вторую карточку по старому срезу.
    private async Task ProcessFileEventAsync(string fullPath, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await Task.Delay(_timing.Debounce, token);
            if (!File.Exists(fullPath) && !_fileCache.ContainsKey(fullPath)) return;
            // .gitignore проверяем после debounce (реже) и до чтения файла
            if (IsGitIgnored(fullPath)) return;

            var rel = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
            var newContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            _fileCache.TryGetValue(fullPath, out var oldContent);
            // Кэш обновляем ДО проверки атрибуции: подавленная карточка не должна оставить
            // это правку в diff-базе для следующего события своего же хода
            _fileCache[fullPath] = newContent;
            // Гасим только реальный no-op (содержимое побайтово то же). Раньше здесь стояла
            // проверка added==0 && removed==0 от CountLineDiff — из-за неё правка, менявшая
            // содержимое строки без изменения их числа (замена значения, переименование),
            // считалась 0/0 и карточка не уходила вовсе.
            if (newContent == oldContent) return;
            var (added, removed) = CountLineDiff(oldContent, newContent);
            // Атрибуция «чей файл». Чужая активная заявка гасит карточку (её покажет
            // собственный watcher чата-источника). А вот отсутствие заявок на момент
            // debounce (400мс) ничего не значит: заявка автора правки подтверждается
            // только по успешному tool_result, который при медленном стриме приходит
            // ПОЗЖЕ самого файла — 400мс на это не хватает, и правка чужого
            // параллельного хода уезжала в ленту этого чата как «Изменение вне чата»
            // (а через историю — в «файлы этого чата» на панели Изменений). Поэтому
            // кандидат на «вне чата» перепроверяется после дополнительной паузы
            // (FileWatcherTiming.ExternalRetry): за суммарные ~1.9с опоздавший
            // tool_result успевает дойти.
            var external = false;
            if (_attributor is not null && _ownerSessionId is not null)
            {
                if (_attributor.IsClaimedByOther(_ownerSessionId, fullPath)) return;
                if (!_attributor.IsClaimedBySelf(_ownerSessionId, fullPath))
                {
                    // Точка синхронизации для тестов (в проде null): позволяет заявке
                    // прийти строго внутри retry-окна, без гонки со сном
                    if (_timing.BeforeExternalRetry is { } beforeRetry) await beforeRetry(fullPath);
                    await Task.Delay(_timing.ExternalRetry, token);
                    // За паузу автор определился: чужая заявка — гасим (карточку покажет
                    // его собственный watcher), своя заявка (опоздавший tool_result) —
                    // правка нашего хода (external=false), по-прежнему никаких заявок —
                    // правка извне (человек, форматтер, bash)
                    if (_attributor.IsClaimedByOther(_ownerSessionId, fullPath)) return;
                    external = !_attributor.IsClaimedBySelf(_ownerSessionId, fullPath);
                }
            }
            _ = _onMessage(new FileChangedMessage(rel, added, removed, external));
        }
        catch (OperationCanceledException) { /* Stop/новый цикл debounce — карточка не нужна */ }
        catch { /* файл занят/удалён между событиями watcher-а — пропускаем */ }
        finally
        {
            // Снять свою запись с дебаунса, только если она всё ещё наша: более свежая
            // гонка событий могла уже заменить её новым CTS — его снимать нельзя.
            // TryRemove(KeyValuePair) атомарен (снимает ровно эту пару), в отличие от
            // пары TryGetValue+TryRemoveByKey с окном подмены между ними
            _debounce.TryRemove(KeyValuePair.Create(fullPath, cts));
        }
    }

    // Игнор по служебным каталогам-сегментам пути и маскам имени файла (из конфига).
    private bool ShouldIgnore(string fullPath)
    {
        var rel = Path.GetRelativePath(_rootPath, fullPath);
        // Путь вне rootPath (GetRelativePath вернул абсолютный/«..») — не наш, игнор
        if (Path.IsPathRooted(rel) || rel.StartsWith("..")) return true;
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Любой каталог-сегмент (кроме последнего — имени файла) в чёрном списке
        for (var i = 0; i < segments.Length - 1; i++)
            if (_ignoreDirs.Contains(segments[i])) return true;
        var fileName = segments[^1];
        foreach (var pattern in _options.IgnoreFilePatterns)
            if (FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true)) return true;
        return false;
    }

    // Игнорируется ли путь git-ом (git check-ignore). Только в git-репо и при
    // включённой опции; вердикт кэшируется на сессию.
    private bool IsGitIgnored(string fullPath)
    {
        if (!_options.RespectGitignore || !_isGitRepo) return false;
        return _gitIgnoreCache.GetOrAdd(fullPath, p =>
        {
            try
            {
                var psi = new ProcessStartInfo("git")
                {
                    WorkingDirectory = _rootPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[] { "check-ignore", "-q", p }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi)!;
                if (!proc.WaitForExit(1500))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return false;
                }
                // exit 0 — путь игнорируется; 1 — нет; 128 — ошибка/не репо
                return proc.ExitCode == 0;
            }
            catch { return false; }
        });
    }

    // Мультисет-diff по содержимому строк, а не разница счётчиков: O(n) через подсчёт
    // вхождений — правка строки (без изменения их числа) даёт честные 1 добавлена/1 удалена
    // вместо 0/0. Не различает перестановку строк без изменения контента — компромисс ради
    // O(n) вместо LCS/Myers, чтобы большие файлы не подвешивали ход.
    private static (int added, int removed) CountLineDiff(string? oldContent, string? newContent)
    {
        var oldLines = oldContent?.Split('\n') ?? [];
        var newLines = newContent?.Split('\n') ?? [];

        var counts = new Dictionary<string, int>(oldLines.Length);
        foreach (var line in oldLines)
            counts[line] = counts.GetValueOrDefault(line) + 1;

        var added = 0;
        foreach (var line in newLines)
        {
            if (counts.TryGetValue(line, out var count) && count > 0)
                counts[line] = count - 1;
            else
                added++;
        }

        var removed = counts.Values.Sum(c => Math.Max(c, 0));
        return (added, removed);
    }
}
