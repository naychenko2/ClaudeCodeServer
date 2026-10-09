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

    // Только список LoRA: остальное шва ручкам персонажей не нужно
    private sealed class LoraMedia(IReadOnlyList<string>? loras) : ILocalImageMedia
    {
        public bool Available => true;
        public bool Configured => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult(loras);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }

    private TestWebApplicationFactory Factory(CapturingJobs? jobs = null, ILocalImageMedia? media = null)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
            if (jobs is not null) services.AddSingleton<IImageEditJobs>(jobs);
            if (media is not null) services.AddSingleton(media);
        };
        _factories.Add(factory);
        return factory;
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

    // Имя LoRA внутри пространства владельца-тестера
    private static string Own(TestWebApplicationFactory factory, string name) =>
        LoraNamespace.PrefixFor(factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id) + name;

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    [Fact]
    public async Task Ручка_loras_отдаёт_список_или_available_false()
    {
        var all = new List<string> { "ccs/чужой/c.safetensors", "bare.safetensors" };
        var withList = Factory(media: new LoraMedia(all));
        all.InsertRange(0, [Own(withList, "a.safetensors"), Own(withList, "b.safetensors")]);
        var (id, _) = CreateProject(withList, TestWebApplicationFactory.TestUsername);
        var body = await Json(await withList.CreateAuthenticatedClient().GetAsync($"/api/projects/{id}/image-editor/loras"));
        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("items").EnumerateArray().Select(e => e.GetString()).Should().Equal(Own(withList, "a.safetensors"), Own(withList, "b.safetensors"));

        foreach (var factory in new[] { Factory(media: new LoraMedia(null)), Factory() })
        {
            var (pid, _) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
            var resp = await factory.CreateAuthenticatedClient().GetAsync($"/api/projects/{pid}/image-editor/loras");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var down = await Json(resp);
            down.GetProperty("available").GetBoolean().Should().BeFalse();
            down.GetProperty("items").GetArrayLength().Should().Be(0);
        }
    }

    [Fact]
    public async Task Создание_с_LoRA_пишет_манифест_а_неизвестное_имя_не_создаёт_персонажа()
    {
        var known = new List<string>();
        var factory = Factory(media: new LoraMedia(known));
        known.Add(Own(factory, "anya.safetensors"));
        var (id, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var anya = Own(factory, "anya.safetensors");
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{id}/image-editor";

        var bad = CharacterForm("Аня");
        bad.Add(new StringContent(Own(factory, "evil.safetensors")), "loraName");
        bad.Add(new StringContent("anya_lora"), "loraTrigger");
        (await client.PostAsync($"{api}/characters", bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.Exists(Path.Combine(root, "characters")).Should().BeFalse("отказ по LoRA не оставляет персонажа");

        var ok = CharacterForm("Аня");
        ok.Add(new StringContent(anya), "loraName");
        ok.Add(new StringContent("anya_lora"), "loraTrigger");
        ok.Add(new StringContent("0.9"), "loraStrength");
        var created = await client.PostAsync($"{api}/characters", ok);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var local = (await Json(created)).GetProperty("local");
        local.GetProperty("lora").GetString().Should().Be(anya);
        local.GetProperty("strength").GetDouble().Should().Be(0.9);

        var notNumber = new MultipartFormDataContent
        {
            { new StringContent(anya), "loraName" }, { new StringContent("t"), "loraTrigger" },
            { new StringContent("много"), "loraStrength" },
        };
        (await client.PutAsync($"{api}/characters/anya", notNumber)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var remove = new MultipartFormDataContent { { new StringContent("true"), "removeLora" } };
        var removed = await client.PutAsync($"{api}/characters/anya", remove);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(removed)).TryGetProperty("local", out var gone).Should().BeTrue();
        gone.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Чужая_или_голая_LoRA_в_патче_отвечает_403_и_персонаж_не_создаётся()
    {
        var factory = Factory(media: new LoraMedia(["ccs/00000000000000000000000000000000/x.safetensors", "bare.safetensors"]));
        var (id, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        foreach (var name in new[] { "ccs/00000000000000000000000000000000/x.safetensors", "bare.safetensors" })
        {
            var form = CharacterForm("Аня");
            form.Add(new StringContent(name), "loraName");
            form.Add(new StringContent("anya_lora"), "loraTrigger");
            var resp = await factory.CreateAuthenticatedClient().PostAsync($"/api/projects/{id}/image-editor/characters", form);
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Json(resp)).GetProperty("code").GetString().Should().Be("lora_not_visible");
        }
        Directory.Exists(Path.Combine(root, "characters")).Should().BeFalse();
    }

    [Fact]
    public async Task Недоступный_ComfyUI_при_задании_LoRA_отвечает_503()
    {
        var factory = Factory(media: new LoraMedia(null));
        var (id, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var form = CharacterForm("Аня");
        form.Add(new StringContent(Own(factory, "a.safetensors")), "loraName");
        form.Add(new StringContent("anya_lora"), "loraTrigger");

        var resp = await factory.CreateAuthenticatedClient().PostAsync($"/api/projects/{id}/image-editor/characters", form);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        Directory.Exists(Path.Combine(root, "characters")).Should().BeFalse();
    }

    [Fact]
    public async Task Создание_список_и_фото_у_владельца()
    {
        var factory = Factory();
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
