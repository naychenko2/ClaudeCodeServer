using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Ручки версий LoRA персонажа (ADR-026-contracts §7.3)
[Trait("Category", "Host")]
public class CharacterLoraVersionsEndpointsTests : IDisposable
{
    private readonly List<TestWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        GC.SuppressFinalize(this);
    }

    // Список заполняют после создания фабрики: префикс LoRA зависит от id пользователя этой фабрики
    private sealed class LoraMedia(IReadOnlyList<string>? initial) : ILocalImageMedia
    {
        public IReadOnlyList<string>? Loras { get; set; } = initial;
        public bool Available => true;
        public bool Configured => true;
        public Task<int?> QueueLengthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> ListLorasAsync(CancellationToken ct) => Task.FromResult(Loras);
        public int? EtaSeconds(LocalImageOp op, int count, int images) => throw new NotSupportedException();
        public Task<LocalImageSubmitted> SubmitAsync(LocalImageRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<LocalImagePoll> PollAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(string ticket, CancellationToken ct) => throw new NotSupportedException();
    }

    // media == null — штатный шов хоста
    private TestWebApplicationFactory Factory(ILocalImageMedia? media)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
            // Без подмены остаётся штатный шов хоста: LocalMedia выключен, список LoRA — null
            if (media is null) return;
            services.RemoveAll<ILocalImageMedia>();
            services.AddSingleton(media);
        };
        _factories.Add(factory);
        return factory;
    }

    private static string Api(string projectId) => $"/api/projects/{projectId}/image-editor/characters/anya/lora-versions";

    private static string OwnerLora(TestWebApplicationFactory factory, string file = "anya_v1.safetensors") =>
        LoraNamespace.PrefixFor(factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id) + file;

    private static async Task<(string Id, string Root)> ProjectWithCharacter(TestWebApplicationFactory factory)
    {
        var (id, root) = CharacterEndpointsTests.CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var form = new MultipartFormDataContent { { new StringContent("Аня"), "name" } };
        for (var i = 1; i <= 3; i++)
        {
            var photo = new ByteArrayContent(TestImages.Jpeg((byte)i));
            photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(photo, "photos", $"IMG_{i}.jpg");
        }
        (await factory.CreateAuthenticatedClient().PostAsync($"/api/projects/{id}/image-editor/characters", form))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        return (id, root);
    }

    private static CharacterLoraRun Run(string runId, string status, string? loraFile = null, string? title = null) =>
        new(1, runId, "job", status, title, "standard", "ccsanya", 30, "hash",
            DateTimeOffset.UtcNow, status == CharacterLoraRuns.Status.Ready ? DateTimeOffset.UtcNow : null,
            loraFile, null, null, null);

    private static CharacterLoraRuns Runs(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<CharacterLoraRuns>();

    private static StringContent Body(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage resp) => (await Json(resp)).GetProperty("code").GetString()!;

    private static HttpRequestMessage Patch(string url, object body) =>
        new(HttpMethod.Patch, url) { Content = Body(body) };

    private const string Ready = "aaaaaaaaaaaa", Failed = "bbbbbbbbbbbb", Training = "cccccccccccc";

    [Fact]
    public async Task Список_отдаёт_loraFile_только_у_ready()
    {
        var factory = Factory(null);
        var lora = OwnerLora(factory);
        var (id, root) = await ProjectWithCharacter(factory);
        var client = factory.CreateAuthenticatedClient();
        Runs(factory).Create(root, "anya", Run(Ready, "ready", lora));
        Runs(factory).Create(root, "anya", Run(Failed, "failed", lora));
        Runs(factory).Create(root, "anya", Run(Training, "training"));

        var items = (await Json(await client.GetAsync(Api(id)))).GetProperty("items").EnumerateArray().ToList();
        string? File(string runId)
        {
            var item = items.Single(i => i.GetProperty("runId").GetString() == runId);
            return item.TryGetProperty("loraFile", out var v) ? v.GetString() : null;
        }
        File(Ready).Should().Be(lora);
        File(Failed).Should().BeNull();
        File(Training).Should().BeNull();
    }

    [Fact]
    public async Task Список_показывает_подключённую_версию_и_свою_LoRA()
    {
        var factory = Factory(null);
        var lora = OwnerLora(factory);
        var (id, root) = await ProjectWithCharacter(factory);
        var client = factory.CreateAuthenticatedClient();
        Runs(factory).Create(root, "anya", Run(Ready, "ready", lora));
        Runs(factory).Create(root, "anya", Run(Failed, "failed"));
        Runs(factory).Create(root, "anya", Run("dddddddddddd", "deleting", OwnerLora(factory, "gone.safetensors")));

        var body = await Json(await client.GetAsync(Api(id)));
        body.GetProperty("items").GetArrayLength().Should().Be(2, "deleting в списке не показывается");
        body.GetProperty("own").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("limit").GetInt32().Should().Be(5);
        body.GetProperty("used").GetInt32().Should().Be(1);
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("connected").GetBoolean())
            .Should().AllBeEquivalentTo(false);

        var m1 = new LoraMedia([]);
        // Своя LoRA вручную: ни одна версия не совпала — own
        var withOwn = Factory(m1);
        m1.Loras = [OwnerLora(withOwn, "manual.safetensors")];
        var (id2, root2) = await ProjectWithCharacter(withOwn);
        var c2 = withOwn.CreateAuthenticatedClient();
        var manual = OwnerLora(withOwn, "manual.safetensors");
        var form = new MultipartFormDataContent { { new StringContent(manual), "loraName" }, { new StringContent("hand"), "loraTrigger" } };
        (await c2.PutAsync($"/api/projects/{id2}/image-editor/characters/anya", form)).StatusCode.Should().Be(HttpStatusCode.OK);
        Runs(withOwn).Create(root2, "anya", Run(Ready, "ready", OwnerLora(withOwn)));
        var own = await Json(await c2.GetAsync(Api(id2)));
        own.GetProperty("own").GetProperty("lora").GetString().Should().Be(manual);
    }

    [Fact]
    public async Task Своя_LoRA_несёт_дату_обучения_если_обучена_у_другого_персонажа_проекта()
    {
        var m = new LoraMedia([]);
        var f = Factory(m);
        var lora = OwnerLora(f, "boris.safetensors");
        m.Loras = [lora];
        var (id, root) = await ProjectWithCharacter(f);
        var c = f.CreateAuthenticatedClient();
        var boris = new MultipartFormDataContent { { new StringContent("Борис"), "name" } };
        for (var i = 1; i <= 3; i++)
        {
            var photo = new ByteArrayContent(TestImages.Jpeg((byte)i));
            photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            boris.Add(photo, "photos", $"IMG_{i}.jpg");
        }
        var created = await c.PostAsync($"/api/projects/{id}/image-editor/characters", boris);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var borisSlug = (await Json(created)).GetProperty("slug").GetString()!;
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Runs(f).Create(root, borisSlug, Run(Ready, "ready", lora) with { TrainedAt = at });
        var form2 = new MultipartFormDataContent { { new StringContent(lora), "loraName" }, { new StringContent("hand"), "loraTrigger" } };
        (await c.PutAsync($"/api/projects/{id}/image-editor/characters/anya", form2)).StatusCode.Should().Be(HttpStatusCode.OK);

        var own = (await Json(await c.GetAsync(Api(id)))).GetProperty("own");
        own.GetProperty("lora").GetString().Should().Be(lora);
        own.GetProperty("trainedAt").GetDateTimeOffset().Should().Be(at);
    }

    [Fact]
    public async Task Подключение_пишет_LoRA_в_манифест_и_список_отмечает_connected()
    {
        var m = new LoraMedia([]);
        var f = Factory(m);
        var (id, root) = await ProjectWithCharacter(f);
        var lora = OwnerLora(f);
        m.Loras = [lora];
        Runs(f).Create(root, "anya", Run(Ready, "ready", lora));
        var client = f.CreateAuthenticatedClient();

        var resp = await client.PostAsync($"{Api(id)}/{Ready}/connect", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var local = (await Json(resp)).GetProperty("local");
        local.GetProperty("lora").GetString().Should().Be(lora);
        local.GetProperty("trigger").GetString().Should().Be("ccsanya");
        local.GetProperty("strength").GetDouble().Should().Be(0.8);

        var list = await Json(await client.GetAsync(Api(id)));
        list.GetProperty("items")[0].GetProperty("connected").GetBoolean().Should().BeTrue();
        list.GetProperty("own").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Подключение_отказывает_по_контракту()
    {
        var f = Factory(new LoraMedia(["ccs/other/x.safetensors"]));
        var (id, root) = await ProjectWithCharacter(f);
        var lora = OwnerLora(f);
        var runs = Runs(f);
        runs.Create(root, "anya", Run(Training, "training"));
        runs.Create(root, "anya", Run(Failed, "failed"));
        runs.Create(root, "anya", Run("eeeeeeeeeeee", "ready", "ccs/чужой/x.safetensors"));
        runs.Create(root, "anya", Run(Ready, "ready", lora));
        var client = f.CreateAuthenticatedClient();

        (await Code(await client.PostAsync($"{Api(id)}/ffffffffffff/connect", null))).Should().Be("version_not_found");
        var notReady = await client.PostAsync($"{Api(id)}/{Training}/connect", null);
        notReady.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(notReady)).Should().Be("version_not_ready");
        (await Code(await client.PostAsync($"{Api(id)}/{Failed}/connect", null))).Should().Be("version_not_ready");

        var foreign = await client.PostAsync($"{Api(id)}/eeeeeeeeeeee/connect", null);
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(foreign)).Should().Be("lora_not_visible");

        var missing = await client.PostAsync($"{Api(id)}/{Ready}/connect", null);
        missing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(missing)).Should().Be("lora_missing");
    }

    [Fact]
    public async Task Подключение_без_списка_ComfyUI_это_503()
    {
        var down = Factory(new LoraMedia(null));
        var (id, root) = await ProjectWithCharacter(down);
        Runs(down).Create(root, "anya", Run(Ready, "ready", OwnerLora(down)));
        var resp = await down.CreateAuthenticatedClient().PostAsync($"{Api(id)}/{Ready}/connect", null);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Code(resp)).Should().Be("unavailable");
    }

    [Fact]
    public async Task Переименование_пустое_название_сбрасывает_к_шаблону()
    {
        var f = Factory(null);
        var (id, root) = await ProjectWithCharacter(f);
        var runs = Runs(f);
        runs.Create(root, "anya", Run(Ready, "ready", OwnerLora(f), "Старое"));
        runs.Create(root, "anya", Run(Training, "training"));
        var client = f.CreateAuthenticatedClient();

        var named = await client.SendAsync(Patch($"{Api(id)}/{Ready}", new { title = "  До бороды " }));
        named.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(named)).GetProperty("title").GetString().Should().Be("До бороды");

        var cleared = await client.SendAsync(Patch($"{Api(id)}/{Ready}", new { title = "   " }));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(cleared)).GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        runs.Get(root, "anya", Ready)!.Title.Should().BeNull();

        var tooLong = await client.SendAsync(Patch($"{Api(id)}/{Ready}", new { title = new string('я', 61) }));
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Code(tooLong)).Should().Be("bad_title");
        (await client.SendAsync(Patch($"{Api(id)}/{Ready}", new { title = new string('я', 60) }))).StatusCode.Should().Be(HttpStatusCode.OK);

        var busy = await client.SendAsync(Patch($"{Api(id)}/{Training}", new { title = "x" }));
        busy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(busy)).Should().Be("version_training");
        (await Code(await client.SendAsync(Patch($"{Api(id)}/ffffffffffff", new { title = "x" })))).Should().Be("version_not_found");
    }

    [Fact]
    public async Task Удаление_подключённой_снимает_LoRA_и_помечает_версию_deleting()
    {
        var m = new LoraMedia([]);
        var media = Factory(m);
        var lora = OwnerLora(media);
        m.Loras = [lora];
        var (id, root) = await ProjectWithCharacter(media);
        var runs = Runs(media);
        runs.Create(root, "anya", Run(Ready, "ready", lora));
        runs.Create(root, "anya", Run(Failed, "failed"));
        runs.Create(root, "anya", Run(Training, "training"));
        var client = media.CreateAuthenticatedClient();
        (await client.PostAsync($"{Api(id)}/{Ready}/connect", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var busy = await client.DeleteAsync($"{Api(id)}/{Training}");
        busy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(busy)).Should().Be("version_training");
        (await Code(await client.DeleteAsync($"{Api(id)}/ffffffffffff"))).Should().Be("version_not_found");

        var failed = await client.DeleteAsync($"{Api(id)}/{Failed}");
        failed.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Json(failed)).GetProperty("disconnected").GetBoolean().Should().BeFalse();
        runs.Get(root, "anya", Failed)!.Status.Should().Be("deleting");

        var resp = await client.DeleteAsync($"{Api(id)}/{Ready}");
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await Json(resp);
        body.GetProperty("status").GetString().Should().Be("deleting");
        body.GetProperty("disconnected").GetBoolean().Should().BeTrue();
        runs.Get(root, "anya", Ready)!.Status.Should().Be("deleting");

        var character = await Json(await client.GetAsync($"/api/projects/{id}/image-editor/characters/anya"));
        (character.TryGetProperty("local", out var local) ? local.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null, "LoRA снята с персонажа");
        (await Code(await client.DeleteAsync($"{Api(id)}/{Ready}"))).Should().Be("version_not_found");
    }

    [Fact]
    public async Task Чужой_владелец_не_видит_версии_и_не_трогает_их()
    {
        var f = Factory(new LoraMedia([]));
        var (id, root) = await ProjectWithCharacter(f);
        Runs(f).Create(root, "anya", Run(Ready, "ready", OwnerLora(f)));
        var stranger = f.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await stranger.GetAsync(Api(id))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"{Api(id)}/{Ready}/connect", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.SendAsync(Patch($"{Api(id)}/{Ready}", new { title = "x" }))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"{Api(id)}/{Ready}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Runs(f).Get(root, "anya", Ready)!.Status.Should().Be("ready");
    }
}
