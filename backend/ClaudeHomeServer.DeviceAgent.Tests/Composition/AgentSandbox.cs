using ClaudeHomeServer.DeviceAgent.Composition;

namespace ClaudeHomeServer.DeviceAgent.Tests.Composition;

/// <summary>
/// Машина в миниатюре: разрешённый корень с проектом внутри и «чужой» каталог рядом —
/// туда ведут ссылки наружу. Всё во временной папке, удаляется после теста.
/// </summary>
internal sealed class AgentSandbox : IDisposable
{
    public string Base { get; } = Path.Combine(Path.GetTempPath(), "agent-g7-" + Guid.NewGuid().ToString("N"));
    public string AllowedRoot => Path.Combine(Base, "allowed");
    public string Project => Path.Combine(AllowedRoot, "proj");
    public string Outside => Path.Combine(Base, "outside");

    public AgentSandbox()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "секрет");
        Directory.CreateDirectory(Path.Combine(Outside, "dir"));
        File.WriteAllText(Path.Combine(Outside, "dir", "inner.txt"), "внутри чужого");
        File.WriteAllText(Path.Combine(Project, "a.txt"), "привет");
    }

    public AgentPathPolicy Policy(AgentLimits? limits = null) => new(new FixedRoots(AllowedRoot), limits);

    /// <summary>Ссылка; без прав на ссылки (Windows без режима разработчика) — пропуск теста.</summary>
    public void Link(string linkPath, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(linkPath, target);
            else File.CreateSymbolicLink(linkPath, target);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Skip.If(true, "Нет прав создавать символические ссылки: " + e.Message);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Base, recursive: true); } catch { /* временная папка */ }
    }

    internal sealed class FixedRoots(params string[] roots) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = roots;
    }
}
