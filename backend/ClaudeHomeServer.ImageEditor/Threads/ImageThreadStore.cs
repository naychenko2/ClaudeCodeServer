using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.ImageEditor.Threads;

// Хранилище нитей и фокуса чата (ADR-019 §1): data/image-threads/{ownerId}/{sessionId}.json.
// Корень отдельный от рабочей папки редактора (data/image-editor): TTL-чистка его не видит, а
// бэкап берёт по умолчанию — карточка в ленте живёт бессрочно, её нить тоже.
//
// Сессию не трогает никогда: смена фокуса — настройка чата, она не пишет sessions.json и не
// двигает UpdatedAt. У Session поэтому и нет полей нитей — они целиком живут в модуле.
public sealed class ImageThreadStore(string root, TimeProvider? time = null)
{
    public const string DirName = "image-threads";

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
            var stack = new ImageThreadStack(NewId(), [], null, false);
            var thread = new ImageThread(NewId(), file, [], draftFolder, [stack], stack.StackId,
                null, null, _time.GetUtcNow().UtcDateTime);
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

    private ImageThreadsState Read(string ownerId, string sessionId)
    {
        var path = StatePath(ownerId, sessionId);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<ImageThreadsState>(File.ReadAllText(path), Json) is { } state)
                return state with { Threads = state.Threads ?? [] };
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
