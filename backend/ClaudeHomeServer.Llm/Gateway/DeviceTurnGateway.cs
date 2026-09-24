using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Шов IDeviceTurnGateway для удалённого раннера: старт хода через шлюз — единственная точка
// UpstreamSelector.StartTurn, своей логики выбора маршрута здесь нет.
public sealed class DeviceTurnGateway(UpstreamSelector selector, TurnTokenService tokens) : IDeviceTurnGateway
{
    public DeviceTurnGatewayStart StartTurn(string ownerId, string sessionId, string deviceId, string? model)
    {
        // Устройство к токену не привязываем: вход шлюза (TurnTokenEndpointFilter) устройство
        // звонящего пока не проверяет, и токен с DeviceId получил бы 401 на каждом запросе.
        // Привязка вернётся вместе с проверкой учётки устройства на входе шлюза.
        var start = selector.StartTurn(tokens, ownerId, sessionId, deviceId: null, model);
        return start.Token is { } issued
            ? new DeviceTurnGatewayStart(new DeviceExecGateway(issued.Grant.TurnId, issued.Token), null)
            : new DeviceTurnGatewayStart(null, start.FailureText ?? "Шлюз не выдал ходу маршрут.");
    }

    public void EndTurn(string gatewayTurnId) => tokens.RevokeTurn(gatewayTurnId);
}
