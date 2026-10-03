using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChatContext;
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
