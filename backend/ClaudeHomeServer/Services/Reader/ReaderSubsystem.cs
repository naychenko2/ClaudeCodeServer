using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.Reader;

// Подсистема ридера ссылок (панель «Чтение», ADR-005): панель держит один
// именованный клиент `link-reader` (см. `ReaderService.HttpClientName`),
// построенный не через `AddQuietHttpClient` — нам нужен кастомный `SocketsHttpHandler`
// из `ReaderHttpHandlerFactory.Create` (TOCTOU-safe линия обороны поверх предварительного
// `SsrfGuard`: `ConnectCallback` сам резолвит хост прямо перед подключением и режет
// приватные адреса, а `UseProxy = false` исключает системный egress-прокси —
// иначе HTTP forward и HTTPS CONNECT релеят на приватные и loopback-адреса
// без всякой фильтрации (ADR-005 §2)). `WithoutEgressProxy()` тут НЕ звать
// именно поэтому: он подменяет handler на голый `HttpClientHandler`, и
// `ConnectCallback` теряется.
//
// Известная граница: `SsrfGuard` (Services/ корень) — общая инфраструктура,
// используется также `FilesController`, `McpServersController` и `McpProbeService`.
// В подсистему не переезжает (перенос сменил бы namespace у большого числа
// вызывающих — это отдельное решение).
public sealed class ReaderSubsystem : IAppSubsystem
{
    public string Key => "reader";

    public string Title => "Ридер ссылок";

    public void Register(IServiceCollection services, IConfiguration config)
    {
        // Таймауты — явные, в ReaderService (заголовки/операция).
        services.AddHttpClient(ReaderService.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeCodeServer-Reader/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
                config.GetValue("Reader:AcceptLanguage", "en-US,en;q=0.9")!);
        })
        .ConfigurePrimaryHttpMessageHandler(ReaderHttpHandlerFactory.Create);

        services.AddSingleton<ReaderQuotaService>();
        services.AddSingleton<ReaderService>();
    }
}
