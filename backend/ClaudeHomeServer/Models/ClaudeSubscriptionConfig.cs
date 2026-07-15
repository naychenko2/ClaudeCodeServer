namespace ClaudeHomeServer.Models;

// Дополнительная подписка Claude (секция конфига "ClaudeSubscriptions": словарь key → конфиг).
// Позволяет подключить второй/третий аккаунт Claude на одном сервере для балансировки
// нагрузки и failover при исчерпании лимита.
public class ClaudeSubscriptionConfig
{
    // Ключ подписки из словаря конфига (напр. "my-second"). Заполняется реестром при загрузке.
    public string Key { get; set; } = "";

    // Человекочитаемое имя (для логов и отладки)
    public string DisplayName { get; set; } = "";

    // OAuth-токен от claude setup-token на аккаунте второй подписки.
    // Получить: запустить `claude setup-token` на машине с браузером, залогиненной в нужный аккаунт.
    public string OAuthToken { get; set; } = "";

    // Провайдер включён только при наличии токена
    public bool Enabled => !string.IsNullOrWhiteSpace(OAuthToken);
}
