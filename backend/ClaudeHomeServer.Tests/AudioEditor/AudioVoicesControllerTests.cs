using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using ClaudeHomeServer.Services.AudioEditor.Voices;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.AudioEditor;

// Ручки библиотеки «Голоса» на собранном приложении (ADR-021 §2): CRUD по voices/<slug>/ проекта,
// образцы из загрузки и из файлов проекта, модель RVC из версии нити, изоляция владельцев, отказ
// локальному проекту до диска и пустое состояние личного чата.
[Trait("Category", "Host")]
public class AudioVoicesControllerTests : IClassFixture<AudioVoicesControllerTests.Hosts>
{
    private static readonly byte[] Wav = [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, 7, 7];

    private readonly Hosts _hosts;

    public AudioVoicesControllerTests(Hosts hosts) => _hosts = hosts;

    // Хост на КЛАСС: конфигурация одна — клон-фейк как IAudioEngine. Проект, образцы и нити
    // каждый тест заводит свои в собственном каталоге TempDir; счётчик фейка сбрасывает Use()
    public sealed class Hosts : IDisposable
    {
        public MiniMaxHost MiniMax { get; } = new();

        public void Dispose() => MiniMax.Dispose();
    }

    public sealed class MiniMaxHost : TestWebApplicationFactory
    {
        public MiniMaxCloner Cloner { get; } = new();

        // «Личное» — в памяти, папка внутри TempDir: боевой стор требует домашней папки пользователя
        public FakePersonal Personal { get; }

        public MiniMaxHost()
        {
            Personal = new FakePersonal(Path.Combine(TempDir, "Personal"));
            ExtraServices = services =>
            {
                services.AddSingleton<IAudioEngine>(Cloner);
                services.AddSingleton<IPersonalProjects>(Personal);
            };
        }

        // Чистый счётчик фейка и нет папки «Личного» на начало теста
        public MiniMaxHost Use()
        {
            Cloner.Reset();
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

    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private string _ownerId = "", _projectId = "", _projectRoot = "";

    private void Start()
    {
        _factory = _hosts.MiniMax.Use();
        var users = _factory.Services.GetRequiredService<UserStore>();
        var user = users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        _ownerId = user.Id;
        _projectRoot = Path.Combine(_factory.TempDir, "voices_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectRoot);
        _projectId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("v-" + Guid.NewGuid().ToString("N")[..6], _projectRoot, user.Id, user.Username).Id;
        File.WriteAllBytes(Path.Combine(_projectRoot, "anya.wav"), Wav);
        _client = _factory.CreateAuthenticatedClient();
    }

    // Поставщик-подставка: единственная модель сама создаёт клон MiniMax; считает прогоны
    public sealed class MiniMaxCloner : IAudioEngine
    {
        private int _runs;
        public int Runs => _runs;
        public string Key => "fake-clone";
        public string Label => "Фейк-клон";
        public string PriceUnit => AudioPriceUnits.Free;
        public bool Enabled => true;

        public IReadOnlyList<AudioModelInfo> Models =>
        [
            new("fake-mm-clone", "Клон", new AudioCaps([AudioOp.CloneVoice], ["ru"], [AudioVoiceKind.Clone], [AudioOutputs.Audio],
                AudioLicenses.Commercial, AudioPriceUnits.Free), new AudioPriceHint(0, AudioPriceUnits.Free, "run")),
        ];

        public string? LibraryVoicesRefusal => null;
        public string? LibraryVoiceRefusal(AudioModelInfo model, AudioOp op, AudioVoiceUse voice) => null;
        public (string Key, bool Creates)? StoredClone(AudioModelInfo model, AudioOp op) => (VoiceProviders.MiniMax, true);

        public void Reset() => _runs = 0;

        public Task<AudioResult> RunAsync(AudioRequest req, IProgress<AudioProgress> progress, CancellationToken ct)
        {
            Interlocked.Increment(ref _runs);
            return Task.FromResult(new AudioResult(AudioOutcome.Ok, [new AudioFile("main", [1], "audio/wav", ".wav")], null, false,
                null, null, [new AudioVoiceCacheEntry(VoiceProviders.MiniMax, "mm-fresh")]));
        }

        public Task<bool> CancelRemoteAsync(string remoteId, CancellationToken ct) => Task.FromResult(false);
    }

    private string Voices => $"/api/projects/{_projectId}/audio-editor/voices";

    private static async Task<JsonElement> Json(HttpResponseMessage resp) =>
        JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

    private static MultipartFormDataContent Form(string? name, string? transcript = null, string? projectFile = null, bool upload = true)
    {
        var form = new MultipartFormDataContent();
        if (name is not null) form.Add(new StringContent(name), "name");
        if (transcript is not null) form.Add(new StringContent(transcript), "transcript");
        if (projectFile is not null) form.Add(new StringContent(projectFile), "projectFiles");
        if (upload)
        {
            var file = new ByteArrayContent(Wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "files", "мой голос.wav");
        }
        return form;
    }

    private async Task<string> CreateVoice(string name = "Аня")
    {
        var resp = await _client.PostAsync(Voices, Form(name, "Привет", projectFile: "anya.wav"));
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await Json(resp)).GetProperty("slug").GetString()!;
    }

    [Fact]
    public async Task Crud_ИзЗагрузкиИФайлаПроекта()
    {
        Start();
        var slug = await CreateVoice();
        var dir = Path.Combine(_projectRoot, "voices", slug);
        Directory.GetFiles(dir).Select(Path.GetFileName).Should()
            .BeEquivalentTo("voice.json", "sample-01.wav", "sample-02.wav");

        var list = await Json(await _client.GetAsync(Voices));
        list.GetProperty("available").GetBoolean().Should().BeTrue();
        var voice = list.GetProperty("voices").EnumerateArray().Single();
        voice.GetProperty("name").GetString().Should().Be("Аня");
        voice.GetProperty("transcript").GetString().Should().Be("Привет");
        voice.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("state").GetString())
            .Should().AllBe("none");

        var renamed = await _client.PatchAsJsonAsync($"{Voices}/{slug}", new { name = "Анна" });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        (await Json(await _client.GetAsync($"{Voices}/{slug}"))).GetProperty("name").GetString().Should().Be("Анна");

        var added = await _client.PostAsync($"{Voices}/{slug}/samples", Form(null));
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        (await Json(added)).GetProperty("samples").GetArrayLength().Should().Be(3);

        var sample = await _client.GetAsync($"{Voices}/{slug}/files/sample-03.wav");
        sample.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sample.Content.ReadAsByteArrayAsync()).Should().Equal(Wav);

        var removed = await _client.DeleteAsync($"{Voices}/{slug}/samples/sample-01.wav");
        removed.StatusCode.Should().Be(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());
        File.Exists(Path.Combine(dir, "sample-01.wav")).Should().BeFalse();

        (await _client.DeleteAsync($"{Voices}/{slug}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(dir).Should().BeFalse();
        (await _client.GetAsync($"{Voices}/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Отказы_ввода_и_пути()
    {
        Start();
        var notAudio = await _client.PostAsync(Voices, Form("Аня", projectFile: "../escape.wav", upload: false));
        notAudio.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var slug = await CreateVoice();
        foreach (var url in new[] { $"{Voices}/..", $"{Voices}/%2e%2e%2fvoices", $"{Voices}/{slug}/files/voice.json",
                     $"{Voices}/{slug}/files/..%2fvoice.json" })
            (await _client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, url);

        (await _client.PostAsync(Voices, Form("  "))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.GetDirectories(Path.Combine(_projectRoot, "voices")).Should().ContainSingle();
    }

    // F2-M1: образцы из файлов проекта проходят общий предикат private (ProjectLinkGuard.ResolveInsideAs,
    // субъект — вызывающий): чужой .cc-attachments/private/{X} — 400 как «файл не найден» (наличие не
    // раскрываем), в voices/ ничего не записано на обеих ручках; свой private и обычный файл — как раньше
    [Fact]
    public async Task Чужой_private_в_образцах_400_в_voices_ничего_не_записано()
    {
        Start();
        var foreign = Path.Combine(_projectRoot, ".cc-attachments", "private", "other-user", "g1");
        Directory.CreateDirectory(foreign);
        File.WriteAllBytes(Path.Combine(foreign, "secret.wav"), Wav);

        // Create: чужое private — отказ как «файл не найден», включая нормализованный обход
        foreach (var file in new[]
                 {
                     ".cc-attachments/private/other-user/g1/secret.wav",
                     ".cc-attachments/PRIVATE/other-user/g1/../g1/secret.wav",
                 })
        {
            var denied = await _client.PostAsync(Voices, Form("Аня", "Привет", projectFile: file, upload: false));
            denied.StatusCode.Should().Be(HttpStatusCode.BadRequest, file);
            var body = await Json(denied);
            body.GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.InvalidRequest, file);
            // дошёл именно до отказа пути: текст — тот же, что у «вне проекта»
            body.GetProperty("error").GetString().Should().Contain("Файл не найден в проекте", file);
        }
        Directory.Exists(Path.Combine(_projectRoot, "voices")).Should().BeFalse("отказанное создание не трогает диск");
        (await Json(await _client.GetAsync(Voices))).GetProperty("voices").GetArrayLength().Should().Be(0);

        // AddSamples: тот же отказ, образцы голоса не изменились
        var slug = await CreateVoice();
        var dir = Path.Combine(_projectRoot, "voices", slug);
        var added = await _client.PostAsync($"{Voices}/{slug}/samples",
            Form(null, projectFile: ".cc-attachments/private/other-user/g1/secret.wav", upload: false));
        added.StatusCode.Should().Be(HttpStatusCode.BadRequest, await added.Content.ReadAsStringAsync());
        (await Json(added)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.InvalidRequest);
        Directory.GetFiles(dir).Select(Path.GetFileName).Should().BeEquivalentTo("voice.json", "sample-01.wav", "sample-02.wav");
    }

    // Свой private участника и обычный файл проекта остаются образцами на обеих ручках
    [Fact]
    public async Task Свой_private_и_обычный_файл_становятся_образцом()
    {
        Start();
        // валидный WAV, но байтами отличимый от Wav — чтобы проверить, что образец взят из private-файла
        byte[] mine = [.. "RIFF"u8, 1, 1, 1, 1, .. "WAVE"u8, 3, 3];
        var own = Path.Combine(_projectRoot, ".cc-attachments", "private", _ownerId, "g1");
        Directory.CreateDirectory(own);
        File.WriteAllBytes(Path.Combine(own, "mine.wav"), mine);
        var ownRel = $".cc-attachments/private/{_ownerId}/g1/mine.wav";

        // Create: загрузка + обычный файл проекта — как раньше
        var slug = await CreateVoice();
        var dir = Path.Combine(_projectRoot, "voices", slug);
        Directory.GetFiles(dir).Select(Path.GetFileName).Should().BeEquivalentTo("voice.json", "sample-01.wav", "sample-02.wav");

        // AddSamples: свой private — принят, его байты легли в образец
        var added = await _client.PostAsync($"{Voices}/{slug}/samples", Form(null, projectFile: ownRel, upload: false));
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        (await Json(added)).GetProperty("samples").GetArrayLength().Should().Be(3);
        File.ReadAllBytes(Path.Combine(dir, "sample-03.wav")).Should().Equal(mine);

        // Create: свой private — единственный источник образца
        var created = await _client.PostAsync(Voices, Form("Борис", "Привет", projectFile: ownRel, upload: false));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var ownSlug = (await Json(created)).GetProperty("slug").GetString()!;
        File.ReadAllBytes(Path.Combine(_projectRoot, "voices", ownSlug, "sample-01.wav")).Should().Equal(mine);
    }

    [Fact]
    public async Task Rvc_ИзВерсииНити_ПараЛожитсяВместе()
    {
        Start();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var chatId = (await sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "Обучение")).Id;
        var store = _factory.Services.GetRequiredService<AudioJobThreads>().Store;
        var opened = store.Open(_ownerId, chatId, null, "", 0, null);
        var threadId = opened.State.Focus!;
        var jobId = "job" + Guid.NewGuid().ToString("N")[..8];
        var jobDir = _factory.Services.GetRequiredService<AudioEditWorkspace>().JobDir(_ownerId, jobId);
        Directory.CreateDirectory(jobDir);
        File.WriteAllBytes(Path.Combine(jobDir, "voice.pth"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(jobDir, "voice.index"), [4, 5]);
        store.AddEditVersion(_ownerId, chatId, threadId,
            [new(AudioFileRoles.Model, "voice.pth"), new(AudioFileRoles.Index, "voice.index")], null, jobId: jobId);

        var resp = await _client.PostAsJsonAsync($"{Voices}/rvc", new { name = "Андрей", sessionId = chatId, threadId });

        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var voice = await Json(resp);
        voice.GetProperty("kind").GetString().Should().Be("rvc");
        var dir = Path.Combine(_projectRoot, "voices", voice.GetProperty("slug").GetString()!);
        File.ReadAllBytes(Path.Combine(dir, "voice.pth")).Should().Equal(1, 2, 3);
        File.ReadAllBytes(Path.Combine(dir, "voice.index")).Should().Equal(4, 5);

        // Версия без .index — отказ, папка не заводится
        store.AddEditVersion(_ownerId, chatId, threadId, [new(AudioFileRoles.Model, "voice.pth")], null, jobId: jobId);
        var half = await _client.PostAsJsonAsync($"{Voices}/rvc", new { name = "Половина", sessionId = chatId, threadId });
        half.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Directory.GetDirectories(Path.Combine(_projectRoot, "voices")).Should().ContainSingle();
    }

    [Fact]
    public async Task Чужой_проект_404()
    {
        Start();
        var slug = await CreateVoice();
        var second = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        (await second.GetAsync(Voices)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await second.DeleteAsync($"{Voices}/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(Path.Combine(_projectRoot, "voices", slug)).Should().BeTrue();

        (await _factory.CreateClient().GetAsync(Voices)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Локальный_проект_отказ_до_диска()
    {
        Start();
        // Отдельный локальный проект: тест выносит его на несуществующий путь — общий проект
        // других тестов затронут быть не должен
        var trap = Path.Combine(_factory.TempDir, "trap_" + Guid.NewGuid().ToString("N")[..8]);
        var localRoot = Path.Combine(_factory.TempDir, "local_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(localRoot);
        var localId = _factory.Services.GetRequiredService<ProjectManager>()
            .Create("loc-" + Guid.NewGuid().ToString("N")[..6], localRoot, _ownerId, TestWebApplicationFactory.TestUsername).Id;
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        var project = projects.GetById(localId)!;
        project.DeviceId = "dev";
        project.RootPath = trap;

        try
        {
            var localVoices = $"/api/projects/{localId}/audio-editor/voices";
            var responses = new[]
            {
                await _client.GetAsync(localVoices),
                await _client.PostAsync(localVoices, Form("Аня")),
                await _client.DeleteAsync($"{localVoices}/anya"),
            };

            foreach (var r in responses)
            {
                r.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await Json(r)).GetProperty("code").GetString().Should().Be(ProjectCapabilityGuard.Code);
            }
            Directory.Exists(trap).Should().BeFalse();
        }
        finally
        {
            // «Битый» проект с несуществующим RootPath не должен жить в реестре общего хоста до конца класса
            projects.Delete(localId);
        }
    }

    [Fact]
    public async Task Use_сбрасывает_счётчик_фейка()
    {
        var cloner = _hosts.MiniMax.Cloner;
        await cloner.RunAsync(null!, null!, CancellationToken.None);
        cloner.Runs.Should().Be(1);

        _hosts.MiniMax.Use();

        cloner.Runs.Should().Be(0);
    }

    private async Task<string> PersonalChat()
    {
        Start();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        return (await sessions.CreateChatAsync(_ownerId, ClaudeMode.AcceptEdits, name: "Личный")).Id;
    }

    // Чтение папку «Личного» не создаёт: список пуст, правка и удаление несуществующего голоса — 404 без мутаций
    [Fact]
    public async Task Личный_чат_без_папки_список_пуст_папка_не_создаётся()
    {
        var chatId = await PersonalChat();
        var url = $"/api/audio-editor/chats/{chatId}/voices";

        var resp = await _client.GetAsync(url);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Json(resp);
        body.GetProperty("available").GetBoolean().Should().BeTrue();
        body.GetProperty("voices").GetArrayLength().Should().Be(0);
        (await _client.GetAsync($"{url}/anya")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PatchAsJsonAsync($"{url}/anya", new { name = "X" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"{url}/anya")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsync($"{url}/anya/samples", Form(null))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Directory.Exists(_hosts.MiniMax.Personal.Root).Should().BeFalse();
    }

    // CRUD голоса в личном чате: добавление создаёт папку «Личного», файлы лежат в Personal/voices/<slug>/
    [Fact]
    public async Task Личный_чат_Crud_голоса_в_папке_Личного()
    {
        var chatId = await PersonalChat();
        var url = $"/api/audio-editor/chats/{chatId}/voices";

        var created = await _client.PostAsync(url, Form("Аня", "Привет"));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var slug = (await Json(created)).GetProperty("slug").GetString()!;
        var dir = Path.Combine(_hosts.MiniMax.Personal.Root, "voices", slug);
        File.Exists(Path.Combine(dir, "sample-01.wav")).Should().BeTrue();

        var list = await Json(await _client.GetAsync(url));
        list.GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("slug").GetString()).Should().Equal(slug);

        (await _client.PatchAsJsonAsync($"{url}/{slug}", new { name = "Анна" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await _client.GetAsync($"{url}/{slug}"))).GetProperty("name").GetString().Should().Be("Анна");

        var added = await _client.PostAsync($"{url}/{slug}/samples", Form(null));
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        (await Json(added)).GetProperty("samples").GetArrayLength().Should().Be(2);
        var sample = await _client.GetAsync($"{url}/{slug}/files/sample-02.wav");
        sample.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sample.Content.ReadAsByteArrayAsync()).Should().Equal(Wav);
        (await _client.DeleteAsync($"{url}/{slug}/samples/sample-01.wav")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.DeleteAsync($"{url}/{slug}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Directory.Exists(dir).Should().BeFalse();
        (await _client.GetAsync($"{url}/{slug}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Файлов проекта у личного чата нет: путь из формы — 400, голос не создан
    [Fact]
    public async Task Личный_чат_файлы_проекта_в_форме_отклоняются()
    {
        var chatId = await PersonalChat();
        var url = $"/api/audio-editor/chats/{chatId}/voices";
        await _client.PostAsync(url, Form("Аня"));

        var resp = await _client.PostAsync(url, Form("Борис", projectFile: "voices/anya/sample-01.wav", upload: false));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
        (await Json(await _client.GetAsync(url))).GetProperty("voices").GetArrayLength().Should().Be(1);
    }

    // Голос личного чата не виден из чужого чата проекта: ручка личного чата — только для чата без проекта
    [Fact]
    public async Task Личные_голоса_через_чат_проекта_недоступны()
    {
        Start();
        var sessions = _factory.Services.GetRequiredService<SessionManager>();
        var chat = await sessions.CreateAsync(_projectId, ClaudeMode.AcceptEdits, name: "В проекте");

        var resp = await _client.GetAsync($"/api/audio-editor/chats/{chat.Id}/voices");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Пересоздание клона в две фазы: без quoteId — котировка с ценой и ничего не запущено; с quoteId — задача
    // ровно по котировке ЭТОГО голоса. Котировка чужого голоса, повтор и выдуманный id — 404; чужой
    // поставщик — 400; нет голоса — 404. Id клона в ответах нет
    [Fact]
    public async Task Recreate_ДвеФазы_И_МаршрутОшибок()
    {
        Start();
        var cloner = _hosts.MiniMax.Cloner;
        var slug = await CreateVoice();
        var other = await CreateVoice("Борис");
        var url = $"{Voices}/{slug}/recreate";

        var quoted = await _client.PostAsync($"{url}?provider=minimax", null);
        quoted.StatusCode.Should().Be(HttpStatusCode.OK, await quoted.Content.ReadAsStringAsync());
        var quote = await Json(quoted);
        quote.GetProperty("recreateVoice").GetString().Should().Be(slug);
        var quoteId = quote.GetProperty("quoteId").GetString()!;
        cloner.Runs.Should().Be(0, "котировка ничего не запускает");

        // Котировка выписана на другой голос — как несуществующая
        var foreign = await _client.PostAsync($"{Voices}/{other}/recreate?quoteId={quoteId}", null);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(foreign)).GetProperty("code").GetString().Should().Be(AudioEditErrorCodes.QuoteNotFound);
        (await _client.PostAsync($"{url}?quoteId=nope", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        cloner.Runs.Should().Be(0);

        var started = await _client.PostAsync($"{url}?quoteId={quoteId}", null);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        var jobId = (await Json(started)).GetProperty("jobId").GetString()!;
        var jobs = _factory.Services.GetRequiredService<AudioEditJobService>();
        await jobs.WhenDone(jobId);
        cloner.Runs.Should().Be(1);
        var voice = await Json(await _client.GetAsync($"{Voices}/{slug}"));
        voice.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "minimax")
            .GetProperty("state").GetString().Should().Be("ok");
        voice.ToString().Should().NotContain("mm-fresh");

        // Котировка одноразовая
        (await _client.PostAsync($"{url}?quoteId={quoteId}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsync($"{url}?provider=higgsfield", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsync($"{Voices}/nobody/recreate?provider=minimax", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        cloner.Runs.Should().Be(1);
    }
}
