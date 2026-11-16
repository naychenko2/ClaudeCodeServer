using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Дефект Х-3 (ADR-040, решение 2): PersonalAudioEditorController.Save создавал корень «Личного» до проверки
// mode/folder/thread — отказ по неверному запросу оставлял папку на диске. Теперь корень создаётся хуком
// EnsurePersonalRootAsync на записи: отказ папки не оставляет, успешный путь пишет файл как раньше.
// Поставщик — фейк в памяти; «Личное» — фейковый стор с папкой внутри TempDir, чтобы видеть её отсутствие.
[Trait("Category", "Host")]
public class PersonalAudioSaveFolderTests : IClassFixture<PersonalAudioSaveFolderTests.Hosts>
{
    private static readonly byte[] Mp3 = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
    private static readonly byte[] Abc = "X:1\nK:C\nCDEF|"u8.ToArray();

    private readonly Hosts _hosts;

    public PersonalAudioSaveFolderTests(Hosts hosts) => _hosts = hosts;

    // Хост на КЛАСС: конфигурация одна — фейк-движок и фейковый «Личное». Чат, нить и версию
    // каждый тест заводит сам; на начало теста папки «Личного» нет
    public sealed class Hosts : IDisposable
    {
        public PersonalHost Home { get; } = new();

        public void Dispose() => Home.Dispose();
    }

    public sealed class PersonalHost : TestWebApplicationFactory
    {
        public FakePersonal Personal { get; }

        public PersonalHost()
        {
            Personal = new FakePersonal(Path.Combine(TempDir, "Personal"));
            ExtraServices = services =>
            {
                services.AddSingleton<IAudioEngine>(new FakeEngine());
                services.AddSingleton<IPersonalProjects>(Personal);
            };
        }

        // Сброс: на начало теста папки «Личного» нет
        public PersonalHost Use()
        {
            Personal.Reset();
            return this;
        }
    }

    public sealed class FakePersonal(string root) : IPersonalProjects
    {
        private Project? _project;

        public string Root => root;

        public void Reset()
        {
            _project = null;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        public Project? GetPersonalOf(string ownerId) => _project;

        public Task<Project> EnsurePersonalAsync(string ownerId, CancellationToken ct = default)
        {
            Directory.CreateDirectory(root);
            return Task.FromResult(_project ??= new Project
                { Id = "personal-id", OwnerId = ownerId, RootPath = root, Kind = ProjectKind.Personal });
        }

        public Project? GetByIdIncludingPersonal(string id) => null;

        public IReadOnlyCollection<Project> GetAllPersonal() => _project is null ? [] : [_project];

        public bool DeleteOf(string ownerId) => false;
    }

    // Озвучка даёт звук и партитуру — два файла версии в рабочей папке задачи
    private sealed class FakeEngine : IAudioEngine
    {
        private static readonly AudioModelInfo Model = new("fake-model", "Фейк",
            new AudioCaps([AudioOp.Speak], ["ru"], [], [AudioOutputs.Audio], AudioLicenses.Mit, AudioPriceUnits.Free),
            new AudioPriceHint(0, AudioPriceUnits.Free, "run"));

        public string Key => "fake";
        public string Label => "Фейк";
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;
        public IReadOnlyList<AudioModelInfo> Models => [Model];

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct) =>
            Task.FromResult(new AudioResult(AudioOutcome.Ok,
                [new("main", Mp3, "audio/mpeg", ".mp3"), new("score", Abc, "text/plain", ".abc")],
                new AudioCost(0, AudioPriceUnits.Free), false, null, null));

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private string _ownerId = "";

    private void Start()
    {
        _factory = _hosts.Home.Use();
        _ownerId = _factory.Services.GetRequiredService<UserStore>().FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        _client = _factory.CreateAuthenticatedClient();
    }

    private SessionManager Sessions => _factory.Services.GetRequiredService<SessionManager>();

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    // Личный чат → черновик → озвучка; возвращает url чата, нить и id новой версии.
    // Предусловие всех тестов: сам по себе этот путь «Личного» не создаёт (ADR-040, решение 2)
    private async Task<(string ChatUrl, string ThreadId, string VersionId)> PersonalDraft()
    {
        var chat = await Sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный");
        var chatUrl = $"/api/audio-editor/chats/{chat.Id}";

        var opened = await _client.PostAsJsonAsync($"{chatUrl}/threads", new { draftFolder = "", revision = 0 });
        opened.StatusCode.Should().Be(HttpStatusCode.OK, await opened.Content.ReadAsStringAsync());
        var threadId = (await Json(opened)).GetProperty("focus").GetString()!;

        var quote = await _client.PostAsJsonAsync($"{chatUrl}/quote",
            new { mode = "voice", operation = "speak", provider = "fake", model = "fake-model", count = 1, text = "Привет" });
        quote.StatusCode.Should().Be(HttpStatusCode.OK, await quote.Content.ReadAsStringAsync());
        using var form = new MultipartFormDataContent
        {
            { new StringContent((await Json(quote)).GetProperty("quoteId").GetString()!), "quoteId" },
            { new StringContent(chat.Id), "sessionId" },
            { new StringContent(threadId), "threadId" },
            { new StringContent("Привет"), "text" },
        };
        var started = await _client.PostAsync($"{chatUrl}/jobs", form);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var jobId = (await Json(started)).GetProperty("jobId").GetString()!;
        await _factory.Services.GetRequiredService<AudioEditJobService>().WhenDone(jobId);

        Directory.Exists(_hosts.Home.Personal.Root).Should().BeFalse("чат, черновик и задача «Личного» не создают");
        var versionId = _factory.Services.GetRequiredService<AudioJobThreads>().Store
            .Get(_ownerId, chat.Id).Threads.Single(t => t.Id == threadId).Versions.Single(v => v.JobId == jobId).Id;
        return (chatUrl, threadId, versionId);
    }

    // Приёмка c1: отказ по mode или folder — 400 и папки «Личного» на диске нет
    [Theory]
    [InlineData("неизвестный режим", "не-режим", null)]
    [InlineData("папка чужого вида", null, "images")]
    [InlineData("выход за корень", null, "../etc")]
    public async Task Плохой_mode_или_folder_отказаны_Личное_не_создано(string case_, string? mode, string? folder)
    {
        Start();
        var (chatUrl, threadId, versionId) = await PersonalDraft();

        var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save", new { versionId, mode, folder });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, case_);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.InvalidRequest, case_);
        Directory.Exists(_hosts.Home.Personal.Root).Should().BeFalse(case_ + ": отказ не оставляет папки");
    }

    // Приёмка c1: отказ по несуществующей нити — 404 и папки тоже нет
    [Fact]
    public async Task Неизвестная_нить_отказана_Личное_не_создано()
    {
        Start();
        var (chatUrl, _, versionId) = await PersonalDraft();

        var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads/нет-нити/save", new { versionId });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(resp)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.ThreadNotFound);
        Directory.Exists(_hosts.Home.Personal.Root).Should().BeFalse("отказ по нити — тоже до создания корня");
    }

    // Приёмка c2: успешное сохранение создаёт «Личное» и кладёт все файлы версии в audio/
    [Fact]
    public async Task Успешное_сохранение_в_личный_чат_создаёт_Личное_и_кладёт_файлы_в_audio()
    {
        Start();
        var (chatUrl, threadId, versionId) = await PersonalDraft();

        var resp = await _client.PostAsJsonAsync($"{chatUrl}/threads/{threadId}/save", new { versionId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var root = _hosts.Home.Personal.Root;
        Directory.Exists(root).Should().BeTrue();
        var body = await Json(resp);
        body.GetProperty("path").GetString().Should().Be("audio/audio.mp3");
        body.GetProperty("files").EnumerateArray().Select(f => f.GetString())
            .Should().BeEquivalentTo("audio/audio.mp3", "audio/audio.abc");
        File.ReadAllBytes(Path.Combine(root, "audio", "audio.mp3")).Should().Equal(Mp3);
        File.ReadAllBytes(Path.Combine(root, "audio", "audio.abc")).Should().Equal(Abc);
    }
}
