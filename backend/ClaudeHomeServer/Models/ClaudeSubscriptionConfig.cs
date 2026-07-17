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

    // Альтернатива OAuth: API-ключ (sk-ant-...). Если задан — используется как
    // ANTHROPIC_AUTH_TOKEN в изолированном профиле, без .credentials.json.
    // Приоритет: ApiKey > OAuthToken.
    public string ApiKey { get; set; } = "";

    // Аккаунту доступны Opus-модели. false — план без Opus (например, Pro):
    // пул не отдаёт такому аккаунту чаты с пином opus-тира, иначе CLI падает
    // «There's an issue with the selected model (opus)».
    public bool SupportsOpus { get; set; } = true;

    // Провайдер включён при наличии хотя бы одного способа аутентификации
    public bool Enabled => !string.IsNullOrWhiteSpace(OAuthToken) || !string.IsNullOrWhiteSpace(ApiKey);
}
