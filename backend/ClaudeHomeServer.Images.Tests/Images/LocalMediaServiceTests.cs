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
        int maxQueue = 4, bool fastDefault = false, string upscale1440 = "single")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:VideoFastDefault"] = fastDefault ? "true" : "false",
            ["LocalMedia:Upscale1440Mode"] = upscale1440,
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
    public void Клиент_ЛатентыТолькоИзOutput()
    {
        var entry = JsonNode.Parse("""
            {"status":{"status_str":"success","completed":true},
             "outputs":{"9":{"latents":[
                {"filename":"lm_1_video_00001_.latent","subfolder":"ccs-local-media/latents","type":"output"},
                {"filename":"ccs-local-media-lm_1-video.latent","subfolder":"","type":"input"}]}}}
            """)!.AsObject();

        var parsed = ComfyClient.ParseHistory(entry);

        parsed.Latents.Should().ContainSingle().Which.FileName.Should().Be("lm_1_video_00001_.latent");
        parsed.Files.Should().BeEmpty();
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

    [Fact]
    public async Task Стор_UpdateЧужимВладельцем_ЗадачуНеМеняет()
    {
        var (service, store) = Build();
        var mine = (await service.SubmitAsync(Generate(), default)).View!.Job;

        store.Update(mine.Id, "owner-2", j => j.Status = LocalMediaStatuses.Failed).Should().BeNull();

        store.Get(mine.Id, Owner)!.Status.Should().Be(mine.Status);
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

    // ─── MiniMax H3: видео по тексту, латенты, апскейл, инпейнт, референсы ────

    private static JsonNode Graph(FakeComfy comfy, int index = -1) =>
        comfy.Prompts[index < 0 ? comfy.Prompts.Count + index : index]["prompt"]!;

    private static LocalMediaRequest TextVideo(string? orientation = null, bool? fast = null, string size = "full") =>
        new(Owner, ProjectId, null, LocalMediaOps.TextToVideo, Prompt: "гавань на закате", VideoSize: size,
            Orientation: orientation, Fast: fast);

    // Завершённая видеозадача с латентами — источник для апскейла
    private async Task<LocalMediaJob> CompletedVideoAsync(LocalMediaService service, LocalMediaRequest request,
        bool withLatents = true)
    {
        var job = (await service.SubmitAsync(request, default)).View!.Job;
        _comfy.CompleteVideo(job.PromptId, job.Id, LocalMediaTestImages.Mp4(job.Width!.Value, job.Height!.Value, 5.17),
            withLatents);
        return (await service.GetAsync(Owner, job.Id, default))!.Job;
    }

    [Fact]
    public async Task ТекстВВидео_ПортретИЛатентПодJobId_ВремяПоЗамерам()
    {
        var (service, store) = Build();

        var result = await service.SubmitAsync(TextVideo(orientation: "portrait"), default);

        result.Error.Should().BeNull();
        var t2v = Graph(_comfy)["t2v"]!["inputs"]!;
        t2v["width"]!.GetValue<int>().Should().Be(768);
        t2v["height"]!.GetValue<int>().Should().Be(1344);
        t2v.AsObject().ContainsKey("first_frame").Should().BeFalse();
        var job = result.View!.Job;
        Graph(_comfy)["lat_save_v"]!["inputs"]!["filename_prefix"]!.GetValue<string>()
            .Should().Be($"ccs-local-media/latents/{job.Id}_video");
        Graph(_comfy).AsObject().ContainsKey("sparse").Should().BeFalse("fast по умолчанию выключен");
        result.View.EtaSeconds.Should().Be(310, "замер d653be60: T2V 5 с 1344×768");
        store.Get(job.Id, Owner)!.Prompt.Should().Be("гавань на закате");
        store.Get(job.Id, Owner)!.Frames.Should().Be(124);
    }

    [Fact]
    public async Task Видео_FastПоУмолчаниюИзНастроек_ЯвныйFastСильнее()
    {
        var (service, _) = Build(fastDefault: true);

        (await service.SubmitAsync(TextVideo(), default)).View!.EtaSeconds.Should().Be(310, "fast у T2V не мерен — время обычного");
        Graph(_comfy)["ms"]!["inputs"]!["sparse_attention"]!.GetValue<bool>().Should().BeTrue();
        Graph(_comfy)["sparse"]!["class_type"]!.GetValue<string>().Should().Be("BlockSparseAttention");

        await service.SubmitAsync(TextVideo(fast: false), default);
        Graph(_comfy).AsObject().ContainsKey("sparse").Should().BeFalse();
    }

    [Fact]
    public void Время_ТолькоИзмеренное()
    {
        (int, int) full = (1344, 768), half = (864, 480);

        LocalMediaService.ImageToVideoEta(full, 5, false).Should().Be(333);
        LocalMediaService.ImageToVideoEta(full, 5, true).Should().Be(234);
        LocalMediaService.ImageToVideoEta((768, 1344), 10, false).Should().Be(943);
        LocalMediaService.ImageToVideoEta(full, 10, true).Should().Be(585);
        LocalMediaService.ImageToVideoEta(full, 3, false).Should().Be(333, "короче 5 с — не дольше 5-секундного");
        LocalMediaService.ImageToVideoEta(half, 5, false).Should().Be(94);
        LocalMediaService.ImageToVideoEta(half, 10, false).Should().BeNull("10 с на half не мерены");
        LocalMediaService.TextToVideoEta(full, 5).Should().Be(310);
        LocalMediaService.TextToVideoEta(full, 10).Should().BeNull();
        LocalMediaService.TextToVideoEta(half, 5).Should().BeNull();
        LocalMediaService.InpaintEta(half, 5).Should().Be(98);
        LocalMediaService.InpaintEta(full, 5).Should().BeNull();
        LocalMediaService.Upscale1440Eta(124, tiled: true).Should().Be(915);
        LocalMediaService.Upscale1440Eta(124, tiled: false).Should().Be(1065);
        LocalMediaService.Upscale1440Eta(243, tiled: true).Should().BeNull();
    }

    [Fact]
    public void Настройки_УмолчанияПоЗамерам()
    {
        var options = LocalMediaOptions.Read(new ConfigurationBuilder().Build());

        options.VideoFastDefault.Should().BeTrue("sparse — та же картинка в 1,4–1,6 раза быстрее");
        options.Upscale1440Tiled.Should().BeTrue("tiled быстрее single и не впритык по видеопамяти");
    }

    [Fact]
    public async Task КартинкаВВидео_ПоследнийКадрПоJobId()
    {
        File.WriteAllBytes(Path.Combine(_root, "first.png"), LocalMediaTestImages.Png(1344, 768));
        var (service, _) = Build(maxPerOwner: 3);
        var last = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Complete(last.PromptId, ($"{last.Id}_00001_.png", LocalMediaTestImages.Png(64, 64)));
        await service.GetAsync(Owner, last.Id, default);

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.ImageToVideo,
            Prompt: "она оборачивается", Images: ["first.png"], LastFrame: last.Id), default);

        result.Error.Should().BeNull();
        var job = result.View!.Job;
        Graph(_comfy)["i2v"]!["inputs"]!["last_frame"]!.ToJsonString().Should().Be("[\"img_last\",0]");
        Graph(_comfy)["img_last"]!["inputs"]!["image"]!.GetValue<string>().Should().Be($"ccs-local-media/{job.Id}-last.png");
    }

    [Fact]
    public async Task ГотовоеВидео_ЛатентВСтореНоНеВПроекте()
    {
        var (service, store) = Build();

        var job = await CompletedVideoAsync(service, TextVideo());

        job.Status.Should().Be(LocalMediaStatuses.Completed);
        job.Outputs.Should().ContainSingle().Which.ContentType.Should().Be("video/mp4");
        job.LatentVideo.Should().Be($"ccs-local-media/latents/{job.Id}_video_00001_.latent");
        job.LatentAudio.Should().Be($"ccs-local-media/latents/{job.Id}_audio_00001_.latent");
        Directory.EnumerateFiles(_root, "*.latent", SearchOption.AllDirectories).Should().BeEmpty();
        _projects.Notified.Should().ContainSingle();
    }

    [Fact]
    public async Task Апскейл_ЛатентВКореньInput_ГрафПоИсходнойЗадаче()
    {
        File.WriteAllBytes(Path.Combine(_root, "frame.png"), LocalMediaTestImages.Png(768, 1344));
        var (service, store) = Build();
        var source = await CompletedVideoAsync(service, new LocalMediaRequest(Owner, ProjectId, null,
            LocalMediaOps.ImageToVideo, Prompt: "идёт снег", Images: ["frame.png"], Seed: 77));

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoUpscale,
            SourceJobId: source.Id, UpscaleTarget: "2k"), default);

        result.Error.Should().BeNull();
        var job = result.View!.Job;
        job.Heavy.Should().BeTrue();
        job.Seed.Should().Be(77, "без явного seed апскейл берёт seed исходной задачи");
        var latentName = $"ccs-local-media-{job.Id}-video.latent";
        _comfy.UploadPaths.Should().Contain(latentName, "LoadLatent видит только корень input");
        _comfy.UploadedBytes[latentName].Should().Equal(_comfy.Files[$"ccs-local-media/latents/{source.Id}_video_00001_.latent"]);

        var graph = Graph(_comfy);
        graph["lat_v"]!["inputs"]!["latent"]!.GetValue<string>().Should().Be(latentName);
        graph["lat_a"]!["inputs"]!["latent"]!.GetValue<string>().Should().Be($"ccs-local-media-{job.Id}-audio.latent");
        graph["img"]!["inputs"]!["image"]!.GetValue<string>().Should().Be(store.Get(source.Id, Owner)!.ComfyFirstFrame);
        graph["i2v_hi"]!["inputs"]!["prompt"]!.GetValue<string>().Should().Be("идёт снег");
        graph["i2v_hi"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(1536, "портретное видео — стороны переставлены");
        graph["i2v_hi"]!["inputs"]!["height"]!.GetValue<int>().Should().Be(2688);
        graph["refine"]!["class_type"]!.GetValue<string>().Should().Be("MMH3SplitUpscale", "2K — только тайлами");
    }

    [Theory]
    [InlineData("single", "SamplerCustomAdvanced", 1065)]
    [InlineData("tiled", "MMH3SplitUpscale", 915)]
    public async Task Апскейл1440_РежимИзНастроек(string mode, string refineClass, int eta)
    {
        var (service, _) = Build(upscale1440: mode);
        var source = await CompletedVideoAsync(service, TextVideo());

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoUpscale,
            SourceJobId: source.Id), default);

        result.Error.Should().BeNull();
        Graph(_comfy)["refine"]!["class_type"]!.GetValue<string>().Should().Be(refineClass);
        result.View!.EtaSeconds.Should().Be(eta);
        Graph(_comfy)["i2v_hi"]!["inputs"]!["width"]!.GetValue<int>().Should().Be(2528);
        Graph(_comfy).AsObject().ContainsKey("img").Should().BeFalse("у видео по тексту первого кадра нет");
    }

    [Fact]
    public async Task Апскейл_ЧужойJobId_НеНайден()
    {
        var (service, _) = Build();
        var source = await CompletedVideoAsync(service, TextVideo());
        _projects.Roots[("owner-2", ProjectId)] = _root;
        var before = _comfy.Prompts.Count;

        var result = await service.SubmitAsync(new LocalMediaRequest("owner-2", ProjectId, null, LocalMediaOps.VideoUpscale,
            SourceJobId: source.Id), default);

        result.Error.Should().Be($"Задача {source.Id} не найдена.");
        _comfy.Prompts.Should().HaveCount(before);
        _comfy.UploadPaths.Should().NotContain(p => p.EndsWith(".latent"));
    }

    [Fact]
    public async Task Апскейл_ПоКартинке_Отказ()
    {
        var (service, _) = Build();
        var image = (await service.SubmitAsync(Generate(), default)).View!.Job;
        _comfy.Complete(image.PromptId, ($"{image.Id}_00001_.png", LocalMediaTestImages.Png(64, 64)));
        await service.GetAsync(Owner, image.Id, default);

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoUpscale,
            SourceJobId: image.Id), default);

        result.Error.Should().Contain("только для видео");
        _comfy.Prompts.Should().HaveCount(1);
    }

    [Fact]
    public async Task Апскейл_ПоловинныйРазмерИлиБезЛатента_Отказ()
    {
        var (service, _) = Build(maxPerOwner: 4);
        var half = await CompletedVideoAsync(service, TextVideo(size: "half"));
        var noLatent = await CompletedVideoAsync(service, TextVideo(), withLatents: false);

        (await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoUpscale,
            SourceJobId: half.Id), default)).Error.Should().Contain("только для видео размера full");
        (await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoUpscale,
            SourceJobId: noLatent.Id), default)).Error.Should().Contain("нет сохранённого латента");
    }

    [Fact]
    public async Task ТяжёлыеЗадачи_НеБольшеОднойУВладельца_ЛёгкиеИдут()
    {
        File.WriteAllBytes(Path.Combine(_root, "clip.mp4"), LocalMediaTestImages.Mp4(864, 480, 5.17));
        File.WriteAllBytes(Path.Combine(_root, "mask.png"), LocalMediaTestImages.Png(864, 480));
        File.WriteAllBytes(Path.Combine(_root, "ref.png"), LocalMediaTestImages.Png(512, 512));
        var (service, store) = Build(maxPerOwner: 4);
        var inpaint = new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoInpaint,
            Prompt: "на подоконнике цветок", Video: "clip.mp4", Mask: "mask.png");

        (await service.SubmitAsync(inpaint, default)).View!.Job.Heavy.Should().BeTrue();
        var maxRefs = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null,
            LocalMediaOps.ReferenceToVideo, Prompt: "<Picture 1> машет рукой", Images: ["ref.png"], Identity: "max"), default);
        var light = await service.SubmitAsync(TextVideo(), default);

        maxRefs.Error.Should().Contain("тяжёлая");
        light.Error.Should().BeNull("лёгкая задача тяжёлым лимитом не ограничена");
        store.ActiveHeavyCount(Owner).Should().Be(1);

        // Чужая тяжёлая задача лимит этого владельца не занимает
        _projects.Roots[("owner-2", ProjectId)] = _root;
        (await service.SubmitAsync(inpaint with { OwnerId = "owner-2" }, default)).Error.Should().BeNull();
    }

    [Fact]
    public async Task Инпейнт_ГрафИДлинаПоВидео()
    {
        File.WriteAllBytes(Path.Combine(_root, "clip.mp4"), LocalMediaTestImages.Mp4(480, 864, 10.125));
        File.WriteAllBytes(Path.Combine(_root, "mask.png"), LocalMediaTestImages.Png(480, 864));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoInpaint,
            Prompt: "x", Video: "clip.mp4", Mask: "mask.png"), default);

        result.Error.Should().BeNull();
        var job = result.View!.Job;
        var r2v = Graph(_comfy)["r2v"]!["inputs"]!;
        r2v["width"]!.GetValue<int>().Should().Be(480);
        r2v["length"]!.GetValue<int>().Should().Be(243);
        Graph(_comfy)["src"]!["inputs"]!["file"]!.GetValue<string>().Should().Be($"ccs-local-media/{job.Id}-video.mp4");
        Graph(_comfy)["mask"]!["inputs"]!["image"]!.GetValue<string>().Should().Be($"ccs-local-media/{job.Id}-mask.png");
    }

    [Theory]
    [InlineData(1344, 768, 12.0, 1344, 768, "секунд")]
    [InlineData(1280, 720, 5.0, 1280, 720, "размер")]
    [InlineData(864, 480, 5.0, 1344, 768, "Маска")]
    public async Task Инпейнт_НевалидныйВход_Отказ(int w, int h, double seconds, int maskW, int maskH, string error)
    {
        File.WriteAllBytes(Path.Combine(_root, "clip.mp4"), LocalMediaTestImages.Mp4(w, h, seconds));
        File.WriteAllBytes(Path.Combine(_root, "mask.png"), LocalMediaTestImages.Png(maskW, maskH));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoInpaint,
            Prompt: "x", Video: "clip.mp4", Mask: "mask.png"), default);

        result.Error.Should().Contain(error);
        _comfy.Prompts.Should().BeEmpty();
        _comfy.Uploads.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../outside.mp4", "mask.png")]
    [InlineData("clip.mp4", "../outside.png")]
    public async Task Инпейнт_ВходВнеПроекта_Отказ(string video, string mask)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, "outside.mp4"), LocalMediaTestImages.Mp4(864, 480, 5));
        File.WriteAllBytes(Path.Combine(_tempDir, "outside.png"), LocalMediaTestImages.Png(864, 480));
        File.WriteAllBytes(Path.Combine(_root, "clip.mp4"), LocalMediaTestImages.Mp4(864, 480, 5));
        File.WriteAllBytes(Path.Combine(_root, "mask.png"), LocalMediaTestImages.Png(864, 480));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.VideoInpaint,
            Prompt: "x", Video: video, Mask: mask), default);

        result.Error.Should().Contain("вне папки проекта");
        _comfy.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Референсы_КартинкиВидеоЗвук_ЗагружаютсяИСвязываются()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.png"), LocalMediaTestImages.Png(512, 512));
        File.WriteAllBytes(Path.Combine(_root, "b.png"), LocalMediaTestImages.Png(512, 512));
        File.WriteAllBytes(Path.Combine(_root, "ref.mp4"), LocalMediaTestImages.Mp4(1344, 768, 6));
        File.WriteAllBytes(Path.Combine(_root, "voice.wav"), LocalMediaTestImages.Wav());
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.ReferenceToVideo,
            Prompt: "<Picture 1> и <Picture 2> говорят голосом <Audio 1>", Images: ["a.png", "b.png"],
            RefVideos: ["ref.mp4"], RefAudios: ["voice.wav"], DurationSeconds: 10), default);

        result.Error.Should().BeNull();
        var job = result.View!.Job;
        job.Heavy.Should().BeFalse("identity по умолчанию — match");
        _comfy.Uploads.Should().Equal($"{job.Id}-ref1.png", $"{job.Id}-ref2.png", $"{job.Id}-refvid1.mp4", $"{job.Id}-refaud1.wav");
        var r2v = Graph(_comfy)["r2v"]!["inputs"]!;
        r2v["ref_image_size"]!.GetValue<string>().Should().Be("match");
        r2v["length"]!.GetValue<int>().Should().Be(243);
        r2v["ref_audios.ref_audio_0"]!.ToJsonString().Should().Be("[\"ref_aud0\",0]");
    }

    [Theory]
    [InlineData("short.mp4", null, "от 2 до 15 секунд")]
    [InlineData(null, "notes.txt", "не звук")]
    public async Task Референсы_НевалидныйВход_Отказ(string? video, string? audio, string error)
    {
        File.WriteAllBytes(Path.Combine(_root, "a.png"), LocalMediaTestImages.Png(512, 512));
        File.WriteAllBytes(Path.Combine(_root, "short.mp4"), LocalMediaTestImages.Mp4(864, 480, 1.2));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "не звук, а текст длиннее двенадцати байт");
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.ReferenceToVideo,
            Prompt: "x", Images: ["a.png"], RefVideos: video is null ? [] : [video], RefAudios: audio is null ? [] : [audio]),
            default);

        result.Error.Should().Contain(error);
        _comfy.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Референсы_НеизвестныйIdentity_Отказ()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.png"), LocalMediaTestImages.Png(512, 512));
        var (service, _) = Build();

        var result = await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.ReferenceToVideo,
            Prompt: "x", Images: ["a.png"], Identity: "ultra"), default);

        result.Error.Should().Contain("identity");
    }

    [Fact]
    public void Mp4Проба_РазмерИДлительность_НеMp4Null()
    {
        MediaProbe.ReadMp4(LocalMediaTestImages.Mp4(1344, 768, 5.17))
            .Should().Be(new MediaProbe.VideoInfo(1344, 768, 5.17));
        MediaProbe.ReadMp4(LocalMediaTestImages.Png(10, 10)).Should().BeNull();
        MediaProbe.DetectAudioExtension(LocalMediaTestImages.Wav()).Should().Be(".wav");
    }
}
