using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.AudioEditor.Threads;

// Хранилище нитей и фокуса чата (ADR-021 §2): data/audio-threads/{ownerId}/{sessionId}.json.
// Корень отдельный от рабочей папки модуля (data/audio-editor): TTL-чистка его не видит, а бэкап
// берёт по умолчанию — карточка в ленте живёт бессрочно, её нить тоже.
//
// Сессию не трогает никогда: связь с чатом — только по sessionId, у Session полей нитей нет, смена
// фокуса не пишет sessions.json и не двигает UpdatedAt.
//
// Записи от человека идут с ревизией, от которой он считал (устарела — Conflict, на ручке 409).
// Записи от сервера (запуск, его итог, сохранение) идут с revision = null: они не затирают чужую
// правку, а дописывают факт, и тоже поднимают ревизию.
//
// Форма повторяет ImageThreadStore, но без стопок и шагов: у звука правка без ИИ — версия.
public sealed class AudioThreadStore(string root, TimeProvider? time = null)
{
    public const string DirName = "audio-threads";
    public const int MaxEvents = 30;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static AudioThreadStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new AudioThreadStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public DateTime Now() => _time.GetUtcNow().UtcDateTime;

    public AudioThreadsState Get(string ownerId, string sessionId)
    {
        lock (_gate) return Read(ownerId, sessionId);
    }

    // Взять звук в работу: нить по этому файлу уже есть — фокус на неё (Existing), новую не плодим;
    // черновик заводится всегда новый. settings — настройки новой нити, существующую они не трогают;
    // name — предложенное имя файла черновика. revision = null — запись агента или сервера без сверки
    public AudioThreadWrite Open(string ownerId, string sessionId, string? file, string? draftFolder, long? revision,
        AudioThreadSettings? settings = null, string? name = null)
    {
        if ((file is null) == (draftFolder is null))
            throw new ArgumentException("Нужно ровно одно: звуковой файл или папка черновика");
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new AudioThreadWrite(AudioThreadWriteStatus.Conflict, current);
            if (settings is not null && !AudioModes.IsValid(settings.Mode))
                return new AudioThreadWrite(AudioThreadWriteStatus.Invalid, current);

            if (file is not null && current.Threads.FirstOrDefault(t => t.File == file) is { } existing)
            {
                var focused = current.Focus == existing.Id ? current
                    : current with { Focus = existing.Id, Revision = current.Revision + 1 };
                if (!ReferenceEquals(focused, current)) Save(ownerId, sessionId, focused);
                return new AudioThreadWrite(AudioThreadWriteStatus.Ok, focused) { Thread = existing, Existing = true };
            }

            var thread = NewThread(file, draftFolder, settings) with { Name = file is null ? name : null };
            var next = current with
            {
                Threads = [.. current.Threads, thread],
                Focus = thread.Id,
                Revision = current.Revision + 1,
            };
            Save(ownerId, sessionId, next);
            return new AudioThreadWrite(AudioThreadWriteStatus.Ok, next) { Thread = thread };
        }
    }

    // Взять нить в работу или снять выбор (threadId = null). Тот же фокус повторно — Ok без записи
    public AudioThreadWrite SetFocus(string ownerId, string sessionId, string? threadId, long? revision)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new AudioThreadWrite(AudioThreadWriteStatus.Conflict, current);
            if (threadId is not null && current.Threads.All(t => t.Id != threadId))
                return new AudioThreadWrite(AudioThreadWriteStatus.ThreadNotFound, current);
            if (current.Focus == threadId)
                return new AudioThreadWrite(AudioThreadWriteStatus.Ok, current);

            var next = current with { Focus = threadId, Revision = current.Revision + 1 };
            Save(ownerId, sessionId, next);
            return new AudioThreadWrite(AudioThreadWriteStatus.Ok, next);
        }
    }

    // Убрать нить, где нечего терять (передумал или пустой черновик), вместе с фокусом. У нити с
    // версиями или идущим запуском — Invalid
    public AudioThreadWrite Remove(string ownerId, string sessionId, string threadId, long revision) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
        {
            if (thread.HasVersions || thread.HasRunningLaunch)
                return new AudioThreadWrite(AudioThreadWriteStatus.Invalid, state);
            var next = state with
            {
                Threads = [.. state.Threads.Where(t => t.Id != threadId)],
                Focus = state.Focus == threadId ? null : state.Focus,
            };
            return new AudioThreadWrite(AudioThreadWriteStatus.Ok, next);
        });

    // Последние настройки нити (режим, операция, поставщик, модель, поля операции). Пишутся и от
    // человека (с ревизией), и при запуске (null — без сверки). Другие нити чата не трогает
    public AudioThreadWrite SetSettings(string ownerId, string sessionId, string threadId, AudioThreadSettings settings,
        long? revision) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
            AudioModes.IsValid(settings.Mode)
                ? Replace(state, thread with { Settings = settings })
                : new AudioThreadWrite(AudioThreadWriteStatus.Invalid, state));

    // «Продолжить от версии»: версия становится текущей, ничего не удаляет; focus — заодно взять
    // нить в работу
    public AudioThreadWrite SetCurrentVersion(string ownerId, string sessionId, string threadId, string versionId,
        long? revision, bool focus = false) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
        {
            if (thread.Version(versionId) is not { } version)
                return new AudioThreadWrite(AudioThreadWriteStatus.VersionNotFound, state);
            var written = Replace(state, thread with { CurrentVersionId = version.Id });
            return focus ? written with { State = written.State with { Focus = threadId } } : written;
        });

    // Запуск в нить: его версия-основа становится текущей (запускали от неё). Лицензия модели
    // фиксируется здесь, в запуске. Повтор с тем же jobId ничего не дописывает
    public AudioThreadWrite AddLaunch(string ownerId, string sessionId, string threadId, AudioThreadLaunch launch,
        AudioThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, null, (state, thread) =>
        {
            if (thread.Launches.Any(l => l.JobId == launch.JobId)) return new AudioThreadWrite(AudioThreadWriteStatus.Ok, state);
            return Replace(state, thread with
            {
                Launches = [.. thread.Launches, launch],
                CurrentVersionId = thread.Version(launch.BaseVersionId)?.Id ?? thread.CurrentVersionId,
            }, log);
        });

    // Запуск кончился: status — AudioThreadLaunchStatus.*, results — (номер варианта, файлы с ролями)
    // по порядку. Каждый вариант — новая версия внизу с основой и лицензией запуска; вариант, уже
    // ставший версией, не дублируется. Текущей становится первая новая версия, если человек не сменил
    // текущую, пока шла задача. Запуск уже не идёт — ничего не меняет. Набор файлов без файлов, с
    // неизвестной или повторной ролью — Invalid без записи
    public AudioThreadWrite FinishLaunch(string ownerId, string sessionId, string threadId, string jobId, string status,
        IReadOnlyList<(int Variant, IReadOnlyList<AudioVersionFile> Files)> results, AudioThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, null, (state, thread) =>
        {
            if (thread.Launches.FirstOrDefault(l => l.JobId == jobId) is not { Status: AudioThreadLaunchStatus.Running } launch)
                return new AudioThreadWrite(AudioThreadWriteStatus.Ok, state);
            if (!results.All(r => ValidFiles(r.Files)))
                return new AudioThreadWrite(AudioThreadWriteStatus.Invalid, state);

            var number = NextNumber(thread);
            var now = Now();
            List<AudioThreadVersion> added = [.. results
                .Where(r => !thread.Versions.Any(v => v.JobId == jobId && v.Variant == r.Variant))
                .Select((r, i) => new AudioThreadVersion(NewId(), number + i, jobId, r.Variant,
                    launch.BaseVersionId, r.Files, launch.License, now))];
            var moveCurrent = added.Count > 0 && thread.CurrentVersionId == launch.BaseVersionId;
            var after = thread with
            {
                Versions = [.. thread.Versions, .. added],
                CurrentVersionId = moveCurrent ? added[0].Id : thread.CurrentVersionId,
                Launches = [.. thread.Launches.Select(l => l.JobId == jobId ? l with { Status = status } : l)],
            };
            return Replace(state, after, log) with { NewVersions = added };
        });

    // Правка без ИИ (обрезка, фейды, громкость, сведение стемов) — новая версия от основы (null —
    // от текущей), она же становится текущей. jobId — папка рабочей области с файлами версии: по нему
    // её держит чистка по TTL и находит склейка. License — у правки без ИИ своей лицензии нет,
    // наследуется от основы: обрезанный трек YuE2 остаётся CC BY-NC
    public AudioThreadWrite AddEditVersion(string ownerId, string sessionId, string threadId,
        IReadOnlyList<AudioVersionFile> files, long? revision, AudioThreadEvent? log = null,
        string? jobId = null, string? baseVersionId = null) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
        {
            if (!ValidFiles(files))
                return new AudioThreadWrite(AudioThreadWriteStatus.Invalid, state);
            var basis = baseVersionId is null ? thread.CurrentVersion : thread.Version(baseVersionId);
            if (baseVersionId is not null && basis is null)
                return new AudioThreadWrite(AudioThreadWriteStatus.VersionNotFound, state);
            var version = new AudioThreadVersion(NewId(), NextNumber(thread), jobId, null, basis?.Id, files,
                basis?.License, Now());
            return Replace(state, thread with
            {
                Versions = [.. thread.Versions, version],
                CurrentVersionId = version.Id,
            }, log) with { NewVersions = [version] };
        });

    // Версию сохранили в проект: нить идёт за новым путём (черновик перестаёт быть черновиком), прежний
    // путь уходит в Lineage. Версии не трогает: исходник остаётся тем файлом, от которого начинали
    public AudioThreadWrite MoveToFile(string ownerId, string sessionId, string threadId, string path,
        AudioThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, null, (state, thread) => Replace(state, thread with
        {
            File = path,
            DraftFolder = null,
            Lineage = thread.File is { } old && old != path ? [.. thread.Lineage, old] : thread.Lineage,
        }, log));

    // Сохранили версию без основного звука (только стемы): нить остаётся при своём файле, черновик
    // получает имя группы вместо «Новый звук». Журнал пишется в любом случае
    public AudioThreadWrite NameDraft(string ownerId, string sessionId, string threadId, string name,
        AudioThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, null, (state, thread) =>
            Replace(state, thread.File is null ? thread with { Name = name } : thread, log));

    // Состояние для блока хода и записи журнала, ещё не показанные ходу; курсор сдвигается.
    // Ревизию не трогает: сборка хода — не правка состояния
    public (AudioThreadsState State, IReadOnlyList<AudioThreadEvent> Fresh) TakeForTurn(string ownerId, string sessionId)
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

    // Ветка чата получает копию нитей источника с теми же threadId и versionId: якоря в
    // скопированном history.json ветки продолжают разрешаться. false — у источника нитей нет
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

    // После перезапуска: каждый запуск в статусе running — Interrupted с записью журнала для хода.
    // Версии, уже ставшие версиями, остаются. null — снимать нечего (ничего не записано)
    public AudioThreadsState? InterruptRunning(string ownerId, string sessionId,
        Func<AudioThread, AudioThreadLaunch, AudioThreadEvent> log)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            var events = current.Events.ToList();
            var changed = false;
            var threads = current.Threads.Select(t =>
            {
                if (!t.HasRunningLaunch) return t;
                changed = true;
                foreach (var launch in t.Launches.Where(l => l.Status == AudioThreadLaunchStatus.Running))
                    events.Add(log(t, launch));
                return t with
                {
                    Launches = [.. t.Launches.Select(l => l.Status == AudioThreadLaunchStatus.Running
                        ? l with { Status = AudioThreadLaunchStatus.Interrupted }
                        : l)],
                };
            }).ToList();
            if (!changed) return null;
            var next = current with
            {
                Threads = threads,
                Events = [.. events.TakeLast(MaxEvents)],
                Revision = current.Revision + 1,
            };
            Save(ownerId, sessionId, next);
            return next;
        }
    }

    // Задачи, чьи файлы держат версии нитей владельца: рабочая папка их не чистит по TTL,
    // версия живёт столько же, сколько нить (как шаги у картинок)
    public IReadOnlySet<string> ReferencedJobs(string ownerId)
    {
        var jobs = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var dir = Path.Combine(Root, Safe(ownerId));
            if (!Directory.Exists(dir)) return jobs;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                foreach (var thread in ReadFile(file).Threads)
                    foreach (var version in thread.Versions)
                        if (version.JobId is { } job) jobs.Add(job);
        }
        return jobs;
    }

    public string StatePath(string ownerId, string sessionId) =>
        Path.Combine(Root, Safe(ownerId), Safe(sessionId) + ".json");

    // Правка одной нити: revision = null — запись сервера без сверки. Нить не найдена — отказ
    // без записи; результат Ok с изменённым состоянием сохраняется с новой ревизией
    private AudioThreadWrite Mutate(string ownerId, string sessionId, string threadId, long? revision,
        Func<AudioThreadsState, AudioThread, AudioThreadWrite> change)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new AudioThreadWrite(AudioThreadWriteStatus.Conflict, current);
            if (current.Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
                return new AudioThreadWrite(AudioThreadWriteStatus.ThreadNotFound, current);

            var written = change(current, thread);
            if (written.Status != AudioThreadWriteStatus.Ok || written.State == current)
                return written with { State = current, Thread = written.Status == AudioThreadWriteStatus.Ok ? thread : null };

            var next = written.State with { Revision = current.Revision + 1 };
            Save(ownerId, sessionId, next);
            return written with { State = next, Thread = next.Threads.FirstOrDefault(t => t.Id == threadId) };
        }
    }

    // Нить на своём месте в списке плюс запись журнала, если есть (журнал — последние MaxEvents)
    private static AudioThreadWrite Replace(AudioThreadsState state, AudioThread thread, AudioThreadEvent? log = null)
    {
        var old = state.Threads.First(t => t.Id == thread.Id);
        if (old.Equals(thread) && log is null) return new AudioThreadWrite(AudioThreadWriteStatus.Ok, state);
        var next = state with { Threads = [.. state.Threads.Select(t => t.Id == thread.Id ? thread : t)] };
        if (log is not null) next = next with { Events = [.. next.Events.Append(log).TakeLast(MaxEvents)] };
        return new AudioThreadWrite(AudioThreadWriteStatus.Ok, next);
    }

    // Хотя бы один файл, роли известные и без повторов, путь непустой
    private static bool ValidFiles(IReadOnlyList<AudioVersionFile> files) =>
        files.Count > 0
        && files.All(f => AudioFileRoles.IsValid(f.Role) && !string.IsNullOrWhiteSpace(f.Path))
        && files.Select(f => f.Role).Distinct(StringComparer.Ordinal).Count() == files.Count;

    private static int NextNumber(AudioThread thread) => thread.Versions.Count == 0 ? 1 : thread.Versions.Max(v => v.Number) + 1;

    // Нить по файлу начинается с исходника в работе, черновик — без версий
    private AudioThread NewThread(string? file, string? draftFolder, AudioThreadSettings? settings)
    {
        var now = Now();
        return new AudioThread(NewId(), file, [], draftFolder, now)
        {
            Versions = file is null ? [] : [AudioThreadVersion.Origin(file, now)],
            CurrentVersionId = file is null ? null : AudioThreadVersion.OriginId,
            Settings = settings,
        };
    }

    private AudioThreadsState Read(string ownerId, string sessionId) => ReadFile(StatePath(ownerId, sessionId));

    private static AudioThreadsState ReadFile(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AudioThreadsState>(File.ReadAllText(path), Json) is { } state)
                return state with
                {
                    Threads = [.. (state.Threads ?? []).Select(Normalize)],
                    Events = state.Events ?? [],
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return AudioThreadsState.Empty;
    }

    // Пропущенные в файле списки читаются пустыми: поля добавляются аддитивно
    private static AudioThread Normalize(AudioThread t) => t with
    {
        Lineage = t.Lineage ?? [],
        Versions = [.. (t.Versions ?? []).Select(v => v with { Files = v.Files ?? [] })],
        Launches = t.Launches ?? [],
    };

    // Через временный файл: оборванная запись не оставит битый JSON
    private void Save(string ownerId, string sessionId, AudioThreadsState state)
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
