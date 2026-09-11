namespace ClaudeHomeServer.Services.Execution;

// Адаптер SandboxManager → ISandboxPortRange (Core).
// Шов для ProjectServices (Этап 5, волна C, шаг 2): DevServerService берёт
// только пул preview-портов, ничего больше от песочницы ему не нужно —
// конкретные методы SandboxManager (EnsureRunningAsync/BuildRunArgs/...)
// остаются инкапсулированы в Execution.
//
// Регистрация: один синглтон в Program.cs (см. регистрацию `ISandboxPortRange`
// рядом с `AddSingleton<SandboxManager>`).
public sealed class SandboxPortRangeAdapter : ISandboxPortRange
{
    private readonly SandboxManager _sandbox;

    public SandboxPortRangeAdapter(SandboxManager sandbox)
    {
        _sandbox = sandbox;
    }

    public int PortRangeStart => _sandbox.Options.PortRangeStart;
    public int PortRangeSize => _sandbox.Options.PortRangeSize;
}
