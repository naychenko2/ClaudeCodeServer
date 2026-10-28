using System.Reflection;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Ревью Б5, п.2: DI-обвязка журнала. Единственный писатель EngineCallLedger — контейнерный
// CheapTextRunner (LlmSubsystem.cs:256,259): если параметр до него не дойдёт (null) или придёт
// второй экземпляр, боевой «Дежурство» видел бы пустой журнал — и ничего в коде не сломалось бы.
[Trait("Category", "Host")]
public class EngineCallLedgerDiTests
{
    [Fact]
    public void ContainerRunnerGetsTheContainerLedger()
    {
        using var factory = new TestWebApplicationFactory
        {
            // Тестовый хост по умолчанию подменяет ICheapTextRunner заглушкой
            // (TestWebApplicationFactory.cs:165) — перерегистрируем настоящий:
            // тест ровно об обвязке настоящего раннера.
            ExtraServices = services => services.AddSingleton<ICheapTextRunner, CheapTextRunner>(),
        };
        var runner = (CheapTextRunner)factory.Services.GetRequiredService<ICheapTextRunner>();
        // Поле primary-конструктора параметра `ledger` (CheapTextRunner.cs:9): Roslyn генерирует
        // его как «<ledger>P» (факт замера, не догадка). При смене имени компилятором тест
        // падает со списком полей типа — правится одной строкой.
        var field = typeof(CheapTextRunner).GetField("<ledger>P", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field ?? throw new InvalidOperationException(
            "поля «<ledger>P» нет; instance-поля: " + string.Join(", ",
                typeof(CheapTextRunner).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(f => f.Name))));
        var ledger = field!.GetValue(runner);
        Assert.NotNull(ledger);
        Assert.Same(factory.Services.GetRequiredService<EngineCallLedger>(), ledger);
    }
}
