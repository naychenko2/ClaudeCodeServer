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
/// Потокобезопасно: добавление и снятие идут из OnConnected/OnDisconnected разных
/// соединений, чтение — из HTTP-запроса членства. Лок один и грубый: соединения
/// открываются и закрываются редко, а точность здесь дороже пропускной способности.
/// </summary>
public sealed class UserConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _byUser = [];

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

    /// <summary>Снять соединение (OnDisconnectedAsync). Пустой набор пользователя удаляется за ним.</summary>
    public void Remove(string? userId, string connectionId)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_gate)
        {
            if (!_byUser.TryGetValue(userId, out var connections)) return;
            connections.Remove(connectionId);
            if (connections.Count == 0) _byUser.Remove(userId);
        }
    }

    /// <summary>Все живые соединения пользователя — для выкидывания из групп хаба.</summary>
    public IReadOnlyList<string> ConnectionIdsOf(string userId)
    {
        lock (_gate)
            return _byUser.TryGetValue(userId, out var connections) ? connections.ToArray() : [];
    }
}
