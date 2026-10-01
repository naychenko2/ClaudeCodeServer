using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.AudioEditor.Tests.Jobs;

// Склейка как операция модуля: куски — версии нитей чата и файлы проекта, итог — новая нить с версией 1.
// Сам звук — забота шва (FfmpegAudioDspTests), здесь шов подставной и запоминает, что ему передали
public sealed class AudioConcatServiceTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Session = "chat-1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-concat-" + Guid.NewGuid().ToString("N"));
    private readonly AudioThreadStore _store;
    private readonly AudioEditWorkspace _workspace;
    private readonly FakeDsp _dsp = new();
    private readonly Project _project;
    private readonly AudioEditScope _scope;
    private static readonly AudioEditScope Personal = new(AudioEditScope.Personal, null);

    public AudioConcatServiceTests()
    {
        _store = new AudioThreadStore(Path.Combine(_root, "threads"));
        _workspace = new AudioEditWorkspace(Path.Combine(_root, "work"));
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "sfx"));
        File.WriteAllBytes(Path.Combine(projectRoot, "sfx", "jingle.wav"), "JINGLE"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "secret.wav"), "SECRET"u8.ToArray());
        _project = new Project { Id = "p-1", RootPath = projectRoot };
        _scope = AudioEditScope.Of(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AudioConcatService Service(IAudioDsp? dsp = null, bool noDsp = false)
    {
        var feed = new Mock<IChatFeed>();
        feed.Setup(f => f.AppendRecordAsync(It.IsAny<string>(), It.IsAny<StoredModuleRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance, null, feed.Object);
        return new AudioConcatService(threads, _workspace, NullLogger<AudioConcatService>.Instance, noDsp ? null : dsp ?? _dsp);
    }

    // Нить-черновик с версией от задачи: файл main лежит в рабочей папке задачи
    private (string ThreadId, string VersionId) ThreadWithVersion(string content, string? license = null)
    {
        var thread = _store.Open(Owner, Session, null, "", null).Thread!;
        var jobId = Guid.NewGuid().ToString("N");
        var path = _workspace.SaveFile(Owner, jobId, 1, AudioFileRoles.Main, System.Text.Encoding.UTF8.GetBytes(content), ".wav");
        _store.AddLaunch(Owner, Session, thread.Id,
            new AudioThreadLaunch(jobId, null, _store.Now(), AudioThreadLaunchStatus.Running, "human", null, license));
        var done = _store.FinishLaunch(Owner, Session, thread.Id, jobId, AudioThreadLaunchStatus.Done,
            [(1, (IReadOnlyList<AudioVersionFile>)[new AudioVersionFile(AudioFileRoles.Main, path)])]);
        return (thread.Id, done.NewVersions[0].Id);
    }

    [Fact]
    public async Task Склейка_версии_нити_и_файла_проекта_заводит_новую_нить_с_версией_1()
    {
        var (threadId, versionId) = ThreadWithVersion("VOICE", license: "CC BY-NC 4.0");
        var before = _store.Get(Owner, Session).Threads.Single();

        var result = await Service().ConcatAsync(Owner, _scope, new AudioConcatInput(Session,
        [
            new AudioConcatPiece(ThreadId: threadId, VersionId: versionId),
            new AudioConcatPiece(ProjectFile: "sfx/jingle.wav"),
            new AudioConcatPiece(ThreadId: threadId),
        ], Joint: AudioJoint.Pause(0.5), Joints: [null, AudioJoint.Crossfade(1.5)], Name: "podcast-full.wav",
            Format: AudioFormat.Mp3, Folder: "sfx"), CancellationToken.None);

        result.Error.Should().BeNull();
        var dto = result.Value!;
        dto.Name.Should().Be("podcast-full.mp3");

        // Куски по порядку, стыки — общий и свой, громкость выравнивается по умолчанию
        _dsp.Pieces.Select(System.Text.Encoding.UTF8.GetString).Should().Equal("VOICE", "JINGLE", "VOICE");
        _dsp.Joints.Should().Equal(AudioJoint.Pause(0.5), AudioJoint.Crossfade(1.5));
        _dsp.Lufs.Should().Be(AudioDspLimits.ConcatLufs);
        _dsp.Format.Should().Be(AudioFormat.Mp3);

        var state = _store.Get(Owner, Session);
        state.Threads.Should().HaveCount(2);
        state.Threads.Single(t => t.Id == before.Id).Should().BeEquivalentTo(before, "исходная нить не меняется");
        var created = state.Threads.Single(t => t.Id == dto.ThreadId);
        state.Focus.Should().Be(created.Id);
        created.File.Should().BeNull();
        created.DraftFolder.Should().Be("sfx");
        created.Name.Should().Be("podcast-full.mp3");
        var version = created.Versions.Should().ContainSingle().Subject;
        version.Id.Should().Be(dto.VersionId);
        version.Number.Should().Be(1);
        version.JobId.Should().Be(dto.JobId);
        version.License.Should().Be("CC BY-NC 4.0");
        created.CurrentVersionId.Should().Be(version.Id);
        var saved = Path.Combine(_workspace.JobDir(Owner, dto.JobId), version.File(AudioFileRoles.Main)!.Path);
        File.ReadAllBytes(saved).Should().Equal(FakeDsp.Result);
        _store.ReferencedJobs(Owner).Should().Contain(dto.JobId, "рабочая папка не чистит файл склейки по TTL");
    }

    [Fact]
    public async Task Без_флажка_громкость_не_выравнивается_а_стык_по_умолчанию_встык()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var result = await Service().ConcatAsync(Owner, _scope, new AudioConcatInput(Session,
            [new AudioConcatPiece(threadId), new AudioConcatPiece(threadId)], NormalizeLoudness: false), CancellationToken.None);

        result.Error.Should().BeNull();
        _dsp.Lufs.Should().BeNull();
        _dsp.Joints.Should().Equal(AudioJoint.Butt);
    }

    [Fact]
    public async Task Меньше_двух_кусков_отказ_до_шва()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var result = await Service().ConcatAsync(Owner, _scope, new AudioConcatInput(Session, [new AudioConcatPiece(threadId)]),
            CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain("два куска");
        _dsp.Calls.Should().Be(0);
        _store.Get(Owner, Session).Threads.Should().HaveCount(1);
    }

    [Fact]
    public async Task Больше_двадцати_кусков_отказ()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var pieces = Enumerable.Range(0, AudioDspLimits.MaxConcatPieces + 1).Select(_ => new AudioConcatPiece(threadId)).ToList();
        var result = await Service().ConcatAsync(Owner, _scope, new AudioConcatInput(Session, pieces), CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        _dsp.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Файл_проекта_в_личной_области_отказ()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var result = await Service().ConcatAsync(Owner, Personal, new AudioConcatInput(Session,
            [new AudioConcatPiece(threadId), new AudioConcatPiece(ProjectFile: "sfx/jingle.wav")]), CancellationToken.None);

        result.ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        result.Error.Should().Contain(AudioConcatService.PersonalProjectFileText);
        _dsp.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Исходник_нити_в_личной_области_тоже_файл_проекта_отказ()
    {
        var origin = _store.Open(Owner, Session, "sfx/jingle.wav", null, null).Thread!;
        var (threadId, _) = ThreadWithVersion("A");
        var result = await Service().ConcatAsync(Owner, Personal, new AudioConcatInput(Session,
            [new AudioConcatPiece(threadId), new AudioConcatPiece(origin.Id, AudioThreadVersion.OriginId)]), CancellationToken.None);

        result.Error.Should().Contain(AudioConcatService.PersonalProjectFileText);
        _dsp.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("../secret.wav")]
    [InlineData("/etc/passwd")]
    [InlineData("sfx/нет-такого.wav")]
    public async Task Путь_вне_проекта_или_несуществующий_отказ(string file)
    {
        var (threadId, _) = ThreadWithVersion("A");
        var result = await Service().ConcatAsync(Owner, _scope, new AudioConcatInput(Session,
            [new AudioConcatPiece(threadId), new AudioConcatPiece(ProjectFile: file)]), CancellationToken.None);

        result.Error.Should().StartWith("Кусок 2:");
        _dsp.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Нить_чужого_чата_и_кусок_без_ссылки_отказ()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var svc = Service();
        (await svc.ConcatAsync(Owner, _scope, new AudioConcatInput("chat-2",
            [new AudioConcatPiece(threadId), new AudioConcatPiece(threadId)]), CancellationToken.None))
            .Error.Should().Contain("нить не найдена");
        (await svc.ConcatAsync(Owner, _scope, new AudioConcatInput(Session,
            [new AudioConcatPiece(threadId), new AudioConcatPiece(threadId, ProjectFile: "sfx/jingle.wav")]), CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        (await svc.ConcatAsync(Owner, _scope, new AudioConcatInput(Session,
            [new AudioConcatPiece(threadId), new AudioConcatPiece(threadId)], Name: "../x"), CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.InvalidRequest);
        _dsp.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Без_ffmpeg_dsp_unavailable()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var input = new AudioConcatInput(Session, [new AudioConcatPiece(threadId), new AudioConcatPiece(threadId)]);

        (await Service(noDsp: true).ConcatAsync(Owner, _scope, input, CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.DspUnavailable);
        (await Service(new FakeDsp { Available = false }).ConcatAsync(Owner, _scope, input, CancellationToken.None))
            .ErrorCode.Should().Be(AudioEditErrorCodes.DspUnavailable);
    }

    [Fact]
    public async Task Отказ_шва_нить_не_заводит()
    {
        var (threadId, _) = ThreadWithVersion("A");
        var result = await Service(new FakeDsp { Error = "Кусок 1 короче плавных переходов на его краях." })
            .ConcatAsync(Owner, _scope, new AudioConcatInput(Session, [new AudioConcatPiece(threadId), new AudioConcatPiece(threadId)]),
                CancellationToken.None);

        result.Error.Should().Contain("короче плавных переходов");
        _store.Get(Owner, Session).Threads.Should().HaveCount(1);
    }

    private sealed class FakeDsp : IAudioDsp
    {
        public static readonly byte[] Result = "CONCAT"u8.ToArray();

        public bool Available { get; init; } = true;
        public string? Error { get; init; }
        public int Calls { get; private set; }
        public IReadOnlyList<byte[]> Pieces { get; private set; } = [];
        public IReadOnlyList<AudioJoint> Joints { get; private set; } = [];
        public double? Lufs { get; private set; }
        public AudioFormat Format { get; private set; }

        public Task<AudioDspOutput> ConcatAsync(IReadOnlyList<byte[]> pieces, IReadOnlyList<AudioJoint> joints,
            double? normalizeLufs, AudioFormat format, CancellationToken ct)
        {
            Calls++;
            (Pieces, Joints, Lufs, Format) = (pieces, joints, normalizeLufs, format);
            return Task.FromResult(Error is null ? new AudioDspOutput(Result, format, null) : AudioDspOutput.Fail(Error));
        }

        public Task<AudioDspInfo?> ProbeAsync(byte[] audio, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioPeaks> PeaksAsync(byte[] audio, int points, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioDspOutput> TrimFadeGainAsync(byte[] audio, AudioEdit edit, CancellationToken ct) => throw new NotSupportedException();
        public Task<AudioDspOutput> NormalizeAsync(byte[] audio, double targetLufs, AudioFormat format, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AudioDspOutput> MixAsync(IReadOnlyList<AudioStem> stems, AudioFormat format, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<AudioDspOutput> ConvertAsync(byte[] audio, AudioFormat format, int? sampleRate, int? channels, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
