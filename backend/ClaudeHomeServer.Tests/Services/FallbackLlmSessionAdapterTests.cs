using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

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

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false)
        {
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

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, LlmProviderRegistry? providers = null,
        string model = "sonnet", string provider = "acc-a",
        int? modelFallbackMax = null, string? ownerId = null,
        string[]? chain = null,
        Func<string?>? effectiveModel = null,
        ProviderHealthRegistry? health = null)
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
            health: health);
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

    // Стартовый провайдер в кулдауне → стартуем сразу с первого живого шага цепочки (маркер
    // provider_switched, Reason=unreachable), не тратя попытку на мёртвый эндпоинт.
    [Fact]
    public async Task КулдаунСтартовогоПровайдера_СтартСЖивогоШага()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:p-alive:ApiKey"] = "sk-a",
            ["LlmProviders:p-alive:AnthropicBaseUrl"] = "https://a.example.com",
            ["LlmProviders:p-alive:Models:0:Id"] = "m-alive",
        };
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
        var pool = BuildPool("acc-a");
        var health = new ProviderHealthRegistry();
        health.MarkUnavailable("acc-a");   // стартовый провайдер sonnet (нативный claude, acc-a) в кулдауне
        var (sut, inner) = BuildSut(pool, providers, model: "sonnet", provider: "acc-a",
            chain: ["sonnet", "m-alive"], health: health);
        inner.Scripts.Enqueue(() => inner.Emit(Success()));   // старт на m-alive через стартовую подмену

        await sut.SendMessageAsync("сделай");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финал");

        // Стартовая подмена: acc-a в кулдауне → одна попытка сразу на m-alive, без расхода на acc-a.
        inner.Attempts.Should().ContainSingle();
        inner.Attempts[0].Should().Be(("p-alive", "m-alive"), "старт с живого шага, а не с кулдаунного acc-a");
        var marker = Downstream().OfType<ProviderSwitchedMessage>().Single();
        marker.Reason.Should().Be("unreachable");
        marker.Provider.Should().Be("p-alive");
    }
}
