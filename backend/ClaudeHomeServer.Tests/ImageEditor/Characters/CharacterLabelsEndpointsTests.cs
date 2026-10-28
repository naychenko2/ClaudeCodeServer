using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Метки фото в API персонажа (ADR-027 §1, §10): DTO и PUT photoLabels гейтятся флагом character-smart-photos
[Trait("Category", "Host")]
public class CharacterLabelsEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new()
    {
        ExtraServices = services =>
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m"))),
    };

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private string UserId =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private void Flag(bool on) =>
        _factory.Services.GetRequiredService<UserStore>().SetFeatureFlag(UserId, FeatureFlagKeys.CharacterSmartPhotos, on);

    // Персонаж на диске с одной ручной меткой у первого фото; возвращает (api, имена файлов)
    private async Task<(string Api, string Root, string[] Files)> SeedAsync()
    {
        var (id, root) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        var created = CharacterStore.Create(root,
            new CharacterDraft("Аня", null, [.. Enumerable.Range(1, 3).Select(i => new CharacterPhotoUpload(TestImages.Jpeg((byte)i), i == 1 ? "front" : null))]),
            DateTime.UtcNow);
        var files = created.Value!.Manifest.Photos.Select(p => p.File).ToArray();
        CharacterStore.Update(root, UserId, "anya", new CharacterPatch(null, null, [], [], null, null,
            new Dictionary<string, CharacterPhotoLabels> { [files[1]] = new("profile", "level", "smile") }))
            .Should().NotBeNull();
        await Task.CompletedTask;
        return ($"/api/projects/{id}/image-editor", root, files);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static string[] Keys(JsonElement photo) => [.. photo.EnumerateObject().Select(p => p.Name).Order()];

    [Fact]
    public async Task Флаг_выключен_фото_в_DTO_как_до_фичи()
    {
        var (api, _, files) = await SeedAsync();
        Flag(false);
        var client = _factory.CreateAuthenticatedClient();

        var one = await Json(await client.GetAsync($"{api}/characters/anya"));
        var list = await Json(await client.GetAsync($"{api}/characters"));

        foreach (var photos in new[] { one.GetProperty("photos"), list[0].GetProperty("photos") })
            photos.EnumerateArray().Should().OnlyContain(p => Keys(p).SequenceEqual(new[] { "angle", "file", "primary" }));
        one.GetProperty("photos")[1].GetProperty("file").GetString().Should().Be(files[1]);
    }

    [Fact]
    public async Task Флаг_включён_DTO_отдаёт_метки_и_источник_а_у_неразмеченного_фото_их_нет()
    {
        var (api, _, files) = await SeedAsync();
        Flag(true);

        var photos = (await Json(await _factory.CreateAuthenticatedClient().GetAsync($"{api}/characters/anya")))
            .GetProperty("photos");

        var labeled = photos.EnumerateArray().Single(p => p.GetProperty("file").GetString() == files[1]);
        labeled.GetProperty("angle").GetString().Should().Be("profile");
        labeled.GetProperty("pitch").GetString().Should().Be("level");
        labeled.GetProperty("emotion").GetString().Should().Be("smile");
        labeled.GetProperty("labelSource").GetString().Should().Be("human");
        var plain = photos.EnumerateArray().Single(p => p.GetProperty("file").GetString() == files[2]);
        Keys(plain).Should().Equal("angle", "file", "primary");
    }

    [Fact]
    public async Task PUT_photoLabels_при_включённом_флаге_пишет_метки_человека()
    {
        var (api, root, files) = await SeedAsync();
        Flag(true);
        var form = new MultipartFormDataContent
        {
            { new StringContent($$$"""{"{{{files[2]}}}":{"angle":"three-quarter","emotion":"laugh"}}"""), "photoLabels" },
        };

        var resp = await _factory.CreateAuthenticatedClient().PutAsync($"{api}/characters/anya", form);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = CharacterStore.Get(root, "anya")!.Photos.Single(p => p.File == files[2]);
        saved.Should().Match<CharacterPhoto>(p => p.Angle == "three-quarter" && p.Emotion == "laugh" && p.LabelSource == "human");
        (await Json(resp)).GetProperty("photos").EnumerateArray()
            .Single(p => p.GetProperty("file").GetString() == files[2]).GetProperty("labelSource").GetString().Should().Be("human");
    }

    [Fact]
    public async Task PUT_photoLabels_при_выключенном_флаге_400_и_метки_не_меняются()
    {
        var (api, root, files) = await SeedAsync();
        Flag(false);
        var form = new MultipartFormDataContent
        {
            { new StringContent($$$"""{"{{{files[2]}}}":{"angle":"profile"}}"""), "photoLabels" },
        };

        var resp = await _factory.CreateAuthenticatedClient().PutAsync($"{api}/characters/anya", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        CharacterStore.Get(root, "anya")!.Photos.Single(p => p.File == files[2]).LabelSource.Should().BeNull();
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("""{"чужое.jpg":{"angle":"profile"}}""")]
    [InlineData("""{"FILE":{"angle":"вверх ногами"}}""")]
    [InlineData("""{"FILE":null}""")]
    public async Task PUT_photoLabels_с_мусором_отвечает_400(string body)
    {
        var (api, root, files) = await SeedAsync();
        Flag(true);
        var form = new MultipartFormDataContent { { new StringContent(body.Replace("FILE", files[2])), "photoLabels" } };

        var resp = await _factory.CreateAuthenticatedClient().PutAsync($"{api}/characters/anya", form);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        CharacterStore.Get(root, "anya")!.Photos.Single(p => p.File == files[2]).LabelSource.Should().BeNull();
    }
}
