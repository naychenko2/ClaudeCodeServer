using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services.Desktop;

// Шлюз десктопного чата в фолбэке (ADR-008 о десктопном агенте, «Последствия»):
// межпровайдерного фолбэка у такого чата нет — цепочка хода режется до пула Claude,
// иначе транскрипт с кадрами рабочего стола уезжает стороннему вендору вместе с
// переносом профиля (TryMigrateTranscript + чужой ANTHROPIC_BASE_URL).
// Проверяется поведением адаптера, а не приватным методом: важен факт «попытки на
// стороннем провайдере не было», а не форма обрезки.
public class DesktopFallbackGateTests
{
    private readonly List<ServerMessage> _downstream = [];

    // Фейковый внутренний адаптер: на каждую попытку хода отыгрывает свой скрипт событий
    // (калька FakeInnerAdapter из FallbackLlmSessionAdapterTests — тот private в своём классе).
    private sealed class FakeInnerAdapter(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public Queue<Action> Scripts { get; } = new();
        // (Provider, Model) на момент запуска каждой попытки
        public List<(string Provider, string? Model)> Attempts { get; } = [];

        public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;
        public int CurrentTurnAgentDepth => 0;
        public bool CurrentTurnSuppressTasksExecute => false;
        public bool HasLiveTurn => false;
        public bool HasQueuedTurn => false;
        public bool OrchestrationActive => false;
        public bool HasPendingBg => false;
        public bool HasTrackedBg => false;
        public bool IsContinuationInFlight => false;
        public long SubmittedTurnSeq { get; private set; }

        public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
            int agentDepth = 0, bool suppressTasksExecute = false)
        {
            SubmittedTurnSeq++;
            lock (Attempts) Attempts.Add((Info.Provider ?? "", Info.Model));
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
        foreach (var key in keys)
            dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    // Сторонние провайдеры p1..pN с моделями m1..mN — шаги цепочки, ради которых чат
    // и уехал бы к чужому вендору.
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

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, LlmProviderRegistry providers, string[] chain, bool desktopChat)
    {
        var session = new Session { Model = chain[0], Provider = "acc-a", DesktopChat = desktopChat };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, providers, Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            effectiveChain: () => chain);
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private static ResultMessage Success() => new("success", 100, 1, null, null);
    private static ResultMessage ApiError(string status) => new("success", 100, 1, null, null, ApiErrorStatus: status);

    // Провал доставки одной попытки: is_error-текст ПЕРЕД result — как шлёт CLI
    private static void EmitRateLimit(FakeInnerAdapter inner)
    {
        inner.Emit(new ErrorMessage("API Error: 429 rate limit", ExpectResultFollows: true));
        inner.Emit(ApiError("429"));
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

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

    [Fact]
    public async Task ДесктопныйЧат_ШагиСтороннихПровайдеровВырезаны_ХодУходитНаСледующуюМодельПула()
    {
        // Цепочка: своя модель → сторонний провайдер p1 → снова модель пула Claude.
        // Десктопный чат обязан перепрыгнуть m1 и уйти сразу на opus.
        var (sut, inner) = BuildSut(BuildPool("acc-a"), BuildChainProviders(1),
            ["sonnet", "m1", "opus"], desktopChat: true);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("посмотри, что на экране");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Model.Should().Be("opus", "шаг стороннего провайдера пропущен, взят следующий шаг пула Claude");
        inner.Attempts.Should().NotContain(a => a.Provider == "p1" || a.Model == "m1");
        Downstream().OfType<ProviderSwitchedMessage>().Should().NotContain(m => m.Provider == "p1");
        Downstream().OfType<ResultMessage>().Last().Subtype.Should().Be("success");
    }

    [Fact]
    public async Task ОбычныйЧат_ТаЖеЦепочка_УходитНаСтороннегоПровайдера()
    {
        // Контроль: без признака десктопного чата цепочка работает как прежде —
        // иначе первый тест был бы зелёным и при сломанном шлюзе.
        var (sut, inner) = BuildSut(BuildPool("acc-a"), BuildChainProviders(1),
            ["sonnet", "m1", "opus"], desktopChat: false);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("p1");
        inner.Attempts[1].Model.Should().Be("m1");
    }

    [Fact]
    public async Task ДесктопныйЧат_ВесьХвостЦепочкиСторонний_ЧестнаяОшибкаБезМиграции()
    {
        // Запасных шагов пула Claude не осталось — ход обязан завершиться ошибкой,
        // а не уехать к чужому вендору. Авто-ретраев и подмен здесь быть не должно.
        var (sut, inner) = BuildSut(BuildPool("acc-a"), BuildChainProviders(2),
            ["sonnet", "m1", "m2"], desktopChat: true);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));

        await sut.SendMessageAsync("покажи окно");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(1);
        Downstream().OfType<ResultMessage>().Last().Subtype.Should().Be("error");
        Downstream().OfType<ProviderSwitchedMessage>().Should().BeEmpty();
        // Модель и провайдер чата после провала — исходные (подмены не было)
        sut.Info.Provider.Should().Be("acc-a");
        sut.Info.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task ДесктопныйЧат_РотацияПодписокПулаРаботает()
    {
        // Уровень 1 правилом не затронут: другой аккаунт того же пула Claude — тот же
        // эндпоинт и тот же владелец данных, кадры никуда не уезжают.
        var pool = BuildPool("acc-a", "acc-b");
        var (sut, inner) = BuildSut(pool, BuildChainProviders(1), ["sonnet", "m1"], desktopChat: true);
        inner.Scripts.Enqueue(() => EmitRateLimit(inner));
        inner.Scripts.Enqueue(() => inner.Emit(Success()));

        await sut.SendMessageAsync("посмотри, что за ошибка");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(2);
        inner.Attempts[1].Provider.Should().Be("acc-b");
        Downstream().OfType<ResultMessage>().Last().Subtype.Should().Be("success");
    }
}
