using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Управление маршрутом фоновых действий (локаль/claude) — эндпоинт только для админов.
public class LocalActionsAdminControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public LocalActionsAdminControllerTests(TestWebApplicationFactory factory)
    {
        _admin = factory.CreateAuthenticatedClient();
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    [Fact]
    public async Task ОбычныйПользовательНеМожетМенятьМаршрут()
    {
        var put = await _user.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "claude" });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var del = await _user.DeleteAsync($"/api/admin/local-actions/{LocalActionCatalog.NotesTags}");
        del.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task АдминПереключаетМаршрутИВидитЭтоВUsage()
    {
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "claude" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("source").GetString().Should().Be("admin");
        // Легаси-значение одиночной «модели по умолчанию» нормализуется в средний слот:
        // в ответ уходит слот, а не эхо ввода — селектор в UI знает только слоты и модели
        body.GetProperty("route").GetString().Should().Be("tier:medium");

        // Экран «Использование» отдаёт тот же источник — тумблер в UI будет отмечен как переопределённый
        var usage = await _admin.GetFromJsonAsync<JsonElement>("/api/usage");
        var action = usage.GetProperty("ollama").GetProperty("actions").EnumerateArray()
            .First(a => a.GetProperty("key").GetString() == LocalActionCatalog.NotesTags);
        action.GetProperty("source").GetString().Should().Be("admin");

        // Сброс возвращает действие к дефолту каталога
        var del = await _admin.DeleteAsync($"/api/admin/local-actions/{LocalActionCatalog.NotesTags}");
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await del.Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("source").GetString().Should().Be("default");
    }

    [Fact]
    public async Task АдминНазначаетМестуСлотТира()
    {
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "tier:strong" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("route").GetString().Should().Be("tier:strong");
        body.GetProperty("source").GetString().Should().Be("admin");

        await _admin.DeleteAsync($"/api/admin/local-actions/{LocalActionCatalog.NotesTags}");
    }

    [Fact]
    public async Task НеизвестноеДействие404()
    {
        var put = await _admin.PutAsJsonAsync("/api/admin/local-actions/bogus-action", new { route = "local" });
        put.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task АдминПрименяетTiersПресеты()
    {
        var tiers = await _admin.PostAsJsonAsync("/api/admin/local-actions/preset", new { preset = "tiers" });
        tiers.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await tiers.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("preset").GetString().Should().Be("tiers");

        var tiersLocal = await _admin.PostAsJsonAsync("/api/admin/local-actions/preset", new { preset = "tiers-local" });
        tiersLocal.StatusCode.Should().Be(HttpStatusCode.OK);
        body = await tiersLocal.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("preset").GetString().Should().Be("tiers-local");
    }

    [Fact]
    public async Task НесуществующаяМодель400()
    {
        // Опечатка в id модели должна отлавливаться при сохранении, а не всплывать
        // при первом фоновом вызове через полчаса
        var put = await _admin.PutAsJsonAsync(
            $"/api/admin/local-actions/{LocalActionCatalog.NotesTags}", new { route = "нет-такой-модели" });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
