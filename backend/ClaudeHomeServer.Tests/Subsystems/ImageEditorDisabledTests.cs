using System.Net;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Characters;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Subsystems;

// Модуль редактора картинок выключен гейтом Subsystems:ImageEditor:Enabled=false (ADR-018 §10.1).
// Динамический модуль отказывает до Register, его сборка не подключается к MVC, и ручки
// image-editor/* уходят из маршрутизации: 404, а не 500, как у статических вертикалей.
// Гейт доезжает до хоста через UseSetting — механика и почему это не гонка разобраны в
// шапке NotesDisabledTests. Ключ модуля совпадает с ключом подсистемы (imageeditor), поэтому
// ModuleLoader читает ровно эту секцию: ключи конфигурации без учёта регистра.
public class ImageEditorDisabledTests : IDisposable
{
    private readonly DisabledFactory _disabled = new();

    // Контрольный хост: без оверрайда модуль грузится — значит пустота ниже от гейта,
    // а не от опечатки в маршруте
    private readonly TestWebApplicationFactory _enabled = new();

    public void Dispose()
    {
        _disabled.Dispose();
        _enabled.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class DisabledFactory : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Subsystems:ImageEditor:Enabled", "false");
        }
    }

    private static List<string> EditorRoutes(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(r => r.Contains("image-editor", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void Выключенный_модуль_не_даёт_ни_одного_маршрута_редактора()
    {
        EditorRoutes(_disabled).Should().BeEmpty("сборка выключенного модуля не подключается к MVC");
        EditorRoutes(_enabled).Should().Contain(r => r.EndsWith("image-editor/catalog", StringComparison.Ordinal),
            "контроль: включённый модуль отдаёт ручки редактора");
    }

    // Мёртвый MCP-сервер в конфиге хода дал бы «fetch failed» у всех инструментов (ADR-018 §10.5,
    // риск 8). Контрольной половины пока нет: тулсет image-editor регистрируется в шаге 13, там же
    // появится BuildImageEditorContext с гейтом «тулсет есть в реестре» и его проверка → null.
    [Fact]
    public void Выключенный_модуль_не_даёт_тулсета_image_editor()
    {
        var registry = _disabled.Services.GetRequiredService<McpToolsetRegistry>();

        registry.Find("image-editor").Should().BeNull();
        registry.Names.Should().NotContain("image-editor");
    }

    [Theory]
    [InlineData("GET", "catalog")]
    [InlineData("POST", "quote")]
    [InlineData("POST", "transform")]
    [InlineData("GET", "characters")]
    [InlineData("POST", "chats")]
    [InlineData("GET", "chats?path=images/hero.png")]
    [InlineData("PUT", "chats/any/path")]
    public async Task Ручки_редактора_при_выключенном_модуле_404_а_не_500(string method, string tail)
    {
        CharacterEndpointsTests.EnableFlag(_disabled, TestWebApplicationFactory.TestUsername);
        var (projectId, _) = CharacterEndpointsTests.CreateProject(_disabled, TestWebApplicationFactory.TestUsername);
        var client = _disabled.CreateAuthenticatedClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/projects/{projectId}/image-editor/{tail}");
        if (method is "POST" or "PUT") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Гейт контроллера тоже отвечает 404, но с телом { error }; несопоставленный маршрут — пустой
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty("404 от маршрутизации, а не от гейта контроллера");
    }
}
