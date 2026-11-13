using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;
using ClaudeHomeServer.Services.VideoEditor.Scenes;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// «Сохранить сцену» в «Личное» (ADR-040 §5, Ф2-3): версия клипа → Personal/video/scene-NN.mp4 (повтор → .v2.mp4, .v3…).
// Только клип, без фильма и кадров: фильмы «Личного» — Ф2-6, до неё FilmSceneSaver для личной области отказывает.
// Только CreateNew, перезаписи нет; занятое имя, названное человеком, — 409 name_taken. Папку «Личного» создаёт
// этот пишущий путь, лениво. Границу корня и символические ссылки держит ProjectLinkGuard.
public sealed class PersonalSceneSaver(
    VideoJobThreads threads,
    VideoEditWorkspace workspace,
    PersonalFolderResolver personal)
{
    private const int MaxAutoAttempts = 99;

    public async Task<FilmCallResult<SaveSceneResult>> SaveAsync(string ownerId, VideoEditScope scope, SaveSceneRequest? req,
        string initiator, CancellationToken ct)
    {
        if (!scope.IsPersonal)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, "Сохранение в «Личное» — только в личном чате");
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.SceneId))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, "Не указана сцена");
        if (!string.IsNullOrWhiteSpace(req.FilmPath))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.PersonalScopeNoFilms, "Фильмы — только в чате проекта");
        if (PersonalMediaFolders.Resolve(PersonalMediaFolders.Video, req.Folder, out var folder) is { } folderError)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.OutsideAllowedFolders, folderError);

        if (!threads.OwnScene(ownerId, scope.Key, req.SessionId, req.SceneId))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.SceneNotFound, "Сцена не найдена в этом чате");
        var state = threads.Store.Get(ownerId, req.SessionId);
        var scene = state.Scenes.First(s => s.SceneId == req.SceneId);
        var version = string.IsNullOrWhiteSpace(req.VersionId)
            ? scene.Versions.FirstOrDefault(v => v.VersionId == scene.CurrentVersionId)
            : scene.Versions.FirstOrDefault(v => v.VersionId == req.VersionId.Trim());
        if (version is null)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.VersionNotFound, "Версии нет в этой сцене");

        string? source;
        try { source = workspace.FindClip(ownerId, version.JobId, version.Variant); }
        catch (ArgumentException) { source = null; }
        if (source is null || !File.Exists(source))
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.FileNotFound,
                "Файла клипа больше нет — рабочая папка очищена");
        if (new FileInfo(source).Length > SafeMediaDownloader.VideoMaxBytes)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, "Клип больше 300 МБ");

        if (await personal.EnsureAsync(ownerId, ct) is not { } project)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.InvalidRequest, PersonalFolderResolver.UnavailableText);
        if (PersonalMediaFolders.EnsureDirectory(project.RootPath, folder) is not { } dir)
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.OutsideAllowedFolders,
                "Путь идёт через символическую ссылку или вне папки «Личного»");

        FilmCallResult<string> saved;
        try
        {
            saved = await SaveClipAsync(project.RootPath, dir, folder, source, req.FileName, IndexOf(state, scene.SceneId) + 1, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return FilmCallResult<SaveSceneResult>.Fail(VideoEditorErrors.OutsideAllowedFolders,
                "Путь идёт через символическую ссылку или вне папки «Личного»");
        }
        if (!saved.IsOk) return FilmCallResult<SaveSceneResult>.Fail(saved.ErrorCode!, saved.Error!);
        var clipPath = saved.Value!;

        var text = VideoFeedTexts.SceneSavedPersonal(initiator, scene.Name, clipPath);
        var written = threads.Store.AddSavedFile(ownerId, req.SessionId, scene.SceneId,
            new VideoSavedFileDto(version.VersionId, clipPath, threads.Store.Now()),
            new VideoThreadEvent(threads.Store.Now(), VideoThreadEventKinds.Saved, text, scene.SceneId));
        if (written.Status == VideoThreadWriteStatus.Ok)
            await threads.BroadcastAsync(ownerId, scope.Key, req.SessionId, written.State);
        await threads.SavedAsync(req.SessionId, text,
            new { sceneId = scene.SceneId, versionId = version.VersionId, path = clipPath, framePaths = Array.Empty<string>(), addedToFilm = false, initiator }, ct);
        return FilmCallResult<SaveSceneResult>.Ok(new SaveSceneResult(clipPath, [], false));
    }

    private static int IndexOf(VideoThreadsState state, string sceneId)
    {
        for (var i = 0; i < state.Scenes.Count; i++) if (state.Scenes[i].SceneId == sceneId) return i;
        return 0;
    }

    // Имя человека — как есть (занято → name_taken); без имени scene-NN.mp4, занятое — .v2.mp4, .v3.mp4…
    private static async Task<FilmCallResult<string>> SaveClipAsync(string root, string dir, string folder, string source,
        string? fileName, int number, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var name = fileName.Trim();
            if (!FilmPaths.IsPlainFileName(name) || !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return FilmCallResult<string>.Fail(VideoEditorErrors.InvalidRequest, "Имя файла — простое имя с расширением .mp4");
            var explicitPath = $"{folder}/{name}";
            return await TryCreateAsync(root, Path.Combine(dir, name), source, ct)
                ? FilmCallResult<string>.Ok(explicitPath)
                : FilmCallResult<string>.Fail(VideoEditorErrors.NameTaken, $"Файл {explicitPath} уже есть — перезаписи нет");
        }

        var stem = $"scene-{number:00}";
        for (var attempt = 1; attempt <= MaxAutoAttempts; attempt++)
        {
            var name = $"{stem}{(attempt == 1 ? "" : $".v{attempt}")}.mp4";
            if (await TryCreateAsync(root, Path.Combine(dir, name), source, ct)) return FilmCallResult<string>.Ok($"{folder}/{name}");
        }
        return FilmCallResult<string>.Fail(VideoEditorErrors.NameTaken, "Не нашлось свободного имени для сцены");
    }

    // CreateNew: false — файл уже есть. Недописанный файл при сбое или отмене убирается; ссылка на пути — отказ
    private static async Task<bool> TryCreateAsync(string root, string full, string source, CancellationToken ct)
    {
        ProjectLinkGuard.EnsureNoLink(root, full);
        FileStream target;
        try { target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) when (File.Exists(full) || new FileInfo(full).LinkTarget is not null) { return false; }
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
}
