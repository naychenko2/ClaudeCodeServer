using System.Net;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Строит транспорт клиента "link-reader" (ADR-005, раздел 2) — общий
/// SSRF-примитив Core <see cref="SsrfSafeHandler"/> с настройками ридера: без кук, креденшалов,
/// авто-редиректов и системного egress-прокси, с <c>ConnectCallback</c>, который сам резолвит хост
/// прямо перед подключением и режет не публичные адреса — вторая, TOCTOU-safe линия обороны поверх
/// предварительной проверки в <see cref="ReaderService"/>. Своего <c>ConnectCallback</c> у ридера
/// нет (ADR-025, «Фаза 3», S2): прежняя копия брала первый публичный адрес имени, а примитив требует,
/// чтобы публичными были ВСЕ. Поэтому сайт со смешанными DNS-записями (публичный и приватный адрес)
/// ридер не откроет — отказ тот же, что на приватный адрес (<c>local-address</c>).
///
/// <c>UseProxy = false</c>, а не общий <c>WithoutEgressProxy()</c> из QuietHttpLogger.cs:
/// тот подменяет весь handler на голый <c>HttpClientHandler</c>, и <c>ConnectCallback</c> теряется.
/// Egress-прокси проверен (задача SSRF-обхода, 2026-08-03): и HTTP forward, и HTTPS CONNECT релеят
/// на приватные и loopback-адреса без всякой фильтрации (см. ADR-005, раздел 2).
/// </summary>
public static class ReaderHttpHandlerFactory
{
    /// <param name="resolve">Резолвер имени для тестов; null — системный DNS.</param>
    public static SsrfSafeHttpHandler Create(Func<string, CancellationToken, Task<IPAddress[]>>? resolve) =>
        SsrfSafeHandler.Create(new SsrfSafeHandlerOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            Resolve = resolve,
        });

    public static SsrfSafeHttpHandler Create() => Create(null);
}
