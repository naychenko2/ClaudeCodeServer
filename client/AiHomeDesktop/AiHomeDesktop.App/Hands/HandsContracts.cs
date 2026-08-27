namespace AiHomeDesktop.App.Hands;

/// <summary>
/// Заявка чата на сеанс рук: человек выбирает по имени чата, проекта и персоны.
/// Безымянной кнопки «Начать сеанс» не бывает — сеанс всегда начинается для конкретного чата.
/// </summary>
public sealed record HandsRequest(
    string ChatSessionId,
    string? Chat,
    string? Project,
    string? Persona,
    DateTimeOffset RequestedAt)
{
    public string ChatTitle => string.IsNullOrWhiteSpace(Chat) ? ChatSessionId : Chat!;

    /// <summary>Строка для списка заявок: «Чат — проект — персона», пустые части опускаются.</summary>
    public string Subtitle => string.Join(" · ", new[] { Project, Persona }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Текущий сеанс рук этого устройства (ответ GET /api/devices/hands).</summary>
public sealed record HandsSessionInfo(
    string ChatSessionId,
    string? Chat,
    string? Device,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset IdleDeadlineAt,
    DateTimeOffset HardDeadlineAt)
{
    public string ChatTitle => string.IsNullOrWhiteSpace(Chat) ? ChatSessionId : Chat!;

    /// <summary>
    /// Сколько осталось до предела — минимум из 15 минут простоя и потолка в 2 часа.
    /// Считает сам сервер (поле expiresAt), клиент только показывает отсчёт.
    /// </summary>
    public TimeSpan TimeLeft(DateTimeOffset now) => ExpiresAt > now ? ExpiresAt - now : TimeSpan.Zero;

    /// <summary>Отчёт кончается простоем, а не потолком, — так и пишем в окне.</summary>
    public bool EndsByIdle => IdleDeadlineAt <= HardDeadlineAt;
}

/// <summary>Чем кончилась попытка начать сеанс. Отказ сервера — 409 { outcome, message }.</summary>
public sealed record HandsStartOutcome(bool Started, string? Outcome, string Message, HandsSessionInfo? Session = null);

/// <summary>
/// Поводы остановки, которые называет сам клиент. Совпадают с
/// <c>DesktopHandsEndReasons</c> на сервере — своих клиент не изобретает.
/// </summary>
public static class HandsStopReasons
{
    /// <summary>Человек нажал «Стоп» (кнопка в окне или пункт трея).</summary>
    public const string Stopped = "stopped";

    /// <summary>Окно оболочки закрыто. Жизнь в трее закрытием НЕ считается.</summary>
    public const string ClientClosed = "client_closed";
}

/// <summary>
/// Эндпоинты сеанса рук со стороны устройства. Ходят под токеном устройства: заголовки
/// <c>Authorization: Device {токен}</c> и <c>X-Device-Fingerprint</c> ставит оболочка на
/// самом HttpClient — токен здесь не живёт.
/// </summary>
public interface IHandsApi
{
    /// <summary>GET /api/devices/hands/requests — очередь заявок владельца.</summary>
    Task<IReadOnlyList<HandsRequest>> RequestsAsync(CancellationToken ct = default);

    /// <summary>GET /api/devices/hands — сеанс этого устройства либо null.</summary>
    Task<HandsSessionInfo?> CurrentAsync(CancellationToken ct = default);

    /// <summary>POST /api/devices/hands/start — начать сеанс для выбранного чата.</summary>
    Task<HandsStartOutcome> StartAsync(string chatSessionId, CancellationToken ct = default);

    /// <summary>POST /api/devices/hands/stop — «Стоп» вне канала агента.</summary>
    Task<bool> StopAsync(string reason, CancellationToken ct = default);
}

/// <summary>Что показывает трей: отдельная иконка на время активного сеанса.</summary>
public enum HandsIndicatorState
{
    /// <summary>Сеанса нет.</summary>
    Idle,

    /// <summary>Есть заявки, которых человек ещё не видел.</summary>
    Requested,

    /// <summary>Сеанс идёт: руки отданы чату.</summary>
    Active
}

/// <summary>Индикатор сеанса в трее. Реализация — иконка трея оболочки.</summary>
public interface IHandsIndicator
{
    /// <summary>Обновить иконку и подсказку. Подсказка называет чат — «руки на home» без чата бессмысленно.</summary>
    void Update(HandsIndicatorState state, string? chatTitle);
}
