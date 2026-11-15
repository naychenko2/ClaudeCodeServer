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
/// «Работать с этим» в личной области (дефект e3b442e5): OpenIn личного чата берёт звуковой файл
/// из корня «Личного» и заводит по нему нить; отказ называет папку «Личное», а не «проект».
/// Ручки вызываются напрямую (как в PersonalAudioWritePathTests): HTTP-слой здесь не проверяется,
/// проверяется, что делает сам контроллер с областью и путём файла.
/// </summary>
public sealed class PersonalAudioOpenInTests : IDisposable
{
    private const string Owner = "owner-1";
    private const string Chat = "chat-personal";
    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 7, 7];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aud-open-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _root;
    private readonly AudioThreadStore _store;
    private readonly AudioEditWorkspace _workspace;
    private readonly FakePersonal _personal;
    private readonly List<IDisposable> _disposable = [];

    public PersonalAudioOpenInTests()
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

    // «Личное» на диске с звуковым файлом: тот же путь, что кладёт панель «Звук из Личного»
    private async Task SeedPersonalFileAsync()
    {
        await _personal.EnsurePersonalAsync(Owner);
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        File.WriteAllBytes(Path.Combine(_root, "audio", "qa-tone.wav"), Wav);
    }

    private static ObjectResult Denied(IActionResult? result) => result.Should().BeOfType<ObjectResult>().Subject;

    [Fact]
    public async Task Личный_OpenIn_по_файлу_Личного_заводит_нить()
    {
        await SeedPersonalFileAsync();

        var result = await Controller().Open(Chat, new AudioThreadOpenRequest("audio/qa-tone.wav", null, null, 0), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var state = _store.Get(Owner, Chat);
        state.Threads.Should().ContainSingle(t => t.File == "audio/qa-tone.wav",
            "файл «Личного» становится нитью: путь относительный, как его видит панель");
    }

    [Fact]
    public async Task Личный_отказ_по_файлу_называет_Личное_а_не_проект()
    {
        await SeedPersonalFileAsync();

        var denied = Denied(await Controller().Open(Chat,
            new AudioThreadOpenRequest("audio/нет-такой.wav", null, null, 0), default));

        denied.StatusCode.Should().Be(400);
        denied.Value!.ToString()!.Should().Contain("«Личном»", "в личном чате нет проекта: текст обязан называть папку");
        denied.Value!.ToString()!.Should().NotContain("проекте");
    }

    [Fact]
    public async Task Личный_без_папки_Личного_отказывает_по_Личному_и_папку_не_создаёт()
    {
        var denied = Denied(await Controller().Open(Chat,
            new AudioThreadOpenRequest("audio/qa-tone.wav", null, null, 0), default));

        denied.StatusCode.Should().Be(400);
        denied.Value!.ToString()!.Should().Contain("«Личном»");
        Directory.Exists(_root).Should().BeFalse("чтение не создаёт папку: читать нечего — отказ до диска");
    }

    [Fact]
    public async Task Чужой_пользователь_личной_нить_не_открывает()
    {
        await SeedPersonalFileAsync();
        var controller = Controller();
        controller.ControllerContext = AsUser("stranger-9");

        var denied = Denied(await controller.Open(Chat, new AudioThreadOpenRequest("audio/qa-tone.wav", null, null, 0), default));

        denied.StatusCode.Should().Be(404, "чужой личный чат неотличим от «нет такого чата», как у PersonalFilesController");
        _store.Get(Owner, Chat).Threads.Should().BeEmpty("отказ не заводит нить у владельца");
    }
}
