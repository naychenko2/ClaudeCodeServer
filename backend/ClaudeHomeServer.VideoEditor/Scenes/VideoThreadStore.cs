using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.VideoEditor.Contracts;

namespace ClaudeHomeServer.Services.VideoEditor.Scenes;

// Хранилище нитей сцен и фокуса чата (ADR-022 §2): data/video-threads/{ownerId}/{sessionId}.json.
// Корень отдельный от рабочей папки модуля (data/video-editor): TTL-чистка его не видит, а бэкап берёт
// по умолчанию — карточка в ленте живёт бессрочно, её нить тоже.
//
// Сессию не трогает никогда: связь с чатом — только по sessionId, у Session полей нитей нет, смена
// фокуса не пишет sessions.json и не двигает UpdatedAt.
//
// Записи от человека идут с ревизией, от которой он считал (устарела — Conflict, на ручке 409 со свежим
// состоянием). Записи от сервера (запуск, его итог, сохранение) идут с revision = null: они не затирают
// чужую правку, а дописывают факт, и тоже поднимают ревизию.
public sealed class VideoThreadStore(string root, TimeProvider? time = null)
{
    public const string DirName = "video-threads";
    public const int MaxEvents = 30;
    public const int MaxCount = 4;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static VideoThreadStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new VideoThreadStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public DateTime Now() => _time.GetUtcNow().UtcDateTime;

    public VideoThreadsState Get(string ownerId, string sessionId)
    {
        lock (_gate) return Read(ownerId, sessionId);
    }

    // Новая сцена в папке folder (путь проекта через «/», "" — не задана), сразу в фокусе. Настройки —
    // из префов области (вызывающий собирает), пустые поля берутся при запуске по цепочке
    public VideoThreadWrite AddScene(string ownerId, string sessionId, string folder, VideoSceneSettingsDto settings,
        long? revision, string? name = null)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new VideoThreadWrite(VideoThreadWriteStatus.Conflict, current);
            if (!ValidSettings(settings))
                return new VideoThreadWrite(VideoThreadWriteStatus.Invalid, current);

            var scene = new VideoSceneDto(NewId(), name ?? NextName(current), folder, settings, [], null, [], null, [], null, Now());
            var next = current with
            {
                Scenes = [.. current.Scenes, scene],
                Focus = current.Focus with { SceneId = scene.SceneId },
                Revision = current.Revision + 1,
            };
            Save(ownerId, sessionId, next);
            return new VideoThreadWrite(VideoThreadWriteStatus.Ok, next) { Scene = scene };
        }
    }

    // Взять сцену и/или фильм в работу: sceneId = null снимает выбор сцены, filmPath = null — открытый фильм.
    // Тот же фокус повторно — Ok без записи
    public VideoThreadWrite SetFocus(string ownerId, string sessionId, VideoFocusDto focus, long? revision)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new VideoThreadWrite(VideoThreadWriteStatus.Conflict, current);
            if (focus.SceneId is not null && current.Scenes.All(s => s.SceneId != focus.SceneId))
                return new VideoThreadWrite(VideoThreadWriteStatus.SceneNotFound, current);
            if (current.Focus == focus)
                return new VideoThreadWrite(VideoThreadWriteStatus.Ok, current);

            var next = current with { Focus = focus, Revision = current.Revision + 1 };
            Save(ownerId, sessionId, next);
            return new VideoThreadWrite(VideoThreadWriteStatus.Ok, next);
        }
    }

    // Убрать сцену, где нечего терять (передумал или пустая), вместе с фокусом. У сцены с версиями или
    // идущим запуском — Invalid
    public VideoThreadWrite Remove(string ownerId, string sessionId, string sceneId, long revision) =>
        Mutate(ownerId, sessionId, sceneId, revision, (state, scene) =>
        {
            if (scene.Versions.Count > 0 || HasRunning(scene))
                return new VideoThreadWrite(VideoThreadWriteStatus.Invalid, state);
            return new VideoThreadWrite(VideoThreadWriteStatus.Ok, state with
            {
                Scenes = [.. state.Scenes.Where(s => s.SceneId != sceneId)],
                Focus = state.Focus.SceneId == sceneId ? state.Focus with { SceneId = null } : state.Focus,
            });
        });

    // Настройки сцены (текст, кадры, поставщик, модель, длительность…). Пишутся от человека (с ревизией)
    // и агентом (null — без сверки)
    public VideoThreadWrite SetSettings(string ownerId, string sessionId, string sceneId, VideoSceneSettingsDto settings,
        long? revision, VideoThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, sceneId, revision, (state, scene) =>
            ValidSettings(settings)
                ? Replace(state, scene with { Settings = settings }, log)
                : new VideoThreadWrite(VideoThreadWriteStatus.Invalid, state));

    // «Продолжить от версии»: версия становится текущей, ничего не удаляет; focus — заодно взять сцену в работу
    public VideoThreadWrite SetCurrentVersion(string ownerId, string sessionId, string sceneId, string versionId,
        long? revision, bool focus = false) =>
        Mutate(ownerId, sessionId, sceneId, revision, (state, scene) =>
        {
            if (scene.Versions.All(v => v.VersionId != versionId))
                return new VideoThreadWrite(VideoThreadWriteStatus.VersionNotFound, state);
            var written = Replace(state, scene with { CurrentVersionId = versionId });
            return focus ? written with { State = written.State with { Focus = written.State.Focus with { SceneId = sceneId } } } : written;
        });

    // Запуск в сцену. Повтор с тем же jobId ничего не дописывает
    public VideoThreadWrite AddLaunch(string ownerId, string sessionId, string sceneId, VideoLaunchDto launch,
        VideoThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, sceneId, null, (state, scene) =>
            scene.Launches.Any(l => l.JobId == launch.JobId)
                ? new VideoThreadWrite(VideoThreadWriteStatus.Ok, state)
                : Replace(state, scene with { Launches = [.. scene.Launches, launch] }, log));

    // Запуск кончился: status — VideoLaunchStatus.*, results — варианты по порядку. Каждый вариант — новая
    // версия внизу с провайдером/моделью/лицензией запуска и снимком входов; вариант, уже ставший
    // версией, не дублируется. Текущей становится первая новая версия. Запуск уже не идёт — ничего
    // не меняет
    public VideoThreadWrite FinishLaunch(string ownerId, string sessionId, string sceneId, string jobId, string status,
        IReadOnlyList<VideoVariantResult> results, VideoInputsSnapshotDto inputs, string? error = null,
        VideoThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, sceneId, null, (state, scene) =>
        {
            if (scene.Launches.FirstOrDefault(l => l.JobId == jobId) is not { Status: VideoLaunchStatus.Running } launch)
                return new VideoThreadWrite(VideoThreadWriteStatus.Ok, state);

            var number = scene.Versions.Count == 0 ? 1 : scene.Versions.Max(v => v.Number) + 1;
            var now = Now();
            List<VideoClipVersionDto> added = [.. results
                .Where(r => !scene.Versions.Any(v => v.JobId == jobId && v.Variant == r.Variant))
                .Select((r, i) => new VideoClipVersionDto(NewId(), number + i, jobId, r.Variant, launch.Provider, launch.Model,
                    r.DurationSec, r.SizeBytes, r.HasSound, launch.License, r.Cost, launch.Initiator, inputs, now))];
            var after = scene with
            {
                Versions = [.. scene.Versions, .. added],
                CurrentVersionId = added.Count > 0 ? added[0].VersionId : scene.CurrentVersionId,
                Launches = [.. scene.Launches.Select(l => l.JobId == jobId
                    ? l with { Status = status, Interrupted = status == VideoLaunchStatus.Interrupted, Error = error }
                    : l)],
            };
            return Replace(state, after, log) with { NewVersions = added };
        });

    // Версию сохранили в проект (блок 2 вызывает): запись «версия → файл». Повтор того же файла не дублируется
    public VideoThreadWrite AddSavedFile(string ownerId, string sessionId, string sceneId, VideoSavedFileDto saved,
        VideoThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, sceneId, null, (state, scene) =>
            scene.SavedFiles.Any(f => f.Path == saved.Path)
                ? new VideoThreadWrite(VideoThreadWriteStatus.Ok, state)
                : Replace(state, scene with { SavedFiles = [.. scene.SavedFiles, saved] }, log));

    // Место сцены в фильме (блок 2 вызывает при «В фильм →»); null — вне фильма
    public VideoThreadWrite SetFilmRef(string ownerId, string sessionId, string sceneId, VideoFilmRefDto? filmRef) =>
        Mutate(ownerId, sessionId, sceneId, null, (state, scene) => Replace(state, scene with { FilmRef = filmRef }));

    // Состояние для блока хода и записи журнала, ещё не показанные ходу; курсор сдвигается.
    // Ревизию не трогает: сборка хода — не правка состояния
    public (VideoThreadsState State, IReadOnlyList<VideoThreadEvent> Fresh) TakeForTurn(string ownerId, string sessionId)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            var cursor = current.TurnCursor;
            var fresh = current.Events.Where(e => cursor is null || e.At > cursor).ToList();
            if (fresh.Count > 0)
                Save(ownerId, sessionId, current with { TurnCursor = fresh.Max(e => e.At) });
            return (current, fresh);
        }
    }

    // Чат удалён — нити уходят вместе с ним (подписчик session/deleted)
    public void Delete(string ownerId, string sessionId)
    {
        lock (_gate)
        {
            var path = StatePath(ownerId, sessionId);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Ветка чата получает копию нитей источника с теми же sceneId и versionId: якоря в скопированном
    // history.json ветки продолжают разрешаться. false — у источника нитей нет
    public bool Copy(string ownerId, string fromSessionId, string toSessionId)
    {
        lock (_gate)
        {
            var from = StatePath(ownerId, fromSessionId);
            if (!File.Exists(from)) return false;
            var to = StatePath(ownerId, toSessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            return true;
        }
    }

    // Все чаты с нитями: (владелец, чат) — для сверки после перезапуска
    public IReadOnlyList<(string OwnerId, string SessionId)> Chats()
    {
        lock (_gate)
        {
            if (!Directory.Exists(Root)) return [];
            return [.. Directory.EnumerateDirectories(Root).SelectMany(dir => Directory.EnumerateFiles(dir, "*.json")
                .Select(file => (Path.GetFileName(dir), Path.GetFileNameWithoutExtension(file))))];
        }
    }

    // После перезапуска: каждый запуск в статусе running — interrupted с записью журнала для хода.
    // Версии, уже ставшие версиями, остаются. null — снимать нечего
    public VideoThreadsState? InterruptRunning(string ownerId, string sessionId,
        Func<VideoSceneDto, VideoLaunchDto, VideoThreadEvent> log)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            var events = current.Events.ToList();
            var changed = false;
            var scenes = current.Scenes.Select(s =>
            {
                if (!HasRunning(s)) return s;
                changed = true;
                foreach (var launch in s.Launches.Where(l => l.Status == VideoLaunchStatus.Running))
                    events.Add(log(s, launch));
                return s with
                {
                    Launches = [.. s.Launches.Select(l => l.Status == VideoLaunchStatus.Running
                        ? l with { Status = VideoLaunchStatus.Interrupted, Interrupted = true }
                        : l)],
                };
            }).ToList();
            if (!changed) return null;
            var next = current with
            {
                Scenes = scenes,
                Events = [.. events.TakeLast(MaxEvents)],
                Revision = current.Revision + 1,
            };
            Save(ownerId, sessionId, next);
            return next;
        }
    }

    // Задачи, чьи файлы держат версии сцен владельца: рабочая папка их не чистит по TTL, версия живёт
    // столько же, сколько нить
    public IReadOnlySet<string> ReferencedJobs(string ownerId)
    {
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var dir = Path.Combine(Root, Safe(ownerId));
            if (!Directory.Exists(dir)) return jobs;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                foreach (var scene in ReadFile(file).Scenes)
                    foreach (var version in scene.Versions)
                        jobs.Add(version.JobId);
        }
        return jobs;
    }

    public string StatePath(string ownerId, string sessionId) =>
        Path.Combine(Root, Safe(ownerId), Safe(sessionId) + ".json");

    public static bool HasRunning(VideoSceneDto scene) => scene.Launches.Any(l => l.Status == VideoLaunchStatus.Running);

    // Настройки в пределах: число вариантов 1..MaxCount, длительность положительная
    private static bool ValidSettings(VideoSceneSettingsDto s) =>
        s.Count is null or (>= 1 and <= MaxCount) && s.DurationSec is null or > 0;

    // Правка одной сцены: revision = null — запись сервера без сверки. Сцена не найдена — отказ без записи
    private VideoThreadWrite Mutate(string ownerId, string sessionId, string sceneId, long? revision,
        Func<VideoThreadsState, VideoSceneDto, VideoThreadWrite> change)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new VideoThreadWrite(VideoThreadWriteStatus.Conflict, current);
            if (current.Scenes.FirstOrDefault(s => s.SceneId == sceneId) is not { } scene)
                return new VideoThreadWrite(VideoThreadWriteStatus.SceneNotFound, current);

            var written = change(current, scene);
            if (written.Status != VideoThreadWriteStatus.Ok || written.State == current)
                return written with { State = current, Scene = written.Status == VideoThreadWriteStatus.Ok ? scene : null };

            var next = written.State with { Revision = current.Revision + 1 };
            Save(ownerId, sessionId, next);
            return written with { State = next, Scene = next.Scenes.FirstOrDefault(s => s.SceneId == sceneId) };
        }
    }

    // Сцена на своём месте плюс запись журнала, если есть (журнал — последние MaxEvents)
    private static VideoThreadWrite Replace(VideoThreadsState state, VideoSceneDto scene, VideoThreadEvent? log = null)
    {
        var old = state.Scenes.First(s => s.SceneId == scene.SceneId);
        if (old.Equals(scene) && log is null) return new VideoThreadWrite(VideoThreadWriteStatus.Ok, state);
        var next = state with { Scenes = [.. state.Scenes.Select(s => s.SceneId == scene.SceneId ? scene : s)] };
        if (log is not null) next = next with { Events = [.. next.Events.Append(log).TakeLast(MaxEvents)] };
        return new VideoThreadWrite(VideoThreadWriteStatus.Ok, next);
    }

    // «Сцена N»: следующий за наибольшим из уже названных так
    private static string NextName(VideoThreadsState state)
    {
        var max = state.Scenes
            .Select(s => s.Name.StartsWith("Сцена ", StringComparison.Ordinal) && int.TryParse(s.Name[6..], out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"Сцена {max + 1}";
    }

    private VideoThreadsState Read(string ownerId, string sessionId) => ReadFile(StatePath(ownerId, sessionId));

    private static VideoThreadsState ReadFile(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<VideoThreadsState>(File.ReadAllText(path), Json) is { } state)
                return state with
                {
                    Focus = state.Focus ?? new VideoFocusDto(null, null),
                    Scenes = [.. (state.Scenes ?? []).Select(Normalize)],
                    Events = state.Events ?? [],
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return VideoThreadsState.Empty;
    }

    // Пропущенные в файле списки читаются пустыми: поля добавляются аддитивно
    private static VideoSceneDto Normalize(VideoSceneDto s) => s with
    {
        Versions = s.Versions ?? [],
        Launches = s.Launches ?? [],
        SavedFiles = s.SavedFiles ?? [],
        Stale = null,
    };

    // Через временный файл: оборванная запись не оставит битый JSON
    private void Save(string ownerId, string sessionId, VideoThreadsState state)
    {
        var path = StatePath(ownerId, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    // id владельца и сессии — из claim и маршрута; маршрут приходит снаружи, отсюда белый список
    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}
