namespace ClaudeHomeServer.Hubs;

/// <summary>
/// Живые соединения пользователя: userId → набор connectionId (S1-4). Реестр нужен точкам
/// отзыва доступа: SignalR адресует группу только соединением, и чтобы выкинуть человека
/// из групп проекта СРАЗУ (разрез §2.2, без перезагрузки клиента), сервер должен знать все
/// его вкладки — на момент отзыва HTTP-запрос не видит ни одного соединения.
///
/// Ключ — userId соединения (его JWT sub), не арендатор проекта: это личная операция над
/// подключениями человека, а не проектное действие с ключом хранилищ (сторож
/// ProjectActionCallerKeyGuard называет это место в снимке явно).
///
/// Группы, на которые соединение подписывалось (S1-4b): перечисления групп в SignalR нет,
/// и снять отозванного со всех его групп можно только по записи подписки — по живым сервисам
/// проекта группа уже остановленного сервиса была бы пропущена и вернулась бы при следующем
/// его подъёме.
///
/// Потокобезопасно: добавление и снятие идут из OnConnected/OnDisconnected разных
/// соединений, чтение — из HTTP-запроса членства. Лок один и грубый: соединения
/// открываются и закрываются редко, а точность здесь дороже пропускной способности.
/// </summary>
public sealed class UserConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _byUser = [];
    // connectionId → записанные группы подписки (S1-4b). Ключ — соединение: SignalR
    // адресует группу и снимает с неё только соединением
    private readonly Dictionary<string, HashSet<string>> _groupsByConnection = [];

    /// <summary>Записать соединение пользователя (OnConnectedAsync). Соединение без sub не чьё — не пишем.</summary>
    public void Add(string? userId, string connectionId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var connections))
                _byUser[userId] = connections = [];
            connections.Add(connectionId);
        }
    }

    /// <summary>Снять соединение (OnDisconnectedAsync). Пустой набор пользователя удаляется за ним.
    /// Запись групп снимается по соединению всегда: OnDisconnected зовёт его при любом sub,
    /// а запись без владельца — просто мусор.</summary>
    public void Remove(string? userId, string connectionId)
    {
        lock (_gate)
        {
            _groupsByConnection.Remove(connectionId);
            if (string.IsNullOrEmpty(userId)) return;
            if (!_byUser.TryGetValue(userId, out var connections)) return;
            connections.Remove(connectionId);
            if (connections.Count == 0) _byUser.Remove(userId);
        }
    }

    /// <summary>Записать группу, на которую соединение подписывалось (точка JoinPreviewLog, S1-4b).
    /// Записывается ровно то, что передано Groups.AddToGroupAsync: точка отзыва снимает
    /// по этой же строке.</summary>
    public void AddGroup(string connectionId, string group)
    {
        if (string.IsNullOrEmpty(group)) return;
        lock (_gate)
        {
            if (!_groupsByConnection.TryGetValue(connectionId, out var groups))
                _groupsByConnection[connectionId] = groups = [];
            groups.Add(group);
        }
    }

    /// <summary>Записанные группы соединения — для точки отзыва членства (S1-4b).</summary>
    public IReadOnlyList<string> GroupsOf(string connectionId)
    {
        lock (_gate)
            return _groupsByConnection.TryGetValue(connectionId, out var groups) ? groups.ToArray() : [];
    }

    /// <summary>Все живые соединения пользователя — для выкидывания из групп хаба.</summary>
    public IReadOnlyList<string> ConnectionIdsOf(string userId)
    {
        lock (_gate)
            return _byUser.TryGetValue(userId, out var connections) ? connections.ToArray() : [];
    }
}
