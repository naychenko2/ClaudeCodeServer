using System.Collections.Concurrent;
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

// Ручки персонажей api/projects/{id}/image-editor/characters* (ADR-017, разделы 1 и 10):
// флаг гейтит сервер, фото видит только владелец проекта, slug с обходом пути — 404,
// выбранный персонаж уходит в запуск генерации.
public class CharacterEndpointsTests : IDisposable
{
    private readonly List<TestWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        GC.SuppressFinalize(this);
    }

    // Запоминает, что контроллер собрал для исполнителя задач
    private sealed class CapturingJobs : IImageEditJobs
    {
        public readonly ConcurrentQueue<ImageEditJobInput> Started = new();

        public Task<ImageEditCallResult<ImageEditQuoteDto>> QuoteAsync(
            string ownerId, string projectId, ImageEditQuoteRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ImageEditCallResult<ImageEditJobCreatedDto>> StartAsync(
            string ownerId, string projectId, ImageEditJobInput input, CancellationToken ct)
        {
            Started.Enqueue(input);
            return Task.FromResult(ImageEditCallResult<ImageEditJobCreatedDto>.Ok(new ImageEditJobCreatedDto("job-1")));
        }

        public ImageEditJobDto? Get(string ownerId, string projectId, string jobId) => null;

        public Task<ImageEditJobDto?> CancelAsync(string ownerId, string projectId, string jobId, CancellationToken ct) =>
            Task.FromResult<ImageEditJobDto?>(null);

        public EditedImage? OpenVariant(string ownerId, string projectId, string jobId, int variant) => null;
    }

    private TestWebApplicationFactory Factory(CapturingJobs? jobs = null)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
            if (jobs is not null) services.AddSingleton<IImageEditJobs>(jobs);
        };
        _factories.Add(factory);
        return factory;
    }

    internal static void EnableFlag(TestWebApplicationFactory factory, string username)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.FindByUsername(username)!.Id, FeatureFlagKeys.ImageEditor, true).Should().BeTrue();
    }

    internal static (string Id, string Root) CreateProject(TestWebApplicationFactory factory, string username)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(username)!;
        var dir = Path.Combine(factory.TempDir, "img_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var id = factory.Services.GetRequiredService<ProjectManager>()
            .Create("img-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, username).Id;
        return (id, dir);
    }

    private static MultipartFormDataContent CharacterForm(string name, int photos = 3)
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
    public async Task Флаг_выключен_ручки_персонажей_и_обсуждения_404()
    {
        var factory = Factory();
        var (projectId, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{projectId}/image-editor";

        var discuss = new MultipartFormDataContent { { new StringContent("что поправить?"), "text" } };
        discuss.Add(new ByteArrayContent(TestImages.Jpeg(1)), "annotated", "annotated.jpg");

        var responses = new[]
        {
            await client.GetAsync($"{api}/characters"),
            await client.PostAsync($"{api}/characters", CharacterForm("Аня")),
            await client.GetAsync($"{api}/characters/anya"),
            await client.PutAsync($"{api}/characters/anya", CharacterForm("Аня", 0)),
            await client.DeleteAsync($"{api}/characters/anya"),
            await client.GetAsync($"{api}/characters/anya/photos/face-01.jpg"),
            await client.PostAsync($"{api}/discuss", discuss),
        };

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(root, "characters")).Should().BeFalse("при выключенном флаге запись не доходит до диска");
        factory.Services.GetRequiredService<SessionManager>().GetByProject(projectId)
            .Should().BeEmpty("при выключенном флаге чат не создаётся");
    }

    [Fact]
    public async Task Создание_список_и_фото_у_владельца()
    {
        var factory = Factory();
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var (projectId, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{projectId}/image-editor";

        var created = await client.PostAsync($"{api}/characters", CharacterForm("Аня"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await Json(created);
        body.GetProperty("slug").GetString().Should().Be("anya");
        body.GetProperty("path").GetString().Should().Be("characters/anya");
        body.TryGetProperty("providers", out _).Should().BeFalse("привязки к аккаунту поставщика наружу не отдаём");
        File.Exists(Path.Combine(root, "characters", "anya", "character.json")).Should().BeTrue();

        var list = await Json(await client.GetAsync($"{api}/characters"));
        list.EnumerateArray().Select(c => c.GetProperty("slug").GetString()).Should().Equal("anya");

        var photo = await client.GetAsync($"{api}/characters/anya/photos/face-01.jpg");
        photo.StatusCode.Should().Be(HttpStatusCode.OK);
        photo.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await photo.Content.ReadAsByteArrayAsync()).Should().Equal(TestImages.Jpeg(1));

        (await client.GetAsync($"{api}/characters/anya/photos/character.json")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "ручка фото отдаёт только файлы из списка фото");
        (await client.GetAsync($"{api}/characters/..%2Fanya/photos/face-01.jpg")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var deleted = await client.DeleteAsync($"{api}/characters/anya");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(Path.Combine(root, "characters", "anya")).Should().BeFalse();
    }

    [Fact]
    public async Task Чужой_пользователь_не_читает_фото_персонажа()
    {
        var factory = Factory();
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        EnableFlag(factory, TestWebApplicationFactory.SecondUsername);
        var (projectId, _) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var owner = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{projectId}/image-editor";
        (await owner.PostAsync($"{api}/characters", CharacterForm("Аня"))).StatusCode.Should().Be(HttpStatusCode.Created);

        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await stranger.GetAsync($"{api}/characters/anya/photos/face-01.jpg")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"{api}/characters")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"{api}/characters/anya")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"{api}/characters/anya")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.GetAsync($"{api}/characters/anya/photos/face-01.jpg")).StatusCode
            .Should().Be(HttpStatusCode.OK, "после попыток чужого фото владельца на месте");
    }

    [Fact]
    public async Task Выбранный_персонаж_уходит_в_запуск_генерации()
    {
        var jobs = new CapturingJobs();
        var factory = Factory(jobs);
        EnableFlag(factory, TestWebApplicationFactory.TestUsername);
        var (projectId, _) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{projectId}/image-editor";
        (await client.PostAsync($"{api}/characters", CharacterForm("Аня", 4))).StatusCode.Should().Be(HttpStatusCode.Created);

        var form = new MultipartFormDataContent
        {
            { new StringContent("q-1"), "quoteId" },
            { new StringContent("в осеннем парке"), "prompt" },
            { new StringContent("anya"), "characterSlug" },
        };
        form.Add(new ByteArrayContent(TestImages.Jpeg(9)), "source", "hero.jpg");

        var started = await client.PostAsync($"{api}/jobs", form);

        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var input = jobs.Started.Should().ContainSingle().Subject;
        input.Character.Should().Be(new CharacterRef("anya", "Аня", null));
        input.References.Should().HaveCount(CharacterStore.PhotosPerRequest)
            .And.OnlyContain(r => r.Role == ReferenceRole.Character);

        form = new MultipartFormDataContent
        {
            { new StringContent("q-1"), "quoteId" },
            { new StringContent("../x"), "characterSlug" },
        };
        form.Add(new ByteArrayContent(TestImages.Jpeg(9)), "source", "hero.jpg");
        (await client.PostAsync($"{api}/jobs", form)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        jobs.Started.Should().ContainSingle("неизвестный персонаж не доходит до исполнителя");
    }
}
