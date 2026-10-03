using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// «Сохранить сцену» в проект (ADR-022 §2): версия клипа → video/<фильм>/scene-NN.mp4 (повтор → .v2.mp4, .v3…),
// кадры-нити из «Картинок» той же группой в video/<фильм>/кадры/. Только CreateNew: перезаписи нет, занятое
// имя, названное человеком, — 409 name_taken. Новый файл сам встаёт в фильм папки с точкой «обновлена».
//
// Писать можно только в video/** и music/**; границу проекта и символические ссылки держит
// ProjectLinkGuard.ResolveInside. Кадры читаются швом IImageFrameSource (модуль нитей картинок не читает).
public sealed class FilmSceneSaver(
    FilmService service,
    VideoEditWorkspace workspace,
    ILogger<FilmSceneSaver> log,
    IImageFrameSource? images = null)
{
    private const int MaxAutoAttempts = 99;

    public async Task<FilmCallResult<SaveSceneResult>> SaveAsync(string ownerId, VideoEditScope scope,
        SaveSceneRequest? req, string initiator, CancellationToken ct)
    {
        var project = FilmService.ProjectOf(scope);
        if (!project.IsOk) return FilmCallResult<SaveSceneResult>.Fail(project.ErrorCode!, project.Error!);
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.SceneId))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, "Не указана сцена");

        var threads = service.Threads;
        if (!threads.OwnScene(ownerId, scope.Key, req.SessionId, req.SceneId))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.SceneNotFound, "Сцена не найдена в этом чате");
        var state = threads.Store.Get(ownerId, req.SessionId);
        var scene = state.Scenes.First(s => s.SceneId == req.SceneId);
        var version = string.IsNullOrWhiteSpace(req.VersionId)
            ? scene.Versions.FirstOrDefault(v => v.VersionId == scene.CurrentVersionId)
            : scene.Versions.FirstOrDefault(v => v.VersionId == req.VersionId.Trim());
        if (version is null)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.VersionNotFound, "Версии нет в этой сцене");

        // Папка фильма: из запроса, иначе папка сцены; только video/<фильм> и только внутри проекта
        // Путь открытого фильма (необязательный): сцена встаёт в него, папка сцены = папка фильма
        string? explicitFilm = null;
        if (!string.IsNullOrWhiteSpace(req.FilmPath))
        {
            var openFilm = FilmService.ResolveFilm(scope, req.FilmPath);
            if (!openFilm.IsOk) return FilmCallResult<SaveSceneResult>.Fail(openFilm.ErrorCode!, openFilm.Error!);
            explicitFilm = openFilm.Value!.Relative;
        }
        var folder = explicitFilm is not null
            ? FilmPaths.FolderOf(explicitFilm)
            : FilmPaths.Normalize(req.Folder) ?? FilmPaths.Normalize(scene.Folder);
        if (folder is null)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, "Не указана папка фильма: video/<фильм>");
        if (!folder.StartsWith(FilmPaths.VideoRoot + "/", StringComparison.Ordinal) || !FilmPaths.IsAllowed(folder))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.OutsideAllowedFolders,
                "Сцены сохраняются только в video/<фильм>/");
        var folderResolved = FilmService.ResolveInside(project.Value!, folder);
        if (!folderResolved.IsOk) return FilmCallResult<SaveSceneResult>.Fail(folderResolved.ErrorCode!, folderResolved.Error!);

        string? source;
        try { source = workspace.FindClip(ownerId, version.JobId, version.Variant); }
        catch (ArgumentException) { source = null; }
        if (source is null || !File.Exists(source))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.FileNotFound,
                "Файла клипа больше нет — рабочая папка очищена");
        if (new FileInfo(source).Length > SafeMediaDownloader.VideoMaxBytes)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, "Клип больше 300 МБ");

        var saved = await SaveClipAsync(project.Value!, folder, source, req.FileName, IndexOf(state, scene.SceneId) + 1, ct);
        if (!saved.IsOk) return FilmCallResult<SaveSceneResult>.Fail(saved.ErrorCode!, saved.Error!);
        var clipPath = saved.Value!;

        // Кадры — ПОСЛЕ клипа: занятое имя клипа (name_taken) не оставит в проекте осиротевших кадров
        var framePaths = new List<string>();
        var snapshotFrames = new Dictionary<FrameRef, string?>();
        foreach (var frame in new[] { scene.Settings.FrameA, scene.Settings.FrameB })
        {
            if (frame is null || snapshotFrames.ContainsKey(frame)) continue;
            snapshotFrames[frame] = frame.Kind == FrameRef.KindFile
                ? frame.Path
                : await SaveFrameAsync(ownerId, project.Value!, folder, frame, framePaths, ct);
        }


        // Версия → файл проекта: сцена помнит, какая версия в каком файле лежит
        var text = VideoFeedTexts.SceneSaved(initiator, scene.Name, clipPath);
        var written = threads.Store.AddSavedFile(ownerId, req.SessionId, scene.SceneId,
            new VideoSavedFileDto(version.VersionId, clipPath, threads.Store.Now()),
            new VideoThreadEvent(threads.Store.Now(), VideoThreadEventKinds.Saved, text, scene.SceneId));

        var added = await AddToFilmAsync(ownerId, scope, folder, explicitFilm, clipPath, scene, version, snapshotFrames, initiator, ct);

        if (written.Status == VideoThreadWriteStatus.Ok)
            await threads.BroadcastAsync(ownerId, scope.Key, req.SessionId, written.State);
        // Копилка правок этого фильма — раньше строки сохранения: порядок ленты = порядок событий
        var feedFilm = explicitFilm ?? DefaultFilmPath(folder);
        await service.FlushPatchFeedAsync(req.SessionId, feedFilm);
        await threads.SavedAsync(req.SessionId, text,
            new { sceneId = scene.SceneId, versionId = version.VersionId, path = clipPath, framePaths, addedToFilm = added, initiator }, ct);
        return FilmCallResult<SaveSceneResult>.Ok(new SaveSceneResult(clipPath, framePaths, added));
    }

    private static string DefaultFilmPath(string folder) =>
        $"{folder}/{folder[(folder.LastIndexOf('/') + 1)..]}{FilmPaths.FilmExtension}";

    private static int IndexOf(VideoThreadsState state, string sceneId)
    {
        for (var i = 0; i < state.Scenes.Count; i++) if (state.Scenes[i].SceneId == sceneId) return i;
        return 0;
    }

    // Клип в папку фильма CreateNew. Имя человека — как есть (занято → name_taken); без имени scene-NN.mp4, а
    // занятое — .v2.mp4, .v3.mp4…: повторное сохранение той же сцены не затирает прежнее
    private async Task<FilmCallResult<string>> SaveClipAsync(Project project, string folder, string source,
        string? fileName, int number, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var name = fileName.Trim();
            if (!FilmPaths.IsPlainFileName(name) || !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return FilmCallResult<string>.Fail(VideoEditorErrors.InvalidRequest, "Имя файла — простое имя с расширением .mp4");
            var explicitPath = $"{folder}/{name}";
            var resolved = FilmService.ResolveInside(project, explicitPath);
            if (!resolved.IsOk) return FilmCallResult<string>.Fail(resolved.ErrorCode!, resolved.Error!);
            return await TryCreateAsync(resolved.Value!.Full, source, ct)
                ? FilmCallResult<string>.Ok(explicitPath)
                : FilmCallResult<string>.Fail(VideoEditorErrors.NameTaken, $"Файл {explicitPath} уже есть — перезаписи нет");
        }

        var stem = $"scene-{number:00}";
        for (var attempt = 1; attempt <= MaxAutoAttempts; attempt++)
        {
            var candidate = $"{folder}/{stem}{(attempt == 1 ? "" : $".v{attempt}")}.mp4";
            var resolved = FilmService.ResolveInside(project, candidate);
            if (!resolved.IsOk) return FilmCallResult<string>.Fail(resolved.ErrorCode!, resolved.Error!);
            if (await TryCreateAsync(resolved.Value!.Full, source, ct)) return FilmCallResult<string>.Ok(candidate);
        }
        return FilmCallResult<string>.Fail(VideoEditorErrors.NameTaken, "Не нашлось свободного имени для сцены");
    }

    // CreateNew: false — файл уже есть. Недописанный файл при сбое или отмене убирается
    private static async Task<bool> TryCreateAsync(string full, string source, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        FileStream target;
        try { target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) when (File.Exists(full)) { return false; }
        try
        {
            await using (target)
            {
                await using var from = File.OpenRead(source);
                await from.CopyToAsync(target, ct);
            }
            return true;
        }
        catch
        {
            try { File.Delete(full); } catch (IOException) { }
            throw;
        }
    }

    // Кадр-нить из «Картинок» → video/<фильм>/кадры/кадр-N.<ext> CreateNew. null — кадр не прочитался: сцена всё
    // равно сохраняется, в снимке фильма кадра не будет
    private async Task<string?> SaveFrameAsync(string ownerId, Project project, string folder, FrameRef frame,
        List<string> framePaths, CancellationToken ct)
    {
        if (images is null || frame.ThreadId is null || frame.VersionId is null) return null;
        var image = await images.GetAsync(ownerId, frame.ThreadId, frame.VersionId, ct);
        if (image is null) return null;
        var extension = image.ContentType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
        for (var n = 1; n <= MaxAutoAttempts; n++)
        {
            var relative = $"{folder}/кадры/кадр-{n}{extension}";
            var resolved = FilmService.ResolveInside(project, relative);
            if (!resolved.IsOk) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.Value!.Full)!);
            try
            {
                await using var target = new FileStream(resolved.Value.Full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await target.WriteAsync(image.Bytes, ct);
            }
            catch (IOException) when (File.Exists(resolved.Value.Full)) { continue; }
            framePaths.Add(relative);
            return relative;
        }
        return null;
    }

    // Новый файл сцены встаёт в фильм своей папки (файл <папка>/<имя папки>.film или открытый фильм из запроса; нет — заводится). Фильм
    // невалидный, чужой схемы или с этой сценой — не добавляем: сохранение клипа от этого не страдает
    private async Task<bool> AddToFilmAsync(string ownerId, VideoEditScope scope, string folder, string? explicitFilm,
        string clipPath, VideoSceneDto scene, VideoClipVersionDto version, Dictionary<FrameRef, string?> frames, string initiator,
        CancellationToken ct)
    {
        try
        {
            var filmPath = explicitFilm ?? DefaultFilmPath(folder);
            var resolved = FilmService.ResolveFilm(scope, filmPath);
            if (!resolved.IsOk) return false;
            var film = resolved.Value!;
            var settings = scene.Settings;
            var snapshot = new FilmSceneSnapshot(settings.Text,
                settings.FrameA is { } a && frames.TryGetValue(a, out var pa) ? pa : null,
                settings.FrameB is { } b && frames.TryGetValue(b, out var pb) ? pb : null,
                version.Provider, version.Model, (int)Math.Round(version.DurationSec));
            var item = new FilmItem(clipPath, [0, Math.Max(version.DurationSec, 0.2)], snapshot);

            FilmStore.Write written;
            var current = service.Films.ReadFile(film.Full);
            if (current.Status == FilmStore.ReadStatus.NotFound)
            {
                var aspect = settings.Aspect is { } asp && FilmFormat.AspectSize(asp) is not null ? asp : "16:9";
                written = service.Films.WriteFile(film.Full, new FilmDocument(FilmDocument.CurrentSchema, aspect, [item], [], null, []),
                    null, create: true);
            }
            else
            {
                written = service.Films.Update(film.Full, doc =>
                {
                    var applied = FilmPatcher.Apply(doc, [new FilmPatchOp(FilmPatchOps.Add, clipPath, Trim: [.. item.Trim], Scene: snapshot)]);
                    return applied.Document;
                });
            }
            if (written.Status != FilmStore.WriteStatus.Ok) return false;
            // Update без изменений (сцена уже в фильме) — запись Ok без нового файла: «добавлена» только при факте
            if (written.Current.Document!.Items.All(i => i.File != clipPath)) return false;
            if (initiator == VideoInitiators.Agent) service.MarkClaude(ownerId, scope, film.Relative, clipPath);
            await service.AfterWriteAsync(ownerId, scope, film, written.Current, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Видео: сцена {Path} не встала в фильм", clipPath);
            return false;
        }
    }
}
