using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Tests.Services;

// Фолбэк-декоратор адаптера (ADR «Порядок резолва модели…» §2–4): ротация подписок
// пула при ошибках доставки, цепочка провайдеров уровнем 2, пара «модель × подписка»
// не повторяется, потолок 5 подмен, неизвестная ошибка = без фолбэка, interrupt
// пользователя = без фолбэка, исчерпание = финальный сбой со следом пар.
public class FallbackLlmSessionAdapterTests
{
    private readonly List<ServerMessage> _downstream = [];

    // Фейковый внутренний адаптер: скрипты событий на каждую попытку хода
    private sealed class FakeInnerAdapter(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public Queue<Action> Scripts { get; } = new();
        // (Provider, Model) на момент запуска каждой попытки
        public List<(string Provider, string? Model)> Attempts { get; } = [];
        public int Interrupts;

        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;
        public int CurrentTurnAgentDepth => 0;
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool OrchestrationActive => false;
        // Номер поданных прогонам ходов (калька ClaudeSession.SubmittedTurnSeq): растёт на
        // каждой подаче. Тесты, которым метка не нужна, эмитят ExitedMessage() с TurnSeq=0 —
        // адаптер трактует такое exited как своё (fail-open), поведение прежнее.
        public long SubmittedTurnSeq { get; private set; }

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false)
        {
            SubmittedTurnSeq++;
            Attempts.Add((Info.Provider ?? "", Info.Model));
            if (Scripts.Count > 0) Scripts.Dequeue()();
            return Task.CompletedTask;
        }

        // В тестах обработчик (перехват фолбэка + коллектор) отрабатывает синхронно
        public void Emit(ServerMessage msg) => Sink?.Invoke(msg).GetAwaiter().GetResult();

        public Task StartAsync() => Task.CompletedTask;
        public Task CompactAsync() => Task.CompletedTask;
        public void RespondPermission(string requestId, string behavior) { }
        public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
        public void RespondPlan(string requestId, bool approve, string? feedback) { }
        public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
        public bool TrySetModelLive(string model) => false;
        public void Interrupt() => Interrupts++;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ClaudeSubscriptionPool BuildPool(params string[] keys)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in keys)
            dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static LlmProviderRegistry BuildProviders()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Пул с DisplayName у части подписок: ключ → имя (пустое имя — подписка без DisplayName,
    // в ленте остаётся ключ). Для проверки, что пользовательский текст показывает имя, а не ключ.
    private static ClaudeSubscriptionPool BuildPoolWithNames(params (string Key, string DisplayName)[] subs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, name) in subs)
        {
            dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
            if (!string.IsNullOrWhiteSpace(name))
                dict[$"ClaudeSubscriptions:{key}:DisplayName"] = name;
        }
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Провайдер с DisplayName и моделью — чтобы проверить, что в ленту идёт имя провайдера.
    private static LlmProviderRegistry BuildProviderWithName(string key, string displayName, string modelId)
    {
        var dict = new Dictionary<string, string?>
        {
            [$"LlmProviders:{key}:ApiKey"] = "sk-test",
            [$"LlmProviders:{key}:AnthropicBaseUrl"] = "https://api.example.com",
            [$"LlmProviders:{key}:DisplayName"] = displayName,
            [$"LlmProviders:{key}:Models:0:Id"] = modelId,
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // N сторонних провайдеров p1..pN с моделями m1..mN — цепочка шагов, у каждого свой
    // провайдер и нет пула подписок. Каждый шаг цепочки = одна попытка = одна подмена
    // (IsProviderSwitch), поэтому потолок подмен здесь проверяется переходами по цепочке,
    // а не бесплатными ротациями пула (волна 2: только IsProviderSwitch тратит потолок).
    private static LlmProviderRegistry BuildChainProviders(int count)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 1; i <= count; i++)
        {
            dict[$"LlmProviders:p{i}:ApiKey"] = $"sk-{i}";
            dict[$"LlmProviders:p{i}:AnthropicBaseUrl"] = $"https://p{i}.example.com";
            dict[$"LlmProviders:p{i}:Models:0:Id"] = $"m{i}";
        }
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static string[] ChainModels(int count) =>
        Enumerable.Range(1, count).Select(i => $"m{i}").ToArray();

    // Сторонние провайдеры с ЗАЯВЛЕННЫМ окном моделей (LlmModelConfig.ContextWindow) — для
    // проверки фильтра ёмкости цепочки: кандидат с окном меньше контекста хода отсеивается.
    private static LlmProviderRegistry BuildWindowProviders(params (string Key, string Model, int Window)[] specs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, model, window) in specs)
        {
            dict[$"LlmProviders:{key}:ApiKey"] = $"sk-{key}";
            dict[$"LlmProviders:{key}:AnthropicBaseUrl"] = $"https://{key}.example.com";
            dict[$"LlmProviders:{key}:Models:0:Id"] = model;
            dict[$"LlmProviders:{key}:Models:0:ContextWindow"] = window.ToString();
        }
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Эмит «Prompt is too long» одной попытки: текст ошибки (ErrorText → ContextOverflow) +
    // result со статусом 400. Как на проде: CLI шлёт is_error-текст ПЕРЕД result.
    private static void EmitOverflow(FakeInnerAdapter inner, string text = "Prompt is too long.")
    {
        inner.Emit(new ErrorMessage(text, ExpectResultFollows: true));
        inner.Emit(ApiError("400"));
    }

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, LlmProviderRegistry? providers = null,
        string model = "sonnet", string provider = "acc-a",
        int? modelFallbackMax = null, string? ownerId = null,
        string[]? chain = null,
        Func<string?>? effectiveModel = null,
        ProviderHealthRegistry? health = null,
        Action? persist = null,
        ContextCapacityRegistry? capacity = null,
        Func<int>? lastContextTokens = null,
        Func<string, string, IReadOnlyList<string>?, int, bool, Task>? enqueueBypass = null,
        Action<string>? orchestrationDone = null)
    {
        var session = new Session { Model = model, Provider = provider, OwnerId = ownerId };
        var inner = new FakeInnerAdapter(session);
        // Прокидываем отдельный экземпляр стора с заданным потолком: тестам нужно
        // явно зафиксировать то или иное значение, иначе они зависели бы от файла
        // data/model-fallback.json на хостовой машине. На каждый SUT — свежая
        // временная папка, чтобы значения не утекали между тестами.
        FallbackSettingsStore? store = null;
        if (modelFallbackMax is { } v)
        {
            store = new FallbackSettingsStore(BuildTempConfiguration(out _), log: null);
            Assert.Null(store.SetGlobal(v));
        }
        var sut = new FallbackLlmSessionAdapter(inner,
            // По умолчанию эффективная модель следует за session.Model (обновляется ApplyTarget).
            // Явный effectiveModel имитирует продовое EffectiveModel = Resolve(...) — НАМЕРЕНИЕ
            // хода, которое не следует за подменой на шаг цепочки стороннего провайдера
            // (инцидент 2026-08-07: повторы одной пары до потолка).
            effectiveModel ?? (() => session.Model),
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store,
            effectiveChain: chain is null ? null : () => chain,
            health: health,
            persist: persist,
            capacity: capacity,
            lastContextTokens: lastContextTokens,
            enqueueBypass: enqueueBypass,
            orchestrationDone: orchestrationDone);
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    // Конфиг с уникальным DataPath во временной папке (как в SpecialtyTemplatesServiceTests):
    // стор пишет файл рядом, и без изоляции по путям один тест протёк бы в другой.
    private static IConfiguration BuildTempConfiguration(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "ccs_fb_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(tempDir, "projects.json"),
            }).Build();
    }

    private static ResultMessage Success() => new("success", 100, 1, null, null);
    private static ResultMessage ApiError(string status) => new("success", 100, 1, null, null, ApiErrorStatus: status);

    private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Не дождался: {what}");
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    [Fact]
    public async Task Ошибка429_РотацияПодписки_ПовторУспешен()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new ErrorMessage("API Error: 429 rate limit", ExpectResultFollows: true));
            inner.Emit(ApiError("429"));
        });
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[0].Provider.Should().Be("acc-a");
        inner.Attempts[1].Provider.Should().Be("acc-b");
        pool.IsExhausted("acc-a").Should().BeTrue("подписка с 429 помечается исчерпанной");
        pool.IsExhausted("acc-b").Should().BeFalse();
        // Смена подписки того же пула Claude (уровень 1) — ТИХО: без ProviderSwitchedMessage,
        // пользователь видит один ход. Маркер только при смене типа поставщика (уровень 2).
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("success");
        // Ход — одно сообщение пользователя, сколько бы попыток ни было
        inner.Info.MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task НеизвестнаяОшибка_ФолбэкНеЗапускается()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("418")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "result");

        inner.Attempts.Should().ContainSingle("неизвестная ошибка — fail-closed, без повтора");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.ApiErrorStatus.Should().Be("418");
        pool.IsExhausted("acc-a").Should().BeFalse("не лимитный класс — подписка не помечается");
    }

    [Fact]
    public async Task ОбрывПотока_ПослеПервогоТокена_ПерезапускНаДругойПодписке()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new TextDeltaMessage("начал отвечать и"));   // поток оборвался посреди ответа
            inner.Emit(new ExitedMessage());                       // процесс умер без result
        });
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2, "обрыв stream — ошибка доставки, ход перезапускается");
        inner.Attempts[1].Provider.Should().Be("acc-b");
        pool.IsExhausted("acc-a").Should().BeFalse("обрыв — не квота аккаунта, подписка не банится");
    }

    [Fact]
    public async Task InterruptПользователя_ФолбэкНеЗапускается()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            sut.Interrupt();                       // пользователь нажал «Стоп»
            inner.Emit(new ExitedMessage());       // процесс убит interrupt'ом
        });

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ExitedMessage>().Any(), "exited");

        inner.Attempts.Should().ContainSingle("остановка пользователем — не ошибка доставки");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task ИсчерпаниеЦепочки_ФинальныйСбойСоСледомПар()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        // Финал — ошибочный result: существующий путь TaskExecutionService пометит
        // задачу исполнителя сбоем и уведомит постановщика
        var final = Downstream().OfType<ResultMessage>().Should().ContainSingle().Subject;
        final.Subtype.Should().Be("error");
        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Contain("Ни одна из доступных моделей не ответила");
        // У подписок пула без DisplayName имя пустое — фолбэк «Аккаунт Claude», сырые
        // ключи acc-a/acc-b в пользовательский текст не попадают
        error.Text.Should().Contain("Аккаунт Claude");
        error.Text.Should().NotContain("acc-a").And.NotContain("acc-b");
        error.Text.Should().Contain("слишком много запросов", "429 → RateLimit → человекочитаемая причина");
        error.Text.Should().Contain("Попробуйте позже или выберите другую модель в настройках чата.");
    }

    // Волна 2: потолок подмен тратится только на смену ТИПА поставщика (шаг цепочки), а
    // ротации подписок пула бесплатны. Поэтому потолок проверяется на цепочке сторонних
    // провайдеров (каждый шаг — отдельная подмена), а не на ротациях пула.
    [Fact]
    public async Task ПотолокПятьПодмен_ШестаяПопыткаНеЗапускается()
    {
        var providers = BuildChainProviders(7);
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, providers, model: "m1", provider: "p1",
            chain: ChainModels(7), modelFallbackMax: 5);
        for (var i = 0; i < 7; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));   // обрыв — не помечает пул

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(6, "1 стартовая + максимум 5 подмен (переходов по цепочке)");
        inner.Attempts.Select(a => a.Provider).Distinct().Should().HaveCount(6,
            "пара «модель × подписка» пробуется не более одного раза");
        // Каждый переход по цепочке — маркер; 5 переходов до упора в потолок
        Downstream().OfType<ProviderSwitchedMessage>().Should().HaveCount(5);
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error");
    }

    [Fact]
    public async Task ПотолокНеЗадан_ДефолтЧетыреПодмены()
    {
        // Без стора (modelFallbackMax = null) потолок — дефолт 4 (FallbackSettingsStore.DefaultMaxSubstitutions).
        // Цепочка из 6 сторонних шагов, но больше 5 попыток (1 стартовая + 4 подмены) не пройдёт.
        var providers = BuildChainProviders(6);
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, providers, model: "m1", provider: "p1",
            chain: ChainModels(6));
        for (var i = 0; i < 6; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(5, "1 стартовая + максимум 4 подмены по дефолту");
        Downstream().OfType<ProviderSwitchedMessage>().Should().HaveCount(4);
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error");
        Downstream().OfType<ErrorMessage>().Should().ContainSingle()
            .Which.Text.Should().Contain("Ни одна из доступных моделей не ответила");
    }

    [Fact]
    public async Task ВнеДиапазона_КлампитсяКДефолту()
    {
        // Потолок 99 в файле выходит за жёсткий 1..5 — стор отвергает значение (валидация),
        // global остаётся null, и читается дефолт 4. Иначе жёсткий потолок ADR был бы обойдён.
        var providers = BuildChainProviders(6);
        var chain = ChainModels(6);
        var pool = BuildPool("acc-a");
        var store = new FallbackSettingsStore(BuildTempConfiguration(out _));
        Assert.Equal("Потолок подмен должен быть в диапазоне 1..5", store.SetGlobal(99));
        var session = new Session { Model = "m1", Provider = "p1", OwnerId = null };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store,
            effectiveChain: () => chain);
        inner.Sink = sut.HandleMessageAsync;
        for (var i = 0; i < 6; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(5, "99 отвергнут валидацией → global=null → дефолт 4 → 1 + 4 подмены");
    }

    [Fact]
    public async Task ЛичныйПотолокВладельцаБьётГлобальный()
    {
        // Глобально потолок 1, лично у владельца — 5. per-owner слой перебивает глобальный.
        var providers = BuildChainProviders(7);
        var chain = ChainModels(7);
        var pool = BuildPool("acc-a");
        var store = new FallbackSettingsStore(BuildTempConfiguration(out _));
        Assert.Null(store.SetGlobal(1));
        Assert.Null(store.SetOwner("owner-x", 5));
        var session = new Session { Model = "m1", Provider = "p1", OwnerId = "owner-x" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store,
            effectiveChain: () => chain);
        inner.Sink = sut.HandleMessageAsync;
        for (var i = 0; i < 7; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(6, "per-owner 5 перебивает глобальный 1 → 1 + 5 подмен");
    }

    [Fact]
    public async Task ДругойВладелецНеВидитЛичныйПотолок()
    {
        // Личный потолок owner-x — 1, у другого владельца должна быть глобальная 5.
        // per-owner изоляция: значения одного владельца не должны просачиваться к другому.
        var providers = BuildChainProviders(7);
        var chain = ChainModels(7);
        var pool = BuildPool("acc-a");
        var store = new FallbackSettingsStore(BuildTempConfiguration(out _));
        Assert.Null(store.SetGlobal(5));
        Assert.Null(store.SetOwner("owner-x", 1));
        var session = new Session { Model = "m1", Provider = "p1", OwnerId = "owner-y" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store,
            effectiveChain: () => chain);
        inner.Sink = sut.HandleMessageAsync;
        for (var i = 0; i < 7; i++)
            inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(6,
            "owner-y не имеет своей записи → global=5 → 1 + 5 подмен");
    }

    // Волна 2: ротации подписок того же пула бесплатны — потолок подмен на них не тратится.
    // Пул из 3 подписок, потолок 1, цепочка [sonnet, deepseek-chat]. Все три sonnet-подписки
    // перепробуются (ротации, бесплатно), и лишь переход к deepseek тратит единственную
    // подмену. Если бы ротации считались, после acc-b (sub=1>=1) ход упал бы, и acc-c/deepseek
    // не позвались бы — а они зовутся.
    [Fact]
    public async Task РотацияПулаБесплатна_ПотолокТолькоНаШагЦепочки()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a", "acc-b", "acc-c");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"], modelFallbackMax: 1);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a — исчерпан → ротация acc-b (бесплатно)
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-b — исчерпан → ротация acc-c (бесплатно)
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-c — исчерпан → шаг цепочки deepseek (sub=1)
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // deepseek — substitutions=1>=1 → финал

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // 4 попытки: 3 бесплатных ротации пула + 1 платный переход к deepseek.
        inner.Attempts.Should().HaveCount(4);
        inner.Attempts[3].Should().Be(("deepseek", "deepseek-chat"), "единственная подмена — шаг цепочки");
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle();
    }

    // --- Цепочка пресета (ADR-007 §4, §8) ---

    [Fact]
    public async Task Цепочка_СбойШага1_ИдётНаШаг2()
    {
        // §8: ход с цепочкой при ошибке перезапускается на модели ВТОРОГО шага, а не на
        // модели-эквиваленте автоподбора. Цепочка: sonnet (нативный claude) → deepseek-chat.
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, BuildProviders(), model: "sonnet",
            chain: ["sonnet", "deepseek-chat"]);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // шаг 1 (sonnet, acc-a) — сбой
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // шаг 2 (deepseek-chat) — успех

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");
        // restore модели выполняется в finally — после выпускания result. Ждём полного
        // завершения оркестрации, иначе Info читается до восстановления.
        await WaitForAsync(() => !sut.FallbackTurnActive, "завершение оркестрации (restore в finally)");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[0].Should().Be(("acc-a", "sonnet"));
        inner.Attempts[1].Should().Be(("deepseek", "deepseek-chat"), "шаг 2 цепочки, а не автоподбор");
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Label.Should().Contain("Цепочка пресета");
        // Волна 2: подмена не персистится — после хода модель чата восстановлена в исходную.
        inner.Info.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task ЦепочкаИсчерпана_АвтоподборНеСрабатывает()
    {
        // §8: при цепочке автоподбор выключен — исчерпание цепочки = финальный сбой,
        // к сторонним провайдерам сами не уходим (уровень 2 не работает).
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
            ["LlmProviders:glm:ApiKey"] = "sk-glm",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com",
            ["LlmProviders:glm:Models:0:Id"] = "glm-4",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "deepseek-chat"]);
        // Все попытки — 429 (шаг 1 sonnet × acc-a, шаг 2 deepseek-chat)
        for (var i = 0; i < 5; i++)
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // Ровно две попытки — цепочка исчерпана, к glm (автоподбор) НЕ ушли
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts.Should().NotContain(p => p.Provider == "glm",
            "автоподбор при цепочке выключен");
        Downstream().OfType<ResultMessage>().Single().Subtype.Should().Be("error");
    }

    // Репро инцидента 2026-08-08 (волны 1+2): чат с замороженной opus (нативный claude), цепочка
    // пресета «Основной — Сильный»: opus → kimi-k3 → glm-5.2. Обе подписки Claude исчерпаны —
    // ход уходит на kimi-k3 (шаг 2 цепочки) одним маркером «Цепочка пресета: шаг 2», а не в
    // алфавитный автоподбор alibabacloud/qwen. Провайдеры вне цепочки не зовутся вовсе.
    [Fact]
    public async Task РепроИнцидента_ОбеПодпискиClaudeМертвы_ИдёмНаШагЦепочки()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:kimi:ApiKey"] = "sk-kimi",
            ["LlmProviders:kimi:AnthropicBaseUrl"] = "https://kimi.example.com",
            ["LlmProviders:kimi:Models:0:Id"] = "kimi-k3",
            // Сторонний провайдер ВНЕ цепочки — автоподбор не должен к нему уйти
            ["LlmProviders:alibabacloud:ApiKey"] = "sk-ali",
            ["LlmProviders:alibabacloud:AnthropicBaseUrl"] = "https://ali.example.com",
            ["LlmProviders:alibabacloud:Models:0:Id"] = "qwen-max",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("claude", "claude-2");   // обе подписки пула Claude
        var (sut, inner) = BuildSut(pool, providers, model: "opus", provider: "claude",
            chain: ["opus", "kimi-k3", "glm-5.2"], effectiveModel: () => "opus");
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // claude × opus — исчерпан
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // claude-2 × opus — исчерпан
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // kimi-k3 (шаг 2) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // Две попытки opus (ротация подписок пула, бесплатно) → шаг 2 цепочки kimi-k3.
        // alibabacloud/qwen (автоподбор) НЕ зовётся — автоподбора больше нет.
        inner.Attempts.Should().HaveCount(3);
        inner.Attempts[0].Should().Be(("claude", "opus"));
        inner.Attempts[1].Should().Be(("claude-2", "opus"), "уровень 1: ротация подписок пула");
        inner.Attempts[2].Should().Be(("kimi", "kimi-k3"), "шаг 2 цепочки, а не автоподбор");
        inner.Attempts.Should().NotContain(p => p.Provider == "alibabacloud",
            "автоподбор удалён: провайдеры вне цепочки не зовутся");
        var marker = Downstream().OfType<ProviderSwitchedMessage>().Single();
        marker.Label.Should().Contain("Цепочка пресета: шаг 2");
        marker.Provider.Should().Be("kimi");
    }

    [Fact]
    public async Task RateLimitRejected_СнимаетСПаузыИРотирует()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
            inner.Emit(new RateLimitMessage("five_hour", ResetsAt: null, Status: "rejected")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Interrupts.Should().Be(1, "ход, вставший на паузу до сброса окна, снимается interrupt'ом");
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("acc-b");
        pool.IsExhausted("acc-a").Should().BeTrue();
    }

    // M1: rate_limit_event приходит через SessionManager.OnMessageAsync, который
    // своим ходом ВЫЗЫВАЕТ _pool.MarkExhausted и TryPoolFailover. Под фолбэк-оркестрацией
    // ротацией владеет адаптер; если оба отреагируют — на одно событие уйдут ДВА
    // provider_switched, а Info.Provider уже сменён адаптером и поздний MarkExhausted
    // SessionManager'а банит только что выбранную здоровую подписку. Гард в SessionManager
    // (entry.Process is FallbackLlmSessionAdapter && fb.FallbackTurnActive → return) это
    // закрывает. Здесь проверяем сценарий «оркестратор сам пометил нужную подписку»:
    // на одну попытку с rate_limit оркестратор выставил ровно один provider_switched.
    [Fact]
    public async Task ГонкаСессионногоФейловера_НетДубляМаркера()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
            inner.Emit(new RateLimitMessage("five_hour", ResetsAt: null, Status: "rejected")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // Подмена уровня 1 (ротация подписок того же пула) проходит тихо — маркера в ленте
        // нет вовсе, дублить нечего. На стороне SessionManager гард в тестах не симулируется
        // (этот SUT не SessionManager), но в проде при активном FallbackTurnActive он отрежет
        // свой MarkExhausted/ProviderSwitchedMessage.
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
    }

    // M1, продолжение: при оркестрации адаптер сам помечает исчерпанной ПРЕЖНЮЮ
    // подписку (ту, на которой пришёл rate_limit) и выбирает НОВУЮ. Проверяем:
    // 1) acc-a помечена исчерпанной, 2) acc-b — нет (только что выбрана),
    // 3) переключение пошло.
    [Fact]
    public async Task ГонкаСессионногоФейловера_ЗдороваяПодпискаНеБанится()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
            inner.Emit(new RateLimitMessage("five_hour", ResetsAt: null, Status: "rejected")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[0].Provider.Should().Be("acc-a");
        inner.Attempts[1].Provider.Should().Be("acc-b", "оркестратор выбрал acc-b, а не остался на acc-a");
        pool.IsExhausted("acc-a").Should().BeTrue("оркестратор сам пометил исчерпанную");
        pool.IsExhausted("acc-b").Should().BeFalse(
            "только что выбранная здоровая подписка не должна быть помечена исчерпанной");
    }

    // Классификация причины попадает в ProviderSwitchedMessage.Reason — фронт
    // по нему выбирает каноническую формулировку подсказки. RateLimit → rate_limit.
    // Проверяем на переходе по цепочке (смена типа поставщика): на уровне 1 маркера в ленте нет.
    [Fact]
    public async Task RateLimit_ПричинаВМаркере_RateLimit()
    {
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, BuildProviders(), model: "sonnet",
            chain: ["sonnet", "deepseek-chat"]);
        inner.Scripts.Enqueue(() =>
            inner.Emit(new RateLimitMessage("five_hour", ResetsAt: null, Status: "rejected")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ProviderSwitchedMessage>().Any(), "маркер");

        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Reason.Should().Be("rate_limit");
    }

    // Обрыв потока посреди ответа: класс Unreachable → wire-имя unreachable.
    // Также переход по цепочке (смена типа поставщика) — на уровне 1 маркера в ленте нет.
    [Fact]
    public async Task ОбрывПотока_ПричинаВМаркере_Unreachable()
    {
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, BuildProviders(), model: "sonnet",
            chain: ["sonnet", "deepseek-chat"]);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new TextDeltaMessage("начал"));
            inner.Emit(new ExitedMessage());
        });
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ProviderSwitchedMessage>().Any(), "маркер");

        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Reason.Should().Be("unreachable");
    }

    // Ротация подписок того же пула (уровень 1) — модель не меняется, адаптер не
    // шлёт ProviderSwitchedMessage c Model (Label=null), сессионный фейловер мог
    // бы прислать свой. Маркера в ленте нет — это и есть «уровень 1 без пилюли».
    [Fact]
    public async Task ГонкаСессионногоФейловера_Уровень1_БезМаркера()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
            inner.Emit(new RateLimitMessage("five_hour", ResetsAt: null, Status: "rejected")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // Уровень 1 (ротация подписок того же пула) — ТИХИЙ: ProviderSwitchedMessage не
        // шлётся вовсе, сессионный фейловер тут не идёт (мы не SessionManager). Маркеров
        // в ленте быть не должно — поэтому BeEmpty, а не «не больше одного»: иначе assertion
        // прошёл бы, даже если маркеры снова начнут слать.
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
    }

    // Волна 2 (ADR-007 §4): автоподбор удалён полностью. Цепочка из одной модели (хвоста нет,
    // слот пустой) после исчерпания пула завершается честной ошибкой — к сторонним провайдерам
    // сами не уходим. Это и есть удаление уровня 2: без настроенного хвоста магии нет.
    // Сторож продовому входу: ClaudeSession.EffectiveTurnChain всегда отдаёт минимум один
    // элемент, поэтому chain:["opus"] имитирует реальный ход с замороженной моделью персоны.
    [Fact]
    public async Task ЦепочкаОднойМодели_ПулИсчерпан_ФинальнаяОшибка_АвтоподборНеСрабатывает()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:alibabacloud:ApiKey"] = "sk-ali",
            ["LlmProviders:alibabacloud:AnthropicBaseUrl"] = "https://ali.example.com",
            ["LlmProviders:alibabacloud:Models:0:Id"] = "qwen-max",
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("claude-1");
        var (sut, inner) = BuildSut(pool, providers, model: "opus", provider: "claude-1",
            chain: ["opus"], effectiveModel: () => "opus");
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // claude-1/opus — исчерпан

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // Одна попытка: пул claude исчерпан, цепочки (хвоста) нет → честная ошибка.
        // К сторонним провайдерам (alibabacloud/deepseek) сами не уходим — автоподбора нет.
        inner.Attempts.Should().ContainSingle();
        inner.Attempts.Should().NotContain(p => p.Provider == "alibabacloud" || p.Provider == "deepseek",
            "автоподбор удалён: Enabled не обходится");
        Downstream().OfType<ResultMessage>().Single().Subtype.Should().Be("error");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
    }

    // Смена аккаунта внутри пула Claude (уровень 1) проходит ТИХО — без ProviderSwitchedMessage;
    // маркер появляется только при переходе по цепочке на стороннего провайдера.
    [Fact]
    public async Task СменаАккаунтаВнутриПула_БезМаркера_СменаТипа_СМаркером()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"]);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a — исчерпан → уровень 1 (acc-b, тихо)
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-b — исчерпан → шаг цепочки (deepseek, маркер)
        inner.Scripts.Enqueue(() => inner.Emit(Success()));         // deepseek — успех

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        inner.Attempts.Should().HaveCount(3);
        inner.Attempts[0].Should().Be(("acc-a", "sonnet"));
        inner.Attempts[1].Should().Be(("acc-b", "sonnet"));
        inner.Attempts[2].Should().Be(("deepseek", "deepseek-chat"));
        // Ровно один маркер — только переход acc-b → deepseek (шаг цепочки, смена типа поставщика);
        // переход acc-a → acc-b внутри пула Claude прошёл без маркера.
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Provider.Should().Be("deepseek");
    }

    // Постановка «Человеческий текст ошибки»: в ленте нет служебных терминов
    // («фолбэк», «подмена», «потолок», «пара»), есть три блока (заголовок, попытки,
    // подсказка), разделённые пустой строкой; по строке на попытку. ChatItemView
    // рендерит переносы (white-space: pre-wrap), так что обходной « · » не нужен.
    // Модель в тексте не упоминается — только поставщик и причина.
    [Fact]
    public async Task Исчерпание_ЧеловеческийТекст_БезСлужебныхТерминов()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        error.Text.Should().Contain("Ни одна из доступных моделей не ответила");
        error.Text.Should().Contain("Попробуйте позже или выберите другую модель в настройках чата.");
        // Служебных терминов и технических деталей в ленте нет — они ушли в лог
        error.Text.Should().NotContain("фолбэк");
        error.Text.Should().NotContain("Фолбэк");
        error.Text.Should().NotContain("подмен");
        error.Text.Should().NotContain("потолок");
        error.Text.Should().NotContain("пар:");
        error.Text.Should().NotContain("Последняя ошибка");
        // Модель попытки (sonnet) в пользовательском тексте не упоминается
        error.Text.Should().NotContain("sonnet");
        // Три блока, разделённые пустой строкой; по строке на попытку.
        var blocks = error.Text.Split("\n\n");
        blocks.Should().HaveCount(3, "заголовок / список попыток / подсказка");
        blocks[0].Should().StartWith("Ни одна из доступных моделей не ответила");
        var attemptLines = blocks[1].Split('\n');
        attemptLines.Should().HaveCount(2, "по строке на попытку");
        // Подписки пула без DisplayName → фолбэк «Аккаунт Claude» в каждой строке попытки;
        // сырых ключей acc-a/acc-b в пользовательском тексте нет
        attemptLines[0].Should().Contain("Аккаунт Claude");
        attemptLines[1].Should().Contain("Аккаунт Claude");
        error.Text.Should().NotContain("acc-a").And.NotContain("acc-b");
        blocks[2].Should().StartWith("Попробуйте позже");
        // Обходного разделителя « · » в сообщении больше нет
        error.Text.Should().NotContain(" · ");
    }

    // Имя поставщика вместо ключа: подписка с DisplayName показывается именем (ключ
    // acc-a не виден), без DisplayName — нейтральным фолбэком «Аккаунт Claude» (сырой
    // ключ acc-b в текст не попадает). DisplayName провайдера тоже подставляется.
    // Проверка постановки «Имя поставщика вместо ключа».
    [Fact]
    public async Task Исчерпание_ИмяПоставщикаВместоКлюча()
    {
        var pool = BuildPoolWithNames(("acc-a", "Claude 2 (Max)"), ("acc-b", ""));
        var providers = BuildProviderWithName("deepseek", "DeepSeek", "deepseek-chat");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"]);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a (Claude 2 Max) — RateLimit
        inner.Scripts.Enqueue(() => inner.Emit(new ExitedMessage()));// acc-b (без имени) — обрыв, Unreachable
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // deepseek (DeepSeek) — RateLimit

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        var error = Downstream().OfType<ErrorMessage>().Should().ContainSingle().Subject;
        // Подписка с DisplayName → имя, ключа acc-a в ленте нет
        error.Text.Should().Contain("Claude 2 (Max)");
        error.Text.Should().NotContain("acc-a");
        // Подписка без DisplayName → фолбэк «Аккаунт Claude», сырой ключ acc-b не виден
        error.Text.Should().Contain("Аккаунт Claude");
        error.Text.Should().NotContain("acc-b");
        // Провайдер с DisplayName → имя
        error.Text.Should().Contain("DeepSeek");
        // Человекочитаемые причины: 429 → RateLimit → «слишком много запросов»,
        // обрыв (ExitedMessage) → Unreachable → «сервис не отвечает»
        error.Text.Should().Contain("слишком много запросов").And.Contain("сервис не отвечает");
        inner.Attempts.Should().HaveCount(3);
    }

    // Волна 2: подмена не переписывает модель чата навсегда. После хода с переходом по цепочке
    // Info.Model был переписан ApplyTarget в deepseek-chat, но в finally восстанавливается
    // исходная sonnet — чат не залипает на подмене (инцидент 2026-08-07: залипание на qwen).
    [Fact]
    public async Task ПослеХодаСПодменой_МодельЧатаВосстанавливается()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"]);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a → шаг цепочки deepseek
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // deepseek — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
        await WaitForAsync(() => !sut.FallbackTurnActive, "завершение оркестрации (restore в finally)");

        // Внутри хода Info.Model был переписан в deepseek-chat, но в finally восстановлена
        // исходная sonnet — подмена не персистится в модель чата.
        inner.Info.Model.Should().Be("sonnet", "подмена восстанавливается после хода");
        inner.Info.Provider.Should().Be("acc-a", "провайдер восстанавливается после хода");
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("deepseek", "deepseek-chat"), "ход действительно шёл на deepseek");
    }

    // CAS: если во время хода модель сменили руками (Info != applied), finally НЕ восстанавливает —
    // выбор пользователя побеждает, подмена его не перетирает. Пользовательский ввод имитируем
    // перезаписью Info в скрипте успешной попытки (гонка с UI, пока оркестратор держит ход).
    [Fact]
    public async Task РучнаяСменаМоделиВоВремяХода_НеПеретираетсяCAS()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"]);
        var session = inner.Info;
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a → шаг цепочки deepseek
        inner.Scripts.Enqueue(() =>
        {
            // Во время успешной попытки пользователь сменил модель руками (гонка с UI):
            // Info переписан вручную, оркестратор этого не делал.
            session.Model = "manual-choice";
            session.Provider = "manual-prov";
            inner.Emit(Success());
        });

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
        await WaitForAsync(() => !sut.FallbackTurnActive, "завершение оркестрации (restore в finally)");

        // CAS: Info != applied (manual-choice ≠ deepseek-chat) → finally не восстанавливает.
        session.Model.Should().Be("manual-choice", "ручной выбор пользователя не перетёрт подменой");
        session.Provider.Should().Be("manual-prov");
    }

    // --- Кулдаун недоступности провайдера (волна 2) ---

    // Кулдаун: шаг цепочки с провайдером в кулдауне пропускается, фолбэк идёт к следующему
    // живому шагу. Цепочка [sonnet, m-cooled, m-alive], p-cooled помечен недоступным заранее.
    [Fact]
    public async Task КулдаунПровайдера_ШагЦепочкиПропускается()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p-cooled:ApiKey"] = "sk-c",
            ["LlmProviders:p-cooled:AnthropicBaseUrl"] = "https://c.example.com",
            ["LlmProviders:p-cooled:Models:0:Id"] = "m-cooled",
            ["LlmProviders:p-alive:ApiKey"] = "sk-a",
            ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://a.example.com",
            ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var health = new ProviderHealthRegistry();
        health.MarkUnavailable("p-cooled");   // шаг 2 цепочки — в кулдауне
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "m-cooled", "m-alive"], health: health);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a → цепочка: шаг 2 (p-cooled) пропущен
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // m-alive (шаг 3) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // Шаг 2 (m-cooled/p-cooled) в кулдауне — пропущен; попытка 2 = m-alive (шаг 3).
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-alive", "m-alive"), "шаг 2 в кулдауне пропущен");
        Downstream().OfType<ProviderSwitchedMessage>().Single().Label.Should().Contain("шаг 3");
    }

    // fail-open: ВСЕ шаги цепочки в кулдауне — берём первого остывшего (кулдаун — наблюдение,
    // а не запрет; эндпоинт мог уже подняться, а ход должен идти).
    [Fact]
    public async Task КулдаунПровайдера_FailOpen_БерётОстывшего()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p-cooled:ApiKey"] = "sk-c",
            ["LlmProviders:p-cooled:AnthropicBaseUrl"] = "https://c.example.com",
            ["LlmProviders:p-cooled:Models:0:Id"] = "m-cooled",
            ["LlmProviders:p-alive:ApiKey"] = "sk-a",
            ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://a.example.com",
            ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var health = new ProviderHealthRegistry();
        health.MarkUnavailable("p-cooled");
        health.MarkUnavailable("p-alive");   // ВСЕ шаги цепочки в кулдауне
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "m-cooled", "m-alive"], health: health);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a → цепочка: все в кулдауне → fail-open
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // m-cooled (первый остывший) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // Все в кулдауне → fail-open берёт первого остывшего (m-cooled, шаг 2).
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-cooled", "m-cooled"), "fail-open: первый остывший шаг цепочки");
        Downstream().OfType<ProviderSwitchedMessage>().Single().Label.Should().Contain("шаг 2");
    }

    // Стартовый СТОРОННИЙ провайдер в кулдауне → стартуем сразу с первого живого шага цепочки
    // (маркер provider_switched, Reason=unreachable), не тратя попытку на мёртвый эндпоинт.
    // Подписки пула Claude кулдауном не помечаются (Major 2) — стартовую подмену проверяем
    // на стороннем провайдере, как и бывает в проде после фикса.
    [Fact]
    public async Task КулдаунСтартовогоПровайдера_СтартСЖивогоШага()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p-dead:ApiKey"] = "sk-d",
            ["LlmProviders:p-dead:AnthropicBaseUrl"] = "https://d.example.com",
            ["LlmProviders:p-dead:Models:0:Id"] = "m-dead",
            ["LlmProviders:p-alive:ApiKey"] = "sk-a",
            ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://a.example.com",
            ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var health = new ProviderHealthRegistry();
        health.MarkUnavailable("p-dead");   // стартовый сторонний провайдер в кулдауне
        var (sut, inner) = BuildSut(pool, providers, model: "m-dead", provider: "p-dead",
            chain: ["m-dead", "m-alive"], health: health);
        inner.Scripts.Enqueue(() => inner.Emit(Success()));   // старт на m-alive через стартовую подмену

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // Стартовая подмена: p-dead в кулдауне → одна попытка сразу на m-alive, без расхода на p-dead.
        inner.Attempts.Should().ContainSingle();
        inner.Attempts[0].Should().Be(("p-alive", "m-alive"), "старт с живого шага, а не с кулдаунного p-dead");
        var marker = Downstream().OfType<ProviderSwitchedMessage>().Single();
        marker.Reason.Should().Be("unreachable");
        marker.Provider.Should().Be("p-alive");
    }

    // --- Переполнение контекста (ContextOverflow) ---
    // Репро инцидента 09.08: фолбэк ушёл на шаг цепочки, но ПРИНЯВШАЯ модель не переварила
    // контекст → ход умер с «Prompt is too long». Теперь overflow — шаг по цепочке, а не смерть хода.

    // Overflow уводит на следующий шаг цепочки, а не убивает ход. Подписка пула НЕ помечается
    // исчерпанной (окно — не квота), эндпоинт НЕ помечается недоступным (он ответил). skip уровня 1
    // (ротация подписок той же модели): у m2 нет окна в каталоге → fail-open → шаг происходит.
    [Fact]
    public async Task Overflow_УводитНаШагЦепочки_НеУбиваяХод()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p2", "m2", 1_000_000));
        var capacity = new ContextCapacityRegistry();
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m2"], capacity: capacity, lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet × acc-a — overflow
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m2 (шаг 2) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");
        await WaitForAsync(() => !sut.FallbackTurnActive, "завершение оркестрации");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p2", "m2"), "overflow → шаг цепочки, а не смерть хода");
        // Наблюдение записано: модель не приняла ~100k токенов
        capacity.ObservedCeiling("sonnet").Should().Be(100_000);
        // Overflow — не квота и не мёртвый эндпоинт: подписка и здоровье не тронуты
        pool.IsExhausted("acc-a").Should().BeFalse("overflow — не лимит аккаунта");
        // Маркер подмены несёт reason=context_overflow (фронт покажет каноничную формулировку)
        Downstream().OfType<ProviderSwitchedMessage>().Single().Reason.Should().Be("context_overflow");
    }

    // Кандидат цепочки с ЗАЯВЛЕННЫМ окном меньше контекста хода — пропускается (модель точно
    // упадёт с тем же overflow). Цепочка [sonnet, m-small(50k), m-big(500k)], контекст 100k.
    [Fact]
    public async Task Overflow_КандидатСМеньшимОкном_Пропускается()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 50_000), ("p-big", "m-big", 500_000));
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small", "m-big"], capacity: new(), lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet — overflow
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-big — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // m-small (шаг 2, окно 50k) отсеян — контекст 100k; попытка 2 = m-big (шаг 3)
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-big", "m-big"), "шаг 2 (m-small, 50k) пропущен — идём к шагу 3");
        Downstream().OfType<ProviderSwitchedMessage>().Single().Label.Should().Contain("шаг 3");
    }

    // Наблюдение точнее конфига: m-small заявлено 200k (прошло бы по конфигу), но в прошлых ходах
    // модель упала на 80k → effective=min(200000, 80000)=80k < 100k → отсеивается по наблюдению.
    [Fact]
    public async Task Overflow_НаблюдениеТочнееКонфига_ОтсеиваетПоФакту()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 200_000), ("p-big", "m-big", 500_000));
        var capacity = new ContextCapacityRegistry();
        capacity.RecordOverflow("m-small", 80_000);   // в прошлом ходе m-small не принял 80k
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small", "m-big"], capacity: capacity, lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet — overflow
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-big — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // m-small отсеян по наблюдению (заявленное 200k обрезано наблюдённым 80k), а не по конфигу
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-big", "m-big"), "m-small отсеян по наблюдению, не по конфигу");
    }

    // Fail-open при отсутствии оценки контекста: accessor контекста не задан (ContextEstimate=0) →
    // WouldFit всегда true → шаг происходит даже на заведомо малое окно. Лучше попробовать, чем сдаться.
    [Fact]
    public async Task Overflow_НетОценкиКонтекста_FailOpen_ШагНаМалоеОкно()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 50_000));
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small"], capacity: new(), lastContextTokens: () => 0);  // оценки нет
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet — overflow
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-small (50k) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // Оценки контекста нет → фильтр по ёмкости выключен → шаг на m-small несмотря на малое окно
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-small", "m-small"), "fail-open: без оценки контекста шаг не отсеивается");
    }

    // Все шаги цепочки не вмещают контекст → финальный текст про окно (/compact / смена модели),
    // а не общее «модели не ответили». Без зацикливания: одна попытка, отсеянный шаг не пробуется.
    [Fact]
    public async Task Overflow_ВсеШагиНеВмещают_ФинальныйТекстПроОкно()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 50_000));
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small"], capacity: new(), lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet — overflow; m-small (50k) отсеян → финал

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // Только sonnet — m-small отсеян WouldFit, зацикливания нет
        inner.Attempts.Should().ContainSingle();
        // Финальный текст — про переполнение окна и действия, а не про лежащие эндпоинты
        var error = Downstream().OfType<ErrorMessage>().First(m => m.Text.Contains("/compact"));
        error.Text.Should().Contain("ни одна из доступных моделей не смогла");
        error.Text.Should().NotContain("Ни одна из доступных моделей не ответила");
        var final = Downstream().OfType<ResultMessage>().Should().ContainSingle().Subject;
        final.Subtype.Should().Be("error");
    }

    // --- Фиксы ревью ContextOverflow (Major 1–3, Minor-review) ---

    // Major 1: граница contextTokens == observed. m-small заявлено 200k (прошло бы по конфигу),
    // но в прошлых ходах упала на 100k → при повторе с тем же контекстом 100k её отсеивать ОБЯЗАНЫ.
    // Старый код сводил к min(200000, 100000)=100000 >= 100000 → пропускал отказавшую модель, и она
    // падала снова. После фикса observed сравнивается СТРОГО: contextTokens >= observed → отсеиваем.
    [Fact]
    public async Task Overflow_ГраницаКонтекстРавенНаблюдению_ОтсеиваетсяСтрого()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 200_000), ("p-big", "m-big", 500_000));
        var capacity = new ContextCapacityRegistry();
        capacity.RecordOverflow("m-small", 100_000);   // упала на 100k
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small", "m-big"], capacity: capacity, lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet — overflow
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-big — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // m-small отсеян НА ГРАНИЦЕ (контекст 100k == наблюдению 100k), а не пропущен — попытка 2 = m-big.
        // При старом баге (min >= context) m-small прошёл бы, и попытка 2 упала бы на нём с тем же overflow.
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-big", "m-big"),
            "контекст == наблюдённому размеру отказа → модель отсеивается строго, не проходит по равенству");
    }

    // Major 3: фильтр по ёмкости применяется ТОЛЬКО при ContextOverflow. Для RateLimit размер
    // контекста ни при чём, а оценка измерена токенизатором прошлой модели — она не должна молча
    // выкидывать живого кандидата. Здесь m-small (50k) при контексте 100k на ошибке 429 ПРОБУЕТСЯ,
    // а не отсекается. При старом коде (фильтр для всех классов) она бы отсеклась → финал без попытки.
    [Fact]
    public async Task ФильтрЁмкости_ТолькоПриContextOverflow_RateLimitНеОтсекаетПоОкну()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 50_000));
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small"], capacity: new(), lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // sonnet/acc-a — RateLimit (не overflow!)
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // m-small (50k) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // При RateLimit фильтр по ёмкости выключен: m-small (50k) пробуется несмотря на контекст 100k.
        // Старый код (фильтр для всех классов) отсёк бы её и завершил ход одной попыткой.
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-small", "m-small"),
            "RateLimit не имеет отношения к размеру окна — кандидат не отсекается по ёмкости");
    }

    // Д4 (инцидент 2026-08-10): наблюдённый потолок ObservedCeiling — точный факт отказа модели,
    // поэтому отсекает шаг цепочки при ЛЮБОМ классе ошибки, не только при ContextOverflow. На проде
    // при Unreachable ход ушёл на kimi-k3 с контекстом ~830K (наблюдение уже было от прошлого overflow)
    // → гарантированный «Prompt is too long» и впустую потраченная подмена. Здесь обрыв sonnet
    // (Unreachable), шаг 2 m-prev уже падал на 80k → отсеивается, идём к m-alive (шаг 3). Заявленные
    // окна не заданы — фильтр работает только по наблюдению.
    [Fact]
    public async Task НеOverflow_НаблюдённыйПотолокОтсекаетШагЦепочки()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p-prev:ApiKey"] = "sk-prev",
            ["LlmProviders:p-prev:AnthropicBaseUrl"] = "https://prev.example.com",
            ["LlmProviders:p-prev:Models:0:Id"] = "m-prev",
            ["LlmProviders:p-alive:ApiKey"] = "sk-alive",
            ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://alive.example.com",
            ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var capacity = new ContextCapacityRegistry();
        capacity.RecordOverflow("m-prev", 80_000);   // в прошлом ходе m-prev не принял 80k
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-prev", "m-alive"], capacity: capacity, lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new TextDeltaMessage("начал"));   // поток оборвался — gotEvent
            inner.Emit(new ExitedMessage());             // процесс умер без result → Unreachable
        });
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-alive (шаг 3) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // m-prev отсеян по наблюдению при Unreachable (80k ≤ контекста 100k) — попытка 2 = m-alive.
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-alive", "m-alive"),
            "наблюдённый потолок отсекает шаг цепочки при любом классе ошибки, не только при overflow");
        Downstream().OfType<ProviderSwitchedMessage>().Single().Label.Should().Contain("шаг 3");
    }

    // Д4 (продолжение): при не-overflow классе неточное ЗАЯВЛЕННОЕ окно каталога НЕ отсекает
    // кандидата (Major 3 сохранён) — отсекает только точное наблюдение. Тот же Unreachable, но у
    // m-small лишь заявленное окно 50k (< контекста 100k), а наблюдения нет → кандидат проходит.
    [Fact]
    public async Task НеOverflow_ЗаявленноеОкноНеОтсекает_FailOpenПоНаблюдению()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-small", "m-small", 50_000));
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-small"], capacity: new(), lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new TextDeltaMessage("начал"));   // обрыв потока → Unreachable
            inner.Emit(new ExitedMessage());
        });
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-small (заявленное 50k) — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // m-small (заявленное 50k < контекста 100k) при Unreachable ПРОБУЕТСЯ — заявленное окно
        // отсекает только при ContextOverflow. Наблюдения нет → fail-open по нему.
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-small", "m-small"),
            "заявленное окно каталога не отсекает кандидата при не-overflow классе (Major 3 сохранён)");
    }

    // Major 3 (запас): заявленное окно, РАВНОЕ контексту, не проходит — нужен 10%-й запас на
    // расхождение токенизаторов. m-tight заявлено 100k, контекст 100k → отсеивается (100k < 110k),
    // идём к m-big. Без запаса (старое точное сравнение) модель «впритык» прошла бы и упала.
    [Fact]
    public async Task Overflow_ЗаявленноеОкноВпритык_ОтсекаетсяСЗапасом()
    {
        var pool = BuildPool("acc-a");
        var providers = BuildWindowProviders(("p-tight", "m-tight", 100_000), ("p-big", "m-big", 500_000));
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet",
            chain: ["sonnet", "m-tight", "m-big"], capacity: new(), lastContextTokens: () => 100_000);
        inner.Scripts.Enqueue(() => EmitOverflow(inner));   // sonnet — overflow
        inner.Scripts.Enqueue(() => inner.Emit(Success()));  // m-big — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        // m-tight (заявленное 100k == контексту 100k) отсеян с запасом 10% → попытка 2 = m-big.
        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Should().Be(("p-big", "m-big"),
            "заявленное окно «впритык» к контексту отсеивается — нужен запас на расхождение токенизаторов");
    }

    // --- Фиксы ревью Глеба (Major 1/2, Minor 4) ---

    // Major 1: restore модели в finally должен сопровождаться персистом — иначе финальный
    // result успевает сохранить sessions.json с ПОДМЕНЁННОЙ моделью (ApplyStatusAsync →
    // SaveSessions ДО finally), и рестарт сервера в окне залипил бы чат на подмене. Здесь
    // persist-колбэк пишет «стор» — проверяем, что после хода с подменой в нём исходная модель.
    [Fact]
    public async Task ПодменаМодели_PersistПослеВосстановления_ПишетИсходную()
    {
        var providers = BuildProviders();   // deepseek/deepseek-chat — сторонний шаг цепочки
        var pool = BuildPool("acc-a");
        var persisted = new List<(string? Model, string? Provider)>();
        FakeInnerAdapter? innerRef = null;
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"],
            persist: () => { if (innerRef is { } i) persisted.Add((i.Info.Model, i.Info.Provider)); });
        innerRef = inner;
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a → шаг цепочки deepseek
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // deepseek-chat — успех

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
        await WaitForAsync(() => !sut.FallbackTurnActive, "restore в finally");

        // Ход действительно уходил на подмену deepseek-chat…
        inner.Attempts[1].Should().Be(("deepseek", "deepseek-chat"));
        // …но persist после restore записал ИСХОДНУЮ модель (не подмену).
        persisted.Should().NotBeEmpty("restore в finally должен персистить восстановленные значения");
        persisted[^1].Model.Should().Be("sonnet", "persist пишет восстановленную модель, а не подмену");
        persisted[^1].Provider.Should().Be("acc-a");
        inner.Info.Model.Should().Be("sonnet");
    }

    // Major 2: кулдаун недоступности НЕ помечает подписки пула Claude — один 529 на нативной
    // подписке не должен уводить следующие ходы на сторонний шаг 2 мимо живой подписки пула.
    // Здесь: первый ход на opus/claude ловит 529 (ProviderError), ротация на claude-2 — успех.
    // Кулдаун на «claude» НЕ ставится → второй ход не уходит на kimi, а остаётся в пуле Claude.
    // После тихой ротации (IsProviderSwitch:false) Provider НЕ восстанавливается — ход реально
    // прошёл и свежий транскрипт записан на claude-2, поэтому второй ход стартует с claude-2.
    [Fact]
    public async Task Кулдаун_НеТрогаетПодпискиПула_СледующийХодНачинаетсяСНей()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:kimi:ApiKey"] = "sk-kimi",
            ["LlmProviders:kimi:AnthropicBaseUrl"] = "https://kimi.example.com",
            ["LlmProviders:kimi:Models:0:Id"] = "kimi-k3",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("claude", "claude-2");
        var health = new ProviderHealthRegistry();
        var (sut, inner) = BuildSut(pool, providers, model: "opus", provider: "claude",
            chain: ["opus", "kimi-k3"], effectiveModel: () => "opus", health: health);
        // Ход 1: claude × opus — 529 (ProviderError), ротация на claude-2 — успех.
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("529")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал хода 1");
        await WaitForAsync(() => !sut.FallbackTurnActive, "завершение хода 1");

        // Кулдаун на ключе подписки пула НЕ поставлен (Major 2) — иначе второй ход ушёл бы на kimi.
        health.IsUnavailable("claude").Should().BeFalse("подписки пула не помечаются кулдауном");
        // Тихая ротация оставила чат на claude-2 (где прошёл ход и лежит свежий транскрипт).
        inner.Info.Provider.Should().Be("claude-2", "после тихой ротации Provider не восстанавливается");

        // Ход 2: стартовая подмена проверяет кулдаун «claude-2» → false → старт с opus/claude-2.
        inner.Scripts.Enqueue(() => inner.Emit(Success()));
        await sut.SendMessageAsync("ещё");
        await WaitForAsync(() => inner.Attempts.Count >= 3, "старт хода 2");

        // Последняя попытка — старт второго хода с claude-2 × opus (в пуле Claude), а не kimi-k3.
        inner.Attempts[^1].Should().Be(("claude-2", "opus"),
            "второй ход стартует с подписки пула (claude-2 после тихой ротации), а не со шага 2 цепочки");
    }

    // Minor 4: покомпонентный CAS restore. Пользователь сменил во время хода ТОЛЬКО модель
    // (провайдер остался подменённым) — модель не восстанавливаем (ручной выбор), а провайдер
    // восстанавливаем. Раньше попарный CAS блокировал восстановление провайдера из-за модели.
    [Fact]
    public async Task Restore_ПокомпонентныйCAS_ПровайдерВосстанавливаетсяНезависимо()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "deepseek-chat"]);
        var session = inner.Info;
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // acc-a → шаг цепочки deepseek
        inner.Scripts.Enqueue(() =>
        {
            // Во время успешной попытки пользователь сменил ТОЛЬКО модель (провайдер deepseek
            // остался подменённым applied). Покомпонентный CAS: модель не трогаем, провайдер вернём.
            session.Model = "manual-choice";   // ≠ deepseek-chat → модель не восстанавливается
            inner.Emit(Success());
        });

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
        await WaitForAsync(() => !sut.FallbackTurnActive, "restore в finally");

        // Модель — ручной выбор пользователя (не перетёрта), провайдер — восстановлен в acc-a.
        session.Model.Should().Be("manual-choice", "ручная модель не перетёрта (CAS по модели не прошёл)");
        session.Provider.Should().Be("acc-a", "провайдер восстановлен покомпонентно (CAS по провайдеру прошёл)");
    }

    // --- Сохранение свежего транскрипта хода при restore (регрессия волны 2) ---
    //
    // TranscriptMigrator.TryMigrate — это File.Copy, а не move: ход на профиле A поймал ошибку
    // → транскрипт скопирован A→B → финальный ответ CLI записан ТОЛЬКО в профиль B. Восстановить
    // Provider в A — и следующий ход на --resume с A не вспомнит свой ответ, а TryPoolFailover
    // ещё и затрёт свежий B устаревшей копией A→B. Поэтому restore различает тип последней подмены:
    //   • тихая ротация подписки того же пула — Provider оставляем новым (транскрипт там);
    //   • смена типа поставщика — Provider восстанавливаем, но перед этим копируем транскрипт обратно.

    // Провайдер deepseek с профилем во временной папке (claude-profiles/deepseek). ClaudeUserProfileDir
    // указывает на несуществующую папку → синк настроек из ~/.claude не идёт (чистый тестовый профиль).
    private static LlmProviderRegistry BuildProvidersInDir(string baseDir)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(baseDir, "projects.json"),
            ["ClaudeUserProfileDir"] = Path.Combine(baseDir, "user-claude"),
            ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Цепочка сторонних провайдеров p1..pN (модели m1..mN), каждый со своим профилем во временной
    // папке (claude-profiles/p{i}). Нужна для тестов переноса транскрипта по цепочке подмен:
    // у каждого шага свой физический профиль, куда копируется транскрипт.
    private static LlmProviderRegistry BuildChainProvidersInDir(string baseDir, int count)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(baseDir, "projects.json"),
            ["ClaudeUserProfileDir"] = Path.Combine(baseDir, "user-claude"),
        };
        for (var i = 1; i <= count; i++)
        {
            dict[$"LlmProviders:p{i}:ApiKey"] = $"sk-{i}";
            dict[$"LlmProviders:p{i}:AnthropicBaseUrl"] = $"https://p{i}.example.com";
            dict[$"LlmProviders:p{i}:Models:0:Id"] = $"m{i}";
        }
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Перехват логов адаптера: проверяем, что провал обратного переноса не прошёл молча.
    private sealed class SpyLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    // (а) Тихая ротация подписки того же пула: Provider остаётся новым, обратного переноса нет.
    // Ход на acc-a поймал 429 → ротация на acc-b (тихо, IsProviderSwitch:false) → успех на acc-b.
    // Свежий ответ записан в профиль acc-b, поэтому чат оставляем на acc-b (транскрипт консистентен).
    [Fact]
    public async Task ТихаяРотацияПула_ProviderОстаётсяНовым_ТранскриптНеКопируетсяНазад()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_silent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var providers = BuildProvidersInDir(baseDir);
            var pool = BuildPool("acc-a", "acc-b");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-silent";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            var subAccA = Path.Combine(baseDir, "claude-profiles", "sub-acc-a");
            var subAccB = Path.Combine(baseDir, "claude-profiles", "sub-acc-b");
            var accAFile = Path.Combine(subAccA, "projects", flat, csid + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(accAFile)!);
            File.WriteAllText(accAFile, "QUESTION");

            var session = new Session { Model = "sonnet", Provider = "acc-a", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "sonnet" });
            inner.Sink = sut.HandleMessageAsync;
            var accBFile = Path.Combine(subAccB, "projects", flat, csid + ".jsonl");
            // acc-a — 429 → тихая ротация на acc-b (копия acc-a→acc-b). acc-b — успех + ответ CLI.
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() =>
            {
                File.AppendAllText(accBFile, "\nANSWER");
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Тихая ротация: Provider остался новым (acc-b) — там, где прошёл ход и лежит транскрипт.
            session.Provider.Should().Be("acc-b", "после тихой ротации Provider не восстанавливается");
            session.Model.Should().Be("sonnet");
            // Обратного переноса НЕ было: acc-a хранит исходный транскрипт без свежего ответа.
            File.ReadAllText(accAFile).Should().NotContain("ANSWER",
                "обратный перенос при тихой ротации не вызывается");
            // Свежий ответ остался в acc-b (там, где ход реально прошёл).
            File.ReadAllText(accBFile).Should().Contain("ANSWER");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // (б) Смена типа поставщика (шаг цепочки): Model/Provider восстановлены И транскрипт перенесён
    // обратно в профиль исходного провайдера. Проверяем факт наличия файла во временных профилях,
    // а не поле в памяти: acc-a после хода должен содержать свежий ответ, скопированный из deepseek.
    [Fact]
    public async Task СменаПоставщика_ModelProviderВосстановлены_ТранскриптПеренесёнНазад()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_switch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var providers = BuildProvidersInDir(baseDir);
            var pool = BuildPool("acc-a");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-switch";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            var subAccA = Path.Combine(baseDir, "claude-profiles", "sub-acc-a");
            var deepseek = Path.Combine(baseDir, "claude-profiles", "deepseek");
            var accAFile = Path.Combine(subAccA, "projects", flat, csid + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(accAFile)!);
            File.WriteAllText(accAFile, "QUESTION");

            var session = new Session { Model = "sonnet", Provider = "acc-a", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "sonnet", "deepseek-chat" });
            inner.Sink = sut.HandleMessageAsync;
            var deepseekFile = Path.Combine(deepseek, "projects", flat, csid + ".jsonl");
            // acc-a — 429 → шаг цепочки deepseek (копия acc-a→deepseek). deepseek — успех + ответ CLI.
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() =>
            {
                File.AppendAllText(deepseekFile, "\nANSWER");
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Смена поставщика: Model и Provider восстановлены в исходные (иначе пара opus × kimi).
            session.Model.Should().Be("sonnet", "модель восстановлена после смены поставщика");
            session.Provider.Should().Be("acc-a", "провайдер восстановлен после смены поставщика");
            // Обратный перенос: свежий ответ скопирован из deepseek в профиль acc-a — иначе
            // следующий ход на --resume с acc-a не нашёл бы ответ модели.
            File.ReadAllText(accAFile).Should().Contain("ANSWER",
                "после restore транскрипт перенесён обратно в профиль исходного провайдера");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // (в) Провал обратного переноса не роняет ход и не проходит молча: Model/Provider восстановлены,
    // result доставлен, в логе — Warning с id сессии. Профиль deepseek пустеет до restore →
    // TranscriptMigrator.TryMigrate не находит транскрипт и возвращает false.
    [Fact]
    public async Task СменаПоставщика_ПровалОбратногоПереноса_НеРоняетХод_ПишетWarning()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_fail_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var providers = BuildProvidersInDir(baseDir);
            var pool = BuildPool("acc-a");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-fail";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            var subAccA = Path.Combine(baseDir, "claude-profiles", "sub-acc-a");
            var deepseek = Path.Combine(baseDir, "claude-profiles", "deepseek");
            var accAFile = Path.Combine(subAccA, "projects", flat, csid + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(accAFile)!);
            File.WriteAllText(accAFile, "QUESTION");

            var session = new Session { Model = "sonnet", Provider = "acc-a", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var spy = new SpyLogger();
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "sonnet", "deepseek-chat" }, log: spy);
            inner.Sink = sut.HandleMessageAsync;
            var deepseekFile = Path.Combine(deepseek, "projects", flat, csid + ".jsonl");
            // acc-a — 429 → шаг цепочки deepseek (копия acc-a→deepseek). deepseek — успех, НО транскрипт
            // в deepseek пропал к моменту restore (профиль удалили/диск упал) → обратный перенос не найдёт источник.
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() =>
            {
                if (File.Exists(deepseekFile)) File.Delete(deepseekFile);
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Ход не роняется: Model/Provider восстановлены, result доставлен успехом.
            session.Model.Should().Be("sonnet");
            session.Provider.Should().Be("acc-a");
            Downstream().OfType<ResultMessage>().Should().ContainSingle()
                .Which.Subtype.Should().Be("success");
            // Провал переноса не прошёл молча: Warning с id сессии и признаком обратного переноса.
            spy.Entries.Should().Contain(e => e.Level == LogLevel.Warning
                && e.Message.Contains("Обратный перенос") && e.Message.Contains(csid),
                "провал обратного переноса пишется в лог с id сессии");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // --- Стартовая подмена по кулдауну: перенос транскрипта (Major) ---

    // Major: стартовый сторонний провайдер в кулдауне → старт сразу с живого шага цепочки.
    // Транскрипт обязан переехать в профиль шага ДО подмены: иначе --resume не найдёт разговор,
    // ход упадёт (класс None), и чат встанет колом до TTL кулдауна, хотя оба провайдера живы.
    // Здесь: p-dead в кулдауне, история лежит в профиле p-dead → после стартовой подмены она
    // оказывается в профиле p-alive (куда пойдёт --resume), а свежий ответ копируется обратно в p-dead.
    [Fact]
    public async Task СтартоваяПодмена_Кулдаун_ПереноситТранскриптВПрофильШага()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_start_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            // p-dead / p-alive — сторонние провайдеры со своими профилями во временной папке.
            var dict = new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(baseDir, "projects.json"),
                ["ClaudeUserProfileDir"] = Path.Combine(baseDir, "user-claude"),
                ["LlmProviders:p-dead:ApiKey"] = "sk-d",
                ["LlmProviders:p-dead:AnthropicBaseUrl"] = "https://d.example.com",
                ["LlmProviders:p-dead:Models:0:Id"] = "m-dead",
                ["LlmProviders:p-alive:ApiKey"] = "sk-a",
                ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://a.example.com",
                ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
            };
            var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
            var pool = BuildPool("acc-a");
            var health = new ProviderHealthRegistry();
            health.MarkUnavailable("p-dead");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-start";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            var deadDir = Path.Combine(baseDir, "claude-profiles", "p-dead", "projects", flat);
            var aliveDir = Path.Combine(baseDir, "claude-profiles", "p-alive", "projects", flat);
            Directory.CreateDirectory(deadDir);
            File.WriteAllText(Path.Combine(deadDir, csid + ".jsonl"), "HISTORY");

            var session = new Session { Model = "m-dead", Provider = "p-dead", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "m-dead", "m-alive" }, health: health);
            inner.Sink = sut.HandleMessageAsync;
            var aliveFile = Path.Combine(aliveDir, csid + ".jsonl");
            // Стартовая подмена (миграция p-dead→p-alive) выполняется до первой попытки, поэтому
            // первая попытка идёт на p-alive — туда и дописываем ответ CLI.
            inner.Scripts.Enqueue(() =>
            {
                File.AppendAllText(aliveFile, "\nANSWER");
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Одна попытка — сразу на живом шаге p-alive (стартовая подмена сработала).
            inner.Attempts.Should().ContainSingle();
            inner.Attempts[0].Should().Be(("p-alive", "m-alive"), "старт с живого шага, а не с кулдаунного p-dead");
            // Транскрипт перенесён в профиль шага p-alive — иначе --resume не нашёл бы разговор.
            File.Exists(aliveFile).Should().BeTrue("транскрипт перенесён в профиль шага p-alive");
            File.ReadAllText(aliveFile).Should().Contain("HISTORY", "история переехала из p-dead в p-alive");
            // Свежий ответ скопирован обратно в профиль исходного провайдера (restore при смене поставщика).
            File.ReadAllText(Path.Combine(deadDir, csid + ".jsonl")).Should().Contain("ANSWER",
                "после restore ответ перенесён обратно в профиль p-dead");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // Major, fail-open: при провале переноса транскрипта стартовая подмена НЕ применяется —
    // остаёмся на исходной паре, и ход идёт (кулдаун — наблюдение, а не запрет). Здесь транскрипта
    // нет нигде → TryMigrateTranscript возвращает false → подмена отменяется, попытка уходит на p-dead.
    [Fact]
    public async Task СтартоваяПодмена_ПровалПереноса_FailOpen_ОстаётсяНаИсходнойПаре()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_open_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var dict = new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(baseDir, "projects.json"),
                ["ClaudeUserProfileDir"] = Path.Combine(baseDir, "user-claude"),
                ["LlmProviders:p-dead:ApiKey"] = "sk-d",
                ["LlmProviders:p-dead:AnthropicBaseUrl"] = "https://d.example.com",
                ["LlmProviders:p-dead:Models:0:Id"] = "m-dead",
                ["LlmProviders:p-alive:ApiKey"] = "sk-a",
                ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://a.example.com",
                ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
            };
            var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
            var pool = BuildPool("acc-a");
            var health = new ProviderHealthRegistry();
            health.MarkUnavailable("p-dead");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            // ClaudeSessionId задан, но транскрипта НЕТ нигде → TryMigrateTranscript(false).
            var session = new Session { Model = "m-dead", Provider = "p-dead", ClaudeSessionId = "csid-open" };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "m-dead", "m-alive" }, health: health);
            inner.Sink = sut.HandleMessageAsync;
            inner.Scripts.Enqueue(() => inner.Emit(Success()));

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Подмена НЕ применена: попытка ушла на исходную пару p-dead × m-dead.
            inner.Attempts.Should().ContainSingle();
            inner.Attempts[0].Should().Be(("p-dead", "m-dead"), "провал переноса → остаёмся на исходной паре");
            // Стартовая пара не изменилась — маркера подмены нет.
            Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
            session.Model.Should().Be("m-dead");
            session.Provider.Should().Be("p-dead");
            Downstream().OfType<ResultMessage>().Should().ContainSingle()
                .Which.Subtype.Should().Be("success", "ход пошёл на исходной паре (fail-open)");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // Несколько подмен подряд (шаг → шаг → шаг): обратный перенос в finally идёт из ПОСЛЕДНЕГО
    // профиля цепочки (где прошёл ход и записан свежий ответ), а не из первого/промежуточного.
    // Цепочка m1(p1) → m2(p2) → m3(p3); ответ CLI пишется только в профиль p3; после хода он
    // должен оказаться в p1 (исходный провайдер, куда вернётся --resume). Если бы копия шла из
    // p1/p2 — ответ бы в p1 не попал.
    [Fact]
    public async Task НесколькоПодменПодряд_КопияНазадИзПоследнегоПрофиляЦепочки()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_chain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var providers = BuildChainProvidersInDir(baseDir, 3);   // p1/m1, p2/m2, p3/m3
            var pool = BuildPool("acc-a");   // одна подписка — m1 сторонний, ротаций пула нет
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-chain";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            string Profile(int i) => Path.Combine(baseDir, "claude-profiles", $"p{i}", "projects", flat, csid + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(Profile(1))!);
            File.WriteAllText(Profile(1), "HISTORY");

            var session = new Session { Model = "m1", Provider = "p1", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "m1", "m2", "m3" });
            inner.Sink = sut.HandleMessageAsync;
            // m1/p1 — сбой → шаг m2/p2 (копия p1→p2). m2/p2 — сбой → шаг m3/p3 (копия p2→p3).
            // m3/p3 — успех + ответ CLI пишется ТОЛЬКО в профиль p3.
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() =>
            {
                File.AppendAllText(Profile(3), "\nANSWER");
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Три попытки — по одной на каждый шаг цепочки.
            inner.Attempts.Should().HaveCount(3);
            inner.Attempts[0].Should().Be(("p1", "m1"));
            inner.Attempts[1].Should().Be(("p2", "m2"));
            inner.Attempts[2].Should().Be(("p3", "m3"));
            // Исходный провайдер восстановлен.
            session.Provider.Should().Be("p1");
            session.Model.Should().Be("m1");
            // Свежий ответ скопирован из ПОСЛЕДНЕГО профиля (p3) в исходный (p1) — иначе следующий
            // ход на --resume из p1 не нашёл бы ответ модели.
            File.ReadAllText(Profile(1)).Should().Contain("ANSWER",
                "обратный перенос идёт из последнего профиля цепочки (p3), где записан ответ");
            // Подтверждение, что ответ жил только в p3: в промежуточном p2 его нет.
            File.Exists(Profile(2)).Should().BeTrue("p2 получил копию при прямом переносе p1→p2");
            File.ReadAllText(Profile(2)).Should().NotContain("ANSWER",
                "ответ записан только в p3 — значит, копия в p1 пришла именно из p3");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // Регрессия ревью 2026-08-08 (sticky-флаг): за ход БЫЛА смена типа поставщика (шаг цепочки,
    // IsProviderSwitch:true), а ЗА НЕЙ — тихая ротация подписки того же пула (IsProviderSwitch:false).
    // Раньше appliedProviderSwitch = next.IsProviderSwitch присваивал тип ПОСЛЕДНЕЙ подмены (false),
    // и finally НЕ восстанавливал Provider/Model и НЕ переносил транскрипт обратно — пара
    // Model=deepseek-chat × Provider=claude-2 персистилась, а следующий ход по модели уходил в профиль
    // deepseek, где свежего ответа нет → молчаливая потеря хода из контекста CLI. После фикса флаг
    // накапливается (|=), restore отрабатывает полностью.
    //
    // Цепочка [deepseek-chat, opus]: шаг 1 (deepseek, сторонний) — сбой → шаг цепочки opus на подписку
    // пула claude (нативная, IsProviderSwitch:true) → 429 → тихая ротация на claude-2 (free) → успех.
    // Ответ CLI пишется только в профиль sub-claude-2; после хода должен оказаться в sub-claude (origProvider).
    [Fact]
    public async Task ШагЦепочкиЗатемТихаяРотация_ModelProviderВосстановлены_ТранскриптИзПоследнейПары()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_sticky_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var dict = new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(baseDir, "projects.json"),
                ["ClaudeUserProfileDir"] = Path.Combine(baseDir, "user-claude"),
                ["LlmProviders:deepseek:ApiKey"] = "sk-ds",
                ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://ds.example.com",
                ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
            };
            var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
            var pool = BuildPool("claude", "claude-2");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-sticky";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            string Profile(string key) => Path.Combine(baseDir, "claude-profiles", key, "projects", flat, csid + ".jsonl");

            var persisted = new List<(string? Model, string? Provider)>();
            // origProvider = claude (ключ пула), чтобы шаг цепочки opus резолвился на подписку пула.
            var session = new Session { Model = "deepseek-chat", Provider = "claude", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "deepseek-chat", "opus" },
                persist: () => persisted.Add((session.Model, session.Provider)));
            inner.Sink = sut.HandleMessageAsync;

            // История лежит в стартовом профиле deepseek (currentKey на старте = deepseek по модели).
            Directory.CreateDirectory(Path.GetDirectoryName(Profile("deepseek"))!);
            File.WriteAllText(Profile("deepseek"), "HISTORY");
            var claude2File = Profile("sub-claude-2");

            // deepseek × deepseek-chat — 429 → шаг цепочки opus (на подписку пула claude).
            // claude × opus — 429 → тихая ротация на claude-2 (IsProviderSwitch:false).
            // claude-2 × opus — успех + ответ CLI пишется в профиль sub-claude-2.
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() =>
            {
                File.AppendAllText(claude2File, "\nANSWER");
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Три попытки. Первая — стартовая пара сессии: origProvider=claude (ключ пула) выбран
            // так, чтобы шаг цепочки opus резолвился на подписку пула, а не на сторонний deepseek.
            // Реальный профиль транскрипта первой попытки — deepseek (по модели deepseek-chat),
            // поэтому HISTORY лежит в Profile("deepseek"); Attempts пишут Info.Provider, а не профиль.
            inner.Attempts.Should().HaveCount(3);
            inner.Attempts[0].Should().Be(("claude", "deepseek-chat"));
            inner.Attempts[1].Should().Be(("claude", "opus"), "шаг цепочки на подписку пула (смена поставщика)");
            inner.Attempts[2].Should().Be(("claude-2", "opus"), "тихая ротация подписки того же пула");
            // Маркер подмены — ровно один (только шаг цепочки); тихая ротация прошла без маркера.
            Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle();

            // Sticky-флаг: после шага цепочки + тихой ротации ОБА поля восстановлены.
            session.Model.Should().Be("deepseek-chat", "модель восстановлена (sticky-флаг не сброшен тихой ротацией)");
            session.Provider.Should().Be("claude", "провайдер восстановлен (тихая ротация не сбросила накопленный флаг)");
            // Свежий ответ скопирован из профиля последней пары (sub-claude-2) в исходный профиль (sub-claude).
            File.ReadAllText(Profile("sub-claude")).Should().Contain("ANSWER",
                "обратный перенос идёт из актуального профиля последней пары (sub-claude-2)");
            // Persist вызван с восстановленными значениями.
            persisted.Should().NotBeEmpty("restore после sticky-флага персистит восстановленные значения");
            persisted[^1].Model.Should().Be("deepseek-chat");
            persisted[^1].Provider.Should().Be("claude");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // Находка ревью 2026-08-10 №1: после успешного хода с подменой restore вызывает
    // TryCopyTranscriptBack с preserveLongerDestination=false. Приёмник (acc-a) на момент
    // restore короче источника (deepseek после записи CLI) — функция обязана перезаписать,
    // иначе следующий ход на --resume из acc-a не нашёл бы свежий ответ модели.
    [Fact]
    public async Task preserveLongerDestination_false_on_success_no_dangling_turn()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ccs_fb_preserve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var providers = BuildProvidersInDir(baseDir);
            var pool = BuildPool("acc-a");
            var rootPath = Path.Combine(baseDir, "workdir");
            Directory.CreateDirectory(rootPath);
            const string csid = "csid-preserve";
            var flat = TranscriptMigrator.FlattenCwd(rootPath);
            var subAccA = Path.Combine(baseDir, "claude-profiles", "sub-acc-a");
            var deepseek = Path.Combine(baseDir, "claude-profiles", "deepseek");
            var accAFile = Path.Combine(subAccA, "projects", flat, csid + ".jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(accAFile)!);
            // Приёмник (dst) короче источника на момент restore: 800 байт истории прошлого хода.
            File.WriteAllText(accAFile, new string('Q', 800));

            var session = new Session { Model = "sonnet", Provider = "acc-a", ClaudeSessionId = csid };
            var inner = new FakeInnerAdapter(session);
            var sut = new FallbackLlmSessionAdapter(inner, () => session.Model,
                msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
                pool, providers, rootPath, launcher: null, initialProfileRoot: null,
                effectiveChain: () => new[] { "sonnet", "deepseek-chat" });
            inner.Sink = sut.HandleMessageAsync;
            var deepseekFile = Path.Combine(deepseek, "projects", flat, csid + ".jsonl");
            // acc-a — 429 → шаг цепочки deepseek (прямой перенос acc-a→deepseek, файл = 800 байт).
            // deepseek — успех, CLI дописывает 192 'A' + "\nANSWER" (200 байт) → deepseek = 1000 байт.
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
            inner.Scripts.Enqueue(() =>
            {
                File.AppendAllText(deepseekFile, new string('A', 193) + "\nANSWER");
                inner.Emit(Success());
            });

            await sut.SendMessageAsync("сделай");
            await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");
            await WaitForAsync(() => !sut.FallbackTurnActive, "restore");

            // Provider/Model восстановлены, и главное — приёмник переписан на 1000 байт источника
            // несмотря на то, что до restore он был короче (800 < 1000). preserveLongerDestination=false
            // на успешном ходе перезаписывает безусловно, иначе --resume из acc-a потерял бы ответ.
            session.Provider.Should().Be("acc-a");
            session.Model.Should().Be("sonnet");
            new FileInfo(accAFile).Length.Should().Be(1000,
                "приёмник перезаписан 1000-байтным источником: preserve=false на успехе не отказывает перезаписать, когда dst < src");
        }
        finally { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, recursive: true); }
    }

    // === Привязка терминальных событий к прогону (починка прод-дефекта 2026-08-09) ===

    // Симптом 1: намеренное прерывание хода (Interrupt ради очереди / «Стоп») не должно
    // классифицироваться как обрыв (Unreachable) и запускать подмену провайдера. На проде ход,
    // уже получивший события (gotEvent=true — модель начала отвечать), при прерывании умирал без
    // result → ProcessGone → Unreachable → подмена (33 ложных подмены за 3 дня, выели квоту
    // qwen3.8-max). Interrupt выставляет _userInterrupted, и ход завершается без фолбэка;
    // дополнительно outcome несёт InterruptedByUser — второй эшелон на случай, если проверка
    // _userInterrupted в цикле проиграет гонку с поздним обрывом.
    [Fact]
    public async Task НамеренныйInterrupt_ПриХодеССобытиями_ПодменыПровайдераНет()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new TextDeltaMessage("начал отвечать"));  // gotEvent=true: ход жив, события были
            sut.Interrupt();                                     // намеренное прерывание (ради очереди)
            inner.Emit(new ExitedMessage());                     // процесс убит, result не пришёл
        });

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ExitedMessage>().Any(), "exited убитого процесса");

        inner.Attempts.Should().ContainSingle("намеренное прерывание — не ошибка доставки, фолбэка нет");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty("подмена провайдера не запускается");
        pool.IsExhausted("acc-a").Should().BeFalse("Interrupt — не квота, подписка не банится");
    }

    // Симптом 2: осиротевший ExitedMessage приходит, когда оркестрация фолбэка уже снята
    // (_turn=null — ход завершился, финал ушёл downstream). Это «хвост» доживающего/позднего
    // процесса; SessionManager уже разобрал очередь по первому терминалу. Раньше такой Exited
    // уходил вниз и давал повторный разбор очереди (дублирующиеся авто-ходы «Персона-исполнитель
    // завершила»). Теперь ExitedMessage при снятой оркестрации глотается адаптером.
    [Fact]
    public async Task ОсиротевшийExited_ПослеЗавершённогоХода_НеИдётВниз()
    {
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() => inner.Emit(Success()));   // ход завершился успешно

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "result завершённого хода");

        // Оркестрация снята — _turn=null. Поздний Exited доживающего процесса:
        var before = Downstream().Count;
        inner.Emit(new ExitedMessage());

        // Exited фильтруется адаптером и НЕ уходит downstream — иначе SessionManager получил бы
        // повторный терминал и запустил разбор очереди ещё раз.
        Downstream().OfType<ExitedMessage>().Should().BeEmpty("осиротевший Exited глотается адаптером");
        Downstream().Count.Should().Be(before, "никаких новых событий вниз не ушло");
    }

    // Симптом 2 (дополнение): осиротевший Exited фильтруется только при снятой оркестрации.
    // При активном ходе (turn есть) Exited от текущего прогона обязан дойти до SessionManager —
    // по нему разбирается очередь после намеренного прерывания (DrainOnExitedRun). Проверка, что
    // фильтр не отрезал штатный путь: Exited при активном turn уходит вниз как обычно.
    [Fact]
    public async Task Exited_ПриАктивномХоде_ИдётВнизКакТерминал()
    {
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(Success());              // result текущего хода
            inner.Emit(new ExitedMessage());    // выход процесса — оба при активном turn
        });

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ExitedMessage>().Any(), "exited при активном ходе");

        // SettleAsync выпускает оба задержанных сообщения (result + exited) — фильтр осиротевших
        // их не трогает, они ушли пока turn был активен.
        Downstream().OfType<ResultMessage>().Should().ContainSingle();
        Downstream().OfType<ExitedMessage>().Should().ContainSingle();
    }

    // П3: доставка под активной оркестрацией (повторный авто-ход в ещё занятый чат) раньше шла
    // байпасом в _inner — невидимая очередь _turnLock без дедупа и фолбэк-защиты. Теперь ход
    // возвращается в серверную Pending через колбэк EnqueueBypass и дождётся конца оркестрации,
    // а OrchestrationDone в finally адаптера сигнализирует SessionManager разобрать очередь.
    [Fact]
    public async Task БайпасАктивнойОркестрации_ВозвратВPending_Вместо_Inner()
    {
        var pool = BuildPool("acc-a");
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drainSignaled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requeued = new List<string>();
        var (sut, inner) = BuildSut(pool,
            enqueueBypass: (_, text, _, _, _) => { lock (requeued) requeued.Add(text); return Task.CompletedTask; },
            orchestrationDone: _ => drainSignaled.TrySetResult(true));
        inner.Sink = sut.HandleMessageAsync;

        // Первый ход: стартует оркестрацию и «зависает» (не эмитит result) — _turn остаётся активен.
        inner.Scripts.Enqueue(() => firstStarted.TrySetResult(true));
        _ = sut.SendMessageAsync("ход-1");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sut.OrchestrationActive.Should().BeTrue("первый ход под оркестрацией, result ещё не пришёл");

        // Второй ход приходит, пока оркестрация активна — уходит в серверную Pending (колбэк),
        // а НЕ в _inner (inner.Attempts не должен вырасти на этом ходе).
        await sut.SendMessageAsync("доклад-в-занятый-чат");
        lock (requeued) requeued.Should().ContainSingle().Which.Should().Be("доклад-в-занятый-чат");
        inner.Attempts.Should().HaveCount(1, "байпасный ход НЕ уходит в _inner — он ждёт в серверной Pending");

        // Завершаем первый ход: оркестрация сбрасывает _turn и в finally сигнализирует DrainNextPending.
        inner.Emit(Success());
        await WaitForAsync(() => !sut.OrchestrationActive, "сброс _turn после result");
        await drainSignaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Без колбэка EnqueueBypass (тесты без SessionManager) адаптер откатывается к прежнему байпасу
    // в _inner — чтобы не терять ходы. OrchestrationDone при этом не вызывается (нечего разбирать).
    [Fact]
    public async Task БайпасБезКолбэка_ОткатВ_Inner()
    {
        var pool = BuildPool("acc-a");
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (sut, inner) = BuildSut(pool); // без enqueueBypass/orchestrationDone
        inner.Sink = sut.HandleMessageAsync;
        inner.Scripts.Enqueue(() => firstStarted.TrySetResult(true));
        _ = sut.SendMessageAsync("ход-1");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await sut.SendMessageAsync("ход-2-байпас");
        // inner получил второй ход напрямую (откат к байпасу при отсутствии колбэка)
        inner.Attempts.Should().HaveCount(2);

        inner.Emit(Success());
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "result первого хода");
    }

    // Инцидент 2026-08-11: чат «Проверка MCP vdi» уехал с opus на glm-5.2 при живой подписке.
    // Ход человека пришёл через 4 с после result предыдущего хода, а ещё через 3 с штатно умер
    // ДОЖИВАВШИЙ процесс предыдущего прогона — его exited резолвил попытку нового хода как
    // ProcessGone → Unreachable → подмена модели. Решение вынесено чистой функцией: метка
    // прогона в exited против снимка на начало попытки.
    [Fact]
    public void IsOwnExited_ЧужойПрогонНеРезолвитПопытку_СвойРезолвит()
    {
        // Снимок на начало попытки = 7 (последний ход подан предыдущему прогону).
        // Его exited несёт ту же метку — терминал чужого прогона.
        FallbackLlmSessionAdapter.IsOwnExited(exitedTurnSeq: 7, attemptTurnSeq: 7)
            .Should().BeFalse("прогон обслуживал ход, поданный ДО попытки — его смерть не наш обрыв");
        FallbackLlmSessionAdapter.IsOwnExited(exitedTurnSeq: 6, attemptTurnSeq: 7)
            .Should().BeFalse("прогон ещё старше — тем более чужой");
        // Ход попытки подан после снимка, поэтому его прогон несёт метку строго больше.
        FallbackLlmSessionAdapter.IsOwnExited(exitedTurnSeq: 8, attemptTurnSeq: 7)
            .Should().BeTrue("прогон принял ход этой попытки — его смерть = обрыв доставки");
        // Метки нет (адаптер без прогонов, тесты) — прежнее поведение.
        FallbackLlmSessionAdapter.IsOwnExited(exitedTurnSeq: 0, attemptTurnSeq: 0)
            .Should().BeTrue("метка недоступна — fail-open, иначе попытка зависла бы навсегда");
    }

    // Репро инцидента целиком: попытка идёт, приходит exited ПРЕДЫДУЩЕГО прогона (метка = снимку),
    // затем нормальный result собственного хода. Подмены модели быть не должно.
    [Fact]
    public async Task ExitedПредыдущегоПрогона_ПриЖивойПопытке_ПодменыНет()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);

        // Первый ход: завершается result'ом, но его процесс ещё доживает (exited пока не пришёл).
        inner.Scripts.Enqueue(() => inner.Emit(Success()));
        await sut.SendMessageAsync("первый ход");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "result первого хода");
        var firstRunSeq = inner.SubmittedTurnSeq;

        // Второй ход человека — и на нём штатно умирает процесс ПЕРВОГО прогона.
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new ExitedMessage(firstRunSeq));   // чужой терминал: ход в нём подан раньше
            inner.Emit(Success());                        // собственный ход отвечает штатно
        });

        await sut.SendMessageAsync("второй ход");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Count() == 2, "result второго хода");

        inner.Attempts.Should().HaveCount(2, "чужой exited не должен перезапускать ход");
        inner.Attempts[1].Provider.Should().Be("acc-a", "подписка не менялась");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty("подмены модели нет");
        Downstream().OfType<ResultMessage>().Should().OnlyContain(r => r.Subtype == "success");
    }

    // Обратная сторона: exited СОБСТВЕННОГО прогона (метка = метке хода) по-прежнему
    // резолвит попытку как ProcessGone — обрыв доставки, ход перезапускается на другой паре.
    [Fact]
    public async Task СвоёExited_РезолвитProcessGone_ХодПерезапускается()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(new TextDeltaMessage("начал отвечать и"));
            inner.Emit(new ExitedMessage(inner.SubmittedTurnSeq));   // умер процесс ЭТОГО хода
        });
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2, "обрыв своего прогона — ошибка доставки, ход перезапускается");
        inner.Attempts[1].Provider.Should().Be("acc-b");
    }

    // Своё exited ПОСЛЕ result не создаёт второго исхода: попытка уже разрешена result'ом,
    // exited остаётся обычным терминалом (существующая проверка AttemptResolved).
    [Fact]
    public async Task СвоёExitedПослеResult_НовогоИсходаНет()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
        {
            inner.Emit(Success());
            inner.Emit(new ExitedMessage(inner.SubmittedTurnSeq));   // штатный выход после хода
        });

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ExitedMessage>().Any(), "exited после result");

        inner.Attempts.Should().ContainSingle("успешный ход не перезапускается");
        Downstream().OfType<ResultMessage>().Should().ContainSingle();
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
    }
}
