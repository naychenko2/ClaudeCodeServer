using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// /api/deploy — граница привилегий (ADR-010): ручка запускает код на хосте под учёткой
// владельца. Здесь проверяем именно поверхность: доступ (аноним, не-админ), отказ на
// машине без контура (в тестовой среде секции Deploy нет — штатный путь «выключено»)
// и заголовок X-Build у health, который не должен ломать контракт эндпоинта.
public class DeployControllerTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task Без_авторизации_401()
    {
        var anonymous = factory.CreateClient();

        (await anonymous.PostAsJsonAsync("/api/deploy/start", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/deploy/status"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/api/deploy/rollback", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Не_админу_403()
    {
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await user.PostAsJsonAsync("/api/deploy/start", new { }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync("/api/deploy/status"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Контур_не_настроен_503_not_configured()
    {
        var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.PostAsJsonAsync("/api/deploy/start", new { @ref = "master" });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("reason").GetString().Should().Be("not_configured");
    }

    [Fact]
    public async Task Откат_при_выключенном_контуре_тоже_503()
    {
        var admin = factory.CreateAuthenticatedClient();

        (await admin.PostAsJsonAsync("/api/deploy/rollback", new { }))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Статус_отдаёт_пустой_журнал_и_флаг_выключено()
    {
        var admin = factory.CreateAuthenticatedClient();

        var resp = await admin.GetAsync("/api/deploy/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("enabled").GetBoolean().Should().BeFalse();
        body.GetProperty("history").GetArrayLength().Should().Be(0);
        body.GetProperty("releases").GetArrayLength().Should().Be(0);
    }

    // Контракт /api/health не меняется: аноним, 204, пустое тело. Файла build-id.txt рядом
    // с тестовым хостом нет — значит и заголовка X-Build быть не должно.
    [Fact]
    public async Task Health_остаётся_анонимным_204_без_заголовка_если_сборка_неизвестна()
    {
        var anonymous = factory.CreateClient();

        var resp = await anonymous.GetAsync("/api/health");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        resp.Headers.Contains(BuildIdProvider.HeaderName).Should().BeFalse();
    }
}
