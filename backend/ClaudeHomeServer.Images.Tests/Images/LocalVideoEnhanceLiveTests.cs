using System.Diagnostics;
using ClaudeHomeServer.Services.Images.LocalMedia;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Живой прогон «Увеличить» против настоящего ComfyUI: включается переменной CCS_LIVE_COMFY (адрес ComfyUI), иначе пропускается.
// Арендой арбитра GPU занимается тот, кто запускает (POST /lease), тест её не берёт. ffmpeg и ffprobe — на хосте.
// Результат кладётся в CCS_LIVE_OUT (по умолчанию /tmp/enhance-live)
public class LocalVideoEnhanceLiveTests
{
    private sealed class Http(string url) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    private sealed class CliDsp : IVideoDsp
    {
        public bool Available => true;

        public async Task<VideoDspInfo?> ProbeAsync(string path, CancellationToken ct)
        {
            var o = await Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "csv=p=0", path);
            var lines = o.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var v = lines.First(l => l.StartsWith("video", StringComparison.Ordinal)).Split(',');
            var hasAudio = lines.Any(l => l.StartsWith("audio", StringComparison.Ordinal));
            var dur = double.Parse(lines.Last().Trim(), System.Globalization.CultureInfo.InvariantCulture);
            return new VideoDspInfo(dur, int.Parse(v[1]), int.Parse(v[2]), 24, hasAudio);
        }

        public async Task<VideoDspResult> MuxAudioAsync(string videoPath, string audioSourcePath, string outPath, CancellationToken ct)
        {
            await Run("ffmpeg", "-y", "-i", videoPath, "-i", audioSourcePath, "-map", "0:v:0", "-map", "1:a:0?", "-c", "copy", "-shortest", outPath);
            return File.Exists(outPath) ? VideoDspResult.Success : VideoDspResult.Fail("нет файла");
        }

        public Task<VideoDspResult> FilmstripAsync(string path, string outPath, int frames, int height, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoDspResult> LastFrameAsync(string path, string outPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoDspResult> AssembleAsync(FilmPlan plan, string outPath, IProgress<VideoAssembleProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public static async Task<string> Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return o;
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Живое_увеличение_роликa_со_звуком(int scale)
    {
        var url = Environment.GetEnvironmentVariable("CCS_LIVE_COMFY");
        if (string.IsNullOrEmpty(url)) return;
        var outDir = Environment.GetEnvironmentVariable("CCS_LIVE_OUT") ?? "/tmp/enhance-live";
        Directory.CreateDirectory(outDir);
        var src = Path.Combine(outDir, "source.mp4");
        await CliDsp.Run("ffmpeg", "-y", "-f", "lavfi", "-i", "testsrc2=s=864x480:r=24:d=5", "-f", "lavfi", "-i", "sine=f=440:d=5",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", src);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LocalMedia:Enabled"] = "true", ["LocalMedia:ComfyUrl"] = url,
        }).Build();
        var client = new ComfyClient(new Http(url), config);
        var images = new LocalImageMediaAdapter(client, config, NullLogger<LocalImageMediaAdapter>.Instance);
        var adapter = new LocalVideoMediaAdapter(client, config, images, NullLogger<LocalVideoMediaAdapter>.Instance, dsp: new CliDsp());

        var sw = Stopwatch.StartNew();
        var submitted = await adapter.SubmitEnhanceAsync(new LocalVideoEnhanceRequest(await File.ReadAllBytesAsync(src), scale), default);
        submitted.Error.Should().BeNull();
        LocalVideoPoll poll;
        do
        {
            await Task.Delay(2000);
            poll = await adapter.PollAsync(submitted.Ticket!, default);
        } while (poll.State is LocalVideoState.Queued or LocalVideoState.Running && sw.Elapsed < TimeSpan.FromMinutes(8));

        poll.State.Should().Be(LocalVideoState.Completed, poll.Error);
        var dst = Path.Combine(outDir, $"x{scale}.mp4");
        await File.WriteAllBytesAsync(dst, poll.File!.Bytes);
        File.WriteAllText(Path.Combine(outDir, $"x{scale}.txt"), $"eta={submitted.EtaSeconds} real={sw.Elapsed.TotalSeconds:0.0}s bytes={poll.File.Bytes.Length} {poll.File.Width}x{poll.File.Height} sound={poll.File.HasSound}");
        poll.File.HasSound.Should().BeTrue();
    }
}
