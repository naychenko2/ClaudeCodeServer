using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.ImageEditor.Fakes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.ImageEditor.Characters;

// Ручки обучающих фото и обучения LoRA персонажа (ADR-026-contracts §7.1, §7.2, §7.4)
public class CharacterLoraTrainEndpointsTests : IDisposable
{
    private readonly List<TestWebApplicationFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeTrainer : ILocalLoraTrainer
    {
        public bool Enabled { get; set; } = true;
        public LoraTrainBusy? BusyNow { get; set; }
        public LoraTrainStartResult StartResult { get; set; } = new("lm_job1", 0, 4850);
        public LoraCancelResult CancelResult { get; set; } = LoraCancelResult.Cancelled;
        public List<LoraTrainStart> Starts { get; } = [];
        public List<(string Owner, string Job, bool Admin)> Cancels { get; } = [];

        public LoraTrainBusy? Busy(string ownerId) =>
            BusyNow is null ? null : BusyNow with { Mine = BusyNow.Mine };

        public TimeSpan StartDelay { get; set; }

        public async Task<LoraTrainStartResult> StartAsync(LoraTrainStart request, CancellationToken ct)
        {
            Starts.Add(request);
            if (StartDelay > TimeSpan.Zero) await Task.Delay(StartDelay, ct);
            return StartResult;
        }

        public Task<LoraTrainState?> GetAsync(string ownerId, string jobId, CancellationToken ct) =>
            Task.FromResult<LoraTrainState?>(null);

        public IReadOnlyList<LoraTrainState> ListFor(string ownerId) => [];
        public IReadOnlyList<LoraTrainState> ListAll() => [];

        public Task<LoraCancelResult> CancelAsync(string ownerId, string jobId, bool asAdmin, CancellationToken ct)
        {
            Cancels.Add((ownerId, jobId, asAdmin));
            return Task.FromResult(CancelResult);
        }

        public Task<LoraDeleteState> DeleteRunAsync(string ownerId, string projectId, string slug, string runId, CancellationToken ct) =>
            Task.FromResult(LoraDeleteState.Deleted);
    }

    // trainer == null — шва нет вовсе (подсистема images выключена)
    private TestWebApplicationFactory Factory(FakeTrainer? trainer)
    {
        var factory = new TestWebApplicationFactory();
        factory.ExtraServices = services =>
        {
            services.AddSingleton<IImageEditor>(new FakeImageEditor("fal", models: FakeImageEditor.Model("m")));
            services.RemoveAll<ILocalLoraTrainer>();
            if (trainer is not null) services.AddSingleton<ILocalLoraTrainer>(trainer);
        };
        _factories.Add(factory);
        return factory;
    }

    private static string Api(string projectId) => $"/api/projects/{projectId}/image-editor";
    private const string Train = "characters/anya/lora-train";
    private const string Photos = "characters/anya/training-photos";

    private static async Task<(string Id, string Root)> ProjectWithCharacter(TestWebApplicationFactory factory, string username)
    {
        var (id, root) = CharacterEndpointsTests.CreateProject(factory, username);
        var form = new MultipartFormDataContent { { new StringContent("Аня"), "name" } };
        for (var i = 1; i <= 3; i++)
        {
            var photo = new ByteArrayContent(TestImages.Jpeg((byte)i));
            photo.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(photo, "photos", $"IMG_{i}.jpg");
        }
        var client = username == TestWebApplicationFactory.TestUsername
            ? factory.CreateAuthenticatedClient()
            : factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        (await client.PostAsync($"{Api(id)}/characters", form)).StatusCode.Should().Be(HttpStatusCode.Created);
        return (id, root);
    }

    // PNG с IHDR нужного размера; seed делает содержимое (а значит и имя) уникальным
    private static byte[] Png(int seed)
    {
        var b = new byte[28];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        "IHDR"u8.ToArray().CopyTo(b, 12);
        b[18] = 2; b[19] = 0x58; b[22] = 2; b[23] = 0x58;   // 600×600
        BitConverter.GetBytes(seed).CopyTo(b, 24);
        return b;
    }

    private static MultipartFormDataContent Files(int from, int count)
    {
        var form = new MultipartFormDataContent();
        for (var i = from; i < from + count; i++)
        {
            var content = new ByteArrayContent(Png(i));
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "files", $"p{i}.png");
        }
        return form;
    }

    private static async Task UploadAsync(HttpClient client, string projectId, int total)
    {
        for (var from = 0; from < total; from += 10)
            (await client.PostAsync($"{Api(projectId)}/{Photos}", Files(from, Math.Min(10, total - from))))
                .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static StringContent Body(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static StringContent Start(string? trigger = null, bool confirm = true, string preset = "standard") =>
        Body(new { preset, trigger, confirm });

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static async Task<string> Code(HttpResponseMessage resp) => (await Json(resp)).GetProperty("code").GetString()!;

    [Fact]
    public async Task Фото_добавляются_читаются_и_удаляются()
    {
        var factory = Factory(new FakeTrainer());
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var added = await client.PostAsync($"{Api(id)}/{Photos}", Files(0, 2));
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(added);
        body.GetProperty("photos").GetProperty("count").GetInt32().Should().Be(2);
        body.GetProperty("rejected").GetArrayLength().Should().Be(0);

        var list = await Json(await client.GetAsync($"{Api(id)}/{Photos}"));
        list.GetProperty("min").GetInt32().Should().Be(20);
        list.GetProperty("photosHash").GetString().Should().StartWith("sha256:");
        var name = list.GetProperty("items")[0].GetProperty("name").GetString()!;

        var file = await client.GetAsync($"{Api(id)}/{Photos}/{name}");
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.Should().StartWith("image/");

        (await client.DeleteAsync($"{Api(id)}/{Photos}/{name}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var gone = await client.DeleteAsync($"{Api(id)}/{Photos}/{name}");
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(gone)).Should().Be("photo_not_found");
        var badName = await client.GetAsync($"{Api(id)}/{Photos}/..%2Fx.png");
        badName.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Фото_отказ_по_одному_не_роняет_запрос_а_пустой_и_большой_запрос_это_400()
    {
        var factory = Factory(new FakeTrainer());
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        var form = Files(0, 1);
        var junk = new ByteArrayContent([1, 2, 3, 4]);
        junk.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(junk, "files", "junk.png");
        var mixed = await Json(await client.PostAsync($"{Api(id)}/{Photos}", form));
        mixed.GetProperty("photos").GetProperty("count").GetInt32().Should().Be(1);
        mixed.GetProperty("rejected")[0].GetProperty("reason").GetString().Should().Be("format");

        var empty = await client.PostAsync($"{Api(id)}/{Photos}", new MultipartFormDataContent { { new StringContent("x"), "note" } });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Code(empty)).Should().Be("invalid_request");
        var many = await client.PostAsync($"{Api(id)}/{Photos}", Files(100, 11));
        many.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Нет_персонажа_или_кривой_slug_это_404_character_not_found()
    {
        var factory = Factory(new FakeTrainer());
        var (id, _) = CharacterEndpointsTests.CreateProject(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        foreach (var url in new[] { Photos, Train, "characters/ghost/training-photos", "characters/..%2Fx/lora-train" })
        {
            var resp = await client.GetAsync($"{Api(id)}/{url}");
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound, url);
        }
        var post = await client.PostAsync($"{Api(id)}/{Train}", Start());
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(post)).Should().Be("character_not_found");
    }

    [Fact]
    public async Task Чужой_владелец_не_видит_и_не_трогает_фото_и_обучение()
    {
        var trainer = new FakeTrainer();
        var factory = Factory(trainer);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var owner = factory.CreateAuthenticatedClient();
        await UploadAsync(owner, id, 20);
        var name = (await Json(await owner.GetAsync($"{Api(id)}/{Photos}"))).GetProperty("items")[0].GetProperty("name").GetString()!;
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await stranger.GetAsync($"{Api(id)}/{Photos}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"{Api(id)}/{Photos}", Files(900, 1))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"{Api(id)}/{Photos}/{name}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"{Api(id)}/{Photos}/{name}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"{Api(id)}/{Train}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"{Api(id)}/{Train}", Start())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.DeleteAsync($"{Api(id)}/{Train}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        trainer.Starts.Should().BeEmpty();
        (await owner.GetAsync($"{Api(id)}/{Photos}/{name}")).StatusCode.Should().Be(HttpStatusCode.OK, "у владельца фото на месте");
    }

    [Fact]
    public async Task Без_шва_ручки_обучения_отвечают_503_no_seam_а_фото_работают()
    {
        var factory = Factory(null);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        foreach (var resp in new[]
        {
            await client.GetAsync($"{Api(id)}/{Train}"),
            await client.PostAsync($"{Api(id)}/{Train}", Start()),
            await client.DeleteAsync($"{Api(id)}/{Train}"),
            await client.GetAsync("/api/image-editor/lora-train/active"),
            await client.DeleteAsync("/api/image-editor/lora-train/active"),
        })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await Code(resp)).Should().Be("no_seam");
        }
        (await client.GetAsync($"{Api(id)}/{Photos}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Состояние_отдаёт_занятость_карты_пресеты_и_триггер_по_умолчанию()
    {
        var until = new DateTime(2026, 10, 10, 10, 5, 0, DateTimeKind.Utc);
        var trainer = new FakeTrainer { BusyNow = new LoraTrainBusy(until, false, "lm_other") };
        var factory = Factory(trainer);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);

        var state = await Json(await factory.CreateAuthenticatedClient().GetAsync($"{Api(id)}/{Train}"));
        state.GetProperty("enabled").GetBoolean().Should().BeTrue();
        state.GetProperty("gpu").GetProperty("busy").GetBoolean().Should().BeTrue();
        state.GetProperty("gpu").GetProperty("mine").GetBoolean().Should().BeFalse();
        state.GetProperty("gpu").GetProperty("busyUntil").GetDateTime().Should().Be(until);
        state.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
        state.GetProperty("defaultTrigger").GetString().Should().Be("ccsanya");
        state.GetProperty("presets")[0].GetProperty("key").GetString().Should().Be("standard");
        state.GetProperty("presets")[0].GetProperty("etaSeconds").GetInt32().Should().Be(4850);
        state.GetProperty("photosChanged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Старт_успешный_создаёт_версию_training_с_хешем_и_триггером()
    {
        var trainer = new FakeTrainer();
        var factory = Factory(trainer);
        var (id, root) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);
        var hash = (await Json(await client.GetAsync($"{Api(id)}/{Photos}"))).GetProperty("photosHash").GetString();

        var resp = await client.PostAsync($"{Api(id)}/{Train}", Start("ccsmy_anya"));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await Json(resp);
        body.GetProperty("jobId").GetString().Should().Be("lm_job1");
        body.GetProperty("etaSeconds").GetInt32().Should().Be(4850);
        var runId = body.GetProperty("runId").GetString()!;

        trainer.Starts.Should().ContainSingle().Which.Should().Match<LoraTrainStart>(s =>
            s.Slug == "anya" && s.Trigger == "ccsmy_anya" && s.Photos.Count == 20 && s.RunId == runId);
        var run = factory.Services.GetRequiredService<CharacterLoraRuns>().Get(root, "anya", runId)!;
        run.Status.Should().Be(CharacterLoraRuns.Status.Training);
        run.JobId.Should().Be("lm_job1");
        run.PhotosHash.Should().Be(hash);
        run.PhotosCount.Should().Be(20);
        run.Trigger.Should().Be("ccsmy_anya");
        run.Preset.Should().Be("standard");

        var state = await Json(await client.GetAsync($"{Api(id)}/{Train}"));
        state.GetProperty("current").GetProperty("runId").GetString().Should().Be(runId);
    }

    [Fact]
    public async Task Старт_без_триггера_берёт_автотриггер()
    {
        var trainer = new FakeTrainer();
        var factory = Factory(trainer);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);

        (await client.PostAsync($"{Api(id)}/{Train}", Start())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        trainer.Starts.Single().Trigger.Should().Be("ccsanya");
    }

    [Fact]
    public async Task Старт_проверки_до_сервиса_дают_400_и_403_и_не_ставят_задачу()
    {
        var trainer = new FakeTrainer();
        var factory = Factory(trainer);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();

        async Task Expect(StringContent body, HttpStatusCode status, string code)
        {
            var resp = await client.PostAsync($"{Api(id)}/{Train}", body);
            resp.StatusCode.Should().Be(status, code);
            (await Code(resp)).Should().Be(code);
        }

        await Expect(Start(), HttpStatusCode.BadRequest, "too_few_photos");
        await UploadAsync(client, id, 20);
        await Expect(Start(confirm: false), HttpStatusCode.BadRequest, "confirm_required");
        await Expect(Start(preset: "turbo"), HttpStatusCode.BadRequest, "bad_preset");
        await Expect(Start("Bad Trigger"), HttpStatusCode.BadRequest, "bad_trigger");
        trainer.Enabled = false;
        await Expect(Start(), HttpStatusCode.Forbidden, "disabled");
        trainer.Starts.Should().BeEmpty();
    }

    [Fact]
    public async Task Старт_предел_пяти_версий_это_409_version_limit()
    {
        var trainer = new FakeTrainer();
        var factory = Factory(trainer);
        var (id, root) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);
        var runs = factory.Services.GetRequiredService<CharacterLoraRuns>();
        for (var i = 0; i < CharacterLoraRuns.MaxVersions; i++)
            runs.Create(root, "anya", new CharacterLoraRun(1, $"00000000000{i}", null, CharacterLoraRuns.Status.Ready, null,
                "standard", "ccsanya", 20, "sha256:x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, $"f{i}.safetensors", 20, null, null));

        var resp = await client.PostAsync($"{Api(id)}/{Train}", Start());
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Code(resp)).Should().Be("version_limit");
        trainer.Starts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LoraTrainErrorCode.OwnerBusy, HttpStatusCode.Conflict, "owner_busy")]
    [InlineData(LoraTrainErrorCode.GpuBusy, HttpStatusCode.Conflict, "gpu_busy")]
    [InlineData(LoraTrainErrorCode.QueueFull, HttpStatusCode.Conflict, "queue_full")]
    [InlineData(LoraTrainErrorCode.Unavailable, HttpStatusCode.ServiceUnavailable, "unavailable")]
    [InlineData(LoraTrainErrorCode.Disabled, HttpStatusCode.Forbidden, "disabled")]
    [InlineData(LoraTrainErrorCode.TooFewPhotos, HttpStatusCode.BadRequest, "too_few_photos")]
    public async Task Отказ_сервиса_старта_отображается_в_код_и_не_оставляет_версию(
        LoraTrainErrorCode error, HttpStatusCode status, string code)
    {
        var until = new DateTime(2026, 10, 10, 10, 5, 0, DateTimeKind.Utc);
        var trainer = new FakeTrainer
        {
            StartResult = new LoraTrainStartResult(null, null, null, error, null, new LoraTrainBusy(until, false, "lm_x")),
        };
        var factory = Factory(trainer);
        var (id, root) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);

        var resp = await client.PostAsync($"{Api(id)}/{Train}", Start());
        resp.StatusCode.Should().Be(status);
        var body = await Json(resp);
        body.GetProperty("code").GetString().Should().Be(code);
        if (error == LoraTrainErrorCode.GpuBusy) body.GetProperty("busyUntil").GetDateTime().Should().Be(until);
        factory.Services.GetRequiredService<CharacterLoraRuns>().List(root, "anya").Should().BeEmpty();
    }

    [Fact]
    public async Task Отмена_своего_обучения_204_а_без_обучения_404_not_training()
    {
        var trainer = new FakeTrainer();
        var factory = Factory(trainer);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);

        var idle = await client.DeleteAsync($"{Api(id)}/{Train}");
        idle.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(idle)).Should().Be("not_training");

        (await client.PostAsync($"{Api(id)}/{Train}", Start())).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.DeleteAsync($"{Api(id)}/{Train}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        trainer.Cancels.Should().ContainSingle().Which.Should().Match<(string Owner, string Job, bool Admin)>(c =>
            c.Job == "lm_job1" && !c.Admin);
    }

    [Fact]
    public async Task Уровень_инстанса_не_админ_получает_403_на_отмену_чужого_и_не_видит_jobId()
    {
        var trainer = new FakeTrainer { BusyNow = new LoraTrainBusy(DateTime.UtcNow.AddHours(1), false, "lm_other") };
        var factory = Factory(trainer);
        var admin = factory.CreateAuthenticatedClient();
        var user = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var forbidden = await user.DeleteAsync("/api/image-editor/lora-train/active");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Code(forbidden)).Should().Be("forbidden");
        trainer.Cancels.Should().BeEmpty("чужое обучение пользователь отменить не может");

        var seenByUser = await Json(await user.GetAsync("/api/image-editor/lora-train/active"));
        seenByUser.GetProperty("busy").GetBoolean().Should().BeTrue();
        seenByUser.TryGetProperty("jobId", out _).Should().BeFalse("не-админу ключа jobId нет вовсе");
        (await Json(await admin.GetAsync("/api/image-editor/lora-train/active")))
            .GetProperty("jobId").GetString().Should().Be("lm_other");

        (await admin.DeleteAsync("/api/image-editor/lora-train/active")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        trainer.Cancels.Should().ContainSingle().Which.Admin.Should().BeTrue();

        trainer.BusyNow = null;
        var idle = await admin.DeleteAsync("/api/image-editor/lora-train/active");
        idle.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Code(idle)).Should().Be("not_training");
    }
    [Fact]
    public async Task Два_параллельных_старта_одного_персонажа_дают_одну_запись_training()
    {
        var trainer = new FakeTrainer { StartDelay = TimeSpan.FromMilliseconds(300) };
        var factory = Factory(trainer);
        var (id, root) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);

        var responses = await Task.WhenAll(
            client.PostAsync($"{Api(id)}/{Train}", Start()),
            client.PostAsync($"{Api(id)}/{Train}", Start()));

        responses.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.Accepted, HttpStatusCode.Conflict]);
        (await Code(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict))).Should().Be("owner_busy");
        trainer.Starts.Should().ContainSingle();
        factory.Services.GetRequiredService<CharacterLoraRuns>().List(root, "anya")
            .Count(r => r.Status == CharacterLoraRuns.Status.Training).Should().Be(1);
    }

    [Fact]
    public async Task Отмена_уже_завершённого_обучения_идемпотентна_204()
    {
        var trainer = new FakeTrainer { CancelResult = LoraCancelResult.AlreadyFinished };
        var factory = Factory(trainer);
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        await UploadAsync(client, id, 20);
        (await client.PostAsync($"{Api(id)}/{Train}", Start())).StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await client.DeleteAsync($"{Api(id)}/{Train}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Фото_крупнее_восьми_мегабайт_отклоняется_как_too_big()
    {
        var factory = Factory(new FakeTrainer());
        var (id, _) = await ProjectWithCharacter(factory, TestWebApplicationFactory.TestUsername);
        var client = factory.CreateAuthenticatedClient();
        var form = new MultipartFormDataContent();
        var big = new ByteArrayContent(new byte[9 * 1024 * 1024]);
        big.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(big, "files", "big.png");

        var resp = await client.PostAsync($"{Api(id)}/{Photos}", form);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = (await Json(resp)).GetProperty("rejected");
        rejected.GetArrayLength().Should().Be(1);
        rejected[0].GetProperty("reason").GetString().Should().Be("too_big");
    }
}
