namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>Общий предок ошибок CDP. Тексты — по-английски: они доходят до модели.</summary>
public class CdpException(string message) : Exception(message);

/// <summary>Браузер ответил на команду ошибкой (поле <c>error</c> ответа).</summary>
public sealed class CdpProtocolException(string method, int code, string errorMessage)
    : CdpException($"{method} failed: {errorMessage} (code {code})")
{
    public string Method { get; } = method;
    public int Code { get; } = code;
    public string ErrorMessage { get; } = errorMessage;
}

/// <summary>Браузер не ответил на команду за отведённое время.</summary>
public sealed class CdpTimeoutException(string method, TimeSpan timeout)
    : CdpException($"{method}: no response from the browser within {timeout.TotalSeconds:0.#} s")
{
    public string Method { get; } = method;
}

/// <summary>Труба до браузера оборвалась: все ожидающие команды и события завершаются им.</summary>
public sealed class CdpDisconnectedException(string reason)
    : CdpException("Browser connection lost: " + reason)
{
    public string Reason { get; } = reason;
}
