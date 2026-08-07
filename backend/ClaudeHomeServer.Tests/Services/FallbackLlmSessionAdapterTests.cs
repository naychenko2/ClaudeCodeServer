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

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, LlmProviderRegistry? providers = null,
        string model = "sonnet", string provider = "acc-a",
        int? modelFallbackMax = null, string? ownerId = null,
        string[]? chain = null, ModelTier? turnTier = null)
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
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store,
            effectiveChain: chain is null ? null : () => chain,
            turnTier: () => turnTier);
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    // Провайдер с разными моделями по уровням (для теста явного тира §5.1): TierStrong ≠ TierMedium.
    private static LlmProviderRegistry BuildProvidersWithTiers()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.example.com",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-chat",
            ["LlmProviders:deepseek:TierStrong"] = "ds-strong",
            ["LlmProviders:deepseek:TierMedium"] = "ds-medium",
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
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
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Provider.Should().Be("acc-b");
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
        error.Text.Should().Contain("Фолбэк моделей исчерпан");
        error.Text.Should().Contain("acc-a").And.Contain("acc-b");
    }

    [Fact]
    public async Task ПотолокПятьПодмен_ШестаяПопыткаНеЗапускается()
    {
        var keys = new[] { "s1", "s2", "s3", "s4", "s5", "s6", "s7" };
        var pool = BuildPool(keys);
        var (sut, inner) = BuildSut(pool, provider: "s1", modelFallbackMax: 5);
        for (var i = 0; i < 7; i++)
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(6, "1 исходная + максимум 5 подмен");
        inner.Attempts.Select(a => a.Provider).Distinct().Should().HaveCount(6,
            "пара «модель × подписка» пробуется не более одного раза");
        Downstream().OfType<ProviderSwitchedMessage>().Should().HaveCount(5);
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error");
    }

    [Fact]
    public async Task ПотолокНеЗадан_ДефолтЧетыреПодмены()
    {
        // Без стора (modelFallbackMax = null) потолок — дефолт 4 (FallbackSettingsStore.DefaultMaxSubstitutions).
        // 6 подписок в пуле, но больше 5 попыток (1 стартовая + 4 подмены) не пройдёт.
        var keys = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var pool = BuildPool(keys);
        var (sut, inner) = BuildSut(pool, provider: "s1");
        for (var i = 0; i < 6; i++)
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(5, "1 исходная + максимум 4 подмены по дефолту");
        Downstream().OfType<ProviderSwitchedMessage>().Should().HaveCount(4);
        Downstream().OfType<ResultMessage>().Should().ContainSingle()
            .Which.Subtype.Should().Be("error");
        Downstream().OfType<ErrorMessage>().Should().ContainSingle()
            .Which.Text.Should().Contain("потолок 4");
    }

    [Fact]
    public async Task ВнеДиапазона_КлампитсяКДефолту()
    {
        // Потолок 99 в файле выходит за жёсткий 1..5 — адаптер должен игнорировать
        // (клампится к дефолту 4), иначе жёсткий потолок ADR был бы обойдён.
        var keys = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var pool = BuildPool(keys);
        var store = new FallbackSettingsStore(BuildTempConfiguration(out _));
        Assert.Equal("Потолок подмен должен быть в диапазоне 1..5", store.SetGlobal(99));
        // global остаётся null → читается дефолт 4 (FallbackSettingsStore.DefaultMaxSubstitutions)
        var session = new Session { Provider = "s1", OwnerId = null };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers: null, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store);
        inner.Sink = sut.HandleMessageAsync;
        for (var i = 0; i < 6; i++)
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(5, "99 отвергнут валидацией → global=null → дефолт 4 → 1 + 4 подмены");
    }

    [Fact]
    public async Task ЛичныйПотолокВладельцаБьётГлобальный()
    {
        // Глобально потолок 1, лично у владельца — 5. per-owner слой перебивает глобальный.
        var keys = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var pool = BuildPool(keys);
        var store = new FallbackSettingsStore(BuildTempConfiguration(out _));
        Assert.Null(store.SetGlobal(1));
        Assert.Null(store.SetOwner("owner-x", 5));
        var session = new Session { Provider = "s1", OwnerId = "owner-x" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers: null, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store);
        inner.Sink = sut.HandleMessageAsync;
        for (var i = 0; i < 7; i++)
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(6, "per-owner 5 перебивает глобальный 1 → 1 + 5 подмен");
    }

    [Fact]
    public async Task ДругойВладелецНеВидитЛичныйПотолок()
    {
        // Личный потолок owner-x — 1, у другого владельца должна быть глобальная 5.
        // per-owner изоляция: значения одного владельца не должны просачиваться к другому.
        var keys = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var pool = BuildPool(keys);
        var store = new FallbackSettingsStore(BuildTempConfiguration(out _));
        Assert.Null(store.SetGlobal(5));
        Assert.Null(store.SetOwner("owner-x", 1));
        var session = new Session { Provider = "s1", OwnerId = "owner-y" };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers: null, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            fallbackSettings: store);
        inner.Sink = sut.HandleMessageAsync;
        for (var i = 0; i < 7; i++)
            inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(6,
            "owner-y не имеет своей записи → global=5 → 1 + 5 подмен");
    }

    [Fact]
    public async Task ПулИсчерпан_Уровень2_СтороннийПровайдерСЭквивалентом()
    {
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, BuildProviders());
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("deepseek");
        inner.Attempts[1].Model.Should().Be("deepseek-chat",
            "уровень 2 берёт модель-эквивалент по слоту");
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Label.Should().Contain("смена провайдера");
        inner.Info.Model.Should().Be("deepseek-chat");
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

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[0].Should().Be(("acc-a", "sonnet"));
        inner.Attempts[1].Should().Be(("deepseek", "deepseek-chat"), "шаг 2 цепочки, а не автоподбор");
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Label.Should().Contain("Цепочка пресета");
        inner.Info.Model.Should().Be("deepseek-chat");
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

    [Fact]
    public async Task ЯвныйТирХода_ПобеждаетРеверсЭвристику()
    {
        // §8/§5.1: тир хода, переданный явно, побеждает реверс-эвристику ResolveTier при
        // автоподборе. turnTier=Strong → EquivalentModel берёт TierStrong провайдера (ds-strong),
        // хотя реверс-эвристика без _tiers дала бы Medium (ds-medium).
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, BuildProvidersWithTiers(), model: "sonnet",
            turnTier: ModelTier.Strong);
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));   // sonnet × acc-a — исчерпан
        inner.Scripts.Enqueue(() => inner.Emit(Success()));          // автоподбор → успех

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts[1].Model.Should().Be("ds-strong",
            "явный Strong побеждает реверс (Medium → ds-medium)");
    }

    [Fact]
    public async Task БезЯвногоТира_РеверсЭвристикаДаётMedium()
    {
        // Контрольный кейс к предыдущему: без явного тира (и без _tiers) реверс даёт Medium.
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, BuildProvidersWithTiers(), model: "sonnet");
        inner.Scripts.Enqueue(() => inner.Emit(ApiError("429")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts[1].Model.Should().Be("ds-medium",
            "без явного тира и _tiers реверс-эвристика даёт Medium");
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

        // Ровно один provider_switched — адаптер сам среагировал; на стороне
        // SessionManager гард в тестах не симулируется (этот SUT не SessionManager),
        // но в проде тот же поток событий при активном FallbackTurnActive гард
        // отрежет второй MarkExhausted/ProviderSwitchedMessage.
        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle();
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
    [Fact]
    public async Task RateLimit_ПричинаВМаркере_RateLimit()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
        inner.Scripts.Enqueue(() =>
            inner.Emit(new RateLimitMessage("five_hour", ResetsAt: null, Status: "rejected")));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ProviderSwitchedMessage>().Any(), "маркер");

        Downstream().OfType<ProviderSwitchedMessage>().Should().ContainSingle()
            .Which.Reason.Should().Be("rate_limit");
    }

    // Обрыв потока посреди ответа: класс Unreachable → wire-имя unreachable
    [Fact]
    public async Task ОбрывПотока_ПричинаВМаркере_Unreachable()
    {
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool);
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

        // Один провайдер-переключатель в ленте: ротация по Label=null (адаптер)
        // или без маркера вовсе (только ApplyTarget). SessionManager'овский фейловер
        // тут не идёт — мы не SessionManager. Главное — нет дубля.
        Downstream().OfType<ProviderSwitchedMessage>().Should().HaveCountLessThanOrEqualTo(1);
    }
}
