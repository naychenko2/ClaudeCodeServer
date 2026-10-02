using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Серверный ход провайдера через шлюз LLM (ADR-016 §2, пункт про серверный ход): процесс CLI на
// сервере ходит в провайдера не напрямую, а через /gw/t/{turnId}/llm — ради нормализатора
// ответа (NormalizeToolInputArrays). Старт — единственная точка UpstreamSelector.StartTurn,
// своей логики выбора маршрута здесь нет, как у DeviceTurnGateway.
//
// Токен без устройства (шлюз принимает его только на LLM-маршруте и только с маршрутом
// провайдера, TurnTokenEndpointFilter) живёт, пока жив процесс CLI: один процесс ClaudeSession
// переживает несколько ходов, отзыв — End по выходу, kill или сбою запуска.
public sealed class ServerTurnGateway(UpstreamSelector selector, TurnTokenService tokens,
    IOptionsMonitor<LlmGatewayOptions> options)
{
    public bool Enabled => options.CurrentValue.Enabled;

    public GatewayTurnStart Start(string ownerId, string sessionId, string? model) =>
        selector.StartTurn(tokens, ownerId, sessionId, deviceId: null, model, TurnTokenLifetime.Process);

    public void End(string gatewayTurnId) => tokens.RevokeTurn(gatewayTurnId);

    // Токен ещё принимается шлюзом: его мог снять потолок жизни TurnTokenService, пока процесс
    // жив, — такому процессу следующий ход не отдаём, иначе он получил бы 401
    public bool IsAlive(string gatewayTurnId, string token) => tokens.Validate(gatewayTurnId, token) is not null;
}
