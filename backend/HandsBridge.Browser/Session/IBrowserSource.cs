using ClaudeHomeServer.HandsBridge.Browser.Cdp;

namespace ClaudeHomeServer.HandsBridge.Browser.Session;

/// <summary>
/// Итог <see cref="IBrowserSource.AcquireAsync"/>: браузер либо отказ для модели (по-английски, как
/// остальные отказы моста). <see cref="Restarted"/> — прежний Chrome умер (окно закрыли руками),
/// поднят новый: ссылки снимка устарели.
/// </summary>
public sealed record BrowserAcquire(CdpBrowser? Browser, bool Restarted, string? Refusal);

/// <summary>Откуда сессия берёт браузер: на Windows — Chrome моста, в тестах — подделка трубы.</summary>
public interface IBrowserSource
{
    Task<BrowserAcquire> AcquireAsync(CancellationToken cancellationToken = default);
}
