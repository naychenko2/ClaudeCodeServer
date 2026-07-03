using System.Text.Json;
using ClaudeHomeServer.Protocol;
using WebPush;

namespace ClaudeHomeServer.Services;

// Отправка web-push уведомлений (VAPID). Ключи автогенерируются при первом старте
// в data/vapid-keys.json (по образцу jwt-secret). Мёртвые подписки (404/410 от
// push-сервиса браузера) вычищаются автоматически.
public class PushService
{
    private readonly PushSubscriptionStore _store;
    private readonly ILogger<PushService> _log;
    private readonly WebPushClient _client = new();
    private readonly VapidDetails _vapid;

    public string PublicKey => _vapid.PublicKey;

    public PushService(IConfiguration config, PushSubscriptionStore store, ILogger<PushService> log)
    {
        _store = store;
        _log = log;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))!;
        var keysPath = Path.Combine(dataDir, "vapid-keys.json");
        // Subject обязателен по спеке VAPID: mailto: или https:-URL владельца сервера
        var subject = config["Push:Subject"] ?? "https://naychenko.me";

        if (File.Exists(keysPath))
        {
            var saved = JsonSerializer.Deserialize<VapidKeysFile>(File.ReadAllText(keysPath));
            if (saved is { PublicKey.Length: > 0, PrivateKey.Length: > 0 })
            {
                _vapid = new VapidDetails(subject, saved.PublicKey, saved.PrivateKey);
                return;
            }
        }

        var fresh = VapidHelper.GenerateVapidKeys();
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(keysPath, JsonSerializer.Serialize(
            new VapidKeysFile(fresh.PublicKey, fresh.PrivateKey),
            new JsonSerializerOptions { WriteIndented = true }));
        _vapid = new VapidDetails(subject, fresh.PublicKey, fresh.PrivateKey);
        _log.LogInformation("Сгенерированы VAPID-ключи для web push: {Path}", keysPath);
    }

    /// <summary>
    /// Шлёт уведомление на все подписанные устройства пользователя.
    /// Ошибки отдельных устройств не роняют рассылку (и не должны ронять планировщик).
    /// </summary>
    public async Task SendToUserAsync(string userId, NotificationMessage message)
    {
        var subscriptions = _store.GetByUser(userId);
        if (subscriptions.Count == 0) return;

        // tag: браузер заменяет уведомление с тем же тегом — нет дублей по одной задаче
        var payload = JsonSerializer.Serialize(new
        {
            title = message.Title,
            body = message.Body,
            url = message.Url,
            kind = message.Kind,
            tag = message.Url ?? message.Title,
        });

        foreach (var sub in subscriptions)
        {
            try
            {
                await _client.SendNotificationAsync(
                    new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth), payload, _vapid);
            }
            catch (WebPushException ex) when (
                ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                _log.LogInformation("Мёртвая push-подписка удалена: {Endpoint}", sub.Endpoint);
                _store.RemoveByEndpoint(sub.Endpoint);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Не удалось отправить push на {Endpoint}", sub.Endpoint);
            }
        }
    }

    private sealed record VapidKeysFile(string PublicKey, string PrivateKey);
}
