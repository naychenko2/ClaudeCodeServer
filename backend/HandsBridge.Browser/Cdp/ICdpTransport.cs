namespace ClaudeHomeServer.HandsBridge.Browser.Cdp;

/// <summary>
/// Труба до браузера в режиме <c>--remote-debugging-pipe</c>: рамка — JSON с нулевым байтом в
/// конце. На Windows её реализуют анонимные трубы запущенного Chrome, в тестах — подделка.
/// Потоки и процесс принадлежат транспорту: соединение их не закрывает.
/// </summary>
public interface ICdpTransport
{
    /// <summary>Поток от браузера к нам.</summary>
    Stream Read { get; }

    /// <summary>Поток от нас к браузеру.</summary>
    Stream Write { get; }

    /// <summary>Завершается, когда процесс браузера вышел.</summary>
    Task Exited { get; }
}
