using ClaudeHomeServer.HandsBridge.Browser.Session;
using ModelContextProtocol.Protocol;

namespace ClaudeHomeServer.HandsBridge.Browser;

/// <summary>
/// Ответ сессии браузера в форме ответа инструмента: картинка (если есть) и текст. Каждый вызов
/// оставляет в <c>hands.log</c> строку замера (<see cref="BrowserReply.LogLine"/>).
/// </summary>
internal static class BrowserToolResult
{
    public static CallToolResult From(string tool, BrowserReply reply)
    {
        HandsLog.Write(reply.LogLine(tool));

        var content = new List<ContentBlock>();
        if (reply.Png is not null)
            content.Add(ImageContentBlock.FromBytes(reply.Png, "image/png"));
        content.Add(new TextContentBlock { Text = reply.Text });
        return new CallToolResult { Content = content, IsError = reply.IsError };
    }
}
