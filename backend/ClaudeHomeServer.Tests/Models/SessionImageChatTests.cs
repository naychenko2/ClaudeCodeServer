using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ImageEditor;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Models;

// Контракты волны 0 редактора v2 (ADR-018): привязка чата к картинке живёт на Session и едет
// в sessions.json и на фронт; старые записи читаются без миграции.
public class SessionImageChatTests
{
    // Опции стора сессий (SessionManager._jsonOpts)
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Правила wire для фронта: camelCase + enum'ы строками
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void СтараяЗаписьБезПоля_ЧатНеКартинки()
    {
        const string legacy = """{"id":"s1","provider":"claude","model":"sonnet"}""";
        var session = JsonSerializer.Deserialize<Session>(legacy, StoreJson)!;
        session.ImageChat.Should().BeNull("аддитивное поле: старый sessions.json читается без миграции");
    }

    [Fact]
    public void ЧатКартинки_ПереживаетКругСериализацииСтора()
    {
        var original = new Session
        {
            Id = "s1",
            ImageChat = new SessionImageChat
            {
                CurrentPath = "images/hero.v2.png",
                Lineage = ["images/hero.png"],
            },
        };

        var restored = JsonSerializer.Deserialize<Session>(JsonSerializer.Serialize(original, StoreJson), StoreJson)!;

        restored.ImageChat.Should().NotBeNull();
        restored.ImageChat!.CurrentPath.Should().Be("images/hero.v2.png");
        restored.ImageChat.Lineage.Should().Equal("images/hero.png");
    }

    [Fact]
    public void ОбычныйЧат_ПереживаетКругСериализацииБезПривязки()
    {
        var restored = JsonSerializer.Deserialize<Session>(
            JsonSerializer.Serialize(new Session { Id = "s1" }, StoreJson), StoreJson)!;
        restored.ImageChat.Should().BeNull();
    }

    [Fact]
    public void ЧатКартинки_УходитНаФронтВCamelCase()
    {
        var session = new Session { ImageChat = new SessionImageChat { CurrentPath = "a.png", Lineage = ["b.png"] } };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(session, WireJson));

        var chat = doc.RootElement.GetProperty("imageChat");
        chat.GetProperty("currentPath").GetString().Should().Be("a.png");
        chat.GetProperty("lineage")[0].GetString().Should().Be("b.png");
    }

    [Fact]
    public void ОперацииПравки_ЧитаютсяПоДискриминаторуType()
    {
        const string json = """
            {"base":{"stepId":"st1"},
             "ops":[{"type":"autoOrient"},
                    {"type":"crop","rect":{"x":0.1,"y":0.2,"width":0.5,"height":0.5}},
                    {"type":"rotate","degrees":90},
                    {"type":"flip","axis":"horizontal"},
                    {"type":"resize","percent":50}],
             "encode":{"format":"webp","quality":80}}
            """;

        var req = JsonSerializer.Deserialize<ImageTransformRequest>(json, WireJson)!;

        req.Base.StepId.Should().Be("st1");
        req.Ops.Select(o => o.GetType()).Should().Equal(
            typeof(AutoOrientOp), typeof(CropOp), typeof(RotateOp), typeof(FlipOp), typeof(ResizeOp));
        req.Ops.OfType<FlipOp>().Single().Axis.Should().Be(ImageFlipAxis.Horizontal);
        req.Ops.OfType<ResizeOp>().Single().LockAspect.Should().BeTrue("замок пропорций включён по умолчанию");
        req.Encode.Should().Be(new ImageEncodeSpec(ImageEncodeFormat.Webp, 80));
    }

    [Fact]
    public void СобытиеСостоянияЧата_ТипИЧатВБазовомSessionId()
    {
        var state = new ImageChatState("закат", ImageEditInitiator.Agent, "fal", "flux", EditMode.Auto, 2,
            [], null, null, null, null, null, true, [], 3);
        var msg = new ImageChatStateMessage("p1", 3, state, ImageEditInitiator.Agent, []) { SessionId = "s1" };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(msg, msg.GetType(), WireJson));
        doc.RootElement.GetProperty("type").GetString().Should().Be("image_chat_state");
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("s1");
        doc.RootElement.GetProperty("changedBy").GetString().Should().Be("agent");
        doc.RootElement.GetProperty("state").GetProperty("promptAuthor").GetString().Should().Be("agent");
    }
}
