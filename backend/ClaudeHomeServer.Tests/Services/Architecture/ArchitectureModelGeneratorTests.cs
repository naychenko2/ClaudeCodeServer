using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Architecture;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services.Architecture;

// Сканер проектов-маркеров и генератор целиком на временной папке (пути — через
// Path.GetTempPath/Path.Combine: CI гоняет тесты на Linux).
public sealed class ArchitectureModelGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arch-gen-" + Guid.NewGuid().ToString("N"));

    public ArchitectureModelGeneratorTests()
    {
        Write("backend/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup><ProjectReference Include="..\App.Core\App.Core.csproj" /></ItemGroup>
            </Project>
            """);
        Write("backend/App.Core/App.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("backend/App.Tests/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("backend/App/bin/Debug/Junk.csproj", "<Project />");
        Write("frontend/package.json", """{ "name": "web", "dependencies": { "react": "18" } }""");
        Write("frontend/node_modules/x/package.json", """{ "name": "x" }""");
        Write("backend/App.Core/Composition/IAppSubsystem.cs", "public interface IAppSubsystem {}");
        Write("backend/App/Chat/ChatSubsystem.cs", """
            public sealed class ChatSubsystem : IAppPhaseSubsystem
            {
                public string Key => "chat";
                public string Title => "Чат";
            }
            """);
    }

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

    private static CodeSnapshotInput Snapshot() => new(
        [
            new CodeTypeInfo("IAppSubsystem", "IAppSubsystem", "backend/App.Core/Composition/IAppSubsystem.cs", "Interface"),
            new CodeTypeInfo("IAppPhaseSubsystem", "IAppPhaseSubsystem", "backend/App.Core/Composition/IAppSubsystem.cs", "Interface"),
            new CodeTypeInfo("ChatSubsystem", "ChatSubsystem", @"backend\App\Chat\ChatSubsystem.cs", "Class"),
            new CodeTypeInfo("Page", "Page", "frontend/src/pages/Page.tsx", "Class"),
        ],
        [
            new CodeEdgeInfo("IAppPhaseSubsystem", "IAppSubsystem", "Implements"),
            new CodeEdgeInfo("ChatSubsystem", "IAppPhaseSubsystem", "Implements"),
        ],
        DateTimeOffset.Parse("2026-09-25T10:00:00Z"));

    [Fact]
    public void ScanUnits_НаходитМаркеры_СсылкиИТесты_ПропускаетСлужебное()
    {
        var units = ArchitectureSourceScanner.ScanUnits(_root);

        units.Select(u => u.RelDir).Should().Equal("backend/App", "backend/App.Core", "backend/App.Tests", "frontend");
        units.Single(u => u.Name == "App").References.Should().Equal("backend/App.Core");
        units.Single(u => u.Name == "App.Tests").IsTest.Should().BeTrue();
        units.Single(u => u.RelDir == "frontend").Should().Match<SourceUnit>(u => u.Name == "web" && u.Technology == "react");
    }

    [Fact]
    public void ReadSubsystemTitles_ИдётЧерезИнтерфейсНаследник()
    {
        var titles = ArchitectureSourceScanner.ReadSubsystemTitles(Snapshot(),
            rel => File.ReadAllText(Path.Combine(_root, rel.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))));

        titles.Should().Equal(new Dictionary<string, string> { ["ChatSubsystem"] = "Чат" });
    }

    [Fact]
    public async Task GenerateAsync_ПишетМодельИМетку_ПовторноСохраняетОписание()
    {
        var generator = new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance);

        var first = await generator.GenerateAsync(_root, "Demo", Snapshot(), CancellationToken.None);
        first.Containers.Should().Be(3);
        first.GraphBuiltAt.Should().StartWith("2026-09-25T10:00:00");

        var modelPath = Path.Combine(_root, "docs", "architecture", "model.viaduct.json");
        var doc = JsonNode.Parse(File.ReadAllText(modelPath))!.AsObject();
        var app = doc["state"]!["model"]!["containers"]!.AsArray().OfType<JsonObject>()
            .Single(o => (string?)o["name"] == "App");
        ((string)app["description"]!).Should().Be("Чат");
        app["description"] = "Описал руками";
        File.WriteAllText(modelPath, doc.ToJsonString());

        var second = await generator.GenerateAsync(_root, "Demo", Snapshot(), CancellationToken.None);

        second.Added.Should().Be(0);
        var again = JsonNode.Parse(File.ReadAllText(modelPath))!;
        again["state"]!["model"]!["containers"]!.AsArray().OfType<JsonObject>()
            .Single(o => (string?)o["name"] == "App")["description"]!.GetValue<string>()
            .Should().Be("Описал руками");

        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "docs", "architecture", "model.viaduct.meta.json")))!;
        meta["graphBuiltAt"]!.GetValue<string>().Should().StartWith("2026-09-25T10:00:00");
    }

    [Fact]
    public async Task GenerateAsync_БитыйФайлМодели_НеПерезаписывает()
    {
        Write("docs/architecture/model.viaduct.json", "{ не json");
        var generator = new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance);

        var act = () => generator.GenerateAsync(_root, "Demo", Snapshot(), CancellationToken.None);

        await act.Should().ThrowAsync<ArchitectureModelCorruptException>();
        File.ReadAllText(Path.Combine(_root, "docs", "architecture", "model.viaduct.json")).Should().Be("{ не json");
    }

    [Fact]
    public async Task GenerateAsync_КириллицаВФайлеБуквально_БезЭскейпов()
    {
        // Модель лежит под git: \uXXXX вместо русских имён делает дифф нечитаемым.
        var generator = new ArchitectureModelGenerator(NullLogger<ArchitectureModelGenerator>.Instance);

        await generator.GenerateAsync(_root, "Демо-система", Snapshot(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(_root, "docs", "architecture", "model.viaduct.json"));
        text.Should().Contain("Демо-система").And.Contain("Чат");
        text.Should().NotContain("\\u04");
    }
}
