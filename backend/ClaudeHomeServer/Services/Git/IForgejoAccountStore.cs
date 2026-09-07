namespace ClaudeHomeServer.Services.Git;

/// <summary>Узкий контракт Git для сохранения аккаунта Forgejo пользователя.</summary>
public interface IForgejoAccountStore
{
    /// <summary>Сохраняет аккаунт; возвращает false, если пользователь не найден.</summary>
    bool SetForgejoAccount(string userId, string username, string token, string? password = null);
}
