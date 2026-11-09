using ClaudeHomeServer.Services.Execution;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Подмена процесс-глобального IsolationOptions.Instance на ровно нужный вызов. Возврат —
// сравнивающий: восстанавливает прежнее значение, только если Instance всё ещё наш. Слепой
// `Instance = prev` затирал бы подмену, которую успел сделать чужой хост (Program.cs каждого
// WebApplicationFactory пишет туда конфиг хоста). Сравнение со свойством не атомарно, но окно —
// две инструкции, а не весь тест.
internal sealed class IsolationOptionsScope : IDisposable
{
    private readonly IsolationOptions _prev;
    private readonly IsolationOptions _ours;

    public IsolationOptionsScope(IsolationOptions ours)
    {
        _prev = IsolationOptions.Instance;
        _ours = ours;
        IsolationOptions.Instance = ours;
    }

    public void Dispose()
    {
        if (ReferenceEquals(IsolationOptions.Instance, _ours)) IsolationOptions.Instance = _prev;
    }
}

// Коллекция процесс-глобального состояния: тесты правят IsolationOptions.Instance
[Collection(TestCollections.ProcessGlobalState)]
public sealed class IsolationOptionsScopeTests
{
    [Fact]
    public void Возврат_прежнего_значения_если_Instance_всё_ещё_наш()
    {
        var before = IsolationOptions.Instance;

        using (new IsolationOptionsScope(new IsolationOptions { Enabled = true }))
            IsolationOptions.Instance.Enabled.Should().BeTrue();

        IsolationOptions.Instance.Should().BeSameAs(before);
    }

    [Fact]
    public void Чужую_подмену_в_окне_возврат_не_затирает()
    {
        var before = IsolationOptions.Instance;
        var foreign = new IsolationOptions { Enabled = false, Slice = "foreign.slice" };
        try
        {
            using (new IsolationOptionsScope(new IsolationOptions { Enabled = true }))
                IsolationOptions.Instance = foreign;

            IsolationOptions.Instance.Should().BeSameAs(foreign, "чужой хост подменил опции в окне — их возвращать назад нельзя");
        }
        finally
        {
            IsolationOptions.Instance = before;
        }
    }
}
