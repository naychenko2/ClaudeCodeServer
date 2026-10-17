using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Composition;

// Сторож DI-цикла подсистемы Llm (ADR-014, подраздел «DI-цикл через фабрики»).
//
// Цикл (внесён коммитом 704b0746d, fix/tool-call-leak, мера A):
//   SessionManager → ILlmSessionAdapterFactory → LlmSessionAdapterFactory
//     → IToolCallLeakSink → LocalModelTurnMeter → ISessionDirectory
//     → SessionDirectoryAdapter → SessionManager.
//
// Microsoft DI не детектит его: цикл «размазан» по фабричным делегатам
// (LocalModelTurnMeter и IToolCallLeakSink — `sp => ...`), а не по прямым
// конструкторным зависимостям. Вместо исключения — зависание на старте (CPU 0).
//
// Тест строит РЕАЛЬНЫЙ DI-граф (TestWebApplicationFactory = эквивалент
// ServiceCollection.BuildServiceProvider() с полным набором регистраций
// Program.cs) и резолвит SessionManager — это строит весь граф и замыкает
// цикл. Ключевой момент: TestWebApplicationFactory по умолчанию подменяет
// боевой LlmSessionAdapterFactory на стаб (FakeLlmSessionAdapterFactory,
// без зависимости IToolCallLeakSink) — поэтому ExtraServices возвращает
// реальный: только он несёт рёбро, замыкавшее цикл.
//
// С фиксом (Lazy<IToolCallLeakSink> в LlmSessionAdapterFactory) построение
// завершается. Без фикса (прямой IToolCallLeakSink) — DI-цикл: тест висит
// (или падает бесконечной рекурсией), а не проходит.
public class LlmSubsystemDiCycleTests
{
    [Fact]
    public void DiContainer_СтроитсяБезЦикла_Llm()
    {
        using var factory = new TestWebApplicationFactory
        {
            // Возвращаем боевой LlmSessionAdapterFactory (фабрика подменяет его на стаб,
            // чтобы не запускать claude.exe). Только реальный несёт зависимость
            // IToolCallLeakSink, замыкавшую DI-цикл. Регистрация позже — побеждает.
            ExtraServices = services =>
                services.AddSingleton<ILlmSessionAdapterFactory, LlmSessionAdapterFactory>(),
        };

        var sp = factory.Services;

        // Резолв SessionManager (= ISessionDirectory через SessionDirectoryAdapter) строит
        // весь граф: SessionManager → LlmSessionAdapterFactory → IToolCallLeakSink →
        // LocalModelTurnMeter → ISessionDirectory → SessionManager. Без фикса — зависание.
        var sessionManager = sp.GetRequiredService<SessionManager>();
        sessionManager.Should().NotBeNull(
            "DI-граф подсистемы Llm строится без цикла: SessionManager резолвится");
    }
}
