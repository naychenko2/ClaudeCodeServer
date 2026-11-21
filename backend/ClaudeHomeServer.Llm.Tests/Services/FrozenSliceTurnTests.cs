using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// S2 · заморозка среза изоляции (окно бенча tb21, инцидент 21.11): отказ среды исполнения —
// отдельный класс ошибки хода, НЕ ошибка провайдера. Что ловят тесты:
//  • чат: маркер из ClaudeSession → класс SliceFrozen в TurnErrorClassifier → ветка
//    FailLocalCauseAsync в адаптере: attempts=1, без ProviderSwitchedMessage, финальный
//    result=error, текст ровно «среда исполнения на сервере заморожена, повторите позже»;
//  • one-shot: первый CLI-шаг упал IsolationUnavailableException(SliceFrozen) — остальные
//    CLI-шаги цепочки не выполняются, HTTP-движок ILocalLlmClient ответить может; пустой
//    HTTP — исходное исключение наружу, финального claude-шага нет;
//  • прочая ошибка CLI по-прежнему идёт дальше по цепочке (фильтр узкий, не «любой сбой CLI»);
//  • stderr умершего systemd-run «Cannot (start | perform operation on) frozen unit»
//    преобразуется OneShotClaudeRunner'ом в то же типизированное исключение (гонка
//    заморозки мимо проверки S1).
public class FrozenSliceTurnTests
{
    // ========== Часть 1: чат — ClaudeSession → Classifier → FallbackAdapter ==========

    private readonly List<ServerMessage> _downstream = [];
    // Взводится приёмником на первом ResultMessage: ждём событие, а не опрашиваем список по часам
    private readonly TaskCompletionSource _finalResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeInnerAdapter(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public Queue<Action> Scripts { get; } = new();
        public List<(string Provider, string? Model)> Attempts { get; } = [];

        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;
        public int CurrentTurnAgentDepth => 0;
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool HasTrackedCommandBg => false;
        public bool IsContinuationInFlight => false;
        public long SubmittedTurnSeq { get; private set; }

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false, string? turnModelOverride = null)
        {
            SubmittedTurnSeq++;
            Attempts.Add((Info.Provider ?? "", Info.Model));
            if (Scripts.Count > 0) Scripts.Dequeue()();
            return Task.CompletedTask;
        }

        public void Emit(ServerMessage msg) => Sink?.Invoke(msg).GetAwaiter().GetResult();

        public Task StartAsync() => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public void Interrupt() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ClaudeSubscriptionPool BuildPool(params string[] keys)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in keys) dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static LlmProviderRegistry BuildProviders()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p1:ApiKey"] = "sk-1",
            ["LlmProviders:p1:AnthropicBaseUrl"] = "https://p1.example.com",
            ["LlmProviders:p1:Models:0:Id"] = "m1",
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, string[]? chain = null)
    {
        var session = new Session { Model = "sonnet", Provider = "acc-a" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg =>
            {
                lock (_downstream) _downstream.Add(msg);
                if (msg is ResultMessage) _finalResult.TrySetResult();
                return Task.CompletedTask;
            },
            pool, BuildProviders(), Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            isChildTurn: () => false,
            effectiveChain: chain is null ? null : () => chain,
            events: new TurnEventBus());
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    // Потолок 30 с — страховка от зависания, а не проверяемая скорость
    private async Task WaitFinalResultAsync()
    {
        try
        {
            await _finalResult.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("не дождались: финальный result");
        }
    }

    // Details так же собирает ветка ClaudeSession (маркер первым токеном, дальше ex.Message)
    private static string FrozenDetails =>
        TurnErrorClassifier.SliceFrozenMarker + " Не могу запустить изолированный процесс: slice 'ccs-agents.slice' заморожен";

    [Fact]
    public void Классификатор_МаркерЗаморозки_SliceFrozen_АНеUnreachable()
    {
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = FrozenDetails })
            .Should().Be(FallbackErrorClass.SliceFrozen);
        TurnErrorClassifier.WireName(FallbackErrorClass.SliceFrozen).Should().Be("slice_frozen");
        // Маркер, процитированный не первым токеном, заморозкой не считается
        TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = "см. " + FrozenDetails })
            .Should().Be(FallbackErrorClass.Unreachable);
    }

    [Fact]
    public void ТекстЗаморозки_РовноФормулировкаИзПостановки() =>
        TurnFailureText.SliceFrozen.Should().Be("среда исполнения на сервере заморожена, повторите позже");

    // c2: попытка одна, подмены нет, финал — error с фиксированным текстом.
    // Мутация «убрать ветку SliceFrozen в адаптере» краснит: попытки станет две, появится
    // ProviderSwitchedMessage, а ход закончится успехом второй попытки.
    [Fact]
    public async Task ЗаморозкаСреза_ХодError_БезПодменыИКулдауна()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool, chain: ["sonnet", "m1"]);
        inner.Scripts.Enqueue(() => inner.Emit(new ErrorMessage(TurnFailureText.SliceFrozen, Details: FrozenDetails)));
        // При живой ветке заморозки не выполняется: ход кончается на первой попытке
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitFinalResultAsync();

        inner.Attempts.Should().HaveCount(1, "заморозка среды — та же машина, смена пары не лечит");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        pool.IsExhausted("acc-a").Should().BeFalse();
        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Be(TurnFailureText.SliceFrozen);
        error.Text.Should().Be("среда исполнения на сервере заморожена, повторите позже");
        error.Details.Should().NotContain(TurnErrorClassifier.SliceFrozenMarker,
            "служебный маркер человеку не показываем");
        error.Details.Should().Contain("ccs-agents.slice", "сырая причина остаётся в «Подробностях»");
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error", "ход не состоялся — статус обязан это показать");
    }

    // Лаунчер, чей срез заморожен: Start отказывает тем же исключением, что бросит
    // LocalProcessRunner.Start (S1) при замороженном slice.
    private sealed class FrozenLauncher : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => false;
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;
        public Process Start(ProcessSpec spec) => throw new IsolationUnavailableException(
            IsolationUnavailableException.SliceFrozen,
            "Не могу запустить изолированный процесс: slice 'ccs-agents.slice' заморожен",
            "ccs-agents.slice");
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
        public bool LocalModelsPaused => false;
    }

    // Вход инварианта чата: маркер в Details ставит сама ClaudeSession (общий catch хода),
    // и именно по нему классификатор узнаёт заморозку — русской формулировки в Text ему мало.
    [Fact]
    public async Task МаркерИзClaudeSession_КлассификаторВидитЗаморозку()
    {
        var sent = new List<ServerMessage>();
        var errorSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-frozen-slice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m =>
            {
                lock (sent) sent.Add(m);
                if (m is ErrorMessage) errorSeen.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: new FrozenLauncher());
        var session = new ClaudeSession(new Session(), context);
        try
        {
            await session.SendMessageAsync("ход");
            await errorSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));

            ErrorMessage error;
            lock (sent) error = sent.OfType<ErrorMessage>().Should().ContainSingle().Subject;
            error.Text.Should().Be(TurnFailureText.SliceFrozen);
            error.Details.Should().StartWith(TurnErrorClassifier.SliceFrozenMarker,
                "классификатор работает с сырым текстом Details — по Text он заморозку не узнает");
            TurnErrorClassifier.Classify(new TurnAttemptOutcome { HasResult = false, ErrorText = error.Details })
                .Should().Be(FallbackErrorClass.SliceFrozen);
        }
        finally
        {
            await session.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* temp — уберёт уборка */ }
        }
    }

    // ========== Часть 2: one-shot — OneShotClaudeRunner → CheapTextRunner ==========

    // Клиент локального движка: считает обращения и отвечает; «не дошёл» = Calls 0.
    private sealed class FakeLocalClient(string? answer = "ответ локали") : ILocalLlmClient
    {
        public int Calls;
        public bool Enabled => true;
        public string BaseUrl => "http://local";
        public string Model => "qwen3.8-27b";
        public string TextModel => "qwen3.8-27b";
        public string ProviderKey => LocalLlmOptions.LlamaServer;

        public Task<string?> ChatJsonAsync(string systemPrompt, string userPrompt, object jsonFormat,
            CancellationToken ct = default, string? model = null, int? timeoutMs = null, int? numPredict = null,
            int? numCtx = null, string? ownerId = null, string? label = null)
        { Calls++; return Task.FromResult(answer); }

        public Task<string?> GenerateTextAsync(string prompt, string? model, TimeSpan timeout, int numPredict,
            int numCtx, string? ownerId = null, string? label = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }

        public async Task<(string? Text, UsageInfo? Usage)> GenerateTextDetailedAsync(string prompt, string? model,
            TimeSpan timeout, int numPredict, int numCtx, string? ownerId = null, string? label = null,
            CancellationToken ct = default)
            => (await GenerateTextAsync(prompt, model, timeout, numPredict, numCtx, ownerId, label, ct), null);

        public Task<ChatTurnResult> ChatTurnAsync(IReadOnlyList<ChatMsg> messages, string? model, TimeSpan timeout,
            int numPredict, int numCtx, string? ownerId, Func<string, Task>? onDelta = null,
            CancellationToken ct = default)
            => Task.FromResult(new ChatTurnResult(answer, null));

        public Task WarmUpAsync(string? model, CancellationToken ct = default) => Task.CompletedTask;
    }

    // CLI-шаг, падающий отказом среды (как OneShotClaudeRunner при замороженном slice)
    private sealed class FrozenCliOneShot : IOneShotRunner
    {
        public readonly List<string?> Calls = [];
        public string? NormalizeModel(string? model) => model;

        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        { Calls.Add(model); throw Frozen(); }

        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        { Calls.Add(model); throw Frozen(); }

        private static IsolationUnavailableException Frozen() => new(
            IsolationUnavailableException.SliceFrozen,
            "Не могу запустить изолированный процесс: slice 'ccs-agents.slice' заморожен",
            "ccs-agents.slice");
    }

    // Контроль фильтра: первая попытка падает ОБЫЧНОЙ ошибкой — цепочка обязана идти дальше
    private sealed class FailFirstOneShot : IOneShotRunner
    {
        public readonly List<string?> Calls = [];
        public string? NormalizeModel(string? model) => model;

        public Task<string> RunAsync(string prompt, string? model = null, TimeSpan? timeout = null,
            CancellationToken ct = default, string? ownerId = null, string? effort = null, string? label = null)
        { Calls.Add(model); return Task.FromResult($"CLAUDE[{Calls.Count}]"); }

        public Task<OneShotResult> RunDetailedAsync(string prompt, string? model = null,
            TimeSpan? timeout = null, CancellationToken ct = default, string? ownerId = null,
            string? effort = null, string? label = null)
        {
            Calls.Add(model);
            if (Calls.Count == 1) throw new InvalidOperationException("обычный сбой CLI");
            return Task.FromResult(new OneShotResult($"CLAUDE[{Calls.Count}]", null, 0));
        }
    }

    private sealed class NullHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }

    private static IConfiguration Config(Dictionary<string, string?> d)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        d["DataPath"] = Path.Combine(dir, "projects.json");
        return TestConfig.Build(d);
    }

    // Стенд: место NotesTags с маршрутом-моделью «m1» (CLI, не direct) и основной локалью
    // по выбору теста. Цепочка: m1 (CLI) → локаль (HTTP ILocalLlmClient) → claude (CLI).
    private static (CheapTextRunner Runner, FakeLocalClient? Main, IOneShotRunner Claude) Stand(
        IOneShotRunner claude, FakeLocalClient? mainLocal)
    {
        var config = Config([]);
        var ollama = (ILocalLlmClient?)mainLocal
            ?? new OllamaClient(new NullHttpFactory(), config, NullLogger<OllamaClient>.Instance);
        var store = new LocalActionOverridesStore(config, NullLogger<LocalActionOverridesStore>.Instance);
        store.Set(LocalActionCatalog.NotesTags, "m1");
        var router = new LocalActionRouter(ollama, store, config, NullLogger<LocalActionRouter>.Instance);
        var cloud = new CloudCheapClient(new NullHttpFactory(), config, new LlmProviderRegistry(config),
            NullLogger<CloudCheapClient>.Instance);
        var runner = new CheapTextRunner(router, ollama, cloud, claude,
            NullLogger<CheapTextRunner>.Instance);
        return (runner, mainLocal, claude);
    }

    // c3, половина 1: HTTP-движок ответил — его результат и возвращён, финальный CLI не звался.
    [Fact]
    public async Task ЗаморозкаСреды_HttpДвижокОтветил_CliНеПеребирается()
    {
        var claude = new FrozenCliOneShot();
        var main = new FakeLocalClient();
        var (runner, _, _) = Stand(claude, main);

        var answer = await runner.RunAsync(LocalActionCatalog.NotesTags, "промпт");

        answer.Should().Be("ответ локали", "HTTP-движок ILocalLlmClient процессов не порождает и отвечать может");
        main.Calls.Should().Be(1);
        claude.Calls.Should().HaveCount(1, "единственный CLI-вызов — упавший выбранный шаг; финального claude нет");
    }

    // c3, половина 2: HTTP пуст — исходное исключение наружу, финального CLI-шага нет.
    [Fact]
    public async Task ЗаморозкаСреды_HttpПуст_ИсключениеНаружу_ФинальногоКлиНет()
    {
        var claude = new FrozenCliOneShot();
        var main = new FakeLocalClient(answer: null);
        var (runner, _, _) = Stand(claude, main);

        var ex = await Assert.ThrowsAsync<IsolationUnavailableException>(
            () => runner.RunAsync(LocalActionCatalog.NotesTags, "промпт"));

        ex.Reason.Should().Be(IsolationUnavailableException.SliceFrozen);
        main.Calls.Should().Be(1, "HTTP-движку ответить разрешено — он просто не ответил");
        claude.Calls.Should().HaveCount(1, "финальный CLI-шаг заменён исходным исключением");
    }

    // Та же семантика у второй копии цепочки (RunDetailedAsync) — копии расходятся, инвариант
    // обязан жить в обеих.
    [Fact]
    public async Task ЗаморозкаСреды_DetailЦепочка_ИсключениеНаружу_ФинальногоКлиНет()
    {
        var claude = new FrozenCliOneShot();
        var (runner, _, _) = Stand(claude, mainLocal: null);

        var ex = await Assert.ThrowsAsync<IsolationUnavailableException>(
            () => runner.RunDetailedAsync(LocalActionCatalog.NotesTags, "промпт"));

        ex.Reason.Should().Be(IsolationUnavailableException.SliceFrozen);
        claude.Calls.Should().HaveCount(1);
    }

    // Фильтр узкий: прочая ошибка CLI — по-прежнему «модель недоступна, иду дальше по цепочке»,
    // ход доходит до финального claude (второй CLI-вызов) и завершается ответом.
    [Fact]
    public async Task ОбычнаяОшибкаCli_ИдётДальшеПоЦепочке_ФинальныйClaudeВызывается()
    {
        var claude = new FailFirstOneShot();
        var (runner, _, _) = Stand(claude, mainLocal: null);

        var answer = await runner.RunAsync(LocalActionCatalog.NotesTags, "промпт");

        answer.Should().StartWith("CLAUDE[");
        claude.Calls.Should().HaveCount(2, "обычный сбой CLI не отменяет финальный шаг");
    }

    // Гонка заморозки мимо проверки S1: systemd-run умер со своим каноническим stderr —
    // раннер преобразует его в то же типизированное исключение, что и старт раннера.
    [Fact]
    public void FrozenStderr_ПреобразуетсяВИсключениеЗаморозки()
    {
        foreach (var stderr in new[]
                 {
                     "Cannot start frozen unit ccs-run-abc.scope",
                     "Cannot perform operation on frozen unit",
                 })
        {
            var ex = OneShotClaudeRunner.FrozenUnitRefusal(stderr);
            ex.Reason.Should().Be(IsolationUnavailableException.SliceFrozen);
            ex.Message.Should().Contain("slice заморожен").And.Contain(stderr);
        }
        // Не всякий stderr — заморозка: обычные отказы CLI остаются собой
        IsolationUnavailableException.IsFrozenUnitRefusal("Not logged in · Please run /login")
            .Should().BeFalse();
    }
}
