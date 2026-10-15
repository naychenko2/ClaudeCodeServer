using System.Net;
using System.Net.Sockets;

namespace ClaudeHomeServer.Services;

/// <summary>
/// Соединение не открыто SSRF-проверкой в момент connect: имя не резолвится
/// (<see cref="DnsFailed"/>) либо среди его адресов есть не публичный. Приходит
/// <see cref="Exception.InnerException"/> у <see cref="HttpRequestException"/> — по нему потребитель
/// отличает отказ проверки от сетевой ошибки.
/// </summary>
public sealed class SsrfBlockedException(string host, bool dnsFailed)
    : Exception(dnsFailed ? $"Имя {host} не резолвится — соединение не открыто" : $"Адрес {host} не публичный — соединение запрещено")
{
    public string Host => host;

    public bool DnsFailed => dnsFailed;
}

/// <summary>Настройки транспорта, которые потребителям действительно нужны разные; правила SSRF не настраиваются.</summary>
public sealed record SsrfSafeHandlerOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public DecompressionMethods AutomaticDecompression { get; init; } = DecompressionMethods.None;

    /// <summary>Срок жизни соединения в пуле; null — умолчание <see cref="SocketsHttpHandler"/>.</summary>
    public TimeSpan? PooledConnectionLifetime { get; init; }

    /// <summary>
    /// Резолвер имени; null — системный DNS. Подменяют тесты (rebinding, имя с публичным и
    /// приватным адресом): проверка «все адреса публичные» стоит после него и не подменяется.
    /// </summary>
    public Func<string, CancellationToken, Task<IPAddress[]>>? Resolve { get; init; }
}

/// <summary>
/// Единственный транспорт к адресам, пришедшим извне (ADR-025, «Фаза 3», S2; ADR-005 §2): ридер ссылок,
/// <see cref="SafeMediaDownloader"/> и блоки <c>http.*</c> процессов. Своих <c>ConnectCallback</c> у
/// потребителей нет — копии расходились (ридер брал первый публичный адрес, загрузчик требовал все), и
/// сторож-тест не даёт завести третью.
/// <list type="bullet">
/// <item><c>UseProxy = false</c>: исходящий прокси пересылает на приватные и loopback-адреса без
/// фильтра, а через него <c>ConnectCallback</c> видел бы адрес прокси, а не цели;</item>
/// <item><c>ConnectCallback</c> резолвит имя прямо перед соединением и требует, чтобы публичными были
/// ВСЕ адреса имени (fail-closed): проверка до запроса не в счёт — между ней и connect имя могут
/// перерезолвить (DNS rebinding). Соединение идёт только с проверенным адресом;</item>
/// <item>без авто-редиректов и кук: следующий адрес потребитель проверяет сам или не ходит туда вовсе;</item>
/// <item>проверку TLS-сертификата не трогаем — отключить её настройками нельзя.</item>
/// </list>
/// </summary>
public static class SsrfSafeHandler
{
    public static SocketsHttpHandler Create(SsrfSafeHandlerOptions? options = null)
    {
        options ??= new SsrfSafeHandlerOptions();
        var resolve = options.Resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            PreAuthenticate = false,
            AutomaticDecompression = options.AutomaticDecompression,
            ConnectTimeout = options.ConnectTimeout,
            ConnectCallback = (context, ct) => ConnectAsync(context, resolve, ct),
        };
        if (options.PooledConnectionLifetime is { } lifetime) handler.PooledConnectionLifetime = lifetime;
        return handler;
    }

    /// <summary>
    /// Адреса имени, если публичны ВСЕ; пустой резолв или ошибка DNS — <see cref="SsrfBlockedException"/>
    /// с <see cref="SsrfBlockedException.DnsFailed"/>, хоть один не публичный — без него.
    /// </summary>
    public static async Task<IPAddress[]> ResolvePublicAsync(string host,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolve(host, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw new SsrfBlockedException(host, dnsFailed: true);
            }
        }
        if (addresses.Length == 0) throw new SsrfBlockedException(host, dnsFailed: true);
        if (!addresses.All(SsrfGuard.IsPublic)) throw new SsrfBlockedException(host, dnsFailed: false);
        return addresses;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        var addresses = await ResolvePublicAsync(context.DnsEndPoint.Host, resolve, ct);
        // Проверены все адреса — пробуем их по порядку: недоступный IPv6 не роняет запрос
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last!;
    }
}
