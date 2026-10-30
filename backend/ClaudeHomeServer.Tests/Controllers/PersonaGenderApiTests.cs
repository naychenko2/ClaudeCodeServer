using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Пол персоны (Gender): REST create/update принимает строки "female"/"male"/"unspecified"
// (глобальный JsonStringEnumConverter), null в update = не менять, явный "unspecified" — сброс.
// Поле персистентно: после create/update GET возвращает сохранённое значение.
[Trait("Category", "Host")]
public class PersonaGenderApiTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public PersonaGenderApiTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> CreateAsync(HttpClient c, object body)
    {
        var resp = await c.PostAsJsonAsync("/api/personas", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> UpdateAsync(HttpClient c, string id, object body)
    {
        var resp = await c.PutAsJsonAsync($"/api/personas/{id}", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string GenderOf(JsonElement persona) =>
        persona.GetProperty("gender").GetString()!;

    [Fact]
    public async Task Create_СЖенскимПолом_СохраняетИВозвращаетFemale()
    {
        var persona = await CreateAsync(_client, new { name = "Кира", gender = "female" });
        GenderOf(persona).Should().Be("female");

        // Поле дошло до стора: свежий GET возвращает то же значение
        var fresh = await _client.GetFromJsonAsync<JsonElement>($"/api/personas/{persona.GetProperty("id")}");
        GenderOf(fresh).Should().Be("female");
    }

    [Fact]
    public async Task Create_БезПоляGender_ДефолтUnspecified()
    {
        var persona = await CreateAsync(_client, new { name = "Марк" });
        GenderOf(persona).Should().Be("unspecified");
    }

    [Fact]
    public async Task Update_БезПоляGender_СохраняетПрежнееЗначение()
    {
        var created = await CreateAsync(_client, new { name = "Кира", gender = "female" });
        var id = created.GetProperty("id").GetString()!;

        var updated = await UpdateAsync(_client, id, new { role = "Backend" });
        GenderOf(updated).Should().Be("female");
    }

    [Fact]
    public async Task Update_СМужскимПолом_ОбновляетДоMale()
    {
        var created = await CreateAsync(_client, new { name = "Кира", gender = "female" });
        var id = created.GetProperty("id").GetString()!;

        var updated = await UpdateAsync(_client, id, new { gender = "male" });
        GenderOf(updated).Should().Be("male");
    }

    [Fact]
    public async Task Update_СЯвнымUnspecified_СбрасываетСигналРода()
    {
        var created = await CreateAsync(_client, new { name = "Кира", gender = "female" });
        var id = created.GetProperty("id").GetString()!;

        var updated = await UpdateAsync(_client, id, new { gender = "unspecified" });
        GenderOf(updated).Should().Be("unspecified");
    }
}
