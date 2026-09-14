namespace ClaudeHomeServer.Services.Git;

/// <summary>
/// Read-проекция креденшалов Forgejo для вертикали Git. Без пароля: пароль
/// живёт в <c>Models.User.ForgejoPassword</c> и в зону Git не отдаётся —
/// контракт вертикали на чтение уже сокращён до минимума (логин + токен).
/// </summary>
public sealed record ForgejoAccountView(string? ForgejoUsername, string? ForgejoToken);

/// <summary>Узкий контракт Git для хранения и чтения аккаунта Forgejo пользователя.</summary>
public interface IForgejoAccountStore
{
    /// <summary>Сохраняет аккаунт; возвращает false, если пользователь не найден.</summary>
    bool SetForgejoAccount(string userId, string username, string token, string? password = null);

    /// <summary>
    /// Текущий аккаунт Forgejo (логин + токен) или null, если пользователь
    /// не провижнен. null-параметр userId — отказ (внутренняя ошибка).
    /// </summary>
    ForgejoAccountView? GetAccount(string userId);
}