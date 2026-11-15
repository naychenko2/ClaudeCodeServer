using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Границы двух ручек B09: «одна специальность» (чужой владелец не видит чужих наследников,
// «нет специальности» и неизвестный ключ — 404) и отметка «секции просмотрены» (чужой владелец
// её не ставит). Каталог специальностей общий — право читать его есть у всех, поэтому граница
// здесь не в статусе ручки, а в том, ЧЬИ персоны в неё попадают (PersonaManager.SpecialtyInheritors
// берёт вызывающего, SpecialtiesController.cs:60).
[Trait("Category", "Host")]
public class SpecialtiesSectionsSeenAccessTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _owner; // testuser — владелец своей персоны
    private readonly HttpClient _other; // seconduser — чужой владелец

    public SpecialtiesSectionsSeenAccessTests(TestWebApplicationFactory factory)
    {
        _owner = factory.CreateAuthenticatedClient();
        _other = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
    }

    private async Task<string> CreatePersonaAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/personas", new { name, specialty = "reviewer" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<List<string>> InheritorIdsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/specialties/reviewer");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("inheritors").EnumerateArray()
            .Select(i => i.GetProperty("personaId").GetString()!)];
    }

    [Fact]
    public async Task GetSpecialty_НеизвестныйКлючИНетСпециальности_404()
    {
        (await _owner.GetAsync("/api/specialties/нет-такой-роли")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // «none» — запись каталога «Не задана»: наследников у неё нет по построению,
        // ручки правки (секции, отметка базы) к такой персоне не применяются
        (await _owner.GetAsync("/api/specialties/none")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSpecialty_Наследники_ТолькоСвоегоВладельца()
    {
        var id = await CreatePersonaAsync(_owner, "Моя под ролью");

        // Своя персона видна в своём списке — иначе проверка ниже была бы пустой
        (await InheritorIdsAsync(_owner)).Should().Contain(id);

        (await InheritorIdsAsync(_other)).Should().NotContain(id,
            "чужая персона не попадает в наследники чужого владельца: «обновилось от специальности» его не касается");
    }

    [Fact]
    public async Task MarkSectionsSeen_ЧужомуВладельцу_404_Своему_БазаПишется()
    {
        var id = await CreatePersonaAsync(_owner, "Моя для отметки");

        (await _other.PostAsync($"/api/personas/{id}/sections-seen", null)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "чужая персона не видна — базу отметки со стороны не сбросить");

        var mine = await _owner.PostAsync($"/api/personas/{id}/sections-seen", null);
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sectionsSeenVersion")
            .GetString().Should().NotBeNullOrEmpty("база отметки — у своего владельца");
    }
}
