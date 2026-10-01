using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.AudioEditor;
using ClaudeHomeServer.Services.AudioEditor.Prefs;
using ClaudeHomeServer.Services.AudioEditor.Threads;
using FluentAssertions;

namespace ClaudeHomeServer.AudioEditor.Tests.Prefs;

// Входы операции (Inputs) в настройках нити и префах: белый список ключей и типов, пути — только
// внутри проекта, у личной области путей нет; старые записи без Inputs читаются
public sealed class AudioOpInputsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "audio-inputs-" + Guid.NewGuid().ToString("N"));
    private readonly AudioEditScope _project;

    public AudioOpInputsTests()
    {
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        _project = AudioEditScope.Of(new Project { Id = "p-1", RootPath = projectRoot });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static JsonObject Full() => JsonNode.Parse("""
        {
          "language": "en-US",
          "referencePath": "samples/anya.wav",
          "startSec": 1.5,
          "endSec": 12,
          "voice": "voice:anya",
          "pieces": [ { "threadId": "t1", "versionId": "v2" }, { "projectFile": "intro.mp3" } ],
          "joint": { "kind": "crossfade", "seconds": 0.5 },
          "joints": [ null ],
          "dialogue": [ { "text": "Привет", "voice": "voice:anya" }, { "text": "Здравствуй" } ]
        }
        """)!.AsObject();

    [Fact]
    public void Полный_набор_входов_принимается()
    {
        AudioOpInputs.Validate(Full(), _project).Should().BeNull();
        AudioOpInputs.Validate(null, _project).Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "speed": 1 }""", "inputs.speed")]
    [InlineData("""{ "language": 5 }""", "inputs.language")]
    [InlineData("""{ "startSec": -1 }""", "inputs.startSec")]
    [InlineData("""{ "startSec": "1" }""", "inputs.startSec")]
    [InlineData("""{ "voice": "anya" }""", "inputs.voice")]
    [InlineData("""{ "voice": "voice:../x" }""", "inputs.voice")]
    [InlineData("""{ "joint": { "kind": "slide" } }""", "inputs.joint")]
    [InlineData("""{ "pieces": [ { "threadId": "t1", "projectFile": "a.mp3" } ] }""", "inputs.pieces")]
    [InlineData("""{ "pieces": [ { "file": "a.mp3" } ] }""", "inputs.pieces")]
    [InlineData("""{ "dialogue": [ { "text": "a", "speed": 1 } ] }""", "inputs.dialogue")]
    public void Неизвестный_ключ_и_неверный_тип_отказ(string json, string where)
    {
        AudioOpInputs.Validate(JsonNode.Parse(json)!.AsObject(), _project).Should().StartWith(where);
    }

    [Theory]
    [InlineData("referencePath", "../secret.wav")]
    [InlineData("referencePath", "/etc/passwd")]
    public void Путь_вне_проекта_отказ(string key, string path)
    {
        AudioOpInputs.Validate(new JsonObject { [key] = path }, _project).Should().Be($"inputs.{key}: путь вне проекта");
        AudioOpInputs.Validate(JsonNode.Parse($$"""{ "pieces": [ { "projectFile": {{JsonSerializer.Serialize(path)}} } ] }""")!.AsObject(),
            _project).Should().Contain("путь вне проекта");
    }

    [Fact]
    public void Путь_через_ссылку_отказ()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(_project.Project!.RootPath, "link"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        AudioOpInputs.Validate(new JsonObject { ["referencePath"] = "link/a.wav" }, _project)
            .Should().Be("inputs.referencePath: путь вне проекта");
    }

    [Fact]
    public void Личный_чат_без_путей_проекта()
    {
        var personal = new AudioEditScope(AudioEditScope.Personal, null);
        AudioOpInputs.Validate(new JsonObject { ["referencePath"] = "a.wav" }, personal)
            .Should().Be("inputs.referencePath: у личного чата путей проекта нет");
        AudioOpInputs.Validate(new JsonObject { ["language"] = "ru", ["voice"] = "voice:anya" }, personal).Should().BeNull();
    }

    [Fact]
    public void Префы_хранят_входы_отдельно_и_старая_запись_читается()
    {
        var store = new AudioPrefsStore(Path.Combine(_root, "prefs"));
        var path = store.PathOf("owner-1", "p-1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "voice": { "operation": "speak", "count": 2, "fields": { "speed": 1.2 } } }""");

        var old = store.Get("owner-1", "p-1", AudioModes.Voice)!;
        old.Inputs.Should().BeNull();
        old.Fields!["speed"]!.GetValue<double>().Should().Be(1.2);

        store.Save("owner-1", "p-1", AudioModes.Voice, old with { Inputs = new JsonObject { ["language"] = "ru" } });
        var saved = store.Get("owner-1", "p-1", AudioModes.Voice)!;
        saved.Inputs!.ToJsonString().Should().Be("""{"language":"ru"}""");
        saved.Fields!.ToJsonString().Should().Be("""{"speed":1.2}""");

        var thread = saved.ToThreadSettings(AudioModes.Voice);
        thread.Inputs!.ToJsonString().Should().Be("""{"language":"ru"}""");
        thread.Fields!.ContainsKey("language").Should().BeFalse();
    }

    [Fact]
    public void Старые_настройки_нити_без_входов_читаются()
    {
        var store = new AudioThreadStore(Path.Combine(_root, "threads"));
        var threadId = store.Open("owner-1", "chat-1", null, "", null).Thread!.Id;
        var path = Directory.GetFiles(store.Root, "*.json", SearchOption.AllDirectories).Single();
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["threads"]![0]!["settings"] = JsonNode.Parse("""{ "mode": "voice", "operation": "speak", "fields": { "speed": 1 } }""");
        File.WriteAllText(path, json.ToJsonString());

        var settings = store.Get("owner-1", "chat-1").Threads.Single(t => t.Id == threadId).Settings!;
        settings.Inputs.Should().BeNull();
        settings.Fields!["speed"]!.GetValue<int>().Should().Be(1);
    }
}
