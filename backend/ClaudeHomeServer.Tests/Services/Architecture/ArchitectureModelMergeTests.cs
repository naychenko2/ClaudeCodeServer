using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Сборка стартовой модели (ArchitectureModelBuilder) и слияние с файлом Viaduct
// (ArchitectureModelMerger): повторная сборка не затирает ручную работу.
public class ArchitectureModelMergeTests
{
    private static ArchitectureInput SampleInput(params CodeTypeInfo[] extraTypes)
    {
        var units = new[]
        {
            new SourceUnit("backend/App", "App", "csharp", false, ["backend/App.Core"]),
            new SourceUnit("backend/App.Core", "App.Core", "csharp", false, []),
            new SourceUnit("backend/App.Tests", "App.Tests", "csharp", true, ["backend/App"]),
            new SourceUnit("frontend", "web", "react", false, []),
        };
        var types = new List<CodeTypeInfo>
        {
            new("App.Auth.Jwt", "Jwt", "backend/App/Services/Auth/Jwt.cs", "Class"),
            new("App.Auth.Keys", "Keys", "backend/App/Services/Auth/Keys.cs", "Class"),
            new("App.Chat.Hub", "Hub", "backend/App/Services/Chat/Hub.cs", "Class"),
            new("App.Core.ISub", "IAppSubsystem", "backend/App.Core/Composition/ISub.cs", "Interface"),
            new("App.Core.Safe", "SafePath", "backend/App.Core/Services/SafePath.cs", "Class"),
            new("App.Tests.JwtTests", "JwtTests", "backend/App.Tests/JwtTests.cs", "Class"),
            new("web.Button", "Button", "frontend/src/components/ui/Button.tsx", "Class"),
            new("web.Page", "Page", "frontend/src/pages/Page.tsx", "Class"),
        };
        types.AddRange(extraTypes);
        var edges = new[]
        {
            new CodeEdgeInfo("App.Chat.Hub", "App.Auth.Jwt", "Calls"),
            new CodeEdgeInfo("App.Chat.Hub", "App.Auth.Keys", "References"),
            new CodeEdgeInfo("App.Auth.Jwt", "App.Core.Safe", "Calls"),
            new CodeEdgeInfo("web.Page", "web.Button", "References"),
            new CodeEdgeInfo("App.Tests.JwtTests", "App.Auth.Jwt", "Calls"),
        };
        return new ArchitectureInput("Demo", units,
            new CodeSnapshotInput(types, edges, DateTimeOffset.Parse("2026-09-25T10:00:00Z")),
            new Dictionary<string, string> { ["App.Chat.Hub"] = "Чат" });
    }

    private static JsonArray Arr(JsonObject doc, string name) => (JsonArray)doc["state"]!["model"]![name]!;

    private static JsonObject ByName(JsonObject doc, string list, string name) =>
        Arr(doc, list).OfType<JsonObject>().Single(o => (string?)o["name"] == name);

    [Fact]
    public void Build_КонтейнерыИзПроектов_ТестыВыброшены_СвязиИзProjectReference()
    {
        var model = ArchitectureModelBuilder.Build(SampleInput());

        model.Containers.Select(c => c.Name).Should().BeEquivalentTo(["App", "App.Core", "web"]);
        var app = model.Containers.Single(c => c.Name == "App");
        app.Connections.Should().ContainSingle().Which.TargetKey.Should().Be("backend/App.Core");
        app.Technology.Should().Be("csharp");
        // Одна подсистема в контейнере — её заголовок идёт в описание контейнера и компонента.
        app.Description.Should().Be("Чат");
        model.Components.Single(c => c.Key == "backend/App::Services/Chat").Description.Should().Be("Чат");

        model.Components.Select(c => c.Key).Should().NotContain(k => k.Contains("Tests"));
        model.Components.Single(c => c.Key == "backend/App::Services/Chat").Connections
            .Should().ContainSingle(c => c.TargetKey == "backend/App::Services/Auth");
        model.Components.Single(c => c.Key == "frontend::pages").Connections
            .Should().ContainSingle(c => c.TargetKey == "frontend::components/ui");
    }

    [Fact]
    public void Build_Детерминирован()
    {
        var a = ArchitectureModelMerger.Merge(null, ArchitectureModelBuilder.Build(SampleInput())).Document.ToJsonString();
        var b = ArchitectureModelMerger.Merge(null, ArchitectureModelBuilder.Build(SampleInput())).Document.ToJsonString();
        a.Should().Be(b);
    }

    [Fact]
    public void Build_ПотолокКомпонентовВКонтейнере()
    {
        var many = Enumerable.Range(0, ArchitectureModelBuilder.MaxComponentsPerContainer + 7)
            .Select(i => new CodeTypeInfo($"App.F{i}", $"F{i}", $"backend/App/F{i:00}/T.cs", "Class"))
            .ToArray();
        var model = ArchitectureModelBuilder.Build(SampleInput(many));

        model.Components.Count(c => c.ParentKey == "backend/App")
            .Should().Be(ArchitectureModelBuilder.MaxComponentsPerContainer);
    }

    [Fact]
    public void Build_БезПроектовМаркеров_СворачиваетПоПапкамВерхнегоУровня()
    {
        var input = new ArchitectureInput("Py", [],
            new CodeSnapshotInput(
                [
                    new CodeTypeInfo("a", "A", "api/handlers/a.py", "Class"),
                    new CodeTypeInfo("b", "B", "worker/jobs/b.py", "Class"),
                ],
                [new CodeEdgeInfo("a", "b", "Calls")],
                null),
            new Dictionary<string, string>());

        var model = ArchitectureModelBuilder.Build(input);

        model.Containers.Select(c => c.Key).Should().Equal("api", "worker");
        model.Containers.Single(c => c.Key == "api").Connections.Should().ContainSingle(c => c.TargetKey == "worker");
        model.Components.Select(c => c.Key).Should().Equal("api::handlers", "worker::jobs");
    }

    [Fact]
    public void Merge_СНуля_ДаётPersistОбёрткуViaduct()
    {
        var merged = ArchitectureModelMerger.Merge(null, ArchitectureModelBuilder.Build(SampleInput()));
        var doc = merged.Document;

        doc["version"]!.GetValue<int>().Should().Be(0);
        doc["state"]!["model"]!["viewLevel"]!.GetValue<string>().Should().Be("system");
        Arr(doc, "systems").Should().ContainSingle();
        Arr(doc, "codeElements").Should().BeEmpty();

        var sysId = (string)Arr(doc, "systems")[0]!["id"]!;
        var app = ByName(doc, "containers", "App");
        ((string)app["systemId"]!).Should().Be(sysId);
        ((string)app["type"]!).Should().Be("container");
        var core = ByName(doc, "containers", "App.Core");
        app["connections"]!.AsArray().Select(c => (string)c!["targetId"]!).Should().Equal((string)core["id"]!);

        var auth = ByName(doc, "components", "Services/Auth");
        ((string)auth["containerId"]!).Should().Be((string)app["id"]!);
        ((string)auth["systemId"]!).Should().Be(sysId);
        merged.Matched.Should().Be(0);
    }

    [Fact]
    public void Merge_Повторно_СохраняетРучнуюРаботуИНеПлодитДубли()
    {
        var generated = ArchitectureModelBuilder.Build(SampleInput());
        var first = ArchitectureModelMerger.Merge(null, generated).Document;

        // Человек поработал в редакторе.
        var app = ByName(first, "containers", "App");
        app["description"] = "Бэкенд — описал руками";
        app["position"] = new JsonObject { ["x"] = 999, ["y"] = -5 };
        app["docs"] = new JsonArray(new JsonObject { ["title"] = "ADR" }); // чужое поле Viaduct
        var chat = ByName(first, "components", "Services/Chat");
        chat["description"] = "Хаб чата, моё описание";
        app["connections"]!.AsArray().Add(new JsonObject { ["targetId"] = "manual-target", ["label"] = "руками" });
        Arr(first, "containers").Add(new JsonObject
        {
            ["id"] = "manual-1", ["name"] = "Dify", ["type"] = "container",
            ["systemId"] = (string)Arr(first, "systems")[0]!["id"]!,
            ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["connections"] = new JsonArray(),
        });

        var second = ArchitectureModelMerger.Merge(JsonNode.Parse(first.ToJsonString()), generated);
        var doc = second.Document;

        second.Added.Should().Be(0);
        second.ConnectionsAdded.Should().Be(0);
        var app2 = ByName(doc, "containers", "App");
        ((string)app2["description"]!).Should().Be("Бэкенд — описал руками");
        app2["position"]!["x"]!.GetValue<int>().Should().Be(999);
        app2["docs"]!.AsArray().Should().ContainSingle();
        app2["connections"]!.AsArray().Should().HaveCount(2);
        ((string)ByName(doc, "components", "Services/Chat")["description"]!).Should().Be("Хаб чата, моё описание");
        ByName(doc, "containers", "Dify")["id"]!.GetValue<string>().Should().Be("manual-1");
        Arr(doc, "containers").Should().HaveCount(4);
        Arr(doc, "components").Should().HaveCount(Arr(first, "components").Count);
    }

    [Fact]
    public void Build_ФайлыВКорнеКонтейнера_КомпонентНазванПоКонтейнеру_КлючПрежний()
    {
        var model = ArchitectureModelBuilder.Build(SampleInput(
            new CodeTypeInfo("App.Program", "Program", "backend/App/Program.cs", "Class"),
            new CodeTypeInfo("App.Core.Root", "Root", "backend/App.Core/Root.cs", "Class")));

        var roots = model.Components.Where(c => c.Key.EndsWith("::" + ArchitecturePathFolding.RootName)).ToList();
        roots.Select(c => c.Name).Should().BeEquivalentTo(["App", "App.Core"], "у каждого контейнера свой корень, а не «(корень)» у всех");
        roots.Select(c => c.Key).Should().BeEquivalentTo(
            ["backend/App::" + ArchitecturePathFolding.RootName, "backend/App.Core::" + ArchitecturePathFolding.RootName],
            "ключ — основа стабильного id, его не меняем");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(18, 5)]
    [InlineData(60, ArchitectureModelMerger.MaxGridColumns)]
    public void GridColumnsFor_ПочтиКвадрат(int count, int columns) =>
        ArchitectureModelMerger.GridColumnsFor(count).Should().Be(columns);

    [Fact]
    public void Merge_ЧетыреКонтейнера_СеткаДваНаДва_АНеЛиния()
    {
        var input = new ArchitectureInput("Demo",
            new[] { "a", "b", "c", "d" }.Select(n => new SourceUnit(n, n, "csharp", false, [])).ToArray(),
            new CodeSnapshotInput([], [], null), new Dictionary<string, string>());

        var doc = ArchitectureModelMerger.Merge(null, ArchitectureModelBuilder.Build(input)).Document;

        Arr(doc, "containers").OfType<JsonObject>()
            .Select(o => ((int)o["position"]!["x"]!, (int)o["position"]!["y"]!))
            .Should().BeEquivalentTo(new[]
            {
                (0, 0), (ArchitectureModelMerger.GridStepX, 0),
                (0, ArchitectureModelMerger.GridStepY), (ArchitectureModelMerger.GridStepX, ArchitectureModelMerger.GridStepY),
            });
    }

    [Fact]
    public void FreeCell_ЗанятыеКлеткиПропускает_ВтомЧислеДробныеКоординаты()
    {
        var siblings = new List<JsonObject>
        {
            new() { ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 } },
            new() { ["position"] = new JsonObject { ["x"] = 350.5, ["y"] = 10.25 } }, // сдвинут руками
            new() { ["name"] = "без позиции" },
        };

        ArchitectureModelMerger.FreeCell(siblings, 2).Should().Be((0, ArchitectureModelMerger.GridStepY));
    }

    [Fact]
    public void Merge_ЭлементСозданныйРуками_НаходитсяПоИмени_ИДержитСвойId()
    {
        var generated = ArchitectureModelBuilder.Build(SampleInput());
        var existing = JsonNode.Parse("""
            {"state":{"model":{"systems":[{"id":"s-hand","name":"Мой продукт","type":"system",
              "position":{"x":1,"y":2},"connections":[]}],
              "containers":[{"id":"c-hand","name":"app","systemId":"s-hand","type":"container",
              "description":"руками","position":{"x":5,"y":5},"connections":[]}],
              "components":[],"codeElements":[],"viewLevel":"container","activeSystemId":"s-hand"}},
             "version":0}
            """);

        var doc = ArchitectureModelMerger.Merge(existing, generated).Document;

        // Единственную систему, переименованную руками, берём как свою.
        Arr(doc, "systems").Should().ContainSingle().Which!["name"]!.GetValue<string>().Should().Be("Мой продукт");
        // Контейнер «app» совпал с «App» по имени без учёта регистра: id и описание — ручные.
        var app = Arr(doc, "containers").OfType<JsonObject>().Single(o => (string?)o["id"] == "c-hand");
        ((string)app["description"]!).Should().Be("руками");
        ((string)app["technology"]!).Should().Be("csharp"); // пустое поле дописано
        Arr(doc, "components").OfType<JsonObject>()
            .Where(o => (string?)o["name"] == "Services/Auth")
            .Should().ContainSingle().Which["containerId"]!.GetValue<string>().Should().Be("c-hand");
        doc["state"]!["model"]!["viewLevel"]!.GetValue<string>().Should().Be("container");
    }
}
