using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Контекст съёмок видео и их файлы в ComfyUI (находка 2 ревью Б2): латенты в output, первый кадр и копии латентов
// апскейла в input живут не дольше ILocalVideoMedia.ShotTtl. Время — свои часы, файлы — настоящие во временных
// каталогах: удаляются только имена самой съёмки, по точному имени, чужое и похожее остаётся
public class LocalVideoShotCleanupTests : IDisposable
{
    private readonly FakeComfy _comfy = new();
    private readonly ManualClock _clock = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lv_shots_" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly string _output;

    public LocalVideoShotCleanupTests()
    {
        _input = Directory.CreateDirectory(Path.Combine(_root, "comfy", "input")).FullName;
        _output = Directory.CreateDirectory(Path.Combine(_root, "comfy", "output")).FullName;
    }

    public void Dispose()
    {
        TestFs.DeleteDirectoryResilient(_root);
        GC.SuppressFinalize(this);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private LocalVideoMediaAdapter Build(bool cleanupConfigured = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_root, "data", "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:VideoFastDefault"] = "false",
        };
        if (cleanupConfigured)
        {
            settings["LocalMedia:ComfyInputDir"] = _input;
            settings["LocalMedia:ComfyOutputDir"] = _output;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var client = new ComfyClient(new FakeComfyFactory(_comfy), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var cleanup = new LocalMediaCleanup(new LocalMediaJobStore(config), NullLogger<LocalMediaCleanup>.Instance);
        return new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance, cleanup, _clock);
    }

    private string Touch(string root, string name)
    {
        var full = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, name);
        return full;
    }

    private static LocalVideoRequest Scene() =>
        new("идёт снег", LocalMediaTestImages.Png(1344, 768), null, 5, "full", false, Seed: 7);

    // Съёмка завершена, её файлы на «диске ComfyUI»: первый кадр в input, два латента в output
    private async Task<(LocalVideoMediaAdapter Adapter, string Ticket, string Id, string[] Files)> ShotDone()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Scene(), default);
        submitted.Ticket.Should().NotBeNull(submitted.Error);
        var id = _comfy.Uploads[0][..35];
        _comfy.CompleteVideo(submitted.Ticket!, id, [1, 2, 3]);
        (await adapter.PollAsync(submitted.Ticket!, default)).State.Should().Be(LocalVideoState.Completed);
        string[] files =
        [
            Touch(_input, $"ccs-local-media/{id}-in1.png"),
            Touch(_output, $"ccs-local-media/latents/{id}_video_00001_.latent"),
            Touch(_output, $"ccs-local-media/latents/{id}_audio_00001_.latent"),
        ];
        return (adapter, submitted.Ticket!, id, files);
    }

    [Fact]
    public async Task Sweep_before_ttl_keeps_everything_and_after_ttl_removes_latents_inputs_and_the_context()
    {
        var (adapter, ticket, _, files) = await ShotDone();
        adapter.CanUpscale(ticket).Should().BeTrue();

        _clock.Advance(ILocalVideoMedia.ShotTtl - TimeSpan.FromMinutes(1));
        adapter.Sweep();
        adapter.CanUpscale(ticket).Should().BeTrue("срок ещё не вышел");
        files.Should().AllSatisfy(f => File.Exists(f).Should().BeTrue(f));

        _clock.Advance(TimeSpan.FromMinutes(2));
        adapter.Sweep();

        adapter.CanUpscale(ticket).Should().BeFalse("повтор невозможен — контекст выметен");
        files.Should().AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
        (await adapter.SubmitUpscaleAsync(ticket, "1440p", default)).Error.Should().Contain("больше не доступна");
    }

    [Fact]
    public async Task Sweep_also_removes_leftover_upscale_input_copies_of_the_expired_shot()
    {
        var (adapter, ticket, _, _) = await ShotDone();
        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);
        up.Error.Should().BeNull();
        var upId = _comfy.UploadPaths.Single(p => p.EndsWith("-video.latent"))["ccs-local-media-".Length..^"-video.latent".Length];
        var copies = new[]
        {
            Touch(_input, $"ccs-local-media-{upId}-video.latent"),
            Touch(_input, $"ccs-local-media-{upId}-audio.latent"),
        };

        _clock.Advance(ILocalVideoMedia.ShotTtl + TimeSpan.FromMinutes(1));
        adapter.Sweep();

        copies.Should().AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
    }

    [Fact]
    public async Task Sweep_never_touches_foreign_similar_names_or_directories()
    {
        var (adapter, _, id, _) = await ShotDone();
        var otherId = "lv_" + new string('b', 32);
        var foreign = new[]
        {
            Touch(_input, $"ccs-local-media/{otherId}-in1.png"),
            Touch(_output, $"ccs-local-media/latents/{otherId}_video_00001_.latent"),
            Touch(_output, "ccs-local-media/latents/чужой.latent"),
            Touch(_input, "ccs-local-media/lm_" + new string('c', 32) + "-in1.png"),
        };
        var dirNamedLikeLatent = Directory.CreateDirectory(Path.Combine(_output, "ccs-local-media", "latents", $"{id}_video_00001_.dir")).FullName;

        _clock.Advance(ILocalVideoMedia.ShotTtl + TimeSpan.FromMinutes(1));
        adapter.Sweep();

        foreign.Should().AllSatisfy(f => File.Exists(f).Should().BeTrue($"чужое не удаляем: {f}"));
        Directory.Exists(dirNamedLikeLatent).Should().BeTrue("каталог по маске не трогаем");
    }

    [Fact]
    public async Task Finished_upscale_removes_its_input_latent_copies_at_once_but_keeps_the_shot_latents_for_a_retry()
    {
        var (adapter, ticket, _, shotFiles) = await ShotDone();
        var up = await adapter.SubmitUpscaleAsync(ticket, "1440p", default);
        var upId = _comfy.UploadPaths.Single(p => p.EndsWith("-video.latent"))["ccs-local-media-".Length..^"-video.latent".Length];
        var copies = new[]
        {
            Touch(_input, $"ccs-local-media-{upId}-video.latent"),
            Touch(_input, $"ccs-local-media-{upId}-audio.latent"),
        };

        _comfy.CompleteVideo(up.Ticket!, "lv_" + new string('a', 32), [9, 9, 9], withLatents: false);
        (await adapter.PollAsync(up.Ticket!, default)).State.Should().Be(LocalVideoState.Completed);

        copies.Should().AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
        shotFiles.Should().AllSatisfy(f => File.Exists(f).Should().BeTrue("латент съёмки нужен повтору, пока не вышел срок"));
        adapter.CanUpscale(ticket).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_shot_releases_its_first_frame_and_the_context_at_once()
    {
        var adapter = Build();
        var submitted = await adapter.SubmitAsync(Scene(), default);
        var id = _comfy.Uploads[0][..35];
        var frame = Touch(_input, $"ccs-local-media/{id}-in1.png");

        _comfy.Fail(submitted.Ticket!, "CUDA out of memory");
        (await adapter.PollAsync(submitted.Ticket!, default)).State.Should().Be(LocalVideoState.Failed);

        File.Exists(frame).Should().BeFalse("увеличивать нечего — первый кадр не нужен");
        adapter.CanUpscale(submitted.Ticket!).Should().BeFalse();
    }

    [Fact]
    public async Task Without_configured_comfy_dirs_sweep_clears_memory_and_deletes_nothing()
    {
        var adapter = Build(cleanupConfigured: false);
        var submitted = await adapter.SubmitAsync(Scene(), default);
        var id = _comfy.Uploads[0][..35];
        _comfy.CompleteVideo(submitted.Ticket!, id, [1]);
        await adapter.PollAsync(submitted.Ticket!, default);
        var file = Touch(_input, $"ccs-local-media/{id}-in1.png");

        _clock.Advance(ILocalVideoMedia.ShotTtl + TimeSpan.FromMinutes(1));
        adapter.Sweep();

        adapter.CanUpscale(submitted.Ticket!).Should().BeFalse();
        File.Exists(file).Should().BeTrue("каталоги не заданы — чистка файлов выключена");
    }

    [Fact]
    public async Task New_shoot_also_sweeps_expired_contexts()
    {
        var (adapter, ticket, _, files) = await ShotDone();
        _clock.Advance(ILocalVideoMedia.ShotTtl + TimeSpan.FromMinutes(1));

        _comfy.Uploads.Clear();
        (await adapter.SubmitAsync(Scene(), default)).Ticket.Should().NotBeNull();

        adapter.CanUpscale(ticket).Should().BeFalse();
        files.Should().AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
    }

    [Fact]
    public async Task Collector_sweeps_by_timer_without_any_new_shoot()
    {
        var (adapter, ticket, _, files) = await ShotDone();
        _clock.Advance(ILocalVideoMedia.ShotTtl + TimeSpan.FromMinutes(1));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_root, "data", "projects.json"),
            ["LocalMedia:Enabled"] = "true",
            ["LocalMedia:ComfyUrl"] = "http://comfy.test:8188",
            ["LocalMedia:ComfyInputDir"] = _input,
            ["LocalMedia:ComfyOutputDir"] = _output,
        }).Build();
        var store = new LocalMediaJobStore(config);
        var service = new LocalMediaService(new ComfyClient(new FakeComfyFactory(_comfy), config), store, new FakeProjectAccess(), config,
            NullLogger<LocalMediaService>.Instance);
        var collector = new LocalMediaCollector(service, new LocalMediaCleanup(store, NullLogger<LocalMediaCleanup>.Instance),
            NullLogger<LocalMediaCollector>.Instance, adapter);

        await collector.StartAsync(default);
        try
        {
            // Первый проход цикла идёт сразу при старте: ждём результат, а не угадываем паузу
            for (var i = 0; i < 500 && files.Any(File.Exists); i++) await Task.Delay(10);
        }
        finally
        {
            await collector.StopAsync(default);
        }

        adapter.CanUpscale(ticket).Should().BeFalse();
        files.Should().AllSatisfy(f => File.Exists(f).Should().BeFalse(f));
    }
}
