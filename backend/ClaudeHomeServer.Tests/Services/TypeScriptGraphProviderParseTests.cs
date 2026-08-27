using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Юнит-тесты TypeScriptGraphProvider: маппинг JSON-контракта экстрактора в Core.CodeGraph
// и поведение «экстрактора нет — пустой граф» до стыковки с реальным скриптом.
// Прогон настоящего Node-экстрактора — отдельная задача приёмки (TypeScriptGraphProviderTests).
public class TypeScriptGraphProviderParseTests
{
    private static TypeScriptGraphProvider CreateProvider() =>
        new(NullLogger<TypeScriptGraphProvider>.Instance, new ConfigurationBuilder().Build());

    [Fact]
    public void ParseSnapshot_КонтрактPascalCase_МаппитУзлыРёбраИMetadataПропускает()
    {
        var json = """
        {
          "Nodes": [
            { "Id": "src/components/ui/Segmented:SegmentedControl", "Label": "SegmentedControl",
              "FullyQualifiedName": "src/components/ui/Segmented:SegmentedControl",
              "SourceFile": "src/components/ui/Segmented.tsx", "SourceLocation": "line 12",
              "Kind": "ui-primitive" },
            { "Id": "src/hooks/useSession:useSession", "Label": "useSession",
              "FullyQualifiedName": "src/hooks/useSession:useSession",
              "SourceFile": "src/hooks/useSession.ts", "SourceLocation": "line 3",
              "Kind": "hook" }
          ],
          "Edges": [
            { "Source": "src/components/ui/Segmented:SegmentedControl", "Target": "src/hooks/useSession:useSession",
              "Relation": "References", "Confidence": "Extracted" }
          ],
          "Metadata": { "files": 42 }
        }
        """;

        var graph = TypeScriptGraphProvider.ParseSnapshot(json);

        graph.Nodes.Should().HaveCount(2);
        graph.Nodes["src/components/ui/Segmented:SegmentedControl"].Kind.Should().Be(NodeKind.UiPrimitive);
        graph.Nodes["src/hooks/useSession:useSession"].Kind.Should().Be(NodeKind.Hook);
        graph.Edges.Should().ContainSingle(e =>
            e.Source == "src/components/ui/Segmented:SegmentedControl"
            && e.Target == "src/hooks/useSession:useSession"
            && e.Relation == EdgeRelation.References
            && e.Confidence == EdgeConfidence.Extracted);
    }

    [Fact]
    public void ParseSnapshot_ТерпимКCamelCaseЧисламПсевдонимамKindИМусоруПередJson()
    {
        var json = "node v22\n" + """
        {
          "nodes": [
            { "id": "src/lib/format:formatMoney", "label": "formatMoney",
              "fullyQualifiedName": "src/lib/format:formatMoney",
              "sourceFile": "src/lib/format.ts", "sourceLocation": 7,
              "kind": "utility" },
            { "id": "src/lib/unknown:thing", "kind": "mystery" }
          ],
          "edges": [
            { "source": "src/lib/unknown:thing", "target": "src/lib/format:formatMoney" }
          ]
        }
        """;

        var graph = TypeScriptGraphProvider.ParseSnapshot(json);

        graph.Nodes.Should().HaveCount(2);
        var util = graph.Nodes["src/lib/format:formatMoney"];
        util.Kind.Should().Be(NodeKind.Util);
        util.SourceLocation.Should().Be("7");
        // Неизвестный Kind → Util; Relation/Confidence без значений → References/Inferred.
        graph.Nodes["src/lib/unknown:thing"].Kind.Should().Be(NodeKind.Util);
        graph.Edges.Should().ContainSingle(e =>
            e.Relation == EdgeRelation.References && e.Confidence == EdgeConfidence.Inferred);
    }

    [Fact]
    public void ParseSnapshot_ФактическийКонтрактЭкстрактора_NameCategoryFilePath_FromToKindИМодульныеРёбра()
    {
        // Ровно то, что печатает frontend/scripts/codegraph-extractor.mjs (сверено прогоном):
        // узлы { Id: «файл::имя», Name, Category, FilePath }, рёбра { From, To, Kind },
        // Category «ui-примитив» — кириллицей, From модуля-импортёра — суффикс «::*».
        var json = """
        {
          "Nodes": [
            { "Id": "components/ui/Segmented.tsx::SegmentedControl", "Name": "SegmentedControl",
              "Category": "ui-примитив", "FilePath": "components/ui/Segmented.tsx" },
            { "Id": "features/projects/AvatarMenu.tsx::AvatarMenu", "Name": "AvatarMenu",
              "Category": "component", "FilePath": "features/projects/AvatarMenu.tsx" },
            { "Id": "features/projects/AvatarMenu.tsx::AVATAR_SIZES", "Name": "AVATAR_SIZES",
              "Category": "util", "FilePath": "features/projects/AvatarMenu.tsx" }
          ],
          "Edges": [
            { "From": "features/projects/AvatarMenu.tsx::*", "To": "components/ui/Segmented.tsx::SegmentedControl",
              "Kind": "References" },
            { "From": "gone.tsx::*", "To": "components/ui/Segmented.tsx::SegmentedControl" }
          ],
          "Metadata": { "SourceRoot": "src", "TotalFiles": 546 }
        }
        """;

        var graph = TypeScriptGraphProvider.ParseSnapshot(json);

        graph.Nodes.Should().HaveCount(3);
        var seg = graph.Nodes["components/ui/Segmented.tsx::SegmentedControl"];
        seg.Label.Should().Be("SegmentedControl");
        seg.FullyQualifiedName.Should().Be("components/ui/Segmented.tsx::SegmentedControl");
        seg.SourceFile.Should().Be("components/ui/Segmented.tsx");
        seg.Kind.Should().Be(NodeKind.UiPrimitive);
        graph.Nodes["features/projects/AvatarMenu.tsx::AvatarMenu"].Kind
            .Should().Be(NodeKind.Component);

        // Модульное ребро From «файл::*» разворачивается во все именованные узлы файла;
        // модуль без узлов (gone.tsx) отбрасывается как висящий конец.
        graph.Edges.Should().HaveCount(2);
        graph.Edges.Should().Contain(e =>
            e.Source == "features/projects/AvatarMenu.tsx::AvatarMenu"
            && e.Target == "components/ui/Segmented.tsx::SegmentedControl"
            && e.Relation == EdgeRelation.References
            && e.Confidence == EdgeConfidence.Inferred);
        graph.Edges.Should().Contain(e =>
            e.Source == "features/projects/AvatarMenu.tsx::AVATAR_SIZES"
            && e.Target == "components/ui/Segmented.tsx::SegmentedControl");
    }

    [Fact]
    public void ParseSnapshot_КонстантаИОбъектСМетодами_РазличаютсяПоKind()
    {
        // Экстрактор решает «данные или поведение» по AST инициализатора: токены
        // дизайн-системы (C, FONT, ICON_*) — чистые данные → constant; api (объект со
        // стрелочными методами) → util. Здесь — стыковка обеих строк Category.
        var json = """
        {
          "Nodes": [
            { "Id": "lib/design.ts::C", "Name": "C", "Category": "constant", "FilePath": "lib/design.ts" },
            { "Id": "lib/design.ts::FONT", "Name": "FONT", "Category": "константа", "FilePath": "lib/design.ts" },
            { "Id": "lib/api.ts::api", "Name": "api", "Category": "util", "FilePath": "lib/api.ts" }
          ],
          "Edges": []
        }
        """;

        var graph = TypeScriptGraphProvider.ParseSnapshot(json);

        graph.Nodes["lib/design.ts::C"].Kind.Should().Be(NodeKind.Constant);
        graph.Nodes["lib/design.ts::FONT"].Kind.Should().Be(NodeKind.Constant);
        graph.Nodes["lib/api.ts::api"].Kind.Should().Be(NodeKind.Util,
            "объект со стрелочными методами — поведение, не константа");
    }

    [Fact]
    public void ParseSnapshot_ОтбрасываетВисящиеРёбраИДубли()
    {
        var json = """
        {
          "Nodes": [
            { "Id": "a:A", "Kind": "component" },
            { "Id": "b:B", "Kind": "hook" }
          ],
          "Edges": [
            { "Source": "a:A", "Target": "b:B" },
            { "Source": "a:A", "Target": "b:B" },
            { "Source": "a:A", "Target": "нет-такого-узла" }
          ]
        }
        """;

        var graph = TypeScriptGraphProvider.ParseSnapshot(json);

        graph.Edges.Should().ContainSingle(e => e.Source == "a:A" && e.Target == "b:B");
    }

    [Fact]
    public async Task BuildAsync_БезЭкстрактораВозлеПроектаВозвращаетПустойГраф()
    {
        // Временный каталог вне репозитория: подъём от него не находит frontend/scripts.
        var dir = Path.Combine(Path.GetTempPath(), "tsgraph_no_extractor_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var graph = await CreateProvider().BuildAsync(dir, CancellationToken.None);

            graph.Nodes.Should().BeEmpty("экстрактора нет — честный пустой граф, не исключение");
            graph.Edges.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
