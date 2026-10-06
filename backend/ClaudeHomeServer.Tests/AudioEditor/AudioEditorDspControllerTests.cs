using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Ручки «Без ИИ» на собранном приложении: правка и склейка её версии, пики, нет шва — 503
// dsp_unavailable, а не 500. Шов IAudioDsp — подставной (регистрация позже боевой побеждает)
public class AudioEditorDspControllerTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly FakeDsp _dsp = new();
    private HttpClient? _client;
    private string _projectId = "";

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<string> Start(bool available = true)
    {
        _dsp.Available = available;
        _factory.ExtraServices = services => services.AddSingleton<IAudioDsp>(_dsp);
        var users = _factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        users.SetFeatureFlag(user.Id, FeatureFlagKeys.AudioEditor, true).Should().BeTrue();
        var dir = Path.Combine(_factory.TempDir, "dsp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "intro.mp3"), "INTRO"u8.ToArray());
        _projectId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("dsp-" + Guid.NewGuid().ToString("N")[..6], dir, user.Id, TestWebApplicationFactory.TestUsername).Id;
        _client = _factory.CreateAuthenticatedClient();
        var chatId = (await _factory.Services.GetRequiredService<SessionManager>()
            .CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Звук")).Id;
        return $"/api/projects/{_projectId}/audio-editor/sessions/{chatId}";
    }

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private async Task<string> OpenIntro(string chat)
    {
        var resp = await _client!.PostAsJsonAsync($"{chat}/threads", new { file = "intro.mp3", revision = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("focus").GetString()!;
    }

    [Fact]
    public async Task Правка_даёт_версию_которую_принимает_склейка_и_пики()
    {
        var chat = await Start();
        var threadId = await OpenIntro(chat);

        var edit = await _client!.PostAsJsonAsync($"{chat}/threads/{threadId}/edit", new { op = "trim", startSec = 0.5, endSec = 2 });
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
        var body = await Json(edit);
        var versionId = body.GetProperty("versionId").GetString()!;
        body.GetProperty("jobId").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("number").GetInt32().Should().Be(1);

        var peaks = await _client!.GetAsync($"{chat}/threads/{threadId}/versions/{versionId}/peaks?points=3");
        peaks.StatusCode.Should().Be(HttpStatusCode.OK, await peaks.Content.ReadAsStringAsync());
        (await Json(peaks)).GetProperty("peaks").GetArrayLength().Should().Be(3);
        _dsp.PeaksInput.Should().Be("TRIM:INTRO");

        var concat = await _client!.PostAsJsonAsync($"{chat}/concat", new
        {
            pieces = new object[] { new { threadId, versionId }, new { projectFile = "intro.mp3" } },
            joint = new { kind = "pause", seconds = 0.5 },
            name = "full.wav",
        });
        concat.StatusCode.Should().Be(HttpStatusCode.OK, await concat.Content.ReadAsStringAsync());
        _dsp.ConcatPieces.Select(p => Encoding.UTF8.GetString(p)).Should().Equal("TRIM:INTRO", "INTRO");
        _dsp.Joints.Should().Equal(AudioJoint.Pause(0.5));
    }

    [Fact]
    public async Task Без_ffmpeg_ручки_без_ИИ_отвечают_503_dsp_unavailable()
    {
        var chat = await Start(available: false);
        var threadId = await OpenIntro(chat);

        var responses = new[]
        {
            await _client!.PostAsJsonAsync($"{chat}/threads/{threadId}/edit", new { op = "normalize" }),
            await _client!.PostAsJsonAsync($"{chat}/threads/{threadId}/mix", new { stems = new[] { new { role = "stem:vocals" } } }),
            await _client!.GetAsync($"{chat}/threads/{threadId}/versions/origin/peaks"),
            await _client!.PostAsJsonAsync($"{chat}/concat", new
            {
                pieces = new object[] { new { projectFile = "intro.mp3" }, new { projectFile = "intro.mp3" } },
            }),
        };

        foreach (var resp in responses)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await resp.Content.ReadAsStringAsync());
            (await Json(resp)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.DspUnavailable);
        }
    }

    [Fact]
    public async Task Неизвестная_операция_и_устаревшая_ревизия()
    {
        var chat = await Start();
        var threadId = await OpenIntro(chat);

        (await _client!.PostAsJsonAsync($"{chat}/threads/{threadId}/edit", new { op = "reverse" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var stale = await _client!.PostAsJsonAsync($"{chat}/threads/{threadId}/edit", new { op = "trim", endSec = 1, revision = 0 });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(stale)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.RevisionConflict);
    }

    private sealed class FakeDsp : IAudioDsp
    {
        public bool Available { get; set; } = true;
        public string? PeaksInput { get; private set; }
        public IReadOnlyList<byte[]> ConcatPieces { get; private set; } = [];
        public IReadOnlyList<AudioJoint> Joints { get; private set; } = [];

        private static AudioDspOutput Out(string op, byte[] input, AudioFormat format) =>
            new(Encoding.UTF8.GetBytes(op + ":" + Encoding.UTF8.GetString(input)), format, null);

        public Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct, AudioDspInfo? known = null) =>
            Task.FromResult(Out("TRIM", audio, edit.Format));

        public Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs, AudioFormat format, CancellationToken ct,
            AudioDspInfo? known = null) =>
            Task.FromResult(Out("NORMALIZE", audio, format));

        public Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct) =>
            Task.FromResult(Out("MIX", [.. stems.SelectMany(s => s.Audio)], format));

        public Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct)
        {
            PeaksInput = Encoding.UTF8.GetString(audio);
            return Task.FromResult(new AudioPeaks([.. Enumerable.Repeat(0.5f, points)], 1, null));
        }

        public Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
            double? normalizeLufs, AudioFormat format, CancellationToken ct)
        {
            (ConcatPieces, Joints) = (pieces, joints);
            return Task.FromResult(new AudioDspOutput("CONCAT"u8.ToArray(), format, null));
        }

        public Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct) =>
            Task.FromResult(Out("CONVERT", audio, format));

        public Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct) => throw new NotSupportedException();
    }
}
