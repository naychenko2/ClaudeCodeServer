using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

// Записи ленты чата картинки (ADR-018 §1–3): полиморфный StoredMessage в history.json.
public class StoredMessageImageChatTests
{
    // Опции ChatHistoryService: camelCase, без конвертера enum'ов
    private static readonly JsonSerializerOptions HistoryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static List<StoredMessage> RoundTrip(List<StoredMessage> history, out JsonElement raw)
    {
        var json = JsonSerializer.Serialize(history, HistoryJson);
        raw = JsonDocument.Parse(json).RootElement.Clone();
        return JsonSerializer.Deserialize<List<StoredMessage>>(json, HistoryJson)!;
    }

    [Fact]
    public void ЗаписиЗапускаИПереездаФайла_ПереживаютКругСериализацииИстории()
    {
        var history = new List<StoredMessage>
        {
            new StoredImageLaunchMessage
            {
                By = "human",
                Prompt = "убрать провод",
                Provider = "fal",
                Model = "flux-fill",
                Count = 2,
                Estimate = new ImageEditEstimateDto(0.1, "usd", true, ImageEditEstimateSources.Catalog),
                JobId = "j1",
                Timestamp = 1,
            },
            new StoredImageFileMovedMessage { From = "images/hero.png", To = "images/blog/hero-evening.png", Timestamp = 2 },
        };

        var restored = RoundTrip(history, out var raw);

        raw[0].GetProperty("kind").GetString().Should().Be("image_launch");
        raw[1].GetProperty("kind").GetString().Should().Be("image_file_moved");

        var launch = restored[0].Should().BeOfType<StoredImageLaunchMessage>().Subject;
        launch.By.Should().Be("human");
        launch.Prompt.Should().Be("убрать провод");
        launch.Count.Should().Be(2);
        launch.Estimate!.Amount.Should().Be(0.1);
        launch.JobId.Should().Be("j1");

        var moved = restored[1].Should().BeOfType<StoredImageFileMovedMessage>().Subject;
        moved.From.Should().Be("images/hero.png");
        moved.To.Should().Be("images/blog/hero-evening.png");
    }

    [Fact]
    public void СнимокХолста_ЕдетВСообщенииПользователя_СтароеСообщениеБезНего()
    {
        var restored = RoundTrip(
        [
            new StoredUserMessage("что с небом?") { ImageSnapshot = new StoredImageSnapshot("rev-7", false) },
            new StoredUserMessage("обычное"),
        ], out var raw);

        raw[0].GetProperty("imageSnapshot").GetProperty("revision").GetString().Should().Be("rev-7");
        var snap = restored[0].Should().BeOfType<StoredUserMessage>().Subject.ImageSnapshot;
        snap.Should().Be(new StoredImageSnapshot("rev-7", false));
        ((StoredUserMessage)restored[1]).ImageSnapshot.Should().BeNull();
    }
}
