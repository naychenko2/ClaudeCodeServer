using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Reader;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Единый SSRF-примитив Core (ADR-025, «Фаза 3», S2): правило «все адреса имени публичные» в момент
// connect, прямое соединение без прокси, без редиректов, TLS не отключается, только HTTP/1.1–2. При исправном правиле сеть наружу не трогаем:
// цель — loopback-порт теста, публичный адрес в смешанном резолве — 8.8.8.8, куда при поломке правила ушло бы
// соединение и упало бы иначе, чем отказом проверки.
public class SsrfSafeHandlerTests
{
    private static readonly IPAddress Public = IPAddress.Parse("8.8.8.8");

    private static Func<string, CancellationToken, Task<IPAddress[]>> Resolves(params IPAddress[] addresses) =>
        (_, _) => Task.FromResult(addresses);

    // Три потребителя примитива — правило обязано держаться у каждого
    public static IEnumerable<object[]> Consumers() =>
    [
        ["primitive"], ["reader"], ["media"],
    ];

    private static SsrfSafeHttpHandler Handler(string consumer, Func<string, CancellationToken, Task<IPAddress[]>>? resolve) =>
        consumer switch
        {
            "primitive" => SsrfSafeHandler.Create(new SsrfSafeHandlerOptions { Resolve = resolve }),
            "reader" => ReaderHttpHandlerFactory.Create(resolve),
            "media" => SafeMediaDownloader.CreateHandler(resolve),
            _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, null),
        };

    private static async Task<Exception?> SendAsync(HttpMessageHandler handler, string url)
    {
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var _ = await client.GetAsync(url);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task Rebinding_ИмяВМоментСоединенияПриватное_СоединениеНеОткрыто(string consumer)
    {
        // Проверка до запроса «увидела» публичный адрес, а в момент connect имя смотрит на loopback.
        // localhost с системным DNS: без ConnectCallback транспорт дошёл бы до порта теста
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var handler = Handler(consumer, resolve: null);

        var error = await SendAsync(handler, $"http://localhost:{port}/");

        error.Should().BeOfType<HttpRequestException>()
            .Which.InnerException.Should().BeOfType<SsrfBlockedException>()
            .Which.DnsFailed.Should().BeFalse();
        listener.Pending().Should().BeFalse("до цели не должно дойти ни одного соединения");
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task ИмяСПубличнымИПриватнымАдресом_Отказ(string consumer)
    {
        // Смешанный DNS: прежний ридер брал первый публичный адрес и открыл бы такой сайт
        using var handler = Handler(consumer, Resolves(Public, IPAddress.Loopback));

        var error = await SendAsync(handler, "http://mixed.example/");

        error.Should().BeOfType<HttpRequestException>()
            .Which.InnerException.Should().BeOfType<SsrfBlockedException>()
            .Which.DnsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task ПриватныйАдресПервым_ПубличныйВторым_ТожеОтказ()
    {
        using var handler = SsrfSafeHandler.Create(new SsrfSafeHandlerOptions { Resolve = Resolves(IPAddress.Loopback, Public) });

        var error = await SendAsync(handler, "http://mixed.example/");

        error!.InnerException.Should().BeOfType<SsrfBlockedException>();
    }

    [Fact]
    public async Task ПустойРезолв_ОтказКакDns()
    {
        using var handler = SsrfSafeHandler.Create(new SsrfSafeHandlerOptions { Resolve = Resolves() });

        var error = await SendAsync(handler, "http://nothing.example/");

        error!.InnerException.Should().BeOfType<SsrfBlockedException>().Which.DnsFailed.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public void Транспорт_БезПрокси_БезРедиректов_TlsНеОтключён(string consumer)
    {
        using var handler = Handler(consumer, null);

        handler.Transport.UseProxy.Should().BeFalse();
        handler.Transport.AllowAutoRedirect.Should().BeFalse();
        handler.Transport.UseCookies.Should().BeFalse();
        handler.Transport.SslOptions.RemoteCertificateValidationCallback.Should().BeNull("проверку сертификата не отключить");
        handler.Transport.ConnectCallback.Should().NotBeNull();
    }

    public static IEnumerable<object[]> Http3Allowed() =>
        from consumer in new[] { "primitive", "reader", "media" }
        from request in new[] { "3.0-exact", "3.0-or-lower", "1.1-or-higher", "2.0-or-higher" }
        select new object[] { consumer, request };

    [Theory]
    [MemberData(nameof(Http3Allowed))]
    public async Task Http3Разрешён_ОтказДоСоединения(string consumer, string request)
    {
        // QUIC соединяется мимо ConnectCallback: без отказа HTTP/3 (по версии или по Alt-Svc при
        // OrHigher) дошёл бы до адреса без проверки. Цель и резолв — loopback: при поломке запрос ушёл бы
        // в транспорт и упал бы его ошибкой (SSRF-отказ или QUIC), а не отказом версии
        var parts = request.Split('-', 2);
        var policy = parts[1] switch
        {
            "exact" => HttpVersionPolicy.RequestVersionExact,
            "or-lower" => HttpVersionPolicy.RequestVersionOrLower,
            _ => HttpVersionPolicy.RequestVersionOrHigher,
        };
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var handler = Handler(consumer, Resolves(IPAddress.Loopback));
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{port}/")
        {
            Version = Version.Parse(parts[0]),
            VersionPolicy = policy,
        };

        var act = () => client.SendAsync(message);

        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("HTTP/3 запрещён*");
        listener.Pending().Should().BeFalse();
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("2.0")]
    public async Task Http11И2_ПропускаютсяКПроверкеАдреса(string version)
    {
        // Обёртка режет только HTTP/3: обычный запрос доходит до ConnectCallback и его правила
        using var handler = SsrfSafeHandler.Create(new SsrfSafeHandlerOptions { Resolve = Resolves(IPAddress.Loopback) });
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        using var message = new HttpRequestMessage(HttpMethod.Get, "http://private.example/")
        {
            Version = Version.Parse(version),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var act = () => client.SendAsync(message);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.InnerException.Should().BeOfType<SsrfBlockedException>();
    }

    // Корень репозитория от каталога сборки: .git — папка или файл worktree
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !Directory.Exists(Path.Combine(dir.FullName, ".git"))
               && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
    }

    [Fact]
    public void Сторож_СвойConnectCallbackЕстьТолькоУПримитива()
    {
        // Копии расходились (ридер — первый публичный адрес, загрузчик — все): третьей не бывать
        var backend = Path.Combine(RepoRoot(), "backend");
        var assignment = new Regex(@"\bConnectCallback\s*=(?!=)", RegexOptions.CultureInvariant);
        var owners = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin" || p.EndsWith(".Tests", StringComparison.Ordinal)))
            .Where(f => assignment.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(backend, f).Replace('\\', '/'))
            .ToList();

        owners.Should().Equal("ClaudeHomeServer.Core/Services/SsrfSafeHandler.cs");
    }

    [Fact]
    public void Сторож_НастройкиSsrfТранспортаВнеCoreНеМеняются()
    {
        // Прокси, редиректы и проверку сертификата задаёт только примитив: потребитель, включивший их
        // у полученного транспорта, молча снял бы правило адресов (через прокси проверялся бы адрес
        // прокси) или TLS. Запись через .Transport/InnerHandler запрещена везде вне Core, а в файлах,
        // которые берут транспорт у фабрик примитива, — любое присваивание этих свойств.
        // Остаточный риск, который регулярки не ловят (держится только ревью):
        // - `.Transport` передан аргументом в метод другого файла, без вызова фабрики, и настроен там;
        // - запись через приведение: `((SocketsHttpHandler)x.InnerHandler!).UseProxy = …` — между
        //   `InnerHandler` и свойством стоит `)`, а не точка
        var backend = Path.Combine(RepoRoot(), "backend");
        var settings = @"(UseProxy|Proxy|AllowAutoRedirect|ConnectCallback|SslOptions|RemoteCertificateValidationCallback|ServerCertificateCustomValidationCallback|InnerHandler)";
        var throughTransport = new Regex(@"\.(Transport|InnerHandler)\s*\.\s*" + settings + @"\b[^;]*?(?<![=!<>])=(?![=>])",
            RegexOptions.CultureInvariant);
        var anyAssignment = new Regex(@"\b" + settings + @"\s*=(?![=>])", RegexOptions.CultureInvariant);
        var consumer = new Regex(@"\b(SsrfSafeHttpHandler|SsrfSafeHandler\.Create|ReaderHttpHandlerFactory\.Create|SafeMediaDownloader\.CreateHandler)\b",
            RegexOptions.CultureInvariant);
        var core = Path.Combine(backend, "ClaudeHomeServer.Core") + Path.DirectorySeparatorChar;
        // Комментарии не код: «UseProxy = false» в пояснении — не присваивание
        static string Code(string text) => Regex.Replace(text, @"//[^\n]*", "");

        var violations = Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(core, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin" || p.EndsWith(".Tests", StringComparison.Ordinal)))
            .Where(f =>
            {
                var code = Code(File.ReadAllText(f));
                return throughTransport.IsMatch(code) || (consumer.IsMatch(code) && anyAssignment.IsMatch(code));
            })
            .Select(f => Path.GetRelativePath(backend, f).Replace('\\', '/'))
            .ToList();

        violations.Should().BeEmpty();
    }
}
