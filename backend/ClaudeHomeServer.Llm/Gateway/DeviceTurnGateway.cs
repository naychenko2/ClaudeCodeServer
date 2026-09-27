using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Services.Llm.Gateway;

// Шов IDeviceTurnGateway для удалённого раннера: старт хода через шлюз — единственная точка
// UpstreamSelector.StartTurn, своей логики выбора маршрута здесь нет.
//
// Токен привязан к устройству (шлюз пускает его только вместе с учёткой этого устройства,
// TurnTokenEndpointFilter) и живёт, пока жив процесс CLI: отзыв — EndTurn раннера по выходу,
// kill или сбою запуска, а не turn/completed.
public sealed class DeviceTurnGateway(UpstreamSelector selector, TurnTokenService tokens) : IDeviceTurnGateway
{
    public DeviceTurnGatewayStart StartTurn(string ownerId, string sessionId, string deviceId, string? model)
    {
        var start = selector.StartTurn(tokens, ownerId, sessionId, deviceId, model, TurnTokenLifetime.Process);
        return start.Token is { } issued
            ? new DeviceTurnGatewayStart(new DeviceExecGateway(issued.Grant.TurnId, issued.Token), null)
            : new DeviceTurnGatewayStart(null, start.FailureText ?? "Шлюз не выдал ходу маршрут.");
    }

    public void EndTurn(string gatewayTurnId) => tokens.RevokeTurn(gatewayTurnId);
}
