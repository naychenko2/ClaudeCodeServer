using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Проход 1 «Собрать архитектуру»: кандидаты L1 из конфига/кода (ArchitectureExternalScanner),
// карта происхождения в метаданных, «нет в коде», неворскрешение удалённого человеком
// и дозапись метаданных без потери чужих полей.
public sealed class ArchitectureCandidatesAndOriginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-cand-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T1 = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
    private static readonly DateTimeOffset T2 = DateTimeOffset.Parse("2026-10-02T10:00:00Z");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string rel, string text)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static JsonArray Arr(JsonObject doc, string name) => (JsonArray)doc["state"]!["model"]![name]!;

    private static JsonObject ById(JsonObject doc, string level, string id) =>
        Arr(doc, level).OfType<JsonObject>().Single(o => (string?)o["id"] == id);

    private static List<string> Tags(JsonObject el) =>
        el["tags"] is JsonArray a ? a.Select(t => (string)t!).ToList() : [];

    private static GeneratedElement Ctr(string key, string name) => new(key, name, null, null, "system", []);

    private static GeneratedModel Model(IEnumerable<GeneratedElement> containers,
        IEnumerable<GeneratedElement>? components = null, IEnumerable<GeneratedElement>? externals = null) =>
        new(new GeneratedElement("system", "Demo", null, null, null,
                (externals ?? []).Select(e => new GeneratedConnection(e.Key, "использует")).ToList()),
            containers.ToList(), (components ?? []).ToList(), externals?.ToList());

    // ---------- Сканер кандидатов ----------

    [Fact]
    public void FromAppSettings_СекцияСАдресом_КлючДобавляетУверенность_ЗначенияНеУтекают()
    {
        const string text = """
            {
              // комментарии в appsettings легальны
              "Logging": { "Url": "http://log" },
              "Dify": { "BaseUrl": "https://dify.secret.host", "ApiKey": "dataset-SECRETKEY" },
              "Perplexity": { "Api": { "Endpoint": "https://api.perplexity.ai" } },
              "Session": { "AutoSaveSeconds": 5 },
              "Mcp": { "McpTasksApiUrl": "http://localhost:5000" },
            }
            """;

        var found = ArchitectureExternalScanner.FromAppSettings("backend/App/appsettings.json", text).ToList();

        found.Select(c => c.Name).Should().Equal("Dify", "Perplexity", "Mcp");
        found.Single(c => c.Name == "Dify").Confidence.Should().Be(70);
        found.Single(c => c.Name == "Perplexity").Confidence.Should().Be(50);
        // Секрет-инвариант: ни адреса, ни ключа в выходе нет
        var serialized = JsonSerializer.Serialize(found);
        serialized.Should().NotContain("secret.host").And.NotContain("SECRETKEY")
            .And.NotContain("perplexity.ai").And.NotContain("localhost");
    }

    [Theory]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Development.json", true)]
    [InlineData("appsettings.Production.json", true)]
    [InlineData("appsettings.Local.json", false)]
    [InlineData("appsettings.local.json", false)]
    [InlineData("appsettings.Local.example.json", false)]
    [InlineData("settings.json", false)]
    public void IsTrackedAppSettings_LocalНеЧитается(string file, bool expected) =>
        ArchitectureExternalScanner.IsTrackedAppSettings(file).Should().Be(expected);

    [Fact]
    public void Scan_КонфигКомпозКод_ДедупСкладываетУверенность_LocalИТестыМимо()
    {
        Write("backend/App/appsettings.json", """{ "Dify": { "BaseUrl": "x", "ApiKey": "y" } }""");
        Write("backend/App/appsettings.Local.json", """{ "Hidden": { "Url": "x", "ApiKey": "y" } }""");
        Write("docker-compose.yml", """
            services:
              app:
                build: .
                ports: ["5000:5000"]
              dify:
                image: langgenius/dify-api
              signoz:  # наблюдаемость
                image: signoz/signoz
            volumes:
              data:
            """);
        Write("backend/App/Program.cs", """
            services.AddHttpClient("perplexity", c => {});
            services.AddQuietHttpClient<FalClient>("fal");
            services.AddHttpClient<TypedOnly>();
            """);
        Write("backend/App.Tests/Fixture.cs", """services.AddHttpClient("test-only");""");

        var result = ArchitectureExternalScanner.Scan(_root);

        var names = result.Accepted.Select(c => c.Name).ToList();
        names.Should().BeEquivalentTo(["Dify", "signoz", "perplexity", "fal"]);
        names.Should().NotContain(["Hidden", "app", "test-only", "data"]);
        var dify = result.Accepted.Single(c => c.Name == "Dify");
        dify.Confidence.Should().Be(100);
        dify.Source.Should().Contain("appsettings.json").And.Contain("docker-compose.yml");
        names[0].Should().Be("Dify");
    }

    [Fact]
    public void Rank_ПотолокИПорог_ЛишнееВСводку()
    {
        var raw = Enumerable.Range(0, 25)
            .Select(i => new ExternalCandidate($"Svc{i:00}", "HTTP API", "src", 50))
            .Append(new ExternalCandidate("Weak", null, "src", 10));

        var result = ArchitectureExternalScanner.Rank(raw);

        result.Accepted.Should().HaveCount(ArchitectureExternalScanner.MaxExternalSystems);
        result.Rejected.Select(c => c.Name).Should().Contain("Weak").And.HaveCount(6);
    }

    // ---------- Кандидаты в модели ----------

    [Fact]
    public void Build_КандидатыСтановятсяВнешнимиСистемамиСТегом_СвязьОтСистемыПроекта()
    {
        var input = new ArchitectureInput("Demo", [], new CodeSnapshotInput([], [], null),
            new Dictionary<string, string>(),
            [new ExternalCandidate("Dify", "HTTP API", "appsettings.json: секция Dify", 70)]);

        var merged = ArchitectureModelMerger.Merge(null, ArchitectureModelBuilder.Build(input), null, T1);

        var id = ArchitectureModelMerger.StableId("system", "external:dify");
        var dify = ById(merged.Document, "systems", id);
        ((bool)dify["external"]!).Should().BeTrue();
        Tags(dify).Should().Equal(ArchitectureModelBuilder.CandidateTag);
        ((string)dify["description"]!).Should().Contain("appsettings.json: секция Dify");
        ((string)dify["technology"]!).Should().Be("HTTP API");
        var project = Arr(merged.Document, "systems").OfType<JsonObject>().Single(o => (string?)o["name"] == "Demo");
        project["connections"]!.AsArray().Select(c => (string?)c!["targetId"]).Should().Equal(id);
        merged.Candidates.Should().Be(1);
        ((string?)merged.Elements[id]!["origin"]).Should().Be("code");
    }

    [Fact]
    public void Merge_ДваПрогонаПодряд_НиМодельНиКартаНеМеняются()
    {
        var gen = Model([Ctr("a", "A"), Ctr("b", "B")],
            externals: [new GeneratedElement("external:dify", "Dify", "d", "HTTP API", null, [], External: true)]);
        var first = ArchitectureModelMerger.Merge(null, gen, null, T1);
        var second = ArchitectureModelMerger.Merge(first.Document, gen, first.Elements, T2);

        second.Document.ToJsonString().Should().Be(first.Document.ToJsonString());
        second.Elements.ToJsonString().Should().Be(first.Elements.ToJsonString());
        (second.Added, second.MarkedMissing, second.Unmarked, second.SkippedDeleted).Should().Be((0, 0, 0, 0));
    }

    // ---------- «Нет в коде» ----------

    [Fact]
    public void Merge_ПропалИзКода_ТегИОтметка_ВернулсяСнимаются_БезДублей()
    {
        var full = Model([Ctr("a", "A"), Ctr("b", "B")]);
        var first = ArchitectureModelMerger.Merge(null, full, null, T1);
        var bId = ArchitectureModelMerger.StableId("container", "b");

        var gone = ArchitectureModelMerger.Merge(first.Document, Model([Ctr("a", "A")]), first.Elements, T2);
        Tags(ById(gone.Document, "containers", bId)).Should().Equal(ArchitectureModelMerger.MissingTag);
        ((string?)gone.Elements[bId]!["missingSince"]).Should().StartWith("2026-10-02");
        gone.MarkedMissing.Should().Be(1);
        gone.Missing.Should().Equal("B");

        // Повторный прогон: тег не дублируется, отметка не сдвигается
        var again = ArchitectureModelMerger.Merge(gone.Document, Model([Ctr("a", "A")]), gone.Elements, T2.AddDays(1));
        Tags(ById(again.Document, "containers", bId)).Should().Equal(ArchitectureModelMerger.MissingTag);
        ((string?)again.Elements[bId]!["missingSince"]).Should().StartWith("2026-10-02");
        again.MarkedMissing.Should().Be(0);

        var back = ArchitectureModelMerger.Merge(again.Document, full, again.Elements, T2.AddDays(2));
        var b = ById(back.Document, "containers", bId);
        b.ContainsKey("tags").Should().BeFalse();
        back.Elements[bId]!.AsObject().ContainsKey("missingSince").Should().BeFalse();
        back.Unmarked.Should().Be(1);
    }

    [Fact]
    public void Merge_ТриЧужихТега_ТегНеСтавится_ФактВСводке()
    {
        var first = ArchitectureModelMerger.Merge(null, Model([Ctr("a", "A"), Ctr("b", "B")]), null, T1);
        var bId = ArchitectureModelMerger.StableId("container", "b");
        ById(first.Document, "containers", bId)["tags"] = new JsonArray("x", "y", "z");

        var gone = ArchitectureModelMerger.Merge(first.Document, Model([Ctr("a", "A")]), first.Elements, T2);

        Tags(ById(gone.Document, "containers", bId)).Should().Equal("x", "y", "z");
        gone.Missing.Should().Equal("B");
        gone.Elements[bId]!["missingSince"].Should().NotBeNull();
    }

    [Fact]
    public void Merge_УдалённоеЧеловеком_НеВоскрешается_ВложенноеТоже()
    {
        var gen = Model([Ctr("a", "A"), Ctr("b", "B")],
            [new GeneratedElement("b::x", "X", null, null, "b", [])]);
        var first = ArchitectureModelMerger.Merge(null, gen, null, T1);
        var bId = ArchitectureModelMerger.StableId("container", "b");
        var xId = ArchitectureModelMerger.StableId("component", "b::x");
        var doc = first.Document;
        Arr(doc, "containers").Remove(ById(doc, "containers", bId));
        Arr(doc, "components").Remove(ById(doc, "components", xId));

        var second = ArchitectureModelMerger.Merge(doc, gen, first.Elements, T2);

        Arr(second.Document, "containers").OfType<JsonObject>().Should().NotContain(o => (string?)o["id"] == bId);
        Arr(second.Document, "components").Should().BeEmpty();
        // Контейнер плюс его вложенный компонент
        second.SkippedDeleted.Should().Be(2);
        ((string?)second.Elements[bId]!["userDeletedAt"]).Should().StartWith("2026-10-02");
        second.Missing.Should().BeEmpty();
    }

    [Fact]
    public void Merge_СтараяМодельБезКарты_GenСчитаетсяКодом_UuidВручную_РучноеНеМетится()
    {
        var aId = ArchitectureModelMerger.StableId("container", "a");
        var goneId = ArchitectureModelMerger.StableId("container", "gone");
        var sysId = ArchitectureModelMerger.StableId("system", "system");
        const string manualId = "3f2c1d7e-0000-4000-8000-000000000001";
        var existing = JsonNode.Parse($$"""
            { "state": { "model": {
                "systems": [ { "id": "{{sysId}}", "name": "Demo", "type": "system", "connections": [] } ],
                "containers": [
                  { "id": "{{aId}}", "name": "A", "type": "container", "systemId": "{{sysId}}", "connections": [] },
                  { "id": "{{goneId}}", "name": "Gone", "type": "container", "systemId": "{{sysId}}", "connections": [] },
                  { "id": "{{manualId}}", "name": "Руками", "type": "container", "systemId": "{{sysId}}", "connections": [] }
                ],
                "components": [], "codeElements": [], "viewLevel": "system" } }, "version": 0 }
            """);

        var merged = ArchitectureModelMerger.Merge(existing, Model([Ctr("a", "A")]), null, T1);

        ((string?)merged.Elements[aId]!["origin"]).Should().Be("code");
        ((string?)merged.Elements[goneId]!["origin"]).Should().Be("code");
        merged.Elements.ContainsKey(manualId).Should().BeFalse();
        Tags(ById(merged.Document, "containers", goneId)).Should().Equal(ArchitectureModelMerger.MissingTag);
        ById(merged.Document, "containers", manualId).ContainsKey("tags").Should().BeFalse();
        merged.Missing.Should().Equal("Gone");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Merge_МоделиНетИлиОнаПустая_ВсёСоздаётсяЗаново_НеСчитаетсяУдалённым(bool emptyModel)
    {
        var ids = new[] { "a", "b", "c" }.Select(k => ArchitectureModelMerger.StableId("container", k)).ToList();
        var meta = new JsonObject();
        foreach (var id in ids) meta[id] = new JsonObject { ["origin"] = "code" };
        JsonNode? existing = emptyModel
            ? JsonNode.Parse("""{ "state": { "model": { "systems": [], "containers": [], "components": [], "codeElements": [] } }, "version": 0 }""")
            : null;

        var merged = ArchitectureModelMerger.Merge(existing, Model([Ctr("a", "A"), Ctr("b", "B"), Ctr("c", "C")]), meta, T1);

        Arr(merged.Document, "containers").OfType<JsonObject>().Select(o => (string?)o["id"]).Should().BeEquivalentTo(ids);
        merged.SkippedDeleted.Should().Be(0);
        foreach (var id in ids) merged.Elements[id]!.AsObject().ContainsKey("userDeletedAt").Should().BeFalse();
    }

    [Fact]
    public void Merge_КомпонентПеренесёнРукамиВДругойКонтейнер_ОдинОбъект_ПереносУважен()
    {
        var gen = Model([Ctr("a", "A"), Ctr("b", "B")], [new GeneratedElement("a::x", "X", null, null, "a", [])]);
        var first = ArchitectureModelMerger.Merge(null, gen, null, T1);
        var bId = ArchitectureModelMerger.StableId("container", "b");
        var xId = ArchitectureModelMerger.StableId("component", "a::x");
        ById(first.Document, "components", xId)["containerId"] = bId;

        var second = ArchitectureModelMerger.Merge(first.Document, gen, first.Elements, T2);

        var xs = Arr(second.Document, "components").OfType<JsonObject>().Where(o => (string?)o["id"] == xId).ToList();
        xs.Should().ContainSingle();
        ((string?)xs[0]["containerId"]).Should().Be(bId);
        second.Added.Should().Be(0);
        second.Matched.Should().Be(4); // система, два контейнера, компонент
    }

    [Fact]
    public void Merge_ЧеловекУдалилСтрелкуККандидату_КандидатОстался_СтрелкаНеВозвращается()
    {
        var gen = Model([Ctr("a", "A")],
            externals: [new GeneratedElement("external:dify", "Dify", "d", "HTTP API", null, [], External: true)]);
        var first = ArchitectureModelMerger.Merge(null, gen, null, T1);
        var project = Arr(first.Document, "systems").OfType<JsonObject>().Single(o => (string?)o["name"] == "Demo");
        project["connections"]!.AsArray().Should().ContainSingle();
        project["connections"] = new JsonArray();

        var second = ArchitectureModelMerger.Merge(first.Document, gen, first.Elements, T2);

        var projectAfter = Arr(second.Document, "systems").OfType<JsonObject>().Single(o => (string?)o["name"] == "Demo");
        projectAfter["connections"]!.AsArray().Should().BeEmpty();
        second.ConnectionsAdded.Should().Be(0);
        ById(second.Document, "systems", ArchitectureModelMerger.StableId("system", "external:dify")).Should().NotBeNull();
    }

    [Fact]
    public void Merge_ПереименованоРуками_IdСохраняется_НетВКодеНеСтавится()
    {
        var gen = Model([Ctr("a", "A")]);
        var first = ArchitectureModelMerger.Merge(null, gen, null, T1);
        var aId = ArchitectureModelMerger.StableId("container", "a");
        ById(first.Document, "containers", aId)["name"] = "Альфа";

        var second = ArchitectureModelMerger.Merge(first.Document, gen, first.Elements, T2);

        var containers = Arr(second.Document, "containers").OfType<JsonObject>().ToList();
        containers.Should().ContainSingle();
        ((string?)containers[0]["id"]).Should().Be(aId);
        ((string?)containers[0]["name"]).Should().Be("Альфа");
        Tags(containers[0]).Should().BeEmpty();
        second.Missing.Should().BeEmpty();
        second.Added.Should().Be(0);
    }

    // ---------- Симлинки и junction в обходе ----------

    [Fact]
    public void Scan_СимлинкНаКаталог_ОбходВНегоНеИдёт()
    {
        Write("backend/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("backend/App/appsettings.json", """{ "Dify": { "BaseUrl": "x", "ApiKey": "y" } }""");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "backend", "Link"), Path.Combine(_root, "backend", "App"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows без режима разработчика симлинки не даёт — проверка живёт в CI на Linux
            return;
        }

        ArchitectureSourceScanner.ScanUnits(_root).Select(u => u.RelDir).Should().Equal("backend/App");
        ArchitectureExternalScanner.Scan(_root).Accepted.Single(c => c.Name == "Dify").Source.Should().NotContain("Link");
    }

    // ---------- Метаданные: дозапись вместо перезаписи ----------

    [Fact]
    public async Task GenerateAsync_СохраняетUpdatedAtИUpdatedBy_ПишетКартуПроисхождения()
    {
        Write("backend/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("backend/App/appsettings.json", """{ "Dify": { "BaseUrl": "https://dify.local", "ApiKey": "k" } }""");
        Write("docs/architecture/model.viaduct.meta.json",
            """{ "updatedAt": "2026-09-30T00:00:00+00:00", "updatedBy": "Вера", "свое": 1 }""");
        var generator = new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance);

        var result = await generator.GenerateAsync(_root, "Demo", new CodeSnapshotInput([], [], null), CancellationToken.None);

        result.Candidates.Should().Be(1);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "docs", "architecture", "model.viaduct.meta.json")))!;
        ((string?)meta["updatedBy"]).Should().Be("Вера");
        ((string?)meta["updatedAt"]).Should().StartWith("2026-09-30");
        ((int)meta["свое"]!).Should().Be(1);
        ((string?)meta["generator"]).Should().Be("ccs-code-v1");
        meta["elements"]!.AsObject().Select(p => (string?)p.Value!["origin"]).Should().OnlyContain(o => o == "code")
            .And.HaveCountGreaterThan(1);
        var model = File.ReadAllText(Path.Combine(_root, "docs", "architecture", "model.viaduct.json"));
        model.Should().NotContain("dify.local");
    }

    [Fact]
    public async Task StoreWriteAsync_Origins_ДописываютсяВКарту_ЧужоеЦело()
    {
        Write("docs/architecture/model.viaduct.meta.json",
            """{ "generatedAt": "2026-09-30T00:00:00+00:00", "elements": { "gen-container-aa": { "origin": "code" } } }""");
        var store = new ArchitectureModelStore();

        var outcome = await store.WriteAsync(_root, """{ "state": {} }""", null, "Максим", CancellationToken.None,
            new Dictionary<string, string> { ["new-id"] = ArchitectureModelMerger.OriginAgent });

        outcome.Saved.Should().BeTrue();
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "docs", "architecture", "model.viaduct.meta.json")))!;
        ((string?)meta["elements"]!["new-id"]!["origin"]).Should().Be("agent");
        ((string?)meta["elements"]!["gen-container-aa"]!["origin"]).Should().Be("code");
        ((string?)meta["generatedAt"]).Should().StartWith("2026-09-30");
    }
}
