using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Чистка файлов задач в input/output ComfyUI на временных каталогах: удаляется ровно то, что
// записано в задаче и построено из её jobId; чужое, похожее, каталоги и ссылки остаются
public class LocalMediaCleanupTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string ProjectId = "proj-1";

    private readonly string _tempDir;
    private readonly string _input;
    private readonly string _output;
    private readonly LocalMediaJobStore _store;
    private readonly LocalMediaCleanup _cleanup;
    private readonly string _a = NewId();
    private readonly string _b = NewId();

    public LocalMediaCleanupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "local_media_cleanup_" + Guid.NewGuid().ToString("N"));
        _input = Directory.CreateDirectory(Path.Combine(_tempDir, "comfy", "input")).FullName;
        _output = Directory.CreateDirectory(Path.Combine(_tempDir, "comfy", "output")).FullName;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
        }).Build();
        _store = new LocalMediaJobStore(config);
        _cleanup = new LocalMediaCleanup(_store, NullLogger<LocalMediaCleanup>.Instance);
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_tempDir);
        GC.SuppressFinalize(this);
    }

    private static string NewId() => "lm_" + Guid.NewGuid().ToString("N");

    private LocalMediaOptions Options(bool configured = true) => new()
    {
        JobRetentionDays = 7,
        ComfyInputDir = configured ? _input : "",
        ComfyOutputDir = configured ? _output : "",
    };

    private string Touch(string root, string name)
    {
        var full = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, name);
        return full;
    }

    private LocalMediaJob AddJob(string id, string op, string status = LocalMediaStatuses.Completed,
        Action<LocalMediaJob>? setup = null)
    {
        var job = new LocalMediaJob
        {
            Id = id, OwnerId = Owner, ProjectId = ProjectId, Op = op, Status = status,
            FinishedAt = DateTime.UtcNow,
        };
        setup?.Invoke(job);
        _store.Add(job);
        return job;
    }

    [Fact]
    public void ЗавершённаяКартинка_УдаляетсяРовноСвоё_ЧужоеИПохожееОстаются()
    {
        AddJob(_a, LocalMediaOps.EditImage, setup: j =>
        {
            j.ComfyInputs = [$"ccs-local-media/{_a}-in1.png", $"ccs-local-media/{_a}-in2.webp"];
            j.ComfyOutputs = [$"ccs-local-media/{_a}_00001_.png"];
        });
        AddJob(_b, LocalMediaOps.EditImage, status: LocalMediaStatuses.Running, setup: j =>
            j.ComfyInputs = [$"ccs-local-media/{_b}-in1.png"]);
        var own = new[]
        {
            Touch(_input, $"ccs-local-media/{_a}-in1.png"),
            Touch(_input, $"ccs-local-media/{_a}-in2.webp"),
            Touch(_output, $"ccs-local-media/{_a}_00001_.png"),
        };
        var foreign = new[]
        {
            Touch(_input, $"ccs-local-media/{_b}-in1.png"),
            Touch(_output, $"ccs-local-media/{_b}_00001_.png"),
            // Похожие имена, которых нет в задаче
            Touch(_input, $"ccs-local-media/{_a}-in3.png"),
            Touch(_input, $"ccs-local-media/x{_a}-in1.png"),
            Touch(_output, $"ccs-local-media/{_a}_00002_.png"),
            Touch(_output, $"{_a}_00001_.png"),
        };

        _cleanup.Run(Options(), DateTime.UtcNow);

        own.Should().AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
        foreign.Should().AllSatisfy(f => File.Exists(f).Should().BeTrue(f));
        _store.Get(_a, Owner)!.TransientCleaned.Should().BeTrue();
        _store.Get(_b, Owner)!.TransientCleaned.Should().BeFalse("незавершённую задачу чистка не трогает");
    }

    // Мутация: IsOwnName, всегда отвечающий true, удалит чужие имена из подпорченного стора
    [Fact]
    public void ИменаНеОтJobIdЗадачи_НеУдаляются_ДажеЕслиЗаписаныВЗадаче()
    {
        AddJob(_a, LocalMediaOps.EditImage, setup: j =>
        {
            j.ComfyInputs = [$"ccs-local-media/{_b}-in1.png", $"other/{_a}-in1.png", $"ccs-local-media/{_a}-../../keep.txt"];
            j.ComfyOutputs = [$"ccs-local-media/{_b}_00001_.png", $"ccs-local-media/{_a}-00001_.png"];
        });
        var files = new[]
        {
            Touch(_input, $"ccs-local-media/{_b}-in1.png"),
            Touch(_input, $"other/{_a}-in1.png"),
            Touch(_input, "keep.txt"),
            Touch(_output, $"ccs-local-media/{_b}_00001_.png"),
            Touch(_output, $"ccs-local-media/{_a}-00001_.png"),
        };

        _cleanup.Run(Options(), DateTime.UtcNow);

        files.Should().AllSatisfy(f => File.Exists(f).Should().BeTrue(f));
        LocalMediaCleanup.IsOwnName($"ccs-local-media/{_b}-in1.png", _a, ["ccs-local-media/"], '-').Should().BeFalse();
        LocalMediaCleanup.IsOwnName($"ccs-local-media/{_a}-in1.png", _a, ["ccs-local-media/"], '-').Should().BeTrue();
    }

    [Fact]
    public void Каталоги_НеУдаляютсяНикогда()
    {
        AddJob(_a, LocalMediaOps.EditImage, setup: j => j.ComfyInputs = [$"ccs-local-media/{_a}-dir"]);
        var dir = Directory.CreateDirectory(Path.Combine(_input, "ccs-local-media", $"{_a}-dir")).FullName;
        var inside = Touch(dir, "file.png");
        var log = new CapturingLogger();

        new LocalMediaCleanup(_store, log).Run(Options(), DateTime.UtcNow);

        Directory.Exists(dir).Should().BeTrue();
        File.Exists(inside).Should().BeTrue();
        // Держится на явной проверке каталога: на Linux File.Exists для каталога и так ложь
        log.Messages.Should().ContainSingle(m => m.Contains("каталог, не трогаю"));
    }

    private sealed class CapturingLogger : ILogger<LocalMediaCleanup>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void ВидеоПоКадру_ЛатентыИПервыйКадрЖивутДоЗабыванияЗадачи()
    {
        AddJob(_a, LocalMediaOps.ImageToVideo, setup: j =>
        {
            j.ComfyInputs = [$"ccs-local-media/{_a}-in1.png", $"ccs-local-media/{_a}-last.png"];
            j.ComfyFirstFrame = $"ccs-local-media/{_a}-in1.png";
            j.ComfyOutputs = [$"ccs-local-media/{_a}_00001_.mp4"];
            j.LatentVideo = $"ccs-local-media/latents/{_a}_video_00001_.latent";
            j.LatentAudio = $"ccs-local-media/latents/{_a}_audio_00001_.latent";
        });
        var first = Touch(_input, $"ccs-local-media/{_a}-in1.png");
        var last = Touch(_input, $"ccs-local-media/{_a}-last.png");
        var mp4 = Touch(_output, $"ccs-local-media/{_a}_00001_.mp4");
        var latentVideo = Touch(_output, $"ccs-local-media/latents/{_a}_video_00001_.latent");
        var latentAudio = Touch(_output, $"ccs-local-media/latents/{_a}_audio_00001_.latent");
        var foreignLatent = Touch(_output, $"ccs-local-media/latents/{_b}_video_00001_.latent");

        _cleanup.Run(Options(), DateTime.UtcNow);

        File.Exists(last).Should().BeFalse();
        File.Exists(mp4).Should().BeFalse();
        File.Exists(first).Should().BeTrue("по первому кадру апскейл пересобирает conditioning");
        File.Exists(latentVideo).Should().BeTrue("латент нужен апскейлу, пока задача в сторе");
        File.Exists(latentAudio).Should().BeTrue();

        _cleanup.Run(Options(), DateTime.UtcNow.AddDays(8));

        _store.Get(_a, Owner).Should().BeNull();
        File.Exists(first).Should().BeFalse();
        File.Exists(latentVideo).Should().BeFalse();
        File.Exists(latentAudio).Should().BeFalse();
        File.Exists(foreignLatent).Should().BeTrue();
    }

    [Fact]
    public void Апскейл_КопииЛатентовВКорнеInputУбираютсяСразу_ЛатентыИсточникаОстаются()
    {
        // Задача без списка ComfyInputs (поставлена до его появления): копии строятся из jobId
        AddJob(_a, LocalMediaOps.VideoUpscale, setup: j => j.ComfyOutputs = [$"ccs-local-media/{_a}_00001_.mp4"]);
        var copyVideo = Touch(_input, $"ccs-local-media-{_a}-video.latent");
        var copyAudio = Touch(_input, $"ccs-local-media-{_a}-audio.latent");
        var foreignCopy = Touch(_input, $"ccs-local-media-{_b}-video.latent");
        var sourceLatent = Touch(_output, $"ccs-local-media/latents/{_b}_video_00001_.latent");

        _cleanup.Run(Options(), DateTime.UtcNow);

        File.Exists(copyVideo).Should().BeFalse();
        File.Exists(copyAudio).Should().BeFalse();
        File.Exists(foreignCopy).Should().BeTrue();
        File.Exists(sourceLatent).Should().BeTrue();
    }

    [Fact]
    public void ПроваленнаяЗадача_ВходыУбираются_ПервыйКадрТоже()
    {
        AddJob(_a, LocalMediaOps.ImageToVideo, status: LocalMediaStatuses.Failed, setup: j =>
        {
            j.ComfyInputs = [$"ccs-local-media/{_a}-in1.png"];
            j.ComfyFirstFrame = $"ccs-local-media/{_a}-in1.png";
        });
        var first = Touch(_input, $"ccs-local-media/{_a}-in1.png");

        _cleanup.Run(Options(), DateTime.UtcNow);

        File.Exists(first).Should().BeFalse("у проваленной задачи апскейла не будет");
    }

    [Fact]
    public void СимволическаяСсылка_НеТрогается()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir, "outside")).FullName;
        var target = Touch(outside, $"{_a}_00001_.png");
        Directory.CreateSymbolicLink(Path.Combine(_output, "ccs-local-media"), outside);
        var linkedFileTarget = Touch(outside, "real.png");
        Directory.CreateDirectory(Path.Combine(_input, "ccs-local-media"));
        var fileLink = Path.Combine(_input, "ccs-local-media", $"{_a}-in1.png");
        File.CreateSymbolicLink(fileLink, linkedFileTarget);
        AddJob(_a, LocalMediaOps.GenerateImage, setup: j =>
        {
            j.ComfyInputs = [$"ccs-local-media/{_a}-in1.png"];
            j.ComfyOutputs = [$"ccs-local-media/{_a}_00001_.png"];
        });

        _cleanup.Run(Options(), DateTime.UtcNow);

        File.Exists(target).Should().BeTrue("каталог результатов идёт через ссылку наружу");
        new FileInfo(fileLink).LinkTarget.Should().NotBeNull("сама ссылка остаётся");
        File.Exists(linkedFileTarget).Should().BeTrue();
    }

    [Fact]
    public void ПустыеНастройки_НичегоНеУдаляется_ЗадачаЖдётЧистки()
    {
        AddJob(_a, LocalMediaOps.GenerateImage, setup: j =>
        {
            j.ComfyInputs = [$"ccs-local-media/{_a}-in1.png"];
            j.ComfyOutputs = [$"ccs-local-media/{_a}_00001_.png"];
        });
        var input = Touch(_input, $"ccs-local-media/{_a}-in1.png");
        var output = Touch(_output, $"ccs-local-media/{_a}_00001_.png");

        _cleanup.Run(Options(configured: false), DateTime.UtcNow);
        _cleanup.Run(new LocalMediaOptions { ComfyInputDir = _input, ComfyOutputDir = " " }, DateTime.UtcNow);

        File.Exists(input).Should().BeTrue();
        File.Exists(output).Should().BeTrue();
        _store.Get(_a, Owner)!.TransientCleaned.Should().BeFalse("после включения чистки задача ещё уберётся");
    }

    // Сервис записывает в задачу ровно то, что загрузил в input и получил в output
    [Fact]
    public async Task Сервис_ЗаписываетВходыИВыходыComfy_ЧисткаИхУбирает()
    {
        var root = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        File.WriteAllBytes(Path.Combine(root, "cat.png"), LocalMediaTestImages.Png(16, 16));
        var projects = new FakeProjectAccess();
        projects.Roots[(Owner, ProjectId)] = root;
        var comfy = new FakeComfy();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "data", "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
        }).Build();
        var store = new LocalMediaJobStore(config);
        var service = new LocalMediaService(new ComfyClient(new FakeComfyFactory(comfy), config), store, projects, config,
            NullLogger<LocalMediaService>.Instance);

        var job = (await service.SubmitAsync(new LocalMediaRequest(Owner, ProjectId, null, LocalMediaOps.EditImage,
            Prompt: "в шляпе", Images: ["cat.png"]), default)).View!.Job;
        comfy.Complete(job.PromptId, ($"{job.Id}_00001_.png", LocalMediaTestImages.Png(16, 16)));
        var done = (await service.GetAsync(Owner, job.Id, default))!.Job;

        done.ComfyInputs.Should().Equal(comfy.UploadPaths);
        done.ComfyOutputs.Should().Equal($"ccs-local-media/{job.Id}_00001_.png");

        var files = done.ComfyInputs.Select(n => Touch(_input, n)).Concat(done.ComfyOutputs.Select(n => Touch(_output, n))).ToList();
        new LocalMediaCleanup(store, NullLogger<LocalMediaCleanup>.Instance).Run(Options(), DateTime.UtcNow);

        files.Should().NotBeEmpty().And.AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
    }
}
