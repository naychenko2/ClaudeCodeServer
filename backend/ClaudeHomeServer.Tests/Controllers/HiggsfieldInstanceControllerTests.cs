using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Контракт эндпоинта <c>GET /api/higgsfield/status</c>:
/// <c>{ connected, expiresAt?, health?, error? }</c>. <c>health</c> и <c>error</c>
/// подмешиваются из McpStatusStore (ServiceOwnerId/Key) — иначе карточка в UI не видит
/// «Failed» от HiggsfieldToolset и продолжает рисовать зелёный тон на лежащем апстриме.
/// Это и есть регрессия задачи 67b7c30a: до правки контроллер отдавал только флаг из
/// higgsfield.json, и человек в UI не видел реального состояния интеграции.
/// </summary>
public class HiggsfieldInstanceControllerTests
{
    private static (HiggsfieldInstanceController Controller, McpStatusStore Statuses,
        string DataDir) NewController(string? connectionName = null)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "ccs-hf-ctrl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
            }).Build();
        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);
        var oauth = new McpOAuthService(registry, secrets, statuses,
            new NotFoundHandlerFactory(), config,
            NullLogger<McpOAuthService>.Instance);
        var service = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
            config, NullLogger<HiggsfieldOAuthService>.Instance);
        var controller = new HiggsfieldInstanceController(service, oauth, statuses);
        return (controller, statuses, dir);
    }

    [Fact]
    public void Status_ПриЧистомХранилище_ВозвращаетБазовыйКонтрактБезHealth()
    {
        var (controller, _, dir) = NewController();
        try
        {
            var result = controller.Status().Should().BeOfType<OkObjectResult>().Subject;
            var payload = result.Value;
            payload.Should().NotBeNull("контроллер обязан вернуть объект с health/error");
            var t = payload!.GetType();
            t.GetProperty("connected")!.GetValue(payload).Should().Be(false);
            t.GetProperty("expiresAt")!.GetValue(payload).Should().BeNull();
            // health и error необязательные, но если они заданы — не должны лгать:
            // пустое хранилище → null в обоих полях.
            t.GetProperty("health")!.GetValue(payload).Should().BeNull();
            t.GetProperty("error")!.GetValue(payload).Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Status_ПриFailedВStore_ВозвращаетHealthFailedИТекстОшибки()
    {
        var (controller, statuses, dir) = NewController();
        try
        {
            // Имитируем лежащий апстрим: HiggsfieldToolset уже записал Failed в стор.
            statuses.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
                McpServerStatuses.Failed, "HTTP 500 boom");

            var result = controller.Status().Should().BeOfType<OkObjectResult>().Subject;
            var payload = result.Value!;
            var t = payload.GetType();
            t.GetProperty("health")!.GetValue(payload).Should().Be(McpServerStatuses.Failed,
                "карточка в UI обязана показать Failed — иначе громкий отказ остался незаметным");
            t.GetProperty("error")!.GetValue(payload).Should().Be("HTTP 500 boom",
                "текст ошибки нужен для диагностики — иначе карточка зелёная, лог пустой");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Status_ПриConnectedВStore_ВозвращаетHealthConnectedИErrorNull()
    {
        var (controller, statuses, dir) = NewController();
        try
        {
            // Прогрев уже прошёл — карточка должна быть здоровой.
            statuses.RecordProbe(HiggsfieldOAuthService.ServiceOwnerId, HiggsfieldOAuthService.Key,
                McpServerStatuses.Connected, error: null);

            var result = controller.Status().Should().BeOfType<OkObjectResult>().Subject;
            var payload = result.Value!;
            var t = payload.GetType();
            t.GetProperty("health")!.GetValue(payload).Should().Be(McpServerStatuses.Connected,
                "здоровая интеграция должна остаться здоровой — иначе регрессия на пустом месте");
            t.GetProperty("error")!.GetValue(payload).Should().BeNull(
                "Connected + текст ошибки — противоречие; в UI будет красная плашка ниоткуда");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Заглушка HttpClient, чтобы McpOAuthService не падал при первом же EnsureFresh.
    // Нам нужен только сам объект McpOAuthService — реальный OAuth в этих тестах не зовётся.
    private sealed class NotFoundHandlerFactory : IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name)
        {
            return new System.Net.Http.HttpClient(new NotFoundHandler(),
                disposeHandler: false);
        }
    }

    private sealed class NotFoundHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}