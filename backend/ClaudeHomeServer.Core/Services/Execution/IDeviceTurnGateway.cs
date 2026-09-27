using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.Execution;

/// <summary>
/// Шов выдачи шлюза ходу на устройстве (ADR-016 §2): реализует вертикаль Llm поверх
/// <c>UpstreamSelector.StartTurn</c> и <c>TurnTokenService</c>, потребляет Execution
/// (<c>RemoteProcessRunner</c>) — прямой ссылки Execution → Llm нет.
///
/// Маршрут (провайдер, аккаунт, модель) решается и привязывается к токену ДО запуска CLI:
/// отказ — ход не стартует на устройстве вовсе.
/// </summary>
public interface IDeviceTurnGateway
{
    /// <summary>Выдать ходу маршрут и токен. Не бросает: отказ — <see cref="DeviceTurnGatewayStart.FailureText"/>.</summary>
    DeviceTurnGatewayStart StartTurn(string ownerId, string sessionId, string deviceId, string? model);

    /// <summary>Отозвать токен хода: процесс на устройстве кончился или не запустился.</summary>
    void EndTurn(string gatewayTurnId);
}

/// <summary>Выдача шлюза ходу либо текст отказа для карточки ошибки хода.</summary>
public sealed record DeviceTurnGatewayStart(DeviceExecGateway? Gateway, string? FailureText);
