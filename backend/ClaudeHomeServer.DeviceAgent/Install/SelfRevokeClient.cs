using System.Net;
using ClaudeHomeServer.DeviceAgent.Pairing;
using ClaudeHomeServer.DeviceAgent.Sidecar;

namespace ClaudeHomeServer.DeviceAgent.Install;

internal enum SelfRevokeStatus { Revoked, AlreadyGone, Failed }

internal interface ISelfRevoke
{
    Task<(SelfRevokeStatus Status, string? Error)> RevokeAsync(Uri server, string deviceToken, string fingerprint, CancellationToken ct);
}

/// <summary>
/// Самоотзыв (Р12): <c>DELETE /api/devices/self</c> под авторизацией устройства — токен плюс
/// отпечаток, как у хаба. Какое устройство снимать, сервер берёт из токена.
/// </summary>
internal sealed class SelfRevokeClient(HttpClient http) : ISelfRevoke
{
    public async Task<(SelfRevokeStatus Status, string? Error)> RevokeAsync(
        Uri server, string deviceToken, string fingerprint, CancellationToken ct)
    {
        // Токен по открытому каналу не уходит, как и при сопряжении; loopback — дев-стенд
        if (!ServerChannel.IsSecure(server))
            return (SelfRevokeStatus.Failed, ServerChannel.InsecureError);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(server, "api/devices/self"));
        request.Headers.TryAddWithoutValidation("Authorization", SidecarProxy.DeviceAuthPrefix + deviceToken);
        request.Headers.TryAddWithoutValidation(SidecarProxy.FingerprintHeader, fingerprint);
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent or HttpStatusCode.OK => (SelfRevokeStatus.Revoked, null),
                // Токен уже не принимается или устройства нет — снимать на сервере нечего
                HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => (SelfRevokeStatus.AlreadyGone, null),
                _ => (SelfRevokeStatus.Failed, $"сервер ответил {(int)response.StatusCode}"),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (SelfRevokeStatus.Failed, e.Message);
        }
    }
}
