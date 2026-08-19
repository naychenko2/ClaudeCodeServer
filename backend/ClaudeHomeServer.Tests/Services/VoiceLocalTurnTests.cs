using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Локальный голосовой ход (место chat-voice на «Локальная»): реплики разговора
// исполняются прямым HTTP-вызовом Ollama мимо claude CLI, ответ приходит
// синтетическими ServerMessage через общий конвейер OnMessageAsync.
// Фикстура повторяет SessionManagerTests (моки хаба/прокси, temp-каталог), но
// OllamaClient — настоящий, с перехватчиком HTTP: проверяем протокол ветки,
// а не сеть.
public class VoiceLocalTurnTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChatHistoryService _historyService;
    private readonly LocalActionOverridesStore _actionOverrides;
    private readonly SessionManager _sut;
    private readonly List<ServerMessage> _sentMessages = new();
    private readonly object _sentMessagesLock = new();
    private readonly FakeOllamaHttp _ollamaHttp = new();

    private List<T> Sent<T>()
    {
        lock (_sentMessagesLock) return _sentMessages.OfType<T>().ToList();
    }

    public VoiceLocalTurnTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "voice_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
                // Локаль включена — иначе UsesLocal(chat-voice) всегда false
                ["Ollama:Model"] = "qwen-test",
                ["Ollama:BaseUrl"] = "http://localhost:11434",
                ["Delivery:AwaitProcessExitSeconds"] = "0",
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _historyService = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                if (args.Length > 0 && args[0] is ServerMessage msg)
                    lock (_sentMessagesLock)
                        _sentMessages.Add(msg);
            })
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.Is<string>(g => !g.StartsWith("project_") && !g.StartsWith("user_"))))
            .Returns(clientProxy.Object);
        clients.Setup(c => c.Group(It.Is<string>(g => g.StartsWith("project_") || g.StartsWith("user_"))))
            .Returns(new Mock<IClientProxy>().Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config, NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _actionOverrides = new LocalActionOverridesStore(config);
        var assignments = new ModelAssignmentResolver(appSettings, _actionOverrides,
            new UserModelTierResolver(userStore, appSettings));

        // Настоящий OllamaClient с перехватчиком HTTP: ответы задаёт тест
        var ollama = new OllamaClient(_ollamaHttp, config, NullLogger<OllamaClient>.Instance);
        var router = new LocalActionRouter(ollama, _actionOverrides, config, NullLogger<LocalActionRouter>.Instance);

        _sut = new SessionManager(projectManager, hub.Object, _historyService, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox,
            router: router, ollama: ollama);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<Session> MakeVoiceChatAsync(bool local = true)
    {
        var user = ((UserStore)GetInstanceField(_sut, "_users")!).Add("voice-user", "password123", "user");
        var session = await _sut.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits);
        _sut.SetVoiceMode(session.Id, true);
        if (local)
            _actionOverrides.Set(LocalActionCatalog.ChatVoice, LocalActionOverridesStore.LocalRoute);
        else
            _actionOverrides.Reset(LocalActionCatalog.ChatVoice);
        return session;
    }

    private static object GetInstanceField(object obj, string name) =>
        obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(obj)!;

    private object GetEntry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sessions = (System.Collections.IDictionary)field.GetValue(_sut)!;
        return sessions[sessionId]!;
    }

    // Ждём события в _sentMessages (RunLocalVoiceTurnAsync — fire-and-forget Task.Run)
    private async Task<List<T>> WaitForAsync<T>(Func<List<T>> take, TimeSpan timeout) where T : ServerMessage
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var items = take();
            if (items.Count > 0) return items;
            await Task.Delay(20);
        }
        return take();
    }

    [Fact]
    public void Каталог_МестоChatVoice_НеагентноеИБезЛокалиПоУмолчанию()
    {
        var action = LocalActionCatalog.Find(LocalActionCatalog.ChatVoice);
        action.Should().NotBeNull("место должно быть в каталоге");
        action!.Agentic.Should().BeFalse("разговор не требует инструментов CLI — локаль пригодна");
        action.DefaultLocal.Should().BeFalse("включается осознанно, админом");
        action.Group.Should().Be("Чаты и персоны");
    }

    [Fact]
    public async Task ЛокальныйХод_ОтвечаетСинтетическимиСобытиямиИПишетИсторию()
    {
        var session = await MakeVoiceChatAsync();
        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":"Привет, я на связи."},"prompt_eval_count":42,"eval_count":7}""";

        await _sut.SendMessageAsync(session.Id, "привет", []);

        (await WaitForAsync(() => Sent<SessionStartedMessage>(), TimeSpan.FromSeconds(5)))
            .Should().ContainSingle("локальный ход стартует как обычный");
        (await WaitForAsync(() => Sent<ResultMessage>(), TimeSpan.FromSeconds(5)))
            .Should().ContainSingle().Which.Subtype.Should().Be("success");
        // text_delta потоком + exited закрывает ход: склейка кусков = ответ модели
        var deltas = await WaitForAsync(() => Sent<TextDeltaMessage>(), TimeSpan.FromSeconds(5));
        string.Concat(deltas.Select(d => d.Text)).Should().Be("Привет, я на связи.");
        (await WaitForAsync(() => Sent<ExitedMessage>(), TimeSpan.FromSeconds(5)))
            .Should().ContainSingle("exited закрывает ход и возвращает статус Active");

        // Запрос к Ollama: system + одна реплика (текущая, из аккумулятора)
        _ollamaHttp.LastBody.Should().NotBeNull();
        var doc = JsonDocument.Parse(_ollamaHttp.LastBody!);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToList();
        // Текущая реплика уже в аккумуляторе (OnUserMessage до диспетчеризации) —
        // отдельно в messages не добавляется, дубля нет
        roles.Should().Equal("system", "user");
        doc.RootElement.GetProperty("model").GetString().Should().Be("qwen-test");

        // История пишется под id чата (ClaudeSessionId ещё нет)
        _sut.GetById(session.Id)!.ClaudeSessionId.Should().BeNull("локальная ветка его не ставит");
        var history = await _historyService.LoadAsync(session.Id);
        history.OfType<StoredTextMessage>().Should().ContainSingle(t => t.Text == "Привет, я на связи.",
            "ответ разговора попадает в общую ленту");
    }

    [Fact]
    public async Task ЛокальныйХод_ИдётПотокомИОтдаётКускиПоПредложениям()
    {
        // Ради озвучки: первый кусок ответа должен уходить в ленту ДО конца генерации,
        // а границей куска служит предложение (фронт режет речь по ним же)
        var session = await MakeVoiceChatAsync();
        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":"Первое. Второе."},"prompt_eval_count":9,"eval_count":4}""";
        _ollamaHttp.NextChunks = ["Первое", ".", " Второе", "."];

        await _sut.SendMessageAsync(session.Id, "привет", []);
        await WaitForAsync(() => Sent<ExitedMessage>(), TimeSpan.FromSeconds(5));

        _ollamaHttp.LastBody.Should().Contain("\"stream\":true", "разговорный ход ходит потоком");
        var deltas = Sent<TextDeltaMessage>();
        deltas.Should().HaveCount(2, "куски копятся до конца предложения, а не шлются по токену");
        deltas[0].Text.Should().Be("Первое.");
        deltas[1].Text.Should().Be(" Второе.");
        string.Concat(deltas.Select(d => d.Text)).Should().Be("Первое. Второе.");

        // Токены из финального чанка done:true — учёт расхода не теряется на потоке
        Sent<ResultMessage>().Should().ContainSingle()
            .Which.Usage!.InputTokens.Should().Be(9);
    }

    [Fact]
    public async Task ЛокальныйХод_ХвостБезТочки_ДоезжаетПоследнимКуском()
    {
        // Модель кончила ответ без терминальной пунктуации — накопленный хвост обязан
        // уйти в ленту, иначе фраза потерялась бы вместе с озвучкой
        var session = await MakeVoiceChatAsync();
        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":"ага"},"prompt_eval_count":3,"eval_count":1}""";
        _ollamaHttp.NextChunks = ["а", "га"];

        await _sut.SendMessageAsync(session.Id, "привет", []);
        await WaitForAsync(() => Sent<ExitedMessage>(), TimeSpan.FromSeconds(5));

        string.Concat(Sent<TextDeltaMessage>().Select(d => d.Text)).Should().Be("ага");
        var history = await _historyService.LoadAsync(session.Id);
        history.OfType<StoredTextMessage>().Should().ContainSingle(t => t.Text == "ага");
    }

    [Fact]
    public async Task Гейт_МаршрутНеЛокальный_РазговорИдётЧерезКли()
    {
        // Гейт — чистая логика, проверяем прямо (без реального SendMessage: CLI-ветка
        // подняла бы настоящий процесс claude в temp-папке теста)
        var session = await MakeVoiceChatAsync(local: false);
        var entry = GetEntry(session.Id);
        var gate = typeof(SessionManager).GetMethod("ShouldRunLocalVoice",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        gate.Invoke(_sut, [entry, /*auto*/ false, /*systemDirective*/ false, new List<string>()])!
            .Should().Be(false, "маршрут места не local — разговор идёт через CLI");

        // …а при назначении «Локальная» тот же чат гейт проходит
        _actionOverrides.Set(LocalActionCatalog.ChatVoice, LocalActionOverridesStore.LocalRoute);
        gate.Invoke(_sut, [entry, false, false, new List<string>()])!
            .Should().Be(true, "маршрут local + VoiceMode + ход человека");
    }

    [Theory]
    [InlineData(true, false, 0, "auto-ход (доклад автоматизации)")]
    [InlineData(false, true, 0, "системная директива цикла")]
    [InlineData(false, false, 1, "вложение к реплике")]
    public async Task Гейт_ЧуждыеУсловия_ЛокальныйХодОтключен(bool auto, bool systemDirective, int attached, string because)
    {
        var session = await MakeVoiceChatAsync();
        var entry = GetEntry(session.Id);
        var gate = typeof(SessionManager).GetMethod("ShouldRunLocalVoice",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var paths = new List<string>(Enumerable.Repeat("file.txt", attached));
        gate.Invoke(_sut, [entry, auto, systemDirective, paths])!
            .Should().Be(false, because);
    }

    [Fact]
    public async Task Гейт_ЦиклДоцотово_ЛокальныйХодОтключен()
    {
        var session = await MakeVoiceChatAsync();
        var entry = GetEntry(session.Id);
        var gate = typeof(SessionManager).GetMethod("ShouldRunLocalVoice",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // Цикл «до готово» активен — локаль не воспроизведёт протокол маркера
        await _sut.SetWorkLoopAsync(session.Id, true);
        gate.Invoke(_sut, [entry, false, false, new List<string>()])!
            .Should().Be(false, "при живом цикле «до готово» ход идёт через CLI");

        await _sut.SetWorkLoopAsync(session.Id, false);
        gate.Invoke(_sut, [entry, false, false, new List<string>()])!
            .Should().Be(true, "цикл снят — разговор снова на локали");
    }

    [Fact]
    public async Task Стоп_ОтменяетЛокальныйХодИЗакрываетЕгоЧерезExited()
    {
        var session = await MakeVoiceChatAsync();
        // Ответ не приходит, пока тест не отпустит гейт, — ход висит в HTTP-вызове
        _ollamaHttp.Hold = true;

        await _sut.SendMessageAsync(session.Id, "привет", []);
        var entry = GetEntry(session.Id);
        // Ждём, пока ветка выставит LocalVoiceCts (ход реально пошёл)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
               && entry.GetType().GetField("LocalVoiceCts")!.GetValue(entry) is null)
            await Task.Delay(20);

        _sut.Interrupt(session.Id);
        _ollamaHttp.Release(); // отпускаем висящий HTTP — отмена уже стрельнула по CTS

        // exited закрывает ход: статус возвращается в Active (не Error, не зависший Working)
        (await WaitForAsync(() => Sent<ExitedMessage>(), TimeSpan.FromSeconds(5)))
            .Should().ContainSingle("отменённый ход закрывается exited-ом самой ветки");
        _sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Active,
            "после «Стоп» локальный ход возвращает чат в рабочее состояние");
    }

    [Fact]
    public async Task ИсторияЛокальныхХодов_ПодхватываетсяПослеУсловногоРестарта()
    {
        // Локальный ход без ClaudeSessionId пишет историю в data/sessions/{id чата};
        // EnsureProcessCoreAsync при null-транскрипте теперь читает её оттуда — лента
        // разговора не теряется при оживлении аккумулятора (рестарт сервера)
        var session = await MakeVoiceChatAsync();
        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":"Реплика на локали."},"prompt_eval_count":10,"eval_count":5}""";
        await _sut.SendMessageAsync(session.Id, "привет", []);
        await WaitForAsync(() => Sent<ResultMessage>(), TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        var history = await _historyService.LoadAsync(session.Id);
        history.OfType<StoredTextMessage>().Should().ContainSingle(t => t.Text == "Реплика на локали.",
            "локальный ход пишет историю по id чата (транскрипта CLI ещё нет)");

        // «Рестарт»: аккумулятор сброшен, ClaudeSessionId всё ещё null — оживление
        // читает историю по тому же ключу
        var entry = GetEntry(session.Id);
        entry.GetType().GetField("Accumulator")!.SetValue(entry, null);
        var ensure = typeof(SessionManager).GetMethod("EnsureAccumulatorAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)ensure.Invoke(_sut, [entry])!;
        entry.GetType().GetField("Accumulator")!.GetValue(entry).Should().NotBeNull();
        var revived = (TurnAccumulator)entry.GetType().GetField("Accumulator")!.GetValue(entry)!;
        revived.GetAll().OfType<StoredTextMessage>()
            .Should().ContainSingle(t => t.Text == "Реплика на локали.",
                "лента разговора переживает рестарт сервера");
    }

    [Fact]
    public async Task ВозвратНаКли_ПослеЛокальныхРеплик_ДописываетКонтекстРазговора()
    {
        var session = await MakeVoiceChatAsync();
        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":"Говорю с локали."},"prompt_eval_count":10,"eval_count":5}""";
        await _sut.SendMessageAsync(session.Id, "привет", []);
        await WaitForAsync(() => Sent<ResultMessage>(), TimeSpan.FromSeconds(5));
        await Task.Delay(200); // finally ветки: LocalTurnsSinceCli++

        // Снимаем local-маршрут: следующий ход пойдёт через CLI, и его текст должен
        // получить префикс-сводку разговора
        _actionOverrides.Reset(LocalActionCatalog.ChatVoice);
        var entry = GetEntry(session.Id);
        entry.GetType().GetField("LocalTurnsSinceCli")!.GetValue(entry)
            .Should().Be(1, "локальный ход посчитан");

        var method = typeof(SessionManager).GetMethod("BuildCliTurnText",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = (string)method.Invoke(_sut, [entry, "теперь по делу"])!;
        result.Should().Contain("[Контекст: до этого пользователь разговаривал голосом",
            "CLI не знает реплик разговора — им нужна сводка");
        result.Should().Contain("Говорю с локали.", "в сводке хвост истории разговора");
        result.Should().Contain("теперь по делу", "исходный текст хода сохранён");
        // Счётчик сброшен — вторая сводка не дублируется
        entry.GetType().GetField("LocalTurnsSinceCli")!.GetValue(entry)
            .Should().Be(0, "сводка одноразовая");
    }

    [Fact]
    public async Task ПустойОтвет_ЧестнаяОшибка_БезФолбэка()
    {
        var session = await MakeVoiceChatAsync();
        var messageCountBefore = _sut.GetById(session.Id)!.MessageCount;
        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":""}}""";

        await _sut.SendMessageAsync(session.Id, "привет", []);

        (await WaitForAsync(() => Sent<ErrorMessage>(), TimeSpan.FromSeconds(5)))
            .Should().ContainSingle("пустой ответ — видимое сообщение об ошибке");
        (await WaitForAsync(() => Sent<ExitedMessage>(), TimeSpan.FromSeconds(5)))
            .Should().ContainSingle("ход закрыт");
        // Фолбэка на CLI нет: адаптер чата (создан при CreateChatAsync, процесса под ним
        // нет — старт ленивый) не получил сообщение, MessageCount не растёт
        await Task.Delay(300);
        _sut.GetById(session.Id)!.MessageCount.Should().Be(messageCountBefore,
            "реплика без ответа не уходит в CLI повторно");
    }

    private void ClearSent()
    {
        lock (_sentMessagesLock) _sentMessages.Clear();
    }

    [Fact]
    public async Task Очередь_ПослеЛокальногоResult_ДоставляетСледующееСообщение()
    {
        // Реплика, отправленная во время локального хода, встаёт в «честную очередь»;
        // result локального хода должен её доставить (drain в OnMessageAsync)
        var session = await MakeVoiceChatAsync();
        _ollamaHttp.Hold = true; // первый ход висит в HTTP

        await _sut.SendMessageAsync(session.Id, "первый вопрос", []);
        var entry = GetEntry(session.Id);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
               && entry.GetType().GetField("LocalVoiceCts")!.GetValue(entry) is null)
            await Task.Delay(20);

        // Вторая реплика в обход очереди клиента — прямо в серверную (занятый чат)
        await _sut.SendMessageAsync(session.Id, "второй вопрос", []);
        _sut.GetPending(session.Id).Count.Should().BeGreaterThan(0,
            "реплика при живом ходе встаёт в серверную очередь");

        _ollamaHttp.NextResponse = """{"message":{"role":"assistant","content":"Ответ."},"prompt_eval_count":5,"eval_count":2}""";
        _ollamaHttp.Release();

        // Первый ход завершается — очередь должна разгрузиться: второй вопрос доходит
        // до Ollama (второй HTTP-вызов с текстом «второй вопрос»)
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && _ollamaHttp.RequestCount < 2)
            await Task.Delay(20);
        _ollamaHttp.RequestCount.Should().BeGreaterThanOrEqualTo(2,
            "доставка очереди после result локального хода запускает следующий ход");
        await WaitForAsync(() => Sent<ExitedMessage>(), TimeSpan.FromSeconds(5));
    }

    // Перехватчик HTTP Ollama: отвечает заготовленным телом, запоминает тело запроса.
    // Hold — держит ответ, пока тест не отпустит (проверка «Стоп» на висящем ходе).
    //
    // Разговорный ход ходит потоком (stream:true) — тогда фейк отдаёт NDJSON: строка на
    // кусок плюс финальная done:true со счётчиками токенов. Заготовка задаётся привычным
    // цельным JSON (NextResponse), а разбивку на куски — NextChunks: так тесты, которым
    // важен только результат, разбирать протокол не обязаны.
    private sealed class FakeOllamaHttp : IHttpClientFactory
    {
        public string? NextResponse { get; set; }
        // Куски потока (текст). null — весь ответ придёт одной строкой NDJSON
        public string[]? NextChunks { get; set; }
        public string? LastBody { get; private set; }
        public int RequestCount { get; private set; }
        public bool Hold { get; set; }
        private readonly TaskCompletionSource _holdGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient CreateClient(string name)
        {
            var handler = new StubHandler(this);
            return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        }

        public void Release() => _holdGate.TrySetResult();

        // Цельный ответ Ollama → NDJSON-поток
        private static string ToNdjson(string whole, string[]? chunks)
        {
            using var doc = JsonDocument.Parse(whole);
            var root = doc.RootElement;
            var content = root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c)
                ? c.GetString() ?? "" : "";
            var pieces = chunks ?? (content.Length > 0 ? [content] : System.Array.Empty<string>());

            var lines = pieces
                .Select(p => JsonSerializer.Serialize(new { message = new { role = "assistant", content = p }, done = false }))
                .ToList();
            var final = new Dictionary<string, object> { ["done"] = true };
            if (root.TryGetProperty("prompt_eval_count", out var pe)) final["prompt_eval_count"] = pe.GetInt32();
            if (root.TryGetProperty("eval_count", out var ec)) final["eval_count"] = ec.GetInt32();
            lines.Add(JsonSerializer.Serialize(final));
            return string.Join("\n", lines) + "\n";
        }

        private sealed class StubHandler(FakeOllamaHttp owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                owner.RequestCount++;
                owner.LastBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                if (owner.Hold)
                    await owner._holdGate.Task.WaitAsync(cancellationToken);
                var body = owner.NextResponse ?? """{"message":{"role":"assistant","content":""}}""";
                if (owner.LastBody?.Contains("\"stream\":true") == true)
                    body = ToNdjson(body, owner.NextChunks);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                };
            }
        }
    }
}
