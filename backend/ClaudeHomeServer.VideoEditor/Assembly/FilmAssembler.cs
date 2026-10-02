using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Films;

namespace ClaudeHomeServer.Services.VideoEditor.Assembly;

// Сборка фильма в film.mp4 (ADR-022 §4): ffmpeg на хосте, всегда перекодирование, без ИИ и без денег. Шов
// IVideoDsp сам берёт слот единого BuildConcurrencyGate и запускает процесс тяжёлым через системный local-запуск
// (scope ccs-agents.slice) — здесь второго семафора НЕТ: только потолок модуля поверх слота (FilmBuildRegistry,
// 1 сборка на владельца, 2 на инстанс) и учёт состояния для ручек.
//
// Итог — film.mp4 рядом с .film, повтор → film.v2.mp4, .v3… (CreateNew: перезаписи нет). Сборка идёт во временный
// файл и переименовывается только целой; отмена убивает дерево ffmpeg и удаляет временный файл. Запись builds[] в
// .film — под замком файла, с хешем входов НА МОМЕНТ плана: правка за время сборки делает фильм «устаревшим» сама.
// Потолки: до 50 сцен, клип до 300 МБ, VideoEditor:AssembleTimeoutMinutes (20) на процесс.
public sealed class FilmAssembler(
    FilmService films,
    FilmBuildRegistry registry,
    IConfiguration config,
    ILogger<FilmAssembler> log,
    IVideoDsp? dsp = null,
    ISpendCollector? spend = null,
    TimeProvider? time = null)
{
    public const int DefaultTimeoutMinutes = 20;
    public const string SpendLabel = "сборка фильма";
    private const int MaxFileAttempts = 99;
    private const int KeptBuilds = 10;
    // Минимум длины куска клипа — как в шве: короче ffmpeg пустое видео
    private const double MinPiece = 0.2;
    // Фоновые сборки: сервер гасится — сигнал отмены всем, чтобы ffmpeg не пережил хост
    private readonly List<Task> _running = [];

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    private TimeSpan Timeout => TimeSpan.FromMinutes(Math.Clamp(config.GetValue("VideoEditor:AssembleTimeoutMinutes", DefaultTimeoutMinutes), 1, 240));

    public FilmBuildStatusDto? Status(string ownerId, VideoEditScope scope, string? path)
    {
        var resolved = FilmService.ResolveFilm(scope, path);
        return resolved.IsOk ? registry.Get(ownerId, scope.Key, resolved.Value!.Relative) : null;
    }

    public bool Cancel(string ownerId, VideoEditScope scope, string? path)
    {
        var resolved = FilmService.ResolveFilm(scope, path);
        return resolved.IsOk && registry.Cancel(ownerId, scope.Key, resolved.Value!.Relative);
    }

    // Заявка: быстрые проверки — здесь, остальное и сама сборка — в фоне. Возвращает состояние «ждём слот»
    public FilmCallResult<FilmBuildStatusDto> Start(string ownerId, VideoEditScope scope, string? path, string initiator,
        string? sessionId = null)
    {
        if (dsp is not { Available: true })
            return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.DspUnavailable,
                "Сборка недоступна: на сервере нет ffmpeg или выключены «Картинки»");
        var resolved = FilmService.ResolveFilm(scope, path);
        if (!resolved.IsOk) return FilmCallResult<FilmBuildStatusDto>.Fail(resolved.ErrorCode!, resolved.Error!);
        var film = resolved.Value!;

        var read = films.Films.ReadFile(film.Full);
        switch (read.Status)
        {
            case FilmStore.ReadStatus.NotFound:
                return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.FileNotFound, "Фильм не найден");
            case FilmStore.ReadStatus.Unsupported:
                return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.FilmSchemaUnsupported, read.Error ?? "Схема .film не поддерживается");
            case FilmStore.ReadStatus.Invalid:
                return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.FilmInvalid, read.Error ?? "Фильм не прошёл проверку");
        }
        var doc = read.Document!;
        if (doc.Items.Count == 0)
            return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.InvalidRequest, "В фильме нет сцен — собирать нечего");

        // Файлы и размеры — сразу и до заявки: человек видит причину ответом, а не провалом в фоне
        foreach (var item in doc.Items)
        {
            var clip = Resolve(film.Project, item.File);
            if (!clip.IsOk) return FilmCallResult<FilmBuildStatusDto>.Fail(clip.ErrorCode!, clip.Error!);
            if (new FileInfo(clip.Value!.Full).Length > SafeMediaDownloader.VideoMaxBytes)
                return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.InvalidRequest, $"Клип больше 300 МБ: {item.File}");
        }
        if (doc.Music is { } music)
        {
            var track = Resolve(film.Project, music.File);
            if (!track.IsOk) return FilmCallResult<FilmBuildStatusDto>.Fail(track.ErrorCode!, track.Error!);
        }

        var begin = registry.TryBegin(ownerId, scope.Key, film.Relative, Now, out var entry);
        if (begin != FilmBuildRegistry.BeginStatus.Started)
            return FilmCallResult<FilmBuildStatusDto>.Fail(VideoEditorErrors.TooManyJobs, begin switch
            {
                FilmBuildRegistry.BeginStatus.FilmBusy => "Этот фильм уже собирается",
                FilmBuildRegistry.BeginStatus.OwnerLimit => "У вас уже идёт сборка фильма — дождитесь её",
                _ => "На сервере идёт слишком много сборок — попробуйте позже",
            });

        var task = Task.Run(() => RunAsync(ownerId, scope, film, doc, entry!, initiator, sessionId));
        lock (_running)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(task);
        }
        return FilmCallResult<FilmBuildStatusDto>.Ok(entry!.Status);
    }

    // Ждёт завершения фоновых сборок: для тестов и мягкой остановки хоста
    public async Task WhenIdleAsync()
    {
        Task[] pending;
        lock (_running) pending = [.. _running];
        await Task.WhenAll(pending);
    }

    private static FilmCallResult<FilmService.Resolved> Resolve(Project project, string relative)
    {
        var inside = FilmService.ResolveInside(project, relative);
        if (!inside.IsOk) return inside;
        return File.Exists(inside.Value!.Full)
            ? inside
            : FilmCallResult<FilmService.Resolved>.Fail(VideoEditorErrors.FileNotFound, $"Файл не найден: {relative}");
    }

    private async Task RunAsync(string ownerId, VideoEditScope scope, FilmService.Resolved film, FilmDocument doc,
        FilmBuildRegistry.Entry entry, string initiator, string? sessionId)
    {
        var sw = Stopwatch.StartNew();
        var tmp = Path.Combine(Path.GetDirectoryName(film.Full)!, $".film-build-{Guid.NewGuid():N}.part");
        string state = FilmBuildStates.Failed;
        string? error = null, outFile = null;
        try
        {
            var planned = await PlanAsync(film, doc, entry.Cts.Token);
            if (planned.Error is not null) { error = planned.Error; return; }

            // Хеш входов — на момент плана: правка за время сборки сделает фильм устаревшим, а не «собранным»
            var hash = FilmStaleness.SourceHash(film.Project.RootPath, doc);
            var progress = new Progress<VideoAssembleProgress>(p => OnProgress(ownerId, scope, film, entry, p));
            var result = await dsp!.AssembleAsync(planned.Plan!, tmp, progress, entry.Cts.Token);
            if (!result.Ok) { error = result.Error; return; }

            outFile = MoveToFinal(film, tmp);
            if (outFile is null) { error = "Не нашлось свободного имени для film.mp4"; return; }
            var recorded = films.Films.Update(film.Full, current => current with
            {
                Builds = [.. current.Builds.TakeLast(KeptBuilds - 1), new FilmBuild(outFile, hash, Now)],
            });
            if (recorded.Status != FilmStore.WriteStatus.Ok)
                log.LogWarning("Видео: сборка {File} готова, но запись в {Path} не удалась: {Status}", outFile, film.Relative, recorded.Status);
            state = FilmBuildStates.Done;
        }
        catch (OperationCanceledException)
        {
            state = FilmBuildStates.Cancelled;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Видео: сборка фильма {Path} упала", film.Relative);
            error = "Сборка не удалась: внутренняя ошибка";
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
            RecordSpend(ownerId, scope, sw.Elapsed, initiator);
            var done = new FilmBuildStatusDto(state, state == FilmBuildStates.Done ? 1 : entry.Status.Progress, outFile, error, entry.Status.StartedAt);
            registry.Set(entry, done);
            await PublishAsync(ownerId, scope, film);
            await RecordBuildAsync(ownerId, scope, film, done, initiator, sessionId);
            entry.Cts.Dispose();
        }
    }

    // План по документу: клипы пробуются (длина, звук, размер), обрезка укладывается в длину клипа
    private async Task<(FilmPlan? Plan, string? Error)> PlanAsync(FilmService.Resolved film, FilmDocument doc, CancellationToken ct)
    {
        var clips = new List<FilmPlanClip>();
        foreach (var item in doc.Items)
        {
            var clip = Resolve(film.Project, item.File);
            if (!clip.IsOk) return (null, clip.Error);
            var info = await dsp!.ProbeAsync(clip.Value!.Full, ct);
            if (info is null) return (null, $"Клип не разобран как видео: {item.File}");
            var start = Math.Clamp(item.Trim[0], 0, Math.Max(0, info.Seconds - MinPiece));
            var end = Math.Min(item.Trim[1], info.Seconds);
            if (end - start < MinPiece) return (null, $"Обрезка клипа {item.File} выходит за его длину ({info.Seconds:0.#} с)");
            clips.Add(new FilmPlanClip(clip.Value.Full, start, end, info.HasAudio));
        }
        FilmPlanMusic? music = null;
        if (doc.Music is { } m)
        {
            var track = Resolve(film.Project, m.File);
            if (!track.IsOk) return (null, track.Error);
            music = new FilmPlanMusic(track.Value!.Full, m.Volume, m.FadeOut);
        }
        var size = FilmFormat.AspectSize(doc.Aspect) ?? (1280, 720);
        return (new FilmPlan(size.Width, size.Height, 24, clips,
            [.. doc.Cuts.Select(c => new FilmPlanCut(c.Type, c.Sec))], music, Timeout), null);
    }

    // film.mp4 → film.v2.mp4 → …: CreateNew через Move без перезаписи; временный файл лежит в той же папке
    private static string? MoveToFinal(FilmService.Resolved film, string tmp)
    {
        var folder = FilmPaths.FolderOf(film.Relative);
        var fullFolder = Path.GetDirectoryName(film.Full)!;
        for (var attempt = 1; attempt <= MaxFileAttempts; attempt++)
        {
            var name = attempt == 1 ? "film.mp4" : $"film.v{attempt}.mp4";
            var target = Path.Combine(fullFolder, name);
            if (File.Exists(target)) continue;
            // Символическая ссылка с этим именем (даже висячая) — не наш файл: пропускаем
            if (IsLink(film.Project.RootPath, target)) continue;
            try
            {
                File.Move(tmp, target, overwrite: false);
                return $"{folder}/{name}";
            }
            catch (IOException) when (File.Exists(target)) { }
        }
        return null;
    }

    private static bool IsLink(string root, string full) => ProjectLinkGuard.HasLink(root, full);

    private void OnProgress(string ownerId, VideoEditScope scope, FilmService.Resolved film, FilmBuildRegistry.Entry entry,
        VideoAssembleProgress p)
    {
        // Progress<T> доставляет отчёты с запозданием: после завершения сборки поздний отчёт не должен вернуть «идёт»
        if (!entry.Active) return;
        var state = p.Stage == VideoAssembleProgress.Waiting ? FilmBuildStates.Waiting : FilmBuildStates.Running;
        var before = entry.Status;
        // Событие только на смену состояния или процента: не заливаем сокет на каждую строку -progress
        if (before.State == state && (int)(before.Progress * 100) == (int)(p.Fraction * 100)) return;
        registry.Set(entry, before with { State = state, Progress = Math.Max(before.Progress, p.Fraction) });
        _ = PublishAsync(ownerId, scope, film);
    }

    private async Task PublishAsync(string ownerId, VideoEditScope scope, FilmService.Resolved film)
    {
        try
        {
            if (films.State(ownerId, scope, film.Relative).Value is { } state)
                await films.Threads.BroadcastFilmAsync(ownerId, scope.Key, film.Relative, state);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Видео: состояние сборки {Path} не разослано", film.Relative);
        }
    }

    // Итог сборки в ленте — ЕДИНСТВЕННАЯ точка записи для человека и агента (требование Андрея 2026-10-02): готовая
    // сборка — карточка video_film_built, отказ и отмена — тихая строка video_note; initiator только в data
    private async Task RecordBuildAsync(string ownerId, VideoEditScope scope, FilmService.Resolved film, FilmBuildStatusDto status,
        string initiator, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !films.Threads.OwnChat(ownerId, scope.Key, sessionId)) return;
        var agent = initiator == VideoInitiators.Agent;
        var name = FilmPaths.NameOf(film.Relative);
        var data = new { filmPath = film.Relative, file = status.File, state = status.State, error = status.Error, initiator };
        if (status.State == FilmBuildStates.Done)
            await films.Threads.FilmBuiltAsync(sessionId.Trim(), $"{(agent ? "Claude собрал" : "Вы собрали")} фильм {name}: {status.File}", data);
        else
            await films.Threads.NoteAsync(sessionId.Trim(),
                $"Сборка фильма {name} {(status.State == FilmBuildStates.Cancelled ? "отменена" : "не удалась")}", data);
    }

    // Сборка денег не стоит, но попадает в учёт нулём: видно, что фильм собирали и сколько это заняло
    private void RecordSpend(string ownerId, VideoEditScope scope, TimeSpan elapsed, string initiator)
    {
        if (spend is null) return;
        try
        {
            spend.Record(new SpendRecord
            {
                Timestamp = Now,
                OwnerId = ownerId,
                ProjectId = VideoEditScope.ProjectIdOf(scope.Key),
                Initiator = initiator == VideoInitiators.Agent ? SpendInitiators.Agent : SpendInitiators.Human,
                Provider = "ffmpeg",
                Source = SpendSources.Free,
                CostUsd = 0,
                DurationMs = (long)elapsed.TotalMilliseconds,
                Label = SpendLabel,
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Видео: не удалось записать трату сборки фильма");
        }
    }
}
