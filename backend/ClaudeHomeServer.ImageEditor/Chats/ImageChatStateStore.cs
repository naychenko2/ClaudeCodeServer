using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeHomeServer.Services.ImageEditor.Chats;

// Состояние редактора чата картинки на сервере (ADR-018 §2): {рабочая папка}/{ownerId}/chats/
// {sessionId}.json плюс последняя маска рядом. Живёт в рабочей папке редактора — тот же TTL-кеш
// на 7 дней и вне бэкапа, что варианты задач (ImageEditWorkspace.Sweep, BackupPaths).
//
// Revision растёт с каждой записью, которую видит редактор. Запись со старой ревизией — конфликт:
// фронт перечитывает состояние, а не затирает чужую правку (агента или другой вкладки).
// Журнал Events ведёт только сервер; курсор «что уже ушло в ход» живёт отдельно от ревизии,
// поэтому сборка хода не выбивает редактору 409.
public sealed class ImageChatStateStore(ImageEditWorkspace workspace, TimeProvider? time = null)
{
    // Журнал нужен ходу «с прошлого сообщения», а не как история: старое отрезается
    public const int MaxEvents = 20;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ImageChatState Empty => new(
        Prompt: "", PromptAuthor: ImageEditInitiator.Human, Provider: null, Model: null, Mode: EditMode.Auto,
        Count: 1, References: [], CharacterSlug: null, Marks: null, CanvasRevision: null, LastSentRevision: null,
        CurrentStepId: null, MatchSourceSize: true, Events: [], Revision: 0);

    // Файл на диске: состояние и курсор журнала для хода
    private sealed record Stored(ImageChatState State, DateTime? TurnCursor);

    public ImageChatState Get(string ownerId, string sessionId)
    {
        lock (_gate) return Read(ownerId, sessionId).State;
    }

    // Запись редактора. Сервер хранит свой журнал и ставит ревизию сам; маска приходит только
    // при смене canvasRevision, а смена холста без маски снимает прежнюю — она от другой картинки
    public ImageChatStateWrite Write(string ownerId, string sessionId, ImageChatState incoming, byte[]? mask)
    {
        lock (_gate)
        {
            var stored = Read(ownerId, sessionId);
            var current = stored.State;
            if (incoming.Revision != current.Revision)
                return new ImageChatStateWrite(false, current, []);

            var next = incoming with
            {
                References = incoming.References ?? [],
                Events = current.Events,
                Revision = current.Revision + 1,
            };
            var maskPath = MaskPath(ownerId, sessionId);
            if (mask is { Length: > 0 })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(maskPath)!);
                File.WriteAllBytes(maskPath, mask);
            }
            else if (next.CanvasRevision != current.CanvasRevision && File.Exists(maskPath))
            {
                File.Delete(maskPath);
            }
            Save(ownerId, sessionId, stored with { State = next });
            return new ImageChatStateWrite(true, next, Diff(current, next));
        }
    }

    // Запись журнала от сервера (запуск, итог): ревизия растёт — открытый редактор получит
    // новое состояние событием и не затрёт журнал своей устаревшей записью
    public ImageChatState AppendEvent(string ownerId, string sessionId, ImageChatEvent e)
    {
        lock (_gate)
        {
            var stored = Read(ownerId, sessionId);
            var events = stored.State.Events.Append(e).TakeLast(MaxEvents).ToList();
            var next = stored.State with { Events = events, Revision = stored.State.Revision + 1 };
            Save(ownerId, sessionId, stored with { State = next });
            return next;
        }
    }

    // Состояние для блока хода и события журнала, ещё не показанные ходу; курсор сдвигается.
    // Ревизию не трогает: сборка хода — не правка состояния
    public (ImageChatState State, IReadOnlyList<ImageChatEvent> NewEvents) TakeForTurn(string ownerId, string sessionId)
    {
        lock (_gate)
        {
            var stored = Read(ownerId, sessionId);
            var cursor = stored.TurnCursor;
            var fresh = stored.State.Events.Where(e => cursor is null || e.At > cursor).ToList();
            if (fresh.Count > 0)
                Save(ownerId, sessionId, stored with { TurnCursor = fresh.Max(e => e.At) });
            return (stored.State, fresh);
        }
    }

    public byte[]? ReadMask(string ownerId, string sessionId)
    {
        var path = MaskPath(ownerId, sessionId);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public DateTime Now() => _time.GetUtcNow().UtcDateTime;

    private Stored Read(string ownerId, string sessionId)
    {
        var path = StatePath(ownerId, sessionId);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) is { } stored)
                return stored with { State = stored.State with { Events = stored.State.Events ?? [], References = stored.State.References ?? [] } };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new Stored(Empty, null);
    }

    // Через временный файл: оборванная запись не оставит битый JSON, а переименование двигает
    // mtime папки — Sweep не снесёт состояние, которым пользуются
    private static void SaveTo(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private void Save(string ownerId, string sessionId, Stored stored) =>
        SaveTo(StatePath(ownerId, sessionId), JsonSerializer.Serialize(stored, Json));

    private string ChatsDir(string ownerId) =>
        Path.Combine(workspace.Root, Safe(ownerId), ImageEditorPaths.ChatsDirName);

    private string StatePath(string ownerId, string sessionId) =>
        Path.Combine(ChatsDir(ownerId), Safe(sessionId) + ".json");

    private string MaskPath(string ownerId, string sessionId) =>
        Path.Combine(ChatsDir(ownerId), Safe(sessionId) + ".mask.png");

    // Поля, которые поменялись, — для метки «✦ модель сменил Claude»; журнал и ревизия не в счёт
    public static IReadOnlyList<ImageChatStateChange> Diff(ImageChatState before, ImageChatState after)
    {
        var a = JsonSerializer.SerializeToElement(before with { Events = [], Revision = 0 }, Json);
        var b = JsonSerializer.SerializeToElement(after with { Events = [], Revision = 0 }, Json);
        var changes = new List<ImageChatStateChange>();
        foreach (var prop in b.EnumerateObject())
        {
            var old = a.GetProperty(prop.Name);
            if (old.GetRawText() != prop.Value.GetRawText())
                changes.Add(new ImageChatStateChange(prop.Name, old.Clone(), prop.Value.Clone()));
        }
        return changes;
    }

    // id владельца и сессии — из claim и маршрута; маршрут приходит снаружи, отсюда белый список
    private static string Safe(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Contains("..")
            || segment.IndexOfAny(['/', '\\', ':']) >= 0 || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Недопустимый идентификатор", nameof(segment));
        return segment;
    }
}

// Ok = false — конфликт ревизии, State — актуальное состояние на сервере
public sealed record ImageChatStateWrite(bool Ok, ImageChatState State, IReadOnlyList<ImageChatStateChange> Changes);
