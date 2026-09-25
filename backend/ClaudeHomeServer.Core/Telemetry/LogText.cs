namespace ClaudeHomeServer.Core.Telemetry;

/// <summary>
/// Внешний текст для строки лога. Перевод строки внутри значения после таймстемпов
/// TimestampedConsoleWriter неотличим от настоящей записи (CWE-117), а сообщение без лимита
/// раздувает лог — поэтому одна строка и, по желанию, потолок длины.
/// </summary>
public static class LogText
{
    public static string OneLine(string text, int maxLength = int.MaxValue)
    {
        if (text.Length > maxLength) text = text[..maxLength] + "…";
        return text.Replace("\r", " ").Replace("\n", " ");
    }
}
