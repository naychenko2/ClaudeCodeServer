using ClaudeHomeServer.Services.Execution;

namespace ClaudeHomeServer.Tests;

// Заглушка фабрики драйверов среды исполнения: всегда локальный запуск
internal sealed class TestLauncherFactory : ILauncherFactory
{
    public static readonly TestLauncherFactory Instance = new();
    public IProcessLauncher Local => LocalProcessRunner.Instance;
    public IProcessLauncher ForOwner(string? ownerId) => LocalProcessRunner.Instance;
}
