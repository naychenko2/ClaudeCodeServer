using System.Net;
using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services.Yandex;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Остаток на биллинг-аккаунте Yandex Cloud: обмен ключа сервисного аккаунта на IAM-токен
// и чтение billingAccounts. Форма ответа — по документации Billing API v1: баланс приходит
// СТРОКОЙ, валюта отдельным полем.
public class YandexAccountServiceTests
{
    private const string BillingBody = """
        {"billingAccounts":[{"id":"b1","name":"Основной","createdAt":"2025-01-01T00:00:00Z","countryCode":"ru","currency":"RUB","active":true,"balance":"1234.56"}]}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public readonly List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Настоящий RSA-ключ: подпись JWT в провайдере не мокается, поэтому тест заодно
    // проверяет, что ключ из конфига реально разбирается и токен собирается
    private static string TestPrivateKeyPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static YandexAccountService Create(StubHandler handler,
        string? privateKey, string? billingAccountId = null)
    {
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["Yandex:Billing:ServiceAccountId"] = privateKey is null ? null : "aje-test",
            ["Yandex:Billing:KeyId"] = privateKey is null ? null : "key-test",
            ["Yandex:Billing:PrivateKey"] = privateKey,
            ["Yandex:Billing:BillingAccountId"] = billingAccountId,
        });
        var factory = new StubHttpFactory(handler);
        var iam = new YandexIamTokenProvider(factory, config, NullLogger<YandexIamTokenProvider>.Instance);
        return new YandexAccountService(factory, config, iam, NullLogger<YandexAccountService>.Instance);
    }

    private static HttpResponseMessage Route(HttpRequestMessage req) =>
        req.RequestUri!.ToString().Contains("iam.api")
            ? Json("""{"iamToken":"t1","expiresAt":"2099-01-01T00:00:00Z"}""")
            : Json(BillingBody);

    // --- разбор ответа ---

    [Fact]
    public void Parse_ЖивойОтвет_ЧитаетБалансСтрокойИВалюту()
    {
        var acc = YandexAccountService.Parse(BillingBody, null);
        acc.Should().NotBeNull();
        acc!.Id.Should().Be("b1");
        acc.Balance.Should().Be("1234.56", "баланс приходит строкой — так и отдаём, без арифметики");
        acc.Currency.Should().Be("RUB");
        acc.Active.Should().BeTrue();
    }

    [Fact]
    public void Parse_НесколькоАккаунтов_БерётУказанный()
    {
        const string body = """
            {"billingAccounts":[{"id":"b1","balance":"1","active":true},{"id":"b2","balance":"2","active":false}]}
            """;
        YandexAccountService.Parse(body, "b2")!.Id.Should().Be("b2");
        YandexAccountService.Parse(body, null)!.Id.Should().Be("b1", "без явного id берётся первый");
        YandexAccountService.Parse(body, "нет-такого").Should().BeNull();
    }

    [Fact]
    public void Parse_ПустойСписокИлиМусор_Null()
    {
        // Пустой список — типичный ответ при роли, выданной на облако вместо биллинг-аккаунта
        YandexAccountService.Parse("""{"billingAccounts":[]}""", null).Should().BeNull();
        YandexAccountService.Parse("{}", null).Should().BeNull();
        YandexAccountService.Parse("не json", null).Should().BeNull();
    }

    // --- поведение сервиса ---

    [Fact]
    public async Task GetAsync_БезКлюча_ВыключенИВСетьНеХодит()
    {
        var handler = new StubHandler(Route);
        var res = await Create(handler, privateKey: null).GetAsync();

        res.Enabled.Should().BeFalse();
        res.Account.Should().BeNull();
        handler.Calls.Should().Be(0, "ненастроенная фича не должна дёргать сеть");
    }

    [Fact]
    public async Task GetAsync_СКлючом_МеняетJwtНаIamИЧитаетБаланс()
    {
        var handler = new StubHandler(Route);
        var res = await Create(handler, TestPrivateKeyPem()).GetAsync();

        res.Enabled.Should().BeTrue();
        res.Error.Should().BeNull();
        res.Account!.Balance.Should().Be("1234.56");
        handler.Urls.Should().HaveCount(2);
        handler.Urls[0].Should().Contain("iam.api", "сперва обмен ключа на IAM-токен");
        handler.Urls[1].Should().Contain("billing.api");
    }

    [Fact]
    public async Task GetAsync_ПовторныйВызов_БерётсяИзКэша()
    {
        var handler = new StubHandler(Route);
        var svc = Create(handler, TestPrivateKeyPem());

        await svc.GetAsync();
        var second = await svc.GetAsync();

        second.Account!.Balance.Should().Be("1234.56");
        handler.Calls.Should().Be(2, "второй показ баланса живёт на кэше, сеть не трогает");
    }

    [Fact]
    public async Task GetAsync_БиллингОтвергает_ОтдаётПричинуБезИсключения()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString().Contains("iam.api")
            ? Json("""{"iamToken":"t1","expiresAt":"2099-01-01T00:00:00Z"}""")
            : Json("""{"code":7,"message":"permission denied"}""", HttpStatusCode.Forbidden));

        var res = await Create(handler, TestPrivateKeyPem()).GetAsync();

        res.Enabled.Should().BeTrue();
        res.Account.Should().BeNull();
        res.Error.Should().Contain("403");
    }

    [Fact]
    public async Task GetAsync_ПустойСписокАккаунтов_ПодсказываетПроРоль()
    {
        var handler = new StubHandler(req => req.RequestUri!.ToString().Contains("iam.api")
            ? Json("""{"iamToken":"t1","expiresAt":"2099-01-01T00:00:00Z"}""")
            : Json("""{"billingAccounts":[]}"""));

        var res = await Create(handler, TestPrivateKeyPem()).GetAsync();

        // Самая частая причина: роль выдана на облако, а нужна на биллинг-аккаунте —
        // Яндекс в этом случае не отказывает, а молча отдаёт пустой список
        res.Error.Should().Contain("billing.accounts.viewer");
    }

    [Fact]
    public async Task IamToken_БитыйКлюч_НетТокенаИНетПоходаВСеть()
    {
        var handler = new StubHandler(Route);
        var res = await Create(handler, "не-pem-вовсе").GetAsync();

        res.Enabled.Should().BeTrue("ключ задан — фича включена, просто не работает");
        res.Error.Should().Contain("IAM-токен");
        handler.Calls.Should().Be(0, "подпись падает до сетевого запроса");
    }

    // --- JWT для обмена: Яндекс требует iss, aud, iat, exp и kid в заголовке ---

    [Fact]
    public async Task Jwt_НесётОбязательныеПоляИПодписанPS256()
    {
        string? sentJwt = null;
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("iam.api"))
            {
                var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                sentJwt = doc.RootElement.GetProperty("jwt").GetString();
                return Json("""{"iamToken":"t1","expiresAt":"2099-01-01T00:00:00Z"}""");
            }
            return Json(BillingBody);
        });

        await Create(handler, TestPrivateKeyPem()).GetAsync();

        sentJwt.Should().NotBeNull();
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(sentJwt);
        token.Header.Alg.Should().Be("PS256");
        token.Header.Kid.Should().Be("key-test");
        token.Issuer.Should().Be("aje-test");
        token.Audiences.Should().Contain("https://iam.api.cloud.yandex.net/iam/v1/tokens");
        // iat обязателен по документации обмена: без него Яндекс отвергает JWT
        token.Payload.Should().ContainKey("iat");
        token.Payload.Should().ContainKey("exp");
    }

    [Fact]
    public void UnescapeNewlines_ЛитеральныеПереносы_СтановятсяНастоящими()
    {
        // Ключ, скопированный из JSON одной строкой: переносы записаны двумя символами.
        // Настоящий PEM после этого обязан разбираться, иначе баланс молча не работает
        var pem = TestPrivateKeyPem();
        var flattened = pem.Replace("\n", "\\n");

        var restored = YandexIamTokenProvider.UnescapeNewlines(flattened);

        restored.Should().Be(pem);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(restored);
        // Уже настоящие переносы правка не портит
        YandexIamTokenProvider.UnescapeNewlines(pem).Should().Be(pem);
    }
}
