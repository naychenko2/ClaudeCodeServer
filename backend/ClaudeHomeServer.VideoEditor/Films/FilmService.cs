using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Assembly;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// Фильмы проекта (ADR-022 §2): список, состояние, правка патчем под ревизией. Только область проекта: у личной
// области нет диска проекта, и всё здесь отказывает ДО RootPath (personal_scope_no_films). Писать можно только в
// video/** и music/** через ProjectLinkGuard.ResolveInside; символическая ссылка наружу — отказ.
//
// Состояние фильма вне файла (FilmSideStore) — траты и пометки «✦ Claude»; признаки «устарел» и «● обновлена»
// вычисляются (FilmStaleness). Агент (блок 3) правит теми же методами с initiator = Agent.
public sealed class FilmService(
    FilmStore films,
    FilmSideStore side,
    VideoJobThreads threads,
    FilmBuildRegistry builds,
    ILogger<FilmService> log,
    IVideoDsp? dsp = null,
    TimeProvider? time = null)
{
    private const int MaxListed = 200;

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public FilmStore Films => films;
    public VideoJobThreads Threads => threads;

    // ── Путь фильма и корень проекта ──────────────────────────────────────────────

    public sealed record Resolved(Project Project, string Relative, string Full);

    // Корень проекта области; у личной области — отказ до диска
    public static FilmCallResult<Project> ProjectOf(VideoEditScope scope) =>
        scope.Project is { } project
            ? FilmCallResult<Project>.Ok(project)
            : FilmCallResult<Project>.Fail(VideoEditorErrors.PersonalScopeNoFilms, "Фильмы — только в чате проекта");

    public static FilmCallResult<Resolved> ResolveFilm(VideoEditScope scope, string? raw)
    {
        var project = ProjectOf(scope);
        if (!project.IsOk) return FilmCallResult<Resolved>.Fail(project.ErrorCode!, project.Error!);
        if (FilmPaths.Normalize(raw) is not { } relative)
            return FilmCallResult<Resolved>.Fail(VideoEditorErrors.InvalidRequest, "Не указан путь фильма");
        if (!FilmPaths.IsFilmPath(relative))
            return FilmCallResult<Resolved>.Fail(
                FilmPaths.IsAllowed(relative) ? VideoEditorErrors.InvalidRequest : VideoEditorErrors.OutsideAllowedFolders,
                "Фильм — файл .film в папке video/<фильм>/");
        return ResolveInside(project.Value!, relative);
    }

    // Путь внутри проекта (не через ссылку наружу), уже по allow-list video/** и music/**
    public static FilmCallResult<Resolved> ResolveInside(Project project, string relative)
    {
        if (!FilmPaths.IsAllowed(relative))
            return FilmCallResult<Resolved>.Fail(VideoEditorErrors.OutsideAllowedFolders, "Писать можно только в video/ и music/");
        return ProjectLinkGuard.ResolveInside(project.RootPath, relative) is { } full
            ? FilmCallResult<Resolved>.Ok(new Resolved(project, relative, full))
            : FilmCallResult<Resolved>.Fail(VideoEditorErrors.OutsideAllowedFolders,
                "Путь идёт через символическую ссылку или вне проекта");
    }

    // ── Список ────────────────────────────────────────────────────────────────────

    public FilmCallResult<IReadOnlyList<FilmSummaryDto>> List(VideoEditScope scope)
    {
        var project = ProjectOf(scope);
        if (!project.IsOk) return FilmCallResult<IReadOnlyList<FilmSummaryDto>>.Fail(project.ErrorCode!, project.Error!);
        var root = project.Value!.RootPath;
        var videoDir = Path.Combine(root, FilmPaths.VideoRoot);
        var found = new List<FilmSummaryDto>();
        // Символические ссылки (файлы и каталоги) перечисление пропускает: список не выводит за проект
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 8,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        };
        if (Directory.Exists(videoDir) && ProjectLinkGuard.HasLink(root, videoDir) is false)
        {
            foreach (var file in Directory.EnumerateFiles(videoDir, "*" + FilmPaths.FilmExtension, options).Order())
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!FilmPaths.IsFilmPath(relative)) continue;
                found.Add(Summary(root, relative, films.ReadFile(file)));
                if (found.Count >= MaxListed) break;
            }
        }
        return FilmCallResult<IReadOnlyList<FilmSummaryDto>>.Ok(found);
    }

    private static FilmSummaryDto Summary(string root, string relative, FilmStore.Read read)
    {
        var name = FilmPaths.NameOf(relative);
        if (read.Document is not { } doc) return new FilmSummaryDto(relative, name, 0, 0, false, false);
        var valid = read.Status == FilmStore.ReadStatus.Ok;
        return new FilmSummaryDto(relative, name, doc.Items.Count, Math.Round(FilmFormat.DurationOf(doc), 2),
            valid && FilmStaleness.IsStale(root, doc), valid);
    }

    // ── Состояние ─────────────────────────────────────────────────────────────────

    public FilmCallResult<FilmStateDto> State(string ownerId, VideoEditScope scope, string? path)
    {
        var resolved = ResolveFilm(scope, path);
        if (!resolved.IsOk) return FilmCallResult<FilmStateDto>.Fail(resolved.ErrorCode!, resolved.Error!);
        return StateOf(ownerId, scope, resolved.Value!, films.ReadFile(resolved.Value!.Full));
    }

    // Состояние из уже прочитанного файла. Неизвестная схема читается (Document есть), но с причиной в ошибке
    // только у записи: чтение отдаёт состояние как есть — фронт видит Schema ≠ 1 и показывает «только чтение»
    public FilmCallResult<FilmStateDto> StateOf(string ownerId, VideoEditScope scope, Resolved film, FilmStore.Read read)
    {
        switch (read.Status)
        {
            case FilmStore.ReadStatus.NotFound:
                return FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден");
            case FilmStore.ReadStatus.Invalid:
                return FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FilmInvalid, read.Error ?? "Фильм не прошёл проверку");
        }
        var doc = read.Document!;
        var root = film.Project.RootPath;
        var ledger = SyncLedger(ownerId, scope, film.Relative);
        var spent = new VideoSpentDto(
            ledger.Spends.Where(s => s.Currency == "usd").Sum(s => s.Amount),
            ledger.Spends.Where(s => s.Currency == "credits").Sum(s => s.Amount),
            ledger.Spends.Sum(s => s.LocalSeconds));
        var staleScenes = StaleSceneFiles(ownerId, scope);
        var marks = doc.Items.Select((item, i) => new FilmItemMarkDto(i,
            ledger.ClaudeFiles.Contains(item.File),
            read.Status == FilmStore.ReadStatus.Ok && FilmStaleness.IsUpdated(root, doc, item),
            staleScenes.Contains(item.File))).ToList();
        return FilmCallResult<FilmStateDto>.Ok(new FilmStateDto(film.Relative, read.Revision, doc, spent, marks,
            builds.Get(ownerId, scope.Key, film.Relative)));
    }

    // Траты сцен фильма (версии клипов сцен, чья папка — папка фильма либо сцена уже стоит в нём): копятся в
    // файле состояния и не убывают — отброшенный вариант и удалённый чат счёт не стирают
    private FilmSide SyncLedger(string ownerId, VideoEditScope scope, string filmPath)
    {
        var folder = FilmPaths.FolderOf(filmPath);
        var entries = new List<FilmSpendEntry>();
        foreach (var scene in OwnerScenes(ownerId, scope).Select(x => x.Scene))
        {
            if (scene.FilmRef?.Path != filmPath && FilmPaths.Normalize(scene.Folder) != folder) continue;
            foreach (var group in scene.Versions.GroupBy(v => v.JobId))
            {
                var launch = scene.Launches.FirstOrDefault(l => l.JobId == group.Key);
                var first = group.OrderBy(v => v.Variant).First();
                foreach (var version in group)
                {
                    if (version.Cost is not { } cost) continue;
                    // «GPU-секунды» — время локального запуска от старта до готовности, один раз на запуск
                    var seconds = cost.Currency == "local" && version == first && launch is not null
                        ? Math.Max(0, (version.CreatedAt - launch.At).TotalSeconds)
                        : 0;
                    entries.Add(new FilmSpendEntry(version.VersionId, cost.Currency, cost.Amount, Math.Round(seconds, 1), version.CreatedAt));
                }
            }
        }
        var current = side.Get(ownerId, scope.Key, filmPath);
        if (entries.All(e => current.Spends.Any(s => s.VersionId == e.VersionId))) return current;
        return side.Update(ownerId, scope.Key, filmPath, s => s with
        {
            Spends = [.. s.Spends, .. entries.Where(e => s.Spends.All(x => x.VersionId != e.VersionId))],
        });
    }

    // Сцены владельца в чатах ЭТОЙ области: (чат, сцена)
    internal IEnumerable<(string SessionId, VideoSceneDto Scene)> OwnerScenes(string ownerId, VideoEditScope scope)
    {
        foreach (var (owner, sessionId) in threads.Store.Chats())
        {
            if (owner != ownerId || !threads.OwnChat(ownerId, scope.Key, sessionId)) continue;
            foreach (var scene in threads.Store.Get(ownerId, sessionId).Scenes) yield return (sessionId, scene);
        }
    }

    // Файлы строк, чья сцена «переснять»: кадр или текст сцены изменены после съёмки сохранённой версии
    private HashSet<string> StaleSceneFiles(string ownerId, VideoEditScope scope)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, scene) in OwnerScenes(ownerId, scope))
        {
            if (VideoStale.Compute(scene) is null) continue;
            foreach (var saved in scene.SavedFiles) files.Add(saved.Path);
        }
        return files;
    }

    // ── Правка патчем ─────────────────────────────────────────────────────────────

    public async Task<FilmCallResult<FilmStateDto>> PatchAsync(string ownerId, VideoEditScope scope, string? path,
        FilmPatch? patch, string initiator, CancellationToken ct)
    {
        if (patch is null || patch.Ops is null || patch.Ops.Count == 0 || string.IsNullOrWhiteSpace(patch.ExpectedRevision))
            return FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.InvalidRequest, "Пустая правка: нужны ревизия и операции");
        var resolved = ResolveFilm(scope, path);
        if (!resolved.IsOk) return FilmCallResult<FilmStateDto>.Fail(resolved.ErrorCode!, resolved.Error!);
        var film = resolved.Value!;

        var read = films.ReadFile(film.Full);
        var refusal = Refusal(read);
        if (refusal is not null) return refusal;
        if (read.Revision != patch.ExpectedRevision) return Conflict(ownerId, scope, film, read);

        var ops = new List<FilmPatchOp>();
        foreach (var op in patch.Ops)
        {
            var prepared = await PrepareAsync(film.Project, op, ct);
            if (!prepared.IsOk) return FilmCallResult<FilmStateDto>.Fail(prepared.ErrorCode!, prepared.Error!);
            ops.Add(prepared.Value!);
        }
        var applied = FilmPatcher.Apply(read.Document!, ops);
        if (applied.Document is null)
            return FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.InvalidRequest, applied.Error ?? "Правка не подходит фильму");

        var written = films.WriteFile(film.Full, applied.Document, patch.ExpectedRevision, create: false);
        switch (written.Status)
        {
            case FilmStore.WriteStatus.Conflict:
                return Conflict(ownerId, scope, film, written.Current);
            case FilmStore.WriteStatus.Invalid:
                return FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FilmInvalid, written.Error ?? "Фильм не прошёл проверку");
            case FilmStore.WriteStatus.NotFound:
                return FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден");
        }

        MarkTouched(ownerId, scope, film.Relative, read.Document!, applied, initiator);
        await AfterWriteAsync(ownerId, scope, film, written.Current, ct);
        return StateOf(ownerId, scope, film, written.Current);
    }

    // Операция до патчера: файлы add и music существуют внутри проекта, у add без обрезки длина — по пробе клипа
    // (шов IVideoDsp) либо по снимку сцены
    private async Task<FilmCallResult<FilmPatchOp>> PrepareAsync(Project project, FilmPatchOp op, CancellationToken ct)
    {
        switch (op.Op)
        {
            case FilmPatchOps.Add:
            {
                if (FilmPaths.Normalize(op.File) is not { } file)
                    return FilmCallResult<FilmPatchOp>.Fail(VideoEditorErrors.InvalidRequest, "Не указан файл сцены");
                var inside = ResolveInside(project, file);
                if (!inside.IsOk) return FilmCallResult<FilmPatchOp>.Fail(inside.ErrorCode!, inside.Error!);
                if (!File.Exists(inside.Value!.Full))
                    return FilmCallResult<FilmPatchOp>.Fail(VideoEditorErrors.FileNotFound, $"Файл сцены не найден: {file}");
                var trim = op.Trim;
                if (trim is null)
                {
                    double? seconds = dsp is { Available: true } && await dsp.ProbeAsync(inside.Value.Full, ct) is { } info
                        ? info.Seconds
                        : op.Scene?.DurationSec;
                    if (seconds is not > 0)
                        return FilmCallResult<FilmPatchOp>.Fail(VideoEditorErrors.InvalidRequest,
                            "Не удалось определить длину клипа: укажите обрезку");
                    trim = [0, seconds.Value];
                }
                return FilmCallResult<FilmPatchOp>.Ok(op with { File = file, Trim = trim });
            }
            case FilmPatchOps.Music when op.Music is { } music:
            {
                if (FilmPaths.Normalize(music.File) is not { } file)
                    return FilmCallResult<FilmPatchOp>.Fail(VideoEditorErrors.InvalidRequest, "Не указан файл музыки");
                var inside = ResolveInside(project, file);
                if (!inside.IsOk) return FilmCallResult<FilmPatchOp>.Fail(inside.ErrorCode!, inside.Error!);
                if (!File.Exists(inside.Value!.Full))
                    return FilmCallResult<FilmPatchOp>.Fail(VideoEditorErrors.FileNotFound, $"Файл музыки не найден: {file}");
                return FilmCallResult<FilmPatchOp>.Ok(op with { Music = music with { File = file } });
            }
            default:
                return FilmCallResult<FilmPatchOp>.Ok(op);
        }
    }

    // Чтение, после которого править нельзя: неизвестная схема или битый файл
    private static FilmCallResult<FilmStateDto>? Refusal(FilmStore.Read read) => read.Status switch
    {
        FilmStore.ReadStatus.NotFound => FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден"),
        FilmStore.ReadStatus.Unsupported => FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FilmSchemaUnsupported,
            read.Error ?? "Схема .film не поддерживается: файл открыт только для чтения"),
        FilmStore.ReadStatus.Invalid => FilmCallResult<FilmStateDto>.Fail(VideoEditorErrors.FilmInvalid,
            read.Error ?? "Фильм не прошёл проверку"),
        _ => null,
    };

    private FilmCallResult<FilmStateDto> Conflict(string ownerId, VideoEditScope scope, Resolved film, FilmStore.Read current)
    {
        var fresh = StateOf(ownerId, scope, film, current).Value;
        return new FilmCallResult<FilmStateDto>(null, VideoEditorErrors.RevisionConflict,
            "Фильм уже поменяли — перечитайте его") { Conflict = fresh };
    }

    // Пометки «✦ Claude»: агент поставил, человек сбил своей правкой той же строки
    private void MarkTouched(string ownerId, VideoEditScope scope, string filmPath, FilmDocument before,
        FilmPatcher.Result applied, string initiator)
    {
        var agent = initiator == VideoInitiators.Agent;
        var touchedFiles = applied.TouchedIndexes.Select(i => applied.Document!.Items[i].File).ToHashSet();
        var removed = before.Items.Select(i => i.File).Except(applied.Document!.Items.Select(i => i.File)).ToHashSet();
        side.Update(ownerId, scope.Key, filmPath, s =>
        {
            var files = s.ClaudeFiles.Where(f => !removed.Contains(f) && (agent || !touchedFiles.Contains(f))).ToList();
            if (agent) files.AddRange(touchedFiles.Where(f => !files.Contains(f)));
            return files.SequenceEqual(s.ClaudeFiles) ? s : s with { ClaudeFiles = files };
        });
    }

    // После записи: сцены чатов узнают своё место в фильме, фронт получает свежее состояние
    internal async Task AfterWriteAsync(string ownerId, VideoEditScope scope, Resolved film, FilmStore.Read written,
        CancellationToken ct)
    {
        try
        {
            await LinkScenesAsync(ownerId, scope, film.Relative, written.Document!);
            if (StateOf(ownerId, scope, film, written).Value is { } state)
                await threads.BroadcastFilmAsync(ownerId, scope.Key, film.Relative, state);
        }
        catch (Exception ex)
        {
            // Запись фильма уже сделана — сбой следа её не отменяет
            log.LogWarning(ex, "Видео: след правки фильма {Path} не записан", film.Relative);
        }
    }

    // Место сцены в фильме (FilmRef): сцена, чей сохранённый файл стоит в строке i, знает путь и номер строки;
    // убранная из фильма — нет
    private async Task LinkScenesAsync(string ownerId, VideoEditScope scope, string filmPath, FilmDocument doc)
    {
        foreach (var (sessionId, scene) in OwnerScenes(ownerId, scope).ToList())
        {
            var row = IndexOfSaved(doc, scene);
            var want = row >= 0 ? new VideoFilmRefDto(filmPath, row) : null;
            if (scene.FilmRef is { } have && have.Path != filmPath) continue;
            if (scene.FilmRef == want) continue;
            var written = threads.Store.SetFilmRef(ownerId, sessionId, scene.SceneId, want);
            if (written.Status == VideoThreadWriteStatus.Ok)
                await threads.BroadcastAsync(ownerId, scope.Key, sessionId, written.State);
        }
    }

    private static int IndexOfSaved(FilmDocument doc, VideoSceneDto scene)
    {
        for (var i = 0; i < doc.Items.Count; i++)
            if (scene.SavedFiles.Any(f => f.Path == doc.Items[i].File)) return i;
        return -1;
    }

    // ── Для других служб модуля ───────────────────────────────────────────────────

    // Запоминает нить звука, из которой фильм ждёт музыку: первая готовая версия станет music/<фильм>.mp3
    public FilmCallResult<FilmPendingMusic> ExpectMusic(string ownerId, VideoEditScope scope, string? path,
        string sessionId, string threadId)
    {
        var resolved = ResolveFilm(scope, path);
        if (!resolved.IsOk) return FilmCallResult<FilmPendingMusic>.Fail(resolved.ErrorCode!, resolved.Error!);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(threadId))
            return FilmCallResult<FilmPendingMusic>.Fail(VideoEditorErrors.InvalidRequest, "Не указана нить звука");
        var read = films.ReadFile(resolved.Value!.Full);
        if (read.Status == FilmStore.ReadStatus.NotFound)
            return FilmCallResult<FilmPendingMusic>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден");
        var pending = new FilmPendingMusic(sessionId, threadId, Now);
        side.Update(ownerId, scope.Key, resolved.Value.Relative, s => s with { PendingMusic = pending, Path = resolved.Value.Relative });
        return FilmCallResult<FilmPendingMusic>.Ok(pending);
    }

    // Пометка «✦ Claude» на строке: агент сохранил или поправил её
    internal void MarkClaude(string ownerId, VideoEditScope scope, string filmPath, string file) =>
        side.Update(ownerId, scope.Key, filmPath, s => s.ClaudeFiles.Contains(file) ? s : s with { ClaudeFiles = [.. s.ClaudeFiles, file] });

    public FilmSide SideOf(string ownerId, string projectId, string filmPath) => side.Get(ownerId, projectId, filmPath);
}
