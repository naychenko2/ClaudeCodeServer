using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Pre-flight проба локального эндпоинта: отличает «локальный llama.cpp/vLLM выключен» от
/// «облачный провайдер упал». При Down → сразу FailLocalDownAsync, без шагов цепочки —
/// сосед-облако всё равно не починит мёртвый эндпоинт, только сожжёт лимиты.
/// </summary>
public class FallbackLocalDownTests
{
    private readonly List<ServerMessage> _downstream = [];

    private sealed class FakeLocalProbe(LocalProbeOutcome outcome) : ILocalEndpointProbe
    {
        public LocalProbeOutcome Outcome { get; } = outcome;
        public int Calls { get; private set; }
        public Task<LocalProbeOutcome> CheckAsync(LlmProviderConfig provider, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Outcome);
        }
        public void Invalidate(string providerKey) { }
    }

    private sealed class FakeInnerAdapter(Session info) : ILlmSessionAdapter
    {
        public Session Info { get; } = info;
        public Func<ServerMessage, Task>? Sink;
        public List<(string Provider, string? Model)> Attempts { get; } = [];
        public Queue<Action> Scripts { get; } = new();

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
            int agentDepth = 0, bool suppressTasksExecute = false)
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
        public int Interrupts;
        public Action? OnInterrupt;
        public void Interrupt() { Interrupts++; OnInterrupt?.Invoke(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ClaudeSubscriptionPool BuildPool(params string[] keys)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var key in keys) dict[$"ClaudeSubscriptions:{key}:OAuthToken"] = $"token-{key}";
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private static LlmProviderRegistry BuildProviders(bool localIsLocal = true)
    {
        var dict = new Dictionary<string, string?>
        {
            // local-qwen — локальный провайдер с заполненным каталогом (проба найдёт модель)
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:8080",
            ["LlmProviders:local-qwen:IsLocal"] = localIsLocal ? "true" : "false",
            ["LlmProviders:local-qwen:Models:0:Id"] = "qwen38-27b",
            ["LlmProviders:local-qwen:Models:0:ContextWindow"] = "57344",
            // glm — облачный сосед (на случай цепочки)
            ["LlmProviders:glm:ApiKey"] = "zai-key",
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://api.z.ai/api/anthropic",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5.2",
        };
        return new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection(dict).Build());
    }

    private (FallbackLlmSessionAdapter Sut, FakeInnerAdapter Inner) BuildSut(
        ClaudeSubscriptionPool pool, ILocalEndpointProbe? localProbe,
        string? provider = "local-qwen", string? model = "qwen38-27b",
        string[]? chain = null)
    {
        var session = new Session { Model = model, Provider = provider };
        var inner = new FakeInnerAdapter(session);
        var sut = new FallbackLlmSessionAdapter(inner,
            () => session.Model,
            msg => { lock (_downstream) _downstream.Add(msg); return Task.CompletedTask; },
            pool, BuildProviders(), Path.GetTempPath(), launcher: null, initialProfileRoot: null,
            effectiveChain: chain is null ? null : () => chain,
            egressRetryDelay: TimeSpan.FromMilliseconds(10),
            localProbe: localProbe);
        inner.Sink = sut.HandleMessageAsync;
        return (sut, inner);
    }

    private List<ServerMessage> Downstream()
    {
        lock (_downstream) return [.. _downstream];
    }

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"не дождались: {what}");
    }

    [Fact]
    public async Task ЛокальныйМёртв_СразуFailБезЗапускаПроцесса()
    {
        // Pre-flight проба показала Down — inner.SendMessageAsync НЕ вызван, в ленту ушла
        // понятная красная карточка LocalModelDown, финальный result ошибочный. Сосед по
        // цепочке НЕ использовался (это бы сожгло лимит облачного провайдера на заведомо
        // мёртвый эндпоинт).
        var pool = BuildPool("acc-a");
        var probe = new FakeLocalProbe(LocalProbeOutcome.Down);
        var chain = new[] { "qwen38-27b", "glm-5.2" };  // сосед-облако есть, но НЕ должен ходить
        var (sut, inner) = BuildSut(pool, probe, chain: chain);

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().BeEmpty("pre-flight проба сказала Down — процесс CLI не запускается");
        probe.Calls.Should().Be(1);
        var error = Downstream().OfType<ErrorMessage>().Single();
        error.Text.Should().Be(TurnFailureText.LocalModelDown);
        var result = Downstream().OfType<ResultMessage>().Single();
        result.Subtype.Should().Be("error", "ход не состоялся — статус чата обязан это показать (P29)");
    }

    [Fact]
    public async Task ЛокальныйAlive_ПробаОдинРаз_ХодИдёт()
    {
        // Probe Alive — проба вызвана, затем обычный путь: процесс запущен, ответ пришёл.
        var pool = BuildPool("acc-a");
        var probe = new FakeLocalProbe(LocalProbeOutcome.Alive);
        var (sut, inner) = BuildSut(pool, probe);
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        probe.Calls.Should().Be(1);
        inner.Attempts.Should().HaveCount(1, "процесс запущен, попытка одна");
        Downstream().OfType<ResultMessage>().Single().Subtype.Should().Be("success");
    }

    [Fact]
    public async Task ОблачныйПровайдер_ПробаНеЗовётся()
    {
        // Провайдер НЕ помечен IsLocal — pre-flight проба не применяется (NotApplicable),
        // даже если бы пробник был задан. Тест защищает регресс «проба дёргается для всех».
        var pool = BuildPool("acc-a");
        var probe = new FakeLocalProbe(LocalProbeOutcome.Down);
        var (sut, inner) = BuildSut(pool, probe,
            provider: "glm", model: "glm-5.2");
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        probe.Calls.Should().Be(0, "облачный провайдер не подлежит pre-flight пробе локального эндпоинта");
        inner.Attempts.Should().HaveCount(1);
        Downstream().OfType<ResultMessage>().Single().Subtype.Should().Be("success");
    }

    [Fact]
    public async Task ПробаНеСконфигурирована_СтароеПоведениеБезИзменений()
    {
        // null-пробник (тесты без DI или pre-flight ещё не подключён) — проба пропускается,
        // ход идёт штатным путём через inner. Защита от регресса «внезапно все ходы
        // валятся на pre-flight».
        var pool = BuildPool("acc-a");
        var (sut, inner) = BuildSut(pool, localProbe: null!);  // null — проба выключена
        inner.Scripts.Enqueue(() => inner.Emit(new ResultMessage("success", 10, 1, null, null)));

        await sut.SendMessageAsync("сделай что-нибудь");
        await WaitForAsync(() => Downstream().OfType<ResultMessage>().Any(), "финальный result");

        inner.Attempts.Should().HaveCount(1);
        Downstream().OfType<ResultMessage>().Single().Subtype.Should().Be("success");
    }
}
