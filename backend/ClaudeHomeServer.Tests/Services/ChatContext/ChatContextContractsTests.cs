using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.AudioEditor.Controllers;
using ClaudeHomeServer.Services.AudioEditor.Jobs;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Controllers;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.ChatContext;

// Контракты контекста чата (ADR-023, КТ-1): JSON-примеры из docs/adr/ADR-023-contracts.md
// десериализуются в записи и сериализуются обратно без потерь — так форма на проводе
// (camelCase, by строкой) зафиксирована одним источником примеров.
public class ChatContextContractsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Dto_RoundTrips_WithoutLoss() => AssertRoundTrip<ChatContextDto>("dto");

    [Fact]
    public void Conflict_RoundTrips_WithoutLoss() => AssertRoundTrip<ChatContextConflictDto>("conflict");

    [Fact]
    public void ChangedEvent_RoundTrips_WithoutLoss()
    {
        var node = ExampleNode("event");
        var msg = node.Deserialize<ChatContextChangedMessage>(Json)!;
        msg.Type.Should().Be(ChatContextEventNames.Changed);
        msg.SessionId.Should().Be("c1");
        msg.Context.Primary!.By.Should().Be(ContextActor.Agent);

        var back = JsonSerializer.SerializeToNode(msg, Json);
        JsonNode.DeepEquals(back, node).Should().BeTrue($"событие должно сериализоваться обратно без потерь: {back}");
    }

    [Fact]
    public void Dto_Example_ReadsGrayRefAndActor()
    {
        var dto = ExampleNode("dto").Deserialize<ChatContextDto>(Json)!;
        dto.Primary!.Role.Should().BeNull();
        dto.Refs.Should().HaveCount(3);
        dto.Refs.Single(r => r.Kind == "project-file").UsedBy.Should().BeEmpty();
        dto.Refs.Single(r => r.Kind == "image" ).By.Should().Be(ContextActor.Agent);
    }

    [Fact]
    public void Dto_HasNoExecutorField()
    {
        typeof(ChatContextDto).GetProperties().Select(p => p.Name).Should().NotContain("Executor");
    }

    // ── Запуск по ревизии (КТ-3): примеры тел из раздела «Запуск по ревизии контекста» ──

    [Fact]
    public void ImageQuoteRequest_RoundTrips_AndCarriesContextRevision()
    {
        AssertRoundTrip<ImageEditQuoteRequest>("image-quote-request");
        ExampleNode("image-quote-request").Deserialize<ImageEditQuoteRequest>(Json)!.ContextRevision.Should().Be(7);
    }

    [Fact]
    public void ImageQuote_RoundTrips_WithExecutorRows()
    {
        AssertRoundTrip<ImageEditQuoteDto>("image-quote");
        var rows = ExampleNode("image-quote").Deserialize<ImageEditQuoteDto>(Json)!.Executors!;
        rows.Should().HaveCount(3);
        rows.Single(r => r.Disabled).Reason.Should().NotBeNullOrEmpty("серая строка несёт причину");
    }

    [Fact]
    public void AudioQuoteRequest_RoundTrips_AndCarriesContextRevision()
    {
        AssertRoundTrip<AudioQuoteRequest>("audio-quote-request");
        ExampleNode("audio-quote-request").Deserialize<AudioQuoteRequest>(Json)!.ContextRevision.Should().Be(9);
    }

    [Fact]
    public void AudioQuote_RoundTrips_WithExecutorRows()
    {
        AssertRoundTrip<AudioQuoteDto>("audio-quote");
        ExampleNode("audio-quote").Deserialize<AudioQuoteDto>(Json)!.Executors.Should().ContainSingle();
    }

    [Fact]
    public void MixAndConcat_RoundTrip_AndCarryContextRevision()
    {
        AssertRoundTrip<AudioMixRequest>("audio-mix-request");
        AssertRoundTrip<AudioConcatRequest>("audio-concat-request");
        ExampleNode("audio-mix-request").Deserialize<AudioMixRequest>(Json)!.ContextRevision.Should().Be(9);
        ExampleNode("audio-concat-request").Deserialize<AudioConcatRequest>(Json)!.ContextRevision.Should().Be(9);
    }

    // Запуски идут multipart-формой: поля формы — публичные свойства класса, поэтому сверяем,
    // что каждое поле примера есть у формы и что у формы есть ContextRevision
    [Fact]
    public void JobForms_ExamplesMatchFormProperties()
    {
        AssertFormFields(typeof(ImageEditorEndpoints.StartJobForm), "image-job-form");
        AssertFormFields(typeof(AudioStartJobForm), "audio-job-form");
    }

    [Fact]
    public void AudioJobInput_HasContextRevision() =>
        typeof(AudioJobInput).GetProperty(nameof(AudioJobInput.ContextRevision))!.PropertyType.Should().Be(typeof(long?));

    [Fact]
    public void ImageLaunchRequest_HasContextRevision() =>
        typeof(ImageEditLaunchRequest).GetProperty(nameof(ImageEditLaunchRequest.ContextRevision))!.PropertyType.Should().Be(typeof(long?));

    // ── «Видео» (КТ-5) ──

    [Fact]
    public void VideoDto_RoundTrips_AndReadsFrameRoles()
    {
        AssertRoundTrip<ChatContextDto>("video-dto");
        var dto = ExampleNode("video-dto").Deserialize<ChatContextDto>(Json)!;
        dto.Primary!.Kind.Should().Be("video-scene");
        dto.Refs.Select(r => r.Role).Should().BeEquivalentTo(["frame-a", "frame-b"]);
        dto.Refs.Should().OnlyContain(r => r.UsedBy.Contains("shoot"));
    }

    [Fact]
    public void VideoQuoteAndLaunch_RoundTrip_AndCarryContextRevision()
    {
        AssertRoundTrip<VideoQuoteRequest>("video-quote-request");
        AssertRoundTrip<VideoLaunchRequest>("video-launch-request");
        ExampleNode("video-quote-request").Deserialize<VideoQuoteRequest>(Json)!.ContextRevision.Should().Be(12);
        ExampleNode("video-launch-request").Deserialize<VideoLaunchRequest>(Json)!.Params!["request"]!.ToString()
            .Should().Be("добавь туман над долиной");
    }

    private static void AssertFormFields(Type form, string label)
    {
        var props = form.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ((JsonObject)ExampleNode(label)).Select(kv => kv.Key))
            props.Should().Contain(key, $"поле «{key}» примера «{label}» должно быть у {form.Name}");
        props.Should().Contain("ContextRevision");

        // обратная сверка: поле формы, которого нет в примере, обязано быть названо в контракте в обратных кавычках
        // (прозой «не нужны»/«остаются»); совсем не описанное поле — красный
        var keys = ((JsonObject)ExampleNode(label)).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var doc = File.ReadAllText(FindContractsFile());
        foreach (var prop in form.GetProperties().Select(p => p.Name).Where(n => !keys.Contains(n)))
            Regex.IsMatch(doc, "`" + Regex.Escape(prop) + "`", RegexOptions.IgnoreCase)
                .Should().BeTrue($"поле формы {form.Name}.{prop} нет ни в примере «{label}», ни в тексте контракта");
    }

    private static void AssertRoundTrip<T>(string label)
    {
        var node = ExampleNode(label);
        var back = JsonSerializer.SerializeToNode(node.Deserialize<T>(Json), Json);
        JsonNode.DeepEquals(back, node).Should().BeTrue(
            $"пример «{label}» должен сериализоваться обратно без потерь.\nбыло:  {node}\nстало: {back}");
    }

    private static JsonNode ExampleNode(string label)
    {
        var text = File.ReadAllText(FindContractsFile());
        var m = Regex.Match(text, "```json " + Regex.Escape(label) + @"\r?\n(.*?)\r?\n```", RegexOptions.Singleline);
        m.Success.Should().BeTrue($"в ADR-023-contracts.md нет блока ```json {label}");
        return JsonNode.Parse(m.Groups[1].Value)!;
    }

    private static string FindContractsFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "adr", "ADR-023-contracts.md");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("docs/adr/ADR-023-contracts.md не найден выше каталога тестов");
    }
}
