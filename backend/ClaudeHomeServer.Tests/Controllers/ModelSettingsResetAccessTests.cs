using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Маршруты сброса настроек моделей: гейт роли на глобальном слое, валидация ключа
// и разведение scope — персоны чистятся только у «Только для меня».
public class ModelSettingsResetAccessTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _admin;
    private readonly HttpClient _user;

    public ModelSettingsResetAccessTests(TestWebApplicationFactory factory)
    {
        _admin = factory.CreateAuthenticatedClient(); // testuser — admin
        _user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    [Fact]
    public async Task СбросГлобального_БезAdmin_403()
    {
        var response = await _user.PostAsJsonAsync("/api/specialties/settings/reset/global", new { });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "общий слой сбрасывает только admin");
    }

    [Fact]
    public async Task Сброс_НеизвестныйИлиПустойКлюч_400_БезЗаписи()
    {
        await _admin.PutAsJsonAsync("/api/specialties/settings/global", new
        {
            specialties = new Dictionary<string, object>
            {
                ["analyst"] = new { access = "full", tierStrong = "opus" },
            },
            presets = Array.Empty<object>(),
        });

        (await _admin.PostAsJsonAsync("/api/specialties/settings/reset/global", new { key = "no-such" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PostAsJsonAsync("/api/specialties/settings/reset/global", new { key = "" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.GetAsync("/api/specialties/settings/reset/global/preview?key=no-such"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var settings = await (await _user.GetAsync("/api/specialties/settings"))
            .Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("global").GetProperty("specialties").GetProperty("analyst")
            .GetProperty("tierStrong").GetString().Should().Be("opus", "400 ничего не пишет");

        await _admin.PostAsJsonAsync("/api/specialties/settings/reset/global", new { });
    }

    // reset/global чистит ОБЩИЙ слой и персон не касается; reset/owner после снятия
    // слоёв сузился ровно до «сбросить уровни у моих персон» (ADR-012).
    [Fact]
    public async Task Сброс_ГлобальныйScope_НеТрогаетПерсон_OwnerScope_ЧиститПерсон()
    {
        var persona = await (await _admin.PostAsJsonAsync("/api/personas", new
        {
            name = "Сбрасываемый",
            tierStrong = "opus",
            tierWeak = "haiku",
        })).Content.ReadFromJsonAsync<JsonElement>();
        var id = persona.GetProperty("id").GetString()!;

        var global = await (await _admin.PostAsJsonAsync("/api/specialties/settings/reset/global", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        global.TryGetProperty("personas", out _).Should().BeFalse(
            "сброс общего слоя про специальности, а не про персон");
        global.GetProperty("specialties").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        (await Tier(id)).Should().Be("opus", "общий слой персон владельца не касается");

        var preview = await (await _admin.GetAsync("/api/specialties/settings/reset/owner/preview"))
            .Content.ReadFromJsonAsync<JsonElement>();
        preview.GetProperty("personas").GetInt32().Should().Be(1);
        preview.GetProperty("personaNames").EnumerateArray().Single().GetString()
            .Should().Be("Сбрасываемый");
        (await Tier(id)).Should().Be("opus", "предпросмотр ничего не меняет");

        var reset = await (await _admin.PostAsJsonAsync("/api/specialties/settings/reset/owner", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        reset.GetProperty("personas").GetInt32().Should().Be(1);
        (await Tier(id)).Should().BeNull();
    }

    // Тело запроса необязательно: жест «сбросить всё» шлётся без key
    [Fact]
    public async Task Сброс_БезТелаЗапроса_200()
    {
        var response = await _user.PostAsync("/api/specialties/settings/reset/owner", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string?> Tier(string personaId)
    {
        var persona = await (await _admin.GetAsync($"/api/personas/{personaId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        return persona.GetProperty("tierStrong").GetString();
    }
}
