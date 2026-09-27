namespace ClaudeHomeServer.HandsBridge.Policy;

/// <summary>
/// Решение гейта. <see cref="Reason"/> отказа уходит модели как есть, поэтому говорит, что
/// сделать вместо (формулировки — на английском, как и весь остальной вывод моста).
/// </summary>
public sealed record HandsDecision(bool Allowed, string? Reason)
{
    public static readonly HandsDecision Allow = new(true, null);

    public static HandsDecision Deny(string reason) => new(false, reason);
}

/// <summary>Решение по запуску программы: при разрешении — нормализованный путь, его и запускаем.</summary>
public sealed record HandsLaunchDecision(bool Allowed, string? Reason, string? ProgramPath)
{
    public static HandsLaunchDecision Allow(string programPath) => new(true, null, programPath);

    public static HandsLaunchDecision Deny(string reason) => new(false, reason, null);
}
