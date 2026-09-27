using ClaudeHomeServer.DeviceAgent.Cli;

namespace ClaudeHomeServer.DeviceAgent.Exec;

/// <summary>Копия CLI на время хода. Путь — только отсюда: CLI пользователя и PATH не используются.</summary>
internal interface ICliHandle : IDisposable
{
    string ExecutablePath { get; }
    string Version { get; }
}

/// <summary>Источник копий CLI для ходов. Боевой — управляемая копия (<see cref="ManagedCli"/>).</summary>
internal interface ICliLeaseSource
{
    /// <summary>null — харнес не готов, <paramref name="problem"/> — причина для отказа хода.</summary>
    ICliHandle? TryAcquire(out string? problem);
}

internal sealed class ManagedCliLeaseSource(ManagedCli cli) : ICliLeaseSource
{
    public ICliHandle? TryAcquire(out string? problem)
    {
        var lease = cli.TryAcquire(out problem);
        return lease is null ? null : new Handle(lease);
    }

    private sealed class Handle(CliLease lease) : ICliHandle
    {
        public string ExecutablePath => lease.ExecutablePath;
        public string Version => lease.Version;
        public void Dispose() => lease.Dispose();
    }
}
