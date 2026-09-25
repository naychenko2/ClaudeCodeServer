using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Credentials;
using ClaudeHomeServer.DeviceAgent.Pairing;

namespace ClaudeHomeServer.DeviceAgent.Tests.Pairing;

/// <summary>Сопряжение — существующий поток ADR-008: POST /api/devices/pair с кодом и отпечатком.</summary>
public class PairingClientTests
{
    private const string Token = "device-token-SECRET-pair";

    private sealed class MemoryStore : IDeviceTokenStore
    {
        public Dictionary<string, string> Saved { get; } = new();
        public string Describe => "память";
        public string? Read(string deviceId) => Saved.GetValueOrDefault(deviceId);
        public void Save(string deviceId, string token) => Saved[deviceId] = token;
        public void Delete(string deviceId) => Saved.Remove(deviceId);
    }

    private sealed class Server(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Код_меняется_на_токен_который_сразу_уходит_в_хранилище_ОС()
    {
        var server = new Server(HttpStatusCode.OK,
            $$"""{"deviceId":"dev-1","name":"Ноутбук","deviceToken":"{{Token}}","tokenVersion":1}""");
        var store = new MemoryStore();

        var registration = await new PairingClient(new HttpClient(server))
            .PairAsync(new Uri("https://home.example/"), " abcd2345 ", "Ноутбук", "1.0.0", store);

        server.Request!.RequestUri!.ToString().Should().Be("https://home.example/api/devices/pair");
        using var sent = JsonDocument.Parse(server.RequestBody!);
        sent.RootElement.GetProperty("code").GetString().Should().Be("abcd2345");
        sent.RootElement.GetProperty("fingerprint").GetString().Should().Be(MachineIdentity.Fingerprint()).And.HaveLength(64);
        sent.RootElement.GetProperty("clientVersion").GetString().Should().Be("1.0.0");

        store.Saved.Should().Equal(new Dictionary<string, string> { ["dev-1"] = Token });
        registration.DeviceId.Should().Be("dev-1");
        registration.ToString().Should().NotContain(Token, "регистрация на диске без секрета");
    }

    [Fact]
    public async Task По_открытому_каналу_сопряжения_нет()
    {
        var act = () => new PairingClient(new HttpClient(new Server(HttpStatusCode.OK, "{}")))
            .PairAsync(new Uri("http://192.168.1.10:5000/"), "ABCD2345", "n", "1", new MemoryStore());
        await act.Should().ThrowAsync<PairingException>().WithMessage("*HTTPS*");
    }

    [Fact]
    public async Task Отказ_сервера_показывается_его_текстом()
    {
        var act = () => new PairingClient(new HttpClient(new Server(HttpStatusCode.BadRequest,
                """{"error":"Код не подходит или истёк. Выпусти новый код в веб-интерфейсе"}""")))
            .PairAsync(new Uri("https://home.example/"), "ABCD2345", "n", "1", new MemoryStore());
        await act.Should().ThrowAsync<PairingException>().WithMessage("Код не подходит*");
    }

    [Fact]
    public void Отпечаток_по_формуле_сервера()
    {
        // MachineFingerprint.Of на сервере: SHA-256 от имени машины в нижнем регистре
        MachineIdentity.Fingerprint(" MyBox ").Should().Be(MachineIdentity.Fingerprint("mybox"));
        MachineIdentity.Fingerprint("mybox").Should()
            .Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("mybox"u8.ToArray())));
    }
}
