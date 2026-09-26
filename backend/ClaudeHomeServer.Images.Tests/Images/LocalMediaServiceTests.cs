using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Клиент ComfyUI и сервис локальной генерации на фейковом HTTP: проверки заявки (лимиты,
// путь вне проекта, чужая задача), постановка графа и сбор результата в папку проекта
public class LocalMediaServiceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string ProjectId = "proj-1";

    private readonly string _tempDir;
    private readonly string _root;
    private readonly FakeComfy _comfy = new();
    private readonly FakeProjectAccess _projects = new();

    public LocalMediaServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "local_media_" + Guid.NewGuid().ToString("N"));
        _root = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects.Roots[(Owner, ProjectId)] = _root;
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private (LocalMediaService Service, LocalMediaJobStore Store) Build(bool enabled = true, int maxPerOwner = 2,
        int maxQueue = 4)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["LocalMedia:Enabled"] = enabled ? "true" : "false",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:MaxQueuedPerOwner"] = maxPerOwner.ToString(),
            ["LocalMedia:MaxComfyQueue"] = maxQueue.ToString(),
            ["LocalMedia:PollIntervalMs"] = "500",
        }).Build();
        var store = new LocalMediaJobStore(config);
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        return (new LocalMediaService(client, store, _projects, config, NullLogger<LocalMediaService>.Instance), store);
    }

    private static LocalMediaRequest Generate(string owner = Owner, string project = ProjectId) =>
        new(owner, project, "session-1", LocalMediaOps.GenerateImage, Prompt: "котик на подоконнике");

    // ─── Клиент ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Клиент_Prompt_ВсегдаСПревьюLatent2rgb()
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Generate(), default);

        result.Error.Should().BeNull();
        var sent = _comfy.Prompts.Should().ContainSingle().Subject;
        sent["extra_data"]!["preview_method"]!.GetValue<string>().Should().Be("latent2rgb",
            "без него превью TAEHV под DynamicVRAM роняет общий процесс ComfyUI");
        sent["prompt"]!["enc"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Be("котик на подоконнике");
    }

    [Fact]
    public async Task Клиент_ОтказГрафа_ТекстОшибкиНоды()
    {
        var (service, store) = Build();
        _comfy.RejectPrompt = """{"error":{"message":"Prompt outputs failed validation"},"node_errors":{"ks":{"errors":[{"message":"Value not in list","details":"sampler_name"}]}}}""";

        var result = await service.SubmitAsync(Generate(), default);

        result.Error.Should().Contain("Prompt outputs failed validation").And.Contain("Value not in list");
        store.ActiveCount(Owner).Should().Be(0, "непоставленная задача не занимает лимит");
    }

    [Fact]
    public void Клиент_История_ВидеоИОшибкаРазбираются()
    {
        var video = JsonNode.Parse("""
            {"status":{"status_str":"success","completed":true,"messages":[]},
             "outputs":{"save":{"images":[{"filename":"lm_1_00001_.mp4","subfolder":"ccs-local-media","type":"output"}],"animated":[true]},
                        "preview":{"images":[{"filename":"tmp.png","subfolder":"","type":"temp"}]}}}
            """)!.AsObject();
        var parsed = ComfyClient.ParseHistory(video);
        parsed.Completed.Should().BeTrue();
        parsed.Files.Should().ContainSingle().Which.FileName.Should().Be("lm_1_00001_.mp4");

        var failed = JsonNode.Parse("""
            {"status":{"status_str":"error","completed":false,"messages":[["execution_error",{"node_type":"KSampler","exception_message":"CUDA out of memory"}]]},
             "outputs":{}}
            """)!.AsObject();
        var error = ComfyClient.ParseHistory(failed);
        error.Failed.Should().BeTrue();
        error.Error.Should().Be("KSampler: CUDA out of memory");
    }

    [Fact]
    public void Клиент_ПозицияВОчереди()
    {
        var queue = new ComfyQueueState(["a"], ["b", "c"]);
        queue.PositionOf("a").Should().Be(0);
        queue.PositionOf("c").Should().Be(2);
        queue.PositionOf("zzz").Should().BeNull();
        queue.Length.Should().Be(3);
    }

    // ─── Проверки заявки ─────────────────────────────────────────────────────

    [Fact]
    public async Task Выключено_Отказ_БезПоходаВComfy()
    {
        var (service, _) = Build(enabled: false);

        var result = await service.SubmitAsync(Generate(), default);

        result.Error.Should().Contain("выключена");
        _comfy.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ЛимитВладельца_ВтораяЗадачаОтклоняется()
    {
        var (service, _) = Build(maxPerOwner: 1);

        (await service.SubmitAsync(Generate(), default)).Error.Should().BeNull();
        var second = await service.SubmitAsync(Generate(), default);

        second.Error.Should().Contain("незавершённые");
        _comfy.Prompts.Should().HaveCount(1);
    }

    [Fact]
    public async Task ОчередьComfyЗанята_ЧестныйОтказ_БезПостановки()
    {
        var (service, _) = Build(maxQueue: 2);
        _comfy.Running.Add("чужой-прогон");
        _comfy.Pending.Add("ещё-один");

        var result = await service.SubmitAsync(Generate(), default);

        result.Error.Should().Contain("Очередь локальной GPU занята").And.Contain("облаке");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task ComfyНедоступен_ПонятныйОтказ()
    {
        var (service, _) = Build();
        _comfy.Down = true;

        var result = await service.SubmitAsync(Generate(), default);

        result.Error.Should().Contain("ComfyUI недоступен");
    }

    [Fact]
    public async Task ПроектНедоступен_Отказ()
    {
        var (service, _) = Build();

        var result = await service.SubmitAsync(Generate(project: "чужой-проект"), default);

        result.Error.Should().Contain("Проект чата недоступен");
        _comfy.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/../../outside.png")]
    public async Task ВходВнеПроекта_Отказ_БезЗагрузки(string path)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "outside.png"), LocalMediaTestImages.Png(10, 10));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.EditImage,
            Prompt: "x", Images: [path]), default);

        result.Error.Should().Contain("вне папки проекта");
        _comfy.Uploads.Should().BeEmpty();
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task ВходНеКартинка_Отказ()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "не картинка");
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.FaceDetail,
            Images: ["notes.txt"]), default);

        result.Error.Should().Contain("не картинка");
    }

    [Fact]
    public async Task ВидеоДлиннееПотолка_Отказ()
    {
        File.WriteAllBytes(Path.Combine(_root, "frame.png"), LocalMediaTestImages.Png(1344, 768));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.ImageToVideo,
            Prompt: "идёт снег", Images: ["frame.png"], DurationSeconds: 11), default);

        result.Error.Should().Contain("от 1 до 10 секунд");
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Видео_ПортретныйКадр_ПортретноеВидео()
    {
        File.WriteAllBytes(Path.Combine(_root, "frame.png"), LocalMediaTestImages.Png(768, 1344));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.ImageToVideo,
            Prompt: "идёт снег", Images: ["frame.png"], DurationSeconds: 10), default);

        result.Error.Should().BeNull();
        var i2v = _comfy.Prompts.Single()["prompt"]!["i2v"]!["inputs"]!;
        i2v["width"]!.GetValue<int>().Should().Be(768);
        i2v["height"]!.GetValue<int>().Should().Be(1344);
        i2v["length"]!.GetValue<int>().Should().Be(243);
        i2v["first_frame"]!.ToJsonString().Should().Be("[\"img\",0]");
        _comfy.Prompts.Single()["prompt"]!["img"]!["inputs"]!["image"]!.GetValue<string>()
            .Should().StartWith("ccs-local-media/lm_");
    }

    [Fact]
    public async Task ЧужаяЗадача_НеНайдена_НиСтатусом_НиВходом()
    {
        var (service, _) = Build();
        var mine = (await service.SubmitAsync(Generate(), default)).View!.Job;

        (await service.GetAsync("owner-2", mine.Id, default)).Should().BeNull("чужой job_id неотличим от несуществующего");

        _projects.Roots[("owner-2", "proj-2")] = _root;
        var viaInput = await service.SubmitAsync(new LocalMediaRequest("owner-2", "proj-2", null, LocalMediaOps.FaceDetail,
            Images: [mine.Id]), default);
        viaInput.Error.Should().Contain("не найдена");
    }

    // ─── Опрос и сбор результата ─────────────────────────────────────────────

    [Fact]
    public async Task Готово_ФайлВAttachmentsПроекта_СРазмерами_ИУведомлением()
    {
        var (service, _) = Build();
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Complete(job.PromptId, ($"{job.Id}_00001_.png", LocalMediaTestImages.Png(1328, 1328)),
            ($"{job.Id}_00002_.png", LocalMediaTestImages.Png(1328, 1328)));

        var view = await service.GetAsync(Owner, job.Id, default);

        view!.Job.Status.Should().Be(LocalMediaStatuses.Completed);
        var folder = $".cc-attachments/local-media/{job.CreatedAt:yyyy-MM-dd}";
        view.Job.Outputs.Select(o => o.Path).Should().Equal($"{folder}/{job.Id}-1.png", $"{folder}/{job.Id}-2.png");
        view.Job.Outputs[0].Width.Should().Be(1328);
        view.Job.Outputs[0].ContentType.Should().Be("image/png");
        File.Exists(Path.Combine(_root, ".cc-attachments", "local-media", $"{job.CreatedAt:yyyy-MM-dd}", $"{job.Id}-1.png"))
            .Should().BeTrue();
        _projects.Notified.Should().Equal(view.Job.Outputs.Select(o => o.Path));

        // Повторный опрос не пишет файл второй раз
        await service.GetAsync(Owner, job.Id, default);
        _projects.Notified.Should().HaveCount(2);
    }

    [Fact]
    public async Task ГотоваяКартинка_ВходомСледующейОперацииПоJobId()
    {
        var (service, _) = Build();
        var first = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Complete(first.PromptId, ($"{first.Id}_00001_.png", LocalMediaTestImages.Png(64, 64)));
        await service.GetAsync(Owner, first.Id, default);

        var next = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.FaceDetail,
            Images: [first.Id]), default);

        next.Error.Should().BeNull();
        _comfy.Uploads.Should().ContainSingle().Which.Should().StartWith(next.View!.Job.Id + "-in1");
    }

    [Fact]
    public async Task ОшибкаComfy_ЗадачаFailed_СТекстом()
    {
        var (service, store) = Build();
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Fail(job.PromptId, "CUDA out of memory");

        var view = await service.GetAsync(Owner, job.Id, default);

        view!.Job.Status.Should().Be(LocalMediaStatuses.Failed);
        view.Job.Error.Should().Contain("CUDA out of memory");
        store.ActiveCount(Owner).Should().Be(0, "упавшая задача освобождает лимит");
    }

    [Fact]
    public async Task ВОчереди_ПозицияИСтатус()
    {
        var (service, _) = Build();
        _comfy.Running.Add("чужой-прогон");
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;

        var view = await service.GetAsync(Owner, job.Id, default);

        view!.Job.Status.Should().Be(LocalMediaStatuses.Queued);
        view.Position.Should().Be(1);
    }

    [Fact]
    public async Task ЗадачаПропалаИзОчереди_Failed()
    {
        var (service, _) = Build();
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Pending.Clear();

        var view = await service.GetAsync(Owner, job.Id, default);

        view!.Job.Status.Should().Be(LocalMediaStatuses.Failed);
        view.Job.Error.Should().Contain("пропала");
    }

    [Fact]
    public async Task ComfyНедоступенПриОпросе_ЗадачаЖива_Предупреждение()
    {
        var (service, _) = Build();
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Down = true;

        var view = await service.GetAsync(Owner, job.Id, default);

        view!.Job.Status.Should().Be(LocalMediaStatuses.Queued);
        view.Warning.Should().Contain("недоступен");
    }

    [Fact]
    public async Task Ожидание_ВозвращаетПоТаймауту_ЧужиеОтдельно()
    {
        var (service, _) = Build();
        _comfy.Running.Add("чужой-прогон");
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;

        var (jobs, missing) = await service.WaitAsync(Owner, [job.Id, "lm_" + new string('0', 32)], 1, default);

        jobs.Should().ContainSingle().Which.Job.Status.Should().Be(LocalMediaStatuses.Queued);
        missing.Should().ContainSingle();
    }

    [Fact]
    public async Task Коллектор_ДобираетРезультатБезАгента_СторПереживаетРестарт()
    {
        var (service, _) = Build();
        var job = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Complete(job.PromptId, ($"{job.Id}_00001_.png", LocalMediaTestImages.Png(32, 32)));

        // «Рестарт бэкенда»: новый сервис и стор над тем же файлом
        var (restarted, store) = Build();
        (await restarted.CollectPendingAsync(default)).Should().Be(1);

        store.Get(job.Id, Owner)!.Status.Should().Be(LocalMediaStatuses.Completed);
    }
}
