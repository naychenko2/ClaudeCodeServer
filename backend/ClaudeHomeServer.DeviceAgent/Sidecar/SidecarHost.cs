using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.DeviceAgent.Sidecar;

/// <summary>
/// Kestrel сайдкара — только на 127.0.0.1 (порт по умолчанию выбирает ОС). Отдельный
/// маленький веб-хост: у него свой адрес, и CLI он виден, а наружу — нет.
/// </summary>
internal sealed class SidecarHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SidecarHost(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>Базовый адрес, <c>http://127.0.0.1:{порт}</c>, без завершающего слэша.</summary>
    public string Url { get; }

    public int Port => new Uri(Url).Port;

    public static async Task<SidecarHost> StartAsync(
        TurnGrants grants, IDeviceIdentity device, ILoggerFactory loggers, int port = 0,
        HttpMessageHandler? gatewayHandler = null, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Свой логгер у сайдкара — агента; журнал запросов Kestrel не нужен (в нём адреса ходов)
        builder.Logging.ClearProviders();

        var tunnelLog = loggers.CreateLogger("ClaudeHomeServer.DeviceAgent.Sidecar.ConnectTunnel");
        var boundPort = 0;
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(IPAddress.Loopback, port, listen => listen.Use(ConnectTunnel.Middleware(tunnelLog, () => boundPort)));
        });

        // К шлюзу — без системного прокси: адрес сервера задан явно, а прокси машины
        // мог бы увидеть токен устройства в заголовках
        var http = new HttpClient(gatewayHandler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        builder.Services.AddSingleton(new SidecarProxy(grants, device, http, loggers.CreateLogger<SidecarProxy>()));

        var app = builder.Build();
        app.Run(context => context.RequestServices.GetRequiredService<SidecarProxy>().HandleAsync(context));
        await app.StartAsync(ct);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        boundPort = new Uri(address).Port;
        return new SidecarHost(app, $"http://127.0.0.1:{boundPort}");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
