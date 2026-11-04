namespace ClaudeHomeServer.Services.Access;

/// <summary>
/// Реестр актора хода (S2a-1, сверка А2): «кто запустил идущий ход чата». Ключ — sessionId, а не
/// turnId: в MCP-вызов turnId не приезжает (контекст — владелец из <c>sub</c>, сессия-вызыватель
/// из <c>X-Caller-Session-Id</c>, хвост маршрута). Реестр только читается тулсетами; пишет его
/// ядро сессий в двух точках подачи хода, актор в MCP-конфиг, заголовки и env не попадает
/// (состав <c>tools/list</c> от актора зависеть не может — McpToolsetStabilityTests).
/// </summary>
public interface ITurnActorRegistry
{
    /// <summary>
    /// Актор хода чата. Владелец проверяется как у гейта делегирования (<c>GetOwned</c>).
    /// null — актор неизвестен: чата нет, он чужой или удалён, после рестарта реестр пуст, подача
    /// без инициатора. Вызывающий трактует null как «самое узкое» (fail-closed). Пока у прогона
    /// живы фоновые агенты, актор «липкий»; при смешении акторов выигрывает сужение.
    /// </summary>
    string? Actor(string sessionId, string ownerId);
}

/// <summary>
/// Вердикт рамки хода по чату (S2a-1): тулсетам нужен вердикт, а не актор. Контракт держит шов, а не
/// <see cref="TurnFrame.Resolve"/>: «чата нет / вызов без заголовка / чужой или удалённый» —
/// <see cref="TurnFrameKind.Narrow"/> (прецедент DelegatedTurnGate при failOpenWhenUnknown:false) и это
/// ОТЛИЧАЕТСЯ от «чат вне проекта» (<see cref="TurnFrameKind.Wide"/>: личное пространство и так его).
/// </summary>
public interface ITurnFrameSource
{
    /// <summary>Рамка хода чата <paramref name="sessionId"/> для владельца <paramref name="ownerId"/>.</summary>
    TurnFrameKind Frame(string? sessionId, string ownerId);
}
