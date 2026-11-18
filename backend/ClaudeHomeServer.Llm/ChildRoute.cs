using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.ChildMode;

namespace ClaudeHomeServer.Services.Llm;

/// <summary>
/// Отказ запуска хода ребёнка (И2): нет записи пула с API-ключом, профиль нечитаем или запись
/// несёт токен подписки. Откатываться на родную подписку нельзя — только отказ.
/// Текст показывается человеку (TurnFailureText.ForException).
/// </summary>
public sealed class ChildRouteRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// Маршрут хода ребёнка (И2): запись ClaudeSubscriptionPool из ChildProfile.ProviderKey, у которой
/// есть ApiKey и нет OAuthToken. Любое другое состояние — отказ, а не откат на подписку.
/// </summary>
public static class ChildApiKeyRoute
{
    public const string RefusedText =
        "Ход не выполнен: для детского режима не настроен отдельный API-ключ. Обратитесь к администратору.";

    public static ClaudeSubscriptionConfig Resolve(ChildPolicySnapshot snapshot, ClaudeSubscriptionPool? pool)
    {
        if (snapshot.DenyAll || snapshot.Profile is not { } profile
            || string.IsNullOrWhiteSpace(profile.ProviderKey))
            throw new ChildRouteRefusedException(RefusedText);
        var entry = pool?.All.FirstOrDefault(s => s.Key == profile.ProviderKey);
        // Запись с OAuthToken — подписка, даже если ApiKey тоже задан: токен мог бы уехать в ход.
        if (entry is null || string.IsNullOrWhiteSpace(entry.ApiKey) || !string.IsNullOrWhiteSpace(entry.OAuthToken))
            throw new ChildRouteRefusedException(RefusedText);
        return entry;
    }
}
