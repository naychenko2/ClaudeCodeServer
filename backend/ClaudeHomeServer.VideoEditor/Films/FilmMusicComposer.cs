using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Jobs;

namespace ClaudeHomeServer.Services.VideoEditor.Films;

// «Сочинить под фильм…» (ADR-022 §3): черновик музыки заводится в «Звуке» швом IAudioTrackSource, нить звука
// запоминается у фильма как ожидаемая музыка (состояние вне файла), а первая готовая версия сама ложится в
// music/<фильм>.mp3 (только CreateNew: занятое имя → .v2) и становится музыкой фильма. Узнаёт о версии ТОЛЬКО
// событием IMediaEvents; нитей звука модуль не читает. Нет редактора звука — шва нет: отказ с понятным текстом.
public sealed class FilmMusicComposer(
    FilmService films,
    FilmSideStore side,
    VideoJobThreads threads,
    IProjectManager projects,
    ILogger<FilmMusicComposer> log,
    IAudioTrackSource? audio = null)
{
    // Громкость и затухание новой музыки по умолчанию: человек правит их в панели фильма
    public const int DefaultVolume = 60;
    public const double DefaultFadeOut = 4;
    private const int MaxNameAttempts = 99;
    private const int MaxStyleChars = 300;

    // Не короче этого модели музыки «Звука» не снимают (минимум песенных моделей каталога): заготовка под
    // короткий фильм честно получает «не короче 10 с», а не обещание 0:05
    public const int MinMusicSeconds = 10;

    public async Task<FilmCallResult<FilmMusicDraftDto>> CreateDraftAsync(string ownerId, VideoEditScope scope, string? path,
        FilmMusicRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
            return FilmCallResult<FilmMusicDraftDto>.Fail(VideoEditorErrors.InvalidRequest, "Не указан чат");
        if (audio is null)
            return FilmCallResult<FilmMusicDraftDto>.Fail(VideoEditorErrors.ProviderUnavailable,
                "Редактор звука выключен: музыку под фильм сочинить нельзя");
        var resolved = FilmService.ResolveFilm(scope, path);
        if (!resolved.IsOk) return FilmCallResult<FilmMusicDraftDto>.Fail(resolved.ErrorCode!, resolved.Error!);
        var read = films.Films.ReadFile(resolved.Value!.Full);
        if (read.Status == FilmStore.ReadStatus.NotFound)
            return FilmCallResult<FilmMusicDraftDto>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден");
        if (!threads.OwnChat(ownerId, scope.Key, req.SessionId.Trim()))
            return FilmCallResult<FilmMusicDraftDto>.Fail(VideoEditorErrors.ChatNotFound, "Чат не найден");

        var draft = await audio.CreateDraftAsync(ownerId, scope.Key, req.SessionId.Trim(), FilmPaths.MusicRoot, ct);
        if (draft is null)
            return FilmCallResult<FilmMusicDraftDto>.Fail(VideoEditorErrors.ChatNotFound, "Не удалось завести звук в этом чате");
        var expected = films.ExpectMusic(ownerId, scope, path, req.SessionId.Trim(), draft.ThreadId);
        return expected.IsOk
            ? FilmCallResult<FilmMusicDraftDto>.Ok(DraftOf(draft.ThreadId, read.Document))
            : FilmCallResult<FilmMusicDraftDto>.Fail(expected.ErrorCode!, expected.Error!);
    }

    // Длина и стиль заготовки: длина — фильма, но не короче минимума моделей (причина — в DurationNote), стиль —
    // из текстов сцен снимков фильма
    internal static FilmMusicDraftDto DraftOf(string threadId, FilmDocument? doc)
    {
        var seconds = doc is null ? 0 : (int)Math.Ceiling(FilmFormat.DurationOf(doc));
        var actual = Math.Max(seconds, MinMusicSeconds);
        var texts = doc?.Items.Select(i => i.Scene?.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList() ?? [];
        var style = string.Join("; ", texts);
        if (style.Length > MaxStyleChars) style = style[..MaxStyleChars].TrimEnd() + "…";
        return new FilmMusicDraftDto(threadId, seconds, MinMusicSeconds, actual,
            actual > seconds ? $"Модели музыки снимают не короче {MinMusicSeconds} с" : null,
            style.Length == 0 ? null : style);
    }

    // Подписчик AudioVersionAdded: фильмы владельца, ждущие эту нить. Версия без основного файла (одни стемы)
    // фильм не закрывает — ждёт следующую
    public async Task OnAudioVersionAsync(AudioVersionAdded evt)
    {
        if (audio is null) return;
        foreach (var (projectId, state) in side.WaitingForMusic(evt.OwnerId, evt.ThreadId))
        {
            try
            {
                await LandAsync(evt, projectId, state);
            }
            catch (Exception ex)
            {
                // Фильм остаётся в ожидании: следующая версия нити попробует снова
                log.LogWarning(ex, "Видео: музыка нити {ThreadId} не легла в фильм {Path}", evt.ThreadId, state.Path);
            }
        }
    }

    private async Task LandAsync(AudioVersionAdded evt, string projectId, FilmSide state)
    {
        if (projects.GetById(projectId) is not { } project || project.OwnerId != evt.OwnerId) return;
        var scope = VideoEditScope.Of(project);
        var resolved = FilmService.ResolveFilm(scope, state.Path);
        if (!resolved.IsOk) return;
        var film = resolved.Value!;
        if (await audio!.GetMainFileAsync(evt.OwnerId, evt.ThreadId, evt.VersionId, default) is not { } track) return;

        var extension = track.Extension is { Length: > 1 and <= 6 } e && e[0] == '.' && e[1..].All(char.IsAsciiLetterOrDigit)
            ? e : ".mp3";
        var stem = $"{FilmPaths.MusicRoot}/{FilmPaths.NameOf(film.Relative)}";
        string? musicPath = null;
        for (var attempt = 1; attempt <= MaxNameAttempts && musicPath is null; attempt++)
        {
            var candidate = $"{stem}{(attempt == 1 ? "" : $".v{attempt}")}{extension}";
            if (FilmService.ResolveInside(project, candidate) is not { IsOk: true, Value: { } target }) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target.Full)!);
            try
            {
                await using var stream = new FileStream(target.Full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(track.Bytes);
                musicPath = candidate;
            }
            catch (IOException) when (File.Exists(target.Full)) { }
        }
        if (musicPath is null) return;

        var written = films.Films.Update(film.Full, doc => doc with
        {
            Music = new FilmMusic(musicPath, doc.Music?.Volume ?? DefaultVolume, doc.Music?.FadeOut ?? DefaultFadeOut),
        });
        if (written.Status != FilmStore.WriteStatus.Ok) return;
        side.Update(evt.OwnerId, projectId, film.Relative, s => s with { PendingMusic = null });
        await films.AfterWriteAsync(evt.OwnerId, scope, film, written.Current, default);
        if (state.PendingMusic is { } pending)
            await threads.NoteAsync(pending.SessionId, $"Музыка фильма «{FilmPaths.NameOf(film.Relative)}» готова: {musicPath}",
                new { filmPath = film.Relative, music = musicPath, threadId = evt.ThreadId, versionId = evt.VersionId });
    }
}
