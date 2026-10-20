using System.Net;
using System.Net.Http.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Замки выкатки на уровне HTTP. Фича по умолчанию выключена конфигом — в тестовом окружении
// её никто не включает, и это ровно то состояние, в котором код приезжает на чужую машину:
// пункта меню нет, запуск отвечает 404.
//
// Проверяем два разных отказа, которые легко перепутать: не-админ получает отказ по правам,
// а выключенная фича — 404, потому что дело не в правах и говорить «вам нельзя» было бы враньём.
// И отдельно — что GET отвечает 200 даже выключенным: его зовёт шапка при каждом монтировании,
// и 404 шумел бы в консоли у каждого админа.
public class TrayDeployControllerTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    [Fact]
    public async Task GetStatus_НеАдмин_Отказ()
    {
        var client = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.GetAsync("/api/admin/deploy/status");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Launch_НеАдмин_Отказ()
    {
        var client = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var response = await client.PostAsync("/api/admin/deploy", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetStatus_ФичаВыключена_Отвечает200СПризнакомEnabledFalse()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/deploy/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>();
        body!.Enabled.Should().BeFalse();
        body.CanLaunch.Should().BeFalse();
    }

    [Fact]
    public async Task Launch_ФичаВыключена_404()
    {
        var client = factory.CreateAuthenticatedClient();

        var response = await client.PostAsync("/api/admin/deploy", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record StatusResponse(bool Enabled, bool CanLaunch, string? Reason);
}
