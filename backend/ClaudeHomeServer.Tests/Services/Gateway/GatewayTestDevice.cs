using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Llm.Gateway;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.Gateway;

// Учётка устройства для тестов шлюза: настоящий DeviceRegistry во временном каталоге и
// настоящая схема DesktopDeviceAuthHandler — вход шлюза принимает токен хода только вместе
// с ней (ADR-016 §2).
public sealed class GatewayTestDevice : IDisposable
{
    public const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gw-device-" + Guid.NewGuid().ToString("N")[..10]);

    public GatewayTestDevice(string ownerId = "owner-1", DeviceRegistry? registry = null)
    {
        Registry = registry ?? new DeviceRegistry(Directory.CreateDirectory(_dir).FullName);
        (Id, Token) = Register(ownerId, "gw-test", Fingerprint);
    }

    public DeviceRegistry Registry { get; }
    public string Id { get; }
    public string Token { get; }

    // Ещё одно устройство в том же реестре (чужое для токенов, выданных на Id)
    public (string Id, string Token) Register(string ownerId, string name, string fingerprint)
    {
        var (device, token) = Registry.Register(ownerId, name, fingerprint);
        return (device.Id, token);
    }

    public void AddTo(IServiceCollection services)
    {
        services.AddSingleton(Registry);
        services.AddAuthentication().AddDesktopDeviceAuth();
    }

    public HttpRequestMessage Sign(HttpRequestMessage request) => Sign(request, Token, Fingerprint);

    public static HttpRequestMessage Sign(HttpRequestMessage request, string deviceToken, string fingerprint)
    {
        request.Headers.TryAddWithoutValidation("Authorization", DesktopDeviceAuthHandler.TokenPrefix + deviceToken);
        request.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.DeviceFingerprintHeader, fingerprint);
        return request;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
