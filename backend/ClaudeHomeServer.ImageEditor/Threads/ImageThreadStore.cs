using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Хранилище нитей и фокуса чата (ADR-019 §1): data/image-threads/{ownerId}/{sessionId}.json.
// Корень отдельный от рабочей папки редактора (data/image-editor): TTL-чистка его не видит, а
// бэкап берёт по умолчанию — карточка в ленте живёт бессрочно, её нить тоже.
//
// Сессию не трогает никогда: смена фокуса — настройка чата, она не пишет sessions.json и не
// двигает UpdatedAt. У Session поэтому и нет полей нитей — они целиком живут в модуле.
//
// Записи от человека идут с ревизией, от которой он считал (устарела — Conflict). Записи от
// сервера (старт задачи, сохранение, переименование файла) ревизию не сверяют: они не затирают
// чужую правку, а дописывают факт, и тоже поднимают ревизию.
public sealed class ImageThreadStore(string root, TimeProvider? time = null)
{
    public const string DirName = "image-threads";
    public const int MaxEvents = 30;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ImageThreadStore FromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return new ImageThreadStore(Path.Combine(Path.GetDirectoryName(dataPath)!, DirName));
    }

    public DateTime Now() => _time.GetUtcNow().UtcDateTime;

    public ImageThreadsState Get(string ownerId, string sessionId)
    {
        lock (_gate) return Read(ownerId, sessionId);
    }

    // Новая нить по файлу проекта или черновик по папке; focus — сразу взять её в работу.
    // Первая стопка заводится вместе с нитью: её якорь ложится в ленту
    public (ImageThreadsState State, ImageThread Thread) Create(string ownerId, string sessionId,
        string? file, string? draftFolder, bool focus)
    {
        if ((file is null) == (draftFolder is null))
            throw new ArgumentException("Нужно ровно одно: файл картинки или папка черновика");
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            var thread = NewThread(file, draftFolder);
            var next = current with
            {
                Threads = [.. current.Threads, thread],
                Focus = focus ? thread.Id : current.Focus,
                Revision = current.Revision + 1,
            };
            Save(ownerId, sessionId, next);
            return (next, thread);
        }
    }

    // Взять картинку в работу от человека (POST threads): нить по этому файлу уже есть — фокус на
    // неё (Existing), новую не плодим; черновик заводится всегда новый
    public ImageThreadWrite Open(string ownerId, string sessionId, string? file, string? draftFolder, long revision)
    {
        if ((file is null) == (draftFolder is null))
            throw new ArgumentException("Нужно ровно одно: файл картинки или папка черновика");
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision != current.Revision)
                return new ImageThreadWrite(ImageThreadWriteStatus.Conflict, current);

            if (file is not null && current.Threads.FirstOrDefault(t => t.File == file) is { } existing)
            {
                var focused = current.Focus == existing.Id ? current
                    : current with { Focus = existing.Id, Revision = current.Revision + 1 };
                if (!ReferenceEquals(focused, current)) Save(ownerId, sessionId, focused);
                return new ImageThreadWrite(ImageThreadWriteStatus.Ok, focused) { Thread = existing, Existing = true };
            }

            var thread = NewThread(file, draftFolder);
            var next = current with
            {
                Threads = [.. current.Threads, thread],
                Focus = thread.Id,
                Revision = current.Revision + 1,
            };
            Save(ownerId, sessionId, next);
            return new ImageThreadWrite(ImageThreadWriteStatus.Ok, next) { Thread = thread };
        }
    }

    // Взять нить в работу или снять выбор (threadId = null). revision — та, от которой считал
    // вызывающий; устарела — конфликт. Тот же фокус повторно — Ok без записи
    public ImageThreadWrite SetFocus(string ownerId, string sessionId, string? threadId, long revision)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision != current.Revision)
                return new ImageThreadWrite(ImageThreadWriteStatus.Conflict, current);
            if (threadId is not null && current.Threads.All(t => t.Id != threadId))
                return new ImageThreadWrite(ImageThreadWriteStatus.ThreadNotFound, current);
            if (current.Focus == threadId)
                return new ImageThreadWrite(ImageThreadWriteStatus.Ok, current);

            var next = current with { Focus = threadId, Revision = current.Revision + 1 };
            Save(ownerId, sessionId, next);
            return new ImageThreadWrite(ImageThreadWriteStatus.Ok, next);
        }
    }

    // Убрать нить без шагов (передумал брать картинку или пустой черновик) вместе с фокусом.
    // У нити с шагами или с задачей, чьи варианты ждут выбора, — Invalid: там есть что терять
    public ImageThreadWrite Remove(string ownerId, string sessionId, string threadId, long revision) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
        {
            if (thread.HasSteps || thread.PendingJobId is not null)
                return new ImageThreadWrite(ImageThreadWriteStatus.Invalid, state);
            var next = state with
            {
                Threads = [.. state.Threads.Where(t => t.Id != threadId)],
                Focus = state.Focus == threadId ? null : state.Focus,
            };
            return new ImageThreadWrite(ImageThreadWriteStatus.Ok, next);
        });

    // Новый шаг нити — взятый вариант или правка без ИИ. Текущий шаг последний в текущей стопке —
    // шаг дописывается в неё. Иначе был откат: текущая стопка замораживается (Old), новая стопка
    // начинается общими шагами до точки отката и новым шагом (Forked — вызывающему в ленту).
    // clearPendingJobId — задача, чьи варианты этим разобраны
    public ImageThreadWrite AddStep(string ownerId, string sessionId, string threadId, string stepId, long revision,
        string? clearPendingJobId = null, ImageThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
        {
            var pending = clearPendingJobId is not null && thread.PendingJobId == clearPendingJobId ? null : thread.PendingJobId;
            if (thread.Owns(stepId))
                return Replace(state, thread with { PendingJobId = pending, CurrentStepId = stepId }, log);

            var stack = thread.CurrentStack;
            var at = thread.CurrentStepId;
            if (stack is null || stack.Steps.Count == 0 || stack.Steps[^1] == at)
            {
                // Пустая текущая стопка: у свежей нити или после отката в старую — шаги до точки
                // отката переносятся в неё, чтобы стопка читалась с первого шага
                var head = stack is { Steps.Count: > 0 } ? stack.Steps : Prefix(thread, at);
                var target = stack ?? new ImageThreadStack(NewId(), [], at, false);
                var grown = target with { Steps = [.. head, stepId] };
                IReadOnlyList<ImageThreadStack> stacks = stack is null
                    ? [.. thread.Stacks, grown]
                    : [.. thread.Stacks.Select(s => s.StackId == grown.StackId ? grown : s)];
                return Replace(state, thread with
                {
                    Stacks = stacks,
                    CurrentStackId = grown.StackId,
                    CurrentStepId = stepId,
                    PendingJobId = pending,
                }, log);
            }

            var index = at is null ? -1 : IndexOf(stack.Steps, at);
            var keptFrom = at is null ? 1 : index >= 0 ? index + 2 : (int?)null;
            int? keptTo = keptFrom is { } from && from <= stack.Steps.Count ? stack.Steps.Count : null;
            if (keptTo is null) keptFrom = null;

            var frozen = stack with { Old = true };
            var fresh = new ImageThreadStack(NewId(), [.. Prefix(thread, at), stepId], at, false);
            var forkedThread = thread with
            {
                Stacks = [.. thread.Stacks.Select(s => s.StackId == stack.StackId ? frozen : s), fresh],
                CurrentStackId = fresh.StackId,
                CurrentStepId = stepId,
                PendingJobId = pending,
            };
            return Replace(state, forkedThread, log) with { Forked = new ImageThreadFork(frozen, fresh, keptFrom, keptTo) };
        });

    // Откат: сделать шаг нити текущим (null — вернуться к исходнику). Стопки не трогает — форк
    // случится на следующем шаге, если откатились не на последний
    public ImageThreadWrite Rollback(string ownerId, string sessionId, string threadId, string? stepId, long revision) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
            stepId is not null && !thread.Owns(stepId)
                ? new ImageThreadWrite(ImageThreadWriteStatus.StepNotFound, state)
                : Replace(state, thread with { CurrentStepId = stepId }));

    // «Не брать»: варианты задачи больше не ждут выбора. Задача уже не та — ничего не меняет
    public ImageThreadWrite Dismiss(string ownerId, string sessionId, string threadId, string jobId, long revision) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) =>
            Replace(state, thread with { PendingJobId = thread.PendingJobId == jobId ? null : thread.PendingJobId }));

    public ImageThreadWrite SetSettings(string ownerId, string sessionId, string threadId, ImageThreadSettings settings,
        long revision) =>
        Mutate(ownerId, sessionId, threadId, revision, (state, thread) => Replace(state, thread with { Settings = settings }));

    // Задача запущена по нити — её варианты ждут выбора (прежняя ожидавшая задача вытесняется)
    public ImageThreadWrite SetPending(string ownerId, string sessionId, string threadId, string jobId,
        ImageThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, null, (state, thread) => Replace(state, thread with { PendingJobId = jobId }, log));

    // Человек сохранил картинку нити в проект: нить идёт за новым файлом, прежний уходит в
    // Lineage; у черновика файл появляется впервые, папка черновика больше не нужна
    public ImageThreadWrite MoveToFile(string ownerId, string sessionId, string threadId, string path,
        ImageThreadEvent? log = null) =>
        Mutate(ownerId, sessionId, threadId, null, (state, thread) => Replace(state, thread with
        {
            File = path,
            DraftFolder = null,
            Lineage = thread.File is { } old && old != path ? [.. thread.Lineage, old] : thread.Lineage,
        }, log));

    // Файл переименовали через файловый API: пути нитей чата переписываются следом. false —
    // ни один путь не поменялся (ничего не записано)
    public bool RewritePaths(string ownerId, string sessionId, Func<string, string> move)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            var changed = false;
            var threads = current.Threads.Select(t =>
            {
                var file = t.File is null ? null : move(t.File);
                var lineage = t.Lineage.Select(move).ToList();
                if (file == t.File && lineage.SequenceEqual(t.Lineage)) return t;
                changed = true;
                return t with { File = file, Lineage = lineage };
            }).ToList();
            if (!changed) return false;
            Save(ownerId, sessionId, current with { Threads = threads, Revision = current.Revision + 1 });
            return true;
        }
    }

    // Состояние для блока хода и записи журнала, ещё не показанные ходу; курсор сдвигается.
    // Ревизию не трогает: сборка хода — не правка состояния
    public (ImageThreadsState State, IReadOnlyList<ImageThreadEvent> Fresh) TakeForTurn(string ownerId, string sessionId)
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

    // Все шаги, на которые ссылаются нити владельца: их не трогает чистка рабочей папки
    public IReadOnlySet<string> ReferencedSteps(string ownerId)
    {
        var steps = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var dir = Path.Combine(Root, Safe(ownerId));
            if (!Directory.Exists(dir)) return steps;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var state = ReadFile(file);
                foreach (var thread in state.Threads)
                {
                    foreach (var stack in thread.Stacks) steps.UnionWith(stack.Steps);
                    if (thread.CurrentStepId is { } current) steps.Add(current);
                }
            }
        }
        return steps;
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

    // Ветка чата получает копию нитей источника с теми же threadId и stackId: якоря в
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

    public string StatePath(string ownerId, string sessionId) =>
        Path.Combine(Root, Safe(ownerId), Safe(sessionId) + ".json");

    // Правка одной нити: revision = null — запись сервера без сверки. Нить не найдена — отказ
    // без записи; результат Ok с изменённым состоянием сохраняется с новой ревизией
    private ImageThreadWrite Mutate(string ownerId, string sessionId, string threadId, long? revision,
        Func<ImageThreadsState, ImageThread, ImageThreadWrite> change)
    {
        lock (_gate)
        {
            var current = Read(ownerId, sessionId);
            if (revision is { } r && r != current.Revision)
                return new ImageThreadWrite(ImageThreadWriteStatus.Conflict, current);
            if (current.Threads.FirstOrDefault(t => t.Id == threadId) is not { } thread)
                return new ImageThreadWrite(ImageThreadWriteStatus.ThreadNotFound, current);

            var written = change(current, thread);
            if (written.Status != ImageThreadWriteStatus.Ok || written.State == current)
                return written with { State = current, Thread = written.Status == ImageThreadWriteStatus.Ok ? thread : null };

            var next = written.State with { Revision = current.Revision + 1 };
            Save(ownerId, sessionId, next);
            return written with { State = next, Thread = next.Threads.FirstOrDefault(t => t.Id == threadId) };
        }
    }

    // Нить на своём месте в списке плюс запись журнала, если есть
    private ImageThreadWrite Replace(ImageThreadsState state, ImageThread thread, ImageThreadEvent? log = null)
    {
        var old = state.Threads.First(t => t.Id == thread.Id);
        if (old.Equals(thread) && log is null) return new ImageThreadWrite(ImageThreadWriteStatus.Ok, state);
        var next = state with { Threads = [.. state.Threads.Select(t => t.Id == thread.Id ? thread : t)] };
        if (log is not null) next = next with { Events = [.. next.Events.Append(log).TakeLast(MaxEvents)] };
        return new ImageThreadWrite(ImageThreadWriteStatus.Ok, next);
    }

    // Шаги от первого до точки отката включительно — по стопке, где она лежит (текущая первой)
    private static IReadOnlyList<string> Prefix(ImageThread thread, string? at)
    {
        if (at is null) return [];
        var stack = new[] { thread.CurrentStack }.Concat(thread.Stacks)
            .FirstOrDefault(s => s is not null && s.Steps.Contains(at));
        return stack is null ? [] : [.. stack.Steps.Take(IndexOf(stack.Steps, at) + 1)];
    }

    private static int IndexOf(IReadOnlyList<string> steps, string id)
    {
        for (var i = 0; i < steps.Count; i++)
            if (steps[i] == id) return i;
        return -1;
    }

    private ImageThread NewThread(string? file, string? draftFolder)
    {
        var stack = new ImageThreadStack(NewId(), [], null, false);
        return new ImageThread(NewId(), file, [], draftFolder, [stack], stack.StackId, null, null, Now());
    }

    private ImageThreadsState Read(string ownerId, string sessionId) => ReadFile(StatePath(ownerId, sessionId));

    private static ImageThreadsState ReadFile(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ImageThreadsState>(File.ReadAllText(path), Json) is { } state)
                return state with
                {
                    Threads = [.. (state.Threads ?? []).Select(t => t with
                    {
                        Lineage = t.Lineage ?? [],
                        Stacks = [.. (t.Stacks ?? []).Select(s => s with { Steps = s.Steps ?? [] })],
                    })],
                    Events = state.Events ?? [],
                };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return ImageThreadsState.Empty;
    }

    // Через временный файл: оборванная запись не оставит битый JSON
    private void Save(string ownerId, string sessionId, ImageThreadsState state)
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
