using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

[Trait("Category", "Host")]
public class AuthControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithToken()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestWebApplicationFactory.TestUsername,
            password = TestWebApplicationFactory.TestPassword
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("username").GetString().Should().Be(TestWebApplicationFactory.TestUsername);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestWebApplicationFactory.TestUsername,
            password = "wrong-password"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_UnknownUser_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nobody",
            password = "whatever"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_EmptyCredentials_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "",
            password = ""
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_WithValidToken_Returns200WithUsername()
    {
        var authed = _factory.CreateAuthenticatedClient();
        var response = await authed.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("username").GetString().Should().Be(TestWebApplicationFactory.TestUsername);
        body.GetProperty("userId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_Returns401()
    {
        var response = await _client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithToken_Returns200()
    {
        var authed = _factory.CreateAuthenticatedClient();
        var response = await authed.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_RevokesOtherSessions_AndReturnsFreshTokenToCaller()
    {
        // Два входа одним пользователем = два устройства. Берём seconduser: пароль в тесте
        // меняется, и основной testuser сломал бы соседние тесты класса (фабрика общая)
        const string newPassword = "password-after-change";
        var fromDesktop = _factory.GetToken(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var fromTablet = _factory.GetToken(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var changeResponse = await SendAs(fromDesktop, HttpMethod.Put, "/api/auth/password", new
        {
            currentPassword = TestWebApplicationFactory.SecondPassword,
            newPassword
        });

        changeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var freshToken = JsonSerializer
            .Deserialize<JsonElement>(await changeResponse.Content.ReadAsStringAsync())
            .GetProperty("token").GetString()!;

        // Второе устройство осталось со старым токеном — оно должно быть отозвано
        (await SendAs(fromTablet, HttpMethod.Get, "/api/auth/me"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // Токен, которым меняли пароль, тоже мёртв — работает только выданный в ответе
        (await SendAs(fromDesktop, HttpMethod.Get, "/api/auth/me"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAs(freshToken, HttpMethod.Get, "/api/auth/me"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Возвращаем пароль обратно, чтобы тест не зависел от порядка выполнения
        var restore = await SendAs(freshToken, HttpMethod.Put, "/api/auth/password", new
        {
            currentPassword = newPassword,
            newPassword = TestWebApplicationFactory.SecondPassword
        });
        restore.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CacheThresholds_SaveThenReset_RoundTripsThroughMe()
    {
        var authed = _factory.CreateAuthenticatedClient();

        var save = await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 10, warn5mMin = 2 });
        save.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = (await MeBody(authed)).GetProperty("cacheWarnThresholds");
        saved.GetProperty("warn1hMin").GetInt32().Should().Be(10);
        saved.GetProperty("warn5mMin").GetInt32().Should().Be(2);

        // Пустое тело — сброс к дефолтам фронта: в /me снова null (поля нет)
        var reset = await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { });
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MeBody(authed)).GetProperty("cacheWarnThresholds").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(0, 2)]     // ниже нижней границы
    [InlineData(60, 2)]    // часовой порог не меньше всего срока — кэш был бы «остывающим» с рождения
    [InlineData(10, 5)]    // 5-минутный порог не меньше всего срока
    [InlineData(10, null)] // половина пары
    [InlineData(null, 2)]  // симметричная половина пары
    public async Task CacheThresholds_OutOfRange_Returns400AndKeepsValue(int? warn1h, int? warn5m)
    {
        var authed = _factory.CreateAuthenticatedClient();
        await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { });

        var response = await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = warn1h, warn5mMin = warn5m });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MeBody(authed)).GetProperty("cacheWarnThresholds").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CacheThresholds_ForeignBody_Returns400AndKeepsValue()
    {
        var authed = _factory.CreateAuthenticatedClient();
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 10, warn5mMin = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Опечатка в имени — не тихий сброс: запрос отвергается, сохранённая настройка остаётся
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMins = 10, warn5mMins = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MeBody(authed)).GetProperty("cacheWarnThresholds").GetProperty("warn1hMin")
            .GetInt32().Should().Be(10);

        // Старые имена (секунды) на записи тоже не принимаются: единица — только минуты
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hSec = 600, warn5mSec = 300 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await MeBody(authed)).GetProperty("cacheWarnThresholds").GetProperty("warn5mMin")
            .GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task CacheThresholds_NonNumericValues_Returns400AndKeepsValue()
    {
        var authed = _factory.CreateAuthenticatedClient();
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 10, warn5mMin = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Строка вместо числа — 400, а не тихий сброс: единица — минуты, «10» — не минута
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = "10", warn5mMin = "2" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Дробь — тоже 400: порог — целое число минут
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 10.5, warn5mMin = 2.5 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Отвергнутый запрос не трогает сохранённую настройку
        (await MeBody(authed)).GetProperty("cacheWarnThresholds").GetProperty("warn1hMin")
            .GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task CacheThresholds_BoundaryValues_Accepted()
    {
        var authed = _factory.CreateAuthenticatedClient();

        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 59, warn5mMin = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 1, warn5mMin = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await authed.PutAsJsonAsync("/api/auth/cache-thresholds", new { });
    }

    [Fact]
    public async Task CacheThresholds_PerOwner_OtherUserKeepsDefault()
    {
        var owner = _factory.CreateAuthenticatedClient();
        (await owner.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 15, warn5mMin = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Другой владелец не видит чужой порог: ручка трогает только свой профиль
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        (await MeBody(other)).GetProperty("cacheWarnThresholds").ValueKind.Should().Be(JsonValueKind.Null);

        // И настройка другого владельца не перезаписывает порог собственника
        await other.PutAsJsonAsync("/api/auth/cache-thresholds", new { warn1hMin = 2, warn5mMin = 1 });
        (await MeBody(owner)).GetProperty("cacheWarnThresholds").GetProperty("warn1hMin")
            .GetInt32().Should().Be(15);

        // Возвращаем обоих к дефолтам — фабрика общая для класса
        await owner.PutAsJsonAsync("/api/auth/cache-thresholds", new { });
        await other.PutAsJsonAsync("/api/auth/cache-thresholds", new { });
    }

    private static async Task<JsonElement> MeBody(HttpClient client) =>
        JsonSerializer.Deserialize<JsonElement>(await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());

    private async Task<HttpResponseMessage> SendAs(
        string token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) }
        };
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }
}
