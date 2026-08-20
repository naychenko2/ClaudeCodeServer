using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// REST раздела «Инциденты». Тестовый хост поднимается без ключа SigNoz — это ровно тот
/// случай, ради которого опции инцидентов регистрируются независимо от
/// <c>AlertsOptions.IsUsable</c>: эндпоинты обязаны отвечать «не настроено», а не падать
/// на резолве зависимостей контроллера.
/// </summary>
public class TelemetryIncidentsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public TelemetryIncidentsControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Список_БезКлючаSigNoz_ОтдаётСтатусНеНастроено()
    {
        var client = _factory.CreateAuthenticatedClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/incidents");

        body.GetProperty("status").GetString().Should().Be("notConfigured",
            "пустой список без статуса читался бы как «всё тихо»");
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Досье_НеизвестныйОтпечаток_ПриНенастроеннойТелеметрии_ОтдаётСтатус()
    {
        var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/telemetry/incidents/fp-неизвестный");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("notConfigured");
    }

    [Fact]
    public async Task ТекстДосье_ОтдаётсяMarkdown()
    {
        var client = _factory.CreateAuthenticatedClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/telemetry/incidents/fp-1/text");

        body.GetProperty("text").GetString().Should().Contain("## Инцидент:");
    }

    [Fact]
    public async Task БезТокена_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/telemetry/incidents");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
