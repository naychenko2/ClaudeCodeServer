namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>
/// Переменные, которые обязан получать КАЖДЫЙ запуск управляемой копии: версию меняет
/// только сервер. <c>DISABLE_AUTOUPDATER</c> гасит фоновую проверку, <c>DISABLE_UPDATES</c> —
/// ещё и ручные <c>claude update</c>/<c>claude install</c> (по документации — ровно для
/// раздачи CLI своими каналами). Allow-list env хода (задача 2.2) их пропускает.
/// </summary>
public static class ManagedCliEnvironment
{
    public static readonly IReadOnlyDictionary<string, string> Variables = new Dictionary<string, string>
    {
        ["DISABLE_AUTOUPDATER"] = "1",
        ["DISABLE_UPDATES"] = "1",
    };

    public static void ApplyTo(IDictionary<string, string?> env)
    {
        foreach (var (key, value) in Variables) env[key] = value;
    }
}
