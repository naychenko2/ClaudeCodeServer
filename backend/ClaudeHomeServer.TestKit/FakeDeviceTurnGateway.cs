using System.Collections.Concurrent;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Шов шлюза для тестов раннера: выдаёт ходу заданный токен либо отказывает текстом
/// <see cref="Refuse"/>; помнит выдачи и отзывы.
/// </summary>
internal sealed class FakeDeviceTurnGateway : IDeviceTurnGateway
{
    private int _issued;

    public string Token { get; init; } = "tt_test-turn-token";
    public string? Refuse { get; set; }
    public ConcurrentQueue<(string OwnerId, string SessionId, string DeviceId, string? Model)> Started { get; } = new();
    public ConcurrentQueue<string> Ended { get; } = new();
    public TaskCompletionSource<string> FirstEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeviceTurnGatewayStart StartTurn(string ownerId, string sessionId, string deviceId, string? model)
    {
        Started.Enqueue((ownerId, sessionId, deviceId, model));
        return Refuse is not null
            ? new DeviceTurnGatewayStart(null, Refuse)
            : new DeviceTurnGatewayStart(new DeviceExecGateway($"gw-{Interlocked.Increment(ref _issued)}", Token), null);
    }

    public void EndTurn(string gatewayTurnId)
    {
        Ended.Enqueue(gatewayTurnId);
        FirstEnded.TrySetResult(gatewayTurnId);
    }
}
