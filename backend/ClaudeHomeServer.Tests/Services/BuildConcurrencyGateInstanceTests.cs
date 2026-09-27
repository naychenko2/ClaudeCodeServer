using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Общий потолок процесса: повторная установка тем же значением НЕ подменяет гейт. Проверяется
// отдельным классом в коллекции глобального состояния — Instance один на процесс ОС, а тестовых
// хостов (каждый зовёт Configure из Program.cs) в прогоне десятки.
[Collection(TestCollections.ProcessGlobalState)]
public class BuildConcurrencyGateInstanceTests
{
    [Fact]
    public void ПовторныйПодъёмХоста_НеОбнуляетЗанятыеСлоты()
    {
        var before = BuildConcurrencyGate.Instance;
        try
        {
            BuildConcurrencyGate.Configure(2);
            var gate = BuildConcurrencyGate.Instance;
            using var busy = gate.TryAcquire(new ProcessSpec { FileName = "dotnet", Heavy = true })!;
            gate.Available.Should().Be(1);

            // Второй хост того же процесса читает тот же конфиг
            BuildConcurrencyGate.Configure(2);

            BuildConcurrencyGate.Instance.Should().BeSameAs(gate,
                "подмена отдала бы новому гейту полный потолок поверх идущих сборок");
            BuildConcurrencyGate.Instance.Available.Should().Be(1, "занятый слот остался занятым");
        }
        finally
        {
            // Прогон продолжают другие классы — возвращаем процессу прежний потолок
            BuildConcurrencyGate.Configure(before.Limit == 0 ? -1 : before.Limit);
        }
    }

    [Fact]
    public void ДругоеЗначениеПотолка_ГейтПересоздаётся()
    {
        var before = BuildConcurrencyGate.Instance;
        try
        {
            BuildConcurrencyGate.Configure(2);
            var gate = BuildConcurrencyGate.Instance;

            BuildConcurrencyGate.Configure(3);

            BuildConcurrencyGate.Instance.Should().NotBeSameAs(gate);
            BuildConcurrencyGate.Instance.Limit.Should().Be(3);
        }
        finally
        {
            BuildConcurrencyGate.Configure(before.Limit == 0 ? -1 : before.Limit);
        }
    }
}
