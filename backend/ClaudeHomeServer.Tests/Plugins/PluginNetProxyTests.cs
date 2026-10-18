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
    private static (PluginNetProxy Proxy, FakeHttpHandler Handler) NewProxy(
        PluginSecretsStore secrets, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHttpHandler(respond);
        var proxy = new PluginNetProxy(secrets, new HttpClient(handler));
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
