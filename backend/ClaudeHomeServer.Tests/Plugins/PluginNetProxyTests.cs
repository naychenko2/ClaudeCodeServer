using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Plugins;

// Прокси net:{host} (ADR-031 §3.4, шаг 3.3): обработчик net.fetch. Тесты (критерий):
//   - чужой хост → denied (точный хост из granted, не подстрока);
//   - редирект на другой хост → upstream;
//   - адрес из приватной сети → upstream (SsrfGuard);
//   - секрет не в теле и заголовках ответа (эхо вычищается);
//   - превышение размера ответа → limit.
// Мутации (критерий): сравнение хоста по подстроке → тест чужого хоста красный;
// отключаем SsrfGuard → тест приватного адреса красный; возвращаем значение секрета в ответ
// → тест эха красный.
public class PluginNetProxyTests : IDisposable
{
    private const string OwnerA = "owner-a";
    private const string PluginId = "local-a/pkg";

    private readonly string _dir;
    private readonly PluginSecretsStore _secrets;

    public PluginNetProxyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccs_netproxy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _secrets = new PluginSecretsStore(BuildConfig(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration BuildConfig(string dataDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            })
            .Build();

    // Fake HttpMessageHandler: отвечает по делегату (каждый тест задаёт своё поведение).
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }

    // Прокси с fake-клиентом. Таймаут короткий, лимиты по умолчанию.
    // dnsResolver — фейковый резолв для детерминированного DNS rebinding (critical 1);
    // null — системный Dns (как в бою).
    private static (PluginNetProxy Proxy, FakeHttpHandler Handler) NewProxy(
        PluginSecretsStore secrets, Func<HttpRequestMessage, HttpResponseMessage> respond,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        var handler = new FakeHttpHandler(respond);
        var proxy = new PluginNetProxy(secrets, handler, dnsResolver);
        proxy.Timeout = TimeSpan.FromSeconds(5);
        return (proxy, handler);
    }

    private static PluginCallContext Ctx() => new(OwnerA, PluginId, null, null);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    // Ответ-хелпер: статус + тело + (опционально) заголовки.
    private static HttpResponseMessage Resp(int status, string body,
        (string Name, string Value)[]? headers = null)
    {
        var msg = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
            foreach (var (name, value) in headers)
                msg.Headers.TryAddWithoutValidation(name, value);
        return msg;
    }

    // --- 1. Чужой хост → denied (точный хост, не подстрока) ---
    // Мутация: сравнение хоста по подстроке (== → Contains) → этот тест красный:
    // api.linear.app.evil.com «содержит» granted api.linear.app, но это другой хост.

    [Fact]
    public async Task ЧужойХост_ОтдаётDenied()
    {
        var (proxy, _) = NewProxy(_secrets, _ => Resp(200, "ok"));
        // granted: net:api.linear.app; url — на api.linear.app.evil.com (суперстрока,
        // но ДРУГОЙ хост: точное сравнение отклоняет, подстрока — пропустила бы).
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app.evil.com/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("denied");
        result.Error.Message.Should().Contain("api.linear.app.evil.com");
    }

    // Контроль: свой хост (совпадает с granted) → не denied (идёт дальше, 200).
    [Fact]
    public async Task СвойХост_НеDenied_Отдаёт200()
    {
        var (proxy, _) = NewProxy(_secrets, _ => Resp(200, "ok"));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeTrue("свой хост совпадает с granted");
    }

    // --- 2. Редирект на другой хост → upstream ---

    [Fact]
    public async Task РедиректНаДругойХост_ОтдаётUpstream()
    {
        // 302 → Location: https://api.github.com/ (другой хост) → upstream.
        var (proxy, _) = NewProxy(_secrets, _ =>
            Resp(302, "", [("Location", "https://api.github.com/redirected")]));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("upstream");
        result.Error.Message.Should().Contain("api.github.com");
    }

    // Контроль: редирект на тот же хост → 200 (не upstream).
    [Fact]
    public async Task РедиректНаТотЖеХост_Отдаёт200()
    {
        var (proxy, _) = NewProxy(_secrets, req =>
        {
            // Первый запрос → 302 на тот же хост; повторный → 200.
            return req.RequestUri!.AbsolutePath == "/graphql"
                ? Resp(302, "", [("Location", "https://api.linear.app/done")])
                : Resp(200, "done");
        });
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeTrue("редирект на тот же хост допускается");
    }

    // --- 3. Адрес из приватной сети → upstream (SsrfGuard) ---
    // Мутация: отключаем SsrfGuard (CheckAsync → Public) → этот тест красный
    // (127.0.0.1 — loopback, без guard прошёл бы как публичный).

    [Fact]
    public async Task ПриватныйАдрес_ОтдаётUpstream()
    {
        // 127.0.0.1 — loopback (приватный). granted net:127.0.0.1 (хост совпадает),
        // SsrfGuard → Private → upstream.
        var (proxy, _) = NewProxy(_secrets, _ => Resp(200, "ok"));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"http://127.0.0.1/secret"}"""),
            ["net:127.0.0.1"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("upstream");
        result.Error.Message.Should().Contain("127.0.0.1");
    }

    // --- 3a. Редирект на приватный адрес → upstream (rework 12, critical 1) ---
    // Исходный хост публичный и выдан, но Location — 127.0.0.1 (loopback): редирект на
    // приватный адрес — тот же SSRF. Мутация: убрать повторную SSRF-проверку на редиректе
    // (и «разный хост») → тест красный.
    [Fact]
    public async Task РедиректНаПриватныйАдрес_ОтдаётUpstream()
    {
        var (proxy, _) = NewProxy(_secrets, req =>
            req.RequestUri!.AbsolutePath == "/graphql"
                ? Resp(302, "", [("Location", "http://127.0.0.1/secret")])
                : Resp(200, "ok"));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("upstream");
        result.Error.Message.Should().Contain("127.0.0.1");
    }

    // --- 3b. DNS rebinding: хост, который при втором резолве даёт приватный адрес → upstream ---
    // (rework 12, critical 1). Фейковый резолвер: первый резолв — публичный (предварительная
    // проверка проходит), второй (на редиректе) — приватный. Мутация: убрать повторную
    // SSRF-проверку на итерации редиректа → тест красный.
    [Fact]
    public async Task DnsRebinding_ВторойРезолвПриватный_ОтдаётUpstream()
    {
        var calls = 0;
        Func<string, CancellationToken, Task<IPAddress[]>> fakeDns = (host, ct) =>
        {
            var result = Interlocked.Increment(ref calls) == 1
                ? new[] { IPAddress.Parse("8.8.8.8") }
                : new[] { IPAddress.Parse("127.0.0.1") };
            return Task.FromResult(result);
        };

        // Редирект на тот же хост (rebind.example) — «разный хост» не сработает,
        // ловит только повторная SSRF-проверка (второй резолв → приватный).
        var (proxy, _) = NewProxy(_secrets, req =>
            req.RequestUri!.AbsolutePath == "/graphql"
                ? Resp(302, "", [("Location", "https://rebind.example/done")])
                : Resp(200, "ok"),
            dnsResolver: fakeDns);

        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://rebind.example/graphql"}"""),
            ["net:rebind.example"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("upstream");
        result.Error.Message.Should().Contain("rebind.example");
    }

    // --- 3c. ConnectCallback: вторая линия защиты (rework 12, critical 1) ---
    // Предварительная SSRF-проверка (ResolveAndCheckAsync) резолвит хост через _dnsResolver
    // и видит публичный адрес. Но боевой транспорт (SocketsHttpHandler) резолвит хост
    // ЗАНОВО прямо перед TCP-connect в ConnectCallback: если второй резолв даёт приватный
    // адрес (DNS rebinding), соединение отбивается. Мутация: ослабить проверку в
    // ConnectCallback (ResolvePublicAsync → всегда addresses[0], без SsrfGuard.IsPublic)
    // → тест красный (приватный адрес 127.0.0.1 прошёл бы как публичный).
    [Fact]
    public async Task ConnectCallback_ВторойРезолвПриватный_ОтдаётUpstream()
    {
        var calls = 0;
        Func<string, CancellationToken, Task<IPAddress[]>> fakeDns = (host, ct) =>
        {
            var result = Interlocked.Increment(ref calls) == 1
                ? new[] { IPAddress.Parse("8.8.8.8") }        // первый резолв: публичный
                : new[] { IPAddress.Parse("127.0.0.1") };      // второй (ConnectCallback): приватный
            return Task.FromResult(result);
        };

        // Боевой транспорт: SocketsHttpHandler с ConnectCallback, использующим fakeDns.
        // (Не fake-HttpMessageHandler: ConnectCallback живёт в SocketsHttpHandler,
        // а не в HttpMessageHandler — только боевой транспорт его вызывает.)
        var handler = PluginNetProxy.CreateHandler(fakeDns);
        var proxy = new PluginNetProxy(_secrets, handler, fakeDns);
        proxy.Timeout = TimeSpan.FromSeconds(5);

        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://rebind.example/graphql"}"""),
            ["net:rebind.example"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("upstream");
        result.Error.Message.Should().Contain("rebind.example");
    }

    // --- 4. Секрет не в теле и заголовках ответа (эхо вычищается) ---
    // Мутация: возвращаем значение секрета в ответ (не вычищаем) → этот тест красный.

    [Fact]
    public async Task СекретНеВТелеИЗаголовкахОтвет()
    {
        // Плагин имеет секрет LINEAR_TOKEN; args подставляют {{secret:LINEAR_TOKEN}} в
        // Authorization. Fake-хост эхо-ит значение в тело ответа — прокси обязан вычистить.
        const string secretValue = "super-secret-value-xyz";
        _secrets.Set(OwnerA, PluginId, "LINEAR_TOKEN", secretValue);

        var (proxy, _) = NewProxy(_secrets, req =>
        {
            // Эхо: берём Authorization из запроса и кладём в тело ответа.
            var auth = req.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v!) : "";
            return Resp(200, $"{{\"auth\":\"{auth}\"}}",
                [("X-Request-Id", auth)]);
        });

        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql","headers":{"Authorization":"{{secret:LINEAR_TOKEN}}"}}"""),
            ["net:api.linear.app", "secret:LINEAR_TOKEN"], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Data!);
        json.Should().NotContain(secretValue, "значение секрета не в теле ответа (эхо вычищено)");
        json.Should().Contain(PluginNetProxy.Redacted, "эхо заменено маркером REDACTED");
    }

    // Контроль: без права secret:NAME плейсхолдер не подставляется (значение не уходит).
    [Fact]
    public async Task БезПраваСекрета_ПлейсхолдерНеПодставлен()
    {
        const string secretValue = "hidden-value-abc";
        _secrets.Set(OwnerA, PluginId, "LINEAR_TOKEN", secretValue);

        var (proxy, _) = NewProxy(_secrets, _ => Resp(200, "ok"));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql","headers":{"Authorization":"{{secret:LINEAR_TOKEN}}"}}"""),
            ["net:api.linear.app"], CancellationToken.None); // secret:LINEAR_TOKEN НЕ в granted

        result.Ok.Should().BeTrue();
        // Плейсхолдер остался как есть (право не выдано) → значение не подставлялось.
        var json = JsonSerializer.Serialize(result.Data!);
        json.Should().NotContain(secretValue);
    }

    // --- 4a. Auth prefix: нормализация пробела (rework 12, minor 10) ---
    // prefix "Bearer " (с пробелом) → "Bearer <значение>" (ровно один пробел, не два).
    [Fact]
    public async Task AuthPrefix_НормализуетПробел()
    {
        const string secretValue = "tok-abc";
        _secrets.Set(OwnerA, PluginId, "LINEAR_TOKEN", secretValue);

        string? capturedAuth = null;
        var (proxy, _) = NewProxy(_secrets, req =>
        {
            if (req.Headers.TryGetValues("Authorization", out var v))
                capturedAuth = string.Join(",", v!);
            return Resp(200, "ok");
        });

        await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql","auth":{"secret":"LINEAR_TOKEN","prefix":"Bearer "}}"""),
            ["net:api.linear.app", "secret:LINEAR_TOKEN"], CancellationToken.None);

        capturedAuth.Should().Be("Bearer " + secretValue, "ровно один пробел между префиксом и значением");
    }

    // --- 4b. RedactSecrets на уровне байт (rework 12, minor 9) ---
    // Тело — не-UTF-8 (бинарный) контент, содержащий байты значения секрета: redaction
    // находит и заменяет значение на уровне байт, не декодируя тело как UTF-8.
    [Fact]
    public async Task RedactSecrets_БайтыНеТекстовогоТела_ЗаменяетЗначение()
    {
        const string secretValue = "secret-bytes-xyz";
        _secrets.Set(OwnerA, PluginId, "LINEAR_TOKEN", secretValue);

        var secretBytes = Encoding.UTF8.GetBytes(secretValue);
        var body = new byte[2 + secretBytes.Length + 2];
        body[0] = 0xFF; body[1] = 0xFE;           // не-UTF-8 префикс
        secretBytes.CopyTo(body, 2);
        body[^2] = 0x00; body[^1] = 0x01;           // не-UTF-8 суффикс

        var (proxy, _) = NewProxy(_secrets, _ =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            return msg;
        });

        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql","headers":{"Authorization":"{{secret:LINEAR_TOKEN}}"}}"""),
            ["net:api.linear.app", "secret:LINEAR_TOKEN"], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Data!);
        json.Should().Contain(PluginNetProxy.Redacted, "значение секрета в бинарном теле заменено маркером");
        json.Should().NotContain(secretValue, "само значение наружу не уходит");
    }

    // --- 5. Превышение размера ответа → limit ---

    [Fact]
    public async Task ПревышениеРазмера_ОтдаётLimit()
    {
        var (proxy, _) = NewProxy(_secrets, _ => Resp(200, new string('x', 2048)));
        proxy.MaxResponseBytes = 1024; // лимит 1 KiB, ответ 2 KiB → limit

        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("limit");
    }

    // --- Контроль: happy path — 200, статус/заголовки/тело в ответе ---

    [Fact]
    public async Task HappyPath_ОтдаётСтатусЗаголовкиТело()
    {
        var (proxy, _) = NewProxy(_secrets, _ =>
            Resp(200, """{"data":"ok"}""", [("Content-Type", "application/json")]));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeTrue();
        var json = JsonSerializer.SerializeToElement(result.Data!);
        json.GetProperty("status").GetInt32().Should().Be(200);
        json.GetProperty("body").GetString().Should().Be("""{"data":"ok"}""");
        json.GetProperty("headers").GetProperty("Content-Type").GetString()!
            .Should().StartWith("application/json");
    }

    // --- Контроль: неизвестный метод → invalid ---

    [Fact]
    public async Task НеподдерживаемыйМетод_ОтдаётInvalid()
    {
        var (proxy, _) = NewProxy(_secrets, _ => Resp(200, "ok"));
        var result = await proxy.HandleAsync(Ctx(), "net.fetch",
            Args("""{"url":"https://api.linear.app/graphql","method":"DELETE"}"""),
            ["net:api.linear.app"], CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.Code.Should().Be("invalid");
    }
}
