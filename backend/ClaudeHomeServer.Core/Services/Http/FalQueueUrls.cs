namespace ClaudeHomeServer.Services.Http;

// Сверка адресов из ответа очереди fal (status_url, response_url, cancel_url): на них уходит
// ключ Fal:ApiKey, поэтому — только https и только хост fal (fal.run и его поддомены, в том
// числе queue.fal.run) либо хост настроенной базы очереди Fal:QueueBase. Иначе ключ не шлём.
// Общий для модулей картинок и звука: вертикаль не ссылается на вертикаль
public static class FalQueueUrls
{
    private const string FalHost = "fal.run";

    public static bool IsTrusted(string? url, string queueBase)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        var host = uri.IdnHost;
        if (host.Equals(FalHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + FalHost, StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(queueBase, UriKind.Absolute, out var configured)
            && configured.Scheme == Uri.UriSchemeHttps
            && host.Equals(configured.IdnHost, StringComparison.OrdinalIgnoreCase)
            && uri.Port == configured.Port;
    }
}
