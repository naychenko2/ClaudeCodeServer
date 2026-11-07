using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeHomeServer.Services.ChatUserState;

// Личное состояние чата по пользователю (разрез shared-projects §«Личное состояние чата»,
// шаг S1-6): data/chat-user-state/{userId}.json, ключ записи — sessionId.
//
// LastReadAt/IsPinned/NotificationsMuted лежат на Session (Core/Models/Session.cs) как значения
// владельца; в общем чате это состояния «первого прочитавшего», а не каждого участника. Стор —
// оверлей поверх Session, а не замена: нет файла или нет записи — Get отдаёт null («не задано»),
// решение о значении с Session — за вызывающим. Поля на Session стор не трогает, формат не ломается.
//
// Запись атомарна (JsonFileStore: tmp + File.Move) и сериализована по пути файла: весь
// read-modify-write одного файла пользователя держит лок по полному пути, а не по экземпляру
// стора — иначе два экземпляра на одном корне затирают записи друг друга. Битый файл не стирается молча: цельный
// уходит в .corrupt-*.bak, частично валинный поднимается поэлементно (битая запись пропускается,
// копия исходника ложится рядом в .partial-*.bak) — тот же путь, что у остальных JSON-сторов.
// Каталог лежит в data/ и попадает в бэкап сам (для него нет строки-исключения в BackupPaths).
public sealed class ChatUserStateStore(string root, TimeProvider? time = null)
{
    public const string DirName = "chat-user-state";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Корень — из существующего резолвера путей данных (тот же приём, что у ChatContextStore):
    // каталог data — директория DataPath, а не хардкод пути.
    public static string RootFromConfig(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, DirName);
    }

    // null — нет файла или нет записи для этого чата: состояние не задано, значение с Session
    // решает вызывающий. Чтение ничего не пишет.
    public UserChatState? Get(string userId, string sessionId)
    {
        RequireSessionId(sessionId);
        // Лок не берётся: вся запись держится под локом по пути файла в Update, а сам перенос
        // файла атомарен (tmp + File.Move, JsonFileStore.cs:332) — частично записанного файла
        // читателю не встречается.
        var all = ReadAll(FileFor(userId));
        return all is not null && all.TryGetValue(sessionId, out var state) ? state : null;
    }

    // Все записи пользователя одним чтением файла — для списков (по записи на чат файл не читаем).
    public IReadOnlyDictionary<string, UserChatState> GetAll(string userId) =>
        ReadAll(FileFor(userId)) ?? [];

    // Отметить чат прочитанным. По умолчанию — текущее время; вызывающий может передать отметку
    // сам (например, когда событие уже известно).
    public UserChatState MarkRead(string userId, string sessionId, DateTime? at = null) =>
        Update(userId, sessionId, s => s with { LastReadAt = at ?? _time.GetUtcNow().UtcDateTime });

    public UserChatState SetPinned(string userId, string sessionId, bool pinned) =>
        Update(userId, sessionId, s => s with { IsPinned = pinned });

    public UserChatState SetMuted(string userId, string sessionId, bool muted) =>
        Update(userId, sessionId, s => s with { NotificationsMuted = muted });

    // Одна операция правит одно поле, остальные сохраняет. Без изменений файл не трогается
    // (тот же порядок, что у записей без изменений в ChatContextStore).
    private UserChatState Update(string userId, string sessionId, Func<UserChatState, UserChatState> apply)
    {
        RequireSessionId(sessionId);
        var path = FileFor(userId);
        // Лок по пути файла, а не по экземпляру: файл один на пользователя и держит записи по
        // всем его чатам, а Update — read-modify-write всего файла. Два экземпляра стора на одном
        // корне (например, стор зарегистрирован в DI не синглтоном) без общего лока затирают
        // записи друг друга молча. Тот же приём, что у JsonFileStore: лок по полному пути,
        // число путей конечно — словарь не растёт бесконтрольно (JsonFileStore.cs:342-345).
        lock (LockFor(path))
        {
            var all = ReadAll(path) ?? [];
            var current = all.TryGetValue(sessionId, out var existing) ? existing : new UserChatState(null, null, null);
            var next = apply(current);
            if (next == current) return current;
            all[sessionId] = next;
            JsonFileStore.Save(path, all, Json);
            return next;
        }
    }

    // Лок на путь файла, общий для всех экземпляров стора этого процесса.
    private static readonly ConcurrentDictionary<string, Lock> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private static Lock LockFor(string path) =>
        PathLocks.GetOrAdd(Path.GetFullPath(path), _ => new Lock());

    private static Dictionary<string, UserChatState>? ReadAll(string path) =>
        JsonFileStore.Load<Dictionary<string, UserChatState>>(path, Json);

    private string FileFor(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.IndexOfAny(['/', '\\']) >= 0 || userId.Contains(".."))
            throw new ArgumentException("Недопустимый идентификатор пользователя");
        return SafePath.Join(root, userId + ".json");
    }

    // sessionId — ключ словаря внутри файла пользователя, в путь не кладётся; пустой ключ
    // всё равно бессмысленен и отсекается на входе.
    private static void RequireSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Недопустимый идентификатор сессии");
    }
}

// Состояние одного чата для одного пользователя. null поля — «не задано»: значение
// берётся с Session (решение за вызывающим), поэтому оверлей не дублирует поля молча.
public sealed record UserChatState(
    DateTime? LastReadAt,
    bool? IsPinned,
    bool? NotificationsMuted);
