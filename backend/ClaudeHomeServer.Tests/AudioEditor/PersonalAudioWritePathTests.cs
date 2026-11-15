using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Engines;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Media;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.AudioEditor;

/// <summary>
/// Пишущие пути «Звука» в личной области (ADR-040 §1.2, §5). Папка «Личного» создаётся только там,
/// где её просит допустимый вход: запуск со списком файлов и склейка с негодным путём отвечают,
/// не оставив папки. Сохранение версии: «Сохранить как» и черновик идут в папку вида (audio/),
/// «следующая версия» нити по файлу — рядом с файлом.
/// Ручки вызываются напрямую (как в SharedProjectEditorScopeGatesTests): HTTP-слой и гейт чата
/// здесь не проверяются — проверяется, что делает сам контроллер с папкой и путём записи.
/// </summary>
public sealed class PersonalAudioWritePathTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-personal";
    private const string Job = "job-1";
    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 7, 7];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aud-write-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly AudioThreadStore _store;
    private readonly AudioEditWorkspace _workspace;
    private readonly FakePersonal _personal;
    private readonly List<IDisposable> _disposable = [];

    public PersonalAudioWritePathTests()
    {
        _root = Path.Combine(_dir, "Personal");
        _store = new AudioThreadStore(Path.Combine(_dir, AudioThreadStore.DirName));
        _workspace = new AudioEditWorkspace(Path.Combine(_dir, "work"));
        _personal = new FakePersonal(_root);
    }

    public void Dispose()
    {
        foreach (var d in _disposable) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // «Личное» с созданием папки на Ensure: отсутствие папки видно как отсутствие папки
    private sealed class FakePersonal(string root) : IPersonalProjects
    {
        private Project? _project;

        public Project? GetPersonalOf(string ownerId) => _project;

        public Task<Project> EnsurePersonalAsync(string ownerId, CancellationToken ct = default)
        {
            Directory.CreateDirectory(root);
            return Task.FromResult(_project ??= new Project
                { Id = "personal", OwnerId = ownerId, RootPath = root, Kind = ProjectKind.Personal });
        }

        public Project? GetByIdIncludingPersonal(string id) => null;

        public IReadOnlyCollection<Project> GetAllPersonal() => _project is null ? [] : [_project];

        public bool DeleteOf(string ownerId) => false;
    }

    private PersonalAudioEditorController Controller()
    {
        var directory = new Mock<ISessionDirectory>();
        directory.Setup(d => d.GetById(Chat)).Returns(new Session { Id = Chat, OwnerId = Owner });
        var prefs = new AudioPrefsService(new AudioPrefsStore(Path.Combine(_dir, AudioPrefsStore.DirName)));
        var threads = new AudioJobThreads(_store, NullLogger<AudioJobThreads>.Instance);
        var jobs = new AudioEditJobService([], _workspace, NullLogger<AudioEditJobService>.Instance, threads, prefs);
        _disposable.Add(jobs);
        // ffmpeg в этом мире есть: отказ склейки должен дойти до разбора кусков, а не упереться в DspUnavailable
        var dsp = new Mock<IAudioDsp>();
        dsp.Setup(d => d.Available).Returns(true);
        var concat = new AudioConcatService(threads, _workspace, NullLogger<AudioConcatService>.Instance, dsp.Object);
        var engine = new DspAudioEngine(threads, _workspace, NullLogger<DspAudioEngine>.Instance, dsp.Object);
        var gate = new AudioEditScopeGate(Mock.Of<IProjectManager>(), directory.Object, Mock.Of<IProjectAccess>(),
            new PersonalFolderResolver(_personal));
        return new PersonalAudioEditorController(gate, [], jobs, threads, prefs, _workspace, engine, concat)
        { ControllerContext = AsUser(Owner) };
    }

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) },
    };

    // Нить (по файлу или черновик) с версией, чей главный файл лежит в рабочей папке задачи
    private string OpenThreadWithVersion(string? file)
    {
        var threadId = _store.Open(Owner, Chat, file, file is null ? "" : null, null).Thread!.Id;
        var rel = _workspace.SaveFile(Owner, Job, 1, AudioFileRoles.Main, Wav, ".wav");
        _store.AddEditVersion(Owner, Chat, threadId, [new AudioVersionFile(AudioFileRoles.Main, rel)], null, jobId: Job);
        return threadId;
    }

    private static void Rejected(IActionResult? result)
    {
        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        new[] { 400, 404 }.Should().Contain(obj.StatusCode.GetValueOrDefault(),
            "отказ по входу, а не пропуск в личную область");
    }

    private static AudioProjectSaver.Saved OkSaved(IActionResult? result) =>
        result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<AudioProjectSaver.Saved>().Subject;

    // ── Ленивое создание: только по допустимому пути ─────────────────────────────

    [Fact]
    public async Task Запуск_с_негодным_путём_отказывается_и_папку_Личного_не_создаёт()
    {
        var result = await Controller().Start(Chat, new AudioStartJobForm
        {
            SessionId = Chat,
            Text = "привет",
            ClipPaths = ["audio/../x.wav"],
        }, default);

        Rejected(result);
        Directory.Exists(_root).Should().BeFalse("отказ по пути случился до ленивого создания папки");
    }

    [Fact]
    public async Task Склейка_с_негодным_куском_отказывается_и_папку_Личного_не_создаёт()
    {
        var result = await Controller().Concat(Chat, new AudioConcatRequest(
            [new AudioConcatPiece(ProjectFile: "audio/ok.wav"), new AudioConcatPiece(ProjectFile: "audio/../x.wav")]),
            default);

        Rejected(result);
        Directory.Exists(_root).Should().BeFalse("один негодный путь не даёт создать папку ради остальных");
    }

    // Положительная половина: с допустимыми путями ленивое создание доходит до дела —
    // выше отказ был именно по пути, а не по устройству ручки
    [Fact]
    public async Task Запуск_с_допустимым_путём_создаёт_Личное_прежде_чем_отказать_по_файлу()
    {
        var result = await Controller().Start(Chat, new AudioStartJobForm
        {
            SessionId = Chat,
            Text = "привет",
            ReferencePath = "audio/model.rvc",
        }, default);

        Rejected(result);
        Directory.Exists(_root).Should().BeTrue("допустимый вход — тот, ради которого папку и создают");
    }

    // ── Сохранение версии: куда именно пишется ───────────────────────────────────

    [Fact]
    public async Task Сохранить_как_нити_по_файлу_идёт_в_папку_вида_а_не_рядом_с_файлом()
    {
        var threadId = OpenThreadWithVersion("notes/old.wav");

        var saved = OkSaved(await Controller().Save(Chat, threadId,
            new AudioSaveRequest(null, AudioProjectSaver.As, null, "new"), default));

        saved.Path.Should().Be("audio/new.wav",
            "без папки вида по умолчанию «Сохранить как» ушло бы рядом с файлом нити");
        File.Exists(Path.Combine(_root, "audio", "new.wav")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "notes", "new.wav")).Should().BeFalse();
    }

    [Fact]
    public async Task Сохранить_как_с_выбранной_папкой_идёт_в_неё_даже_у_нити_по_файлу()
    {
        var threadId = OpenThreadWithVersion("notes/old.wav");

        var saved = OkSaved(await Controller().Save(Chat, threadId,
            new AudioSaveRequest(null, AudioProjectSaver.As, "audio/sub", "new"), default));

        saved.Path.Should().Be("audio/sub/new.wav", "выбор папки у нити по файлу не теряется");
        File.Exists(Path.Combine(_root, "audio", "sub", "new.wav")).Should().BeTrue();
    }

    [Fact]
    public async Task Следующая_версия_черновика_Личного_идёт_в_папку_вида()
    {
        var threadId = OpenThreadWithVersion(null);

        var saved = OkSaved(await Controller().Save(Chat, threadId,
            new AudioSaveRequest(null, AudioProjectSaver.NextVersion, null, null), default));

        saved.Path.Should().Be("audio/audio.wav", "черновику личной области некуда идти «рядом с файлом» — только в audio/");
    }
}
