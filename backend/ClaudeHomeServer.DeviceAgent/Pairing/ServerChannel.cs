namespace ClaudeHomeServer.DeviceAgent.Pairing;

/// <summary>
/// Агентская сторона правила <c>DeviceChannelGuard.IsSecure</c> (ADR-008): код, токен
/// устройства и команды ходят только по HTTPS, открытый http — лишь на петле (дев-стенд).
/// Одна точка на все каналы: сопряжение, самоотзыв, хаб управления, канал исполнения.
/// </summary>
internal static class ServerChannel
{
    public const string InsecureError =
        "сервер не по HTTPS: по открытому каналу код и токен устройства не отправляются (http допустим только для localhost)";

    public static bool IsSecure(Uri server) =>
        server.Scheme == Uri.UriSchemeHttps || (server.Scheme == Uri.UriSchemeHttp && server.IsLoopback);
}

/// <summary>Сервер указан по открытому каналу — соединение не открывается вовсе.</summary>
internal sealed class InsecureServerException() : Exception(ServerChannel.InsecureError);
