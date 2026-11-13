using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Threads;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using ClaudeHomeServer.Tests.ImageEditor.Threads;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Персонажи личной области api/image-editor/chats/{sessionId}/characters* (ADR-040, Ф2-4): папка — «Личное»
// владельца. Читающие ручки папку не создают, первый персонаж создаёт её лениво; чужой личный чат и чат
// проекта неотличимы (404); обучающие фото и версии LoRA идут теми же контроллерами по второму маршруту.
[Trait("Category", "Host")]
public class PersonalCharacterEndpointsTests : IClassFixture<PersonalCharacterEndpointsTests.Host>
{
    public sealed class Host : TestWebApplicationFactory
    {
        internal readonly FacesEndpointTests.FakeLocalMedia Media = new();

        public Host()
        {
            ExtraServices = services =>
            {
                services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
                services.RemoveAll<ILocalImageMedia>();
                services.AddSingleton<ILocalImageMedia>(Media);
            };
        }
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PersonalCharacterEndpointsTests(Host host)
    {
        _factory = host;
        _client = _factory.CreateAuthenticatedClient();
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();
    private IPersonalProjects PersonalStore => _factory.Services.GetRequiredService<IPersonalProjects>();

    private string OwnerOf(string username) =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!.Id;

    // Личный чат ЧУЖОГО владельца заводит каждый тест; «Личное» у владельца — одно на все тесты класса, поэтому
    // тесты, которым нужна чистая папка, берут второго пользователя
    private Task<Session> PersonalChat(string username = TestWebApplicationFactory.TestUsername) =>
        Sessions.CreateChatAsync(OwnerOf(username), ClaudeMode.AcceptEdits, name: "Личный чат");

    private static string Api(string chatId) => $"/api/image-editor/chats/{chatId}";

    private static MultipartFormDataContent Form(string name, int photos = 3)
    {
        var form = new MultipartFormDataContent { { new StringContent(name), "name" } };
        for (var i = 1; i <= photos; i++)
        {
            var photo = new ByteArrayContent(TestImages.Jpeg((byte)i));
            photo.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(photo, "photos", $"IMG_{i}.jpg");
        }
        return form;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    [Fact]
    public async Task Личное_без_папки_список_пуст_а_чтение_папку_не_создаёт()
    {
        var owner = OwnerOf(TestWebApplicationFactory.SecondUsername);
        var client = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var chat = await PersonalChat(TestWebApplicationFactory.SecondUsername);
        PersonalStore.GetPersonalOf(owner).Should().BeNull("предпосылка: «Личного» у второго пользователя ещё нет");

        var list = await client.GetAsync($"{Api(chat.Id)}/characters");
        var one = await client.GetAsync($"{Api(chat.Id)}/characters/anya");
        var photo = await client.GetAsync($"{Api(chat.Id)}/characters/anya/photos/face-01.jpg");
        var loras = await client.GetAsync($"{Api(chat.Id)}/loras");
        var trainingPhotos = await client.GetAsync($"{Api(chat.Id)}/characters/anya/training-photos");

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(list)).GetArrayLength().Should().Be(0);
        one.StatusCode.Should().Be(HttpStatusCode.NotFound);
        photo.StatusCode.Should().Be(HttpStatusCode.NotFound);
        loras.StatusCode.Should().Be(HttpStatusCode.OK);
        trainingPhotos.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(trainingPhotos)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.CharacterNotFound);
        PersonalStore.GetPersonalOf(owner).Should().BeNull("GET-пути «Личное» не создают");
    }

    [Fact]
    public async Task Первый_персонаж_создаёт_Личное_и_CRUD_идёт_в_его_папке()
    {
        var owner = OwnerOf(TestWebApplicationFactory.TestUsername);
        var chat = await PersonalChat();
        var api = Api(chat.Id);

        var created = await _client.PostAsync($"{api}/characters", Form("Аня"));

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var personal = PersonalStore.GetPersonalOf(owner);
        personal.Should().NotBeNull("пишущая ручка создала «Личное» лениво");
        personal!.Kind.Should().Be(ProjectKind.Personal);
        File.Exists(Path.Combine(personal.RootPath, "characters", "anya", "character.json")).Should().BeTrue();
        var body = await Json(created);
        body.GetProperty("slug").GetString().Should().Be("anya");
        body.TryGetProperty("providers", out _).Should().BeFalse();

        var list = await Json(await _client.GetAsync($"{api}/characters"));
        list.EnumerateArray().Select(c => c.GetProperty("slug").GetString()).Should().Contain("anya");
        (await _client.GetAsync($"{api}/characters/anya")).StatusCode.Should().Be(HttpStatusCode.OK);
        var photo = await _client.GetAsync($"{api}/characters/anya/photos/face-01.jpg");
        photo.StatusCode.Should().Be(HttpStatusCode.OK);
        photo.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");

        var rename = new MultipartFormDataContent { { new StringContent("Аня Ивановна"), "name" }, { new StringContent("рыжая"), "description" } };
        var updated = await _client.PutAsync($"{api}/characters/anya", rename);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        (await Json(updated)).GetProperty("description").GetString().Should().Be("рыжая");

        (await _client.DeleteAsync($"{api}/characters/anya")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync($"{api}/characters/anya")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(personal.RootPath, "characters", "anya")).Should().BeFalse();
    }

    [Fact]
    public async Task Ключ_области_и_нити_личного_чата_не_меняются_папкой()
    {
        var chat = await PersonalChat();
        (await _client.PostAsync($"{Api(chat.Id)}/characters", Form("Боря"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var threads = await _client.PostAsJsonAsync($"{Api(chat.Id)}/threads", new { draftFolder = "", revision = 0 });

        threads.StatusCode.Should().Be(HttpStatusCode.OK, await threads.Content.ReadAsStringAsync());
        Sessions.GetById(chat.Id)!.ProjectId.Should().BeNull("Session.ProjectId у личного чата остаётся null");
    }

    [Fact]
    public async Task Чужой_личный_чат_и_чат_проекта_404_одним_телом()
    {
        var foreign = await PersonalChat(TestWebApplicationFactory.SecondUsername);
        var (projectId, _) = CharacterEndpointsTests.CreateProject(_factory, TestWebApplicationFactory.TestUsername);
        var projectChat = await Sessions.CreateAsync(projectId, ClaudeMode.AcceptEdits, name: "Чат проекта");

        var bodies = new List<string>();
        foreach (var id in new[] { foreign.Id, projectChat.Id, "нет-такого" })
        {
            foreach (var resp in new[]
                     {
                         await _client.GetAsync($"{Api(id)}/characters"),
                         await _client.GetAsync($"{Api(id)}/loras"),
                         await _client.PostAsync($"{Api(id)}/characters", Form("Аня")),
                         await _client.GetAsync($"{Api(id)}/characters/anya/training-photos"),
                         await _client.GetAsync($"{Api(id)}/characters/anya/lora-versions"),
                     })
            {
                resp.StatusCode.Should().Be(HttpStatusCode.NotFound, id);
                bodies.Add(await resp.Content.ReadAsStringAsync());
            }
        }

        bodies.Distinct().Should().ContainSingle("чужой, проектный и несуществующий чат неотличимы");
    }

    [Fact]
    public async Task Обучающие_фото_и_версии_LoRA_личного_персонажа_идут_вторым_маршрутом()
    {
        var chat = await PersonalChat();
        var api = Api(chat.Id);
        (await _client.PostAsync($"{api}/characters", Form("Вера"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var photos = await _client.GetAsync($"{api}/characters/vera/training-photos");
        var versions = await _client.GetAsync($"{api}/characters/vera/lora-versions");
        var missing = await _client.GetAsync($"{api}/characters/nobody/lora-versions");

        photos.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(photos)).GetProperty("count").GetInt32().Should().Be(0);
        versions.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(versions)).GetProperty("items").GetArrayLength().Should().Be(0);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(missing)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.CharacterNotFound);
    }

    [Fact]
    public async Task Маршрут_faces_у_личной_области_есть_отказ_по_версии_а_не_маршрутизации()
    {
        var chat = await PersonalChat();
        var opened = await _client.PostAsJsonAsync($"{Api(chat.Id)}/threads", new { draftFolder = "", revision = 0 });
        var threadId = (await Json(opened)).GetProperty("focus").GetString()!;

        var faces = await _client.PostAsync(
            $"{Api(chat.Id)}/threads/{threadId}/versions/{ImageThreadVersion.OriginId}/faces", null!);

        faces.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(faces)).GetProperty("code").GetString().Should().Be(ImageEditErrorCodes.VersionNotFound,
            "маршрут есть: у пустого черновика нет картинки версии");
    }
}
