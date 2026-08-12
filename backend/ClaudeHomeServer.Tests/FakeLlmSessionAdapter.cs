using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Tests;

// Стаб LLM-адаптера для интеграционных тестов (TestWebApplicationFactory): не запускает
// реальный claude.exe. Подменяет ILlmSessionAdapterFactory, чтобы сервер-инициированные
// ходы (kickoff онбординга, «просыпание» персоны) не падали/не висли на запуске процесса
// и не протекали боевым окружением разработчика. Нейтрален для тестов, не запускающих ход:
// Create отдаёт inert-адаптер, и если SendMessageAsync не зовут — ничего не происходит.
//
// Фабрика хранит созданные адаптеры по sessionId — тест после HTTP-вызова достаёт адаптер
// и проверяет, что первый ход действительно ушёл (SentMessages) и с каким текстом.
internal sealed class FakeLlmSessionAdapterFactory : ILlmSessionAdapterFactory
{
    public ConcurrentDictionary<string, FakeLlmSessionAdapter> Adapters { get; } = new();

    public ILlmSessionAdapter Create(Session session, LlmSessionContext context)
        => Adapters.GetOrAdd(session.Id, _ => new FakeLlmSessionAdapter(session));

    public void Reset() => Adapters.Clear();
}

// Стаб адаптера: запоминает ходы, ничего не стримит и не запускает процесс. Чего НЕ умеет
// (намеренно): НЕ эмитит завершение хода — нет событий result/exited, статус сессии остаётся
// Working навсегда. Поэтому тестам, проверяющим именно ЗАВЕРШЕНИЕ хода (переход Working→Idle,
// result-событие, очистку), этот стаб не подходит — им нужен мок, умеющий завершаться.
// Контроллер-тесты онбординга этим не страдают: им важен факт вызова (SentMessages), а не
// результат, а hosted-сервисы в Testing выключены — незавершённый ход никто не опрашивает.
internal sealed class FakeLlmSessionAdapter : ILlmSessionAdapter
{
    public Session Info { get; }
    public List<string> SentMessages { get; } = [];

    public FakeLlmSessionAdapter(Session info) => Info = info;

    public LlmCapabilities Capabilities => LlmCapabilitiesCatalog.Claude;
    public int CurrentTurnAgentDepth => 0;
    public bool CurrentTurnSuppressTasksExecute => false;
    public bool HasLiveTurn => false;
    public bool OrchestrationActive => false;
    public bool HasPendingBg => false;

    public Task StartAsync() => Task.CompletedTask;

    public Task SendMessageAsync(string text, IReadOnlyList<string>? attachedPaths = null,
        int agentDepth = 0, bool suppressTasksExecute = false)
    {
        lock (SentMessages) SentMessages.Add(text);
        return Task.CompletedTask;
    }

    public Task CompactAsync() => Task.CompletedTask;
    public void RespondPermission(string requestId, string behavior) { }
    public void AnswerQuestion(string toolUseId, string updatedInputJson) { }
    public void RespondPlan(string requestId, bool approve, string? feedback) { }
    public bool TrySetPermissionModeLive(ClaudeMode mode) => false;
    public bool TrySetModelLive(string model) => false;
    public void Interrupt() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
