using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.AudioEditor.Tests.Engines;

// Правки без ИИ как версии нити: каждая операция — новая версия с собственным jobId, файл — в папке
// этой задачи; сведение берёт только перечисленные и не выключенные стемы; склейка принимает такую
// версию. Сам звук — забота шва (FfmpegAudioDspTests), здесь шов подставной и запоминает вызовы
public sealed class DspAudioEngineTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Session = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-dsp-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly AudioEditWorkspace _workspace;
    private readonly AudioJobThreads _threads;
    private readonly FakeDsp _dsp = new();
    private readonly AudioEditScope _scope;

    public DspAudioEngineTests()
    {
        _store = new AudioThreadStore(Path.Combine(_root, "threads"));
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
        _threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllBytes(Path.Combine(projectRoot, "song.mp3"), "SONG"u8.ToArray());
        _scope = AudioEditScope.Of(new Project { Id = "p-1", RootPath = projectRoot });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DspAudioEngine Engine(IAudioDsp? dsp = null, bool noDsp = false) =>
        new(_threads, _workspace, NullLogger<DspAudioEngine>.Instance, noDsp ? null : dsp ?? _dsp);

    private string OpenSong() => _store.Open(Owner, Session, "song.mp3", null, null).Thread!.Id;

    // Версия запуска со стемами vocals, drums, bass в рабочей папке задачи
    private (string ThreadId, string VersionId) ThreadWithStems(string? license = null)
    {
        var threadId = OpenSong();
        var jobId = Guid.NewGuid().ToString("N");
        IReadOnlyList<AudioVersionFile> files = [.. new[] { "vocals", "drums", "bass" }.Select(name =>
            new AudioVersionFile(AudioFileRoles.Stem(name),
                _workspace.SaveFile(Owner, jobId, 1, AudioFileRoles.Stem(name), Encoding.UTF8.GetBytes(name.ToUpperInvariant()), ".wav")))];
        _store.AddLaunch(Owner, Session, threadId,
            new AudioThreadLaunch(jobId, AudioThreadVersion.OriginId, _store.Now(), AudioThreadLaunchStatus.Running, "human", null, license));
        var done = _store.FinishLaunch(Owner, Session, threadId, jobId, AudioThreadLaunchStatus.Done, [(1, files)]);
        return (threadId, done.NewVersions[0].Id);
    }

    private string FileOf(AudioThreadVersion version) =>
        Path.Combine(_workspace.JobDir(Owner, version.JobId!), version.File(AudioFileRoles.Main)!.Path);

    [Fact]
    public async Task Обрезка_даёт_новую_версию_с_jobId_и_файлом_в_папке_задачи()
    {
        var threadId = OpenSong();

        var result = await Engine().EditAsync(Owner, _scope,
            new AudioDspEditInput(Session, threadId, AudioDspEditOp.Trim, StartSec: 1, EndSec: 3), CancellationToken.None);

        result.ErrorCode.Should().BeNull(result.Error);
        var thread = _store.Get(Owner, Session).Threads.Single();
        // Версия, а не шаг: в нити исходник и новая версия 1, текущая — она
        thread.Versions.Should().HaveCount(2);
        var version = thread.Versions[1];
        version.Id.Should().Be(result.Value!.VersionId);
        version.Number.Should().Be(1);
        version.JobId.Should().Be(result.Value.JobId).And.NotBeNullOrEmpty();
        version.BaseVersionId.Should().Be(AudioThreadVersion.OriginId);
        thread.CurrentVersionId.Should().Be(version.Id);

        File.ReadAllText(FileOf(version)).Should().Be("TRIM:SONG");
        Path.GetExtension(FileOf(version)).Should().Be(".mp3", "формат по умолчанию — как у исходника");
        _dsp.Edit.Should().Be(new AudioEdit(1, 3, Format: AudioFormat.Mp3));
        // Чистка по TTL держит папку правки, пока жива нить
        _store.ReferencedJobs(Owner).Should().Contain(version.JobId!);
        thread.Launches.Should().BeEmpty("правка без ИИ — не запуск");
        _store.Get(Owner, Session).Events.Should().Contain(e => e.Kind == AudioThreadEventKinds.Edited && e.JobId == version.JobId);
    }

    [Fact]
    public async Task Правка_от_правки_идёт_от_основы_и_наследует_лицензию()
    {
        var (threadId, stemsVersion) = ThreadWithStems(license: "CC BY-NC 4.0");
        var mixed = await Engine().MixAsync(Owner, _scope,
            new AudioMixInput(Session, threadId, [new("stem:vocals")]), CancellationToken.None);

        var normalized = await Engine().EditAsync(Owner, _scope,
            new AudioDspEditInput(Session, threadId, AudioDspEditOp.Normalize, Format: AudioFormat.Flac), CancellationToken.None);

        normalized.ErrorCode.Should().BeNull(normalized.Error);
        var thread = _store.Get(Owner, Session).Threads.Single();
        var version = thread.Version(normalized.Value!.VersionId)!;
        version.BaseVersionId.Should().Be(mixed.Value!.VersionId);
        version.License.Should().Be("CC BY-NC 4.0");
        version.JobId.Should().NotBe(mixed.Value.JobId);
        _dsp.Lufs.Should().Be(AudioDspLimits.DefaultLufs);
        File.ReadAllText(FileOf(version)).Should().Be("NORMALIZE:MIX:VOCALS");
        Path.GetExtension(FileOf(version)).Should().Be(".flac");
        thread.Version(stemsVersion).Should().NotBeNull("старые версии остаются");
    }

    [Fact]
    public async Task Сведение_берёт_только_перечисленные_и_невыключенные_стемы()
    {
        var (threadId, stemsVersion) = ThreadWithStems();

        var result = await Engine().MixAsync(Owner, _scope, new AudioMixInput(Session, threadId,
        [
            new("stem:vocals", GainDb: -3),
            new("stem:drums", Muted: true),
        ], BaseVersionId: stemsVersion), CancellationToken.None);

        result.ErrorCode.Should().BeNull(result.Error);
        // bass не перечислен, drums выключен — в ffmpeg идёт один голос со своей громкостью
        _dsp.Stems.Should().ContainSingle();
        Encoding.UTF8.GetString(_dsp.Stems[0].Audio).Should().Be("VOCALS");
        _dsp.Stems[0].GainDb.Should().Be(-3);

        var version = _store.Get(Owner, Session).Threads.Single().Version(result.Value!.VersionId)!;
        version.BaseVersionId.Should().Be(stemsVersion);
        version.JobId.Should().Be(result.Value.JobId);
        version.Files.Should().ContainSingle().Which.Role.Should().Be(AudioFileRoles.Main);
        File.ReadAllText(FileOf(version)).Should().Be("MIX:VOCALS");
    }

    [Fact]
    public async Task Сведение_без_звучащих_или_с_чужим_стемом_отказ_без_версии()
    {
        var (threadId, _) = ThreadWithStems();

        var allMuted = await Engine().MixAsync(Owner, _scope,
            new AudioMixInput(Session, threadId, [new("stem:vocals", Muted: true)]), CancellationToken.None);
        var missing = await Engine().MixAsync(Owner, _scope,
            new AudioMixInput(Session, threadId, [new("stem:piano")]), CancellationToken.None);

        allMuted.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        missing.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        missing.Error.Should().Contain("stem:piano");
        _store.Get(Owner, Session).Threads.Single().Versions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Пики_версии_строятся_по_её_файлу()
    {
        var threadId = OpenSong();

        var result = await Engine().PeaksAsync(Owner, _scope, Session, threadId, AudioThreadVersion.OriginId, null, 4,
            CancellationToken.None);

        result.ErrorCode.Should().BeNull(result.Error);
        result.Value!.Peaks.Should().Equal(0.1f, 0.5f, 1f, 0.2f);
        result.Value.Seconds.Should().Be(12.5);
        _dsp.PeaksInput.Should().Be("SONG");
        _dsp.Points.Should().Be(4);
    }

    [Fact]
    public async Task Без_шва_или_ffmpeg_все_операции_dsp_unavailable()
    {
        var threadId = OpenSong();
        foreach (var engine in new[] { Engine(noDsp: true), Engine(new FakeDsp { Available = false }) })
        {
            (await engine.EditAsync(Owner, _scope, new AudioDspEditInput(Session, threadId, AudioDspEditOp.Trim, EndSec: 1),
                CancellationToken.None)).ErrorCode.Should().Be(AudioEditErrorCodes.DspUnavailable);
            (await engine.MixAsync(Owner, _scope, new AudioMixInput(Session, threadId, [new("stem:vocals")]),
                CancellationToken.None)).ErrorCode.Should().Be(AudioEditErrorCodes.DspUnavailable);
            (await engine.PeaksAsync(Owner, _scope, Session, threadId, AudioThreadVersion.OriginId, null, 10,
                CancellationToken.None)).ErrorCode.Should().Be(AudioEditErrorCodes.DspUnavailable);
        }
        _store.Get(Owner, Session).Threads.Single().Versions.Should().ContainSingle();
    }

    [Fact]
    public async Task Устаревшая_ревизия_конфликт_до_ffmpeg()
    {
        var threadId = OpenSong();
        var stale = _store.Get(Owner, Session).Revision - 1;

        var result = await Engine().EditAsync(Owner, _scope,
            new AudioDspEditInput(Session, threadId, AudioDspEditOp.Trim, EndSec: 1, Revision: stale), CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.RevisionConflict);
        _dsp.Edit.Should().BeNull();
    }

    [Fact]
    public async Task Чужая_нить_и_исходник_в_личной_области_отказ()
    {
        var threadId = OpenSong();

        (await Engine().EditAsync("someone-else", _scope, new AudioDspEditInput(Session, threadId, AudioDspEditOp.Trim, EndSec: 1),
            CancellationToken.None)).ErrorCode.Should().Be(AudioEditErrorCodes.ThreadNotFound);
        (await Engine().EditAsync(Owner, new AudioEditScope(AudioEditScope.Personal, null),
            new AudioDspEditInput(Session, threadId, AudioDspEditOp.Trim, EndSec: 1), CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.FileNotFound);
        _dsp.Edit.Should().BeNull();
    }

    [Fact]
    public async Task Склейка_принимает_версию_правки_без_ИИ()
    {
        var threadId = OpenSong();
        var edited = await Engine().EditAsync(Owner, _scope,
            new AudioDspEditInput(Session, threadId, AudioDspEditOp.GainFade, FadeInSec: 0.5, GainDb: 2), CancellationToken.None);
        edited.ErrorCode.Should().BeNull(edited.Error);

        var concat = await new AudioConcatService(_threads, _workspace, NullLogger<AudioConcatService>.Instance, _dsp)
            .ConcatAsync(Owner, _scope, new AudioConcatInput(Session,
            [
                new AudioConcatPiece(ThreadId: threadId, VersionId: edited.Value!.VersionId),
                new AudioConcatPiece(ThreadId: threadId, VersionId: AudioThreadVersion.OriginId),
            ]), CancellationToken.None);

        concat.ErrorCode.Should().BeNull(concat.Error);
        _dsp.ConcatPieces.Select(p => Encoding.UTF8.GetString(p)).Should().Equal("GAIN:SONG", "SONG");
    }

    private sealed class FakeDsp : IAudioDsp
    {
        public bool Available { get; init; } = true;
        public AudioEdit? Edit { get; private set; }
        public double? Lufs { get; private set; }
        public IReadOnlyList<AudioStem> Stems { get; private set; } = [];
        public IReadOnlyList<byte[]> ConcatPieces { get; private set; } = [];
        public string? PeaksInput { get; private set; }
        public int Points { get; private set; }

        private static AudioDspOutput Out(string op, byte[] input, AudioFormat format) =>
            new(Encoding.UTF8.GetBytes(op + ":" + Encoding.UTF8.GetString(input)), format, null);

        public Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct, AudioDspInfo? known = null)
        {
            Edit = edit;
            return Task.FromResult(Out(edit.StartSeconds is null && edit.EndSeconds is null ? "GAIN" : "TRIM", audio, edit.Format));
        }

        public Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs, AudioFormat format, CancellationToken ct,
            AudioDspInfo? known = null)
        {
            Lufs = targetLufs;
            return Task.FromResult(Out("NORMALIZE", audio, format));
        }

        public Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct)
        {
            Stems = stems;
            return Task.FromResult(Out("MIX", [.. stems.SelectMany(s => s.Audio)], format));
        }

        public Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct)
        {
            (PeaksInput, Points) = (Encoding.UTF8.GetString(audio), points);
            return Task.FromResult(new AudioPeaks([0.1f, 0.5f, 1f, 0.2f], 12.5, null));
        }

        public Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
            double? normalizeLufs, AudioFormat format, CancellationToken ct)
        {
            ConcatPieces = pieces;
            return Task.FromResult(new AudioDspOutput("CONCAT"u8.ToArray(), format, null));
        }

        public Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct) =>
            Task.FromResult(Out("CONVERT", audio, format));

        public Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct) => throw new NotSupportedException();
    }
}
