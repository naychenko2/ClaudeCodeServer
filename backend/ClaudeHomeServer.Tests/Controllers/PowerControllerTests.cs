using System.Net;
using System.Net.Http.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Замки управления питанием на уровне HTTP. Фича по умолчанию выключена конфигом — в тестовом
// окружении её никто не включает, и это ровно то состояние, в котором код приезжает на чужую
// машину: пункта меню нет, команда отвечает 404.
//
// Два отказа легко перепутать, поэтому проверяем оба: не-админ получает отказ по правам, а
// выключенная фича — 404, потому что дело не в правах. И отдельно — что GET отвечает 200 даже
// выключенным: его зовёт шапка при каждом монтировании.
public class PowerControllerTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private sealed record StatusResponse(bool Enabled, bool Available, int DelaySeconds, object? Pending);

    [Fact]
    public async Task GetStatus_НеАдмин_Отказ()
    {
        var client = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.GetAsync("/api/admin/power/status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Schedule_НеАдмин_Отказ()
    {
        var client = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.PostAsJsonAsync("/api/admin/power", new { action = "shutdown" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetStatus_ФичаВыключена_Отвечает200СПризнакомEnabledFalse()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/power/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>();
        body!.Enabled.Should().BeFalse();
        body.Pending.Should().BeNull();
    }

    [Fact]
    public async Task Schedule_ФичаВыключена_404()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync("/api/admin/power", new { action = "shutdown" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cancel_ФичаВыключена_404()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/admin/power/cancel", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
