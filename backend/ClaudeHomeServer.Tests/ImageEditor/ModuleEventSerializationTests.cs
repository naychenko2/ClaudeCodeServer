using System.Buffers;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.ImageEditor;

// События редактора объявлены в сборке модуля, а не в Core (ADR-018 §10.1): Main их типов не
// видит и отдаёт в SendAsync("message", object). Поля доезжают до фронта, только если протокол
// хаба сериализует аргумент по ФАКТИЧЕСКОМУ типу, а не по объявленному ServerMessage. Тест
// берёт протокол из собранного хоста — с его конвертером enum'ов — и смотрит на байты.
public class ModuleEventSerializationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private JsonElement Wire(ServerMessage message)
    {
        var protocol = _factory.Services.GetServices<IHubProtocol>().OfType<JsonHubProtocol>().Single();
        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("message", [message]), buffer);
        // Кадр JSON-протокола SignalR заканчивается разделителем 0x1E
        var frame = buffer.WrittenSpan[..^1];
        return JsonDocument.Parse(frame.ToArray()).RootElement.GetProperty("arguments")[0].Clone();
    }

    [Fact]
    public void Событие_модуля_уходит_со_своими_полями()
    {
        typeof(ImageEditProgressMessage).Assembly.GetName().Name.Should().Be("ClaudeHomeServer.ImageEditor",
            "проверяется именно тип из сборки модуля");

        var json = Wire(new ImageEditProgressMessage("job-1", "p-1", EditStage.Queued, 2, "chat-1", ImageEditInitiator.Agent));

        json.GetProperty("type").GetString().Should().Be(ImageEditEventNames.Progress);
        json.GetProperty("jobId").GetString().Should().Be("job-1");
        json.GetProperty("projectId").GetString().Should().Be("p-1");
        json.GetProperty("queuePosition").GetInt32().Should().Be(2);
        json.GetProperty("chatSessionId").GetString().Should().Be("chat-1");
        json.GetProperty("initiator").GetString().Should().Be("agent", "enum уходит строкой, как у остальных событий");
    }

    // Живая пара записи модуля (ADR-019 §2): data модуля уходит как есть, а не строкой JSON,
    // поле recordType не путается с type события протокола
    [Fact]
    public void Запись_модуля_уходит_с_data_как_объектом()
    {
        var data = JsonSerializer.SerializeToElement(new { threadId = "t1", stackId = "s1" });

        var json = Wire(new ModuleRecordMessage("imageeditor", "image_thread", data, "Картинка: hero.png", 5)
            { SessionId = "chat-1" });

        json.GetProperty("type").GetString().Should().Be("module_record");
        json.GetProperty("sessionId").GetString().Should().Be("chat-1");
        json.GetProperty("module").GetString().Should().Be("imageeditor");
        json.GetProperty("recordType").GetString().Should().Be("image_thread");
        json.GetProperty("data").GetProperty("stackId").GetString().Should().Be("s1");
        json.GetProperty("fallback").GetString().Should().Be("Картинка: hero.png");
    }
}
