using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Credentials;

namespace ClaudeHomeServer.DeviceAgent.Pairing;

/// <summary>
/// Сопряжение — существующий поток ADR-008 (<c>DevicePairingService</c> на сервере):
/// человек выпускает в вебе одноразовый код, агент меняет его на токен устройства через
/// <c>POST /api/devices/pair</c>. Никаких других учётных данных у агента нет и не будет.
/// </summary>
internal sealed record DeviceRegistration(string ServerUrl, string DeviceId, string DeviceName, string Fingerprint)
{
    public static DeviceRegistration? Load(string file)
    {
        if (!File.Exists(file)) return null;
        return JsonSerializer.Deserialize<DeviceRegistration>(File.ReadAllText(file), Json);
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

internal static class MachineIdentity
{
    /// <summary>
    /// Отпечаток машины — та же формула, что у сервера (<c>MachineFingerprint.Of</c> в
    /// вертикали Desktop): SHA-256 от имени машины в нижнем регистре. Сервер сверяет его на
    /// каждом запросе устройства.
    /// </summary>
    public static string Fingerprint(string? machineName = null) =>
        Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes((machineName ?? Environment.MachineName).Trim().ToLowerInvariant())));
}

internal sealed class PairingException(string message) : Exception(message);

internal sealed class PairingClient(HttpClient http)
{
    private sealed record PairRequest(string Code, string Name, string Fingerprint, string? ClientVersion);

    private sealed record PairResponse(string DeviceId, string Name, string DeviceToken, int TokenVersion);

    private sealed record ErrorResponse(string? Error);

    /// <summary>
    /// Обмен кода на токен. Токен сразу уходит в хранилище ОС — наружу из метода он не
    /// возвращается и нигде не печатается.
    /// </summary>
    public async Task<DeviceRegistration> PairAsync(
        Uri server, string code, string deviceName, string clientVersion, IDeviceTokenStore store, CancellationToken ct = default)
    {
        // По открытому каналу код и токен не выдаются (ADR-008); loopback — дев-стенд
        if (server.Scheme != Uri.UriSchemeHttps && !server.IsLoopback)
            throw new PairingException("сопряжение только по HTTPS: по открытому каналу код и токен не выдаются");

        var fingerprint = MachineIdentity.Fingerprint();
        using var response = await http.PostAsJsonAsync(new Uri(server, "api/devices/pair"),
            new PairRequest(code.Trim(), deviceName, fingerprint, clientVersion), ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, ct);
            throw new PairingException(response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => error ?? "слишком много попыток — подожди и выпусти новый код",
                _ => error ?? $"сервер отказал в сопряжении ({(int)response.StatusCode})",
            });
        }

        var body = await response.Content.ReadFromJsonAsync<PairResponse>(ct)
            ?? throw new PairingException("сервер вернул пустой ответ на сопряжение");
        if (string.IsNullOrEmpty(body.DeviceToken) || string.IsNullOrEmpty(body.DeviceId))
            throw new PairingException("в ответе сервера нет токена устройства");

        store.Save(body.DeviceId, body.DeviceToken);
        return new DeviceRegistration(server.ToString(), body.DeviceId, body.Name, fingerprint);
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<ErrorResponse>(ct))?.Error; }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }
}
