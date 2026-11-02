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
[Trait("Category", "Host")]
public class CharacterEndpointsTests : IClassFixture<CharacterEndpointsTests.Hosts>
{
    private readonly Hosts _hosts;

    public CharacterEndpointsTests(Hosts hosts) => _hosts = hosts;

    // Хост на КОНФИГУРАЦИЮ, а не на тест: объекты фабрик создаются сразу, ASP.NET-хост внутри каждой
    // стартует лениво; гасятся вместе с фикстурой. Состояние каждый тест заводит сам: фейки сбрасывает
    // Use() хостов JobsHost и MediaHost (в самом Hosts метода нет), проект и персонажей — CreateProject
    // с уникальным именем и каталогом
    public sealed class Hosts : IDisposable
    {
        // Только фейковый fal-драйвер, швы штатные
        public PlainHost Plain { get; } = new();
        // Исполнитель задач запоминает запуск
        public JobsHost Jobs { get; } = new();
        // Список LoRA отдаёт шов ComfyUI
        public MediaHost Media { get; } = new();

        public void Dispose()
        {
            Plain.Dispose();
            Jobs.Dispose();
            Media.Dispose();
        }
    }

    public sealed class PlainHost : TestWebApplicationFactory
    {
        public PlainHost() =>
            ExtraServices = services =>
                services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
    }

    public sealed class JobsHost : TestWebApplicationFactory
    {
        public CapturingJobs Jobs { get; } = new();

        public JobsHost()
        {
            ExtraServices = services =>
            {
                services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
                services.AddSingleton<IImageEditJobs>(Jobs);
            };
        }

        public JobsHost Use()
        {
            Jobs.Reset();
            return this;
        }
    }

    public sealed class MediaHost : TestWebApplicationFactory
    {
        public LoraMedia Media { get; } = new();

        public MediaHost()
        {
            ExtraServices = services =>
            {
                services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
                services.AddSingleton<ILocalImageMedia>(Media);
            };
        }

        // Список LoRA на тест задаёт сам тест (префикс зависит от id пользователя хоста)
        public MediaHost Use(IReadOnlyList<string>? loras)
        {
            Media.Loras = loras;
            return this;
        }
    }

    // Запоминает, что контроллер собрал для исполнителя задач
    public sealed class CapturingJobs : IImageEditJobs
    {
        public readonly ConcurrentQueue<ImageEditJobInput> Started = new();

        public void Reset() => Started.Clear();

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
    public sealed class LoraMedia : ILocalImageMedia
    {
        public IReadOnlyList<string>? Loras { get; set; }
        public bool Available => true;
        public bool Configured => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult(Loras);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
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
        var withList = _hosts.Media.Use(null);
        withList.Media.Loras = new List<string>
        {
            Own(withList, "a.safetensors"), Own(withList, "b.safetensors"), "ccs/чужой/c.safetensors", "bare.safetensors",
        };
        var (id, _) = CreateProject(withList, TestWebApplicationFactory.TestUsername);
        var body = await Json(await withList.CreateAuthenticatedClient().GetAsync($"/api/projects/{id}/image-editor/loras"));
        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("items").EnumerateArray().Select(e => e.GetString()).Should().Equal(Own(withList, "a.safetensors"), Own(withList, "b.safetensors"));

        var downHosts = new[] { (TestWebApplicationFactory)_hosts.Media.Use(null), _hosts.Plain };
        foreach (var factory in downHosts)
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
    public async Task Ручка_loras_отдаёт_даты_только_обученных_и_только_видимых()
    {
        var factory = _hosts.Media.Use(null);
        var trainedFile = Own(factory, "trained.safetensors");
        factory.Media.Loras = new List<string> { trainedFile, Own(factory, "manual.safetensors"), "ccs/чужой/c.safetensors" };
        var (id, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        (await client.PostAsync($"/api/projects/{id}/image-editor/characters", CharacterForm("Аня")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var runs = factory.Services.GetRequiredService<CharacterLoraRuns>();
        CharacterLoraRun Ready(string runId, string file) => new(1, runId, "job", "ready", null, "standard", "ccsanya",
            30, "hash", at, at, file, null, null, null);
        runs.Create(root, "anya", Ready("aaaaaaaaaaaa", trainedFile));
        runs.Create(root, "anya", Ready("bbbbbbbbbbbb", "ccs/чужой/c.safetensors"));

        var body = await Json(await client.GetAsync($"/api/projects/{id}/image-editor/loras"));
        var trained = body.GetProperty("trained");
        trained.EnumerateObject().Select(p => p.Name).Should().Equal(trainedFile);
        trained.GetProperty(trainedFile).GetDateTimeOffset().Should().Be(at);
    }

    [Fact]
    public async Task Создание_с_LoRA_пишет_манифест_а_неизвестное_имя_не_создаёт_персонажа()
    {
        var factory = _hosts.Media.Use(null);
        factory.Media.Loras = new List<string> { Own(factory, "anya.safetensors") };
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
        var factory = _hosts.Media.Use(["ccs/00000000000000000000000000000000/x.safetensors", "bare.safetensors"]);
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
        var factory = _hosts.Media.Use(null);
        var (id, root) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var form = CharacterForm("Аня");
        form.Add(new StringContent(Own(factory, "a.safetensors")), "loraName");
        form.Add(new StringContent("anya_lora"), "loraTrigger");

        var resp = await factory.CreateAuthenticatedClient().PostAsync($"/api/projects/{id}/image-editor/characters", form);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        Directory.Exists(Path.Combine(root, "characters")).Should().BeFalse();
    }

    [Fact]
    public async Task Пустое_описание_в_PUT_стирает_а_отсутствующее_не_меняет()
    {
        var factory = _hosts.Plain;
        var (id, _) = CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var api = $"/api/projects/{id}/image-editor";

        var form = CharacterForm("Аня");
        form.Add(new StringContent("рыжая"), "description");
        (await client.PostAsync($"{api}/characters", form)).StatusCode.Should().Be(HttpStatusCode.Created);

        var keep = new MultipartFormDataContent { { new StringContent("Аня"), "name" } };
        var kept = await Json(await client.PutAsync($"{api}/characters/anya", keep));
        kept.GetProperty("description").GetString().Should().Be("рыжая");

        var erase = new MultipartFormDataContent { { new StringContent(""), "description" } };
        var erased = await Json(await client.PutAsync($"{api}/characters/anya", erase));
        (erased.TryGetProperty("description", out var d) ? d.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null);
        var again = (await Json(await client.GetAsync($"{api}/characters"))).EnumerateArray().Single();
        (again.TryGetProperty("description", out var d2) ? d2.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Создание_список_и_фото_у_владельца()
    {
        var factory = _hosts.Plain;
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
        var factory = _hosts.Plain;
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
        var factory = _hosts.Jobs.Use();
        var jobs = factory.Jobs;
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
        input.Character.Should().BeEquivalentTo(new CharacterRef("anya", "Аня", null), o => o.Excluding(c => c.FacePhotos));
        input.Character!.FacePhotos.Should().NotBeNullOrEmpty().And.HaveCount(4)
            .And.HaveCountLessThanOrEqualTo(10);
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
